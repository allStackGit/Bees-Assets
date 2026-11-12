//! Source-grounded Hive Mind vision inputs and cost measurements.
//!
//! Unity: HiveMindVision.CanSee and GameState.RefreshHiveMindMapObjectVision.
//! The shared CombatWorld refreshes live ship and obstacle discoveries with
//! its authoritative collider geometry and world-owned learned caches.
//! MapObjects use their real CombatWorld primary colliders; obstacles
//! use authored clearance-mapping colliders when present. Both discovery
//! kinds share the same sticky faction-vision cache.

use crate::physics::VisionProximity;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct HiveMindObserver {
    pub ship_id: i64,
    /// Source: HiveMindVision.GetEffectiveRange(ship):
    /// ship.Sight != 0 ? ship.Sight : ship.MaxRange.
    pub effective_range: i32,
    /// The actual ship's IsHiveMindControlled / GameState registration.
    pub registered: bool,
    /// Both component and Unity CircleCollider2D must be enabled.
    pub enabled: bool,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize)]
pub struct VisionSweepCounts {
    pub completed_sweeps: u64,
    pub suppressed_sweeps: u64,
    pub active_observers: u64,
    /// All logically eligible observer/enemy pairs (comparable to the old scan).
    pub live_ship_pairs: u64,
    /// Pairs actually examined after the physics grid or dense fallback.
    pub ship_pairs_tested: u64,
    pub unlearned_obstacle_pairs: u64,
    /// Obstacle spatial candidate count across all source observers.
    /// Compare with unlearned_obstacle_pairs for broadphase savings.
    pub obstacle_candidates: u64,
    /// Rebindings of the existing shared ship/obstacle spatial grid for
    /// visibility; a multi-observer vision pass should need at most one.
    pub obstacle_vision_grid_rebuilds: u64,
    /// Cheap conservative source-hull radius rejects before any geometry
    /// refresh/exact Collider2D.ClosestPoint call. Included in the usual
    /// bounds-rejection total for comparable historical profiling.
    pub obstacle_radius_rejections: u64,
    pub aabb_rejections: u64,
    pub exact_collider_tests: u64,
    pub visible_enemy_pairs: u64,
    pub first_ship_discoveries: u64,
    pub first_obstacle_discoveries: u64,
    pub first_mining_discoveries: u64,
    pub first_map_object_discoveries: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum VisionError {
    InvalidObserverId(i64),
    InvalidEffectiveRange(i64),
    UnknownObserver(i64),
    DuplicatedObserver(i64),
    StorageMismatch,
    InvalidObserverState(i64),
}

/// A single authoritative first faction-wide sighting made by the actual
/// collider vision pass. It carries only identity and sighting category,
/// never fabricated reward, command TSV or model observation values.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct VisionFirstSighting {
    pub observer_id: i64,
    pub side: i32,
    pub target_id: i64,
    pub kind: crate::level_discovery::DiscoveryKind,
}

/// Rollup throughput costs without allocations inside the combat loop.
impl VisionSweepCounts {
    /// Account for broadphase and exact geometry effort in both ship and
    /// environment vision, without rechecking the collider or allocating.
    pub(crate) fn record_proximity(&mut self, result: VisionProximity) -> bool {
        match result {
            VisionProximity::RejectedByBounds => {
                self.aabb_rejections += 1;
                false
            }
            VisionProximity::OutsideCollider => {
                self.exact_collider_tests += 1;
                false
            }
            VisionProximity::WithinRange => {
                self.exact_collider_tests += 1;
                true
            }
        }
    }

    /// Status samples use monotonic successful-step totals. A counter reset
    /// must invalidate the interval rather than fabricate negative work.
    pub fn checked_delta(self, before: Self) -> Option<Self> {
        Some(Self {
            completed_sweeps: self.completed_sweeps.checked_sub(before.completed_sweeps)?,
            suppressed_sweeps: self.suppressed_sweeps.checked_sub(before.suppressed_sweeps)?,
            active_observers: self.active_observers.checked_sub(before.active_observers)?,
            live_ship_pairs: self.live_ship_pairs.checked_sub(before.live_ship_pairs)?,
            ship_pairs_tested: self.ship_pairs_tested.checked_sub(before.ship_pairs_tested)?,
            unlearned_obstacle_pairs: self.unlearned_obstacle_pairs.checked_sub(before.unlearned_obstacle_pairs)?,
            obstacle_candidates: self.obstacle_candidates.checked_sub(before.obstacle_candidates)?,
            obstacle_vision_grid_rebuilds: self.obstacle_vision_grid_rebuilds.checked_sub(before.obstacle_vision_grid_rebuilds)?,
            obstacle_radius_rejections: self.obstacle_radius_rejections.checked_sub(before.obstacle_radius_rejections)?,
            aabb_rejections: self.aabb_rejections.checked_sub(before.aabb_rejections)?,
            exact_collider_tests: self.exact_collider_tests.checked_sub(before.exact_collider_tests)?,
            visible_enemy_pairs: self.visible_enemy_pairs.checked_sub(before.visible_enemy_pairs)?,
            first_ship_discoveries: self.first_ship_discoveries.checked_sub(before.first_ship_discoveries)?,
            first_obstacle_discoveries: self.first_obstacle_discoveries.checked_sub(before.first_obstacle_discoveries)?,
            first_mining_discoveries: self.first_mining_discoveries.checked_sub(before.first_mining_discoveries)?,
            first_map_object_discoveries: self.first_map_object_discoveries.checked_sub(before.first_map_object_discoveries)?,
        })
    }

    pub fn merge(&mut self, other: &Self) {
        self.completed_sweeps += other.completed_sweeps;
        self.suppressed_sweeps += other.suppressed_sweeps;
        self.active_observers += other.active_observers;
        self.live_ship_pairs += other.live_ship_pairs;
        self.ship_pairs_tested += other.ship_pairs_tested;
        self.unlearned_obstacle_pairs += other.unlearned_obstacle_pairs;
        self.obstacle_candidates += other.obstacle_candidates;
        self.obstacle_vision_grid_rebuilds += other.obstacle_vision_grid_rebuilds;
        self.obstacle_radius_rejections += other.obstacle_radius_rejections;
        self.aabb_rejections += other.aabb_rejections;
        self.exact_collider_tests += other.exact_collider_tests;
        self.visible_enemy_pairs += other.visible_enemy_pairs;
        self.first_ship_discoveries += other.first_ship_discoveries;
        self.first_obstacle_discoveries += other.first_obstacle_discoveries;
        self.first_mining_discoveries += other.first_mining_discoveries;
        self.first_map_object_discoveries += other.first_map_object_discoveries;
    }
}
