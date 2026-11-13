//! Authored scene-to-world construction shared by native game and RL.
//! Source FleetShip vitals, original health, scene motion, squad lifetimes,
//! policy bindings, episode rewards and metadata MUST be supplied by the
//! owning Level. No training-only combat state or guesswork is introduced.

use std::num::NonZeroU32;
use bees_gameplay::episode::NativeEpisodeRewardCoordinator;
use bees_gameplay::level_events::{NativeLevelEventProcessor, NativeLevelEventError};
use bees_gameplay::group_rewards::{NativeGroupRewardDelivery, GroupRewardError};
use bees_gameplay::level_rules::{
    NativeLevelState, NativeLevelRewardMetadata, NativeLevelCommandContext,
    NativeLevelVisionError,
};
use bees_gameplay::level_host::{NativeLevelActivationContext, NativeLevelObservationContext, NativeLevelNotificationSink};
use bees_gameplay::inference::NativePolicyInference;
use bees_gameplay::squads::SquadRosterError;
use bees_hivemind::session::SquadLifetime;
use bees_sim::collision::{Aabb, Pose};
use bees_sim::combat::ShipVitals;
use bees_sim::combat_world::{CombatWorld, CombatWorldError, NativeTurretSpec, NativeTurretRangeSpec, NativeShipTargetGeometry, NativeLevelGeometry, NativeWeaponRankSource, ShipCollisionProfile};
use bees_sim::asteroid_spawn::FullAsteroidPrototype;
use bees_sim::geometry::Vec2;
use bees_sim::navigation::NavigationError;
use bees_sim::combat_perception_bridge::CombatPerceptionError;
use bees_sim::control::PolicyFrame;
use bees_sim::movement::ShipMotion;
use bees_sim::navigation_movement::GameMovementProfile;
use bees_sim::map_object::CombatMapObject;
use bees_sim::physics::{ObstacleBody, ShipShape};
use bees_sim::obstacle::CombatObstacle;
use bees_sim::simulation::{Simulation, World, WorldId};
use crate::runtime::{NativeHostRuntime, HostInitError};
use crate::durable_connection::DurableHiveMindConnection;
use crate::ship_specials::NativeAuthoredShipSpecial;
use crate::scene_assets::NativeSceneAssets;

/// Exactly the values obtained from a live authored ship. The original
/// health can differ from a training-adjusted MaxHealth. A controller
/// and its frame are registered ONLY if the source supplies the binding.
#[derive(Clone)]
pub struct NativeAuthoredShip {
    pub combat_activity: Option<bees_sim::combat_world::NativeCombatActivity>,
    pub special_weapon: Option<bees_sim::combat_world::NativeSpecialWeaponSpec>,
    pub weapon_rank_source: Option<NativeWeaponRankSource>,
    pub target_geometry: Option<NativeShipTargetGeometry>,
    pub entity_id: i64,
    pub ship_type: u8,
    pub side: i32,
    pub vitals: ShipVitals,
    pub motion: ShipMotion,
    /// Actual source turning radius and sprite half-extents. Stationary
    /// members also need this for shared squad speed/finalization preflight.
    pub game_movement: GameMovementProfile,
    pub original_health: i32,
    pub size_class: f32,
    /// Actual child trigger geometry, in the same body-local frame as the hull.
    pub healing_zone: Option<ShipShape>,
    pub warp_zone: Option<ShipShape>,
    pub policy_capabilities: NativeAuthoredPolicyCapabilities,
    pub fleet_ship_id: Option<i64>,
    pub policy: Option<NativeAuthoredPolicy>,
    /// Imported CarrierShip.Carrier for an initially parented ordinary or
    /// policy Drone; installed after all ships exist, including forward links.
    pub drone_carrier_id: Option<i64>,
    pub cease_fire: bool,
    pub turrets: Vec<NativeAuthoredTurret>,
    pub special: Option<NativeAuthoredShipSpecial>,
}

