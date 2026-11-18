//! A native-game entry point into the existing Python MA-POCA pipeline.
//!
//! Fleet battles and checkpoint gallery remain owned by the native game;
//! bootstrap, optimizer, holdout and publication remain solely owned by the
//! Python trainer. A job gets one private output tree and one process.

use std::env;
use std::error::Error;
use std::fs::{self, OpenOptions, File};
use std::io::{Read, Seek, SeekFrom};
use std::io::{self, ErrorKind};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};

use bees_host::presentation::{select_library, BattleMenuRow};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};

fn invalid(message: impl Into<String>) -> io::Error {
    io::Error::new(ErrorKind::InvalidInput, message.into())
}

fn choose_count(label: &str, choices: &[u32]) -> Result<Option<u32>, Box<dyn Error>> {
    let titles = choices.iter().map(|number|
        format!("{number}  |  {label}")
    ).collect::<Vec<_>>();
    let rows = titles.iter().map(|title| BattleMenuRow {
        title, status: "  [Training resource]", unlocked: true,
    }).collect::<Vec<_>>();
    let mut selection = 0;
    Ok(select_library(&rows, &mut selection, false)?
        .map(|index| choices[index]))
}

fn archived_battles(save_root: &Path) -> Result<Vec<(PathBuf, String)>, Box<dyn Error>> {
    let root = save_root.join("battles").join("fleet-skirmish");
    if !root.is_dir() {
        return Ok(Vec::new());
    }
    let mut battles = Vec::new();
    for entry in fs::read_dir(root)? {
        let entry = entry?;
        if !entry.file_type()?.is_dir() ||
            !entry.file_name().to_string_lossy().starts_with("battle-")
        {
            continue;
        }
        let run_path = entry.path().join("replay-source-run.json");
        if !run_path.is_file() { continue; }
        let run: Value = serde_json::from_slice(&fs::read(&run_path)?)?;
        let Some(levels) = run["levels"].as_array() else { continue };
        if run["episodes_per_world"].as_u64() != Some(1) ||
            !run["run_id"].as_str().is_some_and(|id| id.starts_with("native-fleet-")) ||
            levels.len() != 1 ||
            ["survival", "reinforcements"].iter()
                .any(|key| !levels[0][*key].is_null())
        {
            continue;
        }
        let title = format!(
            "{}  |  {}",
            entry.file_name().to_string_lossy(),
            run["run_id"].as_str().unwrap_or("Fleet Skirmish"),
        );
        battles.push((run_path, title));
    }
    battles.sort_unstable_by(|a, b| b.0.cmp(&a.0));
    Ok(battles)
}

/// Resolve tooling at runtime: never bake the developer's checkout path into
/// a Windows/Linux game executable. Packaged games can ship tools alongside
/// their binary, or the user can point BEES_NATIVE_TOOLS to the existing
/// project directory. No separate learner implementation is introduced.
fn converter_script() -> Result<PathBuf, Box<dyn Error>> {
    let mut roots = Vec::new();
    if let Some(root) = env::var_os("BEES_NATIVE_TOOLS") {
        roots.push(PathBuf::from(root));
    }
    let cwd = env::current_dir()?;
    roots.push(cwd.join("native").join("tools"));
    roots.push(cwd.join("tools"));
    if let Some(parent) = env::current_exe()?.parent() {
        roots.push(parent.join("tools"));
        roots.push(parent.join("native").join("tools"));
    }
    for root in roots {
        let path = root.join("prepare_fleet_training.py");
        if path.is_file() {
            return Ok(path.canonicalize()?);
        }
    }
    Err(invalid(
        "native MA-POCA tools not installed; provide BEES_NATIVE_TOOLS pointing to native/tools",
    ).into())
}


fn approved_hub_plan() -> Result<Option<(PathBuf, String, u16, String)>, Box<dyn Error>> {
    let Some(path) = env::var_os("BEES_NATIVE_HUB_PLAN") else {
        return Ok(None);
    };
    let plan = PathBuf::from(path).canonicalize()?;
    let bytes = fs::read(&plan)?;
    let source: Value = serde_json::from_slice(&bytes)?;
    let collectors = source["collectors"].as_array();
    if source["schema_version"].as_u64() != Some(1) ||
        source["kind"].as_str() != Some("native_hub_round_dispatch") ||
        source["schedule_start"].as_bool() != Some(true) ||
        source["confirm_unattended_start"].as_bool() != Some(true) ||
        source["confirm_gpu_start"].as_bool().is_none() ||
        !collectors.is_some_and(|items| (2..=16).contains(&items.len()))
    {
        return Err(invalid(
            "native Hub plan requires 2-16 allocated machines and explicit unattended Start approval"
        ).into());
    }
    let listener = env::var("BEES_NATIVE_BUNDLE_LISTEN").map_err(|_|
        invalid("Hub fleet training requires BEES_NATIVE_BUNDLE_LISTEN=HOST:PORT")
    )?;
    let token = env::var("BEES_NATIVE_BUNDLE_TOKEN").map_err(|_|
        invalid("Hub fleet training requires an operator-owned BEES_NATIVE_BUNDLE_TOKEN")
    )?;
    if !(32..=256).contains(&token.len()) ||
        !token.bytes().all(|ch| (33..=126).contains(&ch)) {
        return Err(invalid("native collector bundle token must be 32..256 printable ASCII characters").into());
    }
    if listener.is_empty() || listener.len() > 255 ||
        !listener.rsplit_once(':').is_some_and(|(host, port)|
            !host.is_empty() && port.parse::<u16>().is_ok_and(|n| n != 0))
    {
        return Err(invalid("native collector receiver requires valid HOST:PORT").into());
    }
    let port = match env::var("BEES_NATIVE_HUB_PORT") {
        Ok(value) => value.parse::<u16>().ok().filter(|value| *value > 0)
            .ok_or_else(|| invalid("BEES_NATIVE_HUB_PORT must be between 1 and 65535"))?,
        Err(env::VarError::NotPresent) => 7148,
        Err(error) => return Err(error.into()),
    };
    Ok(Some((plan, listener, port, format!("{:x}", Sha256::digest(&bytes)))))
}

