//! Low-cost aggregate simulation measurements. The sampler deliberately does
//! not store per-step histories; dashboard/export code can consume snapshots.

use std::time::Duration;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct StepCounts {
    pub decisions: u64,
    pub active_ships: u32,
    pub active_projectiles: u32,
    pub events: u64,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Measurements {
    pub ticks: u64,
    pub decisions: u64,
    pub events: u64,
    /// Rules updates that returned an error and invalidated their world.
    pub failed_steps: u64,
    pub sampled_ticks: u64,
    pub sampled_wall_nanos: u128,
    pub last_active_ships: u32,
    pub last_active_projectiles: u32,
}

impl Measurements {
    #[inline]
    pub fn record(&mut self, counts: StepCounts, sampled: Option<Duration>) {
        self.ticks += 1;
        self.decisions += counts.decisions;
        self.events += counts.events;
        self.last_active_ships = counts.active_ships;
        self.last_active_projectiles = counts.active_projectiles;
        if let Some(duration) = sampled {
            self.sampled_ticks += 1;
            self.sampled_wall_nanos += duration.as_nanos();
        }
    }

    pub fn sampled_ticks_per_second(&self) -> Option<f64> {
        if self.sampled_wall_nanos == 0 {
            return None;
        }
        Some(self.sampled_ticks as f64 * 1e9 / self.sampled_wall_nanos as f64)
    }
}
