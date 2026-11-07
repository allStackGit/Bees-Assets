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
use std::fs::{self, File};
use std::io::{self, ErrorKind};
use std::num::NonZeroU32;
use std::path::{Path, PathBuf};
use std::rc::Rc;
use std::sync::Arc;

use bees_gameplay::level_host::NativeLevelStopKind;
use bees_gameplay::squads::SquadRosterError;
use bees_hivemind::session::SquadLifetime;
use bees_host::durable_connection::DurableHiveMindConnection;
use bees_host::notification_file::DurableDiagnosticNotifications;
use bees_host::policy_process::ResidentPolicyProcess;
use bees_host::presentation::NativeSoftwareRenderer;
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
    #[serde(default = "one_episode")]
    episodes_per_world: u32,
    rounds_per_window: u32,
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

fn make_setup(
    config: &RunConfig,
    level: &LevelConfig,
    assets: &NativeSceneAssets,
    period: NonZeroU32,
    next_id: Rc<Cell<i64>>,
    seed: u64,
) -> NativeExportedPolicyLevelSetup<MinionAllocator> {
    NativeExportedPolicyLevelSetup {
        world_id: WorldId(level.world_id),
        level_id: level.level_id,
        seed,
        broadphase_cell_size: config.broadphase_cell_size,
        decision_period: period,
        initial_full_asteroids: 0,
        groups: None,
        ship_catalog: assets.shared_ship_catalog(),
        model_slots: level.model_slots.clone(),
        allocate_minion: allocate_from_stage(next_id),
    }
}