/// All machine allocation and start authorization stays in the unchanged
/// user-approved Hub plan. The native game chooses only the aggregate World
/// budget and forwards it to the already existing native Hub coordinator.
fn hub_arguments(
    command: &mut Command, job: &Path, source: &Value,
) -> Result<(), Box<dyn Error>> {
    let plan = PathBuf::from(source["hub_plan"].as_str()
        .ok_or_else(|| invalid("Hub training invocation is missing its approved plan"))?);
    let bytes = fs::read(&plan)?;
    let expected = source["hub_plan_sha256"].as_str()
        .ok_or_else(|| invalid("Hub training invocation is missing pinned plan identity"))?;
    if format!("{:x}", Sha256::digest(&bytes)) != expected {
        return Err(invalid("approved Hub machine allocation changed since native training selection").into());
    }
    let listener = source["hub_receiver_listen"].as_str()
        .ok_or_else(|| invalid("native collector upload receiver is missing"))?;
    let port = source["hub_port"].as_u64()
        .filter(|value| (1..=65535).contains(value))
        .ok_or_else(|| invalid("native Hub operator port is invalid"))?;
    let worlds = source["hub_worlds"].as_u64()
        .filter(|value| (2..=16384).contains(value))
        .ok_or_else(|| invalid("native Hub World budget is invalid"))?;
    // The learner receives archives over its authenticated streaming
    // receiver. These are separate per-job directories, not shared
    // checkpoints or uncontrolled writes to Experiment Hub's data root.
    let inbox = job.join("bundle-inbox");
    let outbox = job.join("collector-input-outbox");
    fs::create_dir_all(&inbox)?;
    fs::create_dir_all(&outbox)?;
    command.arg("--hub-auto-worlds").arg(worlds.to_string())
        .arg("--bundle-inbox").arg(inbox)
        .arg("--collector-input-outbox").arg(outbox)
        .arg("--bundle-receiver-listen").arg(listener)
        .arg("--hub-dispatch-plan").arg(plan)
        .arg("--hub-dispatch-port").arg(port.to_string())
        .arg("--auto-balance-collectors");
    Ok(())
}

fn valid_torch_device(device: &str) -> bool {
    // These are the same PyTorch-visible device indices on CUDA and ROCm.
    // The training interpreter validates actual availability before using
    // a chosen device; the game never guesses PCI or visible GPU topology.
    matches!(device, "cpu" | "cuda" | "cuda:0" | "cuda:1" | "cuda:2" | "cuda:3")
}

fn append_training_arguments(
    command: &mut Command, job: &Path, recipe: &Value,
) -> Result<(), Box<dyn Error>> {
    // First launch and resume must execute the same immutable training recipe.
    let rounds = recipe["rounds"].as_u64()
        .filter(|n| (1..=99_999).contains(n))
        .ok_or_else(|| invalid("invalid training round count"))?;
    let side = recipe["learner_side"].as_u64().unwrap_or(1);
    if ![1, 2].contains(&side) || recipe["evaluation_side"].as_u64() != Some(side) ||
        recipe["holdout_worlds"].as_u64() != Some(8) {
        return Err(invalid("training learner and holdout sides do not match").into());
    }
    let slot = side - 1;
    // Native Simulation resets the original CombatWorld and policy routing
    // after each episode without respawning a process or reloading weights.
    // Keep this part of the immutable model bootstrap identity on resume.
    let episodes = recipe["episodes_per_world"].as_u64().unwrap_or(1);
    if ![1, 2, 4, 8, 16].contains(&episodes) {
        return Err(invalid("invalid native episodes-per-arena count").into());
    }
    command.arg("--episodes-per-world").arg(episodes.to_string())
        .arg("--train-rounds").arg(rounds.to_string())
        .arg("--learner-slot").arg(slot.to_string());
    if !recipe["self_play_refresh_every"].is_null() {
        let every = recipe["self_play_refresh_every"].as_u64()
            .filter(|n| rounds > 1 && (1..=99_999).contains(n))
            .ok_or_else(|| invalid("invalid self-play refresh interval"))?;
        command.arg("--self-play-opponent-slot").arg((1-slot).to_string())
            .arg("--self-play-refresh-every").arg(every.to_string());
    }
    if !recipe["early_stop_patience"].is_null() {
        let patience = recipe["early_stop_patience"].as_u64()
            .filter(|n| rounds >= 10 && (1..=99_999).contains(n))
            .ok_or_else(|| invalid("invalid fixed-holdout early stopping policy"))?;
        command.arg("--early-stop-patience").arg(patience.to_string());
    }
    let evaluation_every = recipe["evaluation_every"].as_u64().unwrap_or(1);
    if ![1, 2, 5, 10].contains(&evaluation_every) ||
        evaluation_every > rounds
    {
        return Err(invalid("invalid native fixed-holdout evaluation cadence").into());
    }
    command.arg("--evaluation-every").arg(evaluation_every.to_string());
    if recipe["hub_plan"].is_string() {
        if !recipe["worlds"].is_null() || !recipe["physics_workers"].is_null() ||
            !recipe["rollout_processes"].is_null()
        {
            return Err(invalid("Hub allocation cannot also claim local CPU workers").into());
        }
        hub_arguments(command, job, recipe)?;
    } else {
        if !recipe["hub_worlds"].is_null() {
            return Err(invalid("local allocation cannot claim distributed Worlds").into());
        }
        let worlds = recipe["worlds"].as_u64()
            .filter(|n| (1..=1024).contains(n))
            .ok_or_else(|| invalid("invalid native local World count"))?;
        let physics = recipe["physics_workers"].as_u64()
            .filter(|n| (1..=128).contains(n))
            .ok_or_else(|| invalid("invalid native physics worker count"))?;
        command.arg("--rollout-worlds").arg(worlds.to_string())
            .arg("--physics-workers").arg(physics.to_string());
        if !recipe["rollout_processes"].is_null() {
            let processes = recipe["rollout_processes"].as_u64()
                .filter(|n| (1..=256).contains(n) && *n <= worlds)
                .ok_or_else(|| invalid("invalid native rollout process count"))?;
            if recipe["rollout_binary"].is_null() {
                return Err(invalid("native rollout processes require a compiled binary").into());
            }
            command.arg("--rollout-workers").arg(processes.to_string());
        }
    }
    // The learner's local holdout evaluator always needs the same native
    // binary, even when its training experience comes entirely from Hub.
    // This is NOT a Hub collector allocation or remote process request.
    if !recipe["rollout_binary"].is_null() {
        let binary = PathBuf::from(recipe["rollout_binary"].as_str()
            .ok_or_else(|| invalid("invalid native rollout executable path"))?);
        let expected = recipe["rollout_binary_sha256"].as_str()
            .ok_or_else(|| invalid("missing pinned native rollout executable identity"))?;
        if !binary.is_file() || sha256_file(&binary)? != expected {
            return Err(invalid("compiled native rollout executable changed since training selection").into());
        }
        command.arg("--rollout-binary").arg(binary);
    } else if !recipe["rollout_binary_sha256"].is_null() {
        return Err(invalid("native rollout executable digest has no binary").into());
    }
    if !recipe["curriculum_plan"].is_null() {
        let plan = PathBuf::from(recipe["curriculum_plan"].as_str()
            .ok_or_else(|| invalid("invalid source-authored Stage curriculum plan"))?);
        let expected = recipe["curriculum_plan_sha256"].as_str()
            .ok_or_else(|| invalid("native curriculum plan has no pinned source identity"))?;
        if !plan.is_file() || sha256_file(&plan)? != expected {
            return Err(invalid("native curriculum plan changed after experiment selection").into());
        }
        command.arg("--curriculum-plan").arg(plan);
    } else if !recipe["curriculum_plan_sha256"].is_null() {
        return Err(invalid("native curriculum has a digest without an authored plan").into());
    }
    if !recipe["trainer_config"].is_null() {
        let config = PathBuf::from(recipe["trainer_config"].as_str()
            .ok_or_else(|| invalid("invalid pinned trainer configuration path"))?);
        let expected = recipe["trainer_config_sha256"].as_str()
            .ok_or_else(|| invalid("missing pinned PPO configuration identity"))?;
        if !config.starts_with(job) || !config.is_file() ||
            format!("{:x}", Sha256::digest(fs::read(&config)?)) != expected {
            return Err(invalid("native PPO configuration changed since experiment creation").into());
        }
        command.arg("--trainer-config").arg(config);
    } else if !recipe["trainer_config_sha256"].is_null() {
        return Err(invalid("orphan native trainer configuration digest").into());
    }
    let device = recipe["optimizer_device"].as_str()
        .filter(|value| valid_torch_device(value))
        .ok_or_else(|| invalid("invalid native optimizer device"))?;
    let inference = recipe["inference_device"].as_str().unwrap_or("cpu");
    if !valid_torch_device(inference) ||
        (inference != "cpu" &&
         (recipe["hub_plan"].is_string() || device != inference))
    {
        return Err(invalid(
            "GPU policy inference requires a locally allocated CUDA/ROCm optimizer"
        ).into());
    }
    command.arg("--inference-device").arg(inference);
    let saves = PathBuf::from(recipe["save_root"].as_str()
        .ok_or_else(|| invalid("missing native save directory"))?);
    let game_id = recipe["game_id"].as_str()
        .ok_or_else(|| invalid("missing native trained game ID"))?;
    let title = recipe["title"].as_str()
        .ok_or_else(|| invalid("missing native trained game title"))?;
    if !saves.is_dir() || !job.starts_with(saves.join("training").join("fleet-skirmish")) {
        return Err(invalid("training job is not owned by the saved native game").into());
    }
    command.arg("--optimizer-device").arg(device)
        .arg("--holdout-worlds").arg("8")
        .arg("--evaluation-side").arg(side.to_string())
        .arg("--library-save-root").arg(saves)
        .arg("--library-game-id").arg(game_id)
        .arg("--library-game-title").arg(title);
    Ok(())
}



