//! World-local ownership of Unity-style path requests.
//!
//! The authoritative algorithms live in NavigationSearch; this module owns
//! only request identity, ship lifecycle, queued work and completion routing.
//! In Unity, superseding a *running* search invalidates its eventual result
//! but does not cancel Task.Run. Replan once the stale worker finishes,
//! starting from the ship's current position. Jobs still waiting to start
//! can be replaced without a backlog of obsolete requests.

use std::collections::{HashMap, VecDeque};

use crate::geometry::Vec2;
use crate::navigation_search::NavigationPath;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NavigationRequestError {
    DuplicateShip,
    UnknownShip,
    InvalidRequest,
    IdExhausted,
}

#[derive(Clone, Copy, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct NavigationRequest {
    pub ship_id: i64,
    pub lifecycle_id: u64,
    pub request_id: u64,
    pub start: Vec2,
    pub destination: Vec2,
    pub ship_clearance: i32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NavigationRequestDisposition {
    Queued,
    UpdatedQueued,
    DeferredUntilRunningCompletes,
    Unchanged,
}

#[derive(Clone, Debug, PartialEq)]
pub enum NavigationCompletion {
    Accepted {
        request: NavigationRequest,
        path: Option<NavigationPath>,
    },
    /// Discarded the obsolete result and queued a route to the newest
    /// destination, using the ship position supplied at completion time.
    Requeued { ship_id: i64, lifecycle_id: u64 },
    Discarded,
}

#[derive(Clone, Debug, Default, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
struct ShipPathOwner {
    lifecycle_id: u64,
    request_id: u64,
    /// A queue entry exists for this ship; it can be reused across multiple
    /// destination updates rather than accumulating stale jobs.
    queued: bool,
    waiting: Option<NavigationRequest>,
    running: Option<NavigationRequest>,
    deferred: Option<NavigationRequest>,
}

/// All authoritative ship/path identity and source FIFO state at a fixed
/// gameplay boundary. OS worker threads and search results are NOT saved:
/// interrupted jobs must be reclaimed using each ship's restored position.
#[derive(serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NavigationRequestsCheckpoint {
    schema_version: u32,
    next_lifecycle_id: u64,
    next_request_id: u64,
    ships: HashMap<i64, ShipPathOwner>,
    waiting_ship_ids: VecDeque<(i64, u64)>,
    claimable_waiting: usize,
}

/// The Level's path worker scheduler may run one or several requests at a
/// time. No mutable state, queues or job IDs are process-global.
#[derive(Default)]
pub struct NavigationRequests {
    next_lifecycle_id: u64,
    next_request_id: u64,
    ships: HashMap<i64, ShipPathOwner>,
    /// Queue tokens include the ship incarnation, so retiring/reusing a
    /// numeric ID cannot claim an obsolete position in the source FIFO.
    waiting_ship_ids: VecDeque<(i64, u64)>,
    /// Exact number of claimable waiting jobs, maintained only when the
    /// authoritative ship request owner changes state. Stale queue IDs from
    /// dead ships do not make each path-worker pump linear in past spawns.
    claimable_waiting: usize,
}

impl NavigationRequests {
    pub fn new() -> Self { Self::default() }

    /// Persist true request/lifecycle ownership, including which searches
    /// were already claimed, without copying the OS worker or path map.
    /// Called only at the shared Level checkpoint boundary.
    pub fn checkpoint(&self) -> NavigationRequestsCheckpoint {
        NavigationRequestsCheckpoint {
            schema_version: 1,
            next_lifecycle_id: self.next_lifecycle_id,
            next_request_id: self.next_request_id,
            ships: self.ships.clone(),
            waiting_ship_ids: self.waiting_ship_ids.clone(),
            claimable_waiting: self.claimable_waiting,
        }
    }

