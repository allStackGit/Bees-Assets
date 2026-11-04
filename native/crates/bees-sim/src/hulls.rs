//! Initial native copies of authored collider geometry from Unity prefabs.
//!
//! Source: Prefabs/Entities/Ships/{Wasp,Gunship}.prefab, PolygonCollider2D
//! m_Points.m_Paths[0]. Both prefab root transforms have unit XY scale.
//! These are asset data (not a second set of gameplay rules). The full port
//! must move all authored collider geometry into the unified asset pipeline.

use crate::geometry::Vec2;

pub const WASP: &[Vec2] = &[
    Vec2::new(2.4442577, -2.5234876),
    Vec2::new(2.9380958, -1.967566),
    Vec2::new(0.5269673, 1.6315573),
    Vec2::new(-0.3314182, 1.642128),
    Vec2::new(-2.879995, -1.8550031),
    Vec2::new(-2.3753653, -2.4535508),
    Vec2::new(-1.2937722, -1.5328571),
    Vec2::new(0.011103481, -3.1284966),
    Vec2::new(1.3038396, -1.5454162),
];

pub const GUNSHIP: &[Vec2] = &[
    Vec2::new(-0.24497725, 2.5836384),
    Vec2::new(-2.8234913, -2.4484444),
    Vec2::new(2.7876117, -2.4286904),
    Vec2::new(0.27364227, 2.5895834),
];

/// Source: Prefabs/Entities/Projectiles/{Bee,Human} Small Laser Shot.prefab.
/// Both use a trigger CircleCollider2D radius 0.5 and a kinematic body.
pub const SMALL_LASER_SHOT_RADIUS: f32 = 0.5;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum InitialHull {
    Wasp,
    Gunship,
}

impl InitialHull {
    pub const fn vertices(self) -> &'static [Vec2] {
        match self {
            Self::Wasp => WASP,
            Self::Gunship => GUNSHIP,
        }
    }
}
