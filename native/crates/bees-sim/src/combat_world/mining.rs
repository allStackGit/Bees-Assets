//! Source v26 mining from the same physical world as rendered gameplay.
//! Mine health is authoritative here; the Level owns persistent FleetShip
//! resources and must commit each yielded amount before reward processing.

use super::*;

const MINING_FACTORY_TYPE: u8 = 9;
const MINING_CARPENTER_BEE_TYPE: u8 = 4;
const MINING_ACTION_RATE: i32 = 5;
const MINING_COOLDOWN_SECONDS: f32 = 5.0;

#[derive(Clone, Copy, Debug)]
pub(super) struct PolicyMiningRuntime {
    pub(super) cooldown_remaining: f32,
}

impl CombatWorld {
    /// Only Factory and CarpenterBee have a v26 mining action. Fleet
    /// identity must be present so persistent resources can be credited
    /// through the native Level's authoritative fleet-accounting adapter.
    pub fn configure_policy_mining(
        &mut self, ship_id: i64,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let ship = &self.ships[index];
        if !matches!(ship.ship_type, MINING_FACTORY_TYPE | MINING_CARPENTER_BEE_TYPE) ||
            ship.vitals.is_dead() || !self.physics.ships()[index].active ||
            self.ship_fleet_identity(ship_id).is_none() {
            return Err(CombatWorldError::InvalidPolicyMining);
        }
        let runtime = self.policy_ship_runtime.get_mut(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if runtime.mining.is_some() {
            return Err(CombatWorldError::InvalidPolicyMining);
        }
        runtime.mining = Some(PolicyMiningRuntime { cooldown_remaining: 0.0 });
        Ok(())
    }

    /// Source FindTouchingMiningAsteroid: lowest live obstacle ID wins.
    /// Every check uses the actual authored primary collider, not an
    /// approximating sphere or the separate proximity/navigation shapes.
    fn touching_mining_asteroid(&self, ship_id: i64) -> Option<i64> {
        let &ship_index = self.ship_by_id.get(&ship_id)?;
        if self.ships[ship_index].vitals.is_dead() ||
            !self.physics.ships()[ship_index].active { return None; }
        for &obstacle_id in &self.mining_obstacle_ids {
            let Some(&index) = self.obstacle_by_id.get(&obstacle_id) else {
                continue;
            };
            let asteroid = &self.obstacles[index];
            if asteroid.kind != ObstacleKind::MiningAsteroid ||
                !asteroid.vitals.is_some_and(|v| !v.is_dead && v.health > 0) {
                continue;
            }
            if self.physics.ship_obstacle_hulls_touch(ship_index, index) {
                return Some(obstacle_id);
            }
        }
        None
    }

    pub(super) fn policy_mining_ready(&self, ship_id: i64) -> bool {
        self.policy_ship_runtime.get(&ship_id)
            .and_then(|runtime| runtime.mining)
            .is_some_and(|mining| mining.cooldown_remaining <= 0.0) &&
            self.touching_mining_asteroid(ship_id).is_some()
    }

    /// MiningAsteroid.Kill(false) removes both authored gameplay and
    /// physics membership. No CollisionAsteroid breakup or extra RNG is
    /// generated; the Level owns command-finalization and presentation.
    fn retire_mining_asteroid(&mut self, index: usize) {
        let obstacle = &mut self.obstacles[index];
        let obstacle_id = obstacle.entity_id;
        if let Some(vitals) = obstacle.vitals.as_mut() {
            vitals.is_dead = true;
        }
        self.physics.obstacles_mut()[index].active = false;
        self.discovery.retire(DiscoveryKind::Obstacle, obstacle_id);
        self.obstacle_by_id.remove(&obstacle_id);
        if let Ok(at) = self.mining_obstacle_ids.binary_search(&obstacle_id) {
            self.mining_obstacle_ids.remove(at);
        }
        self.free_obstacle_slots.push(index);
        self.events.push(CombatEvent::MiningAsteroidDepleted { obstacle_id });
        self.policy_special_measurements.mining_obstacles_depleted += 1;
    }

    /// A successful policy mining request consumes exactly min(5, Health)
    /// and starts a five-second cooldown. The real fleet value is applied
    /// by NativeLevelRules before the shared episode/economic consumer.
    /// In the absence of fleet metadata the Level fails rather than
    /// silently recording a fake mining reward.
    pub(super) fn apply_pending_policy_mining(
        &mut self,
    ) -> Result<(), CombatWorldError> {
        self.policy_pending_mining.sort_unstable();
        for row in 0..self.policy_pending_mining.len() {
            let ship_id = self.policy_pending_mining[row];
            self.policy_special_measurements.mining_requests += 1;
            if !self.policy_ship_runtime.get(&ship_id)
                .and_then(|runtime| runtime.mining)
                .is_some_and(|mining| mining.cooldown_remaining <= 0.0) {
                continue;
            }
            let Some(obstacle_id) = self.touching_mining_asteroid(ship_id)
            else { continue; };
            let &index = self.obstacle_by_id.get(&obstacle_id)
                .ok_or(CombatWorldError::InvalidPolicyMining)?;
            let health = self.obstacles[index].vitals
                .ok_or(CombatWorldError::InvalidPolicyMining)?.health;
            let amount_mined = MINING_ACTION_RATE.min(health);
            if amount_mined <= 0 { continue; }
            let &ship_index = self.ship_by_id.get(&ship_id)
                .ok_or(CombatWorldError::UnknownShip)?;
            let new_cargo = self.ships[ship_index].vitals.mined_cargo()
                .checked_add(amount_mined)
                .ok_or(CombatWorldError::InvalidPolicyMining)?;
            let depleted = {
                let vitals = self.obstacles[index].vitals.as_mut()
                    .ok_or(CombatWorldError::InvalidPolicyMining)?;
                vitals.health -= amount_mined;
                vitals.health <= 0
            };
            // Source TryApplyMiningAction immediately updates
            // FleetShip.MineralsMinedThisLevel and Ship.Tsv before any
            // subsequent same-tick weapon hits or safe warp. Advance
            // the world-local value now; the Level commits and reconciles
            // the persistent FleetShip value before reward consumption.
            self.ships[ship_index].vitals.set_mined_cargo(new_cargo)?;
            if let Some(mining) = self.policy_ship_runtime.get_mut(&ship_id)
                .and_then(|runtime| runtime.mining.as_mut()) {
                mining.cooldown_remaining = MINING_COOLDOWN_SECONDS;
            }
            let side = self.ship(ship_id)
                .ok_or(CombatWorldError::UnknownShip)?.side;
            self.events.push(CombatEvent::CapabilityMined {
                ship_id, obstacle_id, side, amount_mined,
            });
            self.policy_special_measurements.mining_actions_completed += 1;
            if depleted {
                self.retire_mining_asteroid(index);
            }
        }
        self.policy_pending_mining.clear();
        Ok(())
    }

    /// Commit only the actual post-mining persistent FleetShip value from
    /// the Level. A dead ship stays dead; the economic consumer still uses
    /// the Level's saved fleet value for final exit/forfeiture accounting.
    pub fn sync_mined_fleet_value(
        &mut self, ship_id: i64, mined_total: i32,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if mined_total < 0 {
            return Err(CombatWorldError::InvalidPolicyMining);
        }
        self.ships[index].vitals.set_mined_cargo(mined_total)?;
        Ok(())
    }
}

