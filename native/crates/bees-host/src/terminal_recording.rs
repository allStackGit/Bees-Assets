//! Complete a replay after its immutable native run receipt was committed.
//!
//! The source World has already finished. Replaying or re-awarding the match
//! merely to publish its missing trace footer would create a second career
//! transaction. The committed run_final and the original host checkpoint
//! together authorize a final footer ONLY when the complete recorded
//! physical/gameplay signatures and input counts are already durable.

use std::collections::HashMap;
use std::error::Error;
use std::fs::{self, File, OpenOptions};
use std::io::{self, BufRead, BufReader, Read, Seek, SeekFrom, Write};
use std::path::Path;

use bees_host::checkpoint_file::{CheckpointFileError, NativeHostCheckpointFile};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};

use super::{contextual, invalid, NativeBattleRecoveryCursor, NativeRecordedPrefixHasher};

fn verified_policy_digest(path: &Path) -> io::Result<String> {
    let metadata = fs::symlink_metadata(path)?;
    if !metadata.file_type().is_file() {
        return Err(invalid("committed native policy actions are not a regular file"));
    }
    let mut file = File::open(path)?;
    let mut magic = [0_u8; 8];
    file.read_exact(&mut magic)?;
    if &magic != b"BEESPLY1" {
        return Err(invalid("committed native policy action stream has an invalid header"));
    }
    file.seek(SeekFrom::Start(0))?;
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let n = file.read(&mut buffer)?;
        if n == 0 { break; }
        digest.update(&buffer[..n]);
    }
    Ok(format!("{:x}", digest.finalize()))
}

