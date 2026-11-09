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

/* The legacy GetMatchupStrategy request is generated at the command-queue
 * boundary, not from the selected enemy or a previously cached matchup.
 * Squad.GetShipsForMatchup takes the first 64 members in Squad.GetShips order,
 * then AddToMatchup sorts their authored ShipTypeLetter characters. The bans
 * come from UserProgressData.AllShipTypes (source's fixed Type A-X set)
 * minus the CURRENT enemy-side ships
 * in Level.State.Ships, not from visibility or the opponent's last selection.
 */
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct NativeTargetingRequestInputs {
    pub ships: String,
    pub banned: Vec<String>,
}

#[derive(Debug)]
pub enum NativeTargetingSourceError {
    MissingOwner,
    InvalidEnemySide,
    IncompleteRoster,
    EmptySquad,
    MissingShip(i64),
    InvalidShipType(u8),
    InvalidShipLetter(i64),
    MissingAuthoredShipLetter(i64, bees_sim::combat_world::CombatWorldError),
}

/// Unity Utilities.ConvertShipTypeToCharacter, indexed by ConfigData.ShipTypes
/// (not by the alphabetic ShipTypeLetter ordinal). Includes Type X in the
/// bans even though the deployed targeting-strategy registry stops at W.
const SOURCE_TYPE_LETTERS: [u8; bees_sim::ship_assets::SHIP_TYPE_COUNT] = [
    b'T', // Barge
    b'W', // Beacon
    b'M', // Beehive
    b'G', // Bumblebee
    b'J', // CarpenterBee
    b'O', // Carrier
    b'S', // Cruiser
    b'C', // Dreadnought
    b'P', // Drone
    b'R', // Factory
    b'U', // FireBarge
    b'H', // Flagship
    b'N', // Frigate
    b'D', // Gunship
    b'I', // Honeybee
    b'B', // Hornet
    b'K', // Leafcutter
    b'A', // Queen
    b'E', // Scout
    b'Q', // Striker
    b'V', // WarpGate
    b'F', // Wasp
    b'L', // YellowJacket
    b'X', // HumanTarget
];

/// Construct one source-authored GetMatchupStrategy payload before touching
/// the connection's persistent request counter. UserProgressData.SetShipTypes
/// builds AllShipTypes from the fixed Type A-X shooting strategy registry,
/// so the canonical source table above is the entire ban universe.
/// 'enemy_side' is the Level's configured opposing Bee/Human side, not any
/// arbitrary non-own faction. No independent fleet state is retained.
pub fn build_source_targeting_request(
    world: &CombatWorld,
    roster: &NativeSquadRoster,
    owner: SquadLifetime,
    enemy_side: i32,
) -> Result<NativeTargetingRequestInputs, NativeTargetingSourceError> {
    use NativeTargetingSourceError as Error;

    let squad = roster.get(owner).ok_or(Error::MissingOwner)?;
    if enemy_side <= 0 || enemy_side == squad.side {
        return Err(Error::InvalidEnemySide);
    }
    if !roster.covers_live_roster(world) {
        return Err(Error::IncompleteRoster);
    }
    if squad.members.is_empty() {
        return Err(Error::EmptySquad);
    }

    let mut letters = Vec::with_capacity(squad.members.len().min(64));
    for &ship_id in squad.members.iter().take(64) {
        let ship = world.ship(ship_id).ok_or(Error::MissingShip(ship_id))?;
        if ship.side != squad.side || ship.vitals.is_dead() ||
            !world.ship_body(ship_id).is_some_and(|body| body.active)
        {
            return Err(Error::MissingShip(ship_id));
        }
        // The Unity ShipTypeLetter is authored per ship. Reuse its existing
        // native rank input instead of inventing it from collision geometry.
        let letter = world.weapon_target_rank_stats(ship_id)
            .map_err(|error| Error::MissingAuthoredShipLetter(ship_id, error))?
            .ship_type_letter;
        if letter >= 24 { return Err(Error::InvalidShipLetter(ship_id)); }
        letters.push(b'A' + letter);
    }
    letters.sort_unstable();
    let ships = String::from_utf8(letters)
        .expect("validated ASCII Unity ShipTypeLetter values");

    let mut current_enemy_types = [false; bees_sim::ship_assets::SHIP_TYPE_COUNT];
    for &lifetime in roster.source_order() {
        let other = roster.get(lifetime).ok_or(Error::IncompleteRoster)?;
        if other.side != enemy_side { continue; }
        for &ship_id in &other.members {
            let ship = world.ship(ship_id).ok_or(Error::MissingShip(ship_id))?;
            if ship.side != enemy_side || ship.vitals.is_dead() ||
                !world.ship_body(ship_id).is_some_and(|body| body.active)
            {
                return Err(Error::MissingShip(ship_id));
            }
            let type_id = usize::from(ship.ship_type);
            if type_id >= current_enemy_types.len() {
                return Err(Error::InvalidShipType(ship.ship_type));
            }
            current_enemy_types[type_id] = true;
        }
    }

    let banned = SOURCE_TYPE_LETTERS.iter().enumerate()
        .filter(|(kind, _)| !current_enemy_types[*kind])
        .map(|(_, letter)| format!("Type {}", char::from(*letter)))
        .collect();
    Ok(NativeTargetingRequestInputs { ships, banned })
}
