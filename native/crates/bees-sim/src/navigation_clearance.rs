//! Unity Pathfinder clearance maps, owned per native combat world.
//!
//! This is the two-pass eight-neighbor clearance transform from Pathfinder.cs
//! and the static signed-clearance transform from Pathfinder.Search.cs.
//! Dynamic moving-asteroid occupancy replaces cells in a COPY of the static
//! clearance map before recalculating, exactly as Unity does. Path search may
//! read an immutable snapshot without racing a concurrent update.
use std::collections::{HashMap, VecDeque};
use std::sync::Arc;

use crate::geometry::Vec2;
use crate::navigation::{
    NavigationError, NavigationGrid, NavigationRasterCounts,
};
use crate::physics::ObstacleBody;

const UNREACHABLE_SIGNED: i32 = i32::MAX / 4;

/// Immutable, search-only clearance for an in-flight path worker. Retains no
/// static connectivity cache or obstacle raster scratch. A stable authored
/// static map is shared across frames and workers; only moving-asteroid
/// clearance is captured anew when it changes.
#[derive(Clone, Debug)]
pub struct NavigationSearchSnapshot {
    grid: NavigationGrid,
    signed_static: Arc<Vec<i32>>,
    dynamic: Vec<i32>,
}

/// The synchronous world and asynchronous workers search the identical
/// physical clearance fields without copying ownership-only map state.
pub trait NavigationSearchLayers {
    fn grid(&self) -> NavigationGrid;
    fn dynamic_clearance(&self) -> &[i32];
    fn static_signed_clearance(&self) -> &[i32];
}

impl NavigationSearchLayers for NavigationSearchSnapshot {
    fn grid(&self) -> NavigationGrid { self.grid }
    fn dynamic_clearance(&self) -> &[i32] { &self.dynamic }
    fn static_signed_clearance(&self) -> &[i32] { self.signed_static.as_slice() }
}

#[derive(Clone, Debug)]
pub struct NavigationClearance {
    grid: NavigationGrid,
    base: Vec<i32>,
    signed_static: Arc<Vec<i32>>,
    dynamic: Vec<i32>,
    moving_blocked: Vec<bool>,
    /// The dynamic field is already the static field unless an occupied
    /// collision-asteroid cell changed it. Avoid copying a full map in
    /// successive empty-asteroid fixed steps.
    dynamic_matches_static: bool,
    /// An obstacle-free rectangular map has one connected walkable region
    /// at every clearance. No per-clearance flood-fill cache is needed.
    has_static_blocked_cells: bool,
    /// Unity's static-region cache is keyed by effective ship clearance.
    /// Dynamic asteroids do not invalidate these permanent connectivity
    /// components; static obstacle updates do.
    static_components_by_clearance: HashMap<i32, Vec<i32>>,
}

impl NavigationSearchLayers for NavigationClearance {
    fn grid(&self) -> NavigationGrid { self.grid }
    fn dynamic_clearance(&self) -> &[i32] { &self.dynamic }
    fn static_signed_clearance(&self) -> &[i32] { self.signed_static.as_slice() }
}

impl NavigationClearance {
    pub fn new(grid: NavigationGrid, static_blocked: &[bool]) -> Result<Self, NavigationError> {
        if grid.width == 0 || grid.height == 0 ||
            grid.width.checked_mul(grid.height) != Some(static_blocked.len()) {
            return Err(NavigationError::IncorrectCellBuffer);
        }
        let length = static_blocked.len();
        let mut layers = Self {
            grid, base: vec![0; length],
            signed_static: Arc::new(vec![0; length]),
            dynamic: vec![0; length],
            moving_blocked: vec![false; length],
            dynamic_matches_static: true,
            has_static_blocked_cells: false,
            static_components_by_clearance: HashMap::new(),
        };
        layers.set_static_blocked(static_blocked)?;
        Ok(layers)
    }

    pub fn grid(&self) -> NavigationGrid { self.grid }
    pub fn static_clearance(&self) -> &[i32] { &self.base }
    pub fn static_signed_clearance(&self) -> &[i32] { self.signed_static.as_slice() }
    pub fn dynamic_clearance(&self) -> &[i32] { &self.dynamic }

    /// Capture the moving clearance without duplicating the static map,
    /// static connected components, or raster scratch per dispatch frame.
    /// Copy-on-write keeps prior workers' signed static field valid when
    /// authored static obstacles are rebuilt.
    pub fn search_snapshot(&self) -> NavigationSearchSnapshot {
        NavigationSearchSnapshot {
            grid: self.grid,
            signed_static: Arc::clone(&self.signed_static),
            dynamic: self.dynamic.clone(),
        }
    }

