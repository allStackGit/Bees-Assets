//! Bounded JSON encoding for the deployed Hive Mind WebSocket protocol.
//!
//! Production server admission limits are not a reason to build large
//! unbounded request objects in the native client. This codec validates
//! the framing before handing a message to per-connection request owners.
//! TLS/WebSocket authentication remains the transport adapter's job.

use crate::protocol::{
    GetStrategyRequest, GetStrategyResponse, GetTargetingRequest,
    GetTargetingResponse, StoreCommandsRequest, StoreCommandsResponse,
    MAX_SAFE_REQUEST_HASH, STORE_COMMANDS_TYPE, STRATEGY_REQUEST_TYPE,
    TARGETING_REQUEST_TYPE, LOCAL_MOVE_TO_POINT_NAME,
};

/// BeesServer security admission caps production messages at 8 MiB.
pub const MAX_HIVEMIND_MESSAGE_BYTES: usize = 8 * 1024 * 1024;

#[derive(Debug)]
pub enum HiveMindWireError {
    TooLarge,
    InvalidJson(serde_json::Error),
    InvalidMessageType,
    InvalidRequest,
}

impl From<serde_json::Error> for HiveMindWireError {
    fn from(value: serde_json::Error) -> Self {
        Self::InvalidJson(value)
    }
}

/// Three HiveMind reply types that are already source-ported. The same
/// authenticated WebSocket also carries Level setup, settings and other
/// replies; these are deliberately left to their existing host handlers.
#[derive(Debug)]
pub enum HiveMindServerResponse {
    Strategy(GetStrategyResponse),
    Targeting(GetTargetingResponse),
    StoreCommands(StoreCommandsResponse),
}

#[derive(serde::Deserialize)]
struct ResponseType {
    #[serde(rename = "Type")]
    message_type: String,
}

/// Decode one bounded, authenticated HiveMind reply. Unknown valid JSON
/// message types return None so the caller can hand setup/reconnect/user
/// data messages to their appropriate owner. Malformed known messages are
/// errors, not unverified or half-applied strategy selections.
pub fn decode_hivemind_response_frame(
    data: &[u8],
) -> Result<Option<HiveMindServerResponse>, HiveMindWireError> {
    within_limit(data)?;
    let header: ResponseType = serde_json::from_slice(data)?;
    match header.message_type.as_str() {
        STRATEGY_REQUEST_TYPE => Ok(Some(HiveMindServerResponse::Strategy(
            decode_strategy_response(data)?,
        ))),
        TARGETING_REQUEST_TYPE => Ok(Some(HiveMindServerResponse::Targeting(
            decode_targeting_response(data)?,
        ))),
        STORE_COMMANDS_TYPE => Ok(Some(HiveMindServerResponse::StoreCommands(
            decode_store_commands_response(data)?,
        ))),
        _ => Ok(None),
    }
}

fn within_limit(bytes: &[u8]) -> Result<(), HiveMindWireError> {
    if bytes.is_empty() || bytes.len() > MAX_HIVEMIND_MESSAGE_BYTES {
        return Err(HiveMindWireError::TooLarge);
    }
    Ok(())
}

fn valid_hash(hash: i64) -> bool {
    (1..=MAX_SAFE_REQUEST_HASH).contains(&hash)
}

pub fn encode_strategy_request(
    request: &GetStrategyRequest,
) -> Result<Vec<u8>, HiveMindWireError> {
    if request.message_type != STRATEGY_REQUEST_TYPE ||
        !valid_hash(request.hash) ||
        request.matchup.is_empty() || request.shooting_matchup.is_empty() ||
        !request.banned_strats.iter().any(|s| s == LOCAL_MOVE_TO_POINT_NAME) {
        return Err(HiveMindWireError::InvalidRequest);
    }
    let encoded = serde_json::to_vec(request)?;
    within_limit(&encoded)?;
    Ok(encoded)
}

pub fn decode_strategy_response(
    data: &[u8],
) -> Result<GetStrategyResponse, HiveMindWireError> {
    within_limit(data)?;
    let response: GetStrategyResponse = serde_json::from_slice(data)?;
    if response.message_type != STRATEGY_REQUEST_TYPE ||
        !valid_hash(response.hash) {
        return Err(HiveMindWireError::InvalidMessageType);
    }
    Ok(response)
}

pub fn encode_targeting_request(
    request: &GetTargetingRequest,
) -> Result<Vec<u8>, HiveMindWireError> {
    if request.message_type != TARGETING_REQUEST_TYPE ||
        !valid_hash(request.hash) || request.ships.is_empty() {
        return Err(HiveMindWireError::InvalidRequest);
    }
    let encoded = serde_json::to_vec(request)?;
    within_limit(&encoded)?;
    Ok(encoded)
}

pub fn decode_targeting_response(
    data: &[u8],
) -> Result<GetTargetingResponse, HiveMindWireError> {
    within_limit(data)?;
    let response: GetTargetingResponse = serde_json::from_slice(data)?;
    if response.message_type != TARGETING_REQUEST_TYPE ||
        !valid_hash(response.hash) {
        return Err(HiveMindWireError::InvalidMessageType);
    }
    Ok(response)
}

pub fn encode_store_commands(
    request: &StoreCommandsRequest,
) -> Result<Vec<u8>, HiveMindWireError> {
    if request.message_type != STORE_COMMANDS_TYPE || !valid_hash(request.hash) {
        return Err(HiveMindWireError::InvalidRequest);
    }
    let encoded = serde_json::to_vec(request)?;
    within_limit(&encoded)?;
    Ok(encoded)
}

pub fn decode_store_commands_response(
    data: &[u8],
) -> Result<StoreCommandsResponse, HiveMindWireError> {
    within_limit(data)?;
    let response: StoreCommandsResponse = serde_json::from_slice(data)?;
    if response.message_type != STORE_COMMANDS_TYPE ||
        !valid_hash(response.hash) {
        return Err(HiveMindWireError::InvalidMessageType);
    }
    Ok(response)
}
