//! Generate a v26 model manifest from the one frozen Rust ABI constant.
//!
//! Usage:
//!   cargo run --manifest-path native/Cargo.toml -p bees-host \
//!     --example policy_manifest -- model-slots.json native-models.json
//!
//! model-slots.json contains just an array of real ONNX/TorchScript model
//! slot records. The generated output supplies the exact policy signature;
//! no caller should duplicate or hand-edit the 1,000+ character ABI string.

use std::collections::HashSet;
use std::env;
use std::error::Error;
use std::fs::{self, File};
use std::io::{Error as IoError, ErrorKind};
use std::path::PathBuf;

use bees_sim::policy::{ABI_VERSION, POLICY_SIGNATURE};
use serde_json::{json, Value};

fn invalid(reason: &'static str) -> IoError {
    IoError::new(ErrorKind::InvalidInput, reason)
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
    }
    let manifest = json!({
        "format": "bees-native-model-v1",
        "abi_version": ABI_VERSION,
        "policy_signature": POLICY_SIGNATURE,
        "models": models,
    });
    let bytes = serde_json::to_vec_pretty(&manifest)?;
    fs::write(output, bytes)?;
    Ok(())
}
