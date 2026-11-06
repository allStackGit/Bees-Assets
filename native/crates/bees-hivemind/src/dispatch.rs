//! Unity Socket.HandleStrategicCommandResponse command selection.
//!
//! The command selected by BeesServer is a learned strategy ID; the
//! executable command may be changed by squad composition. These are
//! different identities. Historical outcome attribution MUST retain
//! the server-selected ID and reservations after a composition redirect.

use crate::outcomes::{FinalizedCommand, OutcomeSettlement};
use crate::protocol::{
    HiveMindProtocolError, StrategyIdentity, ValidatedStrategySelection,
    ValidatedTargetingSelection, COMMAND_STRATEGIES, SHOOTING_STRATEGIES,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LocalCommandKind {
    Aggressive,
    BombingRun,
    Charge,
    Retreat,
    MoveToRandom,
    CircleSquad,
    RightSwipe,
    LeftSwipe,
    ClosestFriendly,
    InAndOut,
    Patrol,
    Guard,
    Scouting,
    Mining,
    FullRetreat,
    Hold,
    Heal,
    /// A local/scripted user command, never returned by the server.
    MoveToPoint,
}

impl LocalCommandKind {
    /// Unity's command pool and client enum names, not BeesServer
    /// strategy names. "Defensive" is executed as Retreat and "Random"
    /// is executed as MoveToRandom.
    pub fn from_server_strategy_id(id: u8) -> Option<Self> {
        Some(match id {
            1 => Self::Aggressive,
            2 => Self::Retreat,
            3 => Self::MoveToRandom,
            4 => Self::CircleSquad,
            5 => Self::RightSwipe,
            6 => Self::LeftSwipe,
            7 => Self::ClosestFriendly,
            8 => Self::InAndOut,
            9 => Self::Patrol,
            10 => Self::Guard,
            11 => Self::Scouting,
            12 => Self::Mining,
            13 => Self::FullRetreat,
            14 => Self::Hold,
            15 => Self::Heal,
            _ => return None,
        })
    }

    pub fn redirects_for_special_composition(self) -> bool {
        matches!(self, Self::Aggressive | Self::CircleSquad |
            Self::RightSwipe | Self::LeftSwipe | Self::InAndOut)
    }
}

/// Snapshot of Unity Squad.HasOnlyBombers / Squad.HasOnlyBarges.
/// The caller derives this from live ship types, never from the server's
/// ability to choose a particular strategy. Empty squads are not valid.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SquadCommandComposition {
    pub only_bombers: bool,
    pub only_barges: bool,
    pub has_members: bool,
}

impl SquadCommandComposition {
    pub fn from_unity_ship_types(ship_types: impl IntoIterator<Item = i32>) -> Self {
        // ConfigData.ShipTypes, not the letter-based preferred target IDs.
        const BARGE: i32 = 0;
        const FIRE_BARGE: i32 = 10;
        const STRIKER: i32 = 19;
        const YELLOW_JACKET: i32 = 22;
        let mut has_members = false;
        let mut only_bombers = true;
        let mut only_barges = true;
        for type_id in ship_types {
            has_members = true;
            only_bombers &= matches!(type_id, STRIKER | YELLOW_JACKET | FIRE_BARGE);
            only_barges &= type_id == BARGE;
        }
        Self { only_bombers, only_barges, has_members }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ResolvedHiveMindCommand {
    /// The server's persistent learned strategy identity; DO NOT replace
    /// this with BombingRun or Charge after composition dispatch.
    pub server_strategy: StrategyIdentity,
    pub local_command: LocalCommandKind,
    /// Actual shooting strategy after Unity's command-specific override.
    /// The server's original selected shooting reservation is still
    /// carried separately and may need an explicit discard on finalize.
    pub effective_shooting: StrategyIdentity,
    pub selected_shooting: StrategyIdentity,
    pub command_outcome_id: i64,
    pub shooting_outcome_id: i64,
}

impl ResolvedHiveMindCommand {
    /// A failed native world cannot claim command or shooting TSV.
    /// Use the existing validated OutcomeBatch/StoreCommands transaction
    /// to discard both reservations. A separately issued targeting
    /// reservation is only discarded when its real selection is supplied.
    pub fn discard_unearned(
        &self,
        targeting: Option<&ValidatedTargetingSelection>,
    ) -> FinalizedCommand {
        FinalizedCommand {
            command: OutcomeSettlement::Discard {
                outcome_id: self.command_outcome_id,
            },
            shooting: OutcomeSettlement::Discard {
                outcome_id: self.shooting_outcome_id,
            },
            targeting: targeting.map(|selection|
                OutcomeSettlement::Discard {
                    outcome_id: selection.outcome_id,
                }),
        }
    }
}

pub fn resolve_hivemind_command(
    selection: &ValidatedStrategySelection,
    composition: SquadCommandComposition,
) -> Result<ResolvedHiveMindCommand, HiveMindProtocolError> {
    if !composition.has_members ||
        (composition.only_bombers && composition.only_barges) {
        return Err(HiveMindProtocolError::InvalidRequest);
    }
    let server_index = usize::from(selection.command.id).checked_sub(1)
        .ok_or(HiveMindProtocolError::UnknownStrategy)?;
    if COMMAND_STRATEGIES.get(server_index).copied() !=
        Some(selection.command.name) {
        return Err(HiveMindProtocolError::StrategyIdentityMismatch);
    }
    let shooting_index = usize::from(selection.shooting.id).checked_sub(1)
        .ok_or(HiveMindProtocolError::UnknownStrategy)?;
    if SHOOTING_STRATEGIES.get(shooting_index).copied() !=
        Some(selection.shooting.name) {
        return Err(HiveMindProtocolError::StrategyIdentityMismatch);
    }

    let mut local_command = LocalCommandKind::from_server_strategy_id(
        selection.command.id,
    ).ok_or(HiveMindProtocolError::UnknownStrategy)?;
    if local_command.redirects_for_special_composition() {
        if composition.only_bombers {
            local_command = LocalCommandKind::BombingRun;
        } else if composition.only_barges {
            local_command = LocalCommandKind::Charge;
        }
    }
    let effective_shooting = if local_command == LocalCommandKind::Retreat {
        // Socket.HandleStrategicCommandResponse passes FirstSeen to
        // Retreat.Execute regardless of the server-selected shooting type.
        StrategyIdentity { id: 1, name: SHOOTING_STRATEGIES[0] }
    } else {
        selection.shooting
    };
    Ok(ResolvedHiveMindCommand {
        server_strategy: selection.command,
        local_command,
        effective_shooting,
        selected_shooting: selection.shooting,
        command_outcome_id: selection.command_outcome_id,
        shooting_outcome_id: selection.shooting_outcome_id,
    })
}
