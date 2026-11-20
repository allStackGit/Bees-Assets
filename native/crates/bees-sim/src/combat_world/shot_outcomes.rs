//! Unity RlProjectileNearMissTelemetry root-shot lifetime bookkeeping.
//! A real policy turret launch creates a root. Rocket explosions and
//! splitter children increment its live continuation count before their
//! parent ends; exactly one completion is emitted for the final child.
//! The separate physical-nearness detector marks verified near passes.

use std::collections::{BTreeMap, HashMap};

/// Source RlOneVsOneReward.NearMissDistance.
pub(super) const NEAR_MISS_DISTANCE: f32 = 2.0;

#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct ProjectileRootOutcome {
    pub root_shot_id: i64,
    pub shooter_id: i64,
    pub shooter_side: i32,
    pub enemy_contact: bool,
    pub near_enemy: bool,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
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

#[derive(Clone, Copy, Debug, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
struct ActiveRoot {
    shooter_id: i64,
    shooter_side: i32,
    live: usize,
    contact: bool,
    near: bool,
}

/// Persist the one authoritative policy root-shot ledger, including
/// continuation ownership. This does not generate any new physics shot or
/// training reward; the corresponding projectile bodies are restored by
/// the shared CombatWorld at the same fixed-step boundary.
#[derive(serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct ProjectileOutcomeCheckpoint {
    schema_version: u32,
    roots: BTreeMap<i64, ActiveRoot>,
    totals: ProjectileOutcomeMeasurements,
}

#[derive(Default)]
pub(super) struct ProjectileOutcomeTracker {
    active: HashMap<i64, ActiveRoot>,
    totals: ProjectileOutcomeMeasurements,
}

impl ProjectileOutcomeTracker {
    pub(super) fn checkpoint(&self) -> ProjectileOutcomeCheckpoint {
        ProjectileOutcomeCheckpoint {
            schema_version: 1,
            roots: self.active.iter().map(|(&id, &root)| (id, root)).collect(),
            totals: ProjectileOutcomeMeasurements {
                in_flight_roots: self.active.len(),
                ..self.totals
            },
        }
    }

    pub(super) fn restore_checkpoint(
        &mut self, checkpoint: ProjectileOutcomeCheckpoint,
    ) -> Result<(), &'static str> {
        if checkpoint.schema_version != 1 ||
            checkpoint.roots.len() != checkpoint.totals.in_flight_roots ||
            checkpoint.totals.contacted > checkpoint.totals.completed ||
            checkpoint.totals.completed !=
                checkpoint.totals.contacted.saturating_add(checkpoint.totals.missed) ||
            checkpoint.totals.near_missed > checkpoint.totals.missed ||
            checkpoint.totals.completed
                .checked_add(checkpoint.roots.len() as u64)
                .is_none_or(|count| count > checkpoint.totals.launched) ||
            checkpoint.roots.iter().any(|(&root_id, state)|
                root_id <= 0 || state.shooter_id <= 0 ||
                state.shooter_side <= 0 || state.live == 0)
        {
            return Err("invalid native projectile root outcome checkpoint");
        }
        // No root may be counted completed while its split/explosion
        // continuations still exist. Restore only the original owners;
        // the paired physical projectile identities are checked by the
        // enclosing CombatWorld before resuming game or training ticks.
        self.active = checkpoint.roots.into_iter().collect();
        self.totals = checkpoint.totals;
        Ok(())
    }

    /// A tracked root's continuation count must equal the active physical
    /// projectile bodies carrying its original immutable attribution.
    /// Game-controlled (untracked) roots are deliberately excluded.
    pub(super) fn validate_active_projectiles(
        &self, projectiles: &[crate::physics::ProjectileBody],
    ) -> Result<(), &'static str> {
        let mut observed = HashMap::<i64, usize>::new();
        for projectile in projectiles.iter().filter(|projectile| projectile.active) {
            let root = projectile.motion.attribution.root_shot_id;
            if self.active.contains_key(&root) {
                *observed.entry(root).or_insert(0) += 1;
            }
        }
        if self.active.iter().any(|(&root, owner)|
            observed.get(&root).copied().unwrap_or(0) != owner.live
        ) {
            return Err("native policy projectile continuations disagree with physical shot ownership");
        }
        Ok(())
    }

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
