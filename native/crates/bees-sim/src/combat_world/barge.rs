//! Source Barge.ChargeForward, HitShip and StopCharge for RL control.
//! Phases, health, movement and collision all live in the normal combat world.

use super::*;
use crate::movement::STOP_MOVEMENT;
use crate::geometry::heading_towards;

const WINDUP: f32 = 2.0;
const CHARGING: f32 = 1.0;
const COOLDOWN: f32 = 10.0;
const CHARGE_SPEED: f32 = 80.0;
const FINISH_DAMAGE: i32 = 200;

#[derive(Clone, Copy, Debug)]
pub(super) struct PolicyBargeCharge {
    pub(super) power: i32,
    /// 0 ready; 1 wind-up; 2 charging; 3 cooldown (Unity RlChargePhase).
    pub(super) phase: u8,
    pub(super) elapsed: f32,
    pub(super) heading: u16,
}

/// A scripted Barge runs the same authored phase durations and physical
/// hit transaction as a policy Barge; its selected Bomb weapon and the
/// originating HiveMind command determine target and attribution.
#[derive(Clone, Copy, Debug)]
pub(super) struct GameBargeCharge {
    pub(super) phase: u8, // 1 wind-up, 2 charge, 3 cooldown, 4 completed
    pub(super) elapsed: f32,
    pub(super) target_id: Option<i64>,
    /// Barge.ChargeForward aims at Level.ForceBounds(target.GetPosition)
    /// at wind-up completion, even when the Barge itself can override
    /// bounds (Ship.MoveToDirectionOfPoint always calls ForceBounds).
    pub(super) map_bounds: Aabb,
    /// Source Ship.CanOverrideBounds, supplied by the owning Level.
    pub(super) can_override_bounds: bool,
    pub(super) power: i32,
    pub(super) command_outcome_id: Option<i64>,
}

impl CombatWorld {
    /// Initiate a real scripted Barge.ChargeForward only after the owning
    /// Level's HasTargetsWithinChargingRange verified heading, obstacles
    /// and current Charge.ShipsWithinRange membership. This world checks
    /// the originally reserved Bomb target, real ship state and movement
    /// profile before accepting the command.
    pub fn try_start_game_barge_charge(
        &mut self, ship_id: i64, command_outcome_id: Option<i64>,
        map_bounds: Aabb, can_override_bounds: bool,
    ) -> Result<bool, CombatWorldError> {
        if !map_bounds.min.is_finite() || !map_bounds.max.is_finite() ||
            map_bounds.min.x > map_bounds.max.x ||
            map_bounds.min.y > map_bounds.max.y {
            return Err(CombatWorldError::InvalidGameMovementOrder);
        }
        if command_outcome_id.is_some_and(|id| id <= 0) {
            return Err(CombatWorldError::InvalidBombTarget);
        }
        let &ship_index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[ship_index].ship_type != 0 {
            return Err(CombatWorldError::InvalidBombTarget);
        }
        if self.frozen_after_elimination ||
            self.ships[ship_index].vitals.is_dead() ||
            !self.physics.ships()[ship_index].active {
            return Ok(false);
        }
        if self.policy_ship_runtime.contains_key(&ship_id) {
            return Err(CombatWorldError::UnsupportedPolicyActions);
        }
        if self.game_barge_charges.contains_key(&ship_id) {
            return Ok(false);
        }
        self.validate_ship_game_order(ship_id)?;
        let weapon_id = *self.game_bomb_weapon_by_ship.get(&ship_id)
            .ok_or(CombatWorldError::InvalidBombTarget)?;
        let held = self.reservations.held_by(weapon_id)
            .ok_or(CombatWorldError::InvalidBombTarget)?;
        if held.side != self.ships[ship_index].side {
            return Err(CombatWorldError::InvalidBombTarget);
        }
        let Some(&target_index) = self.ship_by_id.get(&held.target_id) else {
            return Ok(false);
        };
        if self.ships[target_index].vitals.is_dead() ||
            !self.physics.ships()[target_index].active ||
            self.ships[target_index].side == held.side {
            return Ok(false);
        }
        let target_id = held.target_id;
        let power = held.amount;
        self.begin_game_barge_charge(ship_index, Some(target_id),
            map_bounds, can_override_bounds, power, command_outcome_id)
    }

