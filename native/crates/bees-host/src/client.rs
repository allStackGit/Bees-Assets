//! Optional native window around the shared host's borrowed render view.
//! Camera/inspection stay local; typed player moves carry world/episode
//! identity to the shared host. Policy decisions keep their real model route.

use std::time::{Duration, Instant};
use bees_sim::collision::Aabb;
use bees_sim::simulation::WorldId;
use bees_hivemind::session::SquadLifetime;
use minifb::{Key, KeyRepeat, MouseButton, MouseMode, Window, WindowOptions};
use crate::presentation::{squad_name_key, HudAction, NativeCamera, NativeControls,
    NativeRenderWorldView, NativeSoftwareRenderer, NativeSupportMode, WorldMoveDrag};
use crate::game_audio::{NativeGameAudio, NativeAudioMeasurements};
use crate::unity_scene_snapshot::{NativeMusicLocation, NativeMapBackdrop};
use bees_gameplay::player_inputs::{NativeHeldFireAction, NativePlayerHeldFire, NativePlayerSquadMoves,
    NativePlayerSquadFire, NativePlayerSquadCeaseFire,
    NativePlayerSquadSpecial, NativePlayerShipSpecialKind,
    NativePlayerSquadMatchSpeed, NativePlayerSquadShooting,
    NativePlayerSquadLockOn, NativePlayerSquadChase,
    NativePlayerSquadSelection};
use crate::player_trace::NativePlayerInput;
use bees_sim::geometry::Vec2;

#[derive(Clone, Debug, PartialEq)]
pub enum NativeClientAction {
    Wait, Step, Quit, Restart, ChooseNewSquads, SwitchSides { from_side:i32 },
    WorldChanged, SideChanged,
    SaveLevel { name: String },
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
    map_backgrounds: Vec<Option<NativeMapBackdrop>>,
    bounds: Aabb,
    selected_world: usize,
    selection_owner: Option<(WorldId, u64)>,
    submitted_selection: Vec<SquadLifetime>,
    hotseat: Option<NativeHotseat>,
    paused: bool,
    controls: NativeControls,
    controls_open: bool,
    /// Unity GameMenus.ChangeGameSpeed cycles 1x, 1.5x, 2x.
    /// This is only fixed-tick wall pacing, never a physics dt change.
    game_speed: u8,
    show_controls_help: bool,
    /// Original in-battle menu; reuses help's frozen simulation clock.
    battle_menu_open: bool,
    battle_menu_press: Option<usize>,
    surrender_confirmation: Option<(WorldId, u64, i32)>,
    /// Read-only Fleet Codex modal. Reuses the existing help pause/input
    /// boundary rather than introducing an independent game clock.
    codex_open: bool,
    codex_selected: u8,
    /// Unity Ship.ShowShipStats appears only after a stationary one-second
    /// hover. The cursor query samples at a bounded UI cadence, never on a
    /// physics tick and never through a new mutable gameplay subsystem.
    hovered_ship: Option<i64>,
    hover_started: Instant,
    hover_sampled: Instant,
    pub ship_inspection_frames: u64,
    pub ship_inspection_queries: u64,
    // Window titles are OS messages, not part of a physical simulation
    // step. Refresh their optional diagnostics at human-readable cadence.
    last_title_update: Option<Instant>,
    performance: NativeLivePerformance,
    /// Authored camera default when the player has no saved override.
    source_edge_scrolling: bool,
    /// The original FreePlay/FishTank menu may disable display fog;
    /// campaign and Challenge visibility cannot be overridden.
    allow_freeplay_fog: bool,
    allow_freeplay_restart: bool,
    allow_freeplay_save: bool,
    allow_new_squads: bool,
    level_name_input: Option<String>,
    /// Source per-Level Map.MinZoom/MaxZoom. No gameplay-state copy.
    source_zoom_by_world: Vec<Option<(f32, f32)>>,
    next_step: Instant,
    /// Fixed ticks approved for the current client frame, not yet executed.
    pending_steps: u32,
    previous_frame: Instant,
    left_was_down: bool,
    right_was_down: bool,
    /// Presentation-only camera grab; no player input or physical tick.
    middle_drag_last: Option<(f32, f32)>,
    right_press: Option<WorldMoveDrag>,
    last_left_press: Option<Instant>,
    double_left_press: bool,
    input_feedback: &'static str,
    result_controls: Option<NativeResultControls>,
    /// Post-battle GameMenus.ShowLevelSummary, projected once from the
    /// same completed physical match accounting as the remote receipts.
    match_summary_visible: bool,
    match_summary: Option<(WorldId,u64,i32,Vec<String>)>,
    /// Already committed outcome rewards. These aren't inferred from
    /// in-game mining cargo or client-side asset counts.
    campaign_ending_awards: Option<(usize,u64)>,
    playback: bool,
    dialogue_sequence: Option<u32>,
    dialogue_line: usize,
    dialogue_pause_started: Option<(u32, usize, Instant)>,
    dialogue_reveal: Option<(u32, usize, Instant)>,
    dialogue_reveal_forced: bool,
    dialogue_visible: bool,
    held_fire_target: Option<Vec2>,
    /// User-pinned range display; the source combined gesture adds only a
    /// transient overlay and cannot alter this independent preference.
    range_overlay_pinned: bool,
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
    pub map_background_pixels: u64,
    pub map_background_cached_pixels: u64,
    pub map_ring_layer_draws: u64,
    pub map_fog_texture_pixels: u64,
    pub carrier_deck_overlay_draws: u64,
    pub barge_charge_effect_draws: u64,
    pub queen_part_sprite_draws: u64,
    pub queen_explosion_layer_draws: u64,
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
    pub minimap_terrain_markers: u64,
    pub minimap_briefing_mining_markers: u64,
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
    pub right_drag_move_orders_submitted: u64,
    pub tactical_button_clicks: u64,
    pub squad_tab_clicks: u64,
    pub shooting_priority_requests_submitted: u64,
    pub selected_command_rows_drawn: u64,
    pub command_target_markers_drawn: u64,
}

impl NativeClient {
    /// Shift and Ctrl both extend a selection in the native multiplayer
    /// window. Keep the same UI-only gesture for local play; this does not
    /// change authenticated squad authority or fixed-tick commands.
    fn selection_additive(&self) -> bool {
        self.window.is_key_down(Key::LeftCtrl) ||
        self.window.is_key_down(Key::RightCtrl) ||
        self.window.is_key_down(Key::LeftShift) ||
        self.window.is_key_down(Key::RightShift)
    }

