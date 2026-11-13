//! Source-grounded native route search from Unity Pathfinder.Search.cs.
//!
//! Uses Unity's 8-neighbor ordering, 10/14 movement costs, minimum and
//! preferred clearance, no-corner-cutting, signed-static egress and grid-line
//! smoothing. A search owns reusable arrays; game and RL share this same code.
//! The Unity 5-second wall-clock abort and async task dispatch belong to the
//! game scheduler, not the deterministic per-world navigation algorithm.

use std::time::Instant;

use crate::geometry::Vec2;
use crate::navigation::{NavigationError, NavigationGrid};
use crate::navigation_clearance::NavigationSearchLayers;

pub const MINIMUM_CLEARANCE: i32 = 2;
pub const PREFERRED_CLEARANCE_BUFFER: i32 = 2;
pub const HORIZONTAL_COST: i32 = 10;
pub const DIAGONAL_COST: i32 = 14;

const NEIGHBORS: [(i32, i32); 8] = [
    (-1, -1), (-1, 0), (-1, 1), (0, -1),
    (0, 1), (1, -1), (1, 0), (1, 1),
];
const NO_PARENT: usize = usize::MAX;

#[derive(Clone, Debug, PartialEq)]
pub struct NavigationPath {
    pub start: (usize, usize),
    pub end: (usize, usize),
    /// Unity Path.Points excludes the original start cell.
    pub points: Vec<Vec2>,
    /// The first N points form the no-clearance egress path from a
    /// statically obstructed start, if one was needed.
    pub egress_point_count: usize,
}

/// One route-searcher's reusable buffers. Multiple workers/arenas may each
/// own an instance, while immutable clearance snapshots can be shared.
#[derive(Debug)]
pub struct NavigationSearch {
    grid: NavigationGrid,
    cost_to_here: Vec<i32>,
    total_cost: Vec<i32>,
    heuristic_cost: Vec<i32>,
    previous_index: Vec<usize>,
    open_stamp: Vec<u32>,
    closed_stamp: Vec<u32>,
    search_stamp: u32,
    heap: Vec<usize>,
}

impl NavigationSearch {
    pub fn new(grid: NavigationGrid) -> Result<Self, NavigationError> {
        let Some(n) = grid.width.checked_mul(grid.height) else {
            return Err(NavigationError::InvalidMap);
        };
        if grid.width == 0 || grid.height == 0 ||
            grid.width > i32::MAX as usize || grid.height > i32::MAX as usize ||
            n > i32::MAX as usize {
            return Err(NavigationError::InvalidMap);
        }
        Ok(Self {
            grid,
            cost_to_here: vec![0; n],
            total_cost: vec![0; n],
            heuristic_cost: vec![0; n],
            previous_index: vec![NO_PARENT; n],
            open_stamp: vec![0; n],
            closed_stamp: vec![0; n],
            search_stamp: 0,
            heap: Vec::new(),
        })
    }

    /// Returns None for an unreachable route, including a blocked target or
    /// a start that cannot escape static geometry. The caller supplies the
    /// active map's same immutable clearance snapshot for every request in
    /// that scheduler frame.
    pub fn find_path(
        &mut self,
        layers: &impl NavigationSearchLayers,
        start: (usize, usize),
        end: (usize, usize),
        ship_clearance: i32,
    ) -> Result<Option<NavigationPath>, NavigationError> {
        self.find_path_until(layers, start, end, ship_clearance, None)
    }

    /// The interactive search worker enforces Unity's five-second limit.
    /// Headless/synchronous route queries use None for deterministic work.
    pub fn find_path_until(
        &mut self,
        layers: &impl NavigationSearchLayers,
        start: (usize, usize),
        end: (usize, usize),
        ship_clearance: i32,
        deadline: Option<Instant>,
    ) -> Result<Option<NavigationPath>, NavigationError> {
        if self.grid != layers.grid() ||
            start.0 >= self.grid.width || start.1 >= self.grid.height ||
            end.0 >= self.grid.width || end.1 >= self.grid.height {
            return Err(NavigationError::InvalidMap);
        }
        let hard = ship_clearance.max(MINIMUM_CLEARANCE);
        let preferred = hard.saturating_add(PREFERRED_CLEARANCE_BUFFER);
        let original_start = self.to_index(start.0, start.1);
        let mut start_index = original_start;
        let end_index = self.to_index(end.0, end.1);
        let dynamic = layers.dynamic_clearance();
        if dynamic[end_index] < hard { return Ok(None); }

        let mut egress = None;
        if dynamic[start_index] < hard {
            let Some(points) = self.find_static_egress(
                start_index, hard, dynamic, layers.static_signed_clearance(), deadline,
            ) else { return Ok(None) };
            start_index = *points.last().expect("an egress always has its start");
            egress = Some(points);
        }

        let Some(regular) = self.find_normal(start_index, end_index, dynamic, hard, preferred, deadline)
        else { return Ok(None) };
        let regular = self.smooth(&regular, dynamic, hard, preferred);
        let egress_point_count = egress.as_ref().map_or(0, |x| x.len().saturating_sub(1));
        let mut indexes = if let Some(mut egress) = egress {
            // The first normal route index is the final egress index.
            egress.extend_from_slice(&regular[1..]);
            egress
        } else { regular };
        if !indexes.is_empty() { indexes.remove(0); }

        Ok(Some(NavigationPath {
            start, end, egress_point_count,
            points: indexes.iter().map(|&index| {
                let (x, y) = self.coords(index);
                self.grid.cell_center(x, y)
            }).collect(),
        }))
    }

