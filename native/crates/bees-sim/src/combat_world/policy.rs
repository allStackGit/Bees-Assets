//! Source-defined v26 policy movement and allied communication, applied
//! to the SAME CombatWorld used by rendered gameplay and headless workers.
//!
//! Unity: RlOneVsOneAgent.OnActionReceived, ApplyMovementCommand,
//! SetCommunicationActions and Ship.RlDirectionalMovement.
//! Source-supported turrets, mining, healing, ship specials and authored
//! WarpGate returns execute in the same shared CombatWorld. Unported
//! weapon subclasses remain masked, never treated as no-ops.

use super::*;
use crate::policy_batch::PolicyActionMask;
use super::weapons::PolicyTurretMount;
use super::striker::PolicyStrikerRuntime;
use super::barge::PolicyBargeCharge;
use super::scout::PolicyScoutRuntime;
use super::healing::PolicyHealingRuntime;
use super::mining::PolicyMiningRuntime;

pub const WARP_GATE_TYPE: u8 = 20;

/// One source of truth for v26's readiness, resource, cooldown and
/// phase observation channels. A missing special yields all zeros.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PolicySpecialObservations {
    pub has_special: bool,
    pub ready: bool,
    pub resource_fraction: f32,
    pub cooldown_fraction: f32,
    pub phase: f32,
}

impl PolicySpecialObservations {
    const NONE: Self = Self {
        has_special: false, ready: false,
        resource_fraction: 0.0, cooldown_fraction: 0.0, phase: 0.0,
    };
}

/// Exactly one authored ship special can own an agent at a time.
#[derive(Clone, Copy, Debug)]
pub(super) enum PolicyShipSpecial {
    Striker(PolicyStrikerRuntime),
    FireBarge,
    YellowJacket { bomb_power: i32 },
    Barge(PolicyBargeCharge),
    Scout(PolicyScoutRuntime),
}

#[derive(Clone, Debug)]
pub(super) struct PolicyShipRuntime {
    pub(super) frame: PolicyFrame,
    /// Unique controller bind generation from this CombatWorld's roster
    /// revision; rebinding the same ship is a fresh decision clock.
    pub(super) bind_revision: u64,
    pub(super) movement_disabled: bool,
    pub(super) control: PolicyControl,
    /// Real physical turret mounts, in the exact five authored weapon slots.
    pub(super) turrets: [Option<PolicyTurretMount>; 5],
    /// Last physical hull angle used to rotate attached turrets.
    pub(super) last_hull_rotation: f32,
    /// Forwarded from the live gameplay ship's cease-fire state.
    pub(super) cease_fire: bool,
    /// This is a single native owner, not overlapping optional subclasses.
    pub(super) special: Option<PolicyShipSpecial>,
    /// Healing is its own action branch, and can coexist with a
    /// registered ship special without conflicting state ownership.
    pub(super) healing: Option<PolicyHealingRuntime>,
    pub(super) mining: Option<PolicyMiningRuntime>,
    /// Source human-side Warp action can coexist with a ship special.
    pub(super) warp_enabled: bool,
}

impl CombatWorld {
    /// The registry contains exactly one bound policy controller per
    /// living controlled ship. Retirement removes it atomically with
    /// death, so this is the complete input roster at decision time.
    /// The inference host must submit one observation for each member.
    pub fn bound_policy_agent_count(&self) -> usize {
        self.policy_ship_runtime.len()
    }

    /// Monotonic per-world controller membership revision. Rebinding a
    /// controller invalidates cached roster checks as well.
    pub fn policy_roster_revision(&self) -> u64 {
        self.policy_roster_revision
    }

    pub(super) fn bump_policy_roster_revision(&mut self) {
        self.policy_roster_revision = self.policy_roster_revision.checked_add(1)
            .expect("native policy roster revision exhausted");
    }

