//! World-local assembly of the real v26 policy observation inputs.
//! This combines existing physical/perception projections in source order,
//! without duplicating any movement, combat, vision or reward rules.
//! External Level-only values (authored weapon stats, static occupancy,
//! mining, exploration and campaign data) MUST be provided, never guessed.

use bees_sim::collision::Aabb;
use bees_sim::combat_perception_bridge::{
    AuthoredShipPerception, CombatPerceptionError,
    ControlledShipPerception, LiveShipCandidateCollector,
    observed_ship_from_world, self_source_from_world,
    capability_source_from_world,
};
use bees_sim::combat_world::CombatWorld;
use bees_sim::episode_ship_identity::EpisodeShipIdentities;
use bees_sim::full_perception::PolicyPerceptionSnapshot;
use bees_sim::perception::{CapabilitySource, EXPLORATION_LEN, NAV_LEN};
use bees_sim::perception_entities::ObservedShip;
use bees_sim::perception_environment::{
    CollisionAsteroid, MapObject, MiningAsteroid,
};
use bees_sim::perception_weapons::SelfWeapon;
use bees_sim::simulation::RandomStream;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeWorldPerceptionError {
    MissingAuthoredShip(i64),
    Combat(CombatPerceptionError),
}
impl From<CombatPerceptionError> for NativeWorldPerceptionError {
    fn from(error: CombatPerceptionError) -> Self {
        Self::Combat(error)
    }
}

/// Everything the current native CombatWorld cannot yet derive. Every
/// input must come from the owning real game Level/episode, not a
/// training-only approximation. The lists are already filtered through
/// the native Level's actual visibility and discovery registries.
pub struct NativeLevelPerceptionInputs<'a> {
    pub level_bounds: Aabb,
    pub controller: ControlledShipPerception,
    pub capability: CapabilitySource,
    pub bee_side: bool,
    pub normalized_episode_progress: f32,
    /// Original living carrier, if the observer is its child. Its
    /// episode-scoped identity is assigned before ordinary ally slots.
    pub parent_carrier_id: Option<i64>,
    pub own_weapons: &'a [Option<SelfWeapon>; 5],
    pub visible_mining: &'a [MiningAsteroid],
    pub visible_map_objects: &'a [MapObject],
    pub visible_collision_asteroids: &'a [CollisionAsteroid],
    pub navigation_world_grid: &'a [f32; NAV_LEN],
    pub exploration_last_seen: Option<&'a [f32; EXPLORATION_LEN]>,
}

/// Construct ONE assembler per world and episode. All candidate and
/// output vectors are reused across agents and ticks. Identity
/// permutation state is per-episode, not shared across environments.
pub struct NativeWorldPerceptionAssembler {
    identities: EpisodeShipIdentities,
    candidates: LiveShipCandidateCollector,
    allies: Vec<ObservedShip>,
    enemies: Vec<ObservedShip>,
}

impl NativeWorldPerceptionAssembler {
    pub fn new(identities: EpisodeShipIdentities) -> Self {
        Self {
            identities,
            candidates: LiveShipCandidateCollector::new(),
            allies: Vec::new(),
            enemies: Vec::new(),
        }
    }

    /// The caller's world/episode RNG owns the permutation seed.
    pub fn from_random(random: &mut RandomStream) -> Self {
        Self::new(EpisodeShipIdentities::from_random(random))
    }

    pub fn reset_episode(&mut self, random: &mut RandomStream) {
        self.identities.reset(random);
        self.allies.clear();
        self.enemies.clear();
    }

    pub fn assigned_identity_count(&self) -> usize {
        self.identities.assigned_count()
    }

    /// Observer FIRST, optional parent carrier SECOND, then source-sorted
    /// allies and ever-discovered enemies. This preserves the episode
    /// permutation assignment order while returning slices into reused
    /// per-world scratch. The snapshot must be encoded before the next
    /// `assemble` call reuses those buffers.
    pub fn assemble<'a>(
        &'a mut self,
        world: &CombatWorld,
        ship_id: i64,
        inputs: NativeLevelPerceptionInputs<'a>,
        mut authored_ship: impl FnMut(i64) -> Option<AuthoredShipPerception>,
    ) -> Result<PolicyPerceptionSnapshot<'a>, NativeWorldPerceptionError> {
        let metadata = authored_ship(ship_id)
            .ok_or(NativeWorldPerceptionError::MissingAuthoredShip(ship_id))?;
        let ship = self_source_from_world(
            world, ship_id, inputs.level_bounds, metadata,
            inputs.controller, &mut self.identities,
        )?;
        let capability =
            capability_source_from_world(world, ship_id, inputs.capability);
        let parent_carrier = if let Some(parent) = inputs.parent_carrier_id {
            let parent_metadata = authored_ship(parent).ok_or(
                NativeWorldPerceptionError::MissingAuthoredShip(parent),
            )?;
            Some(observed_ship_from_world(
                world, parent, parent_metadata, &mut self.identities,
            )?)
        } else {
            None
        };
        self.candidates.collect_allies(
            world, ship_id, &mut self.identities,
            |id| authored_ship(id), &mut self.allies,
        )?;
        self.candidates.collect_learned_enemies(
            world, ship_id, &mut self.identities,
            |id| authored_ship(id), &mut self.enemies,
        )?;
        let side = world.ship(ship_id).ok_or(
            NativeWorldPerceptionError::Combat(
                CombatPerceptionError::MissingShip(ship_id),
            ),
        )?.side;
        Ok(PolicyPerceptionSnapshot {
            ship,
            capability,
            side,
            bee_side: inputs.bee_side,
            normalized_episode_progress: inputs.normalized_episode_progress,
            parent_carrier,
            same_side_ships: &self.allies,
            vision_cache_enemies: &self.enemies,
            own_weapons: inputs.own_weapons,
            visible_mining: inputs.visible_mining,
            visible_map_objects: inputs.visible_map_objects,
            visible_collision_asteroids: inputs.visible_collision_asteroids,
            navigation_world_grid: inputs.navigation_world_grid,
            exploration_last_seen: inputs.exploration_last_seen,
            self_entity_id: ship_id,
        })
    }
}
