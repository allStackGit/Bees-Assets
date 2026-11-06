//! Source v26 mining from the same physical world as rendered gameplay.
//! Mine health is authoritative here; the Level owns persistent FleetShip
//! resources and must commit each yielded amount before reward processing.

use super::*;

/// Unity ConfigData.ShipTypes (authored enum IDs).
pub const MINING_FACTORY_TYPE: u8 = 9;
pub const MINING_CARPENTER_BEE_TYPE: u8 = 4;

#[inline]
pub fn is_mining_ship_type(ship_type: u8) -> bool {
    matches!(ship_type, MINING_FACTORY_TYPE | MINING_CARPENTER_BEE_TYPE)
}
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
        if !is_mining_ship_type(ship.ship_type) ||
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

    /// Source Squad.GetNearestMiningAsteroid uses the live squad bounds
    /// center and the Level's living mining-asteroid registry. Stable
    /// increasing entity IDs preserve registry order for exact-distance ties.
    /// Return the selected physical obstacle's current position as well.
    pub fn nearest_mining_asteroid(&self, squad_center: Vec2) -> Option<(i64, Vec2)> {
        if !squad_center.is_finite() { return None; }
        let mut nearest = None;
        let mut distance_squared = f32::INFINITY;
        for &id in &self.mining_obstacle_ids {
            let Some(&index) = self.obstacle_by_id.get(&id) else {
                continue;
            };
            if !self.physics.obstacles()[index].active ||
                self.obstacles[index].kind != ObstacleKind::MiningAsteroid ||
                !self.obstacles[index].live() {
                continue;
            }
            let position = self.physics.obstacles()[index].pose.position;
            let distance = (position - squad_center).length_squared();
            if nearest.is_none() || distance < distance_squared {
                nearest = Some((id, position));
                distance_squared = distance;
            }
        }
        nearest
    }

    /// Current live mining target geometry. Moving asteroids may drift
    /// between command polls; the target is always followed at its current
    /// world position, but the random destination offset stays fixed.
    pub fn mining_asteroid_position(&self, id: i64) -> Option<Vec2> {
        let &index = self.obstacle_by_id.get(&id)?;
        let obstacle = &self.obstacles[index];
        if obstacle.kind != ObstacleKind::MiningAsteroid ||
            !obstacle.vitals.is_some_and(|v| !v.is_dead && v.health > 0) ||
            !self.physics.obstacles()[index].active {
            return None;
        }
        Some(self.physics.obstacles()[index].pose.position)
    }

    /// Source Mining.Execute draws a random point within a disk whose
    /// radius is 30% of the smaller world collider AABB half-extent,
    /// clamped to 3..12. No heuristic shape replacement or extra scan.
    pub fn mining_asteroid_spread_radius(&self, id: i64) -> Option<f32> {
        let &index = self.obstacle_by_id.get(&id)?;
        if self.mining_asteroid_position(id).is_none() { return None; }
        let bounds = self.physics.obstacles()[index].primary_bounds()?;
        let half_x = (bounds.max.x - bounds.min.x) * 0.5;
        let half_y = (bounds.max.y - bounds.min.y) * 0.5;
        Some((half_x.min(half_y) * 0.3).clamp(3.0, 12.0))
    }

    /// Exact primary-collider contact for a source Mining command's
    /// FoundAsteroid membership. This is a pure, indexed read: it does
    /// not trigger an extraction or create an independent contact cache.
    pub fn ship_touches_mining_asteroid(
        &self, ship_id: i64, obstacle_id: i64,
    ) -> bool {
        let Some(&ship_index) = self.ship_by_id.get(&ship_id) else {
            return false;
        };
        let Some(&obstacle_index) = self.obstacle_by_id.get(&obstacle_id) else {
            return false;
        };
        self.obstacles[obstacle_index].kind == ObstacleKind::MiningAsteroid &&
            self.obstacles[obstacle_index].live() &&
            self.physics.ship_obstacle_hulls_touch(ship_index, obstacle_index)
    }

    /// Shared world-authoritative extraction for policy actions and the
    /// source timed Mining squad command. The caller supplies its distinct
    /// live miners in source squad-registration order. For commands these
    /// are ships that previously entered the target's authored collider;
    /// Unity continues their five-second timer without retesting contact.
    ///
    /// A single rate of five minerals per miner is capped by live asteroid
    /// health. Integer division distributes the entire yield, with earlier
    /// registered ships receiving the remainder, exactly like Mining.Mine.
    /// Preflight all recipients before modifying any physical resource.
    /// FleetShip persistence is committed by the native Level event handoff.
    pub fn mine_asteroid_for_ships(
        &mut self, obstacle_id: i64, ship_ids: &[i64],
    ) -> Result<i32, CombatWorldError> {
        let &index = self.obstacle_by_id.get(&obstacle_id)
            .ok_or(CombatWorldError::UnknownObstacle)?;
        if !self.physics.obstacles()[index].active ||
            self.obstacles[index].kind != ObstacleKind::MiningAsteroid {
            return Err(CombatWorldError::InvalidPolicyMining);
        }
        let health = self.obstacles[index].vitals
            .filter(|v| !v.is_dead && v.health > 0)
            .ok_or(CombatWorldError::InvalidPolicyMining)?.health;
        let count = i32::try_from(ship_ids.len())
            .map_err(|_| CombatWorldError::InvalidPolicyMining)?;
        let rate = count.checked_mul(MINING_ACTION_RATE)
            .filter(|&value| value > 0)
            .ok_or(CombatWorldError::InvalidPolicyMining)?;
        let amount = rate.min(health);
        let base = amount / count;
        let remainder = amount % count;
        let mut side = None;
        for (i, &ship_id) in ship_ids.iter().enumerate() {
            if ship_ids[..i].contains(&ship_id) {
                return Err(CombatWorldError::InvalidPolicyMining);
            }
            let &ship_index = self.ship_by_id.get(&ship_id)
                .ok_or(CombatWorldError::UnknownShip)?;
            let ship = &self.ships[ship_index];
            if !is_mining_ship_type(ship.ship_type) ||
                ship.vitals.is_dead() || !self.physics.ships()[ship_index].active ||
                self.ship_fleet_identity(ship_id).is_none() ||
                side.is_some_and(|same_side| same_side != ship.side) {
                return Err(CombatWorldError::InvalidPolicyMining);
            }
            side = Some(ship.side);
            let share = base + i32::from(i < remainder as usize);
            ship.vitals.mined_cargo().checked_add(share)
                .ok_or(CombatWorldError::InvalidPolicyMining)?;
        }

        // Source Mining.Mine deducts the whole interval once, regardless
        // of how many ships each earn their integer share.
        self.obstacles[index].vitals.as_mut()
            .expect("prevalidated live mining asteroid").health -= amount;
        for (i, &ship_id) in ship_ids.iter().enumerate() {
            let share = base + i32::from(i < remainder as usize);
            if share == 0 { continue; }
            let ship_index = self.ship_by_id[&ship_id];
            let ship = &mut self.ships[ship_index];
            let new_cargo = ship.vitals.mined_cargo() + share;
            ship.vitals.set_mined_cargo(new_cargo)?;
            self.events.push(CombatEvent::CapabilityMined {
                ship_id, obstacle_id, side: ship.side, amount_mined: share,
            });
        }
        if self.obstacles[index].vitals
            .expect("prevalidated live mining asteroid").health == 0 {
            self.retire_mining_asteroid(index);
        }
        Ok(amount)
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
            let mined = self.mine_asteroid_for_ships(
                obstacle_id, std::slice::from_ref(&ship_id),
            )?;
            if mined == 0 { continue; }
            if let Some(mining) = self.policy_ship_runtime.get_mut(&ship_id)
                .and_then(|runtime| runtime.mining.as_mut()) {
                mining.cooldown_remaining = MINING_COOLDOWN_SECONDS;
            }
            self.policy_special_measurements.mining_actions_completed += 1;
        }
        self.policy_pending_mining.clear();
        Ok(())
    }

    /// Synchronize externally changed persistent FleetShip minerals
    /// before starting a fixed tick. During native policy or timed command
    /// mining, CombatWorld already updates TSV and NativeLevelRules
    /// requires the Level's credited value to match exactly. Do not use
    /// this as a second, post-damage mining credit path.
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

