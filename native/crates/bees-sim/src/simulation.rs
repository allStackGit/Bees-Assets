//! Single authoritative fixed-step dispatch for game, training and evaluation.
//!
//! A world is independent of rendering and network control. A rules
//! implementation belongs to the game core, never to a training adapter.
//! This initial foundation deliberately does not substitute approximate
//! physics for Unity's behavior.

use std::collections::VecDeque;
use std::num::{NonZeroU32, NonZeroUsize};
use std::sync::Mutex;
use std::time::{Duration, Instant};

use crate::policy::Decision;
use crate::telemetry::{Measurements, StepCounts};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, serde::Serialize, serde::Deserialize)]
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
    fixed_step_seconds: Option<f32>,
    tick: u64,
    /// Monotonic episode identity for rejecting delayed policy inference
    /// after a world is reset with the same WorldId and agent identifiers.
    episode_revision: u64,
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
            fixed_step_seconds: None,
            tick: 0,
            episode_revision: 0,
            failed: false,
            measurements: Measurements::default(),
        }
    }

    pub fn tick(&self) -> u64 {
        self.tick
    }

    pub fn episode_revision(&self) -> u64 {
        self.episode_revision
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
        self.episode_revision = self.episode_revision.checked_add(1)
            .expect("native world episode identity exhausted");
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

    /// A normal terminal episode is not a failed world. The scheduler
    /// must stop advancing it until the owner explicitly resets it.
    /// Physics-only worlds can use the default never-completed rule.
    fn is_complete(&self, _world: &World<S>) -> bool { false }
}

#[derive(Debug)]
pub enum StepError<E> {
    MissingWorld { index: usize },
    FailedWorld { id: WorldId },
    CompletedWorld { id: WorldId },
    Rules { id: WorldId, source: E },
}

/// Work assigned to one CPU worker in an independent simulation window.
/// Counters are aggregated after the worker finishes, without per-tick locks
/// or per-step trace allocations.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ParallelWorkerMeasurements {
    pub arenas: usize,
    pub ticks: u64,
    pub integrated_bodies: u64,
    pub narrowphase_tests: u64,
    /// Whole worker wall time; sampled once per window, including queue
    /// claims but not aggregation on the caller thread.
    pub wall_elapsed: Duration,
}

impl ParallelWorkerMeasurements {
    fn from_report(
        arenas: usize, measurements: &Measurements, wall_elapsed: Duration,
    ) -> Self {
        Self {
            arenas, ticks: measurements.ticks,
            integrated_bodies: measurements.integrated_bodies,
            narrowphase_tests: measurements.narrowphase_tests,
            wall_elapsed,
        }
    }
}

/// Results for a bounded independent rollout window. A failed world is
/// marked failed by the same step implementation used in serial mode; other
/// worlds continue. The caller must reset failed worlds explicitly.
#[derive(Debug)]
pub struct ParallelWindow<E> {
    pub measurements: Measurements,
    pub failed_worlds: Vec<StepError<E>>,
    pub skipped_failed_worlds: usize,
    /// Worlds that finished normally (or were already complete) in
    /// this window; no failed-step measurement is recorded for them.
    pub completed_worlds: usize,
    /// Actual host wall elapsed, including worker startup and decision
    /// generation. Unlike summed per-step timings, this measures observed
    /// multi-core throughput for this specific window.
    pub wall_elapsed: Duration,
    /// Work completed by each worker, in worker-creation order; includes
    /// completed or already-failed arenas with zero successful ticks.
    pub worker_loads: Vec<ParallelWorkerMeasurements>,
}

impl<E> ParallelWindow<E> {
    pub fn steps_per_second(&self) -> Option<f64> {
        let seconds = self.wall_elapsed.as_secs_f64();
        if seconds == 0.0 { return None; }
        Some(self.measurements.ticks as f64 / seconds)
    }
}

impl<E> Default for ParallelWindow<E> {
    fn default() -> Self {
        Self {
            measurements: Measurements::default(),
            failed_worlds: Vec::new(),
            skipped_failed_worlds: 0,
            completed_worlds: 0,
            wall_elapsed: Duration::ZERO,
            worker_loads: Vec::new(),
        }
    }
}

