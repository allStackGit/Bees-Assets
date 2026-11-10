//! Bounded socket I/O for the shared game/headless runtime. The simulation
//! thread retains its one durable connection; the transport holds only wire
//! messages and ephemeral in-flight hashes, never a second outcome ledger.
use std::{collections::HashSet, io, sync::mpsc::{self, Receiver, SyncSender, TryRecvError, TrySendError}, thread, time::Duration};
use bees_hivemind::protocol::{GetStrategyRequest, GetTargetingRequest};
use crate::{durable_connection::DurableHiveMindConnection,
    socket::{HiveMindSocketError, HiveMindSocketEvent, NativeHiveMindSocket,
        NativeSocketMeasurements, PreparedSocketRequest, SetupLevelIdentity}};

const QUEUE_CAPACITY: usize = 16;

#[derive(Debug)]
pub enum NativeSocketWorkerError {
    Socket(HiveMindSocketError),
    Spawn(io::Error),
    Backpressure,
    Closed,
}
impl From<HiveMindSocketError> for NativeSocketWorkerError {
    fn from(error: HiveMindSocketError) -> Self { Self::Socket(error) }
}

struct WorkerEvent {
    result: Result<Option<HiveMindSocketEvent>, HiveMindSocketError>,
    measurements: NativeSocketMeasurements,
}
enum WorkerCommand { Send(PreparedSocketRequest), Close }

pub struct NativeSocketWorker {
    outgoing: SyncSender<WorkerCommand>,
    incoming: Receiver<WorkerEvent>,
    ready: bool,
    closed: bool,
    // Flow control only. Actual requests/retries remain in the connection.
    queued_stores: HashSet<i64>,
    // Socket-local flow control only; all pending request identity and
    // outcome correlation stays inside DurableHiveMindConnection.session.
    queued_requests: HashSet<i64>,
    measurements: NativeSocketMeasurements,
    queue_backpressure: u64,
}

impl NativeSocketWorker {
    /// DNS, TLS, handshake and all blocking reads/writes happen off the
    /// simulation/render thread. No automatic reconnect or retry loop.
    pub fn start(url: String, allow_insecure_loopback: bool) -> Result<Self, NativeSocketWorkerError> {
        let (outgoing, commands) = mpsc::sync_channel(QUEUE_CAPACITY);
        let (events, incoming) = mpsc::sync_channel(QUEUE_CAPACITY);
        thread::Builder::new().name("bees-hivemind-io".into()).spawn(move || {
            let mut socket = match NativeHiveMindSocket::connect(&url, allow_insecure_loopback) {
                Ok(socket) => socket,
                Err(error) => { let _ = events.send(WorkerEvent { result: Err(error),
                    measurements: NativeSocketMeasurements::default() }); return; }
            };
            let run = |socket: &mut NativeHiveMindSocket| -> Result<(), HiveMindSocketError> {
                socket.set_io_timeout(Duration::from_millis(20), Duration::from_secs(5))?;
                loop {
                    // A bounded send pass preserves receive/ack progress even
                    // under sustained strategy traffic or full output queues.
                    for _ in 0..QUEUE_CAPACITY {
                        match commands.try_recv() {
                            Ok(WorkerCommand::Send(request)) => socket.send_prepared(request)?,
                            Ok(WorkerCommand::Close) => {
                                socket.close_and_wait(Duration::from_secs(1))?;
                                let _ = events.send(WorkerEvent {
                                    result: Ok(None),
                                    measurements: socket.measurements(),
                                });
                                return Ok(());
                            }
                            Err(TryRecvError::Disconnected) => {
                                let _ = socket.close();
                                return Ok(());
                            }
                            Err(TryRecvError::Empty) => break,
                        }
                    }
                    if let Some(event) = socket.poll_event()? {
                        if events.send(WorkerEvent { result: Ok(Some(event)), measurements: socket.measurements() }).is_err() {
                            let _ = socket.close();
                            return Ok(());
                        }
                    }
                }
            };
            if let Err(error) = run(&mut socket) {
                let _ = events.send(WorkerEvent { result: Err(error), measurements: socket.measurements() });
            }
        }).map_err(NativeSocketWorkerError::Spawn)?;
        Ok(Self { outgoing, incoming, ready: false, closed: false, queued_stores: HashSet::new(),
            queued_requests: HashSet::new(),
            measurements: NativeSocketMeasurements::default(), queue_backpressure: 0 })
    }

