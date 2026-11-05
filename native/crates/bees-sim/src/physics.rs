//! World-owned kinematic trigger physics.
//!
//! The current Unity Wasp/Gunship and small laser-shot prefabs use kinematic
//! Rigidbody2D with discrete collision detection and trigger colliders.
//! Their authored primary ship colliders live in native/assets/ship-colliders.json
//! and are loaded once through ship_assets. Projectile radii are still being
//! migrated into their own native asset catalog. This layer integrates prescribed velocity and reports trigger
//! *entries*; it does not implement game damage, blocking, or collision
//! impulses. The gameplay core must consume the events, just as Projectile
//! consumes Unity's OnTriggerEnter2D queue.
//!
//! Exact Unity physics step/callback ordering and the current Physics2D
//! collision-layer matrix remain to be established before parity is claimed.

use std::sync::Arc;

use crate::collision::{
    circle_overlaps_polygon, circles_overlap, polygons_overlap,
    swept_box_circle_distance, swept_box_polygon_distance, Aabb, Pose,
};
use crate::geometry::Vec2;
use crate::movement::ShipMotion;
use crate::projectile::ProjectileMotion;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct TriggerEnter {
    pub projectile_id: i64,
    pub ship_id: i64,
}

/// Separate trigger stream: Unity projectiles queue ship and obstacle
/// contacts independently and consume ship contacts first.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct ObstacleTriggerEnter {
    pub projectile_id: i64,
    pub obstacle_id: i64,
}

/// Primary hull overlap between a ship and a participating obstacle.
/// Near/touch classification requires the separately authored proximity
/// collider and is not implied by this contact.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct ShipObstacleContact {
    pub ship_id: i64,
    pub obstacle_id: i64,
}

/// Obstacle pairs have a stable, canonical ID order regardless of which
/// physical body initiated the broadphase query.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct ObstaclePairContact {
    pub first_obstacle_id: i64,
    pub second_obstacle_id: i64,
}

/// Proximity is directional: the source obstacle owns the larger child
/// trigger, and the target has its regular primary collider.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct ObstacleProximityContact {
    pub source_obstacle_id: i64,
    pub other_obstacle_id: i64,
}

fn update_contact_changes<T: Copy + Ord>(
    current: &mut Vec<T>,
    previous: &mut Vec<T>,
    entered: &mut Vec<T>,
    exited: &mut Vec<T>,
) {
    current.sort_unstable();
    current.dedup();
    entered.clear();
    exited.clear();
    let mut previous_index = 0;
    let mut current_index = 0;
    while previous_index < previous.len() && current_index < current.len() {
        match previous[previous_index].cmp(&current[current_index]) {
            std::cmp::Ordering::Less => {
                exited.push(previous[previous_index]);
                previous_index += 1;
            }
            std::cmp::Ordering::Greater => {
                entered.push(current[current_index]);
                current_index += 1;
            }
            std::cmp::Ordering::Equal => {
                previous_index += 1;
                current_index += 1;
            }
        }
    }
    exited.extend_from_slice(&previous[previous_index..]);
    entered.extend_from_slice(&current[current_index..]);
    std::mem::swap(previous, current);
}

/// Unity 2D physics layers are 0..31. The actual layer collision matrix
/// must be imported from the authoritative project settings. No permissive
/// default is provided for game execution.
#[derive(Clone, Debug)]
pub struct CollisionMatrix {
    rows: [u32; 32],
}

impl CollisionMatrix {
    pub const fn from_rows(rows: [u32; 32]) -> Self {
        Self { rows }
    }

    #[inline]
    pub fn allows(&self, first: u8, second: u8) -> bool {
        if first >= 32 || second >= 32 {
            return false;
        }
        (self.rows[usize::from(first)] & (1u32 << second)) != 0
    }
}

/// Shared authored ship collider. Offset and dimensions are imported from
/// the actual Entity.Collider reference, not arbitrary nearby vision/range
/// sensor colliders in Unity prefabs.
#[derive(Clone, Debug)]
pub enum ShipShape {
    Polygon(Arc<[Vec2]>),
    Circle { offset: Vec2, radius: f32 },
    Box { offset: Vec2, half_extents: Vec2 },
}

impl From<Arc<[Vec2]>> for ShipShape {
    fn from(hull: Arc<[Vec2]>) -> Self {
        Self::Polygon(hull)
    }
}

impl ShipShape {
    fn valid(&self) -> bool {
        match self {
            Self::Polygon(vertices) => {
                vertices.len() >= 3 && vertices.iter().all(|vertex| vertex.is_finite())
            }
            Self::Circle { offset, radius } => {
                offset.is_finite() && radius.is_finite() && *radius >= 0.0
            }
            Self::Box { offset, half_extents } => {
                offset.is_finite() && half_extents.is_finite()
                    && half_extents.x > 0.0 && half_extents.y > 0.0
            }
        }
    }
}

/// Outcome of a geometric Hive Mind vision query. Distinguishing the
/// bounding-box rejection from the collider test lets benchmark telemetry
/// quantify the actual cost of broadphase versus narrowphase perception.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum VisionProximity {
    RejectedByBounds,
    OutsideCollider,
    WithinRange,
}

/// A transformed collision shape with cached geometry. Ships, rockets,
/// explosions and beam projectiles all use exactly this implementation.
#[derive(Clone, Debug)]
struct WorldCollider {
    shape: ShipShape,
    polygon_world: Vec<Vec2>,
    circle_world: Vec2,
    bounds: Option<Aabb>,
    cached_pose: Option<Pose>,
}

impl WorldCollider {
    fn new(shape: ShipShape) -> Result<Self, PhysicsError> {
        if !shape.valid() {
            return Err(PhysicsError::InvalidCollider);
        }
        Ok(Self {
            shape,
            polygon_world: Vec::new(),
            circle_world: Vec2::ZERO,
            bounds: None,
            cached_pose: None,
        })
    }

    fn replace_shape(&mut self, shape: ShipShape) -> Result<(), PhysicsError> {
        if !shape.valid() {
            return Err(PhysicsError::InvalidCollider);
        }
        self.shape = shape;
        self.cached_pose = None;
        Ok(())
    }

    #[inline]
    fn update(&mut self, pose: Pose) {
        if self.cached_pose == Some(pose) {
            return;
        }
        match &self.shape {
            ShipShape::Polygon(vertices) => {
                pose.transform_into(vertices, &mut self.polygon_world);
                self.bounds = Aabb::from_points(&self.polygon_world);
            }
            ShipShape::Box { offset, half_extents } => {
                let x = half_extents.x;
                let y = half_extents.y;
                let corners = [
                    Vec2::new(offset.x - x, offset.y - y),
                    Vec2::new(offset.x + x, offset.y - y),
                    Vec2::new(offset.x + x, offset.y + y),
                    Vec2::new(offset.x - x, offset.y + y),
                ];
                pose.transform_into(&corners, &mut self.polygon_world);
                self.bounds = Aabb::from_points(&self.polygon_world);
            }
            ShipShape::Circle { offset, radius } => {
                self.polygon_world.clear();
                self.circle_world = pose.transform_point(*offset);
                self.bounds = Some(Aabb::circle(self.circle_world, *radius));
            }
        }
        self.cached_pose = Some(pose);
    }