    /// Reclaim in-flight searches on a new host, preserving original
    /// request/lifecycle IDs and pending FIFO tokens. An obsolete running
    /// result is never accepted after restart; if a newer deferred request
    /// exists it takes precedence and starts at the RESTORED ship position.
    ///
    /// The position callback must use the same authoritative CombatWorld
    /// being restored at this fixed-tick boundary. Validate everything and
    /// prepare a new queue before replacing any live scheduler state.
    pub fn restore_checkpoint(
        &mut self,
        checkpoint: NavigationRequestsCheckpoint,
        mut ship_position: impl FnMut(i64) -> Option<Vec2>,
    ) -> Result<(), NavigationRequestError> {
        if checkpoint.schema_version != 1 {
            return Err(NavigationRequestError::InvalidRequest);
        }
        let valid_job = |ship_id: i64, owner: &ShipPathOwner,
            job: &NavigationRequest, latest: bool| {
            job.ship_id == ship_id && job.lifecycle_id == owner.lifecycle_id &&
            job.request_id > 0 &&
            job.request_id <= checkpoint.next_request_id &&
            (!latest || job.request_id == owner.request_id) &&
            job.start.is_finite() && job.destination.is_finite() &&
            job.ship_clearance >= 0
        };
        for (&ship_id, owner) in &checkpoint.ships {
            if ship_id <= 0 || owner.lifecycle_id == 0 ||
                owner.lifecycle_id > checkpoint.next_lifecycle_id ||
                owner.request_id > checkpoint.next_request_id ||
                owner.waiting.is_some() && owner.running.is_some() ||
                owner.deferred.is_some() && owner.running.is_none()
            {
                return Err(NavigationRequestError::InvalidRequest);
            }
            if owner.waiting.as_ref().is_some_and(|job|
                !valid_job(ship_id, owner, job, true)) ||
               owner.running.as_ref().is_some_and(|job|
                !valid_job(ship_id, owner, job, false)) ||
               owner.deferred.as_ref().is_some_and(|job|
                !valid_job(ship_id, owner, job, true)) {
                return Err(NavigationRequestError::InvalidRequest);
            }
        }

        // Keep surviving waiting jobs in their original source FIFO order.
        // Stale queue tokens are disposable; a previously claimed running
        // job is appended after still-waiting jobs for the new worker pool.
        let mut owners = checkpoint.ships;
        let mut queue = VecDeque::new();
        let mut claimed = std::collections::HashSet::new();
        for (ship_id, lifecycle_id) in checkpoint.waiting_ship_ids {
            if let Some(owner) = owners.get(&ship_id) {
                if owner.lifecycle_id == lifecycle_id && owner.queued &&
                    owner.waiting.is_some() && claimed.insert(ship_id)
                {
                    queue.push_back((ship_id, lifecycle_id));
                }
            }
        }
        let mut recovered = Vec::new();
        for (&ship_id, owner) in &mut owners {
            if let Some(running) = owner.running.take() {
                let latest = owner.deferred.take().or_else(||
                    (running.request_id == owner.request_id).then_some(running));
                if let Some(mut job) = latest {
                    job.start = ship_position(ship_id)
                        .filter(|position| position.is_finite())
                        .ok_or(NavigationRequestError::InvalidRequest)?;
                    owner.waiting = Some(job);
                    recovered.push((job.request_id, ship_id, owner.lifecycle_id));
                }
            }
            owner.queued = claimed.contains(&ship_id);
            if owner.waiting.is_some() && !owner.queued {
                recovered.push((
                    owner.waiting.expect("present").request_id,
                    ship_id,
                    owner.lifecycle_id,
                ));
            }
        }
        recovered.sort_unstable();
        for (_, ship_id, lifecycle_id) in recovered {
            if !claimed.insert(ship_id) {
                continue;
            }
            owners.get_mut(&ship_id).expect("validated owner").queued = true;
            queue.push_back((ship_id, lifecycle_id));
        }
        let waiting = owners.values().filter(|owner| owner.waiting.is_some()).count();
        if waiting != queue.len() {
            return Err(NavigationRequestError::InvalidRequest);
        }
        self.next_lifecycle_id = checkpoint.next_lifecycle_id;
        self.next_request_id = checkpoint.next_request_id;
        self.ships = owners;
        self.waiting_ship_ids = queue;
        self.claimable_waiting = waiting;
        Ok(())
    }

    /// Starting a new ship instance creates a lifecycle identity that cannot
    /// accept a completion belonging to any earlier pooled object.
    pub fn register_ship(&mut self, ship_id: i64) -> Result<u64, NavigationRequestError> {
        if ship_id <= 0 || self.ships.contains_key(&ship_id) {
            return Err(NavigationRequestError::DuplicateShip);
        }
        let lifecycle = self.next_lifecycle_id.checked_add(1)
            .ok_or(NavigationRequestError::IdExhausted)?;
        self.next_lifecycle_id = lifecycle;
        self.ships.insert(ship_id, ShipPathOwner {
            lifecycle_id: lifecycle, ..ShipPathOwner::default()
        });
        Ok(lifecycle)
    }

    /// Killed, deactivated or released ships cannot own any queued result.
    /// Already-running jobs remain free to finish, but their results vanish.
    pub fn retire_ship(&mut self, ship_id: i64) {
        if let Some(owner) = self.ships.remove(&ship_id) {
            if owner.waiting.is_some() && owner.running.is_none() {
                self.claimable_waiting -= 1;
            }
            self.compact_obsolete_queue();
        }
    }

