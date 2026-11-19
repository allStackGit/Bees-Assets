//! Explicit player-owned training sessions exposed as native game battles.
//!
//! The training controller remains the sole writer of model/checkpoint data.
//! The game launcher only reads its verified publication receipts, then calls
//! the ordinary native battle runner with a fresh player-save output folder.

use std::collections::HashSet;
use std::error::Error;
use std::fs::{self, File};
use std::io::{self, Read};
use std::path::{Path, PathBuf};

use serde::Deserialize;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};

use super::RunConfig;

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Registration {
    schema_version: u32,
    models: Vec<RegisteredModel>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct RegisteredModel {
    id: String,
    title: String,
    receipt: PathBuf,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct PublishedModelRegistration {
    schema_version: u32,
    id: String,
    title: String,
    receipt: PathBuf,
}

pub(super) struct TrainedGame {
    pub id: String,
    pub title: String,
    pub status: String,
    receipt: PathBuf,
}

fn invalid(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}

fn text_field<'a>(value: &'a Value, name: &str) -> io::Result<&'a str> {
    value.get(name).and_then(Value::as_str)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| invalid("trained checkpoint receipt is missing a required field"))
}

fn verified_file(path: &str, sha256: &str) -> io::Result<PathBuf> {
    if sha256.len() != 64 || !sha256.bytes().all(|ch| ch.is_ascii_hexdigit()) {
        return Err(invalid("trained checkpoint has an invalid SHA-256 identity"));
    }
    let file = Path::new(path);
    if !file.is_absolute() {
        return Err(invalid("trained checkpoint receipt must use absolute artifact paths"));
    }
    let file = file.canonicalize()?;
    if !file.is_file() { return Err(invalid("trained checkpoint artifact is not a file")); }
    let mut hasher = Sha256::new();
    let mut stream = File::open(&file)?;
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let count = stream.read(&mut buffer)?;
        if count == 0 { break; }
        hasher.update(&buffer[..count]);
    }
    let actual = format!("{:x}", hasher.finalize());
    if !actual.eq_ignore_ascii_case(sha256) {
        return Err(invalid("trained checkpoint artifact changed after publication"));
    }
    Ok(file)
}

/// A published training run is rooted in the learner's directory; moving
/// those JSON bytes to a player save must not retarget its authored scenes,
/// model weights or worker script. Bind every read-only file dependency to
/// the original verified source *before* archiving the run. A bare Python
/// executable name remains PATH-owned on either operating system.
fn rebase_source_files(run: &mut Value, original_run: &Path) -> io::Result<()> {
    let root = original_run.parent().ok_or_else(||
        invalid("verified trained run has no source directory"))?;
    let resolve = |value: &str| -> io::Result<PathBuf> {
        let path = Path::new(value);
        let file = if path.is_absolute() { path.to_owned() } else { root.join(path) };
        let file = file.canonicalize()?;
        if !file.is_file() {
            return Err(invalid("trained run dependency must be a regular file"));
        }
        Ok(file)
    };
    for field in ["policy_worker", "model_manifest", "collision_matrix", "stage_palette"] {
        if let Some(value) = run.get(field).and_then(Value::as_str).map(str::to_owned) {
            run[field] = json!(resolve(&value)?);
        }
    }
    if let Some(python) = run.get("python").and_then(Value::as_str).map(str::to_owned) {
        let path = Path::new(&python);
        if path.is_absolute() || path.components().count() > 1 {
            run["python"] = json!(resolve(&python)?);
        }
    }
    for level in run["levels"].as_array_mut()
        .ok_or_else(|| invalid("trained run has no source Levels"))?
    {
        let value = text_field(level, "scene")?.to_owned();
        level["scene"] = json!(resolve(&value)?);
    }
    Ok(())
}

