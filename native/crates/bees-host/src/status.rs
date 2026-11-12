//! Snapshot-to-snapshot host telemetry, independent of UI and training.
//!
//! Capture after a bounded rollout window, not inside each agent or fixed
//! step. All counters come from the existing simulation/model/connection
//! owners; no second tracker and no per-step event log are maintained.

use std::time::{Instant, SystemTime, UNIX_EPOCH};

use bees_gameplay::level_host::{NativeLevelWindowCounts, NativeRuntimeStatus};
use serde::Serialize;

pub const STATUS_SCHEMA_VERSION: u32 = 1;

/// A JSON-friendly instantaneous view; backlog counts are gauges rather
/// than deltas. The game and worker can publish the same schema.
#[derive(Clone, Copy, Debug, Serialize)]
pub struct NativeHostGauges {
    pub worlds_total: usize,
    pub worlds_running: usize,
    pub worlds_completed: usize,
    pub worlds_aborted: usize,
    pub worlds_failed: usize,
    pub ships_living: usize,
    pub commands_active: usize,
    /// Current occupancy and per-world retained bucket pools summed across
    /// worlds, sampled at the same cadence as all other host gauges.
    pub spatial_grid_active_buckets: usize,
    pub spatial_grid_retained_buckets: usize,
    /// Live episode-local physical special effects (all controllers).
    /// Snapshot gauges, not cross-episode rates or host lifetime totals.
    pub special_scout_beacons_spawned: u64,
    pub special_fire_barge_self_detonations: u64,
    pub special_game_barge_charges_started: u64,
    pub special_game_barge_charge_hits: u64,
    pub special_game_barge_charge_completions: u64,
    pub player_chase: bees_gameplay::level_rules::NativePlayerChaseMeasurements,
    /// Current episodes' cumulative vision cost, not a process-lifetime
    /// rate. After episode reset these counts restart from zero.
    pub vision: bees_sim::vision::VisionSweepCounts,
    pub navigation_jobs_completed: u64,
    pub notifications_queued: usize,
    pub squad_retirements_queued: usize,
    pub hivemind_requests_pending: usize,
    pub store_commands_pending: usize,
    pub store_reconciliations_pending: usize,
    /// Per-episode game-owned asteroid setup/pooling, sampled without scanning
    /// physical objects or retaining a history of spawn events.
    pub full_asteroids_active: usize,
    pub full_asteroid_profiles_cached: usize,
    pub full_asteroid_profiles_created: u64,
    pub full_asteroid_profiles_reused: u64,
    pub full_asteroid_destination_draws: u64,
    pub full_asteroid_destination_exhaustions: u64,
}

impl From<&NativeRuntimeStatus> for NativeHostGauges {
    fn from(value: &NativeRuntimeStatus) -> Self {
        Self {
            worlds_total: value.total_worlds,
            worlds_running: value.running_worlds,
            worlds_completed: value.completed_worlds,
            worlds_aborted: value.aborted_worlds,
            worlds_failed: value.failed_worlds,
            ships_living: value.living_ships,
            commands_active: value.active_commands,
            spatial_grid_active_buckets: value.spatial_grid_active_buckets,
            spatial_grid_retained_buckets: value.spatial_grid_retained_buckets,
            special_scout_beacons_spawned: value.special_scout_beacons_spawned,
            special_fire_barge_self_detonations: value.special_fire_barge_self_detonations,
            special_game_barge_charges_started: value.special_game_barge_charges_started,
            special_game_barge_charge_hits: value.special_game_barge_charge_hits,
            special_game_barge_charge_completions: value.special_game_barge_charge_completions,
            player_chase: value.player_chase,
            vision: value.vision,
            navigation_jobs_completed: value.navigation_jobs_completed,
            notifications_queued: value.queued_notifications,
            squad_retirements_queued: value.queued_squad_retirements,
            hivemind_requests_pending: value.pending_hivemind_requests,
            store_commands_pending: value.pending_store_commands,
            store_reconciliations_pending: value.pending_store_reconciliations,
            full_asteroids_active: value.full_asteroids_active,
            full_asteroid_profiles_cached: value.full_asteroid_profiles_cached,
            full_asteroid_profiles_created: value.full_asteroid_profiles_created,
            full_asteroid_profiles_reused: value.full_asteroid_profiles_reused,
            full_asteroid_destination_draws: value.full_asteroid_destination_draws,
            full_asteroid_destination_exhaustions:
                value.full_asteroid_destination_exhaustions,
        }
    }
}

