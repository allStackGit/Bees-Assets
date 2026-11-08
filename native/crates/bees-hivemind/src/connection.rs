//! One transport-neutral HiveMind connection owner for native game and
//! worker clients. Unity's Socket assigns every request type a unique
//! Utilities.Unique53Hash on the SAME WebSocket; strategy, targeting and
//! StoreCommands cannot use independent counters or session registries.
//!
//! This object owns the already-implemented correlation session and
//! retry/acknowledgement ledger. The socket adapter still owns TLS,
//! authentication, server connection lifetime and actual send/receive.
//! A checkpoint must be DURABLY persisted before any newly allocated
//! request or StoreCommands payload is transmitted. It does not contain
//! server credentials or claim an in-memory queue is persistent storage.

use serde::{Deserialize, Serialize};
use std::io::Write;
use std::collections::BTreeMap;

const CONNECTION_CHECKPOINT_VERSION: u32 = 1;

use crate::outcomes::{
    OutcomeBatch, PendingStoreCommands, StoreAcknowledgement,
    RecordedOutcomeBatch,
};
use crate::protocol::{
    GetStrategyRequest, GetTargetingRequest, HiveMindProtocolError,
    RequestHashAllocator, StoreCommandsRequest, StoreCommandsResponse,
};
use crate::session::{
    HiveMindSession, HiveMindSessionError, SquadLifetime,
};

/// Only state that remains meaningful after a process restart. Strategy
/// and targeting replies still in flight cannot be reconstructed as valid
/// live commands without their original, live Squad instances: they are
/// retired when the WebSocket disconnects and newly requested after a
/// new authenticated Level/session is established.
/// Highest durably queued journal position for one original run and world.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
struct JournalPosition {
    episode_revision: u64,
    event_sequence: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NativeConnectionCheckpoint {
    schema_version: u32,
    hashes: RequestHashAllocator,
    stores: PendingStoreCommands,
    /// Persisted atomically with each retry payload; retained even after
    /// server acknowledgement so an old journal cannot be queued twice.
    #[serde(default)]
    journal_progress: BTreeMap<String, BTreeMap<u64, JournalPosition>>,
}

/// Borrowed serialization view: large StoreCommands queues are streamed
/// directly instead of deep-cloned for every durable checkpoint write.
#[derive(Serialize)]
struct NativeConnectionCheckpointView<'a> {
    schema_version: u32,
    hashes: &'a RequestHashAllocator,
    stores: &'a PendingStoreCommands,
    journal_progress: &'a BTreeMap<String, BTreeMap<u64, JournalPosition>>,
}

impl NativeConnectionCheckpoint {
    pub fn pending_store_count(&self) -> usize {
        self.stores.pending_count()
    }

    /// Enumerate persisted retryable requests, preserving each original Hash
    /// and payload. Ordering is not contractual; a reconnect host may sort
    /// the hashes once without copying the pending outcomes.
    pub fn pending_store_hashes(&self) -> impl Iterator<Item = i64> + '_ {
        self.stores.pending_hashes()
    }

    /// Enumerate terminal 409 records, which require reconciliation rather
    /// than retransmitting the already-partially-committed original batch.
    pub fn pending_reconciliation_hashes(&self) -> impl Iterator<Item = i64> + '_ {
        self.stores.reconciliation_hashes()
    }

    pub fn reconciliation_count(&self) -> usize {
        self.stores.reconciliation_count()
    }
}

#[derive(Debug)]
pub enum NativeConnectionError {
    Protocol(HiveMindProtocolError),
    Session(HiveMindSessionError),
    EmptyOutcomeBatch,
    InvalidJournalIdentity,
}

impl From<HiveMindProtocolError> for NativeConnectionError {
    fn from(value: HiveMindProtocolError) -> Self { Self::Protocol(value) }
}
impl From<HiveMindSessionError> for NativeConnectionError {
    fn from(value: HiveMindSessionError) -> Self { Self::Session(value) }
}

/// No duplicate independent request allocator, no extra reservation ledger.
/// One instance is shared across every native Level using the same
/// authenticated server WebSocket.
pub struct NativeHiveMindConnection {
    hashes: RequestHashAllocator,
    session: HiveMindSession,
    stores: PendingStoreCommands,
    journal_progress: BTreeMap<String, BTreeMap<u64, JournalPosition>>,
}

impl NativeHiveMindConnection {
    /// The caller must supply a new cryptographically random Unity-compatible
    /// 21-bit client prefix, or resume an existing prefix from its durable
    /// NativeConnectionCheckpoint. Never use a fixed shared constant.
    pub fn new(client_prefix: u32) -> Result<Self, HiveMindProtocolError> {
        Ok(Self {
            hashes: RequestHashAllocator::new(client_prefix)?,
            session: HiveMindSession::new(),
            stores: PendingStoreCommands::new(),
            journal_progress: BTreeMap::new(),
        })
    }