    /// An episode/pooled-object reset renews lifecycle ownership even if the
    /// public ship ID is retained by its caller.
    pub fn renew_lifecycle(&mut self, ship_id: i64) -> Result<u64, NavigationRequestError> {
        if !self.ships.contains_key(&ship_id) {
            return Err(NavigationRequestError::UnknownShip);
        }
        let lifecycle = self.next_lifecycle_id.checked_add(1)
            .ok_or(NavigationRequestError::IdExhausted)?;
        self.next_lifecycle_id = lifecycle;
        let previous = &self.ships[&ship_id];
        if previous.waiting.is_some() && previous.running.is_none() {
            self.claimable_waiting -= 1;
        }
        // The previous incarnation's FIFO token is obsolete. The first
        // new request will enqueue a fresh token at the correct tail.
        self.ships.insert(ship_id, ShipPathOwner {
            lifecycle_id: lifecycle,
            ..ShipPathOwner::default()
        });
        self.compact_obsolete_queue();
        Ok(lifecycle)
    }

    /// Invalidating a request does not free the running worker slot. Unity
    /// still occupies a worker thread until its old search returns, even
    /// after StopMoving invalidates the result. Keep that ownership until
    /// completion; otherwise repeated orders can launch duplicate searches.
    pub fn invalidate(&mut self, ship_id: i64) -> Result<(), NavigationRequestError> {
        if !self.ships.contains_key(&ship_id) {
            return Err(NavigationRequestError::UnknownShip);
        }
        let new_id = self.reserve_request_id()?;
        let owner = self.ships.get_mut(&ship_id).expect("checked");
        owner.request_id = new_id;
        if owner.waiting.take().is_some() && owner.running.is_none() {
            self.claimable_waiting -= 1;
        }
        // Never clear owner.running here: CPU work still owns its slot.
        owner.deferred = None;
        self.compact_obsolete_queue();
        Ok(())
    }

    pub fn is_pathfinding(&self, ship_id: i64) -> bool {
        self.ships.get(&ship_id).is_some_and(|owner|
            owner.waiting.is_some() || owner.deferred.is_some() ||
            owner.running.is_some_and(|job|
                job.request_id == owner.request_id))
    }

    pub fn lifecycle_id(&self, ship_id: i64) -> Option<u64> {
        self.ships.get(&ship_id).map(|owner| owner.lifecycle_id)
    }

    /// Implements the relevant MoveToPoint behavior:
    ///   - duplicate destination while searching => leave request alone;
    ///   - newly detected obstacle or changed destination while running =>
    ///     invalidate, retain only latest, and wait for old worker;
    ///   - changed destination while still queued => replace in place.
    /// The game controller decides whether an obstacle actually requires A*.
    pub fn request(
        &mut self,
        ship_id: i64,
        start: Vec2,
        destination: Vec2,
        ship_clearance: i32,
        newly_found_obstacle: bool,
    ) -> Result<NavigationRequestDisposition, NavigationRequestError> {
        if !start.is_finite() || !destination.is_finite() || ship_clearance < 0 {
            return Err(NavigationRequestError::InvalidRequest);
        }
        let owner = self.ships.get(&ship_id)
            .ok_or(NavigationRequestError::UnknownShip)?;
        let existing_destination = owner.deferred.or(owner.waiting).or(owner.running)
            .map(|job| job.destination);
        if existing_destination == Some(destination) && !newly_found_obstacle {
            return Ok(NavigationRequestDisposition::Unchanged);
        }
        let lifecycle_id = owner.lifecycle_id;
        let running = owner.running.is_some();
        let queued = owner.waiting.is_some();
        let request_id = self.reserve_request_id()?;
        let request = NavigationRequest {
            ship_id, lifecycle_id, request_id,
            start, destination, ship_clearance,
        };
        let owner = self.ships.get_mut(&ship_id).expect("checked");
        owner.request_id = request_id;
        if running {
            owner.deferred = Some(request);
            return Ok(NavigationRequestDisposition::DeferredUntilRunningCompletes);
        }
        if owner.waiting.is_none() {
            self.claimable_waiting += 1;
        }
        owner.waiting = Some(request);
        if !owner.queued {
            owner.queued = true;
            self.waiting_ship_ids.push_back((ship_id, lifecycle_id));
        }
        Ok(if queued {
            NavigationRequestDisposition::UpdatedQueued
        } else {
            NavigationRequestDisposition::Queued
        })
    }

    /// True when the pending queue includes a claimable request.
    pub fn has_waiting_jobs(&self) -> bool {
        self.claimable_waiting != 0
    }

