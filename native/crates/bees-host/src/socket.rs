//! Authenticated BeesServer WebSocket transport for the native host.
//!
//! Shares the server's production WSS + "game" subprotocol, setup-level
//! authentication, one server Game across Levels and request-hash namespace.
//! Request/command outcome ownership remains in DurableHiveMindConnection.
//! Socket I/O is blocking: a rendered client must run this adapter on its
//! network thread, never on its frame/physics thread.
//!
//! Neither a socket write nor a 200-byte response is a durable outcome
//! acknowledgement. Forward every non-setup frame to the native host's
//! receive_frame, where the canonical StoreCommands ledger is persisted.

use std::net::TcpStream;
use std::collections::HashSet;

use bees_hivemind::protocol::{
    GetStrategyRequest, GetTargetingRequest, StoreCommandsRequest,
    MAX_SAFE_REQUEST_HASH,
};
use serde::{Deserialize, Serialize};
use tungstenite::client::IntoClientRequest;
use tungstenite::http::header::{HeaderValue, SEC_WEBSOCKET_PROTOCOL};
use tungstenite::stream::MaybeTlsStream;
use tungstenite::{Message, WebSocket};

use crate::durable_connection::{
    DurableConnectionError, DurableHiveMindConnection,
};

const MAX_SERVER_MESSAGE_BYTES: usize = 8 * 1024 * 1024;
const SUBPROTOCOL: &str = "game";

/// O(1) connection-level counters; sampled between rollout/network windows,
/// never an extra per-agent or fixed-step telemetry event.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeSocketMeasurements {
    pub outgoing_frames: u64,
    pub outgoing_json_bytes: u64,
    pub incoming_frames: u64,
    pub incoming_json_bytes: u64,
    pub setup_accepted: u64,
    /// Actual StoreCommands transmissions, not server acknowledgements.
    pub store_transmissions: u64,
    pub transport_errors: u64,
    pub protocol_rejections: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SetupKind { Initial, Reconnect }

impl SetupKind {
    fn message_type(self) -> &'static str {
        match self {
            Self::Initial => "setup-level",
            Self::Reconnect => "reconnect-level",
        }
    }
}

/// Source-compatible SetupLevel wire inputs. A caller owns its Steam auth
/// ticket and explicitly selects the server's LevelId and GameId. The
/// ticket is never checkpointed or included in diagnostic status snapshots.
pub struct SetupLevelIdentity<'a> {
    pub kind: SetupKind,
    pub level_id: i32,
    pub server_game_id: i64,
    pub user_id: u64,
    pub auth_ticket: Option<&'a str>,
    pub version: f32,
}

#[derive(Serialize)]
struct SetupLevelWire<'a> {
    #[serde(rename = "Type")]
    message_type: &'static str,
    #[serde(rename = "Hash")]
    hash: i64,
    #[serde(rename = "LevelId")]
    level_id: i32,
    #[serde(rename = "GameId")]
    server_game_id: i64,
    #[serde(rename = "UserId")]
    user_id: String,
    #[serde(rename = "AuthTicket")]
    auth_ticket: Option<&'a str>,
    #[serde(rename = "Version")]
    version: f32,
}

#[derive(Deserialize)]
struct ServerResponseIdentity {
    #[serde(rename = "Type")]
    message_type: String,
    #[serde(rename = "Hash")]
    hash: i64,
}

#[derive(Deserialize)]
struct SetupServerResponse {
    #[serde(rename = "Type")]
    message_type: String,
    #[serde(rename = "Hash")]
    hash: i64,
    #[serde(rename = "Status")]
    status: i32,
    #[serde(rename = "GameId")]
    game_id: Option<i64>,
}

#[derive(Clone, Copy, Debug)]
struct PendingSetup {
    hash: i64,
    kind: SetupKind,
}

