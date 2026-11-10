//! Optional native window around the shared host's borrowed render view.
//! Camera/inspection stay local; typed player moves carry world/episode
//! identity to the shared host. Policy decisions keep their real model route.

use std::time::{Duration, Instant};
use bees_sim::collision::Aabb;
use bees_sim::simulation::WorldId;
use minifb::{Key, KeyRepeat, MouseButton, MouseMode, Window, WindowOptions};
use crate::presentation::{NativeCamera, NativeRenderWorldView, NativeSoftwareRenderer, NativeSupportMode};
use bees_gameplay::player_inputs::{
    NativePlayerSquadMoves, NativePlayerSquadCommands, NativePlayerSquadFire,
    NativePlayerSquadCeaseFire,
};
use bees_sim::geometry::Vec2;

#[derive(Clone, Debug, PartialEq)]
pub enum NativeClientAction { Wait, Step, Quit, WorldChanged, Move(NativePlayerSquadMoves), Support(NativePlayerSquadCommands), Fire(NativePlayerSquadFire), CeaseFire(NativePlayerSquadCeaseFire) }

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum NativeTargetMode { Support(NativeSupportMode), ManualFire }

pub struct NativeClient {
    window: Window,
    renderer: NativeSoftwareRenderer,
    bounds: Aabb,
    selected_world: usize,
    selection_owner: Option<(WorldId, u64)>,
    paused: bool,
    next_step: Instant,
    previous_frame: Instant,
    left_was_down: bool,
    right_was_down: bool,
    input_feedback: &'static str,
    selection_drag: Option<(Vec2, Vec2)>,
    support_mode: Option<NativeTargetMode>,
    pub rendered_frames: u64,
    /// Total software framebuffer pixels actually drawn across changing sizes.
    pub rendered_pixels: u64,
    pub render_wall_seconds: f64,
    pub ui_wall_seconds: f64,
    /// Includes minifb's frame pacing and display transfer, separate from
    /// CPU software rendering time.
    pub window_update_wall_seconds: f64,
    pub culled_ship_draws: u64,
    pub culled_obstacle_draws: u64,
    pub culled_projectile_draws: u64,
    pub culled_piece_draws: u64,
    pub weapon_geometry_queries: u64,
    pub weapon_range_candidates_culled: u64,
    pub viewport_resizes: u64,
}

impl NativeClient {
    pub fn new(bounds: Aabb) -> Result<Self, Box<dyn std::error::Error>> {
        let renderer = NativeSoftwareRenderer::new(1100, 760, bounds)?;
        let mut window = Window::new("Bees native", 1100, 760,
            WindowOptions { resize: true, ..WindowOptions::default() })?;
        window.set_target_fps(60);
        let now = Instant::now();
        Ok(Self { window, renderer, bounds, selected_world: 0, selection_owner: None,
            paused: false, next_step: now, previous_frame: now, left_was_down: false,
            right_was_down: false, input_feedback: "",
            selection_drag: None, support_mode: None,
            rendered_frames: 0, rendered_pixels: 0, render_wall_seconds: 0.0,
            ui_wall_seconds: 0.0, window_update_wall_seconds: 0.0,
            culled_ship_draws: 0, culled_obstacle_draws: 0,
            culled_projectile_draws: 0, culled_piece_draws: 0,
            weapon_geometry_queries: 0, weapon_range_candidates_culled: 0,
            viewport_resizes: 0 })
    }

    pub fn selected_world(&self) -> usize { self.selected_world }

    pub fn viewport_size(&self) -> (usize, usize) { self.renderer.size() }

