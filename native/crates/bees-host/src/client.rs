//! Optional native window around the shared host's borrowed render view.
//! Camera/inspection stay local; typed player moves carry world/episode
//! identity to the shared host. Policy decisions keep their real model route.

use std::time::{Duration, Instant};
use bees_sim::collision::Aabb;
use bees_sim::simulation::WorldId;
use minifb::{Key, KeyRepeat, MouseButton, MouseMode, Window, WindowOptions};
use crate::presentation::{NativeCamera, NativeRenderWorldView, NativeSoftwareRenderer, NativeSupportMode};
use bees_gameplay::player_inputs::{
    NativePlayerSquadMoves, NativePlayerSquadCommands, NativePlayerSquadStops,
    NativePlayerSquadManualFire,
};
use bees_sim::geometry::Vec2;

#[derive(Clone, Debug, PartialEq)]
pub enum NativeClientAction {
    Wait, Step, Quit, Move(NativePlayerSquadMoves),
    Stop(NativePlayerSquadStops), Support(NativePlayerSquadCommands),
    ManualFire(NativePlayerSquadManualFire),
}

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
    support_mode: Option<NativeSupportMode>,
    pub rendered_frames: u64,
    pub render_wall_seconds: f64,
}

impl NativeClient {
    pub fn new(bounds: Aabb) -> Result<Self, Box<dyn std::error::Error>> {
        let renderer = NativeSoftwareRenderer::new(1100, 760, bounds)?;
        let mut window = Window::new("Bees native", 1100, 760, WindowOptions::default())?;
        window.set_target_fps(60);
        let now = Instant::now();
        Ok(Self { window, renderer, bounds, selected_world: 0, selection_owner: None,
            paused: false, next_step: now, previous_frame: now, left_was_down: false,
            right_was_down: false, input_feedback: "",
            selection_drag: None, support_mode: None,
            rendered_frames: 0, render_wall_seconds: 0.0 })
    }

    pub fn selected_world(&self) -> usize { self.selected_world }

