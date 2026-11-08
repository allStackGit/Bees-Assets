//! Authored Bomb/Charge identity and actual child-trigger/body membership.
//! Shared by rendered commands and headless Levels, never inferred from range stats.
use super::*;
use super::weapon_ranges::{WeaponRangeContacts, refresh_circle_contacts};

#[derive(Clone, Copy, Debug)]
pub struct NativeSpecialWeaponSpec {
    pub weapon_id: i64,
    pub slot: usize,
    pub power: i32,
    /// Captured Weapon.GetPosition relative to the ship, not its visual piece.
    pub mount_offset: Vec2,
    /// Charge.ShipsWithinRange, in ship-local coordinates.
    pub charge_range: Option<NativeTurretRangeSpec>,
    /// ProximityCollider.NearbyEnemyShips, in ship-local coordinates.
    pub proximity: Option<NativeTurretRangeSpec>,
}

pub(super) struct SpecialWeaponContacts {
    spec: NativeSpecialWeaponSpec,
    charge: WeaponRangeContacts,
    proximity: WeaponRangeContacts,
    body: HashSet<i64>,
    next_body: HashSet<i64>,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct SpecialWeaponMeasurements {
    pub grid_rebuilds: u64,
    pub candidates: u64,
    pub trigger_hull_tests: u64,
    pub trigger_entries: u64,
    pub trigger_exits: u64,
    pub body_hull_tests: u64,
    pub body_entries: u64,
}

impl CombatWorld {
    pub fn configure_special_weapon(&mut self, ship_id: i64, spec: NativeSpecialWeaponSpec)
        -> Result<(), CombatWorldError>
    {
        let ship = self.ship(ship_id).ok_or(CombatWorldError::UnknownShip)?;
        if !matches!(ship.ship_type, 0 | 10 | 19 | 22) || spec.weapon_id <= 0 ||
            spec.power < 0 || spec.slot >= 5 || !spec.mount_offset.is_finite() ||
            spec.charge_range.is_some_and(|value| !value.valid()) ||
            spec.proximity.is_some_and(|value| !value.valid()) ||
            (ship.ship_type == 0 && spec.charge_range.is_none()) ||
            (ship.ship_type != 0 && spec.proximity.is_none()) ||
            self.special_weapons.contains_key(&ship_id) ||
            self.special_weapons.values().any(|value| value.spec.weapon_id == spec.weapon_id) ||
            self.ship_by_id.contains_key(&spec.weapon_id) || self.projectile_by_id.contains_key(&spec.weapon_id) ||
            self.obstacle_by_id.contains_key(&spec.weapon_id) || self.map_objects.contains_key(&spec.weapon_id) {
            return Err(CombatWorldError::InvalidBombTarget);
        }
        let next = spec.weapon_id.checked_add(1).ok_or(CombatWorldError::EntityIdExhausted)?;
        self.next_entity_id = self.next_entity_id.max(next);
        self.special_weapons.insert(ship_id, SpecialWeaponContacts { spec,
            charge: WeaponRangeContacts::default(), proximity: WeaponRangeContacts::default(),
            body: HashSet::new(), next_body: HashSet::new() });
        Ok(())
    }

    pub fn special_weapon_spec(&self, ship_id: i64) -> Option<NativeSpecialWeaponSpec> {
        self.special_weapons.get(&ship_id).map(|value| value.spec)
    }

    pub fn special_weapon_measurements(&self) -> SpecialWeaponMeasurements { self.special_weapon_measurements }

    pub fn special_weapon_origin(&self, ship_id: i64) -> Option<Vec2> {
        let spec = self.special_weapon_spec(ship_id)?;
        let body = self.ship_body(ship_id)?;
        Some(Pose { position: body.motion.position, rotation_degrees: body.motion.rotation_degrees }
            .transform_point(spec.mount_offset))
    }

    pub fn bomb_target_is_nearby(&self, ship_id: i64, target_id: i64) -> Result<bool, CombatWorldError> {
        let contacts = self.special_weapons.get(&ship_id).ok_or(CombatWorldError::MissingAutomaticTargetingInputs)?;
        if contacts.spec.proximity.is_none() { return Err(CombatWorldError::InvalidBombTarget); }
        Ok(contacts.proximity.contains(target_id) && self.ship(target_id).is_some_and(|ship| !ship.vitals.is_dead()))
    }

