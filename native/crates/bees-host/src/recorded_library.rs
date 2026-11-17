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

pub(super) struct RecordedBattle {
    pub title: String,
    pub status: String,
    pub game_id: String,
    pub source_config: PathBuf,
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

/// Restrict recordings to game IDs already validated by NativeLibrary and
/// to their directly owned save directories; no recursive filesystem walk.
pub(super) fn list(
    save_root: &Path, games: impl Iterator<Item = (String, String, PathBuf)>,
) -> io::Result<Vec<RecordedBattle>> {
    let mut clips = Vec::new();
    for (game_id, title, config) in games {
        let directory = save_root.join("battles").join(&game_id);
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
            let Some(number) = battle_number(&name) else { continue };
            if !entry.file_type()?.is_dir() { continue; }
            let run_dir = entry.path().join("run");
            if !fs::symlink_metadata(&run_dir)
                .is_ok_and(|meta| meta.file_type().is_dir()) { continue; }
            let trace = run_dir.join("player-inputs.jsonl");
            if !complete_trace(&trace).unwrap_or(false) { continue; }
            clips.push(RecordedBattle {
                title: format!("{title}  |  Battle {number:06}"),
                status: "  [Recorded]".to_owned(),
                game_id: game_id.clone(),
                source_config: config.clone(),
                trace,
                number,
            });
        }
    }
    // Recent launches first, deterministic within one game and across OSes.
    clips.sort_by(|left,right|
        right.number.cmp(&left.number)
            .then_with(|| left.game_id.cmp(&right.game_id)));
    clips.truncate(256);
    Ok(clips)
}
