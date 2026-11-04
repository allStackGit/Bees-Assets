//! World-owned kinematic trigger physics.
//!
//! The current Unity Wasp/Gunship and small laser-shot prefabs use kinematic
//! Rigidbody2D with discrete collision detection and trigger colliders.
//! Their authored collision polygons and projectile radii are ported in
//! `hulls`. This layer integrates prescribed velocity and reports trigger
//! *entries*; it does not implement game damage, blocking, or collision
//! impulses. The gameplay core must consume the events, just as Projectile
//! consumes Unity's OnTriggerEnter2D queue.
//!
//! Exact Unity physics step/callback ordering and the current Physics2D
//! collision-layer matrix remain to be established before parity is claimed.

use std::sync::Arc;

use crate::collision::{circle_overlaps_polygon, circles_overlap, Aabb, Pose};
use crate::geometry::Vec2;
use crate::movement::ShipMotion;
use crate::projectile::ProjectileMotion;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct TriggerEnter {
    pub projectile_id: i64,
    pub ship_id: i64,
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

#[derive(Clone, Debug)]
pub struct ShipBody {
    pub id: i64,
    pub motion: ShipMotion,
    pub layer: u8,
    pub active: bool,
    pub shape: ShipShape,
    hull_world: Vec<Vec2>,
    circle_world: Vec2,
    bounds: Option<Aabb>,
    cached_pose: Option<Pose>,
}

impl ShipBody {
    pub fn new(
        id: i64,
        motion: ShipMotion,
        layer: u8,
        shape: impl Into<ShipShape>,
    ) -> Result<Self, PhysicsError> {
        let shape = shape.into();
        if layer >= 32 || !shape.valid() {
            return Err(PhysicsError::InvalidCollider);
        }
        Ok(Self {
            id, motion, layer, active: true, shape,
            hull_world: Vec::new(), circle_world: Vec2::ZERO,
            bounds: None, cached_pose: None,
        })
    }

    fn update_hull(&mut self) {
        let pose = Pose {
            position: self.motion.position,
            rotation_degrees: self.motion.rotation_degrees,
        };
        if self.cached_pose == Some(pose) {
            return;
        }
        match &self.shape {
            ShipShape::Polygon(vertices) => {
                pose.transform_into(vertices, &mut self.hull_world);
                self.bounds = Aabb::from_points(&self.hull_world);
            }
            ShipShape::Box { offset, half_extents } => {
                let x = half_extents.x;
                let y = half_extents.y;
                let points = [
                    Vec2::new(offset.x - x, offset.y - y),
                    Vec2::new(offset.x + x, offset.y - y),
                    Vec2::new(offset.x + x, offset.y + y),
                    Vec2::new(offset.x - x, offset.y + y),
                ];
                pose.transform_into(&points, &mut self.hull_world);
                self.bounds = Aabb::from_points(&self.hull_world);
            }
            ShipShape::Circle { offset, radius } => {
                self.hull_world.clear();
                self.circle_world = pose.transform_point(*offset);
                self.bounds = Some(Aabb::circle(self.circle_world, *radius));
            }
        }
        self.cached_pose = Some(pose);
    }

    #[inline]
    fn overlaps_projectile(&self, center: Vec2, radius: f32) -> bool {
        match self.shape {
            ShipShape::Circle { radius: hull_radius, .. } => {
                circles_overlap(self.circle_world, hull_radius, center, radius)
            }
            ShipShape::Polygon(_) | ShipShape::Box { .. } => {
                circle_overlaps_polygon(center, radius, &self.hull_world)
            }
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ProjectileBody {
    pub id: i64,
    pub motion: ProjectileMotion,
    pub layer: u8,
    pub radius: f32,
    pub active: bool,
}

impl ProjectileBody {
    pub fn new(id: i64, motion: ProjectileMotion, layer: u8, radius: f32) -> Result<Self, PhysicsError> {
        if layer >= 32 || !radius.is_finite() || radius < 0.0 {
            return Err(PhysicsError::InvalidCollider);
        }
        Ok(Self { id, motion, layer, radius, active: true })
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PhysicsStepCounts {
    pub moved_bodies: u64,
    pub broadphase_candidates: u64,
    pub narrowphase_tests: u64,
    pub trigger_enters: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PhysicsError {
    InvalidBounds,
    InvalidCollider,
    DuplicateEntityId,
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

    fn rebuild(&mut self, ships: &[ShipBody]) {
        for &index in &self.touched {
            self.cells[index].clear();
        }
        self.touched.clear();
        self.overflow.clear();
        self.seen.resize(ships.len(), 0);
        for (ship_index, ship) in ships.iter().enumerate() {
            if !ship.active {
                continue;
            }
            let Some(bounds) = ship.bounds else { continue };
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
    grid: SpatialGrid,
    candidates: Vec<usize>,
    overlaps_previous: Vec<TriggerEnter>,
    overlaps_current: Vec<TriggerEnter>,
    entries: Vec<TriggerEnter>,
    counts: PhysicsStepCounts,
}

impl KinematicPhysics {
    pub fn new(area: Aabb, cell_size: f32) -> Result<Self, PhysicsError> {
        Ok(Self {
            ships: Vec::new(),
            projectiles: Vec::new(),
            grid: SpatialGrid::new(area, cell_size)?,
            candidates: Vec::new(),
            overlaps_previous: Vec::new(),
            overlaps_current: Vec::new(),
            entries: Vec::new(),
            counts: PhysicsStepCounts::default(),
        })
    }

    pub fn ships(&self) -> &[ShipBody] { &self.ships }
    pub fn ships_mut(&mut self) -> &mut [ShipBody] { &mut self.ships }
    pub fn projectiles(&self) -> &[ProjectileBody] { &self.projectiles }
    pub fn projectiles_mut(&mut self) -> &mut [ProjectileBody] { &mut self.projectiles }
    pub fn counts(&self) -> PhysicsStepCounts { self.counts }
    pub fn trigger_entries(&self) -> &[TriggerEnter] { &self.entries }

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
            self.counts.moved_bodies += 1;
        }
        for projectile in &mut self.projectiles {
            if !projectile.active { continue; }
            projectile.motion.position += projectile.motion.velocity * dt;
            self.counts.moved_bodies += 1;
        }
        self.grid.rebuild(&self.ships);
        self.overlaps_current.clear();
        for projectile in &self.projectiles {
            if !projectile.active { continue; }
            let area = Aabb::circle(projectile.motion.position, projectile.radius);
            self.grid.candidates(area, &mut self.candidates);
            self.counts.broadphase_candidates += self.candidates.len() as u64;
            for &index in &self.candidates {
                let ship = &self.ships[index];
                if !ship.active || !collision_matrix.allows(projectile.layer, ship.layer) {
                    continue;
                }
                let Some(bounds) = ship.bounds else { continue };
                if !area.overlaps(bounds) {
                    continue;
                }
                self.counts.narrowphase_tests += 1;
                if ship.overlaps_projectile(projectile.motion.position, projectile.radius) {
                    self.overlaps_current.push(TriggerEnter {
                        projectile_id: projectile.id,
                        ship_id: ship.id,
                    });
                }
            }
        }
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
            events: counts.trigger_enters,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::hulls::{GUNSHIP, SMALL_LASER_SHOT_RADIUS};

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
            Arc::from(GUNSHIP),
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
            12, Arc::from(GUNSHIP),
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
    fn out_of_bounds_hulls_are_not_dropped_from_grid() {
        let mut physics = KinematicPhysics::new(
            Aabb { min: Vec2::new(0.0, 0.0), max: Vec2::new(10.0, 10.0) }, 4.0,
        ).unwrap();
        physics.add_ship(ShipBody::new(
            9, ShipMotion::new(Vec2::new(-1.0, -1.0), 0.0, 0.0, 0.0, 4.0),
            12, Arc::from(GUNSHIP),
        ).unwrap()).unwrap();
        physics.add_projectile(ProjectileBody::new(
            10, ProjectileMotion::new(Vec2::new(-1.0, -1.0), 0.0, 0.0, 20.0, 10, 0),
            10, 0.5,
        ).unwrap()).unwrap();
        assert_eq!(physics.step(0.02, &matrix_for_test()).unwrap().len(), 1);
    }
}