    /// Reuse an exclusively owned prior frame's search buffer. Its dynamic
    /// values are overwritten before any new job can observe the snapshot.
    /// A snapshot still held by a running worker must never be modified.
    pub fn refresh_search_snapshot(&self, snapshot: &mut NavigationSearchSnapshot) {
        debug_assert_eq!(snapshot.grid, self.grid);
        snapshot.signed_static = Arc::clone(&self.signed_static);
        snapshot.dynamic.copy_from_slice(&self.dynamic);
        snapshot.unobstructed = self.unobstructed();
    }

    /// Unity CanOccupyDestination's clearance criterion, after the owning
    /// Level has enforced ForceBounds and HasObstacles. The caller must still
    /// reject destinations that were outside the Level's configured bounds.
    pub fn can_occupy_cell(&self, level_position: Vec2, ship_clearance: i32) -> bool {
        if !level_position.is_finite() { return false; }
        let (x, y) = self.grid.to_cell(level_position);
        self.dynamic[y * self.grid.width + x] >= ship_clearance.max(2)
    }

    /// Unity TryFindNearestValidDestination's ring search, including the
    /// x-then-y edge scanning order and closest Euclidean candidate within
    /// the first radius with any walkable cell. The Level must first apply
    /// ForceBounds; callers skip this when Level.HasObstacles is false.
    pub fn nearest_walkable_destination(
        &self,
        bounded_destination: Vec2,
        ship_clearance: i32,
    ) -> Option<Vec2> {
        if !bounded_destination.is_finite() { return None; }
        let (cx, cy) = self.grid.to_cell(bounded_destination);
        let clearance = ship_clearance.max(2);
        let max_radius = self.grid.width.max(self.grid.height);
        for radius in 0..=max_radius {
            let min_x = cx.saturating_sub(radius);
            let max_x = cx.saturating_add(radius).min(self.grid.width - 1);
            let min_y = cy.saturating_sub(radius);
            let max_y = cy.saturating_add(radius).min(self.grid.height - 1);
            let mut best: Option<(Vec2, f32)> = None;
            let mut consider = |x: usize, y: usize| {
                if self.dynamic[y * self.grid.width + x] < clearance { return; }
                let candidate = self.grid.cell_center(x, y);
                let distance = (candidate - bounded_destination).length_squared();
                if best.as_ref().is_none_or(|(_, value)| distance < *value) {
                    best = Some((candidate, distance));
                }
            };
            for x in min_x..=max_x {
                consider(x, min_y);
                if max_y != min_y { consider(x, max_y); }
            }
            for y in min_y.saturating_add(1)..max_y {
                consider(min_x, y);
                if max_x != min_x { consider(max_x, y); }
            }
            if let Some((point, _)) = best { return Some(point); }
        }
        None
    }

    /// Unity Pathfinder.Connectivity.AreStaticallyConnected: reject only
    /// when two otherwise statically walkable locations lie in different
    /// permanently reachable regions. Starts inside authored geometry must
    /// be permitted here so the separate signed-clearance egress can work.
    /// Callers apply the Level's ForceBounds before converting coordinates.
    pub fn are_statically_connected(
        &mut self,
        start: Vec2,
        destination: Vec2,
        ship_clearance: i32,
    ) -> bool {
        if !start.is_finite() || !destination.is_finite() {
            return false;
        }
        let clearance = ship_clearance.max(2);
        let (sx, sy) = self.grid.to_cell(start);
        let (dx, dy) = self.grid.to_cell(destination);
        let from = sy * self.grid.width + sx;
        let to = dy * self.grid.width + dx;
        if self.base[from] < clearance || self.base[to] < clearance {
            return true;
        }
        // Without blocked static cells the walkable interior is a single
        // rectangle, irrespective of the ship's clearance threshold.
        if !self.has_static_blocked_cells { return true; }
        if !self.static_components_by_clearance.contains_key(&clearance) {
            let components = self.build_static_components(clearance);
            self.static_components_by_clearance.insert(clearance, components);
        }
        let components = &self.static_components_by_clearance[&clearance];
        components[from] != 0 && components[from] == components[to]
    }

