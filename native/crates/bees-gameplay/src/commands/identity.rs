//! Command execution identity is independent of learned server reservations.

use bees_hivemind::{dispatch::{LocalCommandKind, ResolvedHiveMindCommand},
    protocol::{StrategyIdentity, COMMAND_STRATEGIES, SHOOTING_STRATEGIES}};
use super::GameCommandError;
use bees_hivemind::session::SquadLifetime;
use bees_sim::geometry::Vec2;

#[derive(Clone, Copy, Debug)]
pub enum NativeLocalCommandAction<'a> {
    /// Only the source mission/script queue issues these. A player ground
    /// click remains a separate direct movement authority transaction.
    MoveToPoint { destination: Vec2 },
    MoveToRandom { range: f32 },
    Aggressive,
    TargetEnemy { target: SquadLifetime },
    Retreat,
    CircleSquad,
    RightSwipe,
    LeftSwipe,
    InAndOut,
    /// Player actions bind an explicitly clicked enemy lifetime; scripted
    /// and HiveMind commands continue using their normal target source.
    CircleAt { target: SquadLifetime },
    RightSwipeAt { target: SquadLifetime },
    LeftSwipeAt { target: SquadLifetime },
    InAndOutAt { target: SquadLifetime },
    BombingRun,
    Charge,
    Hold,
    Scouting,
    ClosestFriendly,
    Guard { target: SquadLifetime },
    Patrol { top_left: Vec2, bottom_right: Vec2 },
    Mining { asteroid_id: i64 },
    /// Choose the next living physical mining asteroid using the shared
    /// MiningRun nearest-target selection, without authored obstacle IDs.
    MiningNearest,
    Heal { beehive_ids: &'a [i64] },
    /// Let the existing HealRun select live same-side Beehives by distance
    /// and reserve actual free healing slots.
    HealAvailable,
    FullRetreat { warp_gate_id: i64 },
}

impl NativeLocalCommandAction<'_> {
    pub fn kind(self) -> LocalCommandKind {
        match self {
            Self::MoveToPoint { .. } => LocalCommandKind::MoveToPoint,
            Self::MoveToRandom { .. } => LocalCommandKind::MoveToRandom,
            Self::Aggressive | Self::TargetEnemy { .. } => LocalCommandKind::Aggressive,
            Self::Retreat => LocalCommandKind::Retreat,
            Self::CircleSquad | Self::CircleAt { .. } => LocalCommandKind::CircleSquad,
            Self::RightSwipe | Self::RightSwipeAt { .. } => LocalCommandKind::RightSwipe,
            Self::LeftSwipe | Self::LeftSwipeAt { .. } => LocalCommandKind::LeftSwipe,
            Self::InAndOut | Self::InAndOutAt { .. } => LocalCommandKind::InAndOut,
            Self::BombingRun => LocalCommandKind::BombingRun,
            Self::Charge => LocalCommandKind::Charge,
            Self::Hold => LocalCommandKind::Hold,
            Self::Scouting => LocalCommandKind::Scouting,
            Self::ClosestFriendly => LocalCommandKind::ClosestFriendly,
            Self::Guard { .. } => LocalCommandKind::Guard,
            Self::Patrol { .. } => LocalCommandKind::Patrol,
            Self::Mining { .. } | Self::MiningNearest => LocalCommandKind::Mining,
            Self::Heal { .. } | Self::HealAvailable => LocalCommandKind::Heal,
            Self::FullRetreat { .. } => LocalCommandKind::FullRetreat,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub enum NativeLocalCommandSource { Player, Scripted }

/// Level-owned local command sequence (starting at zero),
/// scoped by the enclosing command's SquadLifetime and world episode.
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NativeLocalCommandIdentity {
    pub source: NativeLocalCommandSource,
    pub sequence: u64,
}

#[derive(Clone, Debug)]
pub enum NativeCommandOrigin {
    HiveMind(ResolvedHiveMindCommand),
    Local(NativeLocalCommandIdentity),
}

/// Stable protocol identities for a native command. Names are derived
/// from the HiveMind registry on restore, never trusted from checkpoint
/// JSON, and the local command variant remains independent of the
/// server-selected learning strategy.
#[derive(serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NativeCommandSelectionCheckpoint {
    schema_version: u32,
    local_command: LocalCommandKind,
    effective_shooting_id: u8,
    origin: NativeCommandOriginCheckpoint,
}

#[derive(serde::Serialize, serde::Deserialize)]
enum NativeCommandOriginCheckpoint {
    HiveMind {
        server_strategy_id: u8,
        selected_shooting_id: u8,
        issued_effective_shooting_id: u8,
        command_outcome_id: i64,
        shooting_outcome_id: i64,
    },
    Local(NativeLocalCommandIdentity),
}

fn registry_identity(
    id: u8,
    registry: &'static [&'static str],
) -> Result<StrategyIdentity, GameCommandError> {
    let name = usize::from(id).checked_sub(1)
        .and_then(|index| registry.get(index).copied())
        .ok_or(GameCommandError::InvalidCommandOrigin)?;
    Ok(StrategyIdentity { id, name })
}

#[derive(Clone, Debug)]
pub struct NativeCommandSelection {
    pub(super) local_command: LocalCommandKind,
    pub(super) effective_shooting: StrategyIdentity,
    origin: NativeCommandOrigin,
}

// Notification persistence uses the same validated source identity schema
// as the command director and command-credit recovery ledgers.
impl serde::Serialize for NativeCommandSelection {
    fn serialize<S: serde::Serializer>(&self, serializer: S)
        -> Result<S::Ok, S::Error>
    {
        serde::Serialize::serialize(&self.checkpoint(), serializer)
    }
}

