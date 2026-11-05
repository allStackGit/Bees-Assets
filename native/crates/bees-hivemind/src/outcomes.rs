//! Client-owned Hive Mind outcome accounting and acknowledgement.
//!
//! The server reserves temporary OutcomeIds while selecting strategies;
//! the client must credit or explicitly discard EACH issued reservation.
//! This module prepares the existing StoreCommands wire format. It never
//! pretends a successful send is a durable commit; the server controls the
//! transaction and final acknowledgement. A native transport must persist
//! the pending submission before sending and restore it after reconnect.

use std::collections::{HashMap, HashSet};

use serde::{Deserialize, Serialize};

use crate::protocol::{
    HiveMindProtocolError, StoreCommandsRequest, StoreCommandsResponse,
    StoredOutcome, ValidatedStrategySelection, ValidatedTargetingSelection,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum OutcomeSettlement {
    Credit { outcome_id: i64, tsv: i64 },
    Discard { outcome_id: i64 },
}

impl OutcomeSettlement {
    pub fn outcome_id(self) -> i64 {
        match self {
            Self::Credit { outcome_id, .. } | Self::Discard { outcome_id } =>
                outcome_id,
        }
    }
}

/// The game supplies actual eligibility. For example, shooting TSV is
/// credited ONLY when the original shooting strategy influenced combat.
/// Merely receiving a shooting strategy does not justify crediting it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FinalizedCommand {
    pub command: OutcomeSettlement,
    /// GetStrategy always reserves shooting as well as command learning.
    /// The caller must explicitly credit or discard that second reservation.
    pub shooting: OutcomeSettlement,
    pub targeting: Option<OutcomeSettlement>,
}

impl FinalizedCommand {
    /// Build from the server's validated reservations. An absence of
    /// shooting TSV explicitly discards its reserved ID; an absent
    /// targeting selection means no targeting reservation was issued.
    /// The game must decide eligibility based on actual executed behavior.
    pub fn from_selections(
        strategy: &ValidatedStrategySelection,
        command_tsv: i64,
        shooting_tsv: Option<i64>,
        targeting: Option<(&ValidatedTargetingSelection, Option<i64>)>,
    ) -> Self {
        Self {
            command: OutcomeSettlement::Credit {
                outcome_id: strategy.command_outcome_id, tsv: command_tsv,
            },
            shooting: match shooting_tsv {
                Some(tsv) => OutcomeSettlement::Credit {
                    outcome_id: strategy.shooting_outcome_id, tsv,
                },
                None => OutcomeSettlement::Discard {
                    outcome_id: strategy.shooting_outcome_id,
                },
            },
            targeting: targeting.map(|(selection, tsv)| match tsv {
                Some(tsv) => OutcomeSettlement::Credit {
                    outcome_id: selection.outcome_id, tsv,
                },
                None => OutcomeSettlement::Discard {
                    outcome_id: selection.outcome_id,
                },
            }),
        }
    }
}

#[derive(Default)]
pub struct OutcomeBatch {
    seen: HashSet<i64>,
    strategic: Vec<StoredOutcome>,
    shooting: Vec<StoredOutcome>,
    targeting: Vec<StoredOutcome>,
    discarded: Vec<i64>,
}

impl OutcomeBatch {
    pub fn new() -> Self { Self::default() }

    pub fn is_empty(&self) -> bool { self.seen.is_empty() }

    /// Reject every duplicate before modifying the batch. A temporary
    /// reservation must never be credited to two strategy categories or
    /// be both rewarded and discarded.
    pub fn add_finalized(
        &mut self,
        command: FinalizedCommand,
    ) -> Result<(), HiveMindProtocolError> {
        let mut incoming = HashSet::new();
        for settlement in [Some(command.command), Some(command.shooting), command.targeting]
            .into_iter().flatten() {
            let id = settlement.outcome_id();
            if id <= 0 {
                return Err(HiveMindProtocolError::InvalidReservation);
            }
            if !incoming.insert(id) || self.seen.contains(&id) {
                return Err(HiveMindProtocolError::DuplicateReservation);
            }
        }
        self.seen.extend(incoming);
        self.push(command.command, OutcomeCategory::Strategic);
        self.push(command.shooting, OutcomeCategory::Shooting);
        if let Some(targeting) = command.targeting {
            self.push(targeting, OutcomeCategory::Targeting);
        }
        Ok(())
    }

    /// A response received for a retired or superseded squad may still
    /// contain live server reservations. Instead of dropping them, emit
    /// the same explicit discard marker used by Unity StoreCommands.
    pub fn discard_reservations(
        &mut self,
        ids: impl IntoIterator<Item = i64>,
    ) -> Result<(), HiveMindProtocolError> {
        let ids: Vec<_> = ids.into_iter().collect();
        let mut incoming = HashSet::new();
        for &id in &ids {
            if id <= 0 { return Err(HiveMindProtocolError::InvalidReservation); }
            if self.seen.contains(&id) || !incoming.insert(id) {
                return Err(HiveMindProtocolError::DuplicateReservation);
            }
        }
        self.seen.extend(incoming);
        for id in ids {
            self.push(OutcomeSettlement::Discard { outcome_id: id },
                OutcomeCategory::Strategic);
        }
        Ok(())
    }

    /// Callers must account for both the primary and shooting reservation,
    /// including a separate targeting reservation if one was also issued.
    /// This helper is deliberately restricted to the two outcomes present
    /// in GetStrategyResponse; it does not invent targeting ownership.
    pub fn discard_selection(
        &mut self,
        selection: &ValidatedStrategySelection,
    ) -> Result<(), HiveMindProtocolError> {
        self.discard_reservations([
            selection.command_outcome_id,
            selection.shooting_outcome_id,
        ])
    }

