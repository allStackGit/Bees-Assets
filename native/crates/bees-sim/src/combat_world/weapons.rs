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
use crate::geometry::{delta_angle, heading_towards, timed_turret_rotation, Vec2};
use crate::perception_weapons::{SelfWeapon, TurretObservation};
use crate::perception_entities::ObservedWeapon;
use crate::turret::TurretState;

/// Authored, immutable Unity Ship rank fields that cannot be derived
/// from the canonical physical motion/vitals. No duplicated mutable health,
/// velocity or damage ledger is maintained here.
#[derive(Clone, Copy, Debug)]
pub struct NativeGameTargetFacts {
    pub ship_type_letter: u8,
    pub firepower: f32,
    pub max_range: f32,
    /// Unity Ship.LastKilled uses Time.frameCount, updated by KilledShip.
    pub last_killed: Option<i64>,
    /// Starting real FleetShip.DamageDone; live values are subsequently
    /// owned by CombatWorld's fleet-ID ledger, not frozen in this copy.
    pub fleet_damage_done: Option<i32>,
}

impl NativeGameTargetFacts {
    pub fn valid(self) -> bool {
        self.ship_type_letter < 24 &&
            self.last_killed.is_none_or(|frame| frame >= 0) &&
            self.fleet_damage_done.is_none_or(|damage| damage >= 0) &&
            self.firepower.is_finite() && self.firepower >= 0.0 &&
            self.max_range.is_finite() && self.max_range >= 0.0
    }
}

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
    /// FullShipTurret derives from LaserBuilder, queues until the Flagship
    /// Laser Charging clip event at 2.75s, then fires along its current
    /// physical heading (regardless of earlier policy aim).
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

#[derive(Clone, Copy, Debug)]
pub(super) struct PolicyTurretMount {
    pub(super) spec: NativeTurretSpec,
    pub(super) state: TurretState,
    pub(super) timer: ScaledTimer,
    /// None until an accepted policy shot starts the authored charging clip.
    /// A second targeting pass cannot restart an already charging clip.
    pub(super) charge_elapsed_seconds: Option<f32>,
    /// Unity BeamCannon.IsFiringLaserBeam is set on its single active beam.
    /// Cleared by the canonical CombatWorld::end_projectile on any kill path.
    pub(super) active_beam_projectile_id: Option<i64>,
    /// Real ordinary weapon's selected TargetShip. Neural mounts do not
    /// use this field and keep policy point-fire independent.
    pub(super) target_ship_id: Option<i64>,
    pub(super) fire_at_front: Option<bool>,
}

