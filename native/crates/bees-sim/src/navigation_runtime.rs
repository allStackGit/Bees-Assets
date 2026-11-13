//! Persistent per-Level pathfinding workspace with authored map transforms.
//!
//! Unity Pathfinder owns its static/dynamic clearance arrays and one set of
//! scratch buffers per search worker. The native rendering client and RL
//! runner both use this module and the same CombatWorld route ownership.
//! This object never owns or snapshots entities: callers supply live
//! ObstacleBody iterators from the world whenever geometry changes.

use crate::geometry::Vec2;
use crate::navigation::{NavigationError, NavigationGrid, NavigationRasterCounts};
use crate::navigation_clearance::NavigationClearance;
use crate::navigation_requests::NavigationRequest;
use crate::navigation_search::{NavigationPath, NavigationSearch};
use crate::physics::ObstacleBody;

/// The complete 2D affine portion of Level.Map.Transform.TransformPoint
/// and InverseTransformPoint. Explicit basis columns support translation,
/// rotation, nonuniform scale and nested-parent shear without assuming
/// that each game Level is at the world origin.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NavigationMapFrame {
    pub origin_world: Vec2,
    pub x_axis_world: Vec2,
    pub y_axis_world: Vec2,
}

impl NavigationMapFrame {
    pub const IDENTITY: Self = Self {
        origin_world: Vec2::ZERO,
        x_axis_world: Vec2::new(1.0, 0.0),
        y_axis_world: Vec2::new(0.0, 1.0),
    };

    pub fn valid(self) -> bool {
        self.origin_world.is_finite() &&
        self.x_axis_world.is_finite() &&
        self.y_axis_world.is_finite() &&
        self.determinant().is_finite() &&
        self.determinant().abs() > 1.0e-12
    }

    fn determinant(self) -> f32 {
        self.x_axis_world.x * self.y_axis_world.y -
            self.x_axis_world.y * self.y_axis_world.x
    }

    pub fn level_to_world(self, p: Vec2) -> Vec2 {
        self.origin_world +
            self.x_axis_world * p.x +
            self.y_axis_world * p.y
    }

    pub fn world_to_level(self, p: Vec2) -> Vec2 {
        let d = p - self.origin_world;
        let det = self.determinant();
        Vec2::new(
            (d.x * self.y_axis_world.y - d.y * self.y_axis_world.x) / det,
            (self.x_axis_world.x * d.y - self.x_axis_world.y * d.x) / det,
        )
    }
}

/// Actual rasterization work performed by this Level. Counters are
/// episode-local and count completed operations rather than estimates.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize)]
pub struct NavigationRasterMeasurements {
    pub static_rebuilds: u64,
    pub dynamic_asteroid_passes: u64,
    pub static_only_restores: u64,
}

impl NavigationRasterMeasurements {
    pub fn merge(&mut self, other: Self) {
        self.static_rebuilds += other.static_rebuilds;
        self.dynamic_asteroid_passes += other.dynamic_asteroid_passes;
        self.static_only_restores += other.static_only_restores;
    }
}

/// A reusable source-equivalent navigation workspace. Clearance is
/// refreshed from actual world geometry; search arrays are reused across
/// requests. A caller may own a workspace per arena or per Level.
pub struct NavigationRuntime {
    map_frame: NavigationMapFrame,
    clearance: NavigationClearance,
    search: NavigationSearch,
    /// None until the first physical static-obstacle rasterization. Empty
    /// static scenes can reuse the same clearance until a body appears.
    static_obstacles_present: Option<bool>,
    /// Last authoritative CombatWorld static obstacle revision that
    /// populated this clearance. None after external manual map edits.
    world_static_revision: Option<u64>,
    raster_measurements: NavigationRasterMeasurements,
}

impl NavigationRuntime {
    pub fn new(
        grid: NavigationGrid,
        frame: NavigationMapFrame,
    ) -> Result<Self, NavigationError> {
        if !frame.valid() {
            return Err(NavigationError::NonFiniteMapTransform);
        }
        let search = NavigationSearch::new(grid)?;
        let n = grid.width.checked_mul(grid.height)
            .ok_or(NavigationError::InvalidMap)?;
        let clearance = NavigationClearance::new(grid, &vec![false; n])?;
        Ok(Self { map_frame: frame, clearance, search,
            static_obstacles_present: None, world_static_revision: None,
            raster_measurements: NavigationRasterMeasurements::default() })
    }

