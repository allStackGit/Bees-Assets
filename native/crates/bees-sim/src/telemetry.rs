//! Low-cost aggregate simulation measurements. The sampler deliberately does
//! not store per-step histories; dashboard/export code can consume snapshots.

use std::time::Duration;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct StepCounts {
    pub decisions: u64,
    /// One completed fixed tick's HiveMind vision work. Tick-zero scene
    /// setup is deliberately excluded from per-step workload totals.
    pub vision: crate::vision::VisionSweepCounts,
    pub active_ships: u32,
    pub active_projectiles: u32,
    pub events: u64,
    /// Physical HitEvent callbacks, not launched shots or estimated damage.
    pub enemy_hits: u64,
    pub friendly_hits: u64,
    /// Actual TSV removed by attributed hits. Unattributed collisions and
    /// economic rewards are deliberately excluded.
    pub enemy_hit_tsv_loss: u64,
    pub friendly_hit_tsv_loss: u64,
    /// Real damage with no valid shooter; never included in attacker TSV.
    pub unattributed_damage_events: u64,
    pub unattributed_damage_tsv_loss: u64,
    /// Final policy projectile roots, counted once after all continuations.
    pub policy_roots_completed: u64,
    pub policy_roots_contacted: u64,
    pub policy_roots_near_missed: u64,
    /// Physics already computes these counts during each fixed step.
    /// Export them without extra collision queries or clock reads.
    pub integrated_bodies: u64,
    pub broadphase_candidates: u64,
    pub ship_grid_overflow_bodies: u64,
    pub obstacle_grid_overflow_bodies: u64,
    /// Shared spatial-grid rebuilds split by population, not inferred from
    /// broadphase candidate counts. No additional collision query is run.
    pub ship_grid_rebuilds: u64,
    pub obstacle_grid_rebuilds: u64,
    pub narrowphase_tests: u64,
    pub trigger_enters: u64,
    pub automatic_weapon_target_queries: u64,
    pub automatic_weapon_queue_builds: u64,
    pub automatic_weapon_distance_queries: u64,
    pub special_weapon_grid_rebuilds: u64,
    pub special_weapon_candidates: u64,
    pub special_weapon_trigger_hull_tests: u64,
    pub special_weapon_trigger_entries: u64,
    pub special_weapon_trigger_exits: u64,
    pub special_weapon_body_hull_tests: u64,
    pub special_weapon_body_entries: u64,
    pub automatic_weapon_shots: u64,
    /// Authored RangeCollider work; separate from projectile collision pairs.
    pub weapon_range_grid_rebuilds: u64,
    pub weapon_range_candidates: u64,
    pub weapon_range_exact_tests: u64,
    /// MapObject checks use the existing obstacle broadphase, not a second index.
    pub weapon_range_map_object_grid_rebuilds: u64,
    pub weapon_range_map_object_candidates: u64,
    pub weapon_range_map_object_exact_tests: u64,
    pub weapon_range_projectile_tests: u64,
    pub weapon_range_enters: u64,
    pub weapon_range_exits: u64,
    pub weapon_range_projectile_exits: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Measurements {
    pub ticks: u64,
    pub decisions: u64,
    /// Cumulative successful fixed-step vision work across episode resets.
    pub vision: crate::vision::VisionSweepCounts,
    pub events: u64,
    pub enemy_hits: u64,
    pub friendly_hits: u64,
    pub enemy_hit_tsv_loss: u64,
    pub friendly_hit_tsv_loss: u64,
    pub unattributed_damage_events: u64,
    pub unattributed_damage_tsv_loss: u64,
    pub policy_roots_completed: u64,
    pub policy_roots_contacted: u64,
    pub policy_roots_near_missed: u64,
    pub integrated_bodies: u64,
    pub broadphase_candidates: u64,
    pub ship_grid_overflow_bodies: u64,
    pub obstacle_grid_overflow_bodies: u64,
    /// Shared spatial-grid rebuilds split by population, not inferred from
    /// broadphase candidate counts. No additional collision query is run.
    pub ship_grid_rebuilds: u64,
    pub obstacle_grid_rebuilds: u64,
    pub narrowphase_tests: u64,
    pub trigger_enters: u64,
    pub automatic_weapon_target_queries: u64,
    pub automatic_weapon_queue_builds: u64,
    pub automatic_weapon_distance_queries: u64,
    pub special_weapon_grid_rebuilds: u64,
    pub special_weapon_candidates: u64,
    pub special_weapon_trigger_hull_tests: u64,
    pub special_weapon_trigger_entries: u64,
    pub special_weapon_trigger_exits: u64,
    pub special_weapon_body_hull_tests: u64,
    pub special_weapon_body_entries: u64,
    pub automatic_weapon_shots: u64,
    /// Authored RangeCollider work; separate from projectile collision pairs.
    pub weapon_range_grid_rebuilds: u64,
    pub weapon_range_candidates: u64,
    pub weapon_range_exact_tests: u64,
    /// MapObject checks use the existing obstacle broadphase, not a second index.
    pub weapon_range_map_object_grid_rebuilds: u64,
    pub weapon_range_map_object_candidates: u64,
    pub weapon_range_map_object_exact_tests: u64,
    pub weapon_range_projectile_tests: u64,
    pub weapon_range_enters: u64,
    pub weapon_range_exits: u64,
    pub weapon_range_projectile_exits: u64,
    /// Rules updates that returned an error and invalidated their world.
    pub failed_steps: u64,
    /// Sampled failed updates are accounted separately so unsuccessful
    /// frames cannot bias successful-step throughput or latency percentiles.
    pub sampled_failed_steps: u64,
    pub sampled_failed_wall_nanos: u128,
    pub sampled_ticks: u64,
    pub sampled_wall_nanos: u128,
    /// Log2 nanosecond histogram, recorded only on opted-in sampled steps.
    /// Values are counts, not timings from every individual tick.
    /// Exporting it permits approximate p50/p95/p99 without per-tick logs.
    pub sampled_step_latency_log2_ns: [u64; 64],
    pub last_active_ships: u32,
    pub last_active_projectiles: u32,
}

// Long fixed-size arrays do not implement Default on all supported Rust
// toolchains. Keep the zero state explicit and allocation-free.
impl Default for Measurements {
    fn default() -> Self {
        Self {
            ticks: 0,
            decisions: 0,
            vision: crate::vision::VisionSweepCounts::default(),
            events: 0,
            enemy_hits: 0,
            friendly_hits: 0,
            enemy_hit_tsv_loss: 0,
            friendly_hit_tsv_loss: 0,
            unattributed_damage_events: 0,
            unattributed_damage_tsv_loss: 0,
            policy_roots_completed: 0,
            policy_roots_contacted: 0,
            policy_roots_near_missed: 0,
            integrated_bodies: 0,
            broadphase_candidates: 0,
            ship_grid_overflow_bodies: 0,
            obstacle_grid_overflow_bodies: 0,
            ship_grid_rebuilds: 0,
            obstacle_grid_rebuilds: 0,
            narrowphase_tests: 0,
            trigger_enters: 0,
            automatic_weapon_target_queries: 0,
            automatic_weapon_queue_builds: 0,
            automatic_weapon_distance_queries: 0,
            special_weapon_grid_rebuilds: 0,
            special_weapon_candidates: 0,
            special_weapon_trigger_hull_tests: 0,
            special_weapon_trigger_entries: 0,
            special_weapon_trigger_exits: 0,
            special_weapon_body_hull_tests: 0,
            special_weapon_body_entries: 0,
            automatic_weapon_shots: 0,
            weapon_range_grid_rebuilds: 0,
            weapon_range_candidates: 0,
            weapon_range_exact_tests: 0,
            weapon_range_map_object_grid_rebuilds: 0,
            weapon_range_map_object_candidates: 0,
            weapon_range_map_object_exact_tests: 0,
            weapon_range_projectile_tests: 0,
            weapon_range_enters: 0,
            weapon_range_exits: 0,
            weapon_range_projectile_exits: 0,
            failed_steps: 0,
            sampled_failed_steps: 0,
            sampled_failed_wall_nanos: 0,
            sampled_ticks: 0,
            sampled_wall_nanos: 0,
            sampled_step_latency_log2_ns: [0; 64],
            last_active_ships: 0,
            last_active_projectiles: 0,
        }
    }
}

impl Measurements {
    #[inline]
    pub fn record(&mut self, counts: StepCounts, sampled: Option<Duration>) {
        self.ticks += 1;
        self.decisions += counts.decisions;
        self.vision.merge(&counts.vision);
        self.events += counts.events;
        self.enemy_hits += counts.enemy_hits;
        self.friendly_hits += counts.friendly_hits;
        self.enemy_hit_tsv_loss += counts.enemy_hit_tsv_loss;
        self.friendly_hit_tsv_loss += counts.friendly_hit_tsv_loss;
        self.unattributed_damage_events += counts.unattributed_damage_events;
        self.unattributed_damage_tsv_loss += counts.unattributed_damage_tsv_loss;
        self.policy_roots_completed += counts.policy_roots_completed;
        self.policy_roots_contacted += counts.policy_roots_contacted;
        self.policy_roots_near_missed += counts.policy_roots_near_missed;
        self.integrated_bodies += counts.integrated_bodies;
        self.broadphase_candidates += counts.broadphase_candidates;
        self.ship_grid_overflow_bodies += counts.ship_grid_overflow_bodies;
        self.obstacle_grid_overflow_bodies += counts.obstacle_grid_overflow_bodies;
        self.ship_grid_rebuilds += counts.ship_grid_rebuilds;
        self.obstacle_grid_rebuilds += counts.obstacle_grid_rebuilds;
        self.narrowphase_tests += counts.narrowphase_tests;
        self.trigger_enters += counts.trigger_enters;
        self.automatic_weapon_target_queries += counts.automatic_weapon_target_queries;
        self.automatic_weapon_queue_builds += counts.automatic_weapon_queue_builds;
        self.automatic_weapon_distance_queries += counts.automatic_weapon_distance_queries;
        self.special_weapon_grid_rebuilds += counts.special_weapon_grid_rebuilds;
        self.special_weapon_candidates += counts.special_weapon_candidates;
        self.special_weapon_trigger_hull_tests += counts.special_weapon_trigger_hull_tests;
        self.special_weapon_trigger_entries += counts.special_weapon_trigger_entries;
        self.special_weapon_trigger_exits += counts.special_weapon_trigger_exits;
        self.special_weapon_body_hull_tests += counts.special_weapon_body_hull_tests;
        self.special_weapon_body_entries += counts.special_weapon_body_entries;
        self.automatic_weapon_shots += counts.automatic_weapon_shots;
        self.weapon_range_grid_rebuilds += counts.weapon_range_grid_rebuilds;
        self.weapon_range_candidates += counts.weapon_range_candidates;
        self.weapon_range_exact_tests += counts.weapon_range_exact_tests;
        self.weapon_range_map_object_grid_rebuilds += counts.weapon_range_map_object_grid_rebuilds;
        self.weapon_range_map_object_candidates += counts.weapon_range_map_object_candidates;
        self.weapon_range_map_object_exact_tests += counts.weapon_range_map_object_exact_tests;
        self.weapon_range_projectile_tests += counts.weapon_range_projectile_tests;
        self.weapon_range_enters += counts.weapon_range_enters;
        self.weapon_range_exits += counts.weapon_range_exits;
        self.weapon_range_projectile_exits += counts.weapon_range_projectile_exits;
        self.last_active_ships = counts.active_ships;
        self.last_active_projectiles = counts.active_projectiles;
        if let Some(duration) = sampled {
            let nanos = duration.as_nanos();
            self.sampled_ticks += 1;
            self.sampled_wall_nanos += nanos;
            // A log2 bucket is one leading-zero operation and an increment.
            // No sampling-time allocation or sort is required.
            let index = if nanos == 0 { 0 } else {
                (u128::BITS - 1 - nanos.leading_zeros()) as usize
            }.min(self.sampled_step_latency_log2_ns.len() - 1);
            self.sampled_step_latency_log2_ns[index] += 1;
        }
    }

    #[inline]
    pub fn record_failure(&mut self, sampled: Option<Duration>) {
        self.failed_steps += 1;
        if let Some(duration) = sampled {
            self.sampled_failed_steps += 1;
            self.sampled_failed_wall_nanos += duration.as_nanos();
        }
    }

    /// Aggregate independent world/worker windows in a deterministic order.
    /// This has no hot-path overhead: callers merge once per rollout window.
    /// A snapshot's last active counts describe the final contributing
    /// successful step, not the sum of all currently live worlds.
    pub fn merge(&mut self, other: &Self) {
        self.ticks += other.ticks;
        self.decisions += other.decisions;
        self.vision.merge(&other.vision);
        self.events += other.events;
        self.enemy_hits += other.enemy_hits;
        self.friendly_hits += other.friendly_hits;
        self.enemy_hit_tsv_loss += other.enemy_hit_tsv_loss;
        self.friendly_hit_tsv_loss += other.friendly_hit_tsv_loss;
        self.unattributed_damage_events += other.unattributed_damage_events;
        self.unattributed_damage_tsv_loss += other.unattributed_damage_tsv_loss;
        self.policy_roots_completed += other.policy_roots_completed;
        self.policy_roots_contacted += other.policy_roots_contacted;
        self.policy_roots_near_missed += other.policy_roots_near_missed;
        self.integrated_bodies += other.integrated_bodies;
        self.broadphase_candidates += other.broadphase_candidates;
        self.ship_grid_overflow_bodies += other.ship_grid_overflow_bodies;
        self.obstacle_grid_overflow_bodies += other.obstacle_grid_overflow_bodies;
        self.ship_grid_rebuilds += other.ship_grid_rebuilds;
        self.obstacle_grid_rebuilds += other.obstacle_grid_rebuilds;
        self.narrowphase_tests += other.narrowphase_tests;
        self.trigger_enters += other.trigger_enters;
        self.automatic_weapon_target_queries += other.automatic_weapon_target_queries;
        self.automatic_weapon_queue_builds += other.automatic_weapon_queue_builds;
        self.automatic_weapon_distance_queries += other.automatic_weapon_distance_queries;
        self.special_weapon_grid_rebuilds += other.special_weapon_grid_rebuilds;
        self.special_weapon_candidates += other.special_weapon_candidates;
        self.special_weapon_trigger_hull_tests += other.special_weapon_trigger_hull_tests;
        self.special_weapon_trigger_entries += other.special_weapon_trigger_entries;
        self.special_weapon_trigger_exits += other.special_weapon_trigger_exits;
        self.special_weapon_body_hull_tests += other.special_weapon_body_hull_tests;
        self.special_weapon_body_entries += other.special_weapon_body_entries;
        self.automatic_weapon_shots += other.automatic_weapon_shots;
        self.weapon_range_grid_rebuilds += other.weapon_range_grid_rebuilds;
        self.weapon_range_candidates += other.weapon_range_candidates;
        self.weapon_range_exact_tests += other.weapon_range_exact_tests;
        self.weapon_range_map_object_grid_rebuilds += other.weapon_range_map_object_grid_rebuilds;
        self.weapon_range_map_object_candidates += other.weapon_range_map_object_candidates;
        self.weapon_range_map_object_exact_tests += other.weapon_range_map_object_exact_tests;
        self.weapon_range_projectile_tests += other.weapon_range_projectile_tests;
        self.weapon_range_enters += other.weapon_range_enters;
        self.weapon_range_exits += other.weapon_range_exits;
        self.weapon_range_projectile_exits += other.weapon_range_projectile_exits;
        self.failed_steps += other.failed_steps;
        self.sampled_failed_steps += other.sampled_failed_steps;
        self.sampled_failed_wall_nanos += other.sampled_failed_wall_nanos;
        self.sampled_ticks += other.sampled_ticks;
        self.sampled_wall_nanos += other.sampled_wall_nanos;
        for (total, &count) in self.sampled_step_latency_log2_ns.iter_mut()
            .zip(other.sampled_step_latency_log2_ns.iter()) {
            *total += count;
        }
        if other.ticks > 0 {
            self.last_active_ships = other.last_active_ships;
            self.last_active_projectiles = other.last_active_projectiles;
        }
    }

    pub fn sampled_ticks_per_second(&self) -> Option<f64> {
        if self.sampled_wall_nanos == 0 {
            return None;
        }
        Some(self.sampled_ticks as f64 * 1e9 / self.sampled_wall_nanos as f64)
    }

    /// Approximate sampled step latency: upper inclusive nanosecond bound
    /// of the logarithmic bucket containing the requested percentile.
    /// This is deliberately not presented as an exact percentile.
    pub fn sampled_step_percentile_upper_nanos(&self, percentile: u32) -> Option<u128> {
        if self.sampled_ticks == 0 || !(1..=100).contains(&percentile) {
            return None;
        }
        let rank = (u128::from(self.sampled_ticks) *
            u128::from(percentile) + 99) / 100;
        let mut seen = 0_u128;
        for (bucket, &count) in self.sampled_step_latency_log2_ns.iter().enumerate() {
            seen += u128::from(count);
            if seen >= rank {
                return Some((1_u128 << (bucket + 1)) - 1);
            }
        }
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sampled_latency_and_physics_work_are_bounded_aggregates() {
        let mut measurements = Measurements::default();
        measurements.record(StepCounts {
            active_ships: 2, active_projectiles: 7,
            integrated_bodies: 11, broadphase_candidates: 22,
            narrowphase_tests: 5, trigger_enters: 3,
            ..StepCounts::default()
        }, Some(Duration::from_nanos(100)));
        measurements.record(StepCounts {
            active_ships: 3, integrated_bodies: 15,
            broadphase_candidates: 9, narrowphase_tests: 2,
            ..StepCounts::default()
        }, Some(Duration::from_nanos(1000)));
        assert_eq!(measurements.ticks, 2);
        assert_eq!(measurements.integrated_bodies, 26);
        assert_eq!(measurements.broadphase_candidates, 31);
        assert_eq!(measurements.narrowphase_tests, 7);
        assert_eq!(measurements.trigger_enters, 3);
        assert_eq!(measurements.last_active_ships, 3);
        assert_eq!(measurements.sampled_ticks, 2);
        assert_eq!(measurements.sampled_step_percentile_upper_nanos(50), Some(127));
        assert_eq!(measurements.sampled_step_percentile_upper_nanos(95), Some(1023));
        assert_eq!(measurements.sampled_step_percentile_upper_nanos(0), None);
    }
}
