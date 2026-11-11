//! Authored rotating-turret gameplay, using the canonical CombatWorld.
//!
//! Source: Turret.Create/Setup/TargetingSequence, Turret.Aiming,
//! RlOneVsOneAgent.ApplyWeaponCommand, Level.AddProjectile and Projectile.Setup.
//! Rotating turrets, DualCannon, LaserBuilder/SplitShot and FullShipTurret
//! share this controller. Charged weapons reproduce the event time in their
//! Unity animation clip. BeamCannon owns a single growing beam projectile
//! until it dies; Bomb has a separate delivery lifecycle and is unsupported.
//! Missing prefab and live Level fields are errors, not guessed defaults.

use super::*;
use crate::control::WeaponRequest;
use crate::geometry::{delta_angle, heading_towards, Vec2};
use crate::perception_weapons::{SelfWeapon, TurretObservation};
use crate::perception_entities::ObservedWeapon;
use crate::turret::TurretState;

/// One ordinary shot, or the two exact DualCannon muzzle offsets from its
/// authored Unity weapon instance. Never approximate the two barrels from
/// ship hull geometry.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum NativeFirePattern {
    Single,
    DualCannon {
        left_offset: Vec2,
        right_offset: Vec2,
    },
    /// LaserBuilder.SendProjectile starts the Leaf Cutter Shot Form clip;
    /// LaserBuilderControl.Fire calls ActuallyShoot at the clip event 0.8s.
    LaserBuilder,
    /// Flagship Laser Charging fires at its authored 2.75s event. Policy
    /// control samples the physical heading at launch; game control uses
    /// its aligned target point and can pause the clip while turning.
    FullShipTurret,
    /// BeamCannon.SendProjectile creates one persistent LaserBeam (type 8).
    /// A second firing pass cannot spawn another until its Kill clears the
    /// owner. LaserBeam.ExtendBeam updates collider/position every fixed tick.
    BeamCannon,
}

impl NativeFirePattern {
    fn fire_event_seconds(self) -> Option<f32> {
        match self {
            Self::LaserBuilder => Some(0.8),
            Self::FullShipTurret => Some(2.75),
            _ => None,
        }
    }
}

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
    /// DualCannon uses two HumanSmall muzzles. LaserBuilder and
    /// FullShipTurret delay their shots until their authored animation event.
    pub fire_pattern: NativeFirePattern,
}

/// The real authored child circle, relative to the turret piece origin and rotation.
#[derive(Clone, Copy, Debug)]
pub struct NativeTurretRangeSpec {
    pub offset: Vec2,
    pub radius: f32,
    pub layer: u8,
    pub enabled: bool,
}

impl NativeTurretRangeSpec {
    pub fn valid(self) -> bool {
        self.offset.is_finite() && self.radius.is_finite() && self.radius > 0.0 && self.layer < 32
    }
}

#[derive(Clone, Copy, Debug)]
pub struct NativeWeaponRange {
    pub origin: Vec2,
    pub center: Vec2,
    pub radius: f32,
    pub layer: u8,
    pub enabled: bool,
}

/// Geometric facts, deliberately separate from complete target eligibility.
#[derive(Clone, Copy, Debug)]
pub struct NativeWeaponTargetGeometry {
    pub closest_hull_point: Vec2,
    pub enemy_hull_overlaps_range: bool,
    pub line_obstructed: bool,
}

impl NativeTurretSpec {
    fn valid(self, catalog: &ProjectileAssetCatalog) -> Result<(), CombatWorldError> {
        let valid_pattern = match self.weapon_type {
            2 | 3 | 5 | 7 | 8 =>
                matches!(self.fire_pattern, NativeFirePattern::Single),
            6 => self.projectile_type == 6 &&
                matches!(self.fire_pattern, NativeFirePattern::DualCannon { .. }),
            9 => self.projectile_type == 9 &&
                matches!(self.fire_pattern, NativeFirePattern::LaserBuilder),
            4 => self.projectile_type == 4 &&
                matches!(self.fire_pattern, NativeFirePattern::FullShipTurret),
            1 => self.projectile_type == 8 &&
                matches!(self.fire_pattern, NativeFirePattern::BeamCannon),
            _ => false,
        };
        let supported_projectile = matches!(
            self.projectile_type, 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11
        );
        if !valid_pattern || !supported_projectile ||
            (self.weapon_type == 5) != (self.projectile_type == 5) {
            return Err(CombatWorldError::UnsupportedPolicyTurret {
                weapon_type: self.weapon_type,
                projectile_type: self.projectile_type,
            });
        }
        // Projectile.Range is an int in Unity, and LaserBeam.Setup uses
        // integer Range / 2 before widening its collider. An odd authored
        // range must not silently become a fractional half range.
        if matches!(self.fire_pattern, NativeFirePattern::BeamCannon) &&
            (self.range.fract() != 0.0 || self.range >= i32::MAX as f32) {
            return Err(CombatWorldError::InvalidPolicyTurret);
        }
        if let NativeFirePattern::DualCannon {
            left_offset, right_offset,
        } = self.fire_pattern {
            if !left_offset.is_finite() || !right_offset.is_finite() {
                return Err(CombatWorldError::InvalidPolicyTurret);
            }
        }
        if !self.mount_offset.is_finite() ||
            !self.initial_rotation_degrees.is_finite() ||
            !self.rotation_rate.is_finite() || self.rotation_rate < 0.0 ||
            !self.range.is_finite() || self.range <= 0.0 ||
            self.power < 0 ||
            !self.rate_of_fire.is_finite() || self.rate_of_fire <= 0.0 ||
            !self.projectile_value.is_finite() || self.projectile_value < 0.0 ||
            !self.projectile_speed.is_finite() ||
            (matches!(self.fire_pattern, NativeFirePattern::BeamCannon)
                && self.projectile_speed != 0.0) ||
            (!matches!(self.fire_pattern, NativeFirePattern::BeamCannon)
                && self.projectile_speed <= 0.0) {
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
        if matches!(self.fire_pattern, NativeFirePattern::BeamCannon) &&
            !matches!(&asset.shape, Some(ShipShape::Box {
                offset, half_extents,
            }) if *offset == Vec2::ZERO &&
                *half_extents == Vec2::new(1.0, 0.5))
        {
            // LaserBeam.ClearData sets localScale (1, 0.5), and its
            // authored prefab has a 2x2 BoxCollider2D. Extending any
            // incompatible shape would silently break hit detection.
            return Err(CombatWorldError::InvalidPolicyTurret);
        }
        if asset.shot_speed.is_some_and(|known|
            (known - self.projectile_speed).abs() > 0.00001) {
            return Err(CombatWorldError::InvalidPolicyTurret);
        }
        Ok(())
    }
}

#[derive(Clone, Debug)]
pub(super) struct MountedTurret {
    pub(super) spec: NativeTurretSpec,
    range_collider: Option<NativeTurretRangeSpec>,
    pub(super) aim_at_front_of_ship: Option<bool>,
    pub(super) state: TurretState,
    pub(super) timer: ScaledTimer,
    /// Active authored charging clip, shared by game and policy control.
    /// A policy targeting pass cannot restart an already queued clip.
    pub(super) charge_elapsed_seconds: Option<f32>,
    /// Source FullShipTurret pauses Animator.speed while unaligned. Cease-fire
    /// skips Aim and therefore retains the last animation speed until its event.
    game_charge_paused: bool,
    /// Unity BeamCannon.IsFiringLaserBeam is set on its single active beam.
    /// Cleared by the canonical CombatWorld::end_projectile on any kill path.
    pub(super) active_beam_projectile_id: Option<i64>,
    pub(super) manual_target: Option<Vec2>,
    pub(super) automatic: Option<Box<super::automatic_weapons::AutomaticTurret>>,
}

/// Physical weapon state belongs to a ship, independently of the policy
/// controller that supplies its current requests. Beam teardown and mounted
/// rotation read this same owner through every projectile lifecycle path.
#[derive(Clone, Debug)]
pub(super) struct ShipWeapons {
    pub(super) turrets: [Option<MountedTurret>; 5],
    pub(super) last_hull_rotation: f32,
    pub(super) cease_fire: bool,
    pub(super) range_contacts: [super::weapon_ranges::WeaponRangeContacts; 5],
}

impl ShipWeapons {
    pub(super) fn new(rotation: f32) -> Self {
        Self { turrets: std::array::from_fn(|_| None), last_hull_rotation: rotation, cease_fire: false,
            range_contacts: std::array::from_fn(|_| super::weapon_ranges::WeaponRangeContacts::default()) }
    }
}

impl MountedTurret {
    pub(super) fn automatic_state(&self) -> &super::automatic_weapons::AutomaticTurret {
        self.automatic.as_deref().expect("initialized authored automatic weapon")
    }
    pub(super) fn automatic_state_mut(&mut self) -> &mut super::automatic_weapons::AutomaticTurret {
        self.automatic.as_deref_mut().expect("initialized authored automatic weapon")
    }

