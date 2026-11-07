//! Authored scene-to-world construction shared by native game and RL.
//! Source FleetShip vitals, original health, scene motion, squad lifetimes,
//! policy bindings, episode rewards and metadata MUST be supplied by the
//! owning Level. No training-only combat state or guesswork is introduced.

use std::num::NonZeroU32;
use bees_gameplay::episode::NativeEpisodeRewardCoordinator;
use bees_gameplay::level_events::NativeLevelEventProcessor;
use bees_gameplay::level_rules::NativeLevelState;
use bees_gameplay::squads::SquadRosterError;
use bees_hivemind::session::SquadLifetime;
use bees_sim::collision::{Aabb, Pose};
use bees_sim::combat::ShipVitals;
use bees_sim::combat_world::{CombatWorld, CombatWorldError};
use bees_sim::control::PolicyFrame;
use bees_sim::movement::ShipMotion;
use bees_sim::simulation::{World, WorldId};
use crate::scene_assets::NativeSceneAssets;

/// Exactly the values obtained from a live authored ship. The original
/// health can differ from a training-adjusted MaxHealth. A controller
/// and its frame are registered ONLY if the source supplies the binding.
pub struct NativeAuthoredShip {
    pub entity_id: i64,
    pub ship_type: u8,
    pub side: i32,
    pub vitals: ShipVitals,
    pub motion: ShipMotion,
    pub original_health: i32,
    pub fleet_ship_id: Option<i64>,
    pub policy: Option<(PolicyFrame, bool)>,
}

/// Exact source SquadLifetime. A pooled Squad ID alone is not sufficient.
/// The actual Stage/Level spawn order across ship and static obstacle
/// types. Do not sort by entity ID or type: Unity's source list order
/// matters for event ordering and source identity allocation.
pub enum NativeAuthoredEntity {
    Ship(NativeAuthoredShip),
    BoxObstacle {
        entity_id: i64,
        prefab_name: String,
        pose: Option<Pose>,
    },
    PolygonObstacle {
        entity_id: i64,
        pose: Option<Pose>,
    },
}

pub struct NativeAuthoredSquad {
    pub owner: SquadLifetime,
    pub side: i32,
    pub members: Vec<i64>,
}

pub struct NativeAuthoredLevel<M, A> {
    pub world_id: WorldId,
    pub seed: u64,
    pub broadphase_bounds: Aabb,
    pub broadphase_cell_size: f32,
    pub decision_period: NonZeroU32,
    pub entities: Vec<NativeAuthoredEntity>,
    pub squads: Vec<NativeAuthoredSquad>,
    /// Exact scenario count, not a population estimate.
    pub initial_full_asteroids: usize,
    /// Already configured with true starting TSV, sides, timeout and
    /// discovery baselines. It is NOT reconstructed from combat geometry.
    pub episode: NativeEpisodeRewardCoordinator,
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
}

impl NativeSceneAssets {
    /// A failed setup leaves no partially registered Simulation world.
    /// Ship/asteroid collider geometry comes from the same imported assets
    /// used by the game renderer, with one CombatWorld and one squad roster.
    pub fn build_level<M, A>(
        &self,
        authored: NativeAuthoredLevel<M, A>,
    ) -> Result<World<NativeLevelState<M, A>>, NativeSceneBuildError> {
        if authored.entities.is_empty() {
            return Err(NativeSceneBuildError::EmptyScene);
        }
        let mut combat = CombatWorld::new(
            authored.broadphase_bounds,
            authored.broadphase_cell_size,
            self.projectile_catalog(),
        ).map_err(NativeSceneBuildError::Combat)?;
        self.configure_combat_world(&mut combat)
            .map_err(NativeSceneBuildError::Combat)?;

        for entity in authored.entities {
            let NativeAuthoredEntity::Ship(ship) = entity else {
                match entity {
                    NativeAuthoredEntity::BoxObstacle {
                        entity_id, prefab_name, pose,
                    } => combat.spawn_box_obstacle(
                        self.box_catalog(), &prefab_name, entity_id, pose,
                    ).map_err(NativeSceneBuildError::Combat)?,
                    NativeAuthoredEntity::PolygonObstacle {
                        entity_id, pose,
                    } => combat.spawn_polygon_obstacle(
                        self.polygon_catalog(), entity_id, pose,
                    ).map_err(NativeSceneBuildError::Combat)?,
                    NativeAuthoredEntity::Ship(_) => unreachable!(),
                }
                continue;
            };
            let asset = self.ship_catalog().by_type(ship.ship_type)
                .ok_or(NativeSceneBuildError::MissingShipAsset(ship.ship_type))?;
            combat.spawn_ship(
                asset, ship.entity_id, ship.side, ship.vitals, ship.motion,
            ).map_err(NativeSceneBuildError::Combat)?;
            combat.configure_game_ship_original_health(
                ship.entity_id, ship.original_health,
            ).map_err(NativeSceneBuildError::Combat)?;
            if let Some(fleet_id) = ship.fleet_ship_id {
                combat.set_ship_fleet_identity(ship.entity_id, fleet_id)
                    .map_err(NativeSceneBuildError::Combat)?;
            }
            if let Some((frame, movement_disabled)) = ship.policy {
                combat.bind_policy_ship(ship.entity_id, frame, movement_disabled)
                    .map_err(NativeSceneBuildError::Combat)?;
            }
        }
        if combat.total_living_ships() == 0 {
            return Err(NativeSceneBuildError::EmptyScene);
        }
        let mut events = NativeLevelEventProcessor::new(authored.episode);
        for squad in authored.squads {
            events.roster_mut().register_squad(
                &combat, squad.owner, squad.side, &squad.members,
            ).map_err(NativeSceneBuildError::Squad)?;
        }
        if !events.roster().covers_live_roster(&combat) {
            return Err(NativeSceneBuildError::IncompleteSquadRoster);
        }
        let state = NativeLevelState::with_decision_period(
            combat, events, authored.metadata, authored.allocate_minion,
            authored.decision_period,
        );
        let mut world = World::new(authored.world_id, authored.seed, state);
        for _ in 0..authored.initial_full_asteroids {
            world.state.combat.spawn_next_full_collision_asteroid(
                &mut world.random,
            ).map_err(NativeSceneBuildError::Combat)?;
        }
        Ok(world)
    }
}
