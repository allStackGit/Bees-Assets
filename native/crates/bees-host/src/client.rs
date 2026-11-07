//! Optional native window around the shared host's borrowed render view.
//! Input changes camera, inspection and pacing only. Policy decisions still
//! come from the same resident model and authoritative Level driver.

use std::time::{Duration, Instant};
use bees_sim::collision::Aabb;
use bees_sim::simulation::WorldId;
use minifb::{Key, KeyRepeat, MouseButton, MouseMode, Window, WindowOptions};
use crate::presentation::{NativeCamera, NativeRenderWorldView, NativeSoftwareRenderer};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeClientAction { Wait, Step, Quit }

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
            rendered_frames: 0, render_wall_seconds: 0.0 })
    }

    pub fn selected_world(&self) -> usize { self.selected_world }

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
            if self.selection_owner.is_some_and(|(world, _)| world != view.world_id) {
                let (width, height) = self.renderer.size();
                self.renderer.camera = NativeCamera::fit(bounds, width, height);
            }
            self.selection_owner = Some(owner);
        }
        self.bounds = bounds;
        if self.window.is_key_pressed(Key::Space, KeyRepeat::No) {
            self.paused = !self.paused;
            self.next_step = now;
        }
        let single_step = self.paused && self.window.is_key_pressed(Key::N, KeyRepeat::No);
        if self.window.is_key_pressed(Key::Tab, KeyRepeat::No) && world_count > 0 {
            self.selected_world = (self.selected_world + 1) % world_count;
            self.renderer.selected_ship = None;
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
        let left_down = self.window.get_mouse_down(MouseButton::Left);
        if left_down && !self.left_was_down {
            self.renderer.selected_ship = mouse.and_then(|(x, y)| self.renderer.pick_ship(&view, x, y));
        }
        self.left_was_down = left_down;
        let render_started = Instant::now();
        let counts = self.renderer.render(&view, self.bounds);
        let selected = self.renderer.selected_ship.and_then(|id| view.combat.ship(id))
            .filter(|ship| !ship.vitals.is_dead())
            .map(|ship| format!(" | Ship {} type {} side {} HP {}/{} TSV {}",
                ship.entity_id, ship.ship_type, ship.side, ship.vitals.health(),
                ship.vitals.max_health(), ship.vitals.tsv())).unwrap_or_default();
        self.window.set_title(&format!(
            "Bees native | World {} | Episode {} | Tick {} | {} ships, {} shots | {}{} | Space pause, N step, Tab world, arrows pan, wheel zoom, F fit, Esc exit",
            view.world_id.0, view.episode_revision, view.tick, counts.ships, counts.projectiles,
            if self.paused { "PAUSED" } else { "RUNNING" }, selected,
        ));
        self.render_wall_seconds += render_started.elapsed().as_secs_f64();
        self.rendered_frames += 1;
        self.window.update_with_buffer(self.renderer.pixels(), width, height)?;
        if !self.window.is_open() { return Ok(NativeClientAction::Quit); }
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
