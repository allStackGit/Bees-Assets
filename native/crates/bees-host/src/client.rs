//! Optional native window around the shared host's borrowed render view.
//! Camera/inspection stay local; typed player moves carry world/episode
//! identity to the shared host. Policy decisions keep their real model route.

use std::time::{Duration, Instant};
use bees_sim::collision::Aabb;
use bees_sim::simulation::WorldId;
use bees_hivemind::session::SquadLifetime;
use minifb::{Key, KeyRepeat, MouseButton, MouseMode, Window, WindowOptions};
use crate::presentation::{HudAction, NativeCamera, NativeRenderWorldView, NativeSoftwareRenderer, NativeSupportMode};
use crate::game_audio::{NativeGameAudio, NativeAudioMeasurements};
use crate::unity_scene_snapshot::NativeMusicLocation;
use bees_gameplay::player_inputs::{NativeHeldFireAction, NativePlayerHeldFire, NativePlayerSquadMoves,
    NativePlayerSquadFire, NativePlayerSquadCeaseFire,
    NativePlayerSquadSpecial, NativePlayerShipSpecialKind,
    NativePlayerSquadMatchSpeed, NativePlayerSquadShooting,
    NativePlayerSquadLockOn, NativePlayerSquadChase};
use crate::player_trace::NativePlayerInput;
use bees_sim::geometry::Vec2;

#[derive(Clone, Debug, PartialEq)]
pub enum NativeClientAction {
    Wait, Step, Quit, WorldChanged, SideChanged,
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
    /// Capture the input surface on mouse-down. Crossing into the minimap
    /// must not re-map an ordinary world drag into a different coordinate
    /// system halfway through the held gesture.
    minimap: bool,
}

/// Rolling measured presentation throughput, never inferred from target FPS.
/// Counters come from the authoritative completed-tick and rendered-frame
/// paths, so a paused game reports zero SPS rather than invented progress.
struct NativeLivePerformance {
    enabled: bool,
    started: Instant,
    frames_at_start: u64,
    steps_at_start: u64,
    render_at_start: f64,
    present_at_start: f64,
    clamps_at_start: u64,
    fps: f64,
    sps: f64,
    render_ms: f64,
    present_ms: f64,
    lag_clamps: u64,
    sampled: bool,
}

impl NativeLivePerformance {
    fn new(now: Instant) -> Self {
        Self { enabled: false, started: now, frames_at_start: 0,
            steps_at_start: 0, render_at_start: 0.0, present_at_start: 0.0,
            clamps_at_start: 0, fps: 0.0, sps: 0.0, render_ms: 0.0,
            present_ms: 0.0, lag_clamps: 0, sampled: false }
    }

    fn sample(&mut self, now: Instant, frames: u64, steps: u64,
        render_seconds: f64, present_seconds: f64, lag_clamps: u64,
    ) {
        if !self.enabled { return; }
        let elapsed = now.saturating_duration_since(self.started).as_secs_f64();
        if elapsed < 1.0 { return; }
        let frames_seen = frames.saturating_sub(self.frames_at_start);
        self.fps = frames_seen as f64 / elapsed;
        self.sps = steps.saturating_sub(self.steps_at_start) as f64 / elapsed;
        self.render_ms = if frames_seen != 0 {
            (render_seconds - self.render_at_start).max(0.0) * 1000.0 / frames_seen as f64
        } else { 0.0 };
        self.present_ms = if frames_seen != 0 {
            (present_seconds - self.present_at_start).max(0.0) * 1000.0 / frames_seen as f64
        } else { 0.0 };
        self.lag_clamps = lag_clamps.saturating_sub(self.clamps_at_start);
        self.frames_at_start = frames;
        self.steps_at_start = steps;
        self.render_at_start = render_seconds;
        self.present_at_start = present_seconds;
        self.clamps_at_start = lag_clamps;
        self.started = now;
        self.sampled = true;
    }
}

/// Only a local, explicitly player-controlled two-faction game can
/// enter hot-seat mode. This state belongs to the window, never physics.
#[derive(Clone, Copy)]
struct NativeHotseat {
    sides: [i32; 2],
    active: i32,
}

pub struct NativeClient {
    window: Window,
    renderer: NativeSoftwareRenderer,
    audio: NativeGameAudio,
    music_locations: Vec<Option<NativeMusicLocation>>,
    bounds: Aabb,
    selected_world: usize,
    selection_owner: Option<(WorldId, u64)>,
    hotseat: Option<NativeHotseat>,
    paused: bool,
    performance: NativeLivePerformance,
    /// The Unity game exposes mouse-edge scrolling as a user preference.
    /// F6 toggles it locally without adding a gameplay or persisted setting.
    edge_scroll_enabled: bool,
    /// Source per-Level Map.MinZoom/MaxZoom. No gameplay-state copy.
    source_zoom_by_world: Vec<Option<(f32, f32)>>,
    next_step: Instant,
    /// Fixed ticks approved for the current client frame, not yet executed.
    pending_steps: u32,
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
    /// Clicks execute on release over the same button. Crossing its edge
    /// cancels; a HUD press never begins a gameplay selection drag.
    hud_press: Option<HudAction>,
    squad_tab_press: Option<SquadLifetime>,
    /// UI modal captures a shooting option's press/release, never a world
    /// click. Strategy choice is committed only through player input.
    shooting_press: Option<u8>,
    /// Cursor for keyboard/hover selection within the source's fixed
    /// 40-strategy registry. Presentation only; never a second weapon state.
    shooting_focus: u8,
    support_mode: Option<NativeTargetMode>,
    pub rendered_frames: u64,
    pub hotseat_switches: u64,
    pub fixed_steps_executed: u64,
    pub multi_step_frames: u64,
    pub max_steps_per_frame: u32,
    pub pacing_lag_clamps: u64,
    /// Total software framebuffer pixels actually drawn across changing sizes.
    pub rendered_pixels: u64,
    pub render_wall_seconds: f64,
    pub ui_wall_seconds: f64,
    /// Includes minifb's frame pacing and display transfer, separate from
    /// CPU software rendering time.
    pub window_update_wall_seconds: f64,
    pub culled_ship_draws: u64,
    pub ship_sprite_draws: u64,
    pub combat_impacts_observed: u64,
    pub combat_impact_visual_draws: u64,
    pub combat_impact_visuals_dropped: u64,
    pub custom_ship_color_draws: u64,
    pub ship_color_cache_misses: u64,
    pub ship_color_cache_variants: usize,
    pub sprite_raster_candidate_pixels: u64,
    pub ship_fallback_hulls: u64,
    pub ship_art_types_loaded: usize,
    pub asteroid_sprite_draws: u64,
    pub asteroid_piece_sprite_draws: u64,
    pub asteroid_fallback_hulls: u64,
    pub asteroid_art_types_loaded: usize,
    pub projectile_sprite_draws: u64,
    pub projectile_explosion_sprite_draws: u64,
    pub projectile_fallback_hulls: u64,
    pub projectile_art_types_loaded: usize,
    pub culled_obstacle_draws: u64,
    pub culled_projectile_draws: u64,
    pub culled_piece_draws: u64,
    pub weapon_geometry_queries: u64,
    pub weapon_range_candidates_culled: u64,
    pub viewport_resizes: u64,
    pub minimap_markers_drawn: u64,
    pub numbered_squad_rows_drawn: u64,
    pub striker_bomb_indicators_drawn: u64,
    pub minimap_camera_moves: u64,
    pub camera_pan_frames: u64,
    pub camera_wheel_pans: u64,
    pub camera_boundary_clamps: u64,
    pub camera_zoom_clamps: u64,
    pub numbered_shortcut_selections: u64,
    pub cycled_shortcut_selections: u64,
    pub minimap_move_orders_submitted: u64,
    pub tactical_button_clicks: u64,
    pub squad_tab_clicks: u64,
    pub shooting_priority_requests_submitted: u64,
    pub selected_command_rows_drawn: u64,
    pub command_target_markers_drawn: u64,
}

