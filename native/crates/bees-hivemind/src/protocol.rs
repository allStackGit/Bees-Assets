//! Deployed Unity <-> BeesServer Hive Mind protocol, without network code.
//!
//! This crate deliberately preserves server database strategy IDs and JSON
//! property names. The IDs below are NOT the numeric values of Unity's
//! ConfigData.CommandTypes enum. Changing them would corrupt historical
//! learning attribution. The game and headless simulator do not depend on
//! WebSocket, authentication, SQL, or a server event loop.

use std::collections::HashSet;
use std::fmt;

use serde::de::{self, Visitor};
use serde::{Deserialize, Deserializer, Serialize, Serializer};

pub const STRATEGY_REQUEST_TYPE: &str = "get-strategy";
pub const TARGETING_REQUEST_TYPE: &str = "get-matchup-strategy";
pub const STORE_COMMANDS_TYPE: &str = "store-commands";
/// Unity Utilities.Unique53Hash deliberately stays exactly representable
/// in JavaScript Number, unlike generic 64-bit hash generators.
pub const MAX_SAFE_REQUEST_HASH: i64 = (1_i64 << 53) - 1;
pub const REQUEST_CLIENT_PREFIX_BITS: u32 = 21;

/// One allocator per native client process, shared by ALL request types.
/// The host supplies a securely generated 21-bit client prefix. A 32-bit
/// monotonically increasing counter matches Unity's Unique53Hash scheme.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct RequestHashAllocator {
    client_prefix: u32,
    counter: u32,
}

impl RequestHashAllocator {
    pub fn new(client_prefix: u32) -> Result<Self, HiveMindProtocolError> {
        if client_prefix >= (1 << REQUEST_CLIENT_PREFIX_BITS) {
            return Err(HiveMindProtocolError::InvalidRequest);
        }
        Ok(Self { client_prefix, counter: 0 })
    }

    /// Validate an allocator restored from durable connection state.
    /// A restart may continue its original 21-bit client prefix only
    /// when its full counter and pending StoreCommands ledger were
    /// persisted before the corresponding messages were sent.
    pub fn validate(&self) -> Result<(), HiveMindProtocolError> {
        if self.client_prefix >= (1 << REQUEST_CLIENT_PREFIX_BITS) {
            return Err(HiveMindProtocolError::InvalidRequest);
        }
        Ok(())
    }

    /// True only for a request ID already allocated by THIS persisted
    /// client prefix/counter pair, including a previous connection using
    /// the same durable allocator. No server credentials are involved.
    pub fn has_issued(&self, hash: i64) -> bool {
        if hash <= 0 || hash > MAX_SAFE_REQUEST_HASH ||
            self.validate().is_err()
        {
            return false;
        }
        let value = hash as u64;
        (value >> 32) == u64::from(self.client_prefix) &&
            (value as u32) != 0 &&
            (value as u32) <= self.counter
    }

    pub fn next_hash(&mut self) -> Result<i64, HiveMindProtocolError> {
        self.validate()?;
        self.counter = self.counter.checked_add(1)
            .ok_or(HiveMindProtocolError::RequestHashExhausted)?;
        let hash = ((u64::from(self.client_prefix)) << 32)
            | u64::from(self.counter);
        Ok(hash as i64)
    }
}

pub const LOCAL_MOVE_TO_POINT_NAME: &str = "Move To Point";
/// Valid Unity command names that have no independent server strategy ID.
pub const LOCAL_ONLY_COMMAND_NAMES: [&str; 3] = [
    "Bombing Run", "Charge", LOCAL_MOVE_TO_POINT_NAME,
];

/// Persistent BeesServer Game.possibleStratTypes order, IDs 1-15.
pub const COMMAND_STRATEGIES: [&str; 15] = [
    "Aggressive", "Defensive", "Random", "Circle", "Right Swipe",
    "Left Swipe", "Closest Friendly", "In and Out", "Patrol", "Guard",
    "Scouting", "Mining", "Full Retreat", "Hold", "Heal",
];