    pub fn ready(&self) -> bool { self.ready }
    pub fn closed(&self) -> bool { self.closed }
    /// Snapshot at the most recently delivered socket event.
    pub fn measurements(&self) -> NativeSocketMeasurements { self.measurements }
    pub fn queue_backpressure(&self) -> u64 { self.queue_backpressure }

    fn enqueue(&mut self, request: PreparedSocketRequest) -> Result<(), NativeSocketWorkerError> {
        match self.outgoing.try_send(WorkerCommand::Send(request)) {
            Ok(()) => Ok(()),
            Err(TrySendError::Full(_)) => {
                self.queue_backpressure += 1;
                Err(NativeSocketWorkerError::Backpressure)
            }
            Err(TrySendError::Disconnected(_)) => { self.ready = false; Err(NativeSocketWorkerError::Closed) }
        }
    }

    pub fn send_setup(&mut self, connection: &mut DurableHiveMindConnection, identity: SetupLevelIdentity<'_>)
        -> Result<(), NativeSocketWorkerError>
    {
        let request = PreparedSocketRequest::setup(connection, identity)?;
        self.enqueue(request)?;
        self.ready = false;
        Ok(())
    }

    fn enqueue_hivemind(&mut self, hash: i64, prepared: PreparedSocketRequest)
        -> Result<(), NativeSocketWorkerError>
    {
        if self.queued_requests.contains(&hash) { return Ok(()); }
        self.enqueue(prepared)?;
        self.queued_requests.insert(hash);
        Ok(())
    }

    pub fn send_strategy(&mut self, connection: &DurableHiveMindConnection, request: &GetStrategyRequest)
        -> Result<(), NativeSocketWorkerError>
    {
        if !self.ready { return Err(HiveMindSocketError::SetupRequired.into()); }
        let prepared = PreparedSocketRequest::strategy(connection, request)?;
        self.enqueue_hivemind(request.hash, prepared)
    }

    pub fn send_targeting(&mut self, connection: &DurableHiveMindConnection, request: &GetTargetingRequest)
        -> Result<(), NativeSocketWorkerError>
    {
        if !self.ready { return Err(HiveMindSocketError::SetupRequired.into()); }
        let prepared = PreparedSocketRequest::targeting(connection, request)?;
        self.enqueue_hivemind(request.hash, prepared)
    }

    /// Drain eligible pending requests without allocating NEW request hashes.
    /// A full I/O channel stops this bounded pass; the original session
    /// retains unqueued requests for the next pump. This is not a game
    /// command queue, retry authority or additional outcome registry.
    pub fn queue_pending_hivemind_requests(
        &mut self, connection: &DurableHiveMindConnection,
    ) -> Result<usize, NativeSocketWorkerError> {
        if !self.ready { return Ok(0); }
        let session = connection.connection().session();
        self.queued_requests.retain(|hash| session.is_live_request_hash(*hash));
        // Unity allocates every request hash on ONE WebSocket in
        // enqueue order. Do not send all targetings before strategies
        // merely because they use separate session maps: that can starve
        // the strategy following a prior targeting response. A temporary
        // sorted set of keys changes no durable state and keeps lookup,
        // owner validity and the original payload inside the session.
        let mut due = Vec::new();
        due.extend(session.live_targeting_requests()
            .map(|request| (request.hash, true)));
        due.extend(session.live_strategy_requests()
            .map(|request| (request.hash, false)));
        due.sort_unstable_by_key(|&(hash, _)| hash);
        let mut queued = 0;
        for (hash, is_targeting) in due {
            if self.queued_requests.contains(&hash) { continue; }
            let prepared = if is_targeting {
                let request = session.resend_targeting_request(hash)
                    .ok_or(HiveMindSocketError::InvalidRequestOwnership)?;
                PreparedSocketRequest::targeting(connection, request)?
            } else {
                let request = session.resend_request(hash)
                    .ok_or(HiveMindSocketError::InvalidRequestOwnership)?;
                PreparedSocketRequest::strategy(connection, request)?
            };
            match self.enqueue_hivemind(hash, prepared) {
                Ok(()) => queued += 1,
                Err(NativeSocketWorkerError::Backpressure) => break,
                Err(error) => return Err(error),
            }
        }
        Ok(queued)
    }

    /// Deliberate retry of the exact pending hash after a nonterminal
    /// server failure. Do not create a new learning reservation.
    pub fn retry_hivemind(&mut self, hash: i64) {
        self.queued_requests.remove(&hash);
    }

    /// Queue original durable requests once on this socket. Backpressure
    /// leaves unqueued hashes in the canonical owner for the next pump.
    pub fn queue_pending_stores(&mut self, connection: &DurableHiveMindConnection)
        -> Result<usize, NativeSocketWorkerError>
    {
        if !self.ready { return Ok(0); }
        let mut queued = 0;
        for hash in connection.pending_store_hashes().map_err(HiveMindSocketError::from)? {
            if self.queued_stores.contains(&hash) { continue; }
            let request = connection.retry_payload(hash).map_err(HiveMindSocketError::from)?
                .ok_or(HiveMindSocketError::InvalidRequestOwnership)?;
            match self.enqueue(PreparedSocketRequest::store(connection, request)?) {
                Ok(()) => { self.queued_stores.insert(hash); queued += 1; }
                Err(NativeSocketWorkerError::Backpressure) => break,
                Err(error) => return Err(error),
            }
        }
        // Acknowledged hashes need no further transport flow-control state.
        self.queued_stores.retain(|&hash| connection.connection().retry_store(hash).is_some());
        Ok(queued)
    }

    /// Explicit retry after a nonterminal server failure. It remains the
    /// original persisted payload/hash; no per-frame resend timer is invented.
    pub fn retry_store(&mut self, hash: i64) { self.queued_stores.remove(&hash); }

    /// A fresh worker replays pending stores after authenticated setup.
    /// Closing/dropping a worker never alters the connection's durable queue.
    pub fn request_close(&mut self) -> Result<(), NativeSocketWorkerError> {
        if self.closed { return Ok(()); }
        match self.outgoing.try_send(WorkerCommand::Close) {
            Ok(()) => { self.ready = false; Ok(()) }
            Err(TrySendError::Full(_)) => Err(NativeSocketWorkerError::Backpressure),
            Err(TrySendError::Disconnected(_)) => Err(NativeSocketWorkerError::Closed),
        }
    }

    pub fn poll_event(&mut self) -> Result<Option<HiveMindSocketEvent>, NativeSocketWorkerError> {
        if self.closed { return Ok(None); }
        match self.incoming.try_recv() {
            Ok(event) => {
                self.measurements = event.measurements;
                match event.result {
                    Ok(Some(event)) => {
                        if matches!(event, HiveMindSocketEvent::SetupAccepted { .. }) { self.ready = true; }
                        Ok(Some(event))
                    }
                    Ok(None) => {
                        self.ready = false;
                        self.closed = true;
                        Ok(None)
                    }
                    Err(error) => { self.ready = false; Err(error.into()) }
                }
            }
            Err(TryRecvError::Empty) => Ok(None),
            Err(TryRecvError::Disconnected) => { self.ready = false; Err(NativeSocketWorkerError::Closed) }
        }
    }
}
