//! Optional native window around the shared host's borrowed render view.
//! Camera/inspection stay local; typed player moves carry world/episode
//! identity to the shared host. Policy decisions keep their real model route.

use std::time::{Duration, Instant};
use bees_sim::collision::Aabb;
use bees_sim::simulation::WorldId;
use bees_hivemind::session::SquadLifetime;
use minifb::{Key, KeyRepeat, MouseButton, MouseMode, Window, WindowOptions};
use crate::presentation::{NativeCamera, NativeRenderWorldView, NativeSoftwareRenderer, NativeSupportMode};
use bees_gameplay::player_inputs::{NativeHeldFireAction, NativePlayerHeldFire, NativePlayerSquadMoves,
    NativePlayerSquadFire, NativePlayerSquadCeaseFire};
use crate::player_trace::NativePlayerInput;
use bees_sim::geometry::Vec2;

#[derive(Clone, Debug, PartialEq)]
pub enum NativeClientAction {
    Wait, Step, Quit, WorldChanged,
    Input { action: NativePlayerInput, step: bool },
}

#[derive(Clone, Copy)]
pub enum NativeResultControls { Close, PlayAgain, PlayAgainOrWatch, WatchAgain }

pub enum NativeResultAction { PlayAgain, WatchRecording }

impl NativeResultControls {
    fn hint(self) -> &'static str {
        match self {
            Self::Close => "",
            Self::PlayAgain => "Enter play again, ",
            Self::PlayAgainOrWatch => "Enter play again, W watch recording, ",
            Self::WatchAgain => "Enter watch again, ",
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum NativeTargetMode { Support(NativeSupportMode), ManualFire }

const RIGHT_DRAG_INTERVAL: Duration = Duration::from_millis(250);

struct RightPress {
    next_move_at: Instant,
    last_move_pixel: Option<Vec2>,
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
    right_press: Option<RightPress>,
    last_left_press: Option<Instant>,
    double_left_press: bool,
    input_feedback: &'static str,
    result_controls: Option<NativeResultControls>,
    playback: bool,
    dialogue_sequence: Option<u8>,
    dialogue_line: usize,
    dialogue_visible: bool,
    held_fire_target: Option<Vec2>,
    selection_drag: Option<(Vec2, Vec2)>,
    /// UI-only minimap drag. Never emits a gameplay/world input.
    minimap_dragging: bool,
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
    pub minimap_markers_drawn: u64,
    pub numbered_squad_rows_drawn: u64,
    pub minimap_camera_moves: u64,
    pub numbered_shortcut_selections: u64,
    pub cycled_shortcut_selections: u64,
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
            right_was_down: false, right_press: None, last_left_press: None, double_left_press: false,
            input_feedback: "",
            result_controls: None,
            playback: false,
            dialogue_sequence: None,
            dialogue_line: 0,
            dialogue_visible: false,
            held_fire_target: None,
            selection_drag: None, minimap_dragging: false,
            support_mode: None,
            rendered_frames: 0, rendered_pixels: 0, render_wall_seconds: 0.0,
            ui_wall_seconds: 0.0, window_update_wall_seconds: 0.0,
            culled_ship_draws: 0, culled_obstacle_draws: 0,
            culled_projectile_draws: 0, culled_piece_draws: 0,
            weapon_geometry_queries: 0, weapon_range_candidates_culled: 0,
            viewport_resizes: 0,
            minimap_markers_drawn: 0, numbered_squad_rows_drawn: 0,
            minimap_camera_moves: 0, numbered_shortcut_selections: 0,
            cycled_shortcut_selections: 0 })
    }

    /// Initial authored camera focus is a presentation-only concern. The
    /// world, simulation coordinates and replayed player inputs are unchanged.
    pub fn focus_camera(&mut self, center: Vec2) {
        if center.is_finite() {
            self.renderer.camera.center = center;
        }
    }

    /// Presentation-only focus shared by source number hotkeys and the
    /// source-order cycle. The Unity squad center is the live hull bounds
    /// center, not the average of ship-center positions.
    fn focus_selected_squad(
        &mut self, view: &NativeRenderWorldView<'_>, owner: SquadLifetime,
    ) {
        self.support_mode = None;
        self.selection_drag = None;
        self.renderer.selection_box = None;
        self.last_left_press = None;
        self.double_left_press = false;
        self.renderer.selected_ship = self.renderer.selection
            .representative_ships(view).next();
        let Some(squad) = view.roster.get(owner) else { return };
        let mut bounds: Option<Aabb> = None;
        for &ship_id in &squad.members {
            let Some(body) = view.combat.ship_body(ship_id)
                .filter(|body| body.active) else { continue };
            let hull = body.primary_bounds().unwrap_or(Aabb {
                min: body.motion.position,
                max: body.motion.position,
            });
            bounds = Some(match bounds {
                None => hull,
                Some(previous) => Aabb {
                    min: Vec2::new(previous.min.x.min(hull.min.x),
                        previous.min.y.min(hull.min.y)),
                    max: Vec2::new(previous.max.x.max(hull.max.x),
                        previous.max.y.max(hull.max.y)),
                },
            });
        }
        if let Some(bounds) = bounds {
            self.renderer.camera.center = (bounds.min + bounds.max) * 0.5;
        }
    }

    pub fn selected_world(&self) -> usize { self.selected_world }

    pub fn viewport_size(&self) -> (usize, usize) { self.renderer.size() }

    pub fn report_move(&mut self, feedback: &'static str) { self.input_feedback = feedback; }

    pub fn show_result(&mut self, controls: NativeResultControls) { self.result_controls = Some(controls); }
    pub fn set_playback(&mut self) { self.playback = true; }

    pub fn result_action(&self) -> Option<NativeResultAction> {
        if self.dialogue_visible { return None; }
        let controls = self.result_controls?;
        if matches!(controls, NativeResultControls::PlayAgainOrWatch) &&
            self.window.is_key_pressed(Key::W, KeyRepeat::No)
        {
            return Some(NativeResultAction::WatchRecording);
        }
        if !self.window.is_key_pressed(Key::Enter, KeyRepeat::No) { return None; }
        match controls {
            NativeResultControls::Close => None,
            NativeResultControls::PlayAgain | NativeResultControls::PlayAgainOrWatch => Some(NativeResultAction::PlayAgain),
            NativeResultControls::WatchAgain => Some(NativeResultAction::WatchRecording),
        }
    }

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
            self.right_press = None;
            self.minimap_dragging = false;
            self.previous_frame = Instant::now();
            return Ok(NativeClientAction::Wait);
        }
        if self.renderer.resize(window_width, window_height)? {
            // A drag's original screen coordinates no longer describe
            // the same selection box after a window resize.
            self.selection_drag = None;
            self.minimap_dragging = false;
            self.right_press = None;
            self.last_left_press = None;
            self.double_left_press = false;
            self.viewport_resizes += 1;
        }
        let now = Instant::now();
        let elapsed = now.duration_since(self.previous_frame).as_secs_f32().min(0.1);
        self.previous_frame = now;
        let owner = (view.world_id, view.episode_revision);
        if self.selection_owner != Some(owner) {
            self.renderer.selected_ship = None;
            self.selection_drag = None;
            self.minimap_dragging = false;
            self.right_press = None;
            self.support_mode = None;
            self.last_left_press = None;
            self.double_left_press = false;
            self.renderer.selection_box = None;
            self.input_feedback = "";
            if self.selection_owner.is_some_and(|(world, _)| world != view.world_id) {
                let (width, height) = self.renderer.size();
                self.renderer.camera = NativeCamera::fit(bounds, width, height);
            }
            self.selection_owner = Some(owner);
            self.dialogue_sequence = None;
            self.held_fire_target = None;
        }
        self.renderer.selection.synchronize(&view);
        let dialogue = view.campaign_dialogue.and_then(|(sequence, cue)| {
            if self.dialogue_sequence != Some(sequence) {
                self.dialogue_sequence = Some(sequence);
                self.dialogue_line = 0;
            }
            let lines = crate::presentation::campaign_dialogue::lines(cue);
            if self.window.is_key_pressed(Key::K, KeyRepeat::No) {
                self.dialogue_line = lines.len();
            } else if self.dialogue_line < lines.len() && self.window.is_key_pressed(Key::L, KeyRepeat::No) {
                self.dialogue_line += 1;
            }
            lines.get(self.dialogue_line).map(|&(speaker, line)| (cue, speaker, line))
        });
        self.dialogue_visible = dialogue.is_some();
        let briefing = dialogue.is_some_and(|(cue, _, _)| cue.is_briefing());
        if view.terminal_result.is_some() { self.held_fire_target = None; }
        self.bounds = bounds;
        if self.window.is_key_pressed(Key::Space, KeyRepeat::No) {
            self.paused = !self.paused;
            self.next_step = now;
        }
        if self.window.is_key_pressed(Key::B, KeyRepeat::No) {
            self.renderer.show_weapon_ranges = !self.renderer.show_weapon_ranges;
        }
        if self.window.is_key_pressed(Key::I, KeyRepeat::No) {
            self.renderer.show_minimap = !self.renderer.show_minimap;
            if !self.renderer.show_minimap { self.minimap_dragging = false; }
        }
        let single_step = self.paused && self.window.is_key_pressed(Key::N, KeyRepeat::No);
        if self.window.is_key_pressed(Key::Tab, KeyRepeat::No) && world_count > 1 && self.held_fire_target.is_none() {
            self.selected_world = (self.selected_world + 1) % world_count;
            self.renderer.selected_ship = None;
            self.renderer.selection.clear();
            self.selection_drag = None;
            self.support_mode = None;
            self.last_left_press = None;
            self.double_left_press = false;
            self.renderer.selection_box = None;
            // Never apply an input to or render the old World after changing
            // the selection. The caller must borrow the new World before
            // asking us to display another frame. Do not interpret a mouse
            // button held across the switch as a new click in that World.
            self.left_was_down = self.window.get_mouse_down(MouseButton::Left);
            self.right_was_down = self.window.get_mouse_down(MouseButton::Right);
            self.right_press = None;
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
            let factor = 1.15_f32.powf(scroll.clamp(-10.0, 10.0));
            if let Some(map_point) = self.renderer.minimap_world_at(bounds, x, y) {
                // The minimap cursor is in map coordinates, not the camera
                // framebuffer. Zoom toward that actual world point rather
                // than shifting by a potentially massive false offset.
                self.renderer.camera.center = map_point;
                self.minimap_camera_moves += 1;
                self.renderer.camera.zoom_at(factor, width as f32 * 0.5,
                    height as f32 * 0.5, width, height);
            } else {
                self.renderer.camera.zoom_at(factor, x, y, width, height);
            }
        }
        let accepts_orders = !briefing && !self.playback && view.terminal_result.is_none();
        let pointer_active = self.window.is_active();
        // Schema-29 snapshots carry Unity's exact numbered squads and
        // original per-side count. Legacy exports retain PgUp/PgDn cycling
        // but do not guess 1-0 hotkey assignments from live roster order.
        if accepts_orders && pointer_active && self.held_fire_target.is_none() {
            let selected = [
                (Key::Key1, 1), (Key::Key2, 2), (Key::Key3, 3),
                (Key::Key4, 4), (Key::Key5, 5), (Key::Key6, 6),
                (Key::Key7, 7), (Key::Key8, 8), (Key::Key9, 9),
                (Key::Key0, 0),
            ].into_iter().find_map(|(key, number)| {
                self.window.is_key_pressed(key, KeyRepeat::No)
                    .then_some(number)
            }).and_then(|number|
                self.renderer.selection.select_source_number(&view, number)
            );
            let was_numbered = selected.is_some();
            let selected = selected.or_else(|| {
                let forward = self.window.is_key_pressed(
                    Key::PageDown, KeyRepeat::No);
                let backward = self.window.is_key_pressed(
                    Key::PageUp, KeyRepeat::No);
                (forward || backward).then(|| self.renderer.selection
                    .cycle_source_squad(&view, forward)).flatten()
            });
            if let Some(owner) = selected {
                if was_numbered { self.numbered_shortcut_selections += 1; }
                else { self.cycled_shortcut_selections += 1; }
                self.focus_selected_squad(&view, owner);
            }
        }
        let stop_fire = self.window.is_key_pressed(Key::X, KeyRepeat::No);
        let held_fire = if accepts_orders {
            let cursor = mouse.map(|(x, y)| self.renderer.camera.screen_to_world(x, y, width, height));
            if let Some(previous) = self.held_fire_target {
                if !pointer_active || !self.window.is_key_down(Key::J) || stop_fire {
                    self.held_fire_target = None;
                    Some(NativeHeldFireAction::End)
                } else if let Some(target) = cursor.filter(|point| *point != previous) {
                    self.held_fire_target = Some(target);
                    Some(NativeHeldFireAction::Aim { target })
                } else { None }
            } else if pointer_active && self.window.is_key_pressed(Key::J, KeyRepeat::No) &&
                !self.renderer.selection.squads().is_empty()
            {
                cursor.map(|target| {
                    self.held_fire_target = Some(target);
                    self.support_mode = None;
                    self.selection_drag = None;
                    self.renderer.selection_box = None;
                    NativeHeldFireAction::Begin { squads: self.renderer.selection.squads().to_vec(), target }
                })
            } else { None }
        } else { None };
        let point_fire_available = self.held_fire_target.is_none() && held_fire.is_none();
        if accepts_orders {
            for (key, mode, prompt) in [
                (Key::G, NativeTargetMode::Support(NativeSupportMode::Guard), "Guard: click a friendly ship"),
                (Key::P, NativeTargetMode::Support(NativeSupportMode::Patrol), "Patrol: drag the patrol area"),
                (Key::M, NativeTargetMode::Support(NativeSupportMode::Mining), "Mine: click a mining asteroid"),
                (Key::H, NativeTargetMode::Support(NativeSupportMode::Heal), "Heal: click a friendly Beehive"),
                (Key::R, NativeTargetMode::Support(NativeSupportMode::FullRetreat), "Retreat: click a friendly WarpGate"),
                (Key::T, NativeTargetMode::ManualFire, "Point fire: click a point; X stops selected weapons"),
            ] {
                if self.window.is_key_pressed(key, KeyRepeat::No) &&
                    (mode != NativeTargetMode::ManualFire || point_fire_available)
                {
                    self.selection_drag = None;
                    self.renderer.selection_box = None;
                    self.last_left_press = None;
                    self.double_left_press = false;
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
        let right_pressed = right_down && !self.right_was_down;
        let right_released = !right_down && self.right_was_down;
        let cancel_support = right_pressed && pointer_active && self.support_mode.is_some();
        if !pointer_active {
            self.right_press = None;
        } else if right_pressed && accepts_orders {
            self.right_press = (mouse.is_some() && !cancel_support &&
                !self.renderer.selection.squads().is_empty())
                .then_some(RightPress { next_move_at: now + RIGHT_DRAG_INTERVAL, last_move_pixel: None });
        }
        if cancel_support {
            self.support_mode = None;
            self.selection_drag = None;
            self.renderer.selection_box = None;
            self.input_feedback = "Target selection cancelled";
        }
        let mut manual_fire = if accepts_orders && point_fire_available && stop_fire &&
            !self.renderer.selection.squads().is_empty() {
            self.support_mode = None;
            self.selection_drag = None;
            self.renderer.selection_box = None;
            Some(NativePlayerSquadFire { world_id: view.world_id, episode_revision: view.episode_revision,
                squads: self.renderer.selection.squads().to_vec(), target: None })
        } else { None };
        let firing_mode = if accepts_orders && self.window.is_key_pressed(Key::V, KeyRepeat::No) {
            Some(true)
        } else if accepts_orders && self.window.is_key_pressed(Key::A, KeyRepeat::No) {
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
        if accepts_orders && self.window.is_key_pressed(Key::C, KeyRepeat::No) {
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
        if !pointer_active { self.minimap_dragging = false; }
        if pointer_active && left_down && !self.left_was_down &&
            self.support_mode.is_none() &&
            self.held_fire_target.is_none() &&
            mouse.is_some_and(|(x, y)|
                self.renderer.minimap_world_at(self.bounds, x, y).is_some())
        {
            self.minimap_dragging = true;
            self.selection_drag = None;
            self.renderer.selection_box = None;
            self.last_left_press = None;
            self.double_left_press = false;
        }
        if self.minimap_dragging && left_down {
            if let Some(center) = mouse.and_then(|(x, y)|
                self.renderer.minimap_world_at(self.bounds, x, y))
            {
                if self.renderer.camera.center != center {
                    self.renderer.camera.center = center;
                    self.minimap_camera_moves += 1;
                }
            }
        }
        if !cancel_support && !self.minimap_dragging &&
            left_down && !self.left_was_down {
            // Unity treats two presses within 0.25 real seconds as a
            // double-click. The second release must also be prompt.
            self.double_left_press = self.support_mode.is_none() &&
                self.last_left_press.is_some_and(|previous|
                    now.duration_since(previous) <= Duration::from_millis(250));
            self.last_left_press = Some(now);
            self.selection_drag = mouse.map(|(x, y)| (
                self.renderer.camera.screen_to_world(x, y, width, height), Vec2::new(x, y)));
        }
        if !cancel_support && !self.minimap_dragging {
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
                        self.last_left_press = None;
                        self.double_left_press = false;
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
                            if !additive && self.double_left_press &&
                                self.last_left_press.is_some_and(|pressed|
                                    now.duration_since(pressed) <= Duration::from_millis(250)) {
                                if let Some(ship_type) = view.combat.ship(ship).map(|body| body.ship_type) {
                                    self.renderer.selection.select_ship_type(&view, ship_type);
                                }
                            } else {
                                self.renderer.selection.select_ship(&view, ship, additive);
                            }
                        } else { self.renderer.selection.clear(); }
                    }
                }
            }
        }
        if !left_down {
            self.minimap_dragging = false;
            self.selection_drag = None;
            self.renderer.selection_box = None;
            self.double_left_press = false;
        }
        self.left_was_down = left_down;
        let release_click = right_released && self.right_press.take().is_some();
        let player_move = if support_command.is_none() && manual_fire.is_none() &&
            cease_fire_input.is_none() && accepts_orders {
            mouse.filter(|_| !self.renderer.selection.squads().is_empty()).and_then(|(x, y)| {
                let destination = self.renderer.camera.screen_to_world(x, y, width, height);
                if release_click {
                    if let Some(action) = self.renderer.pick_right_click_action(&view, destination) {
                        support_command = self.renderer.selection.support_command(&view, action);
                        return None;
                    }
                } else {
                    if !right_down || self.paused { return None; }
                    let press = self.right_press.as_mut()?;
                    let pixel = Vec2::new(x, y);
                    if now < press.next_move_at || press.last_move_pixel.is_some_and(|last|
                        (pixel - last).length_squared() <= 25.0
                    ) { return None; }
                    press.next_move_at = now + RIGHT_DRAG_INTERVAL;
                    press.last_move_pixel = Some(pixel);
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
        if let Some((_, speaker, line)) = dialogue {
            self.renderer.draw_campaign_dialogue(speaker, line);
        }
        self.render_wall_seconds += render_started.elapsed().as_secs_f64();
        let ui_started = Instant::now();
        let selected = self.renderer.selected_ship.and_then(|id| view.combat.ship(id))
            .filter(|ship| !ship.vitals.is_dead())
            .map(|ship| {
                // Source CarrierShip.Carrier is a live physical relation.
                // The owner can change at the Carrier death boundary;
                // never display the imported, now-stale metadata copy.
                let carrier = view.combat.game_striker_carrier(ship.entity_id)
                    .map(|state| state.carrier_id)
                    .or_else(|| view.combat.policy_striker_carrier(ship.entity_id))
                    .or_else(|| view.combat.drone_carrier(ship.entity_id));
                let carrier_label = match carrier {
                    Some(Some(id)) => format!(" | Carrier {}", id),
                    Some(None) => " | No Carrier".to_owned(),
                    None => String::new(),
                };
                format!(" | Ship {} type {} side {} HP {}/{} TSV {}{} | {}",
                    ship.entity_id, ship.ship_type, ship.side, ship.vitals.health(),
                    ship.vitals.max_health(), ship.vitals.tsv(), carrier_label,
                    view.combat.ship_cease_fire(ship.entity_id)
                        .map(|paused| if paused { "CEASE FIRE" } else { "ATTACK ON SIGHT" })
                        .unwrap_or("FIRING STATUS UNKNOWN"))
            }).unwrap_or_default();
        let title = if let Some((_, speaker, _)) = dialogue {
            format!("Bees native | World {} | Tick {} | {speaker} speaking | L continue, K dismiss, Esc close",
                view.world_id.0, view.tick)
        } else if let Some(result) = view.terminal_result {
            let outcome = crate::presentation::battle_outcome_label(result);
            format!("Bees native | World {} | Episode {} | {}{} | {:.2} s | {}Tab world, Esc close",
                view.world_id.0, view.episode_revision + 1, outcome,
                if result.timed_out { " (time limit)" } else { "" }, result.duration_seconds,
                self.result_controls.map_or("", NativeResultControls::hint))
        } else if self.playback {
            let status = if self.result_controls.is_some() { "Playback complete" }
                else if self.paused { "Playback paused" } else { "Playback" };
            format!("Bees native | World {} | Tick {} | {}{} | {}Tab world, Esc close",
                view.world_id.0, view.tick, status, selected,
                self.result_controls.map_or("Space pause, N step, ", NativeResultControls::hint))
        } else { format!(
            "Bees native | World {} | Episode {} | Tick {} | {} ships, {} shots | {} squads selected | {}{} | {} | range/obstruction {} | Left select/drag, double-click type, Ctrl add, right move/attack | C stop chase, V cease fire, A attack on sight, G guard, P patrol, M mine, H heal, R retreat, hold J manual fire, T point fire, X stop, B ranges, Backspace cancel | Space pause, N step, Tab world, 1-0 squads, PgUp/PgDn cycle, arrows pan, wheel zoom, F fit, I map, Esc exit",
            view.world_id.0, view.episode_revision, view.tick, counts.ships, counts.projectiles,
            self.renderer.selection.squads().len(),
            if self.paused { "PAUSED" } else { "RUNNING" }, selected, self.input_feedback,
            if self.renderer.show_weapon_ranges { format!("{} auto targets, {} contacts, {}/{} blocked, {} exact queries, {} prefiltered, {} missing; {} aim points, {} missing",
                counts.automatic_targets, counts.range_contacts, counts.obstructed_ranges, counts.range_overlaps,
                counts.range_queries, counts.range_candidates_culled, counts.missing_range_data,
                counts.aim_points, counts.missing_aim_data) }
            else { "off".to_owned() },
        ) };
        self.window.set_title(&title);
        self.ui_wall_seconds += ui_started.elapsed().as_secs_f64();
        self.rendered_frames += 1;
        self.rendered_pixels = self.rendered_pixels.saturating_add(
            (width as u64).saturating_mul(height as u64)
        );
        self.culled_ship_draws = self.culled_ship_draws.saturating_add(counts.culled_ships as u64);
        self.culled_obstacle_draws = self.culled_obstacle_draws.saturating_add(counts.culled_obstacles as u64);
        self.culled_projectile_draws = self.culled_projectile_draws.saturating_add(counts.culled_projectiles as u64);
        self.culled_piece_draws = self.culled_piece_draws.saturating_add(counts.culled_pieces as u64);
        self.minimap_markers_drawn = self.minimap_markers_drawn.saturating_add(counts.minimap_ship_markers as u64);
        self.numbered_squad_rows_drawn = self.numbered_squad_rows_drawn.saturating_add(counts.numbered_squad_rows as u64);
        self.weapon_geometry_queries = self.weapon_geometry_queries.saturating_add(counts.range_queries as u64);
        self.weapon_range_candidates_culled = self.weapon_range_candidates_culled
            .saturating_add(counts.range_candidates_culled as u64);
        let window_update_started = Instant::now();
        self.window.update_with_buffer(self.renderer.pixels(), width, height)?;
        self.window_update_wall_seconds += window_update_started.elapsed().as_secs_f64();
        if !self.window.is_open() { return Ok(NativeClientAction::Quit); }
        if accepts_orders && held_fire.is_none() {
            let action = cease_fire_input.map(NativePlayerInput::CeaseFire)
                .or_else(|| manual_fire.map(NativePlayerInput::Fire))
                .or_else(|| support_command.map(NativePlayerInput::Support))
                .or_else(|| player_move.map(NativePlayerInput::Move));
            if let Some(action) = action { return Ok(NativeClientAction::Input { action, step: false }); }
        }
        let after_frame = Instant::now();
        let step = !briefing && (single_step || (!self.paused && after_frame >= self.next_step));
        if step {
            // At most one authoritative step per poll. A slow inference or
            // render pass slows playback instead of changing dt or skipping
            // battle ticks. Preserve fractional pacing when running on time.
            self.next_step = (self.next_step + fixed_dt).max(after_frame);
        }
        if let Some(action) = held_fire {
            let input = NativePlayerHeldFire { world_id: view.world_id, episode_revision: view.episode_revision, action };
            Ok(NativeClientAction::Input { action: NativePlayerInput::HeldFire(input), step })
        } else if step {
            Ok(NativeClientAction::Step)
        } else {
            Ok(NativeClientAction::Wait)
        }
    }
}