impl NativeClient {
    pub fn new(bounds: Aabb) -> Result<Self, Box<dyn std::error::Error>> {
        let renderer = NativeSoftwareRenderer::new(1100, 760, bounds)?;
        let mut window = Window::new("Bees native", 1100, 760,
            WindowOptions { resize: true, ..WindowOptions::default() })?;
        window.set_target_fps(60);
        let now = Instant::now();
        Ok(Self { window, renderer, audio: NativeGameAudio::new(),
            music_locations: Vec::new(), bounds,
            selected_world: 0, selection_owner: None, hotseat: None,
            paused: false, performance: NativeLivePerformance::new(now),
            edge_scroll_enabled: false,
            source_zoom_by_world: Vec::new(),
            next_step: now, pending_steps: 0, previous_frame: now, left_was_down: false,
            right_was_down: false, right_press: None, last_left_press: None, double_left_press: false,
            input_feedback: "",
            result_controls: None,
            playback: false,
            dialogue_sequence: None,
            dialogue_line: 0,
            dialogue_visible: false,
            held_fire_target: None,
            selection_drag: None, minimap_dragging: false,
            hud_press: None, squad_tab_press: None, shooting_press: None,
            shooting_focus: 1, support_mode: None,
            rendered_frames: 0, hotseat_switches: 0,
            fixed_steps_executed: 0, multi_step_frames: 0,
            max_steps_per_frame: 0, pacing_lag_clamps: 0,
            rendered_pixels: 0, render_wall_seconds: 0.0,
            ui_wall_seconds: 0.0, window_update_wall_seconds: 0.0,
            culled_ship_draws: 0, ship_sprite_draws: 0,
            combat_impacts_observed: 0, combat_impact_visual_draws: 0,
            combat_impact_visuals_dropped: 0,
            custom_ship_color_draws: 0, ship_color_cache_misses: 0,
            ship_color_cache_variants: 0,
            sprite_raster_candidate_pixels: 0, ship_fallback_hulls: 0, ship_art_types_loaded: 0,
            asteroid_sprite_draws: 0, asteroid_piece_sprite_draws: 0,
            asteroid_fallback_hulls: 0, asteroid_art_types_loaded: 0,
            projectile_sprite_draws: 0, projectile_explosion_sprite_draws: 0,
            projectile_fallback_hulls: 0,
            projectile_art_types_loaded: 0,
            culled_obstacle_draws: 0,
            culled_projectile_draws: 0, culled_piece_draws: 0,
            weapon_geometry_queries: 0, weapon_range_candidates_culled: 0,
            viewport_resizes: 0,
            minimap_markers_drawn: 0, numbered_squad_rows_drawn: 0,
            striker_bomb_indicators_drawn: 0,
            minimap_camera_moves: 0, camera_pan_frames: 0,
            camera_wheel_pans: 0, camera_boundary_clamps: 0,
            camera_zoom_clamps: 0, numbered_shortcut_selections: 0,
            cycled_shortcut_selections: 0,
            minimap_move_orders_submitted: 0,
            tactical_button_clicks: 0, squad_tab_clicks: 0,
            shooting_priority_requests_submitted: 0,
            selected_command_rows_drawn: 0, command_target_markers_drawn: 0 })
    }

    /// Imported once from the source scene, not inferred from a UI map.
    pub fn set_source_edge_scrolling(&mut self, enabled: Option<bool>) {
        if let Some(enabled) = enabled {
            self.edge_scroll_enabled = enabled;
        }
    }

    pub fn set_source_zoom_limits(&mut self, limits: Vec<Option<(f32, f32)>>) {
        self.source_zoom_by_world = limits;
        let (width, height) = self.renderer.size();
        let authored_zoom = self.active_source_zoom();
        if self.renderer.camera.constrain_source_zoom(
            authored_zoom, height,
        ) {
            self.camera_zoom_clamps += 1;
        }
        self.renderer.camera.constrain_to_map(self.bounds, width, height);
    }

    fn active_source_zoom(&self) -> Option<(f32, f32)> {
        self.source_zoom_by_world.get(self.selected_world).copied().flatten()
    }

