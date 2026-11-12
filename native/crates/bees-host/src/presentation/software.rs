//! Small, window-independent tactical renderer of authoritative live geometry.
//! Owns only pixels/camera/selection; it never mutates or steps a battle.

use std::collections::{HashMap, HashSet};
use std::io::{self, Write};
use std::sync::OnceLock;
use bees_sim::collision::{Aabb, Pose};
use bees_sim::geometry::Vec2;
use bees_sim::physics::ShipShape;
use bees_sim::simulation::{RandomStream, WorldId};
use super::{NativeRenderWorldView, NativeSquadSelection, battle_outcome_label, text};
use super::minimap::MiniMapLayout;
use super::tactical_hud;

#[derive(Clone, Copy, Debug)]
pub struct NativeCamera {
    pub center: Vec2,
    pub pixels_per_unit: f32,
}

impl NativeCamera {
    pub fn fit(bounds: Aabb, width: usize, height: usize) -> Self {
        Self {
            center: (bounds.min + bounds.max) * 0.5,
            pixels_per_unit: ((width as f32 / (bounds.max.x - bounds.min.x).max(1.0))
                .min(height as f32 / (bounds.max.y - bounds.min.y).max(1.0)) * 0.9)
                .clamp(0.001, 1000.0),
        }
    }

    pub fn screen_to_world(self, x: f32, y: f32, width: usize, height: usize) -> Vec2 {
        self.center + Vec2::new(x - width as f32 * 0.5, height as f32 * 0.5 - y)
            * (1.0 / self.pixels_per_unit)
    }

    pub fn zoom_at(&mut self, factor: f32, x: f32, y: f32, width: usize, height: usize) {
        if !factor.is_finite() || factor <= 0.0 { return; }
        let before = self.screen_to_world(x, y, width, height);
        self.pixels_per_unit = (self.pixels_per_unit * factor).clamp(0.001, 1000.0);
        self.center += before - self.screen_to_world(x, y, width, height);
    }
}

#[derive(Clone, Copy, Debug, Default, serde::Serialize)]
pub struct NativeRenderCounts {
    pub ships: usize,
    pub obstacles: usize,
    pub culled_ships: usize,
    pub culled_obstacles: usize,
    pub projectiles: usize,
    pub pieces: usize,
    pub culled_projectiles: usize,
    pub culled_pieces: usize,
    pub range_queries: usize,
    /// Enemy ships excluded by the simulator's existing weapon-range
    /// contact cache, without inspecting their hulls in the renderer.
    pub range_candidates_culled: usize,
    pub range_contacts: usize,
    pub aim_points: usize,
    pub automatic_targets: usize,
    pub missing_aim_data: usize,
    pub range_overlaps: usize,
    pub obstructed_ranges: usize,
    pub missing_range_data: usize,
    pub minimap_ship_markers: usize,
    pub minimap_visible_enemy_markers: usize,
    pub numbered_squad_rows: usize,
    pub special_ready_ships: usize,
    pub tactical_buttons_drawn: usize,
    pub selected_command_rows: usize,
    pub command_target_markers: usize,
    /// Presentation-only pixel coverage; headless physics does no work.
    pub fog_sources: usize,
    pub fog_fading_sources: usize,
    pub fog_occluded_pixels: usize,
    /// One non-stencil, fully exposed viewport this frame.
    pub fog_full_viewport_skips: usize,
    /// One direct dark fill with no player sight sources remaining.
    pub fog_empty_viewport_fills: usize,
}

pub struct NativeSoftwareRenderer {
    width: usize,
    height: usize,
    pixels: Vec<u32>,
    /// Allocated only for fog-enabled graphical Levels and reused by frame.
    fog_coverage: Vec<u8>,
    /// UI lifecycle only: Unity FogOfWarVision remains after a ship death,
    /// shrinking its mask by 0.8 every 0.1 simulation seconds.
    fog_context: Option<(WorldId, u64, Option<i32>)>,
    fog_render_pass: u64,
    fog_live: HashMap<i64, FogLiveSource>,
    fog_fades: Vec<FogFade>,
    pending_fog_fades: Vec<FogFade>,
    pub camera: NativeCamera,
    /// Selection is scoped to a WorldId AND episode revision by the client.
    pub selected_ship: Option<i64>,
    pub selection: NativeSquadSelection,
    // Reused only for large selections; small squads need no hash allocation.
    selected_squad_lookup: HashSet<bees_hivemind::session::SquadLifetime>,
    /// Reused UI dedup for actual player Aggressive/BombingRun targets.
    /// Filled from the live command director before the existing ship
    /// render pass, avoiding any additional ship loop or copied roster.
    command_target_ship_ids: HashSet<i64>,
    pub selection_box: Option<Aabb>,
    /// Optional inspection only. Disabled means no range or linecast work.
    pub show_weapon_ranges: bool,
    /// Presentation-only tactical overview, never used by headless worlds.
    pub show_minimap: bool,
    /// Only affects the window; training runs keep the same headless world.
    pub show_shooting_menu: bool,
    pub focused_shooting_strategy: u8,
    pub active_tactical_action: Option<super::HudAction>,
    pub locked_selection_all: bool,
    pub chasing_selection_all: bool,
    pub matched_speed_selection_all: bool,
    pub ceasefire_selection_all: bool,
    pub attack_on_sight_selection_all: bool,
    pub hovered_tactical_action: Option<super::HudAction>,
    debris_frame: Option<(WorldId, u64, u64)>,
    obstacle_debris: Vec<ObstacleDebris>,
}

#[derive(Clone, Copy)]
struct FogLiveSource {
    position: Vec2,
    radius: f32,
    seen_pass: u64,
}

#[derive(Clone, Copy)]
struct FogFade {
    position: Vec2,
    radius: f32,
    born_tick: u64,
}

impl FogFade {
    fn current_radius(self, tick: u64, dt: f32) -> f32 {
        let elapsed = tick.saturating_sub(self.born_tick) as f32 * dt;
        let phases = (elapsed / 0.1).floor().max(0.0);
        self.radius * 0.8f32.powf(phases)
    }
}

struct ObstacleDebris {
    born: u64,
    position: Vec2,
    velocity: Vec2,
    rotation: f32,
    spin: f32,
    lifetime: f32,
    scale: f32,
    sprite: usize,
}

struct NativeSprite {
    width: usize,
    height: usize,
    pixels: Vec<u8>,
}

struct SpriteAtlas {
    pixels_per_unit: f32,
    sprites: Vec<NativeSprite>,
}

impl SpriteAtlas {
    fn decode(data: &[u8]) -> Self {
        assert_eq!(&data[..4], b"BDS2");
        let read_u16 = |bytes: &[u8]| usize::from(u16::from_le_bytes([bytes[0], bytes[1]]));
        let pixels_per_unit = read_u16(&data[4..]) as f32;
        let count = read_u16(&data[6..]);
        let mut rest = &data[8..];
        let sprites = (0..count).map(|_| {
            let width = read_u16(rest);
            let height = read_u16(&rest[2..]);
            rest = &rest[4..];
            let size = width * height * 4;
            let mut pixels = Vec::with_capacity(size);
            while pixels.len() < size {
                let length = read_u16(rest);
                assert!(length > 0 && pixels.len() + length * 4 <= size);
                for _ in 0..length { pixels.extend_from_slice(&rest[2..6]); }
                rest = &rest[6..];
            }
            NativeSprite { width, height, pixels }
        }).collect();
        assert!(rest.is_empty() && count > 0 && pixels_per_unit > 0.0);
        Self { pixels_per_unit, sprites }
    }
}

fn fire_tank_debris_atlas() -> &'static SpriteAtlas {
    static ATLAS: OnceLock<SpriteAtlas> = OnceLock::new();
    ATLAS.get_or_init(|| SpriteAtlas::decode(include_bytes!("../../../../assets/fire-tank-debris.rgba")))
}