/// Setup traffic is separate from the strategy/targeting/StoreCommands
/// decoder, whose request correlation belongs to the Level driver.
#[derive(Debug)]
pub enum HiveMindSocketEvent {
    SetupAccepted {
        request_hash: i64,
        server_game_id: i64,
    },
    /// Always deliver to NativeHostRuntime::receive_frame or the other
    /// authenticated server-message owner. Never silently discard replies.
    ServerFrame(Vec<u8>),
}

#[derive(Debug)]
pub enum HiveMindSocketError {
    Transport(tungstenite::Error),
    Json(serde_json::Error),
    Durability(DurableConnectionError),
    InvalidEndpoint,
    InsecureEndpointForbidden,
    WrongSubprotocol,
    AuthenticationRequired,
    InvalidSetup,
    SetupAlreadyPending,
    SetupRequired,
    InvalidRequestOwnership,
    InvalidServerFrame,
    UnexpectedSetupResponse,
    SetupRejected(i32),
    MismatchedServerGame,
    ServerFrameTooLarge,
    Disconnected,
}

impl From<tungstenite::Error> for HiveMindSocketError {
    fn from(error: tungstenite::Error) -> Self { Self::Transport(error) }
}
impl From<serde_json::Error> for HiveMindSocketError {
    fn from(error: serde_json::Error) -> Self { Self::Json(error) }
}
impl From<DurableConnectionError> for HiveMindSocketError {
    fn from(error: DurableConnectionError) -> Self { Self::Durability(error) }
}

pub struct NativeHiveMindSocket {
    socket: WebSocket<MaybeTlsStream<TcpStream>>,
    /// True ONLY for a caller-opted-in local development ws:// endpoint.
    secure: bool,
    game_id: Option<i64>,
    pending_setup: Option<PendingSetup>,
    failed: bool,
    measurements: NativeSocketMeasurements,
    /// Only the hashes transmitted on THIS socket. Pending request payloads,
    /// retry authority and acknowledgement remain in the durable connection.
    /// Recreated empty on reconnect and pruned after 200/409 acknowledgements.
    sent_store_hashes: HashSet<i64>,
}

impl NativeHiveMindSocket {
    /// Production always uses certificate-verified WSS. Plaintext WS is
    /// allowed ONLY when explicitly enabled AND the endpoint is localhost
    /// or an IP loopback address; authentication is not bypassed on WSS.
    ///
    /// This is blocking dial/handshake I/O, not suitable for render thread.
    pub fn connect(
        url: &str,
        allow_insecure_loopback: bool,
    ) -> Result<Self, HiveMindSocketError> {
        let mut request = url.into_client_request()?;
        let uri = request.uri();
        let host = uri.host().ok_or(HiveMindSocketError::InvalidEndpoint)?;
        let secure = match uri.scheme_str() {
            Some("wss") => true,
            Some("ws") if allow_insecure_loopback &&
                matches!(host, "localhost" | "127.0.0.1" | "::1" | "[::1]") => false,
            Some("ws") => return Err(HiveMindSocketError::InsecureEndpointForbidden),
            _ => return Err(HiveMindSocketError::InvalidEndpoint),
        };
        request.headers_mut().insert(
            SEC_WEBSOCKET_PROTOCOL, HeaderValue::from_static(SUBPROTOCOL),
        );
        // Never follow an HTTP redirect to a different origin or to
        // plaintext ws:// after initially demanding authenticated WSS.
        // WebSocketConfig is non-exhaustive: initialize from Default and
        // explicitly tighten the two receive limits without constructing
        // fields that may change across tungstenite versions.
        let mut config = tungstenite::protocol::WebSocketConfig::default();
        config.max_frame_size = Some(MAX_SERVER_MESSAGE_BYTES);
        config.max_message_size = Some(MAX_SERVER_MESSAGE_BYTES);
        let (socket, response) =
            tungstenite::client::connect_with_config(request, Some(config), 0)?;
        let confirmed = response.headers().get(SEC_WEBSOCKET_PROTOCOL)
            .and_then(|value| value.to_str().ok());
        if confirmed != Some(SUBPROTOCOL) {
            return Err(HiveMindSocketError::WrongSubprotocol);
        }
        Ok(Self {
            socket, secure, game_id: None,
            pending_setup: None, failed: false,
            measurements: NativeSocketMeasurements::default(),
            sent_store_hashes: HashSet::new(),
        })
    }