/// Interval rates are measured against actual host monotonic wall time, NOT
/// model launch time, sampled physics durations or a guessed tick frequency.
/// Sampled timing cannot imply full-run wall time or accelerator utilization.
#[derive(Clone, Debug, Serialize)]
pub struct NativeHostStatusInterval {
    pub schema_version: u32,
    pub observed_at_unix_ms: Option<u64>,
    pub elapsed_seconds: f64,
    pub world_steps: u64,
    pub world_steps_per_second: f64,
    /// Actual fixed-step vision work across all worlds in this status
    /// interval. Unlike per-episode gauges, survives clean episode resets.
    pub vision_step_work: bees_sim::vision::VisionSweepCounts,
    pub model_rows: u64,
    pub model_rows_per_second: f64,
    /// Successfully validated model rows with authenticated MA-POCA
    /// participants; not inferred from live ship identities.
    pub grouped_actor_rows: u64,
    /// Current resident policy generation, or absent before the first
    /// successfully validated model batch. It is process/run-local.
    pub last_model_generation: Option<u64>,
    pub model_calls: u64,
    pub model_backend_failures: u64,
    pub invalid_model_batches: u64,
    pub game_failed_steps: u64,
    /// Physical hits (not attempts), separated by relative faction.
    pub enemy_hits: u64,
    pub friendly_hits: u64,
    /// Attributed ship TSV damage; no estimated health-to-TSV conversion.
    pub enemy_hit_tsv_loss: u64,
    pub friendly_hit_tsv_loss: u64,
    pub unattributed_damage_events: u64,
    pub unattributed_damage_tsv_loss: u64,
    /// Finalized policy projectile roots, not individual split children.
    pub policy_roots_completed: u64,
    pub policy_roots_contacted: u64,
    pub policy_roots_near_missed: u64,
    pub physics_integrated_bodies: u64,
    pub physics_broadphase_candidates: u64,
    pub physics_ship_grid_overflow_bodies: u64,
    pub physics_obstacle_grid_overflow_bodies: u64,
    /// Actual shared-grid rebuilds in the native simulation window.
    pub physics_ship_grid_rebuilds: u64,
    pub physics_obstacle_grid_rebuilds: u64,
    /// Actual occupied cell insertions across the shared grid's rebuilds.
    pub physics_ship_grid_cells_indexed: u64,
    pub physics_obstacle_grid_cells_indexed: u64,
    pub physics_narrowphase_tests: u64,
    pub physics_trigger_enters: u64,
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
    pub weapon_range_grid_rebuilds: u64,
    pub weapon_range_candidates: u64,
    pub weapon_range_exact_tests: u64,
    pub weapon_range_map_object_grid_rebuilds: u64,
    pub weapon_range_map_object_candidates: u64,
    pub weapon_range_map_object_exact_tests: u64,
    pub weapon_range_projectile_tests: u64,
    pub weapon_range_enters: u64,
    pub weapon_range_exits: u64,
    pub weapon_range_projectile_exits: u64,
    /// Number of successful sampled fixed steps in this interval.
    pub sampled_physics_steps: u64,
    /// Approximate upper nanosecond bound of the sampled p95 bucket.
    pub sampled_physics_p95_upper_nanos: Option<u128>,
    pub sampled_model_forward_calls: u64,
    pub sampled_model_forward_rows: u64,
    /// Only valid if the policy backend synchronizes before returning.
    pub sampled_model_forward_nanos_per_row: Option<f64>,
    /// Completed rollout windows only, accumulated between status samples.
    /// These raw host-wall durations are from selectively sampled rounds,
    /// not estimates of total elapsed time or accelerator utilization.
    pub sampled_rollout_phase_rounds: u64,
    pub sampled_rollout_world_steps: u64,
    pub sampled_rollout_model_rows: u64,
    pub sampled_observation_build_nanos: u128,
    pub sampled_inference_and_validation_nanos: u128,
    pub sampled_world_dispatch_and_step_nanos: u128,
    pub gauges: NativeHostGauges,
}

/// One previous status plus a monotonic clock, not a history buffer.
/// A counter reset (new process or simulation) starts a new baseline.
#[derive(Clone, Copy, Default)]
struct PhaseSamples {
    rounds: u64,
    world_steps: u64,
    model_rows: u64,
    observation_nanos: u128,
    inference_nanos: u128,
    dispatch_nanos: u128,
}

pub struct NativeStatusSampler {
    previous: NativeRuntimeStatus,
    when: Instant,
    pending_phases: PhaseSamples,
}

impl NativeStatusSampler {
    pub fn new(initial: NativeRuntimeStatus) -> Self {
        Self {
            previous: initial,
            when: Instant::now(),
            pending_phases: PhaseSamples::default(),
        }
    }

