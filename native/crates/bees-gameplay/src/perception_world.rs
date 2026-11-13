//! World-local assembly of the real v26 policy observation inputs.
//! This combines existing physical/perception projections in source order,
//! without duplicating any movement, combat, vision or reward rules.
//! External Level-only values (authored weapon stats, mining, exploration
//! and campaign data) MUST be provided, never guessed. Observer-local
//! static occupancy is derived from the authoritative discovered world.

use std::collections::HashMap;

use bees_sim::collision::Aabb;
use bees_sim::combat_perception_bridge::{
    AuthoredShipPerception, CombatPerceptionError,
    ControlledShipPerception, LiveShipCandidateCollector, LiveShipCandidateMeasurements,
    observed_ship_from_world, self_source_from_world,
    capability_source_from_world, collect_learned_map_objects,
    collect_learned_collision_asteroids,
    collect_learned_static_navigation_bounds,
    collect_learned_mining_asteroids,
};
use bees_sim::combat_world::{CombatWorld, CombatWorldError};
use bees_sim::episode_ship_identity::EpisodeShipIdentities;
use bees_sim::full_perception::PolicyPerceptionSnapshot;
use bees_sim::perception::{CapabilitySource, EXPLORATION_LEN};
use bees_sim::local_navigation_observation::LocalNavigationOccupancy;
use bees_sim::perception_entities::ObservedShip;
use bees_sim::perception_environment::{
    CollisionAsteroid, MapObject, MiningAsteroid,
};
use bees_sim::perception_weapons::SelfWeapon;
use bees_sim::simulation::{RandomStream, World, WorldId};

use crate::inference::{
    NativeInferenceError, NativeLevelInferenceBatch, NativePolicyRoute,
};
use crate::level_rules::NativeLevelState;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeWorldPerceptionError {
    MissingAuthoredShip(i64),
    InvalidEpisodeSide(i32),
    Combat(CombatPerceptionError),
    Inference(NativeInferenceError),
    Game(CombatWorldError),
}
impl From<CombatPerceptionError> for NativeWorldPerceptionError {
    fn from(error: CombatPerceptionError) -> Self {
        Self::Combat(error)
    }
}
impl From<NativeInferenceError> for NativeWorldPerceptionError {
    fn from(error: NativeInferenceError) -> Self {
        Self::Inference(error)
    }
}
impl From<CombatWorldError> for NativeWorldPerceptionError {
    fn from(error: CombatWorldError) -> Self {
        Self::Game(error)
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
    /// Original living carrier, if the observer is its child. Its
    /// episode-scoped identity is assigned before ordinary ally slots.
    pub parent_carrier_id: Option<i64>,
    pub own_weapons: &'a [Option<SelfWeapon>; 5],
    // Learned map objects and collision asteroids are projected from
    // CombatWorld's discovery and physical obstacle registries, not
    // supplied by an external observation-only collection.
    pub exploration_last_seen: Option<&'a [f32; EXPLORATION_LEN]>,
}

/// The actual world identity, episode and fixed tick associated with a
/// shared side's learned environment snapshot.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct EnvironmentEpoch {
    world: WorldId,
    episode: u64,
    tick: u64,
}

#[derive(Default)]
struct SideEnvironment {
    epoch: Option<EnvironmentEpoch>,
    objects: Vec<MapObject>,
    asteroids: Vec<CollisionAsteroid>,
    mining: Vec<MiningAsteroid>,
    /// Real discovered static collider AABBs, one collection per side/tick.
    static_navigation: Vec<(bees_sim::geometry::Vec2, bees_sim::geometry::Vec2)>,
}

/// Construct ONE assembler per world and episode. Identity and candidate
/// scratch belong to that world. Environment registries are cached only
/// for the same world+episode+tick and the same observing side, reducing
/// dense-battle cost from every agent scanning all map obstacles to one
/// scan per side and fixed-step decision boundary.
pub struct NativeWorldPerceptionAssembler {
    identities: EpisodeShipIdentities,
    candidates: LiveShipCandidateCollector,
    live_roster_epoch: Option<EnvironmentEpoch>,
    allies: Vec<ObservedShip>,
    enemies: Vec<ObservedShip>,
    /// Live native turrets override the Level's non-turret weapon slots.
    /// Reused across agents, just like ally and enemy buffers.
    own_weapons: [Option<SelfWeapon>; 5],
    /// 21x21 observer-local grid; reused for each agent with no allocation.
    navigation: LocalNavigationOccupancy,
    mining_squad_counts: HashMap<i64, u32>,
    mining_counts_epoch: Option<EnvironmentEpoch>,
    environment_by_side: HashMap<i32, SideEnvironment>,
}