/// Persistent BeesServer Game.shootingStratTypes order, IDs 1-40.
/// Entries 17-40 are preferred target *ship types*, not additional commands.
pub const SHOOTING_STRATEGIES: [&str; 40] = [
    "First Seen", "Random", "Revenge", "Most Dangerous", "Most Health",
    "Least Health", "Most Powerful", "Least Powerful", "Closest",
    "Furthest", "Most Range", "Least Range", "Fastest", "Slowest",
    "Most Valuable", "Least Valuable", "Type A", "Type B", "Type C",
    "Type D", "Type E", "Type F", "Type G", "Type H", "Type I",
    "Type J", "Type K", "Type L", "Type M", "Type N", "Type O",
    "Type P", "Type Q", "Type R", "Type S", "Type T", "Type U",
    "Type V", "Type W", "Type X",
];

/// Persistent BeesServer Game.targetingStratTypes order, IDs 1-40.
/// This is NOT the same numeric order as shooting, even though many
/// targeting and shooting names overlap.
pub const TARGETING_STRATEGIES: [&str; 40] = [
    "Random", "Revenge", "Most Dangerous", "Least Health", "Most Health",
    "Most Powerful", "Least Powerful", "Closest", "Furthest",
    "Most Range", "Least Range", "Fastest", "Slowest",
    "In Combat", "Gang Up", "Most Valuable", "Least Valuable",
    "Type A", "Type B", "Type C", "Type D", "Type E", "Type F",
    "Type G", "Type H", "Type I", "Type J", "Type K", "Type L",
    "Type M", "Type N", "Type O", "Type P", "Type Q", "Type R",
    "Type S", "Type T", "Type U", "Type V", "Type W",
];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct StrategyIdentity {
    pub id: u8,
    pub name: &'static str,
}

impl StrategyIdentity {
    fn from_registry(
        id: i32,
        name: &str,
        registry: &'static [&'static str],
    ) -> Result<Self, HiveMindProtocolError> {
        let index = id.checked_sub(1)
            .and_then(|value| usize::try_from(value).ok())
            .ok_or(HiveMindProtocolError::UnknownStrategy)?;
        let expected = registry.get(index).copied()
            .ok_or(HiveMindProtocolError::UnknownStrategy)?;
        if name != expected {
            return Err(HiveMindProtocolError::StrategyIdentityMismatch);
        }
        Ok(Self { id: id as u8, name: expected })
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum HiveMindProtocolError {
    InvalidRequest,
    RequestHashExhausted,
    UnknownStrategy,
    StrategyIdentityMismatch,
    BannedStrategy,
    RequestIdentityMismatch,
    InvalidReservation,
    DuplicateReservation,
    InvalidResponse,
}

/// Matchup IDs are unsigned xxHash64 decimal identities, NOT JavaScript
/// numbers. Read either the exact JSON integer or decimal string, then
/// retain the decimal representation without floating-point conversion.
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct MatchupIdentity(String);

impl MatchupIdentity {
    pub fn as_decimal(&self) -> &str { &self.0 }

    pub fn from_decimal(s: &str) -> Result<Self, HiveMindProtocolError> {
        let number = s.parse::<u64>()
            .map_err(|_| HiveMindProtocolError::InvalidResponse)?;
        Ok(Self(number.to_string()))
    }
}

impl Serialize for MatchupIdentity {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(&self.0)
    }
}

impl<'de> Deserialize<'de> for MatchupIdentity {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct DecimalVisitor;
        impl<'de> Visitor<'de> for DecimalVisitor {
            type Value = MatchupIdentity;
            fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                f.write_str("an unsigned 64-bit decimal ID as a string or exact JSON integer")
            }
            fn visit_str<E: de::Error>(self, value: &str) -> Result<Self::Value, E> {
                MatchupIdentity::from_decimal(value).map_err(|_| E::custom("invalid matchup ID"))
            }
            fn visit_string<E: de::Error>(self, value: String) -> Result<Self::Value, E> {
                self.visit_str(&value)
            }
            fn visit_u64<E: de::Error>(self, value: u64) -> Result<Self::Value, E> {
                Ok(MatchupIdentity(value.to_string()))
            }
            fn visit_i64<E: de::Error>(self, value: i64) -> Result<Self::Value, E> {
                u64::try_from(value).map(|number| MatchupIdentity(number.to_string()))
                    .map_err(|_| E::custom("negative matchup ID"))
            }
        }
        deserializer.deserialize_any(DecimalVisitor)
    }
}