    /// Unity's eight-neighbor order and no-diagonal-corner-cutting rule.
    /// Components are computed only when queried for a given ship size.
    fn build_static_components(&self, clearance: i32) -> Vec<i32> {
        const NEIGHBORS: [(i32, i32); 8] = [
            (-1, -1), (-1, 0), (-1, 1), (0, -1),
            (0, 1), (1, -1), (1, 0), (1, 1),
        ];
        let mut components = vec![0i32; self.base.len()];
        let mut queue = VecDeque::new();
        let mut next_component = 0i32;
        let width = self.grid.width;
        let height = self.grid.height;

        for origin in 0..self.base.len() {
            if components[origin] != 0 || self.base[origin] < clearance {
                continue;
            }
            next_component += 1;
            components[origin] = next_component;
            queue.push_back(origin);
            while let Some(current) = queue.pop_front() {
                let x = current % width;
                let y = current / width;
                for (dx, dy) in NEIGHBORS {
                    let nx = x as i64 + i64::from(dx);
                    let ny = y as i64 + i64::from(dy);
                    if nx < 0 || ny < 0 ||
                        nx >= width as i64 || ny >= height as i64 {
                        continue;
                    }
                    let nx = nx as usize;
                    let ny = ny as usize;
                    let neighbor = ny * width + nx;
                    if components[neighbor] != 0 || self.base[neighbor] < clearance {
                        continue;
                    }
                    if dx != 0 && dy != 0 &&
                        (self.base[ny * width + x] < clearance ||
                         self.base[y * width + nx] < clearance) {
                        continue;
                    }
                    components[neighbor] = next_component;
                    queue.push_back(neighbor);
                }
            }
        }
        components
    }

    /// Called when the map's *static* obstacles change; it replaces both
    /// base and signed-static layers. Static changes invalidate the old
    /// dynamic map, so the owning Level should refresh dynamic occupancy.
    pub fn set_static_blocked(&mut self, blocked: &[bool]) -> Result<(), NavigationError> {
        if blocked.len() != self.base.len() {
            return Err(NavigationError::IncorrectCellBuffer);
        }
        let mut has_blocked = false;
        for (base, &blocked) in self.base.iter_mut().zip(blocked) {
            *base = if blocked { 0 } else { 1 };
            has_blocked |= blocked;
        }
        self.has_static_blocked_cells = has_blocked;
        self.rebuild_static_fields();
        Ok(())
    }

    /// Rebuild Unity's static clearance from live StaticObstacle geometry,
    /// excluding map-border triggers and moving asteroids. Map-specific
    /// placement stays in the owning CombatWorld and never gets copied into
    /// a long-lived second obstacle registry.
    pub fn update_static_from_obstacles<'a, I, ToWorld, ToLevel>(
        &mut self,
        obstacles: I,
        to_world: ToWorld,
        to_level: ToLevel,
    ) -> Result<NavigationRasterCounts, NavigationError>
    where
        I: IntoIterator<Item = (i64, &'a ObstacleBody)>,
        ToWorld: Fn(Vec2) -> Vec2,
        ToLevel: Fn(Vec2) -> Vec2,
    {
        let counts = self.grid.rasterize_obstacles(
            obstacles, to_world, to_level, &mut self.moving_blocked,
        )?;
        // Static rasterization uses the same source polygon geometry and
        // clearance transform as the blocked-mask initialization path.
        for (base, &blocked) in self.base.iter_mut().zip(&self.moving_blocked) {
            *base = if blocked { 0 } else { 1 };
        }
        self.has_static_blocked_cells = counts.blocked_cells != 0;
        self.rebuild_static_fields();
        Ok(counts)
    }

    fn rebuild_static_fields(&mut self) {
        self.static_components_by_clearance.clear();
        calculate_clearance(self.grid, &mut self.base);
        build_signed_static(self.grid, &self.base, Arc::make_mut(&mut self.signed_static).as_mut_slice());
        self.dynamic.copy_from_slice(&self.base);
        self.moving_blocked.fill(false);
        self.dynamic_matches_static = true;
    }

    /// Unity's UpdateDynamicObstacleLayer: start from the static base,
    /// overlay live collision asteroids, then recompute clearance. No
    /// asteroid lifetime history is duplicated in the navigation system.
    pub fn update_dynamic_asteroids<'a, I, ToWorld, ToLevel>(
        &mut self,
        asteroids: I,
        to_world: ToWorld,
        to_level: ToLevel,
    ) -> Result<NavigationRasterCounts, NavigationError>
    where
        I: IntoIterator<Item = (i64, &'a ObstacleBody)>,
        ToWorld: Fn(Vec2) -> Vec2,
        ToLevel: Fn(Vec2) -> Vec2,
    {
        let counts = self.grid.rasterize_obstacles(
            asteroids, to_world, to_level, &mut self.moving_blocked,
        )?;
        // With no occupied moving cells the precomputed static distance
        // transform is already the exact answer. The world may have been
        // static-only for many frames, so avoid even the redundant copy.
        if counts.blocked_cells == 0 {
            self.restore_static_only();
            return Ok(counts);
        }
        self.dynamic.copy_from_slice(&self.base);
        for (clearance, &moving) in self.dynamic.iter_mut().zip(&self.moving_blocked) {
            if moving { *clearance = 0; }
        }
        calculate_clearance(self.grid, &mut self.dynamic);
        self.dynamic_matches_static = false;
        Ok(counts)
    }

    /// Unity short-circuits moving-asteroid occupancy when disabled.
    pub fn restore_static_only(&mut self) {
        if self.dynamic_matches_static { return; }
        self.dynamic.copy_from_slice(&self.base);
        self.moving_blocked.fill(false);
        self.dynamic_matches_static = true;
    }
}

