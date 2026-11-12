//! FireBarge.Detonate and FireBarge.Kill: the RL ship special launches the
//! source-authored explosion and kills its owner in the SAME combat world.
//! The normal type-14 projectile pipeline owns explosion growth, hits,
//! friendly fire and expiry. No second training-only blast model is used.

use super::*;

impl CombatWorld {
    /// Bind the real authored Bomb.Power for gameplay AND training. A
    /// graphical Fire Barge needs this even when it has no policy controller.
    /// Call after assigning its stable FleetShip identity.
    pub fn configure_fire_barge_bomb(
        &mut self, ship_id: i64, bomb_power: i32,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].ship_type != 10 ||
            self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active ||
            bomb_power < 0 || self.fire_barge_bomb_powers.contains_key(&ship_id) {
            return Err(CombatWorldError::InvalidPolicyFireBarge);
        }
        if !self.fleet_id_by_ship.get(&ship_id).is_some_and(|&id| id != 0) {
            return Err(CombatWorldError::InvalidFleetIdentity);
        }
        self.fire_barge_bomb_powers.insert(ship_id, bomb_power);
        Ok(())
    }

    /// Single authoritative Bomb.Power for the Fire Barge's real type-14
    /// explosion. Command activation must reject a different Level weapon
    /// power rather than reserving one amount and exploding with another.
    pub fn configured_fire_barge_bomb_power(&self, ship_id: i64) -> Option<i32> {
        self.fire_barge_bomb_powers.get(&ship_id).copied()
    }

    /// The policy special uses the same bomb as normal game death handling.
    pub fn configure_policy_fire_barge(
        &mut self, ship_id: i64, bomb_power: i32,
    ) -> Result<(), CombatWorldError> {
        let runtime = self.policy_ship_runtime.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if runtime.special.is_some() {
            return Err(CombatWorldError::InvalidPolicyFireBarge);
        }
        match self.fire_barge_bomb_powers.get(&ship_id) {
            Some(&configured) if configured != bomb_power =>
                return Err(CombatWorldError::InvalidPolicyFireBarge),
            Some(_) => {},
            None => self.configure_fire_barge_bomb(ship_id, bomb_power)?,
        }
        self.policy_ship_runtime.get_mut(&ship_id)
            .expect("validated bound Fire Barge").special =
            Some(PolicyShipSpecial::FireBarge);
        Ok(())
    }

    /// Launch type-14 as a real physical, source-owned explosion. One
    /// successful launch consumes the Bomb.Power registration. A failed
    /// launch leaves it intact, so the Level can diagnose the failure.
    pub(super) fn spawn_fire_barge_explosion(
        &mut self, index: usize, self_detonation: bool,
        command_outcome_id: i64,
    ) -> Result<bool, CombatWorldError> {
        let ship_id = self.ships[index].entity_id;
        let Some(&power) = self.fire_barge_bomb_powers.get(&ship_id) else {
            return Ok(false);
        };
        let shot_id = self.next_entity_id;
        if shot_id == i64::MAX {
            return Err(CombatWorldError::EntityIdExhausted);
        }
        let asset = self.projectile_assets.by_type(14)
            .ok_or(CombatWorldError::InvalidProjectileType(14))?.clone();
        let position = self.physics.ships()[index].motion.position;
        let side = self.ships[index].side;
        self.spawn_projectile(
            &asset, shot_id,
            ShotOwner {
                shooter_id: ship_id, shooter_side: side,
                shooter_is_fire_barge: true, self_detonation,
            },
            power, ProjectileMotion::new(
                position, 0.0, 0.0, 0.0, shot_id, command_outcome_id,
            ),
            0.0,
        )?;
        self.fire_barge_bomb_powers.remove(&ship_id);
        self.events.push(CombatEvent::FireBargeDetonated {
            ship_id, projectile_id: shot_id,
        });
        if self_detonation {
            self.policy_special_measurements.fire_barge_detonations += 1;
        } else {
            self.policy_special_measurements.fire_barge_death_explosions += 1;
        }
        Ok(true)
    }

    /// A gameplay BombingRun timer may request FireBarge.Detonate. The
    /// authoritative ship death must happen inside the NEXT fixed-step
    /// combat transaction, not after the Level has already reconciled the
    /// current tick's economic, roster, reward and terminal events.
    /// Proximity is checked by the owning Level's authored trigger.
    /// The world additionally authenticates the ship's live Bomb target
    /// reservation before admitting the irreversible request.
    pub fn queue_game_fire_barge_detonation(
        &mut self, ship_id: i64, command_outcome_id: Option<i64>,
    ) -> Result<bool, CombatWorldError> {
        if command_outcome_id.is_some_and(|id| id <= 0) {
            return Err(CombatWorldError::InvalidPolicyFireBarge);
        }
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].ship_type != 10 {
            return Err(CombatWorldError::InvalidPolicyFireBarge);
        }
        if self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active {
            return Ok(false);
        }
        let motion = &self.physics.ships()[index].motion;
        if motion.is_policy_controlled && !motion.user_controlled {
            return Err(CombatWorldError::UnsupportedPolicyActions);
        }
        if !self.fire_barge_bomb_powers.contains_key(&ship_id) {
            return Err(CombatWorldError::InvalidPolicyFireBarge);
        }
        let Some(target_id) = self.game_bomb_current_target(ship_id) else {
            return Err(CombatWorldError::InvalidBombTarget);
        };
        let target = self.ship(target_id)
            .ok_or(CombatWorldError::InvalidBombTarget)?;
        if target.vitals.is_dead() ||
            target.side == self.ships[index].side ||
            !self.ship_body(target_id).is_some_and(|body| body.active) {
            return Err(CombatWorldError::InvalidBombTarget);
        }
        if self.pending_game_fire_barges.iter()
            .any(|&(id, _)| id == ship_id) {
            return Ok(false);
        }
        self.pending_game_fire_barges.push((ship_id, command_outcome_id));
        Ok(true)
    }

    /// Source SquadActionBox.Detonate has no BombingRun target requirement.
    /// The player authority adapter validates the exact squad lifetime
    /// before queuing; game specials resolve in the SAME fixed-step death
    /// and explosion transaction as automatic BombingRun detonations.
    pub fn queue_player_fire_barge_detonation(
        &mut self, ship_id: i64,
    ) -> Result<bool, CombatWorldError> {
        if self.frozen_after_elimination { return Ok(false); }
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].ship_type != 10 ||
            self.policy_ship_runtime.contains_key(&ship_id) ||
            !self.fire_barge_bomb_powers.contains_key(&ship_id) {
            return Err(CombatWorldError::InvalidPolicyFireBarge);
        }
        if self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active ||
            self.pending_game_fire_barges.iter().any(|&(id, _)| id == ship_id) {
            return Ok(false);
        }
        self.pending_game_fire_barges.push((ship_id, None));
        Ok(true)
    }

    /// Consume the short gameplay request queue after combat events have
    /// been cleared, before physics, to preserve one reward/retirement pass.
    pub(super) fn apply_pending_game_fire_barges(
        &mut self,
    ) -> Result<(), CombatWorldError> {
        for i in 0..self.pending_game_fire_barges.len() {
            let (ship_id, command_outcome_id) =
                self.pending_game_fire_barges[i];
            let Some(&index) = self.ship_by_id.get(&ship_id) else {
                return Err(CombatWorldError::UnknownShip);
            };
            if self.ships[index].vitals.is_dead() ||
                !self.physics.ships()[index].active {
                continue;
            }
            self.complete_fire_barge_detonation(index, command_outcome_id.unwrap_or(0))?;
        }
        self.pending_game_fire_barges.clear();
        Ok(())
    }

    /// A Fire Barge's special does not require proximity to an enemy. Unity
    /// records a self-detonation, calls Kill, launches type 14 at its actual
    /// center and attributes its own death to no opposing shooter.
    pub(super) fn try_detonate_policy_fire_barge(
        &mut self, ship_id: i64,
    ) -> Result<bool, CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active {
            return Ok(false);
        }
        if !matches!(
            self.policy_ship_runtime.get(&ship_id).and_then(|runtime| runtime.special),
            Some(PolicyShipSpecial::FireBarge)
        ) {
            return Err(CombatWorldError::InvalidPolicyFireBarge);
        }
        self.complete_fire_barge_detonation(index, 0)
    }

    /// Shared one-shot self-detonation transaction for every controller.
    fn complete_fire_barge_detonation(
        &mut self, index: usize, command_outcome_id: i64,
    ) -> Result<bool, CombatWorldError> {
        let ship_id = self.ships[index].entity_id;
        // The bomb registration is consumed at projectile spawn, before
        // retiring the ship, so the death path cannot launch it twice.
        // The projectile snapshots its command identity now; later damage
        // does not depend on whatever command the squad executes next.
        if !self.spawn_fire_barge_explosion(
            index, true, command_outcome_id,
        )? {
            return Err(CombatWorldError::InvalidPolicyFireBarge);
        }
        let remaining_health = self.ships[index].vitals.health();
        let delta = self.ships[index].vitals.apply_damage(remaining_health, false)?
            .expect("validated live Fire Barge");
        self.reservations.target_health_changed(ship_id, 0);
        self.events.push(CombatEvent::UnattributedDamage {
            target_id: ship_id, delta,
        });
        self.retire_dead_ship(index, 0, 0)?;
        Ok(true)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;
    use crate::collision::Aabb;
    use crate::combat::ShipVitals;
    use crate::movement::ShipMotion;
    use crate::policy::Action;
    use crate::ship_assets::ShipAssetCatalog;
    use crate::projectile_assets::ProjectileAssetCatalog;
    use crate::simulation::{World, WorldId};

    #[test]
    fn policy_fire_barge_creates_real_explosion_then_self_kills() {
        let projectiles = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let assets = ShipAssetCatalog::embedded().unwrap();
        let mut state = CombatWorld::new(Aabb {
            min: Vec2::new(-100.0, -100.0),
            max: Vec2::new(100.0, 100.0),
        }, 8.0, projectiles).unwrap();
        for (id, kind, side, position) in [
            (1, 10, 1, Vec2::ZERO),
            (2, 21, 2, Vec2::new(12.0, 0.0)),
        ] {
            state.spawn_ship(
                assets.by_type(kind).unwrap(), id, side,
                ShipVitals::new(100, 100, 1000, 0).unwrap(),
                ShipMotion::new(position, 0.0, 0.0, 0.0, 3.0),
            ).unwrap();
        }
        state.set_ship_fleet_identity(1, 401).unwrap();
        state.bind_policy_ship(1, PolicyFrame::new(0), false).unwrap();
        assert!(!state.policy_action_mask(1).unwrap().special[1]);
        state.configure_policy_fire_barge(1, 35).unwrap();
        assert!(state.policy_action_mask(1).unwrap().special[1]);
        assert_eq!(
            state.configure_policy_fire_barge(1, 35),
            Err(CombatWorldError::InvalidPolicyFireBarge),
        );

        // A real authored collision matrix is required by production; this
        // fixture opens precisely the imported explosion-to-Wasp pair.
        let explosion_layer = state.projectile_assets.by_type(14).unwrap().collision_layer;
        let target_layer = state.physics.ships()[1].layer;
        let mut rows = [0u32; 32];
        rows[usize::from(explosion_layer)] |= 1u32 << target_layer;
        rows[usize::from(target_layer)] |= 1u32 << explosion_layer;
        let rules = CombatWorldRules { collision_matrix: CollisionMatrix::from_rows(rows) };
        let mut world = World::new(WorldId(17), 1234, state);
        let mut action = Action::ZERO;
        action.discrete[5] = 1;
        let decision = Decision { agent_id: 1, action };
        rules.step(&mut world, &[decision], 0.02).unwrap();
        assert_eq!(world.state.ship(1).unwrap().vitals.health(), 0);
        assert!(!world.state.physics.ships()[0].active);
        assert!(world.state.events().iter().any(|event| matches!(
            event, CombatEvent::UnattributedDamage { target_id: 1, .. }
        )));
        assert!(world.state.events().iter().any(|event| matches!(
            event, CombatEvent::ShipDied { victim_id: 1, killer_id: 0, .. }
        )));
        let shot = world.state.events().iter().find_map(|event| match event {
            CombatEvent::FireBargeDetonated { ship_id: 1, projectile_id } =>
                Some(*projectile_id),
            _ => None,
        }).expect("authoritative explosion event");
        let shot_index = *world.state.projectile_by_id.get(&shot).unwrap();
        assert_eq!(world.state.projectiles()[shot_index].projectile_type, 14);
        assert!(world.state.projectiles()[shot_index].owner.self_detonation);
        assert_eq!(world.state.projectiles()[shot_index].power, 35);
        let counts = world.state.policy_special_measurements();
        assert_eq!(counts.requests, 1);
        assert_eq!(counts.fire_barge_detonations, 1);
        assert_eq!(counts.striker_bombs_dropped, 0);

        // Authored animation changes the same collider used by physics.
        // Its normal contacts, not the special handler, damage the enemy.
        let mut saw_hit = false;
        for _ in 0..120 {
            rules.step(&mut world, &[], 0.02).unwrap();
            saw_hit |= world.state.events().iter().any(|event| matches!(
                event, CombatEvent::Hit(hit)
                    if hit.projectile_id == shot &&
                       hit.target_id == 2 &&
                       hit.attacker_id == 1 &&
                       hit.source == DamageSource::Explosion
            ));
        }
        assert!(saw_hit);
        assert_eq!(world.state.ship(2).unwrap().vitals.health(), 65);
    }


    #[test]
    fn non_policy_fire_barge_hit_by_enemy_launches_authored_death_explosion() {
        let projectiles = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let hulls = ShipAssetCatalog::embedded().unwrap();
        let mut world = CombatWorld::new(Aabb {
            min: Vec2::new(-100.0, -100.0),
            max: Vec2::new(100.0, 100.0),
        }, 8.0, projectiles.clone()).unwrap();
        // Ship 1 has no policy controller. Its death must still execute
        // the same FireBarge.Kill mechanics as a policy-controlled unit.
        for (id, kind, side, pos, hp) in [
            (1, 10, 1, Vec2::ZERO, 20),
            (2, 21, 2, Vec2::new(20.0, 0.0), 100),
            (3, 21, 1, Vec2::new(12.0, 0.0), 100),
        ] {
            world.spawn_ship(
                hulls.by_type(kind).unwrap(), id, side,
                ShipVitals::new(hp, 100, 1000, 0).unwrap(),
                ShipMotion::new(pos, 0.0, 0.0, 0.0, 3.0),
            ).unwrap();
        }
        world.set_ship_fleet_identity(1, 1001).unwrap();
        world.configure_fire_barge_bomb(1, 40).unwrap();

        let hit_asset = projectiles.by_type(2).unwrap();
        world.spawn_projectile(
            hit_asset, 20,
            ShotOwner {
                shooter_id: 2, shooter_side: 2,
                shooter_is_fire_barge: false, self_detonation: false,
            },
            25, ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 50.0, 20, 9),
            0.0,
        ).unwrap();
        let explosion_asset = projectiles.by_type(14).unwrap();
        let mut rows = [0u32; 32];
        rows[usize::from(hit_asset.collision_layer)] |=
            1u32 << world.physics.ships()[0].layer;
        rows[usize::from(explosion_asset.collision_layer)] |=
            1u32 << world.physics.ships()[1].layer;
        let matrix = CollisionMatrix::from_rows(rows);

        // First tick generates the contact, next tick resolves the hit.
        world.step_fixed(0.02, &matrix).unwrap();
        world.step_fixed(0.02, &matrix).unwrap();
        assert_eq!(world.ship(1).unwrap().vitals.health(), 0);
        let shot = world.events().iter().find_map(|event| match event {
            CombatEvent::FireBargeDetonated { ship_id: 1, projectile_id } =>
                Some(*projectile_id),
            _ => None,
        }).expect("ordinary Fire Barge death creates a real explosion");
        assert!(world.events().iter().any(|event| matches!(
            event, CombatEvent::ShipDied {
                victim_id: 1, killer_id: 2, command_outcome_id: 9, ..
            }
        )));
        let index = *world.projectile_by_id.get(&shot).unwrap();
        assert_eq!(world.projectiles()[index].projectile_type, 14);
        assert!(!world.projectiles()[index].owner.self_detonation);
        assert_eq!(world.policy_special_measurements().fire_barge_detonations, 0);
        assert_eq!(world.policy_special_measurements().fire_barge_death_explosions, 1);

        let mut enemy_hit = false;
        let mut friendly_hit = false;
        for _ in 0..110 {
            world.step_fixed(0.02, &matrix).unwrap();
            for event in world.events() {
                if let CombatEvent::Hit(hit) = event {
                    if hit.projectile_id == shot &&
                        hit.source == DamageSource::DeathExplosion {
                        enemy_hit |= hit.target_id == 2;
                        friendly_hit |= hit.target_id == 3;
                    }
                }
            }
        }
        assert!(enemy_hit && friendly_hit);
        assert_eq!(world.ship(2).unwrap().vitals.health(), 60);
        assert_eq!(world.ship(3).unwrap().vitals.health(), 60);
        assert_eq!(world.policy_special_measurements().fire_barge_death_explosions, 1);
    }
}
