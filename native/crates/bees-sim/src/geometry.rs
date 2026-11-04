//! Engine-independent 2D math.
//!
//! Angles match the current Unity gameplay conventions rather than the
//! mathematical convention of positive-X-forward. Only formulas directly
//! traced to existing gameplay are represented here.

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

/// Port of `Projectile.SetMovement` velocity convention.
/// `angle_radians` is the existing projectile aim angle, not a ship rotation.
#[inline]
pub fn projectile_velocity(speed: f32, angle_radians: f32) -> Vec2 {
    Vec2::new(-speed * angle_radians.sin(), -speed * angle_radians.cos())
}

/// Port of `Ship.SetMovementVelocity` translation convention.
/// Turning and speed selection remain owned by the gameplay rules.
#[inline]
pub fn ship_velocity(speed: f32, rotation_degrees: f32) -> Vec2 {
    let angle = (rotation_degrees - 180.0).to_radians();
    Vec2::new(speed * angle.sin(), -speed * angle.cos())
}