impl NativeWorldPerceptionAssembler {
    pub fn new(identities: EpisodeShipIdentities) -> Self {
        Self {
            identities,
            candidates: LiveShipCandidateCollector::new(),
            live_roster_epoch: None,
            allies: Vec::new(),
            enemies: Vec::new(),
            own_weapons: [None; 5],
            navigation: LocalNavigationOccupancy::new(),
            mining_squad_counts: HashMap::new(),
            mining_counts_epoch: None,
            environment_by_side: HashMap::new(),
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
        self.own_weapons = [None; 5];
        self.candidates.reset_measurements();
        self.live_roster_epoch = None;
        self.mining_counts_epoch = None;
        // Retain storage for recurring episode resets but never reuse
        // old discovery data under a new identity permutation.
        for environment in self.environment_by_side.values_mut() {
            environment.epoch = None;
        }
    }

    pub fn assigned_identity_count(&self) -> usize {
        self.identities.assigned_count()
    }

    /// Episode-local work counters for the shared policy observation path.
    pub fn candidate_measurements(&self) -> LiveShipCandidateMeasurements {
        self.candidates.measurements()
    }

    /// Boundary observations must not assign identities in the live policy stream.
    pub(crate) fn for_policy_boundary(&self) -> Self {
        Self::new(self.identities.clone())
    }

    /// One game-owned observation path: project canonical ship, parent,
    /// allies and discovered obstacles, then encode the model input
    /// using this ship's actual bound policy frame and action mask.
    /// The temporary snapshot is never retained or copied between ticks.
    pub fn push_agent<'a, M, A>(
        &'a mut self,
        batch: &mut NativeLevelInferenceBatch,
        world: &World<NativeLevelState<M, A>>,
        ship_id: i64,
        route: NativePolicyRoute,
        group_agent_id: Option<u64>,
        inputs: NativeLevelPerceptionInputs<'a>,
        authored_ship: impl FnMut(i64) -> Option<AuthoredShipPerception>,
    ) -> Result<(), NativeWorldPerceptionError> {
        let snapshot = self.assemble(
            world, ship_id, inputs, authored_ship,
        )?;
        batch.push_agent(world, ship_id, route, group_agent_id, &snapshot)?;
        Ok(())
    }

    /// Call when a source Level changes its discovery or obstacle
    /// registry between inference boundaries without a fixed step.
    /// Normal fixed-step changes invalidate automatically via tick.
    pub fn invalidate_environment(&mut self) {
        self.live_roster_epoch = None;
        self.mining_counts_epoch = None;
        for environment in self.environment_by_side.values_mut() {
            environment.epoch = None;
        }
    }

