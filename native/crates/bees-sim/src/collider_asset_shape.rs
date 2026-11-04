//! Common, versioned collider-shape deserialization shared by ship and
//! projectile asset catalogs. JSON is decoded at startup, not during ticks.

use std::sync::Arc;
use serde::Deserialize;

use crate::geometry::Vec2;
use crate::physics::ShipShape;

#[derive(Deserialize)]
#[serde(tag = "kind", rename_all = "lowercase")]
pub(crate) enum RawShape {
    Polygon { vertices: Vec<[f32; 2]> },
    Circle { offset: [f32; 2], radius: f32 },
    Box { offset: [f32; 2], half_extents: [f32; 2] },
}

impl RawShape {
    pub(crate) fn into_shape(self) -> Option<ShipShape> {
        match self {
            Self::Polygon { vertices } => {
                if vertices.len() < 3 ||
                    vertices.iter().flatten().any(|value| !value.is_finite())
                {
                    return None;
                }
                let hull: Vec<Vec2> = vertices.into_iter()
                    .map(|xy| Vec2::new(xy[0], xy[1])).collect();
                Some(ShipShape::Polygon(Arc::from(hull.into_boxed_slice())))
            }
            Self::Circle { offset, radius } => {
                if !radius.is_finite() || radius < 0.0 ||
                    offset.iter().any(|value| !value.is_finite())
                {
                    return None;
                }
                Some(ShipShape::Circle {
                    offset: Vec2::new(offset[0], offset[1]),
                    radius,
                })
            }
            Self::Box { offset, half_extents } => {
                if offset.iter().chain(half_extents.iter())
                    .any(|value| !value.is_finite()) ||
                    half_extents.iter().any(|value| *value <= 0.0)
                {
                    return None;
                }
                Some(ShipShape::Box {
                    offset: Vec2::new(offset[0], offset[1]),
                    half_extents: Vec2::new(half_extents[0], half_extents[1]),
                })
            }
        }
    }
}
