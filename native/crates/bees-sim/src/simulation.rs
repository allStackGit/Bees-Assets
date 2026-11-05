//! Single authoritative fixed-step dispatch for game, training and evaluation.
//!
//! A world is independent of rendering and network control. A rules
//! implementation belongs to the game core, never to a training adapter.
//! This initial foundation deliberately does not substitute approximate
//! physics for Unity's behavior.

use std::num::NonZeroU32;
use std::time::Instant;

use crate::policy::Decision;
use crate::telemetry::{Measurements, StepCounts};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct WorldId(pub u64);

/// Fast, repeatable, per-world integer random stream. Gameplay porting must
/// explicitly decide where Unity's historical RNG semantics are required.
#[derive(Clone, Debug)]
pub struct RandomStream {
    state: u64,
}

impl RandomStream {
    pub const fn new(seed: u64) -> Self {
        Self { state: seed }
    }

    #[inline]
    pub fn next_u64(&mut self) -> u64 {
        self.state = self.state.wrapping_add(0x9e37_79b9_7f4a_7c15);
        let mut value = self.state;
        value = (value ^ (value >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
        value = (value ^ (value >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
        value ^ (value >> 31)
    }

    /// Rejection sampling avoids modulo bias for seeded scenario and frame
    /// selection, while remaining allocation-free and world-local.
    pub fn next_below(&mut self, upper_exclusive: u64) -> u64 {
        assert!(upper_exclusive > 0, "random upper bound must be positive");
        let threshold = upper_exclusive.wrapping_neg() % upper_exclusive;
        loop {
            let value = self.next_u64();
            if value >= threshold {
                return value % upper_exclusive;
            }
        }
    }

    /// Save and restore the exact stream position with a world checkpoint.
    pub const fn state(&self) -> u64 {
        self.state
    }
}

/// World-local state, owned by exactly one simulation. No Unity-style static
/// registries or global RNG: independent arenas can later run concurrently.
#[derive(Debug)]
pub struct World<S> {
    pub id: WorldId,
    pub state: S,
    pub random: RandomStream,
    tick: u64,
    failed: bool,
    /// Episode-local counts and sampled timings, independent for each arena.
    /// The Simulation also retains cumulative totals across all episodes.
    measurements: Measurements,
}

impl<S> World<S> {
    pub fn new(id: WorldId, seed: u64, state: S) -> Self {
        Self {
            id,
            state,
            random: RandomStream::new(seed),
            tick: 0,
            failed: false,
            measurements: Measurements::default(),
        }
    }

    pub fn tick(&self) -> u64 {
        self.tick
    }

    pub fn is_failed(&self) -> bool {
        self.failed
    }

    pub fn measurements(&self) -> Measurements {
        self.measurements
    }

    /// Explicit episode reset clears a failed/finished world; simply calling
    /// step again after an error is intentionally prohibited.
    pub fn reset_episode(&mut self, seed: u64, state: S) {
        self.state = state;
        self.random = RandomStream::new(seed);
        self.tick = 0;
        self.failed = false;
        self.measurements = Measurements::default();
    }
}

/// Rules are implemented once in the shared game crate. The exact same type
/// must be linked into the graphical client and the headless worker.
pub trait GameRules<S> {
    type Error;

    // Rules are shared and stateless; all mutable world scratch/state must be
    // owned by World<S> so independent worlds can run in parallel.
    fn step(
        &self,
        world: &mut World<S>,
        decisions: &[Decision],
        dt_seconds: f32,
    ) -> Result<StepCounts, Self::Error>;
}

#[derive(Debug)]
pub enum StepError<E> {
    MissingWorld { index: usize },
    FailedWorld { id: WorldId },
    Rules { id: WorldId, source: E },
}

/// A single world scheduler is initially deliberately serial. Starting
/// threads on each tick would add overhead; long-lived parallel workers may
/// be introduced only with the same rules and world ownership contracts.
pub struct Simulation<S, R> {
    rules: R,
    worlds: Vec<World<S>>,
    dt_seconds: f32,
    sample_every: Option<NonZeroU32>,
    measurements: Measurements,
}

impl<S, R: GameRules<S>> Simulation<S, R> {
    /// The fixed timestep is explicit; do not silently assume Unity's
    /// project-wide fixedDeltaTime is unchanged.
    pub fn new(rules: R, dt_seconds: f32) -> Result<Self, &'static str> {
        if !dt_seconds.is_finite() || dt_seconds <= 0.0 {
            return Err("fixed-step seconds must be finite and positive");
        }
        Ok(Self {
            rules,
            worlds: Vec::new(),
            dt_seconds,
            sample_every: None,
            measurements: Measurements::default(),
        })
    }

    pub fn set_timing_sample_period(&mut self, every: Option<NonZeroU32>) {
        self.sample_every = every;
    }

    pub fn add_world(&mut self, world: World<S>) -> Result<usize, &'static str> {
        if self.worlds.iter().any(|existing| existing.id == world.id) {
            return Err("world IDs must be unique within a simulation");
        }
        let index = self.worlds.len();
        self.worlds.push(world);
        Ok(index)
    }

    pub fn world(&self, index: usize) -> Option<&World<S>> {
        self.worlds.get(index)
    }

    pub fn world_count(&self) -> usize {
        self.worlds.len()
    }

    /// Reset in place to avoid recreating world storage on every episode.
    pub fn reset_world(&mut self, index: usize, seed: u64, state: S) -> Result<(), &'static str> {
        let world = self.worlds.get_mut(index).ok_or("missing world index")?;
        world.reset_episode(seed, state);
        Ok(())
    }

    pub fn measurements(&self) -> Measurements {
        self.measurements
    }

    pub fn step_world(
        &mut self,
        index: usize,
        decisions: &[Decision],
    ) -> Result<StepCounts, StepError<R::Error>> {
        let world = self.worlds
            .get_mut(index)
            .ok_or(StepError::MissingWorld { index })?;
        if world.failed {
            return Err(StepError::FailedWorld { id: world.id });
        }

        let sample = self.sample_every.is_some_and(|period| {
            world.tick % u64::from(period.get()) == 0
        });
        let start = if sample { Some(Instant::now()) } else { None };

        let counts = match self.rules.step(world, decisions, self.dt_seconds) {
            Ok(counts) => counts,
            Err(source) => {
                // A failing rules update may have partially mutated world state.
                // Never resume the same world silently from that state.
                world.failed = true;
                let duration = start.map(|start| start.elapsed());
                world.measurements.record_failure(duration);
                self.measurements.record_failure(duration);
                return Err(StepError::Rules { id: world.id, source });
            }
        };

        world.tick += 1;
        let duration = start.map(|start| start.elapsed());
        world.measurements.record(counts, duration);
        self.measurements.record(counts, duration);
        Ok(counts)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct CountingRules;

    impl GameRules<u64> for CountingRules {
        type Error = ();

        fn step(
            &self,
            world: &mut World<u64>,
            decisions: &[Decision],
            _dt_seconds: f32,
        ) -> Result<StepCounts, Self::Error> {
            world.state += 1;
            Ok(StepCounts {
                decisions: decisions.len() as u64,
                active_ships: 1,
                ..StepCounts::default()
            })
        }
    }

    #[test]
    fn worlds_are_independent_and_episode_reset_restarts_only_one() {
        let mut simulation = Simulation::new(CountingRules, 0.02).unwrap();
        simulation.add_world(World::new(WorldId(1), 123, 0)).unwrap();
        simulation.add_world(World::new(WorldId(2), 456, 0)).unwrap();
        simulation.step_world(0, &[]).unwrap();
        simulation.step_world(0, &[]).unwrap();
        simulation.step_world(1, &[]).unwrap();
        assert_eq!(simulation.world(0).unwrap().state, 2);
        assert_eq!(simulation.world(1).unwrap().state, 1);

        simulation.reset_world(0, 123, 0).unwrap();
        assert_eq!(simulation.world(0).unwrap().tick(), 0);
        assert_eq!(simulation.world(1).unwrap().tick(), 1);
        assert_eq!(simulation.measurements().ticks, 3);
        assert_eq!(simulation.world(0).unwrap().measurements().ticks, 0);
        assert_eq!(simulation.world(1).unwrap().measurements().ticks, 1);
    }

    #[test]
    fn reproducible_world_random_stream_and_no_modulo_bias_path() {
        let mut one = RandomStream::new(987);
        let mut two = RandomStream::new(987);
        for _ in 0..100 {
            let a = one.next_below(12);
            let b = two.next_below(12);
            assert_eq!(a, b);
            assert!(a < 12);
        }
        assert_eq!(one.state(), two.state());
    }
}