#[derive(Clone, Copy, Debug)]
pub struct NativeHealingEligibility {
    pub bee_side: i32,
    pub ship_size_pixels: [u32; 2],
    pub beehive_size_pixels: [u32; 2],
}

/// Capability eligibility is authored at setup; readiness is always queried
/// from the live CombatWorld, never copied from a captured observation mask.
#[derive(Clone, Copy, Debug, Default)]
pub struct NativeAuthoredPolicyCapabilities {
    pub mining: bool,
    pub healing: Option<NativeHealingEligibility>,
    pub warp_human_side: Option<i32>,
}

/// Exact source SquadLifetime. A pooled Squad ID alone is not sufficient.
/// Complete, real native rotating-turret configuration supplied by the
/// original Unity weapon instances, never guessed from ship class.
#[derive(Clone)]
pub struct NativeAuthoredTurret {
    pub aim_at_front_of_ship: Option<bool>,
    pub range_collider: Option<NativeTurretRangeSpec>,
    pub slot: usize,
    pub spec: NativeTurretSpec,
}

/// Genuine Level/agent binding. Physical mounts and cease-fire remain on
/// NativeAuthoredShip, so ordinary ships need no fabricated policy controller.
#[derive(Clone, Copy)]
pub struct NativeAuthoredPolicy {
    pub frame: PolicyFrame,
    pub movement_disabled: bool,
}

/// Real authored world entities with original source IDs. Unity allocates
/// these monotonically across ships and obstacles, even though its GameState
/// stores them in separate lists. Sort once by that ID at setup to recover
/// original creation order; never reorder the live physics hot path.
pub enum NativeAuthoredEntity {
    Ship(NativeAuthoredShip),
    BoxObstacle {
        entity_id: i64,
        prefab_name: String,
        pose: Option<Pose>,
        /// Exact Unity primary BoxCollider2D, for a live transformed scene.
        /// None is valid when explicitly spawning an unmodified prefab.
        live_corners: Option<[Vec2; 4]>,
    },
    PolygonObstacle {
        entity_id: i64,
        pose: Option<Pose>,
        /// Original source PolygonCollider2D path in owning obstacle-local
        /// coordinates. None only for direct immutable prefab spawning.
        live_vertices: Option<Vec<Vec2>>,
    },
    /// Real source MapObject with its current primary collider and
    /// optional authored Fire Tank explosion/obstacle relationship.
    /// Gameplay damage and discovery are handled by CombatWorld itself.
    MapObject {
        object: CombatMapObject,
        body: ObstacleBody,
    },
    /// Actual initial MiningAsteroid resource health and primary collider,
    /// not a decorative radius. Command/policy mining uses this same
    /// physical obstacle and the canonical fleet-credit event path.
    MiningAsteroid {
        obstacle: CombatObstacle,
        body: ObstacleBody,
    },
    /// Physically existing full asteroid from a source scene snapshot.
    /// Shards require their original parent/family lifetime and cannot
    /// silently enter the ordinary full-asteroid pool.
    FullCollisionAsteroid {
        entity_id: i64,
        profile: FullAsteroidPrototype,
        pose: Pose,
        velocity: Vec2,
        angular_velocity_degrees: f32,
        effective_max_health: i32,
    },
}

impl NativeAuthoredEntity {
    pub fn entity_id(&self) -> i64 {
        match self {
            Self::Ship(ship) => ship.entity_id,
            Self::MapObject { object, .. } => object.entity_id,
            Self::MiningAsteroid { obstacle, .. } => obstacle.entity_id,
            Self::BoxObstacle { entity_id, .. } |
            Self::PolygonObstacle { entity_id, .. } |
            Self::FullCollisionAsteroid { entity_id, .. } => *entity_id,
        }
    }
}