/// The same property names as Scripts/Server/GetStrategy.cs. The caller
/// supplies a request hash unique on its WebSocket connection; a resend
/// reuses the SAME hash. Multiple Levels may share one server Game.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct GetStrategyRequest {
    #[serde(rename = "Type")]
    pub message_type: String,
    #[serde(rename = "Hash")]
    pub hash: i64,
    #[serde(rename = "Matchup")]
    pub matchup: String,
    #[serde(rename = "ShootingMatchup")]
    pub shooting_matchup: String,
    #[serde(rename = "OpponentId")]
    pub opponent_id: u64,
    #[serde(rename = "BannedStrats")]
    pub banned_strats: Vec<String>,
}

impl GetStrategyRequest {
    pub fn new(
        hash: i64,
        matchup: String,
        shooting_matchup: String,
        opponent_id: u64,
        banned_strats: impl IntoIterator<Item = String>,
    ) -> Result<Self, HiveMindProtocolError> {
        if hash <= 0 || hash > MAX_SAFE_REQUEST_HASH ||
            matchup.is_empty() || shooting_matchup.is_empty() {
            return Err(HiveMindProtocolError::InvalidRequest);
        }
        let mut bans = Vec::new();
        let mut seen = HashSet::new();
        for name in banned_strats {
            if !LOCAL_ONLY_COMMAND_NAMES.contains(&name.as_str()) &&
                !COMMAND_STRATEGIES.contains(&name.as_str()) {
                return Err(HiveMindProtocolError::UnknownStrategy);
            }
            if seen.insert(name.clone()) {
                bans.push(name);
            }
        }
        if seen.insert(LOCAL_MOVE_TO_POINT_NAME.to_owned()) {
            bans.push(LOCAL_MOVE_TO_POINT_NAME.to_owned());
        }
        Ok(Self {
            message_type: STRATEGY_REQUEST_TYPE.to_owned(),
            hash, matchup, shooting_matchup, opponent_id,
            banned_strats: bans,
        })
    }
}

/// The existing server emits MatchupID/ShootingStrategyMatchupID (capital
/// "ID"), unlike some older Unity DTO field spellings. Support both on
/// read; do not change the production server's response to satisfy a
/// native-only client convenience.
#[derive(Clone, Debug, Deserialize)]
pub struct GetStrategyResponse {
    #[serde(rename = "Type")]
    pub message_type: String,
    #[serde(rename = "Hash")]
    pub hash: i64,
    #[serde(rename = "Name")]
    pub name: String,
    #[serde(rename = "StrategyId")]
    pub strategy_id: i32,
    #[serde(rename = "OutcomeId")]
    pub outcome_id: i64,
    #[serde(rename = "MatchupString")]
    pub matchup_string: String,
    #[serde(rename = "MatchupID", alias = "MatchupId", default)]
    pub matchup_id: Option<MatchupIdentity>,
    #[serde(rename = "ShootingStrategyName")]
    pub shooting_strategy_name: String,
    #[serde(rename = "ShootingStrategyId")]
    pub shooting_strategy_id: i32,
    #[serde(rename = "ShootingStrategyOutcomeId")]
    pub shooting_strategy_outcome_id: i64,
    #[serde(rename = "ShootingStrategyMatchupString")]
    pub shooting_strategy_matchup_string: String,
    #[serde(rename = "ShootingStrategyMatchupID",
        alias = "ShootingStrategyMatchupId", default)]
    pub shooting_strategy_matchup_id: Option<MatchupIdentity>,
    #[serde(rename = "ServerLatency", default)]
    pub server_latency: Option<f64>,
    #[serde(rename = "Status", default)]
    pub status: Option<i32>,
    #[serde(rename = "IsCached", default)]
    pub is_cached: bool,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ValidatedStrategySelection {
    pub command: StrategyIdentity,
    pub shooting: StrategyIdentity,
    pub command_outcome_id: i64,
    pub shooting_outcome_id: i64,
}