    pub fn discard_targeting(
        &mut self,
        selection: &ValidatedTargetingSelection,
    ) -> Result<(), HiveMindProtocolError> {
        self.discard_reservations([selection.outcome_id])
    }

    pub fn into_request(
        self,
        hash: i64,
    ) -> Result<StoreCommandsRequest, HiveMindProtocolError> {
        let mut request = StoreCommandsRequest::new(hash)?;
        request.commands = self.strategic;
        request.shooting_commands = self.shooting;
        request.targeting_commands = self.targeting;
        request.discarded_outcome_ids = self.discarded;
        Ok(request)
    }

    fn push(&mut self, settlement: OutcomeSettlement, category: OutcomeCategory) {
        match settlement {
            OutcomeSettlement::Credit { outcome_id, tsv } => {
                let row = StoredOutcome::reward(outcome_id, tsv);
                match category {
                    OutcomeCategory::Strategic => self.strategic.push(row),
                    OutcomeCategory::Shooting => self.shooting.push(row),
                    OutcomeCategory::Targeting => self.targeting.push(row),
                }
            }
            OutcomeSettlement::Discard { outcome_id } => {
                // The active server security wrapper recognizes the marker
                // inside Commands. DiscardedOutcomeIds remains an additional
                // explicit protocol field for newer server implementations.
                self.strategic.push(StoredOutcome::discard(outcome_id));
                self.discarded.push(outcome_id);
            }
        }
    }
}

enum OutcomeCategory { Strategic, Shooting, Targeting }

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum StoreAcknowledgement {
    DurableCommit,
    /// The deployed server commits valid rows before returning 409 with
    /// StaleOutcomeIds. These rows are terminal, not retryable as a batch.
    /// Report stale IDs separately for reconciliation; never interpret a
    /// partially committed batch as a failed all-or-nothing write.
    PartiallyCommitted {
        committed_outcome_ids: Vec<i64>,
        stale_outcome_ids: Vec<i64>,
    },
    Rejected { status: i32, stale_outcome_ids: Vec<i64> },
}

/// Transport-neutral retry ownership. Serialization supports a caller
/// persisting the pending submissions outside the physics/learning core.
/// Retrying uses the original Hash and identical payload, so response
/// correlation and the server's durable reservation semantics are preserved.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct PendingStoreCommands {
    submissions: HashMap<i64, StoreCommandsRequest>,
}

impl PendingStoreCommands {
    pub fn new() -> Self { Self::default() }

    pub fn queue(
        &mut self,
        request: StoreCommandsRequest,
    ) -> Result<(), HiveMindProtocolError> {
        if request.hash <= 0 ||
            request.message_type != crate::protocol::STORE_COMMANDS_TYPE ||
            self.submissions.contains_key(&request.hash) {
            return Err(HiveMindProtocolError::InvalidRequest);
        }
        self.submissions.insert(request.hash, request);
        Ok(())
    }

    pub fn pending_count(&self) -> usize { self.submissions.len() }

    pub fn retry_payload(&self, hash: i64) -> Option<&StoreCommandsRequest> {
        self.submissions.get(&hash)
    }

    /// Status 200 confirms durable commit. The server's 409 is unusual:
    /// outcomeReservations.matchUpdatesWithInsertsAndCommit first commits
    /// every valid row, THEN reports the IDs whose reservations were stale.
    /// Retrying the whole 409 batch would re-submit already committed
    /// rewards. Report the terminal partial result instead, with the exact
    /// uncommitted IDs; the transport must reconcile them separately.
    ///
    /// Other failures retain the original request because their database
    /// commit outcome is not established. No network operation is performed
    /// by this ownership ledger.
    pub fn acknowledge(
        &mut self,
        response: &StoreCommandsResponse,
    ) -> Result<StoreAcknowledgement, HiveMindProtocolError> {
        if response.message_type != crate::protocol::STORE_COMMANDS_TYPE {
            return Err(HiveMindProtocolError::RequestIdentityMismatch);
        }
        let request = self.submissions.get(&response.hash)
            .ok_or(HiveMindProtocolError::RequestIdentityMismatch)?;
        if response.acknowledges_durable_commit(response.hash) {
            self.submissions.remove(&response.hash);
            return Ok(StoreAcknowledgement::DurableCommit);
        }
        if response.status == 409 && !response.stale_outcome_ids.is_empty() {
            // A stale ID must refer to a credited reservation from this
            // particular request. Otherwise the message is untrustworthy
            // and cannot retire local ownership.
            let credited: std::collections::BTreeSet<i64> = request.commands.iter()
                .chain(request.shooting_commands.iter())
                .chain(request.targeting_commands.iter())
                .filter(|entry| !entry.discard_reservation && entry.outcome_id > 0)
                .map(|entry| entry.outcome_id).collect();
            let stale: std::collections::BTreeSet<i64> =
                response.stale_outcome_ids.iter().copied().collect();
            if stale.len() != response.stale_outcome_ids.len() ||
                !stale.is_subset(&credited) {
                return Err(HiveMindProtocolError::InvalidResponse);
            }
            let committed_outcome_ids = credited.difference(&stale)
                .copied().collect();
            self.submissions.remove(&response.hash);
            return Ok(StoreAcknowledgement::PartiallyCommitted {
                committed_outcome_ids,
                stale_outcome_ids: response.stale_outcome_ids.clone(),
            });
        }
        Ok(StoreAcknowledgement::Rejected {
            status: response.status,
            stale_outcome_ids: response.stale_outcome_ids.clone(),
        })
    }
}
