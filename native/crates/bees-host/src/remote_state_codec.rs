//! Optional lossless compression of the existing *filtered* multiplayer
//! world_state JSON. This is a presentation transport only: the authoritative
//! simulation, input ordering, fog privileges and recovery protocol are
//! unchanged. Old peers never negotiate this encoding.
use std::io::{self, ErrorKind};

use base64::{engine::general_purpose::STANDARD, Engine as _};
use serde_json::{json, Value};

const MIN_COMPRESS_BYTES: usize = 8 * 1024;
const MAX_WORLD_BYTES: usize = 4 * 1024 * 1024;
const MIN_SAVINGS_BYTES: usize = 256;

fn invalid(description: &'static str) -> io::Error {
    io::Error::new(ErrorKind::InvalidData, description)
}

/// Compress only large already-authorized source projections, and only if the
/// complete newline-delimited JSON wrapper is actually smaller on the wire.
/// One result can be shared between cooperative clients with the same
/// (faction, spectator) visibility; reliable messages are never compressed.
pub fn compress_world_state(raw: &[u8]) -> Option<Vec<u8>> {
    if !(MIN_COMPRESS_BYTES..MAX_WORLD_BYTES).contains(&raw.len()) {
        return None;
    }
    let compressed = lz4_flex::compress_prepend_size(raw);
    let payload = STANDARD.encode(compressed);
    let encoded = serde_json::to_vec(&json!({
        "kind": "world_state_lz4",
        "payload": payload,
    })).ok()?;
    (encoded.len() + MIN_SAVINGS_BYTES < raw.len()).then_some(encoded)
}

/// Decode at most one bounded frame. Validate the claimed size BEFORE LZ4
/// allocation; neither untrusted network input nor decompressed data can
/// exceed the original per-frame JSON memory cap. Never accept nested
/// compressed messages or a non-world_state payload.
pub fn decompress_world_state(frame: &Value) -> io::Result<(Value, usize)> {
    let fields = frame.as_object().ok_or_else(|| invalid("invalid compressed native world state"))?;
    if fields.len() != 2 || frame["kind"] != "world_state_lz4" {
        return Err(invalid("unexpected compressed native state envelope"));
    }
    let encoded = frame["payload"].as_str()
        .ok_or_else(|| invalid("compressed native state has no payload"))?;
    if encoded.len() > MAX_WORLD_BYTES {
        return Err(invalid("compressed native state exceeds transport limit"));
    }
    let compressed = STANDARD.decode(encoded)
        .map_err(|_| invalid("invalid native state base64"))?;
    let prefix: [u8; 4] = compressed.get(..4)
        .ok_or_else(|| invalid("compressed native state is truncated"))?
        .try_into().expect("validated four-byte LZ4 prefix");
    let expected = u32::from_le_bytes(prefix) as usize;
    if !(MIN_COMPRESS_BYTES..MAX_WORLD_BYTES).contains(&expected) {
        return Err(invalid("native decompressed state length is out of bounds"));
    }
    let raw = lz4_flex::decompress_size_prepended(&compressed)
        .map_err(|_| invalid("native world state decompression failed"))?;
    if raw.len() != expected {
        return Err(invalid("native world state decompressed to an unexpected length"));
    }
    let state: Value = serde_json::from_slice(&raw)
        .map_err(|_| invalid("invalid decompressed native state JSON"))?;
    if state["kind"] != "world_state" {
        return Err(invalid("compressed native payload is not a world state"));
    }
    Ok((state, raw.len()))
}