/// A single world scheduler defaults to the cheapest serial path. For
/// independent rollout windows, the same worlds and rules may be partitioned
/// across scoped CPU workers for many steps at a time, amortizing thread
/// creation instead of spawning threads on each simulation tick.
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

    /// Borrow the authoritative configuration without exposing mutation.
    pub fn rules(&self) -> &R { &self.rules }

    /// Common sampling policy for per-world step timing and the host's
    /// batched rollout timings. None disables both timing paths.
    pub fn timing_sample_period(&self) -> Option<NonZeroU32> {
        self.sample_every
    }

    pub fn world_fixed_step_seconds(&self, index: usize) -> Option<f32> {
        self.worlds.get(index).map(|world| world.fixed_step_seconds.unwrap_or(self.dt_seconds))
    }

    /// An imported Level keeps its authored clock across episode resets.
    pub fn add_world_with_fixed_step(&mut self, mut world: World<S>, seconds: f32)
        -> Result<usize, &'static str>
    {
        if !seconds.is_finite() || seconds <= 0.0 {
            return Err("fixed-step seconds must be finite and positive");
        }
        world.fixed_step_seconds = Some(seconds);
        self.add_world(world)
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

    /// Access one owned world between simulation steps or parallel
    /// windows. The mutable borrow prevents concurrent scheduler work
    /// and lets a connection-owned transport apply/acknowledge pending
    /// Hive Mind retirements or refresh game adapters without process
    /// globals. Do not mutate physics mid-step or bypass reset semantics.
    pub fn world_mut(&mut self, index: usize) -> Option<&mut World<S>> {
        self.worlds.get_mut(index)
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
        step_one(
            &self.rules, self.dt_seconds, self.sample_every,
            world, decisions, &mut self.measurements,
        )
    }

    /// Advance independent arenas for multiple fixed steps per CPU worker.
    /// Each world stays on exactly one worker for the entire window; no
    /// world state, RNG, or mutable game scratch is shared between workers.
    ///
    /// `decide` fills a worker-local reusable decision buffer for the
    /// current world. It must derive actions from real observations/policy
    /// state; the simulation does not invent neural actions. A GPU-batched
    /// inference pipeline should use the existing explicit `step_world`
    /// interface instead, so policy inference can synchronize across worlds.
    ///
    /// One arena or one requested worker runs inline, with no thread spawn.
    /// Multiple arenas use scoped threads once per window, not once per
    /// frame. This is not a promise of faster execution for small windows.
    pub fn run_parallel_window<F>(
        &mut self,
        steps_per_world: NonZeroU32,
        worker_count: NonZeroUsize,
        decide: &F,
    ) -> ParallelWindow<R::Error>
    where
        S: Send,
        R: Sync,
        R::Error: Send,
        F: Fn(&World<S>, &mut Vec<Decision>) + Sync,
    {
        let started = Instant::now();
        let mut result = ParallelWindow::default();
        if self.worlds.is_empty() {
            result.wall_elapsed = started.elapsed();
            return result;
        }

        let workers = worker_count.get().min(self.worlds.len());
        let rules = &self.rules;
        let dt = self.dt_seconds;
        let sample = self.sample_every;
        let steps = steps_per_world.get();

        if workers == 1 {
            let mut decisions = Vec::new();
            let mut part = run_partition(
                rules, dt, sample, &mut self.worlds, steps, decide, &mut decisions,
            );
            part.worker_loads.push(ParallelWorkerMeasurements::from_report(
                self.worlds.len(), &part.measurements, started.elapsed(),
            ));
            self.measurements.merge(&part.measurements);
            part.wall_elapsed = started.elapsed();
            return part;
        }

        // Short windows and exactly one arena per worker retain the cheapest
        // fixed-slice dispatch. For longer multi-arena CPU windows, take each
        // independent world from a single bounded queue. A worker owns that
        // world for its WHOLE window, retaining local RNG, game scratch and
        // one reusable action buffer. Only the arena claim takes a lock;
        // physics and policy decisions never execute under one.
        //
        // This avoids one busy world holding a whole static partition while
        // other workers finish their cheap arenas and sit idle. The threshold
        // keeps queue setup off the one-/few-tick path used by clients.
        let outcomes = if steps >= 64 && self.worlds.len() > workers {
            let pending = Mutex::new(self.worlds.iter_mut().collect::<VecDeque<_>>());
            std::thread::scope(|scope| {
                let mut handles = Vec::with_capacity(workers);
                for _ in 0..workers {
                    let pending = &pending;
                    handles.push(scope.spawn(move || {
                        let worker_started = Instant::now();
                        let mut report = ParallelWindow::default();
                        let mut decisions = Vec::new();
                        let mut assigned = 0;
                        loop {
                            let next = pending.lock()
                                .expect("native arena queue poisoned").pop_front();
                            let Some(world) = next else { break };
                            assigned += 1;
                            let part = run_partition(
                                rules, dt, sample,
                                std::slice::from_mut(world), steps, decide, &mut decisions,
                            );
                            report.measurements.merge(&part.measurements);
                            report.skipped_failed_worlds += part.skipped_failed_worlds;
                            report.completed_worlds += part.completed_worlds;
                            report.failed_worlds.extend(part.failed_worlds);
                        }
                        (report, assigned, worker_started.elapsed())
                    }));
                }
                handles.into_iter().map(|handle| handle.join())
                    .collect::<Vec<_>>()
            })
        } else {
            // Fixed consecutive partitions avoid the queue entirely for
            // short windows. Workers still hold their own decision buffers.
            let base = self.worlds.len() / workers;
            let extras = self.worlds.len() % workers;
            std::thread::scope(|scope| {
                let mut remaining = self.worlds.as_mut_slice();
                let mut handles = Vec::with_capacity(workers);
                for index in 0..workers {
                    let size = base + usize::from(index < extras);
                    let (chunk, rest) = remaining.split_at_mut(size);
                    remaining = rest;
                    handles.push(scope.spawn(move || {
                        let worker_started = Instant::now();
                        let mut decisions = Vec::new();
                        let report = run_partition(
                            rules, dt, sample, chunk, steps, decide, &mut decisions,
                        );
                        (report, size, worker_started.elapsed())
                    }));
                }
                handles.into_iter().map(|handle| handle.join())
                    .collect::<Vec<_>>()
            })
        };

        let mut panicked = false;
        for outcome in outcomes {
            match outcome {
                Ok((part, assigned, worker_elapsed)) => {
                    result.worker_loads.push(ParallelWorkerMeasurements::from_report(
                        assigned, &part.measurements, worker_elapsed,
                    ));
                    self.measurements.merge(&part.measurements);
                    result.measurements.merge(&part.measurements);
                    result.skipped_failed_worlds += part.skipped_failed_worlds;
                    result.completed_worlds += part.completed_worlds;
                    result.failed_worlds.extend(part.failed_worlds);
                }
                Err(_) => panicked = true,
            }
        }
        if panicked {
            // A panicking policy or rules callback may have partially
            // mutated its world. Invalidate the whole window. Never let a
            // caught panic cause a silent continuation from corrupt state.
            for world in &mut self.worlds {
                world.failed = true;
            }
            panic!("native world rollout worker panicked");
        }
        result.wall_elapsed = started.elapsed();
        result
    }
}

