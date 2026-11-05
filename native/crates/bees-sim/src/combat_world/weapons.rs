//! Authored rotating-turret gameplay, using the canonical CombatWorld.
//!
//! Source: Turret.Create/Setup/TargetingSequence, Turret.Aiming,
//! RlOneVsOneAgent.ApplyWeaponCommand, Level.AddProjectile and Projectile.Setup.
//! Only simple Turret, LightCannon, RocketTurret, Eye and QueenEye mounts are
//! accepted here: DualCannon, BeamCannon, LaserBuilder/SplitShot,
//! FullShipTurret and Bomb have materially different firing continuations.
//! Missing prefab and live Level fields are errors, not guessed defaults.

use super::*;
use crate::control::WeaponRequest;
use crate::geometry::Vec2;
use crate::perception_weapons::{SelfWeapon, TurretObservation};
use crate::perception_entities::ObservedWeapon;
use crate::turret::TurretState;

/// Supplied from the REAL authored weapon and turret piece, not a collider
/// guess, a training recipe, or an observation copied from another ship.
#[derive(Clone, Copy, Debug)]
pub struct NativeTurretSpec {
    /// ConfigData.WeaponTypes: LightCannon=2, Turret=3,
    /// RocketTurret=5, Eye=7, QueenEye=8.
    pub weapon_type: u8,
    pub projectile_type: u8,
    /// Turret PieceTransform local offset relative to ship center at 0°.
    pub mount_offset: Vec2,
    /// The actual current PieceTransform.eulerAngles.z on ship bind.
    pub initial_rotation_degrees: f32,
    pub rotation_rate: f32,
    /// Authored turret.Range and Power.
    pub range: f32,
    pub power: i32,
    /// Seconds per shot (Unity RateOfFire), NOT shots per second.
    pub rate_of_fire: f32,
    pub projectile_value: f32,
    /// Real projectile prefab Speed. Only projectile type 1 has its Speed
    /// serialized in the current native catalog; other types MUST have a
    /// value supplied by the authored Level/Stage adapter.
    pub projectile_speed: f64,
}

impl NativeTurretSpec {
    fn valid(self, catalog: &ProjectileAssetCatalog) -> Result<(), CombatWorldError> {
        let supported_weapon = matches!(self.weapon_type, 2 | 3 | 5 | 7 | 8);
        // These cases use the same projectile setup and collision rules as
        // base Turret.SendProjectile; subclasses with bespoke launch paths
        // are deliberately not accepted.
        let supported_projectile = matches!(
            self.projectile_type, 1 | 2 | 3 | 5 | 6 | 7 | 10 | 11
        );
        if !supported_weapon || !supported_projectile {
            return Err(CombatWorldError::UnsupportedPolicyTurret {
                weapon_type: self.weapon_type,
                projectile_type: self.projectile_type,
            });
        }
        if (self.weapon_type == 5) != (self.projectile_type == 5) {
            return Err(CombatWorldError::UnsupportedPolicyTurret {
                weapon_type: self.weapon_type,
                projectile_type: self.projectile_type,
            });
        }
        if !self.mount_offset.is_finite() ||
            !self.initial_rotation_degrees.is_finite() ||
            !self.rotation_rate.is_finite() || self.rotation_rate < 0.0 ||
            !self.range.is_finite() || self.range <= 0.0 ||
            self.power < 0 ||
            !self.rate_of_fire.is_finite() || self.rate_of_fire <= 0.0 ||
            !self.projectile_value.is_finite() || self.projectile_value < 0.0 ||
            !self.projectile_speed.is_finite() || self.projectile_speed <= 0.0 {
            return Err(CombatWorldError::InvalidPolicyTurret);
        }
        let asset = catalog.by_type(self.projectile_type)
            .ok_or(CombatWorldError::InvalidProjectileType(self.projectile_type))?;
        if !asset.has_body || asset.shape.is_none() {
            return Err(CombatWorldError::UnsupportedPolicyTurret {
                weapon_type: self.weapon_type,
                projectile_type: self.projectile_type,
            });
        }
        if asset.shot_speed.is_some_and(|known|
            (known - self.projectile_speed).abs() > 0.00001) {
            return Err(CombatWorldError::InvalidPolicyTurret);
        }
        Ok(())
    }
}

#[derive(Clone, Copy, Debug)]
pub(super) struct PolicyTurretMount {
    pub(super) spec: NativeTurretSpec,
    pub(super) state: TurretState,
    pub(super) timer: ScaledTimer,
}

