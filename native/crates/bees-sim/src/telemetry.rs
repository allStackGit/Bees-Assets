//! Low-cost aggregate simulation measurements. The sampler deliberately does
//! not store per-step histories; dashboard/export code can consume snapshots.

use std::time::Duration;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct StepCounts {
    pub decisions: u64,
    pub active_ships: u32,
    pub active_projectiles: u32,
    pub events: u64,
    /// Physics already computes these counts during each fixed step.
    /// Export them without extra collision queries or clock reads.
    pub integrated_bodies: u64,
    pub broadphase_candidates: u64,
    pub narrowphase_tests: u64,
    pub trigger_enters: u64,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Measurements {
    pub ticks: u64,
    pub decisions: u64,
    pub events: u64,
    pub integrated_bodies: u64,
    pub broadphase_candidates: u64,
    pub narrowphase_tests: u64,
    pub trigger_enters: u64,
    /// Rules updates that returned an error and invalidated their world.
    pub failed_steps: u64,
    pub sampled_ticks: u64,
    pub sampled_wall_nanos: u128,
    /// Log2 nanosecond histogram, recorded only on opted-in sampled steps.
    /// Values are counts, not timings from every individual tick.
    /// Exporting it permits approximate p50/p95/p99 without per-tick logs.
    pub sampled_step_latency_log2_ns: [u64; 64],
    pub last_active_ships: u32,
    pub last_active_projectiles: u32,
}

impl Measurements {
    #[inline]
    pub fn record(&mut self, counts: StepCounts, sampled: Option<Duration>) {
        self.ticks += 1;
        self.decisions += counts.decisions;
        self.events += counts.events;
        self.integrated_bodies += counts.integrated_bodies;
        self.broadphase_candidates += counts.broadphase_candidates;
        self.narrowphase_tests += counts.narrowphase_tests;
        self.trigger_enters += counts.trigger_enters;
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

    pub fn sampled_ticks_per_second(&self) -> Option<f64> {
        if self.sampled_wall_nanos == 0 {
            return None;
        }
        Some(self.sampled_ticks as f64 * 1e9 / self.sampled_wall_nanos as f64)
    }
}
