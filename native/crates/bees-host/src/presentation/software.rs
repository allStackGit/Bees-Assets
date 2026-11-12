//! Small, window-independent tactical renderer of authoritative live geometry.
//! Owns only pixels/camera/selection; it never mutates or steps a battle.

use std::collections::HashSet;
use std::io::{self, Write};
use std::sync::OnceLock;
use bees_sim::collision::{Aabb, Pose};
use bees_sim::geometry::Vec2;
use bees_sim::physics::ShipShape;
use bees_sim::simulation::{RandomStream, WorldId};
use super::{NativeRenderWorldView, NativeSquadSelection, battle_outcome_label, text};
use super::minimap::MiniMapLayout;

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
}

pub struct NativeSoftwareRenderer {
    width: usize,
    height: usize,
    pixels: Vec<u32>,
    pub camera: NativeCamera,
    /// Selection is scoped to a WorldId AND episode revision by the client.
    pub selected_ship: Option<i64>,
    pub selection: NativeSquadSelection,
    // Reused only for large selections; small squads need no hash allocation.
    selected_squad_lookup: HashSet<bees_hivemind::session::SquadLifetime>,
    pub selection_box: Option<Aabb>,
    /// Optional inspection only. Disabled means no range or linecast work.
    pub show_weapon_ranges: bool,
    /// Presentation-only tactical overview, never used by headless worlds.
    pub show_minimap: bool,
    debris_frame: Option<(WorldId, u64, u64)>,
    obstacle_debris: Vec<ObstacleDebris>,
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
            camera: NativeCamera::fit(bounds, width, height), selected_ship: None,
            selection: NativeSquadSelection::default(),
            selected_squad_lookup: HashSet::new(),
            selection_box: None, show_weapon_ranges: false,
            show_minimap: true,
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
        self.width = width;
        self.height = height;
        self.selection_box = None;
        Ok(true)
    }

    /// The same coordinate frame used by drawing and click/drag navigation.
    /// Returns None when the overview is hidden or the window is too small.
    pub fn minimap_world_at(&self, bounds: Aabb, x: f32, y: f32) -> Option<Vec2> {
        self.show_minimap.then(|| MiniMapLayout::new(self.width, self.height, bounds))
            .flatten()?.world_at(x, y)
    }

    /// Use the authoritative hull and healing-zone colliders for clicks,
    /// retaining screen-space tolerance for small ships. Large hull edges
    /// must remain selectable even when their centers are offscreen.
    /// This read-only UI query never changes gameplay collision state.
    pub fn pick_ship(&self, view: &NativeRenderWorldView<'_>, x: f32, y: f32) -> Option<i64> {
        if !x.is_finite() || !y.is_finite() { return None; }
        let point = self.camera.screen_to_world(x, y, self.width, self.height);
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
        if self.show_minimap {
            self.draw_minimap(view, bounds);
        }
        self.draw_squad_shortcuts(view);
        counts
    }

    /// Render only known player-owned units. Unlike the full spectator
    /// viewport, this map must not expose hidden enemy locations. Read
    /// permissions and current hull poses from the authoritative world.
    fn draw_minimap(&mut self, view: &NativeRenderWorldView<'_>, bounds: Aabb) {
        let Some(layout) = MiniMapLayout::new(self.width, self.height, bounds)
            else { return };
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

        // The HUD displays no tactical knowledge beyond current
        // player-controlled ships. A future fog-of-war presentation can
        // add authenticated discovered entities without leaking enemies.
        for (ship, body) in view.combat.render_ships() {
            let Some(owner) = view.roster.owning_squad(ship.entity_id)
                else { continue };
            if !view.player_authority.player_permissions(owner)
                .is_some_and(|permissions| permissions.user_controlled) {
                continue;
            }
            let Some((x, y)) = layout.project(body.motion.position)
                else { continue };
            let selected = if self.selection.squads().len() > 8 {
                self.selected_squad_lookup.contains(&owner)
            } else {
                self.selection.squads().contains(&owner)
            };
            let color = if selected { 0xffffff } else { side_color(ship.side) };
            let x = x.round() as i32;
            let y = y.round() as i32;
            let radius = if selected { 3 } else { 1 };
            for yy in (y - radius)..=(y + radius) {
                for xx in (x - radius)..=(x + radius) {
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
    }

    /// A lightweight source-squad tab strip. Shortcut numbers come from the
    /// actual schema-29 Unity export, not an index assigned by the renderer.
    /// It is omitted for legacy scenes lacking authoritative squad numbers.
    /// Only user-controlled ships are listed; enemy roster data stays private.
    fn draw_squad_shortcuts(&mut self, view: &NativeRenderWorldView<'_>) {
        if self.width < 560 || self.height < 360 { return; }
        let mut row = 0usize;
        for &owner in view.roster.source_order() {
            let Some(squad) = view.roster.get(owner) else { continue };
            let Some(number) = squad.source_hotkey_number else { continue };
            if number > 10 || !view.player_authority.player_permissions(owner)
                .is_some_and(|permission| permission.user_controlled) {
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
            let x = 14usize;
            let y = 14 + row * (text::HEIGHT + 6);
            if y + text::HEIGHT + 5 >= self.height / 2 { break; }
            let width = (text::width(&label, 1) + 16).min(228usize);
            for scanline in y.saturating_sub(2)..(y + text::HEIGHT + 4) {
                let start = scanline * self.width + x.saturating_sub(5);
                self.pixels[start..start + width].fill(
                    if selected { 0x35465d } else { 0x142338 });
            }
            text::draw_at(&mut self.pixels, self.width, x, y,
                &label, 1, if selected { [255, 241, 194] }
                    else { [182, 211, 239] });
            row += 1;
            if row == 10 { break; }
        }
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
