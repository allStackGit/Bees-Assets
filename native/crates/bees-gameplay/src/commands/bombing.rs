//! Source BombingRun.Execute's initial per-bomber targeting transaction.
//! The Level owns authored Bomb.Weapon.Id/Power and GetShips order; the
//! canonical combat world owns damage reservations and physical targeting.
//! This is a reusable activation operation, NOT a completed BombingRun
//! timer/Striker/YellowJacket executor.

use std::collections::HashSet;

use bees_hivemind::dispatch::{LocalCommandKind, ResolvedHiveMindCommand};
use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::{CombatWorld, GamePointOrder};
use bees_sim::simulation::RandomStream;
use bees_sim::squad_movement::{SquadFormationMember, SquadFormationOptions};
use bees_sim::timer::ScaledTimer;

use crate::squads::NativeSquadRoster;
use super::{CommandFinishCause, GameCommandError, NativeEnemyTargetStats, NativeSquadOrder};
use super::targeting::NativeAttackTarget;
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

/// Fire Barge-only portion of Unity BombingRun.Timer. Its special is
/// command-triggered when the ORIGINAL selected ship is present in the
/// authored ProximityCollider.NearbyEnemyShips cache (not merely near the
/// bomber's center or touching its primary hull).
///
/// Called on the owning command's timer, after targets were assigned.
/// Mixed squads additionally require the separate Striker/YellowJacket
/// state machine; this method does not masquerade as its replacement.
/// Actual bomb death/explosion is queued for the next fixed combat step,
/// preserving the Level's one-pass episode and squad lifecycle boundary.
///
/// Returns the count of newly queued, source-authorized detonations.
pub fn advance_fire_barge_bombing_run(
    world: &mut CombatWorld,
    members: &[SquadFormationMember],
    formation: SquadFormationOptions,
    command_outcome_id: i64,
    mut target_in_proximity: impl FnMut(i64, i64)
        -> Result<bool, GameCommandError>,
) -> Result<u64, GameCommandError> {
    // Only accepted HiveMind commands may auto-detonate Fire Barges.
    // The server protocol requires positive command OutcomeIds.
    if command_outcome_id <= 0 || !formation.valid() {
        return Err(GameCommandError::InvalidSettings);
    }
    // Fail closed before issuing any partial orders if the caller passes
    // a mixed bomber squad without its necessary Striker/Yellow Jacket
    // handling. Full BombingRun coordination must choose that path.
    for member in members {
        let ship = world.ship(member.ship_id)
            .ok_or(GameCommandError::InvalidSquad)?;
        if !ship.vitals.is_dead() && ship.ship_type != 10 {
            return Err(GameCommandError::MissingCombatSnapshot);
        }
    }
    let mut queued = 0;
    for &member in members {
        let Some(ship) = world.ship(member.ship_id) else {
            return Err(GameCommandError::InvalidSquad);
        };
        // The source executes each live squad member in GetShips order.
        if ship.vitals.is_dead() {
            continue;
        }
        let Some(target_id) = world.game_bomb_current_target(member.ship_id)
        else { continue; };
        let Some(target) = world.ship(target_id) else { continue; };
        if target.vitals.is_dead() || target.side == ship.side {
            continue;
        }
        let destination = world.ship_body(target_id)
            .ok_or(GameCommandError::InvalidCombatSnapshot)?
            .motion.position;
        world.validate_ship_game_order(member.ship_id)?;
        world.move_ship_to_tracked_point(member.ship_id, GamePointOrder {
            destination,
            ship_clearance: member.clearance,
            map_bounds: formation.map_bounds,
            has_obstacles: formation.has_obstacles,
            can_override_bounds: formation.can_override_bounds,
            newly_found_obstacle: false,
        })?;
        if target_in_proximity(member.ship_id, target_id)? &&
            !world.frozen_after_elimination {
            // The caller has already checked the real Level's cached
            // trigger membership. Do not replace that with radial math.
            queued += u64::from(world.queue_game_fire_barge_detonation(
                member.ship_id, command_outcome_id,
            )?);
        }
    }
    Ok(queued)
}


/// Source-compatible timed behavior for a squad consisting ONLY of Fire
/// Barges. This is not a replacement for the Striker/Yellow Jacket states,
/// nor does it create any separate projectile or damage implementation.
pub struct NativeFireBargeBombingRun {
    target: NativeAttackTarget,
    members: Vec<SquadFormationMember>,
    formation: SquadFormationOptions,
    fast_poll: bool,
    timer_loops: u64,
}

