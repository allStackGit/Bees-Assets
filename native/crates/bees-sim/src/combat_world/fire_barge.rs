//! FireBarge.Detonate and FireBarge.Kill: the RL ship special launches the
//! source-authored explosion and kills its owner in the SAME combat world.
//! The normal type-14 projectile pipeline owns explosion growth, hits,
//! friendly fire and expiry. No second training-only blast model is used.

use super::*;

impl CombatWorld {
    /// Register the actual FireBarge Bomb.Power, never a guessed damage value.
    /// The FleetShip identity must already be bound for future attribution.
    pub fn configure_policy_fire_barge(
        &mut self, ship_id: i64, bomb_power: i32,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].ship_type != 10 ||
            self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active || bomb_power < 0 {
            return Err(CombatWorldError::InvalidPolicyFireBarge);
        }
        if !self.fleet_id_by_ship.get(&ship_id).is_some_and(|&id| id > 0) {
            return Err(CombatWorldError::InvalidFleetIdentity);
        }
        let runtime = self.policy_ship_runtime.get_mut(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if runtime.special.is_some() {
            return Err(CombatWorldError::InvalidPolicyFireBarge);
        }
        runtime.special = Some(PolicyShipSpecial::FireBarge { bomb_power });
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
        let power = self.policy_ship_runtime.get(&ship_id)
            .and_then(|runtime| match runtime.special {
                Some(PolicyShipSpecial::FireBarge { bomb_power }) => Some(bomb_power),
                _ => None,
            })
            .ok_or(CombatWorldError::InvalidPolicyFireBarge)?;
        let shot_id = self.next_entity_id;
        if shot_id == i64::MAX {
            return Err(CombatWorldError::EntityIdExhausted);
        }
        let asset = self.projectile_assets.by_type(14)
            .ok_or(CombatWorldError::InvalidProjectileType(14))?.clone();
        let position = self.physics.ships()[index].motion.position;
        let side = self.ships[index].side;
        // Spawn successfully BEFORE retiring the source ship. Subsequent
        // damage belongs to this immutable shot owner, not a recycled ship.
        self.spawn_projectile(
            &asset, shot_id,
            ShotOwner {
                shooter_id: ship_id, shooter_side: side,
                shooter_is_fire_barge: true, self_detonation: true,
            },
            power, ProjectileMotion::new(position, 0.0, 0.0, 0.0, shot_id, 0),
            0.0,
        )?;
        let remaining_health = self.ships[index].vitals.health();
        let delta = self.ships[index].vitals.apply_damage(remaining_health, false)?
            .expect("validated live Fire Barge");
        self.reservations.target_health_changed(ship_id, 0);
        self.events.push(CombatEvent::FireBargeDetonated {
            ship_id, projectile_id: shot_id,
        });
        self.policy_special_measurements.fire_barge_detonations += 1;
        self.events.push(CombatEvent::UnattributedDamage {
            target_id: ship_id, delta,
        });
        self.retire_dead_ship(index, 0, 0);
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
}