    /// Called by the host after an entire native rollout window succeeds.
    /// Do not include failed partial windows as completed measurements.
    pub fn record_window(&mut self, counts: &NativeLevelWindowCounts) {
        let pending = &mut self.pending_phases;
        pending.rounds += u64::from(counts.sampled_rounds);
        pending.world_steps += counts.sampled_world_steps as u64;
        pending.model_rows += counts.sampled_policy_rows as u64;
        pending.observation_nanos += counts.sampled_observation_build_nanos;
        pending.inference_nanos += counts.sampled_inference_and_validation_nanos;
        pending.dispatch_nanos += counts.sampled_world_dispatch_and_step_nanos;
    }

    pub fn sample(&mut self, current: NativeRuntimeStatus)
        -> Option<NativeHostStatusInterval>
    {
        let now = Instant::now();
        let elapsed = now.duration_since(self.when).as_secs_f64();
        if elapsed == 0.0 {
            return None;
        }
        let previous = self.previous;
        let phases = self.pending_phases;
        self.pending_phases = PhaseSamples::default();
        self.previous = current;
        self.when = now;

        // Sample clocks and counters are cumulative. If any reset or
        // shrink, do not fabricate a delta rate across two generations.
        let diff = |now: u64, before: u64| now.checked_sub(before);
        let (s, p) = (&current.simulation, &previous.simulation);
        let (m, q) = (&current.inference, &previous.inference);
        let build = || {
            let world_steps = diff(s.ticks, p.ticks)?;
            let model_rows = diff(m.model_rows, q.model_rows)?;
            let sampled_physics_steps = diff(s.sampled_ticks, p.sampled_ticks)?;
            let sampled_model_forward_rows = diff(
                m.model_forward_timed_rows, q.model_forward_timed_rows,
            )?;
            let model_forward_nanos = m.model_forward_nanos
                .checked_sub(q.model_forward_nanos)?;
            let mut histogram = [0_u64; 64];
            for ((out, &new), &old) in histogram.iter_mut()
                .zip(s.sampled_step_latency_log2_ns.iter())
                .zip(p.sampled_step_latency_log2_ns.iter())
            {
                *out = new.checked_sub(old)?;
            }
            Some(NativeHostStatusInterval {
                schema_version: STATUS_SCHEMA_VERSION,
                observed_at_unix_ms: SystemTime::now()
                    .duration_since(UNIX_EPOCH).ok()
                    .and_then(|duration| duration.as_millis().try_into().ok()),
                elapsed_seconds: elapsed,
                world_steps,
                world_steps_per_second: world_steps as f64 / elapsed,
                vision_step_work: s.vision.checked_delta(p.vision)?,
                model_rows,
                model_rows_per_second: model_rows as f64 / elapsed,
                grouped_actor_rows: diff(
                    m.grouped_actor_rows, q.grouped_actor_rows,
                )?,
                last_model_generation: m.last_model_generation,
                model_calls: diff(m.model_calls, q.model_calls)?,
                model_backend_failures: diff(
                    m.backend_failures, q.backend_failures,
                )?,
                invalid_model_batches: diff(
                    m.invalid_action_batches, q.invalid_action_batches,
                )?,
                game_failed_steps: diff(s.failed_steps, p.failed_steps)?,
                enemy_hits: diff(s.enemy_hits, p.enemy_hits)?,
                friendly_hits: diff(s.friendly_hits, p.friendly_hits)?,
                enemy_hit_tsv_loss: diff(s.enemy_hit_tsv_loss, p.enemy_hit_tsv_loss)?,
                friendly_hit_tsv_loss: diff(s.friendly_hit_tsv_loss, p.friendly_hit_tsv_loss)?,
                unattributed_damage_events: diff(s.unattributed_damage_events, p.unattributed_damage_events)?,
                unattributed_damage_tsv_loss: diff(s.unattributed_damage_tsv_loss, p.unattributed_damage_tsv_loss)?,
                policy_roots_completed: diff(s.policy_roots_completed, p.policy_roots_completed)?,
                policy_roots_contacted: diff(s.policy_roots_contacted, p.policy_roots_contacted)?,
                policy_roots_near_missed: diff(s.policy_roots_near_missed, p.policy_roots_near_missed)?,
                physics_integrated_bodies: diff(
                    s.integrated_bodies, p.integrated_bodies,
                )?,
                physics_broadphase_candidates: diff(
                    s.broadphase_candidates, p.broadphase_candidates,
                )?,
                physics_ship_grid_overflow_bodies: diff(
                    s.ship_grid_overflow_bodies, p.ship_grid_overflow_bodies,
                )?,
                physics_obstacle_grid_overflow_bodies: diff(
                    s.obstacle_grid_overflow_bodies, p.obstacle_grid_overflow_bodies,
                )?,
                physics_ship_grid_rebuilds: diff(
                    s.ship_grid_rebuilds, p.ship_grid_rebuilds,
                )?,
                physics_obstacle_grid_rebuilds: diff(
                    s.obstacle_grid_rebuilds, p.obstacle_grid_rebuilds,
                )?,
                physics_ship_grid_cells_indexed: diff(
                    s.ship_grid_cells_indexed, p.ship_grid_cells_indexed,
                )?,
                physics_obstacle_grid_cells_indexed: diff(
                    s.obstacle_grid_cells_indexed, p.obstacle_grid_cells_indexed,
                )?,
                physics_narrowphase_tests: diff(
                    s.narrowphase_tests, p.narrowphase_tests,
                )?,
                physics_trigger_enters: diff(
                    s.trigger_enters, p.trigger_enters,
                )?,
                weapon_range_grid_rebuilds: diff(s.weapon_range_grid_rebuilds, p.weapon_range_grid_rebuilds)?,
                weapon_range_candidates: diff(s.weapon_range_candidates, p.weapon_range_candidates)?,
                weapon_range_exact_tests: diff(s.weapon_range_exact_tests, p.weapon_range_exact_tests)?,
                weapon_range_map_object_grid_rebuilds: diff(
                    s.weapon_range_map_object_grid_rebuilds, p.weapon_range_map_object_grid_rebuilds,
                )?,
                weapon_range_map_object_candidates: diff(
                    s.weapon_range_map_object_candidates, p.weapon_range_map_object_candidates,
                )?,
                weapon_range_map_object_exact_tests: diff(
                    s.weapon_range_map_object_exact_tests, p.weapon_range_map_object_exact_tests,
                )?,
                weapon_range_projectile_tests: diff(s.weapon_range_projectile_tests, p.weapon_range_projectile_tests)?,
                weapon_range_enters: diff(s.weapon_range_enters, p.weapon_range_enters)?,
                weapon_range_exits: diff(s.weapon_range_exits, p.weapon_range_exits)?,
                weapon_range_projectile_exits: diff(s.weapon_range_projectile_exits, p.weapon_range_projectile_exits)?,
                automatic_weapon_target_queries: diff(s.automatic_weapon_target_queries, p.automatic_weapon_target_queries)?,
                automatic_weapon_queue_builds: diff(s.automatic_weapon_queue_builds, p.automatic_weapon_queue_builds)?,
                automatic_weapon_distance_queries: diff(s.automatic_weapon_distance_queries, p.automatic_weapon_distance_queries)?,
                special_weapon_grid_rebuilds: diff(s.special_weapon_grid_rebuilds, p.special_weapon_grid_rebuilds)?,
                special_weapon_candidates: diff(s.special_weapon_candidates, p.special_weapon_candidates)?,
                special_weapon_trigger_hull_tests: diff(s.special_weapon_trigger_hull_tests, p.special_weapon_trigger_hull_tests)?,
                special_weapon_trigger_entries: diff(s.special_weapon_trigger_entries, p.special_weapon_trigger_entries)?,
                special_weapon_trigger_exits: diff(s.special_weapon_trigger_exits, p.special_weapon_trigger_exits)?,
                special_weapon_body_hull_tests: diff(s.special_weapon_body_hull_tests, p.special_weapon_body_hull_tests)?,
                special_weapon_body_entries: diff(s.special_weapon_body_entries, p.special_weapon_body_entries)?,
                automatic_weapon_shots: diff(s.automatic_weapon_shots, p.automatic_weapon_shots)?,
                sampled_physics_steps,
                sampled_physics_p95_upper_nanos:
                    sampled_percentile_upper(&histogram, sampled_physics_steps, 95),
                sampled_model_forward_calls: diff(
                    m.model_forward_timed_calls, q.model_forward_timed_calls,
                )?,
                sampled_model_forward_rows,
                sampled_model_forward_nanos_per_row:
                    (sampled_model_forward_rows != 0).then(||
                        model_forward_nanos as f64 /
                        sampled_model_forward_rows as f64
                    ),
                sampled_rollout_phase_rounds: phases.rounds,
                sampled_rollout_world_steps: phases.world_steps,
                sampled_rollout_model_rows: phases.model_rows,
                sampled_observation_build_nanos: phases.observation_nanos,
                sampled_inference_and_validation_nanos: phases.inference_nanos,
                sampled_world_dispatch_and_step_nanos: phases.dispatch_nanos,
                gauges: NativeHostGauges::from(&current),
            })
        };
        build()
    }
}

fn sampled_percentile_upper(
    histogram: &[u64], samples: u64, percentile: u32,
) -> Option<u128> {
    if samples == 0 || !(1..=100).contains(&percentile) { return None; }
    let target = (u128::from(samples) * u128::from(percentile) + 99) / 100;
    let mut seen = 0_u128;
    for (bucket, count) in histogram.iter().enumerate() {
        seen += u128::from(*count);
        if seen >= target {
            return Some((1_u128 << (bucket + 1)) - 1);
        }
    }
    None
}