/// Select one authoritative world from a training configuration and keep
/// exactly its first fixed-seed episode. The original run's episode_seeds
/// vector is as long as the trainer's recycle count, so retaining it after
/// setting episodes_per_world=1 would fail LevelConfig::validate_seeds.
fn focus_playable_level(run: &mut Value, selected: usize) -> io::Result<()> {
    let levels = run["levels"].as_array()
        .ok_or_else(|| invalid("trained selection has no authored Levels"))?;
    let level = levels.get(selected)
        .ok_or_else(|| invalid("selected native trained Level is no longer available"))?
        .clone();
    let episodes = run["episodes_per_world"].as_u64()
        .filter(|&count| count > 0)
        .ok_or_else(|| invalid("trained selection has no fixed episode count"))?;
    let rounds = run["max_rounds"].as_u64()
        .filter(|&count| count > 0 && count % episodes == 0)
        .ok_or_else(|| invalid("trained selection has no whole episode budget"))?;
    let mut level = level;
    if let Some(seeds) = level["episode_seeds"].as_array_mut() {
        if seeds.len() as u64 != episodes {
            return Err(invalid("trained source has an invalid episode seed schedule"));
        }
        seeds.truncate(1);
    }
    run["levels"] = json!([level]);
    run["max_rounds"] = json!(rounds / episodes);
    run["episodes_per_world"] = json!(1);
    Ok(())
}

fn archive_source_scene(source: &Path, target: &Path) -> io::Result<PathBuf> {
    let mut input = File::open(source)?;
    let mut output = fs::OpenOptions::new().write(true).create_new(true)
        .open(target)?;
    io::copy(&mut input, &mut output)?;
    output.sync_all()?;
    target.canonicalize()
}

impl TrainedGame {
    /// Show the physical Levels in the current verified checkpoint, rather
    /// than requiring a single-arena session just to play a trained model.
    /// Each choice is derived from the real source RunConfig, not a guessed
    /// filename or a second scene catalog.
    pub fn playable_levels(&self) -> Result<Vec<(u64, i64)>, Box<dyn Error>> {
        let source = self.verified_run()?;
        let run: RunConfig = serde_json::from_slice(&fs::read(source)?)?;
        if run.levels.is_empty() {
            return Err(invalid("trained game contains no playable Levels").into());
        }
        Ok(run.levels.iter().map(|level|
            (level.world_id, level.level_id)
        ).collect())
    }

    fn published_receipt(&self) -> Result<Value, Box<dyn Error>> {
        let receipt: Value = serde_json::from_slice(&fs::read(&self.receipt)?)?;
        if receipt["schema_version"].as_u64() != Some(1) ||
            !matches!(receipt["kind"].as_str(),
                Some("native_training_latest_playable" | "native_training_best_evaluated")) {
            return Err(invalid("unsupported native trained checkpoint receipt").into());
        }
        Ok(receipt)
    }

    pub fn verified_run(&self) -> Result<PathBuf, Box<dyn Error>> {
        let receipt = self.published_receipt()?;
        let record = match receipt["kind"].as_str() {
            Some("native_training_latest_playable") => &receipt,
            Some("native_training_best_evaluated") => &receipt["selected"],
            _ => return Err(invalid("unknown native checkpoint selection").into()),
        };
        let run = verified_file(
            text_field(&receipt, "playable_run")?,
            text_field(&receipt, "playable_run_sha256")?,
        )?;
        let manifest = verified_file(
            text_field(record, "manifest")?,
            text_field(record, "manifest_sha256")?,
        )?;
        let checkpoint = verified_file(
            text_field(record, "checkpoint")?,
            text_field(record, "checkpoint_sha256")?,
        )?;
        let recorded_model: Value = serde_json::from_slice(&fs::read(&manifest)?)?;
        if recorded_model["format"] != "bees-native-model-v1" {
            return Err(invalid("trained game requires an authenticated native model manifest").into());
        }
        let learner_slot = receipt.get("learner_slot").and_then(Value::as_u64);
        let checkpoint_sha = text_field(record, "checkpoint_sha256")?;
        let selected = recorded_model["models"].as_array().is_some_and(|models|
            models.iter().any(|model|
                model["backend"] == "mlagents" &&
                model["sha256"].as_str() == Some(checkpoint_sha) &&
                learner_slot.is_none_or(|slot| model["slot"].as_u64() == Some(slot)))
        );
        if !selected {
            return Err(invalid("trained checkpoint does not match its model manifest").into());
        }
        // The gameplay runner performs the remaining scene, worker, policy
        // route and asset validation on the SAME original authored Levels.
        let _: RunConfig = serde_json::from_slice(&fs::read(&run)?)?;
        let _ = checkpoint; // explicit verification is the model identity.
        Ok(run)
    }


