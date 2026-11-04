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
}

impl<S> World<S> {
    pub fn new(id: WorldId, seed: u64, state: S) -> Self {
        Self {
            id,
            state,
            random: RandomStream::new(seed),
            tick: 0,
            failed: false,
        }
    }

    pub fn tick(&self) -> u64 {
        self.tick
    }

    pub fn is_failed(&self) -> bool {
        self.failed
    }
}

/// Rules are implemented once in the shared game crate. The exact same type
/// must be linked into the graphical client and the headless worker.
pub trait GameRules<S> {
    type Error;

    fn step(
        &mut self,
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
                return Err(StepError::Rules { id: world.id, source });
            }
        };

        world.tick += 1;
        self.measurements.record(counts, start.map(|start| start.elapsed()));
        Ok(counts)
    }
}
