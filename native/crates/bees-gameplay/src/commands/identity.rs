//! Command execution identity is independent of learned server reservations.

use bees_hivemind::{dispatch::{LocalCommandKind, ResolvedHiveMindCommand},
    protocol::{StrategyIdentity, SHOOTING_STRATEGIES}};
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
    BombingRun,
    Charge,
    Hold,
    Guard { target: SquadLifetime },
    Patrol { top_left: Vec2, bottom_right: Vec2 },
    Mining { asteroid_id: i64 },
    Heal { beehive_ids: &'a [i64] },
    FullRetreat { warp_gate_id: i64 },
}

impl NativeLocalCommandAction<'_> {
    pub fn kind(self) -> LocalCommandKind {
        match self {
            Self::MoveToPoint { .. } => LocalCommandKind::MoveToPoint,
            Self::MoveToRandom { .. } => LocalCommandKind::MoveToRandom,
            Self::Aggressive | Self::TargetEnemy { .. } => LocalCommandKind::Aggressive,
            Self::BombingRun => LocalCommandKind::BombingRun,
            Self::Charge => LocalCommandKind::Charge,
            Self::Hold => LocalCommandKind::Hold,
            Self::Guard { .. } => LocalCommandKind::Guard,
            Self::Patrol { .. } => LocalCommandKind::Patrol,
            Self::Mining { .. } => LocalCommandKind::Mining,
            Self::Heal { .. } => LocalCommandKind::Heal,
            Self::FullRetreat { .. } => LocalCommandKind::FullRetreat,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeLocalCommandSource { Player, Scripted }

/// Level-owned local command sequence (starting at zero),
/// scoped by the enclosing command's SquadLifetime and world episode.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeLocalCommandIdentity {
    pub source: NativeLocalCommandSource,
    pub sequence: u64,
}

#[derive(Clone, Debug)]
pub enum NativeCommandOrigin {
    HiveMind(ResolvedHiveMindCommand),
    Local(NativeLocalCommandIdentity),
}

#[derive(Clone, Debug)]
pub struct NativeCommandSelection {
    pub(super) local_command: LocalCommandKind,
    pub(super) effective_shooting: StrategyIdentity,
    origin: NativeCommandOrigin,
}

impl From<ResolvedHiveMindCommand> for NativeCommandSelection {
    fn from(selection: ResolvedHiveMindCommand) -> Self {
        Self { local_command: selection.local_command,
            effective_shooting: selection.effective_shooting,
            origin: NativeCommandOrigin::HiveMind(selection) }
    }
}

impl NativeCommandSelection {
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