impl NativeSoftwareRenderer {
    pub fn new(width: usize, height: usize, bounds: Aabb) -> io::Result<Self> {
        if width == 0 || height == 0 || width > 8192 || height > 8192 ||
            !bounds.min.is_finite() || !bounds.max.is_finite() ||
            bounds.min.x >= bounds.max.x || bounds.min.y >= bounds.max.y {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "invalid viewport or bounds"));
        }
        Ok(Self { width, height, pixels: vec![0; width * height],
            fog_coverage: Vec::new(), fog_context: None, fog_render_pass: 0,
            fog_live: HashMap::new(), fog_fades: Vec::new(),
            pending_fog_fades: Vec::new(),
            camera: NativeCamera::fit(bounds, width, height), selected_ship: None,
            selection: NativeSquadSelection::default(),
            selected_squad_lookup: HashSet::new(),
            command_target_ship_ids: HashSet::new(),
            selection_box: None, show_weapon_ranges: false,
            show_minimap: true, show_shooting_menu: false,
            focused_shooting_strategy: 1,
            active_tactical_action: None, locked_selection_all: false,
            chasing_selection_all: false,
            matched_speed_selection_all: false,
            ceasefire_selection_all: false,
            attack_on_sight_selection_all: false,
            hovered_tactical_action: None,
            debris_frame: None, obstacle_debris: Vec::new() })
    }

    pub fn size(&self) -> (usize, usize) { (self.width, self.height) }
    pub fn pixels(&self) -> &[u32] { &self.pixels }

    /// Resize presentation storage only. Preserve camera world scale and
    /// center, so exposing more pixels shows more of the same battlefield.
    /// Never allocate a new simulation or touch the authoritative world.
    pub fn resize(&mut self, width: usize, height: usize) -> io::Result<bool> {
        if width == self.width && height == self.height { return Ok(false); }
        if width == 0 || height == 0 || width > 8192 || height > 8192 {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "invalid viewport size"));
        }
        self.pixels.resize(width * height, 0);
        if !self.fog_coverage.is_empty() {
            self.fog_coverage.resize(width * height, 0);
        }
        self.width = width;
        self.height = height;
        self.selection_box = None;
        if tactical_hud::shooting_rect(width, height, 0).is_none() {
            self.show_shooting_menu = false;
        }
        Ok(true)
    }

    /// The same coordinate frame used by drawing and click/drag navigation.
    /// Returns None when the overview is hidden or the window is too small.
    pub fn minimap_world_at(&self, bounds: Aabb, x: f32, y: f32) -> Option<Vec2> {
        if self.show_shooting_menu { return None; }
        self.show_minimap.then(|| MiniMapLayout::new(self.width, self.height, bounds))
            .flatten()?.world_at(x, y)
    }

    /// UI-only action hit-test; the client converts intent into the exact
    /// same typed player inputs used by keyboard shortcuts and replay.
    pub fn tactical_action_at(&self, x: f32, y: f32) -> Option<super::HudAction> {
        if self.show_shooting_menu { return None; }
        let action = tactical_hud::hit(self.width, self.height, x, y)?;
        (action == super::HudAction::SelectAll ||
            !self.selection.squads().is_empty()).then_some(action)
    }

    /// Pick a live, user-controllable source squad by the exact tab drawn in
    /// this same render frame, not from a guessed row/squad index.
    pub fn squad_tab_at(&self, view: &NativeRenderWorldView<'_>,
        x: f32, y: f32,
    ) -> Option<bees_hivemind::session::SquadLifetime> {
        if self.show_shooting_menu { return None; }
        let mut row = 0usize;
        for &owner in view.roster.source_order() {
            let Some(squad) = view.roster.get(owner) else { continue };
            let Some(number) = squad.source_hotkey_number else { continue };
            if number > 10 || !view.is_local_player_squad(owner) {
                continue;
            }
            let Some(&ship_id) = squad.members.first() else { continue };
            let Some(ship) = view.combat.ship(ship_id) else { continue };
            let key = if number == 10 { 0 } else { number };
            let label = format!("{key}: type {} / {} ships",
                ship.ship_type, squad.members.len());
            let rect = tactical_hud::squad_tab_rect(
                self.width, self.height, row, text::HEIGHT,
                text::width(&label, 1))?;
            if rect.contains(x, y) { return Some(owner); }
            row += 1;
            if row == 10 { break; }
        }
        None
    }

    pub fn shooting_strategy_at(&self, x: f32, y: f32) -> Option<u8> {
        if !self.show_shooting_menu { return None; }
        tactical_hud::shooting_hit(self.width, self.height, x, y)
    }

    /// Reuse the player squad's actual current ownership, and the source
    /// FogOfWarVision mask radius (half its 2x sight-range diameter). A policy-controlled or enemy hull can never
    /// reveal the local player's map merely because its geometry is nearby.
    fn fog_source_radius(view: &NativeRenderWorldView<'_>, ship_id: i64) -> Option<f32> {
        let owner = view.roster.owning_squad(ship_id)?;
        if !view.is_local_player_squad(owner) { return None; }
        view.player_authority.player_fog_vision_radius(ship_id)
            .filter(|radius| radius.is_finite() && *radius > 0.0)
    }

    /// UI-only hit testing must not choose a ship or obstacle hidden by
    /// the source fog mask. The world continues simulating every entity.
    fn fog_visible_at(&self, view: &NativeRenderWorldView<'_>, point: Vec2) -> bool {
        if !view.player_authority.player_fog_of_war_enabled() { return true; }
        if !point.is_finite() { return false; }
        if view.combat.render_ships().any(|(ship, body)| {
            let Some(radius) = Self::fog_source_radius(view, ship.entity_id)
                else { return false };
            let delta = body.motion.position - point;
            delta.length_squared() <= radius * radius
        }) {
            return true;
        }
        // This is the same transient SpriteMask fade the renderer paints.
        // Never let a fade from a previous world/episode grant visibility.
        self.fog_context == Some((view.world_id, view.episode_revision,
            view.player_authority.player_viewer_side())) &&
            self.fog_fades.iter().any(|fade| {
                let radius = fade.current_radius(view.tick, view.fixed_step_seconds);
                radius >= 1.5 &&
                    (fade.position - point).length_squared() <= radius * radius
            })
    }

    /// Rasterize overlapping source sight discs into one reusable screen
    /// mask. Each observer costs one square root per covered scanline, not
    /// one geometry test for every pixel or other ship. No fog work happens
    /// in the headless simulation or in levels whose source disables fog.
    /// The inexpensive fast path for a source mask covering all four
    /// viewport corners. Only one fully covering disc is needed; unions
    /// of smaller discs take the regular stencil path.
    fn fog_covers_viewport(&self, position: Vec2, source_radius: f32) -> bool {
        let radius = source_radius * self.camera.pixels_per_unit;
        if !radius.is_finite() || radius <= 0.0 { return false; }
        let (x, y) = self.project(position);
        if !x.is_finite() || !y.is_finite() { return false; }
        let dx = x.abs().max((self.width as f32 - x).abs());
        let dy = y.abs().max((self.height as f32 - y).abs());
        radius * radius >= dx * dx + dy * dy
    }

    /// Fill only visible scanline intervals. Keeping this helper free of
    /// renderer borrows allows iterating the reusable live-source map
    /// without allocating/copying it into a second visual registry.
    fn reveal_fog_circle(
        stencil: &mut [u8], camera: NativeCamera, width: usize, height: usize,
        position: Vec2, source_radius: f32,
    ) {
        let radius = source_radius * camera.pixels_per_unit;
        if !radius.is_finite() || radius <= 0.0 { return; }
        let cx = (position.x - camera.center.x) * camera.pixels_per_unit + width as f32 * 0.5;
        let cy = (camera.center.y - position.y) * camera.pixels_per_unit + height as f32 * 0.5;
        if !cx.is_finite() || !cy.is_finite() ||
            cx + radius < 0.0 || cx - radius >= width as f32 ||
            cy + radius < 0.0 || cy - radius >= height as f32 {
            return;
        }
        let first_y = (cy - radius).floor().max(0.0) as usize;
        let last_y = (cy + radius).ceil().min(height as f32 - 1.0) as usize;
        let radius_squared = radius * radius;
        for y in first_y..=last_y {
            let dy = y as f32 + 0.5 - cy;
            if dy.abs() > radius { continue; }
            let half_width = (radius_squared - dy * dy).max(0.0).sqrt();
            if cx + half_width < 0.0 || cx - half_width >= width as f32 {
                continue;
            }
            let first_x = (cx - half_width).ceil().max(0.0) as usize;
            let last_x = (cx + half_width).floor()
                .min(width as f32 - 1.0) as usize;
            if first_x <= last_x {
                let row = y * width;
                stencil[row + first_x..=row + last_x].fill(1);
            }
        }
    }

    fn apply_player_fog(&mut self, view: &NativeRenderWorldView<'_>,
        counts: &mut NativeRenderCounts,
    ) {
        if !view.player_authority.player_fog_of_war_enabled() {
            self.fog_live.clear();
            self.fog_fades.clear();
            self.fog_context = None;
            return;
        }
        // A viewer-side switch in the same Level/episode must not inherit
        // another player's dying-ship reveal masks.
        let context = (view.world_id, view.episode_revision,
            view.player_authority.player_viewer_side());
        if self.fog_context != Some(context) {
            self.fog_live.clear();
            self.fog_fades.clear();
            self.fog_context = Some(context);
            self.fog_render_pass = 0;
        }
        self.fog_render_pass = self.fog_render_pass.wrapping_add(1);
        if self.fog_render_pass == 0 {
            self.fog_render_pass = 1;
            self.fog_live.clear();
        }

        // Minimap visibility reuses this buffer after the main viewport,
        // including when one mask covers everything or none remains.
        self.fog_coverage.resize(self.pixels.len(), 0);
        let mut full_viewport = false;
        for (ship, body) in view.combat.render_ships() {
            let Some(radius) = Self::fog_source_radius(view, ship.entity_id)
                else { continue };
            counts.fog_sources += 1;
            self.fog_live.insert(ship.entity_id, FogLiveSource {
                position: body.motion.position, radius,
                seen_pass: self.fog_render_pass,
            });
            full_viewport |= self.fog_covers_viewport(body.motion.position, radius);
        }
        self.pending_fog_fades.clear();
        let pass = self.fog_render_pass;
        self.fog_live.retain(|&id, last| {
            if last.seen_pass == pass { return true; }
            if view.combat.ship(id).is_some_and(|ship| ship.vitals.is_dead()) {
                self.pending_fog_fades.push(FogFade {
                    position: last.position, radius: last.radius,
                    born_tick: view.tick,
                });
            }
            false
        });
        self.fog_fades.append(&mut self.pending_fog_fades);
        self.fog_fades.retain(|fade|
            fade.current_radius(view.tick, view.fixed_step_seconds) >= 1.5);
        for fade in &self.fog_fades {
            counts.fog_fading_sources += 1;
            full_viewport |= self.fog_covers_viewport(fade.position,
                fade.current_radius(view.tick, view.fixed_step_seconds));
        }
        if full_viewport {
            // Nothing is obscured; skip the O(screen pixels) fill, raster,
            // and masking pass entirely. Minimap visibility is still
            // computed from actual source discs.
            counts.fog_full_viewport_skips = 1;
            return;
        }
        if counts.fog_sources == 0 && counts.fog_fading_sources == 0 {
            // An empty player fleet has no sight. Clearing the final image
            // directly avoids two full-resolution stencil passes.
            self.pixels.fill(0x080e19);
            counts.fog_occluded_pixels = self.pixels.len();
            counts.fog_empty_viewport_fills = 1;
            return;
        }

        self.fog_coverage.fill(0);
        let stencil = &mut self.fog_coverage;
        let camera = self.camera;
        let width = self.width;
        let height = self.height;
        for source in self.fog_live.values() {
            Self::reveal_fog_circle(stencil, camera, width, height,
                source.position, source.radius);
        }
        for fade in &self.fog_fades {
            Self::reveal_fog_circle(stencil, camera, width, height,
                fade.position, fade.current_radius(view.tick, view.fixed_step_seconds));
        }
        for (pixel, visible) in self.pixels.iter_mut().zip(stencil.iter()) {
            if *visible == 0 {
                *pixel = 0x080e19;
                counts.fog_occluded_pixels += 1;
            }
        }
    }

    /// Use the authoritative hull and healing-zone colliders for clicks,
    /// retaining screen-space tolerance for small ships. Large hull edges
    /// must remain selectable even when their centers are offscreen.
    /// This read-only UI query never changes gameplay collision state.
    pub fn pick_ship(&self, view: &NativeRenderWorldView<'_>, x: f32, y: f32) -> Option<i64> {
        if !x.is_finite() || !y.is_finite() { return None; }
        let point = self.camera.screen_to_world(x, y, self.width, self.height);
        if !self.fog_visible_at(view, point) { return None; }
        let radius = 18.0 / self.camera.pixels_per_unit;
        let padding = Vec2::new(radius, radius);
        let bounds = Aabb { min: point - padding, max: point + padding };
        view.combat.physics().input_ship_ids_overlapping_box(bounds).ok()?
            .filter_map(|id| {
                let ship = view.combat.ship(id)?;
                if ship.vitals.is_dead() { return None; }
                let position = view.combat.ship_body(id)?.motion.position;
                let (sx, sy) = self.project(position);
                let distance_squared = (sx - x).powi(2) + (sy - y).powi(2);
                Some((distance_squared, id))
            })
            .min_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)))
            .map(|(_, id)| id)
    }

    /// Target selection uses cached authored hull overlap, with a small screen
    /// tolerance. This emits only identities; the host revalidates every action.
    pub fn pick_support_action(&self, view: &NativeRenderWorldView<'_>,
        mode: super::NativeSupportMode, point: Vec2, patrol_start: Option<Vec2>,
    ) -> Option<bees_gameplay::player_inputs::NativePlayerSupportAction> {
        use super::NativeSupportMode as Mode;
        use bees_gameplay::player_inputs::NativePlayerSupportAction as Action;
        if !point.is_finite() { return None; }
        if mode == Mode::Patrol {
            let start = patrol_start.filter(|start| start.is_finite())?;
            return Some(Action::Patrol {
                top_left: Vec2::new(start.x.min(point.x), start.y.max(point.y)),
                bottom_right: Vec2::new(start.x.max(point.x), start.y.min(point.y)),
            });
        }
        if !self.fog_visible_at(view, point) { return None; }
        let radius = 5.0 / self.camera.pixels_per_unit;
        let bounds = Aabb { min: point - Vec2::new(radius, radius),
            max: point + Vec2::new(radius, radius) };
        if mode == Mode::Mining {
            let id = view.combat.physics().obstacle_ids_overlapping_box(bounds).ok()?
                .filter_map(|id| view.combat.mining_asteroid_position(id)
                    .map(|position| ((position - point).length_squared(), id)))
                .min_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)))?.1;
            return Some(Action::Mining { asteroid_id: id });
        }
        let selected_side = self.selection.squads().first()
            .and_then(|owner| view.roster.get(*owner)).map(|squad| squad.side);
        let id = view.combat.physics().input_ship_ids_overlapping_box(bounds).ok()?
            .filter_map(|id| {
                let ship = view.combat.ship(id)?;
                if ship.vitals.is_dead() || selected_side.is_none() ||
                    selected_side.is_some_and(|side| if mode == Mode::Attack { ship.side == side } else { ship.side != side }) ||
                    (mode == Mode::Heal && ship.ship_type != bees_sim::combat_world::BEEHIVE_TYPE) ||
                    (mode == Mode::FullRetreat && ship.ship_type != bees_sim::combat_world::WARP_GATE_TYPE) {
                    return None;
                }
                let position = view.combat.ship_body(id)?.motion.position;
                Some(((position - point).length_squared(), id))
            }).min_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)))?.1;
        match mode {
            Mode::Attack => Some(Action::Attack { target: view.roster.owning_squad(id)? }),
            Mode::Guard => Some(Action::Guard { target: view.roster.owning_squad(id)? }),
            // Source input sends every Beehive in the clicked squad, in
            // GetShips order, rather than silently restricting it to one hull.
            Mode::Heal => Some(Action::Heal { beehive_ids: view.roster.get(view.roster.owning_squad(id)?)?
                .members.iter().copied().filter(|id| view.combat.ship(*id).is_some_and(|ship|
                    !ship.vitals.is_dead() && ship.ship_type == bees_sim::combat_world::BEEHIVE_TYPE))
                .collect() }),
            Mode::FullRetreat => Some(Action::FullRetreat { warp_gate_id: id }),
            _ => None,
        }
    }

    /// Right-click uses the same target resolution as the explicit tools.
    /// Ground movement is the caller's fallback when no command target is hit.
    pub fn pick_right_click_action(
        &self, view: &NativeRenderWorldView<'_>, point: Vec2,
    ) -> Option<bees_gameplay::player_inputs::NativePlayerSupportAction> {
        use super::NativeSupportMode as Mode;
        if self.selection.squads().is_empty() { return None; }
        [Mode::Attack, Mode::FullRetreat, Mode::Heal, Mode::Mining].into_iter()
            .find_map(|mode| self.pick_support_action(view, mode, point, None))
    }

    pub fn render(&mut self, view: &NativeRenderWorldView<'_>, bounds: Aabb) -> NativeRenderCounts {
        self.selection.synchronize(view);
        // Selected squad count can approach ship count in large battles.
        // Keep 1v1 allocation-free and reuse the large-selection lookup
        // rather than allocating a new hash table every rendered frame.
        let many_selected = self.selection.squads().len() > 8;
        self.selected_squad_lookup.clear();
        if many_selected {
            self.selected_squad_lookup.extend(self.selection.squads().iter().copied());
        }
        // The fixed software viewport is axis-aligned in world space.
        // Margin preserves center markers, health and selected-ship UI at
        // its edges. Cached physical hull bounds stay authoritative.
        let pad = Vec2::new(24.0, 24.0) * (1.0 / self.camera.pixels_per_unit);
        let viewport = Aabb {
            min: self.camera.screen_to_world(
                0.0, self.height as f32, self.width, self.height,
            ) - pad,
            max: self.camera.screen_to_world(
                self.width as f32, 0.0, self.width, self.height,
            ) + pad,
        };
        self.pixels.fill(0x080e19);
        let mut counts = NativeRenderCounts::default();
        self.shape(&ShipShape::Box { offset: (bounds.min + bounds.max) * 0.5,
            half_extents: (bounds.max - bounds.min) * 0.5 },
            Pose { position: Vec2::ZERO, rotation_degrees: 0.0 }, 0x33455e);
        if let Some(zone) = view.combat.active_extraction_bounds() {
            self.shape(&ShipShape::Box { offset: (zone.min + zone.max) * 0.5,
                half_extents: (zone.max - zone.min) * 0.5 },
                Pose { position: Vec2::ZERO, rotation_degrees: 0.0 }, 0x75dbab);
        }
        for (obstacle, body) in view.combat.render_obstacles() {
            counts.obstacles += 1;
            if body.primary_bounds().is_some_and(|hull| !hull.overlaps(viewport)) {
                counts.culled_obstacles += 1;
                continue;
            }
            let object = if obstacle.kind == bees_sim::obstacle::ObstacleKind::MapObject {
                view.combat.map_object(obstacle.entity_id)
            } else { None };
            let visible = view.combat.range_visible_map_object_ids().contains(&obstacle.entity_id);
            let is_fire_tank = object.is_some_and(|object| object.kind == bees_sim::map_object::MapObjectKind::FireTank);
            if let Some(tank) = object.filter(|_| is_fire_tank) {
                self.draw_fire_tank(tank, body.pose, view.tick as f32 * view.fixed_step_seconds);
            } else {
                self.shape(body.shape(), body.pose, if visible { 0x9bd2ae } else { 0x758496 });
            }
            if let Some(object) = object.filter(|_| visible) {
                let color = if is_fire_tank { 0xe1a34b } else { 0x9bd2ae };
                let (x, y) = self.project(body.pose.position);
                let health = (object.health as f32 / object.max_health as f32).clamp(0.0, 1.0);
                self.line(x - 10.0, y - 12.0, x + 10.0, y - 12.0, 0x3c2837);
                self.line(x - 10.0, y - 12.0, x - 10.0 + 20.0 * health, y - 12.0, color);
            }
        }
        for piece in view.combat.render_asteroid_pieces() {
            counts.pieces += 1;
            let position = piece.pose.position;
            if position.x < viewport.min.x || position.x > viewport.max.x ||
                position.y < viewport.min.y || position.y > viewport.max.y
            {
                counts.culled_pieces += 1;
                continue;
            }
            let c = (piece.sprite_opacity() * 160.0) as u32;
            self.cross(position, 1.0, c * 0x010101);
        }
        self.render_obstacle_debris(view, &mut counts);
        self.command_target_ship_ids.clear();
        for &owner in self.selection.squads() {
            let Some(selection) = view.commands.current_selection(owner) else { continue };
            if !matches!(selection.origin(),
                bees_gameplay::commands::NativeCommandOrigin::Local(local)
                if local.source == bees_gameplay::commands::NativeLocalCommandSource::Player
            ) || !matches!(selection.kind(),
                bees_hivemind::dispatch::LocalCommandKind::Aggressive |
                bees_hivemind::dispatch::LocalCommandKind::BombingRun)
            {
                continue;
            }
            let Some(enemy) = view.commands.selected_enemy(owner)
                .and_then(|owner| view.roster.get(owner)) else { continue };
            self.command_target_ship_ids.extend(enemy.members.iter().copied());
        }
        for (ship, body) in view.combat.render_ships() {
            counts.ships += 1;
            let selected_ship = view.roster.owning_squad(ship.entity_id).is_some_and(|owner|
                if many_selected { self.selected_squad_lookup.contains(&owner) }
                else { self.selection.squads().contains(&owner) }
            );
            let position = body.motion.position;
            let center_visible = position.x >= viewport.min.x &&
                position.x <= viewport.max.x &&
                position.y >= viewport.min.y &&
                position.y <= viewport.max.y;
            // Selected offscreen ships still draw their movement and target
            // lines, which may cross the camera. Never cull those commands.
            if !selected_ship && !center_visible &&
                body.primary_bounds().is_some_and(|hull| !hull.overlaps(viewport))
            {
                counts.culled_ships += 1;
                continue;
            }
            let color = side_color(ship.side);
            self.shape(body.shape(), Pose { position: body.motion.position,
                rotation_degrees: body.motion.rotation_degrees }, color);
            self.cross(body.motion.position, 1.5, color);
            let (x, y) = self.project(body.motion.position);
            if view.survival.is_some_and(|survival| survival.protected_ship_id == ship.entity_id) {
                let marker = 0xffd873;
                self.line(x - 5.0, y - 21.0, x, y - 16.0, marker);
                self.line(x, y - 16.0, x + 5.0, y - 21.0, marker);
                self.line(x + 5.0, y - 21.0, x, y - 26.0, marker);
                self.line(x, y - 26.0, x - 5.0, y - 21.0, marker);
            }
            if self.command_target_ship_ids.contains(&ship.entity_id) {
                // Source Squad.MarkTargets shows each surviving enemy
                // ship targeted by a player Aggressive/BombingRun order.
                // Draw in the existing culled ship pass and use actual
                // physical positions; do not maintain a target overlay
                // whose contents can outlive a command or squad.
                let marker = 0xffba6d;
                self.line(x - 11.0, y, x, y - 11.0, marker);
                self.line(x, y - 11.0, x + 11.0, y, marker);
                self.line(x + 11.0, y, x, y + 11.0, marker);
                self.line(x, y + 11.0, x - 11.0, y, marker);
                counts.command_target_markers += 1;
            }
            let health = ship.vitals.health() as f32 / ship.vitals.max_health() as f32;
            self.line(x - 10.0, y - 12.0, x + 10.0, y - 12.0, 0x3c2837);
            self.line(x - 10.0, y - 12.0, x - 10.0 + 20.0 * health, y - 12.0, color);
            if selected_ship {
                self.line(x - 14.0, y - 17.0, x + 14.0, y - 17.0, 0xffffff);
                self.line(x - 14.0, y + 14.0, x + 14.0, y + 14.0, 0xffffff);
                for slot in 0..5 {
                    if let Ok(Some(charge)) = view.combat.weapon_charge(ship.entity_id, slot) {
                        let progress = (charge.elapsed_seconds / charge.event_seconds).clamp(0.0, 1.0);
                        let cy = y + 18.0 + slot as f32 * 3.0;
                        self.line(x - 10.0, cy, x + 10.0, cy, 0x34435a);
                        self.line(x - 10.0, cy, x - 10.0 + 20.0 * progress, cy,
                            if charge.paused { 0xe1a34b } else { 0x62b9ff });
                    }
                }
                if self.show_weapon_ranges {
                    for slot in 0..5 {
                        let range = match view.combat.weapon_range(ship.entity_id, slot) {
                            Ok(Some(range)) => range,
                            Ok(None) => continue,
                            Err(_) => { counts.missing_range_data += 1; continue; },
                        };
                        if let Ok(Some(automatic)) = view.combat.weapon_automatic_status(ship.entity_id, slot) {
                            counts.automatic_targets += usize::from(
                                automatic.target_ship.is_some() || automatic.asteroid_aim_point.is_some(),
                            );
                            if let Some(point) = automatic.asteroid_aim_point {
                                self.cross(point, 2.5, 0xf6df85);
                                counts.aim_points += 1;
                            }
                        }
                        // The same post-physics RangeCollider contact list
                        // used by automatic firing is already indexed by
                        // weapon. Do not scan the whole fleet again for each
                        // selected turret merely to render its range overlay.
                        let contacts = match view.combat.weapon_range_contacts(ship.entity_id, slot) {
                            Ok(Some(contacts)) => contacts,
                            _ => { counts.missing_range_data += 1; continue; },
                        };
                        counts.range_contacts += contacts.ship_ids.len();
                        self.shape(&ShipShape::Circle { offset: Vec2::ZERO, radius: range.radius },
                            Pose { position: range.center, rotation_degrees: 0.0 },
                            if range.enabled { 0x355b80 } else { 0x555555 });
                        let Some(has_obstacles) = view.player_authority.weapon_obstacles_enabled()
                            else { counts.missing_range_data += 1; continue; };
                        // An inactive range cannot draw a valid contact.
                        if !range.enabled { continue; }
                        let enemies = view.combat.total_living_ships()
                            .saturating_sub(view.combat.living_ships_on_side(ship.side));
                        counts.range_candidates_culled +=
                            enemies.saturating_sub(contacts.ship_ids.len());
                        for &target_id in contacts.ship_ids {
                            counts.range_queries += 1;
                            match view.combat.query_weapon_target_geometry(ship.entity_id, slot,
                                target_id, view.collision_matrix, has_obstacles) {
                                Ok(Some(geometry)) if geometry.enemy_hull_overlaps_range => {
                                    counts.range_overlaps += 1;
                                    match view.combat.weapon_target_point(ship.entity_id, slot, target_id) {
                                        Ok(point) => { self.cross(point, 2.5, 0xf6df85); counts.aim_points += 1; },
                                        Err(_) => counts.missing_aim_data += 1,
                                    }
                                    counts.obstructed_ranges += usize::from(geometry.line_obstructed);
                                    let (ox, oy) = self.project(range.origin);
                                    let (tx, ty) = self.project(geometry.closest_hull_point);
                                    self.line(ox, oy, tx, ty,
                                        if geometry.line_obstructed { 0xe1a34b } else { 0x62b9ff });
                                },
                                Ok(_) => (),
                                Err(_) => counts.missing_range_data += 1,
                            }
                        }
                    }
                }
                if let Some(destination) = view.combat.ship_game_destination(ship.entity_id) {
                    let (tx, ty) = self.project(destination);
                    self.line(x, y, tx, ty, 0x526a8a);
                    self.cross(destination, 3.0, 0xddeaff);
                }
            }
        }
        for (_, body) in view.combat.render_projectiles() {
            counts.projectiles += 1;
            let position = body.motion.position;
            let center_visible = position.x >= viewport.min.x &&
                position.x <= viewport.max.x &&
                position.y >= viewport.min.y &&
                position.y <= viewport.max.y;
            let outside_hull = match body.shape() {
                None => true,
                Some(_) => body.primary_bounds()
                    .is_some_and(|hull| !hull.overlaps(viewport)),
            };
            // The script-entity center marker is real UI even when its
            // collider is displaced, so require both to be offscreen.
            if !center_visible && outside_hull {
                counts.culled_projectiles += 1;
                continue;
            }
            if let Some(shape) = body.shape() {
                self.shape(shape, Pose { position,
                    rotation_degrees: body.rotation_degrees }, 0xffd580);
            }
            // Collider-less script entities still have an inspectable position.
            self.cross(position, 1.0, 0xffd580);
        }
        // Only presentation pixels are masked. Commands, physics, learning
        // and the source-owned visibility caches remain unchanged.
        self.apply_player_fog(view, &mut counts);
        if let Some(bounds) = self.selection_box {
            self.shape(&ShipShape::Box { offset: (bounds.min + bounds.max) * 0.5,
                half_extents: (bounds.max - bounds.min) * 0.5 },
                Pose { position: Vec2::ZERO, rotation_degrees: 0.0 }, 0xddeaff);
        }
        if let Some(result) = view.terminal_result {
            self.draw_battle_result(view, result);
        } else if let Some(survival) = view.survival {
            let seconds = survival.seconds_remaining.ceil() as u64;
            let label = match view.combat.ship(survival.protected_ship_id) {
                Some(target) => format!(
                    "{}  {}/{} HP  |  Defend {:02}:{:02}",
                    if target.ship_type == bees_sim::ship_assets::HUMAN_TARGET_TYPE {
                        "Planetary Shield"
                    } else { "Protected ship" },
                    target.vitals.health(), target.vitals.max_health(),
                    seconds / 60, seconds % 60,
                ),
                None => format!(
                    "Protected target lost  |  {:02}:{:02}",
                    seconds / 60, seconds % 60,
                ),
            };
            text::draw_centered(&mut self.pixels, self.width, 8, &label, 1, [239, 247, 255]);
        }
        // The durable Level notification records arrivals exactly once.
        // A short visual acknowledgement survives an outbox ACK, but
        // vanishes naturally with the canonical simulation clock; paused
        // games do not consume its time and no frame timer is allocated.
        if view.terminal_result.is_none() {
            let y = if view.survival.is_some() { 8 + text::HEIGHT + 4 } else { 8 };
            let recently_arrived = view.last_reinforcement_tick
                .is_some_and(|tick| view.tick >= tick &&
                    (view.tick - tick) as f64 *
                        f64::from(view.fixed_step_seconds) < 3.0);
            if recently_arrived {
                text::draw_centered(&mut self.pixels, self.width, y,
                    "Reinforcements arrived", 1, [239, 196, 129]);
            } else if let Some(next_tick) = view.next_reinforcement_tick {
                let remaining_ticks = next_tick.saturating_sub(view.tick);
                let seconds = ((remaining_ticks as f64) *
                    f64::from(view.fixed_step_seconds)).ceil() as u64;
                let label = format!(
                    "Reinforcements: {} wave{}  |  Next {:02}:{:02}",
                    view.reinforcement_waves_pending,
                    if view.reinforcement_waves_pending == 1 { "" } else { "s" },
                    seconds / 60, seconds % 60,
                );
                text::draw_centered(&mut self.pixels, self.width, y,
                    &label, 1, [239, 196, 129]);
            }
        }
        if self.show_minimap {
            let (ships, enemies) = self.draw_minimap(view, bounds);
            counts.minimap_ship_markers = ships;
            counts.minimap_visible_enemy_markers = enemies;
        }
        counts.numbered_squad_rows = self.draw_squad_shortcuts(view);
        counts.special_ready_ships = self.draw_special_readiness(view);
        self.chasing_selection_all = !self.selection.squads().is_empty() &&
            self.selection.squads().iter().all(|&owner|
                view.player_authority.player_should_chase(owner));
        self.matched_speed_selection_all = !self.selection.squads().is_empty() &&
            self.selection.squads().iter().all(|&owner|
                view.player_authority.player_matched_speed(owner).is_some());
        self.ceasefire_selection_all = !self.selection.squads().is_empty() &&
            self.selection.squads().iter().all(|&owner|
                view.player_authority.player_cease_fire(owner) == Some(true));
        self.attack_on_sight_selection_all = !self.selection.squads().is_empty() &&
            self.selection.squads().iter().all(|&owner|
                view.player_authority.player_cease_fire(owner) == Some(false));
        self.locked_selection_all = !self.selection.squads().is_empty() &&
            self.selection.squads().iter().all(|&owner|
                view.player_authority.player_permissions(owner)
                    .is_some_and(|permission| permission.locked_on));
        counts.tactical_buttons_drawn = self.draw_tactical_bar();
        counts.selected_command_rows = self.draw_selected_command_status(view);
        self.draw_selected_priority(view);
        if self.show_shooting_menu { self.draw_shooting_menu(view); }
        counts
    }

    /// Stamp one vision disc into the already allocated main-screen fog
    /// buffer, restricted to the small fixed minimap plot. This avoids
    /// O(player ships x enemy ships) queries for marker visibility, and
    /// allocates no second minimap visibility state.
    fn stamp_minimap_fog(&mut self, layout: MiniMapLayout,
        position: Vec2, source_radius: f32,
    ) {
        if !position.is_finite() || source_radius <= 0.0 { return; }
        let (cx, cy) = layout.project_unbounded(position);
        let radius = source_radius * layout.scale;
        if !radius.is_finite() || radius <= 0.0 || !cx.is_finite() || !cy.is_finite() {
            return;
        }
        if cx + radius < layout.plot_left || cx - radius > layout.plot_right ||
            cy + radius < layout.plot_top || cy - radius > layout.plot_bottom {
            return;
        }
        let top = (cy - radius).ceil().max(layout.plot_top.ceil()) as usize;
        let bottom = (cy + radius).floor()
            .min(layout.plot_bottom.floor()) as usize;
        if top > bottom { return; }
        let radius_sq = radius * radius;
        for y in top..=bottom {
            let dy = y as f32 - cy;
            if dy.abs() > radius { continue; }
            let width = (radius_sq - dy * dy).max(0.0).sqrt();
            if cx + width < layout.plot_left || cx - width > layout.plot_right {
                continue;
            }
            let left = (cx - width).ceil().max(layout.plot_left.ceil()) as usize;
            let right = (cx + width).floor()
                .min(layout.plot_right.floor()) as usize;
            if left <= right {
                let row = y * self.width;
                self.fog_coverage[row + left..=row + right].fill(1);
            }
        }
    }

    /// The physical minimap draws live player ships and currently visible
    /// opponents, rather than silently hiding every enemy. Source fog
    /// limits enemy markers using the same live player masks and fading
    /// vision used by the main viewport. When source fog is OFF, all live
    /// ships are visible as in the unmasked tactical battlefield.
    fn draw_minimap(&mut self, view: &NativeRenderWorldView<'_>, bounds: Aabb)
        -> (usize, usize)
    {
        let Some(layout) = MiniMapLayout::new(self.width, self.height, bounds)
            else { return (0, 0) };
        let mut markers = 0usize;
        let mut enemies = 0usize;
        for y in layout.top..layout.bottom {
            self.pixels[y * self.width + layout.left
                ..y * self.width + layout.right].fill(0x101c2e);
        }
        let edge = 0x6e829e;
        let x0 = layout.plot_left;
        let y0 = layout.plot_top;
        let x1 = layout.plot_right;
        let y1 = layout.plot_bottom;
        self.line(x0, y0, x1, y0, edge);
        self.line(x1, y0, x1, y1, edge);
        self.line(x1, y1, x0, y1, edge);
        self.line(x0, y1, x0, y0, edge);

        let fog = view.player_authority.player_fog_of_war_enabled();
        if fog {
            // The same allocation already holds the main-view fog stencil.
            // It has already been used for that viewport's pixel mask; only
            // its minimap panel region needs resetting here.
            for y in layout.top..layout.bottom {
                let row = y * self.width;
                self.fog_coverage[row + layout.left..row + layout.right].fill(0);
            }
            for (ship, body) in view.combat.render_ships() {
                let Some(radius) = Self::fog_source_radius(view, ship.entity_id)
                    else { continue };
                self.stamp_minimap_fog(layout, body.motion.position, radius);
            }
            for index in 0..self.fog_fades.len() {
                let fade = self.fog_fades[index];
                self.stamp_minimap_fog(layout, fade.position,
                    fade.current_radius(view.tick, view.fixed_step_seconds));
            }
        }

        for (ship, body) in view.combat.render_ships() {
            let owner = view.roster.owning_squad(ship.entity_id);
            let controlled = owner.is_some_and(|id| view.is_local_player_squad(id));
            let Some((x, y)) = layout.project(body.motion.position)
                else { continue };
            let px = x.round() as i32;
            let py = y.round() as i32;
            if !controlled && fog &&
                self.fog_coverage.get(py as usize * self.width + px as usize) != Some(&1) {
                // Never reveal the position of a concealed enemy or an
                // unrelated player's units in a masked local viewport.
                continue;
            }
            markers += 1;
            // Non-commandable same-side escorts remain friendly; only
            // opposite-side units count as tactical enemy sightings.
            if view.player_authority.player_viewer_side()
                .is_some_and(|side| ship.side != side) {
                enemies += 1;
            }
            let selected = controlled && owner.is_some_and(|owner|
                if self.selection.squads().len() > 8 {
                    self.selected_squad_lookup.contains(&owner)
                } else {
                    self.selection.squads().contains(&owner)
                }
            );
            let color = if selected { 0xffffff } else { side_color(ship.side) };
            let radius = if selected { 3 } else { 1 };
            for yy in (py - radius)..=(py + radius) {
                for xx in (px - radius)..=(px + radius) {
                    if xx >= layout.plot_left.floor() as i32 &&
                        xx <= layout.plot_right.ceil() as i32 &&
                        yy >= layout.plot_top.floor() as i32 &&
                        yy <= layout.plot_bottom.ceil() as i32 &&
                        xx >= 0 && yy >= 0 &&
                        (xx as usize) < self.width && (yy as usize) < self.height
                    {
                        self.pixels[yy as usize * self.width + xx as usize] = color;
                    }
                }
            }
        }
        let bottom_left = self.camera.screen_to_world(
            0.0, self.height as f32, self.width, self.height);
        let top_right = self.camera.screen_to_world(
            self.width as f32, 0.0, self.width, self.height);
        if let Some((x0, y0, x1, y1)) = layout.camera_rect(bottom_left, top_right) {
            let color = 0xe2c473;
            self.line(x0, y0, x1, y0, color);
            self.line(x1, y0, x1, y1, color);
            self.line(x1, y1, x0, y1, color);
            self.line(x0, y1, x0, y0, color);
        }
        (markers, enemies)
    }

    /// A lightweight source-squad tab strip. Shortcut numbers come from the
    /// actual schema-29 Unity export, not an index assigned by the renderer.
    /// It is omitted for legacy scenes lacking authoritative squad numbers.
    /// Only user-controlled ships are listed; enemy roster data stays private.
    fn draw_squad_shortcuts(&mut self, view: &NativeRenderWorldView<'_>) -> usize {
        if self.width < 560 || self.height < 360 { return 0; }
        let mut row = 0usize;
        for &owner in view.roster.source_order() {
            let Some(squad) = view.roster.get(owner) else { continue };
            let Some(number) = squad.source_hotkey_number else { continue };
            if number > 10 || !view.is_local_player_squad(owner) {
                continue;
            }
            let Some(&ship_id) = squad.members.first() else { continue };
            let Some(ship) = view.combat.ship(ship_id) else { continue };
            let selected = if self.selection.squads().len() > 8 {
                self.selected_squad_lookup.contains(&owner)
            } else {
                self.selection.squads().contains(&owner)
            };
            let key = if number == 10 { 0 } else { number };
            let label = format!("{key}: type {} / {} ships",
                ship.ship_type, squad.members.len());
            let Some(rect) = tactical_hud::squad_tab_rect(
                self.width, self.height, row, text::HEIGHT,
                text::width(&label, 1)) else { break };
            for scanline in rect.top..rect.bottom {
                let start = scanline * self.width + rect.left;
                self.pixels[start..start + rect.right - rect.left].fill(
                    if selected { 0x35465d } else { 0x142338 });
            }
            text::draw_at(&mut self.pixels, self.width, 14,
                rect.top + 2,
                &label, 1, if selected { [255, 241, 194] }
                    else { [182, 211, 239] });
            row += 1;
            if row == 10 { break; }
        }
        row
    }

    /// Inspect only selected, currently authorized squads. The availability
    /// indicators are sourced from the same combat capability state and
    /// fleet Bomb registration consumed by the player input plan.
    fn draw_special_readiness(&mut self, view: &NativeRenderWorldView<'_>) -> usize {
        if self.width < 560 || self.height < 320 ||
            self.selection.squads().is_empty() { return 0; }
        let mut scouts = 0usize;
        let mut scouts_ready = 0usize;
        let mut barges = 0usize;
        let mut barges_ready = 0usize;
        let mut charge_barges = 0usize;
        let mut charges_ready = 0usize;
        for &owner in self.selection.squads() {
            if !view.player_authority.player_permissions(owner)
                .is_some_and(|permission| permission.user_controlled &&
                    permission.can_accept_input && !permission.locked_on) {
                continue;
            }
            let Some(squad) = view.roster.get(owner) else { continue };
            for &ship_id in &squad.members {
                let Some(ship) = view.combat.ship(ship_id) else { continue };
                let Some(body) = view.combat.ship_body(ship_id) else { continue };
                if ship.vitals.is_dead() || !body.active ||
                    body.motion.is_policy_controlled ||
                    view.combat.policy_frame(ship_id).is_some() { continue; }
                match ship.ship_type {
                    18 if view.combat.game_scout_configured(ship_id) => {
                        scouts += 1;
                        scouts_ready += usize::from(
                            view.combat.player_scout_beacon_ready(ship_id));
                    }
                    10 if view.combat.configured_fire_barge_bomb_power(ship_id)
                        .is_some() => {
                        barges += 1;
                        barges_ready += usize::from(
                            view.combat.player_fire_barge_detonation_ready(ship_id));
                    }
                    0 => {
                        // Legacy snapshots intentionally lack authenticated
                        // CanOverrideBounds and player Charge configuration.
                        // Do not advertise an unsupported ability.
                        if view.combat.player_game_barge_configured(ship_id) {
                            charge_barges += 1;
                            charges_ready += usize::from(
                                view.combat.player_game_barge_charge_ready(ship_id));
                        }
                    }
                    _ => {}
                }
            }
        }
        if scouts == 0 && barges == 0 && charge_barges == 0 { return 0; }
        let x = self.width - 268;
        let y = self.height - 84;
        for row in y.saturating_sub(5)..(y + 78).min(self.height) {
            self.pixels[row * self.width + x - 7
                ..row * self.width + x + 256].fill(0x15243a);
        }
        let beacon = format!("K: Beacons ready {scouts_ready}/{scouts}");
        let detonate = format!("D: Fire Barges ready {barges_ready}/{barges}");
        text::draw_at(&mut self.pixels, self.width, x, y,
            &beacon, 1, if scouts_ready > 0 { [178, 230, 188] }
                else { [136, 151, 172] });
        text::draw_at(&mut self.pixels, self.width, x,
            y + text::HEIGHT + 9, &detonate, 1,
            if barges_ready > 0 { [255, 202, 148] }
            else { [136, 151, 172] });
        let charge = format!("Q: Barge charge ready {charges_ready}/{charge_barges}");
        text::draw_at(&mut self.pixels, self.width, x,
            y + (text::HEIGHT + 9) * 2, &charge, 1,
            if charges_ready > 0 { [244, 211, 142] }
            else { [136, 151, 172] });
        scouts_ready + barges_ready + charges_ready
    }

    fn draw_tactical_bar(&mut self) -> usize {
        let has_selection = !self.selection.squads().is_empty();
        let mut drawn = 0usize;
        for (index, action) in tactical_hud::ACTIONS.iter().copied().enumerate() {
            let Some(rect) = tactical_hud::action_rect(self.width, self.height, index)
                else { return drawn };
            let background = if !has_selection &&
                action != tactical_hud::HudAction::SelectAll {
                0x172334
            } else if self.show_shooting_menu && action == tactical_hud::HudAction::Shooting {
                0x456c78
            } else if action == tactical_hud::HudAction::CeaseFire &&
                self.ceasefire_selection_all {
                0x456c78
            } else if action == tactical_hud::HudAction::AttackOnSight &&
                self.attack_on_sight_selection_all {
                0x456c78
            } else if action == tactical_hud::HudAction::MatchSpeed &&
                self.matched_speed_selection_all {
                0x456c78
            } else if action == tactical_hud::HudAction::FreeSpeed &&
                !has_selection {
                0x172334
            } else if action == tactical_hud::HudAction::Chase &&
                self.chasing_selection_all {
                0x456c78
            } else if action == tactical_hud::HudAction::LockOn &&
                !self.selection.squads().is_empty() &&
                self.locked_selection_all {
                0x456c78
            } else if self.active_tactical_action == Some(action) {
                0x456c78
            } else if self.hovered_tactical_action == Some(action) {
                0x394e65
            } else if matches!(action,
                tactical_hud::HudAction::Detonate |
                tactical_hud::HudAction::StopFire
            ) { 0x503a38 } else { 0x23334a };
            for y in rect.top..rect.bottom {
                self.pixels[y * self.width + rect.left
                    ..y * self.width + rect.right].fill(background);
            }
            let label = action.label();
            let x = rect.left + (rect.right - rect.left)
                .saturating_sub(text::width(label, 1)) / 2;
            let y = rect.top + (rect.bottom - rect.top)
                .saturating_sub(text::HEIGHT) / 2;
            let tone = if has_selection ||
                action == tactical_hud::HudAction::SelectAll {
                [232, 241, 251]
            } else { [101, 114, 132] };
            text::draw_at(&mut self.pixels, self.width, x, y,
                label, 1, tone);
            drawn += 1;
        }
        drawn
    }

    /// Selected-squad command status is borrowed from the same canonical
    /// director as timed AI command execution, weapon strategy selection
    /// and durable outcome settlement. Avoid a second UI status machine.
    fn draw_selected_command_status(&mut self, view: &NativeRenderWorldView<'_>) -> usize {
        if self.selection.squads().is_empty() ||
            self.width < 750 || self.height < 400 {
            return 0;
        }
        let left = self.width - 318;
        let mut row = 0usize;
        for &owner in self.selection.squads() {
            let Some(squad) = view.roster.get(owner) else { continue };
            if !view.player_authority.player_permissions(owner)
                .is_some_and(|permission| permission.user_controlled) {
                continue;
            }
            let label = squad.source_hotkey_number
                .map_or_else(|| "Squad".to_owned(),
                    |number| format!("Squad {number}"));
            let command = view.commands.current_kind(owner)
                .map_or_else(|| "Idle".to_owned(), |kind| format!("{kind:?}"));
            let status = if let Some(enemy) = view.commands.selected_enemy(owner)
                .and_then(|enemy| view.roster.get(enemy))
            {
                format!("{label}: {command} (enemy side {})", enemy.side)
            } else {
                format!("{label}: {command}")
            };
            let y = 16 + row * (text::HEIGHT + 9);
            if y + text::HEIGHT + 10 >= self.height / 2 { break; }
            for pixel_y in (y - 3)..(y + text::HEIGHT + 6) {
                self.pixels[pixel_y * self.width + left
                    ..pixel_y * self.width + left + 306].fill(0x17283e);
            }
            let status = if status.len() > 44 { &status[..44] } else { &status };
            text::draw_at(&mut self.pixels, self.width,
                left + 8, y, status, 1, [221, 236, 249]);
            row += 1;
            if row == 8 { break; }
        }
        row
    }

    /// The live source Squad.GetShootingStrategy, including user changes
    /// since export. The corresponding weapon targeting is read from this
    /// metadata whenever the squad has no active command.
    fn draw_selected_priority(&mut self, view: &NativeRenderWorldView<'_>) {
        if self.selection.squads().is_empty() || self.width < 520 ||
            self.height < 430 { return; }
        let first = self.selection.squads().first().and_then(|&owner|
            view.player_authority.player_shooting_strategy(owner));
        let label = match first {
            Some(shooting) if self.selection.squads().iter().all(|&owner|
                view.player_authority.player_shooting_strategy(owner) == Some(shooting)
            ) => format!("Shooting priority: {} (S to choose)", shooting.name),
            Some(_) => "Shooting priority: mixed (S to choose)".to_owned(),
            None => "Shooting priority unavailable".to_owned(),
        };
        // Unlike the client-only hover feedback, this renderer method
        // also builds in headless-only configurations. The glyph renderer
        // already clips to the framebuffer without client-only text::fit.
        let w = text::width(&label, 1);
        let x = self.width.saturating_sub(w) / 2;
        text::draw_at(&mut self.pixels, self.width, x,
            self.height - 190, &label, 1, [175, 207, 227]);
        let mut count = 0usize;
        let mut first_speed = None;
        let mut same_speed = true;
        for &owner in self.selection.squads() {
            if let Some(speed) = view.player_authority.player_matched_speed(owner) {
                count += 1;
                if let Some(first) = first_speed {
                    same_speed &= speed == first;
                } else {
                    first_speed = Some(speed);
                }
            }
        }
        if let Some(speed) = first_speed {
            let value = if count == self.selection.squads().len() && same_speed {
                format!("Matched speed: {speed:.2}")
            } else {
                format!("{count}/{} squads have matched speed",
                    self.selection.squads().len())
            };
            let x = self.width.saturating_sub(text::width(&value, 1)) / 2;
            text::draw_at(&mut self.pixels, self.width,
                x, self.height - 216, &value, 1, [188, 217, 255]);
        }
        let cease_count = self.selection.squads().iter().filter(|&&owner|
            view.player_authority.player_cease_fire(owner) == Some(true)).count();
        if cease_count > 0 {
            let label = format!("{cease_count}/{} squads set to Cease Fire",
                self.selection.squads().len());
            let x = self.width.saturating_sub(text::width(&label, 1)) / 2;
            text::draw_at(&mut self.pixels, self.width,
                x, self.height - 229, &label, 1, [255, 201, 182]);
        }
        let chase_count = self.selection.squads().iter().filter(|&&owner|
            view.player_authority.player_should_chase(owner)).count();
        if chase_count > 0 {
            let label = format!("{chase_count}/{} squads chasing (E to enable, C to hold)",
                self.selection.squads().len());
            let x = self.width.saturating_sub(text::width(&label, 1)) / 2;
            text::draw_at(&mut self.pixels, self.width,
                x, self.height - 203, &label, 1, [197, 222, 165]);
        }
        let locked_count = self.selection.squads().iter().filter(|&&owner|
            view.player_authority.player_permissions(owner)
                .is_some_and(|permission| permission.locked_on)
        ).count();
        if locked_count > 0 {
            let label = format!("{locked_count}/{} squads locked on (O to toggle)",
                self.selection.squads().len());
            let x = self.width.saturating_sub(text::width(&label, 1)) / 2;
            text::draw_at(&mut self.pixels, self.width,
                x, self.height - 176, &label, 1, [255, 205, 132]);
        }
    }

    /// Present the canonical 40-entry source registry. Current selection
    /// is read from the SAME live metadata used by automatic weapon inputs;
    /// different selected strategies produce no misleading highlighted value.
    fn draw_shooting_menu(&mut self, view: &NativeRenderWorldView<'_>) {
        // Cover the battlefield under the modal without creating another
        // surface, renderer or dynamically allocated panel bitmap.
        let Some(first) = tactical_hud::shooting_rect(self.width, self.height, 0)
            else { return };
        let Some(last) = tactical_hud::shooting_rect(self.width, self.height, 39)
            else { return };
        let left = first.left.saturating_sub(12);
        let right = (last.right + 12).min(self.width);
        let top = first.top.saturating_sub(27);
        let bottom = (last.bottom + 12).min(self.height);
        for y in top..bottom {
            self.pixels[y * self.width + left
                ..y * self.width + right].fill(0x101d2e);
        }
        text::draw_at(&mut self.pixels, self.width, left + 12,
            top + 6, "SHOOTING PRIORITY - arrows + Enter or click / S or Esc cancels",
            1, [225, 235, 250]);
        let selected = self.selection.squads().iter()
            .filter_map(|&owner| view.player_authority.player_shooting_strategy(owner))
            .map(|value| value.id)
            .fold(None, |current, value| match current {
                None => Some(value),
                Some(other) if other == value => Some(value),
                _ => Some(0),
            });
        for (index, &name) in bees_hivemind::protocol::SHOOTING_STRATEGIES.iter().enumerate() {
            let Some(rect) = tactical_hud::shooting_rect(self.width, self.height, index)
                else { return };
            let active = selected == Some((index + 1) as u8);
            let focused = self.focused_shooting_strategy == (index + 1) as u8;
            for y in rect.top..rect.bottom {
                self.pixels[y * self.width + rect.left
                    ..y * self.width + rect.right].fill(
                        if focused { 0x546d83 }
                        else if active { 0x476b70 } else { 0x23344d });
            }
            let label = format!("{:>2}: {name}", index + 1);
            text::draw_at(&mut self.pixels, self.width,
                rect.left + 8, rect.top + 5, &label, 1,
                if active { [247, 255, 204] }
                else if focused { [255, 255, 255] }
                else { [227, 239, 252] });
        }
    }

    /// Keep the current command prompt, feedback, and hovered action
    /// explanation visible inside the actual framebuffer. Window managers
    /// truncate long title strings, particularly over remote desktop.
    /// This remains optional presentation work; the headless simulator does
    /// not allocate or render a second command message.
    #[cfg(feature = "client")]
    pub fn draw_tactical_feedback(&mut self, feedback: &str) {
        if self.width < 520 || self.height < 250 ||
            self.show_shooting_menu { return; }
        let hint = self.hovered_tactical_action
            .map(tactical_hud::HudAction::hint)
            .unwrap_or(feedback);
        if hint.is_empty() { return; }
        let message = text::fit(hint, self.width.saturating_sub(72).min(760));
        let w = (text::width(message, 1) + 20).min(self.width);
        let left = (self.width - w) / 2;
        // The command strip starts at height-134. Draw this hint ABOVE
        // its buttons instead of obscuring the first row.
        let top = if self.height >= 430 { self.height - 166 }
            else { self.height - 42 };
        for y in top..(top + text::HEIGHT + 12).min(self.height) {
            self.pixels[y * self.width + left
                ..y * self.width + left + w].fill(0x16263a);
        }
        text::draw_at(&mut self.pixels, self.width,
            left + 10, top + 6, message, 1, [231, 237, 248]);
    }

    fn draw_battle_result(
        &mut self, view: &NativeRenderWorldView<'_>, result: bees_sim::episode_terminal::EpisodeTerminalResult,
    ) {
        let title = battle_outcome_label(result);
        let reason = if result.extraction_victory { "Extraction" }
            else if view.survival.is_some() { "Defense" }
            else if result.timed_out { "Time limit" } else { "Elimination" };
        let detail = format!("{reason}  /  {:.2} s", result.duration_seconds);
        let context = format!("World {}  /  Episode {}", view.world_id.0, view.episode_revision + 1);
        let title_scale = if self.width >= 400 && self.height >= 176 { 2 } else { 1 };
        let top = if self.height >= 200 { 20 } else { 0 };
        let width = (text::width(&title, title_scale).max(text::width(&detail, 1))
            .max(text::width(&context, 1)) + 40).min(self.width);
        let left = (self.width - width) / 2;
        let panel_height = (title_scale + 2) * text::HEIGHT + 20;
        for y in top..(top + panel_height).min(self.height) {
            let start = y * self.width + left;
            self.pixels[start..start + width].fill(if y < top + 2 { 0xe1a34b } else { 0x152133 });
        }
        text::draw_centered(&mut self.pixels, self.width, top + 8, &context, 1, [155, 178, 206]);
        let title_top = top + 8 + text::HEIGHT;
        text::draw_centered(&mut self.pixels, self.width, title_top, &title, title_scale, [239, 247, 255]);
        text::draw_centered(&mut self.pixels, self.width, title_top + title_scale * text::HEIGHT,
            &detail, 1, [194, 211, 230]);
    }

    #[cfg(feature = "client")]
    pub(crate) fn draw_campaign_dialogue(&mut self, speaker: &str, message: &str) {
        let panel_width = self.width.min(680);
        let max_text_width = panel_width.saturating_sub(32).max(1);
        let mut rest = message;
        let mut lines = Vec::new();
        while !rest.is_empty() {
            let prefix = text::fit(rest, max_text_width);
            if prefix.is_empty() { break; }
            let length = if prefix.len() == rest.len() { prefix.len() }
                else { prefix.rfind(' ').filter(|&index| index != 0).unwrap_or(prefix.len()) };
            lines.push(&rest[..length]);
            rest = rest[length..].trim_start();
        }
        let panel_height = ((lines.len() + 2) * text::HEIGHT + 22).min(self.height);
        let top = self.height - panel_height;
        let left = (self.width - panel_width) / 2;
        for y in top..self.height {
            let color = if y == top { 0x75dbab } else { 0x111e2e };
            self.pixels[y * self.width + left..y * self.width + left + panel_width].fill(color);
        }
        text::draw_centered(&mut self.pixels, self.width, top + 6, speaker, 1, [117, 219, 171]);
        let mut y = top + 9 + text::HEIGHT;
        for line in lines {
            text::draw_centered(&mut self.pixels, self.width, y, line, 1, [239, 247, 255]);
            y += text::HEIGHT;
        }
        text::draw_centered(&mut self.pixels, self.width, y + 4, "L continue  /  K dismiss", 1, [155, 178, 206]);
    }

    fn render_obstacle_debris(
        &mut self, view: &NativeRenderWorldView<'_>, counts: &mut NativeRenderCounts,
    ) {
        use bees_sim::combat_world::CombatEvent;
        let frame = (view.world_id, view.episode_revision, view.tick);
        if self.debris_frame != Some(frame) {
            if self.debris_frame.is_some_and(|previous| (previous.0, previous.1) != (frame.0, frame.1)) {
                self.obstacle_debris.clear();
            }
            self.debris_frame = Some(frame);
            for event in view.combat.events() {
                let CombatEvent::FireTankObstacleBrokenApart { obstacle_id, origin, bounds, .. } = *event else { continue };
                // Fire Tank prefab defaults. This stream belongs exclusively
                // to the tactical renderer and never touches world RNG.
                let mut random = RandomStream::new((obstacle_id as u64).wrapping_mul(397) ^
                    u64::from(origin.x.to_bits()) ^ u64::from(origin.y.to_bits()));
                let mut unit = || (random.next_u64() >> 40) as f32 / 16_777_216.0;
                let sprite_count = fire_tank_debris_atlas().sprites.len();
                for _ in 0..24 {
                    let sprite = (unit() * sprite_count as f32) as usize;
                    let position = bounds.min + Vec2::new(
                        (bounds.max.x - bounds.min.x) * unit(),
                        (bounds.max.y - bounds.min.y) * unit(),
                    );
                    let radial = position - origin;
                    let direction = if radial.length_squared() < 0.01 {
                        let angle = unit() * std::f32::consts::TAU;
                        Vec2::new(angle.cos(), angle.sin())
                    } else {
                        radial * radial.length_squared().sqrt().recip()
                    };
                    let spread = Vec2::new(unit() * 2.0 - 1.0, unit() * 2.0 - 1.0) * 0.35;
                    let direction = direction + spread;
                    let velocity = direction * ((4.0 + 7.0 * unit()) * direction.length_squared().sqrt().recip());
                    self.obstacle_debris.push(ObstacleDebris {
                        born: view.tick, position, velocity,
                        rotation: unit() * 360.0, spin: (2.0 * unit() - 1.0) * 360.0,
                        lifetime: 1.5 * (0.8 + 0.4 * unit()), scale: 3.0 + 3.0 * unit(),
                        sprite,
                    });
                }
            }
        }
        let age = |born| view.tick.saturating_sub(born) as f32 * view.fixed_step_seconds;
        self.obstacle_debris.retain(|piece| age(piece.born) < piece.lifetime);
        for index in 0..self.obstacle_debris.len() {
            let piece = &self.obstacle_debris[index];
            let seconds = age(piece.born);
            let position = piece.position + piece.velocity * (-(-1.25 * seconds).exp_m1() / 1.25);
            counts.pieces += 1;
            let alpha = ((1.0 - seconds / piece.lifetime) / 0.4).min(1.0);
            let angle = (piece.rotation + piece.spin * seconds).to_radians();
            let atlas = fire_tank_debris_atlas();
            let scale = piece.scale * self.camera.pixels_per_unit / atlas.pixels_per_unit;
            if !self.draw_sprite(&atlas.sprites[piece.sprite], position, angle, scale, alpha) {
                counts.culled_pieces += 1;
            }
        }
    }

    fn draw_fire_tank(&mut self, tank: &bees_sim::map_object::CombatMapObject, pose: Pose, seconds: f32) {
        static BODY: OnceLock<SpriteAtlas> = OnceLock::new();
        static SMOKE: OnceLock<SpriteAtlas> = OnceLock::new();
        let body = BODY.get_or_init(|| SpriteAtlas::decode(include_bytes!("../../../../assets/fire-tank-body.rgba")));
        const STAGES: usize = 4;
        let lost = i64::from(tank.max_health) - i64::from(tank.health.max(0));
        let stage = ((lost * STAGES as i64) / i64::from(tank.max_health))
            .clamp(0, STAGES as i64 - 1) as usize;
        // Stable cosmetic variation across frames, independent of gameplay RNG.
        let variant = tank.entity_id as usize % (body.sprites.len() / STAGES);
        let angle = pose.rotation_degrees.to_radians();
        self.draw_sprite(&body.sprites[variant * STAGES + stage], pose.position, angle,
            self.camera.pixels_per_unit / body.pixels_per_unit, 1.0);
        if stage == 0 { return; }

        let smoke = SMOKE.get_or_init(|| SpriteAtlas::decode(include_bytes!("../../../../assets/fire-tank-smoke.rgba")));
        let frame = (seconds * 5.0) as usize;
        // CanisterBomb.SmokePositions, in prefab-local coordinates.
        let offsets = [Vec2::new(2.25, -1.2), Vec2::new(-2.65, 1.4), Vec2::new(-5.1, -0.2)];
        for (plume, &offset) in offsets[..stage].iter().enumerate() {
            self.draw_sprite(&smoke.sprites[(frame + plume) % smoke.sprites.len()],
                pose.transform_point(offset), angle, self.camera.pixels_per_unit / smoke.pixels_per_unit, 1.0);
        }
    }

    fn draw_sprite(&mut self, sprite: &NativeSprite, position: Vec2, angle: f32, scale: f32, alpha: f32) -> bool {
        let (center_x, center_y) = self.project(position);
        let (sin, cos) = angle.sin_cos();
        let half_w = sprite.width as f32 * 0.5;
        let half_h = sprite.height as f32 * 0.5;
        let extent_x = scale * (half_w * cos.abs() + half_h * sin.abs());
        let extent_y = scale * (half_w * sin.abs() + half_h * cos.abs());
        let left = (center_x - extent_x).floor().max(0.0) as usize;
        let right = (center_x + extent_x).ceil().clamp(0.0, self.width as f32) as usize;
        let top = (center_y - extent_y).floor().max(0.0) as usize;
        let bottom = (center_y + extent_y).ceil().clamp(0.0, self.height as f32) as usize;
        if left >= right || top >= bottom { return false; }
        for y in top..bottom {
            let dy = (y as f32 + 0.5 - center_y) / scale;
            for x in left..right {
                let dx = (x as f32 + 0.5 - center_x) / scale;
                let u = (dx * cos - dy * sin + half_w).floor() as isize;
                let v = (dx * sin + dy * cos + half_h).floor() as isize;
                if u < 0 || v < 0 || u as usize >= sprite.width || v as usize >= sprite.height { continue; }
                let offset = (v as usize * sprite.width + u as usize) * 4;
                let rgba = &sprite.pixels[offset..offset + 4];
                let opacity = (f32::from(rgba[3]) * alpha).round() as u32;
                if opacity == 0 { continue; }
                let pixel = &mut self.pixels[y * self.width + x];
                *pixel = text::blend(*pixel, [rgba[0], rgba[1], rgba[2]], opacity);
            }
        }
        true
    }

    /// Portable capture for diagnostics, using exactly the window's pixel buffer.
    pub fn write_ppm(&self, mut writer: impl Write) -> io::Result<()> {
        write!(writer, "P6\n{} {}\n255\n", self.width, self.height)?;
        let mut row = Vec::with_capacity(self.width * 3);
        for pixels in self.pixels.chunks(self.width) {
            row.clear();
            for &p in pixels { row.extend_from_slice(&[(p >> 16) as u8, (p >> 8) as u8, p as u8]); }
            writer.write_all(&row)?;
        }
        Ok(())
    }

    fn project(&self, point: Vec2) -> (f32, f32) {
        ((point.x - self.camera.center.x) * self.camera.pixels_per_unit + self.width as f32 * 0.5,
         (self.camera.center.y - point.y) * self.camera.pixels_per_unit + self.height as f32 * 0.5)
    }

    fn cross(&mut self, point: Vec2, radius: f32, color: u32) {
        let (x, y) = self.project(point);
        self.line(x - radius, y, x + radius, y, color);
        self.line(x, y - radius, x, y + radius, color);
    }

    fn shape(&mut self, shape: &ShipShape, pose: Pose, color: u32) {
        // Match Pose::transform_point exactly, but compute the rotation
        // once per shape instead of twice per edge. Large authored polygon
        // hulls and weapon-range circles can have dozens of segments.
        let (sin, cos) = pose.rotation_degrees.to_radians().sin_cos();
        let world_point = |point: Vec2| Vec2::new(
            pose.position.x + point.x * cos - point.y * sin,
            pose.position.y + point.x * sin + point.y * cos,
        );
        let mut edge = |a, b| {
            let (x0, y0) = self.project(world_point(a));
            let (x1, y1) = self.project(world_point(b));
            self.line(x0, y0, x1, y1, color);
        };
        match shape {
            ShipShape::Polygon(vertices) => {
                if let Some(&last) = vertices.last() {
                    let mut previous = last;
                    for &point in vertices.iter() { edge(previous, point); previous = point; }
                }
            }
            ShipShape::Box { offset: o, half_extents: h } => {
                let corners = [*o + Vec2::new(-h.x, -h.y), *o + Vec2::new(h.x, -h.y),
                    *o + Vec2::new(h.x, h.y), *o + Vec2::new(-h.x, h.y)];
                for i in 0..4 { edge(corners[i], corners[(i + 1) % 4]); }
            }
            ShipShape::Circle { offset, radius } => {
                // All circles use the identical 32-segment tactical outline.
                // Calculate unit vectors once, not 32 trigonometric calls for
                // every projectile, ship and inspection range in every frame.
                const SEGMENTS: usize = 32;
                static UNIT_CIRCLE: OnceLock<[Vec2; SEGMENTS + 1]> = OnceLock::new();
                let vertices = UNIT_CIRCLE.get_or_init(|| std::array::from_fn(|i| {
                    let (sin, cos) = (std::f32::consts::TAU * i as f32
                        / SEGMENTS as f32).sin_cos();
                    Vec2::new(cos, sin)
                }));
                for pair in vertices.windows(2) {
                    edge(*offset + pair[0] * *radius,
                         *offset + pair[1] * *radius);
                }
            }
        }
    }

    fn line(&mut self, x0: f32, y0: f32, x1: f32, y1: f32, color: u32) {
        // Liang-Barsky clipping bounds raster work even for far-off-map geometry.
        if ![x0, y0, x1, y1].iter().all(|x| x.is_finite()) { return; }
        let dx = x1 - x0;
        let dy = y1 - y0;
        let mut lo = 0.0_f32;
        let mut hi = 1.0_f32;
        for (p, q) in [(-dx, x0), (dx, self.width as f32 - 1.0 - x0),
            (-dy, y0), (dy, self.height as f32 - 1.0 - y0)] {
            if p == 0.0 { if q < 0.0 { return; } }
            else if p < 0.0 { lo = lo.max(q / p); }
            else { hi = hi.min(q / p); }
        }
        if lo > hi { return; }
        let ax = x0 + dx * lo;
        let ay = y0 + dy * lo;
        let bx = x0 + dx * hi;
        let by = y0 + dy * hi;
        let steps = ((bx - ax).abs().max((by - ay).abs()).ceil() as usize).max(1);
        for i in 0..=steps {
            let t = i as f32 / steps as f32;
            let x = (ax + (bx - ax) * t).round() as i32;
            let y = (ay + (by - ay) * t).round() as i32;
            if x >= 0 && y >= 0 && (x as usize) < self.width && (y as usize) < self.height {
                self.pixels[y as usize * self.width + x as usize] = color;
            }
        }
    }
}

fn side_color(side: i32) -> u32 {
    const COLORS: [u32; 6] = [0x62d6ff, 0xff778e, 0x8ee6a1, 0xd3a0ff, 0xffd166, 0xe4ecfa];
    COLORS[side.rem_euclid(COLORS.len() as i32) as usize]
}
