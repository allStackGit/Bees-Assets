//! Source-defined v26 policy movement and allied communication, applied
//! to the SAME CombatWorld used by rendered gameplay and headless workers.
//!
//! Unity: RlOneVsOneAgent.OnActionReceived, ApplyMovementCommand,
//! SetCommunicationActions and Ship.RlDirectionalMovement.
//! Ship-specific weapons, firing, healing, mining, warp and specials MUST be
//! implemented through real weapon/gameplay state before enabling their
//! branches. No plausible-looking zero-damage shooting is accepted.

use super::*;
use crate::policy_batch::PolicyActionMask;

#[derive(Clone, Debug)]
pub(super) struct PolicyShipRuntime {
    frame: PolicyFrame,
    movement_disabled: bool,
    control: PolicyControl,
}

impl CombatWorld {
    /// Bind a real living ship to its episode-specific policy frame.
    /// Call only when the episode controller is bound to this ship. A new
    /// CombatWorld owns fresh controllers when an episode resets.
    pub fn bind_policy_ship(
        &mut self,
        ship_id: i64,
        frame: PolicyFrame,
        movement_disabled: bool,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if !self.physics.ships()[index].active ||
            self.ships[index].vitals.is_dead() {
            return Err(CombatWorldError::UnknownShip);
        }
        self.policy_ship_runtime.insert(ship_id, PolicyShipRuntime {
            frame, movement_disabled, control: PolicyControl::new(),
        });
        Ok(())
    }

    /// Same 4 clamped continuous action channels as Unity's private allied
    /// ShipCommunications register. None means this ship has not been bound
    /// to a native policy controller; the observation adapter can then use
    /// an external authoritative controller's communication value instead.
    pub fn policy_communication(&self, ship_id: i64) -> Option<[f32; 4]> {
        self.policy_ship_runtime.get(&ship_id)
            .map(|runtime| runtime.control.communication())
    }

