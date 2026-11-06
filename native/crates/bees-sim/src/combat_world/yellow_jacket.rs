//! YellowJacket contact detonation shared by policy and game commands.
//! Source: Scripts/Entities/Ships/YellowJacket.cs.
//! These are direct simultaneous health hits, not physical projectiles.

use super::*;

impl CombatWorld {
    /// Bind actual Bomb.Power for ShipTypes.YellowJacket=22.
    pub fn configure_policy_yellow_jacket(
        &mut self, ship_id: i64, bomb_power: i32,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].ship_type != 22 ||
            self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active || bomb_power < 0 {
            return Err(CombatWorldError::InvalidPolicyYellowJacket);
        }
        let runtime = self.policy_ship_runtime.get_mut(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if runtime.special.is_some() {
            return Err(CombatWorldError::InvalidPolicyYellowJacket);
        }
        runtime.special = Some(PolicyShipSpecial::YellowJacket { bomb_power });
        Ok(())
    }

    /// V26's explicit special uses Unity's live TouchingShip enemy guard.
    /// Separately commanded automatic BombingRun remains Level work.
    pub(super) fn try_detonate_policy_yellow_jacket(
        &mut self, ship_id: i64, collisions: &CollisionMatrix,
    ) -> Result<bool, CombatWorldError> {
        let &ship_index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[ship_index].vitals.is_dead() ||
            !self.physics.ships()[ship_index].active {
            return Ok(false);
        }
        let power = self.policy_ship_runtime.get(&ship_id)
            .and_then(|runtime| match runtime.special {
                Some(PolicyShipSpecial::YellowJacket { bomb_power }) => Some(bomb_power),
                _ => None,
            })
            .ok_or(CombatWorldError::InvalidPolicyYellowJacket)?;
        let contact = self.find_touching_enemy_ship(ship_index, collisions);
        self.policy_special_measurements.yellow_jacket_hull_checks +=
            contact.candidates;
        self.policy_special_measurements.yellow_jacket_distance_rejections +=
            contact.distance_rejections;
        self.policy_special_measurements.yellow_jacket_exact_overlap_tests +=
            contact.exact_tests;
        let Some(target_id) = contact.target_id else {
            self.policy_special_measurements.yellow_jacket_contact_misses += 1;
            return Ok(false);
        };
        let target_index = *self.ship_by_id.get(&target_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let detonated = self.resolve_yellow_jacket_detonation(
            ship_index, target_index, power, 0,
        )?;
        self.policy_special_measurements.yellow_jacket_detonations +=
            u64::from(detonated);
        Ok(detonated)
    }

    /// Source YellowJacket.OnTriggerEnter2D, for an active game BombingRun.
    /// The Level supplies the real currently commanded contact and outcome
    /// identity; the world authenticates its selected Bomb.TargetShip and
    /// checks authored hulls before applying the shared reciprocal hits.
    /// The resulting events belong to the current combat tick and must be
    /// reconciled by the owning Level before clearing them.
    pub fn detonate_game_yellow_jacket_on_contact(
        &mut self, ship_id: i64, contacted_target_id: i64,
        command_outcome_id: i64, collisions: &CollisionMatrix,
    ) -> Result<bool, CombatWorldError> {
        if command_outcome_id <= 0 {
            return Err(CombatWorldError::InvalidBombTarget);
        }
        let &ship_index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[ship_index].ship_type != 22 {
            return Err(CombatWorldError::InvalidBombTarget);
        }
        let body = &self.physics.ships()[ship_index];
        if body.motion.is_policy_controlled && !body.motion.user_controlled {
            return Err(CombatWorldError::UnsupportedPolicyActions);
        }
        if self.frozen_after_elimination ||
            self.ships[ship_index].vitals.is_dead() || !body.active {
            return Ok(false);
        }
        let Some(&target_index) = self.ship_by_id.get(&contacted_target_id)
        else { return Ok(false) };
        if self.ships[target_index].vitals.is_dead() ||
            !self.physics.ships()[target_index].active ||
            self.ships[target_index].side == self.ships[ship_index].side {
            return Ok(false);
        }
        let Some(&weapon_id) = self.game_bomb_weapon_by_ship.get(&ship_id)
        else { return Ok(false) };
        let Some(held) = self.reservations.held_by(weapon_id)
        else { return Ok(false) };
        if held.target_id != contacted_target_id ||
            held.side != self.ships[ship_index].side {
            return Ok(false);
        }
        self.game_bomb_measurements.yellow_jacket_contact_checks += 1;
        if !self.physics.ship_hulls_overlap(
            ship_index, target_index, collisions,
        ) {
            self.game_bomb_measurements.yellow_jacket_contact_misses += 1;
            return Ok(false);
        }
        // Unity releases Bomb.TargetShip before synchronously applying
        // both hits. No projectile or independent damage ledger is created.
        self.reservations.release(weapon_id);
        let detonated = self.resolve_yellow_jacket_detonation(
            ship_index, target_index, held.amount, command_outcome_id,
        )?;
        self.game_bomb_measurements.yellow_jacket_detonations +=
            u64::from(detonated);
        Ok(detonated)
    }

    /// Both controller types use the same physical simultaneous-damage
    /// transaction, death path, combat events and TSV accounting.
    fn resolve_yellow_jacket_detonation(
        &mut self, ship_index: usize, target_index: usize,
        power: i32, command_outcome_id: i64,
    ) -> Result<bool, CombatWorldError> {
        let ship_id = self.ships[ship_index].entity_id;
        let target_id = self.ships[target_index].entity_id;
        let ship_side = self.ships[ship_index].side;
        let target_side = self.ships[target_index].side;

        // Source computes BOTH hit deltas before calling either Ship.Kill.
        // ShotsHarmless is a projectile flag and must not mask direct damage.
        let enemy_delta = self.ships[target_index].vitals
            .apply_damage(power, false)?.expect("contacted living enemy");
        let self_delta = self.ships[ship_index].vitals
            .apply_damage(power, false)?.expect("living Yellow Jacket");
        self.reservations.target_health_changed(
            target_id, self.ships[target_index].vitals.health(),
        );
        self.reservations.target_health_changed(
            ship_id, self.ships[ship_index].vitals.health(),
        );
        self.events.push(CombatEvent::YellowJacketDetonated {
            ship_id, target_id,
        });
        // Policy contacts carry no command ID. Game contacts retain the
        // originating BombingRun for outgoing damage. Reciprocal damage is
        // physically real but does not invent a command for the opponent.
        self.events.push(CombatEvent::Hit(HitEvent::new(
            0, 0, ship_id, target_id, ship_side, target_side,
            command_outcome_id, DamageSource::Bomb, enemy_delta, false,
        )));
        // Unity's historical LogHitStats credits the contacted ship, but
        // RL RecordUnattributedTsvLoss suppresses its attacker reward.
        self.events.push(CombatEvent::Hit(HitEvent::new(
            0, 0, target_id, ship_id, target_side, ship_side,
            0, DamageSource::Bomb, self_delta, true,
        )));
        if enemy_delta.killed {
            self.retire_dead_ship(target_index, ship_id, 0)?;
        }
        // Source YellowJacket.Detonate always calls Kill after both
        // direct hits. It is NOT conditional on Bomb.Power exceeding
        // its own remaining health. No third attributed hit is generated.
        if !self_delta.killed {
            self.ships[ship_index].vitals.kill_without_hit();
            self.reservations.target_health_changed(ship_id, 0);
        }
        // YellowJacket.HasCompletedRun marks this as self-detonation
        // for RL death attribution despite the historical killer ID.
        self.retire_dead_ship(ship_index, target_id, 0)?;
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
    use crate::projectile_assets::ProjectileAssetCatalog;
    use crate::ship_assets::ShipAssetCatalog;
    use crate::simulation::{World, WorldId};

    #[test]
    fn contact_special_applies_both_direct_bomb_hits_without_a_projectile() {
        let projectiles = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let assets = ShipAssetCatalog::embedded().unwrap();
        let mut state = CombatWorld::new(Aabb {
            min: Vec2::new(-100.0, -100.0),
            max: Vec2::new(100.0, 100.0),
        }, 8.0, projectiles).unwrap();
        for (id, kind, side, position) in [
            (1, 22, 1, Vec2::ZERO),
            (2, 21, 2, Vec2::new(50.0, 0.0)),
        ] {
            state.spawn_ship(
                assets.by_type(kind).unwrap(), id, side,
                ShipVitals::new(100, 100, 1000, 0).unwrap(),
                ShipMotion::new(position, 0.0, 0.0, 0.0, 3.0),
            ).unwrap();
        }
        state.bind_policy_ship(1, PolicyFrame::new(0), false).unwrap();
        assert!(!state.policy_action_mask(1).unwrap().special[1]);
        state.configure_policy_yellow_jacket(1, 60).unwrap();
        assert!(state.policy_action_mask(1).unwrap().special[1]);
        let a = state.physics.ships()[0].layer;
        let b = state.physics.ships()[1].layer;
        let mut rows = [0u32; 32];
        rows[usize::from(a)] |= 1u32 << b;
        rows[usize::from(b)] |= 1u32 << a;
        let rules = CombatWorldRules {
            collision_matrix: CollisionMatrix::from_rows(rows),
        };
        let mut world = World::new(WorldId(41), 3, state);
        let mut action = Action::ZERO;
        action.discrete[5] = 1;
        let decision = Decision { agent_id: 1, action };
        rules.step(&mut world, &[decision], 0.02).unwrap();
        assert_eq!(world.state.ship(2).unwrap().vitals.health(), 100);
        let far = world.state.policy_special_measurements();
        assert_eq!(far.yellow_jacket_contact_misses, 1);
        assert_eq!(far.yellow_jacket_distance_rejections, 1);
        assert_eq!(far.yellow_jacket_exact_overlap_tests, 0);

        world.state.physics.ships_mut()[1].motion.position = Vec2::ZERO;
        rules.step(&mut world, &[decision], 0.02).unwrap();
        assert_eq!(world.state.ship(1).unwrap().vitals.health(), 0);
        assert_eq!(world.state.ship(2).unwrap().vitals.health(), 40);
        assert_eq!(world.state.projectile_storage_metrics().active_projectiles, 0);
        let bomb_hits: Vec<_> = world.state.events().iter().filter_map(|event| {
            if let CombatEvent::Hit(hit) = event {
                if hit.source == DamageSource::Bomb { return Some(*hit); }
            }
            None
        }).collect();
        assert_eq!(bomb_hits.len(), 2);
        assert_eq!(bomb_hits[0].target_id, 2);
        assert!(!bomb_hits[0].self_inflicted);
        assert_eq!(bomb_hits[1].target_id, 1);
        assert!(bomb_hits[1].self_inflicted);

        assert!(world.state.events().iter().any(|event|
            matches!(event, CombatEvent::YellowJacketDetonated {
                ship_id: 1, target_id: 2,
            })));
        assert!(world.state.events().iter().any(|event|
            matches!(event, CombatEvent::ShipDied {
                victim_id: 1, killer_id: 2, ..
            })));
        assert!(!world.state.physics.ships()[0].active);
        assert_eq!(world.state.policy_special_measurements().yellow_jacket_detonations, 1);
    }
}