fn sha256_file(path: &Path) -> io::Result<String> {
    // Unlike trainer YAML, rollout executables may be tens of MiB: hash in
    // bounded chunks without retaining or copying binary bytes into Rust.
    let mut file = File::open(path)?;
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let count = file.read(&mut buffer)?;
        if count == 0 { break; }
        digest.update(&buffer[..count]);
    }
    Ok(format!("{:x}", digest.finalize()))
}

fn available_rollout_binary() -> Result<Option<PathBuf>, Box<dyn Error>> {
    let executable = if cfg!(windows) { "policy_rollout.exe" } else { "policy_rollout" };
    if let Some(value) = env::var_os("BEES_NATIVE_ROLLOUT_BINARY") {
        let file = PathBuf::from(value).canonicalize()?;
        if !file.is_file() {
            return Err(invalid("configured native policy rollout binary is missing").into());
        }
        return Ok(Some(file));
    }
    let cwd = env::current_dir()?;
    let mut candidates = vec![
        cwd.join("native/target/release").join(executable),
        cwd.join("native/target/debug").join(executable),
    ];
    if let Some(parent) = env::current_exe()?.parent() {
        candidates.insert(0, parent.join(executable));
        candidates.push(parent.join("native/target/release").join(executable));
    }
    Ok(candidates.into_iter().find(|path| path.is_file())
       .map(|path| path.canonicalize()).transpose()?)
}

fn choose_trainer_config(save_root: &Path) -> Result<Option<Option<PathBuf>>, Box<dyn Error>> {
    // User-authored PPO configs belong to the player's saved experiments,
    // not the source game or the compiled Rust binary.
    let directory = save_root.join("training").join("configurations");
    let mut configs = Vec::new();
    if directory.is_dir() {
        for entry in fs::read_dir(&directory)? {
            let entry = entry?;
            let file_type = entry.file_type()?;
            let path = entry.path();
            if !file_type.is_file() ||
                !matches!(path.extension().and_then(|s| s.to_str()), Some("yaml" | "yml"))
            {
                continue;
            }
            if entry.metadata()?.len() > 1_048_576 {
                return Err(invalid("native PPO configuration cannot exceed 1 MiB").into());
            }
            configs.push(path.canonicalize()?);
        }
    }
    configs.sort_unstable();
    let mut titles = vec!["Project default PPO configuration".to_string()];
    titles.extend(configs.iter().map(|path|
        path.file_name().map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_else(|| "PPO configuration".to_string())
    ));
    let rows = titles.iter().map(|title| BattleMenuRow {
        title, status: "  [Pinned for training]", unlocked: true,
    }).collect::<Vec<_>>();
    let mut selected = 0;
    Ok(select_library(&rows, &mut selected, false)?.map(|index|
        if index == 0 { None } else { Some(configs[index - 1].clone()) }
    ))
}


fn choose_curriculum_plan(save_root: &Path)
    -> Result<Option<Option<PathBuf>>, Box<dyn Error>>
{
    let directory = save_root.join("training").join("curricula");
    if !directory.is_dir() {
        return Ok(Some(None));
    }
    let mut plans = Vec::new();
    for entry in fs::read_dir(directory)? {
        let entry = entry?;
        if !entry.file_type()?.is_file() ||
            entry.path().extension().and_then(|ext| ext.to_str()) != Some("json") {
            continue;
        }
        if entry.metadata()?.len() > 1_048_576 {
            return Err(invalid("native curriculum Stage plan exceeds 1 MiB").into());
        }
        plans.push(entry.path().canonicalize()?);
    }
    if plans.is_empty() {
        return Ok(Some(None));
    }
    plans.sort_unstable();
    let mut titles = vec!["One fixed physical fleet throughout training".to_owned()];
    titles.extend(plans.iter().map(|path|
        path.file_name().map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_else(|| "Native curriculum Stage plan".to_owned())
    ));
    let rows = titles.iter().map(|title| BattleMenuRow {
        title, status: "  [Existing source-authored Stage transitions]",
        unlocked: true,
    }).collect::<Vec<_>>();
    let mut selected = 0;
    Ok(select_library(&rows, &mut selected, false)?.map(|index|
        if index == 0 { None } else { Some(plans[index - 1].clone()) }
    ))
}

