//! Pure minimap geometry shared by the software renderer and client input.
//! The physical Level bounds are authoritative. No actor registry, fog state,
//! side discovery cache, or simulation state is owned by this UI component.

use bees_sim::collision::Aabb;
use bees_sim::geometry::Vec2;

pub(super) fn terrain_color(mining: bool) -> u32 {
    if mining { 0xffd57a } else { 0x8195a9 }
}

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
        // Native local game uses bottom-left; remote multiplayer reserves
        // that space for authenticated mission dialogue and input feedback.
        Self::at(width, height, bounds, 12, height.checked_sub(154)?)
    }

    /// Same projection, map bounds and hit-testing, placed above the remote
    /// command and dialogue strip rather than duplicating minimap math.
    pub(super) fn top_right(width: usize, height: usize, bounds: Aabb) -> Option<Self> {
        Self::at(width, height, bounds, width.checked_sub(206)?, 12)
    }

    fn at(width: usize, height: usize, bounds: Aabb,
        left: usize, top: usize) -> Option<Self>
    {
        if width < 360 || height < 280 || left + 194 > width ||
            top + 142 > height || !bounds.min.is_finite() ||
            !bounds.max.is_finite() { return None; }
        let extent = bounds.max - bounds.min;
        if extent.x <= 0.0 || extent.y <= 0.0 { return None; }
        const PANEL_W: usize = 194;
        const PANEL_H: usize = 142;
        const INSET: f32 = 9.0;
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

    /// Return a clipped outline only when the hull is large enough on
    /// screen to be readable. Smaller hulls use a center pixel.
    pub fn terrain_outline(self, hull: Aabb) -> Option<(i32, i32, i32, i32)> {
        if !hull.min.is_finite() || !hull.max.is_finite() { return None; }
        let (left, top) = self.project_unbounded(Vec2::new(hull.min.x, hull.max.y));
        let (right, bottom) = self.project_unbounded(Vec2::new(hull.max.x, hull.min.y));
        let left = left.max(self.plot_left).round() as i32;
        let top = top.max(self.plot_top).round() as i32;
        let right = right.min(self.plot_right).round() as i32;
        let bottom = bottom.min(self.plot_bottom).round() as i32;
        (right.saturating_sub(left) >= 4 && bottom.saturating_sub(top) >= 4)
            .then_some((left, top, right, bottom))
    }

    pub fn draw_briefing_mining_marker(self, pixels: &mut [u32], stride: usize, point: Vec2)
        -> bool
    {
        let Some((x, y)) = self.project(point) else { return false };
        let x = x.round() as usize;
        let y = y.round() as usize;
        // The plot inset leaves room for the ring even at the map edge.
        const ROWS: [u16; 9] = [
            0b000111000, 0b011000110, 0b010000010, 0b100000001, 0b100000001,
            0b100000001, 0b010000010, 0b011000110, 0b000111000,
        ];
        for (row, mask) in ROWS.into_iter().enumerate() {
            let start = (y + row - 4) * stride + x - 4;
            for column in 0..9 {
                if mask & (1 << column) != 0 {
                    pixels[start + column] = 0xff493c;
                }
            }
        }
        true
    }

    /// Clip the viewport or an authored objective zone to the navigable map.
    pub fn project_rect(self, min: Vec2, max: Vec2) -> Option<(f32, f32, f32, f32)> {
        if !min.is_finite() || !max.is_finite() { return None; }
        let min = Vec2::new(min.x.max(self.bounds.min.x), min.y.max(self.bounds.min.y));
        let max = Vec2::new(max.x.min(self.bounds.max.x), max.y.min(self.bounds.max.y));
        if min.x >= max.x || min.y >= max.y { return None; }
        Some((
            self.plot_left + (min.x - self.bounds.min.x) * self.scale,
            self.plot_top + (self.bounds.max.y - max.y) * self.scale,
            self.plot_left + (max.x - self.bounds.min.x) * self.scale,
            self.plot_top + (self.bounds.max.y - min.y) * self.scale,
        ))
    }
}