    /// Observer FIRST, optional parent carrier SECOND, then source-sorted
    /// allies and ever-discovered enemies. This preserves the episode
    /// permutation assignment order while returning slices into reused
    /// per-world scratch. The snapshot must be encoded before the next
    /// `assemble` call reuses those buffers.
    pub fn assemble<'a, M, A>(
        &'a mut self,
        owning_world: &World<NativeLevelState<M, A>>,
        ship_id: i64,
        inputs: NativeLevelPerceptionInputs<'a>,
        mut authored_ship: impl FnMut(i64) -> Option<AuthoredShipPerception>,
    ) -> Result<PolicyPerceptionSnapshot<'a>, NativeWorldPerceptionError> {
        let world: &CombatWorld = &owning_world.state.combat;
        let metadata = authored_ship(ship_id)
            .ok_or(NativeWorldPerceptionError::MissingAuthoredShip(ship_id))?;
        let ship = self_source_from_world(
            world, ship_id, inputs.level_bounds, metadata,
            inputs.controller, &mut self.identities,
        )?;
        let mut capability =
            capability_source_from_world(world, ship_id, inputs.capability);
        // Imported/source-authored capability metadata is only an initial
        // snapshot. Training's authoritative economic ledger tracks credits,
        // safe returns and forfeitures, including ships no longer in the
        // live combat roster. Read it here for both ordinary inference and
        // terminal/boundary observations.
        if let Some(side) = world.ship(ship_id).map(|ship| ship.side) {
            if let Some(retainable) =
                owning_world.state.events.retainable_mined_tsv(side)
            {
                capability.team_retainable_mined_tsv = retainable;
            }
        }
        self.own_weapons = *inputs.own_weapons;
        // Real registered turrets own aiming, cooldown, attachment
        // offsets and readiness; an external snapshot may only supply
        // as-yet-unported non-turret weapon slots.
        let native_weapons = world.policy_self_weapons(ship_id)?;
        for (slot, weapon) in native_weapons.into_iter().enumerate() {
            if let Some(weapon) = weapon {
                self.own_weapons[slot] = Some(weapon);
            }
        }
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
        let epoch = EnvironmentEpoch {
            world: owning_world.id,
            episode: owning_world.episode_revision(),
            tick: owning_world.tick(),
        };
        // A one-ship or 1v1 world has no nearest-64 selection problem.
        // Keep the original source scan here: allocating and sorting two
        // side indexes for every inference tick would add fixed overhead
        // to the smallest headless training environments.
        let sides = owning_world.state.events.sides();
        if world.total_living_ships() <= 2 {
            self.live_roster_epoch = None;
            self.candidates.collect_allies(
                world, ship_id, &mut self.identities,
                |id| authored_ship(id), &mut self.allies,
            )?;
            self.candidates.collect_learned_enemies(
                world, ship_id, &mut self.identities,
                |id| authored_ship(id), &mut self.enemies,
            )?;
        } else {
            // All agents in this world see the same immutable source ship
            // population at a fixed decision boundary. The side-indexed
            // roster and discovered enemies are projected once per epoch.
            if self.live_roster_epoch != Some(epoch) {
                self.candidates.prepare_live_roster(world, sides)?;
                self.live_roster_epoch = Some(epoch);
            }
            self.candidates.collect_prepared_allies(
                world, ship_id, &mut self.identities,
                |id| authored_ship(id), &mut self.allies,
            )?;
            self.candidates.collect_prepared_learned_enemies(
                world, ship_id, &mut self.identities,
                |id| authored_ship(id), &mut self.enemies,
            )?;
        }
        let side = world.ship(ship_id).ok_or(
            NativeWorldPerceptionError::Combat(
                CombatPerceptionError::MissingShip(ship_id),
            ),
        )?.side;
        if !sides.contains(&side) {
            return Err(NativeWorldPerceptionError::InvalidEpisodeSide(side));
        }
        // Native mining command enrollment is world-wide, but the known
        // mining asteroid set is per faction. Snapshot the command-owner
        // map once per world tick and reuse for every policy decision.
        if self.mining_counts_epoch != Some(epoch) {
            owning_world.state.events.collect_mining_squad_counts(
                &mut self.mining_squad_counts,
            );
            self.mining_counts_epoch = Some(epoch);
        }
        let environment = self.environment_by_side.entry(side).or_default();
        if environment.epoch != Some(epoch) {
            collect_learned_map_objects(
                world, side, &mut environment.objects,
            )?;
            collect_learned_collision_asteroids(
                world, side, &mut environment.asteroids,
            )?;
            collect_learned_mining_asteroids(
                world, side, &mut environment.mining,
                |id| self.mining_squad_counts.get(&id).copied().unwrap_or(0),
            )?;
            collect_learned_static_navigation_bounds(
                world, side, &mut environment.static_navigation,
            )?;
            environment.epoch = Some(epoch);
        }
        // The Unity 21x21 navigation grid is local to THIS observer,
        // not faction-wide. Use real Level bounds, ship position and the
        // faction's current known static colliders. One 441-cell stack-
        // backed scratch buffer is reused for every agent.
        self.navigation.reset(inputs.level_bounds, ship.position)
            .map_err(CombatPerceptionError::from)?;
        for &(center, extents) in &environment.static_navigation {
            self.navigation.mark_static_aabb(center, extents)
                .map_err(CombatPerceptionError::from)?;
        }
        Ok(PolicyPerceptionSnapshot {
            ship,
            capability,
            side,
            bee_side: side == sides[0],
            normalized_episode_progress:
                owning_world.state.events.normalized_episode_progress(),
            parent_carrier,
            same_side_ships: &self.allies,
            vision_cache_enemies: &self.enemies,
            own_weapons: &self.own_weapons,
            visible_mining: &environment.mining,
            visible_map_objects: &environment.objects,
            visible_collision_asteroids: &environment.asteroids,
            navigation_world_grid: self.navigation.occupancy(),
            exploration_last_seen: inputs.exploration_last_seen,
            self_entity_id: ship_id,
        })
    }
}