fn pin_trainer_config(
    job: &Path, source: Option<&Path>,
) -> Result<(Option<PathBuf>, Option<String>), Box<dyn Error>> {
    let Some(source) = source else { return Ok((None, None)); };
    let contents = fs::read(source)?;
    if contents.is_empty() || contents.len() > 1_048_576 {
        return Err(invalid("native PPO configuration must contain 1 byte..1 MiB").into());
    }
    let sha = format!("{:x}", Sha256::digest(&contents));
    let destination = job.join("trainer-config.yaml");
    let mut file = OpenOptions::new().write(true).create_new(true).open(&destination)?;
    use std::io::Write;
    file.write_all(&contents)?;
    file.sync_all()?;
    if format!("{:x}", Sha256::digest(fs::read(source)?)) != sha {
        return Err(invalid("PPO configuration changed during native experiment selection").into());
    }
    Ok((Some(destination), Some(sha)))
}

/// A game-only release can play without Python or Rust. Keep training
/// actions inaccessible unless their canonical runtime is actually present.
pub(super) fn training_available() -> bool {
    converter_script().is_ok()
}

fn new_training_job(save_root: &Path) -> io::Result<(PathBuf, String)> {
    let root = save_root.join("training").join("fleet-skirmish");
    fs::create_dir_all(&root)?;
    for number in 1..u32::MAX {
        let name = format!("training-{number:06}");
        let folder = root.join(&name);
        match fs::create_dir(&folder) {
            Ok(()) => return Ok((folder, format!("fleet-{name}"))),
            Err(error) if error.kind() == ErrorKind::AlreadyExists => continue,
            Err(error) => return Err(error),
        }
    }
    Err(invalid("native fleet training job directory IDs exhausted"))
}