impl<'de> serde::Deserialize<'de> for NativeCommandSelection {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D)
        -> Result<Self, D::Error>
    {
        let saved = serde::Deserialize::deserialize(deserializer)?;
        Self::from_checkpoint(saved)
            .map_err(|error| serde::de::Error::custom(format!("{error:?}")))
    }
}

impl From<ResolvedHiveMindCommand> for NativeCommandSelection {
    fn from(selection: ResolvedHiveMindCommand) -> Self {
        Self { local_command: selection.local_command,
            effective_shooting: selection.effective_shooting,
            origin: NativeCommandOrigin::HiveMind(selection) }
    }
}

impl NativeCommandSelection {
    pub fn checkpoint(&self) -> NativeCommandSelectionCheckpoint {
        let origin = match &self.origin {
            NativeCommandOrigin::HiveMind(source) =>
                NativeCommandOriginCheckpoint::HiveMind {
                    server_strategy_id: source.server_strategy.id,
                    selected_shooting_id: source.selected_shooting.id,
                    issued_effective_shooting_id: source.effective_shooting.id,
                    command_outcome_id: source.command_outcome_id,
                    shooting_outcome_id: source.shooting_outcome_id,
                },
            NativeCommandOrigin::Local(identity) =>
                NativeCommandOriginCheckpoint::Local(*identity),
        };
        NativeCommandSelectionCheckpoint {
            schema_version: 1,
            local_command: self.local_command,
            effective_shooting_id: self.effective_shooting.id,
            origin,
        }
    }

    /// Rehydrate only canonical source identities, preserving the
    /// distinction between server reservations, composition dispatch,
    /// and a later SetShootingStrategy override.
    pub fn from_checkpoint(
        saved: NativeCommandSelectionCheckpoint,
    ) -> Result<Self, GameCommandError> {
        if saved.schema_version != 1 {
            return Err(GameCommandError::InvalidCommandOrigin);
        }
        let effective = registry_identity(
            saved.effective_shooting_id, &SHOOTING_STRATEGIES,
        )?;
        let origin = match saved.origin {
            NativeCommandOriginCheckpoint::Local(identity) =>
                NativeCommandOrigin::Local(identity),
            NativeCommandOriginCheckpoint::HiveMind {
                server_strategy_id, selected_shooting_id,
                issued_effective_shooting_id, command_outcome_id,
                shooting_outcome_id,
            } => {
                if command_outcome_id <= 0 || shooting_outcome_id <= 0 ||
                    command_outcome_id == shooting_outcome_id {
                    return Err(GameCommandError::InvalidCommandOrigin);
                }
                let server = registry_identity(server_strategy_id, &COMMAND_STRATEGIES)?;
                let selected = registry_identity(selected_shooting_id, &SHOOTING_STRATEGIES)?;
                let issued = registry_identity(issued_effective_shooting_id, &SHOOTING_STRATEGIES)?;
                let original = LocalCommandKind::from_server_strategy_id(
                    server_strategy_id,
                ).ok_or(GameCommandError::InvalidCommandOrigin)?;
                if saved.local_command != original &&
                    !(original.redirects_for_special_composition() &&
                      matches!(saved.local_command, LocalCommandKind::BombingRun |
                          LocalCommandKind::Charge)) {
                    return Err(GameCommandError::InvalidCommandOrigin);
                }
                let expected_shooting = if saved.local_command == LocalCommandKind::Retreat {
                    1
                } else {
                    selected_shooting_id
                };
                if issued.id != expected_shooting {
                    return Err(GameCommandError::InvalidCommandOrigin);
                }
                NativeCommandOrigin::HiveMind(ResolvedHiveMindCommand {
                    server_strategy: server,
                    local_command: saved.local_command,
                    effective_shooting: issued,
                    selected_shooting: selected,
                    command_outcome_id,
                    shooting_outcome_id,
                })
            }
        };
        Ok(Self {
            local_command: saved.local_command,
            effective_shooting: effective,
            origin,
        })
    }

    pub fn local(identity: NativeLocalCommandIdentity, command: LocalCommandKind,
        shooting: StrategyIdentity) -> Result<Self, GameCommandError>
    {
        Self::validate_shooting(shooting)?;
        Ok(Self { local_command: command, effective_shooting: shooting,
            origin: NativeCommandOrigin::Local(identity) })
    }

    /// The native equivalent of Squad.SetShootingStrategy on an active
    /// Command: change its live prioritization without replacing its
    /// origin, AI reservation, command lifetime or target selection.
    pub fn set_shooting(&mut self, shooting: StrategyIdentity)
        -> Result<(), GameCommandError> {
        Self::validate_shooting(shooting)?;
        self.effective_shooting = shooting;
        Ok(())
    }

    pub fn validate_shooting(shooting: StrategyIdentity)
        -> Result<(), GameCommandError> {
        if shooting.id == 0 ||
            SHOOTING_STRATEGIES.get(usize::from(shooting.id - 1))
                .copied() != Some(shooting.name)
        {
            return Err(GameCommandError::InvalidShootingStrategy);
        }
        Ok(())
    }

    pub fn kind(&self) -> LocalCommandKind { self.local_command }
    pub fn shooting(&self) -> StrategyIdentity { self.effective_shooting }
    pub fn origin(&self) -> &NativeCommandOrigin { &self.origin }
    pub fn server(&self) -> Option<&ResolvedHiveMindCommand> {
        match &self.origin { NativeCommandOrigin::HiveMind(value) => Some(value), _ => None }
    }
    pub fn server_outcome(&self) -> Option<i64> {
        self.server().map(|value| value.command_outcome_id)
    }
}
