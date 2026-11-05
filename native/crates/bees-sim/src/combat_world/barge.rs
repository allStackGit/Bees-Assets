//! Source Barge.ChargeForward, HitShip and StopCharge for RL control.
//! Phases, health, movement and collision all live in the normal combat world.

use super::*;
use crate::movement::STOP_MOVEMENT;

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

impl CombatWorld {
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
            self.physics.ship_contact_candidates(
                ship_index, &mut self.barge_contact_candidates,
            );
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
                self.events.push(CombatEvent::BargeChargeHit {
                    ship_id, target_id,
                });
                // Unity Ship.LogAttackingDamage respects MakeShotsHarmless
                // even for Barge charge contact and reciprocal recoil.
                let enemy_delta = self.ships[target_index].vitals
                    .apply_damage(barge.power, self.shots_harmless)?
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
                    0, 0, ship_id, target_id, side, self.ships[target_index].side,
                    0, DamageSource::Charge, enemy_delta, false,
                )));
                self.events.push(CombatEvent::Hit(HitEvent::new(
                    0, 0, target_id, ship_id, self.ships[target_index].side, side,
                    0, DamageSource::Charge, self_delta, true,
                )));
                if enemy_delta.killed {
                    self.retire_dead_ship(target_index, ship_id, 0)?;
                }
                if self_delta.killed {
                    self.retire_dead_ship(ship_index, target_id, 0)?;
                    break;
                }
                if !enemy_delta.killed {
                    self.finish_policy_barge_charge(ship_id)?;
                    break;
                }
            }
        }
        Ok(())
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