    /// An exact non-policy Barge prefab can be activated by the player
    /// even without a BombingRun target reservation, as in Unity's
    /// SquadActionBox.Charge -> Barge.ChargeForward(null).
    pub fn configure_game_barge(
        &mut self, ship_id: i64, power: i32, can_override_bounds: bool,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].ship_type != 0 ||
            self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active || power < 0 ||
            self.policy_ship_runtime.contains_key(&ship_id) ||
            self.game_barge_player_config.contains_key(&ship_id) {
            return Err(CombatWorldError::InvalidPolicyBarge);
        }
        self.game_barge_player_config.insert(ship_id, (power, can_override_bounds));
        Ok(())
    }

    pub fn player_game_barge_configured(&self, ship_id: i64) -> bool {
        self.game_barge_player_config.contains_key(&ship_id)
    }

    pub fn player_game_barge_charge_ready(&self, ship_id: i64) -> bool {
        if self.frozen_after_elimination ||
            !self.game_barge_player_config.contains_key(&ship_id) ||
            !self.game_ship_steering.contains_key(&ship_id) ||
            self.pending_player_barge_charges.contains(&ship_id) ||
            // Command-owned phase-4 entries must remain intact until the
            // command director observes completion/finalization. Player
            // input may not overwrite that still-owned state.
            self.game_barge_charges.contains_key(&ship_id) {
            return false;
        }
        self.ship(ship_id).is_some_and(|ship| !ship.vitals.is_dead()) &&
            self.ship_body(ship_id).is_some_and(|body| body.active &&
                !body.motion.is_policy_controlled &&
                !body.motion.cannot_change_movement_orders)
    }

    pub fn queue_player_barge_charge(
        &mut self, ship_id: i64,
    ) -> Result<bool, CombatWorldError> {
        if !self.game_barge_player_config.contains_key(&ship_id) {
            return Err(CombatWorldError::InvalidPolicyBarge);
        }
        if !self.player_game_barge_charge_ready(ship_id) { return Ok(false); }
        self.pending_player_barge_charges.push(ship_id);
        Ok(true)
    }

    pub(super) fn apply_pending_player_barge_charges(
        &mut self,
    ) -> Result<(), CombatWorldError> {
        for index in 0..self.pending_player_barge_charges.len() {
            let ship_id = self.pending_player_barge_charges[index];
            // Exclude this request from its own readiness check; distinct
            // players cannot queue the same ship twice in a fixed tick.
            let Some(&(power, can_override_bounds)) =
                self.game_barge_player_config.get(&ship_id) else { continue };
            let Some(&ship_index) = self.ship_by_id.get(&ship_id) else { continue };
            if self.frozen_after_elimination ||
                self.ships[ship_index].vitals.is_dead() ||
                !self.physics.ships()[ship_index].active ||
                self.game_barge_charges.contains_key(&ship_id) {
                continue;
            }
            let map_bounds = self.level_geometry.as_ref()
                .ok_or(CombatWorldError::InvalidGameMovementOrder)?
                .map_bounds;
            self.validate_ship_game_order(ship_id)?;
            self.begin_game_barge_charge(ship_index, None, map_bounds,
                can_override_bounds, power, None)?;
        }
        self.pending_player_barge_charges.clear();
        Ok(())
    }

    /// The original targeted command and manual forward charge share one
    /// phase/collision/recoil machine; only target choice and attribution
    /// differ, not physics or combat damage.
    fn begin_game_barge_charge(
        &mut self, ship_index: usize, target_id: Option<i64>,
        map_bounds: Aabb, can_override_bounds: bool, power: i32,
        command_outcome_id: Option<i64>,
    ) -> Result<bool, CombatWorldError> {
        let ship_id = self.ships[ship_index].entity_id;
        self.stop_ship_game_movement(ship_id)?;
        self.physics.ships_mut()[ship_index].motion
            .cannot_change_movement_orders = true;
        self.barge_charge_hits.retain(|&(attacker, _)| attacker != ship_id);
        self.game_barge_charges.insert(ship_id, GameBargeCharge {
            phase: 1, elapsed: 0.0, target_id,
            map_bounds, can_override_bounds, power, command_outcome_id,
        });
        self.game_barge_measurements.starts += 1;
        self.events.push(CombatEvent::BargeChargeStarted { ship_id });
        Ok(true)
    }

    pub fn game_barge_charge_completed(&self, ship_id: i64) -> Option<bool> {
        self.game_barge_charges.get(&ship_id)
            .map(|state| state.phase == 4)
    }

    /// Charge.SetFinalize cancels wind-up/charge/cooldown and resets its
    /// movement lock. All in-flight game charges have one owner, the
    /// authoritative Barge ship; no orphan timer can fire after reset.
    pub fn reset_game_barge_charge(&mut self, ship_id: i64) {
        let Some(charge) = self.game_barge_charges.remove(&ship_id) else {
            return;
        };
        self.barge_charge_hits.retain(|&(attacker, _)|
            attacker != ship_id,
        );
        let Some(&index) = self.ship_by_id.get(&ship_id) else {
            return;
        };
        if self.physics.ships()[index].active &&
            !self.ships[index].vitals.is_dead() {
            let motion = &mut self.physics.ships_mut()[index].motion;
            motion.cannot_change_movement_orders = false;
            motion.current_speed = motion.base_speed;
            if charge.phase != 4 {
                // The old charge's direction is still in steering. While
                // the movement lock was held, the replacement's point
                // orders were stored independently in pending_game_point_orders.
                // Stop only old steering; retain those pending orders for
                // the normal next-tick unlocked-order handoff. After phase 4
                // a replacement could already have installed new steering.
                if let Some(controller) = self.game_ship_steering.get_mut(&ship_id) {
                    controller.stop(motion);
                } else {
                    motion.stop_moving();
                }
            }
        }
    }

    /// Unity Barge.ChargeForward / StopCharge / FinishCoolDown. These are
    /// world-time phases, not independent gameplay/learner timers.
    pub(super) fn advance_game_barge_phases(
        &mut self, dt: f32,
    ) -> Result<(), CombatWorldError> {
        let mut transitions = Vec::new();
        for (&ship_id, charge) in &mut self.game_barge_charges {
            if charge.phase == 4 { continue }
            charge.elapsed += dt;
            match charge.phase {
                1 if charge.elapsed >= WINDUP => {
                    charge.phase = 2;
                    charge.elapsed -= WINDUP;
                    transitions.push((ship_id, 2));
                }
                2 if charge.elapsed >= CHARGING =>
                    transitions.push((ship_id, 3)),
                3 if charge.elapsed >= COOLDOWN =>
                    transitions.push((ship_id, 4)),
                _ => {}
            }
        }
        for (ship_id, next_phase) in transitions {
            let Some(&index) = self.ship_by_id.get(&ship_id) else {
                self.game_barge_charges.remove(&ship_id);
                continue;
            };
            if self.ships[index].vitals.is_dead() ||
                !self.physics.ships()[index].active {
                self.game_barge_charges.remove(&ship_id);
                continue;
            }
            match next_phase {
                2 => {
                    let charge = self.game_barge_charges[&ship_id];
                    let origin = self.physics.ships()[index].motion.position;
                    let rotation = self.physics.ships()[index].motion
                        .rotation_degrees;
                    let heading = charge.target_id
                        .and_then(|id| self.ship_body(id).filter(|body| body.active)
                        .and_then(|body| self.ship(id)
                            .filter(|target| !target.vitals.is_dead())
                            .map(|_| {
                                let destination = Vec2::new(
                                    body.motion.position.x.clamp(
                                        charge.map_bounds.min.x,
                                        charge.map_bounds.max.x,
                                    ),
                                    body.motion.position.y.clamp(
                                        charge.map_bounds.min.y,
                                        charge.map_bounds.max.y,
                                    ),
                                );
                                destination - origin
                            })))
                        .map_or(rotation, heading_towards);
                    // ChargeForward briefly releases the movement lock to
                    // install its heading. Unity's MoveInDirection stops
                    // *current* movement but does not erase the separately
                    // stored WaitingTargetCoordinates from the wind-up.
                    // Public move_ship_in_direction would clear that pending
                    // order; use the same steering operation directly here.
                    let steering = self.game_ship_steering.get_mut(&ship_id)
                        .ok_or(CombatWorldError::MissingGameMovementProfile)?;
                    let motion = &mut self.physics.ships_mut()[index].motion;
                    motion.cannot_change_movement_orders = false;
                    if !steering.set_direction(motion, heading) {
                        return Err(CombatWorldError::InvalidGameMovementOrder);
                    }
                    motion.current_speed = CHARGE_SPEED;
                    motion.cannot_change_movement_orders = true;
                }
                3 => self.stop_game_barge_charge(ship_id)?,
                4 => {
                    let motion = &mut self.physics.ships_mut()[index].motion;
                    motion.current_speed = motion.base_speed;
                    motion.cannot_change_movement_orders = false;
                    self.game_ship_steering.get_mut(&ship_id)
                        .ok_or(CombatWorldError::MissingGameMovementProfile)?
                        .stop(motion);
                    if let Some(charge) = self.game_barge_charges.get_mut(&ship_id) {
                        charge.phase = 4;
                    }
                    self.game_barge_measurements.completions += 1;
                    // A manually triggered ChargeForward has no command
                    // owner that will later retire the completed state.
                    // Release it at the normal cooldown boundary to avoid
                    // scanning inert per-ship timers forever. Targeted
                    // commands retain their completion token until their
                    // existing command-finalization path observes it.
                    if self.game_barge_charges.get(&ship_id)
                        .is_some_and(|charge| charge.target_id.is_none())
                    {
                        self.reset_game_barge_charge(ship_id);
                    }
                }
                _ => unreachable!("authored game Barge phase"),
            }
        }
        Ok(())
    }

    fn stop_game_barge_charge(
        &mut self, ship_id: i64,
    ) -> Result<(), CombatWorldError> {
        let Some(&index) = self.ship_by_id.get(&ship_id) else {
            return Ok(());
        };
        let Some(charge) = self.game_barge_charges.get_mut(&ship_id)
        else { return Ok(()) };
        if charge.phase != 2 { return Ok(()) }
        charge.phase = 3;
        charge.elapsed = 0.0;
        let steering = self.game_ship_steering.get_mut(&ship_id)
            .ok_or(CombatWorldError::MissingGameMovementProfile)?;
        let motion = &mut self.physics.ships_mut()[index].motion;
        motion.current_speed = 0.0;
        // Unity StopCharge calls StopMoving, clearing TargetDirection
        // as well as velocity. Otherwise fixed_step would continue
        // turning the stationary Barge throughout cooldown.
        // The separate WaitingTargetCoordinates must survive.
        steering.stop(motion);
        motion.cannot_change_movement_orders = true;
        self.barge_charge_hits.retain(|&(attacker, _)|
            attacker != ship_id,
        );
        self.game_barge_measurements.stops += 1;
        self.events.push(CombatEvent::BargeChargeStopped { ship_id });
        if let Some(delta) = self.ships[index].vitals
            .apply_damage(FINISH_DAMAGE, false)? {
            self.reservations.target_health_changed(
                ship_id, self.ships[index].vitals.health(),
            );
            self.events.push(CombatEvent::UnattributedDamage {
                target_id: ship_id, delta,
            });
            if delta.killed {
                self.retire_dead_ship(index, 0, 0)?;
            }
        }
        Ok(())
    }

    /// Unity MapBorder.OnTriggerEnter2D calls StopCharge when a game Barge
    /// is still in its forward-charge phase. This consumes the existing
    /// primary-hull entry stream, not another all-ship border scan.
    /// Charge stops only on ENTER, not while resting inside the border.
    pub(super) fn resolve_game_barge_border_contacts(
        &mut self,
    ) -> Result<(), CombatWorldError> {
        // Most training worlds never run scripted Barge commands. Keep
        // their extra border-event processing constant-time and empty.
        if self.game_barge_charges.is_empty() ||
            self.physics.ship_obstacle_entries().is_empty() {
            return Ok(());
        }
        for i in 0..self.physics.ship_obstacle_entries().len() {
            let contact = self.physics.ship_obstacle_entries()[i];
            let Some(&obstacle_index) =
                self.obstacle_by_id.get(&contact.obstacle_id)
            else { continue };
            if self.obstacles[obstacle_index].kind != ObstacleKind::MapBorder {
                continue;
            }
            if self.game_barge_charges.get(&contact.ship_id)
                .is_some_and(|charge|
                    charge.phase == 2 && !charge.can_override_bounds
                ) {
                self.stop_game_barge_charge(contact.ship_id)?;
                self.game_barge_measurements.border_stops += 1;
            }
        }
        Ok(())
    }

    /// Shared real hull-contact broadphase and identical Barge recoil
    /// transaction used by policy charges. No guessed contact radius.
    pub(super) fn resolve_game_barge_contacts(
        &mut self, collisions: &CollisionMatrix,
    ) -> Result<(), CombatWorldError> {
        let mut after_id = 0_i64;
        loop {
            use std::ops::Bound::{Excluded, Unbounded};
            let next = self.game_barge_charges
                .range((Excluded(after_id), Unbounded))
                .find_map(|(&id, state)|
                    (state.phase == 2).then_some(id)
                );
            let Some(ship_id) = next else { break };
            after_id = ship_id;
            let Some(&index) = self.ship_by_id.get(&ship_id) else { continue };
            let charge = self.game_barge_charges[&ship_id];
            let side = self.ships[index].side;
            if self.physics.ship_contact_candidates(
                index, &mut self.barge_contact_candidates,
            ) {
                self.game_barge_measurements.grid_rebuilds += 1;
            }
            self.game_barge_measurements.candidates +=
                self.barge_contact_candidates.len() as u64;
            for i in 0..self.barge_contact_candidates.len() {
                let target_index = self.barge_contact_candidates[i];
                if !self.physics.ships()[index].active ||
                    self.ships[index].vitals.is_dead() {
                    break;
                }
                let target_id = self.ships[target_index].entity_id;
                if index == target_index || self.ships[target_index].side == side ||
                    self.ships[target_index].vitals.is_dead() ||
                    !self.physics.ships()[target_index].active ||
                    self.barge_charge_hits.contains(&(ship_id, target_id)) {
                    continue;
                }
                self.game_barge_measurements.contact_checks += 1;
                if !self.physics.ship_hulls_overlap(
                    index, target_index, collisions,
                ) { continue }
                self.barge_charge_hits.insert((ship_id, target_id));
                self.game_barge_measurements.hits += 1;
                let (enemy_killed, barge_killed) =
                    self.apply_barge_charge_hit(
                        index, target_index, charge.power,
                        charge.command_outcome_id,
                    )?;
                if barge_killed { break }
                if !enemy_killed {
                    self.stop_game_barge_charge(ship_id)?;
                    break;
                }
            }
        }
        Ok(())
    }

    /// The authored Charge.Power comes from Barge.Weapons[0].Power.
    pub fn configure_policy_barge(
        &mut self, ship_id: i64, charge_power: i32,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].ship_type != 0 ||
            self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active || charge_power < 0 {
            return Err(CombatWorldError::InvalidPolicyBarge);
        }
        let runtime = self.policy_ship_runtime.get_mut(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if runtime.special.is_some() {
            return Err(CombatWorldError::InvalidPolicyBarge);
        }
        runtime.special = Some(PolicyShipSpecial::Barge(PolicyBargeCharge {
            power: charge_power, phase: 0, elapsed: 0.0,
            heading: STOP_MOVEMENT,
        }));
        Ok(())
    }

    /// The RL special reserves charge immediately and prevents later
    /// movement actions from interrupting its phase-locked heading.
    pub(super) fn try_begin_policy_barge_charge(
        &mut self, ship_id: i64,
    ) -> Result<bool, CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let Some(PolicyShipSpecial::Barge(barge)) =
            self.policy_ship_runtime.get_mut(&ship_id)
                .and_then(|runtime| runtime.special.as_mut())
        else {
            return Err(CombatWorldError::InvalidPolicyBarge);
        };
        if barge.phase != 0 || self.ships[index].vitals.is_dead() {
            return Ok(false);
        }
        barge.phase = 1;
        barge.elapsed = 0.0;
        let motion = &mut self.physics.ships_mut()[index].motion;
        motion.stop_moving();
        motion.cannot_change_movement_orders = true;
        self.barge_charge_hits.retain(|&(attacker, _)| attacker != ship_id);
        self.events.push(CombatEvent::BargeChargeStarted { ship_id });
        self.policy_special_measurements.barge_charges_started += 1;
        Ok(true)
    }

    /// Barge's phase timers use the simulation dt, matching its scaled
    /// training clock. The original Unity coroutine moves forward on the
    /// heading at wind-up completion, not toward a guessed enemy position.
    pub(super) fn advance_policy_barge_phases(
        &mut self, dt: f32,
    ) -> Result<(), CombatWorldError> {
        // BTreeMap gives stable IDs without sorting or a temporary vector.
        // Only phase transitions change world-owned health and events.
        for (&ship_id, runtime) in &mut self.policy_ship_runtime {
            let Some(PolicyShipSpecial::Barge(barge)) =
                runtime.special.as_mut() else { continue };
            if barge.phase == 0 { continue; }
            barge.elapsed += dt;
            let index = self.ship_by_id[&ship_id];
            let motion = &mut self.physics.ships_mut()[index].motion;
            match barge.phase {
                1 if barge.elapsed >= WINDUP => {
                    barge.phase = 2;
                    barge.elapsed -= WINDUP;
                    barge.heading = motion.rotation_degrees.round_ties_even()
                        .rem_euclid(360.0) as u16;
                    motion.current_speed = CHARGE_SPEED;
                    motion.movement_direction = barge.heading;
                    motion.cannot_change_movement_orders = true;
                }
                2 if barge.elapsed >= CHARGING => {
                    // Deferred until outside this mutable policy iteration.
                    barge.phase = 4;
                }
                3 if barge.elapsed >= COOLDOWN => {
                    barge.phase = 0;
                    barge.elapsed = 0.0;
                    motion.current_speed = motion.base_speed;
                    motion.cannot_change_movement_orders = false;
                    motion.stop_moving();
                }
                _ => {}
            }
        }
        // Collecting one ended ID at a time avoids a per-frame heap
        // allocation and preserves order for any lethal recoil events.
        loop {
            let ended = self.policy_ship_runtime.iter().find_map(|(&ship_id, runtime)|
                if matches!(runtime.special, Some(PolicyShipSpecial::Barge(
                    PolicyBargeCharge { phase: 4, .. }
                ))) { Some(ship_id) } else { None }
            );
            let Some(ship_id) = ended else { break };
            self.finish_policy_barge_charge(ship_id)?;
        }
        Ok(())
    }

    /// End the active rush exactly once: source StopCharge applies the
    /// fixed 200-damage exhaustion before the 10-second cooldown.
    fn finish_policy_barge_charge(
        &mut self, ship_id: i64,
    ) -> Result<(), CombatWorldError> {
        let Some(&index) = self.ship_by_id.get(&ship_id) else {
            return Err(CombatWorldError::UnknownShip);
        };
        let Some(PolicyShipSpecial::Barge(barge)) =
            self.policy_ship_runtime.get_mut(&ship_id)
                .and_then(|runtime| runtime.special.as_mut())
        else { return Ok(()) };
        if barge.phase != 2 && barge.phase != 4 { return Ok(()); }
        barge.phase = 3;
        barge.elapsed = 0.0;
        let motion = &mut self.physics.ships_mut()[index].motion;
        motion.current_speed = 0.0;
        motion.stop_moving();
        motion.cannot_change_movement_orders = true;
        self.barge_charge_hits.retain(|&(attacker, _)| attacker != ship_id);
        self.events.push(CombatEvent::BargeChargeStopped { ship_id });
        self.policy_special_measurements.barge_charges_stopped += 1;
        if let Some(delta) = self.ships[index].vitals
            .apply_damage(FINISH_DAMAGE, false)? {
            self.reservations.target_health_changed(
                ship_id, self.ships[index].vitals.health(),
            );
            self.events.push(CombatEvent::UnattributedDamage {
                target_id: ship_id, delta,
            });
            if delta.killed {
                self.retire_dead_ship(index, 0, 0)?;
            }
        }
        Ok(())
    }

    /// Unity calls HitShip for physical trigger enter/stay. A Barge
    /// damages each enemy once per charge and takes .75 of the actually
    /// applied target health damage as reciprocal self-inflicted recoil.
    pub(super) fn resolve_policy_barge_contacts(
        &mut self, collisions: &CollisionMatrix,
    ) -> Result<(), CombatWorldError> {
        // Advance by stable ship ID, not a mutable roster offset.
        // A lethal charge may remove this or another policy controller.
        let mut after_id = 0_i64;
        loop {
            use std::ops::Bound::{Excluded, Unbounded};
            let next = self.policy_ship_runtime
                .range((Excluded(after_id), Unbounded))
                .next().map(|(&ship_id, _)| ship_id);
            let Some(ship_id) = next else { break };
            after_id = ship_id;
            let Some(PolicyShipSpecial::Barge(barge)) =
                self.policy_ship_runtime.get(&ship_id).and_then(|r| r.special)
            else { continue };
            if barge.phase != 2 { continue; }
            let ship_index = self.ship_by_id[&ship_id];
            let side = self.ships[ship_index].side;
            // Reuse the physics step's existing ship spatial grid instead
            // of scanning every combat ship at 50 ticks per second.
            if self.physics.ship_contact_candidates(
                ship_index, &mut self.barge_contact_candidates,
            ) {
                self.policy_special_measurements.barge_ship_grid_rebuilds += 1;
            }
            self.policy_special_measurements.barge_broadphase_candidates +=
                self.barge_contact_candidates.len() as u64;
            for candidate in 0..self.barge_contact_candidates.len() {
                let target_index = self.barge_contact_candidates[candidate];
                if !self.physics.ships()[ship_index].active ||
                    self.ships[ship_index].vitals.is_dead() {
                    break;
                }
                let target_id = self.ships[target_index].entity_id;
                if target_index == ship_index || self.ships[target_index].side == side ||
                    self.ships[target_index].vitals.is_dead() ||
                    !self.physics.ships()[target_index].active ||
                    self.barge_charge_hits.contains(&(ship_id, target_id)) {
                    continue;
                }
                self.policy_special_measurements.barge_contact_checks += 1;
                if !self.physics.ship_hulls_overlap(
                    ship_index, target_index, collisions,
                ) { continue; }
                self.barge_charge_hits.insert((ship_id, target_id));
                self.policy_special_measurements.barge_contact_hits += 1;
                let (enemy_killed, barge_killed) =
                    self.apply_barge_charge_hit(
                        ship_index, target_index, barge.power, None,
                    )?;
                if barge_killed {
                    break;
                }
                if !enemy_killed {
                    self.finish_policy_barge_charge(ship_id)?;
                    break;
                }
            }
        }
        Ok(())
    }

    /// One authoritative physical Barge charge contact transaction for
    /// policy and game commands. Call only after the real authored hull
    /// overlap and one-hit-per-charge lease have been validated. The
    /// caller owns wind-up/cooldown transitions; this method alone owns
    /// target damage, reciprocal recoil, death and combat attribution.
    ///
    /// Returns (enemy_killed, barge_killed). The source keeps charging
    /// through killed enemies and stops on a surviving enemy.
    fn apply_barge_charge_hit(
        &mut self, ship_index: usize, target_index: usize,
        power: i32, command_outcome_id: Option<i64>,
    ) -> Result<(bool, bool), CombatWorldError> {
        // Existing combat events use zero for absence of a server outcome,
        // including neural-policy hits. Local commands earn no server credit.
        let command_outcome_id = command_outcome_id.unwrap_or(0);
        let ship_id = self.ships[ship_index].entity_id;
        let target_id = self.ships[target_index].entity_id;
        let side = self.ships[ship_index].side;
        let target_side = self.ships[target_index].side;
        self.events.push(CombatEvent::BargeChargeHit {
            ship_id, target_id,
        });
        // Unity Ship.LogAttackingDamage respects MakeShotsHarmless for
        // both direct Charge damage and its own reciprocal recoil.
        let enemy_delta = self.ships[target_index].vitals
            .apply_damage(power, self.shots_harmless)?
            .expect("validated living Barge target");
        let recoil_power = ((enemy_delta.applied_damage as f32) * 0.75) as i32;
        let self_delta = self.ships[ship_index].vitals
            .apply_damage(recoil_power, self.shots_harmless)?
            .expect("validated living charging Barge");
        self.reservations.target_health_changed(
            target_id, self.ships[target_index].vitals.health(),
        );
        self.reservations.target_health_changed(
            ship_id, self.ships[ship_index].vitals.health(),
        );
        self.events.push(CombatEvent::Hit(HitEvent::new(
            0, 0, ship_id, target_id, side, target_side,
            command_outcome_id, DamageSource::Charge, enemy_delta, false,
        )));
        self.events.push(CombatEvent::Hit(HitEvent::new(
            0, 0, target_id, ship_id, target_side, side,
            0, DamageSource::Charge, self_delta, true,
        )));
        if enemy_delta.killed {
            self.retire_dead_ship(
                target_index, ship_id, command_outcome_id,
            )?;
        }
        if self_delta.killed {
            // The reciprocal hit is not a second credited command attack.
            self.retire_dead_ship(ship_index, target_id, 0)?;
        }
        Ok((enemy_delta.killed, self_delta.killed))
    }

    /// Source RlCombatPerception.GetSpecialCooldownFraction: remaining
    /// authored time for current phase plus future cooldown, over 13s.
    /// Unlike RlChargePhase this continuously decreases while wind-up,
    /// charge, or cooldown is running.
    pub fn policy_barge_charge_cooldown_fraction(&self, ship_id: i64) -> Option<f32> {
        let PolicyShipSpecial::Barge(barge) =
            self.policy_ship_runtime.get(&ship_id)?.special? else { return None };
        let remaining = match barge.phase {
            0 => 0.0,
            1 => (WINDUP - barge.elapsed).max(0.0) + CHARGING + COOLDOWN,
            2 => (CHARGING - barge.elapsed).max(0.0) + COOLDOWN,
            3 => (COOLDOWN - barge.elapsed).max(0.0),
            _ => COOLDOWN,
        };
        Some((remaining / (WINDUP + CHARGING + COOLDOWN)).clamp(0.0, 1.0))
    }

    /// Snapshot exposed to the RL observation adapter.
    pub fn policy_barge_charge_phase(&self, ship_id: i64) -> Option<f32> {
        let PolicyShipSpecial::Barge(barge) =
            self.policy_ship_runtime.get(&ship_id)?.special? else { return None };
        Some(barge.phase.min(3) as f32 / 3.0)
    }
}
