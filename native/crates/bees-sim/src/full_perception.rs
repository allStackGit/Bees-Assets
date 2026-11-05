//! Compose exactly one v26 perception row from authoritative native Level
//! snapshots. The same writer is used for rendered play, headless evaluation,
//! training, and live policy inference. It never fabricates missing gameplay
//! state or chooses an enemy from proximity instead of the VisionCache.
//!
//! All source-dependent data is explicitly supplied. The gameplay adapter
//! must populate the snapshots from the live CombatWorld and its squad,
//! vision, map and weapon state before this is a usable training path.

use crate::control::PolicyFrame;
use crate::policy_batch::{
    PolicyBatch, PolicyBatchError, PolicyAgentKey, PolicyActionMask,
};
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

    /// Direct zero-copy integration with the inference batch. A failed
    /// source snapshot leaves the batch length and keys unchanged.
    pub fn push_to_batch(
        &mut self,
        batch: &mut PolicyBatch,
        key: PolicyAgentKey,
        mask: PolicyActionMask,
        source: &PolicyPerceptionSnapshot<'_>,
        frame: PolicyFrame,
    ) -> Result<(), PolicyBatchError> {
        batch.push_encoded_result(key, mask, |row|
            self.write(row, source, frame).map_err(PolicyBatchError::Perception)
        )
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

        // Every block writes exactly its source-defined span. The batch
        // performs the single full-row finite scan before GPU submission;
        // repeating that scan here would double the observation hot-path work.
        self.measurements.observations += 1;
        self.measurements.allies_encoded += allies as u64;
        self.measurements.enemies_encoded += enemies as u64;
        self.measurements.mining_encoded += mining as u64;
        self.measurements.map_objects_encoded += objects as u64;
        self.measurements.collision_asteroids_encoded += asteroids as u64;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::collision::Aabb;
    use crate::geometry::Vec2;
    use crate::perception::{NAV_LEN, EXPLORATION_LEN};
    use crate::policy::{CONTINUOUS_ACTIONS, DISCRETE_BRANCHES};
    use crate::simulation::{World, WorldId};

    #[test]
    fn complete_v26_row_passes_batch_validation_and_keeps_identity() {
        let mut writer = NativePerceptionWriter::new();
        let mut batch = PolicyBatch::new();
        let physical_ship = SelfSource {
            identity: 0.25, ship_type: 1, position: Vec2::ZERO,
            level_bounds: Aabb {
                min: Vec2::new(-64.0, -64.0),
                max: Vec2::new(64.0, 64.0),
            },
            rotation_degrees: 0.0,
            health: 100, max_health: 100,
            speed: 10.0, current_speed: 10.0,
            linear_velocity: Vec2::ZERO,
            rotation_speed: 90.0, longest_side: 4.0,
            effective_vision_range: 40.0,
            max_range: 80.0, firepower: 20.0,
            can_move: true, is_bomber: false, is_carrier: false,
            has_weapons: false, has_turrets: false,
            has_special: false, special_ready: false,
            live_allies_including_self: 1, live_enemies: 0,
        };
        let capabilities = CapabilitySource {
            special_resource_fraction: 0.0,
            special_cooldown_fraction: 0.0,
            special_phase: 0.0, mining_possible: false,
            healing_possible: false, warp_possible: false,
            is_carrier_child: false, has_living_parent_carrier: false,
            ship_mined_tsv: 0, team_retainable_mined_tsv: 0,
            side_starting_tsv: 1000,
        };
        let weapons = [None; 5];
        let navigation = [0.0f32; NAV_LEN];
        let exploration = [-1.0f32; EXPLORATION_LEN];
        let source = PolicyPerceptionSnapshot {
            ship: physical_ship, capability: capabilities,
            side: 1, bee_side: true,
            normalized_episode_progress: 0.25,
            parent_carrier: None, same_side_ships: &[],
            vision_cache_enemies: &[], own_weapons: &weapons,
            visible_mining: &[], visible_map_objects: &[],
            visible_collision_asteroids: &[],
            navigation_world_grid: &navigation,
            exploration_last_seen: Some(&exploration),
            self_entity_id: 5,
        };
        let world = World::new(WorldId(9), 123, ());
        let key = PolicyAgentKey::from_world(&world, 5);
        let mask = PolicyActionMask {
            fire: [false; 5],
            special: [true, false, false, false, false],
        };
        writer.push_to_batch(
            &mut batch, key, mask, &source, PolicyFrame::new(0),
        ).unwrap();
        assert_eq!(batch.len(), 1);
        assert_eq!(batch.observations().len(), OBSERVATION_FLOATS);
        assert!(batch.observations().iter().all(|value| value.is_finite()));
        assert_eq!(writer.measurements().observations, 1);
        assert_eq!(writer.measurements().enemies_encoded, 0);
        let (continuous, discrete) = batch.output_buffers_mut();
        assert_eq!(continuous.len(), CONTINUOUS_ACTIONS);
        assert_eq!(discrete.len(), DISCRETE_BRANCHES.len());
        continuous.fill(0.0);
        discrete.fill(0);
        let mut decisions = Vec::new();
        batch.collect_decisions(&mut decisions).unwrap();
        assert_eq!(decisions.len(), 1);
        assert_eq!(decisions[0].0, key);
    }
}
