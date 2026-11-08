//! Game-owned command TSV accounting from immutable native combat outcomes.
//! This is not a StoreCommands transport or a second shot-history registry.
//! Per-command totals remain pending until a trustworthy episode boundary;
//! only the connection-owned ledger can turn them into server submissions.

use std::collections::BTreeMap;

use bees_hivemind::outcomes::{OutcomeBatch, OutcomeSettlement};
use bees_hivemind::protocol::HiveMindProtocolError;

use crate::commands::{
    FinishedNativeCommand, NativeCommandOrigin, NativeCommandSelection,
    NativeCommandSettlement,
};

#[derive(Debug)]
pub enum CommandCreditError {
    InvalidOutcomeId(i64),
    ArithmeticOverflow(i64),
    DuplicateFinished(i64),
    IncompleteOutcome(i64),
    AbortedCompletedOutcome(i64),
    InvalidOrigin(i64),
    AlreadyClosed,
    Protocol(HiveMindProtocolError),
}

#[derive(Default)]
struct CommandCredit {
    command_tsv: i64,
    shooting_tsv: i64,
    finished: Option<FinishedNativeCommand>,
    aborted: Option<NativeCommandSelection>,
}

/// One command-scoped credit projection for one Level episode. It holds no
/// projectile state: shots and their immutable origin IDs belong to CombatWorld.
/// A completed command stays here so a later hit can credit its original ID.
#[derive(Default)]
pub struct NativeCommandCreditLedger {
    commands: BTreeMap<i64, CommandCredit>,
    closed: bool,
    targeting: std::collections::BTreeSet<i64>,
}

impl NativeCommandCreditLedger {
    pub fn new() -> Self { Self::default() }

    fn credit(
        &mut self, id: Option<i64>, command_delta: i64, shooting_delta: i64,
    ) -> Result<(), CommandCreditError> {
        let Some(id) = id else { return Ok(()); };
        if self.closed { return Err(CommandCreditError::AlreadyClosed); }
        if id <= 0 { return Err(CommandCreditError::InvalidOutcomeId(id)); }
        let entry = self.commands.entry(id).or_default();
        let new_command = entry.command_tsv.checked_add(command_delta)
            .ok_or(CommandCreditError::ArithmeticOverflow(id))?;
        let new_shooting = entry.shooting_tsv.checked_add(shooting_delta)
            .ok_or(CommandCreditError::ArithmeticOverflow(id))?;
        entry.command_tsv = new_command;
        entry.shooting_tsv = new_shooting;
        Ok(())
    }

    /// Source Ship.LogHitStats: shots add signed TSV to their original
    /// command and shooting strategy. A target with an active command loses
    /// TSV in BOTH categories. Friendly fire can penalize both owners.
    pub fn record_hit(
        &mut self, attacker_command: Option<i64>,
        defender_command: Option<i64>, attacker_tsv_delta: i32, tsv_loss: i32,
    ) -> Result<(), CommandCreditError> {
        let attacker_tsv_delta = i64::from(attacker_tsv_delta);
        let damage = i64::from(tsv_loss);
        // If attacker and defender share one command, both deltas apply.
        self.credit(attacker_command, attacker_tsv_delta, attacker_tsv_delta)?;
        self.credit(defender_command, -damage, -damage)
    }

    /// Source Ship.LogDamage: an unattributed collision reduces the victim
    /// command TSV, but does not credit or debit a shooting strategy.
    pub fn record_unattributed(
        &mut self, defender_command: Option<i64>, tsv_loss: i32,
    ) -> Result<(), CommandCreditError> {
        self.credit(defender_command, -i64::from(tsv_loss), 0)
    }

    /// First-sighting TSV belongs only to the command, never its shooting choice.
    pub fn record_sighting(
        &mut self, command: Option<i64>, tsv_delta: i32,
    ) -> Result<(), CommandCreditError> {
        self.credit(command, i64::from(tsv_delta), 0)
    }

    pub fn register_targeting(&mut self, id: i64) -> Result<(), CommandCreditError> {
        if self.closed { return Err(CommandCreditError::AlreadyClosed); }
        if id <= 0 || !self.targeting.insert(id) { return Err(CommandCreditError::InvalidOutcomeId(id)); }
        Ok(())
    }

    pub fn finish(
        &mut self, command: &FinishedNativeCommand,
    ) -> Result<(), CommandCreditError> {
        if self.closed { return Err(CommandCreditError::AlreadyClosed); }
        let Some(id) = command.selection.server_outcome() else {
            return Ok(()); // Local gameplay has no server reservation.
        };
        if id <= 0 { return Err(CommandCreditError::InvalidOutcomeId(id)); }
        let entry = self.commands.entry(id).or_default();
        if entry.finished.is_some() {
            return Err(CommandCreditError::DuplicateFinished(id));
        }
        if command.targeting.is_some_and(|selection| !self.targeting.contains(&selection.outcome_id)) {
            return Err(CommandCreditError::InvalidOutcomeId(command.targeting.unwrap().outcome_id));
        }
        entry.finished = Some(command.clone());
        Ok(())
    }