    /// Initial authored camera focus is a presentation-only concern. The
    /// world, simulation coordinates and replayed player inputs are unchanged.
    pub fn focus_camera(&mut self, center: Vec2) {
        if center.is_finite() {
            self.renderer.camera.center = center;
            let (width, height) = self.renderer.size();
            self.renderer.camera.constrain_to_map(self.bounds, width, height);
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

    pub fn set_music_locations(&mut self, locations: Vec<Option<NativeMusicLocation>>) {
        self.music_locations = locations;
        self.audio.select_location(self.music_locations.first().copied().flatten());
    }

    pub fn audio_measurements(&self) -> NativeAudioMeasurements {
        self.audio.measurements()
    }

    pub fn selected_world(&self) -> usize { self.selected_world }

    /// Used only with the source-authored two-local-player permission
    /// preflight. Remote play has its own authenticated peer side instead.
    pub fn enable_hotseat(&mut self, sides: [i32; 2], initial: i32) {
        if sides[0] != sides[1] && sides.contains(&initial) {
            self.hotseat = Some(NativeHotseat { sides, active: initial });
        }
    }

    pub fn hotseat_side(&self) -> Option<i32> {
        self.hotseat.map(|hotseat| hotseat.active)
    }

    pub fn pending_steps(&self) -> u32 { self.pending_steps }

    /// Commit only ticks actually executed by the common simulation. Model
    /// boundaries, source command pulses and trace checkpoints can shorten
    /// a frame's planned catch-up without losing the remaining clock debt.
    pub fn commit_steps(&mut self, executed: u32, fixed_dt: Duration) {
        debug_assert!(executed > 0 && executed <= self.pending_steps);
        if self.paused {
            // Explicit N stepping must not accumulate realtime clock debt.
            self.next_step = Instant::now();
        } else {
            self.next_step += fixed_dt.saturating_mul(executed);
        }
        self.fixed_steps_executed += u64::from(executed);
        self.multi_step_frames += u64::from(executed > 1);
        self.max_steps_per_frame = self.max_steps_per_frame.max(executed);
        self.pending_steps = 0;
    }

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
        self.pending_steps = 0;
        if !self.window.is_open() {
            return Ok(NativeClientAction::Quit);
        }
        // Esc dismisses the temporary strategy picker before affecting
        // the running battle. Use the press edge so a held key cannot
        // also quit on the following frame after closing the modal.
        if self.window.is_key_pressed(Key::Escape, KeyRepeat::No) {
            if self.renderer.show_shooting_menu {
                self.renderer.show_shooting_menu = false;
                self.shooting_press = None;
            } else {
                return Ok(NativeClientAction::Quit);
            }
        }
        let (window_width, window_height) = self.window.get_size();
        if window_width == 0 || window_height == 0 {
            // Minimized windows have no drawable surface; pump OS events
            // without trying to submit a zero-sized bitmap.
            self.window.update();
            self.right_press = None;
            self.minimap_dragging = false;
            self.hud_press = None;
            self.squad_tab_press = None;
            self.shooting_press = None;
            self.renderer.show_shooting_menu = false;
            self.previous_frame = Instant::now();
            return Ok(NativeClientAction::Wait);
        }
        if self.renderer.resize(window_width, window_height)? {
            // A drag's original screen coordinates no longer describe
            // the same selection box after a window resize.
            self.selection_drag = None;
            self.minimap_dragging = false;
            self.hud_press = None;
            self.squad_tab_press = None;
            self.shooting_press = None;
            self.renderer.show_shooting_menu = false;
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
            self.hud_press = None;
            self.squad_tab_press = None;
            self.shooting_press = None;
            self.renderer.show_shooting_menu = false;
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
        // Hot-seat changes only the local commander's view. A campaign's
        // authored story belongs to its source player side, not whichever
        // opposing commander is currently inspecting the shared World.
        let dialogue_for_viewer = view.campaign_dialogue_side.is_none_or(|side|
            view.player_authority.player_viewer_side() == Some(side));
        let dialogue = view.campaign_dialogue
            .filter(|_| dialogue_for_viewer)
            .and_then(|(sequence, cue)| {
            let first_presentation = self.dialogue_sequence != Some(sequence);
            if first_presentation {
                self.dialogue_sequence = Some(sequence);
                self.dialogue_line = 0;
            }
            let lines = crate::presentation::campaign_dialogue::lines(cue);
            if self.window.is_key_pressed(Key::K, KeyRepeat::No) {
                self.dialogue_line = lines.len();
            } else if self.dialogue_line < lines.len() && self.window.is_key_pressed(Key::L, KeyRepeat::No) {
                self.dialogue_line += 1;
            }
            // Source DialogueManager plays the intercom once, on the
            // first visible line of each newly presented section. A
            // skipped section makes no sound, and advancing within the
            // same section never repeats the cue.
            if first_presentation && self.dialogue_line < lines.len() {
                self.audio.campaign_dialogue_presented();
            }
            lines.get(self.dialogue_line).map(|&(speaker, line)| (cue, speaker, line))
        });
        self.dialogue_visible = dialogue.is_some();
        // Hiding the owner's story from the opposite hot-seat must not
        // advance the authoritative mission before the owner has read it.
        let briefing = if dialogue_for_viewer {
            dialogue.is_some_and(|(cue, _, _)| cue.is_briefing())
        } else {
            view.terminal_result.is_none() &&
                view.campaign_dialogue.is_some_and(|(_, cue)| cue.is_briefing())
        };
        if view.terminal_result.is_some() { self.held_fire_target = None; }
        self.bounds = bounds;
        if self.window.is_key_pressed(Key::Space, KeyRepeat::No) {
            self.paused = !self.paused;
            self.next_step = now;
        }
        self.audio.set_paused(self.paused);
        if self.window.is_active() && self.window.is_key_pressed(Key::F7, KeyRepeat::No) {
            self.input_feedback = if self.audio.toggle_muted() {
                "Audio muted"
            } else { "Audio enabled" };
        }
        if self.window.is_active() && self.window.is_key_pressed(Key::F8, KeyRepeat::No) {
            self.performance.enabled = !self.performance.enabled;
            // A disabled interval must not dilute the next measured SPS.
            self.performance.sampled = false;
            self.performance.started = now;
            self.performance.frames_at_start = self.rendered_frames;
            self.performance.steps_at_start = self.fixed_steps_executed;
            self.performance.render_at_start = self.render_wall_seconds;
            self.performance.present_at_start = self.window_update_wall_seconds;
            self.performance.clamps_at_start = self.pacing_lag_clamps;
        }
        if self.window.is_active() && self.window.is_key_pressed(Key::F9, KeyRepeat::No) &&
            self.held_fire_target.is_none()
        {
            if let Some(hotseat) = &mut self.hotseat {
                hotseat.active = if hotseat.active == hotseat.sides[0] {
                    hotseat.sides[1]
                } else { hotseat.sides[0] };
                self.hotseat_switches += 1;
                // A side switch never carries a selected squad, held HUD
                // action or pointer drag across the faction permission
                // boundary. The next frame builds a fresh borrowed view.
                self.renderer.selected_ship = None;
                self.renderer.selection.clear();
                self.renderer.selection_box = None;
                self.selection_drag = None;
                self.minimap_dragging = false;
                self.support_mode = None;
                self.right_press = None;
                self.hud_press = None;
                self.squad_tab_press = None;
                self.shooting_press = None;
                self.renderer.show_shooting_menu = false;
                self.last_left_press = None;
                self.double_left_press = false;
                self.left_was_down = self.window.get_mouse_down(MouseButton::Left);
                self.right_was_down = self.window.get_mouse_down(MouseButton::Right);
                self.input_feedback = if hotseat.active == hotseat.sides[0] {
                    "Switched to Bees commander"
                } else { "Switched to Humans commander" };
                self.window.update();
                self.next_step = Instant::now();
                return Ok(NativeClientAction::SideChanged);
            }
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
            self.audio.select_location(
                self.music_locations.get(self.selected_world).copied().flatten(),
            );
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
            self.hud_press = None;
            self.squad_tab_press = None;
            self.shooting_press = None;
            self.renderer.show_shooting_menu = false;
            self.window.update();
            self.next_step = Instant::now();
            return Ok(NativeClientAction::WorldChanged);
        }
        let (width, height) = self.renderer.size();
        let authored_zoom = self.active_source_zoom();
        if self.window.is_key_pressed(Key::F, KeyRepeat::No) {
            self.renderer.camera = NativeCamera::fit(self.bounds, width, height);
        }
        if self.window.is_active() && self.window.is_key_pressed(Key::F6, KeyRepeat::No) {
            self.edge_scroll_enabled = !self.edge_scroll_enabled;
            self.input_feedback = if self.edge_scroll_enabled {
                "Mouse-edge scrolling enabled"
            } else { "Mouse-edge scrolling disabled" };
        }
        let speed = 500.0 * elapsed / self.renderer.camera.pixels_per_unit;
        let mut pan_x = 0.0;
        let mut pan_y = 0.0;
        if self.window.is_key_down(Key::Left) { pan_x -= speed; }
        if self.window.is_key_down(Key::Right) { pan_x += speed; }
        if self.window.is_key_down(Key::Up) { pan_y += speed; }
        if self.window.is_key_down(Key::Down) { pan_y -= speed; }
        let mouse = self.window.get_mouse_pos(MouseMode::Discard);
        if self.edge_scroll_enabled && self.window.is_active() &&
            !self.renderer.show_shooting_menu {
            if let Some((x, y)) = mouse.filter(|&(x, y)|
                self.renderer.minimap_world_at(bounds, x, y).is_none()) {
                // Unity's left/right and top/bottom edge pan, adapted to
                // framebuffer coordinates. Fixed physical pixels produce
                // consistent input across 4K and small windows.
                const EDGE: f32 = 18.0;
                if x <= EDGE { pan_x -= speed; }
                else if x >= width as f32 - EDGE { pan_x += speed; }
                if y <= EDGE { pan_y += speed; }
                else if y >= height as f32 - EDGE { pan_y -= speed; }
            }
        }
        if pan_x != 0.0 || pan_y != 0.0 {
            self.renderer.camera.center += Vec2::new(pan_x, pan_y);
            self.camera_pan_frames += 1;
        }
        // Source Shift+wheel pans horizontally and Control+wheel vertically,
        // rather than zooming. Native mouse drivers may also provide a
        // genuine horizontal wheel axis; use the same camera owner.
        if let Some((horizontal, vertical)) = self.window.get_scroll_wheel() {
            let shift = self.window.is_key_down(Key::LeftShift) ||
                self.window.is_key_down(Key::RightShift);
            let control = self.window.is_key_down(Key::LeftCtrl) ||
                self.window.is_key_down(Key::RightCtrl);
            let wheel_pixels = 120.0 / self.renderer.camera.pixels_per_unit;
            let mut pan = Vec2::new(horizontal.clamp(-10.0, 10.0) * wheel_pixels, 0.0);
            if shift {
                pan.x -= vertical.clamp(-10.0, 10.0) * wheel_pixels;
            } else if control {
                pan.y += vertical.clamp(-10.0, 10.0) * wheel_pixels;
            } else if vertical != 0.0 {
                if let Some((x, y)) = mouse {
                    let factor = 1.15_f32.powf(vertical.clamp(-10.0, 10.0));
                    if let Some(map_point) = self.renderer.minimap_world_at(bounds, x, y) {
                        self.renderer.camera.center = map_point;
                        self.minimap_camera_moves += 1;
                        if self.renderer.camera.zoom_at(factor, width as f32 * 0.5,
                            height as f32 * 0.5, width, height, authored_zoom) {
                            self.camera_zoom_clamps += 1;
                        }
                    } else {
                        if self.renderer.camera.zoom_at(factor, x, y, width, height,
                            authored_zoom) {
                            self.camera_zoom_clamps += 1;
                        }
                    }
                }
            }
            if pan.x != 0.0 || pan.y != 0.0 {
                self.renderer.camera.center += pan;
                self.camera_wheel_pans += 1;
            }
        }
        let allows_input = !briefing && !self.playback && view.terminal_result.is_none();
        let pointer_active = self.window.is_active();
        let left_down = self.window.get_mouse_down(MouseButton::Left);
        // The strategy picker is purely presentational. It never changes
        // a weapon state until the native host accepts a typed input. A
        // press captured by this modal cannot become a world selection.
        if !allows_input || self.renderer.selection.squads().is_empty() ||
            self.renderer.size().0 < 980 || self.renderer.size().1 < 520 {
            self.renderer.show_shooting_menu = false;
        }
        let menu_was_open = self.renderer.show_shooting_menu;
        if menu_was_open {
            let mut focus = usize::from(self.shooting_focus.saturating_sub(1));
            // Grid is column-major (4 columns of ten choices); arrow
            // navigation must follow the exact buttons drawn on screen.
            if self.window.is_key_pressed(Key::Up, KeyRepeat::No) {
                focus = focus / 10 * 10 + (focus % 10 + 9) % 10;
            } else if self.window.is_key_pressed(Key::Down, KeyRepeat::No) {
                focus = focus / 10 * 10 + (focus % 10 + 1) % 10;
            } else if self.window.is_key_pressed(Key::Left, KeyRepeat::No) {
                focus = (focus + 30) % 40;
            } else if self.window.is_key_pressed(Key::Right, KeyRepeat::No) {
                focus = (focus + 10) % 40;
            }
            // Hover alone must not steal keyboard focus: a stationary
            // cursor over one cell would otherwise undo every arrow key.
            // Mouse users still click any option directly.
            self.shooting_focus = (focus + 1) as u8;
        }
        if menu_was_open && pointer_active && left_down && !self.left_was_down {
            self.shooting_press = mouse.and_then(|(x, y)|
                self.renderer.shooting_strategy_at(x, y));
            self.selection_drag = None;
            self.minimap_dragging = false;
            self.renderer.selection_box = None;
            self.last_left_press = None;
            self.double_left_press = false;
        }
        let clicked_shooting = if menu_was_open &&
            self.window.is_key_pressed(Key::Enter, KeyRepeat::No)
        {
            self.renderer.show_shooting_menu = false;
            Some(self.shooting_focus)
        } else if menu_was_open && self.left_was_down && !left_down {
            let selection = self.shooting_press.take().filter(|&id|
                mouse.and_then(|(x, y)| self.renderer.shooting_strategy_at(x, y))
                    == Some(id));
            self.renderer.show_shooting_menu = false;
            selection
        } else { None };
        if menu_was_open &&
            (self.window.is_key_pressed(Key::S, KeyRepeat::No) ||
             self.window.is_key_pressed(Key::Backspace, KeyRepeat::No)) {
            self.renderer.show_shooting_menu = false;
            self.shooting_press = None;
        }
        let shooting_input = clicked_shooting.map(|strategy_id| {
            self.shooting_priority_requests_submitted += 1;
            NativePlayerSquadShooting {
                world_id: view.world_id, episode_revision: view.episode_revision,
                squads: self.renderer.selection.squads().to_vec(), strategy_id,
            }
        });
        let accepts_orders = allows_input && !menu_was_open;
        if menu_was_open && !left_down { self.shooting_press = None; }
        if !accepts_orders || !pointer_active {
            self.hud_press = None;
            self.squad_tab_press = None;
        } else if left_down && !self.left_was_down {
            self.hud_press = mouse.and_then(|(x, y)|
                self.renderer.tactical_action_at(x, y));
            self.squad_tab_press = if self.hud_press.is_none() {
                mouse.and_then(|(x, y)| self.renderer.squad_tab_at(&view, x, y))
            } else { None };
            if self.hud_press.is_some() || self.squad_tab_press.is_some() {
                self.selection_drag = None;
                self.minimap_dragging = false;
                self.renderer.selection_box = None;
                self.last_left_press = None;
                self.double_left_press = false;
            }
        }
        let clicked_hud = if self.left_was_down && !left_down {
            self.hud_press.take().filter(|&action|
                mouse.and_then(|(x, y)| self.renderer.tactical_action_at(x, y))
                    == Some(action))
        } else { None };
        if clicked_hud.is_some() {
            self.tactical_button_clicks += 1;
            self.audio.click_ui();
        }
        if accepts_orders && pointer_active &&
            self.held_fire_target.is_none() && (
            clicked_hud == Some(HudAction::Shooting) ||
            self.window.is_key_pressed(Key::S, KeyRepeat::No)
        ) {
            if self.renderer.selection.squads().is_empty() {
                self.input_feedback = "Select squads first";
            } else if self.renderer.size().0 < 980 || self.renderer.size().1 < 520 {
                self.input_feedback = "Enlarge the window to choose shooting priorities";
            } else {
                self.renderer.show_shooting_menu = true;
                self.shooting_focus = self.renderer.selection.squads().first()
                    .and_then(|&owner| view.player_authority.player_shooting_strategy(owner))
                    .map_or(1, |strategy| strategy.id);
                self.support_mode = None;
                self.selection_drag = None;
                self.renderer.selection_box = None;
                self.last_left_press = None;
                self.double_left_press = false;
            }
        }
        let clicked_tab = if self.left_was_down && !left_down {
            self.squad_tab_press.take().filter(|&owner|
                mouse.and_then(|(x, y)| self.renderer.squad_tab_at(&view, x, y))
                    == Some(owner))
        } else { None };
        if let Some(owner) = clicked_tab {
            self.audio.click_ui();
            let additive = self.window.is_key_down(Key::LeftCtrl) ||
                self.window.is_key_down(Key::RightCtrl);
            if self.renderer.selection.select_squad(&view, owner, additive) {
                self.squad_tab_clicks += 1;
                if !additive { self.focus_selected_squad(&view, owner); }
                else {
                    self.renderer.selected_ship = self.renderer.selection
                        .representative_ships(&view).next();
                    self.support_mode = None;
                }
            }
        }
        if accepts_orders && pointer_active &&
            (clicked_hud == Some(HudAction::SelectAll) ||
             ((self.window.is_key_down(Key::LeftCtrl) ||
               self.window.is_key_down(Key::RightCtrl)) &&
              self.window.is_key_pressed(Key::A, KeyRepeat::No)))
        {
            self.renderer.selection.select_all_player_squads(&view);
            self.renderer.selected_ship = self.renderer.selection
                .representative_ships(&view).next();
            self.support_mode = None;
            self.selection_drag = None;
            self.renderer.selection_box = None;
            self.last_left_press = None;
            self.double_left_press = false;
        }
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
        let stop_fire = clicked_hud == Some(HudAction::StopFire) ||
            self.window.is_key_pressed(Key::X, KeyRepeat::No);
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
            for (key, button, mode, prompt) in [
                (None, HudAction::Attack, NativeTargetMode::Support(NativeSupportMode::Attack), "Attack: click an enemy ship"),
                (None, HudAction::Circle, NativeTargetMode::Support(NativeSupportMode::Circle), "Circle: click an enemy squad"),
                (None, HudAction::RightSwipe, NativeTargetMode::Support(NativeSupportMode::RightSwipe), "Right Swipe: click an enemy squad"),
                (None, HudAction::LeftSwipe, NativeTargetMode::Support(NativeSupportMode::LeftSwipe), "Left Swipe: click an enemy squad"),
                (None, HudAction::InAndOut, NativeTargetMode::Support(NativeSupportMode::InAndOut), "In and Out: click an enemy squad"),
                (Some(Key::G), HudAction::Guard, NativeTargetMode::Support(NativeSupportMode::Guard), "Guard: click a friendly ship"),
                (Some(Key::P), HudAction::Patrol, NativeTargetMode::Support(NativeSupportMode::Patrol), "Patrol: drag the patrol area"),
                (Some(Key::M), HudAction::Mine, NativeTargetMode::Support(NativeSupportMode::Mining), "Mine: click a mining asteroid"),
                (Some(Key::H), HudAction::Heal, NativeTargetMode::Support(NativeSupportMode::Heal), "Heal: click a friendly Beehive"),
                (Some(Key::R), HudAction::Retreat, NativeTargetMode::Support(NativeSupportMode::FullRetreat), "Retreat: click a friendly WarpGate"),
                (Some(Key::T), HudAction::PointFire, NativeTargetMode::ManualFire, "Point fire: click a point; X stops selected weapons"),
            ] {
                let triggered = clicked_hud == Some(button) ||
                    key.is_some_and(|key| self.window.is_key_pressed(key, KeyRepeat::No));
                if triggered &&
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
                .then_some(RightPress {
                    next_move_at: now + RIGHT_DRAG_INTERVAL,
                    last_move_pixel: None,
                    minimap: mouse.is_some_and(|(x, y)|
                        self.renderer.minimap_world_at(bounds, x, y).is_some()),
                });
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
                squads: self.renderer.selection.squads().to_vec(), controller_id: 0, target: None })
        } else { None };
        let firing_mode = if accepts_orders &&
            (clicked_hud == Some(HudAction::CeaseFire) ||
             self.window.is_key_pressed(Key::V, KeyRepeat::No)) {
            Some(true)
        } else if accepts_orders &&
            (clicked_hud == Some(HudAction::AttackOnSight) ||
             (!self.window.is_key_down(Key::LeftCtrl) &&
              !self.window.is_key_down(Key::RightCtrl) &&
              self.window.is_key_pressed(Key::A, KeyRepeat::No))) {
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
        // UI ship specials are queued on the physical world, without
        // altering selected squad commands or their HiveMind reservations.
        // K doubles as dialogue skip when narrative is visible, so it
        // must never also issue a Beacon in that frame.
        let special_kind = if accepts_orders && pointer_active &&
            !self.dialogue_visible && self.held_fire_target.is_none() &&
            (clicked_hud == Some(HudAction::Beacon) ||
             self.window.is_key_pressed(Key::K, KeyRepeat::No)) {
            Some(NativePlayerShipSpecialKind::DropBeacon)
        } else if accepts_orders && pointer_active &&
            self.held_fire_target.is_none() &&
            (clicked_hud == Some(HudAction::Detonate) ||
             self.window.is_key_pressed(Key::D, KeyRepeat::No)) {
            Some(NativePlayerShipSpecialKind::Detonate)
        } else if accepts_orders && pointer_active &&
            self.held_fire_target.is_none() &&
            (clicked_hud == Some(HudAction::Charge) ||
             self.window.is_key_pressed(Key::Q, KeyRepeat::No)) {
            Some(NativePlayerShipSpecialKind::Charge)
        } else { None };
        let special_input = special_kind.and_then(|kind| {
            if self.renderer.selection.squads().is_empty() {
                self.input_feedback = "Select squads first";
                None
            } else {
                // The source Fire Barge action has a distinct click for
                // every selected real Fire Barge. Check the same live
                // CombatWorld roster as the eventual user command: never
                // sound an unavailable special for an unrelated squad.
                if kind == NativePlayerShipSpecialKind::Detonate {
                    let barges = self.renderer.selection.squads().iter()
                        .filter_map(|&owner| view.roster.get(owner))
                        .flat_map(|squad| squad.members.iter())
                        .filter(|&&id| view.combat.ship(id).is_some_and(|ship|
                            ship.ship_type == 10 && !ship.vitals.is_dead()))
                        .count();
                    self.audio.click_fire_barge_detonation(barges);
                }
                self.support_mode = None;
                self.selection_drag = None;
                self.renderer.selection_box = None;
                Some(NativePlayerSquadSpecial {
                    world_id: view.world_id,
                    episode_revision: view.episode_revision,
                    squads: self.renderer.selection.squads().to_vec(),
                    kind,
                })
            }
        });
        let chase_input = (accepts_orders && pointer_active &&
            self.held_fire_target.is_none() &&
            (clicked_hud == Some(HudAction::Chase) ||
             self.window.is_key_pressed(Key::E, KeyRepeat::No)))
            .then(|| {
                if self.renderer.selection.squads().is_empty() {
                    self.input_feedback = "Select squads first";
                    return None;
                }
                self.support_mode = None;
                Some(NativePlayerSquadChase {
                    world_id: view.world_id, episode_revision: view.episode_revision,
                    squads: self.renderer.selection.squads().to_vec(), chasing: true,
                })
            }).flatten();
        let lock_on_input = (accepts_orders && pointer_active &&
            self.held_fire_target.is_none() &&
            (clicked_hud == Some(HudAction::LockOn) ||
             self.window.is_key_pressed(Key::O, KeyRepeat::No)))
            .then(|| {
                let squads = self.renderer.selection.squads();
                if squads.is_empty() {
                    self.input_feedback = "Select squads first";
                    return None;
                }
                let currently_locked = squads.iter().all(|&owner|
                    view.player_authority.player_permissions(owner)
                        .is_some_and(|p| p.locked_on)
                );
                self.support_mode = None;
                Some(NativePlayerSquadLockOn {
                    world_id: view.world_id, episode_revision: view.episode_revision,
                    squads: squads.to_vec(), locked: !currently_locked,
                })
            }).flatten();
        let matching = if accepts_orders && pointer_active &&
            (clicked_hud == Some(HudAction::MatchSpeed) ||
             self.window.is_key_pressed(Key::Y, KeyRepeat::No)) {
            Some(true)
        } else if accepts_orders && pointer_active &&
            (clicked_hud == Some(HudAction::FreeSpeed) ||
             self.window.is_key_pressed(Key::U, KeyRepeat::No)) {
            Some(false)
        } else { None };
        let match_speed_input = matching.and_then(|match_speed| {
            if self.renderer.selection.squads().is_empty() {
                self.input_feedback = "Select squads first";
                None
            } else {
                self.support_mode = None;
                self.selection_drag = None;
                self.renderer.selection_box = None;
                Some(NativePlayerSquadMatchSpeed {
                    world_id: view.world_id,
                    episode_revision: view.episode_revision,
                    squads: self.renderer.selection.squads().to_vec(),
                    match_speed,
                })
            }
        });
        let mut support_command = None;
        if accepts_orders &&
            (clicked_hud == Some(HudAction::Hold) ||
             self.window.is_key_pressed(Key::C, KeyRepeat::No)) {
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
        if !pointer_active { self.minimap_dragging = false; }
        if pointer_active && left_down && !self.left_was_down &&
            self.hud_press.is_none() && self.squad_tab_press.is_none() &&
            !menu_was_open && self.support_mode.is_none() &&
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
            self.hud_press.is_none() && self.squad_tab_press.is_none() &&
            !menu_was_open &&
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
        if !cancel_support && !self.minimap_dragging &&
            self.hud_press.is_none() && clicked_hud.is_none() &&
            self.squad_tab_press.is_none() && clicked_tab.is_none() &&
            !menu_was_open {
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
                                squads: self.renderer.selection.squads().to_vec(),
                                controller_id: 0, target: Some(end) });
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
                                if additive {
                                    // Ctrl-click can remove the clicked squad.
                                    // Do not keep its unselected ship highlighted.
                                    self.renderer.selected_ship = self.renderer.selection
                                        .representative_ships(&view).next();
                                }
                            }
                        } else {
                            // Preserve the existing multi-selection when
                            // Ctrl-clicking empty terrain, as the native
                            // remote commander already does. A plain click
                            // still dismisses the selection.
                            if !self.window.is_key_down(Key::LeftCtrl) &&
                                !self.window.is_key_down(Key::RightCtrl) {
                                self.renderer.selection.clear();
                            }
                            self.renderer.selected_ship = self.renderer.selection
                                .representative_ships(&view).next();
                        }
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
        let released_press = if right_released {
            self.right_press.take()
        } else { None };
        let player_move = if special_input.is_none() && match_speed_input.is_none() &&
            lock_on_input.is_none() && chase_input.is_none() &&
            support_command.is_none() && manual_fire.is_none() &&
            cease_fire_input.is_none() && accepts_orders {
            mouse.filter(|_| !self.renderer.selection.squads().is_empty()).and_then(|(x, y)| {
                let captured_minimap = if right_released {
                    released_press.as_ref()?.minimap
                } else {
                    self.right_press.as_ref()?.minimap
                };
                let over_minimap = self.renderer.minimap_world_at(bounds, x, y);
                // A drag beginning outside the map cannot turn into a
                // minimap order by moving over the HUD mid-gesture.
                let destination = if captured_minimap {
                    over_minimap?
                } else {
                    if over_minimap.is_some() { return None; }
                    self.renderer.camera.screen_to_world(x, y, width, height)
                };
                if right_released {
                    if released_press.is_none() { return None; }
                    if !captured_minimap {
                        if let Some(action) = self.renderer.pick_right_click_action(&view, destination) {
                            support_command = self.renderer.selection.support_command(&view, action);
                            return None;
                        }
                        if let Some(target) = self.renderer.pick_demolition_fire_point(
                            &view, destination,
                        ) {
                            // Submit the exact same authenticated point-fire
                            // used by the explicit T tool; firing never
                            // creates a separate mission damage pathway.
                            manual_fire = Some(NativePlayerSquadFire {
                                world_id: view.world_id,
                                episode_revision: view.episode_revision,
                                squads: self.renderer.selection.squads().to_vec(),
                                controller_id: 0,
                                target: Some(target),
                            });
                            self.input_feedback = "Bombardment: target selected";
                            return None;
                        }
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
                if captured_minimap {
                    self.minimap_move_orders_submitted += 1;
                }
                Some(NativePlayerSquadMoves { world_id: view.world_id,
                    episode_revision: view.episode_revision,
                    squads: self.renderer.selection.squads().to_vec(),
                    destination })
            })
        } else { None };
        self.right_was_down = right_down;
        self.renderer.focused_shooting_strategy = self.shooting_focus;
        self.renderer.hovered_tactical_action = if accepts_orders && pointer_active {
            mouse.and_then(|(x, y)| self.renderer.tactical_action_at(x, y))
        } else { None };
        self.renderer.active_tactical_action = match self.support_mode {
            Some(NativeTargetMode::Support(NativeSupportMode::Attack)) => Some(HudAction::Attack),
            Some(NativeTargetMode::Support(NativeSupportMode::Circle)) => Some(HudAction::Circle),
            Some(NativeTargetMode::Support(NativeSupportMode::RightSwipe)) => Some(HudAction::RightSwipe),
            Some(NativeTargetMode::Support(NativeSupportMode::LeftSwipe)) => Some(HudAction::LeftSwipe),
            Some(NativeTargetMode::Support(NativeSupportMode::InAndOut)) => Some(HudAction::InAndOut),
            Some(NativeTargetMode::Support(NativeSupportMode::Guard)) => Some(HudAction::Guard),
            Some(NativeTargetMode::Support(NativeSupportMode::Patrol)) => Some(HudAction::Patrol),
            Some(NativeTargetMode::Support(NativeSupportMode::Mining)) => Some(HudAction::Mine),
            Some(NativeTargetMode::Support(NativeSupportMode::Heal)) => Some(HudAction::Heal),
            Some(NativeTargetMode::Support(NativeSupportMode::FullRetreat)) => Some(HudAction::Retreat),
            Some(NativeTargetMode::ManualFire) => Some(HudAction::PointFire),
            None => None,
        };
        // Clamp after all navigation inputs, numbered-squad focus, minimap
        // drags and window resizing. Never let a game-control input move
        // the graphical camera beyond the authored map.
        if self.renderer.camera.constrain_source_zoom(
            authored_zoom, height,
        ) {
            self.camera_zoom_clamps += 1;
        }
        if self.renderer.camera.constrain_to_map(self.bounds, width, height) {
            self.camera_boundary_clamps += 1;
        }
        // Sampling uses completed values from the preceding frames; never
        // read an uncommitted step or attribute a requested tick as done.
        self.performance.sample(
            now, self.rendered_frames, self.fixed_steps_executed,
            self.render_wall_seconds, self.window_update_wall_seconds,
            self.pacing_lag_clamps,
        );
        let render_started = Instant::now();
        let counts = self.renderer.render(&view, self.bounds);
        // This is the visible viewport, after camera clamp/zoom and before
        // any UI overlay. Combat audio never reads or changes world RNG.
        let (viewport_w, viewport_h) = self.renderer.size();
        let camera = self.renderer.camera;
        let half = Vec2::new(viewport_w as f32, viewport_h as f32)
            * (0.5 / camera.pixels_per_unit);
        self.audio.observe(&view, Aabb {
            min: camera.center - half, max: camera.center + half,
        });
        if accepts_orders {
            self.renderer.draw_tactical_feedback(self.input_feedback);
        }
        if self.performance.enabled {
            let label = if self.performance.sampled {
                format!(
                    "FPS {:.1} | Sim {:.1} SPS ({:.0}% realtime) | Render {:.1}ms | Present+pace {:.1}ms | Lag resets {}",
                    self.performance.fps, self.performance.sps,
                    self.performance.sps * fixed_dt.as_secs_f64() * 100.0,
                    self.performance.render_ms, self.performance.present_ms,
                    self.performance.lag_clamps,
                )
            } else { "Measuring native gameplay throughput...".to_owned() };
            self.renderer.draw_live_performance(&label);
        }
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
            "Bees native | World {} | Episode {} | Tick {} | {} ships, {} shots | {} squads selected | {}{} | {} | range/obstruction {} | Left select/drag, double-click type, Ctrl add, right move/attack (also on map) | C stop chase, V cease fire, A attack on sight, Ctrl+A all squads, K beacon, D detonate, Q charge, Y match, U unmatch, S targets, O lock, E chase, G guard, P patrol, M mine, H heal, R retreat, hold J manual fire, T point fire, X stop, B ranges, Backspace cancel | Space pause, N step, Tab world, 1-0 squads, PgUp/PgDn cycle, arrows pan, wheel zoom, F fit, I map, F7 mute, F8 metrics, F9 side, Esc exit",
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
        self.ship_sprite_draws = self.ship_sprite_draws.saturating_add(counts.ship_sprite_draws as u64);
        self.combat_impacts_observed = self.combat_impacts_observed
            .saturating_add(counts.combat_impacts_observed as u64);
        self.combat_impact_visual_draws = self.combat_impact_visual_draws
            .saturating_add(counts.combat_impact_visual_draws as u64);
        self.combat_impact_visuals_dropped = self.combat_impact_visuals_dropped
            .saturating_add(counts.combat_impact_visuals_dropped as u64);
        self.custom_ship_color_draws = self.custom_ship_color_draws
            .saturating_add(counts.custom_ship_color_draws as u64);
        self.ship_color_cache_misses = self.ship_color_cache_misses
            .saturating_add(counts.ship_color_cache_misses as u64);
        self.ship_color_cache_variants = counts.ship_color_cache_variants;
        self.sprite_raster_candidate_pixels = self.sprite_raster_candidate_pixels
            .saturating_add(counts.sprite_raster_candidate_pixels);
        self.ship_fallback_hulls = self.ship_fallback_hulls.saturating_add(counts.ship_fallback_hulls as u64);
        self.ship_art_types_loaded = counts.ship_art_types_loaded;
        self.asteroid_sprite_draws = self.asteroid_sprite_draws
            .saturating_add(counts.asteroid_sprite_draws as u64);
        self.asteroid_piece_sprite_draws = self.asteroid_piece_sprite_draws
            .saturating_add(counts.asteroid_piece_sprite_draws as u64);
        self.asteroid_fallback_hulls = self.asteroid_fallback_hulls
            .saturating_add(counts.asteroid_fallback_hulls as u64);
        self.asteroid_art_types_loaded = counts.asteroid_art_types_loaded;
        self.projectile_sprite_draws = self.projectile_sprite_draws
            .saturating_add(counts.projectile_sprite_draws as u64);
        self.projectile_explosion_sprite_draws = self.projectile_explosion_sprite_draws
            .saturating_add(counts.projectile_explosion_sprite_draws as u64);
        self.projectile_fallback_hulls = self.projectile_fallback_hulls
            .saturating_add(counts.projectile_fallback_hulls as u64);
        self.projectile_art_types_loaded = counts.projectile_art_types_loaded;
        self.culled_obstacle_draws = self.culled_obstacle_draws.saturating_add(counts.culled_obstacles as u64);
        self.culled_projectile_draws = self.culled_projectile_draws.saturating_add(counts.culled_projectiles as u64);
        self.culled_piece_draws = self.culled_piece_draws.saturating_add(counts.culled_pieces as u64);
        self.minimap_markers_drawn = self.minimap_markers_drawn.saturating_add(counts.minimap_ship_markers as u64);
        self.numbered_squad_rows_drawn = self.numbered_squad_rows_drawn.saturating_add(counts.numbered_squad_rows as u64);
        self.striker_bomb_indicators_drawn = self.striker_bomb_indicators_drawn
            .saturating_add(counts.striker_bomb_indicators as u64);
        self.selected_command_rows_drawn = self.selected_command_rows_drawn
            .saturating_add(counts.selected_command_rows as u64);
        self.command_target_markers_drawn = self.command_target_markers_drawn
            .saturating_add(counts.command_target_markers as u64);
        self.weapon_geometry_queries = self.weapon_geometry_queries.saturating_add(counts.range_queries as u64);
        self.weapon_range_candidates_culled = self.weapon_range_candidates_culled
            .saturating_add(counts.range_candidates_culled as u64);
        let window_update_started = Instant::now();
        self.window.update_with_buffer(self.renderer.pixels(), width, height)?;
        self.window_update_wall_seconds += window_update_started.elapsed().as_secs_f64();
        if !self.window.is_open() { return Ok(NativeClientAction::Quit); }
        if allows_input && held_fire.is_none() {
            let action = shooting_input.map(NativePlayerInput::Shooting)
                .or_else(|| lock_on_input.map(NativePlayerInput::LockOn))
                .or_else(|| chase_input.map(NativePlayerInput::Chase))
                .or_else(|| special_input.map(NativePlayerInput::Special))
                .or_else(|| match_speed_input.map(NativePlayerInput::MatchSpeed))
                .or_else(|| cease_fire_input.map(NativePlayerInput::CeaseFire))
                .or_else(|| manual_fire.map(NativePlayerInput::Fire))
                .or_else(|| support_command.map(NativePlayerInput::Support))
                .or_else(|| player_move.map(NativePlayerInput::Move));
            if let Some(action) = action { return Ok(NativeClientAction::Input { action, step: false }); }
        }
        let after_frame = Instant::now();
        // Render and fixed-tick simulation have independent cadences. Catch
        // up with no more than four authoritative ticks per drawn frame.
        // Cap wall-clock debt rather than entering an unbounded catch-up
        // spiral when rendering, inference or the OS stalls for a long time.
        const MAX_STEPS_PER_FRAME: u32 = 4;
        let max_lag = fixed_dt.saturating_mul(MAX_STEPS_PER_FRAME - 1);
        if !self.paused && after_frame.saturating_duration_since(self.next_step) > max_lag {
            self.next_step = after_frame.checked_sub(max_lag).unwrap_or(after_frame);
            self.pacing_lag_clamps += 1;
        }
        let due = if briefing { 0 } else if single_step { 1 }
            else if !self.paused && after_frame >= self.next_step {
                (1 + after_frame.duration_since(self.next_step).as_nanos()
                    / fixed_dt.as_nanos().max(1))
                    .min(u128::from(MAX_STEPS_PER_FRAME)) as u32
            } else { 0 };
        self.pending_steps = due;
        let step = due > 0;
        if let Some(action) = held_fire {
            let input = NativePlayerHeldFire { world_id: view.world_id, episode_revision: view.episode_revision, controller_id: 0, action };
            Ok(NativeClientAction::Input { action: NativePlayerInput::HeldFire(input), step })
        } else if step {
            Ok(NativeClientAction::Step)
        } else {
            Ok(NativeClientAction::Wait)
        }
    }
}