fn main() -> Result<(), Box<dyn Error>> {
    let mut args = env::args_os().skip(1);
    let config_path = PathBuf::from(args.next().ok_or_else(|| {
        invalid("usage: policy_rollout run.json")
    })?);
    let mut view = false;
    let mut capture = None;
    while let Some(flag) = args.next() {
        if flag == "--view" && !view {
            view = true;
        } else if flag == "--capture" && capture.is_none() {
            capture = Some(PathBuf::from(args.next()
                .ok_or_else(|| invalid("--capture requires an output .ppm path"))?));
        } else {
            return Err(invalid("usage: policy_rollout run.json [--view] [--capture final.ppm]").into());
        }
    }
    #[cfg(not(feature = "client"))]
    if view { return Err(invalid("--view requires building with --features client").into()); }
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
        config.episodes_per_world == 0 ||
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
    // Many headless arenas often share one exported Level. Keep one
    // immutable source template per canonical path, not N copies and
    // filesystem reads. Each arena still parses its own mutable world.
    let mut source_jsons = Vec::with_capacity(config.levels.len());
    let mut source_templates: HashMap<PathBuf, Arc<str>> = HashMap::new();
    for level in &config.levels {
        if level.level_id <= 0 || level.world_id == 0 ||
            !level_ids.insert(level.level_id) ||
            !world_ids.insert(level.world_id) ||
            level.model_slots.is_empty() ||
            !level.model_slots.values().all(|slot| declared.contains(slot))
        {
            return Err(invalid("duplicate/invalid Level or missing real model slot").into());
        }
        let source_path = resolve(root, &level.scene).canonicalize()?;
        let source_json = if let Some(cached) = source_templates.get(&source_path) {
            Arc::clone(cached)
        } else {
            let text: Arc<str> = fs::read_to_string(&source_path)?.into();
            source_templates.insert(source_path, Arc::clone(&text));
            text
        };
        let source = NativePhysicalScene::parse(&source_json)
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
    for source in &mut source_levels {
        for squad in &mut source.source_squads {
            if !seen_squad_ids.insert(squad.item_id) {
                let item_id = next_id.get();
                let following = item_id.checked_add(1)
                    .ok_or_else(|| invalid("Stage squad identities exhausted"))?;
                next_id.set(following);
                squad.item_id = item_id;
                seen_squad_ids.insert(item_id);
            }
        }
    }
    #[cfg(feature = "client")]
    let fixed_dt = std::time::Duration::try_from_secs_f32(source_levels[0].fixed_dt_seconds)
        .map_err(|_| invalid("source fixed duration cannot be represented by the host clock"))?;
    let client_bounds: Vec<_> = source_levels.iter().map(|scene| scene.bounds).collect();
    // Open the optional window before starting the model or creating durable
    // run outputs. A window setup failure leaves no live episode to abandon.
    #[cfg(feature = "client")]
    let mut client = if view { Some(bees_host::client::NativeClient::new(client_bounds[0])?) }
        else { None };
    let mut launches = Vec::with_capacity(source_levels.len());
    for (source, level) in source_levels.into_iter().zip(&config.levels) {
        launches.push((
            source,
            make_setup(
                &config, level, &assets, period,
                Rc::clone(&next_id), level.seed,
            ),
        ));
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
    println!("{}", json!({
        "kind": "run_started",
        "run_id": config.run_id,
        "stage_name": config.stage_name,
        "worlds": config.levels.len(),
        "episodes_per_world": config.episodes_per_world,
        "decision_period": config.decision_period,
        "delivery_every": config.delivery_every,
        "timing_sample_period": config.timing_sample_period,
        "interactive_view": view,
    }));

    let mut rounds = 0_u64;
    let mut failed_window = None;
    let mut completed_episodes = vec![0_u32; config.levels.len()];
    let mut next_status_round = u64::from(window.get());
    #[cfg(feature = "client")]
    let mut user_closed = false;
    #[cfg(not(feature = "client"))]
    let user_closed = false;
    while rounds < config.max_rounds {
        #[cfg(feature = "client")]
        if let Some(client) = &mut client {
            let Some(current) = host.render_world(client.selected_world()) else {
                failed_window = Some("selected Level disappeared".to_owned());
                break;
            };
            match client.poll(current, client_bounds[client.selected_world()],
                host.simulation().world_count(), fixed_dt) {
                Ok(bees_host::client::NativeClientAction::Wait) => continue,
                Ok(bees_host::client::NativeClientAction::Step) => {},
                Ok(bees_host::client::NativeClientAction::Quit) => { user_closed = true; break; },
                Err(error) => { failed_window = Some(format!("native window: {error}")); break; },
            }
        }
        let remaining = config.max_rounds - rounds;
        let requested = if view { 1 } else { window.get() };
        let count = nonzero(remaining.min(u64::from(requested)) as u32, "window")?;
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
        // The reset is an independent fallible transaction. Errors are
        // reported through the same end-of-run abort/report path as a bad
        // model step, rather than escaping main() with live Levels.
        let recycling = (|| -> io::Result<usize> {
            let mut recycled = 0usize;
            for (index, level) in config.levels.iter().enumerate() {
                let finished = host.simulation().world(index)
                    .ok_or_else(|| invalid("registered Level disappeared"))?
                    .state.events.completed();
                if !finished ||
                    completed_episodes[index] == config.episodes_per_world
                {
                    continue;
                }
                completed_episodes[index] += 1;
                if completed_episodes[index] == config.episodes_per_world ||
                    rounds == config.max_rounds
                {
                    continue;
                }
                let mut scene = NativePhysicalScene::parse(&source_jsons[index])
                    .map_err(|error| contextual("cached authored Level", error))?;
                // Replaying an authored template creates new real Stage
                // squad lifetimes, not aliases of retired pooled wrappers.
                for squad in &mut scene.source_squads {
                    let item_id = next_id.get();
                    let following = item_id.checked_add(1)
                        .ok_or_else(|| invalid("Stage squad identities exhausted"))?;
                    next_id.set(following);
                    squad.item_id = item_id;
                }
                let seed = level.seed.wrapping_add(
                    u64::from(completed_episodes[index])
                );
                let setup = make_setup(
                    &config, level, &assets, period, Rc::clone(&next_id), seed,
                );
                let authored = scene.into_exported_policy_only_level(setup)
                    .map_err(|error| contextual("replayed Level source", error))?;
                let replacement = assets.build_level(authored)
                    .map_err(|error| contextual("replayed Level assembly", error))?
                    .state;
                host.reset_world(index, seed, replacement)
                    .map_err(|error| contextual("guarded Level reset", error))?;
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
            next_status_round = rounds.saturating_add(u64::from(window.get()));
            match host.sample_status() {
                Ok(Some(status)) => println!("{}", json!({
                    "kind": "interval",
                    "run_id": config.run_id,
                    "rounds": rounds,
                    "completed_episodes": completed_episodes.iter()
                        .map(|&count| u64::from(count)).sum::<u64>(),
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
        if counts.stopped_with_no_live_worlds && recycled == 0 { break; }
    }

    // A finite run budget is an intentional stop, NEVER a fake terminal
    // win or a completed PPO episode. Durable EpisodeAborted notifications
    // explicitly mark partial trajectories as unusable for learning.
    let count = host.simulation().world_count();
    let mut cleanup_errors = Vec::new();
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
    let status = host.runtime_status()
        .map_err(|error| contextual("final native host status", error))?;
    println!("{}", json!({
        "kind": "run_final",
        "run_id": config.run_id,
        "rounds": rounds,
        "completed_episodes": completed_episodes.iter()
            .map(|&count| u64::from(count)).sum::<u64>(),
        "completed_worlds": status.completed_worlds,
        "aborted_worlds": status.aborted_worlds,
        "failed_worlds": status.failed_worlds,
        "running_worlds": status.running_worlds,
        "pending_notifications": status.queued_notifications,
        "pending_store_commands": status.pending_store_commands,
        "failed_window": failed_window,
        "cleanup_errors": cleanup_errors,
        "user_closed": user_closed,
    }));
    #[cfg(feature = "client")]
    if let Some(client) = &client {
        println!("{}", json!({ "kind": "presentation_final", "run_id": config.run_id,
            "rendered_frames": client.rendered_frames,
            "render_wall_seconds": client.render_wall_seconds }));
    }
    if failed_window.is_some() || !cleanup_errors.is_empty() {
        return Err(invalid(
            "headless rollout failed; run_final identifies the original failure and unresolved obligations",
        ).into());
    }
    Ok(())
}