    /// Expose only actions backed by actual native gameplay, so inference
    /// can use the same authoritative mask as execution. Fire and special
    /// actions remain masked while weapon and ability controls are unported.
    pub fn policy_action_mask(
        &self,
        ship_id: i64,
    ) -> Result<PolicyActionMask, CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if !self.physics.ships()[index].active ||
            self.ships[index].vitals.is_dead() ||
            !self.policy_ship_runtime.contains_key(&ship_id) {
            return Err(CombatWorldError::UnknownShip);
        }
        Ok(PolicyActionMask {
            fire: [false; 5],
            special: [true, false, false, false, false],
        })
    }

    /// Apply only the subset of v26 semantics currently supported by the
    /// native gameplay world: ship movement and allied communication.
    ///
    /// Preflight the WHOLE decision batch before mutating any ship. This
    /// rejects duplicated/missing agents, nonfinite actions and unported
    /// firing/special branches rather than partially executing a batch.
    /// PolicyAgentKey's world/episode/tick freshness must be checked by the
    /// inference host before converting to these ship-ID decisions.
    pub fn apply_policy_decisions(
        &mut self,
        decisions: &[Decision],
    ) -> Result<(), CombatWorldError> {
        self.policy_decision_seen.clear();
        for decision in decisions {
            decision.action.validate()
                .map_err(CombatWorldError::InvalidPolicyAction)?;
            let id = i64::try_from(decision.agent_id)
                .map_err(|_| CombatWorldError::UnknownPolicyAgent(decision.agent_id))?;
            if id <= 0 {
                return Err(CombatWorldError::UnknownPolicyAgent(decision.agent_id));
            }
            if !self.policy_decision_seen.insert(decision.agent_id) {
                return Err(CombatWorldError::DuplicatePolicyAgent(decision.agent_id));
            }
            let Some(&index) = self.ship_by_id.get(&id) else {
                return Err(CombatWorldError::UnknownPolicyAgent(decision.agent_id));
            };
            if !self.policy_ship_runtime.contains_key(&id) ||
                !self.physics.ships()[index].active ||
                self.ships[index].vitals.is_dead() {
                return Err(CombatWorldError::UnknownPolicyAgent(decision.agent_id));
            }
            // Validate against the same world-owned mask supplied to
            // inference. Future weapon implementations must update BOTH
            // the mask and the actual firing path before enabling a branch.
            let mask = self.policy_action_mask(id)?;
            if decision.action.discrete[..5].iter().enumerate()
                .any(|(slot, &choice)| choice == 1 && !mask.fire[slot]) ||
                !mask.special[usize::from(decision.action.discrete[5])] {
                return Err(CombatWorldError::UnsupportedPolicyActions);
            }
        }

        for decision in decisions {
            let id = decision.agent_id as i64;
            let runtime = self.policy_ship_runtime.get_mut(&id)
                .expect("prevalidated policy controller");
            let decoded = runtime.control.decode(
                &decision.action, runtime.frame, &[false; 5],
            ).expect("prevalidated v26 action");
            let &index = self.ship_by_id.get(&id)
                .expect("prevalidated live ship ID");
            let motion = &mut self.physics.ships_mut()[index].motion;
            if runtime.movement_disabled {
                motion.disable_policy_movement();
            } else {
                motion.accept_policy_movement(decoded.movement_world);
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;
    use crate::collision::Aabb;
    use crate::geometry::Vec2;
    use crate::combat::ShipVitals;
    use crate::movement::{ShipMotion, STOP_MOVEMENT};
    use crate::projectile_assets::ProjectileAssetCatalog;
    use crate::ship_assets::ShipAssetCatalog;
    use crate::policy::Action;
    use crate::simulation::WorldId;
    use crate::episode_ship_identity::EpisodeShipIdentities;
    use crate::combat_perception_bridge::{
        observed_ship_from_world, AuthoredShipPerception,
    };

    fn world_with_one_ship() -> CombatWorld {
        let projectiles = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let hulls = ShipAssetCatalog::embedded().unwrap();
        let mut world = CombatWorld::new(Aabb {
            min: Vec2::new(-100.0, -100.0),
            max: Vec2::new(100.0, 100.0),
        }, 8.0, projectiles).unwrap();
        world.spawn_ship(
            hulls.by_type(21).unwrap(), 1, 1,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::ZERO, 0.0, 120.0, 8.0, 3.0),
        ).unwrap();
        world
    }

    #[test]
    fn policy_movement_and_communication_use_shared_combat_rules() {
        let mut combat = world_with_one_ship();
        combat.bind_policy_ship(1, PolicyFrame::new(1), false).unwrap();
        let mask = combat.policy_action_mask(1).unwrap();
        assert_eq!(mask.fire, [false; 5]);
        assert_eq!(mask.special, [true, false, false, false, false]);
        let mut action = Action::ZERO;
        action.continuous[0] = 1.0;
        action.continuous[12] = 0.6;
        action.continuous[13] = -0.4;
        let decision = Decision { agent_id: 1, action };
        let rules = CombatWorldRules {
            collision_matrix: CollisionMatrix::from_rows([0; 32]),
        };
        let mut world = World::new(WorldId(3), 123, combat);
        rules.step(&mut world, &[decision], 0.02).unwrap();
        let motion = &world.state.ship_body(1).unwrap().motion;
        assert!(motion.is_policy_controlled);
        assert_ne!(motion.movement_direction, STOP_MOVEMENT);
        assert_eq!(world.state.policy_communication(1),
            Some([0.6, -0.4, 0.0, 0.0]));
        let mut identities = EpisodeShipIdentities::new(5, 100).unwrap();
        let observed = observed_ship_from_world(
            &world.state, 1,
            AuthoredShipPerception {
                longest_side: 5.0,
                max_weapon_range: 0.0,
                firepower: 0.0,
                is_bomber: false,
                is_carrier_ship: false,
                minerals_mined_this_level: 0,
                observed_weapons: [None; 5],
                allied_communication: None,
            },
            &mut identities,
        ).unwrap();
        assert_eq!(observed.communication, Some([0.6, -0.4, 0.0, 0.0]));
    }

    #[test]
    fn unsupported_fire_or_special_refuses_whole_decision_batch() {
        let mut world = world_with_one_ship();
        world.bind_policy_ship(1, PolicyFrame::new(0), false).unwrap();
        let mut action = Action::ZERO;
        action.continuous[0] = 1.0;
        action.discrete[0] = 1;
        assert_eq!(
            world.apply_policy_decisions(&[Decision { agent_id: 1, action }]),
            Err(CombatWorldError::UnsupportedPolicyActions),
        );
        assert!(!world.ship_body(1).unwrap().motion.is_policy_controlled);
        assert_eq!(world.policy_communication(1), Some([0.0; 4]));

        action.discrete[0] = 0;
        action.discrete[5] = 1;
        assert_eq!(
            world.apply_policy_decisions(&[Decision { agent_id: 1, action }]),
            Err(CombatWorldError::UnsupportedPolicyActions),
        );
        assert!(!world.ship_body(1).unwrap().motion.is_policy_controlled);
    }
}