pub struct NativeAuthoredSquad {
    pub command_bans: Option<Vec<bees_hivemind::dispatch::LocalCommandKind>>,
    pub hotkey_number: Option<u32>,
    pub original_squad_count: Option<u32>,
    pub owner: SquadLifetime,
    pub side: i32,
    pub members: Vec<i64>,
}

impl NativeAuthoredSquad {
    pub(crate) fn register(self, combat: &CombatWorld, roster: &mut bees_gameplay::squads::NativeSquadRoster)
        -> Result<(), SquadRosterError>
    {
        roster.register_squad(combat, self.owner, self.side, &self.members)?;
        if let Some(count) = self.original_squad_count {
            roster.set_source_hotkey(self.owner, self.hotkey_number, count)?;
        }
        if let Some(bans) = self.command_bans { roster.set_command_bans(self.owner, bans)?; }
        Ok(())
    }
}

/// The original MA-POCA group agent IDs are NOT necessarily runtime ship
/// IDs. Retain their exact source identity and true side; never infer a
/// synthetic group participant from a physical hull.
pub struct NativeAuthoredGroups {
    pub delivery: NativeGroupRewardDelivery,
    pub participants: Vec<(i32, u64)>,
}

pub struct NativeAuthoredLevel<M, A> {
    pub reserved_ship_ids: Vec<i64>,
    pub extraction: Option<bees_sim::combat_world::ExtractionZone>,
    pub campaign_dialogue: Option<bees_gameplay::campaign_dialogue::CampaignDialogueSpec>,
    pub weapon_rank_frame: Option<i32>,
    pub level_geometry: Option<NativeLevelGeometry>,
    pub world_id: WorldId,
    pub seed: u64,
    pub broadphase_bounds: Aabb,
    pub broadphase_cell_size: f32,
    pub decision_period: NonZeroU32,
    /// Source Stage.MakeShotsHarmless is part of the actual combat rules,
    /// not a training-only damage model; do not assume false.
    pub shots_harmless: bool,
    pub entities: Vec<NativeAuthoredEntity>,
    pub squads: Vec<NativeAuthoredSquad>,
    /// Exact scenario count, not a population estimate.
    pub initial_full_asteroids: usize,
    /// Map-local asteroid spawn bounds; imported Unity Levels must supply
    /// their own. The immutable prefab palette remains shared by the Stage.
    pub spawn_map_geometry: Option<(Aabb, Vec2)>,
    /// Already configured with true starting TSV, sides, timeout and
    /// discovery baselines. It is NOT reconstructed from combat geometry.
    pub episode: NativeEpisodeRewardCoordinator,
    /// None for rendered game without group rewards; Some for source MA-POCA
    /// with original group identities. The same single event processor
    /// publishes group rewards and terminal/ELO markers.
    pub groups: Option<NativeAuthoredGroups>,
    pub metadata: M,
    pub allocate_minion: A,
}

#[derive(Debug)]
pub enum NativeSceneBuildError {
    EmptyScene,
    MissingShipAsset(u8),
    Combat(CombatWorldError),
    Squad(SquadRosterError),
    IncompleteSquadRoster,
    MissingLiveBoxPose(i64),
    MissingLivePolygonPose(i64),
    Navigation(NavigationError),
    Perception(CombatPerceptionError),
    Vision(NativeLevelVisionError),
    Group(GroupRewardError),
    GroupConfiguration(NativeLevelEventError),
    InitialMining(NativeLevelEventError),
    CampaignDialogue(NativeLevelEventError),
}

