//! Ported projectile motion, range expiry, and delayed-impact attribution.
//!
//! These are the source rules from Projectile.SetMovement,
//! Projectile.FixedUpdate, Projectile.Setup and
//! Projectile.InheritCommandAttributionFrom. The future shared physics world
//! will resolve contacts before calling `has_exceeded_range`; this module
//! intentionally does not invent a substitute for Unity's collision solver.

use crate::geometry::{projectile_velocity, Vec2};

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct ShotAttribution {
    /// The original shot identifier also belongs to explosion and split
    /// continuations, allowing correct hit/near-miss and terminal accounting.
    pub root_shot_id: u64,
    /// Snapshot of Hive Mind command identity at firing, not the command
    /// currently held by the shooter's squad when a delayed shot lands.
    pub command_outcome_id: u64,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ProjectileMotion {
    pub position: Vec2,
    pub starting_position: Vec2,
    pub velocity: Vec2,
    pub range: f32,
    pub attribution: ShotAttribution,
}

impl ProjectileMotion {
    pub fn new(
        position: Vec2,
        speed: f64,
        angle_radians: f32,
        range: f32,
        root_shot_id: u64,
        command_outcome_id: u64,
    ) -> Self {
        Self {
            position,
            starting_position: position,
            velocity: projectile_velocity(speed, angle_radians),
            range,
            attribution: ShotAttribution { root_shot_id, command_outcome_id },
        }
    }

    /// A new projectile from a split or explosion inherits the original
    /// shot's identity, even though its own physical entity ID may differ.
    pub fn inherit_attribution(&mut self, parent: &Self) {
        self.attribution = parent.attribution;
    }

    /// Match the existing `DistanceToPoint(StartingPosition) > Range` check.
    /// Equality with the range is still alive. The physics solver owns
    /// position updates; the projectile's collision callbacks must be handled
    /// before this check, as they currently are in Unity.
    #[inline]
    pub fn has_exceeded_range(&self) -> bool {
        (self.position - self.starting_position)
            .length_squared()
            .sqrt() > self.range
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn range_check_is_strict_and_attribution_survives_continuations() {
        let parent = ProjectileMotion::new(Vec2::ZERO, 30.0, 0.0, 10.0, 42, 73);
        let mut continuation = ProjectileMotion::new(Vec2::ZERO, 20.0, 0.0, 10.0, 99, 0);
        continuation.inherit_attribution(&parent);
        assert_eq!(continuation.attribution, parent.attribution);
        continuation.position = Vec2::new(10.0, 0.0);
        assert!(!continuation.has_exceeded_range());
        continuation.position = Vec2::new(10.1, 0.0);
        assert!(continuation.has_exceeded_range());
    }

    #[test]
    fn shot_velocity_follows_unity_projectile_frame() {
        let projectile = ProjectileMotion::new(Vec2::ZERO, 10.0, 0.0, 10.0, 1, 0);
        assert!(projectile.velocity.x.abs() < 0.000001);
        assert!((projectile.velocity.y + 10.0).abs() < 0.000001);
    }
}
