//! Source RlOneVsOneAgent.CanApplyHealingActionNow and
//! TryApplyHealingAction. A real Bee ship smaller than the Beehive on
//! BOTH authored size axes must touch the Beehive's separate child
//! HealCollider; each successful action restores <=50 HP and starts
//! a one-second cooldown. No damage, AI, or guessed collider geometry.

use super::*;

const BEEHIVE_TYPE: u8 = 2;
const HEAL_HEALTH: i32 = 50;
const HEAL_COOLDOWN_SECONDS: f32 = 1.0;

#[derive(Clone, Copy, Debug, PartialEq)]
pub(super) struct PolicyHealingRuntime {
    pub(super) cooldown_remaining: f32,
}

impl CombatWorld {
    /// This shape is the real Beehive.HealCollider child trigger, not
    /// its primary hull and not a synthetic radius. The game Level
    /// must import it from the authored prefab before healing enables.
    pub fn configure_beehive_healing_zone(
        &mut self, beehive_id: i64, child_shape: ShipShape,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&beehive_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].ship_type != BEEHIVE_TYPE ||
            self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active {
            return Err(CombatWorldError::InvalidPolicyHealing);
        }
        self.physics.ships_mut()[index]
            .set_healing_zone(child_shape)?;
        // Register only after the real child collider was accepted.
        let side = self.ships[index].side;
        self.register_support_zone(BEEHIVE_TYPE, side, beehive_id);
        Ok(())
    }

    /// The Level supplies ConfigData.ShipSizes for both types and the
    /// real BeeSide. Missing/invalid sizes cannot enable a healing
    /// branch based on a guessed hull or observation value.
    pub fn configure_policy_healing(
        &mut self,
        ship_id: i64,
        bee_side: i32,
        ship_size: [u32; 2],
        beehive_size: [u32; 2],
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if bee_side <= 0 || self.ships[index].side != bee_side ||
            self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active ||
            ship_size.contains(&0) || beehive_size.contains(&0) ||
            ship_size[0] >= beehive_size[0] ||
            ship_size[1] >= beehive_size[1] ||
            self.ship_fleet_identity(ship_id).is_none() {
            return Err(CombatWorldError::InvalidPolicyHealing);
        }
        let runtime = self.policy_ship_runtime.get_mut(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if runtime.healing.is_some() {
            return Err(CombatWorldError::InvalidPolicyHealing);
        }
        runtime.healing = Some(PolicyHealingRuntime {
            cooldown_remaining: 0.0,
        });
        Ok(())
    }

    /// The same-side Beehive index is sorted by runtime ID. The first
    /// authored HealCollider contact matches FindTouchingBeehive's
    /// lowest-ID tie-breaker. No-zone levels avoid a full ship scan.
    fn touching_beehive(&self, ship_id: i64) -> Option<i64> {
        let &target_index = self.ship_by_id.get(&ship_id)?;
        let target = &self.ships[target_index];
        if target.vitals.is_dead() { return None; }
        let target_body = &self.physics.ships()[target_index];
        for &beehive_id in self.support_zone_ids.get(&(BEEHIVE_TYPE, target.side))? {
            let Some(&index) = self.ship_by_id.get(&beehive_id) else {
                continue;
            };
            let beehive = &self.ships[index];
            if beehive.ship_type == BEEHIVE_TYPE &&
                beehive.side == target.side &&
                beehive.entity_id != ship_id &&
                !beehive.vitals.is_dead() &&
                self.physics.ships()[index].healing_zone_touches(target_body) {
                return Some(beehive_id);
            }
        }
        None
    }

    /// Source CanUseHealingAction eligibility, independent of whether
    /// the ship is currently injured or touching an available Beehive.
    /// None means the ship is not bound to a native policy controller.
    pub fn policy_healing_capability(&self, ship_id: i64) -> Option<bool> {
        self.policy_ship_runtime.get(&ship_id)
            .map(|runtime| runtime.healing.is_some())
    }

    pub(super) fn policy_healing_ready(&self, ship_id: i64) -> bool {
        let Some(runtime) = self.policy_ship_runtime.get(&ship_id)
            .and_then(|runtime| runtime.healing) else {
            return false;
        };
        let Some(ship) = self.ship(ship_id) else { return false };
        if ship.vitals.is_dead() ||
            ship.vitals.health() >= ship.vitals.max_health() ||
            runtime.cooldown_remaining > 0.0 {
            return false;
        }
        self.touching_beehive(ship_id).is_some()
    }

    /// Called once during the authoritative fixed step, after
    /// cooldown advancement and before projectile physics. All
    /// requests and event IDs remain in stable ship-ID order.
    pub(super) fn apply_pending_policy_healing(
        &mut self,
    ) -> Result<(), CombatWorldError> {
        self.policy_pending_healing.sort_unstable();
        for index in 0..self.policy_pending_healing.len() {
            let ship_id = self.policy_pending_healing[index];
            self.policy_special_measurements.healing_requests += 1;
            if !self.policy_healing_ready(ship_id) {
                continue;
            }
            let Some(beehive_id) = self.touching_beehive(ship_id) else {
                continue;
            };
            self.policy_special_measurements.healing_touch_checks += 1;
            let &ship_index = self.ship_by_id.get(&ship_id)
                .ok_or(CombatWorldError::UnknownShip)?;
            let prior = self.ships[ship_index].vitals.health();
            let tsv_gain = self.ships[ship_index].vitals
                .heal(HEAL_HEALTH)?;
            let healed_health = self.ships[ship_index].vitals.health() - prior;
            if healed_health <= 0 { continue; }
            if let Some(runtime) = self.policy_ship_runtime.get_mut(&ship_id)
                .and_then(|runtime| runtime.healing.as_mut()) {
                runtime.cooldown_remaining = HEAL_COOLDOWN_SECONDS;
            }
            self.reservations.target_health_changed(
                ship_id, self.ships[ship_index].vitals.health(),
            );
            self.policy_special_measurements.healing_actions_completed += 1;
            self.events.push(CombatEvent::CapabilityHealed {
                ship_id, beehive_id, side: self.ships[ship_index].side,
                healed_health, tsv_gain,
            });
        }
        self.policy_pending_healing.clear();
        Ok(())
    }
}