    pub fn report_move(&mut self, feedback: &'static str) { self.input_feedback = feedback; }

    pub fn poll(
        &mut self, view: NativeRenderWorldView<'_>, bounds: Aabb, world_count: usize, fixed_dt: Duration,
    ) -> Result<NativeClientAction, Box<dyn std::error::Error>> {
        if !self.window.is_open() || self.window.is_key_down(Key::Escape) {
            return Ok(NativeClientAction::Quit);
        }
        let (window_width, window_height) = self.window.get_size();
        if window_width == 0 || window_height == 0 {
            // Minimized windows have no drawable surface; pump OS events
            // without trying to submit a zero-sized bitmap.
            self.window.update();
            self.previous_frame = Instant::now();
            return Ok(NativeClientAction::Wait);
        }
        if self.renderer.resize(window_width, window_height)? {
            // A drag's original screen coordinates no longer describe
            // the same selection box after a window resize.
            self.selection_drag = None;
            self.viewport_resizes += 1;
        }
        let now = Instant::now();
        let elapsed = now.duration_since(self.previous_frame).as_secs_f32().min(0.1);
        self.previous_frame = now;
        let owner = (view.world_id, view.episode_revision);
        if self.selection_owner != Some(owner) {
            self.renderer.selected_ship = None;
            self.selection_drag = None;
            self.support_mode = None;
            self.renderer.selection_box = None;
            self.input_feedback = "";
            if self.selection_owner.is_some_and(|(world, _)| world != view.world_id) {
                let (width, height) = self.renderer.size();
                self.renderer.camera = NativeCamera::fit(bounds, width, height);
            }
            self.selection_owner = Some(owner);
        }
        self.renderer.selection.synchronize(&view);
        self.bounds = bounds;
        if self.window.is_key_pressed(Key::Space, KeyRepeat::No) {
            self.paused = !self.paused;
            self.next_step = now;
        }
        if self.window.is_key_pressed(Key::B, KeyRepeat::No) {
            self.renderer.show_weapon_ranges = !self.renderer.show_weapon_ranges;
        }
        let single_step = self.paused && self.window.is_key_pressed(Key::N, KeyRepeat::No);
        if self.window.is_key_pressed(Key::Tab, KeyRepeat::No) && world_count > 1 {
            self.selected_world = (self.selected_world + 1) % world_count;
            self.renderer.selected_ship = None;
            self.renderer.selection.clear();
            self.selection_drag = None;
            self.support_mode = None;
            self.renderer.selection_box = None;
            // Never apply an input to or render the old World after changing
            // the selection. The caller must borrow the new World before
            // asking us to display another frame. Do not interpret a mouse
            // button held across the switch as a new click in that World.
            self.left_was_down = self.window.get_mouse_down(MouseButton::Left);
            self.right_was_down = self.window.get_mouse_down(MouseButton::Right);
            self.window.update();
            self.next_step = Instant::now();
            return Ok(NativeClientAction::WorldChanged);
        }
        let (width, height) = self.renderer.size();
        if self.window.is_key_pressed(Key::F, KeyRepeat::No) {
            self.renderer.camera = NativeCamera::fit(self.bounds, width, height);
        }
        let distance = 500.0 * elapsed / self.renderer.camera.pixels_per_unit;
        if self.window.is_key_down(Key::Left) { self.renderer.camera.center.x -= distance; }
        if self.window.is_key_down(Key::Right) { self.renderer.camera.center.x += distance; }
        if self.window.is_key_down(Key::Up) { self.renderer.camera.center.y += distance; }
        if self.window.is_key_down(Key::Down) { self.renderer.camera.center.y -= distance; }
        let mouse = self.window.get_mouse_pos(MouseMode::Discard);
        if let (Some((_, scroll)), Some((x, y))) = (self.window.get_scroll_wheel(), mouse) {
            self.renderer.camera.zoom_at(1.15_f32.powf(scroll.clamp(-10.0, 10.0)), x, y, width, height);
        }
        {
            for (key, mode, prompt) in [
                (Key::G, NativeTargetMode::Support(NativeSupportMode::Guard), "Guard: click a friendly ship"),
                (Key::P, NativeTargetMode::Support(NativeSupportMode::Patrol), "Patrol: drag the patrol area"),
                (Key::M, NativeTargetMode::Support(NativeSupportMode::Mining), "Mine: click a mining asteroid"),
                (Key::H, NativeTargetMode::Support(NativeSupportMode::Heal), "Heal: click a friendly Beehive"),
                (Key::R, NativeTargetMode::Support(NativeSupportMode::FullRetreat), "Retreat: click a friendly WarpGate"),
                (Key::T, NativeTargetMode::ManualFire, "Point fire: click a point; X stops selected weapons"),
            ] {
                if self.window.is_key_pressed(key, KeyRepeat::No) {
                    self.selection_drag = None;
                    self.renderer.selection_box = None;
                    if self.renderer.selection.squads().is_empty() {
                        self.input_feedback = "Select squads first";
                    } else {
                        self.support_mode = Some(mode);
                        self.input_feedback = prompt;
                    }
                }
            }
            if self.window.is_key_pressed(Key::Backspace, KeyRepeat::No) {
                self.support_mode = None;
                self.selection_drag = None;
                self.renderer.selection_box = None;
                self.input_feedback = "Target selection cancelled";
            }
        }
        let right_down = self.window.get_mouse_down(MouseButton::Right);
        let cancel_support = right_down && !self.right_was_down && self.support_mode.is_some();
        if cancel_support {
            self.support_mode = None;
            self.selection_drag = None;
            self.renderer.selection_box = None;
            self.input_feedback = "Target selection cancelled";
        }
        let mut manual_fire = if self.window.is_key_pressed(Key::X, KeyRepeat::No) &&
            !self.renderer.selection.squads().is_empty() {
            self.support_mode = None;
            self.selection_drag = None;
            self.renderer.selection_box = None;
            Some(NativePlayerSquadFire { world_id: view.world_id, episode_revision: view.episode_revision,
                squads: self.renderer.selection.squads().to_vec(), target: None })
        } else { None };
        let firing_mode = if self.window.is_key_pressed(Key::V, KeyRepeat::No) {
            Some(true)
        } else if self.window.is_key_pressed(Key::A, KeyRepeat::No) {
            Some(false)
        } else { None };
        let cease_fire_input = firing_mode.and_then(|cease_fire| {
            if self.renderer.selection.squads().is_empty() {
                self.input_feedback = "Select squads first";
                None
            } else {
                self.support_mode = None;
                self.selection_drag = None;
                self.renderer.selection_box = None;
                Some(NativePlayerSquadCeaseFire {
                    world_id: view.world_id, episode_revision: view.episode_revision,
                    squads: self.renderer.selection.squads().to_vec(), cease_fire,
                })
            }
        });
        let mut support_command = None;
        if self.window.is_key_pressed(Key::C, KeyRepeat::No) {
            if self.renderer.selection.squads().is_empty() {
                self.input_feedback = "Select squads first";
            } else {
                self.support_mode = None;
                self.selection_drag = None;
                self.renderer.selection_box = None;
                support_command = self.renderer.selection.support_command(
                    &view, bees_gameplay::player_inputs::NativePlayerSupportAction::Hold,
                );
            }
        }
        let left_down = self.window.get_mouse_down(MouseButton::Left);
        if !cancel_support && left_down && !self.left_was_down {
            self.selection_drag = mouse.map(|(x, y)| (
                self.renderer.camera.screen_to_world(x, y, width, height), Vec2::new(x, y)));
        }
        if !cancel_support {
            if let (Some((start, start_pixel)), Some((x, y))) = (self.selection_drag, mouse) {
                let end = self.renderer.camera.screen_to_world(x, y, width, height);
                let dragging = (Vec2::new(x, y) - start_pixel).length_squared() > 16.0;
                let rectangle = Aabb {
                    min: Vec2::new(start.x.min(end.x), start.y.min(end.y)),
                    max: Vec2::new(start.x.max(end.x), start.y.max(end.y)),
                };
                self.renderer.selection_box = (left_down && dragging &&
                    (self.support_mode.is_none() || self.support_mode == Some(NativeTargetMode::Support(NativeSupportMode::Patrol))))
                    .then_some(rectangle);
                if !left_down && self.left_was_down {
                    if let Some(mode) = self.support_mode {
                        if mode == NativeTargetMode::ManualFire {
                            manual_fire = Some(NativePlayerSquadFire { world_id: view.world_id,
                                episode_revision: view.episode_revision,
                                squads: self.renderer.selection.squads().to_vec(), target: Some(end) });
                            self.support_mode = None;
                        } else if let NativeTargetMode::Support(mode) = mode {
                            if let Some(action) = self.renderer.pick_support_action(&view, mode, end, Some(start)) {
                                support_command = self.renderer.selection.support_command(&view, action);
                                self.support_mode = None;
                            } else { self.input_feedback = "Choose a valid target; Backspace cancels"; }
                        }
                    } else if dragging {
                        if rectangle.min.x < rectangle.max.x && rectangle.min.y < rectangle.max.y {
                            let additive = self.window.is_key_down(Key::LeftCtrl) ||
                                self.window.is_key_down(Key::RightCtrl);
                            if self.renderer.selection.select_box(&view, rectangle, additive).is_err() {
                                self.input_feedback = "Selection box is unavailable";
                            }
                        } else { self.renderer.selection.clear(); }
                        self.renderer.selected_ship = self.renderer.selection
                            .representative_ships(&view).next();
                    } else {
                        self.renderer.selected_ship = self.renderer.pick_ship(&view, x, y);
                        if let Some(ship) = self.renderer.selected_ship {
                            let additive = self.window.is_key_down(Key::LeftCtrl) ||
                                self.window.is_key_down(Key::RightCtrl);
                            self.renderer.selection.select_ship(&view, ship, additive);
                        } else { self.renderer.selection.clear(); }
                    }
                }
            }
        }
        if !left_down {
            self.selection_drag = None;
            self.renderer.selection_box = None;
        }
        self.left_was_down = left_down;
        let player_move = if !cancel_support && support_command.is_none() && manual_fire.is_none() &&
            right_down && !self.right_was_down {
            mouse.filter(|_| !self.renderer.selection.squads().is_empty()).and_then(|(x, y)| {
                let destination = self.renderer.camera.screen_to_world(x, y, width, height);
                if let Some(action) = self.renderer.pick_support_action(&view, NativeSupportMode::Attack, destination, None) {
                    support_command = self.renderer.selection.support_command(&view, action);
                    return None;
                }
                Some(NativePlayerSquadMoves { world_id: view.world_id,
                    episode_revision: view.episode_revision,
                    squads: self.renderer.selection.squads().to_vec(),
                    destination })
            })
        } else { None };
        self.right_was_down = right_down;
        let render_started = Instant::now();
        let counts = self.renderer.render(&view, self.bounds);
        self.render_wall_seconds += render_started.elapsed().as_secs_f64();
        let ui_started = Instant::now();
        let selected = self.renderer.selected_ship.and_then(|id| view.combat.ship(id))
            .filter(|ship| !ship.vitals.is_dead())
            .map(|ship| format!(" | Ship {} type {} side {} HP {}/{} TSV {} | {}",
                ship.entity_id, ship.ship_type, ship.side, ship.vitals.health(),
                ship.vitals.max_health(), ship.vitals.tsv(),
                view.combat.ship_cease_fire(ship.entity_id)
                    .map(|paused| if paused { "CEASE FIRE" } else { "ATTACK ON SIGHT" })
                    .unwrap_or("FIRING STATUS UNKNOWN"))).unwrap_or_default();
        self.window.set_title(&format!(
            "Bees native | World {} | Episode {} | Tick {} | {} ships, {} shots | {} squads selected | {}{} | {} | range/obstruction {} | Left select/drag, Ctrl add, right move/attack | C stop chase, V cease fire, A attack on sight, G guard, P patrol, M mine, H heal, R retreat, T point fire, X stop, B ranges, Backspace cancel | Space pause, N step, Tab world, arrows pan, wheel zoom, F fit, Esc exit",
            view.world_id.0, view.episode_revision, view.tick, counts.ships, counts.projectiles,
            self.renderer.selection.squads().len(),
            if self.paused { "PAUSED" } else { "RUNNING" }, selected, self.input_feedback,
            if self.renderer.show_weapon_ranges { format!("{} auto targets, {} contacts, {}/{} blocked, {} exact queries, {} prefiltered, {} missing; {} aim points, {} missing",
                counts.automatic_targets, counts.range_contacts, counts.obstructed_ranges, counts.range_overlaps,
                counts.range_queries, counts.range_candidates_culled, counts.missing_range_data,
                counts.aim_points, counts.missing_aim_data) }
            else { "off".to_owned() },
        ));
        self.ui_wall_seconds += ui_started.elapsed().as_secs_f64();
        self.rendered_frames += 1;
        self.rendered_pixels = self.rendered_pixels.saturating_add(
            (width as u64).saturating_mul(height as u64)
        );
        self.culled_ship_draws = self.culled_ship_draws.saturating_add(counts.culled_ships as u64);
        self.culled_obstacle_draws = self.culled_obstacle_draws.saturating_add(counts.culled_obstacles as u64);
        self.culled_projectile_draws = self.culled_projectile_draws.saturating_add(counts.culled_projectiles as u64);
        self.culled_piece_draws = self.culled_piece_draws.saturating_add(counts.culled_pieces as u64);
        self.weapon_geometry_queries = self.weapon_geometry_queries.saturating_add(counts.range_queries as u64);
        self.weapon_range_candidates_culled = self.weapon_range_candidates_culled
            .saturating_add(counts.range_candidates_culled as u64);
        let window_update_started = Instant::now();
        self.window.update_with_buffer(self.renderer.pixels(), width, height)?;
        self.window_update_wall_seconds += window_update_started.elapsed().as_secs_f64();
        if !self.window.is_open() { return Ok(NativeClientAction::Quit); }
        if let Some(input) = cease_fire_input { return Ok(NativeClientAction::CeaseFire(input)); }
        if let Some(input) = manual_fire { return Ok(NativeClientAction::Fire(input)); }
        if let Some(input) = support_command { return Ok(NativeClientAction::Support(input)); }
        if let Some(input) = player_move { return Ok(NativeClientAction::Move(input)); }
        let after_frame = Instant::now();
        if single_step || (!self.paused && after_frame >= self.next_step) {
            // At most one authoritative step per poll. A slow inference or
            // render pass slows playback instead of changing dt or skipping
            // battle ticks. Preserve fractional pacing when running on time.
            self.next_step = (self.next_step + fixed_dt).max(after_frame);
            Ok(NativeClientAction::Step)
        } else {
            Ok(NativeClientAction::Wait)
        }
    }
}