    /// Restore pending StoreCommands and the exact monotonically increasing
    /// request-hash sequence. In-flight game strategy requests intentionally
    /// do not revive: their old SquadLifetime has no authority in a newly
    /// reconstructed scene. The host must reconnect/setup its Level.
    pub fn restore(
        snapshot: NativeConnectionCheckpoint,
    ) -> Result<Self, HiveMindProtocolError> {
        if snapshot.schema_version != CONNECTION_CHECKPOINT_VERSION {
            return Err(HiveMindProtocolError::InvalidRequest);
        }
        snapshot.hashes.validate()?;
        if !snapshot.stores.owned_request_hashes()
            .all(|hash| snapshot.hashes.has_issued(hash))
        {
            return Err(HiveMindProtocolError::InvalidRequest);
        }
        Ok(Self {
            hashes: snapshot.hashes,
            session: HiveMindSession::new(),
            stores: snapshot.stores,
            journal_progress: snapshot.journal_progress,
        })
    }

    /// Serialize this checkpoint and commit it atomically to durable
    /// storage BEFORE sending any newly created request. Retain old
    /// checkpoints until the replacement has been confirmed durable.
    /// This snapshot contains no authentication secrets.
    pub fn checkpoint(&self) -> NativeConnectionCheckpoint {
        NativeConnectionCheckpoint {
            schema_version: CONNECTION_CHECKPOINT_VERSION,
            hashes: self.hashes.clone(),
            stores: self.stores.clone(),
            journal_progress: self.journal_progress.clone(),
        }
    }

    /// Write the SAME checkpoint format directly to the caller's writer.
    /// No intermediate JSON Vec and no clone of the pending retry ledger.
    /// The transport must use a durable, crash-safe file/database commit
    /// before sending newly allocated requests. This method writes bytes;
    /// it does not claim to fsync, atomically replace files or persist data.
    pub fn write_checkpoint<W: Write>(
        &self, writer: W,
    ) -> Result<(), serde_json::Error> {
        serde_json::to_writer(writer, &NativeConnectionCheckpointView {
            schema_version: CONNECTION_CHECKPOINT_VERSION,
            hashes: &self.hashes,
            stores: &self.stores,
            journal_progress: &self.journal_progress,
        })
    }

    /// Allocate a Hash for setup, reconnect, matchup, settings or other
    /// authenticated messages that share the very same socket namespace.
    /// A 32-bit counter wrap is an error, not a duplicate server request.
    pub fn next_hash(&mut self) -> Result<i64, HiveMindProtocolError> {
        self.hashes.next_hash()
    }

    /// Create an original-correlation strategy request. A connection
    /// receiving its reply must hand session_mut() to the native Level
    /// driver's receive_strategy_response, which validates the real live
    /// owner before executing the server's selected command.
    pub fn request_strategy(
        &mut self,
        owner: SquadLifetime,
        enemy_owner: Option<SquadLifetime>,
        matchup: String,
        shooting_matchup: String,
        opponent_id: u64,
        banned_strats: impl IntoIterator<Item = String>,
    ) -> Result<GetStrategyRequest, NativeConnectionError> {
        let hash = self.next_hash()?;
        Ok(self.session.request_strategy(
            hash, owner, enemy_owner, matchup, shooting_matchup,
            opponent_id, banned_strats,
        )?)
    }

    pub fn request_targeting(
        &mut self,
        owner: SquadLifetime,
        ships: String,
        opponent_id: u64,
        banned: impl IntoIterator<Item = String>,
    ) -> Result<GetTargetingRequest, NativeConnectionError> {
        let hash = self.next_hash()?;
        Ok(self.session.request_targeting(
            hash, owner, ships, opponent_id, banned,
        )?)
    }

    /// Move a validated batch of genuine game-attributed command,
    /// shooting and targeting outcomes into the existing StoreCommands
    /// retry ledger. The game must compute actual TSV and only invoke
    /// this after it knows which reservations are earned or discarded.
    ///
    /// The returned request is NOT safe to transmit until a checkpoint
    /// containing it has been durably committed. Receiving a 409 later
    /// requires the existing stale-ID reconciliation, never replaying
    /// already committed outcomes as a new Hash.
    pub fn queue_outcomes(
        &mut self, batch: OutcomeBatch,
    ) -> Result<StoreCommandsRequest, NativeConnectionError> {
        if batch.is_empty() {
            return Err(NativeConnectionError::EmptyOutcomeBatch);
        }
        let hash = self.next_hash()?;
        let request = batch.into_request(hash)?;
        self.stores.queue(request.clone())?;
        Ok(request)
    }