    /// Charge.Timer uses ship-center heading and center-to-center obstacle LOS,
    /// not Turret's bounds latch or closest-hull LOS predicate.
    pub fn barge_target_within_charging_range(&self, ship_id: i64, target_id: i64)
        -> Result<bool, CombatWorldError>
    {
        let contacts = self.special_weapons.get(&ship_id).ok_or(CombatWorldError::MissingAutomaticTargetingInputs)?;
        if contacts.spec.charge_range.is_none() { return Err(CombatWorldError::InvalidBombTarget); }
        let own = self.ship_body(ship_id).ok_or(CombatWorldError::UnknownShip)?;
        let target = self.ship_body(target_id).ok_or(CombatWorldError::UnknownShip)?;
        let heading = crate::geometry::heading_towards(target.motion.position - own.motion.position);
        Ok(own.active && target.active &&
            crate::geometry::delta_angle(own.motion.rotation_degrees, heading).abs() <= 3.0 &&
            !self.physics.primary_obstacle_linecast(own.motion.position, target.motion.position, 1u32 << 19)? &&
            contacts.charge.contains(target_id))
    }

    pub fn bomber_body_entries(&self) -> &[(i64, i64)] { &self.special_body_entries }

    pub(super) fn refresh_special_weapon_contacts(&mut self, collisions: &CollisionMatrix) {
        self.special_body_entries.clear();
        let mut counts = StepCounts::default();
        for (&id, contacts) in &mut self.special_weapons {
            let index = self.ship_by_id[&id];
            let ship = self.ships[index];
            let body = &self.physics.ships()[index];
            let active = body.active && !ship.vitals.is_dead() && !self.policy_ship_runtime.contains_key(&id);
            let pose = Pose { position: body.motion.position, rotation_degrees: body.motion.rotation_degrees };
            for (spec, cache) in [(contacts.spec.charge_range, &mut contacts.charge),
                (contacts.spec.proximity, &mut contacts.proximity)] {
                if let Some(spec) = spec {
                    refresh_circle_contacts(&mut self.physics, &self.ships, ship.side, active,
                        NativeWeaponRange { origin: pose.position, center: pose.transform_point(spec.offset),
                            radius: spec.radius, layer: spec.layer, enabled: spec.enabled }, cache,
                        &mut self.weapon_range_candidates, &mut self.weapon_range_entering_ids, collisions, &mut counts);
                }
            }
            contacts.next_body.clear();
            if active && matches!(ship.ship_type, 19 | 22) {
                self.special_weapon_measurements.grid_rebuilds += u64::from(self.physics.ship_contact_candidates(index, &mut self.weapon_range_candidates));
                self.special_weapon_measurements.candidates += self.weapon_range_candidates.len() as u64;
                for &target_index in &self.weapon_range_candidates {
                    let target = self.ships[target_index];
                    if target.side == ship.side || target.vitals.is_dead() { continue; }
                    self.special_weapon_measurements.body_hull_tests += 1;
                    if self.physics.ship_hulls_overlap(index, target_index, collisions) {
                        contacts.next_body.insert(target.entity_id);
                        if !contacts.body.contains(&target.entity_id) {
                            self.special_body_entries.push((id, target.entity_id));
                            self.special_weapon_measurements.body_entries += 1;
                        }
                    }
                }
            }
            std::mem::swap(&mut contacts.body, &mut contacts.next_body);
        }
        self.special_weapon_measurements.grid_rebuilds += counts.weapon_range_grid_rebuilds;
        self.special_weapon_measurements.candidates += counts.weapon_range_candidates;
        self.special_weapon_measurements.trigger_hull_tests += counts.weapon_range_exact_tests;
        self.special_weapon_measurements.trigger_entries += counts.weapon_range_enters;
        self.special_weapon_measurements.trigger_exits += counts.weapon_range_exits;
    }
}