    /// Make the selected checkpoint playable from the regular native
    /// library. Only scene controller ownership and policy route change;
    /// CombatWorld, physical assets, source rewards and policy ABI do not.
    /// Freeze the authored scene beside the already archived run so a
    /// future training publication cannot change this battle's replay.
    pub fn archive_challenge(
        &self, parent: &Path, human_side: i32, level_index: usize,
    ) -> Result<PathBuf, Box<dyn Error>> {
        use std::io::Write;
        if !matches!(human_side, 1 | 2) {
            return Err(invalid("trained challenge side must be 1 or 2").into());
        }
        let source_run = self.verified_run()?;
        let receipt = self.published_receipt()?;
        let learner_slot = receipt["learner_slot"].as_u64()
            .ok_or_else(|| invalid("trained challenge receipt lacks a learner model slot"))?;
        let source_bytes = fs::read(&source_run)?;
        if format!("{:x}", Sha256::digest(&source_bytes)) !=
            text_field(&receipt, "playable_run_sha256")?
        {
            return Err(invalid("trained challenge source run changed during publication").into());
        }
        let mut run: Value = serde_json::from_slice(&source_bytes)?;
        if run["hivemind"].is_object() || run["model_updates"].as_array()
            .is_some_and(|updates| !updates.is_empty())
        {
            return Err(invalid("interactive trained challenge requires an offline source").into());
        }
        focus_playable_level(&mut run, level_index)?;
        rebase_source_files(&mut run, &source_run)?;
        let authored_scene = Path::new(text_field(&run["levels"][0], "scene")?);
        let source_scene = if authored_scene.is_absolute() {
            authored_scene.canonicalize()?
        } else {
            source_run.parent().ok_or_else(||
                invalid("trained challenge run has no parent directory"))?
                .join(authored_scene).canonicalize()?
        };
        let mut scene: Value = serde_json::from_slice(&fs::read(&source_scene)?)?;
        if scene["native_game_schema"].as_u64() != Some(1) ||
            !scene["policy_episode"].is_object() ||
            scene["bee_side"].as_i64() != Some(1) ||
            scene["human_side"].as_i64() != Some(2)
        {
            return Err(invalid("interactive trained challenge needs a native-authored policy scene").into());
        }

        let opponent_side = 3 - human_side;
        let mut owned_ships = HashSet::new();
        let mut opponent_team = None;
        let ships = scene["ships"].as_array_mut()
            .ok_or_else(|| invalid("trained challenge scene has no ship roster"))?;
        for ship in ships {
            let side = ship["side"].as_i64()
                .ok_or_else(|| invalid("trained challenge has an unowned ship"))?;
            if side == i64::from(human_side) {
                let id = ship["entity_id"].as_i64()
                    .ok_or_else(|| invalid("trained challenge player ship has no ID"))?;
                owned_ships.insert(id);
                ship.as_object_mut().ok_or_else(||
                    invalid("trained challenge ship is not a JSON object"))?
                    .remove("policy");
                ship["user_controlled"] = Value::Bool(true);
            } else if side == i64::from(opponent_side) {
                ship["user_controlled"] = Value::Bool(false);
                if let Some(team) = ship["policy"]["team_id"].as_u64() {
                    if team > 1 || opponent_team.is_some_and(|old| old != team) {
                        return Err(invalid("trained challenge opponent has inconsistent policy teams").into());
                    }
                    opponent_team = Some(team);
                }
            } else {
                return Err(invalid("trained challenge ship has an unknown side").into());
            }
        }
        let team = opponent_team.ok_or_else(||
            invalid("trained challenge opponent has no native policy controllers"))?;
        if owned_ships.is_empty() {
            return Err(invalid("trained challenge human faction has no ships").into());
        }
        let mut human_squads = HashSet::new();
        for squad in scene["squads"].as_array_mut().ok_or_else(||
            invalid("trained challenge scene has no squads"))? {
            let side = squad["side"].as_i64()
                .ok_or_else(|| invalid("trained challenge squad has no side"))?;
            let is_human = side == i64::from(human_side);
            if !is_human && side != i64::from(opponent_side) {
                return Err(invalid("trained challenge squad has an unknown side").into());
            }
            squad["user_controlled"] = Value::Bool(is_human);
            squad["can_accept_input"] = Value::Bool(is_human);
            if is_human {
                let id = squad["item_id"].as_i64()
                    .ok_or_else(|| invalid("trained challenge player squad has no ID"))?;
                let members = squad["members"].as_array()
                    .ok_or_else(|| invalid("trained challenge squad has no members"))?;
                if members.is_empty() || !members.iter().all(|member|
                    member.as_i64().is_some_and(|id| owned_ships.contains(&id))
                ) {
                    return Err(invalid("trained challenge player squad has an invalid roster").into());
                }
                human_squads.insert(id);
            } else {
                let source = squad.as_object_mut().ok_or_else(||
                    invalid("trained challenge squad is not a JSON object"))?;
                source.remove("hotkey_number");
                source.remove("original_squad_count");
            }
        }
        if human_squads.is_empty() {
            return Err(invalid("trained challenge has no player-controlled squad").into());
        }
        scene["local_player_side"] = json!(human_side);
        // This is a genuine playable match with a selected model opponent,
        // not an RL collector. Its real policy routes and authored deadline
        // stay intact, while terminal/draw rules come from the ordinary
        // native game and no frozen training rewards enter the encounter.
        // Match the Python challenge publisher's exact game-scene contract.
        scene["policy_gameplay"] = Value::Bool(true);
        // Source side identity, hulls, weapons, collision objects and
        // original policy coordinate frames stay put.
        let level = &mut run["levels"][0];
        if let Some(queues) = level["scripted_queues"].as_array_mut() {
            queues.retain(|queue| !queue["squad_item_id"].as_i64()
                .is_some_and(|id| human_squads.contains(&id)));
        }
        let slots = level["model_slots"].as_object_mut()
            .ok_or_else(|| invalid("trained challenge source has no policy slots"))?;
        if !slots.contains_key(&team.to_string()) {
            return Err(invalid("trained challenge opponent has no original policy route").into());
        }
        slots.insert(team.to_string(), json!(learner_slot));
        level.as_object_mut().ok_or_else(||
            invalid("trained challenge Level is not an object"))?.remove("groups");

        let run_name = text_field(&run, "run_id")?.to_owned();
        run["run_id"] = json!(format!(
            "{run_name}-challenge-side-{human_side}-level-{}", level_index + 1
        ));
        run["policy_capture"] = Value::Null;
        run["connection_checkpoint"] = json!("challenge-connection.json");
        run["notifications"] = json!("challenge-events.jsonl");
        run["rollout_summary"] = json!("challenge-summary.json");

        let scene_path = parent.join("replay-source-scene.json");
        let mut scene_out = fs::OpenOptions::new().create_new(true)
            .write(true).open(&scene_path)?;
        serde_json::to_writer_pretty(&mut scene_out, &scene)?;
        scene_out.write_all(b"\n")?;
        scene_out.sync_all()?;
        run["levels"][0]["scene"] = json!("replay-source-scene.json");
        let run_path = parent.join("replay-source-run.json");
        let mut run_out = fs::OpenOptions::new().create_new(true)
            .write(true).open(&run_path)?;
        serde_json::to_writer_pretty(&mut run_out, &run)?;
        run_out.write_all(b"\n")?;
        run_out.sync_all()?;
        Ok(run_path)
    }

