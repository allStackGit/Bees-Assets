//! Authoritative native MapObject health/lifetime.
//!
//! Unity: Scripts/Entities/MapObject.cs. This object is owned by the
//! CombatWorld and shares its real collider/contact pipeline. The special
//! CanisterBomb (Fire Tank) delegates its explosion to the same attributed
//! native projectile gameplay as ordinary weapons. A linked static obstacle
//! is removed by the combat world; graphical debris is a rendering concern.

/// Gameplay MapObject identity, including Unity's source-defined RL map-bit
/// discriminators. The neural encoder imports this type from gameplay;
/// perception must not define gameplay entity kinds.
#[repr(u8)]
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum MapObjectKind {
    Generic = 2,
    FireTank = 3,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MapObjectError {
    InvalidId,
    InvalidMaxHealth,
    UnsupportedFireTank,
    InvalidFireTankPower,
    InvalidFireTankTarget,
}

/// Source CanisterBomb.Power and optional TargetObstacle. The latter is an
/// authored Level entity reference, not an object chosen by proximity.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FireTankConfig {
    pub explosion_power: i32,
    pub target_obstacle_id: Option<i64>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct MapObjectDamage {
    pub before: i32,
    pub after: i32,
    pub destroyed: bool,
}

/// One live Level MapObject, paired with its collider in KinematicPhysics.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CombatMapObject {
    pub entity_id: i64,
    pub kind: MapObjectKind,
    pub health: i32,
    pub max_health: i32,
    pub fire_tank: Option<FireTankConfig>,
}

impl CombatMapObject {
    pub fn new(
        entity_id: i64,
        kind: MapObjectKind,
        max_health: i32,
    ) -> Result<Self, MapObjectError> {
        if entity_id <= 0 { return Err(MapObjectError::InvalidId); }
        if max_health <= 0 { return Err(MapObjectError::InvalidMaxHealth); }
        if kind == MapObjectKind::FireTank {
            return Err(MapObjectError::UnsupportedFireTank);
        }
        Ok(Self {
            entity_id, kind, health: max_health, max_health,
            fire_tank: None,
        })
    }

    /// Authored Fire Tank with its actual CanisterBomb.Power and optional
    /// level-linked obstacle. No default blast power or target is fabricated.
    pub fn new_fire_tank(
        entity_id: i64,
        max_health: i32,
        explosion_power: i32,
        target_obstacle_id: Option<i64>,
    ) -> Result<Self, MapObjectError> {
        if entity_id <= 0 { return Err(MapObjectError::InvalidId); }
        if max_health <= 0 { return Err(MapObjectError::InvalidMaxHealth); }
        if explosion_power < 0 {
            return Err(MapObjectError::InvalidFireTankPower);
        }
        if target_obstacle_id.is_some_and(|id| id <= 0 || id == entity_id) {
            return Err(MapObjectError::InvalidFireTankTarget);
        }
        Ok(Self {
            entity_id,
            kind: MapObjectKind::FireTank,
            health: max_health,
            max_health,
            fire_tank: Some(FireTankConfig {
                explosion_power,
                target_obstacle_id,
            }),
        })
    }

    #[inline]
    pub fn alive(&self) -> bool { self.health > 0 }

    /// MapObject.OnTriggerEnter2D subtracts the projectile's Power and
    /// destroys the MapObject at Health <= 0. Health is allowed to become
    /// negative, as in Unity; the caller retires the collider on destruction.
    pub fn apply_projectile_damage(
        &mut self, power: i32,
    ) -> Option<MapObjectDamage> {
        if !self.alive() || power < 0 { return None; }
        let before = self.health;
        self.health = self.health.saturating_sub(power);
        Some(MapObjectDamage {
            before, after: self.health, destroyed: self.health <= 0,
        })
    }

    pub fn health_fraction(&self) -> f32 {
        (self.health as f32 / self.max_health as f32).clamp(0.0, 1.0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn map_object_health_and_explicit_fire_tank_configuration() {
        let mut object = CombatMapObject::new(5, MapObjectKind::Generic, 100).unwrap();
        let first = object.apply_projectile_damage(30).unwrap();
        assert_eq!((first.before, first.after, first.destroyed), (100, 70, false));
        let last = object.apply_projectile_damage(90).unwrap();
        assert_eq!((last.before, last.after, last.destroyed), (70, -20, true));
        assert!(object.apply_projectile_damage(1).is_none());
        // The ordinary constructor still refuses to invent a Fire Tank's
        // gameplay blast power or linked demolition target.
        assert_eq!(
            CombatMapObject::new(6, MapObjectKind::FireTank, 100),
            Err(MapObjectError::UnsupportedFireTank),
        );
        let tank = CombatMapObject::new_fire_tank(
            6, 120, 40, Some(5),
        ).unwrap();
        assert_eq!(tank.kind, MapObjectKind::FireTank);
        assert_eq!(tank.health, 120);
        assert_eq!(tank.fire_tank, Some(FireTankConfig {
            explosion_power: 40,
            target_obstacle_id: Some(5),
        }));
        assert_eq!(
            CombatMapObject::new_fire_tank(6, 120, -1, None),
            Err(MapObjectError::InvalidFireTankPower),
        );
        assert_eq!(
            CombatMapObject::new_fire_tank(6, 120, 40, Some(6)),
            Err(MapObjectError::InvalidFireTankTarget),
        );
    }
}
