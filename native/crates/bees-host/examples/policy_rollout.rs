//! Run exported, authored Unity policy Levels headlessly through the same
//! Rust CombatWorld, v26 observation encoder and resident model used by
//! the future rendered native client. No synthetic agents or actions.
//!
//! Usage:
//!   cargo run --release -p bees-host --example policy_rollout -- run.json
//!
//! This is a bounded gameplay/inference runner and diagnostic transport,
//! NOT a PPO optimizer, distributed worker or Unity physics-parity claim.

use std::cell::Cell;
use std::collections::{HashMap, HashSet};
use std::env;
use std::error::Error;
use std::fs::File;
use std::io::{self, ErrorKind};
use std::num::NonZeroU32;
use std::path::{Path, PathBuf};
use std::rc::Rc;

use bees_gameplay::level_host::NativeLevelStopKind;
use bees_gameplay::squads::SquadRosterError;
use bees_hivemind::session::SquadLifetime;
use bees_host::durable_connection::DurableHiveMindConnection;
use bees_host::notification_file::DurableDiagnosticNotifications;
use bees_host::policy_process::ResidentPolicyProcess;
use bees_host::scene_assets::NativeSceneAssets;
use bees_host::unity_scene_snapshot::{
    NativeExportedPolicyLevelSetup, NativePhysicalScene,
};
use bees_sim::asteroid_fragments::FragmentSpawnSettings;
use bees_sim::collision::Aabb;
use bees_sim::geometry::Vec2;
use bees_sim::simulation::WorldId;
use serde::Deserialize;
use serde_json::{json, Value};

type MinionAllocator = Box<
    dyn FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