/// Returns true when a completed immutable run receipt exists, whether or
/// not this particular battle recorded a replay. A missing summary remains
/// eligible for genuine World recovery, never terminal reconstruction.
pub(super) fn complete_committed_recording(run: &Path)
    -> Result<bool, Box<dyn Error>>
{
    let summary_path = run.join("rollout-summary.json");
    let metadata = match fs::symlink_metadata(&summary_path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(false),
        Err(error) => return Err(error.into()),
    };
    if !metadata.file_type().is_file() || metadata.len() > 16 * 1024 * 1024 {
        return Err(invalid("native completed receipt is not an immutable regular file").into());
    }
    let summary: Value = serde_json::from_reader(File::open(&summary_path)?)?;
    if summary["kind"] != "run_final" {
        return Err(invalid("native terminal receipt is not a run_final").into());
    }
    // Failed battles and cleanup failures have their own immutable run_final
    // receipt, but NEVER a successful replay footer. A prior failed battle
    // must not prevent a new game-library launch or turn into a recovered
    // victory. Only an absent summary authorizes resuming its actual World.
    if !summary["failed_window"].is_null() ||
        summary["cleanup_errors"].as_array().is_some_and(|errors| !errors.is_empty())
    {
        return Ok(true);
    }
    if summary["cleanup_errors"] != json!([]) {
        return Err(invalid("native terminal receipt has malformed cleanup errors").into());
    }
    // A player explicitly quitting, or a finished WATCH replay, is an
    // immutable completed session but never a new eligible recording.
    // Do not block its next menu launch or manufacture a success footer.
    if summary["user_closed"] == true || summary["player_input_playback"] == true {
        return Ok(true);
    }
    if summary["user_closed"] != false || summary["player_input_playback"] != false {
        return Err(invalid("native terminal receipt has ambiguous replay eligibility").into());
    }

    let trace_path = run.join("player-inputs.jsonl");
    let trace_meta = match fs::symlink_metadata(&trace_path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(true),
        Err(error) => return Err(error.into()),
    };
    if !trace_meta.file_type().is_file() {
        return Err(invalid("native terminal recording is not a regular file").into());
    }
    let final_round = summary["rounds"].as_u64()
        .ok_or_else(|| invalid("native terminal receipt lacks a final round"))?;
    let final_worlds = summary["final_physical_worlds"].as_array()
        .filter(|worlds| !worlds.is_empty())
        .ok_or_else(|| invalid("native terminal receipt lacks physical World signatures"))?;
    let gameplay_worlds = summary["final_gameplay_worlds"].as_array()
        .filter(|worlds| worlds.len() == final_worlds.len())
        .ok_or_else(|| invalid("native terminal receipt lacks matching gameplay signatures"))?;

    // The full transcript is checked only on rare terminal recovery.
    // Ordinary completed battles are detected in a bounded last-page read.
    {
        let mut file = File::open(&trace_path)?;
        let length = file.metadata()?.len();
        if length >= 64 {
            file.seek(SeekFrom::Start(length.saturating_sub(4096)))?;
            let mut tail = Vec::new();
            file.read_to_end(&mut tail)?;
            if let Some(row) = tail.rsplit(|&byte| byte == b'\n')
                .find(|row| !row.iter().all(u8::is_ascii_whitespace))
            {
                if let Ok(last) = serde_json::from_slice::<Value>(row) {
                    if last["kind"] == "player_input_trace_end" {
                        if last["rounds"].as_u64() == Some(final_round) &&
                            last["recorded_inputs"] == summary["recorded_player_inputs"] &&
                            last["user_closed"] == false &&
                            last["failed_window"].is_null()
                        {
                            return Ok(true);
                        }
                        return Err(invalid("terminal replay footer disagrees with committed receipt").into());
                    }
                }
            }
        }
    }

    // No mutable gameplay owner is created. Acquire the SAME host writer
    // lock that protected the original World before checking its cursor or
    // touching recording bytes; a still-live host may never be finalized.
    let checkpoint = match NativeHostCheckpointFile::open(
        run.join("native-host-checkpoint.json"),
    ) {
        Ok(file) => file,
        // The original process may have committed the receipt but still
        // own the socket/recorder during its short terminal drain. Leave
        // its bytes untouched; the normal next menu refresh can discover
        // the footer once that owner releases the host checkpoint lock.
        Err(CheckpointFileError::Io(error))
            if error.kind() == io::ErrorKind::WouldBlock => return Ok(true),
        Err(error) => return Err(contextual("terminal replay host writer lease", error).into()),
    };
    let snapshot = checkpoint.load()
        .map_err(|error| contextual("terminal replay original World", error))?
        .ok_or_else(||
        invalid("completed replay lacks the original durable World checkpoint"))?;
    let cursor: NativeBattleRecoveryCursor =
        serde_json::from_value(snapshot.launcher().clone())?;
    if cursor.run_id != summary["run_id"].as_str().unwrap_or("") ||
        cursor.rounds != final_round
    {
        return Err(invalid("terminal receipt differs from the original checkpoint owner").into());
    }
    let recording = cursor.recording.as_ref().ok_or_else(||
        invalid("terminal World has no committed recording prefix"))?;
    let expected = &recording.input_trace;
    let observed = NativeRecordedPrefixHasher::new().capture(&trace_path, expected.bytes)?;
    if observed.sha256 != expected.sha256 {
        return Err(invalid("native terminal recording changed before its committed World boundary").into());
    }
    if let Some(prefix) = &recording.policy_actions {
        let path = run.join("policy-actions.bin");
        let observed = NativeRecordedPrefixHasher::new().capture(&path, prefix.bytes)?;
        if observed.sha256 != prefix.sha256 {
            return Err(invalid("committed policy recording prefix differs from recovered World").into());
        }
    }

    let mut input_count = 0_u64;
    let mut checkpoint_count = 0_u64;
    let mut network_count = 0_u64;
    let mut snapshots = HashMap::<u64, Value>::new();
    let mut last_kind = String::new();
    let mut terminal_end: Option<Value> = None;
    let mut valid_end = 0_u64;
    let mut reader = BufReader::new(File::open(&trace_path)?);
    let mut line = String::new();
    loop {
        line.clear();
        let bytes = reader.read_line(&mut line)?;
        if bytes == 0 { break; }
        if !line.ends_with('\n') {
            // A crash may interrupt only the final footer after the
            // immutable receipt. Never accept a partial input record.
            break;
        }
        let row: Value = serde_json::from_str(&line)?;
        if terminal_end.is_some() {
            return Err(invalid("native replay has records after its terminal footer").into());
        }
        let kind = row["kind"].as_str().ok_or_else(||
            invalid("native recording has an untyped line"))?;
        match kind {
            "player_input_trace" if valid_end == 0 => {
                if row["run_id"] != summary["run_id"] ||
                    row["run_config_sha256"].as_str() != Some(cursor.config_sha256.as_str()) ||
                    row["gameplay_config_sha256"].as_str() != Some(cursor.gameplay_sha256.as_str()) ||
                    row["policy_manifest_sha256"] != summary["policy_manifest_sha256"]
                {
                    return Err(invalid("native replay header differs from committed run identity").into());
                }
            }
            "player_input" => input_count += 1,
            "hivemind_setup" | "hivemind_frame" => network_count += 1,
            "physical_state_checkpoint" => {
                checkpoint_count += 1;
                if row["round"].as_u64() == Some(final_round) {
                    let id = row["world_id"].as_u64().ok_or_else(||
                        invalid("native replay World signature lacks identity"))?;
                    snapshots.insert(id, row.clone());
                }
            }
            "player_input_trace_end" => terminal_end = Some(row.clone()),
            _ => return Err(invalid("native replay contains a foreign or misplaced record").into()),
        }
        valid_end += bytes as u64;
        last_kind = kind.to_owned();
    }
    if let Some(end) = terminal_end {
        if end["rounds"].as_u64() == Some(final_round) &&
            end["recorded_inputs"].as_u64() == Some(input_count) &&
            end["user_closed"] == false && end["failed_window"].is_null()
        {
            return Ok(true);
        }
        return Err(invalid("native completed replay footer is inconsistent").into());
    }
    if last_kind != "physical_state_checkpoint" ||
        input_count != summary["recorded_player_inputs"].as_u64()
            .ok_or_else(|| invalid("native receipt lacks recorded input count"))? ||
        checkpoint_count != summary["recorded_physical_checkpoints"].as_u64()
            .ok_or_else(|| invalid("native receipt lacks physical checkpoint count"))? ||
        summary["network"]["recorded_hivemind_events"].as_u64()
            .is_some_and(|count| count != network_count) ||
        snapshots.len() != final_worlds.len()
    {
        return Err(invalid("native final recording is missing its authenticated execution prefix").into());
    }
    for world in final_worlds {
        let id = world["world_id"].as_u64().ok_or_else(||
            invalid("native receipt has an invalid World ID"))?;
        let row = snapshots.get(&id).ok_or_else(||
            invalid("native replay lacks its terminal physical World"))?;
        let gameplay = gameplay_worlds.iter().find(|entry|
            entry["world_id"].as_u64() == Some(id)).ok_or_else(||
                invalid("native receipt lacks its matching gameplay World"))?;
        if row["tick"] != world["tick"] ||
            row["episode_revision"] != world["episode_revision"] ||
            row["sha256"] != world["sha256"] ||
            row["gameplay_sha256"] != gameplay["sha256"]
        {
            return Err(invalid("native replay final World differs from the immutable receipt").into());
        }
    }

    let policy = if summary["policy_manifest_sha256"].is_null() {
        if recording.policy_actions.is_some() {
            return Err(invalid("model-free terminal replay has recorded policy actions").into());
        }
        None
    } else {
        if recording.policy_actions.is_none() {
            return Err(invalid("terminal policy replay lacks committed actions").into());
        }
        Some(verified_policy_digest(&run.join("policy-actions.bin"))?)
    };
    let mut file = OpenOptions::new().read(true).write(true).open(&trace_path)?;
    if file.metadata()?.len() != valid_end {
        // Only the partial terminal footer may follow a fully validated
        // physical checkpoint; all earlier bytes are immutable.
        file.set_len(valid_end)?;
    }
    file.seek(SeekFrom::End(0))?;
    writeln!(file, "{}", json!({
        "kind": "player_input_trace_end",
        "rounds": final_round,
        "recorded_inputs": input_count,
        "user_closed": false,
        "failed_window": null,
        "policy_actions_sha256": policy,
    }))?;
    file.sync_all()?;
    println!("{}", json!({
        "kind": "native_terminal_recording_recovered",
        "run_id": summary["run_id"],
        "rounds": final_round,
        "recorded_inputs": input_count,
    }));
    Ok(true)
}
