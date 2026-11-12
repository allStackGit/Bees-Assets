//! Shared launcher for native gameplay, headless rollouts, and evaluation.

use std::cell::Cell;
use std::collections::{HashMap, HashSet};
use std::env;
use std::error::Error;
use std::fs;
use std::io::{self, ErrorKind};
#[cfg(feature = "client")]
use std::io::Write;
use std::num::NonZeroU32;
use std::path::{Path, PathBuf};
use std::rc::Rc;
use std::sync::Arc;
use std::time::Instant;

use bees_gameplay::inference::{NativePolicyInference, NativePolicyRoute};
use bees_gameplay::group_rewards::{team_id_for_side, NativeGroupRewardDelivery};
use bees_gameplay::level_host::NativeLevelStopKind;
use bees_gameplay::squads::SquadRosterError;
use bees_hivemind::session::SquadLifetime;
use bees_host::durable_connection::DurableHiveMindConnection;
use bees_host::socket_worker::NativeSocketWorker;
use bees_host::socket::{SetupKind, SetupLevelIdentity};
use bees_host::runtime::{NativeHostRuntime, NativeHostSocketEvent};
use bees_host::source_command_scheduler::NativeSourceCommandScheduler;
use bees_host::policy_only_metadata::NativePolicyOnlyMetadata;
use bees_host::notification_file::{DurableDiagnosticNotifications, EpisodeEvaluationContext, recover_command_outcomes};
use bees_host::policy_process::{PolicyProcessError, ResidentPolicyProcess};
use bees_host::policy_action_trace::{PolicyActionPlayback, PolicyActionRecorder};
use bees_host::player_trace::{NativePlayerInput, NativePlayerTrace, NativeTraceLevel};
use bees_host::world_fingerprint::{physical_world_sha256, gameplay_world_sha256};
use bees_host::presentation::NativeSoftwareRenderer;
use bees_host::scene_assets::NativeSceneAssets;
use bees_host::scene_builder::{NativeAuthoredEntity, NativeAuthoredGroups};
use bees_host::unity_scene_snapshot::{
    NativeExportedPolicyLevelSetup, NativePhysicalScene,
};
use bees_sim::asteroid_fragments::FragmentSpawnSettings;
use bees_sim::collision::Aabb;
use bees_sim::control::distinct_team_frames;
use bees_sim::geometry::Vec2;
use bees_sim::policy_batch::{PolicyActionMask, PolicyAgentKey};
use bees_sim::simulation::{RandomStream, WorldId};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};

#[cfg(feature = "client")]
#[path = "battle_catalog.rs"]
mod battle_catalog;

type MinionAllocator = Box<
    dyn FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
