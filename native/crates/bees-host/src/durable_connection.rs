//! Fail-closed, checkpoint-before-transmit HiveMind transport ownership.
//!
//! Both graphical and headless hosts use the same connection. New request
//! payloads are returned ONLY after their hash/StoreCommands ledger has
//! been durably checkpointed. A failed persistence operation poisons this
//! instance: the caller must stop sending and restore the last committed
//! checkpoint, rather than continue with uncertain request ownership.
//!
//! This is a disk/connection adapter, not a second reservation ledger.
//! Actual WebSocket I/O, TLS, authentication and command credit attribution
//! remain outside this module.

use std::path::PathBuf;

use bees_gameplay::level_host::{
    NativeHiveMindFrameDelivery, NativeHiveMindFrameError,
    NativeLevelActivationContext, NativeLevelBatchDriver,
};
use bees_gameplay::level_rules::{
    NativeLevelRewardMetadata, NativeLevelRules, NativeLevelState,
};
use bees_gameplay::squads::SquadRosterError;
use bees_hivemind::connection::{
    NativeConnectionError, NativeHiveMindConnection,
};
use bees_hivemind::outcomes::{
    OutcomeBatch, StoreAcknowledgement,
};
use bees_hivemind::protocol::{
    GetStrategyRequest, GetTargetingRequest, HiveMindProtocolError,
    StoreCommandsRequest, StoreCommandsResponse,
};
use bees_hivemind::session::SquadLifetime;
use bees_sim::simulation::Simulation;

use crate::checkpoint_file::{
    CheckpointFileError, ConnectionCheckpointFile,
};

#[derive(Debug)]
pub enum DurableConnectionError {
    /// Checkpoint durability is unknown after a write error. Restart and
    /// reload before transmitting another request or accepting a reply.
    Unavailable,
    Persistence(CheckpointFileError),
    Request(NativeConnectionError),
    Protocol(HiveMindProtocolError),
    Inbound(NativeHiveMindFrameError),
}

/// Single owner of one live correlation session, the canonical pending
/// StoreCommands ledger, and its atomic disk checkpoint. No direct mutable
/// access to the underlying connection is exposed.
pub struct DurableHiveMindConnection {
    connection: NativeHiveMindConnection,
    checkpoint: ConnectionCheckpointFile,
    unavailable: bool,
}

impl DurableHiveMindConnection {
    pub fn open(
        checkpoint_path: impl Into<PathBuf>,
        fresh_client_prefix: u32,
    ) -> Result<Self, CheckpointFileError> {
        let checkpoint = ConnectionCheckpointFile::new(checkpoint_path);
        let connection = checkpoint.open_or_initialize(fresh_client_prefix)?;
        Ok(Self { connection, checkpoint, unavailable: false })
    }

    /// Read-only access for NativeLevelBatchDriver::runtime_status.
    pub fn connection(&self) -> &NativeHiveMindConnection {
        &self.connection
    }

    pub fn is_available(&self) -> bool { !self.unavailable }

    fn ensure_available(&self) -> Result<(), DurableConnectionError> {
        if self.unavailable {
            Err(DurableConnectionError::Unavailable)
        } else {
            Ok(())
        }
    }

    fn persist_or_poison(&mut self) -> Result<(), DurableConnectionError> {
        if let Err(error) = self.checkpoint.persist(&self.connection) {
            self.unavailable = true;
            return Err(DurableConnectionError::Persistence(error));
        }
        Ok(())
    }

    /// Also used for setup/settings/matchup messages sharing the socket
    /// Hash namespace. Never return a sendable Hash before durable commit.
    pub fn next_hash(&mut self) -> Result<i64, DurableConnectionError> {
        self.ensure_available()?;
        let hash = self.connection.next_hash()
            .map_err(DurableConnectionError::Protocol)?;
        self.persist_or_poison()?;
        Ok(hash)
    }

    pub fn request_strategy(
        &mut self,
        owner: SquadLifetime,
        enemy_owner: Option<SquadLifetime>,
        matchup: String,
        shooting_matchup: String,
        opponent_id: u64,
        banned_strats: impl IntoIterator<Item = String>,
    ) -> Result<GetStrategyRequest, DurableConnectionError> {
        self.ensure_available()?;
        let request = self.connection.request_strategy(
            owner, enemy_owner, matchup, shooting_matchup,
            opponent_id, banned_strats,
        ).map_err(DurableConnectionError::Request)?;
        self.persist_or_poison()?;
        Ok(request)
    }