>;

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct BoundsConfig {
    min: [f32; 2],
    max: [f32; 2],
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FragmentConfig {
    map_bounds: BoundsConfig,
    half_map_extents: [f32; 2],
    asteroid_max_speed_exclusive: u32,
    training_health_ratio: Option<f32>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct LevelConfig {
    scene: PathBuf,
    world_id: u64,
    level_id: i64,
    seed: u64,
    model_slots: HashMap<u8, u32>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct RunConfig {
    run_id: String,
    stage_name: String,
    collision_matrix: PathBuf,
    stage_palette: PathBuf,
    fragment_spawn: FragmentConfig,
    python: PathBuf,
    policy_worker: PathBuf,
    model_manifest: PathBuf,
    connection_checkpoint: PathBuf,
    notifications: PathBuf,
    next_squad_item_id: i64,
    broadphase_cell_size: f32,
    decision_period: u32,
    max_rounds: u64,
    rounds_per_window: u32,
    delivery_every: u32,
    timing_sample_period: Option<u32>,
    levels: Vec<LevelConfig>,
}

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

fn main() -> Result<(), Box<dyn Error>> {
    let mut args = env::args_os().skip(1);
    let config_path = PathBuf::from(args.next().ok_or_else(|| {
        invalid("usage: policy_rollout run.json")
    })?);
    if args.next().is_some() {
        return Err(invalid("usage: policy_rollout run.json").into());
    }
    let config_path = config_path.canonicalize()?;
    let root = config_path.parent().ok_or_else(|| invalid("missing config directory"))?;
    let config: RunConfig = serde_json::from_reader(File::open(&config_path)?)?;

    let period = nonzero(config.decision_period, "decision_period")?;
    let window = nonzero(config.rounds_per_window, "rounds_per_window")?;
    let delivery = nonzero(config.delivery_every, "delivery_every")?;
    let timing = config.timing_sample_period
        .map(|value| nonzero(value, "timing_sample_period"))
        .transpose()?;
    if config.run_id.trim().is_empty() || config.stage_name.trim().is_empty() ||
        config.levels.is_empty() || config.max_rounds == 0 ||
        !config.broadphase_cell_size.is_finite() ||
        config.broadphase_cell_size <= 0.0 ||
        config.next_squad_item_id <= 0 ||
        config.next_squad_item_id == i64::MAX
    {
        return Err(invalid("invalid run identity, bounds, allocator or round budget").into());
    }
    let checkpoint = resolve(root, &config.connection_checkpoint);
    let notifications = resolve(root, &config.notifications);
    if checkpoint == notifications {
        return Err(invalid("checkpoint and notification log must be distinct").into());
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
    let manifest_path = resolve(root, &config.model_manifest);
    let manifest: Value = serde_json::from_reader(File::open(&manifest_path)?)?;
    let declared: HashSet<u32> = manifest.get("models")
        .and_then(Value::as_array)
        .ok_or_else(|| invalid("manifest has no models array"))?
        .iter()
        .map(|item| item.get("slot").and_then(Value::as_u64)
            .and_then(|slot| u32::try_from(slot).ok())
            .ok_or_else(|| invalid("manifest model slot is not uint32")))
        .collect::<Result<_, _>>()?;

    let mut level_ids = HashSet::new();
    let mut world_ids = HashSet::new();
    let mut highest_squad_item_id = 0_i64;
    let mut source_levels = Vec::with_capacity(config.levels.len());
    for level in &config.levels {
        if level.level_id <= 0 || level.world_id == 0 ||
            !level_ids.insert(level.level_id) ||
            !world_ids.insert(level.world_id) ||
            level.model_slots.is_empty() ||
            !level.model_slots.values().all(|slot| declared.contains(slot))
        {
            return Err(invalid("duplicate/invalid Level or missing real model slot").into());
        }
        let source = NativePhysicalScene::read(&resolve(root, &level.scene))
            .map_err(|error| contextual("exported Level", error))?;
        if source.stage_name != config.stage_name {
            return Err(invalid("exported Level belongs to a different Stage").into());
        }
        for squad in &source.source_squads {
            highest_squad_item_id = highest_squad_item_id.max(squad.item_id);
        }
        for binding in source.source_policy_bindings.values() {
            if !level.model_slots.contains_key(&binding.team_id) {
                return Err(invalid("Level has an unresolved source policy team").into());
            }
        }
        source_levels.push(source);
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
    let mut launches = Vec::with_capacity(source_levels.len());
    for (source, level) in source_levels.into_iter().zip(&config.levels) {
        let next_id = Rc::clone(&next_id);
        let allocate_minion: MinionAllocator = Box::new(move |mother, _beacon| {
            let item_id = next_id.get();
            let next = item_id.checked_add(1)
                .ok_or(SquadRosterError::InvalidOwner)?;
            next_id.set(next);
            Ok(SquadLifetime {
                level_id: mother.level_id,
                squad_id: item_id,
                lifecycle_id: item_id as u64,
            })
        });
        launches.push((source, NativeExportedPolicyLevelSetup {
            world_id: WorldId(level.world_id),
            level_id: level.level_id,
            seed: level.seed,
            broadphase_cell_size: config.broadphase_cell_size,
            decision_period: period,
            initial_full_asteroids: 0,
            groups: None,
            ship_catalog: assets.shared_ship_catalog(),
            model_slots: level.model_slots.clone(),
            allocate_minion,
        }));
    }

    let model = ResidentPolicyProcess::python_worker(
        &resolve_interpreter(root, &config.python),
        &resolve(root, &config.policy_worker),
        &manifest_path,
    ).map_err(|error| contextual("model worker startup", error))?;
    let sink = DurableDiagnosticNotifications::create_new(
        &notifications, config.run_id.clone(),
    )?;
    let connection = DurableHiveMindConnection::open(checkpoint)
        .map_err(|error| contextual("HiveMind checkpoint", error))?;
    let mut host = assets.build_imported_policy_runtime(
        launches, model, connection, sink,
    ).map_err(|error| contextual("native Level construction", error))?;
    host.set_timing_sample_period(timing);

    let mut rounds = 0_u64;
    let mut failed_window = None;
    while rounds < config.max_rounds {
        let remaining = config.max_rounds - rounds;
        let count = nonzero(remaining.min(u64::from(window.get())) as u32, "window")?;
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
        if let Some(status) = host.sample_status()
            .map_err(|error| contextual("native host status", error))?
        {
            println!("{}", serde_json::to_string(&status)?);
        }
        if counts.stopped_with_no_live_worlds { break; }
    }

    // A finite run budget is an intentional stop, NEVER a fake terminal
    // win or a completed PPO episode. Durable EpisodeAborted notifications
    // explicitly mark partial trajectories as unusable for learning.
    let count = host.simulation().world_count();
    for index in 0..count {
        let world = host.simulation().world(index)
            .ok_or_else(|| invalid("registered Level disappeared"))?;
        if world.state.events.was_aborted() ||
            world.state.events.completed() {
            continue;
        }
        let kind = if world.is_failed() || world.state.events.is_failed() {
            NativeLevelStopKind::AbortFailed
        } else {
            NativeLevelStopKind::CancelLive
        };
        host.stop_world(index, kind)
            .map_err(|error| contextual("final Level stop and delivery", error))?;
    }
    host.sink_mut().sync()?;
    let status = host.runtime_status()
        .map_err(|error| contextual("final native host status", error))?;
    println!("{}", json!({
        "kind": "run_final",
        "run_id": config.run_id,
        "rounds": rounds,
        "completed_worlds": status.completed_worlds,
        "aborted_worlds": status.aborted_worlds,
        "failed_worlds": status.failed_worlds,
        "running_worlds": status.running_worlds,
        "pending_notifications": status.queued_notifications,
        "pending_store_commands": status.pending_store_commands,
        "failed_window": failed_window.is_some(),
    }));
    if let Some(reason) = failed_window {
        return Err(contextual("native Level window failed; unfinished episodes aborted", reason).into());
    }
    Ok(())
}
