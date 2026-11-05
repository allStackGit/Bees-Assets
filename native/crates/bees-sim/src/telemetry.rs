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

    #[inline]
    pub fn record_failure(&mut self, sampled: Option<Duration>) {
        self.failed_steps += 1;
        if let Some(duration) = sampled {
            self.sampled_failed_steps += 1;
            self.sampled_failed_wall_nanos += duration.as_nanos();
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
