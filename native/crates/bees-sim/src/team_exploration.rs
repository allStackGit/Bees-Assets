//! Shared native implementation of Unity's RlTeamExplorationGrid.
//!
//! One instance belongs to one side of one episode. A rendered Level and a
//! headless arena use the same grid and update calls. Sight range must be the
//! actual HiveMindVision.GetEffectiveRange(ship), not a guessed radius.
//! This module does not infer side membership or vision-cache discoveries.

use crate::collision::Aabb;
use crate::geometry::Vec2;
use crate::perception::{EXPLORATION_LEN, EXPLORATION_SIZE};

#[derive(Clone, Copy, Debug)]
pub struct Explorer {
    pub position: Vec2,
    pub effective_vision_range: i32,
    pub alive: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ExplorationError {
    InvalidLevelBounds,
    InvalidProgress,
    InvalidExplorer,
}

/// Per-side 16x16 global sight history, with progress -1 representing a cell
/// never observed this episode. Like Unity, an update checks cell centers
/// within each living allied ship's sight circle, with no heap allocation.
#[derive(Clone, Debug)]
pub struct TeamExplorationGrid {
    last_seen_progress: [f32; EXPLORATION_LEN],
}

impl Default for TeamExplorationGrid {
    fn default() -> Self {
        Self { last_seen_progress: [-1.0; EXPLORATION_LEN] }
    }
}

impl TeamExplorationGrid {
    pub fn new() -> Self { Self::default() }

    /// Explicit episode reset: do not accidentally retain knowledge when a
    /// recycled world starts a different training or evaluation episode.
    pub fn reset(&mut self) {
        self.last_seen_progress.fill(-1.0);
    }

    /// Read-only source consumed by perception::write_exploration. Keeping
    /// the array within its world avoids cross-arena shared mutable state.
    pub fn last_seen(&self) -> &[f32; EXPLORATION_LEN] {
        &self.last_seen_progress
    }

    /// Match RlTeamExplorationGrid.Update: clamp each ship's sight bounding
    /// rectangle to the 16x16 grid, then mark cell centers inside its radius.
    /// The caller selects only allied ships and supplies true episode progress.
    pub fn update(
        &mut self,
        level_bounds: Aabb,
        allied_ships: &[Explorer],
        episode_progress: f32,
    ) -> Result<(), ExplorationError> {
        let width = level_bounds.max.x - level_bounds.min.x;
        let height = level_bounds.max.y - level_bounds.min.y;
        if !level_bounds.min.is_finite() ||
            !level_bounds.max.is_finite() ||
            !width.is_finite() || !height.is_finite() ||
            width <= 0.0 || height <= 0.0 {
            return Err(ExplorationError::InvalidLevelBounds);
        }
        if !episode_progress.is_finite() {
            return Err(ExplorationError::InvalidProgress);
        }
        if allied_ships.iter().any(|ship|
            !ship.position.is_finite() ||
            ship.effective_vision_range < 0) {
            return Err(ExplorationError::InvalidExplorer);
        }

        let cell_width = width / EXPLORATION_SIZE as f32;
        let cell_height = height / EXPLORATION_SIZE as f32;
        for ship in allied_ships {
            if !ship.alive || ship.effective_vision_range <= 0 {
                continue;
            }
            let range = ship.effective_vision_range as f32;
            let range_squared = range * range;
            let min_x = ((ship.position.x - range - level_bounds.min.x) /
                cell_width).floor().clamp(0.0, (EXPLORATION_SIZE - 1) as f32) as usize;
            let max_x = ((ship.position.x + range - level_bounds.min.x) /
                cell_width).floor().clamp(0.0, (EXPLORATION_SIZE - 1) as f32) as usize;
            let min_y = ((ship.position.y - range - level_bounds.min.y) /
                cell_height).floor().clamp(0.0, (EXPLORATION_SIZE - 1) as f32) as usize;
            let max_y = ((ship.position.y + range - level_bounds.min.y) /
                cell_height).floor().clamp(0.0, (EXPLORATION_SIZE - 1) as f32) as usize;
            for y in min_y..=max_y {
                let center_y = level_bounds.min.y + (y as f32 + 0.5) * cell_height;
                let dy = center_y - ship.position.y;
                for x in min_x..=max_x {
                    let center_x = level_bounds.min.x + (x as f32 + 0.5) * cell_width;
                    let dx = center_x - ship.position.x;
                    if dx * dx + dy * dy <= range_squared {
                        self.last_seen_progress[y * EXPLORATION_SIZE + x] =
                            episode_progress;
                    }
                }
            }
        }
        Ok(())
    }

    /// Source-matched GetFreshness. This is measured as normalized episode
    /// progress since last sight, not wall seconds or a frame count.
    pub fn freshness(&self, world_index: usize, episode_progress: f32) -> f32 {
        let Some(&seen) = self.last_seen_progress.get(world_index) else {
            return 0.0;
        };
        if seen < 0.0 { 0.0 }
        else { (1.0 - (episode_progress - seen).max(0.0)).clamp(0.0, 1.0) }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::control::PolicyFrame;
    use crate::perception::{write_exploration, EXPLORATION_START, PERCEPTION_END};
    use crate::policy::OBSERVATION_FLOATS;

    #[test]
    fn living_allied_sight_marks_only_cell_centers_and_resets() {
        let bounds = Aabb {
            min: Vec2::new(0.0, 0.0),
            max: Vec2::new(160.0, 160.0),
        };
        let mut grid = TeamExplorationGrid::new();
        let scouts = [
            Explorer {
                position: Vec2::new(5.0, 5.0),
                effective_vision_range: 8,
                alive: true,
            },
            Explorer {
                position: Vec2::new(155.0, 155.0),
                effective_vision_range: 90,
                alive: false,
            },
        ];
        grid.update(bounds, &scouts, 0.25).unwrap();
        assert_eq!(grid.freshness(0, 0.25), 1.0);
        assert_eq!(grid.freshness(15 + 15 * EXPLORATION_SIZE, 0.25), 0.0);
        assert_eq!(grid.freshness(0, 0.75), 0.5);
        let mut row = [f32::NAN; OBSERVATION_FLOATS];
        write_exploration(&mut row, Some(grid.last_seen()),
            0.75, PolicyFrame::new(0)).unwrap();
        assert_eq!(row[EXPLORATION_START], 0.5);
        assert_eq!(row[PERCEPTION_END - 1], 0.0);
        grid.reset();
        assert_eq!(grid.freshness(0, 0.25), 0.0);
    }
}