#[derive(Debug)]
pub enum NativeRuntimeBuildError {
    EmptyLevels,
    /// An imported policy-only Level failed source validation or could
    /// not map its actual policy teams to loaded model slots.
    Imported(crate::unity_scene_snapshot::NativePolicyOnlyLevelError),
    /// A single batched native runtime has ONE physics fixed dt. Mixed
    /// source stage clocks must not silently run at the first one's rate.
    MixedSourceStage { expected: String, actual: String },
    Scene(NativeSceneBuildError),
    Simulation(&'static str),
    Host(HostInitError),
}

impl NativeSceneAssets {
    /// Assemble multiple fully authored worlds and the ONE batched gameplay/
    /// inference/notification host. No cloned physics worlds or alternate
    /// rendering/worker simulation are constructed. The caller provides the
    /// complete Level metadata and real policy backend.
    pub fn build_runtime<M, A, B, S>(
        &self,
        fixed_dt_seconds: f32,
        levels: impl IntoIterator<Item = NativeAuthoredLevel<M, A>>,
        model: B,
        connection: DurableHiveMindConnection,
        sink: S,
    ) -> Result<NativeHostRuntime<M, A, B, S>, NativeRuntimeBuildError>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext +
            NativeLevelActivationContext + NativeLevelObservationContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
        B: NativePolicyInference,
        S: NativeLevelNotificationSink,
    {
        self.build_runtime_with_steps(fixed_dt_seconds, levels.into_iter()
            .map(|level| (fixed_dt_seconds, level)), model, connection, sink)
    }

    pub(crate) fn build_runtime_with_steps<M, A, B, S>(
        &self,
        default_fixed_step: f32,
        levels: impl IntoIterator<Item = (f32, NativeAuthoredLevel<M, A>)>,
        model: B,
        connection: DurableHiveMindConnection,
        sink: S,
    ) -> Result<NativeHostRuntime<M, A, B, S>, NativeRuntimeBuildError>
    where
        M: NativeLevelRewardMetadata + NativeLevelCommandContext +
            NativeLevelActivationContext + NativeLevelObservationContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
        B: NativePolicyInference,
        S: NativeLevelNotificationSink,
    {
        let mut simulation = Simulation::new(
            self.level_rules(), default_fixed_step,
        ).map_err(NativeRuntimeBuildError::Simulation)?;
        let mut count = 0usize;
        for (fixed_step, authored) in levels {
            let world = self.build_level(authored)
                .map_err(NativeRuntimeBuildError::Scene)?;
            simulation.add_world_with_fixed_step(world, fixed_step)
                .map_err(NativeRuntimeBuildError::Simulation)?;
            count += 1;
        }
        if count == 0 {
            return Err(NativeRuntimeBuildError::EmptyLevels);
        }
        NativeHostRuntime::new(simulation, model, connection, sink)
            .map_err(NativeRuntimeBuildError::Host)
    }