/// The game's existing gallery discovers model receipts between menu turns.
/// Training is a child worker with its own separate stdout/stderr; closing
/// the native window does not make the game a checkpoint writer or owner.
pub(super) fn choose_and_train(save_root: &Path) -> Result<(), Box<dyn Error>> {
    let battles = archived_battles(save_root)?;
    if battles.is_empty() {
        println!("No saved elimination or extraction fleet skirmishes available for native training.");
        return Ok(());
    }
    let rows = battles.iter().map(|(_, title)| BattleMenuRow {
        title, status: "  [Saved physical fleet]", unlocked: true,
    }).collect::<Vec<_>>();
    let mut selected = 0;
    let Some(index) = select_library(&rows, &mut selected, false)? else {
        return Ok(());
    };
    let Some(rounds) = choose_count("MA-POCA optimizer rounds", &[1, 3, 10, 25, 100])?
        else { return Ok(()); };
    let Some(trainer_source) = choose_trainer_config(save_root)? else {
        return Ok(());
    };
    let curriculum_plan = if rounds > 1 {
        let Some(plan) = choose_curriculum_plan(save_root)? else {
            return Ok(());
        };
        plan
    } else {
        None
    };
    let curriculum_plan_sha256 = curriculum_plan.as_ref()
        .map(|path| sha256_file(path)).transpose()?;
    let Some(episodes_per_world) = choose_count(
        "Episodes per persistent native arena", &[1, 2, 4, 8, 16],
    )? else { return Ok(()); };
    let sides = [
        BattleMenuRow {
            title: "Train Bees faction",
            status: "  [Policy team 0; evaluate Bees]", unlocked: true,
        },
        BattleMenuRow {
            title: "Train Humans faction",
            status: "  [Policy team 1; evaluate Humans]", unlocked: true,
        },
    ];
    let mut side_choice = 0;
    let Some(side_index) = select_library(&sides, &mut side_choice, false)? else {
        return Ok(());
    };
    let learner_side = side_index as u32 + 1;
    let learner_slot = side_index as u32;
    let opponent_slot = 1 - learner_slot;
    let refresh_every = if rounds > 1 {
        let rows = [
            BattleMenuRow {
                title: "Fixed Opponent",
                status: "  [Train learner only]", unlocked: true,
            },
            BattleMenuRow {
                title: "Self-Play (refresh every round)",
                status: "  [Update real opponent policy]", unlocked: true,
            },
            BattleMenuRow {
                title: "Self-Play (refresh every 3 rounds)",
                status: "  [Stabler opponent updates]", unlocked: true,
            },
            BattleMenuRow {
                title: "Self-Play (refresh every 5 rounds)",
                status: "  [Longer opponent generations]", unlocked: true,
            },
        ];
        let mut selected = 0;
        let Some(index) = select_library(&rows, &mut selected, false)? else {
            return Ok(());
        };
        match index { 1 => Some(1_u32), 2 => Some(3), 3 => Some(5), _ => None }
    } else {
        None
    };
    let evaluation_every = if rounds >= 10 {
        let choices = [1, 2, 5, 10].into_iter()
            .filter(|&interval| interval <= rounds).collect::<Vec<_>>();
        let Some(interval) = choose_count(
            "Fixed-seed holdout every N optimizer rounds", &choices,
        )? else { return Ok(()); };
        interval
    } else {
        1
    };
    let early_stop_patience = if rounds >= 10 {
        let choices = [
            BattleMenuRow {
                title: "Full selected training rounds",
                status: "  [No early stopping]", unlocked: true,
            },
            BattleMenuRow {
                title: "Stop after 3 stagnant holdouts",
                status: "  [Verified learning plateau]", unlocked: true,
            },
            BattleMenuRow {
                title: "Stop after 5 stagnant holdouts",
                status: "  [Conservative early stop]", unlocked: true,
            },
        ];
        let mut selected = 0;
        let Some(chosen) = select_library(&choices, &mut selected, false)? else {
            return Ok(());
        };
        match chosen { 1 => Some(3_u32), 2 => Some(5_u32), _ => None }
    } else {
        None
    };
    // A configured Hub plan is opt-in, not a prerequisite for local
    // training. An expired or malformed Hub plan must never prevent the
    // player from running an ordinary CPU-only experiment.
    let use_hub = if env::var_os("BEES_NATIVE_HUB_PLAN").is_some() {
        let rows = [
            BattleMenuRow {
                title: "Local CPU arenas", status: "  [This computer]",
                unlocked: true,
            },
            BattleMenuRow {
                title: "Experiment Hub machines", status: "  [Approved resource plan]",
                unlocked: true,
            },
        ];
        let mut selected = 0;
        let Some(choice) = select_library(&rows, &mut selected, false)? else {
            return Ok(());
        };
        choice == 1
    } else {
        false
    };
    let hub = if use_hub { approved_hub_plan()? } else { None };
    let (worlds, workers, hub_worlds, rollout_processes, rollout_binary) = if use_hub {
        let Some(worlds) = choose_count(
            "Total distributed native arenas", &[16, 32, 64, 128, 256, 512, 1024],
        )? else { return Ok(()); };
        // A distributed learner still evaluates holdouts on its own host.
        // Forward a prebuilt rollout executable if available; remote jobs
        // continue using only Hub-approved allocations.
        (None, None, Some(worlds), None, available_rollout_binary()?)
    } else {
        let Some(worlds) = choose_count(
            "Parallel native arenas", &[1, 2, 4, 8, 16, 32, 64],
        )? else { return Ok(()); };
        let binary = available_rollout_binary()?;
        let processes = if binary.is_some() {
            let choices = [1, 2, 4, 8, 16].into_iter()
                .filter(|&count| count <= worlds).collect::<Vec<_>>();
            let Some(count) = choose_count("Persistent rollout processes", &choices)? else {
                return Ok(());
            };
            Some(count)
        } else {
            None
        };
        let Some(workers) = choose_count(
            "CPU physics workers per rollout process", &[1, 2, 4, 8, 16, 32],
        )? else { return Ok(()); };
        (Some(worlds), Some(workers), None, processes, binary)
    };
    let device_rows = [
        BattleMenuRow { title: "CPU", status: "  [Optimizer]", unlocked: true },
        BattleMenuRow { title: "CUDA / ROCm GPU 0", status: "  [Optimizer device cuda:0]", unlocked: true },
        BattleMenuRow { title: "CUDA / ROCm GPU 1", status: "  [Optimizer device cuda:1]", unlocked: true },
        BattleMenuRow { title: "CUDA / ROCm GPU 2", status: "  [Optimizer device cuda:2]", unlocked: true },
        BattleMenuRow { title: "CUDA / ROCm GPU 3", status: "  [Optimizer device cuda:3]", unlocked: true },
    ];
    let mut device_selection = 0;
    let Some(device) = select_library(&device_rows, &mut device_selection, false)?
        else { return Ok(()); };
    let optimizer_device = ["cpu", "cuda:0", "cuda:1", "cuda:2", "cuda:3"][device];
    let inference_device = if device != 0 && !use_hub {
        let rows = [
            BattleMenuRow {
                title: "CPU policy inference",
                status: "  [Keep neural action sampling on CPU]", unlocked: true,
            },
            BattleMenuRow {
                title: "CUDA / ROCm GPU policy inference",
                status: "  [Batch neural actions on the local GPU]", unlocked: true,
            },
        ];
        let mut choice = 0;
        let Some(selected) = select_library(&rows, &mut choice, false)? else {
            return Ok(());
        };
        if selected == 1 { optimizer_device } else { "cpu" }
    } else {
        // Hub's remote collectors can be CPU-only machines, and local CPU
        // optimizers do not imply an available GPU model worker.
        "cpu"
    };

    // Resolve and validate before allocating a permanent job name. GPU
    // availability and the Python environment are validated by the existing
    // bootstrap and learner, not guessed from operating-system strings.
    let converter = converter_script()?;
    let python = env::var_os("BEES_NATIVE_PYTHON")
        .unwrap_or_else(|| std::ffi::OsString::from(
            if cfg!(windows) { "python" } else { "python3" }
        ));
    let saves = save_root.canonicalize()?;
    let source = battles[index].0.canonicalize()?;
    let run: Value = serde_json::from_slice(&fs::read(&source)?)?;
    let title = run["run_id"].as_str().unwrap_or("Native Fleet").to_string();
    let (job, game_id) = new_training_job(&saves)?;
    let (trainer_config, trainer_config_sha256) =
        pin_trainer_config(&job, trainer_source.as_deref())?;
    let prepared = job.join("prepared");
    let model = job.join("model");
    // The game does not own learning state, but it needs the original
    // user-selected immutable invocation to resume through the trainer's
    // existing verified bootstrap/session contracts.
    let recipe = json!({
        "schema_version": 1,
        "kind": "native_game_fleet_training_invocation",
        "source": source,
        "rounds": rounds,
        "episodes_per_world": episodes_per_world,
        "learner_side": learner_side,
        "self_play_refresh_every": refresh_every,
        "early_stop_patience": early_stop_patience,
        "evaluation_every": evaluation_every,
        "trainer_config": trainer_config,
        "trainer_config_sha256": trainer_config_sha256,
        "curriculum_plan": curriculum_plan,
        "curriculum_plan_sha256": curriculum_plan_sha256,
        "worlds": worlds,
        "physics_workers": workers,
        "rollout_processes": rollout_processes,
        "rollout_binary": rollout_binary,
        "rollout_binary_sha256": rollout_binary.as_ref()
            .map(|binary| sha256_file(binary)).transpose()?,
        "hub_worlds": hub_worlds,
        "hub_plan": if use_hub { hub.as_ref().map(|h| h.0.clone()) } else { None },
        "hub_receiver_listen": if use_hub { hub.as_ref().map(|h| h.1.clone()) } else { None },
        "hub_port": if use_hub { hub.as_ref().map(|h| h.2) } else { None },
        "hub_plan_sha256": if use_hub { hub.as_ref().map(|h| h.3.clone()) } else { None },
        "optimizer_device": optimizer_device,
        "inference_device": inference_device,
        "holdout_worlds": 8,
        "evaluation_side": learner_side,
        "game_id": game_id,
        "title": format!("Trained: {title}"),
        "save_root": saves,
    });
    let mut recipe_file = OpenOptions::new().write(true).create_new(true)
        .open(job.join("invocation.json"))?;
    serde_json::to_writer_pretty(&mut recipe_file, &recipe)?;
    use std::io::Write;
    recipe_file.write_all(b"\n")?;
    recipe_file.sync_all()?;
    let stdout_path = job.join("trainer.stdout.log");
    let stderr_path = job.join("trainer.stderr.log");
    let stdout = OpenOptions::new().write(true).create_new(true).open(&stdout_path)?;
    let stderr = OpenOptions::new().write(true).create_new(true).open(&stderr_path)?;
    let mut command = Command::new(python);
    command.arg(&converter).arg(&source).arg(&prepared)
        .arg("--bootstrap-output").arg(&model)
        .arg("--bootstrap-args");
    append_training_arguments(&mut command, &job, &recipe)?;
    let process = command
        .stdin(Stdio::null())
        .stdout(Stdio::from(stdout))
        .stderr(Stdio::from(stderr))
        .spawn()?;
    println!("{}", json!({
        "kind": "native_fleet_training_started",
        "job": job,
        "source": source,
        "pid": process.id(),
        "worlds": worlds,
        "physics_workers": workers,
        "rollout_processes": rollout_processes,
        "rollout_binary": rollout_binary,
        "hub_worlds": hub_worlds,
        "allocation": if use_hub { "experiment_hub" } else { "local" },
        "optimizer_device": optimizer_device,
        "inference_device": inference_device,
        "rounds": rounds,
        "episodes_per_world": episodes_per_world,
        "learner_side": learner_side,
        "self_play_refresh_every": refresh_every,
        "early_stop_patience": early_stop_patience,
        "evaluation_every": evaluation_every,
        "holdout_worlds": 8,
        "stdout": stdout_path,
        "stderr": stderr_path,
        "model": model,
        "game_id": game_id,
    }));
    reap_training_process(process, stderr_path);
    println!("Native fleet training started. Progress is recorded in the training job logs; completed verified checkpoints will appear in the game library.");
    Ok(())
}




