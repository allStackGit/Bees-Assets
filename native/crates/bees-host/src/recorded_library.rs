//! Saved, independently verifiable native battle recordings for the game menu.
//!
//! No second replay implementation: launch the original source configuration
//! with the existing NativePlayerTrace decoder and gameplay runner. A saved
//! battle is selectable only after its durable end record exists. This module
//! never traverses arbitrary user directories or loads training records.

use std::fs::{self, File};
use std::io::{self, Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

use serde_json::Value;

pub(super) enum RecordedSource {
    QuickBattle(PathBuf),
    TrainedBattle(PathBuf),
    CampaignSetup(PathBuf),
}

pub(super) struct RecordedBattle {
    pub title: String,
    pub status: String,
    pub game_id: String,
    pub source: RecordedSource,
    pub trace: PathBuf,
    number: u64,
}

/// The save layout is owned by new_library_battle_output: exactly one
/// battle-NNNNNN directory and one run directory per launched battle.
fn battle_number(name: &str) -> Option<u64> {
    let digits = name.strip_prefix("battle-")?;
    if digits.len() != 6 || !digits.bytes().all(|c| c.is_ascii_digit()) {
        return None;
    }
    digits.parse().ok()
}

/// End-of-stream discovery reads at most the final 4 KiB, even for a trace
/// containing millions of fixed-step entries. Playback still validates the
/// full exact input stream and its source/scene/model hashes at launch.
fn complete_trace(path: &Path) -> io::Result<bool> {
    let metadata = fs::symlink_metadata(path)?;
    if !metadata.file_type().is_file() { return Ok(false); }
    let mut file = File::open(path)?;
    let len = file.metadata()?.len();
    if len < 64 { return Ok(false); }
    let start = len.saturating_sub(4096);
    file.seek(SeekFrom::Start(start))?;
    let mut tail = Vec::new();
    file.read_to_end(&mut tail)?;
    // Read raw bytes because this bounded tail may begin in the middle
    // of a UTF-8 player chat/notice. The complete final JSON line is
    // parsed only after splitting on newline boundaries.
    let Some(line) = tail.rsplit(|&byte| byte == b'\n')
        .find(|line| !line.iter().all(u8::is_ascii_whitespace))
    else { return Ok(false); };
    let Ok(end): Result<Value, _> = serde_json::from_slice(line) else {
        return Ok(false);
    };
    Ok(end["kind"] == "player_input_trace_end" &&
       end["failed_window"].is_null() &&
       end["user_closed"] == false)
}

/// Enumerate one known game's numbered battle folders. Shared by
/// packaged quick battles and frozen trained-model replays.
fn completed_quick_battles(directory: &Path)
    -> io::Result<Vec<(u64, PathBuf, PathBuf)>>
{
    match fs::symlink_metadata(directory) {
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(error) => return Err(error),
        Ok(meta) if !meta.file_type().is_dir() => return Ok(Vec::new()),
        Ok(_) => {},
    }
    let mut completed = Vec::new();
    for entry in fs::read_dir(directory)? {
        let entry = entry?;
        let Some(name) = entry.file_name().to_str().map(str::to_owned)
        else { continue };
        let Some(number) = battle_number(&name) else { continue };
        if !entry.file_type()?.is_dir() { continue; }
        let run_dir = entry.path().join("run");
        if !fs::symlink_metadata(&run_dir)
            .is_ok_and(|meta| meta.file_type().is_dir()) { continue; }
        let trace = run_dir.join("player-inputs.jsonl");
        if complete_trace(&trace).unwrap_or(false) {
            completed.push((number, entry.path(), trace));
        }
    }
    Ok(completed)
}

/// Restrict recordings to game IDs already validated by NativeLibrary and
/// to their directly owned save directories; no recursive filesystem walk.
pub(super) fn list(
    save_root: &Path, games: impl Iterator<Item = (String, String, PathBuf)>,
) -> io::Result<Vec<RecordedBattle>> {
    let mut clips = Vec::new();
    for (game_id, title, config) in games {
        let directory = save_root.join("battles").join(&game_id);
        for (number, _, trace) in completed_quick_battles(&directory)? {
            clips.push(RecordedBattle {
                title: format!("{title}  |  Battle {number:06}"),
                status: "  [Recorded]".to_owned(),
                game_id: game_id.clone(),
                source: RecordedSource::QuickBattle(config.clone()),
                trace,
                number,
            });
        }
    }
    sort_recent(&mut clips);
    Ok(clips)
}

/// Latest/best model receipts can change independently of an archived
/// battle. The game launcher freezes each played model's *run config* in
/// that battle's own folder. Model weights remain referenced by their
/// immutable SHA-verified manifest instead of copied into every save.
pub(super) fn list_trained(
    save_root: &Path, models: impl Iterator<Item = (String, String)>,
) -> io::Result<Vec<RecordedBattle>> {
    let mut clips = Vec::new();
    for (game_id, title) in models {
        let directory = save_root.join("battles").join(&game_id);
        for (number, folder, trace) in completed_quick_battles(&directory)? {
            let frozen = folder.join("replay-source-run.json");
            let Ok(meta) = fs::symlink_metadata(&frozen) else { continue };
            if !meta.file_type().is_file() || meta.len() > 1024 * 1024 {
                continue;
            }
            clips.push(RecordedBattle {
                title: format!("{title}  |  Model battle {number:06}"),
                status: "  [Trained replay]".to_owned(),
                game_id: game_id.clone(),
                source: RecordedSource::TrainedBattle(frozen),
                trace,
                number,
            });
        }
    }
    sort_recent(&mut clips);
    Ok(clips)
}

/// Career battles use the original campaign ledger and therefore cannot
/// replay by loading today's edited fleet. The adjacent immutable setup is
/// the only permitted source of the battle-start fleet projection.
pub(super) fn list_campaigns(
    save_root: &Path, campaigns: impl Iterator<Item = (String, String)>,
) -> io::Result<Vec<RecordedBattle>> {
    let mut clips = Vec::new();
    for (game_id, title) in campaigns {
        let directory = save_root.join("campaigns").join(&game_id);
        match fs::symlink_metadata(&directory) {
            Err(error) if error.kind() == io::ErrorKind::NotFound => continue,
            Err(error) => return Err(error),
            Ok(meta) if !meta.file_type().is_dir() => continue,
            Ok(_) => {},
        }
        for entry in fs::read_dir(&directory)? {
            let entry = entry?;
            let Some(name) = entry.file_name().to_str().map(str::to_owned)
            else { continue };
            let Some(number) = name.strip_prefix("battle-")
                .filter(|suffix| suffix.len() == 4 &&
                    suffix.bytes().all(|ch| ch.is_ascii_digit()))
                .and_then(|suffix| suffix.parse::<u64>().ok())
            else { continue };
            if !entry.file_type()?.is_dir() { continue; }
            let run = entry.path().join("run");
            if !fs::symlink_metadata(&run)
                .is_ok_and(|meta| meta.file_type().is_dir()) { continue; }
            let trace = run.join("player-inputs.jsonl");
            if !complete_trace(&trace).unwrap_or(false) { continue; }

            let setup = entry.path().join("campaign-replay-setup.json");
            let Ok(metadata) = fs::symlink_metadata(&setup) else { continue };
            if !metadata.file_type().is_file() || metadata.len() > 256 * 1024 {
                continue;
            }
            let Ok(bytes) = fs::read(&setup) else { continue };
            let Ok(stored): Result<Value, _> = serde_json::from_slice(&bytes) else {
                continue;
            };
            if stored["schema_version"] != 1 ||
                stored["kind"] != "native_campaign_replay_setup" { continue; }
            let Some(mission_id) = stored["mission_id"].as_str()
                .filter(|name| !name.is_empty() && name.len() <= 80)
            else { continue };
            clips.push(RecordedBattle {
                title: format!("{title}: {mission_id}  |  Battle {number:04}"),
                status: "  [Career replay]".to_owned(),
                // Replays are ordinary game outputs, not campaign receipts.
                game_id: format!("campaign-replay-{game_id}"),
                source: RecordedSource::CampaignSetup(setup),
                trace,
                number,
            });
        }
    }
    sort_recent(&mut clips);
    Ok(clips)
}

pub(super) fn sort_recent(clips: &mut Vec<RecordedBattle>) {
    // Recent launches first, deterministic within one game and across OSes.
    clips.sort_by(|left,right|
        right.number.cmp(&left.number)
            .then_with(|| left.game_id.cmp(&right.game_id)));
    clips.truncate(256);
}
