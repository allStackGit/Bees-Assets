//! Unity Pathfinder clearance maps, owned per native combat world.
//!
//! This is the two-pass eight-neighbor clearance transform from Pathfinder.cs
//! and the static signed-clearance transform from Pathfinder.Search.cs.
//! Dynamic moving-asteroid occupancy replaces cells in a COPY of the static
//! clearance map before recalculating, exactly as Unity does. Path search may
//! read an immutable snapshot without racing a concurrent update.
use crate::geometry::Vec2;
use crate::navigation::{
    NavigationError, NavigationGrid, NavigationRasterCounts,
};
use crate::physics::ObstacleBody;

const UNREACHABLE_SIGNED: i32 = i32::MAX / 4;

#[derive(Debug)]
pub struct NavigationClearance {
    grid: NavigationGrid,
    base: Vec<i32>,
    signed_static: Vec<i32>,
    dynamic: Vec<i32>,
    moving_blocked: Vec<bool>,
}

impl NavigationClearance {
    pub fn new(grid: NavigationGrid, static_blocked: &[bool]) -> Result<Self, NavigationError> {
        if grid.width == 0 || grid.height == 0 ||
            grid.width.checked_mul(grid.height) != Some(static_blocked.len()) {
            return Err(NavigationError::IncorrectCellBuffer);
        }
        let length = static_blocked.len();
        let mut layers = Self {
            grid, base: vec![0; length],
            signed_static: vec![0; length],
            dynamic: vec![0; length],
            moving_blocked: vec![false; length],
        };
        layers.set_static_blocked(static_blocked)?;
        Ok(layers)
    }

    pub fn grid(&self) -> NavigationGrid { self.grid }
    pub fn static_clearance(&self) -> &[i32] { &self.base }
    pub fn static_signed_clearance(&self) -> &[i32] { &self.signed_static }
    pub fn dynamic_clearance(&self) -> &[i32] { &self.dynamic }

    /// Called when the map's *static* obstacles change; it replaces both
    /// base and signed-static layers. Static changes invalidate the old
    /// dynamic map, so the owning Level should refresh dynamic occupancy.
    pub fn set_static_blocked(&mut self, blocked: &[bool]) -> Result<(), NavigationError> {
        if blocked.len() != self.base.len() {
            return Err(NavigationError::IncorrectCellBuffer);
        }
        for (base, &blocked) in self.base.iter_mut().zip(blocked) {
            *base = if blocked { 0 } else { 1 };
        }
        calculate_clearance(self.grid, &mut self.base);
        build_signed_static(self.grid, &self.base, &mut self.signed_static);
        self.dynamic.copy_from_slice(&self.base);
        self.moving_blocked.fill(false);
        Ok(())
    }

    /// Rebuild Unity's static clearance from live StaticObstacle geometry,
    /// excluding map-border triggers and moving asteroids. Map-specific
    /// placement stays in the owning CombatWorld and never gets copied into
    /// a long-lived second obstacle registry.
    pub fn update_static_from_obstacles<'a, I, ToWorld, ToLevel>(
        &mut self,
        obstacles: I,
        to_world: ToWorld,
        to_level: ToLevel,
    ) -> Result<NavigationRasterCounts, NavigationError>
    where
        I: IntoIterator<Item = (i64, &'a ObstacleBody)>,
        ToWorld: Fn(Vec2) -> Vec2,
        ToLevel: Fn(Vec2) -> Vec2,
    {
        let counts = self.grid.rasterize_obstacles(
            obstacles, to_world, to_level, &mut self.moving_blocked,
        )?;
        // The raster mask is the exact static blocking input; setting it
        // refreshes the base, signed static and dynamic clearance maps.
        // set_static_blocked clears the scratch only after reading the mask.
        for (base, &blocked) in self.base.iter_mut().zip(&self.moving_blocked) {
            *base = if blocked { 0 } else { 1 };
        }
        calculate_clearance(self.grid, &mut self.base);
        build_signed_static(self.grid, &self.base, &mut self.signed_static);
        self.dynamic.copy_from_slice(&self.base);
        self.moving_blocked.fill(false);
        Ok(counts)
    }

    /// Unity's UpdateDynamicObstacleLayer: start from the static base,
    /// overlay live collision asteroids, then recompute clearance. No
    /// asteroid lifetime history is duplicated in the navigation system.
    pub fn update_dynamic_asteroids<'a, I, ToWorld, ToLevel>(
        &mut self,
        asteroids: I,
        to_world: ToWorld,
        to_level: ToLevel,
    ) -> Result<NavigationRasterCounts, NavigationError>
    where
        I: IntoIterator<Item = (i64, &'a ObstacleBody)>,
        ToWorld: Fn(Vec2) -> Vec2,
        ToLevel: Fn(Vec2) -> Vec2,
    {
        let counts = self.grid.rasterize_obstacles(
            asteroids, to_world, to_level, &mut self.moving_blocked,
        )?;
        self.dynamic.copy_from_slice(&self.base);
        for (clearance, &moving) in self.dynamic.iter_mut().zip(&self.moving_blocked) {
            if moving { *clearance = 0; }
        }
        calculate_clearance(self.grid, &mut self.dynamic);
        Ok(counts)
    }

    /// Unity short-circuits moving-asteroid occupancy when disabled.
    pub fn restore_static_only(&mut self) {
        self.dynamic.copy_from_slice(&self.base);
        self.moving_blocked.fill(false);
    }
}

