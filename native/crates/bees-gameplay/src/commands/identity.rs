//! Command execution identity is independent of learned server reservations.

use bees_hivemind::{dispatch::{LocalCommandKind, ResolvedHiveMindCommand},
    protocol::{StrategyIdentity, SHOOTING_STRATEGIES}};
use super::GameCommandError;

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
        if shooting.id == 0 || SHOOTING_STRATEGIES.get(usize::from(shooting.id - 1))
            .copied() != Some(shooting.name) {
            return Err(GameCommandError::InvalidShootingStrategy);
        }
        Ok(Self { local_command: command, effective_shooting: shooting,
            origin: NativeCommandOrigin::Local(identity) })
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