    pub fn report_move(&mut self, feedback: &'static str) { self.input_feedback = feedback; }

    pub fn poll(
        &mut self, view: NativeRenderWorldView<'_>, bounds: Aabb, world_count: usize, fixed_dt: Duration,
    ) -> Result<NativeClientAction, minifb::Error> {
        if !self.window.is_open() || self.window.is_key_down(Key::Escape) {
            return Ok(NativeClientAction::Quit);
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
        let single_step = self.paused && self.window.is_key_pressed(Key::N, KeyRepeat::No);
        let changed_world = self.window.is_key_pressed(Key::Tab, KeyRepeat::No) && world_count > 0;
        if changed_world {
            self.selected_world = (self.selected_world + 1) % world_count;
            self.renderer.selected_ship = None;
            self.renderer.selection.clear();
            self.selection_drag = None;
            self.support_mode = None;
            self.renderer.selection_box = None;
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
        if !changed_world {
            for (key, mode, prompt) in [
                (Key::G, NativeSupportMode::Guard, "Guard: click a friendly ship"),
                (Key::P, NativeSupportMode::Patrol, "Patrol: drag the patrol area"),
                (Key::M, NativeSupportMode::Mining, "Mine: click a mining asteroid"),
                (Key::H, NativeSupportMode::Heal, "Heal: click a friendly Beehive"),
                (Key::R, NativeSupportMode::FullRetreat, "Retreat: click a friendly WarpGate"),
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
        // Source StopMoving is immediate: it cancels an existing attack or
        // support command and clears every selected ship's real movement
        // controller. It is not a new AI Hold order.
        let player_stop = if !changed_world &&
            self.window.is_key_pressed(Key::S, KeyRepeat::No)
        {
            self.support_mode = None;
            self.selection_drag = None;
            self.renderer.selection_box = None;
            if self.renderer.selection.squads().is_empty() {
                self.input_feedback = "Select squads first";
                None
            } else {
                Some(NativePlayerSquadStops {
                    world_id: view.world_id,
                    episode_revision: view.episode_revision,
                    squads: self.renderer.selection.squads().to_vec(),
                })
            }
        } else { None };
        // J arms persistent source-style manual point-fire at the
        // cursor. K releases it. This changes turret input only: existing
        // formation and HiveMind movement commands continue unmodified.
        let manual_fire = if !changed_world && !self.renderer.selection.squads().is_empty() {
            let clear = self.window.is_key_pressed(Key::K, KeyRepeat::No);
            let aim = self.window.is_key_pressed(Key::J, KeyRepeat::No);
            if clear || aim {
                let target = if clear { Some(None) } else {
                    mouse.map(|(x, y)| Some(self.renderer.camera
                        .screen_to_world(x, y, width, height)))
                };
                target.map(|target| NativePlayerSquadManualFire {
                    world_id: view.world_id, episode_revision: view.episode_revision,
                    squads: self.renderer.selection.squads().to_vec(), target,
                })
            } else { None }
        } else { None };
        let right_down = self.window.get_mouse_down(MouseButton::Right);
        let cancel_support = right_down && !self.right_was_down && self.support_mode.is_some();
        if cancel_support {
            self.support_mode = None;
            self.selection_drag = None;
            self.renderer.selection_box = None;
            self.input_feedback = "Target selection cancelled";
        }
        let mut support_command = None;
        let left_down = self.window.get_mouse_down(MouseButton::Left);
        if !changed_world && !cancel_support && left_down && !self.left_was_down {
            self.selection_drag = mouse.map(|(x, y)| (
                self.renderer.camera.screen_to_world(x, y, width, height), Vec2::new(x, y)));
        }
        if !changed_world && !cancel_support {
            if let (Some((start, start_pixel)), Some((x, y))) = (self.selection_drag, mouse) {
                let end = self.renderer.camera.screen_to_world(x, y, width, height);
                let dragging = (Vec2::new(x, y) - start_pixel).length_squared() > 16.0;
                let rectangle = Aabb {
                    min: Vec2::new(start.x.min(end.x), start.y.min(end.y)),
                    max: Vec2::new(start.x.max(end.x), start.y.max(end.y)),
                };
                self.renderer.selection_box = (left_down && dragging &&
                    (self.support_mode.is_none() || self.support_mode == Some(NativeSupportMode::Patrol)))
                    .then_some(rectangle);
                if !left_down && self.left_was_down {
                    if let Some(mode) = self.support_mode {
                        if let Some(action) = self.renderer.pick_support_action(&view, mode, end, Some(start)) {
                            support_command = self.renderer.selection.support_command(&view, action);
                            self.support_mode = None;
                        } else { self.input_feedback = "Choose a valid target; Backspace cancels"; }
                    } else if dragging {
                        if rectangle.min.x < rectangle.max.x && rectangle.min.y < rectangle.max.y {
                            if self.renderer.selection.select_box(&view, rectangle).is_err() {
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
        let enemy_target = if !changed_world && !cancel_support &&
            right_down && !self.right_was_down
        {
            mouse.and_then(|(x, y)| self.renderer.pick_enemy_ship(
                &view,
                self.renderer.camera.screen_to_world(x, y, width, height),
            ))
        } else { None };
        if enemy_target.is_some() {
            // Source right-click dispatches Aggressive/Charge/BombingRun,
            // NEVER a point move. Without live authored enemy and weapon
            // snapshots, the attack command must remain unavailable.
            self.input_feedback = "Enemy attack requires native combat command adapter";
        }
        let player_move = if !changed_world && !cancel_support &&
            enemy_target.is_none() && support_command.is_none() &&
            right_down && !self.right_was_down {
            mouse.filter(|_| !self.renderer.selection.squads().is_empty()).map(|(x, y)|
                NativePlayerSquadMoves { world_id: view.world_id,
                    episode_revision: view.episode_revision,
                    squads: self.renderer.selection.squads().to_vec(),
                    destination: self.renderer.camera.screen_to_world(x, y, width, height) })
        } else { None };
        self.right_was_down = right_down;
        let render_started = Instant::now();
        let counts = self.renderer.render(&view, self.bounds);
        let selected = self.renderer.selected_ship.and_then(|id| view.combat.ship(id))
            .filter(|ship| !ship.vitals.is_dead())
            .map(|ship| format!(" | Ship {} type {} side {} HP {}/{} TSV {}",
                ship.entity_id, ship.ship_type, ship.side, ship.vitals.health(),
                ship.vitals.max_health(), ship.vitals.tsv())).unwrap_or_default();
        self.window.set_title(&format!(
            "Bees native | World {} | Episode {} | Tick {} | {} ships, {} shots | {} squads selected | {}{} | {} | Left select/drag, Ctrl add, right move | G guard, P patrol, M mine, H heal, R retreat, S stop, J fire cursor, K cease manual fire, Backspace cancel | Space pause, N step, Tab world, arrows pan, wheel zoom, F fit, Esc exit",
            view.world_id.0, view.episode_revision, view.tick, counts.ships, counts.projectiles,
            self.renderer.selection.squads().len(),
            if self.paused { "PAUSED" } else { "RUNNING" }, selected, self.input_feedback,
        ));
        self.render_wall_seconds += render_started.elapsed().as_secs_f64();
        self.rendered_frames += 1;
        self.window.update_with_buffer(self.renderer.pixels(), width, height)?;
        if !self.window.is_open() { return Ok(NativeClientAction::Quit); }
        if let Some(input) = player_stop { return Ok(NativeClientAction::Stop(input)); }
        if let Some(input) = manual_fire { return Ok(NativeClientAction::ManualFire(input)); }
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
