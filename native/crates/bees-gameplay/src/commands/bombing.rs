//! Source BombingRun.Execute's initial per-bomber targeting transaction.
//! The Level owns authored Bomb.Weapon.Id/Power and GetShips order; the
//! canonical combat world owns damage reservations and physical targeting.
//! One BombingRun timer drives Fire Barges, Yellow Jackets and Strikers;
//! the Striker's persistent readiness/reload remains CombatWorld-owned.

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

/// Activation-only authored Level inputs. The live source roster is
/// borrowed, not copied. The Level must supply genuine IsCarrierSquad
/// identity in addition to the live Bomb weapons and target roster.
#[derive(Clone, Copy)]
pub struct NativeBombingActivation<'a> {
    pub roster: &'a NativeSquadRoster,
    pub enemy_owner: SquadLifetime,
    /// Authored Unity Squad.IsCarrierSquad; never infer from ship types.
    pub is_carrier_squad: bool,
    pub weapons: &'a [NativeBombWeapon],
    pub enemies: &'a [NativeEnemyTargetStats],
}

/// The one authoritative Level projection needed whenever an authored
/// BombingRun command's timer fires. Nearby pairs are ONLY those for
/// which the current bomb target is in the ship's real Unity
/// ProximityCollider.NearbyEnemyShips set. This is not collider-center
/// distance or a second physics contact registry.
pub struct NativeBombingTimerSnapshot {
    pub enemy_owner: SquadLifetime,
    pub enemy_alive: bool,
    pub nearby_targets: Vec<(i64, i64)>,
    /// Populate only when need_retarget was true. This is the original
    /// enemy GetShips order and the actual live Bomb weapon metadata,
    /// not a second persistent target registry.
    pub retarget_enemies: Vec<NativeEnemyTargetStats>,
    pub retarget_weapons: Vec<NativeBombWeapon>,
}

/// Unity BombingRun.Execute calls GetTarget on every current bomber in
/// squad order before starting its recurring two-second command timer.
/// A rejected native replacement preserves the prior command's
/// reservations; a brand-new rejected command leaves no Bomb leases.
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
    assign_initial_special_bomb_targets(
        world, roster, random, squad, enemy_owner, selection,
        weapons, enemies, LocalCommandKind::BombingRun,
    )
}

/// Charge.GetTargetShip uses the very same Bomb.MakeSortedTargetingList
/// and Bomb.DetermineTargetShip as BombingRun.GetTarget.
pub(super) fn assign_initial_charge_targets(
    world: &mut CombatWorld,
    roster: &NativeSquadRoster,
    random: &mut RandomStream,
    squad: NativeSquadOrder<'_>,
    enemy_owner: SquadLifetime,
    selection: &ResolvedHiveMindCommand,
    weapons: &[NativeBombWeapon],
    enemies: &[NativeEnemyTargetStats],
) -> Result<bool, GameCommandError> {
    assign_initial_special_bomb_targets(
        world, roster, random, squad, enemy_owner, selection,
        weapons, enemies, LocalCommandKind::Charge,
    )
}

fn assign_initial_special_bomb_targets(
    world: &mut CombatWorld,
    roster: &NativeSquadRoster,
    random: &mut RandomStream,
    squad: NativeSquadOrder<'_>,
    enemy_owner: SquadLifetime,
    selection: &ResolvedHiveMindCommand,
    weapons: &[NativeBombWeapon],
    enemies: &[NativeEnemyTargetStats],
    expected_command: LocalCommandKind,
) -> Result<bool, GameCommandError> {
    if selection.local_command != expected_command ||
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
        let eligible = match expected_command {
            LocalCommandKind::Charge => ship.ship_type == 0,
            LocalCommandKind::BombingRun => matches!(ship.ship_type, 10 | 19 | 22),
            _ => false,
        };
        if ship.vitals.is_dead() || ship.side != own_entry.side ||
            !eligible {
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
        return Ok(false);
    }
    // Do not release an outgoing command's reservations on failed
    // activation. The Level retains its old command and old OutcomeIds
    // until the replacement is fully installed. This is one short-lived
    // transaction over the canonical world ledger, not another ledger.
    let assigned = world.with_atomic_game_bomb_assignments(|world| {
        // Each Bomb.GetTarget rebuilds its own sorted GetShips copy.
        // Reusing the preceding bomber's ranking changes FirstSeen,
        // Random and all tie-sensitive strategies. Reuse one scratch vec.
        let mut ranked = Vec::with_capacity(enemies.len());
        for weapon in weapons {
            ranked.clear();
            ranked.extend_from_slice(enemies);
            if select_native_game_bomb_target(
                world, roster, weapon.ship_id, weapon.weapon_id, weapon.power,
                enemy_owner, selection.effective_shooting, random, &mut ranked,
            )?.is_none() {
                return Ok(None);
            }
        }
        Ok(Some(()))
    })?;
    Ok(assigned.is_some())
}