impl PolicyTurretMount {
    fn new(spec: NativeTurretSpec, fire_at_front: Option<bool>) -> Self {
        Self {
            state: TurretState::new(
                spec.initial_rotation_degrees, spec.rotation_rate,
            ),
            timer: ScaledTimer::new(spec.rate_of_fire / 3.0, true, false),
            charge_elapsed_seconds: None,
            active_beam_projectile_id: None,
            target_ship_id: None,
            fire_at_front,
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

/// Authored non-policy turrets share PolicyTurretMount's actual rotation,
/// cooldown, charge and beam state without registering a phantom neural agent.
pub(super) struct GameTurretRuntime {
    pub(super) turrets: [Option<PolicyTurretMount>; 5],
    pub(super) last_hull_rotation: f32,
    pub(super) cease_fire: bool,
    /// Unity's manual point-fire state, not an autonomous target selection.
    /// None means no current manual fire input.
    pub(super) manual_target: Option<Vec2>,
    pub(super) shooting_strategy: Option<u8>,
    /// Source Ship.IsUserControlled OR Squad.HasCommand at the current
    /// authoritative Level state. The Level updates the AI command flag.
    pub(super) ai_order_active: bool,
    /// Original RangeCollider enter order per physical weapon slot.
    pub(super) nearby_enemy_ids: [std::collections::BTreeMap<i64, (u64, u64)>; 5],
    pub(super) range_pass: u64,
    pub(super) entry_sequence: u64,
    /// Unity's cached Random shooting queue, re-shuffled only when
    /// physical RangeCollider membership changes.
    pub(super) random_target_order: [Vec<i64>; 5],
}

impl CombatWorld {
    /// One real ordinary weapon slot. Never borrow a neural policy route
    /// merely to make the native client able to launch a projectile.
    pub fn configure_game_turret(
        &mut self, ship_id: i64, slot: usize, spec: NativeTurretSpec,
        cease_fire: bool, shooting_strategy: Option<u8>,
        fire_at_front: Option<bool>,
    ) -> Result<(), CombatWorldError> {
        if slot >= 5 { return Err(CombatWorldError::InvalidPolicyTurret); }
        spec.valid(&self.projectile_assets)?;
        if shooting_strategy.is_some_and(|value| !(1..=40).contains(&value)) {
            return Err(CombatWorldError::InvalidPolicyTurret);
        }
        let index = *self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if !self.physics.ships()[index].active ||
            self.ships[index].vitals.is_dead() {
            return Err(CombatWorldError::UnknownShip);
        }
        if self.policy_ship_runtime.contains_key(&ship_id) ||
            self.physics.ships()[index].motion.is_policy_controlled
        {
            return Err(CombatWorldError::UnsupportedPolicyActions);
        }
        let rotation = self.physics.ships()[index].motion.rotation_degrees;
        let runtime = self.game_turrets.entry(ship_id)
            .or_insert_with(|| GameTurretRuntime {
                turrets: [None; 5], last_hull_rotation: rotation,
                cease_fire, manual_target: None,
                shooting_strategy, ai_order_active: false,
                nearby_enemy_ids: std::array::from_fn(|_| std::collections::BTreeMap::new()),
                range_pass: 0, entry_sequence: 0,
                random_target_order: std::array::from_fn(|_| Vec::new()),
            });
        if runtime.cease_fire != cease_fire ||
            runtime.shooting_strategy != shooting_strategy ||
            runtime.turrets[slot].is_some()
        {
            return Err(CombatWorldError::DuplicatePolicyTurret { ship_id, slot });
        }
        runtime.turrets[slot] =
            Some(PolicyTurretMount::new(spec, fire_at_front));
        Ok(())
    }

    /// Original source rank metadata is imported once for each live ship.
    /// It remains separate from constantly changing health, TSV and motion.
    pub fn configure_game_target_facts(
        &mut self, ship_id: i64, facts: NativeGameTargetFacts,
    ) -> Result<(), CombatWorldError> {
        if !facts.valid() { return Err(CombatWorldError::InvalidPolicyTurret); }
        let index = *self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active
        {
            return Err(CombatWorldError::UnknownShip);
        }
        let fleet_id = self.fleet_id_by_ship.get(&ship_id).copied();
        if let (Some(fleet_id), Some(damage)) =
            (fleet_id, facts.fleet_damage_done)
        {
            use std::collections::hash_map::Entry;
            match self.game_fleet_damage_done.entry(fleet_id) {
                Entry::Vacant(entry) => { entry.insert(i64::from(damage)); }
                Entry::Occupied(entry) if *entry.get() == i64::from(damage) => {}
                Entry::Occupied(_) => return Err(
                    CombatWorldError::InvalidPolicyTurret,
                ),
            }
        }
        if let Some(frame) = facts.last_killed {
            self.game_target_last_kill_epoch = self.game_target_last_kill_epoch
                .max(i64::from(frame));
        }
        self.game_target_facts.insert(ship_id, facts);
        Ok(())
    }

    /// Source-only aim-at-front offset, registered for ALL physical ships
    /// because an ordinary cannon may target a neural-policy opponent.
    pub fn configure_ship_front_aim_offset(
        &mut self, ship_id: i64, offset: Option<f32>,
    ) -> Result<(), CombatWorldError> {
        if offset.is_some_and(|value| !value.is_finite()) {
            return Err(CombatWorldError::InvalidPolicyTurret);
        }
        let index = *self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if self.ships[index].vitals.is_dead() ||
            !self.physics.ships()[index].active
        {
            return Err(CombatWorldError::UnknownShip);
        }
        if let Some(value) = offset {
            self.target_front_offsets.insert(ship_id, value);
        }
        Ok(())
    }

    /// Source Weapon.Targeting requires a live squad command for AI ships,
    /// but always runs for player-controlled ships. This flag is derived
    /// from the owning command director every gameplay step, not stored
    /// in an independent client-side command registry.
    pub fn refresh_game_auto_fire_authority(
        &mut self, mut has_live_squad_command: impl FnMut(i64) -> bool,
    ) {
        for (&id, runtime) in &mut self.game_turrets {
            let user = self.ship_by_id.get(&id)
                .and_then(|&index| self.physics.ships().get(index))
                .is_some_and(|body| body.motion.user_controlled);
            runtime.ai_order_active = !user && has_live_squad_command(id);
        }
    }

    /// Read-only readiness for an authorized local manual-point order. The
    /// gameplay layer still verifies source squad lifetime and permissions.
    pub fn game_ship_has_manual_weapon(&self, ship_id: i64) -> bool {
        self.game_turrets.get(&ship_id).is_some_and(|runtime|
            runtime.turrets.iter().flatten().any(|mount|
                matches!(mount.spec.fire_pattern,
                    NativeFirePattern::Single |
                    NativeFirePattern::DualCannon { .. } |
                    NativeFirePattern::BeamCannon |
                    NativeFirePattern::LaserBuilder)
            )
        )
    }

    pub fn set_game_ship_manual_target(
        &mut self, ship_id: i64, target: Option<Vec2>,
    ) -> Result<(), CombatWorldError> {
        if target.is_some_and(|point| !point.is_finite()) {
            return Err(CombatWorldError::InvalidPolicyTurret);
        }
        let index = *self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let body = &self.physics.ships()[index];
        if !body.active || self.ships[index].vitals.is_dead() ||
            !body.motion.user_controlled || body.motion.is_policy_controlled
        {
            return Err(CombatWorldError::UnsupportedPolicyActions);
        }
        let runtime = self.game_turrets.get_mut(&ship_id)
            .ok_or(CombatWorldError::UnsupportedPolicyActions)?;
        runtime.manual_target = target;
        Ok(())
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

/// Source RangeCollider maintains hostile hull overlaps, independently
/// of line of sight, aiming direction and whether the owning squad has an
/// active order. A Frigate's authored trigger uses Range-7.
fn game_weapon_candidate_in_range(
    physics: &mut crate::physics::KinematicPhysics,
    ships: &[CombatShip], shooter_index: usize, target_index: usize,
    mount: &PolicyTurretMount, mount_position: Vec2,
) -> bool {
    let shooter = &ships[shooter_index];
    let target = &ships[target_index];
    if shooter.side == target.side || shooter.entity_id == target.entity_id ||
        target.vitals.is_dead() ||
        !physics.ships()[target_index].active
    {
        return false;
    }
    let radius = if shooter.ship_type == 12 {
        (mount.spec.range - 7.0).max(0.0)
    } else { mount.spec.range };
    let closest = physics.ships_mut()[target_index]
        .primary_closest_point(mount_position);
    (closest - mount_position).length_squared() <= radius * radius
}

/// Source Turret.IsShipValidTarget / Turret.GetTargetPoint on the live
/// imported hull. The range cache already admits the real enemy collider;
/// this enforces in-bounds, obstacle linecast and the original front-of-ship
/// fallback without using approximated sprite centers for range.
fn game_weapon_live_target(
    physics: &mut crate::physics::KinematicPhysics,
    ships: &[CombatShip],
    target_front_offsets: &std::collections::HashMap<i64, f32>,
    shooter_index: usize, target_index: usize,
    mount: &PolicyTurretMount, mount_position: Vec2,
) -> Result<Option<(Vec2, f32)>, CombatWorldError> {
    if mount.fire_at_front.is_none() ||
        !game_weapon_candidate_in_range(
            physics, ships, shooter_index, target_index,
            mount, mount_position,
        )
    {
        return Ok(None);
    }
    let target_body = &physics.ships()[target_index];
    let target_position = target_body.motion.position;
    let rotation = target_body.motion.rotation_degrees;
    if !physics.point_inside_level_bounds(target_position) {
        return Ok(None);
    }
    let hull = physics.ships_mut()[target_index]
        .primary_closest_point(mount_position);
    if physics.game_weapon_linecast_blocked(mount_position, hull)? {
        return Ok(None);
    }
    let candidate_point = if mount.fire_at_front == Some(true) {
        let Some(&offset) =
            target_front_offsets.get(&ships[target_index].entity_id)
        else { return Ok(None); };
        rotate_around_ship(
            target_position, target_position + Vec2::new(0.0, offset),
            rotation,
        )
    } else { target_position };
    let radius = if ships[shooter_index].ship_type == 12 {
        (mount.spec.range - 7.0).max(0.0)
    } else { mount.spec.range };
    let aimed_point =
        if (candidate_point - mount_position).length_squared() <= radius * radius {
            candidate_point
        } else { hull };
    Ok(Some((aimed_point, (hull - mount_position).length_squared())))
}

/// Rankings from Unity Weapon.MakeSortedTargetingList that can be read from
/// current physical/vitals state without fabricated FleetShip counters or
/// process-global randomness. Other strategies remain unavailable until
/// their genuine mutable metadata/RNG contracts are connected.
fn choose_game_weapon_target(
    physics: &mut crate::physics::KinematicPhysics,
    ships: &[CombatShip],
    ship_by_id: &std::collections::HashMap<i64, usize>,
    target_front_offsets: &std::collections::HashMap<i64, f32>,
    game_target_facts: &std::collections::HashMap<i64, NativeGameTargetFacts>,
    shooter_index: usize, mount: &PolicyTurretMount,
    mount_position: Vec2, strategy: u8,
    reservations: &mut crate::damage_reservations::DamageReservations,
    nearby: &std::collections::BTreeMap<i64, (u64, u64)>,
    random_order: &[i64],
) -> Result<Option<(i64, Vec2)>, CombatWorldError> {
    if !matches!(strategy, 1 | 2 | 5..=40) {
        return Ok(None);
    }
    if matches!(strategy, 7 | 8 | 11 | 12 | 17..=40) &&
        nearby.keys().any(|id| !game_target_facts.contains_key(id))
    {
        // Never rank only those ships with a legacy partial snapshot.
        return Ok(None);
    }
    if strategy == 2 {
        // Unity Shuffle is cached across ordinary targeting passes.
        // Process its actual retained order, not a fresh random winner
        // sampled independently for every shot.
        let mut first_valid = None;
        for &target_id in random_order {
            if !nearby.contains_key(&target_id) { continue; }
            let Some(&target_index) = ship_by_id.get(&target_id) else { continue; };
            if let Some((point, _)) = game_weapon_live_target(
                physics, ships, target_front_offsets,
                shooter_index, target_index, mount, mount_position,
            )? {
                let health = ships[target_index].vitals.health();
                let status = reservations.status(
                    ships[shooter_index].side, target_id, health,
                );
                first_valid.get_or_insert((target_id, point));
                if status.can_select_target() {
                    return Ok(Some((target_id, point)));
                }
            }
        }
        return Ok(first_valid);
    }
    let mut best: Option<(i64, Vec2, f64, u64, bool)> = None;
    for (&target_id, &(first_seen, _)) in nearby {
        let Some(&target_index) = ship_by_id.get(&target_id) else { continue; };
        let Some((target_point, hull_distance_sq)) =
            game_weapon_live_target(
                physics, ships, target_front_offsets,
                shooter_index, target_index, mount, mount_position,
            )?
        else { continue; };
        let target = &ships[target_index];
        let has_damage_budget = reservations.status(
            ships[shooter_index].side, target_id, target.vitals.health(),
        ).can_select_target();
        let rank = match strategy {
            5 | 6 => f64::from(target.vitals.health()),
            7 | 8 => f64::from(game_target_facts[&target_id].firepower),
            9 | 10 => f64::from(hull_distance_sq),
            11 | 12 => f64::from(game_target_facts[&target_id].max_range),
            13 | 14 => f64::from(
                physics.ships()[target_index].motion.base_speed),
            15 | 16 => f64::from(target.vitals.tsv()),
            17..=40 => f64::from(u8::from(
                game_target_facts[&target_id].ship_type_letter ==
                    strategy - 17)),
            _ => 0.0,
        };
        let better = best.is_none_or(|prior| {
            if has_damage_budget != prior.4 {
                return has_damage_budget;
            }
            let compare = rank.total_cmp(&prior.2);
            let preferred = match strategy {
                5 | 7 | 10 | 11 | 13 | 15 | 17..=40 => compare.is_gt(),
                6 | 8 | 9 | 12 | 14 | 16 => compare.is_lt(),
                _ => false,
            };
            preferred || (compare.is_eq() && first_seen < prior.3)
        });
        if better {
            best = Some((
                target_id, target_point, rank, first_seen, has_damage_budget,
            ));
        }
    }
    Ok(best.map(|(id, point, _, _, _)| (id, point)))
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
    spec: NativeTurretSpec,
    is_policy: bool,
    /// Original TargetShip held by this physical shot at launch, never
    /// read again from a later command or reused pooled wrapper.
    target_ship_id: Option<i64>,
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
        runtime.turrets[slot] = Some(PolicyTurretMount::new(spec, None));
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

    /// Read the same active charging state used by gameplay. A renderer
    /// can animate the real Leafcutter/flagship charge without owning a
    /// second timer; a headless runner reads precisely the same state.
    /// Returns (elapsed_seconds, Fire animation event time).
    pub fn policy_weapon_charge(
        &self, ship_id: i64, slot: usize,
    ) -> Result<Option<(f32, f32)>, CombatWorldError> {
        if slot >= 5 {
            return Err(CombatWorldError::InvalidPolicyTurret);
        }
        let runtime = self.policy_ship_runtime.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        Ok(runtime.turrets[slot].as_ref().and_then(|mount| {
            mount.charge_elapsed_seconds.and_then(|elapsed|
                mount.spec.fire_pattern.fire_event_seconds()
                    .map(|event| (elapsed, event)))
        }))
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
    /// Actual hit and death events are the only authority for ongoing
    /// Revenge and MostDangerous ranks. Keep both in their real physical
    /// fleet/ship lifetime; never sample a stale Unity export each tick.
    pub(super) fn reconcile_game_target_rank_events(&mut self) {
        self.game_target_last_kill_epoch =
            self.game_target_last_kill_epoch.saturating_add(1);
        let epoch = self.game_target_last_kill_epoch;
        for event in &self.events {
            match event {
                CombatEvent::Hit(hit)
                    if hit.attacker_side != hit.target_side &&
                        hit.delta.tsv_loss > 0 =>
                {
                    if let Some(fleet_id) =
                        self.fleet_id_by_ship.get(&hit.attacker_id)
                    {
                        if let Some(damage) =
                            self.game_fleet_damage_done.get_mut(fleet_id)
                        {
                            *damage = damage.saturating_add(
                                i64::from(hit.delta.tsv_loss),
                            );
                        }
                    }
                }
                CombatEvent::ShipDied { killer_id, victim_id, .. }
                    if *killer_id > 0 && killer_id != victim_id =>
                {
                    if let Some(facts) = self.game_target_facts.get_mut(killer_id) {
                        facts.last_killed = Some(epoch);
                    }
                }
                _ => {}
            }
        }
    }

    pub(super) fn advance_authored_turrets(
        &mut self, dt: f32,
    ) -> Result<(), CombatWorldError> {
        self.advance_authored_turrets_with_random(dt, None)
    }

    pub(super) fn advance_authored_turrets_with_random(
        &mut self, dt: f32, mut random: Option<&mut RandomStream>,
    ) -> Result<(), CombatWorldError> {
        self.turret_pending_shots.clear();
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
                if let Some(elapsed) = mount.charge_elapsed_seconds {
                    let next = elapsed + dt;
                    if mount.spec.fire_pattern.fire_event_seconds()
                        .is_some_and(|event| next + 0.000001 >= event) {
                        // LaserBuilderControl.Fire -> ActuallyShoot:
                        // the previously queued request survives later
                        // policy fire=false, but cannot fire if ceased.
                        mount.charge_elapsed_seconds = None;
                        launch_now = !runtime.cease_fire
                            && mount.state.is_rl_controlled;
                    } else {
                        mount.charge_elapsed_seconds = Some(next);
                    }
                }
                if let Some(due) = mount.timer.begin_update(dt) {
                    let may_fire = match mount.spec.fire_pattern {
                        NativeFirePattern::FullShipTurret => true,
                        _ => mount.state.is_aligned_with_target,
                    };
                    if mount.state.targeting_timer_pass(
                        may_fire, runtime.cease_fire,
                    ) {
                        if mount.spec.fire_pattern.fire_event_seconds().is_some() {
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
                        if matches!(mount.spec.fire_pattern,
                            NativeFirePattern::FullShipTurret) {
                            // FullShipTurret.GetRlForwardFirePoint samples
                            // its physical rotation at the Fire event,
                            // not the point requested when charging began.
                            let rad = mount.state.rotation_degrees.to_radians();
                            position + Vec2::new(-rad.sin(), rad.cos())
                        } else {
                            mount.state.target_world
                        };
                    self.turret_pending_shots.push(PendingTurretShot {
                        ship_id,
                        slot: slot as u8,
                        shooter_side: ship.side,
                        shooter_is_fire_barge: ship.ship_type == 10,
                        turret_position: position,
                        ship_position: pose,
                        turret_rotation: mount.state.rotation_degrees,
                        target_world,
                        spec: mount.spec,
                        is_policy: true,
                        target_ship_id: None,
                    });
                }
            }
        }

        // Ordinary player and HiveMind turrets use the SAME mount,
        // cooldown, shot queue, projectile physics and damage path as
        // policy-controlled turrets. Auto-targeting follows the original
        // RangeCollider candidate lifecycle; AI only acquires while its
        // source squad owns a real command, player ships may always acquire.
        // Manual point-fire has priority and never creates TargetShip.
        let ships = &self.ships;
        let ship_by_id = &self.ship_by_id;
        let target_front_offsets = &self.target_front_offsets;
        let game_target_facts = &self.game_target_facts;
        let physics = &mut self.physics;
        let reservations = &mut self.reservations;
        for (&ship_id, runtime) in &mut self.game_turrets {
            let Some(&index) = ship_by_id.get(&ship_id) else { continue };
            let ship = &ships[index];
            let body = &physics.ships()[index];
            if !body.active || ship.vitals.is_dead() { continue; }
            let pose = body.motion.position;
            let heading = body.motion.rotation_degrees;
            let user_controlled = body.motion.user_controlled;
            let hull_delta = heading - runtime.last_hull_rotation;
            runtime.last_hull_rotation = heading;
            for (slot, turret) in runtime.turrets.iter_mut().enumerate() {
                let Some(mount) = turret else { continue };
                mount.state.apply_hull_turn(hull_delta);
                // Fixed FullShipTurret owns hull turning and a distinct
                // charge. LaserBuilder uses animation-event firing below,
                // rather than the rotating cannon's cooldown trigger.
                if !matches!(mount.spec.fire_pattern,
                    NativeFirePattern::Single |
                    NativeFirePattern::DualCannon { .. } |
                    NativeFirePattern::BeamCannon |
                    NativeFirePattern::LaserBuilder)
                {
                    continue;
                }
                let position = mount.position(pose, heading);
                let manual = runtime.manual_target.filter(|_| !runtime.cease_fire);
                let auto = manual.is_none() && !runtime.cease_fire &&
                    (user_controlled || runtime.ai_order_active) &&
                    mount.fire_at_front.is_some() &&
                    matches!(runtime.shooting_strategy, Some(1 | 2 | 5..=40)) &&
                    (runtime.shooting_strategy != Some(2) || random.is_some());
                if manual.is_some() {
                    mount.target_ship_id = None;
                }
                let auto_point = if auto {
                    if let Some(target_id) = mount.target_ship_id {
                        if let Some(&target_index) = ship_by_id.get(&target_id) {
                            game_weapon_live_target(
                                physics, ships, target_front_offsets,
                                index, target_index, mount, position,
                            )?.map(|(point, _)| point)
                        } else { None }
                    } else { None }
                } else { None };
                if auto && auto_point.is_none() {
                    mount.target_ship_id = None;
                }
                let aim = manual.or(auto_point);
                if let Some(point) = aim {
                    mount.state.target_world = point;
                    if matches!(mount.spec.fire_pattern, NativeFirePattern::LaserBuilder) {
                        // LaserBuilder.Aim never physically rotates the
                        // cannon; the firing animation controls readiness.
                        mount.state.is_aligned_with_target = true;
                    } else {
                        let desired = heading_towards(point - position);
                        let (rotation, aligned) = timed_turret_rotation(
                            mount.state.rotation_degrees, desired,
                            mount.spec.rotation_rate, dt,
                        );
                        mount.state.rotation_degrees = rotation;
                        mount.state.is_aligned_with_target = aligned;
                    }
                } else {
                    mount.state.is_aligned_with_target = false;
                    // Turret.FixedUpdate never aims during cease-fire.
                    // Otherwise return to the hull only without a live
                    // target, as Unity Turret.Aim does.
                    if !runtime.cease_fire &&
                        !matches!(mount.spec.fire_pattern, NativeFirePattern::LaserBuilder)
                    {
                        let (rotation, _) = timed_turret_rotation(
                            mount.state.rotation_degrees, heading,
                            mount.spec.rotation_rate, dt,
                        );
                        mount.state.rotation_degrees = rotation;
                    }
                }
                // Unity LaserBuilder.SetTargetShip / Aim activates its
                // animation on real target acquisition. The animation
                // event ActuallyShoot occurs at 0.8s and revalidates the
                // live target. Unlike RL fire, ordinary gameplay does not
                // start this charge from a cooldown request.
                if matches!(mount.spec.fire_pattern, NativeFirePattern::LaserBuilder) {
                    if aim.is_none() || runtime.cease_fire {
                        mount.charge_elapsed_seconds = None;
                    } else if let Some(elapsed) = mount.charge_elapsed_seconds {
                        let next = elapsed + dt;
                        if mount.spec.fire_pattern.fire_event_seconds()
                            .is_some_and(|event| next + 0.000001 >= event)
                        {
                            self.turret_pending_shots.push(PendingTurretShot {
                                ship_id, slot: slot as u8, shooter_side: ship.side,
                                shooter_is_fire_barge: ship.ship_type == 10,
                                turret_position: position, ship_position: pose,
                                turret_rotation: mount.state.rotation_degrees,
                                target_world: mount.state.target_world,
                                spec: mount.spec, is_policy: false,
                                target_ship_id: mount.target_ship_id,
                            });
                            mount.charge_elapsed_seconds = None;
                        } else {
                            mount.charge_elapsed_seconds = Some(next);
                        }
                    } else {
                        mount.charge_elapsed_seconds = Some(0.0);
                    }
                }
                if let Some(due) = mount.timer.begin_update(dt) {
                    let passes = mount.state.targeting_passes.saturating_add(1);
                    mount.state.targeting_passes = passes;
                    let due_to_fire = passes >=
                        crate::turret::TARGETING_PASSES_PER_SHOT ||
                        (mount.state.ready_to_fire &&
                            mount.state.is_aligned_with_target);
                    if due_to_fire {
                        let launch_now = aim.is_some() &&
                            mount.state.is_aligned_with_target &&
                            !runtime.cease_fire &&
                            !matches!(mount.spec.fire_pattern, NativeFirePattern::LaserBuilder) &&
                            !(matches!(mount.spec.fire_pattern,
                                NativeFirePattern::BeamCannon) &&
                                mount.active_beam_projectile_id.is_some());
                        if launch_now {
                            self.turret_pending_shots.push(PendingTurretShot {
                                ship_id, slot: slot as u8, shooter_side: ship.side,
                                shooter_is_fire_barge: ship.ship_type == 10,
                                turret_position: position, ship_position: pose,
                                turret_rotation: mount.state.rotation_degrees,
                                target_world: mount.state.target_world,
                                spec: mount.spec, is_policy: false,
                                target_ship_id: mount.target_ship_id,
                            });
                        }
                        // Non-RL ready-to-fire is only latched when a
                        // valid automatic TargetShip was not yet aligned.
                        mount.state.ready_to_fire = auto && aim.is_some() &&
                            !mount.state.is_aligned_with_target;
                        mount.state.targeting_passes = 0;
                    }
                    if auto && (!due_to_fire || mount.target_ship_id.is_none()) {
                        // Source Targeting() refreshes its enemy list on
                        // ordinary cooldown passes. Range membership is
                        // distinct from obstacle/visibility eligibility.
                        runtime.range_pass = runtime.range_pass.saturating_add(1);
                        let current_pass = runtime.range_pass;
                        let nearby = &mut runtime.nearby_enemy_ids[slot];
                        let mut membership_changed = false;
                        for target_index in 0..ships.len() {
                            if !game_weapon_candidate_in_range(
                                physics, ships, index, target_index,
                                mount, position,
                            ) {
                                continue;
                            }
                            let target_id = ships[target_index].entity_id;
                            use std::collections::btree_map::Entry;
                            match nearby.entry(target_id) {
                                Entry::Occupied(mut entry) => {
                                    entry.get_mut().1 = current_pass;
                                }
                                Entry::Vacant(entry) => {
                                    membership_changed = true;
                                    runtime.entry_sequence =
                                        runtime.entry_sequence.saturating_add(1);
                                    entry.insert((runtime.entry_sequence, current_pass));
                                }
                            }
                        }
                        let old_count = nearby.len();
                        nearby.retain(|_, (_, last_seen)| *last_seen == current_pass);
                        membership_changed |= nearby.len() != old_count;
                        if runtime.shooting_strategy == Some(2) &&
                            (membership_changed ||
                                runtime.random_target_order[slot].is_empty())
                        {
                            if let Some(rng) = random.as_deref_mut() {
                                let mut entered: Vec<(u64, i64)> = nearby.iter()
                                    .map(|(&id, &(sequence, _))| (sequence, id))
                                    .collect();
                                entered.sort_unstable_by_key(|&(sequence, _)| sequence);
                                let order = &mut runtime.random_target_order[slot];
                                order.clear();
                                order.extend(entered.into_iter().map(|(_, id)| id));
                                // Unity Utilities.Shuffle: Fisher-Yates
                                // from the final index down. The sole
                                // world RNG supplies real episode entropy.
                                for i in (1..order.len()).rev() {
                                    let j = rng.next_below((i + 1) as u64) as usize;
                                    order.swap(i, j);
                                }
                            }
                        }
                        if let Some(strategy) = runtime.shooting_strategy {
                            mount.target_ship_id = choose_game_weapon_target(
                                physics, ships, ship_by_id, target_front_offsets,
                                game_target_facts,
                                index, mount, position, strategy,
                                reservations, nearby,
                                &runtime.random_target_order[slot],
                            )?.map(|(target_id, _)| target_id);
                        }
                    }
                    mount.timer.finish_update(due);
                }
            }
        }

        // Policy and ordinary turrets share a single world ID allocator.
        // Do not group shots by controller type: mixed-player/headless
        // battles must produce the same physical ID order as a single
        // source ship/weapon traversal. Policy-only order is unchanged.
        if self.turret_pending_shots.len() > 1 {
            self.turret_pending_shots.sort_unstable_by_key(|shot|
                (shot.ship_id, shot.slot));
        }

        // Reserve enough monotonic entity IDs for complete volleys before
        // creating any projectiles. DualCannon must not spawn only its first
        // barrel if the shared ID space is exhausted.
        let shot_count: usize = self.turret_pending_shots.iter()
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
        for index in 0..self.turret_pending_shots.len() {
            let shot = self.turret_pending_shots[index];
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
            for &position in positions[..barrels].iter() {
                let projectile_id = self.next_entity_id;
                if !shot.is_policy {
                    if let Some(target_id) = shot.target_ship_id {
                        let health = self.ship(target_id)
                            .ok_or(CombatWorldError::UnknownShip)?
                            .vitals.health();
                        // Unity Weapon.SendProjectile reserves once at
                        // launch. The resulting real projectile ID owns
                        // the lease through impact, split/transfer and
                        // the canonical end_projectile release.
                        self.reservations.reserve(
                            projectile_id, shot.shooter_side,
                            target_id, health, power,
                        )?;
                    }
                }
                let motion = ProjectileMotion::new(
                    position,
                    shot.spec.projectile_speed,
                    angle,
                    shot.spec.range,
                    projectile_id, // each shot is its own RL root
                    0, // policy shot has no Hive Mind command outcome
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
                // Source DualCannon increments FleetShip.ShotsFired twice.
                // Emit once per actual shot so reward and telemetry counts
                // match the physical projectiles, not timer passes.
                if matches!(shot.spec.fire_pattern, NativeFirePattern::BeamCannon) {
                    let mount = if shot.is_policy {
                        self.policy_ship_runtime.get_mut(&shot.ship_id)
                            .and_then(|runtime| runtime.turrets[usize::from(shot.slot)].as_mut())
                    } else {
                        self.game_turrets.get_mut(&shot.ship_id)
                            .and_then(|runtime| runtime.turrets[usize::from(shot.slot)].as_mut())
                    }.expect("validated physical beam cannon mount");
                    mount.active_beam_projectile_id = Some(projectile_id);
                }
                if shot.is_policy {
                    // Policy root/near-miss rewards cannot be attributed
                    // to a non-policy ship's manual point-fire.
                    self.track_policy_projectile_root(projectile_id)?;
                    self.events.push(CombatEvent::PolicyWeaponFired {
                        ship_id: shot.ship_id,
                        slot: shot.slot,
                        projectile_id,
                    });
                } else {
                    self.events.push(CombatEvent::GameWeaponFired {
                        ship_id: shot.ship_id,
                        slot: shot.slot,
                        projectile_id,
                    });
                }
            }
        }
        self.turret_pending_shots.clear();
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
            let turret_rotation = self.policy_ship_runtime.get(&owner_id)
                .and_then(|runtime| runtime.turrets.iter().flatten().find_map(|turret|
                    (turret.active_beam_projectile_id == Some(beam_id))
                        .then_some(turret.state.rotation_degrees)))
                .or_else(|| self.game_turrets.get(&owner_id).and_then(|runtime|
                    runtime.turrets.iter().flatten().find_map(|turret|
                        (turret.active_beam_projectile_id == Some(beam_id))
                            .then_some(turret.state.rotation_degrees))));
            let Some(turret_rotation) = turret_rotation else {
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
            world.advance_authored_turrets(0.02).unwrap();
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
        assert_eq!(world.policy_ship_runtime.get(&1).unwrap().turrets[0]
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
        world.advance_authored_turrets(0.02).unwrap();
        world.advance_beam_projectiles().unwrap();
        let rotation = world.policy_ship_runtime.get(&1).unwrap().turrets[0]
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
            world.advance_authored_turrets(0.02).unwrap();
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
        assert_eq!(world.policy_ship_runtime.get(&1).unwrap().turrets[0]
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
            world.advance_authored_turrets(0.02).unwrap();
        }
        assert_eq!(world.projectile_storage_metrics().active_projectiles, 0);
        let (elapsed, event) = world.policy_weapon_charge(1, 0).unwrap().unwrap();
        assert!(elapsed > 0.0 && elapsed < event);
        assert_eq!(event, 0.8);
        for _ in 0..30 {
            world.advance_authored_turrets(0.02).unwrap();
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
            world.advance_authored_turrets(0.02).unwrap();
        }
        assert_eq!(world.projectile_storage_metrics().active_projectiles, 0);
        let (elapsed, event) = world.policy_weapon_charge(1, 0).unwrap().unwrap();
        assert!(elapsed < event);
        assert_eq!(event, 2.75);

        // The Unity animation event retains the queued shot even after
        // a subsequent policy decision drops fire_requested.
        action.discrete[0] = 0;
        world.apply_policy_decisions(&[Decision { agent_id: 1, action }])
            .unwrap();
        world.physics.ships_mut()[0].motion.rotation_degrees = 90.0;
        for _ in 0..155 {
            world.advance_authored_turrets(0.02).unwrap();
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
            world.advance_authored_turrets(0.02).unwrap();
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
