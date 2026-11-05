//! Striker's actual Bomb weapon is a ship special, not a turret.
//!
//! Sources: Striker.TryToDropBombs / DropBomb / CheckCarrierReload,
//! StrikerBomb.Setup/KillSequence, RlOneVsOneAgent.ApplySpecialAction.
//! This owns only policy Striker readiness and its carrier relation. The
//! existing world StrikerBombFuse owns delayed damage, original target and
//! original shooter-fleet attribution, even after the Striker is destroyed.

use super::*;

/// The RL special repeatedly attempts an actual contact drop. There is no
/// BombingRun command in native policy control, just as in Unity's RL path.
#[derive(Clone, Copy, Debug)]
pub(super) struct PolicyStrikerRuntime {
    pub(super) carrier_id: i64,
    pub(super) bomb_power: i32,
    pub(super) ready: bool,
    pub(super) reload_timer: ScaledTimer,
}

impl CombatWorld {
    /// Bind the current Striker's real Carrier and authored Bomb.Power.
    /// The fleet identity must already be bound for delayed damage credit.
    /// Unity Striker is ShipTypes.Striker=19, WeaponTypes.Bomb=0.
    pub fn configure_policy_striker(
        &mut self,
        striker_id: i64,
        carrier_id: i64,
        bomb_power: i32,
    ) -> Result<(), CombatWorldError> {
        let &ship_index = self.ship_by_id.get(&striker_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let &carrier_index = self.ship_by_id.get(&carrier_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let ship = &self.ships[ship_index];
        let carrier = &self.ships[carrier_index];
        if ship.ship_type != 19 || ship.entity_id == carrier.entity_id ||
            ship.side != carrier.side || ship.vitals.is_dead() ||
            carrier.vitals.is_dead() || !self.physics.ships()[ship_index].active ||
            !self.physics.ships()[carrier_index].active || bomb_power < 0 {
            return Err(CombatWorldError::InvalidPolicyStriker);
        }
        if !self.fleet_id_by_ship.get(&striker_id)
            .is_some_and(|&fleet_id| fleet_id > 0) {
            return Err(CombatWorldError::InvalidFleetIdentity);
        }
        let runtime = self.policy_ship_runtime.get_mut(&striker_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if runtime.striker.is_some() {
            return Err(CombatWorldError::InvalidPolicyStriker);
        }
        runtime.striker = Some(PolicyStrikerRuntime {
            carrier_id,
            bomb_power,
            ready: true, // Striker.ClearData: IsBombReady = true
            reload_timer: ScaledTimer::new(1.0, true, false),
        });
        Ok(())
    }

    /// Current authoritative loaded state for UI and RL observations.
    pub fn policy_striker_bomb_ready(&self, striker_id: i64) -> Option<bool> {
        self.policy_ship_runtime.get(&striker_id)?
            .striker.as_ref().map(|striker| striker.ready)
    }

    /// Striker.TryToDropBombs is a contact-triggered ship special. Find an
    /// enemy physically touching the *real* collider, never one merely
    /// within an invented radius. Unity caches its most recent TouchingShip;
    /// native ambiguity is resolved by stable runtime entity ID.
    pub(super) fn try_drop_policy_striker_bomb(
        &mut self, striker_id: i64,
    ) -> Result<bool, CombatWorldError> {
        let &ship_index = self.ship_by_id.get(&striker_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let runtime = self.policy_ship_runtime.get(&striker_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let Some(profile) = runtime.striker else {
            return Err(CombatWorldError::InvalidPolicyStriker);
        };
        if !profile.ready || !self.physics.ships()[ship_index].active ||
            self.ships[ship_index].vitals.is_dead() {
            return Ok(false);
        }
        let side = self.ships[ship_index].side;
        let mut target: Option<i64> = None;
        for index in 0..self.ships.len() {
            if index == ship_index || self.ships[index].side == side ||
                self.ships[index].vitals.is_dead() ||
                !self.physics.ships()[index].active {
                continue;
            }
            if self.physics.ship_hulls_overlap(ship_index, index) {
                let id = self.ships[index].entity_id;
                if target.map_or(true, |previous| id < previous) {
                    target = Some(id);
                }
            }
        }
        let Some(target_id) = target else { return Ok(false); };
        let fleet_id = *self.fleet_id_by_ship.get(&striker_id)
            .ok_or(CombatWorldError::InvalidFleetIdentity)?;
        let shot_id = self.next_entity_id;
        if shot_id >= i64::MAX {
            return Err(CombatWorldError::EntityIdExhausted);
        }
        // Unity RL training resolves this as a Level-owned two-second
        // timer. The native game world uses the already-authored equivalent
        // StrikerBombFuse (1.5s attachment + 0.5s damage). Projectile #12
        // has no physical collision body, so attaching at the target center
        // changes no hit/damage outcome. A rendered Level may choose the
        // source random cosmetic attach point when it imports the event.
        let target_index = *self.ship_by_id.get(&target_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let position = self.physics.ships()[target_index].motion.position;
        self.spawn_striker_bomb(
            shot_id,
            ShotOwner {
                shooter_id: striker_id,
                shooter_side: side,
                shooter_is_fire_barge: false,
                self_detonation: false,
            },
            target_id, fleet_id, profile.bomb_power,
            ProjectileMotion::new(position, 0.0, 0.0, 0.0, shot_id, 0),
        )?;
        // Do not consume readiness unless the real bomb/fuse was created.
        self.policy_ship_runtime.get_mut(&striker_id)
            .expect("validated Striker runtime").striker.as_mut()
            .expect("validated bomb profile").ready = false;
        self.events.push(CombatEvent::StrikerBombDropped {
            ship_id: striker_id,
            target_id,
            projectile_id: shot_id,
        });
        Ok(true)
    }

    /// Actual Striker.CheckCarrierReload uses its recurring 1s Level timer:
    /// loaded state returns when the Striker is within 15 world units of
    /// its still-living Carrier. No magical replenishment by cooldown alone.
    pub(super) fn advance_policy_striker_reloads(&mut self, dt: f32) {
        for (&ship_id, runtime) in &mut self.policy_ship_runtime {
            let Some(profile) = runtime.striker.as_mut() else { continue };
            let Some(due) = profile.reload_timer.begin_update(dt) else {
                continue;
            };
            let ready_before = profile.ready;
            if !profile.ready {
                if let (Some(&striker_index), Some(&carrier_index)) = (
                    self.ship_by_id.get(&ship_id),
                    self.ship_by_id.get(&profile.carrier_id),
                ) {
                    let striker = &self.ships[striker_index];
                    let carrier = &self.ships[carrier_index];
                    if !striker.vitals.is_dead() && !carrier.vitals.is_dead() &&
                        self.physics.ships()[striker_index].active &&
                        self.physics.ships()[carrier_index].active {
                        let a = self.physics.ships()[striker_index].motion.position;
                        let b = self.physics.ships()[carrier_index].motion.position;
                        if (a - b).length_squared() < 15.0 * 15.0 {
                            profile.ready = true;
                        }
                    }
                }
            }
            profile.reload_timer.finish_update(due);
            if !ready_before && profile.ready {
                self.events.push(CombatEvent::StrikerBombReloaded {
                    ship_id,
                    carrier_id: profile.carrier_id,
                });
            }
        }
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

    fn actual_striker_world() -> CombatWorld {
        let projectiles = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let hulls = ShipAssetCatalog::embedded().unwrap();
        let mut world = CombatWorld::new(Aabb {
            min: Vec2::new(-200.0, -200.0),
            max: Vec2::new(200.0, 200.0),
        }, 8.0, projectiles).unwrap();

        // 19=Striker; 5=Carrier; 21=Wasp. These use their actual
        // prefab hulls, not a training-only collision radius.
        for (id, ship_type, side, position) in [
            (1, 19, 1, Vec2::new(-100.0, 0.0)),
            (2, 5, 1, Vec2::new(-75.0, 0.0)),
            (3, 21, 2, Vec2::new(75.0, 0.0)),
        ] {
            world.spawn_ship(
                hulls.by_type(ship_type).unwrap(), id, side,
                ShipVitals::new(100, 100, 1000, 0).unwrap(),
                ShipMotion::new(position, 0.0, 0.0, 0.0, 3.0),
            ).unwrap();
        }
        world.set_ship_fleet_identity(1, 101).unwrap();
        world.bind_policy_ship(1, PolicyFrame::new(0), false).unwrap();
        world.configure_policy_striker(1, 2, 25).unwrap();
        world
    }

    #[test]
    fn actual_hull_contact_triggers_striker_special_and_delayed_projectile_damage() {
        let state = actual_striker_world();
        assert_eq!(state.policy_action_mask(1).unwrap().fire, [false; 5]);
        assert_eq!(state.policy_action_mask(1).unwrap().special,
            [true, true, false, false, false]);
        let mut world = World::new(WorldId(23), 7, state);
        let rules = CombatWorldRules {
            collision_matrix: CollisionMatrix::from_rows([0; 32]),
        };
        let mut action = Action::ZERO;
        action.discrete[5] = 1; // Unity ShipSpecialAction
        let command = Decision { agent_id: 1, action };

        // A ready Striker cannot bomb a distant enemy. No projectile or
        // readiness change occurs until the authored colliders touch.
        rules.step(&mut world, &[command], 0.02).unwrap();
        assert_eq!(world.state.policy_striker_bomb_ready(1), Some(true));
        assert_eq!(world.state.projectile_storage_metrics().active_projectiles, 0);

        // Move the actual Striker collider into the Wasp's hull, then
        // execute the identical RL special through real CombatWorldRules.
        let target_position = world.state.physics.ships()[2].motion.position;
        world.state.physics.ships_mut()[0].motion.position = target_position;
        rules.step(&mut world, &[command], 0.02).unwrap();
        let shot_id = world.state.events().iter().find_map(|event| match event {
            CombatEvent::StrikerBombDropped {
                ship_id: 1, target_id: 3, projectile_id,
            } => Some(*projectile_id),
            _ => None,
        }).expect("contacted Striker must drop its real bomb");
        let index = *world.state.projectile_by_id.get(&shot_id).unwrap();
        assert!(world.state.physics.projectiles()[index].active);
        assert_eq!(world.state.projectiles()[index].projectile_type, 12);
        assert_eq!(world.state.projectiles()[index].power, 25);
        assert_eq!(world.state.projectiles()[index].owner.shooter_id, 1);
        assert_eq!(world.state.policy_striker_bomb_ready(1), Some(false));
        assert!(!world.state.policy_action_mask(1).unwrap().special[1]);
        assert_eq!(world.state.ship(3).unwrap().vitals.health(), 100);

        // The same native StrikerBombFuse owns detonation at 1.5 seconds
        // and damage 0.5 seconds later. Before the second callback the
        // target's health remains unaffected.
        let mut detonated = false;
        let mut hit = false;
        for _ in 0..120 {
            rules.step(&mut world, &[], 0.02).unwrap();
            detonated |= world.state.events().iter().any(|event|
                matches!(event, CombatEvent::BombDetonated {
                    projectile_id, target_id: 3,
                } if *projectile_id == shot_id));
            hit |= world.state.events().iter().any(|event|
                matches!(event, CombatEvent::Hit(h)
                    if h.projectile_id == shot_id && h.shooter_id == 1));
        }
        assert!(detonated);
        assert!(hit);
        assert_eq!(world.state.ship(3).unwrap().vitals.health(), 75);
        assert!(!world.state.physics.projectiles()[index].active);
        assert!(!world.state.policy_action_mask(1).unwrap().special[1]);
    }

    #[test]
    fn bomb_reloads_on_carrier_timer_not_on_elapsed_bomb_fuse() {
        let mut state = actual_striker_world();
        state.physics.ships_mut()[0].motion.position =
            state.physics.ships()[2].motion.position;
        let mut action = Action::ZERO;
        action.discrete[5] = 1;
        state.apply_policy_decisions(&[Decision { agent_id: 1, action }])
            .unwrap();
        assert_eq!(state.policy_striker_bomb_ready(1), Some(false));
        assert_eq!(state.projectile_storage_metrics().active_projectiles, 1);

        // The carrier is far away: neither time alone nor a completed
        // bomb fuse can rearm the Striker.
        state.advance_policy_striker_reloads(2.0);
        assert_eq!(state.policy_striker_bomb_ready(1), Some(false));

        // Reapproach the actual carrier; the next recurring one-second
        // check makes another policy special legal.
        state.physics.ships_mut()[0].motion.position =
            state.physics.ships()[1].motion.position;
        state.advance_policy_striker_reloads(1.02);
        assert_eq!(state.policy_striker_bomb_ready(1), Some(true));
        assert!(state.policy_action_mask(1).unwrap().special[1]);
        assert!(state.events().iter().any(|event|
            matches!(event, CombatEvent::StrikerBombReloaded {
                ship_id: 1, carrier_id: 2,
            })));

        // Physically touching the target again creates a SECOND distinct
        // fuse without replacing the first, which is still in flight.
        state.physics.ships_mut()[0].motion.position =
            state.physics.ships()[2].motion.position;
        state.apply_policy_decisions(&[Decision { agent_id: 1, action }])
            .unwrap();
        assert_eq!(state.projectile_storage_metrics().active_projectiles, 2);
        assert_eq!(state.policy_striker_bomb_ready(1), Some(false));
    }
}