    pub fn request_targeting(
        &mut self,
        owner: SquadLifetime,
        ships: String,
        opponent_id: u64,
        banned: impl IntoIterator<Item = String>,
    ) -> Result<GetTargetingRequest, DurableConnectionError> {
        self.ensure_available()?;
        let request = self.connection.request_targeting(
            owner, ships, opponent_id, banned,
        ).map_err(DurableConnectionError::Request)?;
        self.persist_or_poison()?;
        Ok(request)
    }

    pub fn queue_outcomes(
        &mut self,
        batch: OutcomeBatch,
    ) -> Result<StoreCommandsRequest, DurableConnectionError> {
        self.ensure_available()?;
        let request = self.connection.queue_outcomes(batch)
            .map_err(DurableConnectionError::Request)?;
        self.persist_or_poison()?;
        Ok(request)
    }

    /// A raw authenticated server frame is first routed by its original
    /// SquadLifetime. On StoreCommands 200/409, persist the modified retry
    /// ledger before returning the acknowledgement to the socket owner.
    /// Strategy/targeting receipts still require explicit application and
    /// subsequent durable outcome settlement by the caller.
    pub fn receive_frame<M, A>(
        &mut self,
        driver: &mut NativeLevelBatchDriver,
        simulation: &mut Simulation<NativeLevelState<M, A>, NativeLevelRules>,
        frame: &[u8],
    ) -> Result<Option<NativeHiveMindFrameDelivery>, DurableConnectionError>
    where
        M: NativeLevelRewardMetadata + NativeLevelActivationContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
    {
        self.ensure_available()?;
        let delivery = driver.receive_hivemind_frame(
            simulation, &mut self.connection, frame,
        ).map_err(DurableConnectionError::Inbound)?;
        if matches!(
            &delivery,
            Some(NativeHiveMindFrameDelivery::StoreCommands(
                StoreAcknowledgement::DurableCommit |
                StoreAcknowledgement::PartiallyCommitted { .. }
            ))
        ) {
            self.persist_or_poison()?;
        }
        Ok(delivery)
    }

    /// Alternate decoded StoreCommands ingress for clients that handle
    /// WebSocket frame dispatch themselves.
    pub fn acknowledge_store(
        &mut self,
        response: &StoreCommandsResponse,
    ) -> Result<StoreAcknowledgement, DurableConnectionError> {
        self.ensure_available()?;
        let result = self.connection.acknowledge_store(response)
            .map_err(DurableConnectionError::Protocol)?;
        if !matches!(result, StoreAcknowledgement::Rejected { .. }) {
            self.persist_or_poison()?;
        }
        Ok(result)
    }

    /// Deterministic, one-time sorted scan for reconnect resubmission.
    /// The stored payload, not a recreated request, must be retransmitted.
    pub fn pending_store_hashes(&self)
        -> Result<Vec<i64>, DurableConnectionError>
    {
        self.ensure_available()?;
        let mut hashes: Vec<_> =
            self.connection.pending_store_hashes().collect();
        hashes.sort_unstable();
        Ok(hashes)
    }

    pub fn retry_payload(
        &self, hash: i64,
    ) -> Result<Option<&StoreCommandsRequest>, DurableConnectionError> {
        self.ensure_available()?;
        Ok(self.connection.retry_store(hash))
    }

    /// 409s are already partially committed; never retry their original
    /// requests. Expose the exact unresolved reservation IDs to the caller.
    pub fn reconciliation_hashes(&self)
        -> Result<Vec<i64>, DurableConnectionError>
    {
        self.ensure_available()?;
        let mut hashes: Vec<_> =
            self.connection.pending_reconciliation_hashes().collect();
        hashes.sort_unstable();
        Ok(hashes)
    }

    pub fn stale_reconciliation_ids(
        &self, hash: i64,
    ) -> Result<Option<&[i64]>, DurableConnectionError> {
        self.ensure_available()?;
        Ok(self.connection.stale_reconciliation_ids(hash))
    }

    pub fn finish_reconciliation(
        &mut self, hash: i64,
    ) -> Result<bool, DurableConnectionError> {
        self.ensure_available()?;
        let removed = self.connection.finish_reconciliation(hash);
        if removed {
            self.persist_or_poison()?;
        }
        Ok(removed)
    }

    /// Disconnect discards transient strategy/targeting correlations.
    /// Their live Squad instances must be re-registered after reconnect.
    /// Durable StoreCommands requests are NOT retired.
    pub fn retire_socket(&mut self) -> Result<Vec<i64>, DurableConnectionError> {
        self.ensure_available()?;
        Ok(self.connection.retire_connection())
    }
}