/// Source Ship.HasTargetEnemyShipToFollow: the selected target is
/// non-null and not dead. Movement and retarget phase decisions share
/// this predicate so a new target does not receive an early move order.
pub(super) fn has_live_game_bomb_target(
    world: &CombatWorld, ship_id: i64,
) -> bool {
    world.game_bomb_current_target(ship_id)
        .and_then(|id| world.ship(id))
        .is_some_and(|target| !target.vitals.is_dead())
}

/// Reuse the existing Bomb.SetTargetShip strategy and world lease.
/// Healthy, still-selected targets do not consume RNG or metadata work.
pub(super) fn retarget_selected_game_bomb(
    world: &mut CombatWorld,
    roster: &NativeSquadRoster,
    random: &mut RandomStream,
    ship_id: i64, enemy_owner: SquadLifetime,
    selection: &ResolvedHiveMindCommand,
    weapons: &[NativeBombWeapon],
    enemies: &[NativeEnemyTargetStats],
    ranked: &mut Vec<NativeEnemyTargetStats>,
) -> Result<bool, GameCommandError> {
    if has_live_game_bomb_target(world, ship_id) {
        return Ok(true);
    }
    let weapon = weapons.iter().find(|weapon|
        weapon.ship_id == ship_id,
    ).ok_or(GameCommandError::MissingCombatSnapshot)?;
    if weapon.weapon_id <= 0 || weapon.power < 0 {
        return Err(GameCommandError::InvalidCombatSnapshot);
    }
    let entry = roster.get(enemy_owner)
        .ok_or(GameCommandError::InvalidEnemy)?;
    if enemies.len() != entry.members.len() {
        return Err(GameCommandError::InvalidCombatSnapshot);
    }
    ranked.clear();
    ranked.extend_from_slice(enemies);
    Ok(select_native_game_bomb_target(
        world, roster, ship_id, weapon.weapon_id, weapon.power,
        enemy_owner, selection.effective_shooting, random, ranked,
    )?.is_some())
}

/// Source GetSquad().GetShips order is retained throughout the run.
/// The Level owns command state, while actual weapon leases, reload
/// readiness, hulls, and movement remain in the single combat world.
#[derive(Clone, Copy, Debug)]
struct StrikerRunState {
    ship_id: i64,
    completed: bool,
    returned: bool,
}

pub(super) fn send_bomber_to_selected_target(
    world: &mut CombatWorld,
    member: SquadFormationMember,
    formation: SquadFormationOptions,
) -> Result<Option<i64>, GameCommandError> {
    let Some(target_id) = world.game_bomb_current_target(member.ship_id)
    else { return Ok(None) };
    let Some(ship) = world.ship(member.ship_id) else {
        return Err(GameCommandError::InvalidSquad);
    };
    let Some(target) = world.ship(target_id) else {
        return Ok(None);
    };
    if target.vitals.is_dead() || target.side == ship.side {
        return Ok(None);
    }
    let destination = world.ship_body(target_id)
        .ok_or(GameCommandError::InvalidCombatSnapshot)?
        .motion.position;
    world.move_ship_to_tracked_point(member.ship_id, GamePointOrder {
        destination, ship_clearance: member.clearance,
        map_bounds: formation.map_bounds,
        has_obstacles: formation.has_obstacles,
        can_override_bounds: formation.can_override_bounds,
        newly_found_obstacle: false,
    })?;
    Ok(Some(target_id))
}

