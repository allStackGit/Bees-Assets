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
    /// Unity collision damage uses authored OriginalHealth even when the
    /// training durability guard reduces the live/effective health.
    pub original_health: i32,
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
        Self::with_original_health(health, effective_max_health, effective_max_health)
    }

    pub fn with_original_health(
        health: i32,
        effective_max_health: i32,
        original_health: i32,
    ) -> Option<Self> {
        if health <= 0 || effective_max_health <= 0 || original_health <= 0 {
            return None;
        }
        Some(Self {
            health, effective_max_health, original_health, is_dead: false,
        })
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

/// CollisionAsteroid.ObstacleCollision uses the *authored* original health
/// for collision damage, not the episode's effective training durability.
/// The collision zeroes health immediately; GotKilledInCollision then
/// schedules Kill(false) after 0.35 seconds. This function does not set
/// is_dead or remove physics bodies.
pub fn resolve_asteroid_pair_damage(
    first: &mut ObstacleVitals,
    first_size: u8,
    second: &mut ObstacleVitals,
    second_size: u8,
) -> Option<(AsteroidDamage, AsteroidDamage)> {
    if first.is_dead || second.is_dead || first.health <= 0 ||
        second.health <= 0 || first_size == 0 || second_size == 0 {
        return None;
    }
    let first_before = first.health;
    let second_before = second.health;
    if first_size > second_size {
        first.health -= second.original_health.min(first.health);
        second.health = 0;
    } else if first_size < second_size {
        second.health -= first.original_health.min(second.health);
        first.health = 0;
    } else {
        first.health = 0;
        second.health = 0;
    }
    let status = |before: i32, vitals: &ObstacleVitals| AsteroidDamage {
        health_before: before,
        health_after: vitals.health,
        killed: vitals.health == 0,
        cracked: vitals.health > 0 &&
            (vitals.health as f32 / vitals.effective_max_health as f32) < 0.5,
    };
    Some((status(first_before, first), status(second_before, second)))
}

/// Exact spawn counts from CollisionAsteroid.SpawnBreakAwayAsteroids.
/// The consuming world must still supply pool-specific prefab geometry,
/// inherited angular velocity, random velocity/heading, sibling exclusion,
/// and piece fade/expiry before fragmentation is functionally complete.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct AsteroidBreakup {
    pub collision_asteroid_shards: u8,
    pub asteroid_pieces: u16,
}

impl AsteroidBreakup {
    pub fn for_size_class(size_class: u8) -> Self {
        let collision_asteroid_shards = if size_class < 6 {
            0
        } else if size_class == 6 {
            2
        } else {
            3
        };
        let asteroid_pieces = (u16::from(size_class) * 3) / 2;
        Self { collision_asteroid_shards, asteroid_pieces }
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
    fn shard_and_piece_counts_match_unity_size_classes() {
        assert_eq!(AsteroidBreakup::for_size_class(2),
            AsteroidBreakup { collision_asteroid_shards: 0, asteroid_pieces: 3 });
        assert_eq!(AsteroidBreakup::for_size_class(3),
            AsteroidBreakup { collision_asteroid_shards: 0, asteroid_pieces: 4 });
        assert_eq!(AsteroidBreakup::for_size_class(6),
            AsteroidBreakup { collision_asteroid_shards: 2, asteroid_pieces: 9 });
        assert_eq!(AsteroidBreakup::for_size_class(8),
            AsteroidBreakup { collision_asteroid_shards: 3, asteroid_pieces: 12 });
    }

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