    pub fn game_id(&self) -> Option<i64> { self.game_id }
    pub fn measurements(&self) -> NativeSocketMeasurements { self.measurements }
    /// A failed transport or rejected server protocol frame must never
    /// continue with its old transient strategy/targeting correlation
    /// session. Pending StoreCommands remain durable in the connection.
    pub fn is_failed(&self) -> bool { self.failed }

    fn reject_protocol<T>(
        &mut self, error: HiveMindSocketError,
    ) -> Result<T, HiveMindSocketError> {
        self.failed = true;
        self.measurements.protocol_rejections += 1;
        Err(error)
    }

    pub fn ready(&self) -> bool {
        !self.failed && self.game_id.is_some() && self.pending_setup.is_none()
    }

    fn check_available(&self) -> Result<(), HiveMindSocketError> {
        if self.failed {
            Err(HiveMindSocketError::Disconnected)
        } else {
            Ok(())
        }
    }

    fn send<T: Serialize>(&mut self, request: &T)
        -> Result<(), HiveMindSocketError>
    {
        self.check_available()?;
        let json = serde_json::to_string(request)?;
        if json.len() > MAX_SERVER_MESSAGE_BYTES {
            return Err(HiveMindSocketError::ServerFrameTooLarge);
        }
        let bytes = json.len();
        if let Err(error) = self.socket.send(Message::Text(json.into())) {
            self.failed = true;
            self.measurements.transport_errors += 1;
            return Err(HiveMindSocketError::Transport(error));
        }
        self.measurements.outgoing_frames += 1;
        self.measurements.outgoing_json_bytes += bytes as u64;
        Ok(())
    }

    /// Request setup-level or reconnect-level on the ONE server connection.
    /// Save the shared hash sequence BEFORE handing the setup JSON to the
    /// socket, just as with ordinary HiveMind requests. Repeat setup-level
    /// on the same open socket for additional Levels, reusing the server Game.
    pub fn send_setup(
        &mut self,
        connection: &mut DurableHiveMindConnection,
        identity: SetupLevelIdentity<'_>,
    ) -> Result<i64, HiveMindSocketError> {
        self.check_available()?;
        if self.pending_setup.is_some() {
            return Err(HiveMindSocketError::SetupAlreadyPending);
        }
        if identity.user_id == 0 ||
            !identity.version.is_finite() ||
            identity.server_game_id < 0 ||
            identity.server_game_id > MAX_SAFE_REQUEST_HASH ||
            (identity.kind == SetupKind::Reconnect && identity.server_game_id == 0)
        {
            return Err(HiveMindSocketError::InvalidSetup);
        }
        if self.secure &&
            identity.auth_ticket.map_or(true, |ticket| ticket.is_empty())
        {
            return Err(HiveMindSocketError::AuthenticationRequired);
        }
        if identity.kind == SetupKind::Initial && identity.server_game_id != 0 {
            return Err(HiveMindSocketError::InvalidSetup);
        }
        let hash = connection.next_hash()?;
        let payload = SetupLevelWire {
            message_type: identity.kind.message_type(),
            hash,
            level_id: identity.level_id,
            server_game_id: identity.server_game_id,
            user_id: identity.user_id.to_string(),
            auth_ticket: identity.auth_ticket,
            version: identity.version,
        };
        self.send(&payload)?;
        self.pending_setup = Some(PendingSetup { hash, kind: identity.kind });
        Ok(hash)
    }

    fn require_ready(&self) -> Result<(), HiveMindSocketError> {
        self.check_available()?;
        if !self.ready() {
            return Err(HiveMindSocketError::SetupRequired);
        }
        Ok(())
    }