/// Unity Striker.ReturnToCarrierIfNecessary: the source returns to the
/// carrier's current location plus its authored formation offset. Arrival
/// may rearm immediately; the independent 1s reload timer also continues.
fn return_game_striker(
    world: &mut CombatWorld,
    member: SquadFormationMember,
    formation: SquadFormationOptions,
) -> Result<bool, GameCommandError> {
    let carrier = world.game_striker_carrier(member.ship_id)
        .ok_or(GameCommandError::MissingCombatSnapshot)?;
    let Some(carrier_id) = carrier.carrier_id else { return Ok(false) };
    let Some(ship) = world.ship(carrier_id) else { return Ok(false) };
    if ship.vitals.is_dead() { return Ok(false) }
    let Some(body) = world.ship_body(carrier_id) else { return Ok(false) };
    if !body.active { return Ok(false) }
    let destination = body.motion.position + carrier.offset_from_center;
    if world.rearm_game_striker_near_carrier(member.ship_id)? {
        return Ok(true);
    }
    world.move_ship_to_tracked_point(member.ship_id, GamePointOrder {
        destination, ship_clearance: member.clearance,
        map_bounds: formation.map_bounds,
        has_obstacles: formation.has_obstacles,
        can_override_bounds: formation.can_override_bounds,
        newly_found_obstacle: false,
    })?;
    Ok(false)
}

/// Return guard from BombingRun.HaveAllShipsFinished: a Striker with a
/// living carrier must complete its actual return, while a Striker whose
/// carrier died does not block command finalization indefinitely.
fn has_living_game_striker_carrier(
    world: &CombatWorld, striker_id: i64,
) -> bool {
    world.game_striker_carrier(striker_id)
        .and_then(|c| c.carrier_id)
        .and_then(|id| world.ship(id).map(|ship| (id, ship)))
        .is_some_and(|(id, ship)|
            !ship.vitals.is_dead() &&
            world.ship_body(id).is_some_and(|body| body.active)
        )
}

/// Source CheckIfStrikersAreDefenseless is an Execute-time test ONLY for
/// a CarrierSquad. A non-null Carrier counts even if the ship is dead.
/// Do not reserve Bombs or install a timer for this unavailable command.
pub(super) fn defenseless_carrier_strikers(
    world: &CombatWorld, squad: NativeSquadOrder<'_>,
    is_carrier_squad: bool,
) -> bool {
    is_carrier_squad && !squad.members.is_empty() &&
        squad.members.iter().all(|member|
            world.ship(member.ship_id).is_some_and(|ship|
                ship.ship_type == 19 && !ship.vitals.is_dead()
            ) &&
            world.game_striker_bomb_ready(member.ship_id) == Some(false) &&
            world.game_striker_carrier(member.ship_id)
                .is_some_and(|carrier| carrier.carrier_id.is_none())
        )
}

/// Unity's one BombingRun command state for mixed authored bombers.
/// Striker's ship-owned bomb readiness is separate from the command's
/// per-run completed/returned flags. Physical damage and fuses stay in
/// CombatWorld; this controller does not duplicate combat mechanics.
pub struct NativeBombingRun {
    target: NativeAttackTarget,
    members: Vec<SquadFormationMember>,
    formation: SquadFormationOptions,
    fast_poll: bool,
    timer_loops: u64,
    has_contact_bombers: bool,
    only_strikers: bool,
    strikers: Vec<StrikerRunState>,
    /// Reused for strategy-specific sorting on rare lost-target polls.
    ranked_enemies: Vec<NativeEnemyTargetStats>,
}