    pub fn map_frame(&self) -> NavigationMapFrame { self.map_frame }
    pub fn clearance(&self) -> &NavigationClearance { &self.clearance }
    pub fn clearance_mut(&mut self) -> &mut NavigationClearance {
        // External map edits must not inherit the no-static-obstacles fast path.
        self.static_obstacles_present = None;
        self.world_static_revision = None;
        &mut self.clearance
    }
    pub fn static_obstacles_present(&self) -> Option<bool> {
        self.static_obstacles_present
    }

    pub fn world_static_revision(&self) -> Option<u64> {
        self.world_static_revision
    }

    pub fn raster_measurements(&self) -> NavigationRasterMeasurements {
        self.raster_measurements
    }

    /// Update after authored static objects are created, moved or destroyed.
    /// Only StaticObstacle belongs here (not map borders or asteroids).
    pub fn refresh_static<'a, I>(
        &mut self,
        live_static: I,
    ) -> Result<NavigationRasterCounts, NavigationError>
    where
        I: IntoIterator<Item = (i64, &'a ObstacleBody)>,
    {
        let frame = self.map_frame;
        let counts = self.clearance.update_static_from_obstacles(
            live_static,
            |point| frame.level_to_world(point),
            |point| frame.world_to_level(point),
        )?;
        self.static_obstacles_present = Some(counts.active_obstacles != 0);
        self.world_static_revision = None;
        self.raster_measurements.static_rebuilds += 1;
        Ok(counts)
    }

    /// The owning CombatWorld has supplied its actual static geometry and
    /// monotonic revision, not a renderer or inference-side copy. A map
    /// edit through clearance_mut() invalidates this stamp automatically.
    pub fn refresh_world_static<'a, I>(
        &mut self, revision: u64, live_static: I,
    ) -> Result<NavigationRasterCounts, NavigationError>
    where
        I: IntoIterator<Item = (i64, &'a ObstacleBody)>,
    {
        let counts = self.refresh_static(live_static)?;
        self.world_static_revision = Some(revision);
        Ok(counts)
    }

    /// Called only when Unity would request the current fixed-frame dynamic
    /// layer. Passing false restores static-only clearance without searching
    /// through any asteroid bodies.
    pub fn refresh_dynamic<'a, I>(
        &mut self,
        moving_asteroids_enabled: bool,
        live_asteroids: I,
    ) -> Result<NavigationRasterCounts, NavigationError>
    where
        I: IntoIterator<Item = (i64, &'a ObstacleBody)>,
    {
        if !moving_asteroids_enabled {
            self.clearance.restore_static_only();
            self.raster_measurements.static_only_restores += 1;
            return Ok(NavigationRasterCounts::default());
        }
        // Most headless training scenes have zero live collision asteroids
        // even when their Stage configuration permits them. Do not clear an
        // arena-sized occupancy buffer or rebuild the dynamic distance map
        // for an empty iterator on every pathfinding batch.
        let mut asteroids = live_asteroids.into_iter();
        let Some(first) = asteroids.next() else {
            self.clearance.restore_static_only();
            self.raster_measurements.static_only_restores += 1;
            return Ok(NavigationRasterCounts::default());
        };
        let frame = self.map_frame;
        let counts = self.clearance.update_dynamic_asteroids(
            std::iter::once(first).chain(asteroids),
            |point| frame.level_to_world(point),
            |point| frame.world_to_level(point),
        )?;
        self.raster_measurements.dynamic_asteroid_passes += 1;
        Ok(counts)
    }

    /// Unity's Path is expressed in Level-local coordinates. Convert both
    /// request endpoints into that frame, then convert produced waypoints
    /// back into the shared native physics frame before CombatWorld's
    /// GameShipSteering consumes them.
    pub fn search(
        &mut self,
        job: NavigationRequest,
    ) -> Result<Option<NavigationPath>, NavigationError> {
        let frame = self.map_frame;
        let start_local = frame.world_to_level(job.start);
        let end_local = frame.world_to_level(job.destination);
        let mut path = self.search.find_level_path(
            &self.clearance, start_local, end_local, job.ship_clearance,
        )?;
        if let Some(ref mut path) = path {
            for point in &mut path.points {
                *point = frame.level_to_world(*point);
                if !point.is_finite() {
                    return Err(NavigationError::NonFiniteMapTransform);
                }
            }
        }
        Ok(path)
    }
}
