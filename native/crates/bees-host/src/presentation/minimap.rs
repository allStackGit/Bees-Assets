//! Pure minimap geometry shared by the software renderer and client input.
//! The physical Level bounds are authoritative. No actor registry, fog state,
//! side discovery cache, or simulation state is owned by this UI component.

use bees_sim::collision::Aabb;
use bees_sim::geometry::Vec2;

#[derive(Clone, Copy, Debug)]
pub(super) struct MiniMapLayout {
    pub left: usize,
    pub top: usize,
    pub right: usize,
    pub bottom: usize,
    pub plot_left: f32,
    pub plot_top: f32,
    pub plot_right: f32,
    pub plot_bottom: f32,
    bounds: Aabb,
    pub(super) scale: f32,
}

impl MiniMapLayout {
    pub fn new(width: usize, height: usize, bounds: Aabb) -> Option<Self> {
        // Do not cover selection or tactical controls in narrow viewports.
        if width < 360 || height < 280 || !bounds.min.is_finite() ||
            !bounds.max.is_finite() { return None; }
        let extent = bounds.max - bounds.min;
        if extent.x <= 0.0 || extent.y <= 0.0 { return None; }
        const PANEL_W: usize = 194;
        const PANEL_H: usize = 142;
        const MARGIN: usize = 12;
        const INSET: f32 = 9.0;
        let left = MARGIN;
        let top = height - MARGIN - PANEL_H;
        let scale = ((PANEL_W as f32 - 2.0 * INSET) / extent.x)
            .min((PANEL_H as f32 - 2.0 * INSET) / extent.y);
        if !scale.is_finite() || scale <= 0.0 { return None; }
        let plot_w = extent.x * scale;
        let plot_h = extent.y * scale;
        let plot_left = left as f32 + (PANEL_W as f32 - plot_w) * 0.5;
        let plot_top = top as f32 + (PANEL_H as f32 - plot_h) * 0.5;
        Some(Self {
            left, top, right: left + PANEL_W, bottom: top + PANEL_H,
            plot_left, plot_top,
            plot_right: plot_left + plot_w,
            plot_bottom: plot_top + plot_h,
            bounds, scale,
        })
    }

    pub fn world_at(self, x: f32, y: f32) -> Option<Vec2> {
        if !x.is_finite() || !y.is_finite() ||
            x < self.left as f32 || x >= self.right as f32 ||
            y < self.top as f32 || y >= self.bottom as f32 {
            return None;
        }
        // The full minimap panel is draggable, including letterboxing.
        // Clamp to the authored Level instead of allowing off-map camera
        // destinations when dragging inside the panel's margins.
        Some(Vec2::new(
            self.bounds.min.x + ((x - self.plot_left) / self.scale)
                .clamp(0.0, self.bounds.max.x - self.bounds.min.x),
            self.bounds.max.y - ((y - self.plot_top) / self.scale)
                .clamp(0.0, self.bounds.max.y - self.bounds.min.y),
        ))
    }

    pub fn project(self, point: Vec2) -> Option<(f32, f32)> {
        if !point.is_finite() ||
            point.x < self.bounds.min.x || point.x > self.bounds.max.x ||
            point.y < self.bounds.min.y || point.y > self.bounds.max.y {
            return None;
        }
        Some(self.project_unbounded(point))
    }

    /// Fog sources may lie just outside the map while their masks extend
    /// into it; their projected centers must not be clipped at the border.
    pub(super) fn project_unbounded(self, point: Vec2) -> (f32, f32) {
        (
            self.plot_left + (point.x - self.bounds.min.x) * self.scale,
            self.plot_top + (self.bounds.max.y - point.y) * self.scale,
        )
    }

    pub fn camera_rect(self, bottom_left: Vec2, top_right: Vec2) ->
        Option<(f32, f32, f32, f32)>
    {
        let min = Vec2::new(bottom_left.x.max(self.bounds.min.x),
            bottom_left.y.max(self.bounds.min.y));
        let max = Vec2::new(top_right.x.min(self.bounds.max.x),
            top_right.y.min(self.bounds.max.y));
        if min.x >= max.x || min.y >= max.y { return None; }
        Some((
            self.plot_left + (min.x - self.bounds.min.x) * self.scale,
            self.plot_top + (self.bounds.max.y - max.y) * self.scale,
            self.plot_left + (max.x - self.bounds.min.x) * self.scale,
            self.plot_top + (self.bounds.max.y - min.y) * self.scale,
        ))
    }
}
