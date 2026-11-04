//! Source-grounded obstacle identity and projectile-damage accounting.
//!
//! ConfigData.ObstacleTypes values are preserved explicitly. All collision
//! geometry lives in the same KinematicPhysics world as ships/projectiles.
//! Asteroid fragmentation, obstacle ownership and pathfinder invalidation
//! are exposed as lifecycle events until their gameplay consumers are ported.

#[repr(u8)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ObstacleKind {
    StaticObstacle = 0,
    MapBorder = 1,
    CollisionAsteroid = 2,
    MiningAsteroid = 3,
    AsteroidPiece = 4,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ObstacleVitals {
    /// Unity's current Health, which can become negative before Kill(false).
    pub health: i32,
    /// CollisionAsteroid.EffectiveMaxHealth, not OriginalHealth when
    /// training durability adjustments are active.
    pub effective_max_health: i32,
    pub is_dead: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct AsteroidDamage {
    pub health_before: i32,
    pub health_after: i32,
    pub killed: bool,
    pub cracked: bool,
}

impl ObstacleVitals {
    pub fn new(health: i32, effective_max_health: i32) -> Option<Self> {
        if health <= 0 || effective_max_health <= 0 {
            return None;
        }
        Some(Self { health, effective_max_health, is_dead: false })
    }

    /// Projectile.DamageObstacle only damages CollisionAsteroid. Its power
    /// applies even if the game's ship-projectile damage is marked harmless.
    /// Damage can overkill; once destroyed, the asteroid cannot be hit again.
    pub fn apply_asteroid_projectile_damage(&mut self, power: i32) -> Option<AsteroidDamage> {
        if self.is_dead || power < 0 {
            return None;
        }
        let before = self.health;
        self.health = self.health.saturating_sub(power);
        let killed = self.health <= 0;
        self.is_dead = killed;
        let cracked = !killed && self.health > 0 &&
            (self.health as f32 / self.effective_max_health as f32) < 0.5;
        Some(AsteroidDamage {
            health_before: before,
            health_after: self.health,
            killed,
            cracked,
        })
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CombatObstacle {
    pub entity_id: i64,
    pub kind: ObstacleKind,
    pub vitals: Option<ObstacleVitals>,
    /// CollisionAsteroid.SizeClass; needed for eventual shard spawning.
    pub size_class: u8,
}

impl CombatObstacle {
    pub fn live(&self) -> bool {
        !self.vitals.is_some_and(|vitals| vitals.is_dead)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn collision_asteroid_damage_death_and_cracked_threshold() {
        let mut vitals = ObstacleVitals::new(100, 100).unwrap();
        let first = vitals.apply_asteroid_projectile_damage(50).unwrap();
        assert!(!first.cracked);
        let second = vitals.apply_asteroid_projectile_damage(1).unwrap();
        assert!(second.cracked);
        let last = vitals.apply_asteroid_projectile_damage(60).unwrap();
        assert!(last.killed);
        assert_eq!(last.health_after, -11);
        assert!(vitals.apply_asteroid_projectile_damage(10).is_none());
    }
}