    /// Stable live policy roster: registration and ship death update this
    /// one BTreeMap, without creating a second training ship inventory.
    pub fn bound_policy_agent_ids(&self) -> impl Iterator<Item = i64> + '_ {
        self.policy_ship_runtime.keys().copied()
    }

    /// Stable registry IDs with their authoritative controller instance
    /// revisions. Rebinding a living ship changes its instance revision
    /// without creating a second policy roster.
    pub fn bound_policy_agent_bindings(&self)
        -> impl Iterator<Item = (i64, u64)> + '_
    {
        self.policy_ship_runtime.iter()
            .map(|(&id, runtime)| (id, runtime.bind_revision))
    }

    /// Use the controller's actual episode frame for both observation
    /// encoding and action execution. External callers must never
    /// supply a second independently stored frame.
    pub fn policy_frame(&self, ship_id: i64) -> Option<PolicyFrame> {
        self.policy_ship_runtime.get(&ship_id)
            .map(|runtime| runtime.frame)
    }

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
        let rotation = self.physics.ships()[index].motion.rotation_degrees;
        self.bump_policy_roster_revision();
        self.policy_ship_runtime.insert(ship_id, PolicyShipRuntime {
            frame, bind_revision: self.policy_roster_revision,
            movement_disabled, control: PolicyControl::new(),
            turrets: [None; 5],
            last_hull_rotation: rotation,
            cease_fire: false,
            special: None,
            healing: None,
            mining: None,
            warp_enabled: false,
        });
        Ok(())
    }

    /// Source RlCombatPerception.GetSpecialReadiness,
    /// GetSpecialResourceFraction, GetSpecialCooldownFraction and
    /// GetSpecialPhase. This owns the same state as policy_action_mask,
    /// so observation encoding cannot silently use a stale training-only
    /// notion of special readiness.
    ///
    /// None means that no native policy controller is bound. Some(NONE)
    /// means that a native controller exists but no supported special is
    /// currently registered; this must not advertise a fake capability.
    pub fn policy_special_observations(
        &self, ship_id: i64,
    ) -> Option<PolicySpecialObservations> {
        let runtime = self.policy_ship_runtime.get(&ship_id)?;
        let result = match runtime.special {
            None => PolicySpecialObservations::NONE,
            Some(PolicyShipSpecial::Striker(striker)) =>
                PolicySpecialObservations {
                    has_special: true, ready: striker.ready,
                    resource_fraction: if striker.ready { 1.0 } else { 0.0 },
                    cooldown_fraction: 0.0,
                    phase: if striker.ready { 0.0 } else { 0.75 },
                },
            Some(PolicyShipSpecial::FireBarge) |
            Some(PolicyShipSpecial::YellowJacket { .. }) =>
                PolicySpecialObservations {
                    has_special: true, ready: true,
                    resource_fraction: 1.0,
                    cooldown_fraction: 0.0, phase: 0.0,
                },
            Some(PolicyShipSpecial::Barge(barge)) => {
                let ready = barge.phase == 0;
                PolicySpecialObservations {
                    has_special: true, ready,
                    resource_fraction: if ready { 1.0 } else { 0.0 },
                    cooldown_fraction:
                        self.policy_barge_charge_cooldown_fraction(ship_id)?,
                    phase: f32::from(barge.phase.min(3)) / 3.0,
                }
            }
            Some(PolicyShipSpecial::Scout(_)) => {
                let scout = self.policy_scout_beacon_status(ship_id)?;
                PolicySpecialObservations {
                    has_special: true, ready: scout.ready,
                    resource_fraction: scout.resource_fraction,
                    cooldown_fraction: scout.cooldown_fraction,
                    phase: scout.phase,
                }
            }
        };
        Some(result)
    }

    /// Same 4 clamped continuous action channels as Unity's private allied
    /// ShipCommunications register. None means this ship has not been bound
    /// to a native policy controller; the observation adapter can then use
    /// an external authoritative controller's communication value instead.
    pub fn policy_communication(&self, ship_id: i64) -> Option<[f32; 4]> {
        self.policy_ship_runtime.get(&ship_id)
            .map(|runtime| runtime.control.communication())
    }

    /// One per-world capability timer pass for both mining and healing.
    /// Avoid traversing the whole policy roster twice every physics tick,
    /// including large battles where these capabilities are uncommon.
    pub(super) fn advance_policy_capability_cooldowns(&mut self, dt: f32) {
        for runtime in self.policy_ship_runtime.values_mut() {
            if let Some(mining) = runtime.mining.as_mut() {
                mining.cooldown_remaining =
                    (mining.cooldown_remaining - dt).max(0.0);
            }
            if let Some(healing) = runtime.healing.as_mut() {
                healing.cooldown_remaining =
                    (healing.cooldown_remaining - dt).max(0.0);
            }
        }
    }

    /// Expose only actions backed by actual native gameplay. Registered
    /// source-supported turret slots enable their fire branch; missing and
    /// unported weapon slots plus every special remain masked.
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
        let runtime = self.policy_ship_runtime.get(&ship_id)
            .expect("validated bound policy ship");
        Ok(PolicyActionMask {
            fire: std::array::from_fn(|slot| runtime.turrets[slot].is_some()),
            special: [
                true,
                match runtime.special {
                    None => false,
                    Some(PolicyShipSpecial::Striker(striker)) => striker.ready,
                    Some(PolicyShipSpecial::FireBarge) |
                    Some(PolicyShipSpecial::YellowJacket { .. }) => true,
                    Some(PolicyShipSpecial::Barge(barge)) => barge.phase == 0,
                    Some(PolicyShipSpecial::Scout(scout)) =>
                        self.policy_scout_ready(ship_id, scout),
                },
                self.policy_mining_ready(ship_id),
                self.policy_healing_ready(ship_id),
                self.policy_warp_ready(ship_id),
            ],
        })
    }

    /// Apply v26 movement, allied communication and configured base-turret
    /// aim/fire through the same native gameplay state.
    ///
    /// Preflight the WHOLE decision batch before mutating any ship. This
    /// rejects duplicated/missing agents, nonfinite outputs, unsupported
    /// weapon slots and special branches before partially executing a batch.
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
            // inference. A slot is enabled only after real mount registration;
            // future subclasses must connect their actual firing path.
            let mask = self.policy_action_mask(id)?;
            if decision.action.discrete[..5].iter().enumerate()
                .any(|(slot, &choice)| choice == 1 && !mask.fire[slot]) ||
                !mask.special[usize::from(decision.action.discrete[5])] {
                return Err(CombatWorldError::UnsupportedPolicyActions);
            }
        }

        // Apply the whole preflight first; only then replace this tick's
        // pending specials. World execution happens after fixed-step event
        // clearing, so UI, telemetry and RL receive the drop event.
        self.policy_pending_ship_specials.clear();
        self.policy_pending_mining.clear();
        self.policy_pending_healing.clear();
        self.policy_pending_warp.clear();
        for decision in decisions {
            let id = decision.agent_id as i64;
            let (decoded, disabled) = {
                let runtime = self.policy_ship_runtime.get_mut(&id)
                    .expect("prevalidated policy controller");
                let available = std::array::from_fn(|slot|
                    runtime.turrets[slot].is_some());
                (
                    runtime.control.decode(
                        &decision.action, runtime.frame, &available,
                    ).expect("prevalidated v26 action"),
                    runtime.movement_disabled,
                )
            };
            self.apply_policy_turret_requests(id, &decoded.weapons);
            let &index = self.ship_by_id.get(&id)
                .expect("prevalidated live ship ID");
            let motion = &mut self.physics.ships_mut()[index].motion;
            if disabled {
                motion.disable_policy_movement();
            } else {
                motion.accept_policy_movement(decoded.movement_world);
            }
            match decision.action.discrete[5] {
                1 => {
                    // Exactly one ship-class special per controller.
                    self.policy_pending_ship_specials.push(id);
                }
                2 => {
                    // Factory/CarpenterBee uses an authored mine and FleetShip.
                    self.policy_pending_mining.push(id);
                }
                3 => {
                    // Beehive healing is a separate capability request.
                    self.policy_pending_healing.push(id);
                }
                4 => {
                    // A real WarpGate child collider gates a safe EndKill.
                    self.policy_pending_warp.push(id);
                }
                _ => {}
            }
        }
        Ok(())
    }

    /// Commit all validated ship specials inside the shared fixed tick, not
    /// the inference callback. A single sorted queue preserves deterministic
    /// ordering across supported ship classes irrespective of batch order.
    pub(super) fn apply_pending_policy_ship_specials(
        &mut self, collisions: &CollisionMatrix,
    ) -> Result<(), CombatWorldError> {
        self.policy_pending_ship_specials.sort_unstable();
        for index in 0..self.policy_pending_ship_specials.len() {
            let ship_id = self.policy_pending_ship_specials[index];
            self.policy_special_measurements.requests += 1;
            let Some(runtime) = self.policy_ship_runtime.get(&ship_id) else {
                // An earlier action in this tick may have killed the ship.
                continue;
            };
            let Some(special) = runtime.special else {
                return Err(CombatWorldError::UnsupportedPolicyActions);
            };
            match special {
                PolicyShipSpecial::FireBarge =>
                    { self.try_detonate_policy_fire_barge(ship_id)?; }
                PolicyShipSpecial::Striker(_) =>
                    { self.try_drop_policy_striker_bomb(ship_id, collisions)?; }
                PolicyShipSpecial::YellowJacket { .. } =>
                    { self.try_detonate_policy_yellow_jacket(ship_id, collisions)?; }
                PolicyShipSpecial::Barge(_) =>
                    { self.try_begin_policy_barge_charge(ship_id)?; }
                PolicyShipSpecial::Scout(_) =>
                    { self.try_drop_policy_scout_beacon(ship_id)?; }
            }
        }
        self.policy_pending_ship_specials.clear();
        Ok(())
    }
}