    /// Convenience for callers holding Unity-style Level-local coordinates.
    pub fn find_level_path(
        &mut self,
        layers: &impl NavigationSearchLayers,
        start: Vec2,
        end: Vec2,
        ship_clearance: i32,
    ) -> Result<Option<NavigationPath>, NavigationError> {
        if !start.is_finite() || !end.is_finite() {
            return Err(NavigationError::InvalidMap);
        }
        let from = self.grid.to_cell(start);
        let to = self.grid.to_cell(end);
        self.find_path(layers, from, to, ship_clearance)
    }

    pub fn find_level_path_until(
        &mut self,
        layers: &impl NavigationSearchLayers,
        start: Vec2,
        end: Vec2,
        ship_clearance: i32,
        deadline: Option<Instant>,
    ) -> Result<Option<NavigationPath>, NavigationError> {
        if !start.is_finite() || !end.is_finite() {
            return Err(NavigationError::InvalidMap);
        }
        let from = self.grid.to_cell(start);
        let to = self.grid.to_cell(end);
        self.find_path_until(layers, from, to, ship_clearance, deadline)
    }

    fn find_normal(
        &mut self,
        start: usize,
        end: usize,
        clearance: &[i32],
        hard: i32,
        preferred: i32,
        deadline: Option<Instant>,
    ) -> Option<Vec<usize>> {
        let stamp = self.begin_search();
        self.cost_to_here[start] = 0;
        self.heuristic_cost[start] = self.distance(start, end);
        self.total_cost[start] = self.heuristic_cost[start];
        self.previous_index[start] = NO_PARENT;
        self.open_stamp[start] = stamp;
        self.push(start);

        while let Some(current) = self.pop() {
            if deadline.is_some_and(|limit| Instant::now() >= limit) {
                return None;
            }
            if self.closed_stamp[current] == stamp { continue; }
            if current == end {
                return self.reconstruct(start, end);
            }
            self.closed_stamp[current] = stamp;
            let (x, y) = self.coords(current);
            for (dx, dy) in NEIGHBORS {
                let Some((nx, ny)) = self.neighbor(x, y, dx, dy) else { continue };
                let next = self.to_index(nx, ny);
                if self.closed_stamp[next] == stamp ||
                    clearance[next] < hard ||
                    self.diagonal_blocked(x, y, nx, ny, hard, clearance) {
                    continue;
                }
                let move_cost = if dx != 0 && dy != 0 {
                    DIAGONAL_COST
                } else { HORIZONTAL_COST };
                let shortfall = (preferred - clearance[next]).max(0);
                let penalty = shortfall.saturating_mul(shortfall)
                    .saturating_mul(HORIZONTAL_COST);
                let cost = self.cost_to_here[current]
                    .saturating_add(move_cost).saturating_add(penalty);
                if self.open_stamp[next] != stamp || cost < self.cost_to_here[next] {
                    self.cost_to_here[next] = cost;
                    self.heuristic_cost[next] = self.distance(next, end);
                    self.total_cost[next] = cost.saturating_add(self.heuristic_cost[next]);
                    self.previous_index[next] = current;
                    self.open_stamp[next] = stamp;
                    self.push(next);
                }
            }
        }
        None
    }