    /// Unity Collider2D.OverlapPoint / ClosestPoint semantics on the
    /// *authored* transformed shape. Pathfinder's dynamic clearance cells
    /// query both of these, rather than a bounding rectangle approximation.
    #[inline]
    fn contains_point(&self, point: Vec2) -> bool {
        if !self.bounds.is_some_and(|bounds|
            bounds.min.x <= point.x && point.x <= bounds.max.x &&
            bounds.min.y <= point.y && point.y <= bounds.max.y) {
            return false;
        }
        match &self.shape {
            ShipShape::Circle { radius, .. } => {
                (point - self.circle_world).length_squared() <= radius * radius
            }
            _ => crate::collision::circle_overlaps_polygon(point, 0.0, &self.polygon_world),
        }
    }

    /// Unity HiveMindVision.CanSee uses Collider2D.ClosestPoint, not a
    /// target-center approximation. The cached broadphase AABB can cheaply
    /// reject targets beyond range, without changing the exact test for
    /// targets close enough to matter.
    #[inline]
    fn vision_proximity(&self, observer: Vec2, range_squared: f32) -> VisionProximity {
        let Some(bounds) = self.bounds else {
            return VisionProximity::RejectedByBounds;
        };
        let dx = (bounds.min.x - observer.x).max(0.0)
            .max(observer.x - bounds.max.x);
        let dy = (bounds.min.y - observer.y).max(0.0)
            .max(observer.y - bounds.max.y);
        if dx * dx + dy * dy > range_squared {
            return VisionProximity::RejectedByBounds;
        }
        if (self.closest_point(observer) - observer).length_squared()
            <= range_squared {
            VisionProximity::WithinRange
        } else {
            VisionProximity::OutsideCollider
        }
    }

    fn closest_point(&self, point: Vec2) -> Vec2 {
        if self.contains_point(point) { return point; }
        match &self.shape {
            ShipShape::Circle { radius, .. } => {
                self.circle_world +
                    (point - self.circle_world).normalized_or_zero() * *radius
            }
            _ => {
                if self.polygon_world.is_empty() { return point; }
                let mut previous = *self.polygon_world.last()
                    .expect("nonempty polygon hull");
                let mut closest = previous;
                let mut best_distance = f32::INFINITY;
                for &next in &self.polygon_world {
                    let edge = next - previous;
                    let offset = point - previous;
                    let length_sq = edge.length_squared();
                    let t = if length_sq > 0.0 {
                        ((offset.x * edge.x + offset.y * edge.y) / length_sq).clamp(0.0, 1.0)
                    } else { 0.0 };
                    let candidate = previous + edge * t;
                    let distance = (candidate - point).length_squared();
                    if distance < best_distance {
                        best_distance = distance;
                        closest = candidate;
                    }
                    previous = next;
                }
                closest
            }
        }
    }

    #[inline]
    fn overlaps(&self, other: &Self) -> bool {
        if !self.bounds.zip(other.bounds)
            .is_some_and(|(a, b)| a.overlaps(b)) {
            return false;
        }
        match (&self.shape, &other.shape) {
            (ShipShape::Circle { radius: r1, .. }, ShipShape::Circle { radius: r2, .. }) => {
                circles_overlap(self.circle_world, *r1, other.circle_world, *r2)
            }
            (ShipShape::Circle { radius, .. }, _) => {
                circle_overlaps_polygon(self.circle_world, *radius, &other.polygon_world)
            }
            (_, ShipShape::Circle { radius, .. }) => {
                circle_overlaps_polygon(other.circle_world, *radius, &self.polygon_world)
            }
            _ => polygons_overlap(&self.polygon_world, &other.polygon_world),
        }
    }
}

#[derive(Clone, Debug)]
pub struct ShipBody {
    pub id: i64,
    pub motion: ShipMotion,
    pub layer: u8,
    pub active: bool,
    collider: WorldCollider,
}

impl ShipBody {
    pub fn new(
        id: i64,
        motion: ShipMotion,
        layer: u8,
        shape: impl Into<ShipShape>,
    ) -> Result<Self, PhysicsError> {
        if layer >= 32 || !motion.position.is_finite() ||
            !motion.rotation_degrees.is_finite() {
            return Err(PhysicsError::InvalidCollider);
        }
        Ok(Self {
            id,
            motion,
            layer,
            active: true,
            collider: WorldCollider::new(shape.into())?,
        })
    }

    pub fn shape(&self) -> &ShipShape {
        &self.collider.shape
    }

    /// Exact Unity ship-collider sight test. Updating the cached hull here
    /// also permits correct sight checks on newly spawned ships before
    /// their first fixed physics update.
    pub fn vision_proximity(
        &mut self, observer: Vec2, range_squared: f32,
    ) -> VisionProximity {
        if !self.active {
            return VisionProximity::RejectedByBounds;
        }
        self.update_hull();
        self.collider.vision_proximity(observer, range_squared)
    }

    /// Invalidates cached geometry before the next fixed step.
    pub fn replace_shape(&mut self, shape: ShipShape) -> Result<(), PhysicsError> {
        self.collider.replace_shape(shape)
    }

    fn update_hull(&mut self) {
        self.collider.update(Pose {
            position: self.motion.position,
            rotation_degrees: self.motion.rotation_degrees,
        });
    }
}

#[derive(Clone, Debug)]
pub struct ProjectileBody {
    pub id: i64,
    pub motion: ProjectileMotion,
    pub layer: u8,
    pub active: bool,
    /// A projectile may be a stationary explosion or script-driven bomb.
    pub has_body: bool,
    /// Unity's logical projectile rotation, not the ballistic aim angle.
    pub rotation_degrees: f32,
    collider: Option<WorldCollider>,
}

impl ProjectileBody {
    /// Convenience for a circular, velocity-driven laser projectile.
    /// General projectiles are instantiated through ProjectileAssetCatalog.
    pub fn new(
        id: i64,
        motion: ProjectileMotion,
        layer: u8,
        radius: f32,
    ) -> Result<Self, PhysicsError> {
        Self::from_shape(
            id, motion, layer,
            Some(ShipShape::Circle { offset: Vec2::ZERO, radius }),
            true, 0.0,
        )
    }

    /// Shared projectile initializer, including box projectiles and
    /// invisible script-driven entities with no physical collider.
    pub fn from_shape(
        id: i64,
        motion: ProjectileMotion,
        layer: u8,
        shape: Option<ShipShape>,
        has_body: bool,
        rotation_degrees: f32,
    ) -> Result<Self, PhysicsError> {
        if layer >= 32 || !rotation_degrees.is_finite() ||
            !motion.position.is_finite() {
            return Err(PhysicsError::InvalidCollider);
        }
        let collider = shape.map(WorldCollider::new).transpose()?;
        Ok(Self {
            id, motion, layer, active: true, has_body,
            rotation_degrees, collider,
        })
    }

    pub fn shape(&self) -> Option<&ShipShape> {
        self.collider.as_ref().map(|collider| &collider.shape)
    }

    /// Shapes can change at runtime: LaserBeam stretches, explosions grow.
    pub fn replace_shape(&mut self, shape: Option<ShipShape>) -> Result<(), PhysicsError> {
        self.collider = shape.map(WorldCollider::new).transpose()?;
        Ok(())
    }

