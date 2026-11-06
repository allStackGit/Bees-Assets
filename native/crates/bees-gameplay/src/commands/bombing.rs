//! Source BombingRun.Execute's initial per-bomber targeting transaction.
//! The Level owns authored Bomb.Weapon.Id/Power and GetShips order; the
//! canonical combat world owns damage reservations and physical targeting.
//! This is a reusable activation operation, NOT a completed BombingRun
//! timer/Striker/YellowJacket executor.

use std::collections::HashSet;

use bees_hivemind::dispatch::{LocalCommandKind, ResolvedHiveMindCommand};
use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::CombatWorld;
use bees_sim::simulation::RandomStream;

use crate::squads::NativeSquadRoster;
use super::{GameCommandError, NativeEnemyTargetStats, NativeSquadOrder};
use super::targeting::select_native_game_bomb_target;

/// The real Bomb weapon bound to one living ship, supplied in the owning
/// squad's GetShips order. Weapon IDs must be unique for the entire Level.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeBombWeapon {
    pub ship_id: i64,
    pub weapon_id: i64,
    pub power: i32,
}

/// Unity BombingRun.Execute calls GetTarget on every current bomber in
/// squad order before starting its recurring two-second command timer.
/// A failure invokes SetFinalize and releases all surviving Bomb targets.
/// This function performs ONLY that initial assignment; the Level must
/// still issue pursuit orders and implement the subsequent bomber states.
/// It does not generate a separate target/damage cache or synthetic ship.
///
/// Returns false when the targeted squad has no live bomb targets, which
/// is source command unavailability rather than a fabricated success.
/// Validation occurs before mutating the first target reservation.
pub fn assign_initial_bombing_run_targets(
    world: &mut CombatWorld,
    roster: &NativeSquadRoster,
    random: &mut RandomStream,
    squad: NativeSquadOrder<'_>,
    enemy_owner: SquadLifetime,
    selection: &ResolvedHiveMindCommand,
    weapons: &[NativeBombWeapon],
    enemies: &[NativeEnemyTargetStats],
) -> Result<bool, GameCommandError> {
    if selection.local_command != LocalCommandKind::BombingRun ||
        !squad.valid() || squad.owner.level_id != enemy_owner.level_id ||
        !enemy_owner.valid() ||
        !squad.enemy.is_some_and(|enemy|
            enemy.valid() && enemy.is_alive &&
            enemy.squad_id == enemy_owner.squad_id
        )
    {
        return Err(GameCommandError::InvalidSquad);
    }
    let own_entry = roster.get(squad.owner)
        .ok_or(GameCommandError::InvalidSquad)?;
    if own_entry.members.len() != squad.members.len() ||
        squad.members.len() != weapons.len() {
        return Err(GameCommandError::InvalidSquad);
    }
    let mut ids = HashSet::with_capacity(weapons.len());
    for ((member, &ship_id), weapon) in squad.members.iter()
        .zip(&own_entry.members).zip(weapons)
    {
        if member.ship_id != ship_id || weapon.ship_id != ship_id ||
            weapon.weapon_id <= 0 || weapon.power < 0 ||
            !ids.insert(weapon.weapon_id) {
            return Err(GameCommandError::InvalidSquad);
        }
        world.validate_ship_game_order(ship_id)?;
        let ship = world.ship(ship_id).ok_or(GameCommandError::InvalidSquad)?;
        if ship.vitals.is_dead() || ship.side != own_entry.side ||
            !matches!(ship.ship_type, 10 | 19 | 22) {
            return Err(GameCommandError::InvalidSquad);
        }
    }
    let enemy_entry = roster.get(enemy_owner)
        .ok_or(GameCommandError::InvalidEnemy)?;
    if enemy_entry.side == own_entry.side ||
        enemies.len() != enemy_entry.members.len() {
        return Err(GameCommandError::InvalidEnemy);
    }
    if enemies.is_empty() {
        for weapon in weapons {
            world.release_game_bomb_target_for_ship(weapon.ship_id);
        }
        return Ok(false);
    }
    // Each Bomb.GetTarget rebuilds its own sorted copy of EnemySquad.GetShips.
    // Reusing the preceding bomber's reordered list would change FirstSeen,
    // Random and all tie-sensitive strategies. Reuse one scratch allocation.
    let mut ranked = Vec::with_capacity(enemies.len());
    for weapon in weapons {
        ranked.clear();
        ranked.extend_from_slice(enemies);
        match select_native_game_bomb_target(
            world, roster, weapon.ship_id, weapon.weapon_id, weapon.power,
            enemy_owner, selection.effective_shooting, random, &mut ranked,
        ) {
            Ok(Some(_)) => {}
            Ok(None) => {
                for member in weapons {
                    world.release_game_bomb_target_for_ship(member.ship_id);
                }
                return Ok(false);
            }
            Err(err) => {
                // Unity SetFinalize releases the entire squad's Bomb
                // reservations when GetTarget cannot complete activation.
                for member in weapons {
                    world.release_game_bomb_target_for_ship(member.ship_id);
                }
                return Err(err);
            }
        }
    }
    Ok(true)
}