fn reap_training_process(mut child: Child, stderr_path: PathBuf) {
    // Command::spawn is intentionally nonblocking for the game. Child::drop
    // does NOT reap a terminated child on Unix, so one lightweight waiter
    // owns each actual process until it finishes. The learner still owns
    // model checkpoints, round locks and all continuation decisions.
    std::thread::spawn(move || {
        use std::io::Write;
        let pid = child.id();
        let outcome = child.wait();
        let record = match outcome {
            Ok(status) => json!({
                "kind": "native_fleet_training_process_exit",
                "pid": pid,
                "success": status.success(),
                "exit_code": status.code(),
            }),
            Err(error) => json!({
                "kind": "native_fleet_training_process_wait_error",
                "pid": pid,
                "error": error.to_string(),
            }),
        };
        // Append only after the child has closed its own redirected stderr;
        // no polling, extra log database or checkpoint owner is introduced.
        if let Ok(mut file) = OpenOptions::new().append(true).open(stderr_path) {
            let _ = writeln!(file, "{record}");
            let _ = file.sync_all();
        }
    });
}

fn recent_job_logs(job: &Path) -> io::Result<(PathBuf, PathBuf, Option<String>)> {
    // Read only the latest completed or active attempt's diagnostic tail.
    // This is display-only: the native trainer's receipts, not log text,
    // remain the authority for accepted training steps and model identity.
    let mut last = None;
    for entry in fs::read_dir(job)? {
        let entry = entry?;
        let filename = entry.file_name().to_string_lossy().into_owned();
        let Some(number) = filename.strip_prefix("trainer-resume-")
            .and_then(|name| name.strip_suffix(".stderr.log")) else { continue };
        if number.len() == 6 && number.bytes().all(|ch| ch.is_ascii_digit()) &&
            last.as_ref().is_none_or(|(old, _): &(String, PathBuf)| number > old.as_str())
        {
            last = Some((number.to_string(), entry.path()));
        }
    }
    let stderr = last.as_ref().map_or_else(
        || job.join("trainer.stderr.log"), |(_, path)| path.clone(),
    );
    let stdout = last.as_ref().map_or_else(
        || job.join("trainer.stdout.log"), |(number, _)|
            job.join(format!("trainer-resume-{number}.stdout.log")),
    );
    let message = if stderr.is_file() {
        let mut file = File::open(&stderr)?;
        let size = file.metadata()?.len();
        file.seek(SeekFrom::Start(size.saturating_sub(16 * 1024)))?;
        let mut tail = Vec::with_capacity((size.min(16 * 1024)) as usize);
        file.take(16 * 1024).read_to_end(&mut tail)?;
        String::from_utf8_lossy(&tail).lines()
            .rev().map(str::trim).find(|line| !line.is_empty())
            .map(|line| line.chars().take(180).collect::<String>())
    } else {
        None
    };
    Ok((stdout, stderr, message))
}

fn latest_child_exit(message: Option<&str>) -> Option<bool> {
    let status = serde_json::from_str::<Value>(message?).ok()?;
    if status["kind"].as_str() != Some("native_fleet_training_process_exit") {
        return None;
    }
    status["success"].as_bool()
}