>;

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct BoundsConfig {
    min: [f32; 2],
    max: [f32; 2],
}

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct FragmentConfig {
    map_bounds: BoundsConfig,
    half_map_extents: [f32; 2],
    asteroid_max_speed_exclusive: u32,
    training_health_ratio: Option<f32>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GroupParticipantConfig {
    side: i32,
    agent_id: u64,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GroupConfig {
    /// The Unity self-play episode number at the exported initial scene.
    episode_number: u32,
    /// Actual source MA-POCA group identities, not inferred hull IDs.
    participants: Vec<GroupParticipantConfig>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct LevelConfig {
    scene: PathBuf,
    world_id: u64,
    level_id: i64,
    seed: Option<u64>,
    episode_seeds: Option<Vec<u64>>,
    #[serde(default)]
    model_slots: HashMap<u8, u32>,
    /// Omit only for a game Level without MA-POCA group rewards.
    groups: Option<GroupConfig>,
    extraction: Option<ExtractionConfig>,
    survival: Option<SurvivalConfig>,
    campaign_dialogue: Option<bees_gameplay::campaign_dialogue::CampaignDialogueSpec>,
    #[serde(default)]
    reinforcements: Vec<ReinforcementConfig>,
}

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct ReinforcementConfig {
    after_seconds: f32,
    squad_item_ids: Vec<i64>,
    /// Explicit physical starting center (may be outside scene bounds).
    /// Omitted for older captured waves whose ships are already positioned.
    spawn: Option<[f32; 2]>,
    entry: Option<[f32; 2]>,
}

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct ExtractionConfig {
    player_side: i32,
    center: [f32; 2],
    half_extents: [f32; 2],
    activation_radius: f32,
    layer: u8,
}

#[derive(Clone, Copy, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct SurvivalConfig {
    defending_side: i32,
    protected_ship_id: i64,
    seconds: f32,
}

impl LevelConfig {
    fn validate_seeds(&self, episodes: u32) -> io::Result<()> {
        match (self.seed, &self.episode_seeds) {
            (Some(_), None) => Ok(()),
            (None, Some(seeds)) if seeds.len() == episodes as usize => Ok(()),
            _ => Err(invalid(format!(
                "world {} needs either seed or exactly {episodes} episode_seeds",
                self.world_id,
            ))),
        }
    }

    fn episode_seed(&self, offset: u32) -> u64 {
        match &self.episode_seeds {
            Some(seeds) => seeds[offset as usize],
            None => self.seed.expect("validated seed plan").wrapping_add(u64::from(offset)),
        }
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ModelUpdateConfig {
    /// Earliest completed rollout round at which a model can change.
    /// The runner finishes every live episode first and switches before
    /// recycling them, so benchmark outcomes never mix policy versions.
    at_round: u64,
    manifest: PathBuf,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct HiveMindConfig {
    endpoint: String,
    user_id: u64,
    version: f32,
    /// Credentials stay in the environment; never in run files/checkpoints.
    auth_ticket_env: Option<String>,
    #[serde(default)]
    allow_insecure_loopback: bool,
    reconnect_game_id: Option<i64>,
    /// Explicit opt-in: existing network-enabled training runs continue
    /// to use this socket ONLY for StoreCommands unless requested.
    #[serde(default)]
    issue_source_commands: bool,
    #[serde(default = "default_flush_seconds")]
    shutdown_flush_seconds: u32,
}
fn default_flush_seconds() -> u32 { 5 }

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct PriorOutcomeJournalConfig { run_id: String, path: PathBuf }

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct RunConfig {
    run_id: String,
    stage_name: String,
    collision_matrix: PathBuf,
    stage_palette: PathBuf,
    fragment_spawn: FragmentConfig,
    python: Option<PathBuf>,
    policy_worker: Option<PathBuf>,
    model_manifest: Option<PathBuf>,
    connection_checkpoint: PathBuf,
    notifications: PathBuf,
    policy_capture: Option<PathBuf>,
    rollout_summary: Option<PathBuf>,
    /// Stopped run journals, replayed into the ORIGINAL connection before
    /// starting a new incarnation. No previous world is resumed.
    #[serde(default)]
    recover_notifications: Vec<PathBuf>,
    #[serde(default)]
    recover_outcome_journals: Vec<PriorOutcomeJournalConfig>,
    hivemind: Option<HiveMindConfig>,
    next_squad_item_id: i64,
    broadphase_cell_size: f32,
    decision_period: u32,
    max_rounds: u64,
    #[serde(default)]
    model_updates: Vec<ModelUpdateConfig>,
    #[serde(default = "one_episode")]
    episodes_per_world: u32,
    rounds_per_window: u32,
    status_every_rounds: Option<u64>,
    delivery_every: u32,
    timing_sample_period: Option<u32>,
    levels: Vec<LevelConfig>,
}

fn one_episode() -> u32 { 1 }

fn invalid(message: impl Into<String>) -> io::Error {
    io::Error::new(ErrorKind::InvalidInput, message.into())
}

fn contextual(context: &str, error: impl std::fmt::Debug) -> io::Error {
    io::Error::other(format!("{context}: {error:?}"))
}

fn nonzero(value: u32, field: &str) -> io::Result<NonZeroU32> {
    NonZeroU32::new(value).ok_or_else(|| invalid(format!("{field} must be positive")))
}

fn resolve(root: &Path, path: &Path) -> PathBuf {
    if path.is_absolute() { path.to_owned() } else { root.join(path) }
}

fn resolve_interpreter(root: &Path, path: &Path) -> PathBuf {
    // A bare python/python3/py executable resolves through PATH on
    // Windows and Linux; explicit relative script paths use the config dir.
    if !path.is_absolute() && path.components().count() == 1 {
        path.to_owned()
    } else {
        resolve(root, path)
    }
}

fn vec2(coordinates: [f32; 2]) -> Vec2 {
    Vec2::new(coordinates[0], coordinates[1])
}

fn allocate_from_stage(next_id: Rc<Cell<i64>>) -> MinionAllocator {
    Box::new(move |mother, _beacon| {
        let item_id = next_id.get();
        let next = item_id.checked_add(1)
            .ok_or(SquadRosterError::InvalidOwner)?;
        next_id.set(next);
        Ok(SquadLifetime {
            level_id: mother.level_id,
            squad_id: item_id,
            lifecycle_id: item_id as u64,
        })
    })
}


fn episode_number(group: &GroupConfig, offset: u32) -> io::Result<u32> {
    group.episode_number.checked_add(offset)
        .ok_or_else(|| invalid("group episode number exhausted"))
}

/// In Unity, the real side-to-MA-POCA team permutation alternates with
/// episode number. A reused physical scene retains ship geometry and source
/// controller identity; only that episode-owned policy routing changes.
/// An INITIAL import must already match the captured source route.
fn bind_group_routes(
    scene: &mut NativePhysicalScene,
    group: &GroupConfig,
    offset: u32,
    verify_source: bool,
    episode_seed: u64,
) -> io::Result<[i32; 2]> {
    let sides = scene.exported_episode()
        .map_err(|error| contextual("source group sides", error))?
        .sides();
    let number = episode_number(group, offset)?;
    let movement_by_team = scene.source_policy_group.as_ref()
        .map(|source| source.movement_disabled_by_team);
    // Unity draws one of 12 ordered distinct quarter-turn frame pairs
    // at the beginning of EACH new self-play episode. Initial imports
    // retain the actual Unity frames; subsequent native episodes derive
    // a reproducible pair from this world's episode seed, never a global
    // process RNG. Both teams share one pair across every controlled ship.
    let frames = if verify_source { None } else {
        let mut random = RandomStream::new(
            episode_seed ^ 0x96C3_41A7_7F2D_5E19,
        );
        Some(distinct_team_frames(random.next_below(12) as u8))
    };
    for (&ship_id, binding) in &mut scene.source_policy_bindings {
        let side = scene.entities.iter().find_map(|entity| match entity {
            NativeAuthoredEntity::Ship(ship) if ship.entity_id == ship_id =>
                Some(ship.side),
            _ => None,
        }).ok_or_else(|| invalid(format!(
            "source policy ship {ship_id} has no authored side"
        )))?;
        let team = team_id_for_side(sides[0], sides[1], number, side)
            .map_err(|error| contextual("source group team", error))?;
        if verify_source && team != binding.team_id {
            return Err(invalid(format!(
                "source ship {ship_id} belongs to team {}, not configured self-play episode {number} team {team}",
                binding.team_id,
            )));
        }
        if verify_source && movement_by_team.is_some_and(|flags|
            binding.movement_disabled != flags[usize::from(team)]
        ) {
            return Err(invalid(format!(
                "source ship {ship_id} has mismatched team movement restriction",
            )));
        }
        binding.team_id = team;
        if let Some(flags) = movement_by_team {
            binding.movement_disabled = flags[usize::from(team)];
        }
        if let Some((team0, team1)) = frames {
            binding.frame = if team == 0 { team0 } else { team1 };
        }
    }
    Ok(sides)
}

fn make_setup(
    config: &RunConfig,
    level: &LevelConfig,
    assets: &NativeSceneAssets,
    period: NonZeroU32,
    next_id: Rc<Cell<i64>>,
    seed: u64,
    episode_offset: u32,
    sides: [i32; 2],
    remapped_squads: &HashMap<i64, i64>,
) -> io::Result<NativeExportedPolicyLevelSetup<MinionAllocator>> {
    let groups = level.groups.as_ref().map(|group| -> io::Result<NativeAuthoredGroups> {
        let number = episode_number(group, episode_offset)?;
        let delivery = NativeGroupRewardDelivery::new(
            sides[0], sides[1], number,
        ).map_err(|error| contextual("MA-POCA group", error))?;
        Ok(NativeAuthoredGroups {
            delivery,
            participants: group.participants.iter()
                .map(|participant| (participant.side, participant.agent_id))
                .collect(),
        })
    }).transpose()?;

    Ok(NativeExportedPolicyLevelSetup {
        world_id: WorldId(level.world_id),
        level_id: level.level_id,
        seed,
        broadphase_cell_size: config.broadphase_cell_size,
        decision_period: period,
        initial_full_asteroids: 0,
        groups,
        ship_catalog: assets.shared_ship_catalog(),
        model_slots: level.model_slots.clone(),
        extraction: level.extraction.as_ref().map(|zone| bees_sim::combat_world::ExtractionZone {
            player_side: zone.player_side, center: vec2(zone.center), half_extents: vec2(zone.half_extents),
            activation_radius: zone.activation_radius, layer: zone.layer,
        }),
        survival: level.survival.map(|objective| bees_sim::episode_terminal::SurvivalObjective {
            defending_side: objective.defending_side,
            protected_ship_id: objective.protected_ship_id,
            seconds: objective.seconds,
        }),
        campaign_dialogue: level.campaign_dialogue,
        reinforcements: level.reinforcements.iter().map(|wave| bees_host::NativeReinforcementPlan {
            after_seconds: wave.after_seconds,
            squad_item_ids: wave.squad_item_ids.iter().map(|id| remapped_squads.get(id).copied().unwrap_or(*id)).collect(),
            spawn: wave.spawn.map(vec2),
            entry: wave.entry.map(vec2),
        }).collect(),
        allocate_minion: allocate_from_stage(next_id),
    })
}

#[derive(Default)]
struct RolloutModel {
    resident: Option<ResidentPolicyProcess>,
    recorder: Option<PolicyActionRecorder>,
    playback: Option<(PolicyActionPlayback, String)>,
}

impl RolloutModel {
    fn model_manifest_sha256(&self) -> Option<String> {
        self.resident.as_ref().map(ResidentPolicyProcess::model_manifest_sha256)
            .or_else(|| self.playback.as_ref().map(|(_, digest)| digest.clone()))
    }

    fn reload_manifest(&mut self, manifest: &Path) -> Result<u64, PolicyProcessError> {
        self.resident.as_mut().ok_or(PolicyProcessError::Unavailable)?.reload_manifest(manifest)
    }
}

impl NativePolicyInference for RolloutModel {
    type Error = PolicyProcessError;

    fn behavior_statistics(&self) -> &[f32] {
        self.resident.as_ref().map_or(&[], ResidentPolicyProcess::behavior_statistics)
    }

    fn model_generation(&self) -> u64 {
        self.resident.as_ref().map_or(0, ResidentPolicyProcess::model_generation)
    }

    fn forward(
        &mut self, keys: &[PolicyAgentKey], routes: &[NativePolicyRoute],
        observations: &[f32], masks: &[PolicyActionMask],
        continuous: &mut [f32], discrete: &mut [u8],
    ) -> Result<(), Self::Error> {
        if let Some((playback, _)) = &mut self.playback {
            return Ok(playback.apply(keys, routes, observations, masks, continuous, discrete)?);
        }
        self.resident.as_mut().ok_or(PolicyProcessError::Unavailable)?
            .forward(keys, routes, observations, masks, continuous, discrete)?;
        if let Some(recorder) = &mut self.recorder {
            recorder.record(keys, routes, observations, masks, continuous, discrete)?;
        }
        Ok(())
    }
}

type RolloutHost = NativeHostRuntime<NativePolicyOnlyMetadata, MinionAllocator,
    RolloutModel, DurableDiagnosticNotifications>;

/// One canonical capture path for periodic and final v4 checkpoints.
fn replay_world_checkpoint(host: &RolloutHost, index: usize, round: u64) -> Value {
    let world = host.simulation().world(index).expect("registered native replay World");
    json!({
        "kind": "physical_state_checkpoint",
        "round": round,
        "world_id": world.id.0,
        "episode_revision": world.episode_revision(),
        "tick": world.tick(),
        "sha256": physical_world_sha256(
            world.id, world.episode_revision(), world.tick(), &world.state.combat,
        ),
        "gameplay_sha256": gameplay_world_sha256(
            world.id, world.episode_revision(), world.tick(),
            &world.state.combat, world.state.events.roster(),
            world.state.events.commands(), &world.state.metadata,
        ),
    })
}

fn gameplay_config_sha256(
    config: &RunConfig, assets: &NativeSceneAssets, interactive: bool,
) -> String {
    let hivemind = config.hivemind.as_ref().map(|network| json!({
        "endpoint": network.endpoint,
        "user_id": network.user_id,
        "version": network.version,
        "reconnect_game_id": network.reconnect_game_id,
        // Source controller and opponent identities are part of the scene.
        "issue_source_commands": network.issue_source_commands,
        "command_poll_rounds": if interactive { 1 } else { config.rounds_per_window },
    }));
    let mut inputs = json!({
        "source_assets_sha256": assets.source_assets_sha256(),
        "fragment_spawn": config.fragment_spawn,
        "decision_period": config.decision_period,
        "broadphase_cell_size": config.broadphase_cell_size,
        "interactive": interactive,
        "hivemind": hivemind,
    });
    let survival = config.levels.iter().filter_map(|level|
        level.survival.map(|objective| (level.world_id, objective)))
        .collect::<std::collections::BTreeMap<_, _>>();
    if !survival.is_empty() { inputs["survival"] = json!(survival); }
    let extraction = config.levels.iter().filter_map(|level|
        level.extraction.as_ref().map(|zone| (level.world_id, zone)))
        .collect::<std::collections::BTreeMap<_, _>>();
    if !extraction.is_empty() { inputs["extraction"] = json!(extraction); }
    let reinforcements = config.levels.iter().filter(|level| !level.reinforcements.is_empty())
        .map(|level| (level.world_id, &level.reinforcements)).collect::<std::collections::BTreeMap<_, _>>();
    if !reinforcements.is_empty() { inputs["reinforcements"] = json!(reinforcements); }
    let dialogue = config.levels.iter().filter_map(|level|
        level.campaign_dialogue.map(|cue| (level.world_id, cue)))
        .collect::<std::collections::BTreeMap<_, _>>();
    if !dialogue.is_empty() { inputs["campaign_dialogue"] = json!(dialogue); }
    format!("{:x}", Sha256::digest(inputs.to_string().as_bytes()))
}

fn bind_evaluation_episode(
    host: &mut RolloutHost, index: usize, level: &LevelConfig, seed: u64, scene_sha256: &str,
    gameplay_config_sha256: &str,
    episode_offset: u32, decision_period: u32,
) -> io::Result<()> {
    let world = host.simulation().world(index).ok_or_else(|| invalid("missing evaluation Level"))?;
    let id = world.id;
    let revision = world.episode_revision();
    let [bee_side, human_side] = world.state.events.sides();
    let authored_policy_slots_by_side = world.state.metadata.authored_policy_slots_by_side();
    let group_episode_number = level.groups.as_ref().map(|group|
        episode_number(group, episode_offset)).transpose()?;
    let mut group_team_by_side = std::collections::BTreeMap::new();
    if let Some(number) = group_episode_number {
        for side in [bee_side, human_side] {
            let team = team_id_for_side(bee_side, human_side, number, side)
                .map_err(|error| contextual("evaluation group team", error))?;
            group_team_by_side.insert(side, team);
        }
    }
    let mut group_agents_by_team = std::collections::BTreeMap::<_, std::collections::BTreeSet<_>>::new();
    if let Some(group) = &level.groups {
        for participant in &group.participants {
            let team = group_team_by_side[&participant.side];
            group_agents_by_team.entry(team).or_default().insert(participant.agent_id);
        }
    }
    let context = EpisodeEvaluationContext {
        level_id: level.level_id,
        seed,
        scene_sha256: scene_sha256.to_owned(),
        gameplay_config_sha256: gameplay_config_sha256.to_owned(),
        bee_side,
        human_side,
        policy_generation: host.model_mut().model_generation(),
        policy_manifest_sha256: host.model_mut().model_manifest_sha256(),
        model_slots_by_team: level.model_slots.iter().map(|(&team, &slot)| (team, slot)).collect(),
        authored_policy_slots_by_side,
        group_episode_number,
        group_team_by_side,
        group_agents_by_team,
        decision_period,
    };
    host.sink_mut().bind_evaluation_episode(id, revision, context)
}

struct RunNetwork {
    worker: NativeSocketWorker,
    levels: Vec<i32>,
    next_level: usize,
    user_id: u64,
    version: f32,
    auth_ticket: Option<String>,
    flush_seconds: u32,
    issue_source_commands: bool,
    scheduler: NativeSourceCommandScheduler,
}

impl RunNetwork {
    fn start(config: &HiveMindConfig, levels: &[LevelConfig], fixed_dt_by_level: &[f32],
        connection: &mut DurableHiveMindConnection)
        -> io::Result<Self>
    {
        let auth_ticket = config.auth_ticket_env.as_ref().map(|name|
            env::var(name).map_err(|_| invalid("configured HiveMind auth ticket environment variable is unavailable")))
            .transpose()?;
        let levels = levels.iter().map(|level| i32::try_from(level.level_id)
            .map_err(|_| invalid("HiveMind server LevelId must fit int32"))).collect::<Result<Vec<_>,_>>()?;
        if fixed_dt_by_level.len() != levels.len() {
            return Err(invalid("HiveMind command clock has mismatched Level timesteps"));
        }
        let scheduler = NativeSourceCommandScheduler::new(&levels, fixed_dt_by_level)?;
        let mut network = Self {
            worker: NativeSocketWorker::start(config.endpoint.clone(), config.allow_insecure_loopback)
                .map_err(|error| contextual("HiveMind network startup", error))?,
            levels, next_level: 0, user_id: config.user_id, version: config.version,
            auth_ticket, flush_seconds: config.shutdown_flush_seconds,
            issue_source_commands: config.issue_source_commands,
            scheduler,
        };
        let (kind, server_game_id) = config.reconnect_game_id
            .map_or((SetupKind::Initial, 0), |game| (SetupKind::Reconnect, game));
        network.setup_next(connection, kind, server_game_id)?;
        Ok(network)
    }

    fn setup_next(&mut self, connection: &mut DurableHiveMindConnection, kind: SetupKind, server_game_id: i64)
        -> io::Result<()>
    {
        self.worker.send_setup(connection, SetupLevelIdentity {
            kind, level_id: self.levels[self.next_level], server_game_id,
            user_id: self.user_id, auth_ticket: self.auth_ticket.as_deref(), version: self.version,
        }).map_err(|error| contextual("HiveMind Level setup", error))
    }

    // The shared scheduler is also available to other native host adapters.
    // The launcher remains only the source of socket/setup readiness.
    fn issue_due_source_commands(&mut self, host: &mut RolloutHost) -> io::Result<()> {
        if !self.issue_source_commands { return Ok(()); }
        self.scheduler.issue_due(host, &self.worker,
            self.next_level == self.levels.len())
    }

    fn rounds_until_next_source_pulse(&mut self, host: &RolloutHost) -> io::Result<u64> {
        if !self.issue_source_commands || !self.worker.ready() {
            return Ok(u64::MAX);
        }
        self.scheduler.rounds_until_next_pulse(host,
            self.next_level == self.levels.len())
    }

    fn poll(&mut self, host: &mut RolloutHost) -> io::Result<()> {
        for _ in 0..16 {
            let event = if self.issue_source_commands {
                host.poll_network_and_continue_source_commands(&mut self.worker)
            } else {
                host.poll_network(&mut self.worker)
            };
            let Some(event) = event
                .map_err(|error| contextual("HiveMind network", error))? else { break; };
            match event {
                NativeHostSocketEvent::SetupAccepted { request_hash, server_game_id } => {
                    println!("{}", json!({"kind": "hivemind_setup", "request_hash": request_hash,
                        "game_id": server_game_id, "level_id": self.levels[self.next_level]}));
                    self.next_level += 1;
                    if self.next_level < self.levels.len() {
                        self.setup_next(host.connection_mut(), SetupKind::Initial, 0)?;
                    }
                }
                NativeHostSocketEvent::ServerFrame { request_hash,
                    delivery: Some(bees_gameplay::level_host::NativeHiveMindFrameDelivery::StoreCommands(ack)), .. } => {
                    println!("{}", json!({"kind": "hivemind_store_ack", "request_hash": request_hash,
                        "acknowledgement": format!("{ack:?}")}));
                }
                NativeHostSocketEvent::ServerFrame {
                    delivery: Some(
                        bees_gameplay::level_host::NativeHiveMindFrameDelivery::Strategy(_) |
                        bees_gameplay::level_host::NativeHiveMindFrameDelivery::Targeting(_)
                    ), ..
                } if self.issue_source_commands => {
                    // The SAME authoritative Level driver has already
                    // installed the selection and retained learning credit.
                }
                // Reject every other message rather than losing a setting,
                // unowned response or unresolved server reservation.
                NativeHostSocketEvent::ServerFrame { request_hash, delivery, .. } =>
                    return Err(invalid(format!("unowned HiveMind feature reply {request_hash}: {delivery:?}"))),
            }
        }
        Ok(())
    }
}

fn apply_player_input(host: &mut RolloutHost, action: NativePlayerInput) -> Result<&'static str, String> {
    use bees_host::runtime::{HostPlayerInputError, NativePlayerInputOutcome as Outcome};
    use bees_gameplay::player_inputs::{NativePlayerSupportAction, NativePlayerShipSpecialKind};
    // Live window and offline playback use precisely the same host-level
    // authority dispatch. Only human-readable feedback belongs here.
    let outcome = match host.apply_player_input(&action) {
        Ok(outcome) => outcome,
        Err(HostPlayerInputError::Gameplay(error)) if error.is_rejection() =>
            return Ok(match action {
                NativePlayerInput::Move(_) => "Squad cannot accept that move",
                NativePlayerInput::Support(_) => "Squad cannot accept that command",
                NativePlayerInput::Fire(_) | NativePlayerInput::HeldFire(_) =>
                    "Squad cannot accept manual fire",
                NativePlayerInput::CeaseFire(_) => "Cannot change firing mode",
                NativePlayerInput::Special(_) => "Squad cannot accept that special",
                NativePlayerInput::MatchSpeed(_) => "Squad cannot change speed",
                NativePlayerInput::Shooting(_) => "Squad cannot change shooting priority",
                NativePlayerInput::LockOn(_) => "Squad cannot change Lock On",
                NativePlayerInput::Chase(_) => "Squad cannot change Chase",
            }),
        Err(error) => return Err(format!("native player input: {error:?}")),
    };
    let feedback = match (action, outcome) {
        (NativePlayerInput::Move(_), Outcome::Move(counts)) => {
            if counts.accepted_squads == 0 { "Selected squads cannot accept that destination" }
            else if counts.rejected_squads == 0 && counts.unreachable_squads == 0 { "Moves accepted" }
            else { "Eligible squads moved; some unavailable" }
        }
        (NativePlayerInput::Support(input), Outcome::Support(counts)) => {
            if input.action == NativePlayerSupportAction::Hold {
                if counts.stopped_chases > 0 { "Chase stopped" }
                else if counts.accepted_squads > 0 { "No active chase" }
                else { "Selected squads cannot stop chase" }
            } else if counts.accepted_squads == 0 { "Selected squads cannot accept that command" }
            else if counts.rejected_squads == 0 && counts.unavailable_squads == 0 { "Commands accepted" }
            else { "Eligible squads commanded; some unavailable" }
        }
        (NativePlayerInput::Fire(input), Outcome::Fire(counts)) => {
            if counts.accepted_squads == 0 { "Selected squads have no eligible manual weapons" }
            else if input.target.is_some() { "Point fire set" }
            else { "Point fire stopped" }
        }
        (NativePlayerInput::HeldFire(_), Outcome::HeldFire(message)) => message,
        (NativePlayerInput::Chase(input), Outcome::Chase(counts)) => {
            if counts.accepted_squads == 0 { "No selected squads can change Chase" }
            else if counts.rejected_squads > 0 { "Chase updated; some squads unavailable" }
            else if input.chasing { "Chase enabled" }
            else { "Chase disabled" }
        }
        (NativePlayerInput::LockOn(input), Outcome::LockOn(counts)) => {
            if counts.accepted_squads == 0 { "No selected enemy command can change lock" }
            else if counts.rejected_squads > 0 { "Lock On changed; some squads unavailable" }
            else if input.locked { "Locked on to current enemy" }
            else { "Lock On released" }
        }
        (NativePlayerInput::Shooting(_), Outcome::Shooting(counts)) => {
            if counts.accepted_squads == 0 { "No selected squads can change targeting" }
            else if counts.rejected_squads > 0 || counts.unavailable_squads > 0 {
                "Shooting priority changed; some squads unavailable"
            } else { "Shooting priority updated" }
        }
        (NativePlayerInput::MatchSpeed(input), Outcome::MatchSpeed(counts)) => {
            if counts.accepted_squads == 0 { "No selected squads can change speed" }
            else if counts.rejected_squads > 0 || counts.unavailable_squads > 0 {
                "Squad speeds updated; some unavailable"
            } else if input.match_speed { "Squad speeds matched" }
            else { "Original ship speeds restored" }
        }
        (NativePlayerInput::Special(input), Outcome::Special(counts)) => {
            if counts.accepted_squads == 0 { "No selected ships have a ready special" }
            else if counts.rejected_squads > 0 || counts.unavailable_squads > 0 {
                "Ready specials queued; some squads unavailable"
            } else {
                match input.kind {
                    NativePlayerShipSpecialKind::DropBeacon => "Beacon deployment queued",
                    NativePlayerShipSpecialKind::Detonate => "Detonation queued",
                    NativePlayerShipSpecialKind::Charge => "Barge charge queued",
                }
            }
        }
        (NativePlayerInput::CeaseFire(input), Outcome::CeaseFire(counts)) => {
            if counts.accepted_squads == 0 { "Selected squads cannot change firing mode" }
            else if counts.rejected_squads != 0 { "Firing mode updated; some squads unavailable" }
            else if input.cease_fire { "Cease fire enabled" }
            else { "Attack on sight enabled" }
        }
        _ => return Err("native host returned an input outcome for a different action".to_owned()),
    };
    Ok(feedback)
}

const USAGE: &str = "Usage: bees [play|run] <run.json> [--output-dir <new-dir>] [--capture <frame.ppm>]
       bees menu <catalog.json> --output-dir <battle-runs>
  play  Open the native battle window (requires the client feature)
  run   Run the battle headlessly (default)
  --replay-inputs <player-inputs.jsonl>  Play back a recorded offline battle";

struct BattleRestart {
    replay_inputs: Option<PathBuf>,
}

#[derive(Clone, Copy)]
struct CampaignMission<'a> {
    campaign_id: &'a str,
    mission_id: &'a str,
    player_side: i32,
}

#[derive(Serialize, Deserialize)]
struct CampaignOutcome {
    campaign_id: String,
    mission_id: String,
    player_side: i32,
    winner_side: i32,
}

pub fn run() -> Result<(), Box<dyn Error>> {
    let mut args = env::args_os().skip(1);
    let first = args.next().ok_or_else(|| invalid(USAGE))?;
    if first == "--help" || first == "-h" {
        println!("{USAGE}");
        return Ok(());
    }
    if first == "menu" {
        #[cfg(feature = "client")]
        {
            let catalog = PathBuf::from(args.next().ok_or_else(|| invalid(USAGE))?);
            if args.next().is_none_or(|flag| flag != "--output-dir") {
                return Err(invalid(USAGE).into());
            }
            let output = PathBuf::from(args.next().ok_or_else(|| invalid(USAGE))?);
            if args.next().is_some() { return Err(invalid(USAGE).into()); }
            return run_menu(&catalog, &output);
        }
        #[cfg(not(feature = "client"))]
        return Err(invalid("The battle menu requires building with --features client").into());
    }
    let play = first == "play";
    let mut view = play;
    let config_path = PathBuf::from(if view || first == "run" {
        args.next().ok_or_else(|| invalid(USAGE))?
    } else { first });
    let mut capture = None;
    let mut output = None;
    let mut replay_inputs = None;
    while let Some(flag) = args.next() {
        if flag == "--view" && !view {
            view = true;
        } else if flag == "--capture" && capture.is_none() {
            capture = Some(PathBuf::from(args.next()
                .ok_or_else(|| invalid("--capture requires an output .ppm path"))?));
        } else if flag == "--output-dir" && output.is_none() {
            output = Some(PathBuf::from(args.next()
                .ok_or_else(|| invalid("--output-dir requires a new directory path"))?));
        } else if flag == "--replay-inputs" && replay_inputs.is_none() {
            replay_inputs = Some(PathBuf::from(args.next()
                .ok_or_else(|| invalid("--replay-inputs requires a trace path"))?));
        } else {
            return Err(invalid(USAGE).into());
        }
    }
    #[cfg(not(feature = "client"))]
    if view { return Err(invalid("Windowed play requires building with --features client").into()); }
    if replay_inputs.is_some() && output.is_none() {
        return Err(invalid("--replay-inputs requires --output-dir").into());
    }
    run_session(&config_path, play, view, output, capture, replay_inputs, None)
}

fn run_session(
    config_path: &Path, play: bool, view: bool, output: Option<PathBuf>,
    capture: Option<PathBuf>, mut replay_inputs: Option<PathBuf>,
    mission: Option<CampaignMission<'_>>,
) -> Result<(), Box<dyn Error>> {
    let config_path = config_path.canonicalize()?;
    let mut replay_number = 0_u64;
    let mut battle_output = output.clone();
    let mut battle_capture = capture.clone();
    while let Some(restart) = run_battle(&config_path, play, view, battle_output.as_deref(), battle_capture.as_deref(),
        replay_number, replay_inputs.as_deref(), mission)? {
        replay_number = replay_number.checked_add(1).ok_or_else(|| invalid("replay count exhausted"))?;
        let next = output.as_ref().ok_or_else(|| invalid("replay requires --output-dir"))?
            .join(format!("replay-{replay_number:04}"));
        battle_capture = capture.as_ref().map(|_| next.join("final.ppm"));
        battle_output = Some(next);
        replay_inputs = restart.replay_inputs;
    }
    Ok(())
}

#[cfg(feature = "client")]
fn run_menu(catalog_path: &Path, output_root: &Path) -> Result<(), Box<dyn Error>> {
    use bees_host::presentation::{BattleMenuRow, select_battle};

    let catalog_path = catalog_path.canonicalize()?;
    let catalog: battle_catalog::BattleCatalog = serde_json::from_slice(&fs::read(&catalog_path)?)?;
    catalog.validate()?;
    let root = catalog_path.parent().ok_or_else(|| invalid("missing catalog directory"))?;
    fs::create_dir_all(output_root)?;
    let mut progress = catalog.load_progress(output_root)?;
    let mut selected = 0;
    let mut number = 0_u64;
    let mut launch_failed = false;
    loop {
        let rows = catalog.battles.iter().map(|entry| {
            let unlocked = entry.mission.as_ref().is_none_or(|mission|
                mission.requires.iter().all(|id| progress.completed(id)));
            let status = if !unlocked { "  [Locked]" }
                else if entry.mission.as_ref().is_some_and(|mission| progress.completed(&mission.id)) {
                    "  [Complete]"
                } else { "" };
            BattleMenuRow { title: entry.title.trim(), status, unlocked }
        }).collect::<Vec<_>>();
        let Some(index) = select_battle(&rows, &mut selected, launch_failed)? else { break };
        let entry = &catalog.battles[index];
        let config = resolve(root, entry.config_for(&progress));
        let mission = catalog.campaign_id.as_deref().zip(entry.mission.as_ref())
            .map(|(campaign_id, mission)| CampaignMission {
                campaign_id, mission_id: &mission.id, player_side: mission.player_side,
            });
        // Reserve a parent atomically so another launcher cannot claim the
        // same output. The battle owns creation of its normal run directory.
        let output = loop {
            number = number.checked_add(1).ok_or_else(|| invalid("battle count exhausted"))?;
            let directory = output_root.join(format!("battle-{number:04}"));
            match fs::create_dir(&directory) {
                Ok(()) => break directory.join("run"),
                Err(error) if error.kind() == ErrorKind::AlreadyExists => continue,
                Err(error) => return Err(error.into()),
            }
        };
        let result = run_session(&config, true, true, Some(output), None, None, mission);
        if mission.is_some() { progress = catalog.load_progress(output_root)?; }
        launch_failed = result.is_err();
        if let Err(error) = result {
            eprintln!("Could not play {} ({}): {error}", entry.title, config.display());
        }
    }
    Ok(())
}

fn run_battle(
    config_path: &Path, play: bool, view: bool, output: Option<&Path>,
    capture: Option<&Path>, replay_number: u64, replay_inputs: Option<&Path>,
    mission: Option<CampaignMission<'_>>,
) -> Result<Option<BattleRestart>, Box<dyn Error>> {
    let run_start = Instant::now();
    let root = config_path.parent().ok_or_else(|| invalid("missing config directory"))?;
    let config_bytes = fs::read(config_path)?;
    let mut config: RunConfig = serde_json::from_slice(&config_bytes)?;
    if mission.is_some() && (config.levels.len() != 1 || config.episodes_per_world != 1) {
        return Err(invalid("A campaign mission requires one Level and one episode").into());
    }
    if replay_inputs.is_some() && !config.model_updates.is_empty() {
        return Err(invalid("player input playback requires one policy manifest for the whole battle").into());
    }
    if config.status_every_rounds == Some(0) {
        return Err(invalid("status_every_rounds must be positive").into());
    }

    let period = nonzero(config.decision_period, "decision_period")?;
    let window = nonzero(config.rounds_per_window, "rounds_per_window")?;
    let delivery = nonzero(config.delivery_every, "delivery_every")?;
    let timing = config.timing_sample_period
        .map(|value| nonzero(value, "timing_sample_period"))
        .transpose()?;
    if config.run_id.trim().is_empty() || config.stage_name.trim().is_empty() ||
        config.levels.is_empty() || config.max_rounds == 0 ||
        config.episodes_per_world == 0 ||
        !config.broadphase_cell_size.is_finite() ||
        config.broadphase_cell_size <= 0.0 ||
        config.next_squad_item_id <= 0 ||
        config.next_squad_item_id == i64::MAX
    {
        return Err(invalid("invalid run identity, bounds, allocator or round budget").into());
    }
    if replay_number > 0 {
        config.run_id.push_str(&format!("-replay-{replay_number}"));
    }
    for level in &config.levels {
        level.validate_seeds(config.episodes_per_world)?;
    }
    let mut previous_update = 0_u64;
    for update in &config.model_updates {
        if update.at_round <= previous_update ||
            update.at_round >= config.max_rounds ||
            update.manifest.as_os_str().is_empty()
        {
            return Err(invalid(
                "model_updates must be strictly increasing rounds below max_rounds with nonempty manifest paths",
            ).into());
        }
        previous_update = update.at_round;
    }
    if output.is_some() && (!config.recover_notifications.is_empty() ||
        !config.recover_outcome_journals.is_empty() ||
        config.hivemind.as_ref().is_some_and(|network| network.reconnect_game_id.is_some()))
    {
        return Err(invalid("HiveMind recovery and reconnection require the original configured checkpoint and journal paths").into());
    }
    if replay_inputs.is_some() && config.hivemind.is_some() {
        return Err(invalid("Player input playback requires an offline battle").into());
    }
    let checkpoint = output.as_ref().map_or_else(
        || resolve(root, &config.connection_checkpoint), |dir| dir.join("connection-checkpoint.json"),
    );
    let notifications = output.as_ref().map_or_else(
        || resolve(root, &config.notifications), |dir| dir.join("events.jsonl"),
    );
    let policy_capture = config.policy_capture.as_ref().filter(|_| replay_inputs.is_none()).map(|path| output.as_ref().map_or_else(
        || resolve(root, path), |dir| dir.join("decisions.bin"),
    ));
    let summary = output.as_ref().map(|dir| dir.join("rollout-summary.json"))
        .or_else(|| config.rollout_summary.as_ref().map(|path| resolve(root, path)));
    if checkpoint == notifications {
        return Err(invalid("checkpoint and notification log must be distinct").into());
    }
    if policy_capture.as_ref().is_some_and(|path| path == &checkpoint || path == &notifications) {
        return Err(invalid("policy capture, checkpoint and notification log must be distinct").into());
    }
    let bounds = &config.fragment_spawn.map_bounds;
    let fragment_settings = FragmentSpawnSettings {
        map_bounds: Aabb {
            min: vec2(bounds.min), max: vec2(bounds.max),
        },
        half_map_extents: vec2(config.fragment_spawn.half_map_extents),
        asteroid_max_speed_exclusive:
            config.fragment_spawn.asteroid_max_speed_exclusive,
        training_health_ratio: config.fragment_spawn.training_health_ratio,
    };
    if !fragment_settings.valid() {
        return Err(invalid("fragment_spawn must contain real valid Stage settings").into());
    }

    // Preflight source assets, every Level identity and the requested model
    // routes before touching an output file or opening the policy process.
    let assets = NativeSceneAssets::load(
        &resolve(root, &config.collision_matrix),
        &resolve(root, &config.stage_palette),
        &config.stage_name,
        fragment_settings,
    ).map_err(|error| contextual("stage assets", error))?;
    let mut declared = HashSet::new();
    let model_source = match (&config.python, &config.policy_worker, &config.model_manifest) {
        (Some(python), Some(worker), Some(manifest)) => {
            let manifest_path = resolve(root, manifest);
            let bytes = fs::read(&manifest_path)?;
            let manifest: Value = serde_json::from_slice(&bytes)?;
            for item in manifest.get("models").and_then(Value::as_array)
                .ok_or_else(|| invalid("manifest has no models array"))?
            {
                let slot = item.get("slot").and_then(Value::as_u64)
                    .and_then(|slot| u32::try_from(slot).ok())
                    .ok_or_else(|| invalid("manifest model slot is not uint32"))?;
                declared.insert(slot);
            }
            Some((resolve_interpreter(root, python), resolve(root, worker), manifest_path,
                format!("{:x}", Sha256::digest(bytes))))
        }
        (None, None, None) if config.model_updates.is_empty() => None,
        _ => return Err(invalid("policy runs require python, policy_worker, and model_manifest").into()),
    };

    let mut level_ids = HashSet::new();
    let mut world_ids = HashSet::new();
    let mut highest_squad_item_id = 0_i64;
    let mut source_levels = Vec::with_capacity(config.levels.len());
    let mut source_sides = Vec::with_capacity(config.levels.len());
    // Many headless arenas often share one exported Level. Keep one
    // immutable source template per canonical path, not N copies and
    // filesystem reads. Each arena still parses its own mutable world.
    let mut source_jsons = Vec::with_capacity(config.levels.len());
    let mut source_templates: HashMap<PathBuf, (Arc<str>, String)> = HashMap::new();
    let mut source_digests = Vec::with_capacity(config.levels.len());
    let single_level = config.levels.len() == 1;
    for level in &mut config.levels {
        if level.level_id <= 0 || level.world_id == 0 ||
            !level_ids.insert(level.level_id) ||
            !world_ids.insert(level.world_id) ||
            !level.model_slots.values().all(|slot| declared.contains(slot))
        {
            return Err(invalid("duplicate/invalid Level or missing real model slot").into());
        }
        let source_path = resolve(root, &level.scene).canonicalize()?;
        let (source_json, source_digest) = if let Some(cached) = source_templates.get(&source_path) {
            cached.clone()
        } else {
            let text: Arc<str> = fs::read_to_string(&source_path)?.into();
            let digest = format!("{:x}", Sha256::digest(text.as_bytes()));
            source_templates.insert(source_path, (Arc::clone(&text), digest.clone()));
            (text, digest)
        };
        source_digests.push(source_digest);
        let mut source = NativePhysicalScene::parse_with_ship_catalog(&source_json, assets.ship_catalog())
            .map_err(|error| contextual("exported Level", error))?;
        if source.stage_name != config.stage_name {
            return Err(invalid("exported Level belongs to a different Stage").into());
        }
        let sides = source.gameplay_sides();
        if config.hivemind.as_ref().is_some_and(|network| network.issue_source_commands) &&
            (source.schema_version < 27 ||
                source.activate_hivemind.is_none() ||
                source.activate_mining.is_none() ||
                source.hivemind_initial_command_delay_seconds.is_none() ||
                source.starting_positions.is_none() ||
                source.source_squads.iter().any(|squad| squad.hivemind.is_none()))
        {
            return Err(invalid(format!(
                "Level {} requires schema-27 Unity HiveMind activation, timing, home positions, mining and per-squad authority for source commands",
                level.level_id,
            )).into());
        }
        // Schema 16 carries authentic Unity Agent instance identities.
        // New scenes do not need operator-supplied group IDs; any manual
        // values must agree exactly with the recorded source membership.
        if let Some(exported) = &source.source_policy_group {
            if let Some(configured) = &level.groups {
                let mut supplied: Vec<_> = configured.participants.iter()
                    .map(|member| (member.side, member.agent_id)).collect();
                let mut actual: Vec<_> = exported.participants.iter()
                    .map(|member| (member.side, member.agent_id)).collect();
                supplied.sort_unstable();
                actual.sort_unstable();
                if configured.episode_number != exported.episode_number ||
                    supplied != actual
                {
                    return Err(invalid(
                        "explicit group identity does not match exported Unity MA-POCA participants",
                    ).into());
                }
            } else {
                level.groups = Some(GroupConfig {
                    episode_number: exported.episode_number,
                    participants: exported.participants.iter().map(|member|
                        GroupParticipantConfig {
                            side: member.side, agent_id: member.agent_id,
                        }
                    ).collect(),
                });
            }
        }
        if let Some(group) = &level.groups {
            // Old source scenes lack the source's per-team movement flags.
            // They may be evaluated once, but must not silently produce
            // unfaithful alternate-team training trajectories on replay.
            if source.source_policy_group.is_none() &&
                config.episodes_per_world > 1
            {
                return Err(invalid(
                    "MA-POCA episode replay requires schema-16 source group metadata",
                ).into());
            }
            if source.source_policy_bindings.is_empty() {
                return Err(invalid("group source has no bound policy controllers").into());
            }
            if group.episode_number == 0 ||
                group.episode_number.checked_add(config.episodes_per_world - 1)
                    .is_none()
            {
                return Err(invalid("configured group episode range is invalid").into());
            }
            let mut agents = HashSet::new();
            for participant in &group.participants {
                if !sides.contains(&participant.side) {
                    return Err(invalid("MA-POCA participant side does not belong to Level").into());
                }
                if participant.agent_id == 0 ||
                    !agents.insert((participant.side, participant.agent_id))
                {
                    return Err(invalid("MA-POCA participant ID is zero or repeated").into());
                }
            }
            if group.participants.is_empty() {
                return Err(invalid("MA-POCA group has no participating source agents").into());
            }
            // Check the exported initial controller team BEFORE any
            // per-episode permutation or output file creation.
            bind_group_routes(&mut source, group, 0, true, level.episode_seed(0))?;
            // Replayed self-play episodes flip each controlled side's
            // team ID. Only require the other slot when a replay can
            // actually occur; a one-sided/scripted episode may have just
            // one participating policy team.
            if config.episodes_per_world > 1 &&
                source.source_policy_bindings.values().any(|binding|
                    !level.model_slots.contains_key(&(1 - binding.team_id))
                )
            {
                return Err(invalid(
                    "recycled group Levels require real models for both alternating policy teams",
                ).into());
            }
        }
        for squad in &source.source_squads {
            highest_squad_item_id = highest_squad_item_id.max(squad.item_id);
        }
        if level.reinforcements.iter().flat_map(|wave| &wave.squad_item_ids)
            .any(|id| !source.source_squads.iter().any(|squad| squad.item_id == *id))
        {
            return Err(invalid("Reinforcements reference an unknown source squad item ID").into());
        }
        if mission.is_some_and(|mission| !source.source_squads.iter().any(|squad|
            squad.side == mission.player_side && squad.player_permissions.user_controlled))
        {
            return Err(invalid("The campaign player side has no authored player squad").into());
        }
        if let Some(zone) = &level.extraction {
            if !sides.contains(&zone.player_side) ||
                mission.is_some_and(|mission| mission.player_side != zone.player_side) ||
                !source.source_squads.iter().any(|squad|
                    squad.side == zone.player_side && squad.player_permissions.user_controlled)
            {
                return Err(invalid("Extraction must use the authored campaign player side").into());
            }
        }
        if let Some(objective) = &level.survival {
            if mission.is_some_and(|mission| mission.player_side != objective.defending_side) ||
                !source.source_squads.iter().any(|squad|
                    squad.side == objective.defending_side && squad.player_permissions.user_controlled)
            {
                return Err(invalid("Survival must use the authored campaign player side").into());
            }
            if mission.is_some() {
                // The shared survival controller wins only after
                // ceil(seconds / fixed_dt) completed physics ticks.
                // A campaign with fewer available rounds cannot win,
                // regardless of tactical decisions or remaining health.
                let required = (objective.seconds / source.fixed_dt_seconds).ceil();
                if required.is_finite() && required >= 1.0 &&
                    required < u64::MAX as f32 &&
                    required as u64 > config.max_rounds
                {
                    return Err(invalid(format!(
                        "Campaign defense requires at least {} rounds at the authored fixed step; max_rounds is {}",
                        required as u64, config.max_rounds,
                    )).into());
                }
            }
        }
        if let Some(dialogue) = level.campaign_dialogue {
            let player_side = dialogue.player_side();
            if !single_level || !sides.contains(&player_side) ||
                mission.is_some_and(|mission| mission.player_side != player_side) ||
                !source.source_squads.iter().any(|squad|
                    squad.side == player_side && squad.player_permissions.user_controlled)
            {
                return Err(invalid("Campaign dialogue requires one Level and its authored player side").into());
            }
        }
        if model_source.is_none() && !source.source_policy_bindings.is_empty() {
            return Err(invalid("authored policy controllers require a model worker").into());
        }
        for binding in source.source_policy_bindings.values() {
            if !level.model_slots.contains_key(&binding.team_id) {
                return Err(invalid("Level has an unresolved source policy team").into());
            }
        }
        source_levels.push(source);
        source_sides.push(sides);
        source_jsons.push(source_json);
    }
    if config.next_squad_item_id <= highest_squad_item_id {
        return Err(invalid(
            "next_squad_item_id must be the real next Stage pool identity, above all source squads",
        ).into());
    }

    // Unity's Stage Pool.ItemCount is a single monotonic allocator; a
    // shared Rc<Cell> gives independent Level callbacks one identity stream
    // while keeping all mutable state on the simulation-owning thread.
    let next_id = Rc::new(Cell::new(config.next_squad_item_id));
    // Multiple independently driven arena instances may intentionally use
    // the same exported Level template, whose original Stage pool IDs are
    // duplicates. Keep the first source lifetime and allocate fresh IDs
    // for subsequent copies so session ownership remains unambiguous.
    let mut seen_squad_ids = HashSet::new();
    let mut remapped_squads_by_level = Vec::with_capacity(source_levels.len());
    for source in &mut source_levels {
        let mut remapped = HashMap::new();
        for squad in &mut source.source_squads {
            if !seen_squad_ids.insert(squad.item_id) {
                let item_id = next_id.get();
                let following = item_id.checked_add(1)
                    .ok_or_else(|| invalid("Stage squad identities exhausted"))?;
                next_id.set(following);
                remapped.insert(squad.item_id, item_id);
                squad.item_id = item_id;
                seen_squad_ids.insert(item_id);
            }
        }
        remapped_squads_by_level.push(remapped);
    }
    let source_fixed_dt_by_level: Vec<f32> =
        source_levels.iter().map(|source| source.fixed_dt_seconds).collect();
    #[cfg(feature = "client")]
    let client_fixed_dt_by_level: Vec<std::time::Duration> =
        source_fixed_dt_by_level.iter().map(|&dt|
            std::time::Duration::try_from_secs_f32(dt)
                .map_err(|_| invalid("source fixed duration cannot be represented by the host clock"))
        ).collect::<io::Result<_>>()?;
    let client_bounds: Vec<_> = source_levels.iter().map(|scene| scene.bounds).collect();
    #[cfg(feature = "client")]
    let client_zoom_limits: Vec<_> =
        source_levels.iter().map(|scene| scene.camera_zoom).collect();
    // Open the optional window before starting the model or creating durable
    // run outputs. A window setup failure leaves no live episode to abandon.
    #[cfg(feature = "client")]
    let mut client = if view {
        let mut client = bees_host::client::NativeClient::new(client_bounds[0])?;
        client.set_source_zoom_limits(client_zoom_limits);
        // Stage/user settings are common to these simultaneously captured
        // Levels, while an operator can override edge panning with F6.
        client.set_source_edge_scrolling(
            source_levels[0].camera_edge_scrolling,
        );
        // Unity StageTitania2HumanFleetAtCenter assigns this precise
        // Stage.DefaultCameraPosition before the Beenoculars briefing.
        // Keep this in the mission host; the reusable client only knows
        // about an optional authored camera focus, never campaign rules.
        if config.levels.len() == 1 && matches!(
            config.levels[0].campaign_dialogue,
            Some(bees_gameplay::campaign_dialogue::CampaignDialogueSpec::TitaniaBeenoculars { .. })
        ) {
            client.focus_camera(bees_gameplay::campaign_dialogue::TITANIA_II_CENTER);
        }
        if replay_inputs.is_some() { client.set_playback(); }
        Some(client)
    } else { None };
    let mut launches = Vec::with_capacity(source_levels.len());
    for (index, (source, level)) in source_levels.into_iter()
        .zip(&config.levels).enumerate()
    {
        launches.push((
            source,
            make_setup(
                &config, level, &assets, period,
                Rc::clone(&next_id), level.episode_seed(0), 0, source_sides[index], &remapped_squads_by_level[index],
            )?,
        ));
    }

    let gameplay_digest = gameplay_config_sha256(&config, &assets, view || replay_inputs.is_some());
    let config_digest = format!("{:x}", Sha256::digest(&config_bytes));
    let trace_levels = config.levels.iter().zip(source_digests.iter()).map(|(level, digest)|
        NativeTraceLevel {
            world_id: level.world_id,
            level_id: level.level_id,
            scene_sha256: digest.clone(),
            seed: level.seed,
            episode_seeds: level.episode_seeds.clone(),
        }
    ).collect::<Vec<_>>();
    let mut playback = replay_inputs.map(|path|
        NativePlayerTrace::read(path, &config_digest, &gameplay_digest, &trace_levels,
            model_source.as_ref().map(|(_, _, _, digest)| digest.as_str()))
    ).transpose()?;
    if playback.as_ref().is_some_and(|trace| trace.end_round > config.max_rounds) {
        return Err(invalid("player trace exceeds the configured round budget").into());
    }
    let trace_output = output.filter(|_| play && replay_inputs.is_none() &&
        config.hivemind.is_none() && config.model_updates.is_empty());

    let mut model = RolloutModel::default();
    if let (Some(path), Some(trace), Some((_, _, _, manifest_digest))) =
        (replay_inputs, playback.as_ref(), model_source.as_ref())
    {
        let actions = PolicyActionPlayback::open(&path.with_file_name("policy-actions.bin"),
            trace.policy_actions_sha256.as_deref().expect("validated policy trace digest"))?;
        model.playback = Some((actions, manifest_digest.clone()));
    }
    if let Some(directory) = output {
        fs::create_dir(directory)?;
    }
    let mut connection = if config.recover_notifications.is_empty() && config.recover_outcome_journals.is_empty() {
        DurableHiveMindConnection::open(&checkpoint)
    } else { DurableHiveMindConnection::open_existing(&checkpoint) }
        .map_err(|error| contextual("HiveMind checkpoint", error))?;
    for previous in &config.recover_notifications {
        let path = resolve(root, previous);
        if path == notifications || path == checkpoint {
            return Err(invalid("recovery journals must be distinct from new outputs").into());
        }
        let recovered = recover_command_outcomes(&path, &mut connection)
            .map_err(|error| contextual("outcome journal recovery", error))?;
        println!("{}", json!({ "kind": "outcome_journal_recovered", "journal": path,
            "recovery": recovered }));
    }

    for previous in &config.recover_outcome_journals {
        let path = resolve(root, &previous.path);
        if path == notifications || path == checkpoint {
            return Err(invalid("recovery journals must be distinct from new outputs").into());
        }
        let recovered = bees_host::journal_recovery::replay_command_outcomes(&path, &previous.run_id,
            |recorded| connection.queue_journal_outcomes(recorded).map(|request| request.is_some()))
            .map_err(|error| contextual("schema1 outcome journal recovery", error))?;
        println!("{}", json!({"kind":"outcome_journal_recovered","journal":path,
            "newly_checkpointed":recovered.newly_checkpointed,"already_checkpointed":recovered.already_checkpointed}));
    }

    if policy_capture.is_some() && model_source.is_none() {
        return Err(invalid("policy capture requires a resident model worker").into());
    }
    if let Some((python, worker, manifest, expected_sha)) = model_source.filter(|_| replay_inputs.is_none()) {
        let resident = ResidentPolicyProcess::python_worker(python, worker, manifest)
            .map_err(|error| contextual("model worker startup", error))?;
        if resident.model_manifest_sha256() != expected_sha {
            return Err(invalid(
                "model manifest changed between route preflight and authenticated worker startup",
            ).into());
        }
        model.resident = Some(resident);
        if let Some(directory) = trace_output {
            model.recorder = Some(PolicyActionRecorder::create(&directory.join("policy-actions.bin"))?);
        }
    }
    let mut sink = DurableDiagnosticNotifications::create_new(
        &notifications, config.run_id.clone(),
    )?;
    if let Some(path) = policy_capture.as_ref() {
        sink.capture_policy_to(path)?;
    }
    let mut host = assets.build_imported_policy_runtime(
        launches, model, connection, sink,
    ).map_err(|error| contextual("native Level construction", error))?;
    for (index, level) in config.levels.iter().enumerate() {
        bind_evaluation_episode(
            &mut host, index, level, level.episode_seed(0), &source_digests[index],
            &gameplay_digest, 0, config.decision_period,
        )?;
    }
    host.set_timing_sample_period(timing);
    #[cfg(feature = "client")]
    let recorded_inputs = trace_output.map(|directory| directory.join("player-inputs.jsonl"));
    #[cfg(feature = "client")]
    let mut input_trace = if let Some(path) = &recorded_inputs {
        let mut trace = fs::OpenOptions::new().write(true).create_new(true)
            .open(path)?;
        writeln!(trace, "{}", json!({
            "kind": "player_input_trace",
            "schema_version": 4,
            "run_id": config.run_id,
            "run_config_sha256": config_digest,
            "gameplay_config_sha256": gameplay_digest,
            "policy_manifest_sha256": host.model_mut().model_manifest_sha256(),
            "levels": trace_levels,
        }))?;
        Some(trace)
    } else { None };
    let mut network = config.hivemind.as_ref().map(|settings|
        RunNetwork::start(settings, &config.levels, &source_fixed_dt_by_level, host.connection_mut())).transpose()?;
    println!("{}", json!({
        "kind": "run_started",
        "run_id": config.run_id,
        "stage_name": config.stage_name,
        "worlds": config.levels.len(),
        "group_worlds": config.levels.iter()
            .filter(|level| level.groups.is_some()).count(),
        "authenticated_group_agents": config.levels.iter()
            .filter_map(|level| level.groups.as_ref())
            .map(|group| group.participants.len()).sum::<usize>(),
        "episodes_per_world": config.episodes_per_world,
        "scheduled_model_updates": config.model_updates.len(),
        "policy_generation": host.model_mut().model_generation(),
        "policy_manifest_sha256": host.model_mut().model_manifest_sha256(),
        "decision_period": config.decision_period,
        "delivery_every": config.delivery_every,
        "timing_sample_period": config.timing_sample_period,
        "interactive_view": view,
        "source_hivemind_commands": config.hivemind.as_ref()
            .is_some_and(|settings| settings.issue_source_commands),
        "fixed_dt_by_world": config.levels.iter()
            .zip(source_fixed_dt_by_level.iter())
            .map(|(level, &dt)| (level.world_id, dt))
            .collect::<Vec<_>>(),
    }));

    let execution_start = Instant::now();
    let status_every_rounds = config.status_every_rounds.unwrap_or(u64::from(window.get()));
    let mut successful_world_steps = 0_u64;
    let mut successful_policy_rows = 0_u64;
    let mut rounds = 0_u64;
    let mut failed_window = None;
    let mut segment_start_round = 0_u64;
    let mut segment_baseline = host.sink_mut().measurements().clone();
    let mut segment_generation = host.model_mut().model_generation();
    let mut segment_manifest_sha256 = host.model_mut().model_manifest_sha256();
    let mut completed_episodes = vec![0_u32; config.levels.len()];
    let mut next_status_round = status_every_rounds;
    #[cfg(feature = "client")]
    let mut user_closed = false;
    #[cfg(not(feature = "client"))]
    let user_closed = false;
    // A world may finish before other arenas and remain paused at a
    // model boundary. Count each physical episode revision exactly once.
    let mut counted_revisions = vec![None::<u64>; config.levels.len()];
    let mut next_model_update = 0usize;
    // Physical signatures are trace-only. Sample at 64-step intervals,
    // at the start of the next input boundary *after* any model reset,
    // never per physics step or on every idle graphical frame.
    #[cfg(feature = "client")]
    let mut last_physical_checkpoint_round: Option<u64> = None;
    #[cfg(feature = "client")]
    let mut recorded_physical_checkpoints = 0_u64;
    let mut verified_physical_checkpoints = 0_u64;
    let mut verified_gameplay_checkpoints = 0_u64;
    'battle: loop {
        #[cfg(feature = "client")]
        if let Some(trace) = &mut input_trace {
            if (rounds == 0 || rounds % 64 == 0) &&
                last_physical_checkpoint_round != Some(rounds)
            {
                for index in 0..host.simulation().world_count() {
                    if let Err(error) = writeln!(trace, "{}",
                        replay_world_checkpoint(&host, index, rounds)) {
                        failed_window = Some(format!("recording native physical checkpoint: {error}"));
                        break 'battle;
                    }
                    recorded_physical_checkpoints += 1;
                }
                last_physical_checkpoint_round = Some(rounds);
            }
        }
        if let Some(trace) = &mut playback {
            if let Err(error) = trace.load_round(rounds) {
                failed_window = Some(format!(
                    "reading native player trace at round {rounds}: {error}"
                ));
                break 'battle;
            }
            while trace.checkpoints.front().is_some_and(|entry| entry.round == rounds) {
                let entry = trace.checkpoints.pop_front()
                    .expect("checked next physical checkpoint");
                let Some(world) = (0..host.simulation().world_count())
                    .filter_map(|index| host.simulation().world(index))
                    .find(|world| world.id == entry.world_id) else {
                        failed_window = Some(format!(
                            "physical replay checkpoint world {} missing at round {rounds}",
                            entry.world_id.0,
                        ));
                        break 'battle;
                    };
                if world.episode_revision() != entry.episode_revision ||
                    world.tick() != entry.tick ||
                    physical_world_sha256(
                        world.id, world.episode_revision(), world.tick(),
                        &world.state.combat,
                    ) != entry.sha256
                {
                    failed_window = Some(format!(
                        "native physical state diverged at round {rounds}, world {}, episode {}, tick {}",
                        entry.world_id.0, entry.episode_revision, entry.tick,
                    ));
                    break 'battle;
                }
                verified_physical_checkpoints += 1;
                if let Some(expected) = &entry.gameplay_sha256 {
                    let actual = gameplay_world_sha256(
                        world.id, world.episode_revision(), world.tick(),
                        &world.state.combat, world.state.events.roster(),
                        world.state.events.commands(), &world.state.metadata,
                    );
                    if actual != *expected {
                        failed_window = Some(format!(
                            "native gameplay control diverged at round {rounds}, world {}, episode {}, tick {}",
                            entry.world_id.0, entry.episode_revision, entry.tick,
                        ));
                        break 'battle;
                    }
                    verified_gameplay_checkpoints += 1;
                }
            }
            if trace.checkpoints.front().is_some_and(|entry| entry.round < rounds) {
                failed_window = Some(format!(
                    "physical replay checkpoint was skipped before round {rounds}",
                ));
                break 'battle;
            }
            while trace.inputs.front().is_some_and(|entry| entry.round == rounds) {
                let entry = trace.inputs.pop_front().expect("checked next input");
                let (id, episode_revision) = entry.action.episode();
                let matching_world = (0..host.simulation().world_count()).any(|index| {
                    host.simulation().world(index).is_some_and(|world| world.id == id &&
                        world.episode_revision() == episode_revision && world.tick() == entry.tick)
                });
                if !matching_world {
                    failed_window = Some(format!("player trace diverged at round {rounds}, world {}", id.0));
                    break 'battle;
                }
                if let Err(error) = apply_player_input(&mut host, entry.action) {
                    failed_window = Some(error);
                    break 'battle;
                }
            }
            if rounds == trace.end_round {
                if !trace.checkpoints.is_empty() {
                    failed_window = Some("unconsumed physical replay checkpoints at final round".to_owned());
                }
                break;
            }
        }
        if rounds == config.max_rounds { break; }
        // Poll before view pacing/input continues, so idle graphical frames
        // cannot starve acknowledgement or setup delivery.
        if let Some(network) = &mut network {
            if let Err(error) = network.poll(&mut host) {
                failed_window = Some(error.to_string());
                break;
            }
        }
        #[cfg(feature = "client")]
        if let Some(client) = &mut client {
            let Some(current) = host.render_world(client.selected_world()) else {
                failed_window = Some("selected Level disappeared".to_owned());
                break;
            };
            let tick = current.tick;
            let action = client.poll(current, client_bounds[client.selected_world()],
                host.simulation().world_count(), client_fixed_dt_by_level[client.selected_world()]);
            if let (Some(trace), Ok(bees_host::client::NativeClientAction::Input { action: input, .. })) = (&mut input_trace, &action) {
                if let Err(error) = writeln!(trace, "{}", json!({
                        "kind": "player_input",
                        "round": rounds,
                        "tick": tick,
                        "action": input,
                })) {
                    failed_window = Some(format!("recording player input: {error}"));
                    break;
                }
            }
            match action {
                Ok(bees_host::client::NativeClientAction::Wait |
                    bees_host::client::NativeClientAction::WorldChanged) => continue,
                Ok(bees_host::client::NativeClientAction::Step) => {},
                Ok(bees_host::client::NativeClientAction::Input { action: input, step }) => {
                    match apply_player_input(&mut host, input) {
                        Ok(feedback) => client.report_move(feedback),
                        Err(error) => {
                            failed_window = Some(error);
                            break;
                        },
                    }
                    if !step { continue; }
                },
                Ok(bees_host::client::NativeClientAction::Quit) => { user_closed = true; break; },
                Err(error) => { failed_window = Some(format!("native window: {error}")); break; },
            }
        }
        let until_update = config.model_updates.get(next_model_update)
            .filter(|item| item.at_round > rounds)
            .map_or(config.max_rounds, |item| item.at_round);
        // Treat at_round as the earliest eligibility point. Completing
        // the current episodes before reloading prevents mixed-model
        // evaluation outcomes, even if episodes have different lengths.
        let remaining = (config.max_rounds - rounds)
            .min(until_update - rounds);
        let source_pulse_limit = if let Some(network) = &mut network {
            let scheduling = network.issue_due_source_commands(&mut host)
                .and_then(|()| network.rounds_until_next_source_pulse(&host));
            match scheduling {
                Ok(limit) => limit,
                Err(error) => {
                    failed_window = Some(error.to_string());
                    break;
                }
            }
        } else { u64::MAX };
        let count = nonzero(remaining.min(source_pulse_limit)
            .min(u64::from(if view || playback.is_some() { 1 } else { window.get() })) as u32, "window")?;
        let counts = match host.run_window(count, delivery) {
            Ok(counts) => counts,
            Err(error) => {
                // The partial world/trajectory is never counted as a
                // successful window. Retain the cause, then use the same
                // authored cancellation path as a finite-budget stop.
                failed_window = Some(format!("{error:?}"));
                break;
            }
        };
        rounds += u64::from(counts.rounds);
        successful_world_steps += counts.world_steps as u64;
        successful_policy_rows += counts.policy_rows as u64;
        // Pause completed worlds while the slowest active arena reaches
        // the same model-change boundary. A physical episode must never
        // contribute a partially old/partially new benchmark result.
        let completion = (|| -> io::Result<bool> {
            let mut all_finished = true;
            for index in 0..config.levels.len() {
                let world = host.simulation().world(index)
                    .ok_or_else(|| invalid("registered Level disappeared"))?;
                if world.state.events.was_aborted() || world.is_failed() ||
                    world.state.events.is_failed()
                {
                    return Err(invalid("failed or aborted world cannot satisfy a model boundary"));
                }
                if !world.state.events.completed() {
                    all_finished = false;
                    continue;
                }
                let revision = world.episode_revision();
                if counted_revisions[index] != Some(revision) {
                    if completed_episodes[index] >= config.episodes_per_world {
                        return Err(invalid("world exceeded its configured episode budget"));
                    }
                    if policy_capture.is_some() {
                        let (id, tick) = (world.id, world.tick());
                        let observations = host.terminal_observations(index)
                            .map_err(|error| contextual("terminal policy observations", error))?;
                        host.sink_mut().capture_terminal_policy(id, revision, tick, &observations)?;
                    }
                    completed_episodes[index] += 1;
                    counted_revisions[index] = Some(revision);
                }
            }
            Ok(all_finished)
        })();
        let all_finished = match completion {
            Ok(value) => value,
            Err(error) => {
                failed_window = Some(format!("episode accounting: {error}"));
                break;
            }
        };
        let update_due = config.model_updates.get(next_model_update)
            .is_some_and(|item| rounds >= item.at_round);
        if update_due && all_finished {
            if rounds == config.max_rounds {
                failed_window = Some(
                    "scheduled model change reached episode boundary only at the exhausted run budget".into(),
                );
                break;
            }
            if completed_episodes.iter().all(|&finished|
                finished >= config.episodes_per_world
            ) {
                failed_window = Some(format!(
                    "model update requested at round {rounds} but no future episodes remain",
                ));
                break;
            }
            // An overrun may have crossed another scheduled threshold
            // while slower worlds finished. Never claim an intermediate
            // checkpoint was meaningfully evaluated when it ran zero
            // simulated rounds.
            if config.model_updates.get(next_model_update + 1)
                .is_some_and(|item| rounds >= item.at_round)
            {
                failed_window = Some(format!(
                    "model schedule thresholds crossed before an episode boundary at round {rounds}",
                ));
                break;
            }
            let update = &config.model_updates[next_model_update];
            let previous_generation = host.model_mut().model_generation();
            let evaluation_before_update =
                host.sink_mut().measurements().clone();
            let completed_segment = match evaluation_before_update
                .delta_since(&segment_baseline)
            {
                Some(segment) => segment,
                None => {
                    failed_window = Some("durable evaluation counters regressed".into());
                    break;
                }
            };
            match host.model_mut().reload_manifest(
                &resolve(root, &update.manifest),
            ) {
                Ok(generation) => {
                    let manifest_sha256 =
                        host.model_mut().model_manifest_sha256();
                    let segment_mean =
                        completed_segment.mean_episode_seconds();
                    println!("{}", json!({
                        "kind": "policy_segment",
                        "run_id": config.run_id,
                        "policy_generation": segment_generation,
                        "policy_manifest_sha256": segment_manifest_sha256,
                        "from_round": segment_start_round,
                        "through_round": rounds,
                        "evaluation": completed_segment,
                        "mean_episode_seconds": segment_mean,
                    }));
                    println!("{}", json!({
                        "kind": "policy_updated",
                        "run_id": config.run_id,
                        "requested_round": update.at_round,
                        "rounds": rounds,
                        "previous_policy_generation": previous_generation,
                        "policy_generation": generation,
                        "policy_manifest_sha256": manifest_sha256,
                        "evaluation_before_update": evaluation_before_update,
                        "manifest": update.manifest,
                    }));
                    segment_baseline = evaluation_before_update;
                    segment_start_round = rounds;
                    segment_generation = generation;
                    segment_manifest_sha256 = manifest_sha256;
                    next_model_update += 1;
                }
                Err(error) => {
                    failed_window = Some(format!(
                        "scheduled policy update at episode boundary round {rounds}: {error:?}"
                    ));
                    break;
                }
            }
        }
        let holding_for_update = update_due && !all_finished;
        // The reset is independently guarded by the normal episode
        // delivery barrier. No completed world may be reset while its
        // peers are still finishing a checkpoint transition.
        let recycling = (|| -> io::Result<usize> {
            let mut recycled = 0usize;
            for (index, level) in config.levels.iter().enumerate() {
                let finished = host.simulation().world(index)
                    .ok_or_else(|| invalid("registered Level disappeared"))?
                    .state.events.completed();
                if !finished ||
                    holding_for_update ||
                    completed_episodes[index] >= config.episodes_per_world ||
                    rounds == config.max_rounds
                {
                    continue;
                }
                let mut scene = NativePhysicalScene::parse_with_ship_catalog(&source_jsons[index], assets.ship_catalog())
                    .map_err(|error| contextual("cached authored Level", error))?;
                // Replaying an authored template creates new real Stage
                // squad lifetimes, not aliases of retired pooled wrappers.
                let mut remapped = HashMap::new();
                for squad in &mut scene.source_squads {
                    let item_id = next_id.get();
                    let following = item_id.checked_add(1)
                        .ok_or_else(|| invalid("Stage squad identities exhausted"))?;
                    next_id.set(following);
                    remapped.insert(squad.item_id, item_id);
                    squad.item_id = item_id;
                }
                let seed = level.episode_seed(completed_episodes[index]);
                if let Some(group) = &level.groups {
                    bind_group_routes(
                        &mut scene, group, completed_episodes[index], false,
                        seed,
                    )?;
                }
                let setup = make_setup(
                    &config, level, &assets, period, Rc::clone(&next_id), seed,
                    completed_episodes[index], source_sides[index], &remapped,
                )?;
                let authored = scene.into_exported_policy_only_level(setup)
                    .map_err(|error| contextual("replayed Level source", error))?;
                let replacement = assets.build_level(authored)
                    .map_err(|error| contextual("replayed Level assembly", error))?
                    .state;
                host.reset_world(index, seed, replacement)
                    .map_err(|error| contextual("guarded Level reset", error))?;
                bind_evaluation_episode(
                    &mut host, index, level, seed, &source_digests[index],
                    &gameplay_digest, completed_episodes[index], config.decision_period,
                )?;
                recycled += 1;
            }
            Ok(recycled)
        })();
        let recycled = match recycling {
            Ok(recycled) => recycled,
            Err(error) => {
                failed_window = Some(format!("episode turnover: {error}"));
                break;
            }
        };
        if rounds >= next_status_round || counts.stopped_with_no_live_worlds {
        next_status_round = rounds.saturating_add(status_every_rounds);
        let evaluation = host.sink_mut().measurements().clone();
        let mean_episode_seconds = evaluation.mean_episode_seconds();
        match host.sample_status() {
            Ok(Some(status)) => println!("{}", json!({
                "kind": "interval",
                "run_id": config.run_id,
                "rounds": rounds,
                "policy_generation": host.model_mut().model_generation(),
                "policy_manifest_sha256": host.model_mut().model_manifest_sha256(),
                "model_updates_applied": next_model_update,
                "next_model_update_at_round": config.model_updates
                    .get(next_model_update).map(|item| item.at_round),
                "awaiting_clean_episode_boundary": holding_for_update,
                "completed_episodes": completed_episodes.iter()
                    .map(|&count| u64::from(count)).sum::<u64>(),
                "evaluation": evaluation,
                "mean_episode_seconds": mean_episode_seconds,
                "metrics": status,
            })),
            Ok(None) => {},
            Err(error) => {
                failed_window = Some(format!(
                    "native host status after window: {error:?}"
                ));
                break;
            }
        }
        }
        if counts.stopped_with_no_live_worlds && holding_for_update {
            failed_window = Some(
                "model boundary has no active worlds but still lacks a completed episode".into(),
            );
            break;
        }
        if counts.stopped_with_no_live_worlds && recycled == 0 { break; }
    }

    if failed_window.is_none() && !user_closed && playback.as_ref().is_some_and(|trace|
        rounds != trace.end_round || !trace.inputs.is_empty() ||
        !trace.checkpoints.is_empty()
    ) {
        failed_window = Some(format!("player trace ended at a different round than the battle ({rounds})"));
    }

    if failed_window.is_none() && next_model_update < config.model_updates.len() {
        failed_window = Some(format!(
            "{} scheduled policy updates were not reached before the run ended",
            config.model_updates.len() - next_model_update,
        ));
    }

    // A finite run budget is an intentional stop, NEVER a fake terminal
    // win or a completed PPO episode. Durable EpisodeAborted notifications
    // explicitly mark partial trajectories as unusable for learning.
    let count = host.simulation().world_count();
    // Fingerprint each Level once at the original execution boundary,
    // BEFORE finite-budget cancellation/abort notifications can alter
    // runtime state. This is opt-in summary output, not simulation work.
    // The exact same bitwise physical-world signature is used by replay.
    let final_gameplay_worlds = summary.as_ref().map(|_| {
        (0..count).filter_map(|index| {
            let world = host.simulation().world(index)?;
            Some(json!({
                "world_id": world.id.0,
                "episode_revision": world.episode_revision(),
                "tick": world.tick(),
                "sha256": gameplay_world_sha256(
                    world.id, world.episode_revision(), world.tick(),
                    &world.state.combat, world.state.events.roster(),
                    world.state.events.commands(), &world.state.metadata,
                ),
            }))
        }).collect::<Vec<_>>()
    });
    let final_physical_worlds = summary.as_ref().map(|_| {
        (0..count).filter_map(|index| {
            let world = host.simulation().world(index)?;
            Some(json!({
                "world_id": world.id.0,
                "episode_revision": world.episode_revision(),
                "tick": world.tick(),
                "sha256": physical_world_sha256(
                    world.id, world.episode_revision(), world.tick(),
                    &world.state.combat,
                ),
            }))
        }).collect::<Vec<_>>()
    });
    // A trace must cover the physical state of EVERY Level at its final
    // execution boundary, even when the game quits between the sampled
    // 64-round checkpoints. No gameplay owner is modified here.
    // Match the v4 reader contract before finite-budget cancellation.
    #[cfg(feature = "client")]
    if let Some(trace) = &mut input_trace {
        if last_physical_checkpoint_round != Some(rounds) {
            for index in 0..count {
                if let Err(error) = writeln!(trace, "{}",
                    replay_world_checkpoint(&host, index, rounds)) {
                    failed_window.get_or_insert_with(||
                        format!("recording final native physical checkpoint: {error}")
                    );
                    break;
                }
                recorded_physical_checkpoints += 1;
            }
            last_physical_checkpoint_round = Some(rounds);
        }
    }
    let finalization_start = Instant::now();
    let mut cleanup_errors = Vec::new();
    if failed_window.is_none() && !user_closed {
        if let Some((actions, _)) = &mut host.model_mut().playback {
            if let Err(error) = actions.finish() {
                failed_window = Some(format!("policy action playback: {error}"));
            }
        }
    }
    #[cfg(feature = "client")]
    if let Some(mut trace) = input_trace {
        let policy_actions_sha256 = host.model_mut().recorder.take().and_then(|recorder| {
            match recorder.finish() {
                Ok(digest) => Some(digest),
                Err(error) => {
                    let message = format!("policy action trace: {error}");
                    failed_window.get_or_insert_with(|| message.clone());
                    cleanup_errors.push(message);
                    None
                }
            }
        });
        let finished = writeln!(trace, "{}", json!({
            "kind": "player_input_trace_end",
            "rounds": rounds,
            "user_closed": user_closed,
            "failed_window": failed_window,
            "policy_actions_sha256": policy_actions_sha256,
        })).and_then(|()| trace.sync_all());
        if let Err(error) = finished {
            cleanup_errors.push(format!("player input trace: {error}"));
        }
    }
    if let Some(path) = &capture {
        // Captures are diagnostic output, never a replacement world or model
        // input. Failure joins cleanup reporting so live Levels still retire.
        let captured = (|| -> io::Result<()> {
            #[cfg(feature = "client")]
            let index = client.as_ref().map_or(0, |client| client.selected_world());
            #[cfg(not(feature = "client"))]
            let index = 0;
            let current = host.render_world(index)
                .ok_or_else(|| invalid("capture Level disappeared"))?;
            let mut renderer = NativeSoftwareRenderer::new(1100, 760, client_bounds[index])?;
            let counts = renderer.render(&current, client_bounds[index]);
            let mut file = fs::OpenOptions::new().write(true).create_new(true).open(path)?;
            renderer.write_ppm(&mut file)?;
            file.sync_all()?;
            println!("{}", json!({ "kind": "frame_capture", "run_id": config.run_id,
                "world_id": current.world_id.0, "episode_revision": current.episode_revision,
                "tick": current.tick, "path": path, "entities": counts }));
            Ok(())
        })();
        if let Err(error) = captured { cleanup_errors.push(format!("frame capture: {error}")); }
    }
    for index in 0..count {
        let Some(world) = host.simulation().world(index) else {
            cleanup_errors.push(format!("world {index}: disappeared"));
            continue;
        };
        if world.state.events.was_aborted() ||
            world.state.events.completed() {
            continue;
        }
        let kind = if world.is_failed() || world.state.events.is_failed() {
            NativeLevelStopKind::AbortFailed
        } else {
            NativeLevelStopKind::CancelLive
        };
        if let Err(error) = host.stop_world(index, kind) {
            cleanup_errors.push(format!(
                "world {index} final stop/delivery: {error:?}"
            ));
        }
    }
    if let Err(error) = host.sink_mut().sync() {
        cleanup_errors.push(format!("final notification sync: {error}"));
    }
    let mut socket_flush_timed_out = false;
    let mut socket_close_confirmed = false;
    if let Some(network) = &mut network {
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(u64::from(network.flush_seconds));
        while failed_window.is_none() && cleanup_errors.is_empty() {
            let connection = host.connection_mut();
            if network.next_level == network.levels.len() &&
                connection.pending_store_hashes()
                    .map_err(|error| contextual("final pending outcomes", error))?.is_empty() &&
                connection.connection().pending_request_count() == 0
            {
                break;
            }
            if std::time::Instant::now() >= deadline { socket_flush_timed_out = true; break; }
            if let Err(error) = network.poll(&mut host) {
                cleanup_errors.push(error.to_string());
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(1));
        }
        // A bounded wait ending is not an acknowledgement. All remaining
        // original hashes stay in the checkpoint for a later connection.
        if failed_window.is_none() && cleanup_errors.is_empty() && !socket_flush_timed_out {
            match network.worker.request_close() {
                Ok(()) => {
                    while !network.worker.closed() && std::time::Instant::now() < deadline {
                        if let Err(error) = network.poll(&mut host) {
                            cleanup_errors.push(format!("HiveMind socket close: {error}"));
                            break;
                        }
                        if !network.worker.closed() {
                            std::thread::sleep(std::time::Duration::from_millis(1));
                        }
                    }
                    socket_close_confirmed = network.worker.closed();
                }
                Err(error) => cleanup_errors.push(format!("HiveMind socket close request: {error:?}")),
            }
        }
    }
    let status = host.runtime_status()
        .map_err(|error| contextual("final native host status", error))?;
    let evaluation = host.sink_mut().measurements().clone();
    let mean_episode_seconds = evaluation.mean_episode_seconds();
    let final_segment = evaluation.delta_since(&segment_baseline);
    let final_segment_mean = final_segment.as_ref()
        .and_then(|segment| segment.mean_episode_seconds());
    if final_segment.is_none() && failed_window.is_none() {
        failed_window = Some("durable evaluation counters regressed".into());
    }
    let mut captured_policy_rows = None;
    if failed_window.is_none() && cleanup_errors.is_empty()
        && completed_episodes.iter().all(|&finished| finished == config.episodes_per_world)
    {
        match host.sink_mut().finish_policy_capture(successful_policy_rows) {
            Ok(rows) => captured_policy_rows = rows,
            Err(error) => cleanup_errors.push(format!("policy capture finalization: {error}")),
        }
    }
    let policy_generation = host.model_mut().model_generation();
    let policy_manifest_sha256 = host.model_mut().model_manifest_sha256();
    #[cfg(feature = "client")]
    let captured_checkpoints = recorded_physical_checkpoints;
    #[cfg(not(feature = "client"))]
    let captured_checkpoints = 0_u64;
    drop(host.model_mut().resident.take());
    let run_end = Instant::now();
    let wall_seconds = run_end.duration_since(run_start).as_secs_f64();
    let execution_wall_seconds = finalization_start.duration_since(execution_start).as_secs_f64();
    let rate = |count: u64, seconds: f64| {
        (seconds > 0.0).then(|| count as f64 / seconds)
    };
    let campaign_outcome = mission.filter(|_| replay_inputs.is_none() && !user_closed &&
        failed_window.is_none() && cleanup_errors.is_empty() && completed_episodes == [1])
        .and_then(|mission| {
            let result = host.simulation().world(0)?.state.events.latest_finalized_episode()?.result;
            Some(CampaignOutcome {
                campaign_id: mission.campaign_id.to_owned(), mission_id: mission.mission_id.to_owned(),
                player_side: mission.player_side, winner_side: result.winner_side,
            })
        });
    let final_summary = json!({
        "kind": "run_final",
        "run_id": config.run_id,
        "rounds": rounds,
        "throughput": {
            "wall_seconds": wall_seconds,
            "startup_wall_seconds": execution_start.duration_since(run_start).as_secs_f64(),
            "execution_wall_seconds": execution_wall_seconds,
            "finalization_wall_seconds": run_end.duration_since(finalization_start).as_secs_f64(),
            "successful_world_steps": successful_world_steps,
            "successful_policy_rows": successful_policy_rows,
            "world_steps_per_wall_second": rate(successful_world_steps, wall_seconds),
            "policy_rows_per_wall_second": rate(successful_policy_rows, wall_seconds),
            "world_steps_per_execution_second": rate(successful_world_steps, execution_wall_seconds),
            "policy_rows_per_execution_second": rate(successful_policy_rows, execution_wall_seconds),
        },
        "sampled_model_timing": (status.inference.model_forward_timed_calls != 0).then(|| json!({
            "calls": status.inference.model_forward_timed_calls,
            "rows": status.inference.model_forward_timed_rows,
            "wall_seconds": status.inference.model_forward_nanos as f64 / 1e9,
        })),
        "policy_generation": policy_generation,
        "captured_policy_rows": captured_policy_rows,
        "policy_manifest_sha256": policy_manifest_sha256,
        "completed_episodes": completed_episodes.iter()
            .map(|&count| u64::from(count)).sum::<u64>(),
        "completed_worlds": status.completed_worlds,
        // The world-owned counters reset on episode turnover; the simulation
        // records completed fixed-step work separately across all episodes.
        "vision_current_episodes": status.vision,
        "vision_completed_step_totals": status.simulation.vision,
        "campaign_outcome": campaign_outcome,
        "aborted_worlds": status.aborted_worlds,
        "failed_worlds": status.failed_worlds,
        "running_worlds": status.running_worlds,
        "pending_notifications": status.queued_notifications,
        "pending_store_commands": status.pending_store_commands,
        "pending_store_reconciliations": status.pending_store_reconciliations,
        "socket_flush_timed_out": socket_flush_timed_out,
        "pending_hivemind_requests": status.pending_hivemind_requests,
        "socket_close_confirmed": socket_close_confirmed,
        "network": network.as_ref().map(|network| json!({
            "measurements": network.worker.measurements(),
            "queue_backpressure": network.worker.queue_backpressure(),
            "setups_remaining": network.levels.len() - network.next_level,
            "source_commands": {
                "eligible": network.scheduler.measurements().eligible,
                "issued": network.scheduler.measurements().issued,
                "out_of_bounds": network.scheduler.measurements().out_of_bounds,
                "home_moves": network.scheduler.measurements().home_moves,
                "home_unreachable": network.scheduler.measurements().home_unreachable,
                "unauthored_minions": network.scheduler.measurements().unauthored_minions,
            },
            "source_command_timer": network.scheduler.schedule_measurements(),
        })),
        "model_updates_applied": next_model_update,
        "model_updates_remaining": config.model_updates.len() - next_model_update,
        "evaluation": evaluation,
        "mean_episode_seconds": mean_episode_seconds,
        "active_policy_segment": {
            "policy_generation": segment_generation,
            "policy_manifest_sha256": segment_manifest_sha256,
            "from_round": segment_start_round,
            "through_round": rounds,
            "evaluation": final_segment,
            "mean_episode_seconds": final_segment_mean,
        },
        "failed_window": failed_window,
        "cleanup_errors": cleanup_errors,
        "user_closed": user_closed,
        "player_input_playback": playback.is_some(),
        "player_input_playback_complete": playback.as_ref().map(|trace|
            rounds == trace.end_round && trace.inputs.is_empty() &&
            trace.checkpoints.is_empty() && failed_window.is_none()),
        "recorded_physical_checkpoints": captured_checkpoints,
        "verified_physical_checkpoints": verified_physical_checkpoints,
        "verified_gameplay_checkpoints": verified_gameplay_checkpoints,
        "player_trace_stream": playback.as_ref().map(|trace| trace.measurements()),
        "final_physical_worlds": final_physical_worlds,
        "final_gameplay_worlds": final_gameplay_worlds,
    });
    println!("{final_summary}");
    #[cfg(feature = "client")]
    if let Some(client) = &client {
        println!("{}", json!({ "kind": "presentation_final", "run_id": config.run_id,
            "rendered_frames": client.rendered_frames,
            "rendered_pixels": client.rendered_pixels,
            "render_wall_seconds": client.render_wall_seconds,
            "ui_wall_seconds": client.ui_wall_seconds,
            "window_update_wall_seconds": client.window_update_wall_seconds,
            "viewport_resizes": client.viewport_resizes,
            "minimap_markers_drawn": client.minimap_markers_drawn,
            "numbered_squad_rows_drawn": client.numbered_squad_rows_drawn,
            "striker_bomb_indicators_drawn": client.striker_bomb_indicators_drawn,
            "minimap_camera_moves": client.minimap_camera_moves,
            "camera_pan_frames": client.camera_pan_frames,
            "camera_wheel_pans": client.camera_wheel_pans,
            "camera_boundary_clamps": client.camera_boundary_clamps,
            "camera_zoom_clamps": client.camera_zoom_clamps,
            "minimap_move_orders_submitted": client.minimap_move_orders_submitted,
            "tactical_button_clicks": client.tactical_button_clicks,
            "squad_tab_clicks": client.squad_tab_clicks,
            "shooting_priority_requests_submitted": client.shooting_priority_requests_submitted,
            "selected_command_rows_drawn": client.selected_command_rows_drawn,
            "command_target_markers_drawn": client.command_target_markers_drawn,
            "numbered_shortcut_selections": client.numbered_shortcut_selections,
            "cycled_shortcut_selections": client.cycled_shortcut_selections,
            "final_viewport_pixels": client.viewport_size().0 * client.viewport_size().1,
            "culled_ship_draws": client.culled_ship_draws,
            "culled_obstacle_draws": client.culled_obstacle_draws,
            "culled_projectile_draws": client.culled_projectile_draws,
            "culled_piece_draws": client.culled_piece_draws,
            "weapon_geometry_queries": client.weapon_geometry_queries,
            "weapon_range_candidates_culled": client.weapon_range_candidates_culled }));
    }
    if failed_window.is_some() || !cleanup_errors.is_empty() {
        return Err(invalid(
            "headless rollout failed; run_final identifies the original failure and unresolved obligations",
        ).into());
    }
    if let Some(summary_path) = summary {
        let file_path = if campaign_outcome.is_some() { summary_path.with_extension("tmp") }
            else { summary_path.clone() };
        let file = fs::OpenOptions::new().write(true).create_new(true)
            .open(&file_path)?;
        serde_json::to_writer(&file, &final_summary)?;
        file.sync_all()?;
        drop(file);
        if file_path != summary_path {
            fs::rename(file_path, &summary_path)?;
            #[cfg(unix)]
            fs::File::open(summary_path.parent().ok_or_else(|| invalid("missing summary directory"))?)?.sync_all()?;
        }
    }
    if play && !user_closed && (playback.is_some() ||
        completed_episodes.iter().all(|&count| count == config.episodes_per_world)) {
        #[cfg(feature = "client")]
        if let Some(client) = &mut client {
            use bees_host::client::{NativeResultAction, NativeResultControls};
            let controls = match (replay_inputs, recorded_inputs.as_ref(), output) {
                (Some(_), _, _) => NativeResultControls::WatchAgain,
                (None, Some(_), _) => NativeResultControls::PlayAgainOrWatch,
                (None, None, Some(_)) => NativeResultControls::PlayAgain,
                (None, None, None) => NativeResultControls::Close,
            };
            client.show_result(controls);
            loop {
                let index = client.selected_world();
                let current = host.render_world(index)
                    .ok_or_else(|| invalid("completed Level disappeared"))?;
                if matches!(client.poll(current, client_bounds[index], host.simulation().world_count(),
                    client_fixed_dt_by_level[index])?, bees_host::client::NativeClientAction::Quit) {
                    break;
                }
                if let Some(action) = client.result_action() {
                    let replay_inputs = match action {
                        NativeResultAction::PlayAgain => None,
                        NativeResultAction::WatchRecording => replay_inputs.map(Path::to_path_buf).or(recorded_inputs),
                    };
                    return Ok(Some(BattleRestart { replay_inputs }));
                }
            }
        }
    }
    Ok(None)
}
