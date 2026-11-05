//! Unity Pathfinder.Obstacles dynamic collision-asteroid occupancy.
//!
//! This is the exact rasterization boundary used by the old pathfinder:
//! Scale=4, explicit clearance colliders, a 2.5-second velocity lookahead,
//! five overlap-point probes and closest-point checks. It does NOT invent a
//! route-search or training-specific avoidance algorithm. Existing client
//! pathfinding and future native A* can consume the same blocked-cell mask.

use crate::collision::Aabb;
use crate::geometry::Vec2;
use crate::physics::ObstacleBody;

pub const NAVIGATION_CELL_SCALE: f32 = 4.0;
pub const MOVING_OBSTACLE_LOOKAHEAD_SECONDS: f32 = 2.5;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NavigationError {
    InvalidMap,
    IncorrectCellBuffer,
    NonFiniteMapTransform,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NavigationRasterCounts {
    pub active_asteroids: u64,
    pub candidate_cells: u64,
    pub blocked_cells: u64,
}

/// Matches Pathfinder.Setup / ConvertToMapCoordinates and
/// ConvertToLevelCoordinates: Y increases upwards in level coordinates but
/// grid rows increase downwards. The caller owns Level.Map.Transform; the
/// supplied functions must use its TransformPoint and InverseTransformPoint
/// semantics, including any scale or rotation.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NavigationGrid {
    pub width: usize,
    pub height: usize,
    pub half_map_width: f32,
    pub half_map_height: f32,
}

impl NavigationGrid {
    pub fn new(map_width: f32, map_height: f32) -> Result<Self, NavigationError> {
        if !map_width.is_finite() || !map_height.is_finite() ||
            map_width <= 0.0 || map_height <= 0.0 {
            return Err(NavigationError::InvalidMap);
        }
        let width = (map_width / NAVIGATION_CELL_SCALE).ceil() as usize;
        let height = (map_height / NAVIGATION_CELL_SCALE).ceil() as usize;
        if width == 0 || height == 0 || width.checked_mul(height).is_none() {
            return Err(NavigationError::InvalidMap);
        }
        Ok(Self {
            width, height,
            half_map_width: map_width * 0.5,
            half_map_height: map_height * 0.5,
        })
    }

    pub fn cell_count(self) -> usize {
        self.width * self.height
    }

    fn to_cell(self, level: Vec2) -> (usize, usize) {
        let x = ((self.half_map_width + level.x) / NAVIGATION_CELL_SCALE).floor();
        let y = ((self.half_map_height - level.y) / NAVIGATION_CELL_SCALE).floor();
        (
            x.clamp(0.0, (self.width - 1) as f32) as usize,
            y.clamp(0.0, (self.height - 1) as f32) as usize,
        )
    }

    fn cell_center(self, x: usize, y: usize) -> Vec2 {
        Vec2::new(
            -self.half_map_width + x as f32 * NAVIGATION_CELL_SCALE +
                NAVIGATION_CELL_SCALE * 0.5,
            self.half_map_height - y as f32 * NAVIGATION_CELL_SCALE -
                NAVIGATION_CELL_SCALE * 0.5,
        )
    }