    fn update_hull(&mut self) {
        if let Some(collider) = &mut self.collider {
            collider.update(Pose {
                position: self.motion.position,
                rotation_degrees: self.rotation_degrees,
            });
        }
    }
}

/// An authored obstacle collider with a kinematic world transform. Static
/// obstacles and borders use zero velocity; collision asteroids use the
/// body's gameplay-supplied linear/angular velocity. This does not invent
/// Unity Rigidbody2D collision impulses, asteroid AI or prefab geometry.
#[derive(Clone, Debug)]
pub struct ObstacleBody {
    pub id: i64,
    pub pose: Pose,
    pub linear_velocity: Vec2,
    pub angular_velocity_degrees: f32,
    pub layer: u8,
    pub active: bool,
    /// Enable ship/obstacle and obstacle/obstacle hull contact reporting.
    /// Gameplay selects the participants; static decorations do not initiate
    /// expensive pair searches.
    pub tracks_body_contacts: bool,
    collider: WorldCollider,
    proximity_layer: Option<u8>,
    proximity_collider: Option<WorldCollider>,
    /// Navigation-only collider. Never enter it into gameplay contact pairs.
    /// Unity Pathfinder.GetObstaclePoints uses this shape preferentially.
    clearance_layer: Option<u8>,
    clearance_collider: Option<WorldCollider>,
}

impl ObstacleBody {
    pub fn new(
        id: i64,
        pose: Pose,
        layer: u8,
        shape: ShipShape,
        linear_velocity: Vec2,
        angular_velocity_degrees: f32,
    ) -> Result<Self, PhysicsError> {
        if layer >= 32 || !pose.position.is_finite() ||
            !pose.rotation_degrees.is_finite() ||
            !linear_velocity.is_finite() ||
            !angular_velocity_degrees.is_finite()
        {
            return Err(PhysicsError::InvalidCollider);
        }
        let mut body = Self {
            id, pose, linear_velocity, angular_velocity_degrees, layer,
            active: true, tracks_body_contacts: false,
            collider: WorldCollider::new(shape)?,
            proximity_layer: None, proximity_collider: None,
            clearance_layer: None, clearance_collider: None,
        };
        body.update_hull();
        Ok(body)
    }

    pub fn shape(&self) -> &ShipShape {
        &self.collider.shape
    }

    /// Authored proximity is a distinct child trigger, not an enlarged
    /// approximation of the primary collision hull.
    pub fn set_proximity_collider(
        &mut self,
        layer: u8,
        shape: ShipShape,
    ) -> Result<(), PhysicsError> {
        if layer >= 32 {
            return Err(PhysicsError::InvalidCollider);
        }
        let mut collider = WorldCollider::new(shape)?;
        collider.update(self.pose);
        self.proximity_layer = Some(layer);
        self.proximity_collider = Some(collider);
        Ok(())
    }

    pub fn set_clearance_mapping_collider(
        &mut self,
        layer: u8,
        shape: ShipShape,
    ) -> Result<(), PhysicsError> {
        if layer >= 32 {
            return Err(PhysicsError::InvalidCollider);
        }
        let mut collider = WorldCollider::new(shape)?;
        collider.update(self.pose);
        self.clearance_layer = Some(layer);
        self.clearance_collider = Some(collider);
        Ok(())
    }

    /// Source of truth for dynamic pathfinding obstacle bounds after the
    /// latest physics step. Physics keeps the collider updated alongside
    /// its primary hull without adding a second collision pair stream.
    pub fn navigation_bounds(&self) -> Option<Aabb> {
        if !self.active { return None; }
        self.clearance_collider.as_ref().unwrap_or(&self.collider).bounds
    }

    /// MapObject.Collider is its own authored trigger, not the obstacle's
    /// optional pathfinder clearance shape. Project its primary bounds into
    /// native perception without inventing a sprite AABB.
    pub fn primary_bounds(&self) -> Option<Aabb> {
        if !self.active { return None; }
        self.collider.bounds
    }

    /// MapObject visibility uses its primary collider, whereas Obstacle
    /// visibility uses the clearance mapping collider when available.
    pub fn vision_proximity_primary(
        &mut self, observer: Vec2, range_squared: f32,
    ) -> VisionProximity {
        if !self.active {
            return VisionProximity::RejectedByBounds;
        }
        self.update_hull();
        self.collider.vision_proximity(observer, range_squared)
    }

    pub fn navigation_shape(&self) -> &ShipShape {
        &self.clearance_collider.as_ref().unwrap_or(&self.collider).shape
    }

    /// Unity selects the clearance-mapping collider when one exists,
    /// otherwise the primary Collider2D. The authored active hull is used,
    /// not an approximate sprite rectangle or the proximity trigger.
    pub fn vision_proximity(
        &mut self, observer: Vec2, range_squared: f32,
    ) -> VisionProximity {
        if !self.active {
            return VisionProximity::RejectedByBounds;
        }
        self.update_hull();
        self.clearance_collider.as_ref().unwrap_or(&self.collider)
            .vision_proximity(observer, range_squared)
    }

    pub fn navigation_contains_point(&self, point: Vec2) -> bool {
        self.active &&
            self.clearance_collider.as_ref().unwrap_or(&self.collider)
                .contains_point(point)
    }

    pub fn navigation_closest_point(&self, point: Vec2) -> Option<Vec2> {
        if !self.active { return None; }
        Some(self.clearance_collider.as_ref().unwrap_or(&self.collider)
            .closest_point(point))
    }

    fn update_hull(&mut self) {
        self.collider.update(self.pose);
        if let Some(proximity) = &mut self.proximity_collider {
            proximity.update(self.pose);
        }
        if let Some(clearance) = &mut self.clearance_collider {
            clearance.update(self.pose);
        }
    }
}