impl GetStrategyResponse {
    /// A server-selected strategy must agree with the exact request and
    /// the historical ID registry. Unknown, banned and mismatched choices
    /// cannot silently become executable native commands.
    pub fn validate_for(
        &self, request: &GetStrategyRequest,
    ) -> Result<ValidatedStrategySelection, HiveMindProtocolError> {
        if self.status.is_some_and(|status|
                status != 1 && !(200..300).contains(&status)) {
            return Err(HiveMindProtocolError::InvalidResponse);
        }
        if self.message_type != STRATEGY_REQUEST_TYPE ||
            request.message_type != STRATEGY_REQUEST_TYPE ||
            self.hash != request.hash ||
            self.matchup_string != request.matchup {
            return Err(HiveMindProtocolError::RequestIdentityMismatch);
        }
        if self.outcome_id <= 0 || self.shooting_strategy_outcome_id <= 0 ||
            self.outcome_id == self.shooting_strategy_outcome_id {
            return Err(HiveMindProtocolError::InvalidReservation);
        }
        let command = StrategyIdentity::from_registry(
            self.strategy_id, &self.name, &COMMAND_STRATEGIES)?;
        if request.banned_strats.iter().any(|name| name == command.name) {
            return Err(HiveMindProtocolError::BannedStrategy);
        }
        let shooting = StrategyIdentity::from_registry(
            self.shooting_strategy_id,
            &self.shooting_strategy_name, &SHOOTING_STRATEGIES,
        )?;
        Ok(ValidatedStrategySelection {
            command, shooting,
            command_outcome_id: self.outcome_id,
            shooting_outcome_id: self.shooting_strategy_outcome_id,
        })
    }

    pub fn reservation_ids(&self) -> Vec<i64> {
        let mut ids = Vec::with_capacity(2);
        for id in [self.outcome_id, self.shooting_strategy_outcome_id] {
            if id > 0 && !ids.contains(&id) {
                ids.push(id);
            }
        }
        ids
    }
}

/// An individual row of Scripts/Server/ServerStoredCommand.cs.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct StoredOutcome {
    #[serde(rename = "Tsv")]
    pub tsv: i64,
    #[serde(rename = "OutcomeId")]
    pub outcome_id: i64,
    #[serde(rename = "DiscardReservation", default)]
    pub discard_reservation: bool,
}

impl StoredOutcome {
    pub fn reward(outcome_id: i64, tsv: i64) -> Self {
        Self { outcome_id, tsv, discard_reservation: false }
    }
    pub fn discard(outcome_id: i64) -> Self {
        Self { outcome_id, tsv: 0, discard_reservation: true }
    }
}

/// A StoreCommands submission preserves the server's deployed dual
/// discard representation: DiscardedOutcomeIds AND explicit zero-TSV
/// DiscardReservation entries in Commands. Success means durable commit
/// only after the server confirms it, NEVER after serialization or send.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct StoreCommandsRequest {
    #[serde(rename = "Type")]
    pub message_type: String,
    #[serde(rename = "Hash")]
    pub hash: i64,
    #[serde(rename = "Commands")]
    pub commands: Vec<StoredOutcome>,
    #[serde(rename = "ShootingCommands")]
    pub shooting_commands: Vec<StoredOutcome>,
    #[serde(rename = "TargetingCommands")]
    pub targeting_commands: Vec<StoredOutcome>,
    #[serde(rename = "DiscardedOutcomeIds")]
    pub discarded_outcome_ids: Vec<i64>,
}

impl StoreCommandsRequest {
    pub fn new(hash: i64) -> Result<Self, HiveMindProtocolError> {
        if hash <= 0 || hash > MAX_SAFE_REQUEST_HASH {
            return Err(HiveMindProtocolError::InvalidRequest);
        }
        Ok(Self {
            message_type: STORE_COMMANDS_TYPE.to_owned(), hash,
            commands: Vec::new(),
            shooting_commands: Vec::new(),
            targeting_commands: Vec::new(),
            discarded_outcome_ids: Vec::new(),
        })
    }
}

#[derive(Clone, Debug, Deserialize)]
pub struct StoreCommandsResponse {
    #[serde(rename = "Type")]
    pub message_type: String,
    #[serde(rename = "Hash")]
    pub hash: i64,
    #[serde(rename = "Status")]
    pub status: i32,
    #[serde(rename = "StaleOutcomeIds", default)]
    pub stale_outcome_ids: Vec<i64>,
}

