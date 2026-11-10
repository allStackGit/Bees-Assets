//! CarrierShip.Carrier relationship across authored Carrier deaths.
//!
//! The one CombatWorld owns policy Drone links, while game/policy Striker
//! readiness and parent identity remain in their established ship runtimes.
//! Unity Carrier.Kill reassigns both subclasses before retiring the Carrier.

use super::*;

pub(super) const CARRIER_SHIP_TYPE: u8 = 5;
const DRONE_SHIP_TYPE: u8 = 8;

impl CombatWorld {
    /// Install an authenticated, initially living Drone parent from the
    /// scene's original CarrierShip.Carrier reference. Standalone Drones
    /// have no entry; they remain unparented even if a later Carrier spawns.
    pub fn configure_drone_carrier(
        &mut self, drone_id: i64, carrier_id: i64,
    ) -> Result<(), CombatWorldError> {
        let invalid = || CombatWorldError::InvalidDroneCarrier;
        let &drone_index = self.ship_by_id.get(&drone_id).ok_or_else(invalid)?;
        let &carrier_index = self.ship_by_id.get(&carrier_id).ok_or_else(invalid)?;
        let drone = &self.ships[drone_index];
        let carrier = &self.ships[carrier_index];
        if drone_id == carrier_id ||
            drone.ship_type != DRONE_SHIP_TYPE ||
            carrier.ship_type != CARRIER_SHIP_TYPE ||
            drone.side != carrier.side ||
            drone.vitals.is_dead() || carrier.vitals.is_dead() ||
            !self.physics.ships()[drone_index].active ||
            !self.physics.ships()[carrier_index].active ||
            self.drone_carriers.contains_key(&drone_id)
        {
            return Err(invalid());
        }
        self.drone_carriers.insert(drone_id, Some(carrier_id));
        Ok(())
    }

    /// Some(None) distinguishes a formerly carrier-bound Drone whose
    /// last Carrier died from a source Drone that never had a parent.
    /// Policy observations must not fall back to the original imported
    /// Carrier ID after that death.
    pub fn drone_carrier(&self, drone_id: i64) -> Option<Option<i64>> {
        self.drone_carriers.get(&drone_id).copied()
    }

    /// Unity Carrier.Kill selects the first surviving same-side Carrier
    /// in Level.State.Ships order. All child reassignments happen before
    /// retirement, with no reload-timer or command resets.
    pub(super) fn reassign_carrier_children_after_death(
        &mut self, carrier_index: usize,
    ) {
        if self.ships[carrier_index].ship_type != CARRIER_SHIP_TYPE {
            return;
        }
        let dead_id = self.ships[carrier_index].entity_id;
        let side = self.ships[carrier_index].side;
        // Original ship registration order is retained by CombatWorld.
        let replacement = self.ships.iter().enumerate().find_map(|(index, ship)| {
            (index != carrier_index && ship.ship_type == CARRIER_SHIP_TYPE &&
                ship.side == side && !ship.vitals.is_dead() &&
                self.physics.ships()[index].active
            ).then_some(ship.entity_id)
        });
        for runtime in self.game_strikers.values_mut() {
            if runtime.carrier.carrier_id == Some(dead_id) {
                runtime.carrier.carrier_id = replacement;
            }
        }
        for runtime in self.policy_ship_runtime.values_mut() {
            if let Some(PolicyShipSpecial::Striker(striker)) =
                runtime.special.as_mut()
            {
                if striker.carrier_id == Some(dead_id) {
                    striker.carrier_id = replacement;
                }
            }
        }
        for carrier in self.drone_carriers.values_mut() {
            if *carrier == Some(dead_id) {
                *carrier = replacement;
            }
        }
    }
}