    /// Preserve Unity's monotone egress rule: moving from a blocked static
    /// start may not decrease signed static clearance or current combined
    /// clearance, and a diagonal may not cut across less-safe side cells.
    fn find_static_egress(
        &mut self,
        start: usize,
        hard: i32,
        dynamic: &[i32],
        signed: &[i32],
        deadline: Option<Instant>,
    ) -> Option<Vec<usize>> {
        if signed[start] >= hard { return None; }
        let stamp = self.begin_search();
        self.cost_to_here[start] = 0;
        self.total_cost[start] = 0;
        self.heuristic_cost[start] = 0;
        self.previous_index[start] = NO_PARENT;
        self.open_stamp[start] = stamp;
        self.push(start);

        while let Some(current) = self.pop() {
            if deadline.is_some_and(|limit| Instant::now() >= limit) {
                return None;
            }
            if self.closed_stamp[current] == stamp { continue; }
            if signed[current] >= hard && dynamic[current] >= hard {
                return self.reconstruct(start, current);
            }
            self.closed_stamp[current] = stamp;
            let (x, y) = self.coords(current);
            for (dx, dy) in NEIGHBORS {
                let Some((nx, ny)) = self.neighbor(x, y, dx, dy) else { continue };
                let next = self.to_index(nx, ny);
                if self.closed_stamp[next] == stamp ||
                    signed[next] < signed[current] ||
                    dynamic[next] < dynamic[current] ||
                    self.egress_diagonal_blocked(x, y, nx, ny, signed[current],
                        dynamic[current], signed, dynamic) {
                    continue;
                }
                let step_cost = if dx != 0 && dy != 0 {
                    DIAGONAL_COST
                } else { HORIZONTAL_COST };
                let cost = self.cost_to_here[current].saturating_add(step_cost);
                if self.open_stamp[next] != stamp || cost < self.cost_to_here[next] {
                    self.cost_to_here[next] = cost;
                    self.total_cost[next] = cost;
                    self.heuristic_cost[next] = -signed[next];
                    self.previous_index[next] = current;
                    self.open_stamp[next] = stamp;
                    self.push(next);
                }
            }
        }
        None
    }

    fn smooth(
        &self,
        indexes: &[usize],
        clearance: &[i32],
        hard: i32,
        preferred: i32,
    ) -> Vec<usize> {
        if indexes.len() <= 2 { return indexes.to_vec(); }
        let mut smoothed = Vec::with_capacity(indexes.len());
        let mut current = 0;
        smoothed.push(indexes[current]);
        while current + 1 < indexes.len() {
            let mut next = indexes.len() - 1;
            while next > current + 1 &&
                !self.clear_grid_line(indexes[current], indexes[next], clearance, preferred) {
                next -= 1;
            }
            if next == current + 1 &&
                !self.clear_grid_line(indexes[current], indexes[next], clearance, preferred) {
                next = indexes.len() - 1;
                while next > current + 1 &&
                    !self.clear_grid_line(indexes[current], indexes[next], clearance, hard) {
                    next -= 1;
                }
            }
            if !self.clear_grid_line(indexes[current], indexes[next], clearance, hard) {
                next = current + 1;
            }
            smoothed.push(indexes[next]);
            current = next;
        }
        smoothed
    }

    /// Exact integer supercover traversal of Unity's HasClearGridLine,
    /// including both side cells when the line crosses a diagonal corner.
    fn clear_grid_line(
        &self,
        first: usize,
        second: usize,
        clearance: &[i32],
        required: i32,
    ) -> bool {
        let (mut x, mut y) = self.coords(first);
        let (end_x, end_y) = self.coords(second);
        let steps_x = x.abs_diff(end_x);
        let steps_y = y.abs_diff(end_y);
        let direction_x: isize = if end_x > x { 1 } else if end_x < x { -1 } else { 0 };
        let direction_y: isize = if end_y > y { 1 } else if end_y < y { -1 } else { 0 };
        let mut moved_x = 0usize;
        let mut moved_y = 0usize;
        if clearance[first] < required { return false; }
        while moved_x < steps_x || moved_y < steps_y {
            let decision = ((1 + 2 * moved_x) as i64 * steps_y as i64) -
                ((1 + 2 * moved_y) as i64 * steps_x as i64);
            if decision == 0 {
                if !self.cell_clear(x.wrapping_add_signed(direction_x), y, clearance, required) ||
                    !self.cell_clear(x, y.wrapping_add_signed(direction_y), clearance, required) {
                    return false;
                }
                x = x.wrapping_add_signed(direction_x);
                y = y.wrapping_add_signed(direction_y);
                moved_x += 1;
                moved_y += 1;
            } else if decision < 0 {
                x = x.wrapping_add_signed(direction_x);
                moved_x += 1;
            } else {
                y = y.wrapping_add_signed(direction_y);
                moved_y += 1;
            }
            if !self.cell_clear(x, y, clearance, required) { return false; }
        }
        true
    }