impl NativeFireBargeBombingRun {
    /// Source Execute assigns all live bombs before installing its
    /// two-second immediate recurring command timer. Caller owns both
    /// ScaledTimer and the normal HiveMind timeout/outcome settlement.
    pub fn start(
        world: &mut CombatWorld,
        roster: &NativeSquadRoster,
        random: &mut RandomStream,
        squad: NativeSquadOrder<'_>,
        enemy_owner: SquadLifetime,
        selection: &ResolvedHiveMindCommand,
        weapons: &[NativeBombWeapon],
        enemies: &[NativeEnemyTargetStats],
    ) -> Result<Option<Self>, GameCommandError> {
        // Do not partially initialize mixed bomber squads and later
        // pretend their Striker/Yellow Jacket bombs have executed.
        for member in squad.members {
            let ship = world.ship(member.ship_id)
                .ok_or(GameCommandError::InvalidSquad)?;
            if ship.ship_type != 10 {
                return Err(GameCommandError::MissingCombatSnapshot);
            }
        }
        let mut target = NativeAttackTarget::new(enemy_owner.squad_id);
        target.validate(squad.owner, enemy_owner)?;
        if !assign_initial_bombing_run_targets(
            world, roster, random, squad, enemy_owner,
            selection, weapons, enemies,
        )? {
            return Ok(None);
        }
        Ok(Some(Self {
            target,
            members: squad.members.to_vec(),
            formation: squad.formation,
            fast_poll: false,
            timer_loops: 0,
        }))
    }

    pub fn enemy_squad_id(&self) -> i64 {
        self.target.squad_id()
    }

    /// Unity BombingRun.Timer, Fire Barge-only execution.
    /// Caller must invoke after the existing command timer reports due.
    /// The Level validates live enemy lifetime and supplies the real
    /// ProximityCollider.NearbyEnemyShips membership for each current
    /// Fire Barge target. The game world executes physical detonations on
    /// the next fixed step, before that step's ordinary event consumers.
    pub fn on_poll(
        &mut self,
        world: &mut CombatWorld,
        timer: &mut ScaledTimer,
        owner: SquadLifetime,
        enemy_owner: SquadLifetime,
        enemy_alive: bool,
        command_outcome_id: i64,
        mut target_in_proximity: impl FnMut(i64, i64)
            -> Result<bool, GameCommandError>,
    ) -> Result<Option<CommandFinishCause>, GameCommandError> {
        if !enemy_alive {
            return Ok(Some(CommandFinishCause::BombingRunCompleted));
        }
        self.target.validate(owner, enemy_owner)?;
        self.timer_loops = self.timer_loops.saturating_add(1);
        let living_count = self.members.iter().filter(|member|
            world.ship(member.ship_id).is_some_and(|ship|
                !ship.vitals.is_dead()
            ),
        ).count();
        let mut tested = 0_usize;
        let mut any_near = false;
        advance_fire_barge_bombing_run(
            world, &self.members, self.formation,
            command_outcome_id,
            |ship_id, target_id| {
                let near = target_in_proximity(ship_id, target_id)?;
                tested += 1;
                any_near |= near;
                Ok(near)
            },
        )?;
        // Unity AreBombersCloseToEnemyTargets returns true only if at
        // least one bomber is near AND every live bomber is near. Without
        // a live selected target the corresponding entry fails that check.
        let all_near = any_near && tested == living_count;
        if !self.fast_poll && all_near {
            timer.reuse(0.25, true, false);
            self.fast_poll = true;
        } else if self.fast_poll && self.timer_loops % 4 == 0 {
            // HaveAllShipsBombed returns true for a Fire Barge-only squad
            // (it checks only Strikers and Yellow Jackets). Every fourth
            // quarter-second callback therefore returns to the 2s poll.
            timer.reuse(2.0, true, false);
            self.fast_poll = false;
        }
        Ok(None)
    }

    /// On command supersession, a newer BombingRun may already have
    /// reassigned the SAME world-owned Bomb weapon. Never release that
    /// newly acquired reservation through the outgoing command's cleanup.
    /// Ordinary replacement with another strategy releases every old lease.
    pub(crate) fn release_superseded(
        &self, world: &mut CombatWorld,
        replacement: Option<&Self>,
    ) {
        for member in &self.members {
            if replacement.is_some_and(|next|
                next.members.iter().any(|new_member|
                    new_member.ship_id == member.ship_id
                )
            ) {
                continue;
            }
            world.release_game_bomb_target_for_ship(member.ship_id);
        }
    }

    /// Unity BombingRun.SetFinalize releases current squad-member Bomb
    /// targets on timeout, replacement, enemy disappearance or retirement.
    /// World death already releases these same leases, so repeats are inert.
    pub fn release_all(&self, world: &mut CombatWorld) {
        for member in &self.members {
            world.release_game_bomb_target_for_ship(member.ship_id);
        }
    }
}