/// The full-grid version of Unity's CalculateClearance(int[], maxClearance).
/// Grid edge limits are part of its answer, even with no obstacles.
fn calculate_clearance(grid: NavigationGrid, cells: &mut [i32]) {
    let width = grid.width;
    let height = grid.height;
    let cap = width.max(height).min(i32::MAX as usize) as i32;

    for y in 0..height {
        for x in 0..width {
            let i = y * width + x;
            if cells[i] > 0 {
                cells[i] = cap.min((x + 1) as i32).min((y + 1) as i32)
                    .min((width - x) as i32).min((height - y) as i32);
            }
        }
    }

    for y in 0..height {
        for x in 0..width {
            let i = y * width + x;
            if cells[i] == 0 { continue; }
            let mut clearance = cells[i];
            if x > 0 { clearance = clearance.min(cells[i - 1] + 1); }
            if y > 0 { clearance = clearance.min(cells[i - width] + 1); }
            if x > 0 && y > 0 { clearance = clearance.min(cells[i - width - 1] + 1); }
            if x + 1 < width && y > 0 {
                clearance = clearance.min(cells[i - width + 1] + 1);
            }
            cells[i] = clearance;
        }
    }

    for y in (0..height).rev() {
        for x in (0..width).rev() {
            let i = y * width + x;
            if cells[i] == 0 { continue; }
            let mut clearance = cells[i];
            if x + 1 < width { clearance = clearance.min(cells[i + 1] + 1); }
            if y + 1 < height { clearance = clearance.min(cells[i + width] + 1); }
            if x + 1 < width && y + 1 < height {
                clearance = clearance.min(cells[i + width + 1] + 1);
            }
            if x > 0 && y + 1 < height {
                clearance = clearance.min(cells[i + width - 1] + 1);
            }
            cells[i] = clearance;
        }
    }
}

/// Positive clearance for static free cells, negative distance-to-free for
/// blocked cells. In particular, *dynamic* asteroids do not change this map:
/// the separate signed static map guides the escape from a static obstacle.
fn build_signed_static(grid: NavigationGrid, base: &[i32], signed: &mut [i32]) {
    for (signed_cell, &base_cell) in signed.iter_mut().zip(base) {
        *signed_cell = if base_cell == 0 { UNREACHABLE_SIGNED } else { 0 };
    }
    let width = grid.width;
    let height = grid.height;

    for y in 0..height {
        for x in 0..width {
            let i = y * width + x;
            if signed[i] == 0 { continue; }
            let mut distance = signed[i];
            if x > 0 { distance = distance.min(signed[i - 1] + 1); }
            if y > 0 { distance = distance.min(signed[i - width] + 1); }
            if x > 0 && y > 0 { distance = distance.min(signed[i - width - 1] + 1); }
            if x + 1 < width && y > 0 {
                distance = distance.min(signed[i - width + 1] + 1);
            }
            signed[i] = distance;
        }
    }
    for y in (0..height).rev() {
        for x in (0..width).rev() {
            let i = y * width + x;
            if signed[i] == 0 { continue; }
            let mut distance = signed[i];
            if x + 1 < width { distance = distance.min(signed[i + 1] + 1); }
            if y + 1 < height { distance = distance.min(signed[i + width] + 1); }
            if x + 1 < width && y + 1 < height {
                distance = distance.min(signed[i + width + 1] + 1);
            }
            if x > 0 && y + 1 < height {
                distance = distance.min(signed[i + width - 1] + 1);
            }
            signed[i] = distance;
        }
    }
    for (cell, &base_cell) in signed.iter_mut().zip(base) {
        *cell = if base_cell > 0 { base_cell } else { -*cell };
    }
}