/// The full-grid version of Unity's CalculateClearance(int[], maxClearance).
/// Grid edge limits are part of its answer, even with no obstacles.
fn calculate_clearance(grid: NavigationGrid, cells: &mut [i32]) {
    let width = grid.width;
    let height = grid.height;
    let cap = width.max(height).min(i32::MAX as usize) as i32;

    for y in 0..height {
        for x in 0..width {
            let i = y * width + x;
            if cells[i] > 0 {
                cells[i] = cap.min((x + 1) as i32).min((y + 1) as i32)
                    .min((width - x) as i32).min((height - y) as i32);
            }
        }
    }

    for y in 0..height {
        for x in 0..width {
            let i = y * width + x;
            if cells[i] == 0 { continue; }
            let mut clearance = cells[i];
            if x > 0 { clearance = clearance.min(cells[i - 1] + 1); }
            if y > 0 { clearance = clearance.min(cells[i - width] + 1); }
            if x > 0 && y > 0 { clearance = clearance.min(cells[i - width - 1] + 1); }
            if x + 1 < width && y > 0 {
                clearance = clearance.min(cells[i - width + 1] + 1);
            }
            cells[i] = clearance;
        }
    }

    for y in (0..height).rev() {
        for x in (0..width).rev() {
            let i = y * width + x;
            if cells[i] == 0 { continue; }
            let mut clearance = cells[i];
            if x + 1 < width { clearance = clearance.min(cells[i + 1] + 1); }
            if y + 1 < height { clearance = clearance.min(cells[i + width] + 1); }
            if x + 1 < width && y + 1 < height {
                clearance = clearance.min(cells[i + width + 1] + 1);
            }
            if x > 0 && y + 1 < height {
                clearance = clearance.min(cells[i + width - 1] + 1);
            }
            cells[i] = clearance;
        }
    }
}

/// Positive clearance for static free cells, negative distance-to-free for
/// blocked cells. In particular, *dynamic* asteroids do not change this map:
/// the separate signed static map guides the escape from a static obstacle.
fn build_signed_static(grid: NavigationGrid, base: &[i32], signed: &mut [i32]) {
    for (signed_cell, &base_cell) in signed.iter_mut().zip(base) {
        *signed_cell = if base_cell == 0 { UNREACHABLE_SIGNED } else { 0 };
    }
    let width = grid.width;
    let height = grid.height;

    for y in 0..height {
        for x in 0..width {
            let i = y * width + x;
            if signed[i] == 0 { continue; }
            let mut distance = signed[i];
            if x > 0 { distance = distance.min(signed[i - 1] + 1); }
            if y > 0 { distance = distance.min(signed[i - width] + 1); }
            if x > 0 && y > 0 { distance = distance.min(signed[i - width - 1] + 1); }
            if x + 1 < width && y > 0 {
                distance = distance.min(signed[i - width + 1] + 1);
            }
            signed[i] = distance;
        }
    }
    for y in (0..height).rev() {
        for x in (0..width).rev() {
            let i = y * width + x;
            if signed[i] == 0 { continue; }
            let mut distance = signed[i];
            if x + 1 < width { distance = distance.min(signed[i + 1] + 1); }
            if y + 1 < height { distance = distance.min(signed[i + width] + 1); }
            if x + 1 < width && y + 1 < height {
                distance = distance.min(signed[i + width + 1] + 1);
            }
            if x > 0 && y + 1 < height {
                distance = distance.min(signed[i + width - 1] + 1);
            }
            signed[i] = distance;
        }
    }
    for (cell, &base_cell) in signed.iter_mut().zip(base) {
        *cell = if base_cell > 0 { base_cell } else { -*cell };
    }
}