    /// Rebuild only dynamic obstacle occupancy. Unity initializes the full
    /// clearance buffer from its static base and sets blocked dynamic cells
    /// to zero before recalculating clearance distances. Thus this returns
    /// the moving-asteroid *mask*, not a substitute A* clearance array.
    ///
    /// The input should be CombatWorld.dynamic_navigation_asteroids(), which
    /// excludes dead entities but includes delayed-kill asteroids until their
    /// actual removal. All scratch is caller-owned, and no world registry is
    /// duplicated in this module.
    pub fn rasterize_dynamic_asteroids<'a, I, ToWorld, ToLevel>(
        self,
        asteroids: I,
        level_to_world: ToWorld,
        world_to_level: ToLevel,
        blocked: &mut [bool],
    ) -> Result<NavigationRasterCounts, NavigationError>
    where
        I: IntoIterator<Item = (i64, &'a ObstacleBody)>,
        ToWorld: Fn(Vec2) -> Vec2,
        ToLevel: Fn(Vec2) -> Vec2,
    {
        if blocked.len() != self.cell_count() {
            return Err(NavigationError::IncorrectCellBuffer);
        }
        blocked.fill(false);
        let mut counts = NavigationRasterCounts::default();
        for (_, asteroid) in asteroids {
            if !asteroid.active { continue; }
            let Some(bounds) = asteroid.navigation_bounds() else { continue };
            counts.active_asteroids += 1;
            let padding = Vec2::new(
                asteroid.linear_velocity.x.abs() * MOVING_OBSTACLE_LOOKAHEAD_SECONDS,
                asteroid.linear_velocity.y.abs() * MOVING_OBSTACLE_LOOKAHEAD_SECONDS,
            );
            let expanded = Aabb {
                min: bounds.min - padding,
                max: bounds.max + padding,
            };
            let top_left = world_to_level(Vec2::new(expanded.min.x, expanded.max.y));
            let bottom_right = world_to_level(Vec2::new(expanded.max.x, expanded.min.y));
            if !top_left.is_finite() || !bottom_right.is_finite() {
                return Err(NavigationError::NonFiniteMapTransform);
            }
            let (first_x, first_y) = self.to_cell(top_left);
            let (last_x, last_y) = self.to_cell(bottom_right);
            for y in first_y.min(last_y)..=first_y.max(last_y) {
                for x in first_x.min(last_x)..=first_x.max(last_x) {
                    let idx = y * self.width + x;
                    if blocked[idx] { continue; }
                    counts.candidate_cells += 1;
                    let center = self.cell_center(x, y);
                    if Self::cell_touched(
                        asteroid, center,
                        &level_to_world, &world_to_level,
                    )? {
                        blocked[idx] = true;
                        counts.blocked_cells += 1;
                    }
                }
            }
        }
        Ok(counts)
    }

    fn cell_touched<ToWorld, ToLevel>(
        asteroid: &ObstacleBody,
        center: Vec2,
        to_world: &ToWorld,
        to_level: &ToLevel,
    ) -> Result<bool, NavigationError>
    where
        ToWorld: Fn(Vec2) -> Vec2,
        ToLevel: Fn(Vec2) -> Vec2,
    {
        let half = NAVIGATION_CELL_SCALE * 0.5;
        let points = [
            center,
            center + Vec2::new(-half, -half),
            center + Vec2::new(-half, half),
            center + Vec2::new(half, -half),
            center + Vec2::new(half, half),
        ];
        for point in points {
            let world = to_world(point);
            if !world.is_finite() {
                return Err(NavigationError::NonFiniteMapTransform);
            }
            if asteroid.navigation_contains_point(world) {
                return Ok(true);
            }
        }

        let nearest = asteroid.navigation_closest_point(to_world(center))
            .expect("live body has authored navigation collider");
        let nearest = to_level(nearest);
        if !nearest.is_finite() {
            return Err(NavigationError::NonFiniteMapTransform);
        }
        if (nearest.x - center.x).abs() <= half &&
            (nearest.y - center.y).abs() <= half {
            return Ok(true);
        }

        let velocity = asteroid.linear_velocity;
        if velocity != Vec2::ZERO {
            // Unity's Pathfinder.Obstacles uses the velocity directly in
            // level coordinates when probing this projected point.
            let projected = center - velocity * MOVING_OBSTACLE_LOOKAHEAD_SECONDS;
            let projected_world = to_world(projected);
            if !projected_world.is_finite() {
                return Err(NavigationError::NonFiniteMapTransform);
            }
            let nearest = asteroid.navigation_closest_point(projected_world)
                .expect("live body has authored navigation collider");
            let local = to_level(nearest);
            if !local.is_finite() {
                return Err(NavigationError::NonFiniteMapTransform);
            }
            return Ok((local.x - projected.x).abs() <= half &&
                (local.y - projected.y).abs() <= half);
        }
        Ok(false)
    }
}
