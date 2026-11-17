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

    /// Preserve the exact game configuration for this completed battle.
    /// Training may republish latest-playable-run.json after the next round;
    /// its manifest/checkpoint are already SHA-pinned immutable artifacts,
    /// so only these small run-config bytes need to be archived here.
    pub fn archive_playable(&self, parent: &Path)
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
        let archived = parent.join("replay-source-run.json");
        let mut output = fs::OpenOptions::new().write(true).create_new(true)
            .open(&archived)?;
        output.write_all(&bytes)?;
        output.sync_all()?;
        Ok(archived)
    }
}

/// Explicit manual registrations and independently published job receipts.
/// Each training job owns one atomic JSON registration under trained-models.d;
/// simultaneous jobs cannot lose each other's entries by rewriting a shared
/// manifest. Only this bounded save-root folder is scanned, never the
/// trainer's workspace or arbitrary assets.
pub(super) fn load(save_root: &Path) -> Result<Vec<TrainedGame>, Box<dyn Error>> {
    let manifest = save_root.join("trained-models.json");
    let mut registrations = if manifest.is_file() {
        let registry: Registration = serde_json::from_slice(&fs::read(&manifest)?)?;
        if registry.schema_version != 1 {
            return Err(invalid("unsupported trained-model gallery registration").into());
        }
        registry.models
    } else {
        Vec::new()
    };
    if registrations.len() > 64 {
        return Err(invalid("native game gallery supports at most 64 models").into());
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
                return Err(invalid("native trained-model registration is not a regular file").into());
            }
            files.push(path);
            if registrations.len() + files.len() > 64 {
                return Err(invalid("native game gallery supports at most 64 models").into());
            }
        }
        // read_dir order is OS/filesystem dependent. Stable IDs must give
        // stable menu ordering on Linux and Windows even when 32 learners
        // publish independently while a player is in a native battle.
        files.sort_unstable();
        for path in files {
            let registration: PublishedModelRegistration =
                serde_json::from_slice(&fs::read(&path)?)?;
            if registration.schema_version != 1 ||
                path.file_stem().and_then(|part| part.to_str()) != Some(registration.id.as_str()) {
                return Err(invalid("published model ID differs from its registration filename").into());
            }
            registrations.push(RegisteredModel {
                id: registration.id, title: registration.title,
                receipt: registration.receipt,
            });
        }
    }

    let mut seen = HashSet::new();
    let mut games = Vec::with_capacity(registrations.len());
    for model in registrations {
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
        games.push(TrainedGame {
            id: format!("trained-{}", model.id),
            title: model.title,
            status: format!("  {label}  {rounds} rounds"),
            receipt,
        });
    }
    Ok(games)
}
