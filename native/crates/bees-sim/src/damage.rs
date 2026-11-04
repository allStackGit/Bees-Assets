//! Side-effect-free projectile contact eligibility from
//! Scripts/Entities/Projectiles/ProjectileDamagePolicy.cs.
//!
//! No impact may modify health until the shared gameplay rules have also
//! implemented kill ownership, TSV, statistics, observations and RL rewards.
//! A collision event is NOT evidence of a valid damaging hit.

/// Source: ProjectileDamagePolicy.CanBasicProjectileDamage.
/// Fire Barge projectiles can damage the same side; ignored targets cannot.
#[inline]
pub fn can_basic_projectile_damage(
    shooter_side: i32,
    shooter_is_fire_barge: bool,
    target_side: i32,
    target_is_ignored: bool,
) -> bool {
    !target_is_ignored && (shooter_side != target_side || shooter_is_fire_barge)
}

/// Source: ProjectileDamagePolicy.CanExplosionDamage.
/// Fire Tank explosions can damage friendly units even when not fired by a
/// Fire Barge. A dead, harmless or previously hit target is always excluded.
#[inline]
pub fn can_explosion_damage(
    shooter_side: i32,
    shooter_is_fire_barge: bool,
    target_side: i32,
    projectile_is_fire_tank_explosion: bool,
    target_is_dead: bool,
    is_harmless: bool,
    already_hit: bool,
) -> bool {
    if target_is_dead || is_harmless || already_hit {
        return false;
    }
    projectile_is_fire_tank_explosion
        || shooter_side != target_side
        || shooter_is_fire_barge
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn basic_projectiles_follow_authored_friendly_fire_rule() {
        assert!(can_basic_projectile_damage(1, false, 2, false));
        assert!(!can_basic_projectile_damage(1, false, 1, false));
        assert!(can_basic_projectile_damage(1, true, 1, false));
        assert!(!can_basic_projectile_damage(1, true, 2, true));
    }

    #[test]
    fn explosions_preserve_fire_tank_and_duplicate_exclusions() {
        assert!(!can_explosion_damage(1, false, 1, false, false, false, false));
        assert!(can_explosion_damage(1, false, 1, true, false, false, false));
        assert!(can_explosion_damage(1, true, 1, false, false, false, false));
        assert!(!can_explosion_damage(1, true, 2, true, true, false, false));
        assert!(!can_explosion_damage(1, true, 2, true, false, true, false));
        assert!(!can_explosion_damage(1, true, 2, true, false, false, true));
    }
}
