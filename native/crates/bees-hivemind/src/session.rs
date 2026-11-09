//! Per-WebSocket Hive Mind request and squad lifecycle ownership.
//!
//! The *server* owns the authenticated connection's shared Game and durable
//! OutcomeId reservations. This registry does not create a Game for each
//! Unity Level: many Levels may share one connection. It owns only local
//! pending request hashes and the exact squad instance that may apply each
//! server decision. Reconnect/session changes are handled by the transport.

use std::collections::HashMap;

use crate::protocol::{
    GetStrategyRequest, GetStrategyResponse, GetTargetingRequest,
    GetTargetingResponse, HiveMindProtocolError, ValidatedStrategySelection,
    ValidatedTargetingSelection,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
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
    /// Exact enemy lifetime captured when the request was issued. A
    /// delayed server reply must never bind to a pooled squad that later
    /// reused the same numeric ID. No additional server wire field.
    pub enemy_owner: Option<SquadLifetime>,
    pub request: GetStrategyRequest,
    retired: bool,
}

#[derive(Clone, Debug)]
pub struct PendingTargeting {
    pub owner: SquadLifetime,
    pub request: GetTargetingRequest,
    retired: bool,
}

#[derive(Clone, Debug)]
pub enum TargetingReceipt {
    Accepted {
        owner: SquadLifetime,
        selection: ValidatedTargetingSelection,
    },
    /// Delayed targeting selection must be discarded by StoreCommands.
    Retired {
        owner: SquadLifetime,
        reservation_id: i64,
    },
}

#[derive(Clone, Debug)]
pub enum StrategyReceipt {
    Accepted {
        owner: SquadLifetime,
        enemy_owner: Option<SquadLifetime>,
        selection: ValidatedStrategySelection,
    },
    /// Caller MUST explicitly discard both returned reservation IDs via
    /// StoreCommands. This is not a successful command execution.
    Retired {
        owner: SquadLifetime,
        reservation_ids: Vec<i64>,
    },
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum HiveMindSessionError {
    InvalidOwner,
    DuplicateRequestHash,
    UnknownRequest,
    Protocol(HiveMindProtocolError),
}

impl From<HiveMindProtocolError> for HiveMindSessionError {
    fn from(value: HiveMindProtocolError) -> Self {
        Self::Protocol(value)
    }
}

/// All pending strategy requests on ONE WebSocket, regardless of how many
/// Levels share its Hive Mind Game. No process-global mutable request map.
#[derive(Default)]
pub struct HiveMindSession {
    pending: HashMap<i64, PendingStrategy>,
    latest_by_squad: HashMap<SquadLifetime, i64>,
    pending_targeting: HashMap<i64, PendingTargeting>,
    latest_targeting_by_squad: HashMap<SquadLifetime, i64>,
}

impl HiveMindSession {
    pub fn new() -> Self { Self::default() }

    pub fn pending_count(&self) -> usize {
        self.pending.len() + self.pending_targeting.len()
    }

    /// Read-only routing identity for a received strategy reply, including
    /// a request already retired by a later order. The connection remains
    /// the sole owner of pending hashes and server reservation correlation;
    /// Level hosts must not create their own pending-request registry.
    pub fn requested_strategy_owner(&self, hash: i64) -> Option<SquadLifetime> {
        self.pending.get(&hash).map(|pending| pending.owner)
    }

    /// The same read-only correlation for the independent targeting
    /// request registry. A retired request stays identifiable until its
    /// response has produced an explicit StoreCommands discard.
    pub fn requested_targeting_owner(&self, hash: i64) -> Option<SquadLifetime> {
        self.pending_targeting.get(&hash).map(|pending| pending.owner)
    }

    /// Read the original opponent context before consuming a targeting
    /// response. The request-local value must be reused for the ensuing
    /// GetStrategy, never recomputed from a mutable user/session setting.
    pub fn targeting_request_opponent(&self, hash: i64) -> Option<u64> {
        self.pending_targeting.get(&hash).map(|entry| entry.request.opponent_id)
    }

    /// One correlation owner, including requests from all native Levels.
    /// A retired hash remains in the session for late-response settlement,
    /// but is NEVER eligible for network transmission or duplicate polling.
    pub fn has_live_request_for(&self, owner: SquadLifetime) -> bool {
        self.latest_by_squad.get(&owner)
            .and_then(|hash| self.pending.get(hash))
            .is_some_and(|request| !request.retired) ||
        self.latest_targeting_by_squad.get(&owner)
            .and_then(|hash| self.pending_targeting.get(hash))
            .is_some_and(|request| !request.retired)
    }

    /// Transport-only read views; never create another pending-hash map.
    /// The worker sends these ORIGINAL requests once per socket, deferring
    /// backpressured sends without changing the request identity.
    pub fn live_strategy_requests(&self) -> impl Iterator<Item = &GetStrategyRequest> {
        self.pending.iter().filter_map(|(hash, pending)| {
            (!pending.retired &&
                self.latest_by_squad.get(&pending.owner) == Some(hash))
                .then_some(&pending.request)
        })
    }

    pub fn live_targeting_requests(&self) -> impl Iterator<Item = &GetTargetingRequest> {
        self.pending_targeting.iter().filter_map(|(hash, pending)| {
            (!pending.retired &&
                self.latest_targeting_by_squad.get(&pending.owner) == Some(hash))
                .then_some(&pending.request)
        })
    }

