//! World/Level-scoped learned Hive Mind discoveries.
//!
//! Source: GameState.VisionCache and HiveMind*Cache, GameState.Queries.cs,
//! and GameState.Registry.cs. Learned sight differs from transient trigger
//! visibility: leaving range or an observing ship's death NEVER forgets a
//! previously discovered target. Only target removal or Level reset does.
//!
//! The authoritative Level vision detector must call `record` only after
//! validating the observer, target, Level identity, Hive Mind registration,
//! setup-suppression state and actual geometric/trigger sighting. This small
//! store does not invent discoveries or perform an alternate vision test.

use std::collections::{HashMap, HashSet};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DiscoveryKind {
    Ship,
    Obstacle,
    MiningAsteroid,
    MapObject,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DiscoveryError {
    InvalidSide,
    InvalidEntityId,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DiscoveryCounts {
    pub ships: usize,
    pub obstacles: usize,
    pub mining_asteroids: usize,
    pub map_objects: usize,
}

#[derive(Default)]
struct SideDiscovery {
    ships: HashSet<i64>,
    /// HashSet membership is O(1), while the authored game enumerates its
    /// learned ship cache to build a MatchupStrategy target queue. Preserve
    /// first-sighting order without a second sighting/physics calculation.
    ship_sighting_order: Vec<i64>,
    obstacles: HashSet<i64>,
    mining: HashSet<i64>,
    map_objects: HashSet<i64>,
}

#[derive(Default)]
pub struct LevelDiscovery {
    sides: HashMap<i32, SideDiscovery>,
}

impl LevelDiscovery {
    pub fn new() -> Self { Self::default() }

    pub fn clear_level(&mut self) {
        self.sides.clear();
    }

    /// Source-owned faction IDs for optional deterministic replay capture.
    pub fn sides(&self) -> impl Iterator<Item = i32> + '_ {
        self.sides.keys().copied()
    }

    /// Store a discovery event validated by the actual gameplay Level.
    /// Reports the FIRST sighting by any observer on this side, which is
    /// the only sighting that should trigger discovery reward bookkeeping.
    /// Mining asteroids are present in both the mining and generic obstacle
    /// caches, exactly as in GameState.RecordHiveMindMiningAsteroidSighting.
    pub fn record(
        &mut self,
        observer_side: i32,
        kind: DiscoveryKind,
        entity_id: i64,
    ) -> Result<bool, DiscoveryError> {
        if observer_side <= 0 { return Err(DiscoveryError::InvalidSide); }
        if entity_id <= 0 { return Err(DiscoveryError::InvalidEntityId); }
        let side = self.sides.entry(observer_side).or_default();
        let first = match kind {
            DiscoveryKind::Ship => {
                let first = side.ships.insert(entity_id);
                if first { side.ship_sighting_order.push(entity_id); }
                first
            },
            DiscoveryKind::Obstacle => side.obstacles.insert(entity_id),
            DiscoveryKind::MiningAsteroid => {
                side.obstacles.insert(entity_id);
                side.mining.insert(entity_id)
            }
            DiscoveryKind::MapObject => side.map_objects.insert(entity_id),
        };
        Ok(first)
    }

    pub fn has(
        &self,
        observer_side: i32,
        kind: DiscoveryKind,
        entity_id: i64,
    ) -> bool {
        let Some(side) = self.sides.get(&observer_side) else {
            return false;
        };
        match kind {
            DiscoveryKind::Ship => side.ships.contains(&entity_id),
            DiscoveryKind::Obstacle => side.obstacles.contains(&entity_id),
            DiscoveryKind::MiningAsteroid => side.mining.contains(&entity_id),
            DiscoveryKind::MapObject => side.map_objects.contains(&entity_id),
        }
    }

    /// The source's GetShipsVisibleToHiveMind is a persistent HashSet.
    /// Consumers must sort by the v26 observation tie-breakers, NOT rely
    /// on HashSet iteration order when assigning per-episode identities.
    pub fn ship_ids(&self, observer_side: i32) -> Option<&HashSet<i64>> {
        self.sides.get(&observer_side).map(|side| &side.ships)
    }

    /// Insertion-ordered learned enemy ship identities for source
    /// MatchupStrategy.BuildVisibleSquadQueue. The server strategy chooses
    /// the FIRST equally ranked squad, so this is intentionally distinct
    /// from the unordered membership view used by physics and observations.
    /// A removed ship cannot reappear in a later episode from stale cache.
    pub fn ship_ids_in_sighting_order(&self, observer_side: i32) -> &[i64] {
        self.sides.get(&observer_side)
            .map_or(&[], |side| side.ship_sighting_order.as_slice())
    }

    pub fn obstacle_ids(&self, observer_side: i32) -> Option<&HashSet<i64>> {
        self.sides.get(&observer_side).map(|side| &side.obstacles)
    }

    pub fn mining_ids(&self, observer_side: i32) -> Option<&HashSet<i64>> {
        self.sides.get(&observer_side).map(|side| &side.mining)
    }

    pub fn map_object_ids(&self, observer_side: i32) -> Option<&HashSet<i64>> {
        self.sides.get(&observer_side).map(|side| &side.map_objects)
    }

    /// Called when the OBSERVED entity itself is removed from the current
    /// Level. Observer exit/death must never call this. Mining retirement
    /// also removes the corresponding generic obstacle membership.
    /// Returns the number of faction caches that held the removed entity.
    pub fn retire(&mut self, kind: DiscoveryKind, entity_id: i64) -> usize {
        let mut forgotten = 0;
        for side in self.sides.values_mut() {
            let removed = match kind {
                DiscoveryKind::Ship => {
                    let removed = side.ships.remove(&entity_id);
                    if removed {
                        side.ship_sighting_order.retain(|&id| id != entity_id);
                    }
                    removed
                },
                DiscoveryKind::Obstacle => {
                    let general = side.obstacles.remove(&entity_id);
                    let mining = side.mining.remove(&entity_id);
                    general || mining
                }
                DiscoveryKind::MiningAsteroid => {
                    let general = side.obstacles.remove(&entity_id);
                    let mining = side.mining.remove(&entity_id);
                    general || mining
                }
                DiscoveryKind::MapObject => side.map_objects.remove(&entity_id),
            };
            if removed { forgotten += 1; }
        }
        forgotten
    }

    pub fn counts(&self, side: i32) -> DiscoveryCounts {
        self.sides.get(&side).map_or(DiscoveryCounts::default(), |found| {
            DiscoveryCounts {
                ships: found.ships.len(),
                obstacles: found.obstacles.len(),
                mining_asteroids: found.mining.len(),
                map_objects: found.map_objects.len(),
            }
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn learned_discoveries_persist_per_side_until_target_removed_or_level_cleared() {
        let mut learned = LevelDiscovery::new();
        assert_eq!(learned.record(1, DiscoveryKind::Ship, 101), Ok(true));
        assert_eq!(learned.record(1, DiscoveryKind::Ship, 101), Ok(false));
        assert_eq!(learned.record(2, DiscoveryKind::Ship, 101), Ok(true));
        // Observer exit, death or replacement deliberately have NO
        // corresponding invalidation operation on this shared cache.
        assert!(learned.has(1, DiscoveryKind::Ship, 101));
        assert!(learned.has(2, DiscoveryKind::Ship, 101));
        assert_eq!(learned.retire(DiscoveryKind::Ship, 101), 2);
        assert!(!learned.has(1, DiscoveryKind::Ship, 101));

        assert_eq!(learned.record(1, DiscoveryKind::MiningAsteroid, 50), Ok(true));
        assert!(learned.has(1, DiscoveryKind::Obstacle, 50));
        assert!(learned.has(1, DiscoveryKind::MiningAsteroid, 50));
        assert_eq!(learned.retire(DiscoveryKind::MiningAsteroid, 50), 1);
        assert!(!learned.has(1, DiscoveryKind::Obstacle, 50));
        learned.record(1, DiscoveryKind::MapObject, 10).unwrap();
        learned.clear_level();
        assert_eq!(learned.counts(1), DiscoveryCounts::default());
    }
}