/// A game-menu read of learner-owned atomic progress. No polling, process
/// supervision, second measurement journal or checkpoint inspection occurs.
pub(super) fn inspect_training_jobs(save_root: &Path) -> Result<(), Box<dyn Error>> {
    let root = save_root.join("training").join("fleet-skirmish");
    if !root.is_dir() {
        println!("No saved native fleet training jobs.");
        return Ok(());
    }
    let mut jobs = fs::read_dir(&root)?
        .filter_map(|entry| entry.ok())
        .filter(|entry| entry.file_type().is_ok_and(|kind| kind.is_dir()) &&
            entry.file_name().to_string_lossy().starts_with("training-"))
        .map(|entry| entry.path())
        .collect::<Vec<_>>();
    jobs.sort_unstable_by(|a, b| b.cmp(a));
    if jobs.is_empty() {
        println!("No saved native fleet training jobs.");
        return Ok(());
    }
    let mut labels = Vec::with_capacity(jobs.len());
    let mut statuses = Vec::with_capacity(jobs.len());
    for job in &jobs {
        let path = job.join("model").join("session").join("session-progress.json");
        let progress = fs::read(&path).ok().and_then(|bytes|
            serde_json::from_slice::<Value>(&bytes).ok()
        );
        let completed = progress.as_ref()
            .and_then(|data| data["completed_rounds"].as_u64());
        let target = progress.as_ref()
            .and_then(|data| data["target_rounds"].as_u64());
        let sps = progress.as_ref()
            .and_then(|data|
                data["useful_training_throughput"]
                    ["world_steps_per_training_wall_second"].as_f64());
        let rounds = match (completed, target) {
            (Some(done), Some(total)) => format!("{done}/{total} optimizer rounds"),
            _ => "Preparing native model / training".to_string(),
        };
        let speed = sps.filter(|value| value.is_finite())
            .map_or(String::new(), |value| format!("  |  {value:.0} measured SPS"));
        labels.push(format!(
            "{}  |  {rounds}{speed}",
            job.file_name().map(|name| name.to_string_lossy().into_owned())
                .unwrap_or_else(|| "training".to_string())
        ));
        let stopped = progress.as_ref()
            .and_then(|row| row["holdout_stopping"]["stop"].as_bool()) == Some(true);
        let (_, _, diagnostic) = recent_job_logs(job)?;
        let exited = latest_child_exit(diagnostic.as_deref());
        let has_playable = job.join("model").join("session")
            .join("latest-playable.json").is_file();
        let finished = completed.zip(target)
            .is_some_and(|(done, total)| done >= total);
        statuses.push(if exited == Some(false) {
            if has_playable {
                "  [Last attempt failed; playable checkpoint retained]"
            } else {
                "  [Training process failed; inspect diagnostics]"
            }
        } else if stopped {
            "  [Stopped at measured holdout plateau]"
        } else if finished {
            "  [Training complete]"
        } else if has_playable {
            "  [Playable checkpoint]"
        } else if diagnostic.is_some() {
            "  [No checkpoint; recent diagnostics]"
        } else {
            "  [No published checkpoint]"
        });
    }
    let rows = labels.iter().zip(&statuses).map(|(title, status)| BattleMenuRow {
        title, status, unlocked: true,
    }).collect::<Vec<_>>();
    let mut selection = 0;
    let Some(index) = select_library(&rows, &mut selection, false)? else {
        return Ok(());
    };
    let job = &jobs[index];
    let (stdout, stderr, diagnostic) = recent_job_logs(job)?;
    let progress = job.join("model").join("session").join("session-progress.json");
    println!("{}", json!({
        "kind": "native_fleet_training_job",
        "job": job,
        "progress": if progress.is_file() {
            Some(serde_json::from_slice::<Value>(&fs::read(&progress)?)?)
        } else {
            None
        },
        "stdout": stdout,
        "stderr": stderr,
        "recent_diagnostic": diagnostic,
        "model": job.join("model"),
    }));
    let snapshot = if progress.is_file() {
        Some(serde_json::from_slice::<Value>(&fs::read(&progress)?)?)
    } else {
        None
    };
    let done = snapshot.as_ref().is_some_and(|state| {
        state["holdout_stopping"]["stop"].as_bool() == Some(true) ||
        state["completed_rounds"].as_u64().zip(state["target_rounds"].as_u64())
            .is_some_and(|(complete, target)| complete >= target)
    });
    let can_resume = training_available() &&
        job.join("invocation.json").is_file() && !done;
    let mut details = vec![
        ("Back to Game Library".to_string(),
         "  [Inspection complete]".to_string(), true),
        ("Resume Training Job".to_string(),
         "  [Original source/checkpoint]".to_string(), can_resume),
    ];
    if let Some(message) = diagnostic {
        details.push((
            format!("Latest training diagnostic: {message}"),
            "  [See current attempt log for context]".to_string(), false,
        ));
    }
    if let Some(measurement) = &snapshot {
        let steps = measurement["total_world_steps"].as_u64().unwrap_or(0);
        let policy_rows = measurement["total_policy_rows"].as_u64().unwrap_or(0);
        details.push((
            format!("Simulation: {steps} World steps / {policy_rows} policy rows"),
            "  [Verified training output]".to_string(), false,
        ));
        if let Some(sps) = measurement["useful_training_throughput"]
            ["world_steps_per_training_wall_second"].as_f64()
            .filter(|number| number.is_finite())
        {
            details.push((
                format!("Useful training SPS: {sps:.1}"),
                "  [Collection + optimizer wall time]".to_string(), false,
            ));
        }
        if let Some(sps) = measurement["world_steps_per_rollout_wall_second"]
            .as_f64().filter(|number| number.is_finite())
        {
            let distributed = measurement["rollout_wall_seconds_scope"].as_str() ==
                Some("sum_of_independent_collector_wall_seconds");
            details.push((
                format!("Native rollout efficiency: {sps:.1} World steps/s"),
                if distributed {
                    "  [Sum of collector wall times; not fleet-wide SPS]"
                } else {
                    "  [Local rollout wall time]"
                }.to_string(), false,
            ));
        }
        if let Some(model) = measurement["sampled_model_inference"].as_object() {
            let latency = model.get("microseconds_per_sampled_policy_row")
                .and_then(Value::as_f64)
                .filter(|number| number.is_finite());
            let rows = model.get("sampled_rows").and_then(Value::as_u64);
            let rounds = model.get("rounds_with_samples").and_then(Value::as_u64);
            if let (Some(us), Some(rows), Some(rounds)) = (latency, rows, rounds) {
                details.push((
                    format!("Sampled model forward: {us:.1} µs/row ({rows} rows, {rounds} rounds)"),
                    "  [Sampled calls; not whole-fleet GPU utilization]".to_string(), false,
                ));
            }
        }
        let physics = measurement["total_parallel_physics_rounds"].as_u64();
        let serialized = measurement["total_serialized_stage_arenas"].as_u64();
        if let (Some(physics), Some(serialized)) = (physics, serialized) {
            if physics > 0 || serialized > 0 {
                details.push((
                    format!("Physics parallel rounds: {physics}  |  Stage-serialized arenas: {serialized}"),
                    "  [Measured native scheduling, not requested CPU allocation]".to_string(), false,
                ));
            }
        }
        let optimize = measurement["sum_round_optimizer_wall_seconds"]
            .as_f64().unwrap_or(0.0);
        let collect = measurement["sum_round_rollout_wall_seconds"]
            .as_f64().unwrap_or(0.0);
        details.push((
            format!("Optimizer: {optimize:.1}s  |  Rollout: {collect:.1}s"),
            "  [Total measured workload]".to_string(), false,
        ));
        if let Some(rate) = measurement["latest_holdout"]["side_win_rate"]
            .as_f64().filter(|number| number.is_finite())
        {
            details.push((
                format!("Latest holdout win rate: {:.1}%", rate * 100.0),
                "  [Independent fixed-seed evaluation]".to_string(), false,
            ));
        }
        if let Some(rate) = measurement["best_evaluated"]["side_win_rate"]
            .as_f64().filter(|number| number.is_finite())
        {
            details.push((
                format!("Best evaluated win rate: {:.1}%", rate * 100.0),
                "  [Verified checkpoint selection]".to_string(), false,
            ));
        }
        if let Some(stages) = measurement["curriculum"]["stages"].as_array() {
            let active_start = measurement["curriculum"]["active_stage_start_round"]
                .as_u64().unwrap_or(1);
            details.push((
                format!("Native curriculum: {} physical Stage(s), active from round {active_start}",
                    stages.len()),
                "  [Shared simulator, one policy checkpoint lineage]".to_string(),
                false,
            ));
            for (index, stage) in stages.iter().enumerate() {
                let start = stage["start_round"].as_u64().unwrap_or(1);
                let completed = stage["completed_rounds"].as_u64().unwrap_or(0);
                let sps = stage["world_steps_per_training_wall_second"].as_f64()
                    .filter(|number| number.is_finite())
                    .map_or("SPS pending".to_string(), |number|
                        format!("{number:.0} measured SPS")
                    );
                let outcome = stage["fixed_reference_side_win_rate"].as_f64()
                    .filter(|number| number.is_finite())
                    .map_or("holdout pending".to_string(), |number|
                        format!("{:.1}% holdout wins", number * 100.0)
                    );
                details.push((
                    format!("Stage {} (round {start}): {completed} rounds | {sps} | {outcome}", index + 1),
                    "  [Verified per-stage source and learning]".to_string(), false,
                ));
            }
        }
        if let Some(count) = measurement["self_play_opponent_promotions"].as_u64() {
            details.push((
                format!("Self-play opponent promotions: {count}"),
                "  [Completed model generations]".to_string(), false,
            ));
        }
        if let Some(distributed) = measurement["distributed_collection_wall_time"]
            .as_object()
        {
            if let Some(count) = distributed.get("distributed_rounds")
                .and_then(Value::as_u64)
            {
                details.push((
                    format!("Distributed collector rounds: {count}"),
                    "  [Experiment Hub measured collection]".to_string(), false,
                ));
            }
        }
    }
    let actions = details.iter().map(|(title, status, enabled)| BattleMenuRow {
        title: title.as_str(), status: status.as_str(), unlocked: *enabled,
    }).collect::<Vec<_>>();
    let mut action = 0;
    if select_library(&actions, &mut action, false)? == Some(1) {
        resume_training_job(job)?;
    }
    Ok(())
}