    /// Verify this request belongs to the shared connection's original
    /// strategy session before writing. No independent transport hash map.
    pub fn send_strategy(
        &mut self,
        connection: &DurableHiveMindConnection,
        request: &GetStrategyRequest,
    ) -> Result<(), HiveMindSocketError> {
        self.require_ready()?;
        if !connection.is_available() {
            return Err(HiveMindSocketError::Durability(
                DurableConnectionError::Unavailable,
            ));
        }
        if !connection.connection().matches_live_strategy_request(request) {
            return Err(HiveMindSocketError::InvalidRequestOwnership);
        }
        self.send(request)
    }

    pub fn send_targeting(
        &mut self,
        connection: &DurableHiveMindConnection,
        request: &GetTargetingRequest,
    ) -> Result<(), HiveMindSocketError> {
        self.require_ready()?;
        if !connection.is_available() {
            return Err(HiveMindSocketError::Durability(
                DurableConnectionError::Unavailable,
            ));
        }
        if !connection.connection().matches_live_targeting_request(request) {
            return Err(HiveMindSocketError::InvalidRequestOwnership);
        }
        self.send(request)
    }

    /// A StoreCommands payload must be precisely the persisted original,
    /// including its original Hash. Never regenerate or retry 409 batches.
    pub fn send_store(
        &mut self,
        connection: &DurableHiveMindConnection,
        request: &StoreCommandsRequest,
    ) -> Result<(), HiveMindSocketError> {
        self.require_ready()?;
        if connection.retry_payload(request.hash)? != Some(request) {
            return Err(HiveMindSocketError::InvalidRequestOwnership);
        }
        // Direct sends and bulk flushes share the same connection-local
        // admission. A second call on this open socket is a no-op, not a
        // second server write; reconnect creates a fresh send session.
        if self.sent_store_hashes.contains(&request.hash) {
            return Ok(());
        }
        self.send(request)?;
        self.sent_store_hashes.insert(request.hash);
        self.measurements.store_transmissions += 1;
        Ok(())
    }

    /// Transmit only not-yet-sent pending StoreCommands on this authenticated
    /// socket. Each payload is fetched from the connection's DURABLE retry
    /// ledger and keeps its original hash. Poll after checkpointing newly
    /// prepared outcomes. Repeated polls do not spam unacknowledged requests;
    /// a new socket starts empty and sends all still-pending hashes.
    ///
    /// This is deliberately a caller-driven socket-I/O operation, not an
    /// implicit blocking send on the rendering or simulation thread.
    pub fn flush_unsent_stores(
        &mut self,
        connection: &DurableHiveMindConnection,
    ) -> Result<usize, HiveMindSocketError> {
        self.require_ready()?;
        let hashes = connection.pending_store_hashes()?;
        self.sent_store_hashes.retain(|hash|
            hashes.binary_search(hash).is_ok()
        );
        let mut sent = 0usize;
        for hash in hashes {
            if self.sent_store_hashes.contains(&hash) { continue; }
            let original = connection.retry_payload(hash)?
                .ok_or(HiveMindSocketError::InvalidRequestOwnership)?;
            self.send_store(connection, original)?;
            sent += 1;
        }
        Ok(sent)
    }

    /// After an authenticated reconnect, send the exact original
    /// StoreCommands hashes retained on disk. A fresh socket starts with
    /// an empty sent-hash set; an accidental repeated reconnect flush
    /// cannot send the same pending request twice on that socket.
    /// Terminal 409 reconciliation records are deliberately excluded.
    pub fn replay_pending_stores(
        &mut self,
        connection: &DurableHiveMindConnection,
    ) -> Result<usize, HiveMindSocketError> {
        self.flush_unsent_stores(connection)
    }