impl StoreCommandsResponse {
    pub fn acknowledges_durable_commit(&self, request_hash: i64) -> bool {
        self.message_type == STORE_COMMANDS_TYPE &&
            self.hash == request_hash &&
            (self.status == 1 || (200..300).contains(&self.status)) &&
            self.stale_outcome_ids.is_empty()
    }
}

/// Scripts/Server/GetMatchupStrategy.cs; the Banned list is made from
/// currently unavailable enemy ship types ("Type A", etc.), and may
/// legitimately include Type X even though the current server's targeting
/// registry stops at Type W.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct GetTargetingRequest {
    #[serde(rename = "Type")]
    pub message_type: String,
    #[serde(rename = "Hash")]
    pub hash: i64,
    #[serde(rename = "Ships")]
    pub ships: String,
    #[serde(rename = "OpponentId")]
    pub opponent_id: u64,
    #[serde(rename = "Banned")]
    pub banned: Vec<String>,
}

impl GetTargetingRequest {
    pub fn new(
        hash: i64,
        ships: String,
        opponent_id: u64,
        banned: impl IntoIterator<Item = String>,
    ) -> Result<Self, HiveMindProtocolError> {
        if hash <= 0 || hash > MAX_SAFE_REQUEST_HASH || ships.is_empty() {
            return Err(HiveMindProtocolError::InvalidRequest);
        }
        let mut seen = HashSet::new();
        let mut bans = Vec::new();
        for entry in banned {
            if !entry.starts_with("Type ") ||
                entry.len() != 6 ||
                !entry.as_bytes()[5].is_ascii_uppercase() {
                return Err(HiveMindProtocolError::UnknownStrategy);
            }
            if seen.insert(entry.clone()) { bans.push(entry); }
        }
        Ok(Self {
            message_type: TARGETING_REQUEST_TYPE.to_owned(),
            hash, ships, opponent_id, banned: bans,
        })
    }
}

/// The server's separate targeting response uses MatchupID, not MatchupId.
/// Its MatchupString can carry the `target-v2:` learning-key namespace
/// and need not equal the request's raw Ships field.
#[derive(Clone, Debug, Deserialize)]
pub struct GetTargetingResponse {
    #[serde(rename = "Type")]
    pub message_type: String,
    #[serde(rename = "Hash")]
    pub hash: i64,
    #[serde(rename = "Name")]
    pub name: String,
    #[serde(rename = "StrategyId")]
    pub strategy_id: i32,
    #[serde(rename = "OutcomeId")]
    pub outcome_id: i64,
    #[serde(rename = "MatchupString")]
    pub matchup_string: String,
    #[serde(rename = "MatchupID", alias = "MatchupId", default)]
    pub matchup_id: Option<MatchupIdentity>,
    #[serde(rename = "ServerLatency", default)]
    pub server_latency: Option<f64>,
    #[serde(rename = "Status", default)]
    pub status: Option<i32>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ValidatedTargetingSelection {
    pub strategy: StrategyIdentity,
    pub outcome_id: i64,
}

impl GetTargetingResponse {
    pub fn validate_for(
        &self,
        request: &GetTargetingRequest,
    ) -> Result<ValidatedTargetingSelection, HiveMindProtocolError> {
        if self.message_type != TARGETING_REQUEST_TYPE ||
            request.message_type != TARGETING_REQUEST_TYPE ||
            self.hash != request.hash ||
            self.status.is_some_and(|status|
                status != 1 && !(200..300).contains(&status)) {
            return Err(HiveMindProtocolError::RequestIdentityMismatch);
        }
        if self.outcome_id <= 0 {
            return Err(HiveMindProtocolError::InvalidReservation);
        }
        let strategy = StrategyIdentity::from_registry(
            self.strategy_id, &self.name, &TARGETING_STRATEGIES,
        )?;
        if request.banned.iter().any(|name| name == strategy.name) {
            return Err(HiveMindProtocolError::BannedStrategy);
        }
        Ok(ValidatedTargetingSelection {
            strategy,
            outcome_id: self.outcome_id,
        })
    }
}
