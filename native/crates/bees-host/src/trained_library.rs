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
use serde_json::Value;
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
    let mut buffer = [0_u8; 1024 * 1024];
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

impl TrainedGame {
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
}

/// Opt-in gallery. The player lists their training receipts in the private
/// save root, including absolute paths to training on another disk. No repo
/// scanning, shared-machine directory walk, or automatic trust of exports.
pub(super) fn load(save_root: &Path) -> Result<Vec<TrainedGame>, Box<dyn Error>> {
    let path = save_root.join("trained-models.json");
    if !path.is_file() { return Ok(Vec::new()); }
    let registration: Registration = serde_json::from_slice(&fs::read(&path)?)?;
    if registration.schema_version != 1 || registration.models.len() > 64 {
        return Err(invalid("native trained-model gallery requires schema 1 and at most 64 models").into());
    }
    let mut seen = HashSet::new();
    let mut games = Vec::with_capacity(registration.models.len());
    for model in registration.models {
        if model.id.is_empty() || model.id.len() > 40 ||
            !model.id.bytes().all(|ch| ch.is_ascii_alphanumeric() || ch == b'-' || ch == b'_') ||
            !seen.insert(model.id.clone()) ||
            model.title.trim().is_empty() || model.title.len() > 120 ||
            model.receipt.as_os_str().is_empty() {
            return Err(invalid("native trained-model registration has an invalid name or duplicate ID").into());
        }
        let receipt = if model.receipt.is_absolute() {
            model.receipt.canonicalize()?
        } else {
            save_root.join(model.receipt).canonicalize()?
        };
        if !receipt.is_file() { return Err(invalid("native trained-model receipt is not a file").into()); }
        let kind: Value = serde_json::from_slice(&fs::read(&receipt)?)?;
        let label = match (kind["schema_version"].as_u64(), kind["kind"].as_str()) {
            (Some(1), Some("native_training_latest_playable")) => "[Latest checkpoint]",
            (Some(1), Some("native_training_best_evaluated")) => "[Best holdout]",
            _ => return Err(invalid("gallery requires latest-playable or best-evaluated native receipts").into()),
        };
        let rounds = kind["completed_rounds"].as_u64()
            .ok_or_else(|| invalid("trained-model receipt omits completed round count"))?;
        games.push(TrainedGame {
            id: format!("trained-{}", model.id),
            title: model.title,
            status: format!("  {label}  {rounds} rounds"),
            receipt,
        });
    }
    Ok(games)
}