    /// Register a NEW run before its first journal event is written.
    /// An empty per-run cursor map is a durable proof that the run began
    /// with journal-aware checkpoint ownership. Pre-upgrade runs absent
    /// from this map cannot be replayed safely: their StoreCommands may
    /// already have been acknowledged with no surviving local evidence.
    pub fn register_journal_run(
        &mut self, run_id: &str,
    ) -> Result<bool, NativeConnectionError> {
        if run_id.trim().is_empty() {
            return Err(NativeConnectionError::InvalidJournalIdentity);
        }
        if self.journal_progress.contains_key(run_id) {
            return Ok(false);
        }
        self.journal_progress.insert(run_id.to_owned(), BTreeMap::new());
        Ok(true)
    }

    /// Atomically associate a journal's source position with a request
    /// inside the canonical connection checkpoint. The host must persist
    /// this connection before transmitting. On recovery, a source position
    /// already checkpointed is never queued again—even after its request
    /// received 200 and left the pending StoreCommands map.
    ///
    /// Source order is monotonic within each (run, world). Interleaved
    /// worlds and independent runs have separate progress markers.
    pub fn queue_journal_outcomes(
        &mut self, recorded: &RecordedOutcomeBatch,
    ) -> Result<Option<StoreCommandsRequest>, NativeConnectionError> {
        let source = &recorded.source;
        if source.run_id.trim().is_empty() ||
            !self.journal_progress.contains_key(&source.run_id)
        {
            // An old unregistered journal may have been committed and
            // acknowledged before this version recorded source cursors.
            // Never reconstruct a second request under a new hash.
            return Err(NativeConnectionError::InvalidJournalIdentity);
        }
        let position = JournalPosition {
            episode_revision: source.episode_revision,
            event_sequence: source.event_sequence,
        };
        if self.journal_progress.get(&source.run_id)
            .and_then(|worlds| worlds.get(&source.world_id))
            .is_some_and(|previous| position <= *previous)
        {
            return Ok(None);
        }
        let request = self.queue_outcomes(recorded.batch.clone())?;
        self.journal_progress.entry(source.run_id.clone()).or_default()
            .insert(source.world_id, position);
        Ok(Some(request))
    }

    pub fn retry_store(&self, hash: i64) -> Option<&StoreCommandsRequest> {
        self.stores.retry_payload(hash)
    }

    pub fn acknowledge_store(
        &mut self, response: &StoreCommandsResponse,
    ) -> Result<StoreAcknowledgement, HiveMindProtocolError> {
        self.stores.acknowledge(response)
    }

    pub fn pending_store_count(&self) -> usize {
        self.stores.pending_count()
    }

    pub fn pending_reconciliation_count(&self) -> usize {
        self.stores.reconciliation_count()
    }

    /// Borrow the live retry ledger without cloning its outcome payloads.
    pub fn pending_store_hashes(&self) -> impl Iterator<Item = i64> + '_ {
        self.stores.pending_hashes()
    }

    /// A partially committed batch must be reconciled, never retransmitted.
    pub fn pending_reconciliation_hashes(&self) -> impl Iterator<Item = i64> + '_ {
        self.stores.reconciliation_hashes()
    }

    pub fn pending_request_count(&self) -> usize {
        self.session.pending_count()
    }

    /// The socket may send a strategy request only if the single shared
    /// session still owns the exact request hash. No duplicate request
    /// registry is maintained by the transport.
    pub fn matches_live_strategy_request(
        &self, request: &GetStrategyRequest,
    ) -> bool {
        self.session.matches_live_strategy_request(request)
    }

    pub fn matches_live_targeting_request(
        &self, request: &GetTargetingRequest,
    ) -> bool {
        self.session.matches_live_targeting_request(request)
    }

    pub fn stale_reconciliation_ids(&self, hash: i64) -> Option<&[i64]> {
        self.stores.stale_reconciliation_ids(hash)
    }

    pub fn finish_reconciliation(&mut self, hash: i64) -> bool {
        self.stores.finish_reconciliation(hash)
    }

    /// The one connection-owned strategy/targeting registry, borrowed by
    /// the native driver during its original-lifetime response handoff.
    pub fn session_mut(&mut self) -> &mut HiveMindSession {
        &mut self.session
    }

    /// A socket disconnect terminates its transient request correlation.
    /// Pending StoreCommands and hash sequence survive; the returned
    /// unresolved request hashes are for explicit transport diagnostics.
    pub fn retire_connection(&mut self) -> Vec<i64> {
        self.session.retire_connection()
    }
}