impl CombatWorld {
    /// Source WarpGate.WarpCollider is a separate authored child trigger,
    /// never inferred from the parent ship's primary collision hull.
    pub fn configure_warp_gate_zone(
        &mut self, gate_id: i64, child_shape: ShipShape,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&gate_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].ship_type != WARP_GATE_TYPE ||
            self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active {
            return Err(CombatWorldError::InvalidPolicyWarp);
        }
        self.physics.ships_mut()[index].set_warp_zone(child_shape)?;
        // Register only after the authored collider has been accepted.
        // The common index preserves lowest-ID selection without copying
        // geometry or scanning every ship on an action-mask query.
        let side = self.ships[index].side;
        self.register_support_zone(WARP_GATE_TYPE, side, gate_id);
        Ok(())
    }

    /// ConfigData.Configuration.HumanSide is supplied by the owning
    /// Level. Any living Human ship except WarpGate is eligible.
    pub fn configure_policy_warp(
        &mut self, ship_id: i64, human_side: i32,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if human_side <= 0 || self.ships[index].side != human_side ||
            self.ships[index].ship_type == WARP_GATE_TYPE ||
            self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active {
            return Err(CombatWorldError::InvalidPolicyWarp);
        }
        let runtime = self.policy_ship_runtime.get_mut(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        runtime.warp_enabled = true;
        Ok(())
    }

    /// Source FindTouchingWarpGate. The index contains only live gates
    /// with authored child colliders, sorted by ID. Most battles have no
    /// registered gate, making the mask check a single failed map lookup.
    /// For nonempty sets, inspect the exact current collision geometry and
    /// stop at the first contact: the lowest ID always wins.
    fn touching_warp_gate(&self, ship_id: i64) -> Option<i64> {
        let &index = self.ship_by_id.get(&ship_id)?;
        let ship = &self.ships[index];
        let body = &self.physics.ships()[index];
        if ship.vitals.is_dead() || !body.active {
            return None;
        }
        for &gate_id in self.support_zone_ids.get(&(WARP_GATE_TYPE, ship.side))? {
            let Some(&gate_index) = self.ship_by_id.get(&gate_id) else {
                continue;
            };
            let gate = &self.ships[gate_index];
            let gate_body = &self.physics.ships()[gate_index];
            if gate.ship_type == WARP_GATE_TYPE &&
                gate.entity_id != ship_id && gate.side == ship.side &&
                !gate.vitals.is_dead() && gate_body.active &&
                gate_body.warp_zone_touches(body) {
                return Some(gate_id);
            }
        }
        None
    }

    pub(super) fn policy_warp_ready(&self, ship_id: i64) -> bool {
        self.policy_ship_runtime.get(&ship_id)
            .is_some_and(|runtime| runtime.warp_enabled) &&
            self.touching_warp_gate(ship_id).is_some()
    }

    /// Source TryApplyWarpAction calls EndKill, not combat damage.
    /// ShipWarped must precede ShipDied so the same economy consumer
    /// retains mined cargo. No second attributed hit or death explosion.
    pub(super) fn apply_pending_policy_warp(
        &mut self,
    ) -> Result<(), CombatWorldError> {
        self.policy_pending_warp.sort_unstable();
        for item in 0..self.policy_pending_warp.len() {
            let ship_id = self.policy_pending_warp[item];
            self.policy_special_measurements.warp_requests += 1;
            if !self.policy_ship_runtime.get(&ship_id)
                .is_some_and(|runtime| runtime.warp_enabled) {
                continue;
            }
            let Some(gate_id) = self.touching_warp_gate(ship_id) else {
                continue;
            };
            // Policy training's immediate contact warp retains its
            // source timing; game FullRetreat uses the same EndKill
            // transaction only after the authored gate opening event.
            self.end_kill_warp_ship(ship_id, gate_id)?;
            self.policy_special_measurements.warp_actions_completed += 1;
        }
        self.policy_pending_warp.clear();
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