/// Closest collider reached by a moving ship-sized rectangular cast.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct MovementObstacleHit {
    pub obstacle_id: i64,
    pub distance: f32,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PhysicsStepCounts {
    pub integrated_bodies: u64,
    pub broadphase_candidates: u64,
    pub narrowphase_tests: u64,
    pub trigger_enters: u64,
    pub obstacle_trigger_enters: u64,
    pub ship_obstacle_enters: u64,
    pub ship_obstacle_exits: u64,
    pub obstacle_pair_enters: u64,
    pub obstacle_pair_exits: u64,
    pub proximity_ship_enters: u64,
    pub proximity_ship_exits: u64,
    pub proximity_obstacle_enters: u64,
    pub proximity_obstacle_exits: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PhysicsError {
    InvalidBounds,
    InvalidCollider,
    DuplicateEntityId,
    ActiveProjectileSlot,
    MissingProjectileSlot,
    ActiveObstacleSlot,
    MissingObstacleSlot,
    InvalidStep,
    UnsupportedPolicyActions,
}

/// A dense, world-local uniform spatial grid. Ship hulls span all their
/// occupied cells. All allocations are reused between ticks. Out-of-bounds
/// ships are tracked separately rather than silently disappearing.
struct SpatialGrid {
    area: Aabb,
    cell_size: f32,
    width: usize,
    height: usize,
    cells: Vec<Vec<usize>>,
    touched: Vec<usize>,
    overflow: Vec<usize>,
    seen: Vec<u32>,
    generation: u32,
}

impl SpatialGrid {
    fn new(area: Aabb, cell_size: f32) -> Result<Self, PhysicsError> {
        let dx = area.max.x - area.min.x;
        let dy = area.max.y - area.min.y;
        if !cell_size.is_finite() || cell_size <= 0.0
            || !dx.is_finite() || !dy.is_finite() || dx <= 0.0 || dy <= 0.0
        {
            return Err(PhysicsError::InvalidBounds);
        }
        let width = (dx / cell_size).ceil() as usize;
        let height = (dy / cell_size).ceil() as usize;
        let count = width.checked_mul(height).ok_or(PhysicsError::InvalidBounds)?;
        if count == 0 || count > 1_000_000 {
            return Err(PhysicsError::InvalidBounds);
        }
        Ok(Self {
            area, cell_size, width, height,
            cells: vec![Vec::new(); count],
            touched: Vec::new(), overflow: Vec::new(),
            seen: Vec::new(), generation: 0,
        })
    }

    fn range(&self, aabb: Aabb) -> Option<(usize, usize, usize, usize)> {
        if !self.area.overlaps(aabb) {
            return None;
        }
        let cell_x = |x: f32| ((x - self.area.min.x) / self.cell_size)
            .floor().clamp(0.0, (self.width - 1) as f32) as usize;
        let cell_y = |y: f32| ((y - self.area.min.y) / self.cell_size)
            .floor().clamp(0.0, (self.height - 1) as f32) as usize;
        Some((
            cell_x(aabb.min.x), cell_x(aabb.max.x),
            cell_y(aabb.min.y), cell_y(aabb.max.y),
        ))
    }

    fn rebuild_with(&mut self, item_count: usize, mut item_bounds: impl FnMut(usize) -> Option<Aabb>) {
        for &index in &self.touched {
            self.cells[index].clear();
        }
        self.touched.clear();
        self.overflow.clear();
        self.seen.resize(item_count, 0);
        for ship_index in 0..item_count {
            let Some(bounds) = item_bounds(ship_index) else { continue };
            // If a hull protrudes beyond the grid, it must remain queryable
            // even by a projectile entirely outside the grid.
            if bounds.min.x < self.area.min.x || bounds.min.y < self.area.min.y
                || bounds.max.x > self.area.max.x || bounds.max.y > self.area.max.y
            {
                self.overflow.push(ship_index);
                continue;
            }
            if let Some((min_x, max_x, min_y, max_y)) = self.range(bounds) {
                for y in min_y..=max_y {
                    for x in min_x..=max_x {
                        let index = y * self.width + x;
                        if self.cells[index].is_empty() {
                            self.touched.push(index);
                        }
                        self.cells[index].push(ship_index);
                    }
                }
            }
        }
    }

    fn candidates(&mut self, bounds: Aabb, output: &mut Vec<usize>) {
        output.clear();
        self.generation = self.generation.wrapping_add(1);
        if self.generation == 0 {
            self.seen.fill(0);
            self.generation = 1;
        }
        for &ship_index in &self.overflow {
            self.seen[ship_index] = self.generation;
            output.push(ship_index);
        }
        if let Some((min_x, max_x, min_y, max_y)) = self.range(bounds) {
            for y in min_y..=max_y {
                for x in min_x..=max_x {
                    for &ship_index in &self.cells[y * self.width + x] {
                        if self.seen[ship_index] != self.generation {
                            self.seen[ship_index] = self.generation;
                            output.push(ship_index);
                        }
                    }
                }
            }
        }
    }
}

/// The same physical scene type will be owned by graphical and headless
/// worlds. The caller supplies authored movement velocities and consumes
/// trigger entries; no PPO/Unity/rendering dependency is present.
pub struct KinematicPhysics {
    ships: Vec<ShipBody>,
    projectiles: Vec<ProjectileBody>,
    obstacles: Vec<ObstacleBody>,
    grid: SpatialGrid,
    candidates: Vec<usize>,
    overlaps_previous: Vec<TriggerEnter>,
    overlaps_current: Vec<TriggerEnter>,
    entries: Vec<TriggerEnter>,
    obstacle_overlaps_previous: Vec<ObstacleTriggerEnter>,
    obstacle_overlaps_current: Vec<ObstacleTriggerEnter>,
    obstacle_entries: Vec<ObstacleTriggerEnter>,
    ship_obstacle_previous: Vec<ShipObstacleContact>,
    ship_obstacle_current: Vec<ShipObstacleContact>,
    ship_obstacle_enters: Vec<ShipObstacleContact>,
    ship_obstacle_exits: Vec<ShipObstacleContact>,
    obstacle_pairs_previous: Vec<ObstaclePairContact>,
    obstacle_pairs_current: Vec<ObstaclePairContact>,
    obstacle_pair_enters: Vec<ObstaclePairContact>,
    obstacle_pair_exits: Vec<ObstaclePairContact>,
    proximity_ship_previous: Vec<ShipObstacleContact>,
    proximity_ship_current: Vec<ShipObstacleContact>,
    proximity_ship_enters: Vec<ShipObstacleContact>,
    proximity_ship_exits: Vec<ShipObstacleContact>,
    proximity_obstacle_previous: Vec<ObstacleProximityContact>,
    proximity_obstacle_current: Vec<ObstacleProximityContact>,
    proximity_obstacle_enters: Vec<ObstacleProximityContact>,
    proximity_obstacle_exits: Vec<ObstacleProximityContact>,
    counts: PhysicsStepCounts,
}

impl KinematicPhysics {
    pub fn new(area: Aabb, cell_size: f32) -> Result<Self, PhysicsError> {
        Ok(Self {
            ships: Vec::new(),
            projectiles: Vec::new(),
            obstacles: Vec::new(),
            grid: SpatialGrid::new(area, cell_size)?,
            candidates: Vec::new(),
            overlaps_previous: Vec::new(),
            overlaps_current: Vec::new(),
            entries: Vec::new(),
            obstacle_overlaps_previous: Vec::new(),
            obstacle_overlaps_current: Vec::new(),
            obstacle_entries: Vec::new(),
            ship_obstacle_previous: Vec::new(),
            ship_obstacle_current: Vec::new(),
            ship_obstacle_enters: Vec::new(),
            ship_obstacle_exits: Vec::new(),
            obstacle_pairs_previous: Vec::new(),
            obstacle_pairs_current: Vec::new(),
            obstacle_pair_enters: Vec::new(),
            obstacle_pair_exits: Vec::new(),
            proximity_ship_previous: Vec::new(),
            proximity_ship_current: Vec::new(),
            proximity_ship_enters: Vec::new(),
            proximity_ship_exits: Vec::new(),
            proximity_obstacle_previous: Vec::new(),
            proximity_obstacle_current: Vec::new(),
            proximity_obstacle_enters: Vec::new(),
            proximity_obstacle_exits: Vec::new(),
            counts: PhysicsStepCounts::default(),
        })
    }

    /// Test the actual authored ship colliders at their current poses.
    /// Striker.TryToDropBombs requires Ship.Collider.IsTouching rather than
    /// proximity or center distance. This on-demand query avoids paying
    /// for every ship/ship pair when no loaded Striker requests its special.
    /// It refreshes each cached collider, including newly spawned ships.
    pub fn ship_hulls_overlap(
        &mut self, a: usize, b: usize, collisions: &CollisionMatrix,
    ) -> bool {
        if a == b || a >= self.ships.len() || b >= self.ships.len() ||
            !self.ships[a].active || !self.ships[b].active ||
            !collisions.allows(self.ships[a].layer, self.ships[b].layer) {
            return false;
        }
        self.ships[a].update_hull();
        self.ships[b].update_hull();
        self.ships[a].collider.overlaps(&self.ships[b].collider)
    }

    pub fn ships(&self) -> &[ShipBody] { &self.ships }
    pub fn ships_mut(&mut self) -> &mut [ShipBody] { &mut self.ships }
    pub fn projectiles(&self) -> &[ProjectileBody] { &self.projectiles }
    pub fn projectiles_mut(&mut self) -> &mut [ProjectileBody] { &mut self.projectiles }
    pub fn counts(&self) -> PhysicsStepCounts { self.counts }
    pub fn trigger_entries(&self) -> &[TriggerEnter] { &self.entries }
    pub fn obstacle_trigger_entries(&self) -> &[ObstacleTriggerEnter] { &self.obstacle_entries }
    pub fn obstacles(&self) -> &[ObstacleBody] { &self.obstacles }
    pub fn obstacles_mut(&mut self) -> &mut [ObstacleBody] { &mut self.obstacles }

    /// Exact shape sweep of the same rotated sprite-sized rectangle that
    /// Ship.Geometry.GetObstacleInPath passes to Physics2D.BoxCastNonAlloc.
    /// The caller filters gameplay kind and IsDead, as ShouldAvoidObstacle
    /// does in Unity. Only primary layer-19 obstacle hulls are consulted:
    /// map borders, child proximity and pathfinder clearance are not casts.
    /// Collider ordering ties are broken by stable entity ID.
    pub fn cast_ship_movement_box(
        &self,
        origin: Vec2,
        size: Vec2,
        angle_degrees: f32,
        destination: Vec2,
        mut should_avoid: impl FnMut(&ObstacleBody) -> bool,
    ) -> Result<Option<MovementObstacleHit>, PhysicsError> {
        if !origin.is_finite() || !destination.is_finite() ||
            !size.is_finite() || size.x <= 0.0 || size.y <= 0.0 ||
            !angle_degrees.is_finite() {
            return Err(PhysicsError::InvalidCollider);
        }
        let delta = destination - origin;
        let distance = delta.length_squared().sqrt();
        if !distance.is_finite() { return Err(PhysicsError::InvalidCollider); }
        let direction = if distance > 0.0 {
            delta * (1.0 / distance)
        } else { Vec2::ZERO };
        let half = size * 0.5;
        let pose = Pose { position: origin, rotation_degrees: angle_degrees };
        let corners = [
            pose.transform_point(Vec2::new(-half.x, -half.y)),
            pose.transform_point(Vec2::new(half.x, -half.y)),
            pose.transform_point(Vec2::new(half.x, half.y)),
            pose.transform_point(Vec2::new(-half.x, half.y)),
        ];
        let Some(initial_bounds) = Aabb::from_points(&corners) else {
            return Err(PhysicsError::InvalidCollider);
        };
        let sweep_bounds = Aabb {
            min: Vec2::new(
                initial_bounds.min.x.min(initial_bounds.min.x + delta.x),
                initial_bounds.min.y.min(initial_bounds.min.y + delta.y),
            ),
            max: Vec2::new(
                initial_bounds.max.x.max(initial_bounds.max.x + delta.x),
                initial_bounds.max.y.max(initial_bounds.max.y + delta.y),
            ),
        };
        let mut closest: Option<MovementObstacleHit> = None;
        for obstacle in &self.obstacles {
            if !obstacle.active || obstacle.layer != 19 ||
                !should_avoid(obstacle) { continue; }
            let collider = &obstacle.collider;
            if !collider.bounds.is_some_and(|bounds| bounds.overlaps(sweep_bounds)) {
                continue;
            }
            let distance_to_hit = match &collider.shape {
                ShipShape::Circle { radius, .. } => swept_box_circle_distance(
                    &corners, direction, distance, collider.circle_world, *radius,
                ),
                _ => swept_box_polygon_distance(
                    &corners, direction, distance, &collider.polygon_world,
                ),
            };
            if let Some(hit) = distance_to_hit {
                let choose = closest.is_none_or(|prior|
                    hit < prior.distance ||
                    (hit == prior.distance && obstacle.id < prior.obstacle_id));
                if choose {
                    closest = Some(MovementObstacleHit {
                        obstacle_id: obstacle.id, distance: hit,
                    });
                }
            }
        }
        Ok(closest)
    }
    pub fn ship_obstacle_entries(&self) -> &[ShipObstacleContact] { &self.ship_obstacle_enters }
    pub fn ship_obstacle_exits(&self) -> &[ShipObstacleContact] { &self.ship_obstacle_exits }
    pub fn obstacle_pair_entries(&self) -> &[ObstaclePairContact] { &self.obstacle_pair_enters }
    pub fn obstacle_pair_exits(&self) -> &[ObstaclePairContact] { &self.obstacle_pair_exits }
    pub fn proximity_ship_entries(&self) -> &[ShipObstacleContact] { &self.proximity_ship_enters }
    pub fn proximity_ship_exits(&self) -> &[ShipObstacleContact] { &self.proximity_ship_exits }
    pub fn proximity_obstacle_entries(&self) -> &[ObstacleProximityContact] { &self.proximity_obstacle_enters }
    pub fn proximity_obstacle_exits(&self) -> &[ObstacleProximityContact] { &self.proximity_obstacle_exits }

    /// Obstacles retain distinct IDs across the unified world. Their
    /// gameplay kind, health and destruction state live in CombatWorld.
    pub fn add_obstacle(&mut self, body: ObstacleBody) -> Result<usize, PhysicsError> {
        if self.obstacles.iter().any(|other| other.id == body.id) {
            return Err(PhysicsError::DuplicateEntityId);
        }
        let index = self.obstacles.len();
        self.obstacles.push(body);
        Ok(index)
    }

    pub fn add_ship(&mut self, ship: ShipBody) -> Result<usize, PhysicsError> {
        if self.ships.iter().any(|other| other.id == ship.id) {
            return Err(PhysicsError::DuplicateEntityId);
        }
        let index = self.ships.len();
        self.ships.push(ship);
        Ok(index)
    }

    pub fn add_projectile(&mut self, projectile: ProjectileBody) -> Result<usize, PhysicsError> {
        if self.projectiles.iter().any(|other| other.id == projectile.id) {
            return Err(PhysicsError::DuplicateEntityId);
        }
        let index = self.projectiles.len();
        self.projectiles.push(projectile);
        Ok(index)
    }

    /// Recycle the physical storage of an ended projectile without allowing
    /// a new shot to inherit its old contact cache. Projectile IDs remain
    /// unique; the trigger pair set is keyed by ID, never by the array slot.
    pub fn replace_ended_projectile(
        &mut self,
        index: usize,
        projectile: ProjectileBody,
    ) -> Result<(), PhysicsError> {
        let slot = self.projectiles.get_mut(index)
            .ok_or(PhysicsError::MissingProjectileSlot)?;
        if slot.active {
            return Err(PhysicsError::ActiveProjectileSlot);
        }
        *slot = projectile;
        Ok(())
    }

    /// Reuse a destroyed obstacle's physical slot. Entity IDs are never
    /// reused; trigger pairs refer to IDs, not to array indices.
    pub fn replace_ended_obstacle(
        &mut self,
        index: usize,
        obstacle: ObstacleBody,
    ) -> Result<(), PhysicsError> {
        let slot = self.obstacles.get_mut(index)
            .ok_or(PhysicsError::MissingObstacleSlot)?;
        if slot.active {
            return Err(PhysicsError::ActiveObstacleSlot);
        }
        *slot = obstacle;
        Ok(())
    }

    /// Advance kinematic positions once. Set body velocities/headings from
    /// the shared gameplay controllers before calling this. Trigger contacts
    /// are based on the resulting discrete state, never swept from A to B.
    pub fn step(&mut self, dt: f32, collision_matrix: &CollisionMatrix) -> Result<&[TriggerEnter], PhysicsError> {
        if !dt.is_finite() || dt <= 0.0 {
            return Err(PhysicsError::InvalidStep);
        }
        self.counts = PhysicsStepCounts::default();
        for ship in &mut self.ships {
            if !ship.active { continue; }
            ship.motion.position += ship.motion.body_velocity * dt;
            ship.update_hull();
            self.counts.integrated_bodies += 1;
        }
        for projectile in &mut self.projectiles {
            if !projectile.active { continue; }
            if projectile.has_body {
                projectile.motion.position += projectile.motion.velocity * dt;
                self.counts.integrated_bodies += 1;
            }
            projectile.update_hull();
        }
        for obstacle in &mut self.obstacles {
            if !obstacle.active { continue; }
            obstacle.pose.position += obstacle.linear_velocity * dt;
            obstacle.pose.rotation_degrees += obstacle.angular_velocity_degrees * dt;
            obstacle.update_hull();
            self.counts.integrated_bodies += 1;
        }
        self.grid.rebuild_with(self.ships.len(), |index| {
            let ship = &self.ships[index];
            if ship.active { ship.collider.bounds } else { None }
        });
        self.overlaps_current.clear();
        for projectile in &self.projectiles {
            if !projectile.active { continue; }
            let Some(projectile_collider) = &projectile.collider else { continue };
            let Some(area) = projectile_collider.bounds else { continue };
            self.grid.candidates(area, &mut self.candidates);
            self.counts.broadphase_candidates += self.candidates.len() as u64;
            for &index in &self.candidates {
                let ship = &self.ships[index];
                if !ship.active || !collision_matrix.allows(projectile.layer, ship.layer) {
                    continue;
                }
                let Some(bounds) = ship.collider.bounds else { continue };
                if !area.overlaps(bounds) {
                    continue;
                }
                self.counts.narrowphase_tests += 1;
                if ship.collider.overlaps(projectile_collider) {
                    self.overlaps_current.push(TriggerEnter {
                        projectile_id: projectile.id,
                        ship_id: ship.id,
                    });
                }
            }
        }
        // Proximity-to-ship checks reuse the ship grid before it is rebuilt
        // for obstacles. Unity's child layer is evaluated independently
        // from the primary asteroid hull layer.
        self.proximity_ship_current.clear();
        for obstacle in &self.obstacles {
            if !obstacle.active || !obstacle.tracks_body_contacts { continue; }
            let (Some(proximity), Some(layer)) = (
                obstacle.proximity_collider.as_ref(), obstacle.proximity_layer,
            ) else { continue };
            let Some(bounds) = proximity.bounds else { continue };
            self.grid.candidates(bounds, &mut self.candidates);
            self.counts.broadphase_candidates += self.candidates.len() as u64;
            for &index in &self.candidates {
                let ship = &self.ships[index];
                if !ship.active || !collision_matrix.allows(layer, ship.layer) {
                    continue;
                }
                let Some(other_bounds) = ship.collider.bounds else { continue };
                if !bounds.overlaps(other_bounds) { continue; }
                self.counts.narrowphase_tests += 1;
                if proximity.overlaps(&ship.collider) {
                    self.proximity_ship_current.push(ShipObstacleContact {
                        ship_id: ship.id, obstacle_id: obstacle.id,
                    });
                }
            }
        }
        update_contact_changes(
            &mut self.proximity_ship_current, &mut self.proximity_ship_previous,
            &mut self.proximity_ship_enters, &mut self.proximity_ship_exits,
        );
        self.counts.proximity_ship_enters = self.proximity_ship_enters.len() as u64;
        self.counts.proximity_ship_exits = self.proximity_ship_exits.len() as u64;

        // Rebuild the SAME uniform grid for obstacle queries after all
        // ship queries finish. Two grids double the per-world cell-array
        // footprint and buy nothing when these phases are serial.
        self.grid.rebuild_with(self.obstacles.len(), |index| {
            let obstacle = &self.obstacles[index];
            if obstacle.active { obstacle.collider.bounds } else { None }
        });
        // Obstacle contacts retain their own overlap history and queue.
        // A shot can contact a ship and an obstacle in the same physics tick.
        self.obstacle_overlaps_current.clear();
        for projectile in &self.projectiles {
            if !projectile.active { continue; }
            let Some(projectile_collider) = &projectile.collider else { continue };
            let Some(area) = projectile_collider.bounds else { continue };
            self.grid.candidates(area, &mut self.candidates);
            self.counts.broadphase_candidates += self.candidates.len() as u64;
            for &index in &self.candidates {
                let obstacle = &self.obstacles[index];
                if !obstacle.active ||
                    !collision_matrix.allows(projectile.layer, obstacle.layer) {
                    continue;
                }
                let Some(bounds) = obstacle.collider.bounds else { continue };
                if !area.overlaps(bounds) { continue; }
                self.counts.narrowphase_tests += 1;
                if obstacle.collider.overlaps(projectile_collider) {
                    self.obstacle_overlaps_current.push(ObstacleTriggerEnter {
                        projectile_id: projectile.id,
                        obstacle_id: obstacle.id,
                    });
                }
            }
        }
        // These overlap streams describe PRIMARY body geometry only. Unity
        // CollisionAsteroid also has a separate proximity collider; that
        // proximity state must be imported before combat damage is enabled.
        self.proximity_obstacle_current.clear();
        for source in &self.obstacles {
            if !source.active || !source.tracks_body_contacts { continue; }
            let (Some(proximity), Some(layer)) = (
                source.proximity_collider.as_ref(), source.proximity_layer,
            ) else { continue };
            let Some(bounds) = proximity.bounds else { continue };
            self.grid.candidates(bounds, &mut self.candidates);
            self.counts.broadphase_candidates += self.candidates.len() as u64;
            for &index in &self.candidates {
                let other = &self.obstacles[index];
                if !other.active || other.id == source.id ||
                    !collision_matrix.allows(layer, other.layer) {
                    continue;
                }
                let Some(other_bounds) = other.collider.bounds else { continue };
                if !bounds.overlaps(other_bounds) { continue; }
                self.counts.narrowphase_tests += 1;
                if proximity.overlaps(&other.collider) {
                    self.proximity_obstacle_current.push(ObstacleProximityContact {
                        source_obstacle_id: source.id, other_obstacle_id: other.id,
                    });
                }
            }
        }
        update_contact_changes(
            &mut self.proximity_obstacle_current, &mut self.proximity_obstacle_previous,
            &mut self.proximity_obstacle_enters, &mut self.proximity_obstacle_exits,
        );
        self.counts.proximity_obstacle_enters = self.proximity_obstacle_enters.len() as u64;
        self.counts.proximity_obstacle_exits = self.proximity_obstacle_exits.len() as u64;

        self.ship_obstacle_current.clear();
        for ship in &self.ships {
            if !ship.active { continue; }
            let Some(bounds) = ship.collider.bounds else { continue };
            self.grid.candidates(bounds, &mut self.candidates);
            self.counts.broadphase_candidates += self.candidates.len() as u64;
            for &index in &self.candidates {
                let obstacle = &self.obstacles[index];
                if !obstacle.active || !obstacle.tracks_body_contacts ||
                    !collision_matrix.allows(ship.layer, obstacle.layer) {
                    continue;
                }
                let Some(obstacle_bounds) = obstacle.collider.bounds else { continue };
                if !bounds.overlaps(obstacle_bounds) { continue; }
                self.counts.narrowphase_tests += 1;
                if ship.collider.overlaps(&obstacle.collider) {
                    self.ship_obstacle_current.push(ShipObstacleContact {
                        ship_id: ship.id, obstacle_id: obstacle.id,
                    });
                }
            }
        }
        self.obstacle_pairs_current.clear();
        for (source_index, source) in self.obstacles.iter().enumerate() {
            if !source.active || !source.tracks_body_contacts { continue; }
            let Some(bounds) = source.collider.bounds else { continue };
            self.grid.candidates(bounds, &mut self.candidates);
            self.counts.broadphase_candidates += self.candidates.len() as u64;
            for &index in &self.candidates {
                let other = &self.obstacles[index];
                if !other.active || index == source_index ||
                    (other.tracks_body_contacts && index < source_index) ||
                    !collision_matrix.allows(source.layer, other.layer) {
                    continue;
                }
                let Some(other_bounds) = other.collider.bounds else { continue };
                if !bounds.overlaps(other_bounds) { continue; }
                self.counts.narrowphase_tests += 1;
                if source.collider.overlaps(&other.collider) {
                    self.obstacle_pairs_current.push(ObstaclePairContact {
                        first_obstacle_id: source.id.min(other.id),
                        second_obstacle_id: source.id.max(other.id),
                    });
                }
            }
        }
        update_contact_changes(
            &mut self.ship_obstacle_current, &mut self.ship_obstacle_previous,
            &mut self.ship_obstacle_enters, &mut self.ship_obstacle_exits,
        );
        self.counts.ship_obstacle_enters = self.ship_obstacle_enters.len() as u64;
        self.counts.ship_obstacle_exits = self.ship_obstacle_exits.len() as u64;
        update_contact_changes(
            &mut self.obstacle_pairs_current, &mut self.obstacle_pairs_previous,
            &mut self.obstacle_pair_enters, &mut self.obstacle_pair_exits,
        );
        self.counts.obstacle_pair_enters = self.obstacle_pair_enters.len() as u64;
        self.counts.obstacle_pair_exits = self.obstacle_pair_exits.len() as u64;

        self.obstacle_overlaps_current.sort_unstable();
        self.obstacle_overlaps_current.dedup();
        self.obstacle_entries.clear();
        for &pair in &self.obstacle_overlaps_current {
            if self.obstacle_overlaps_previous.binary_search(&pair).is_err() {
                self.obstacle_entries.push(pair);
            }
        }
        self.counts.obstacle_trigger_enters = self.obstacle_entries.len() as u64;
        std::mem::swap(
            &mut self.obstacle_overlaps_previous,
            &mut self.obstacle_overlaps_current,
        );
        // Stable event ordering is necessary for reproducible outcome credit.
        // Unity callback order may differ: parity must be checked explicitly.
        self.overlaps_current.sort_unstable();
        self.overlaps_current.dedup();
        self.entries.clear();
        for &pair in &self.overlaps_current {
            if self.overlaps_previous.binary_search(&pair).is_err() {
                self.entries.push(pair);
            }
        }
        self.counts.trigger_enters = self.entries.len() as u64;
        std::mem::swap(&mut self.overlaps_previous, &mut self.overlaps_current);
        Ok(&self.entries)
    }
}


/// Minimal physics-only entrypoint for the existing multi-world scheduler.
/// This is not a gameplay or training rules implementation: actions are
/// explicitly rejected until the complete shared game controller is ported.
pub struct KinematicPhysicsRules {
    pub collision_matrix: CollisionMatrix,
}

impl crate::simulation::GameRules<KinematicPhysics> for KinematicPhysicsRules {
    type Error = PhysicsError;

    fn step(
        &self,
        world: &mut crate::simulation::World<KinematicPhysics>,
        decisions: &[crate::policy::Decision],
        dt_seconds: f32,
    ) -> Result<crate::telemetry::StepCounts, Self::Error> {
        if !decisions.is_empty() {
            return Err(PhysicsError::UnsupportedPolicyActions);
        }
        world.state.step(dt_seconds, &self.collision_matrix)?;
        let counts = world.state.counts();
        Ok(crate::telemetry::StepCounts {
            decisions: 0,
            active_ships: world.state.ships().iter().filter(|ship| ship.active).count() as u32,
            active_projectiles: world.state.projectiles().iter().filter(|shot| shot.active).count() as u32,
            events: counts.trigger_enters + counts.obstacle_trigger_enters +
                counts.ship_obstacle_enters + counts.ship_obstacle_exits +
                counts.obstacle_pair_enters + counts.obstacle_pair_exits +
                counts.proximity_ship_enters + counts.proximity_ship_exits +
                counts.proximity_obstacle_enters + counts.proximity_obstacle_exits,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::projectile::SMALL_LASER_SHOT_RADIUS;
    use crate::ship_assets::ShipAssetCatalog;
    use crate::projectile_assets::ProjectileAssetCatalog;

    fn gunship_shape() -> ShipShape {
        ShipAssetCatalog::embedded().unwrap().by_type(13).unwrap().shape.clone()
    }

    fn matrix_for_test() -> CollisionMatrix {
        let mut rows = [0u32; 32];
        rows[10] = 1u32 << 12;
        CollisionMatrix::from_rows(rows)
    }

    #[test]
    fn trigger_enters_only_once_and_respects_collision_layers() {
        let mut physics = KinematicPhysics::new(
            Aabb { min: Vec2::new(-25.0, -25.0), max: Vec2::new(25.0, 25.0) },
            8.0,
        ).unwrap();
        physics.add_ship(ShipBody::new(
            2,
            ShipMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 4.0),
            12,
            gunship_shape(),
        ).unwrap()).unwrap();
        physics.add_projectile(ProjectileBody::new(
            5,
            ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 10.0, 5, 1),
            10,
            SMALL_LASER_SHOT_RADIUS,
        ).unwrap()).unwrap();

        assert_eq!(physics.step(0.02, &matrix_for_test()).unwrap().len(), 1);
        assert!(physics.step(0.02, &matrix_for_test()).unwrap().is_empty());
        assert_eq!(physics.counts().narrowphase_tests, 1);
        physics.projectiles_mut()[0].motion.position = Vec2::new(20.0, 20.0);
        assert!(physics.step(0.02, &matrix_for_test()).unwrap().is_empty());
        physics.projectiles_mut()[0].motion.position = Vec2::ZERO;
        assert_eq!(physics.step(0.02, &matrix_for_test()).unwrap().len(), 1);

        let blocked = CollisionMatrix::from_rows([0; 32]);
        assert!(physics.step(0.02, &blocked).unwrap().is_empty());
    }

    #[test]
    fn shared_simulation_scheduler_executes_real_collision_step() {
        use crate::simulation::{Simulation, World, WorldId};

        let mut physics = KinematicPhysics::new(
            Aabb { min: Vec2::new(-16.0, -16.0), max: Vec2::new(16.0, 16.0) },
            8.0,
        ).unwrap();
        physics.add_ship(ShipBody::new(
            1, ShipMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 4.0),
            12, gunship_shape(),
        ).unwrap()).unwrap();
        physics.add_projectile(ProjectileBody::new(
            2, ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 10.0, 2, 0),
            10, SMALL_LASER_SHOT_RADIUS,
        ).unwrap()).unwrap();

        let mut scheduler = Simulation::new(
            KinematicPhysicsRules { collision_matrix: matrix_for_test() },
            0.02,
        ).unwrap();
        scheduler.add_world(World::new(WorldId(5), 123, physics)).unwrap();
        let first = scheduler.step_world(0, &[]).unwrap();
        assert_eq!(first.events, 1);
        let second = scheduler.step_world(0, &[]).unwrap();
        assert_eq!(second.events, 0);
        assert_eq!(scheduler.measurements().ticks, 2);
        assert_eq!(scheduler.measurements().events, 1);
    }

    #[test]
    fn box_and_circle_hulls_use_authored_shapes_not_polygon_approximations() {
        let mut physics = KinematicPhysics::new(
            Aabb { min: Vec2::new(-10.0, -10.0), max: Vec2::new(10.0, 10.0) },
            4.0,
        ).unwrap();
        physics.add_ship(ShipBody::new(
            1, ShipMotion::new(Vec2::new(0.0, 0.0), 30.0, 0.0, 0.0, 2.0),
            12, ShipShape::Box {
                offset: Vec2::ZERO, half_extents: Vec2::new(2.0, 1.0),
            },
        ).unwrap()).unwrap();
        physics.add_ship(ShipBody::new(
            2, ShipMotion::new(Vec2::new(6.0, 0.0), 0.0, 0.0, 0.0, 2.0),
            12, ShipShape::Circle {
                offset: Vec2::new(0.5, 0.0), radius: 1.0,
            },
        ).unwrap()).unwrap();
        physics.add_projectile(ProjectileBody::new(
            3, ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 10.0, 3, 0),
            10, 0.5,
        ).unwrap()).unwrap();
        assert_eq!(physics.step(0.02, &matrix_for_test()).unwrap()[0].ship_id, 1);
        physics.projectiles_mut()[0].motion.position = Vec2::new(6.5, 0.0);
        assert_eq!(physics.step(0.02, &matrix_for_test()).unwrap()[0].ship_id, 2);
    }

    #[test]
    fn flagship_box_shot_hits_polygon_with_real_asset_dimensions() {
        let catalog = ProjectileAssetCatalog::embedded().unwrap();
        let mut physics = KinematicPhysics::new(
            Aabb { min: Vec2::new(-20.0, -20.0), max: Vec2::new(20.0, 20.0) },
            8.0,
        ).unwrap();
        physics.add_ship(ShipBody::new(
            1, ShipMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 2.0),
            12, gunship_shape(),
        ).unwrap()).unwrap();
        let flagship = catalog.by_type(4).unwrap();
        assert!(matches!(&flagship.shape, Some(ShipShape::Box { .. })));
        physics.add_projectile(flagship.spawn_physics_body(
            2, ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 30.0, 2, 0),
            45.0,
        ).unwrap()).unwrap();

        let mut rows = [0u32; 32];
        rows[18] = 1u32 << 12;
        let mask = CollisionMatrix::from_rows(rows);
        assert_eq!(physics.step(0.02, &mask).unwrap(), &[TriggerEnter {
            projectile_id: 2, ship_id: 1,
        }]);
    }

    #[test]
    fn stationary_explosion_contacts_once_and_scripted_bomb_has_no_contact() {
        let catalog = ProjectileAssetCatalog::embedded().unwrap();
        let mut physics = KinematicPhysics::new(
            Aabb { min: Vec2::new(-20.0, -20.0), max: Vec2::new(20.0, 20.0) },
            8.0,
        ).unwrap();
        physics.add_ship(ShipBody::new(
            1, ShipMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 2.0),
            12, gunship_shape(),
        ).unwrap()).unwrap();

        let explosion = catalog.by_type(13).unwrap();
        assert!(!explosion.has_body);
        let moving_motion = ProjectileMotion::new(Vec2::ZERO, 100.0, 0.0, 30.0, 2, 0);
        physics.add_projectile(explosion.spawn_physics_body(
            2, moving_motion, 0.0,
        ).unwrap()).unwrap();

        let bomb = catalog.by_type(12).unwrap();
        assert!(bomb.shape.is_none());
        physics.add_projectile(bomb.spawn_physics_body(
            3, ProjectileMotion::new(Vec2::ZERO, 0.0, 0.0, 0.0, 3, 0),
            0.0,
        ).unwrap()).unwrap();

        let mut rows = [0u32; 32];
        rows[18] = 1u32 << 12;
        rows[0] = 1u32 << 12;
        let mask = CollisionMatrix::from_rows(rows);
        assert_eq!(physics.step(0.02, &mask).unwrap(),
            &[TriggerEnter { projectile_id: 2, ship_id: 1 }]);
        assert_eq!(physics.projectiles()[0].motion.position, Vec2::ZERO);
        assert!(physics.step(0.02, &mask).unwrap().is_empty());
    }

    #[test]
    fn out_of_bounds_hulls_are_not_dropped_from_grid() {
        let mut physics = KinematicPhysics::new(
            Aabb { min: Vec2::new(0.0, 0.0), max: Vec2::new(10.0, 10.0) }, 4.0,
        ).unwrap();
        physics.add_ship(ShipBody::new(
            9, ShipMotion::new(Vec2::new(-1.0, -1.0), 0.0, 0.0, 0.0, 4.0),
            12, gunship_shape(),
        ).unwrap()).unwrap();
        physics.add_projectile(ProjectileBody::new(
            10, ProjectileMotion::new(Vec2::new(-1.0, -1.0), 0.0, 0.0, 20.0, 10, 0),
            10, 0.5,
        ).unwrap()).unwrap();
        assert_eq!(physics.step(0.02, &matrix_for_test()).unwrap().len(), 1);
    }
}
