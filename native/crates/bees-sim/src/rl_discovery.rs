//! First-discovery rewards from Unity RlOneVsOneReward and EpisodeCoordinator.
//! Record a sighting ONLY after real Level/vision discovery verification.
//! All reward amounts pass through the existing episode tactical budget.

use std::collections::HashSet;

use crate::rl_shaping::{EpisodeShaping, RewardEmission};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum DiscoveryKind {
    EnemyShip, MiningAsteroid, StaticObstacle, MapObject, CollisionAsteroid,
}

impl DiscoveryKind {
    fn budget(self) -> f32 {
        match self {
            Self::EnemyShip => 0.006,
            Self::MiningAsteroid => 0.0015,
            Self::StaticObstacle => 0.0015,
            Self::MapObject => 0.001,
            Self::CollisionAsteroid => 0.0025,
        }
    }
    fn index(self) -> usize {
        match self {
            Self::EnemyShip => 0,
            Self::MiningAsteroid => 1,
            Self::StaticObstacle => 2,
            Self::MapObject => 3,
            Self::CollisionAsteroid => 4,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DiscoveryError {
    InvalidSide, InvalidValue,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DiscoveryMeasurements {
    pub first: u64,
    pub duplicate: u64,
    pub rewarded: u64,
}

pub struct EpisodeDiscoveryLedger {
    sides: [i32; 2],
    /// The four fixed-category starting value denominators, in
    /// EnemyShip/MiningAsteroid/StaticObstacle/MapObject order.
    baselines: [[i32; 4]; 2],
    seen: [HashSet<(DiscoveryKind, i64)>; 2],
    collision_counts: [u32; 2],
    measurements: DiscoveryMeasurements,
}

impl EpisodeDiscoveryLedger {
    pub fn new(
        sides: [i32; 2], baselines: [[i32; 4]; 2],
    ) -> Result<Self, DiscoveryError> {
        if sides[0] <= 0 || sides[1] <= 0 || sides[0] == sides[1] {
            return Err(DiscoveryError::InvalidSide);
        }
        if baselines.iter().flatten().any(|&value| value < 0) {
            return Err(DiscoveryError::InvalidValue);
        }
        Ok(Self {
            sides, baselines,
            seen: [HashSet::new(), HashSet::new()],
            collision_counts: [0, 0],
            measurements: DiscoveryMeasurements::default(),
        })
    }

    pub fn measurements(&self) -> DiscoveryMeasurements {
        self.measurements
    }

    /// The Level supplies verified sighting ID and source TSV/SizeClass.
    /// A duplicate remains a duplicate even when the object moves away
    /// and re-enters detection range. Collision asteroids use a convergent
    /// sequence and no episode-start count because new ones may spawn.
    pub fn first_sighting(
        &mut self, shaping: &mut EpisodeShaping,
        side: i32, kind: DiscoveryKind, entity_id: i64, value: i32,
    ) -> Result<Option<RewardEmission>, DiscoveryError> {
        let Some(index) = self.sides.iter().position(|&s| s == side)
        else { return Err(DiscoveryError::InvalidSide) };
        if entity_id <= 0 || value < 0 {
            return Err(DiscoveryError::InvalidValue);
        }
        if !self.seen[index].insert((kind, entity_id)) {
            self.measurements.duplicate += 1;
            return Ok(None);
        }
        self.measurements.first += 1;
        let raw = if kind == DiscoveryKind::CollisionAsteroid {
            let n = self.collision_counts[index] as f32;
            self.collision_counts[index] =
                self.collision_counts[index].saturating_add(1);
            let size = value as f32;
            if value == 0 { 0.0 } else {
                kind.budget() * size / (size + 1.0) /
                    ((n + 1.0) * (n + 2.0))
            }
        } else {
            // Enemy ship discovery uses Max(1, Spotted.Tsv) in Unity.
            let value = if kind == DiscoveryKind::EnemyShip {
                value.max(1)
            } else { value };
            if value <= 0 { 0.0 } else {
                let denominator = value.max(
                    self.baselines[index][kind.index()].max(1),
                ) as f32;
                kind.budget() * value as f32 / denominator
            }
        };
        let result = if raw > 0.0 {
            shaping.add_raw_for_side(side, raw)
        } else { None };
        if result.as_ref().is_some_and(|r| r.amount != 0.0) {
            self.measurements.rewarded += 1;
        }
        Ok(result)
    }
}