impl NativeBombingRun {
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
        is_carrier_squad: bool,
    ) -> Result<Option<Self>, GameCommandError> {
        let mut has_contact_bombers = false;
        let mut only_strikers = true;
        let mut strikers = Vec::new();
        if weapons.len() != squad.members.len() {
            return Err(GameCommandError::InvalidSquad);
        }
        for (member, weapon) in squad.members.iter().zip(weapons) {
            let ship = world.ship(member.ship_id)
                .ok_or(GameCommandError::InvalidSquad)?;
            if !matches!(ship.ship_type, 10 | 19 | 22) {
                return Err(GameCommandError::MissingCombatSnapshot);
            }
            // This command will move every living bomber on its first
            // timer. Fail before obtaining any Bomb.TargetShip leases if
            // the authored movement profile or control ownership is absent.
            world.validate_ship_game_order(member.ship_id)?;
            only_strikers &= ship.ship_type == 19;
            has_contact_bombers |= matches!(ship.ship_type, 19 | 22);
            if ship.ship_type == 19 {
                if world.game_striker_bomb_ready(member.ship_id).is_none() {
                    return Err(GameCommandError::MissingCombatSnapshot);
                }
                strikers.push(StrikerRunState {
                    ship_id: member.ship_id, completed: false, returned: false,
                });
            }
            if weapon.ship_id != member.ship_id ||
                (ship.ship_type == 10 &&
                    world.configured_fire_barge_bomb_power(member.ship_id) !=
                        Some(weapon.power))
            {
                return Err(GameCommandError::MissingCombatSnapshot);
            }
        }
        strikers.sort_unstable_by_key(|s| s.ship_id);
        if defenseless_carrier_strikers(world, squad, is_carrier_squad) {
            return Ok(None);
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
            has_contact_bombers,
            only_strikers,
            strikers,
            ranked_enemies: Vec::new(),
        }))
    }

    pub fn enemy_squad_id(&self) -> i64 {
        self.target.squad_id()
    }

    /// A returning/completed Striker cannot drop twice in one run, and
    /// a dead Yellow Jacket has no physical contact callback. Avoid
    /// requesting a full Level contact projection on those quiet ticks.
    /// The Level still supplies true OnTriggerEnter2D contacts whenever a
    /// live, ready bomber retains a selected Bomb.TargetShip.
    pub fn needs_contact_events(&self, world: &CombatWorld) -> bool {
        self.has_contact_bombers && self.members.iter().any(|member| {
            let Some(ship) = world.ship(member.ship_id) else { return false };
            if ship.vitals.is_dead() { return false }
            let ready = match ship.ship_type {
                19 => self.strikers.binary_search_by_key(
                    &member.ship_id, |state| state.ship_id,
                ).ok().is_some_and(|index| !self.strikers[index].completed) &&
                    world.game_striker_bomb_ready(member.ship_id) == Some(true),
                22 => true,
                _ => false,
            };
            ready && world.game_bomb_current_target(member.ship_id)
                .and_then(|id| world.ship(id))
                .is_some_and(|target| !target.vitals.is_dead())
        })
    }

    /// Striker.DropBomb calls CompleteRun synchronously after creating the
    /// real fuse. Mark only that original command's owned ship, after the
    /// combat world has confirmed delivery and transferred the Bomb lease.
    pub fn may_deliver_contact(&self, ship_id: i64) -> bool {
        match self.strikers.binary_search_by_key(
            &ship_id, |state| state.ship_id,
        ) {
            Ok(index) => !self.strikers[index].completed,
            // Yellow Jackets and Fire Barges have no per-run drop flag.
            Err(_) => true,
        }
    }

    pub fn record_striker_drop(&mut self, ship_id: i64) -> bool {
        let Ok(index) = self.strikers.binary_search_by_key(
            &ship_id, |state| state.ship_id,
        ) else { return false };
        let state = &mut self.strikers[index];
        if state.completed { return false }
        state.completed = true;
        state.returned = false;
        true
    }

    fn all_live_ships_finished(&self, world: &CombatWorld) -> bool {
        self.members.iter().all(|member| {
            let Some(ship) = world.ship(member.ship_id) else { return true };
            if ship.vitals.is_dead() { return true }
            match ship.ship_type {
                10 | 22 => false,
                19 => self.strikers.binary_search_by_key(
                    &member.ship_id, |state| state.ship_id,
                ).ok().is_some_and(|index| {
                    let state = &self.strikers[index];
                    state.completed && (state.returned ||
                        !has_living_game_striker_carrier(
                            world, member.ship_id,
                        ))
                }),
                _ => false,
            }
        })
    }

    /// Only demand costly authored target metadata when a live, still
    /// pursuing bomber has lost its original selected enemy. A Striker
    /// returning to its carrier already dropped its bomb; it does not
    /// retarget until a new BombingRun command.
    pub fn needs_retarget(&self, world: &CombatWorld) -> bool {
        self.members.iter().any(|member| {
            let Some(ship) = world.ship(member.ship_id) else {
                return false;
            };
            if ship.vitals.is_dead() { return false }
            match ship.ship_type {
                19 => {
                    if self.strikers.binary_search_by_key(
                        &member.ship_id, |state| state.ship_id,
                    ).ok().is_some_and(|index|
                        self.strikers[index].completed
                    ) || world.game_striker_bomb_ready(member.ship_id) !=
                        Some(true) {
                        return false;
                    }
                }
                22 => {}
                // Original BombingRun.Timer does not retarget Fire Barges
                // whose selected enemy died. Avoid requesting metadata that
                // Unity would not consume.
                _ => return false,
            }
            !has_live_game_bomb_target(world, member.ship_id)
        })
    }

    /// Unity BombingRun.Timer for Fire Barge / Yellow Jacket squads.
    /// Caller must invoke after the existing command timer reports due.
    /// The Level validates live enemy lifetime and supplies the real
    /// ProximityCollider.NearbyEnemyShips membership for the current
    /// Fire Barge/Yellow Jacket targets. The game world executes physical
    /// Fire Barge detonations on
    /// the next fixed step, before that step's ordinary event consumers.
    pub fn on_poll(
        &mut self,
        world: &mut CombatWorld,
        timer: &mut ScaledTimer,
        owner: SquadLifetime,
        enemy_owner: SquadLifetime,
        enemy_alive: bool,
        selection: &ResolvedHiveMindCommand,
        roster: &NativeSquadRoster,
        random: &mut RandomStream,
        retarget_enemies: &[NativeEnemyTargetStats],
        retarget_weapons: &[NativeBombWeapon],
        mut target_in_proximity: impl FnMut(i64, i64)
            -> Result<bool, GameCommandError>,
    ) -> Result<Option<CommandFinishCause>, GameCommandError> {
        // A recycled enemy squad number cannot complete an unrelated run.
        self.target.validate(owner, enemy_owner)?;
        let command_outcome_id = selection.command_outcome_id;
        if !enemy_alive && !self.only_strikers {
            return Ok(Some(CommandFinishCause::BombingRunCompleted));
        }
        self.timer_loops = self.timer_loops.saturating_add(1);
        if !enemy_alive {
            for state in &mut self.strikers {
                state.completed = true;
            }
        }
        let mut any_near = false;
        let mut all_near = true;
        // Match Unity's GetShips order, including mixed bomber squads.
        // A finished Striker is excluded from subsequent pursuit orders.
        for &member in &self.members {
            let ship = world.ship(member.ship_id)
                .ok_or(GameCommandError::InvalidSquad)?;
            if ship.vitals.is_dead() { continue; }
            let ship_type = ship.ship_type;
            match ship_type {
                19 => {
                    let index = self.strikers.binary_search_by_key(
                        &member.ship_id, |state| state.ship_id,
                    ).map_err(|_| GameCommandError::MissingCombatSnapshot)?;
                    if self.strikers[index].returned { continue; }
                    let completed = self.strikers[index].completed;
                    let ready = world.game_striker_bomb_ready(member.ship_id)
                        .ok_or(GameCommandError::MissingCombatSnapshot)?;
                    if completed || !ready {
                        let arrived = return_game_striker(
                            world, member, self.formation,
                        )?;
                        if arrived {
                            if completed {
                                self.strikers[index].returned = true;
                            } else {
                                send_bomber_to_selected_target(
                                    world, member, self.formation,
                                )?;
                            }
                        }
                        // HasCompletedRun permits no-proximity completion
                        // of AreBombersCloseToEnemyTargets.
                        if !completed { all_near = false; }
                    } else {
                        let had_live_target =
                            has_live_game_bomb_target(world, member.ship_id);
                        if !retarget_selected_game_bomb(
                            world, roster, random, member.ship_id,
                            enemy_owner, selection,
                            retarget_weapons, retarget_enemies,
                            &mut self.ranked_enemies,
                        )? {
                            return Ok(Some(CommandFinishCause::NoEnemyTargets));
                        }
                        // Unity's Timer calls GetTarget on a lost target;
                        // pursuit resumes on the next timer, not this one.
                        if !had_live_target {
                            all_near = false;
                            continue;
                        }
                        if let Some(target_id) =
                            send_bomber_to_selected_target(
                                world, member, self.formation,
                            )? {
                            let near = target_in_proximity(
                                member.ship_id, target_id,
                            )?;
                            any_near |= near;
                            all_near &= near;
                        } else {
                            all_near = false;
                        }
                    }
                }
                10 | 22 => {
                    let had_live_target =
                        has_live_game_bomb_target(world, member.ship_id);
                    if ship_type == 22 && !retarget_selected_game_bomb(
                        world, roster, random, member.ship_id,
                        enemy_owner, selection,
                        retarget_weapons, retarget_enemies,
                        &mut self.ranked_enemies,
                    )? {
                        return Ok(Some(CommandFinishCause::NoEnemyTargets));
                    }
                    // Yellow Jacket.GetTarget only chooses an enemy this
                    // timer; it will pursue that target on the next one.
                    // Fire Barge has no lost-target GetTarget branch at all
                    // in the original BombingRun.Timer.
                    if !had_live_target {
                        all_near = false;
                        continue;
                    }
                    if let Some(target_id) = send_bomber_to_selected_target(
                        world, member, self.formation,
                    )? {
                        let near = target_in_proximity(
                            member.ship_id, target_id,
                        )?;
                        any_near |= near;
                        all_near &= near;
                        if ship_type == 10 && near &&
                            !world.frozen_after_elimination {
                            world.queue_game_fire_barge_detonation(
                                member.ship_id, command_outcome_id,
                            )?;
                        }
                    } else {
                        all_near = false;
                    }
                }
                _ => return Err(GameCommandError::MissingCombatSnapshot),
            }
        }
        if self.all_live_ships_finished(world) {
            return Ok(Some(CommandFinishCause::BombingRunCompleted));
        }
        let close_to_targets = any_near && all_near;
        if !self.fast_poll && close_to_targets {
            timer.reuse(0.25, true, false);
            self.fast_poll = true;
        } else if self.fast_poll && self.timer_loops % 4 == 0 {
            let all_bombed = self.strikers.iter().all(|s| s.completed) &&
                self.members.iter().all(|member|
                    world.ship(member.ship_id).is_none_or(|ship|
                        ship.vitals.is_dead() || ship.ship_type != 22
                    )
                );
            if all_bombed || !close_to_targets || !enemy_alive {
                timer.reuse(2.0, true, false);
                self.fast_poll = false;
            }
        }
        Ok(None)
    }

    /// On command supersession, a newer BombingRun may already have
    /// reassigned the SAME world-owned Bomb weapon. Never release that
    /// newly acquired reservation through the outgoing command's cleanup.
    /// Ordinary replacement with another strategy releases every old lease.
    pub(crate) fn release_superseded(
        &self, world: &mut CombatWorld, retained_ids: &[i64],
    ) {
        release_superseded_bomb_targets(
            world, &self.members, retained_ids,
        );
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

/// Both Charge and BombingRun own the SAME physical Bomb weapon leases.
/// A new command assigns the replacement lease before superseding the
/// previous one. Cleanup must never erase that newly selected target.
pub(super) fn release_superseded_bomb_targets(
    world: &mut CombatWorld,
    outgoing: &[SquadFormationMember],
    retained_ids: &[i64],
) {
    let retained = (retained_ids.len() > 8).then(|| {
        retained_ids.iter().copied().collect::<HashSet<_>>()
    });
    for member in outgoing {
        let preserved = match &retained {
            Some(ids) => ids.contains(&member.ship_id),
            None => retained_ids.contains(&member.ship_id),
        };
        if !preserved {
            world.release_game_bomb_target_for_ship(member.ship_id);
        }
    }
}