impl PolicyTurretMount {
    fn new(spec: NativeTurretSpec) -> Self {
        Self {
            state: TurretState::new(
                spec.initial_rotation_degrees, spec.rotation_rate,
            ),
            timer: ScaledTimer::new(spec.rate_of_fire / 3.0, true, false),
            spec,
        }
    }

    #[inline]
    fn position(&self, ship_position: Vec2, hull_rotation: f32) -> Vec2 {
        let (sine, cosine) = hull_rotation.to_radians().sin_cos();
        let local = self.spec.mount_offset;
        ship_position + Vec2::new(
            local.x * cosine - local.y * sine,
            local.x * sine + local.y * cosine,
        )
    }

    fn self_observation(&self, ship_position: Vec2, hull_rotation: f32) -> SelfWeapon {
        SelfWeapon {
            weapon_type: self.spec.weapon_type,
            position: self.position(ship_position, hull_rotation),
            range: self.spec.range,
            power: self.spec.power as f32,
            rate_of_fire: self.spec.rate_of_fire,
            rotation_rate: self.spec.rotation_rate,
            projectile_value: self.spec.projectile_value,
            projectile_speed: Some(self.spec.projectile_speed),
            turret: Some(TurretObservation {
                rotation_degrees: self.state.rotation_degrees,
                ready_to_fire: self.state.ready_to_fire,
                aligned_with_target: self.state.is_aligned_with_target,
            }),
            has_target_ship: false, // manual policy point-fire
        }
    }

    fn observed_weapon(&self) -> ObservedWeapon {
        ObservedWeapon {
            weapon_type: self.spec.weapon_type,
            range: self.spec.range,
            power: self.spec.power as f32,
            rate_of_fire: self.spec.rate_of_fire,
        }
    }
}

/// A scheduled shot is snapshot before mutating projectile storage. This
/// permits the turret timer to reuse its per-ship state while all projectiles
/// are spawned in deterministic ship-ID and exact weapon-slot order.
#[derive(Clone, Copy, Debug)]
pub(super) struct PendingPolicyShot {
    ship_id: i64,
    slot: u8,
    shooter_side: i32,
    shooter_is_fire_barge: bool,
    turret_position: Vec2,
    target_world: Vec2,
    spec: NativeTurretSpec,
}

