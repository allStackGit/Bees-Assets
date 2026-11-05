//! Unity RlProjectileNearMissTelemetry root-shot lifetime bookkeeping.
//! A real policy turret launch creates a root. Rocket explosions and
//! splitter children increment its live continuation count before their
//! parent ends; exactly one completion is emitted for the final child.
//! The separate physical-nearness detector marks verified near passes.

use std::collections::HashMap;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ProjectileRootOutcome {
    pub root_shot_id: i64,
    pub shooter_id: i64,
    pub shooter_side: i32,
    pub enemy_contact: bool,
    pub near_enemy: bool,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ProjectileOutcomeMeasurements {
    pub launched: u64,
    pub continuations: u64,
    pub completed: u64,
    pub contacted: u64,
    pub missed: u64,
    pub near_missed: u64,
    pub in_flight_roots: usize,
    pub near_grid_rebuilds: u64,
    pub near_broadphase_candidates: u64,
    pub near_hull_checks: u64,
}

#[derive(Clone, Copy, Debug)]
struct ActiveRoot {
    shooter_id: i64,
    shooter_side: i32,
    live: usize,
    contact: bool,
    near: bool,
}

#[derive(Default)]
pub(super) struct ProjectileOutcomeTracker {
    active: HashMap<i64, ActiveRoot>,
    totals: ProjectileOutcomeMeasurements,
}

impl ProjectileOutcomeTracker {
    pub(super) fn launched(&mut self, root: i64, shooter_id: i64, side: i32) {
        debug_assert!(root > 0 && shooter_id > 0 && side > 0);
        let replaced = self.active.insert(root, ActiveRoot {
            shooter_id, shooter_side: side, live: 1,
            contact: false, near: false,
        });
        debug_assert!(replaced.is_none(), "monotonic projectile root IDs");
        self.totals.launched += 1;
    }

    /// Called after the actual child has been accepted by CombatWorld.
    /// Only continuations of a tracked policy root participate.
    pub(super) fn continued(&mut self, root: i64) {
        if let Some(shot) = self.active.get_mut(&root) {
            shot.live += 1;
            self.totals.continuations += 1;
        }
    }

    pub(super) fn enemy_contact(&mut self, root: i64) {
        if let Some(shot) = self.active.get_mut(&root) {
            shot.contact = true;
        }
    }

    pub(super) fn record_ship_grid_rebuild(&mut self) {
        self.totals.near_grid_rebuilds += 1;
    }

    pub(super) fn record_near_candidates(&mut self, count: usize) {
        self.totals.near_broadphase_candidates += count as u64;
    }

    pub(super) fn record_near_hull_check(&mut self) {
        self.totals.near_hull_checks += 1;
    }

    pub(super) fn near_enemy(&mut self, root: i64) {
        if let Some(shot) = self.active.get_mut(&root) {
            if !shot.contact { shot.near = true; }
        }
    }

    pub(super) fn needs_nearness(&self, root: i64) -> bool {
        self.active.get(&root).is_some_and(|shot|
            !shot.contact && !shot.near
        )
    }

    /// Finalize only after every explosion/split continuation ended. A
    /// root that hit at least one enemy is a contact, even if shot damage
    /// was harmless; a physical near pass is a miss only without contact.
    pub(super) fn ended(&mut self, root: i64) -> Option<ProjectileRootOutcome> {
        let active = self.active.get_mut(&root)?;
        debug_assert!(active.live > 0);
        active.live -= 1;
        if active.live > 0 { return None; }
        let shot = self.active.remove(&root)
            .expect("validated final active projectile");
        let result = ProjectileRootOutcome {
            root_shot_id: root,
            shooter_id: shot.shooter_id,
            shooter_side: shot.shooter_side,
            enemy_contact: shot.contact,
            near_enemy: shot.near,
        };
        self.totals.completed += 1;
        if result.enemy_contact {
            self.totals.contacted += 1;
        } else {
            self.totals.missed += 1;
            self.totals.near_missed += u64::from(result.near_enemy);
        }
        Some(result)
    }

    pub(super) fn measurements(&self) -> ProjectileOutcomeMeasurements {
        ProjectileOutcomeMeasurements {
            in_flight_roots: self.active.len(),
            ..self.totals
        }
    }
}