    #[inline]
    fn cell_clear(&self, x: usize, y: usize, cells: &[i32], required: i32) -> bool {
        x < self.grid.width && y < self.grid.height &&
            cells[self.to_index(x, y)] >= required
    }

    #[inline]
    fn diagonal_blocked(
        &self,
        x: usize, y: usize, nx: usize, ny: usize,
        min: i32, cells: &[i32],
    ) -> bool {
        x != nx && y != ny &&
            (cells[self.to_index(x, ny)] < min ||
             cells[self.to_index(nx, y)] < min)
    }

    #[inline]
    fn egress_diagonal_blocked(
        &self,
        x: usize, y: usize, nx: usize, ny: usize,
        min_signed: i32, min_dynamic: i32,
        signed: &[i32], dynamic: &[i32],
    ) -> bool {
        if x == nx || y == ny { return false; }
        let a = self.to_index(x, ny);
        let b = self.to_index(nx, y);
        signed[a] < min_signed || signed[b] < min_signed ||
            dynamic[a] < min_dynamic || dynamic[b] < min_dynamic
    }

    #[inline]
    fn neighbor(&self, x: usize, y: usize, dx: i32, dy: i32) -> Option<(usize, usize)> {
        let nx = x as i64 + i64::from(dx);
        let ny = y as i64 + i64::from(dy);
        if nx < 0 || ny < 0 || nx >= self.grid.width as i64 ||
            ny >= self.grid.height as i64 { return None; }
        Some((nx as usize, ny as usize))
    }

    #[inline]
    fn to_index(&self, x: usize, y: usize) -> usize { y * self.grid.width + x }

    #[inline]
    fn coords(&self, index: usize) -> (usize, usize) {
        (index % self.grid.width, index / self.grid.width)
    }

    fn distance(&self, a: usize, b: usize) -> i32 {
        let (ax, ay) = self.coords(a);
        let (bx, by) = self.coords(b);
        let diagonal = ax.abs_diff(bx).min(ay.abs_diff(by)) as i32;
        let straight = ax.abs_diff(bx).abs_diff(ay.abs_diff(by)) as i32;
        DIAGONAL_COST.saturating_mul(diagonal)
            .saturating_add(HORIZONTAL_COST.saturating_mul(straight))
    }

    fn reconstruct(&self, start: usize, end: usize) -> Option<Vec<usize>> {
        let mut indexes = vec![end];
        let mut current = end;
        while current != start {
            let parent = self.previous_index[current];
            if parent == NO_PARENT || indexes.len() > self.previous_index.len() {
                return None;
            }
            current = parent;
            indexes.push(current);
        }
        indexes.reverse();
        Some(indexes)
    }

    fn begin_search(&mut self) -> u32 {
        self.heap.clear();
        self.search_stamp = self.search_stamp.wrapping_add(1);
        if self.search_stamp == 0 {
            self.open_stamp.fill(0);
            self.closed_stamp.fill(0);
            self.search_stamp = 1;
        }
        self.search_stamp
    }

    #[inline]
    fn higher_priority(&self, a: usize, b: usize) -> bool {
        if self.total_cost[a] != self.total_cost[b] {
            return self.total_cost[a] < self.total_cost[b];
        }
        if self.heuristic_cost[a] != self.heuristic_cost[b] {
            return self.heuristic_cost[a] < self.heuristic_cost[b];
        }
        a < b
    }

    /// Unity's integer min-heap is an array of cell IDs. The keys live in
    /// the search arrays and can be updated before an ID is pushed again.
    fn push(&mut self, node: usize) {
        self.heap.push(node);
        let mut index = self.heap.len() - 1;
        while index > 0 {
            let parent = (index - 1) / 2;
            if self.higher_priority(self.heap[parent], node) { break; }
            self.heap[index] = self.heap[parent];
            index = parent;
        }
        self.heap[index] = node;
    }

    fn pop(&mut self) -> Option<usize> {
        let result = *self.heap.first()?;
        let last = self.heap.pop().expect("nonempty heap");
        if self.heap.is_empty() { return Some(result); }
        let mut index = 0;
        loop {
            let left = index * 2 + 1;
            if left >= self.heap.len() { break; }
            let right = left + 1;
            let child = if right < self.heap.len() &&
                self.higher_priority(self.heap[right], self.heap[left]) {
                right
            } else { left };
            if self.higher_priority(last, self.heap[child]) { break; }
            self.heap[index] = self.heap[child];
            index = child;
        }
        self.heap[index] = last;
        Some(result)
    }
}