/// One authoritative step, independent of serial or partitioned scheduling.
/// The only counters written during the step belong to this world and the
/// single caller-owned aggregate (never contended across threads).
fn step_one<S, R: GameRules<S>>(
    rules: &R,
    dt: f32,
    sample_every: Option<NonZeroU32>,
    world: &mut World<S>,
    decisions: &[Decision],
    aggregate: &mut Measurements,
) -> Result<StepCounts, StepError<R::Error>> {
    if world.failed {
        return Err(StepError::FailedWorld { id: world.id });
    }
    if rules.is_complete(world) {
        return Err(StepError::CompletedWorld { id: world.id });
    }
    let sampled = sample_every.is_some_and(|period|
        world.tick % u64::from(period.get()) == 0
    );
    let start = if sampled { Some(Instant::now()) } else { None };
    let counts = match rules.step(world, decisions, world.fixed_step_seconds.unwrap_or(dt)) {
        Ok(counts) => counts,
        Err(source) => {
            // Rules may have partially mutated a world. It cannot be stepped
            // again until a caller explicitly resets its episode state.
            world.failed = true;
            let duration = start.map(|instant| instant.elapsed());
            world.measurements.record_failure(duration);
            aggregate.record_failure(duration);
            return Err(StepError::Rules { id: world.id, source });
        }
    };
    world.tick += 1;
    let duration = start.map(|instant| instant.elapsed());
    world.measurements.record(counts, duration);
    aggregate.record(counts, duration);
    Ok(counts)
}