    fn new(spec: NativeTurretSpec) -> Self {
        Self {
            state: TurretState::new(
                spec.initial_rotation_degrees, spec.rotation_rate,
            ),
            timer: ScaledTimer::new(spec.rate_of_fire / 3.0, true, false),
            charge_elapsed_seconds: None,
            game_charge_paused: false,
            active_beam_projectile_id: None,
            manual_target: None, automatic: None,
            range_collider: None, aim_at_front_of_ship: None,
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

    pub(super) fn world_range(&self, position: Vec2, hull_rotation: f32, last_hull_rotation: f32)
        -> Result<NativeWeaponRange, CombatWorldError>
    {
        let range = self.range_collider.ok_or(CombatWorldError::MissingWeaponRangeGeometry)?;
        let origin = self.position(position, hull_rotation);
        let rotation = self.state.rotation_degrees + delta_angle(last_hull_rotation, hull_rotation);
        let center = Pose { position: origin, rotation_degrees: rotation }.transform_point(range.offset);
        Ok(NativeWeaponRange { origin, center, radius: range.radius, layer: range.layer, enabled: range.enabled })
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

/// Exact Utilities.RotatePointAroundPoint(ship, muzzle, turret rotation).
/// Rotation is around Ship.GetPosition(), not the turret or muzzle center.
fn rotate_around_ship(pivot: Vec2, muzzle: Vec2, degrees: f32) -> Vec2 {
    let delta = muzzle - pivot;
    let (sin, cos) = degrees.to_radians().sin_cos();
    pivot + Vec2::new(delta.x * cos - delta.y * sin,
                      delta.x * sin + delta.y * cos)
}

/// Read-only world diagnostics for an actual active animation clip.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeWeaponCharge {
    pub elapsed_seconds: f32,
    pub event_seconds: f32,
    pub paused: bool,
}

/// A scheduled shot is snapshot before mutating projectile storage. This
/// permits the turret timer to reuse its per-ship state while all projectiles
/// are spawned in deterministic ship-ID and exact weapon-slot order.
#[derive(Clone, Copy, Debug)]
pub(super) struct PendingTurretShot {
    ship_id: i64,
    slot: u8,
    shooter_side: i32,
    shooter_is_fire_barge: bool,
    turret_position: Vec2,
    ship_position: Vec2,
    turret_rotation: f32,
    target_world: Vec2,
    automatic_targeted: bool,
    projectile_base_id: i64,
    spec: NativeTurretSpec,
    policy_controlled: bool,
    command_outcome_id: i64,
}

impl CombatWorld {
    /// Actual child trigger, in turret coordinates. Nominal weapon range is
    /// insufficient (Frigate, for example, shortens this collider by seven).
    pub fn configure_turret_range(&mut self, ship_id: i64, slot: usize,
        range: NativeTurretRangeSpec,
    ) -> Result<(), CombatWorldError> {
        if !range.valid() { return Err(CombatWorldError::InvalidWeaponRangeGeometry); }
        let mount = self.ship_weapons.get_mut(&ship_id)
            .and_then(|weapons| weapons.turrets.get_mut(slot))
            .and_then(Option::as_mut)
            .ok_or(CombatWorldError::InvalidWeaponRangeGeometry)?;
        mount.range_collider = Some(range);
        Ok(())
    }

    /// Read-only geometry for both presentation and future automatic targeting.
    /// Legacy scenes may fire manually but must not invent a range collider.
    pub fn weapon_range(&self, ship_id: i64, slot: usize)
        -> Result<Option<NativeWeaponRange>, CombatWorldError>
    {
        if slot >= 5 { return Err(CombatWorldError::InvalidWeaponRangeGeometry); }
        let body = self.ship_body(ship_id).filter(|body| body.active)
            .ok_or(CombatWorldError::UnknownShip)?;
        let Some(mount) = self.ship_weapons.get(&ship_id)
            .and_then(|weapons| weapons.turrets[slot].as_ref()) else { return Ok(None) };
        Ok(Some(mount.world_range(body.motion.position, body.motion.rotation_degrees,
            self.ship_weapons[&ship_id].last_hull_rotation)?))
    }

    /// Current range overlap and primary-obstacle line of fire only. This is
    /// NOT IsShipValidTarget: source trigger history, latched IsInBounds,
    /// strategy ranking and front-of-ship aiming belong to the target controller.
    pub fn query_weapon_target_geometry(&self, ship_id: i64, slot: usize,
        target_id: i64, collisions: &CollisionMatrix, has_obstacles: bool,
    ) -> Result<Option<NativeWeaponTargetGeometry>, CombatWorldError> {
        let Some(range) = self.weapon_range(ship_id, slot)? else { return Ok(None) };
        let own = self.ship(ship_id).ok_or(CombatWorldError::UnknownShip)?;
        let target = self.ship(target_id).ok_or(CombatWorldError::UnknownShip)?;
        let body = self.ship_body(target_id).ok_or(CombatWorldError::UnknownShip)?;
        let closest_hull_point = body.primary_closest_point_readonly(range.origin);
        let nearest_range_point = body.primary_closest_point_readonly(range.center);
        let enemy_hull_overlaps_range = range.enabled && body.active &&
            !own.vitals.is_dead() && !target.vitals.is_dead() && own.side != target.side &&
            collisions.allows(range.layer, body.layer) &&
            (nearest_range_point - range.center).length_squared() <= range.radius * range.radius;
        // ConfigData.ObstaclesLayerMask, independent of the contact matrix.
        let line_obstructed = has_obstacles && self.physics.primary_obstacle_linecast(
            range.origin, closest_hull_point, 1u32 << 19)?;
        Ok(Some(NativeWeaponTargetGeometry { closest_hull_point,
            enemy_hull_overlaps_range, line_obstructed }))
    }

    /// Register real game mounts independently of a policy controller.
    pub fn configure_game_turret(&mut self, ship_id: i64, slot: usize,
        spec: NativeTurretSpec,
    ) -> Result<(), CombatWorldError> {
        if slot >= 5 { return Err(CombatWorldError::InvalidPolicyTurret); }
        spec.valid(&self.projectile_assets)?;
        if self.policy_ship_runtime.contains_key(&ship_id) {
            return Err(CombatWorldError::InvalidPolicyTurret);
        }
        let body = self.ship_body(ship_id).filter(|body| body.active)
            .ok_or(CombatWorldError::UnknownShip)?;
        let rotation = body.motion.rotation_degrees;
        let weapons = self.ship_weapons.entry(ship_id).or_insert_with(|| ShipWeapons::new(rotation));
        if weapons.turrets[slot].is_some() {
            return Err(CombatWorldError::DuplicatePolicyTurret { ship_id, slot });
        }
        weapons.turrets[slot] = Some(MountedTurret::new(spec));
        Ok(())
    }

    pub fn validate_game_manual_fire(&self, ship_id: i64, target: Option<Vec2>)
        -> Result<(), CombatWorldError>
    {
        if target.is_some_and(|point| !point.is_finite()) ||
            self.policy_ship_runtime.contains_key(&ship_id) {
            return Err(CombatWorldError::InvalidPolicyTurret);
        }
        self.ship_body(ship_id).filter(|body| body.active)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ship(ship_id).is_none_or(|ship| ship.vitals.is_dead()) {
            return Err(CombatWorldError::UnknownShip);
        }
        Ok(())
    }

    /// None releases manual control without resetting the shared targeting clock.
    pub fn set_game_manual_fire(&mut self, ship_id: i64, target: Option<Vec2>)
        -> Result<usize, CombatWorldError>
    {
        self.validate_game_manual_fire(ship_id, target)?;
        let Some(weapons) = self.ship_weapons.get_mut(&ship_id) else { return Ok(0) };
        let mut count = 0;
        for mount in weapons.turrets.iter_mut().flatten() {
            mount.manual_target = target;
            count += 1;
        }
        Ok(count)
    }

    /// Borrow the actual firing flag shared by ordinary and policy ships.
    /// Rendering and player UI never maintain a second cease-fire state.
    pub fn ship_cease_fire(&self, ship_id: i64) -> Option<bool> {
        self.ship_weapons.get(&ship_id).map(|weapons| weapons.cease_fire)
    }

    pub fn set_ship_cease_fire(&mut self, ship_id: i64, cease_fire: bool)
        -> Result<(), CombatWorldError>
    {
        let rotation = self.ship_body(ship_id).filter(|body| body.active)
            .ok_or(CombatWorldError::UnknownShip)?.motion.rotation_degrees;
        self.ship_weapons.entry(ship_id).or_insert_with(|| ShipWeapons::new(rotation))
            .cease_fire = cease_fire;
        Ok(())
    }
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
        let runtime = self.ship_weapons.get_mut(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if runtime.turrets[slot].is_some() {
            return Err(CombatWorldError::DuplicatePolicyTurret { ship_id, slot });
        }
        let radians = spec.initial_rotation_degrees.to_radians();
        self.policy_ship_runtime.get_mut(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?.control.bind_turret_aim(
            slot, Vec2::new(-radians.sin(), radians.cos()),
        );
        runtime.turrets[slot] = Some(MountedTurret::new(spec));
        Ok(())
    }

    /// The actual game's cease-fire state must be forwarded on change by
    /// the Level adapter. It gates Aim and Fire independently of action
    /// masks, just as Unity does.
    pub fn set_policy_ship_cease_fire(
        &mut self, ship_id: i64, cease_fire: bool,
    ) -> Result<(), CombatWorldError> {
        let runtime = self.ship_weapons.get_mut(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        runtime.cease_fire = cease_fire;
        Ok(())
    }

    /// Read the same active charging state used by gameplay. A renderer
    /// can animate the real Leafcutter/flagship charge without owning a
    /// second timer; a headless runner reads precisely the same state.
    pub fn weapon_charge(
        &self, ship_id: i64, slot: usize,
    ) -> Result<Option<NativeWeaponCharge>, CombatWorldError> {
        if slot >= 5 {
            return Err(CombatWorldError::InvalidPolicyTurret);
        }
        let runtime = self.ship_weapons.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        Ok(runtime.turrets[slot].as_ref().and_then(|mount| {
            mount.charge_elapsed_seconds.and_then(|elapsed|
                mount.spec.fire_pattern.fire_event_seconds()
                    .map(|event| NativeWeaponCharge {
                        elapsed_seconds: elapsed, event_seconds: event,
                        paused: mount.game_charge_paused,
                    }))
        }))
    }

    pub fn policy_self_weapons(
        &self, ship_id: i64,
    ) -> Result<[Option<SelfWeapon>; 5], CombatWorldError> {
        let runtime = self.ship_weapons.get(&ship_id)
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
        let runtime = self.ship_weapons.get(&ship_id)?;
        Some(std::array::from_fn(|slot| {
            runtime.turrets[slot].as_ref().map(|mount|
                mount.observed_weapon()
            )
        }))
    }

    /// Advance mounted rotating weapons with the exact turret targeting
    /// passes and shared ScaledTimer, using the post-hull-turn ship pose.
    /// Called from the canonical gameplay step BEFORE projectile physics.
    pub(super) fn advance_mounted_turrets(&mut self, dt: f32,
        command: Option<&dyn Fn(i64) -> Option<i64>>, random: Option<&mut RandomStream>,
        context: Option<&dyn NativeAutomaticWeaponContext>,
    ) -> Result<(), CombatWorldError> {
        self.pending_turret_shots.clear();
        let mut weapons = std::mem::take(&mut self.ship_weapons);
        let result = self.advance_mounted_weapon_controls(&mut weapons, dt, command, random, context);
        // Restore physical ownership even when an authored input fails.
        self.ship_weapons = weapons;
        result?;

        // Reserve enough monotonic entity IDs for complete volleys before
        // creating any projectiles. DualCannon must not spawn only its first
        // barrel if the shared ID space is exhausted.
        let shot_count: usize = self.pending_turret_shots.iter()
            .map(|shot| match shot.spec.fire_pattern {
                NativeFirePattern::DualCannon { .. } => 2,
                _ => 1,
            }).sum();
        let count_i64 = i64::try_from(shot_count)
            .map_err(|_| CombatWorldError::EntityIdExhausted)?;
        self.next_entity_id.checked_add(count_i64)
            .ok_or(CombatWorldError::EntityIdExhausted)?;

        // Every projectile uses the SAME catalog, entity allocator and
        // collision/damage path as ordinary native gameplay.
        let catalog = Arc::clone(&self.projectile_assets);
        for index in 0..self.pending_turret_shots.len() {
            let shot = self.pending_turret_shots[index];
            let asset = catalog.by_type(shot.spec.projectile_type)
                .ok_or(CombatWorldError::InvalidProjectileType(
                    shot.spec.projectile_type,
                ))?;
            // Unity Utilities.AngleBetweenPoints(start,target) =
            // atan2(start.x-target.x, start.y-target.y).
            // DualCannon.SendProjectile uses Ship.AngleToPoint(TargetPoint)
            // for BOTH muzzles; ordinary Turret uses GetPosition().
            let (positions, barrels, origin, power) =
                match shot.spec.fire_pattern {
                    NativeFirePattern::Single |
                    NativeFirePattern::LaserBuilder |
                    NativeFirePattern::FullShipTurret |
                    NativeFirePattern::BeamCannon => (
                        [shot.turret_position, Vec2::ZERO],
                        1usize,
                        shot.turret_position,
                        shot.spec.power,
                    ),
                    NativeFirePattern::DualCannon {
                        left_offset, right_offset,
                    } => (
                        [
                            rotate_around_ship(
                                shot.ship_position,
                                shot.turret_position + left_offset,
                                shot.turret_rotation,
                            ),
                            rotate_around_ship(
                                shot.ship_position,
                                shot.turret_position + right_offset,
                                shot.turret_rotation,
                            ),
                        ],
                        2usize,
                        shot.ship_position,
                        shot.spec.power / 2,
                    ),
                };
            let angle = (origin.x - shot.target_world.x)
                .atan2(origin.y - shot.target_world.y);
            debug_assert_eq!(self.next_entity_id, shot.projectile_base_id);
            for &position in positions[..barrels].iter() {
                let projectile_id = self.next_entity_id;
                let motion = ProjectileMotion::new(
                    position,
                    shot.spec.projectile_speed,
                    angle,
                    shot.spec.range,
                    projectile_id, // canonical projectile root, tracked only for policy shots
                    shot.command_outcome_id,
                );
                self.spawn_projectile(
                    asset, projectile_id,
                    ShotOwner {
                        shooter_id: shot.ship_id,
                        shooter_side: shot.shooter_side,
                        shooter_is_fire_barge: shot.shooter_is_fire_barge,
                        self_detonation: false,
                    },
                    power, motion, -angle.to_degrees(),
                )?;
                let projectile_index = self.projectile_by_id[&projectile_id];
                self.projectiles[projectile_index].source_turret_slot = Some(shot.slot);
                if shot.automatic_targeted { self.automatic_weapon_measurements.shots += 1; }
                // Source DualCannon increments FleetShip.ShotsFired twice.
                // Emit once per actual shot so reward and telemetry counts
                // match the physical projectiles, not timer passes.
                if matches!(shot.spec.fire_pattern, NativeFirePattern::BeamCannon) {
                    let runtime = self.ship_weapons.get_mut(&shot.ship_id)
                        .expect("prevalidated live beam cannon");
                    let mount = runtime.turrets[usize::from(shot.slot)].as_mut()
                        .expect("prevalidated beam cannon mount");
                    mount.active_beam_projectile_id = Some(projectile_id);
                }
                if shot.policy_controlled { self.track_policy_projectile_root(projectile_id)?; }
                self.events.push(if shot.policy_controlled { CombatEvent::PolicyWeaponFired {
                    ship_id: shot.ship_id,
                    slot: shot.slot,
                    projectile_id,
                } } else { CombatEvent::GameWeaponFired {
                    ship_id: shot.ship_id, slot: shot.slot, projectile_id,
                } });
            }
        }
        self.pending_turret_shots.clear();
        Ok(())
    }

    fn advance_mounted_weapon_controls(&mut self, weapons: &mut BTreeMap<i64, ShipWeapons>,
        dt: f32, command: Option<&dyn Fn(i64) -> Option<i64>>, mut random: Option<&mut RandomStream>,
        context: Option<&dyn NativeAutomaticWeaponContext>,
    ) -> Result<(), CombatWorldError> {
        let mut next_scheduled_id = self.next_entity_id;
        for (&ship_id, runtime) in weapons {
            let policy_controlled = self.policy_ship_runtime.contains_key(&ship_id);
            let Some(&index) = self.ship_by_id.get(&ship_id) else { continue };
            let ship = self.ships[index];
            let body = &self.physics.ships()[index];
            if !body.active || ship.vitals.is_dead() {
                continue;
            }
            let pose = body.motion.position;
            let initial_heading = body.motion.rotation_degrees;
            if runtime.turrets.iter().all(Option::is_none) {
                runtime.last_hull_rotation = initial_heading;
                continue;
            }
            let moving = body.motion.is_moving;
            let body_user_controlled = body.motion.user_controlled;
            let mut heading = initial_heading;
            let mut full_alignment = [false; 5];
            let automatic = !policy_controlled && self.weapon_rank_history.frame.is_some();
            let inputs = if automatic {
                let value = context.ok_or(CombatWorldError::MissingAutomaticTargetingInputs)?.inputs(ship_id)?;
                if !(1..=40).contains(&value.strategy_id) { return Err(CombatWorldError::InvalidAutomaticTargeting); }
                Some(value)
            } else { None };
            let mut automatic_points = [None; 5];
            let mut aiming_at_asteroid = [false; 5];
            let mut has_valid_targets = [false; 5];
            if let Some(inputs) = inputs {
                for (slot, mount) in runtime.turrets.iter_mut().enumerate() {
                    let Some(mount) = mount else { continue };
                    mount.automatic.get_or_insert_with(|| Box::new(Default::default())).command_changed(inputs.command_key);
                    if runtime.cease_fire || mount.manual_target.is_some() { continue; }
                    let range = mount.world_range(pose, initial_heading, runtime.last_hull_rotation)?;
                    automatic_points[slot] = self.automatic_aim_point(ship_id, mount, &runtime.range_contacts[slot], range)?;
                    aiming_at_asteroid[slot] = automatic_points[slot].is_some() &&
                        mount.automatic_state().target.is_none();
                    if automatic_points[slot].is_none() {
                        for &target in runtime.range_contacts[slot].ids() {
                            if self.automatic_target_valid(ship_id, &runtime.range_contacts[slot], range, target)? {
                                has_valid_targets[slot] = true;
                                break;
                            }
                        }
                    }
                }
            }
            if !policy_controlled && !runtime.cease_fire {
                for (slot, mount) in runtime.turrets.iter().enumerate() {
                    let Some(mount) = mount else { continue };
                    if !matches!(mount.spec.fire_pattern, NativeFirePattern::FullShipTurret) {
                        continue;
                    }
                    let Some(target) = mount.manual_target.or(automatic_points[slot]) else { continue };
                    let position = mount.position(pose, heading);
                    let desired = heading_towards(target - position);
                    if moving {
                        let rotation = mount.state.rotation_degrees + initial_heading - runtime.last_hull_rotation;
                        full_alignment[slot] = delta_angle(rotation, desired).abs() <= 3.0;
                    } else {
                        let (next, aligned) = crate::geometry::timed_turret_rotation(
                            heading, desired, mount.spec.rotation_rate, dt);
                        heading = next;
                        full_alignment[slot] = aligned;
                    }
                }
                if heading != initial_heading {
                    self.physics.ships_mut()[index].motion.rotation_degrees = heading;
                }
            }
            let hull_delta = heading - runtime.last_hull_rotation;
            runtime.last_hull_rotation = heading;
            for (slot, turret) in runtime.turrets.iter_mut().enumerate() {
                let Some(mount) = turret else { continue };
                let position = mount.position(pose, heading);
                mount.state.apply_hull_turn(hull_delta);
                if !policy_controlled && !runtime.cease_fire {
                    match mount.spec.fire_pattern {
                        NativeFirePattern::LaserBuilder => {
                            if let Some(target) = mount.manual_target.or(automatic_points[slot]) {
                                mount.state.target_world = target;
                                mount.state.is_aligned_with_target = true;
                                // Source automatic Leaf clips start in SetTargetShip,
                                // not from Aim after a previous clip has completed.
                                if mount.manual_target.is_some() || aiming_at_asteroid[slot] {
                                    mount.charge_elapsed_seconds.get_or_insert(0.0);
                                }
                            } else {
                                mount.state.is_aligned_with_target = false;
                                // LaserBuilder.Aim deactivates its clip when
                                // there is no manual/automatic target.
                                mount.charge_elapsed_seconds = None;
                            }
                        },
                        NativeFirePattern::FullShipTurret => {
                            if let Some(target) = mount.manual_target.or(automatic_points[slot]) {
                                mount.state.target_world = target;
                            }
                            mount.state.is_aligned_with_target = full_alignment[slot];
                            mount.game_charge_paused = !full_alignment[slot];
                            if full_alignment[slot] {
                                mount.charge_elapsed_seconds.get_or_insert(0.0);
                            }
                        },
                        _ => {
                            if let Some(target) = mount.manual_target.or(automatic_points[slot]) {
                                mount.state.target_world = target;
                                mount.state.aim_at_point(position, dt);
                            } else if !automatic || !has_valid_targets[slot] {
                                let (rotation, _) = crate::geometry::timed_turret_rotation(
                                    mount.state.rotation_degrees, heading, mount.spec.rotation_rate, dt);
                                mount.state.rotation_degrees = rotation;
                                mount.state.is_aligned_with_target = false;
                            } else { mount.state.is_aligned_with_target = false; }
                        },
                    }
                }
                match mount.spec.fire_pattern {
                    NativeFirePattern::LaserBuilder => {
                        // LaserBuilder.Aim in RL mode marks the requested
                        // point aligned without rotating the physical mount.
                        if mount.state.is_rl_controlled && !runtime.cease_fire {
                            mount.state.is_aligned_with_target = true;
                        }
                    }
                    NativeFirePattern::FullShipTurret => {
                        // FullShipTurret.AimRlMainCannon never rotates the
                        // hull or turret; it only reports alignment for
                        // diagnostics. Firing eligibility is unconditional.
                        if mount.state.is_rl_controlled && !runtime.cease_fire {
                            let desired = heading_towards(
                                mount.state.target_world - position,
                            );
                            mount.state.is_aligned_with_target = delta_angle(
                                mount.state.rotation_degrees, desired,
                            ).abs() <= 3.0;
                        }
                    }
                    _ => {
                        mount.state.aim_fixed_step(
                            position, dt, runtime.cease_fire,
                        );
                    }
                }
                let mut launch_now = false;
                let mut target_ship_at_launch = None;
                if let Some(elapsed) = mount.charge_elapsed_seconds {
                    let advancing = policy_controlled || !mount.game_charge_paused;
                    let next = elapsed + if advancing { dt } else { 0.0 };
                    if mount.spec.fire_pattern.fire_event_seconds()
                        .is_some_and(|event| next + 0.000001 >= event) {
                        // LaserBuilderControl.Fire -> ActuallyShoot:
                        // the previously queued request survives later
                        // policy fire=false, but cannot fire if ceased.
                        mount.charge_elapsed_seconds = None;
                        launch_now = !runtime.cease_fire && if policy_controlled {
                            mount.state.is_rl_controlled
                        } else {
                            (mount.manual_target.is_some() || automatic_points[slot].is_some()) && mount.state.is_aligned_with_target
                        };
                    } else {
                        mount.charge_elapsed_seconds = Some(next);
                    }
                }
                if launch_now && automatic && mount.manual_target.is_none() {
                    target_ship_at_launch = mount.automatic_state().target;
                }
                if let Some(due) = mount.timer.begin_update(dt) {
                    let may_fire = match mount.spec.fire_pattern {
                        NativeFirePattern::FullShipTurret => true,
                        _ => mount.state.is_aligned_with_target,
                    };
                    let fire = if policy_controlled {
                        mount.state.targeting_timer_pass(may_fire, runtime.cease_fire)
                    } else if automatic && mount.manual_target.is_none() {
                        let range = mount.world_range(pose, heading, runtime.last_hull_rotation)?;
                        self.automatic_targeting_pass(ship_id, mount, &runtime.range_contacts[slot], range,
                            inputs.expect("authored automatic input"), runtime.cease_fire, body_user_controlled,
                            aiming_at_asteroid[slot],
                            random.as_deref_mut().ok_or(CombatWorldError::MissingAutomaticTargetingInputs)?,
                            context.expect("authored automatic context"))?
                    } else {
                        let attempting = (mount.state.ready_to_fire && mount.state.is_aligned_with_target) ||
                            mount.state.targeting_passes + 1 == 3;
                        let fire = mount.state.manual_targeting_timer_pass(mount.manual_target.is_some(), runtime.cease_fire);
                        if automatic && !attempting { mount.automatic_state_mut().target = None; }
                        fire
                    };
                    if automatic { self.refresh_automatic_asteroid_target(ship_id, mount); }
                    if fire {
                        self.record_combat_activity(ship_id);
                        if automatic && mount.manual_target.is_none() {
                            target_ship_at_launch = mount.automatic_state().target;
                            if let Some(target) = target_ship_at_launch { self.record_combat_activity(target); }
                        }
                        if !policy_controlled && matches!(mount.spec.fire_pattern, NativeFirePattern::FullShipTurret) {
                            // Source game FullShipTurret.SendProjectile ends
                            // the active clip; its animation event fires the gun.
                            mount.charge_elapsed_seconds = None;
                        } else if !policy_controlled && matches!(mount.spec.fire_pattern, NativeFirePattern::LaserBuilder) {
                            // Game LaserBuilder.SendProjectile is deliberately
                            // inert. Aim starts its clip independently of cooldown.
                        } else if mount.spec.fire_pattern.fire_event_seconds().is_some() {
                            // Repeated timer passes during an active
                            // charging clip do not restart the clip.
                            if mount.charge_elapsed_seconds.is_none() {
                                mount.charge_elapsed_seconds = Some(0.0);
                            }
                        } else {
                            launch_now = true;
                        }
                    }
                    mount.timer.finish_update(due);
                }
                if launch_now &&
                    !(matches!(mount.spec.fire_pattern,
                        NativeFirePattern::BeamCannon) &&
                        mount.active_beam_projectile_id.is_some()) {
                    // BeamCannon.SendProjectile does nothing while
                    // IsFiringLaserBeam is true, even when the targeting
                    // timer grants another firing opportunity.
                    let target_world =
                        if policy_controlled && matches!(mount.spec.fire_pattern,
                            NativeFirePattern::FullShipTurret) {
                            // FullShipTurret.GetRlForwardFirePoint samples
                            // its physical rotation at the Fire event,
                            // not the point requested when charging began.
                            let rad = mount.state.rotation_degrees.to_radians();
                            position + Vec2::new(-rad.sin(), rad.cos())
                        } else {
                            mount.state.target_world
                        };
                    let command_outcome_id = if policy_controlled { None }
                        else { command.and_then(|resolve| resolve(ship_id)) };
                    if command_outcome_id.is_some_and(|id| id <= 0) {
                        return Err(CombatWorldError::InvalidCommandAttribution);
                    }
                    let target_ship = if mount.manual_target.is_some() { None } else { target_ship_at_launch };
                    let barrels = if matches!(mount.spec.fire_pattern, NativeFirePattern::DualCannon { .. }) { 2 } else { 1 };
                    let base = next_scheduled_id;
                    next_scheduled_id = next_scheduled_id.checked_add(barrels)
                        .ok_or(CombatWorldError::EntityIdExhausted)?;
                    if let Some(target) = target_ship {
                        let health = self.ship(target).ok_or(CombatWorldError::UnknownShip)?.vitals.health();
                        for barrel in 0..barrels {
                            self.reservations.reserve(base + barrel, ship.side, target, health,
                                mount.spec.power / barrels as i32)?;
                        }
                    }
                    if automatic && mount.manual_target.is_some() { mount.automatic_state_mut().target = None; }
                    self.pending_turret_shots.push(PendingTurretShot {
                        automatic_targeted: target_ship.is_some() || aiming_at_asteroid[slot],
                        projectile_base_id: base,
                        ship_id,
                        slot: slot as u8,
                        shooter_side: ship.side,
                        shooter_is_fire_barge: ship.ship_type == 10,
                        turret_position: position,
                        ship_position: pose,
                        turret_rotation: mount.state.rotation_degrees,
                        target_world,
                        spec: mount.spec,
                        policy_controlled, command_outcome_id: command_outcome_id.unwrap_or(0),
                    });
                }
            }
        }

        Ok(())
    }

    /// Execute LaserBeam.FixedUpdate -> ExtendBeam on the authoritative
    /// collider and projectile body. The source prefab is a 2x2 box scaled
    /// initially to (1, 0.5). Each subsequent FixedUpdate grows local X
    /// scale by 2, keeping Y scale, until its *previous* X scale is at
    /// least Range/2. The beam is centered one half-length ahead of the
    /// shooter and moves with the shooter's Rigidbody2D velocity.
    ///
    /// Contacts are processed earlier in the same CombatWorld tick, matching
    /// Projectile.FixedUpdate's base contact-before-ExtendBeam ordering.
    pub(super) fn advance_beam_projectiles(&mut self)
        -> Result<(), CombatWorldError>
    {
        for index in 0..self.projectiles.len() {
            if !self.physics.projectiles()[index].active {
                continue;
            }
            let ImpactBehavior::Beam {
                length_scale, just_spawned, ..
            } = &self.projectiles[index].behavior else {
                continue;
            };
            // A just-created beam enters the physics step with its prefab's
            // 1x0.5 scale. The FIRST following FixedUpdate expands it.
            if *just_spawned {
                if let ImpactBehavior::Beam {
                    just_spawned, ..
                } = &mut self.projectiles[index].behavior {
                    *just_spawned = false;
                }
                continue;
            }
            let old_length = *length_scale;
            let range = self.physics.projectiles()[index].motion.range;
            let owner_id = self.projectiles[index].owner.shooter_id;
            let beam_id = self.projectiles[index].entity_id;
            let Some(&ship_index) = self.ship_by_id.get(&owner_id) else {
                self.end_projectile(index);
                continue;
            };
            if !self.physics.ships()[ship_index].active ||
                self.ships[ship_index].vitals.is_dead() {
                self.end_projectile(index);
                continue;
            }
            let Some(turret_rotation) =
                self.ship_weapons.get(&owner_id).and_then(|runtime|
                    runtime.turrets.iter().flatten().find_map(|turret|
                        (turret.active_beam_projectile_id == Some(beam_id))
                            .then_some(turret.state.rotation_degrees)))
            else {
                // A beam without an owning real cannon cannot be allowed to
                // survive as a permanently active synthetic collider.
                self.end_projectile(index);
                continue;
            };
            // Unity LaserBeam._halfRange = Range / 2 uses integer
            // division, since Projectile.Range is an int.
            if old_length >= (range as i32 / 2) as f32 {
                self.end_projectile(index);
                continue;
            }
            let new_length = old_length + 2.0;
            let body = &self.physics.projectiles()[index];
            let half_height = match body.shape() {
                Some(ShipShape::Box {
                    offset, half_extents,
                }) if *offset == Vec2::ZERO => half_extents.y,
                _ => return Err(CombatWorldError::Physics(
                    PhysicsError::InvalidCollider,
                )),
            };
            let ship_motion = &self.physics.ships()[ship_index].motion;
            let radians = turret_rotation.to_radians();
            let forward = Vec2::new(-radians.sin(), radians.cos());
            // Source worldAngle = BeamCannon.Rotation + 90, then the
            // local scale.x offset is transformed through Shooter's pose.
            // The resulting center is ship center + forward * scale.x.
            let position = ship_motion.position + forward * new_length;
            let velocity = ship_motion.body_velocity;
            let world_rotation = turret_rotation + 90.0;
            let new_shape = ShipShape::Box {
                offset: Vec2::ZERO,
                half_extents: Vec2::new(new_length, half_height),
            };
            let body = &mut self.physics.projectiles_mut()[index];
            body.replace_shape(Some(new_shape))?;
            body.rotation_degrees = world_rotation;
            body.motion.position = position;
            body.motion.velocity = velocity;
            body.motion.angle_radians = -world_rotation.to_radians();
            if let ImpactBehavior::Beam {
                length_scale, ..
            } = &mut self.projectiles[index].behavior {
                *length_scale = new_length;
            }
        }
        Ok(())
    }

    pub(super) fn apply_policy_turret_requests(
        &mut self, ship_id: i64,
        requests: &[WeaponRequest; 5],
    ) {
        let &index = self.ship_by_id.get(&ship_id)
            .expect("prevalidated live policy ship");
        let motion = &self.physics.ships()[index].motion;
        let runtime = self.ship_weapons.get_mut(&ship_id)
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

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;
    use crate::collision::Aabb;
    use crate::combat::ShipVitals;
    use crate::movement::ShipMotion;
    use crate::policy::{Action, Decision};
    use crate::ship_assets::ShipAssetCatalog;
    use crate::simulation::{World, WorldId};

    #[test]
    fn beam_cannon_grows_with_shooter_and_pierces_before_releasing_owner() {
        let projectiles = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let hulls = ShipAssetCatalog::embedded().unwrap();
        let mut world = CombatWorld::new(
            Aabb {
                min: Vec2::new(-500.0, -500.0),
                max: Vec2::new(500.0, 500.0),
            }, 8.0, projectiles,
        ).unwrap();
        for (id, side, position) in [
            (1, 1, Vec2::ZERO),
            (2, 2, Vec2::new(0.0, 60.0)),
            (3, 2, Vec2::new(0.0, 90.0)),
        ] {
            world.spawn_ship(
                hulls.by_type(6).unwrap(), id, side,
                ShipVitals::new(100, 100, 1000, 0).unwrap(),
                ShipMotion::new(position, 0.0, 0.0, 0.0, 3.0),
            ).unwrap();
        }
        world.bind_policy_ship(1, PolicyFrame::new(0), false).unwrap();
        world.configure_policy_turret(1, 0, NativeTurretSpec {
            weapon_type: 1,
            projectile_type: 8,
            mount_offset: Vec2::ZERO,
            initial_rotation_degrees: 0.0,
            rotation_rate: 90.0,
            range: 400.0,
            power: 60,
            rate_of_fire: 0.3,
            projectile_value: 1.0,
            projectile_speed: 0.0, // actual Laser Beam prefab Speed: 0
            fire_pattern: NativeFirePattern::BeamCannon,
        }).unwrap();

        let mut action = Action::ZERO;
        action.continuous[3] = 1.0; // aim +Y
        action.discrete[0] = 1;
        world.apply_policy_decisions(&[Decision { agent_id: 1, action }])
            .unwrap();
        let mut shot_id = None;
        for _ in 0..16 {
            world.advance_mounted_turrets(0.02, None, None, None).unwrap();
            world.advance_beam_projectiles().unwrap();
            shot_id = world.events().iter().find_map(|event| match event {
                CombatEvent::PolicyWeaponFired {
                    ship_id: 1, slot: 0, projectile_id,
                } => Some(*projectile_id),
                _ => None,
            });
            if shot_id.is_some() { break; }
        }
        let id = shot_id.expect("beam cannon fires after three timer passes");
        let index = *world.projectile_by_id.get(&id).unwrap();
        assert_eq!(world.projectiles()[index].projectile_type, 8);
        assert_eq!(world.physics().projectiles()[index].motion.velocity, Vec2::ZERO);
        assert_eq!(world.ship_weapons.get(&1).unwrap().turrets[0]
            .as_ref().unwrap().active_beam_projectile_id, Some(id));
        // The first simulation frame after spawn uses the prefab's
        // initial 1x0.5 box; subsequent frames extend X by two.
        assert!(matches!(world.physics().projectiles()[index].shape(),
            Some(ShipShape::Box { half_extents, .. })
                if *half_extents == Vec2::new(1.0, 0.5)));
        world.advance_beam_projectiles().unwrap();
        let body = &world.physics().projectiles()[index];
        assert!(matches!(body.shape(), Some(ShipShape::Box {
            half_extents, ..
        }) if *half_extents == Vec2::new(3.0, 0.5)));
        assert!((body.motion.position.x).abs() < 0.001);
        assert!((body.motion.position.y - 3.0).abs() < 0.001);
        assert!((body.rotation_degrees - 90.0).abs() < 0.001);

        world.physics.ships_mut()[0].motion.position = Vec2::new(3.0, 2.0);
        world.physics.ships_mut()[0].motion.body_velocity = Vec2::new(2.0, 1.0);
        world.physics.ships_mut()[0].motion.rotation_degrees = 45.0;
        world.advance_mounted_turrets(0.02, None, None, None).unwrap();
        world.advance_beam_projectiles().unwrap();
        let rotation = world.ship_weapons.get(&1).unwrap().turrets[0]
            .as_ref().unwrap().state.rotation_degrees.to_radians();
        let expected_center = Vec2::new(3.0, 2.0) +
            Vec2::new(-rotation.sin(), rotation.cos()) * 5.0;
        let body = &world.physics().projectiles()[index];
        assert!((body.motion.position.x - expected_center.x).abs() < 0.001);
        assert!((body.motion.position.y - expected_center.y).abs() < 0.001);
        assert_eq!(body.motion.velocity, Vec2::new(2.0, 1.0));

        // A second policy fire request cannot launch another projectile
        // until LaserBeam.Kill releases BeamCannon.IsFiringLaserBeam.
        for _ in 0..20 {
            world.advance_mounted_turrets(0.02, None, None, None).unwrap();
        }
        assert_eq!(world.events().iter().filter(|event|
            matches!(event, CombatEvent::PolicyWeaponFired { .. })
        ).count(), 1);

        // LaserBeam.ContactTarget consumes half of the *target's health*
        // as piercing budget. Each unique target receives the full power
        // available at its contact, and repeat contacts do not damage again.
        world.resolve_contact(TriggerEnter { projectile_id: id, ship_id: 2 })
            .unwrap();
        assert_eq!(world.ship(2).unwrap().vitals.health(), 40);
        assert_eq!(world.projectiles()[index].power, 10);
        world.resolve_contact(TriggerEnter { projectile_id: id, ship_id: 2 })
            .unwrap();
        assert_eq!(world.ship(2).unwrap().vitals.health(), 40);
        world.resolve_contact(TriggerEnter { projectile_id: id, ship_id: 3 })
            .unwrap();
        assert_eq!(world.ship(3).unwrap().vitals.health(), 90);
        assert!(!world.physics().projectiles()[index].active);
        assert_eq!(world.ship_weapons.get(&1).unwrap().turrets[0]
            .as_ref().unwrap().active_beam_projectile_id, None);
    }

    #[test]
    fn leafcutter_charge_queues_then_fires_only_at_the_authored_animation_event() {
        let projectiles = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let hulls = ShipAssetCatalog::embedded().unwrap();
        let mut world = CombatWorld::new(
            Aabb {
                min: Vec2::new(-200.0, -200.0),
                max: Vec2::new(200.0, 200.0),
            }, 8.0, projectiles,
        ).unwrap();
        world.spawn_ship(
            hulls.by_type(16).unwrap(), 1, 1,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 3.0),
        ).unwrap();
        world.bind_policy_ship(1, PolicyFrame::new(0), false).unwrap();
        world.configure_policy_turret(1, 0, NativeTurretSpec {
            weapon_type: 9, projectile_type: 9,
            mount_offset: Vec2::ZERO,
            initial_rotation_degrees: 0.0,
            rotation_rate: 90.0,
            range: 80.0, power: 15, rate_of_fire: 0.3,
            projectile_value: 1.0, projectile_speed: 100.0,
            fire_pattern: NativeFirePattern::LaserBuilder,
        }).unwrap();
        let mut action = Action::ZERO;
        action.continuous[3] = 1.0;
        action.discrete[0] = 1;
        world.apply_policy_decisions(&[Decision { agent_id: 1, action }])
            .unwrap();
        // Targeting completes after three source-defined passes, but the
        // real Leaf Cutter Shot Form animation fires only at 0.8 seconds.
        for _ in 0..45 {
            world.advance_mounted_turrets(0.02, None, None, None).unwrap();
        }
        assert_eq!(world.projectile_storage_metrics().active_projectiles, 0);
        let charge = world.weapon_charge(1, 0).unwrap().unwrap();
        let (elapsed, event) = (charge.elapsed_seconds, charge.event_seconds);
        assert!(elapsed > 0.0 && elapsed < event);
        assert_eq!(event, 0.8);
        for _ in 0..30 {
            world.advance_mounted_turrets(0.02, None, None, None).unwrap();
        }
        let fired: Vec<i64> = world.events().iter().filter_map(|event| {
            if let CombatEvent::PolicyWeaponFired {
                ship_id: 1, slot: 0, projectile_id,
            } = event {
                Some(*projectile_id)
            } else { None }
        }).collect();
        assert_eq!(fired.len(), 1);
        let index = *world.projectile_by_id.get(&fired[0]).unwrap();
        assert_eq!(world.projectiles()[index].projectile_type, 9);
        assert_eq!(world.projectiles()[index].power, 15);
        assert_eq!(world.physics().projectiles()[index]
            .motion.attribution.root_shot_id, fired[0]);
    }

    #[test]
    fn flagship_charges_then_uses_its_current_hull_heading_after_policy_reaim() {
        let projectiles = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let hulls = ShipAssetCatalog::embedded().unwrap();
        let mut world = CombatWorld::new(
            Aabb {
                min: Vec2::new(-200.0, -200.0),
                max: Vec2::new(200.0, 200.0),
            }, 8.0, projectiles,
        ).unwrap();
        world.spawn_ship(
            hulls.by_type(11).unwrap(), 1, 1,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 3.0),
        ).unwrap();
        world.bind_policy_ship(1, PolicyFrame::new(0), false).unwrap();
        world.configure_policy_turret(1, 0, NativeTurretSpec {
            weapon_type: 4, projectile_type: 4,
            mount_offset: Vec2::ZERO,
            initial_rotation_degrees: 0.0, rotation_rate: 90.0,
            range: 100.0, power: 30, rate_of_fire: 0.3,
            projectile_value: 1.0, projectile_speed: 100.0,
            fire_pattern: NativeFirePattern::FullShipTurret,
        }).unwrap();
        let mut action = Action::ZERO;
        action.continuous[2] = 1.0; // policy aims +X, cannon initially +Y
        action.discrete[0] = 1;
        world.apply_policy_decisions(&[Decision { agent_id: 1, action }])
            .unwrap();
        for _ in 0..20 {
            world.advance_mounted_turrets(0.02, None, None, None).unwrap();
        }
        assert_eq!(world.projectile_storage_metrics().active_projectiles, 0);
        let charge = world.weapon_charge(1, 0).unwrap().unwrap();
        let (elapsed, event) = (charge.elapsed_seconds, charge.event_seconds);
        assert!(elapsed < event);
        assert_eq!(event, 2.75);

        // The Unity animation event retains the queued shot even after
        // a subsequent policy decision drops fire_requested.
        action.discrete[0] = 0;
        world.apply_policy_decisions(&[Decision { agent_id: 1, action }])
            .unwrap();
        world.physics.ships_mut()[0].motion.rotation_degrees = 90.0;
        for _ in 0..155 {
            world.advance_mounted_turrets(0.02, None, None, None).unwrap();
        }
        let fired: Vec<i64> = world.events().iter().filter_map(|event| {
            if let CombatEvent::PolicyWeaponFired {
                ship_id: 1, slot: 0, projectile_id,
            } = event {
                Some(*projectile_id)
            } else { None }
        }).collect();
        assert_eq!(fired.len(), 1);
        let index = *world.projectile_by_id.get(&fired[0]).unwrap();
        let shot = &world.physics().projectiles()[index];
        assert_eq!(world.projectiles()[index].projectile_type, 4);
        assert_eq!(world.projectiles()[index].power, 30);
        assert!(shot.motion.velocity.x < -99.0);
        assert!(shot.motion.velocity.y.abs() < 0.001);
        assert_eq!(shot.motion.attribution.root_shot_id, fired[0]);
    }

    #[test]
    fn dual_cannon_launches_two_half_power_shots_from_rotated_ship_pivot_muzzles() {
        let projectiles = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let hulls = ShipAssetCatalog::embedded().unwrap();
        let mut combat = CombatWorld::new(
            Aabb {
                min: Vec2::new(-200.0, -200.0),
                max: Vec2::new(200.0, 200.0),
            },
            8.0, Arc::clone(&projectiles),
        ).unwrap();
        let ship_position = Vec2::new(5.0, 8.0);
        combat.spawn_ship(
            hulls.by_type(13).unwrap(), 1, 1,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(ship_position, 90.0, 0.0, 0.0, 3.0),
        ).unwrap();
        combat.spawn_ship(
            hulls.by_type(21).unwrap(), 2, 2,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::new(-35.0, 8.0), 0.0, 0.0, 0.0, 3.0),
        ).unwrap();
        combat.bind_policy_ship(1, PolicyFrame::new(0), false).unwrap();
        let spec = NativeTurretSpec {
            weapon_type: 6,
            projectile_type: 6, // HumanSmall; never the generic turret type
            mount_offset: Vec2::ZERO,
            initial_rotation_degrees: 90.0,
            rotation_rate: 90.0,
            range: 80.0,
            power: 21, // C# 21 / 2 = 10 per shot, not 10.5 or 11
            rate_of_fire: 0.3,
            projectile_value: 1.0,
            projectile_speed: 100.0,
            fire_pattern: NativeFirePattern::DualCannon {
                left_offset: Vec2::new(-1.1, 0.5),
                right_offset: Vec2::new(1.1, 0.5),
            },
        };
        combat.configure_policy_turret(1, 0, spec).unwrap();
        assert_eq!(combat.policy_action_mask(1).unwrap().fire[0], true);
        assert_eq!(combat.policy_self_weapons(1).unwrap()[0]
            .unwrap().weapon_type, 6);
        let invalid_pattern = NativeTurretSpec {
            fire_pattern: NativeFirePattern::Single,
            ..spec
        };
        assert!(matches!(
            combat.configure_policy_turret(1, 1, invalid_pattern),
            Err(CombatWorldError::UnsupportedPolicyTurret { .. }),
        ));

        let mut action = Action::ZERO;
        action.continuous[2] = -1.0; // aim physical -X, aligned at 90°
        action.discrete[0] = 1;
        let decision = Decision { agent_id: 1, action };
        let rules = CombatWorldRules {
            collision_matrix: CollisionMatrix::from_rows([0; 32]),
        };
        let mut world = World::new(WorldId(44), 9, combat);
        let mut fired = Vec::new();
        for tick in 0..30 {
            let decisions = if tick == 0 { &[decision][..] } else { &[][..] };
            rules.step(&mut world, decisions, 0.02).unwrap();
            for event in world.state.events() {
                if let CombatEvent::PolicyWeaponFired {
                    ship_id: 1, slot: 0, projectile_id,
                } = event {
                    fired.push(*projectile_id);
                }
            }
            if fired.len() >= 2 { break; }
        }
        assert_eq!(fired.len(), 2);
        assert_eq!(fired[1], fired[0] + 1);
        let mut bodies = Vec::new();
        for &id in &fired {
            let index = *world.state.projectile_by_id.get(&id).unwrap();
            let shot = &world.state.projectiles()[index];
            assert_eq!(shot.projectile_type, 6);
            assert_eq!(shot.power, 10);
            assert_eq!(shot.owner.shooter_id, 1);
            let body = &world.state.physics().projectiles()[index];
            assert!(body.active);
            assert_eq!(body.motion.attribution.root_shot_id, id);
            bodies.push(body.motion);
        }
        // Unity RotatePointAroundPoint(Ship.GetPosition(),
        // turret.GetPosition()+barrelOffset, turret.Rotation).
        let left = Vec2::new(4.5, 6.9);
        let right = Vec2::new(4.5, 9.1);
        for (body, muzzle) in bodies.iter().zip([left, right]) {
            assert!((body.starting_position.x - muzzle.x).abs() < 1e-4);
            assert!((body.starting_position.y - muzzle.y).abs() < 1e-4);
            assert!(body.velocity.x < 0.0);
        }
        assert!((bodies[0].velocity.x - bodies[1].velocity.x).abs() < 1e-5);
        assert!((bodies[0].velocity.y - bodies[1].velocity.y).abs() < 1e-5);

        // Two actual independent projectile impacts apply 10+10, not
        // one 21-damage synthetic volley. Ordinary combat hit events and
        // target health are updated by the existing canonical path.
        for &id in &fired {
            world.state.resolve_contact(TriggerEnter {
                projectile_id: id, ship_id: 2,
            }).unwrap();
        }
        assert_eq!(world.state.ship(2).unwrap().vitals.health(), 80);
        assert_eq!(world.state.events().iter()
            .filter(|event| matches!(event, CombatEvent::Hit(_)))
            .count(), 2);
    }

    #[test]
    fn authored_turret_fires_via_real_projectiles_after_three_timer_passes() {
        let projectiles = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let speed = projectiles.by_type(1).unwrap().shot_speed.unwrap();
        let hulls = ShipAssetCatalog::embedded().unwrap();
        let mut combat = CombatWorld::new(
            Aabb {
                min: Vec2::new(-100.0, -100.0),
                max: Vec2::new(100.0, 100.0),
            },
            8.0, projectiles,
        ).unwrap();
        combat.spawn_ship(
            hulls.by_type(21).unwrap(), 1, 1,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::ZERO, 0.0, 120.0, 8.0, 3.0),
        ).unwrap();
        combat.spawn_ship(
            hulls.by_type(13).unwrap(), 2, 2,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::new(0.0, 30.0), 0.0, 120.0, 8.0, 3.0),
        ).unwrap();
        combat.bind_policy_ship(1, PolicyFrame::new(0), false).unwrap();
        let spec = NativeTurretSpec {
            weapon_type: 3,
            projectile_type: 1,
            mount_offset: Vec2::new(1.0, 0.0),
            initial_rotation_degrees: 0.0,
            rotation_rate: 120.0,
            range: 80.0,
            power: 20,
            rate_of_fire: 0.3,
            projectile_value: 1.0,
            projectile_speed: speed,
            fire_pattern: NativeFirePattern::Single,
        };
        combat.configure_policy_turret(1, 0, spec).unwrap();
        assert_eq!(
            combat.policy_action_mask(1).unwrap().fire,
            [true, false, false, false, false],
        );
        assert_eq!(combat.policy_action_mask(2),
            Err(CombatWorldError::UnknownShip));
        assert_eq!(combat.policy_self_weapons(1).unwrap()[0]
            .unwrap().position, Vec2::new(1.0, 0.0));

        let mut action = Action::ZERO;
        action.continuous[3] = 1.0; // slot 0 aims physical +Y
        action.discrete[0] = 1; // fire on the next authored ready pass
        let decision = Decision { agent_id: 1, action };
        let rules = CombatWorldRules {
            collision_matrix: CollisionMatrix::from_rows([0; 32]),
        };
        let mut world = World::new(WorldId(42), 7, combat);
        let mut shot_id = None;
        for tick in 0..24 {
            let decisions = if tick == 0 { &[decision][..] } else { &[][..] };
            rules.step(&mut world, decisions, 0.02).unwrap();
            if let Some(event) = world.state.events().iter().find_map(|event|
                match event {
                    CombatEvent::PolicyWeaponFired {
                        ship_id: 1, slot: 0, projectile_id,
                    } => Some(*projectile_id),
                    _ => None,
                }
            ) {
                shot_id = Some(event);
                break;
            }
        }
        let fired_id = shot_id.expect("source targeting timer must commit shot");
        let idx = *world.state.projectile_by_id.get(&fired_id).unwrap();
        let shot = &world.state.projectiles()[idx];
        let body = &world.state.physics().projectiles()[idx];
        assert_eq!(shot.projectile_type, 1);
        assert_eq!(shot.power, 20);
        assert_eq!(shot.owner.shooter_id, 1);
        assert_eq!(shot.owner.shooter_side, 1);
        assert_eq!(body.motion.attribution.root_shot_id, fired_id);
        assert_eq!(body.motion.attribution.command_outcome_id, 0);
        assert_eq!(body.motion.starting_position, Vec2::new(1.0, 0.0));
        assert!(body.motion.velocity.y > 0.0);
        assert_eq!(world.state.policy_self_weapons(1).unwrap()[0]
            .unwrap().turret.unwrap().ready_to_fire, false);

        // Existing direct projectile damage executes normally rather than
        // a training-only hit shortcut. Physics collision tests remain
        // separate; this explicitly supplies an actual queued contact.
        world.state.resolve_contact(TriggerEnter {
            projectile_id: fired_id, ship_id: 2,
        }).unwrap();
        assert_eq!(world.state.ship(2).unwrap().vitals.health(), 80);
    }

    #[test]
    fn original_shot_geometry_and_cease_fire_are_world_owned() {
        let projectiles = Arc::new(ProjectileAssetCatalog::embedded().unwrap());
        let speed = projectiles.by_type(1).unwrap().shot_speed.unwrap();
        let hulls = ShipAssetCatalog::embedded().unwrap();
        let mut world = CombatWorld::new(
            Aabb {
                min: Vec2::new(-100.0, -100.0),
                max: Vec2::new(100.0, 100.0),
            },
            8.0, projectiles,
        ).unwrap();
        world.spawn_ship(
            hulls.by_type(21).unwrap(), 1, 1,
            ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(Vec2::new(10.0, 20.0),
                90.0, 120.0, 8.0, 3.0),
        ).unwrap();
        world.bind_policy_ship(1, PolicyFrame::new(0), false).unwrap();
        let spec = NativeTurretSpec {
            weapon_type: 7, projectile_type: 1,
            mount_offset: Vec2::new(2.0, 0.0),
            initial_rotation_degrees: 90.0,
            rotation_rate: 90.0,
            range: 70.0, power: 12, rate_of_fire: 0.5,
            projectile_value: 1.0, projectile_speed: speed,
            fire_pattern: NativeFirePattern::Single,
        };
        world.configure_policy_turret(1, 3, spec).unwrap();
        let muzzle = world.policy_self_weapons(1).unwrap()[3]
            .unwrap().position;
        assert!((muzzle.x - 10.0).abs() < 1e-5);
        assert!((muzzle.y - 22.0).abs() < 1e-5);
        world.set_policy_ship_cease_fire(1, true).unwrap();
        let mut action = Action::ZERO;
        action.discrete[3] = 1;
        action.continuous[8] = 1.0; // slot 3 aim +X, but cease-fired
        world.apply_policy_decisions(&[Decision { agent_id: 1, action }])
            .unwrap();
        for _ in 0..100 {
            world.advance_mounted_turrets(0.02, None, None, None).unwrap();
        }
        assert_eq!(world.projectile_storage_metrics().active_projectiles, 0);
        assert!(world.policy_self_weapons(1).unwrap()[3]
            .unwrap().turret.unwrap().ready_to_fire);
        assert_eq!(
            world.configure_policy_turret(1, 3, spec),
            Err(CombatWorldError::DuplicatePolicyTurret {
                ship_id: 1, slot: 3,
            }),
        );
    }
}