/// A stopped native game job has exactly one source contract and one model
/// lineage. Let the existing bootstrapper decide whether its finished model
/// may be reused; never copy checkpoints or fabricate a session cursor.
fn resume_training_job(job: &Path) -> Result<(), Box<dyn Error>> {
    let recipe: Value = serde_json::from_slice(
        &fs::read(job.join("invocation.json"))?
    )?;
    if recipe["schema_version"].as_u64() != Some(1) ||
        recipe["kind"].as_str() != Some("native_game_fleet_training_invocation") {
        return Err(invalid("native fleet training job lacks its immutable invocation").into());
    }
    let source = PathBuf::from(recipe["source"].as_str()
        .ok_or_else(|| invalid("native training source is missing"))?);
    let saves = PathBuf::from(recipe["save_root"].as_str()
        .ok_or_else(|| invalid("native training save root is missing"))?);
    let game_id = recipe["game_id"].as_str()
        .ok_or_else(|| invalid("native training game ID is missing"))?;
    let title = recipe["title"].as_str()
        .ok_or_else(|| invalid("native training game title is missing"))?;
    let rounds = recipe["rounds"].as_u64()
        .filter(|n| (1..=99_999).contains(n))
        .ok_or_else(|| invalid("invalid native training round count"))?;
    if !source.is_file() || !saves.is_dir() ||
        !job.starts_with(saves.join("training").join("fleet-skirmish")) {
        return Err(invalid("native training job source or ownership changed").into());
    }
    let model = job.join("model");
    let prepared = job.join("prepared");
    let progress = model.join("session").join("session-progress.json");
    if progress.is_file() {
        let snapshot: Value = serde_json::from_slice(&fs::read(&progress)?)?;
        if snapshot["completed_rounds"].as_u64().is_some_and(|count| count >= rounds) {
            println!("Native fleet training already finished all requested optimizer rounds.");
            return Ok(());
        }
        if snapshot["holdout_stopping"]["stop"].as_bool() == Some(true) {
            println!("Native fleet training already stopped on its approved fixed-holdout learning plateau.");
            return Ok(());
        }
    }
    let converter = converter_script()?;
    let bootstrap = converter.with_file_name("bootstrap_policy.py");
    if !bootstrap.is_file() {
        return Err(invalid("native policy bootstrap script is missing").into());
    }
    let python = env::var_os("BEES_NATIVE_PYTHON")
        .unwrap_or_else(|| std::ffi::OsString::from(
            if cfg!(windows) { "python" } else { "python3" }
        ));
    let mut command = Command::new(python);
    if model.is_dir() {
        if !model.join("receipt.json").is_file() {
            return Err(invalid("native model bootstrap is incomplete; no valid model to resume").into());
        }
        command.arg(&bootstrap).arg(&model).arg("--resume")
            .arg("--scenario").arg("custom")
            .arg("--source-scene").arg(prepared.join("battle.policy.scene.json"))
            .arg("--source-run").arg(prepared.join("battle.policy.run.json"));
    } else if prepared.is_dir() {
        command.arg(&bootstrap).arg(&model)
            .arg("--scenario").arg("custom")
            .arg("--source-scene").arg(prepared.join("battle.policy.scene.json"))
            .arg("--source-run").arg(prepared.join("battle.policy.run.json"));
    } else {
        command.arg(&converter).arg(&source).arg(&prepared)
            .arg("--bootstrap-output").arg(&model)
            .arg("--bootstrap-args");
    }
    append_training_arguments(&mut command, job, &recipe)?;
    let mut pair = None;
    for index in 1..u32::MAX {
        let stdout_path = job.join(format!("trainer-resume-{index:06}.stdout.log"));
        let stderr_path = job.join(format!("trainer-resume-{index:06}.stderr.log"));
        match OpenOptions::new().write(true).create_new(true).open(&stdout_path) {
            Ok(stdout) => {
                let stderr = OpenOptions::new().write(true).create_new(true)
                    .open(&stderr_path)?;
                pair = Some((stdout_path, stderr_path, stdout, stderr));
                break;
            }
            Err(error) if error.kind() == ErrorKind::AlreadyExists => continue,
            Err(error) => return Err(error.into()),
        }
    }
    let (stdout_path, stderr_path, stdout, stderr) =
        pair.ok_or_else(|| invalid("native training resume log identities exhausted"))?;
    let child = command.stdin(Stdio::null())
        .stdout(Stdio::from(stdout))
        .stderr(Stdio::from(stderr))
        .spawn()?;
    println!("{}", json!({
        "kind": "native_fleet_training_resumed",
        "job": job, "pid": child.id(), "model": model,
        "stdout": stdout_path, "stderr": stderr_path,
        "rounds": rounds, "worlds": recipe["worlds"],
        "hub_worlds": recipe["hub_worlds"],
    }));
    reap_training_process(child, stderr_path);
    println!("Existing native fleet training lineage resumed; the learner validates its original bootstrap, receipts and source before proceeding.");
    Ok(())
}
