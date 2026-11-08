//! Turret.IsShipValidTarget/GetTargetPoint and Ship.IsInBounds.
//! The same lazily latched bounds state is used by real game movement orders.

use super::*;

#[derive(Clone, Copy, Debug)]
pub struct NativeLevelGeometry {
    pub map_bounds: Aabb,
    pub has_obstacles: bool,
}

impl NativeLevelGeometry {
    pub(super) fn force_bounds(self, position: Vec2) -> Vec2 {
        Vec2::new(position.x.clamp(self.map_bounds.min.x, self.map_bounds.max.x),
            position.y.clamp(self.map_bounds.min.y, self.map_bounds.max.y))
    }

    pub fn valid(self) -> bool {
        self.map_bounds.min.is_finite() && self.map_bounds.max.is_finite() &&
            self.map_bounds.min.x < self.map_bounds.max.x && self.map_bounds.min.y < self.map_bounds.max.y
    }
}

#[derive(Clone, Copy, Debug)]
pub struct NativeShipTargetGeometry {
    /// Exact source GetHalfHeight() - OffsetFromFrontOfShip for this instance.
    pub front_offset: f32,
    /// Source private _isInBounds latch; never inferred from its current pose.
    pub bounds_latched: bool,
}

impl CombatWorld {
    pub fn configure_level_geometry(&mut self, geometry: NativeLevelGeometry)
        -> Result<(), CombatWorldError>
    {
        if !geometry.valid() || self.level_geometry.is_some() {
            return Err(CombatWorldError::InvalidLevelGeometry);
        }
        self.level_geometry = Some(geometry);
        Ok(())
    }

    pub fn configure_ship_target_geometry(&mut self, ship_id: i64, geometry: NativeShipTargetGeometry)
        -> Result<(), CombatWorldError>
    {
        if !geometry.front_offset.is_finite() || self.ship_target_front_offsets.contains_key(&ship_id) {
            return Err(CombatWorldError::InvalidWeaponTargetingGeometry);
        }
        let index = *self.ship_by_id.get(&ship_id).ok_or(CombatWorldError::UnknownShip)?;
        if !self.physics.ships()[index].active { return Err(CombatWorldError::UnknownShip); }
        self.physics.ships_mut()[index].has_latched_target_bounds |= geometry.bounds_latched;
        self.ship_target_front_offsets.insert(ship_id, geometry.front_offset);
        Ok(())
    }

    pub fn configure_turret_target_geometry(&mut self, ship_id: i64, slot: usize, aim_at_front: bool)
        -> Result<(), CombatWorldError>
    {
        let mount = self.ship_weapons.get_mut(&ship_id)
            .and_then(|weapons| weapons.turrets.get_mut(slot)).and_then(Option::as_mut)
            .ok_or(CombatWorldError::InvalidWeaponTargetingGeometry)?;
        if mount.aim_at_front_of_ship.is_some() { return Err(CombatWorldError::InvalidWeaponTargetingGeometry); }
        mount.aim_at_front_of_ship = Some(aim_at_front);
        Ok(())
    }

    /// Mutates only when the source predicate is actually queried. A ship that
    /// once satisfied it remains in bounds after it subsequently leaves the map.
    /// Presentation must not call this method to inspect a scene.
    pub fn ship_is_in_bounds(&mut self, ship_id: i64) -> Result<bool, CombatWorldError> {
        let geometry = self.level_geometry
            .ok_or(CombatWorldError::MissingLevelGeometry)?;
        let index = *self.ship_by_id.get(&ship_id).ok_or(CombatWorldError::UnknownShip)?;
        let body = &mut self.physics.ships_mut()[index];
        if !body.active { return Err(CombatWorldError::UnknownShip); }
        if !body.has_latched_target_bounds {
            let position = body.motion.position;
            let clamped = geometry.force_bounds(position);
            body.has_latched_target_bounds = position.unity_equal(clamped);
        }
        Ok(body.has_latched_target_bounds)
    }

    /// Read source state without causing the first successful bounds query.
    pub fn ship_target_geometry(&self, ship_id: i64) -> Option<NativeShipTargetGeometry> {
        Some(NativeShipTargetGeometry {
            front_offset: *self.ship_target_front_offsets.get(&ship_id)?,
            bounds_latched: self.ship_body(ship_id)?.has_latched_target_bounds,
        })
    }

    /// Exact source predicate, including range CONTACT history and lazy bounds
    /// latching before line-of-fire. Does not imply aim, cooldown or cease-fire.
    pub fn is_weapon_ship_target_valid(&mut self, ship_id: i64, slot: usize, target_id: i64)
        -> Result<bool, CombatWorldError>
    {
        let Some(range) = self.weapon_range(ship_id, slot)? else { return Ok(false) };
        let contact = self.ship_weapons[&ship_id].range_contacts[slot].contains(target_id);
        self.is_ship_target_valid_in_range(ship_id, range, contact, target_id)
    }

    pub(super) fn is_ship_target_valid_in_range(&mut self, ship_id: i64, range: NativeWeaponRange,
        contact: bool, target_id: i64) -> Result<bool, CombatWorldError>
    {
        let geometry = self.level_geometry.ok_or(CombatWorldError::MissingLevelGeometry)?;
        let own = self.ship(ship_id).ok_or(CombatWorldError::UnknownShip)?;
        let target = self.ship(target_id).ok_or(CombatWorldError::UnknownShip)?;
        if own.vitals.is_dead() || target.vitals.is_dead() || own.side == target.side ||
            !contact {
            return Ok(false);
        }
        if !self.ship_is_in_bounds(target_id)? { return Ok(false); }
        if !geometry.has_obstacles { return Ok(true); }
        let point = self.ship_body(target_id).ok_or(CombatWorldError::UnknownShip)?
            .primary_closest_point_readonly(range.origin);
        Ok(!self.physics.primary_obstacle_linecast(range.origin, point, 1u32 << 19)?)
    }

    /// Turret.GetTargetPoint. This is separate from the closest-hull point used
    /// for LOS: the preferred front point is retained while it lies in range.
    /// If the turret is inside the target hull, fallback returns target center.
    pub fn weapon_target_point(&self, ship_id: i64, slot: usize, target_id: i64)
        -> Result<Vec2, CombatWorldError>
    {
        let range = self.weapon_range(ship_id, slot)?
            .ok_or(CombatWorldError::InvalidWeaponTargetingGeometry)?;
        let aim_at_front = self.ship_weapons[&ship_id].turrets[slot].as_ref()
            .and_then(|mount| mount.aim_at_front_of_ship)
            .ok_or(CombatWorldError::MissingWeaponTargetingGeometry)?;
        self.target_point_for_range(range, aim_at_front, target_id)
    }

    pub(super) fn target_point_for_range(&self, range: NativeWeaponRange, aim_at_front: bool, target_id: i64)
        -> Result<Vec2, CombatWorldError>
    {
        let target = self.ship_body(target_id).filter(|body| body.active)
            .ok_or(CombatWorldError::UnknownShip)?;
        let mut point = target.motion.position;
        if aim_at_front {
            let front = *self.ship_target_front_offsets.get(&target_id)
                .ok_or(CombatWorldError::MissingShipTargetGeometry)?;
            point = Pose { position: point, rotation_degrees: target.motion.rotation_degrees }
                .transform_point(Vec2::new(0.0, front));
        }
        if !range.enabled || (point - range.center).length_squared() > range.radius * range.radius {
            let closest = target.primary_closest_point_readonly(range.origin);
            point = if closest.unity_equal(range.origin) { target.motion.position } else { closest };
        }
        Ok(point)
    }
}
