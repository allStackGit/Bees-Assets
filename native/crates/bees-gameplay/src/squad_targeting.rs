//! Source MatchupStrategy.SortSquads over the canonical Level roster/world.
use bees_hivemind::{protocol::ValidatedTargetingSelection, session::SquadLifetime};
use bees_sim::{combat_world::CombatWorld, ship_assets::ShipAssetCatalog, simulation::RandomStream};
use crate::{commands::NativeSquadCommandDirector, squads::NativeSquadRoster};
use crate::matchup_targeting::{NativeMatchupCandidate, LiveMatchupResolutionError};

#[derive(Clone, Copy, Debug)]
pub struct NativeTargetingContext { pub has_player: bool, pub user_side: i32 }
#[derive(Clone, Copy, Debug)]
pub struct NativeInstalledTargeting {
    pub selection: ValidatedTargetingSelection,
    pub enemy: Option<SquadLifetime>,
}
pub type NativeTargetingError = LiveMatchupResolutionError;
#[derive(Default)]
pub struct NativeSquadTargetingScratch { candidates: Vec<NativeMatchupCandidate> }

/// Installs the result of the one shared matchup selector. Source context
/// and activity come from this same Level, never a caller-supplied guess.
pub fn select_enemy_squad(
    world: &CombatWorld, roster: &NativeSquadRoster, commands: &NativeSquadCommandDirector,
    catalog: &ShipAssetCatalog, context: NativeTargetingContext, owner: SquadLifetime,
    selection: ValidatedTargetingSelection, random: &mut RandomStream,
    scratch: &mut NativeSquadTargetingScratch,
) -> Result<Option<SquadLifetime>, NativeTargetingError> {
    let own = roster.get(owner).ok_or(NativeTargetingError::MissingOwner)?;
    let allied = if selection.strategy.id == 15 {
        commands.first_targeted_enemy(world, roster, own.side)
    } else { None };
    crate::matchup_targeting::select_live_matchup_squad(
        world, roster, catalog, owner, selection.strategy,
        context.has_player && context.user_side == own.side, allied,
        |id| world.ship_in_combat(id), random, &mut scratch.candidates,
    )
}