    /// A failed setup leaves no partially registered Simulation world.
    /// Ship/asteroid collider geometry comes from the same imported assets
    /// used by the game renderer, with one CombatWorld and one squad roster.
    pub fn build_level<M, A>(
        &self,
        authored: NativeAuthoredLevel<M, A>,
    ) -> Result<World<NativeLevelState<M, A>>, NativeSceneBuildError>
    where
        M: NativeLevelCommandContext + NativeLevelRewardMetadata,
    {
        if authored.entities.is_empty() {
            return Err(NativeSceneBuildError::EmptyScene);
        }
        let mut combat = CombatWorld::new(
            authored.broadphase_bounds,
            authored.broadphase_cell_size,
            self.projectile_catalog(),
        ).map_err(NativeSceneBuildError::Combat)?;
        self.configure_combat_world_for_map(
            &mut combat, authored.spawn_map_geometry,
        )
            .map_err(NativeSceneBuildError::Combat)?;
        combat.shots_harmless = authored.shots_harmless;
        if let Some(zone) = authored.extraction {
            combat.configure_extraction(zone).map_err(NativeSceneBuildError::Combat)?;
        }
        if let Some(frame) = authored.weapon_rank_frame {
            combat.enable_automatic_weapon_targeting(frame).map_err(NativeSceneBuildError::Combat)?;
        }
        if let Some(geometry) = authored.level_geometry {
            combat.configure_level_geometry(geometry).map_err(NativeSceneBuildError::Combat)?;
        }

        // Unity assigns GameState entity IDs from a monotonic Stage pool.
        // Imported ship/obstacle lists arrive separately; sort their
        // original IDs once at setup, never in the simulation hot path.
        let mut entities = authored.entities;
        let mut reserved = authored.reserved_ship_ids;
        reserved.sort_unstable();
        let mut reserved = reserved.into_iter().peekable();
        let mut attachments = Vec::new();
        entities.sort_unstable_by_key(NativeAuthoredEntity::entity_id);
        for entity in entities {
            while reserved.peek().is_some_and(|&id| id < entity.entity_id()) {
                combat.reserve_future_ship_id(reserved.next().expect("next reserved ship"))
                    .map_err(NativeSceneBuildError::Combat)?;
            }
            match entity {
                NativeAuthoredEntity::Ship(ship) => {
                    attachments.extend(spawn_authored_ship(self.ship_catalog(), &mut combat, ship, false)?);
                }
                NativeAuthoredEntity::BoxObstacle {
                    entity_id, prefab_name, pose, live_corners,
                } => {
                    if let Some(corners) = live_corners {
                        let pose = pose.ok_or(
                            NativeSceneBuildError::MissingLiveBoxPose(entity_id)
                        )?;
                        combat.spawn_authored_box_obstacle(
                            self.box_catalog(), &prefab_name, entity_id,
                            pose, corners,
                        ).map_err(NativeSceneBuildError::Combat)?;
                    } else {
                        combat.spawn_box_obstacle(
                            self.box_catalog(), &prefab_name, entity_id, pose,
                        ).map_err(NativeSceneBuildError::Combat)?;
                    }
                }
                NativeAuthoredEntity::FullCollisionAsteroid {
                    entity_id, profile, pose, velocity,
                    angular_velocity_degrees, effective_max_health,
                } => {
                    combat.spawn_authored_full_collision_asteroid(
                        entity_id, profile, pose, velocity,
                        angular_velocity_degrees, effective_max_health,
                    ).map_err(NativeSceneBuildError::Combat)?;
                }
                NativeAuthoredEntity::MapObject { object, body } => {
                    combat.add_map_object(object, body)
                        .map_err(NativeSceneBuildError::Combat)?;
                }
                NativeAuthoredEntity::MiningAsteroid { obstacle, body } => {
                    combat.add_obstacle(obstacle, body)
                        .map_err(NativeSceneBuildError::Combat)?;
                }
                NativeAuthoredEntity::PolygonObstacle {
                    entity_id, pose, live_vertices,
                } => {
                    if let Some(vertices) = live_vertices {
                        let pose = pose.ok_or(
                            NativeSceneBuildError::MissingLivePolygonPose(entity_id)
                        )?;
                        combat.spawn_authored_polygon_obstacle(
                            self.polygon_catalog(), entity_id, pose, vertices,
                        ).map_err(NativeSceneBuildError::Combat)?;
                    } else {
                        combat.spawn_polygon_obstacle(
                            self.polygon_catalog(), entity_id, pose,
                        ).map_err(NativeSceneBuildError::Combat)?;
                    }
                }
            }
        }
        for id in reserved {
            combat.reserve_future_ship_id(id).map_err(NativeSceneBuildError::Combat)?;
        }
        install_ship_attachments(&mut combat, attachments)?;
        if combat.total_living_ships() == 0 {
            return Err(NativeSceneBuildError::EmptyScene);
        }
        let mut events = NativeLevelEventProcessor::new(authored.episode);
        // Seed the same live economic ledger that future mining updates and
        // deaths will mutate. This preserves an imported Level's authentic
        // starting FleetShip cargo without a shadow observation accumulator.
        // Native-authored training scenes normally begin with zero cargo.
        for ship in combat.ships() {
            if ship.vitals.is_dead() { continue; }
            let Some(mined) = authored.metadata.current_mined_tsv(ship.entity_id)
                else { continue; };
            if mined != 0 {
                events.record_mined_value(&combat, ship.entity_id, mined)
                    .map_err(NativeSceneBuildError::InitialMining)?;
            }
        }
        if let Some(mut groups) = authored.groups {
            for (side, agent_id) in groups.participants {
                groups.delivery.register_agent(side, agent_id)
                    .map_err(NativeSceneBuildError::Group)?;
            }
            events.configure_group_delivery(groups.delivery)
                .map_err(NativeSceneBuildError::GroupConfiguration)?;
        }
        for squad in authored.squads {
            squad.register(&combat, events.roster_mut()).map_err(NativeSceneBuildError::Squad)?;
        }
        if !events.roster().covers_live_roster(&combat) {
            return Err(NativeSceneBuildError::IncompleteSquadRoster);
        }
        let mut state = NativeLevelState::with_decision_period(
            combat, events, authored.metadata, authored.allocate_minion,
            authored.decision_period,
        );
        if let Some(dialogue) = authored.campaign_dialogue {
            state.configure_campaign_dialogue(dialogue).map_err(NativeSceneBuildError::CampaignDialogue)?;
        }
        let mut world = World::new(authored.world_id, authored.seed, state);
        for _ in 0..authored.initial_full_asteroids {
            world.state.combat.spawn_next_full_collision_asteroid(
                &mut world.random,
            ).map_err(NativeSceneBuildError::Combat)?;
        }
        // Build the Level's pathfinding clearance from the same authored
        // physical objects that gameplay and rendering use. Once at world
        // construction, never as a fabricated empty grid per policy tick.
        world.state.metadata.initialize_navigation_from_world(
            &world.state.combat,
        ).map_err(NativeSceneBuildError::Navigation)?;
        // First native decision at tick zero observes initial physical
        // visibility, not an empty fabricated exploration grid.
        world.state.refresh_vision_and_rewards(0)
            .map_err(NativeSceneBuildError::Vision)?;
        world.state.metadata.update_observation_memory(
            &world.state.combat,
            world.state.events.normalized_episode_progress(),
        ).map_err(NativeSceneBuildError::Perception)?;
        Ok(world)
    }
}

pub(crate) struct SpawnedShipAttachments {
    id: i64,
    policy: bool,
    weapon: Option<bees_sim::combat_world::NativeSpecialWeaponSpec>,
    special: Option<NativeAuthoredShipSpecial>,
    carrier: Option<i64>,
}

pub(crate) fn spawn_authored_ship(
    catalog: &bees_sim::ship_assets::ShipAssetCatalog,
    combat: &mut CombatWorld,
    ship: NativeAuthoredShip,
    reserved: bool,
) -> Result<Option<SpawnedShipAttachments>, NativeSceneBuildError> {
    let asset = catalog.by_type(ship.ship_type)
        .ok_or(NativeSceneBuildError::MissingShipAsset(ship.ship_type))?;
    if reserved {
        combat.spawn_reserved_ship(asset, ship.entity_id, ship.side, ship.vitals, ship.motion)
    } else {
        combat.spawn_ship(asset, ship.entity_id, ship.side, ship.vitals, ship.motion)
    }.map_err(NativeSceneBuildError::Combat)?;
    if let Some(source) = ship.combat_activity {
        combat.configure_combat_activity(ship.entity_id, source)
            .map_err(NativeSceneBuildError::Combat)?;
    }
    combat.configure_ship_game_movement(ship.entity_id, ship.game_movement)
        .map_err(NativeSceneBuildError::Combat)?;
    combat.configure_game_ship_original_health(
        ship.entity_id, ship.original_health,
    ).map_err(NativeSceneBuildError::Combat)?;
    combat.set_ship_collision_profile(ship.entity_id, ShipCollisionProfile {
        size_class: ship.size_class, original_health: ship.original_health,
    }).map_err(NativeSceneBuildError::Combat)?;
    if let Some(shape) = ship.healing_zone {
        combat.configure_beehive_healing_zone(ship.entity_id, shape)
            .map_err(NativeSceneBuildError::Combat)?;
    }
    if let Some(shape) = ship.warp_zone {
        combat.configure_warp_gate_zone(ship.entity_id, shape)
            .map_err(NativeSceneBuildError::Combat)?;
    }
    if let Some(fleet_id) = ship.fleet_ship_id {
        combat.set_ship_fleet_identity(ship.entity_id, fleet_id)
            .map_err(NativeSceneBuildError::Combat)?;
    }
    let is_policy = ship.policy.is_some();
    if let Some(policy) = ship.policy {
        combat.bind_policy_ship(
            ship.entity_id, policy.frame, policy.movement_disabled,
        ).map_err(NativeSceneBuildError::Combat)?;
        if ship.policy_capabilities.mining {
            combat.configure_policy_mining(ship.entity_id)
                .map_err(NativeSceneBuildError::Combat)?;
        }
        if let Some(heal) = ship.policy_capabilities.healing {
            combat.configure_policy_healing(ship.entity_id, heal.bee_side,
                heal.ship_size_pixels, heal.beehive_size_pixels)
                .map_err(NativeSceneBuildError::Combat)?;
        }
        if let Some(side) = ship.policy_capabilities.warp_human_side {
            combat.configure_policy_warp(ship.entity_id, side)
                .map_err(NativeSceneBuildError::Combat)?;
        }
    }
    if let Some(source) = ship.weapon_rank_source {
        combat.configure_weapon_rank_source(source).map_err(NativeSceneBuildError::Combat)?;
    }
    if let Some(geometry) = ship.target_geometry {
        combat.configure_ship_target_geometry(ship.entity_id, geometry)
            .map_err(NativeSceneBuildError::Combat)?;
    }
    combat.set_ship_cease_fire(ship.entity_id, ship.cease_fire)
        .map_err(NativeSceneBuildError::Combat)?;
    for turret in ship.turrets {
        if is_policy {
            combat.configure_policy_turret(ship.entity_id, turret.slot, turret.spec)
        } else {
            combat.configure_game_turret(ship.entity_id, turret.slot, turret.spec)
        }.map_err(NativeSceneBuildError::Combat)?;
        if let Some(front) = turret.aim_at_front_of_ship {
            combat.configure_turret_target_geometry(ship.entity_id, turret.slot, front)
                .map_err(NativeSceneBuildError::Combat)?;
        }
        if let Some(range) = turret.range_collider {
            combat.configure_turret_range(ship.entity_id, turret.slot, range)
                .map_err(NativeSceneBuildError::Combat)?;
        }
    }
    Ok((ship.special_weapon.is_some() || ship.special.is_some() || ship.drone_carrier_id.is_some())
        .then_some(SpawnedShipAttachments {
        id: ship.entity_id, policy: is_policy, weapon: ship.special_weapon,
        special: ship.special, carrier: ship.drone_carrier_id,
    }))
}

pub(crate) fn install_ship_attachments(
    combat: &mut CombatWorld, mut attachments: Vec<SpawnedShipAttachments>,
) -> Result<(), NativeSceneBuildError> {
    // Source carrier links can point forward within the same deployment.
    for ship in &attachments {
        if let Some(spec) = ship.weapon {
            combat.configure_special_weapon(ship.id, spec).map_err(NativeSceneBuildError::Combat)?;
        }
    }
    for ship in &mut attachments {
        if let Some(special) = ship.special.take() {
            special.configure(combat, ship.id, ship.policy).map_err(NativeSceneBuildError::Combat)?;
        }
    }
    for ship in attachments {
        if let Some(carrier) = ship.carrier {
            combat.configure_drone_carrier(ship.id, carrier).map_err(NativeSceneBuildError::Combat)?;
        }
    }
    Ok(())
}