    pub fn new(bounds: Aabb) -> Result<Self, Box<dyn std::error::Error>> {
        let renderer = NativeSoftwareRenderer::new(1100, 760, bounds)?;
        let mut window = Window::new("Bees native", 1100, 760,
            WindowOptions { resize: true, ..WindowOptions::default() })?;
        window.set_target_fps(60);
        let now = Instant::now();
        Ok(Self { window, renderer, audio: NativeGameAudio::new(),
            music_locations: Vec::new(), map_backgrounds: Vec::new(), bounds,
            selected_world: 0, selection_owner: None,
            submitted_selection: Vec::new(), hotseat: None,
            paused: false, game_speed: 0, show_controls_help: false,
            controls: NativeControls::load()?, controls_open: false,
            battle_menu_open: false, battle_menu_press: None,
            surrender_confirmation: None,
            codex_open: false,
            codex_selected: 18,
            hovered_ship: None, hover_started: now, hover_sampled: now,
            ship_inspection_frames: 0, ship_inspection_queries: 0,
            last_title_update: None,
            performance: NativeLivePerformance::new(now),
            source_edge_scrolling: false,
            allow_freeplay_fog: false,
            allow_freeplay_restart: false, allow_freeplay_save: false,
            allow_new_squads: false,
            level_name_input: None,
            source_zoom_by_world: Vec::new(),
            next_step: now, pending_steps: 0, previous_frame: now, left_was_down: false,
            right_was_down: false, middle_drag_last: None, right_press: None, last_left_press: None, double_left_press: false,
            input_feedback: "",
            result_controls: None,
            match_summary_visible: false, match_summary: None,
            campaign_ending_awards: None,
            playback: false,
            dialogue_sequence: None,
            dialogue_line: 0,
            dialogue_pause_started: None, dialogue_reveal: None,
            dialogue_reveal_forced: false, dialogue_visible: false,
            held_fire_target: None,
            range_overlay_pinned: false,
            selection_drag: None, minimap_dragging: false,
            hud_press: None, squad_tab_press: None, shooting_press: None,
            shooting_focus: 1, support_mode: None,
            rendered_frames: 0, hotseat_switches: 0,
            fixed_steps_executed: 0, multi_step_frames: 0,
            max_steps_per_frame: 0, pacing_lag_clamps: 0,
            rendered_pixels: 0, render_wall_seconds: 0.0,
            ui_wall_seconds: 0.0, window_update_wall_seconds: 0.0,
            culled_ship_draws: 0, ship_sprite_draws: 0,
            map_background_pixels: 0, map_background_cached_pixels: 0,
            map_ring_layer_draws: 0, map_fog_texture_pixels: 0,
            carrier_deck_overlay_draws: 0,
            barge_charge_effect_draws: 0,
            queen_part_sprite_draws: 0,
            queen_explosion_layer_draws: 0,
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
            minimap_markers_drawn: 0, minimap_terrain_markers: 0,
            minimap_briefing_mining_markers: 0,
            numbered_squad_rows_drawn: 0,
            striker_bomb_indicators_drawn: 0,
            minimap_camera_moves: 0, camera_pan_frames: 0,
            camera_wheel_pans: 0, camera_boundary_clamps: 0,
            camera_zoom_clamps: 0, numbered_shortcut_selections: 0,
            cycled_shortcut_selections: 0,
            minimap_move_orders_submitted: 0, right_drag_move_orders_submitted: 0,
            tactical_button_clicks: 0, squad_tab_clicks: 0,
            shooting_priority_requests_submitted: 0,
            selected_command_rows_drawn: 0, command_target_markers_drawn: 0 })
    }