    pub fn abort(
        &mut self, selection: &NativeCommandSelection,
    ) -> Result<(), CommandCreditError> {
        if self.closed { return Err(CommandCreditError::AlreadyClosed); }
        let Some(id) = selection.server_outcome() else { return Ok(()); };
        if id <= 0 { return Err(CommandCreditError::InvalidOutcomeId(id)); }
        let entry = self.commands.entry(id).or_default();
        if entry.aborted.is_some() {
            return Err(CommandCreditError::DuplicateFinished(id));
        }
        entry.aborted = Some(selection.clone());
        Ok(())
    }

    /// The source physical world can no longer deliver a hit after an
    /// authoritative episode final/abort. Before then, FinishedCommand is
    /// NOT a safe StoreCommands settlement signal.
    ///
    /// Successful episodes credit command TSV (including authored capability
    /// TSV) and conditionally credit shooting TSV; failed episodes discard
    /// every known reservation, including formerly finished commands.
    /// A hit whose original server command was never finalized fails closed.
    pub fn close_episode(
        &mut self, successful: bool,
    ) -> Result<OutcomeBatch, CommandCreditError> {
        if self.closed { return Err(CommandCreditError::AlreadyClosed); }
        // Reject missing or inconsistent source identities before removing
        // any records from the one episode-owned projection.
        for (&id, entry) in &self.commands {
            if entry.finished.is_none() && entry.aborted.is_none() {
                return Err(CommandCreditError::IncompleteOutcome(id));
            }
            if successful && entry.aborted.is_some() {
                return Err(CommandCreditError::AbortedCompletedOutcome(id));
            }
        }
        let mut batch = OutcomeBatch::new();
        // Build transactionally from borrowed records. On a duplicate
        // reservation or invalid selection the original episode accounting
        // remains available for explicit abort/discard reconciliation.
        for (&id, entry) in &self.commands {
            if successful {
                let finished = entry.finished.as_ref()
                    .ok_or(CommandCreditError::IncompleteOutcome(id))?;
                let shooting = finished.shooting_targeted_enemy
                    .then_some(entry.shooting_tsv);
                match (*finished).clone().settle(entry.command_tsv, shooting, None) {
                    NativeCommandSettlement::HiveMind(outcome) =>
                        batch.add_finalized(outcome)
                            .map_err(CommandCreditError::Protocol)?,
                    NativeCommandSettlement::Local { .. } =>
                        return Err(CommandCreditError::InvalidOrigin(id)),
                }
            } else {
                let selection = entry.aborted.as_ref().or_else(||
                    entry.finished.as_ref().map(|value| &value.selection)
                ).ok_or(CommandCreditError::IncompleteOutcome(id))?;
                let NativeCommandOrigin::HiveMind(source) = selection.origin() else {
                    return Err(CommandCreditError::InvalidOrigin(id));
                };
                batch.discard_reservations([
                    source.command_outcome_id, source.shooting_outcome_id,
                ]).map_err(CommandCreditError::Protocol)?;
            }
        }
        // Strategic TSV (including source spotting/capability value), never
        // the shooting-only combat total, trains an actually used matchup.
        // Several source commands can retain the same copied matchup ID.
        let mut targeting: BTreeMap<_, Option<i64>> = self.targeting.iter().map(|&id| (id, None)).collect();
        if successful {
            for entry in self.commands.values() {
                let Some(command) = &entry.finished else { continue; };
                if !command.targeting_targeted_enemy { continue; }
                let Some(selection) = command.targeting else { continue; };
                let target = selection.outcome_id;
                let earned = targeting.get_mut(&target).ok_or(CommandCreditError::InvalidOutcomeId(target))?;
                let value = entry.command_tsv.checked_add(command.capability_command_tsv.unwrap_or(0))
                    .ok_or(CommandCreditError::ArithmeticOverflow(target))?;
                *earned = Some(earned.unwrap_or(0).checked_add(value)
                    .ok_or(CommandCreditError::ArithmeticOverflow(target))?);
            }
        }
        for (target, earned) in targeting {
            batch.add_targeting(match earned {
                Some(tsv) => OutcomeSettlement::Credit { outcome_id: target, tsv },
                None => OutcomeSettlement::Discard { outcome_id: target },
            }).map_err(CommandCreditError::Protocol)?;
        }
        self.commands.clear();
        self.targeting.clear();
        self.closed = true;
        Ok(batch)
    }
}