    /// Freeze the verified source configuration and its authored scenes,
    /// resolving external model/worker paths relative to the ORIGINAL run
    /// before relocating the file. Later training publications cannot
    /// change this played battle's initial physical world or controller
    /// identity, and replay never silently resolves assets in save-root.
    pub fn archive_playable(&self, parent: &Path, level_index: usize)
        -> Result<PathBuf, Box<dyn Error>>
    {
        use std::io::Write;
        let run = self.verified_run()?;
        let receipt = self.published_receipt()?;
        let bytes = fs::read(&run)?;
        if format!("{:x}", Sha256::digest(&bytes)) !=
            text_field(&receipt, "playable_run_sha256")? {
            return Err(invalid("trained run was republished during game launch").into());
        }
        let mut archived_run: Value = serde_json::from_slice(&bytes)?;
        focus_playable_level(&mut archived_run, level_index)?;
        rebase_source_files(&mut archived_run, &run)?;
        let run_name = text_field(&archived_run, "run_id")?.to_owned();
        archived_run["run_id"] = json!(format!(
            "{run_name}-original-level-{}", level_index + 1
        ));
        let levels = archived_run["levels"].as_array_mut()
            .ok_or_else(|| invalid("trained replay requires source Levels"))?;
        for (index, level) in levels.iter_mut().enumerate() {
            let source = PathBuf::from(text_field(level, "scene")?);
            let filename = format!("replay-level-{index}.scene.json");
            archive_source_scene(&source, &parent.join(&filename))?;
            level["scene"] = json!(filename);
        }
        let archived = parent.join("replay-source-run.json");
        let mut output = fs::OpenOptions::new().write(true).create_new(true)
            .open(&archived)?;
        serde_json::to_writer_pretty(&mut output, &archived_run)?;
        output.write_all(b"\n")?;
        output.sync_all()?;
        Ok(archived)
    }
}

