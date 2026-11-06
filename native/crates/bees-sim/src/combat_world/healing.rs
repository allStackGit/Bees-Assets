//! Source RlOneVsOneAgent.CanApplyHealingActionNow and
//! TryApplyHealingAction. A real Bee ship smaller than the Beehive on
//! BOTH authored size axes must touch the Beehive's separate child
//! HealCollider; each successful action restores <=50 HP and starts
//! a one-second cooldown. No damage, AI, or guessed collider geometry.

use super::*;

const BEEHIVE_TYPE: u8 = 2;
const BEEHIVE_COMMAND_HEALING_SLOTS: u8 = 4;
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

    /// Source Beehive.ShipsHealingHere: a shared four-slot budget for
    /// all timed Heal commands, independent of policy HealCollider actions.
    /// This authoritative world-owned map prevents two squads from
    /// reserving the same Beehive slots independently. Repeated acquisition
    /// by the same ship/hive is idempotent, but a ship cannot hold two slots.
    pub fn try_reserve_beehive_healing_slot(
        &mut self, ship_id: i64, beehive_id: i64,
    ) -> Result<bool, CombatWorldError> {
        let &ship_index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let &hive_index = self.ship_by_id.get(&beehive_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let ship = &self.ships[ship_index];
        let hive = &self.ships[hive_index];
        if ship_id == beehive_id || ship.side != hive.side ||
            hive.ship_type != BEEHIVE_TYPE ||
            ship.vitals.is_dead() || hive.vitals.is_dead() ||
            !self.physics.ships()[ship_index].active ||
            !self.physics.ships()[hive_index].active ||
            ship.vitals.health() >= ship.vitals.max_health() ||
            !self.support_zone_ids
                .get(&(BEEHIVE_TYPE, ship.side))
                .is_some_and(|ids| ids.binary_search(&beehive_id).is_ok()) {
            return Err(CombatWorldError::InvalidPolicyHealing);
        }
        if let Some(&existing) = self.healing_hive_by_ship.get(&ship_id) {
            return Ok(existing == beehive_id);
        }
        let used = self.healing_slots_used_by_hive
            .entry(beehive_id).or_insert(0);
        if *used >= BEEHIVE_COMMAND_HEALING_SLOTS {
            return Ok(false);
        }
        *used += 1;
        self.healing_hive_by_ship.insert(ship_id, beehive_id);
        Ok(true)
    }

    /// Release on healing completion, cancellation, supersession, or death.
    /// No scans of unrelated squads are needed for an ordinary release.
    pub fn release_beehive_healing_slot(&mut self, ship_id: i64) -> bool {
        let Some(hive_id) = self.healing_hive_by_ship.remove(&ship_id)
        else { return false; };
        if let Some(used) = self.healing_slots_used_by_hive.get_mut(&hive_id) {
            debug_assert!(*used > 0);
            *used = used.saturating_sub(1);
            if *used == 0 {
                self.healing_slots_used_by_hive.remove(&hive_id);
            }
        }
        true
    }

    /// A dead Beehive loses all its reservations in one rare O(reserved)
    /// pass. Ordinary fixed ticks never traverse the reservation registry.
    pub(super) fn release_beehive_healing_slots_for_hive(
        &mut self, beehive_id: i64,
    ) {
        self.healing_hive_by_ship.retain(|_, hive_id|
            *hive_id != beehive_id,
        );
        self.healing_slots_used_by_hive.remove(&beehive_id);
    }

    pub fn beehive_healing_slots_available(&self, beehive_id: i64) -> u8 {
        let Some(hive) = self.ship(beehive_id) else { return 0 };
        if hive.ship_type != BEEHIVE_TYPE || hive.vitals.is_dead() {
            return 0;
        }
        let registered = self.support_zone_ids
            .get(&(BEEHIVE_TYPE, hive.side))
            .is_some_and(|ids| ids.binary_search(&beehive_id).is_ok());
        if !registered { return 0; }
        BEEHIVE_COMMAND_HEALING_SLOTS.saturating_sub(
            self.healing_slots_used_by_hive
                .get(&beehive_id).copied().unwrap_or(0),
        )
    }

    /// Source Squad command eligibility bans Heal when every live
    /// same-side Beehive has all four shared slots occupied. This uses
    /// the authored-zone index rather than scanning every world ship.
    pub fn has_available_beehive_healing_slot(&self, side: i32) -> bool {
        self.live_beehive_ids_for_side(side)
            .any(|id| self.beehive_healing_slots_available(id) > 0)
    }

    pub fn reserved_healing_beehive(&self, ship_id: i64) -> Option<i64> {
        self.healing_hive_by_ship.get(&ship_id).copied()
    }

    /// Shared Beehive healing transaction for policy decisions and
    /// gameplay Heal command timers. Contact and one-second cooldown
    /// belong to their respective callers; the real health, TSV, damage
    /// reservations and event are authored in this one physical world.
    /// A timed command may keep healing an enrolled ship even when its
    /// collider drifts outside the trigger, matching Unity HealShips.
    fn heal_ship_at_beehive(
        &mut self, ship_id: i64, beehive_id: i64,
    ) -> Result<Option<i32>, CombatWorldError> {
        let &ship_index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let &hive_index = self.ship_by_id.get(&beehive_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let ship = &self.ships[ship_index];
        let hive = &self.ships[hive_index];
        if ship_id == beehive_id ||
            ship.side != hive.side || hive.ship_type != BEEHIVE_TYPE ||
            ship.vitals.is_dead() || hive.vitals.is_dead() ||
            !self.physics.ships()[ship_index].active ||
            !self.physics.ships()[hive_index].active {
            return Err(CombatWorldError::InvalidPolicyHealing);
        }
        if ship.vitals.health() >= ship.vitals.max_health() {
            return Ok(None);
        }
        let before = self.ships[ship_index].vitals.health();
        let tsv_gain = self.ships[ship_index].vitals.heal(HEAL_HEALTH)?;
        let healed_health = self.ships[ship_index].vitals.health() - before;
        if healed_health <= 0 { return Ok(None); }
        self.reservations.target_health_changed(
            ship_id, self.ships[ship_index].vitals.health(),
        );
        self.events.push(CombatEvent::CapabilityHealed {
            ship_id, beehive_id, side: self.ships[ship_index].side,
            healed_health, tsv_gain,
        });
        Ok(Some(tsv_gain))
    }

    /// Unity HealShips continues healing after first collider entry
    /// while a Beehive reservation is held. Unlike the private policy
    /// transaction, the game command entry must prove ownership of one of
    /// that Beehive's four globally shared slots.
    pub fn heal_reserved_command_ship(
        &mut self, ship_id: i64, beehive_id: i64,
    ) -> Result<Option<i32>, CombatWorldError> {
        if self.reserved_healing_beehive(ship_id) != Some(beehive_id) {
            return Err(CombatWorldError::InvalidPolicyHealing);
        }
        self.heal_ship_at_beehive(ship_id, beehive_id)
    }

    /// The public command interface queries only configured, live
    /// same-side Beehives. Returning a sorted borrowed slice avoids a
    /// whole-ship scan or allocations on three-second movement polls.
    pub fn live_beehive_ids_for_side(&self, side: i32) -> impl Iterator<Item = i64> + '_ {
        self.support_zone_ids.get(&(BEEHIVE_TYPE, side))
            .into_iter().flat_map(|ids| ids.iter().copied())
            .filter(move |id| self.ship(*id).is_some_and(|ship|
                !ship.vitals.is_dead() && ship.ship_type == BEEHIVE_TYPE
            ))
    }

    /// Source Beehive.OnTriggerEnter2D requires the separate authored
    /// HealCollider, not the Beehive ship hull or a guessed radius.
    pub fn ship_touches_beehive_healing_zone(
        &self, ship_id: i64, beehive_id: i64,
    ) -> bool {
        let Some(&ship_index) = self.ship_by_id.get(&ship_id) else {
            return false;
        };
        let Some(&hive_index) = self.ship_by_id.get(&beehive_id) else {
            return false;
        };
        let ship = &self.ships[ship_index];
        let hive = &self.ships[hive_index];
        hive.ship_type == BEEHIVE_TYPE && ship.side == hive.side &&
            ship_id != beehive_id && !ship.vitals.is_dead() &&
            !hive.vitals.is_dead() &&
            self.physics.ships()[hive_index].healing_zone_touches(
                &self.physics.ships()[ship_index],
            )
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
            if self.heal_ship_at_beehive(ship_id, beehive_id)?.is_none() {
                continue;
            }
            if let Some(runtime) = self.policy_ship_runtime.get_mut(&ship_id)
                .and_then(|runtime| runtime.healing.as_mut()) {
                runtime.cooldown_remaining = HEAL_COOLDOWN_SECONDS;
            }
            self.policy_special_measurements.healing_actions_completed += 1;
        }
        self.policy_pending_healing.clear();
        Ok(())
    }
}
