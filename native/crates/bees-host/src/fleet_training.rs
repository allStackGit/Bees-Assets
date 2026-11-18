//! A native-game entry point into the existing Python MA-POCA pipeline.
//!
//! Fleet battles and checkpoint gallery remain owned by the native game;
//! bootstrap, optimizer, holdout and publication remain solely owned by the
//! Python trainer. A job gets one private output tree and one process.

use std::env;
use std::error::Error;
use std::fs::{self, File, OpenOptions};
use std::io::{self, ErrorKind};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

use bees_host::presentation::{select_library, BattleMenuRow};
use serde_json::{json, Value};

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
            ["survival", "extraction", "reinforcements"].iter()
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
        println!("No saved elimination fleet skirmishes available for native training.");
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
    let Some(worlds) = choose_count("Parallel native arenas", &[1, 2, 4, 8, 16, 32, 64])?
        else { return Ok(()); };
    let Some(workers) = choose_count("Native physics CPU workers", &[1, 2, 4, 8, 16, 32])?
        else { return Ok(()); };
    let device_rows = [
        BattleMenuRow { title: "CPU", status: "  [Optimizer]", unlocked: true },
        BattleMenuRow { title: "PyTorch CUDA / ROCm GPU", status: "  [Optimizer]", unlocked: true },
    ];
    let mut device_selection = 0;
    let Some(device) = select_library(&device_rows, &mut device_selection, false)?
        else { return Ok(()); };

    // Resolve and validate before allocating a permanent job name. GPU
    // availability and the Python environment are validated by the existing
    // bootstrap and learner, not guessed from operating-system strings.
    let converter = converter_script()?;
    let python = env::var_os("BEES_NATIVE_PYTHON")
        .unwrap_or_else(|| if cfg!(windows) { "python" } else { "python3" }.into());
    let saves = save_root.canonicalize()?;
    let source = battles[index].0.canonicalize()?;
    let run: Value = serde_json::from_slice(&fs::read(&source)?)?;
    let title = run["run_id"].as_str().unwrap_or("Native Fleet").to_string();
    let (job, game_id) = new_training_job(&saves)?;
    let prepared = job.join("prepared");
    let model = job.join("model");
    let stdout_path = job.join("trainer.stdout.log");
    let stderr_path = job.join("trainer.stderr.log");
    let stdout = OpenOptions::new().write(true).create_new(true).open(&stdout_path)?;
    let stderr = OpenOptions::new().write(true).create_new(true).open(&stderr_path)?;
    let process = Command::new(python)
        .arg(&converter).arg(&source).arg(&prepared)
        .arg("--bootstrap-output").arg(&model)
        .arg("--bootstrap-args")
        .args(["--train-rounds", &rounds.to_string()])
        .args(["--rollout-worlds", &worlds.to_string()])
        .args(["--physics-workers", &workers.to_string()])
        .args(["--optimizer-device", if device == 0 { "cpu" } else { "cuda" }])
        .args(["--holdout-worlds", "8", "--evaluation-side", "1"])
        .args(["--library-save-root", saves.to_str()
            .ok_or_else(|| invalid("save directory requires valid native Unicode"))?])
        .args(["--library-game-id", &game_id])
        .args(["--library-game-title", &format!("Trained: {title}")])
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
        "optimizer_device": if device == 0 { "cpu" } else { "cuda" },
        "rounds": rounds,
        "holdout_worlds": 8,
        "stdout": stdout_path,
        "stderr": stderr_path,
        "model": model,
        "game_id": game_id,
    }));
    println!("Native fleet training started. Progress is recorded in the training job logs; completed verified checkpoints will appear in the game library.");
    Ok(())
}