impl CombatWorld {
    /// Register an exact physical weapon slot after the ship has bound its
    /// policy controller. This is world-owned gameplay state used by policy
    /// masking, self/ally observations, aiming and real shot spawning.
    pub fn configure_policy_turret(
        &mut self,
        ship_id: i64,
        slot: usize,
        spec: NativeTurretSpec,
    ) -> Result<(), CombatWorldError> {
        if slot >= 5 { return Err(CombatWorldError::InvalidPolicyTurret); }
        spec.valid(&self.projectile_assets)?;
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if !self.physics.ships()[index].active ||
            self.ships[index].vitals.is_dead() {
            return Err(CombatWorldError::UnknownShip);
        }
        let runtime = self.policy_ship_runtime.get_mut(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if runtime.turrets[slot].is_some() {
            return Err(CombatWorldError::DuplicatePolicyTurret { ship_id, slot });
        }
        let radians = spec.initial_rotation_degrees.to_radians();
        runtime.control.bind_turret_aim(
            slot, Vec2::new(-radians.sin(), radians.cos()),
        );
        runtime.turrets[slot] = Some(PolicyTurretMount::new(spec));
        Ok(())
    }

    /// The actual game's cease-fire state must be forwarded on change by
    /// the Level adapter. It gates Aim and Fire independently of action
    /// masks, just as Unity does.
    pub fn set_policy_ship_cease_fire(
        &mut self, ship_id: i64, cease_fire: bool,
    ) -> Result<(), CombatWorldError> {
        let runtime = self.policy_ship_runtime.get_mut(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        runtime.cease_fire = cease_fire;
        Ok(())
    }

    pub fn policy_self_weapons(
        &self, ship_id: i64,
    ) -> Result<[Option<SelfWeapon>; 5], CombatWorldError> {
        let runtime = self.policy_ship_runtime.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let body = self.ship_body(ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if !body.active {
            return Err(CombatWorldError::UnknownShip);
        }
        Ok(std::array::from_fn(|slot| {
            runtime.turrets[slot].as_ref().map(|mount|
                mount.self_observation(
                    body.motion.position, body.motion.rotation_degrees,
                )
            )
        }))
    }

    /// Real short weapon summaries for the faction-visible ally/enemy
    /// block. The authoritative metadata collector still owns all
    /// unregistered non-turret slots and their appropriate observations.
    pub fn policy_observed_weapons(
        &self, ship_id: i64,
    ) -> Option<[Option<ObservedWeapon>; 5]> {
        let runtime = self.policy_ship_runtime.get(&ship_id)?;
        Some(std::array::from_fn(|slot| {
            runtime.turrets[slot].as_ref().map(|mount|
                mount.observed_weapon()
            )
        }))
    }

    /// Advance mounted rotating weapons with the exact turret targeting
    /// passes and shared ScaledTimer, using the post-hull-turn ship pose.
    /// Called from the canonical gameplay step BEFORE projectile physics.
    pub(super) fn advance_policy_turrets(
        &mut self, dt: f32,
    ) -> Result<(), CombatWorldError> {
        self.policy_pending_shots.clear();
        for (&ship_id, runtime) in &mut self.policy_ship_runtime {
            let Some(&index) = self.ship_by_id.get(&ship_id) else { continue };
            let ship = &self.ships[index];
            let body = &self.physics.ships()[index];
            if !body.active || ship.vitals.is_dead() {
                continue;
            }
            let pose = body.motion.position;
            let heading = body.motion.rotation_degrees;
            let hull_delta = heading - runtime.last_hull_rotation;
            runtime.last_hull_rotation = heading;
            for (slot, turret) in runtime.turrets.iter_mut().enumerate() {
                let Some(mount) = turret else { continue };
                let position = mount.position(pose, heading);
                mount.state.apply_hull_turn(hull_delta);
                mount.state.aim_fixed_step(position, dt, runtime.cease_fire);
                if let Some(due) = mount.timer.begin_update(dt) {
                    let may_fire = mount.state.is_aligned_with_target;
                    if mount.state.targeting_timer_pass(
                        may_fire, runtime.cease_fire,
                    ) {
                        self.policy_pending_shots.push(PendingPolicyShot {
                            ship_id,
                            slot: slot as u8,
                            shooter_side: ship.side,
                            shooter_is_fire_barge: ship.ship_type == 10,
                            turret_position: position,
                            target_world: mount.state.target_world,
                            spec: mount.spec,
                        });
                    }
                    mount.timer.finish_update(due);
                }
            }
        }

        // All currently available assets and numeric fields were validated
        // at mount registration. A failure from the real projectile subsystem
        // propagates as a world-step failure; it is never a fake shot.
        for index in 0..self.policy_pending_shots.len() {
            let shot = self.policy_pending_shots[index];
            let projectile_id = self.next_entity_id;
            if projectile_id == i64::MAX {
                return Err(CombatWorldError::EntityIdExhausted);
            }
            let asset = self.projectile_assets.by_type(shot.spec.projectile_type)
                .ok_or(CombatWorldError::InvalidProjectileType(
                    shot.spec.projectile_type,
                ))?.clone();
            // Unity Utilities.AngleBetweenPoints(start, target) =
            // atan2(start.x - target.x, start.y - target.y). This angle
            // is NOT the normal ship heading: Projectile.SetMovement
            // negates the sine and cosine for its velocity.
            let angle = (shot.turret_position.x - shot.target_world.x)
                .atan2(shot.turret_position.y - shot.target_world.y);
            let motion = ProjectileMotion::new(
                shot.turret_position,
                shot.spec.projectile_speed,
                angle,
                shot.spec.range,
                projectile_id, // original RL turret shot, not a continuation
                0, // RL agent has no Hive Mind command outcome
            );
            self.spawn_projectile(
                &asset, projectile_id,
                ShotOwner {
                    shooter_id: shot.ship_id,
                    shooter_side: shot.shooter_side,
                    shooter_is_fire_barge: shot.shooter_is_fire_barge,
                    self_detonation: false,
                },
                shot.spec.power, motion, -angle.to_degrees(),
            )?;
            self.events.push(CombatEvent::PolicyWeaponFired {
                ship_id: shot.ship_id,
                slot: shot.slot,
                projectile_id,
            });
        }
        self.policy_pending_shots.clear();
        Ok(())
    }

    pub(super) fn apply_policy_turret_requests(
        &mut self, ship_id: i64,
        requests: &[WeaponRequest; 5],
    ) {
        let &index = self.ship_by_id.get(&ship_id)
            .expect("prevalidated live policy ship");
        let motion = &self.physics.ships()[index].motion;
        let runtime = self.policy_ship_runtime.get_mut(&ship_id)
            .expect("prevalidated policy runtime");
        for (slot, request) in requests.iter().enumerate() {
            if let Some(mount) = runtime.turrets[slot].as_mut() {
                let position = mount.position(
                    motion.position, motion.rotation_degrees,
                );
                mount.state.accept_policy_request(
                    *request, position, mount.spec.range,
                );
            }
        }
    }
}
