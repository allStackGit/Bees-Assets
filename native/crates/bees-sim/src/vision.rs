//! Source-grounded Hive Mind vision inputs and cost measurements.
//!
//! Unity: HiveMindVision.CanSee and GameState.RefreshHiveMindMapObjectVision.
//! The shared CombatWorld refreshes live ship and obstacle discoveries with
//! its authoritative collider geometry and world-owned learned caches.
//! MapObjects are not yet modeled in CombatWorld and remain an explicit
//! gameplay adapter responsibility; their sightings must not be fabricated.

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

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct VisionSweepCounts {
    pub completed_sweeps: u64,
    pub suppressed_sweeps: u64,
    pub active_observers: u64,
    pub live_ship_pairs: u64,
    pub unlearned_obstacle_pairs: u64,
    pub aabb_rejections: u64,
    pub exact_collider_tests: u64,
    pub visible_enemy_pairs: u64,
    pub first_ship_discoveries: u64,
    pub first_obstacle_discoveries: u64,
    pub first_mining_discoveries: u64,
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

    pub fn merge(&mut self, other: &Self) {
        self.completed_sweeps += other.completed_sweeps;
        self.suppressed_sweeps += other.suppressed_sweeps;
        self.active_observers += other.active_observers;
        self.live_ship_pairs += other.live_ship_pairs;
        self.unlearned_obstacle_pairs += other.unlearned_obstacle_pairs;
        self.aabb_rejections += other.aabb_rejections;
        self.exact_collider_tests += other.exact_collider_tests;
        self.visible_enemy_pairs += other.visible_enemy_pairs;
        self.first_ship_discoveries += other.first_ship_discoveries;
        self.first_obstacle_discoveries += other.first_obstacle_discoveries;
        self.first_mining_discoveries += other.first_mining_discoveries;
    }
}