    /// Imported once from the source scene, not inferred from a UI map.
    pub fn set_source_edge_scrolling(&mut self, enabled: Option<bool>) {
        if let Some(enabled) = enabled {
            self.source_edge_scrolling = enabled;
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

    /// Choose the actual map prefab, never infer its art from music.
    pub fn set_map_backgrounds(&mut self, backgrounds: Vec<Option<NativeMapBackdrop>>) {
        self.map_backgrounds = backgrounds;
        self.renderer.set_map_background(
            self.map_backgrounds.get(self.selected_world).copied().flatten(),
        );
    }

    pub fn audio_measurements(&self) -> NativeAudioMeasurements {
        self.audio.measurements()
    }

    /// Load user-local mixer preferences before attaching a Level soundtrack.
    /// A bad settings file never prevents the campaign from starting.
    pub fn set_audio_preferences(&mut self, path: std::path::PathBuf) {
        self.audio.load_preferences(path);
    }

    pub fn selected_world(&self) -> usize { self.selected_world }

    /// Used only with the source-authored two-local-player permission
    /// preflight. Remote play has its own authenticated peer side instead.
    pub fn enable_hotseat(&mut self, sides: [i32; 2], initial: i32) {
        if sides[0] != sides[1] && sides.contains(&initial) {
            self.hotseat = Some(NativeHotseat { sides, active: initial });
            self.renderer.reset_squad_tab_view();
        }
    }

    pub fn hotseat_side(&self) -> Option<i32> {
        self.hotseat.map(|hotseat| hotseat.active)
    }

    pub fn pending_steps(&self) -> u32 { self.pending_steps }

    /// Source GameMenus.ChangeGameSpeed: 1x, 1.5x, 2x.
    /// A new speed does not alter the authored CombatWorld fixed step,
    /// reward/simulation timers, recorded player inputs or the learner.
    fn wall_step_interval(&self, authored: Duration) -> Duration {
        match self.game_speed {
            1 => authored.mul_f64(2.0 / 3.0),
            2 => authored / 2,
            _ => authored,
        }
    }

    /// Commit only ticks actually executed by the common simulation. Model
    /// boundaries, source command pulses and trace checkpoints can shorten
    /// a frame's planned catch-up without losing the remaining clock debt.
    pub fn commit_steps(&mut self, executed: u32, fixed_dt: Duration) {
        debug_assert!(executed > 0 && executed <= self.pending_steps);
        if self.paused {
            // Explicit N stepping must not accumulate realtime clock debt.
            self.next_step = Instant::now();
        } else {
            self.next_step += self.wall_step_interval(fixed_dt)
                .saturating_mul(executed);
        }
        self.fixed_steps_executed += u64::from(executed);
        self.multi_step_frames += u64::from(executed > 1);
        self.max_steps_per_frame = self.max_steps_per_frame.max(executed);
        self.pending_steps = 0;
    }

    pub fn viewport_size(&self) -> (usize, usize) { self.renderer.size() }

    /// Import finalized original ending awards after the career transaction
    /// is durable, never before the completed Level's fleet/outcome commit.
    pub fn set_campaign_ending_awards(
        &mut self, new_ships: usize, minerals_received: u64,
    ) {
        self.campaign_ending_awards = Some((new_ships,minerals_received));
        self.match_summary = None;
    }

    pub fn report_move(&mut self, feedback: &'static str) { self.input_feedback = feedback; }

    pub fn report_level_save(&mut self, saved: bool) {
        use crate::game_audio::NativeUiFeedback;
        let (message, cue) = if saved {
            ("Named Level saved to FreePlay library", NativeUiFeedback::Saved)
        } else {
            ("FreePlay level could not be saved", NativeUiFeedback::Error)
        };
        self.report_move(message);
        self.audio.play_ui_feedback(cue);
    }

    pub fn show_result(&mut self, controls: NativeResultControls) {
        self.result_controls = Some(controls);
        self.match_summary_visible = true;
        self.match_summary = None;
        // The original ShowLevelEndedDialogue resets Time.timeScale to 1.
        // Never carry accelerated playback into an unrelated next battle.
        self.game_speed = 0;
    }
    pub fn set_playback(&mut self) { self.playback = true; }

    pub fn set_freeplay_fog_allowed(&mut self,allowed:bool) {
        self.allow_freeplay_fog=allowed;
    }

    pub fn set_freeplay_restart_allowed(&mut self,allowed:bool) {
        self.allow_freeplay_restart=allowed;
    }

    /// Only the host can commit a named Level to the caller's user library.
    /// The local client edits a name but never saves a World or model.
    pub fn set_freeplay_save_allowed(&mut self,allowed:bool) {
        self.allow_freeplay_save=allowed;
    }

    /// Only the Fleet Builder's own saved games can return directly to
    /// Squad Maker. Its host retains the exclusive save writer throughout
    /// both the old battle's cleanup and the next custom Level authoring.
    pub fn set_choose_new_squads_allowed(&mut self,allowed:bool) {
        self.allow_new_squads=allowed;
    }

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

    /// Collect the authoritative transient sound cues during every
    /// completed simulation tick, including catch-up ticks not rendered.
    /// Never steps physics or queries objects outside the visual viewport.
    pub fn observe_playable_tick(
        &mut self, world_id: WorldId, episode_revision: u64, tick: u64,
        combat: &bees_sim::combat_world::CombatWorld,
        roster: &bees_gameplay::squads::NativeSquadRoster,
        player_authority: &dyn bees_gameplay::player_inputs::NativePlayerAuthority,
    ) {
        // Capture visual effects before the next step replaces the
        // authoritative event slice, even if the window skips this frame.
        self.renderer.capture_unrendered_combat_events(
            world_id, episode_revision, tick, combat, roster,
            player_authority,
        );
        let (width, height) = self.renderer.size();
        let camera = self.renderer.camera;
        let half = Vec2::new(width as f32, height as f32) *
            (0.5 / camera.pixels_per_unit);
        self.audio.observe_combat(world_id, episode_revision, tick, combat,
            Aabb { min: camera.center - half, max: camera.center + half });
    }

    pub fn poll(
        &mut self, view: NativeRenderWorldView<'_>, bounds: Aabb, world_count: usize, fixed_dt: Duration,
    ) -> Result<NativeClientAction, Box<dyn std::error::Error>> {
        self.pending_steps = 0;
        if !self.window.is_open() {
            return Ok(NativeClientAction::Quit);
        }
        let (window_width, window_height) = self.window.get_size();
        let drawable = window_width > 0 && window_height > 0;
        let resized = drawable && self.renderer.resize(window_width, window_height)?;
        if resized || !drawable {
            // Screen-space gestures belong to the layout on mouse-down.
            // Consume buttons held through a resize or minimization so the
            // next release cannot activate a moved control or battlefield.
            self.left_was_down = self.window.get_mouse_down(MouseButton::Left);
            self.right_was_down = self.window.get_mouse_down(MouseButton::Right);
            self.battle_menu_press = None;
            self.selection_drag = None;
            self.renderer.selection_box = None;
            self.middle_drag_last = None;
            self.minimap_dragging = false;
            self.hud_press = None;
            self.squad_tab_press = None;
            self.shooting_press = None;
            self.renderer.show_shooting_menu = false;
            self.right_press = None;
            self.last_left_press = None;
            self.double_left_press = false;
            self.viewport_resizes += u64::from(resized);
        }
        if !drawable {
            // A minimized window has no drawable surface.
            self.window.update();
            self.previous_frame = Instant::now();
            return Ok(NativeClientAction::Wait);
        }
        if let Some((world, revision, side)) = self.surrender_confirmation {
            if (world, revision) != (view.world_id, view.episode_revision) ||
                view.player_authority.player_viewer_side() != Some(side) ||
                !view.can_surrender(side) {
                self.surrender_confirmation = None;
            } else {
                self.renderer.render(&view, bounds);
                self.renderer.draw_surrender_confirmation();
                self.window.update_with_buffer(self.renderer.pixels(), window_width, window_height)?;
                self.next_step = Instant::now();
                self.previous_frame = self.next_step;
                if self.window.is_key_pressed(Key::Escape, KeyRepeat::No) ||
                    self.window.is_key_pressed(Key::N, KeyRepeat::No) {
                    self.surrender_confirmation = None;
                } else if self.window.is_key_pressed(Key::Y, KeyRepeat::No) {
                    self.surrender_confirmation = None;
                    self.paused = false;
                    self.pending_steps = 1;
                    return Ok(NativeClientAction::Input {
                        action: NativePlayerInput::Surrender {
                            world_id: world, episode_revision: revision, side,
                        },
                        step: true,
                    });
                }
                return Ok(NativeClientAction::Wait);
            }
        }
        if let Some(name)=self.level_name_input.as_mut() {
            for key in self.window.get_keys_pressed(KeyRepeat::No) {
                match key {
                    Key::Escape=>{
                        self.level_name_input=None;
                        self.show_controls_help=false;
                        self.next_step=Instant::now();
                        return Ok(NativeClientAction::Wait);
                    },
                    Key::Enter=>{
                        let title=name.trim().to_owned();
                        if !title.is_empty() && title.len()<=80 {
                            self.level_name_input=None;
                            self.show_controls_help=false;
                            self.next_step=Instant::now();
                            return Ok(NativeClientAction::SaveLevel {name:title});
                        }
                    },
                    Key::Backspace=>{name.pop();},
                    key=>{
                        let shift=self.window.is_key_down(Key::LeftShift) ||
                            self.window.is_key_down(Key::RightShift);
                        if let Some(ch)=squad_name_key(key,shift) {
                            if name.len()<80 {name.push(ch);}
                        }
                    }
                }
            }
            self.renderer.render(&view,bounds);
            self.renderer.draw_save_level_name(name);
            self.window.update_with_buffer(self.renderer.pixels(), window_width, window_height)?;
            self.audio.set_paused(true);
            self.next_step=Instant::now();
            self.previous_frame=self.next_step;
            return Ok(NativeClientAction::Wait);
        }
        // Escape returns to the game menu rather than silently abandoning
        // an active campaign. Keep playback/terminal close semantics intact.
        if self.window.is_key_pressed(Key::Escape, KeyRepeat::No) {
            if self.show_controls_help {
                self.show_controls_help = false;
                self.codex_open = false;
                self.battle_menu_open = false;
                self.battle_menu_press = None;
                self.next_step = Instant::now();
            } else if self.renderer.show_shooting_menu {
                self.renderer.show_shooting_menu = false;
                self.shooting_press = None;
            } else if self.playback || view.terminal_result.is_some() {
                return Ok(NativeClientAction::Quit);
            } else {
                self.battle_menu_open = true;
                self.show_controls_help = true;
                self.next_step = Instant::now();
                self.selection_drag = None;
                self.renderer.selection_box = None;
                self.hud_press = None;
                self.squad_tab_press = None;
                self.minimap_dragging = false;
                self.right_press = None;
                self.renderer.show_shooting_menu = false;
                self.shooting_press = None;
                // A held manual-fire gesture belongs to the game host;
                // never freeze the window while leaving those weapons aimed.
                if self.held_fire_target.take().is_some() {
                    return Ok(NativeClientAction::Input {
                        action: NativePlayerInput::HeldFire(NativePlayerHeldFire {
                            world_id: view.world_id,
                            episode_revision: view.episode_revision,
                            controller_id: 0,
                            action: NativeHeldFireAction::End,
                        }),
                        step: false,
                    });
                }
            }
        }
        // The original Settings menu is a client-only modal. A rebind
        // modifies the shared local configuration, never a gameplay input.
        if self.result_controls.is_none() && self.held_fire_target.is_none() &&
            self.window.is_key_pressed(Key::F5,KeyRepeat::No)
        {
            self.controls_open=!self.controls_open;
            self.next_step=Instant::now();
            self.shooting_press=None;
            self.right_press=None;
            self.hud_press=None;
            self.squad_tab_press=None;
            self.selection_drag=None;
            self.renderer.selection_box=None;
            self.renderer.show_shooting_menu=false;
        }
        if self.controls_open {
            self.audio.set_paused(true);
            self.controls.edit(&self.window);
            self.renderer.render(&view,bounds);
            self.renderer.draw_control_settings(&self.controls);
            self.window.update_with_buffer(self.renderer.pixels(), window_width, window_height)?;
            self.next_step=Instant::now();
            self.previous_frame=self.next_step;
            return Ok(NativeClientAction::Wait);
        }
        // The menu owns its input surface. An action is dispatched only
        // after a direct key press or mouse release over the pressed row.
        if self.battle_menu_open {
            let left = self.window.get_mouse_down(MouseButton::Left);
            let hover = self.window.get_mouse_pos(MouseMode::Discard)
                .and_then(|(x, y)|
                    self.renderer.battle_menu_at(
                        x,y,self.allow_freeplay_restart,self.allow_freeplay_save,
                        self.allow_new_squads,
                        self.allow_new_squads &&
                            view.player_authority.player_viewer_side().is_some()));
            if left && !self.left_was_down {
                self.battle_menu_press = hover;
            }
            let click = if !left && self.left_was_down {
                self.battle_menu_press.take().filter(|&pressed| Some(pressed) == hover)
            } else { None };
            self.left_was_down = left;
            let choice = if self.window.is_key_pressed(Key::R, KeyRepeat::No) {
                Some(0)
            } else if self.window.is_key_pressed(Key::H, KeyRepeat::No) ||
                self.window.is_key_pressed(Key::F1, KeyRepeat::No) { Some(1) }
            else if self.window.is_key_pressed(Key::C, KeyRepeat::No) ||
                self.window.is_key_pressed(Key::F2, KeyRepeat::No) { Some(2) }
            else if self.window.is_key_pressed(Key::S, KeyRepeat::No) { Some(3) }
            else if self.window.is_key_pressed(Key::Q, KeyRepeat::No) { Some(4) }
            else if self.allow_freeplay_restart &&
                self.window.is_key_pressed(Key::N,KeyRepeat::No) {Some(5)}
            else if self.allow_freeplay_save &&
                self.window.is_key_pressed(Key::L,KeyRepeat::No) {Some(6)}
            else if self.allow_new_squads &&
                self.window.is_key_pressed(Key::B,KeyRepeat::No) {Some(7)}
            else if self.allow_new_squads &&
                view.player_authority.player_viewer_side().is_some() &&
                self.window.is_key_pressed(Key::Z,KeyRepeat::No) {Some(8)}
            else { click };
            match choice {
                Some(0 | 1 | 2) => {
                    self.battle_menu_open = false;
                    self.battle_menu_press = None;
                    self.show_controls_help = choice != Some(0);
                    self.codex_open = choice == Some(2);
                    self.next_step = Instant::now();
                    return Ok(NativeClientAction::Wait);
                }
                Some(3) => {
                    if let Some(side) = view.player_authority.player_viewer_side()
                        .filter(|&side| view.can_surrender(side)) {
                        self.battle_menu_open = false;
                        self.battle_menu_press = None;
                        self.show_controls_help = false;
                        self.surrender_confirmation = Some((
                            view.world_id, view.episode_revision, side,
                        ));
                        self.next_step = Instant::now();
                        return Ok(NativeClientAction::Wait);
                    }
                }
                Some(4) => return Ok(NativeClientAction::Quit),
                Some(5) if self.allow_freeplay_restart => {
                    // The host owns cleanup, immutable run receipts and a
                    // new original Level. Never reset an existing World
                    // directly from this presentation-only menu.
                    self.battle_menu_open=false;
                    self.battle_menu_press=None;
                    return Ok(NativeClientAction::Restart);
                },
                Some(6) if self.allow_freeplay_save => {
                    self.battle_menu_open=false;
                    self.battle_menu_press=None;
                    self.show_controls_help=true;
                    self.level_name_input=Some(String::new());
                    self.next_step=Instant::now();
                    return Ok(NativeClientAction::Wait);
                },
                Some(7) if self.allow_new_squads => {
                    // The ordinary host finalizes this physical battle
                    // before the source Squad Maker is opened again.
                    self.battle_menu_open=false;
                    self.battle_menu_press=None;
                    return Ok(NativeClientAction::ChooseNewSquads);
                },
                Some(8) if self.allow_new_squads => {
                    if let Some(side)=view.player_authority.player_viewer_side()
                        .filter(|&side|matches!(side,1|2))
                    {
                        self.battle_menu_open=false;
                        self.battle_menu_press=None;
                        return Ok(NativeClientAction::SwitchSides {from_side:side});
                    }
                },
                _ => {},
            }
        }
        if self.result_controls.is_some() && view.terminal_result.is_some() &&
            self.window.is_key_pressed(Key::F5,KeyRepeat::No)
        {
            self.match_summary_visible = !self.match_summary_visible;
        }
        if self.window.is_key_pressed(Key::F1, KeyRepeat::No) &&
            self.held_fire_target.is_none() && !self.battle_menu_open
        {
            self.codex_open = false;
            self.show_controls_help = !self.show_controls_help;
            // Help freezes the local simulation clock. On close, restart
            // the wall-clock pacing baseline rather than counting the time
            // spent reading help as a backlog of physics ticks.
            self.next_step = Instant::now();
            if self.show_controls_help {
                self.renderer.show_shooting_menu = false;
                self.shooting_press = None;
                self.selection_drag = None;
                self.renderer.selection_box = None;
                self.hud_press = None;
                self.squad_tab_press = None;
                self.minimap_dragging = false;
                self.right_press = None;
            }
        }
        let now = Instant::now();
        let elapsed = now.duration_since(self.previous_frame).as_secs_f32().min(0.1);
        self.previous_frame = now;
        let owner = (view.world_id, view.episode_revision);
        if self.selection_owner != Some(owner) {
            self.renderer.selected_ship = None;
            self.hovered_ship = None;
            self.hover_started = now;
            self.hover_sampled = now;
            self.selection_drag = None;
            self.minimap_dragging = false;
            self.hud_press = None;
            self.squad_tab_press = None;
            self.shooting_press = None;
            self.renderer.show_shooting_menu = false;
            self.renderer.reset_squad_tab_view();
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
            // A different World or authentic player-side authority must
            // not inherit the previous view's discovered enemy classes.
            self.codex_open = false;
            self.show_controls_help = false;
            self.submitted_selection.clear();
            self.dialogue_sequence = None;
            self.dialogue_pause_started = None;
            self.held_fire_target = None;
        }
        self.renderer.selection.synchronize(&view);
        if self.window.is_key_pressed(Key::F2, KeyRepeat::No) &&
            self.held_fire_target.is_none()
        {
            self.codex_open = !self.codex_open;
            self.show_controls_help = self.codex_open;
            self.next_step = Instant::now();
            if self.codex_open {
                let known = self.renderer.codex_seen_types();
                if !known[usize::from(self.codex_selected)] {
                    self.codex_selected = known.iter()
                        .position(|seen| *seen).unwrap_or(18) as u8;
                }
                self.renderer.show_shooting_menu = false;
                self.shooting_press = None;
                self.selection_drag = None;
                self.renderer.selection_box = None;
                self.hud_press = None;
                self.squad_tab_press = None;
                self.minimap_dragging = false;
                self.right_press = None;
            }
        }
        if self.codex_open {
            let known = self.renderer.codex_seen_types().iter().enumerate()
                .filter_map(|(kind, known)| (*known &&
                    kind != usize::from(bees_sim::ship_assets::HUMAN_TARGET_TYPE))
                    .then_some(kind as u8))
                .collect::<Vec<_>>();
            if !known.is_empty() {
                let focus = known.iter().position(|&kind| kind == self.codex_selected)
                    .unwrap_or(0);
                if self.window.is_key_pressed(Key::Up, KeyRepeat::No) {
                    self.codex_selected = known[(focus + known.len() - 1) % known.len()];
                } else if self.window.is_key_pressed(Key::Down, KeyRepeat::No) {
                    self.codex_selected = known[(focus + 1) % known.len()];
                }
            }
        }
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
                self.dialogue_pause_started = None;
            }
            let count = crate::presentation::campaign_dialogue::line_count(cue);
            if self.dialogue_reveal.as_ref().is_none_or(|(prior, index, _)|
                *prior != sequence || *index != self.dialogue_line)
            {
                self.dialogue_reveal = Some((sequence, self.dialogue_line, now));
                self.dialogue_reveal_forced = false;
            }
            if !self.show_controls_help && self.window.is_key_pressed(Key::K, KeyRepeat::No) {
                self.dialogue_line = count;
            } else if !self.show_controls_help && self.dialogue_line < count &&
                self.window.is_key_pressed(Key::L, KeyRepeat::No) {
                let incomplete = !self.dialogue_reveal_forced &&
                    crate::presentation::campaign_dialogue::visible_line(
                        cue, self.dialogue_line,
                    ).is_some_and(|(_, message)| {
                        let elapsed = self.dialogue_reveal.map_or(0.0,
                            |(_, _, start)| now.saturating_duration_since(start).as_secs_f32());
                        elapsed * 50.0 < message.chars().count() as f32
                    });
                if incomplete {
                    self.dialogue_reveal_forced = true;
                } else {
                    self.dialogue_line += 1;
                }
            }
            if let Some(seconds) =
                crate::presentation::campaign_dialogue::pause_seconds(cue,self.dialogue_line)
            {
                match self.dialogue_pause_started {
                    Some((revision, index, started))
                        if revision == sequence && index == self.dialogue_line => {
                        if now.duration_since(started).as_secs_f32() >= seconds {
                            self.dialogue_line += 1;
                            self.dialogue_pause_started = None;
                        }
                    }
                    _ => self.dialogue_pause_started =
                        Some((sequence,self.dialogue_line,now)),
                }
            } else {
                self.dialogue_pause_started = None;
            }
            // Source DialogueManager plays the intercom once, on the
            // first visible line of each newly presented section. A
            // skipped section makes no sound, and advancing within the
            // same section never repeats the cue.
            if first_presentation && self.dialogue_line < count {
                self.audio.campaign_dialogue_presented();
            }
            crate::presentation::campaign_dialogue::visible_line(cue,self.dialogue_line)
                .map(|(speaker, line)| (cue, speaker, line))
        });
        if let (Some((sequence, _)), Some(_)) = (
            view.campaign_dialogue.filter(|_| dialogue_for_viewer), dialogue,
        ) {
            if self.dialogue_reveal.as_ref().is_none_or(|(prior, index, _)|
                *prior != sequence || *index != self.dialogue_line)
            {
                self.dialogue_reveal = Some((sequence, self.dialogue_line, now));
                self.dialogue_reveal_forced = false;
            }
        } else {
            self.dialogue_reveal = None;
            self.dialogue_reveal_forced = false;
        }
        self.dialogue_visible = dialogue.is_some();
        self.audio.set_level_intro_ambience(
            dialogue.is_some_and(|(cue, _, _)| cue.is_level_intro()),
        );
        // Hiding the owner's story from the opposite hot-seat must not
        // advance the authoritative mission before the owner has read it.
        let briefing = if dialogue_for_viewer {
            dialogue.is_some_and(|(cue, _, _)| cue.is_briefing())
        } else {
            view.terminal_result.is_none() &&
                view.campaign_dialogue.is_some_and(|(_, cue)| cue.is_briefing())
        };
        if view.terminal_result.is_some() { self.held_fire_target = None; }
        if !self.playback && !self.show_controls_help && !briefing &&
            self.held_fire_target.is_none() && view.terminal_result.is_none() &&
            self.window.is_active() && self.window.is_key_pressed(Key::F3, KeyRepeat::No)
        {
            if let Some(side) = view.player_authority.player_viewer_side() {
                if view.combat.retreat_activation_available(side) {
                    return Ok(NativeClientAction::Input {
                        action: NativePlayerInput::ActivateRetreat {
                            world_id: view.world_id,
                            episode_revision: view.episode_revision,
                            side,
                        },
                        step: false,
                    });
                }
            }
        }
        if !self.playback && !self.show_controls_help && !briefing &&
            self.held_fire_target.is_none() && view.terminal_result.is_none() &&
            self.window.is_active() && self.window.is_key_pressed(Key::F4, KeyRepeat::No) {
            if let Some(side) = view.player_authority.player_viewer_side() {
                if view.can_surrender(side) {
                    self.surrender_confirmation = Some((view.world_id, view.episode_revision, side));
                    self.selection_drag = None;
                    self.renderer.selection_box = None;
                    self.right_press = None;
                    self.hud_press = None;
                    self.squad_tab_press = None;
                    self.shooting_press = None;
                    self.renderer.show_shooting_menu = false;
                    return Ok(NativeClientAction::Wait);
                }
            }
        }
        self.bounds = bounds;
        // Match the original 1x -> 1.5x -> 2x -> 1x button. F12 is
        // reserved for native local speed control; it cannot issue a
        // squad/player input and is inactive during modal overlays.
        if !self.playback && self.window.is_active() && !self.show_controls_help &&
            self.window.is_key_pressed(Key::F12, KeyRepeat::No) {
            self.game_speed = (self.game_speed + 1) % 3;
            self.next_step = now;
            self.input_feedback = match self.game_speed {
                1 => "Game speed 1.5x",
                2 => "Game speed 2x",
                _ => "Game speed 1x",
            };
        }
        if !self.show_controls_help &&
            self.window.is_key_pressed(Key::Space, KeyRepeat::No)
        {
            self.paused = !self.paused;
            self.next_step = now;
        }
        self.audio.set_paused(self.paused || self.show_controls_help);
        if self.window.is_active() && self.window.is_key_pressed(Key::F7, KeyRepeat::No) {
            let shifted=self.window.is_key_down(Key::LeftShift) ||
                self.window.is_key_down(Key::RightShift);
            if shifted {
                if self.allow_freeplay_fog && !self.playback {
                    self.input_feedback=if self.renderer.toggle_freeplay_fog() {
                        "Original free-play fog enabled"
                    } else { "Original free-play fog disabled" };
                } else {
                    self.input_feedback="Fog control is unavailable in this game mode";
                }
            } else {
                self.input_feedback = if self.audio.toggle_muted() {
                    "Audio muted"
                } else { "Audio enabled" };
            }
        }
        // Native player audio settings apply to all current and future
        // soundtrack/effect voices, including source WarpGate loops. Shift
        // adjusts the music channel; unmodified F10/F11 adjust effects.
        // No audio mixer work is added to the physical game or PPO loop.
        if self.window.is_active() {
            let decrease = self.window.is_key_pressed(Key::F10, KeyRepeat::No);
            let increase = self.window.is_key_pressed(Key::F11, KeyRepeat::No);
            if decrease || increase {
                let music = self.window.is_key_down(Key::LeftShift) ||
                    self.window.is_key_down(Key::RightShift);
                let delta = if increase { 0.1 } else { -0.1 };
                self.audio.adjust_levels(music, delta);
                self.input_feedback = match (music, increase) {
                    (true, true) => "Music louder (Shift+F10/F11)",
                    (true, false) => "Music quieter (Shift+F10/F11)",
                    (false, true) => "Effects louder (F10/F11)",
                    (false, false) => "Effects quieter (F10/F11)",
                };
            }
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
        if !self.show_controls_help && self.window.is_active() &&
            self.window.is_key_pressed(Key::F9, KeyRepeat::No) &&
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
                self.renderer.reset_squad_tab_view();
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
                self.middle_drag_last = None;
                self.input_feedback = if hotseat.active == hotseat.sides[0] {
                    "Switched to Bees commander"
                } else { "Switched to Humans commander" };
                self.window.update();
                self.next_step = Instant::now();
                return Ok(NativeClientAction::SideChanged);
            }
        }
        if self.controls.pressed(&self.window,"Show Ranges",KeyRepeat::No) &&
            !self.controls.down(&self.window,"Manual Fire") {
            self.range_overlay_pinned = !self.range_overlay_pinned;
        }
        self.renderer.show_weapon_ranges = self.range_overlay_pinned ||
            (self.window.is_active() && !self.show_controls_help &&
             !self.playback && !briefing &&
             self.controls.combined_range_fire_down(&self.window));
        if self.controls.pressed(&self.window,"Mini Map",KeyRepeat::No) {
            self.renderer.show_minimap = !self.renderer.show_minimap;
            if !self.renderer.show_minimap { self.minimap_dragging = false; }
        }
        let single_step = self.paused && !self.show_controls_help &&
            !self.window.is_key_down(Key::LeftShift) &&
            !self.window.is_key_down(Key::RightShift) &&
            self.window.is_key_pressed(Key::N, KeyRepeat::No);
        if !self.show_controls_help && self.window.is_key_pressed(Key::Tab, KeyRepeat::No) &&
            world_count > 1 && self.held_fire_target.is_none() {
            self.selected_world = (self.selected_world + 1) % world_count;
            self.renderer.set_map_background(
                self.map_backgrounds.get(self.selected_world).copied().flatten(),
            );
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
            self.middle_drag_last = None;
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
        if self.controls.pressed(&self.window,"Fit Camera",KeyRepeat::No) {
            self.renderer.camera = NativeCamera::fit(self.bounds, width, height);
        }
        if self.window.is_active() && self.window.is_key_pressed(Key::F6, KeyRepeat::No) {
            self.input_feedback = self.controls.toggle_edge_scrolling(self.source_edge_scrolling);
        }
        let speed = 500.0 * elapsed / self.renderer.camera.pixels_per_unit;
        let mut pan_x = 0.0;
        let mut pan_y = 0.0;
        if !self.show_controls_help {
            if self.window.is_key_down(Key::Left) { pan_x -= speed; }
            if self.window.is_key_down(Key::Right) { pan_x += speed; }
            if self.window.is_key_down(Key::Up) { pan_y += speed; }
            if self.window.is_key_down(Key::Down) { pan_y -= speed; }
        }
        let mouse = self.window.get_mouse_pos(MouseMode::Discard);
        let camera_grab = self.window.is_active() && !self.show_controls_help &&
            !self.controls_open && !self.battle_menu_open && !self.codex_open &&
            !self.renderer.show_shooting_menu &&
            self.window.get_mouse_down(MouseButton::Middle);
        if camera_grab {
            if let (Some((x, y)), Some((old_x, old_y))) =
                (mouse, self.middle_drag_last)
            {
                // Camera-only movement at the current display zoom. Match
                // the multiplayer window's grab direction and distance.
                pan_x -= (x - old_x) / self.renderer.camera.pixels_per_unit;
                pan_y += (y - old_y) / self.renderer.camera.pixels_per_unit;
            }
            self.middle_drag_last = mouse;
        } else {
            self.middle_drag_last = None;
        }
        if self.controls.edge_scrolling(self.source_edge_scrolling) && self.window.is_active() &&
            !camera_grab && !self.renderer.show_shooting_menu {
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
            // Scroll the same live squad tabs used by pointer selection.
            // Do not change the physical camera or carry a pressed squad
            // across a viewport change.
            let roster_scrolled = !self.show_controls_help &&
                !self.renderer.show_shooting_menu &&
                mouse.is_some_and(|(x, y)|
                    self.renderer.scroll_squad_tabs(&view, x, y, vertical));
            if roster_scrolled {
                self.squad_tab_press = None;
            } else {
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
        }
        let allows_input = !self.show_controls_help && !briefing &&
            !self.playback && view.terminal_result.is_none();
        // Stop inspecting when a UI modal, drag, minimap or command gesture
        // owns the pointer. The source popup is purely observational, so
        // neither pause nor input admission depends on its dwell timer.
        let inspecting = self.window.is_active() && allows_input &&
            !self.renderer.show_shooting_menu && !self.window.get_mouse_down(MouseButton::Left) &&
            !self.window.get_mouse_down(MouseButton::Right) &&
            self.held_fire_target.is_none() && self.selection_drag.is_none();
        if !inspecting {
            self.hovered_ship = None;
            self.hover_started = now;
        } else if now.duration_since(self.hover_sampled) >= Duration::from_millis(100) {
            self.hover_sampled = now;
            let picked = mouse.and_then(|(x, y)| {
                if self.renderer.minimap_world_at(bounds, x, y).is_some() ||
                    self.renderer.tactical_action_at(x, y).is_some() ||
                    self.renderer.squad_tab_at(&view, x, y).is_some() {
                    None
                } else {
                    self.ship_inspection_queries += 1;
                    self.renderer.pick_ship(&view, x, y)
                }
            });
            if picked != self.hovered_ship {
                self.hovered_ship = picked;
                self.hover_started = now;
            }
        }
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
            (self.controls.pressed(&self.window,"Shooting Priority",KeyRepeat::No) ||
             self.window.is_key_pressed(Key::Backspace, KeyRepeat::No)) {
            self.renderer.show_shooting_menu = false;
            self.shooting_press = None;
        }
        // Unity UserSettingsData's Shift+A..16 direct priorities are
        // separate from the 40-way picker. They submit the exact same
        // authoritative shooting action, with no client-owned aiming.
        let direct_shooting = if !menu_was_open && allows_input &&
            pointer_active && self.held_fire_target.is_none() &&
            !self.renderer.selection.squads().is_empty()
        {
            self.controls.shooting_pressed(&self.window)
        } else { None };
        let shooting_input = clicked_shooting.or(direct_shooting).map(|strategy_id| {
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
            self.controls.pressed(&self.window,"Shooting Priority",KeyRepeat::No)
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
            let additive = self.selection_additive();
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
                self.renderer.reveal_squad_tab(&view, owner);
                self.focus_selected_squad(&view, owner);
            }
        }
        let stop_fire = clicked_hud == Some(HudAction::StopFire) ||
            self.controls.pressed(&self.window,"Stop Fire",KeyRepeat::No);
        let held_fire = if accepts_orders {
            let cursor = mouse.map(|(x, y)| self.renderer.camera.screen_to_world(x, y, width, height));
            if let Some(previous) = self.held_fire_target {
                if !pointer_active ||
                    !self.controls.manual_fire_down(&self.window) ||
                    stop_fire {
                    self.held_fire_target = None;
                    Some(NativeHeldFireAction::End)
                } else if let Some(target) = cursor.filter(|point| *point != previous) {
                    self.held_fire_target = Some(target);
                    Some(NativeHeldFireAction::Aim { target })
                } else { None }
            } else if pointer_active &&
                self.controls.manual_fire_started(&self.window) &&
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
            for (key_action, button, mode, prompt) in [
                (None, HudAction::Attack, NativeTargetMode::Support(NativeSupportMode::Attack), "Attack: click an enemy ship"),
                (None, HudAction::Circle, NativeTargetMode::Support(NativeSupportMode::Circle), "Circle: click an enemy squad"),
                (None, HudAction::RightSwipe, NativeTargetMode::Support(NativeSupportMode::RightSwipe), "Right Swipe: click an enemy squad"),
                (None, HudAction::LeftSwipe, NativeTargetMode::Support(NativeSupportMode::LeftSwipe), "Left Swipe: click an enemy squad"),
                (None, HudAction::InAndOut, NativeTargetMode::Support(NativeSupportMode::InAndOut), "In and Out: click an enemy squad"),
                (Some("Guard"), HudAction::Guard, NativeTargetMode::Support(NativeSupportMode::Guard), "Guard: click a friendly ship"),
                (Some("Patrol"), HudAction::Patrol, NativeTargetMode::Support(NativeSupportMode::Patrol), "Patrol: drag the patrol area"),
                (Some("Mine"), HudAction::Mine, NativeTargetMode::Support(NativeSupportMode::Mining), "Mine: click a mining asteroid"),
                (Some("Heal"), HudAction::Heal, NativeTargetMode::Support(NativeSupportMode::Heal), "Heal: click a friendly Beehive"),
                (Some("Retreat"), HudAction::Retreat, NativeTargetMode::Support(NativeSupportMode::FullRetreat), "Retreat: click a friendly WarpGate"),
                (Some("Point Fire"), HudAction::PointFire, NativeTargetMode::ManualFire, "Point fire: click a point; X stops selected weapons"),
            ] {
                // Source Shift tactical aliases and direct shooting keys
                // must never issue *both* a priority and a target order.
                let triggered = clicked_hud == Some(button) ||
                    key_action.is_some_and(|action|
                        match action {
                            "Guard" | "Patrol" =>
                                self.controls.command_pressed(
                                    &self.window,action,KeyRepeat::No),
                            _ => self.controls.pressed(
                                &self.window,action,KeyRepeat::No),
                        });
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
        if !pointer_active || !accepts_orders {
            self.right_press = None;
        } else if right_pressed {
            self.right_press = mouse
                .filter(|&(x, y)| !cancel_support &&
                    !self.renderer.selection.squads().is_empty() &&
                    self.renderer.minimap_world_at(bounds, x, y).is_none())
                .map(|(x, y)| WorldMoveDrag::new(now,
                    self.renderer.camera.screen_to_world(x, y, width, height)));
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
             self.controls.command_pressed(&self.window,"Cease Fire",KeyRepeat::No)) {
            Some(true)
        } else if accepts_orders &&
            (clicked_hud == Some(HudAction::AttackOnSight) ||
             (!self.window.is_key_down(Key::LeftCtrl) &&
              !self.window.is_key_down(Key::RightCtrl) &&
              self.controls.command_pressed(&self.window,"Attack on Sight",KeyRepeat::No))) {
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
             self.controls.command_pressed(&self.window,"Beacon",KeyRepeat::No)) {
            Some(NativePlayerShipSpecialKind::DropBeacon)
        } else if accepts_orders && pointer_active &&
            self.held_fire_target.is_none() &&
            (clicked_hud == Some(HudAction::Detonate) ||
             self.controls.command_pressed(&self.window,"Detonate",KeyRepeat::No)) {
            Some(NativePlayerShipSpecialKind::Detonate)
        } else if accepts_orders && pointer_active &&
            self.held_fire_target.is_none() &&
            (clicked_hud == Some(HudAction::Charge) ||
             self.controls.command_pressed(&self.window,"Charge",KeyRepeat::No)) {
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
             self.controls.command_pressed(&self.window,"Chase",KeyRepeat::No)))
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
             self.controls.pressed(&self.window,"Lock On",KeyRepeat::No)))
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
             self.controls.command_pressed(&self.window,"Match Speed",KeyRepeat::No)) {
            Some(true)
        } else if accepts_orders && pointer_active &&
            (clicked_hud == Some(HudAction::FreeSpeed) ||
             self.controls.pressed(&self.window,"Free Speed",KeyRepeat::No)) {
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
             self.controls.command_pressed(&self.window,"Hold",KeyRepeat::No)) {
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
                            let additive = self.selection_additive();
                            if self.renderer.selection.select_box(&view, rectangle, additive).is_err() {
                                self.input_feedback = "Selection box is unavailable";
                            }
                        } else { self.renderer.selection.clear(); }
                        self.renderer.selected_ship = self.renderer.selection
                            .representative_ships(&view).next();
                    } else {
                        self.renderer.selected_ship = self.renderer.pick_ship(&view, x, y);
                        if let Some(ship) = self.renderer.selected_ship {
                            let additive = self.selection_additive();
                            if !additive && self.double_left_press &&
                                self.last_left_press.is_some_and(|pressed|
                                    now.duration_since(pressed) <= Duration::from_millis(250)) {
                                if let Some(ship_type) = view.combat.ship(ship).map(|body| body.ship_type) {
                                    self.renderer.selection.select_ship_type(&view, ship_type);
                                }
                            } else {
                                self.renderer.selection.select_ship(&view, ship, additive);
                                if additive {
                                    // Ctrl/Shift-click can remove the clicked squad.
                                    // Do not keep its unselected ship highlighted.
                                    self.renderer.selected_ship = self.renderer.selection
                                        .representative_ships(&view).next();
                                }
                            }
                        } else {
                            // Preserve the existing multi-selection when
                            // Ctrl/Shift-clicking empty terrain, as the native
                            // remote commander already does. A plain click
                            // still dismisses the selection.
                            if !self.selection_additive() {
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
                let destination = if let Some(point) = self.renderer.minimap_world_at(bounds, x, y) {
                    // The source minimap issues one ground order at mouse-down.
                    if !right_pressed || !pointer_active || cancel_support { return None; }
                    self.minimap_move_orders_submitted += 1;
                    point
                } else {
                    let point = self.renderer.camera.screen_to_world(x, y, width, height);
                    if right_released {
                        released_press.as_ref()?;
                        if let Some(action) = self.renderer.pick_right_click_action(&view, point) {
                            support_command = self.renderer.selection.support_command(&view, action);
                            return None;
                        }
                        if let Some(target) = self.renderer.pick_demolition_fire_point(&view, point) {
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
                    } else {
                        if !right_down || self.paused ||
                            !self.right_press.as_mut()?.should_move(now, point)
                        {
                            return None;
                        }
                        self.right_drag_move_orders_submitted += 1;
                    }
                    point
                };
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
        if inspecting && now.duration_since(self.hover_started) >= Duration::from_secs(1) {
            if let Some(ship_id) = self.hovered_ship {
                if self.renderer.draw_ship_inspection(&view, ship_id) {
                    self.ship_inspection_frames += 1;
                }
            }
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
            let elapsed = if self.dialogue_reveal_forced {
                1_000_000.0
            } else {
                self.dialogue_reveal
                    .map_or(0.0, |(_, _, began)|
                        now.saturating_duration_since(began).as_secs_f32())
            };
            self.renderer.draw_campaign_dialogue(speaker, line, elapsed);
        }
        // Help is a noninteractive presentation overlay. All underlying
        // authenticated player commands remain disabled until it closes.
        if self.battle_menu_open {
            let can_surrender = view.player_authority.player_viewer_side()
                .is_some_and(|side| view.can_surrender(side));
            self.renderer.draw_battle_menu(
                self.paused,can_surrender,
                self.allow_freeplay_restart,self.allow_freeplay_save,
                self.allow_new_squads,
                self.allow_new_squads &&
                    view.player_authority.player_viewer_side().is_some(),
            );
        } else if self.codex_open {
            self.renderer.draw_fleet_codex(&view, self.codex_selected);
        } else if self.show_controls_help {
            self.renderer.draw_controls_help();
        } else if self.match_summary_visible && self.result_controls.is_some() {
            if let Some(result) = view.terminal_result {
                let owner = view.player_authority.player_viewer_side().unwrap_or(0);
                let key = (view.world_id,view.episode_revision,owner);
                if self.match_summary.as_ref().is_none_or(|(world,revision,side,_)|
                    (*world,*revision,*side)!=key)
                {
                    let mut summary=vec![
                        "BATTLE SUMMARY / ORIGINAL MATCH COUNTS".to_owned(),
                        format!("{} - {}",
                            crate::presentation::battle_outcome_label(result),
                            crate::presentation::battle_outcome_reason(&view,result)),
                        format!("Duration: {:.1} s",result.duration_seconds),
                    ];
                    if let Some((score,counts))=view.player_match_for_side(owner) {
                        summary.push(format!("Score: {score:+}"));
                        if let Some((lost,returned,destroyed))=counts {
                            summary.push(format!("Ships destroyed: {destroyed}"));
                            summary.push(format!("Ships safely returned: {returned}"));
                            summary.push(format!("Ships lost: {lost}"));
                        }
                    } else {
                        summary.push("No owned player score for this battle".into());
                    }
                    if let Some(mined)=view.minerals_mined_for_side(owner) {
                        summary.push(format!("Minerals physically mined: {mined}"));
                    }
                    if let Some((ships,minerals))=self.campaign_ending_awards {
                        summary.push(format!("New FleetShips received: {ships}"));
                        summary.push(format!("Minerals received: {minerals}"));
                    }
                    summary.push(" ".into());
                    summary.push("F5: hide/show this summary".into());
                    summary.push("Enter: play again when available  |  Esc: exit".into());
                    self.match_summary=Some((
                        view.world_id,view.episode_revision,owner,summary,
                    ));
                }
                if let Some((_,_,_,lines))=&self.match_summary {
                    // The cached result has a fixed small number of
                    // original scoreboard fields. Borrow them directly;
                    // no allocation is required on repeated render frames.
                    let mut rows=[""; 14];
                    let mut count=0;
                    for line in lines.iter().take(rows.len()) {
                        rows[count]=line.as_str();
                        count+=1;
                    }
                    self.renderer.draw_match_summary(&rows[..count]);
                }
            }
        }
        self.render_wall_seconds += render_started.elapsed().as_secs_f64();
        let ui_started = Instant::now();
        if self.last_title_update.is_none_or(|last|
            now.duration_since(last) >= Duration::from_millis(250))
        {
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
            "Bees native | World {} | Episode {} | Tick {} | {} ships, {} shots | {} selected | {}{} | {} | Weapon contacts {} | F1 controls | F4 surrender | Esc menu",
            view.world_id.0, view.episode_revision, view.tick, counts.ships, counts.projectiles,
            self.renderer.selection.squads().len(),
            if self.paused { "PAUSED" } else {
                match self.game_speed { 1 => "RUNNING 1.5x", 2 => "RUNNING 2x", _ => "RUNNING 1x" }
            }, selected, self.input_feedback,
            if self.renderer.show_weapon_ranges { format!("{} auto targets, {} contacts, {}/{} blocked, {} exact queries, {} prefiltered, {} missing; {} aim points, {} missing",
                counts.automatic_targets, counts.range_contacts, counts.obstructed_ranges, counts.range_overlaps,
                counts.range_queries, counts.range_candidates_culled, counts.missing_range_data,
                counts.aim_points, counts.missing_aim_data) }
            else { "off".to_owned() },
        ) };
        self.window.set_title(&title);
            self.last_title_update = Some(now);
        }
        self.ui_wall_seconds += ui_started.elapsed().as_secs_f64();
        self.rendered_frames += 1;
        self.rendered_pixels = self.rendered_pixels.saturating_add(
            (width as u64).saturating_mul(height as u64)
        );
        self.culled_ship_draws = self.culled_ship_draws.saturating_add(counts.culled_ships as u64);
        self.ship_sprite_draws = self.ship_sprite_draws.saturating_add(counts.ship_sprite_draws as u64);
        self.map_background_pixels = self.map_background_pixels
            .saturating_add(counts.map_background_pixels as u64);
        self.map_background_cached_pixels = self.map_background_cached_pixels
            .saturating_add(counts.map_background_cached_pixels as u64);
        self.map_ring_layer_draws = self.map_ring_layer_draws
            .saturating_add(counts.map_ring_layer_draws as u64);
        self.map_fog_texture_pixels = self.map_fog_texture_pixels
            .saturating_add(counts.fog_source_texture_pixels as u64);
        self.carrier_deck_overlay_draws = self.carrier_deck_overlay_draws
            .saturating_add(counts.carrier_deck_overlay_draws as u64);
        self.barge_charge_effect_draws = self.barge_charge_effect_draws
            .saturating_add(counts.barge_charge_effect_draws as u64);
        self.queen_part_sprite_draws = self.queen_part_sprite_draws
            .saturating_add(counts.queen_part_sprite_draws as u64);
        self.queen_explosion_layer_draws = self.queen_explosion_layer_draws
            .saturating_add(counts.queen_explosion_layer_draws as u64);
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
        self.minimap_terrain_markers = self.minimap_terrain_markers
            .saturating_add(counts.minimap_terrain_markers as u64);
        self.minimap_briefing_mining_markers = self.minimap_briefing_mining_markers
            .saturating_add(counts.minimap_briefing_mining_markers as u64);
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
            let selected = self.renderer.selection.squads();
            if selected != self.submitted_selection.as_slice() {
                self.submitted_selection.clear();
                self.submitted_selection.extend_from_slice(selected);
                // Only genuine source-authorized selection changes are
                // replayable; pointer movement and hovering remain UI-only.
                return Ok(NativeClientAction::Input {
                    action: NativePlayerInput::Select(NativePlayerSquadSelection {
                        world_id: view.world_id,
                        episode_revision: view.episode_revision,
                        squads: self.submitted_selection.clone(),
                    }),
                    step: false,
                });
            }
        }
        let after_frame = Instant::now();
        // Render and fixed-tick simulation have independent cadences. Catch
        // up with no more than four authoritative ticks per drawn frame.
        // Cap wall-clock debt rather than entering an unbounded catch-up
        // spiral when rendering, inference or the OS stalls for a long time.
        const MAX_STEPS_PER_FRAME: u32 = 4;
        let paced_dt = self.wall_step_interval(fixed_dt);
        let max_lag = paced_dt.saturating_mul(MAX_STEPS_PER_FRAME - 1);
        if !self.paused && !self.show_controls_help &&
            after_frame.saturating_duration_since(self.next_step) > max_lag {
            self.next_step = after_frame.checked_sub(max_lag).unwrap_or(after_frame);
            self.pacing_lag_clamps += 1;
        }
        let due = if briefing || self.show_controls_help { 0 }
            else if single_step { 1 }
            else if !self.paused && after_frame >= self.next_step {
                (1 + after_frame.duration_since(self.next_step).as_nanos()
                    / paced_dt.as_nanos().max(1))
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