// Keep a finite bound for malformed or externally populated save roots,
// but do not cap ordinary experiment history at just 32 latest/best jobs.
// The existing native library menu already pages its selectable rows.
const MAX_TRAINED_GALLERY_ENTRIES: usize = 4096;

/// Explicit manual registrations and independently published job receipts.
/// Each training job owns one atomic JSON registration under trained-models.d;
/// simultaneous jobs cannot lose each other's entries by rewriting a shared
/// manifest. Only this bounded save-root folder is scanned, never the
/// trainer's workspace or arbitrary assets.
pub(super) fn load(save_root: &Path) -> Result<Vec<TrainedGame>, Box<dyn Error>> {
    let manifest = save_root.join("trained-models.json");
    let mut registrations = if manifest.is_file() {
        match fs::read(&manifest).ok()
            .and_then(|bytes| serde_json::from_slice::<Registration>(&bytes).ok()) {
            Some(registry) if registry.schema_version == 1 => registry.models,
            _ => {
                eprintln!(
                    "Ignoring unreadable or unsupported native manual gallery {}; independently published checkpoints remain available",
                    manifest.display(),
                );
                Vec::new()
            }
        }
    } else {
        Vec::new()
    };
    if registrations.len() > MAX_TRAINED_GALLERY_ENTRIES {
        return Err(invalid("native game gallery exceeds its bounded registration inventory").into());
    }

    let published = save_root.join("trained-models.d");
    if published.exists() {
        if !fs::symlink_metadata(&published)?.file_type().is_dir() {
            return Err(invalid("native trained-model publication directory is invalid").into());
        }
        let mut files = Vec::new();
        for entry in fs::read_dir(&published)? {
            let entry = entry?;
            let path = entry.path();
            if path.extension().and_then(|part| part.to_str()) != Some("json") {
                continue; // pending atomic writes are deliberately invisible
            }
            if !entry.file_type()?.is_file() {
                eprintln!(
                    "Ignoring non-regular native training registration {}",
                    path.display(),
                );
                continue;
            }
            files.push(path);
            if registrations.len() + files.len() > MAX_TRAINED_GALLERY_ENTRIES {
                return Err(invalid("native game gallery exceeds its bounded registration inventory").into());
            }
        }
        // read_dir order is OS/filesystem dependent. Stable IDs must give
        // stable menu ordering on Linux and Windows even when 32 learners
        // publish independently while a player is in a native battle.
        files.sort_unstable();
        for path in files {
            // Independent completed jobs publish atomically. A single stale
            // or damaged job entry must not make the entire native game
            // inaccessible, including other valid playable checkpoints.
            let registered = (|| -> Result<RegisteredModel, Box<dyn Error>> {
                let registration: PublishedModelRegistration =
                    serde_json::from_slice(&fs::read(&path)?)?;
                if registration.schema_version != 1 ||
                    path.file_stem().and_then(|part| part.to_str()) != Some(registration.id.as_str()) {
                    return Err(invalid("published model ID differs from its registration filename").into());
                }
                Ok(RegisteredModel {
                    id: registration.id, title: registration.title,
                    receipt: registration.receipt,
                })
            })();
            match registered {
                Ok(registration) => registrations.push(registration),
                Err(error) => eprintln!(
                    "Ignoring invalid native training registration {}: {error}",
                    path.display(),
                ),
            }
        }
    }

    let mut seen = HashSet::new();
    let mut games = Vec::with_capacity(registrations.len());
    for model in registrations {
        if seen.contains(&model.id) {
            eprintln!("Ignoring duplicate native trained-model registration: {}", model.id);
            continue;
        }
        let registered = (|| -> Result<TrainedGame, Box<dyn Error>> {
            if model.id.is_empty() || model.id.len() > 40 ||
                !model.id.bytes().all(|ch| ch.is_ascii_alphanumeric() || ch == b'-' || ch == b'_') ||
                model.title.trim().is_empty() || model.title.len() > 120 ||
                model.receipt.as_os_str().is_empty() {
                return Err(invalid("native trained-model registration has an invalid name").into());
            }
            let receipt = if model.receipt.is_absolute() {
                model.receipt.canonicalize()?
            } else {
                save_root.join(&model.receipt).canonicalize()?
            };
            if !receipt.is_file() {
                return Err(invalid("native trained-model receipt is not a file").into());
            }
            let kind: Value = serde_json::from_slice(&fs::read(&receipt)?)?;
            let label = match (kind["schema_version"].as_u64(), kind["kind"].as_str()) {
                (Some(1), Some("native_training_latest_playable")) => "[Latest checkpoint]",
                (Some(1), Some("native_training_best_evaluated")) => "[Best holdout]",
                _ => return Err(invalid("gallery requires latest-playable or best-evaluated native receipts").into()),
            };
            let rounds = kind["completed_rounds"].as_u64()
                .ok_or_else(|| invalid("trained-model receipt omits completed round count"))?;
            Ok(TrainedGame {
                id: format!("trained-{}", model.id),
                title: model.title.clone(),
                status: format!("  {label}  {rounds} rounds"),
                receipt,
            })
        })();
        match registered {
            Ok(game) => {
                seen.insert(model.id);
                games.push(game);
            }
            Err(error) => eprintln!(
                "Ignoring invalid native trained-model registration {}: {error}",
                model.id,
            ),
        }
    }
    Ok(games)
}