    pub fn is_live_request_hash(&self, hash: i64) -> bool {
        self.pending.get(&hash).is_some_and(|pending|
            !pending.retired &&
            self.latest_by_squad.get(&pending.owner) == Some(&hash)) ||
        self.pending_targeting.get(&hash).is_some_and(|pending|
            !pending.retired &&
            self.latest_targeting_by_squad.get(&pending.owner) == Some(&hash))
    }

    /// The socket sends only the exact prepared request still owned by
    /// this live session. This also excludes superseded/retired requests:
    /// the server must never reserve new outcomes for a canceled choice.
    pub fn matches_live_strategy_request(&self, request: &GetStrategyRequest) -> bool {
        self.pending.get(&request.hash).is_some_and(|pending|
            !pending.retired && pending.request == *request
        )
    }

    pub fn matches_live_targeting_request(&self, request: &GetTargetingRequest) -> bool {
        self.pending_targeting.get(&request.hash).is_some_and(|pending|
            !pending.retired && pending.request == *request
        )
    }

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
        enemy_owner: Option<SquadLifetime>,
        matchup: String,
        shooting_matchup: String,
        opponent_id: u64,
        banned_strats: impl IntoIterator<Item = String>,
    ) -> Result<GetStrategyRequest, HiveMindSessionError> {
        if !owner.valid() || enemy_owner.is_some_and(|enemy|
            !enemy.valid() || enemy.level_id != owner.level_id ||
            enemy == owner,
        ) {
            return Err(HiveMindSessionError::InvalidOwner);
        }
        // The WebSocket transport assigns unique Hash values across ALL
        // request types (setup, strategy, StoreCommands, etc.), not just
        // get-strategy. This registry must not generate colliding hashes.
        if hash <= 0 || self.pending.contains_key(&hash) ||
            self.pending_targeting.contains_key(&hash) {
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
            owner, enemy_owner, request: request.clone(), retired: false,
        });
        Ok(request)
    }

    /// Targeting requests use a separate persistent ID registry and may
    /// overlap with get-strategy on this shared connection. Both kinds
    /// still use ONE transport-wide request Hash namespace.
    pub fn request_targeting(
        &mut self,
        hash: i64,
        owner: SquadLifetime,
        ships: String,
        opponent_id: u64,
        banned: impl IntoIterator<Item = String>,
    ) -> Result<GetTargetingRequest, HiveMindSessionError> {
        if !owner.valid() { return Err(HiveMindSessionError::InvalidOwner); }
        if hash <= 0 || self.pending.contains_key(&hash) ||
            self.pending_targeting.contains_key(&hash) {
            return Err(HiveMindSessionError::DuplicateRequestHash);
        }
        let request = GetTargetingRequest::new(hash, ships, opponent_id, banned)?;
        if let Some(old) = self.latest_targeting_by_squad.insert(owner, hash) {
            if let Some(pending) = self.pending_targeting.get_mut(&old) {
                pending.retired = true;
            }
        }
        self.pending_targeting.insert(hash, PendingTargeting {
            owner, request: request.clone(), retired: false,
        });
        Ok(request)
    }

    pub fn resend_targeting_request(&self, hash: i64) -> Option<&GetTargetingRequest> {
        self.pending_targeting.get(&hash).filter(|p| !p.retired)
            .map(|p| &p.request)
    }

    /// The exact targeting response must match its original request and
    /// the caller's current Squad lifetime before its server reservation
    /// may become an active targeting strategy.
    pub fn receive_targeting(
        &mut self,
        response: &GetTargetingResponse,
        live_owner: Option<SquadLifetime>,
    ) -> Result<TargetingReceipt, HiveMindSessionError> {
        let pending = self.pending_targeting.get(&response.hash)
            .ok_or(HiveMindSessionError::UnknownRequest)?;
        let selection = response.validate_for(&pending.request)?;
        let owner = pending.owner;
        let retired = pending.retired ||
            self.latest_targeting_by_squad.get(&owner) != Some(&response.hash) ||
            live_owner != Some(owner);
        self.pending_targeting.remove(&response.hash);
        if self.latest_targeting_by_squad.get(&owner) == Some(&response.hash) {
            self.latest_targeting_by_squad.remove(&owner);
        }
        if retired {
            Ok(TargetingReceipt::Retired {
                owner, reservation_id: selection.outcome_id,
            })
        } else {
            Ok(TargetingReceipt::Accepted { owner, selection })
        }
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
        self.latest_targeting_by_squad.remove(&owner);
        for pending in self.pending_targeting.values_mut() {
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
        let enemy_owner = pending.enemy_owner;
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
        Ok(StrategyReceipt::Accepted { owner, enemy_owner, selection })
    }

    /// The server owns no known positive reservation until a response is
    /// received. When a WebSocket dies, return unresolved hashes for the
    /// transport's recovery bookkeeping; do NOT falsely claim that server
    /// reservations were discarded or that learning was committed.
    pub fn retire_connection(&mut self) -> Vec<i64> {
        let mut unresolved: Vec<_> = self.pending.keys().copied()
            .chain(self.pending_targeting.keys().copied()).collect();
        unresolved.sort_unstable();
        self.pending.clear();
        self.pending_targeting.clear();
        self.latest_by_squad.clear();
        self.latest_targeting_by_squad.clear();
        unresolved
    }
}
