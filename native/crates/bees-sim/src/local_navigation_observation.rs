//! Source-matched RlCombatPerception local navigation occupancy grid.
//!
//! Unity's 21x21 grid is centered on the observer in LEVEL coordinates,
//! with 6-unit cells. It marks out-of-map cells and visible STATIC obstacles;
//! collision asteroids are represented in their own moving-asteroid slots,
//! and do not appear in this local occupancy raster.
//!
//! This module implements the exact AABB rasterization used by the current
//! Unity perception, not the separate gameplay A* navigation algorithm.

use crate::collision::Aabb;
use crate::geometry::Vec2;
use crate::perception::{NAV_LEN, NAV_SIZE};

pub const NAV_CELL_SIZE: f32 = 6.0;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LocalNavigationError {
    InvalidMap,
    InvalidObserver,
    InvalidObstacle,
    NotInitialized,
}

/// Reusable fixed-size occupancy values in WORLD-GRID index order.
/// perception::write_navigation rotates these to policy-grid index order.
pub struct LocalNavigationOccupancy {
    occupancy: [f32; NAV_LEN],
    grid_min: Vec2,
    grid_max: Vec2,
    initialized: bool,
}

impl Default for LocalNavigationOccupancy {
    fn default() -> Self {
        Self {
            occupancy: [0.0; NAV_LEN],
            grid_min: Vec2::ZERO,
            grid_max: Vec2::ZERO,
            initialized: false,
        }
    }
}

impl LocalNavigationOccupancy {
    pub fn new() -> Self { Self::default() }

    pub fn occupancy(&self) -> &[f32; NAV_LEN] { &self.occupancy }

    /// Begin a new observation. Map bounds and observer position are the
    /// actual source Level.MinX/MaxX/MinY/MaxY and Ship.GetPosition.
    pub fn reset(
        &mut self,
        level_bounds: Aabb,
        observer: Vec2,
    ) -> Result<(), LocalNavigationError> {
        if !observer.is_finite() {
            return Err(LocalNavigationError::InvalidObserver);
        }
        if !level_bounds.min.is_finite() || !level_bounds.max.is_finite() ||
            level_bounds.max.x <= level_bounds.min.x ||
            level_bounds.max.y <= level_bounds.min.y {
            return Err(LocalNavigationError::InvalidMap);
        }
        let half_span = NAV_SIZE as f32 * NAV_CELL_SIZE * 0.5;
        let min = observer - Vec2::new(half_span, half_span);
        self.grid_min = min;
        self.grid_max = min + Vec2::new(2.0 * half_span, 2.0 * half_span);
        self.occupancy.fill(0.0);
        self.initialized = true;
        // The vast majority of policy decisions in open training arenas
        // observe a square wholly inside the Level. Avoid 441 per-cell
        // bounds tests then; visible static AABBs still mark their cells
        // afterward. Use the exact arithmetic of the last cell's maximum
        // (rather than a rounded grid span) to preserve boundary parity.
        let last_cell_min = min + Vec2::new(
            (NAV_SIZE - 1) as f32 * NAV_CELL_SIZE,
            (NAV_SIZE - 1) as f32 * NAV_CELL_SIZE,
        );
        let last_cell_max = last_cell_min + Vec2::new(NAV_CELL_SIZE, NAV_CELL_SIZE);
        if min.x >= level_bounds.min.x && min.y >= level_bounds.min.y &&
            last_cell_max.x <= level_bounds.max.x &&
            last_cell_max.y <= level_bounds.max.y {
            return Ok(());
        }
        for y in 0..NAV_SIZE {
            let cell_min_y = min.y + y as f32 * NAV_CELL_SIZE;
            let cell_max_y = cell_min_y + NAV_CELL_SIZE;
            for x in 0..NAV_SIZE {
                let cell_min_x = min.x + x as f32 * NAV_CELL_SIZE;
                let cell_max_x = cell_min_x + NAV_CELL_SIZE;
                if cell_min_x < level_bounds.min.x ||
                    cell_max_x > level_bounds.max.x ||
                    cell_min_y < level_bounds.min.y ||
                    cell_max_y > level_bounds.max.y {
                    self.occupancy[y * NAV_SIZE + x] = 1.0;
                }
            }
        }
        Ok(())
    }

    /// Port of RlCombatPerception.MarkNavigationAabb. Feed only living,
    /// discovered STATIC obstacles' level-local clearance-collider bounds.
    /// No approximation from ship or dynamic asteroid geometry is allowed.
    pub fn mark_static_aabb(
        &mut self,
        position: Vec2,
        half_extents: Vec2,
    ) -> Result<(), LocalNavigationError> {
        if !self.initialized {
            return Err(LocalNavigationError::NotInitialized);
        }
        if !position.is_finite() || !half_extents.is_finite() ||
            half_extents.x < 0.0 || half_extents.y < 0.0 {
            return Err(LocalNavigationError::InvalidObstacle);
        }
        let min_x = position.x - half_extents.x;
        let max_x = position.x + half_extents.x;
        let min_y = position.y - half_extents.y;
        let max_y = position.y + half_extents.y;
        if max_x <= self.grid_min.x || min_x >= self.grid_max.x ||
            max_y <= self.grid_min.y || min_y >= self.grid_max.y {
            return Ok(());
        }
        let hi = (NAV_SIZE - 1) as f32;
        let x0 = ((min_x - self.grid_min.x) / NAV_CELL_SIZE)
            .floor().clamp(0.0, hi) as usize;
        let x1 = (((max_x - self.grid_min.x) / NAV_CELL_SIZE).ceil() - 1.0)
            .clamp(0.0, hi) as usize;
        let y0 = ((min_y - self.grid_min.y) / NAV_CELL_SIZE)
            .floor().clamp(0.0, hi) as usize;
        let y1 = (((max_y - self.grid_min.y) / NAV_CELL_SIZE).ceil() - 1.0)
            .clamp(0.0, hi) as usize;
        for y in y0..=y1 {
            for x in x0..=x1 {
                self.occupancy[y * NAV_SIZE + x] = 1.0;
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::control::PolicyFrame;
    use crate::perception::{write_navigation, NAV_START};
    use crate::policy::OBSERVATION_FLOATS;

    #[test]
    fn out_of_bounds_and_visible_static_aabbs_use_unity_grid_cells() {
        let mut grid = LocalNavigationOccupancy::new();
        let level = Aabb {
            min: Vec2::new(-50.0, -50.0),
            max: Vec2::new(50.0, 50.0),
        };
        grid.reset(level, Vec2::ZERO).unwrap();
        assert_eq!(grid.occupancy()[0], 1.0);
        assert_eq!(grid.occupancy()[10 * NAV_SIZE + 10], 0.0);
        grid.mark_static_aabb(
            Vec2::new(10.0, 0.0),
            Vec2::new(2.0, 2.0),
        ).unwrap();
        assert_eq!(grid.occupancy()[10 * NAV_SIZE + 11], 1.0);
        let mut row = [f32::NAN; OBSERVATION_FLOATS];
        write_navigation(&mut row, grid.occupancy(),
            PolicyFrame::new(0)).unwrap();
        assert_eq!(row[NAV_START + 10 * NAV_SIZE + 11], 1.0);
    }
}