    /// Read one non-control message. Ping/pong servicing does not create
    /// application events. Setup responses are validated before unlocking
    /// the ordinary request stream. All other messages must be handed to
    /// NativeHostRuntime::receive_frame and/or their separate feature owner.
    pub fn read_event(&mut self) -> Result<HiveMindSocketEvent, HiveMindSocketError> {
        self.check_available()?;
        loop {
            let message = match self.socket.read() {
                Ok(message) => message,
                Err(error) => {
                    self.failed = true;
                    self.measurements.transport_errors += 1;
                    return Err(HiveMindSocketError::Transport(error));
                }
            };
            let frame = match message {
                Message::Text(text) => text.into_bytes(),
                Message::Ping(_) | Message::Pong(_) => {
                    if let Err(error) = self.socket.flush() {
                        self.failed = true;
                        self.measurements.transport_errors += 1;
                        return Err(HiveMindSocketError::Transport(error));
                    }
                    continue;
                }
                Message::Close(_) => {
                    self.failed = true;
                    return Err(HiveMindSocketError::Disconnected);
                }
                Message::Binary(_) | Message::Frame(_) =>
                    return self.reject_protocol(
                        HiveMindSocketError::InvalidServerFrame,
                    ),
            };
            if frame.len() > MAX_SERVER_MESSAGE_BYTES {
                return self.reject_protocol(HiveMindSocketError::ServerFrameTooLarge);
            }
            self.measurements.incoming_frames += 1;
            self.measurements.incoming_json_bytes += frame.len() as u64;
            let response: ServerResponseIdentity = match serde_json::from_slice(&frame) {
                Ok(value) => value,
                Err(error) => return self.reject_protocol(HiveMindSocketError::Json(error)),
            };
            if self.pending_setup.is_some_and(|pending| pending.hash == response.hash) &&
                !matches!(response.message_type.as_str(), "setup-level" | "reconnect-level")
            {
                return self.reject_protocol(HiveMindSocketError::UnexpectedSetupResponse);
            }
            let is_setup = matches!(
                response.message_type.as_str(),
                "setup-level" | "reconnect-level",
            );
            if is_setup {
                let Some(pending) = self.pending_setup else {
                    return self.reject_protocol(HiveMindSocketError::UnexpectedSetupResponse);
                };
                if response.hash != pending.hash ||
                    response.message_type != pending.kind.message_type()
                {
                    return self.reject_protocol(HiveMindSocketError::UnexpectedSetupResponse);
                }
                let ack: SetupServerResponse = match serde_json::from_slice(&frame) {
                    Ok(value) => value,
                    Err(error) => return self.reject_protocol(HiveMindSocketError::Json(error)),
                };
                if ack.hash != pending.hash ||
                    ack.message_type != pending.kind.message_type()
                {
                    self.failed = true;
                    return Err(HiveMindSocketError::UnexpectedSetupResponse);
                }
                if ack.status != 200 {
                    return self.reject_protocol(HiveMindSocketError::SetupRejected(ack.status));
                }
                let Some(id) = ack.game_id
                    .filter(|&id| id > 0 && id <= MAX_SAFE_REQUEST_HASH) else {
                    return self.reject_protocol(HiveMindSocketError::InvalidServerFrame);
                };
                if pending.kind == SetupKind::Initial &&
                    self.game_id.is_some_and(|original| original != id)
                {
                    return self.reject_protocol(HiveMindSocketError::MismatchedServerGame);
                }
                self.pending_setup = None;
                self.game_id = Some(id);
                self.measurements.setup_accepted += 1;
                return Ok(HiveMindSocketEvent::SetupAccepted {
                    request_hash: pending.hash, server_game_id: id,
                });
            }
            return Ok(HiveMindSocketEvent::ServerFrame(frame));
        }
    }

    /// Call DurableHiveMindConnection::retire_socket after loss, retaining
    /// StoreCommands pending outcomes for their next authenticated socket.
    pub fn close(&mut self) -> Result<(), HiveMindSocketError> {
        self.failed = true;
        self.socket.close(None)?;
        Ok(())
    }
}
