//! Per-WebSocket Hive Mind request and squad lifecycle ownership.
//!
//! The *server* owns the authenticated connection's shared Game and durable
//! OutcomeId reservations. This registry does not create a Game for each
//! Unity Level: many Levels may share one connection. It owns only local
//! pending request hashes and the exact squad instance that may apply each
//! server decision. Reconnect/session changes are handled by the transport.

use std::collections::HashMap;

use crate::protocol::{
    GetStrategyRequest, GetStrategyResponse, HiveMindProtocolError,
    ValidatedStrategySelection,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct SquadLifetime {
    pub level_id: i64,
    pub squad_id: i64,
    pub lifecycle_id: u64,
}

impl SquadLifetime {
    pub fn valid(self) -> bool {
        self.level_id > 0 && self.squad_id > 0 && self.lifecycle_id > 0
    }
}

#[derive(Clone, Debug)]
pub struct PendingStrategy {
    pub owner: SquadLifetime,
    pub enemy_squad_id: Option<i64>,
    pub request: GetStrategyRequest,
    retired: bool,
}

#[derive(Clone, Debug)]
pub enum StrategyReceipt {
    Accepted {
        owner: SquadLifetime,
        enemy_squad_id: Option<i64>,
        selection: ValidatedStrategySelection,
    },
    /// Caller MUST explicitly discard both returned reservation IDs via
    /// StoreCommands. This is not a successful command execution.
    Retired {
        owner: SquadLifetime,
        reservation_ids: Vec<i64>,
    },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum HiveMindSessionError {
    InvalidOwner,
    DuplicateRequestHash,
    UnknownRequest,
    InvalidResponse(HiveMindProtocolErrorCode),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum HiveMindProtocolErrorCode {
    InvalidRequest,
    UnknownStrategy,
    StrategyIdentityMismatch,
    BannedStrategy,
    RequestIdentityMismatch,
    InvalidReservation,
    DuplicateReservation,
    InvalidResponse,
}

impl From<&HiveMindProtocolError> for HiveMindProtocolErrorCode {
    fn from(value: &HiveMindProtocolError) -> Self {
        match value {
            HiveMindProtocolError::InvalidRequest => Self::InvalidRequest,
            HiveMindProtocolError::UnknownStrategy => Self::UnknownStrategy,
            HiveMindProtocolError::StrategyIdentityMismatch =>
                Self::StrategyIdentityMismatch,
            HiveMindProtocolError::BannedStrategy => Self::BannedStrategy,
            HiveMindProtocolError::RequestIdentityMismatch =>
                Self::RequestIdentityMismatch,
            HiveMindProtocolError::InvalidReservation => Self::InvalidReservation,
            HiveMindProtocolError::DuplicateReservation => Self::DuplicateReservation,
            HiveMindProtocolError::InvalidResponse => Self::InvalidResponse,
        }
    }
}

impl From<HiveMindProtocolError> for HiveMindSessionError {
    fn from(value: HiveMindProtocolError) -> Self {
        Self::InvalidResponse((&value).into())
    }
}

/// All pending strategy requests on ONE WebSocket, regardless of how many
/// Levels share its Hive Mind Game. No process-global mutable request map.
#[derive(Default)]
pub struct HiveMindSession {
    pending: HashMap<i64, PendingStrategy>,
    latest_by_squad: HashMap<SquadLifetime, i64>,
}

impl HiveMindSession {
    pub fn new() -> Self { Self::default() }

    pub fn pending_count(&self) -> usize { self.pending.len() }

    /// Reissuing an order for a squad retires its prior request but retains
    /// it until a late response can be converted into explicit discards.
    /// The caller must supply a transport-owned connection-unique Hash,
    /// including uniqueness against non-strategy requests.
    /// A new squad lifecycle is a different owner even if a pooled object
    /// reused its numeric Squad.ItemId.
    pub fn request_strategy(
        &mut self,
        hash: i64,
        owner: SquadLifetime,
        enemy_squad_id: Option<i64>,
        matchup: String,
        shooting_matchup: String,
        opponent_id: u64,
        banned_strats: impl IntoIterator<Item = String>,
    ) -> Result<GetStrategyRequest, HiveMindSessionError> {
        if !owner.valid() || enemy_squad_id.is_some_and(|id| id <= 0) {
            return Err(HiveMindSessionError::InvalidOwner);
        }
        // The WebSocket transport assigns unique Hash values across ALL
        // request types (setup, strategy, StoreCommands, etc.), not just
        // get-strategy. This registry must not generate colliding hashes.
        if hash <= 0 || self.pending.contains_key(&hash) {
            return Err(HiveMindSessionError::DuplicateRequestHash);
        }
        let request = GetStrategyRequest::new(
            hash, matchup, shooting_matchup, opponent_id, banned_strats,
        )?;
        if let Some(old) = self.latest_by_squad.insert(owner, hash) {
            if let Some(pending) = self.pending.get_mut(&old) {
                pending.retired = true;
            }
        }
        self.pending.insert(hash, PendingStrategy {
            owner, enemy_squad_id, request: request.clone(), retired: false,
        });
        Ok(request)
    }

    /// Retry the exact original serialized request and Hash. The current
    /// server's per-connection request admission uses Hash as its key.
    pub fn resend_request(&self, hash: i64) -> Option<&GetStrategyRequest> {
        self.pending.get(&hash).filter(|p| !p.retired).map(|p| &p.request)
    }

    /// Called at squad death, enemy-policy ownership change or Level reset.
    /// Keep reservations recoverable if a reply was already in flight.
    pub fn retire_squad(&mut self, owner: SquadLifetime) {
        self.latest_by_squad.remove(&owner);
        for pending in self.pending.values_mut() {
            if pending.owner == owner {
                pending.retired = true;
            }
        }
    }

    /// Route one response to the exact request on this connection. The
    /// caller supplies the current live squad lifetime observed from the
    /// game world; a mismatch produces an explicit discard obligation.
    ///
    /// An invalid response does NOT consume the request or release any
    /// OutcomeIds. Its claimed reservations cannot safely be attributed
    /// to this request; the caller may resend the original hash and must
    /// surface a protocol error rather than guess a settlement.
    pub fn receive_strategy(
        &mut self,
        response: &GetStrategyResponse,
        live_owner: Option<SquadLifetime>,
    ) -> Result<StrategyReceipt, HiveMindSessionError> {
        let pending = self.pending.get(&response.hash)
            .ok_or(HiveMindSessionError::UnknownRequest)?;
        let selection = response.validate_for(&pending.request)?;
        let owner = pending.owner;
        let retired = pending.retired ||
            self.latest_by_squad.get(&owner) != Some(&response.hash) ||
            live_owner != Some(owner);
        let enemy_squad_id = pending.enemy_squad_id;
        self.pending.remove(&response.hash);
        if self.latest_by_squad.get(&owner) == Some(&response.hash) {
            self.latest_by_squad.remove(&owner);
        }
        if retired {
            return Ok(StrategyReceipt::Retired {
                owner,
                reservation_ids: vec![
                    selection.command_outcome_id,
                    selection.shooting_outcome_id,
                ],
            });
        }
        Ok(StrategyReceipt::Accepted { owner, enemy_squad_id, selection })
    }

    /// The server owns no known positive reservation until a response is
    /// received. When a WebSocket dies, return unresolved hashes for the
    /// transport's recovery bookkeeping; do NOT falsely claim that server
    /// reservations were discarded or that learning was committed.
    pub fn retire_connection(&mut self) -> Vec<i64> {
        let mut unresolved: Vec<_> = self.pending.keys().copied().collect();
        unresolved.sort_unstable();
        self.pending.clear();
        self.latest_by_squad.clear();
        unresolved
    }
}
