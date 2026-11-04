//! Engine-independent 2D math and the current Bees orientation conventions.
//!
//! Grounded in Entity.GetDegreesTowardsPoint, Utilities.TimedRotationDifference,
//! Ship.RlDirectionalMovement and Projectile.SetMovement. These formulas are
//! shared between ordinary gameplay and headless simulation.

use core::ops::{Add, AddAssign, Mul, Sub};

#[derive(Clone, Copy, Debug, Default, PartialEq)]
#[repr(C)]
pub struct Vec2 {
    pub x: f32,
    pub y: f32,
}

impl Vec2 {
    pub const ZERO: Self = Self { x: 0.0, y: 0.0 };

    pub const fn new(x: f32, y: f32) -> Self {
        Self { x, y }
    }

    #[inline]
    pub fn length_squared(self) -> f32 {
        self.x * self.x + self.y * self.y
    }

    #[inline]
    pub fn normalized_or_zero(self) -> Self {
        let squared = self.length_squared();
        if squared <= f32::EPSILON {
            return Self::ZERO;
        }
        self * squared.sqrt().recip()
    }

    #[inline]
    pub fn is_finite(self) -> bool {
        self.x.is_finite() && self.y.is_finite()
    }
}

impl Add for Vec2 {
    type Output = Self;
    #[inline]
    fn add(self, rhs: Self) -> Self {
        Self::new(self.x + rhs.x, self.y + rhs.y)
    }
}

impl AddAssign for Vec2 {
    #[inline]
    fn add_assign(&mut self, rhs: Self) {
        self.x += rhs.x;
        self.y += rhs.y;
    }
}

impl Sub for Vec2 {
    type Output = Self;
    #[inline]
    fn sub(self, rhs: Self) -> Self {
        Self::new(self.x - rhs.x, self.y - rhs.y)
    }
}

impl Mul<f32> for Vec2 {
    type Output = Self;
    #[inline]
    fn mul(self, rhs: f32) -> Self {
        Self::new(self.x * rhs, self.y * rhs)
    }
}

/// Bees heading is 0 degrees in +Y, 90 in -X, 180 in -Y and 270 in +X.
/// `Entity.GetDegreesTowardsPoint` uses `-atan2(dx, dy)` and `Mathf.Repeat`.
#[inline]
pub fn heading_towards(direction: Vec2) -> f32 {
    (-direction.x.atan2(direction.y).to_degrees()).rem_euclid(360.0)
}

/// Replicates `Mathf.DeltaAngle(current, target)` for finite degree inputs.
#[inline]
pub fn delta_angle(current: f32, target: f32) -> f32 {
    let delta = (target - current).rem_euclid(360.0);
    if delta > 180.0 { delta - 360.0 } else { delta }
}

/// `Utilities.TimedRotationDifference(Ship,...)`: turn at a constant signed
/// rate beyond a three-degree dead band, without clamping to the target.
/// Rotation is deliberately not normalized; Unity's Ship.Rotation accumulates.
#[inline]
pub fn timed_ship_rotation(rotation: f32, target: f32, degrees_per_second: f32, dt: f32) -> f32 {
    let diff = delta_angle(rotation, target);
    if diff > 3.0 {
        rotation + dt * degrees_per_second
    } else if diff < -3.0 {
        rotation - dt * degrees_per_second
    } else {
        rotation
    }
}

/// `Ship.RlDirectionalMovement` velocity convention. CurrentSpeed is not
/// necessarily the ship's base Speed; special roles can change it temporarily.
#[inline]
pub fn ship_velocity(current_speed: f32, rotation_degrees: f32) -> Vec2 {
    let angle = (rotation_degrees - 180.0).to_radians();
    Vec2::new(current_speed * angle.sin(), -current_speed * angle.cos())
}

/// `Projectile.SetMovement`: Speed is stored as a double in the Unity
/// Projectile class; Mathf.Sin/Cos return f32 before multiplication.
#[inline]
pub fn projectile_velocity(speed: f64, angle_radians: f32) -> Vec2 {
    Vec2::new(
        (-speed * f64::from(angle_radians.sin())) as f32,
        (-speed * f64::from(angle_radians.cos())) as f32,
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn heading_follows_bees_not_standard_math_convention() {
        assert_eq!(heading_towards(Vec2::new(0.0, 1.0)), 0.0);
        assert_eq!(heading_towards(Vec2::new(-1.0, 0.0)), 90.0);
        assert_eq!(heading_towards(Vec2::new(0.0, -1.0)), 180.0);
        assert_eq!(heading_towards(Vec2::new(1.0, 0.0)), 270.0);
    }

    #[test]
    fn timed_rotation_respects_three_degree_deadband_and_unclamped_step() {
        assert_eq!(timed_ship_rotation(0.0, 1.0, 90.0, 0.02), 0.0);
        assert_eq!(timed_ship_rotation(0.0, 90.0, 90.0, 0.02), 1.8);
        assert_eq!(timed_ship_rotation(0.0, 270.0, 90.0, 0.02), -1.8);
        assert_eq!(delta_angle(350.0, 10.0), 20.0);
    }

    #[test]
    fn projectile_and_ship_headings_match_source() {
        let ship = ship_velocity(10.0, 0.0);
        assert!(ship.x.abs() < 0.00001 && (ship.y - 10.0).abs() < 0.00001);
        let projectile = projectile_velocity(10.0, 0.0);
        assert!(projectile.x.abs() < 0.00001 && (projectile.y + 10.0).abs() < 0.00001);
    }
}
