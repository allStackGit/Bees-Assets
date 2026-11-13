//! Bounded, world-local path worker scheduler for native game clients.
//!
//! Unity Pathfinder.Update launches at most ConfigData.MaxThreads background
//! searches, applies finished results on the main thread, and defers a dirty
//! static-layer rebuild until active search workers have completed. This
//! scheduler retains those boundaries while giving each worker reusable
//! NavigationSearch scratch arrays and an immutable per-fixed-frame
//! clearance snapshot. The headless RL runner may continue to use the
//! deterministic synchronous NavigationRuntime API without spawning threads.

use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::{mpsc, Arc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use crate::combat_world::{CombatWorld, CombatWorldError};
use crate::navigation::{NavigationError, NavigationGrid};
use crate::navigation_clearance::NavigationSearchSnapshot;
use crate::navigation_requests::{NavigationCompletion, NavigationRequest};
use crate::navigation_runtime::{NavigationMapFrame, NavigationRuntime};
use crate::navigation_search::{NavigationPath, NavigationSearch};

const UNITY_SEARCH_TIME_LIMIT: Duration = Duration::from_secs(5);

#[derive(Debug)]
pub enum NavigationSchedulerError {
    InvalidWorkerCount,
    InvalidMap(NavigationError),
    World(CombatWorldError),
    Spawn(std::io::Error),
    WorkerUnavailable,
    WorkerPanicked,
    WorkerProtocol,
}

impl From<NavigationError> for NavigationSchedulerError {
    fn from(error: NavigationError) -> Self { Self::InvalidMap(error) }
}

impl From<CombatWorldError> for NavigationSchedulerError {
    fn from(error: CombatWorldError) -> Self { Self::World(error) }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NavigationSchedulerStep {
    pub launched: usize,
    pub completed: usize,
    pub applied: usize,
    pub requeued: usize,
    pub discarded: usize,
    pub busy_workers: usize,
    pub clearance_refreshed: bool,
    pub waiting_for_static_rebuild: bool,
}

struct Job {
    request: NavigationRequest,
    frame: NavigationMapFrame,
    clearance: Arc<NavigationSearchSnapshot>,
}

enum Command {
    Search(Job),
    Shutdown,
}

enum Outcome {
    Completed(Result<Option<NavigationPath>, NavigationError>),
    Panicked,
}

struct Reply {
    worker_index: usize,
    request: NavigationRequest,
    outcome: Outcome,
}

struct Worker {
    sender: mpsc::SyncSender<Command>,
    thread: Option<JoinHandle<()>>,
    running: Option<NavigationRequest>,
    alive: bool,
}

pub struct NavigationScheduler {
    grid: NavigationGrid,
    workers: Vec<Worker>,
    replies: mpsc::Receiver<Reply>,
    clearance_snapshot: Option<Arc<NavigationSearchSnapshot>>,
    snapshot_frame: Option<u64>,
    snapshot_moving_asteroids_present: Option<bool>,
    pending_static_rebuild: bool,
}

impl NavigationScheduler {
    /// The Level chooses how many CPU workers to dedicate to pathfinding.
    /// The explicit upper bound prevents accidental unbounded thread
    /// creation in multi-arena servers.
    pub fn new(grid: NavigationGrid, worker_count: usize) -> Result<Self, NavigationSchedulerError> {
        if !(1..=128).contains(&worker_count) {
            return Err(NavigationSchedulerError::InvalidWorkerCount);
        }
        let (results_tx, replies) = mpsc::channel::<Reply>();
        let mut scheduler = Self {
            grid,
            workers: Vec::with_capacity(worker_count),
            replies,
            clearance_snapshot: None,
            snapshot_frame: None,
            snapshot_moving_asteroids_present: None,
            pending_static_rebuild: true,
        };
        for index in 0..worker_count {
            // Create all potentially fallible search arrays on the owning
            // thread, so worker initialization errors are never hidden.
            let mut search = NavigationSearch::new(grid)?;
            let (sender, receiver) = mpsc::sync_channel::<Command>(1);
            let completed = results_tx.clone();
            let handle = thread::Builder::new()
                .name(format!("bees-navigation-{index}"))
                .spawn(move || {
                    while let Ok(command) = receiver.recv() {
                        let Command::Search(job) = command else { break };
                        let outcome = match catch_unwind(AssertUnwindSafe(|| {
                            solve_job(&mut search, &job)
                        })) {
                            Ok(result) => Outcome::Completed(result),
                            Err(_) => Outcome::Panicked,
                        };
                        let panicked = matches!(&outcome, Outcome::Panicked);
                        if completed.send(Reply {
                            worker_index: index,
                            request: job.request,
                            outcome,
                        }).is_err() {
                            break;
                        }
                        if panicked { break; }
                    }
                })
                .map_err(NavigationSchedulerError::Spawn)?;
            scheduler.workers.push(Worker {
                sender, thread: Some(handle),
                running: None, alive: true,
            });
        }
        Ok(scheduler)
    }

    pub fn busy_workers(&self) -> usize {
        self.workers.iter().filter(|worker| worker.running.is_some()).count()
    }

    fn drain_completed(
        &mut self,
        world: &mut CombatWorld,
        step: &mut NavigationSchedulerStep,
    ) -> Result<(), NavigationSchedulerError> {
        loop {
            let reply = match self.replies.try_recv() {
                Ok(reply) => reply,
                Err(mpsc::TryRecvError::Empty) => break,
                Err(mpsc::TryRecvError::Disconnected) => {
                    return Err(NavigationSchedulerError::WorkerUnavailable);
                }
            };
            let worker = self.workers.get_mut(reply.worker_index)
                .ok_or(NavigationSchedulerError::WorkerProtocol)?;
            if worker.running != Some(reply.request) {
                return Err(NavigationSchedulerError::WorkerProtocol);
            }
            worker.running = None;
            step.completed += 1;
            match reply.outcome {
                Outcome::Completed(Ok(path)) => {
                    match world.complete_path_job(reply.request, path)? {
                        NavigationCompletion::Accepted { .. } => step.applied += 1,
                        NavigationCompletion::Requeued { .. } => step.requeued += 1,
                        NavigationCompletion::Discarded => step.discarded += 1,
                    }
                }
                Outcome::Completed(Err(error)) => {
                    world.abandon_path_job(reply.request);
                    return Err(error.into());
                }
                Outcome::Panicked => {
                    worker.alive = false;
                    world.abandon_path_job(reply.request);
                    return Err(NavigationSchedulerError::WorkerPanicked);
                }
            }
        }
        Ok(())
    }

    /// Call on the world's owning thread, after physics moves obstacles
    /// for this fixed frame. All completed worker results are handled before
    /// allocating fresh slots, exactly like Unity Pathfinder.Update.
    ///
    /// A static geometry change blocks further dispatch until *existing*
    /// workers finish. This matches Unity's static-layer rebuild barrier
    /// and avoids applying a rebuilt clearance map into a live search.
    /// Dynamic asteroid changes instead get a new immutable snapshot on
    /// the next frame, even while older tasks continue using the prior one.
    pub fn pump(
        &mut self,
        world: &mut CombatWorld,
        runtime: &mut NavigationRuntime,
        fixed_frame: u64,
        static_obstacles_dirty: bool,
        collision_asteroids_enabled: bool,
    ) -> Result<NavigationSchedulerStep, NavigationSchedulerError> {
        if runtime.clearance().grid() != self.grid {
            return Err(NavigationError::InvalidMap.into());
        }
        if static_obstacles_dirty {
            self.pending_static_rebuild = true;
            self.clearance_snapshot = None;
        }
        let mut step = NavigationSchedulerStep::default();
        self.drain_completed(world, &mut step)?;

        // If a worker unexpectedly exited outside the search unwind guard,
        // its owner must not remain busy forever. A second drain avoids
        // racing a reply sent just before the worker thread exited.
        if self.workers.iter().any(|worker|
            worker.alive && worker.thread.as_ref().is_some_and(|t| t.is_finished())) {
            self.drain_completed(world, &mut step)?;
            for worker in &mut self.workers {
                if worker.alive &&
                    worker.thread.as_ref().is_some_and(|t| t.is_finished()) {
                    worker.alive = false;
                    if let Some(request) = worker.running.take() {
                        world.abandon_path_job(request);
                        return Err(NavigationSchedulerError::WorkerUnavailable);
                    }
                }
            }
        }

        if self.pending_static_rebuild && self.busy_workers() > 0 {
            step.busy_workers = self.busy_workers();
            step.waiting_for_static_rebuild = true;
            return Ok(step);
        }

        let has_idle = self.workers.iter().any(|worker|
            worker.alive && worker.running.is_none());
        if !has_idle || !world.has_waiting_path_jobs() {
            step.busy_workers = self.busy_workers();
            if step.busy_workers == 0 && world.has_waiting_path_jobs() {
                return Err(NavigationSchedulerError::WorkerUnavailable);
            }
            return Ok(step);
        }

        // A static-only map is identical across fixed frames, even when
        // asteroid collision is configured but the world has no live
        // CollisionAsteroids. Detect arrivals/departures from the actual
        // canonical world, not a stale second obstacle registry.
        let moving_asteroids_present = collision_asteroids_enabled &&
            world.dynamic_navigation_asteroids().next().is_some();
        if self.clearance_snapshot.is_none() ||
            (moving_asteroids_present && self.snapshot_frame != Some(fixed_frame)) ||
            self.snapshot_moving_asteroids_present != Some(moving_asteroids_present) ||
            self.pending_static_rebuild {
            world.refresh_world_navigation(
                runtime,
                self.pending_static_rebuild,
                collision_asteroids_enabled,
            )?;
            self.clearance_snapshot = Some(Arc::new(
                runtime.clearance().search_snapshot()
            ));
            self.snapshot_frame = Some(fixed_frame);
            self.snapshot_moving_asteroids_present = Some(moving_asteroids_present);
            self.pending_static_rebuild = false;
            step.clearance_refreshed = true;
        }
        let snapshot = self.clearance_snapshot.as_ref()
            .expect("snapshot created when workers can launch");
        let frame = runtime.map_frame();
        for worker in &mut self.workers {
            if !worker.alive || worker.running.is_some() { continue; }
            let Some(request) = world.claim_next_path_job() else { break };
            let command = Command::Search(Job {
                request,
                frame,
                clearance: Arc::clone(snapshot),
            });
            if worker.sender.try_send(command).is_err() {
                worker.alive = false;
                world.abandon_path_job(request);
                return Err(NavigationSchedulerError::WorkerUnavailable);
            }
            worker.running = Some(request);
            step.launched += 1;
        }
        step.busy_workers = self.busy_workers();
        if step.busy_workers == 0 && world.has_waiting_path_jobs() {
            return Err(NavigationSchedulerError::WorkerUnavailable);
        }
        Ok(step)
    }
}

fn solve_job(
    search: &mut NavigationSearch,
    job: &Job,
) -> Result<Option<NavigationPath>, NavigationError> {
    let from = job.frame.world_to_level(job.request.start);
    let to = job.frame.world_to_level(job.request.destination);
    let deadline = Instant::now().checked_add(UNITY_SEARCH_TIME_LIMIT);
    let mut result = search.find_level_path_until(
        &job.clearance,
        from,
        to,
        job.request.ship_clearance,
        deadline,
    )?;
    if let Some(ref mut path) = result {
        for point in &mut path.points {
            *point = job.frame.level_to_world(*point);
            if !point.is_finite() {
                return Err(NavigationError::NonFiniteMapTransform);
            }
        }
    }
    Ok(result)
}

impl Drop for NavigationScheduler {
    fn drop(&mut self) {
        let mut joins = Vec::with_capacity(self.workers.len());
        for mut worker in self.workers.drain(..) {
            // The channel can still be full if a submitted task has not
            // been received yet. Dropping the last sender *before* joining
            // ensures recv() eventually sees disconnection even when the
            // nonblocking shutdown message could not be queued.
            let _ = worker.sender.try_send(Command::Shutdown);
            drop(worker.sender);
            if let Some(handle) = worker.thread.take() {
                joins.push(handle);
            }
        }
        for handle in joins {
            let _ = handle.join();
        }
    }
}