    /// On-demand pending work and retained queue slots. The difference
    /// consists of stale IDs/deferred slots awaiting lazy disposal; it
    /// must not be mistaken for a currently executable path request.
    pub fn queue_gauges(&self) -> (usize, usize) {
        (self.claimable_waiting, self.waiting_ship_ids.len())
    }

    /// Worker claims the next still-current job. At most one queue slot per
    /// ship is present; coalescing never creates an obsolete worker tail.
    pub fn next(&mut self) -> Option<NavigationRequest> {
        while let Some((ship_id, lifecycle_id)) = self.waiting_ship_ids.pop_front() {
            let Some(owner) = self.ships.get_mut(&ship_id) else { continue };
            if owner.lifecycle_id != lifecycle_id || !owner.queued {
                continue;
            }
            owner.queued = false;
            let Some(job) = owner.waiting.take() else { continue };
            if owner.running.is_some() { continue; }
            self.claimable_waiting -= 1;
            owner.running = Some(job);
            return Some(job);
        }
        None
    }

    /// Worker completion is accepted only if it still owns both the ship
    /// lifecycle and the latest request ID. A superseded running request
    /// starts at the ship's *current* position, not its stale start cell.
    pub fn complete(
        &mut self,
        job: NavigationRequest,
        path: Option<NavigationPath>,
        current_position: Vec2,
    ) -> Result<NavigationCompletion, NavigationRequestError> {
        if !current_position.is_finite() {
            return Err(NavigationRequestError::InvalidRequest);
        }
        let Some(owner) = self.ships.get_mut(&job.ship_id) else {
            return Ok(NavigationCompletion::Discarded);
        };
        if owner.lifecycle_id != job.lifecycle_id || owner.running != Some(job) {
            return Ok(NavigationCompletion::Discarded);
        }
        owner.running = None;
        if let Some(mut deferred) = owner.deferred.take() {
            deferred.start = current_position;
            if owner.waiting.is_none() {
                self.claimable_waiting += 1;
            }
            owner.waiting = Some(deferred);
            if !owner.queued {
                owner.queued = true;
                self.waiting_ship_ids.push_back((job.ship_id, job.lifecycle_id));
            }
            return Ok(NavigationCompletion::Requeued {
                ship_id: job.ship_id,
                lifecycle_id: job.lifecycle_id,
            });
        }
        if owner.request_id != job.request_id {
            return Ok(NavigationCompletion::Discarded);
        }
        Ok(NavigationCompletion::Accepted { request: job, path })
    }

    /// A search failed at the engine boundary (invalid map/geometry), not
    /// because its destination was unreachable. Release its CPU slot and
    /// clear any pending continuation. Never report such an engine error as
    /// an ordinary Unity "path not found" response.
    pub fn abandon_failed_job(&mut self, job: NavigationRequest) {
        let Some(owner) = self.ships.get_mut(&job.ship_id) else { return };
        if owner.lifecycle_id != job.lifecycle_id ||
            owner.running != Some(job) {
            return;
        }
        owner.running = None;
        if owner.waiting.take().is_some() {
            self.claimable_waiting -= 1;
        }
        owner.deferred = None;
        self.compact_obsolete_queue();
    }

    /// Obsolete tokens are cheap until they dominate the source FIFO.
    /// Compact only after at least 128 more retained slots than useful
    /// claims accumulate, amortizing cleanup over retirements/cancellations
    /// rather than rescanning on each fixed path-worker pump.
    fn compact_obsolete_queue(&mut self) {
        if self.waiting_ship_ids.len() <=
            self.claimable_waiting.saturating_mul(2).saturating_add(128)
        {
            return;
        }
        let owners = &self.ships;
        self.waiting_ship_ids.retain(|&(ship_id, lifecycle)| owners
            .get(&ship_id).is_some_and(|owner|
                owner.lifecycle_id == lifecycle && owner.queued &&
                owner.waiting.is_some() && owner.running.is_none(),
            ));
        // Invalidation can leave a queue token but no waiting request.
        // Clearing its bit here lets a later real request append at the
        // correct tail; valid waiting owners keep their original place.
        for owner in self.ships.values_mut() {
            if owner.queued && (owner.waiting.is_none() || owner.running.is_some()) {
                owner.queued = false;
            }
        }
        debug_assert_eq!(self.waiting_ship_ids.len(), self.claimable_waiting);
    }

    fn reserve_request_id(&mut self) -> Result<u64, NavigationRequestError> {
        let next = self.next_request_id.checked_add(1)
            .ok_or(NavigationRequestError::IdExhausted)?;
        self.next_request_id = next;
        Ok(next)
    }
}
