//! Compose exactly one v26 perception row from authoritative native Level
//! snapshots. The same writer is used for rendered play, headless evaluation,
//! training, and live policy inference. It never fabricates missing gameplay
//! state or chooses an enemy from proximity instead of the VisionCache.
//!
//! All source-dependent data is explicitly supplied. The gameplay adapter
//! must populate the snapshots from the live CombatWorld and its squad,
//! vision, map and weapon state before this is a usable training path.

use crate::control::PolicyFrame;
use crate::perception::{
    CapabilitySource, PerceptionError, SelfSource, write_self,
    write_capability, write_reserved_objectives, write_navigation,
    write_exploration, EXPLORATION_LEN, NAV_LEN,
};
use crate::perception_entities::{
    EntitySlotEncoder, ObservedShip, write_parent_carrier,
};
use crate::perception_environment::{
    EnvironmentSlotEncoder, MiningAsteroid, MapObject, CollisionAsteroid,
};
use crate::perception_weapons::{SelfWeapon, write_self_weapons};
use crate::policy::{OBSERVATION_FLOATS, write_active_observation_tail};

pub struct PolicyPerceptionSnapshot<'a> {
    /// Real controlled ship, real ship type, episode identity and Level bounds.
    pub ship: SelfSource,
    pub capability: CapabilitySource,
    pub side: i32,
    pub bee_side: bool,
    /// Unity's GetNormalizedRlEpisodeProgress.
    pub normalized_episode_progress: f32,
    /// None only if the controlled ship is not the child of a live carrier.
    pub parent_carrier: Option<ObservedShip>,
    /// The Level.GetShips(side) registry, including the observer. The
    /// encoder filters dead ships and the observer by exact runtime ID.
    pub same_side_ships: &'a [ObservedShip],
    /// GetShipsVisibleToHiveMind(side): ever-discovered live enemy cache.
    pub vision_cache_enemies: &'a [ObservedShip],
    /// The ship's physical Weapons[0..5], with missing mounts as None.
    pub own_weapons: &'a [Option<SelfWeapon>; 5],
    /// Source HiveMind-visible object registries.
    pub visible_mining: &'a [MiningAsteroid],
    pub visible_map_objects: &'a [MapObject],
    pub visible_collision_asteroids: &'a [CollisionAsteroid],
    /// Real local Level bounds + visible static obstacle occupancy.
    pub navigation_world_grid: &'a [f32; NAV_LEN],
    /// Team-shared 16x16 sight-recency grid, or None only when the Unity
    /// episode coordinator would report no active exploration snapshot.
    pub exploration_last_seen: Option<&'a [f32; EXPLORATION_LEN]>,
    pub self_entity_id: i64,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PerceptionWork {
    pub observations: u64,
    pub allies_encoded: u64,
    pub enemies_encoded: u64,
    pub mining_encoded: u64,
    pub map_objects_encoded: u64,
    pub collision_asteroids_encoded: u64,
}

/// Owns reusable candidate-sort scratch and only six cumulative counters.
/// No heap-allocated row objects or per-decision observation histories.
#[derive(Default)]
pub struct NativePerceptionWriter {
    entity_slots: EntitySlotEncoder,
    environment_slots: EnvironmentSlotEncoder,
    measurements: PerceptionWork,
}

impl NativePerceptionWriter {
    pub fn new() -> Self { Self::default() }

    pub fn measurements(&self) -> PerceptionWork {
        self.measurements
    }

    /// Writes the entire ABI row. The caller must pass a fresh PolicyBatch
    /// destination prefilled with NaN, and propagate any error. This method
    /// does not apply actions or create rewards.
    pub fn write(
        &mut self,
        row: &mut [f32],
        source: &PolicyPerceptionSnapshot<'_>,
        frame: PolicyFrame,
    ) -> Result<(), PerceptionError> {
        if row.len() != OBSERVATION_FLOATS {
            return Err(PerceptionError::WrongRowSize { actual: row.len() });
        }
        if source.self_entity_id <= 0 || source.side <= 0 {
            return Err(PerceptionError::InvalidSource);
        }
        let origin = source.ship.position;
        write_self(row, source.ship, frame)?;
        write_capability(row, &source.ship, source.capability)?;
        write_parent_carrier(row, source.parent_carrier, origin, frame,
            source.capability.side_starting_tsv)?;
        let allies = self.entity_slots.write_allies(
            row, source.same_side_ships, source.self_entity_id, source.side,
            origin, frame, source.capability.side_starting_tsv,
        )?;
        let enemies = self.entity_slots.write_enemies(
            row, source.vision_cache_enemies, source.side,
            origin, frame, source.capability.side_starting_tsv,
        )?;
        write_self_weapons(row, source.own_weapons, origin,
            source.ship.longest_side, frame)?;
        let mining = self.environment_slots.write_mining(
            row, source.visible_mining, origin, frame)?;
        let objects = self.environment_slots.write_map_objects(
            row, source.visible_map_objects, origin, frame)?;
        let asteroids = self.environment_slots.write_collision_asteroids(
            row, source.visible_collision_asteroids, origin, frame)?;
        write_reserved_objectives(row)?;
        write_navigation(row, source.navigation_world_grid, frame)?;
        write_exploration(row, source.exploration_last_seen,
            source.normalized_episode_progress, frame)?;
        write_active_observation_tail(
            row, source.normalized_episode_progress, source.bee_side,
        ).map_err(|_| PerceptionError::InvalidSource)?;

        if row.iter().any(|feature| !feature.is_finite()) {
            return Err(PerceptionError::InvalidSource);
        }
        self.measurements.observations += 1;
        self.measurements.allies_encoded += allies as u64;
        self.measurements.enemies_encoded += enemies as u64;
        self.measurements.mining_encoded += mining as u64;
        self.measurements.map_objects_encoded += objects as u64;
        self.measurements.collision_asteroids_encoded += asteroids as u64;
        Ok(())
    }
}
