//! Geometry for the authored Unity 2D trigger colliders.
//!
//! The Wasp and Gunship prefabs use concave-capable PolygonCollider2D
//! outlines. Laser shots use CircleCollider2D. Do not replace these hulls
//! with bounding circles: doing so changes both gameplay and training hits.
//!
//! This layer performs no collision filtering, timing or damage. Those
//! depend on physics body state and the game's side/layer rules.

use crate::geometry::Vec2;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Aabb {
    pub min: Vec2,
    pub max: Vec2,
}

impl Aabb {
    pub fn from_points(vertices: &[Vec2]) -> Option<Self> {
        let first = *vertices.first()?;
        let mut bounds = Self { min: first, max: first };
        for &point in vertices.iter().skip(1) {
            bounds.min.x = bounds.min.x.min(point.x);
            bounds.min.y = bounds.min.y.min(point.y);
            bounds.max.x = bounds.max.x.max(point.x);
            bounds.max.y = bounds.max.y.max(point.y);
        }
        Some(bounds)
    }

    pub fn circle(center: Vec2, radius: f32) -> Self {
        Self {
            min: Vec2::new(center.x - radius, center.y - radius),
            max: Vec2::new(center.x + radius, center.y + radius),
        }
    }

    #[inline]
    pub fn overlaps(self, other: Self) -> bool {
        self.min.x <= other.max.x
            && self.max.x >= other.min.x
            && self.min.y <= other.max.y
            && self.max.y >= other.min.y
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Pose {
    pub position: Vec2,
    /// Unity's Transform rotation around Z, in degrees.
    pub rotation_degrees: f32,
}

impl Pose {
    /// Transform an authored local collider offset or vertex.
    #[inline]
    pub fn transform_point(&self, point: Vec2) -> Vec2 {
        let (sin, cos) = self.rotation_degrees.to_radians().sin_cos();
        Vec2::new(
            self.position.x + point.x * cos - point.y * sin,
            self.position.y + point.x * sin + point.y * cos,
        )
    }

    /// Transform prefab collider vertices into the Level map coordinate frame.
    /// Prefab-specific scale is applied when importing the hull asset.
    pub fn transform_into(&self, local_vertices: &[Vec2], out: &mut Vec<Vec2>) {
        out.clear();
        out.reserve(local_vertices.len());
        let (sin, cos) = self.rotation_degrees.to_radians().sin_cos();
        for &vertex in local_vertices {
            out.push(Vec2::new(
                self.position.x + vertex.x * cos - vertex.y * sin,
                self.position.y + vertex.x * sin + vertex.y * cos,
            ));
        }
    }
}

#[inline]
fn cross(a: Vec2, b: Vec2) -> f32 {
    a.x * b.y - a.y * b.x
}

#[inline]
fn dot(a: Vec2, b: Vec2) -> f32 {
    a.x * b.x + a.y * b.y
}

/// Detect circle vs the actual (possibly concave) polygon outline.
/// A circle fully inside the ship is also a contact, not just an edge hit.
/// Requires >=3 non-self-intersecting vertices defining a closed ring.
pub fn circle_overlaps_polygon(
    circle_center: Vec2,
    circle_radius: f32,
    polygon_world: &[Vec2],
) -> bool {
    if polygon_world.len() < 3 || !circle_radius.is_finite() || circle_radius < 0.0 {
        return false;
    }
    let radius_squared = circle_radius * circle_radius;
    let mut inside = false;
    let mut previous = *polygon_world.last().expect("checked length");
    for &current in polygon_world {
        let segment = current - previous;
        let relative = circle_center - previous;
        let length_sq = dot(segment, segment);
        if length_sq > 0.0 {
            let t = (dot(relative, segment) / length_sq).clamp(0.0, 1.0);
            let delta = relative - segment * t;
            if dot(delta, delta) <= radius_squared {
                return true;
            }
        }
        // Ray casting works for concave as well as convex polygons.
        if (previous.y > circle_center.y) != (current.y > circle_center.y) {
            let intersect_x = previous.x
                + (circle_center.y - previous.y) * segment.x / segment.y;
            if circle_center.x < intersect_x {
                inside = !inside;
            }
        }
        previous = current;
    }
    inside
}

/// Exact overlap for two circular trigger colliders.
#[inline]
pub fn circles_overlap(a: Vec2, radius_a: f32, b: Vec2, radius_b: f32) -> bool {
    let delta = a - b;
    let combined = radius_a + radius_b;
    delta.length_squared() <= combined * combined
}

/// Segment intersection for collision between polygon outlines. Collinear
/// contacts and degenerate segments are handled by the closest-edge check.
#[inline]
fn point_on_segment(point: Vec2, a: Vec2, b: Vec2) -> bool {
    let edge = b - a;
    let delta = point - a;
    let length_sq = dot(edge, edge);
    if length_sq == 0.0 {
        return dot(delta, delta) <= 1.0e-10;
    }
    cross(edge, delta).abs() <= 1.0e-5
        && dot(delta, edge) >= 0.0
        && dot(delta, edge) <= length_sq
}

fn segments_intersect(a: Vec2, b: Vec2, c: Vec2, d: Vec2) -> bool {
    let ab = b - a;
    let cd = d - c;
    let ac = c - a;
    let divisor = cross(ab, cd);
    if divisor.abs() <= 1.0e-7 {
        return point_on_segment(c, a, b)
            || point_on_segment(d, a, b)
            || point_on_segment(a, c, d)
            || point_on_segment(b, c, d);
    }
    let t = cross(ac, cd) / divisor;
    let u = cross(ac, ab) / divisor;
    (0.0..=1.0).contains(&t) && (0.0..=1.0).contains(&u)
}

/// Outline-vs-outline overlap for concave polygons without assuming SAT
/// convexity. Suitable for game trigger queries, not physical separation.
pub fn polygons_overlap(a: &[Vec2], b: &[Vec2]) -> bool {
    if a.len() < 3 || b.len() < 3 {
        return false;
    }
    let (Some(aabb_a), Some(aabb_b)) = (Aabb::from_points(a), Aabb::from_points(b)) else {
        return false;
    };
    if !aabb_a.overlaps(aabb_b) {
        return false;
    }
    let mut prev_a = *a.last().expect("checked");
    for &next_a in a {
        let mut prev_b = *b.last().expect("checked");
        for &next_b in b {
            if segments_intersect(prev_a, next_a, prev_b, next_b) {
                return true;
            }
            prev_b = next_b;
        }
        prev_a = next_a;
    }
    // A polygon can be fully enclosed with no intersecting edges.
    circle_overlaps_polygon(a[0], 0.0, b)
        || circle_overlaps_polygon(b[0], 0.0, a)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn concave_polygon_contacts_are_not_bounding_circle_contacts() {
        let notch = [
            Vec2::new(-2.0, -2.0), Vec2::new(2.0, -2.0),
            Vec2::new(2.0, 2.0), Vec2::new(1.0, 2.0),
            Vec2::new(1.0, -1.0), Vec2::new(-1.0, -1.0),
            Vec2::new(-1.0, 2.0), Vec2::new(-2.0, 2.0),
        ];
        assert!(circle_overlaps_polygon(Vec2::new(1.5, 0.0), 0.1, &notch));
        assert!(!circle_overlaps_polygon(Vec2::new(0.0, 1.0), 0.1, &notch));
        assert!(circle_overlaps_polygon(Vec2::new(1.1, 0.0), 0.1, &notch));
    }

    #[test]
    fn turning_a_polygon_changes_world_space_geometry() {
        let mut transformed = Vec::new();
        Pose { position: Vec2::new(4.0, 1.0), rotation_degrees: 90.0 }
            .transform_into(&[Vec2::new(1.0, 0.0)], &mut transformed);
        assert!((transformed[0].x - 4.0).abs() < 0.00001);
        assert!((transformed[0].y - 2.0).abs() < 0.00001);
    }

    #[test]
    fn polygon_containment_and_crossing_detected() {
        let outer = [
            Vec2::new(-2.0, -2.0), Vec2::new(2.0, -2.0),
            Vec2::new(2.0, 2.0), Vec2::new(-2.0, 2.0),
        ];
        let inner = [
            Vec2::new(-1.0, -1.0), Vec2::new(1.0, -1.0),
            Vec2::new(0.0, 1.0),
        ];
        assert!(polygons_overlap(&inner, &outer));
        assert!(!polygons_overlap(&inner, &[
            Vec2::new(6.0, 6.0), Vec2::new(7.0, 6.0),
            Vec2::new(7.0, 7.0), Vec2::new(6.0, 7.0),
        ]));
    }
}
