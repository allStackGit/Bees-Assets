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
        let mut target = None;
        for index in 0..self.ships.len() {
            if index == ship_index || self.ships[index].side == side ||
                self.ships[index].vitals.is_dead() ||
                !self.physics.ships()[index].active {
                continue;
            }
            if self.physics.ship_hulls_overlap(ship_index, index) {
                let id = self.ships[index].entity_id;
                if target.is_none_or(|previous| id < previous) {
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
