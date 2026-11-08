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

pub(crate) fn segments_intersect(a: Vec2, b: Vec2, c: Vec2, d: Vec2) -> bool {
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

/// The first distance along a *unit* movement direction at which a
/// rotated rectangular ship-sized box intersects an authored obstacle
/// polygon. This is the geometric contract of Unity's BoxCastNonAlloc,
/// implemented continuously (no fixed-distance stepping), and supports
/// the concave PolygonCollider2D outlines used by Bees.
pub fn swept_box_polygon_distance(
    box_corners: &[Vec2; 4],
    unit_direction: Vec2,
    distance: f32,
    polygon: &[Vec2],
) -> Option<f32> {
    if polygon.len() < 3 || !unit_direction.is_finite() ||
        !distance.is_finite() || distance < 0.0 {
        return None;
    }
    if polygons_overlap(box_corners, polygon) {
        return Some(0.0);
    }
    let mut nearest: Option<f32> = None;
    let mut edge_start = *polygon.last()?;
    for &edge_end in polygon {
        for &corner in box_corners {
            if let Some(hit) = moving_point_segment_contact(
                corner, unit_direction, distance, edge_start, edge_end,
            ) {
                nearest = Some(nearest.map_or(hit, |value| value.min(hit)));
            }
        }
        edge_start = edge_end;
    }
    // The obstacle can meet a *moving box edge* even if none of its edges
    // meets a moving box vertex. In the box's moving reference frame,
    // each obstacle vertex instead traces the opposite translation.
    let relative_direction = unit_direction * -1.0;
    for &vertex in polygon {
        let mut first = box_corners[3];
        for &second in box_corners {
            if let Some(hit) = moving_point_segment_contact(
                vertex, relative_direction, distance, first, second,
            ) {
                nearest = Some(nearest.map_or(hit, |value| value.min(hit)));
            }
            first = second;
        }
    }
    nearest
}

/// Continuous rectangle sweep against a circle, without approximating the
/// circle as an N-sided polygon. Equivalently, cast the circle center through
/// the four stationary box-edge capsules in the box's moving reference frame.
pub fn swept_box_circle_distance(
    box_corners: &[Vec2; 4],
    unit_direction: Vec2,
    distance: f32,
    circle_center: Vec2,
    radius: f32,
) -> Option<f32> {
    if !circle_center.is_finite() || !radius.is_finite() || radius < 0.0 ||
        !unit_direction.is_finite() || !distance.is_finite() || distance < 0.0 {
        return None;
    }
    if circle_overlaps_polygon(circle_center, radius, box_corners) {
        return Some(0.0);
    }
    let relative_direction = unit_direction * -1.0;
    let mut nearest: Option<f32> = None;
    let mut start = box_corners[3];
    for &end in box_corners {
        if let Some(hit) = moving_point_capsule_contact(
            circle_center, relative_direction, distance, start, end, radius,
        ) {
            nearest = Some(nearest.map_or(hit, |value| value.min(hit)));
        }
        start = end;
    }
    nearest
}

fn moving_point_segment_contact(
    point: Vec2,
    direction: Vec2,
    max_distance: f32,
    start: Vec2,
    end: Vec2,
) -> Option<f32> {
    const EPS: f32 = 1.0e-6;
    let edge = end - start;
    let relative = start - point;
    let denominator = cross(direction, edge);
    if denominator.abs() <= EPS {
        if cross(relative, direction).abs() > EPS { return None; }
        let entry = dot(relative, direction)
            .min(dot(end - point, direction));
        let exit = dot(relative, direction)
            .max(dot(end - point, direction));
        let candidate = entry.max(0.0);
        return (candidate <= exit && candidate <= max_distance).then_some(candidate);
    }
    let travel = cross(relative, edge) / denominator;
    let segment_fraction = cross(relative, direction) / denominator;
    if travel >= -EPS && travel <= max_distance + EPS &&
        segment_fraction >= -EPS && segment_fraction <= 1.0 + EPS {
        return Some(travel.clamp(0.0, max_distance));
    }
    None
}

fn moving_point_capsule_contact(
    point: Vec2,
    direction: Vec2,
    max_distance: f32,
    start: Vec2,
    end: Vec2,
    radius: f32,
) -> Option<f32> {
    let edge = end - start;
    let edge_len_sq = dot(edge, edge);
    if edge_len_sq <= 0.0 { return None; }
    let mut nearest = None;
    // Two semicircular end caps. The first positive quadratic root is
    // the entry time along the moving point's straight trajectory.
    for endpoint in [start, end] {
        let offset = point - endpoint;
        let b = dot(offset, direction);
        let c = dot(offset, offset) - radius * radius;
        let discriminant = b * b - c;
        if discriminant >= 0.0 {
            let t = -b - discriminant.sqrt();
            if t >= 0.0 && t <= max_distance {
                nearest = Some(nearest.map_or(t, |v: f32| v.min(t)));
            }
        }
    }
    let edge_len = edge_len_sq.sqrt();
    let normal = Vec2::new(-edge.y / edge_len, edge.x / edge_len);
    let signed_distance = dot(point - start, normal);
    let rate = dot(direction, normal);
    if rate.abs() > 1.0e-7 {
        for sign in [-1.0f32, 1.0f32] {
            let t = (sign * radius - signed_distance) / rate;
            if t < 0.0 || t > max_distance { continue; }
            let point_at_hit = point + direction * t;
            let projected = dot(point_at_hit - start, edge) / edge_len_sq;
            if (0.0..=1.0).contains(&projected) {
                nearest = Some(nearest.map_or(t, |v: f32| v.min(t)));
            }
        }
    }
    nearest
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
