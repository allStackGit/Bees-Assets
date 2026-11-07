//! Generate a v26 model manifest from the one frozen Rust ABI constant.
//!
//! Usage:
//!   cargo run --manifest-path native/Cargo.toml -p bees-host \
//!     --example policy_manifest -- model-slots.json native-models.json
//!
//! model-slots.json contains just an array of real ONNX/TorchScript model
//! slot records. The generated output supplies the exact policy signature;
//! no caller should duplicate or hand-edit the 1,000+ character ABI string.
//! Every configured model is SHA-256 hashed, and the worker checks that
//! immutable identity before accepting inference rows.

use std::collections::HashSet;
use std::env;
use std::error::Error;
use std::fs::{self, File};
use std::io::{Error as IoError, ErrorKind, Read};
use std::path::{Path, PathBuf};

use bees_sim::policy::{ABI_VERSION, POLICY_SIGNATURE};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};

fn invalid(reason: &'static str) -> IoError {
    IoError::new(ErrorKind::InvalidInput, reason)
}

/// Read model bytes only at manifest-generation time. The running native
/// game/worker never hashes weights inside its inference or physics loop.
fn sha256_file(path: &Path) -> Result<String, IoError> {
    let mut file = File::open(path)?;
    let mut digest = Sha256::new();
    let mut chunk = [0_u8; 64 * 1024];
    loop {
        let read = file.read(&mut chunk)?;
        if read == 0 { break; }
        digest.update(&chunk[..read]);
    }
    Ok(format!("{:x}", digest.finalize()))
}

fn main() -> Result<(), Box<dyn Error>> {
    let mut args = env::args_os().skip(1);
    let source = PathBuf::from(args.next()
        .ok_or_else(|| invalid("expected model slot JSON input path"))?);
    let output = PathBuf::from(args.next()
        .ok_or_else(|| invalid("expected generated model manifest path"))?);
    if args.next().is_some() || source == output {
        return Err(invalid("usage: policy_manifest model-slots.json manifest.json").into());
    }
    let entries: Value = serde_json::from_reader(File::open(&source)?)?;
    let models = entries.as_array()
        .filter(|models| !models.is_empty())
        .ok_or_else(|| invalid("model slots must be a nonempty JSON array"))?;
    let mut used = HashSet::with_capacity(models.len());
    // Model paths are resolved relative to the generated manifest by the
    // resident inference process, so hash against that same final location.
    let output_dir = output.parent().unwrap_or_else(|| Path::new("."));
    let mut verified_models = Vec::with_capacity(models.len());
    for item in models {
        let entry = item.as_object()
            .ok_or_else(|| invalid("each model entry must be an object"))?;
        let slot = entry.get("slot").and_then(Value::as_u64)
            .filter(|value| *value <= u32::MAX as u64)
            .ok_or_else(|| invalid("each model must have a uint32 slot"))?;
        if !used.insert(slot) {
            return Err(invalid("duplicate model slot").into());
        }
        if !entry.get("path").is_some_and(|value|
            value.as_str().is_some_and(|value| !value.is_empty())
        ) {
            return Err(invalid("each model needs a real artifact path").into());
        }
        if !entry.get("backend").is_some_and(|value|
            matches!(value.as_str(), Some("onnx" | "torchscript"))
        ) {
            return Err(invalid("unsupported model backend").into());
        }
        let path = entry["path"].as_str().expect("validated nonempty path");
        let actual_hash = sha256_file(&output_dir.join(path))?;
        if entry.get("sha256").is_some_and(|expected|
            expected.as_str() != Some(actual_hash.as_str())
        ) {
            return Err(invalid("supplied model digest does not match artifact").into());
        }
        let mut verified = entry.clone();
        verified.insert("sha256".to_owned(), Value::String(actual_hash));
        verified_models.push(Value::Object(verified));
    }
    let manifest = json!({
        "format": "bees-native-model-v1",
        "abi_version": ABI_VERSION,
        "policy_signature": POLICY_SIGNATURE,
        "models": verified_models,
    });
    let bytes = serde_json::to_vec_pretty(&manifest)?;
    fs::write(output, bytes)?;
    Ok(())
}