fn run_partition<S, R, F>(
    rules: &R,
    dt: f32,
    sample_every: Option<NonZeroU32>,
    worlds: &mut [World<S>],
    steps: u32,
    decide: &F,
    decisions: &mut Vec<Decision>,
) -> ParallelWindow<R::Error>
where
    R: GameRules<S>,
    F: Fn(&World<S>, &mut Vec<Decision>),
{
    let mut report = ParallelWindow::default();
    // Reuse action memory across every world claimed by this worker.
    for world in worlds {
        if world.failed {
            report.skipped_failed_worlds += 1;
            continue;
        }
        if rules.is_complete(world) {
            report.completed_worlds += 1;
            continue;
        }
        for _ in 0..steps {
            decisions.clear();
            decide(world, decisions);
            if let Err(error) = step_one(
                rules, dt, sample_every, world, decisions.as_slice(),
                &mut report.measurements,
            ) {
                report.failed_worlds.push(error);
                break;
            }
            if rules.is_complete(world) {
                report.completed_worlds += 1;
                break;
            }
        }
    }
    report
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
    fn independent_parallel_rollout_matches_serial_world_state_and_work() {
        use crate::policy::Action;
        let build = || {
            let mut sim = Simulation::new(CountingRules, 0.02).unwrap();
            for id in 1..=7 {
                sim.add_world(World::new(WorldId(id), id * 100, 0)).unwrap();
            }
            sim
        };
        let mut serial = build();
        let mut parallel = build();
        let decide = |world: &World<u64>, out: &mut Vec<Decision>| {
            out.push(Decision {
                agent_id: world.id.0,
                action: Action::ZERO,
            });
            if world.tick() % 3 == 0 {
                out.push(Decision {
                    agent_id: world.id.0 + 100,
                    action: Action::ZERO,
                });
            }
        };
        let mut actions = Vec::new();
        for index in 0..7 {
            for _ in 0..13 {
                actions.clear();
                decide(serial.world(index).unwrap(), &mut actions);
                serial.step_world(index, &actions).unwrap();
            }
        }

        let report = parallel.run_parallel_window(
            NonZeroU32::new(13).unwrap(),
            NonZeroUsize::new(4).unwrap(),
            &decide,
        );
        assert!(report.failed_worlds.is_empty());
        assert_eq!(report.skipped_failed_worlds, 0);
        assert_eq!(report.measurements, serial.measurements());
        assert_eq!(parallel.measurements(), serial.measurements());
        assert_eq!(report.measurements.ticks, 91);
        assert!(report.steps_per_second().is_some());
        for index in 0..7 {
            let a = serial.world(index).unwrap();
            let b = parallel.world(index).unwrap();
            assert_eq!(a.id, b.id);
            assert_eq!(a.state, b.state);
            assert_eq!(a.tick(), b.tick());
            assert_eq!(a.random.state(), b.random.state());
            assert_eq!(a.measurements(), b.measurements());
        }
    }

    #[test]
    fn parallel_rollout_failures_do_not_advance_failed_arenas() {
        struct FailingRules;
        impl GameRules<u64> for FailingRules {
            type Error = &'static str;

            fn step(
                &self,
                world: &mut World<u64>,
                _: &[Decision],
                _: f32,
            ) -> Result<StepCounts, Self::Error> {
                if world.id.0 == 2 && world.tick() == 2 {
                    return Err("arena failed");
                }
                world.state += 1;
                Ok(StepCounts::default())
            }
        }

        let mut sim = Simulation::new(FailingRules, 0.02).unwrap();
        for id in 1..=3 {
            sim.add_world(World::new(WorldId(id), id * 10, 0)).unwrap();
        }
        let decide = |_: &World<u64>, _: &mut Vec<Decision>| {};
        let report = sim.run_parallel_window(
            NonZeroU32::new(5).unwrap(),
            NonZeroUsize::new(3).unwrap(),
            &decide,
        );
        assert_eq!(report.failed_worlds.len(), 1);
        assert_eq!(report.measurements.ticks, 12);
        assert_eq!(report.measurements.failed_steps, 1);
        assert_eq!(sim.world(1).unwrap().tick(), 2);
        assert!(sim.world(1).unwrap().is_failed());
        assert_eq!(sim.world(0).unwrap().tick(), 5);
        assert_eq!(sim.world(2).unwrap().tick(), 5);

        let next = sim.run_parallel_window(
            NonZeroU32::new(1).unwrap(),
            NonZeroUsize::new(3).unwrap(),
            &decide,
        );
        assert_eq!(next.skipped_failed_worlds, 1);
        assert!(next.failed_worlds.is_empty());
        assert_eq!(sim.world(1).unwrap().tick(), 2);
        assert_eq!(sim.world(0).unwrap().tick(), 6);
        assert_eq!(sim.world(2).unwrap().tick(), 6);
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
