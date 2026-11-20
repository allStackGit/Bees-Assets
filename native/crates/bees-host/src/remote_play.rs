//! Opt-in authenticated LAN multiplayer transport for one native Level.
//!
//! Every peer is bound to the player side authenticated by its secret token.
//! Two side tokens permit opposing source-authored human players; one token
//! permits cooperative control of the local player side. The wire transport
//! submits the SAME typed NativePlayerInput as local play and replay.
//! There is no remote physics, command director, policy state or shadow roster.
//!
//! Protocol: newline-delimited JSON. Hello {kind:"hello", token}; then
//! {kind:"input", request_id:u64, action:NativePlayerInput}. The host replies
//! with kind:"input_result" and projects side-filtered kind:"world_state".
use std::collections::{HashMap, HashSet, VecDeque};
use std::io::{self, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::time::{Duration, Instant};

use bees_sim::collision::Aabb;
use bees_sim::combat_world::CombatEvent;
use bees_sim::level_discovery::DiscoveryKind;
use bees_sim::obstacle::ObstacleKind;
use bees_sim::physics::ShipShape;
use bees_sim::geometry::Vec2;
use bees_sim::simulation::WorldId;
use bees_gameplay::player_inputs::{NativeHeldFireAction, NativePlayerHeldFire};
use bees_hivemind::session::SquadLifetime;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};

use crate::player_trace::NativePlayerInput;
use crate::presentation::{NativeFogFade, NativeRenderWorldView};
use crate::unity_scene_snapshot::NativeMusicLocation;

const MAX_PEERS: usize = 4;
const MAX_ACCEPTS_PER_POLL: usize = 8;
const MAX_ORDERS_PER_PEER_POLL: usize = 8;
const MAX_LINE: usize = 64 * 1024;
const MAX_PENDING: usize = 4 * 1024 * 1024;
const MAX_RELIABLE_PENDING: usize = 256 * 1024;
const READ_BYTES_PER_POLL: usize = 128 * 1024;
const WRITE_BYTES_PER_FLUSH: usize = 256 * 1024;
const MAX_ORDERS_PER_POLL: usize = 64;
const STATE_EVERY_TICKS: u64 = 5;
const MAX_PENDING_AUDIO_EVENTS: usize = 32;

#[derive(Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
enum WireInput {
    Hello { token: String },
    Input { request_id: u64, action: NativePlayerInput },
    /// UI acknowledgment only, without an alternative gameplay clock.
    BriefingReady { request_id: u64, world_id: u64,
        episode_revision: u64, sequence: u8 },
}

#[derive(Clone, Serialize, Deserialize)]
pub struct RemoteOrder {
    pub peer: u64,
    pub side: i32,
    pub request_id: u64,
    pub action: NativePlayerInput,
}

/// One authenticated peer's gesture, independent of every other peer.
/// The native Level owns the actual live weapons keyed by the same peer ID,
/// never a duplicate target cache or independent physics simulation.
#[derive(Clone, Copy, Serialize, Deserialize)]
struct HeldFireLease {
    peer: u64,
    side: i32,
    world_id: WorldId,
    episode_revision: u64,
    begin_request_id: u64,
}

impl HeldFireLease {
    fn matches(self, peer: u64, side: i32, input: &NativePlayerHeldFire) -> bool {
        self.peer == peer && self.side == side &&
        self.world_id == input.world_id &&
        self.episode_revision == input.episode_revision
    }

    fn release(self) -> RemoteOrder {
        RemoteOrder {
            peer: self.peer, side: self.side, request_id: 0,
            action: NativePlayerInput::HeldFire(NativePlayerHeldFire {
                world_id: self.world_id,
                episode_revision: self.episode_revision,
                controller_id: self.peer,
                action: NativeHeldFireAction::End,
            }),
        }
    }
}

struct Peer {
    id: u64,
    socket: TcpStream,
    side: Option<i32>,
    last_request_id: u64,
    incoming: Vec<u8>,
    // At most one frame is on the socket. Reliable replies queue separately
    // and are never discarded, while stale unsent state is replaceable.
    outgoing: Vec<u8>,
    written: usize,
    writing_state: bool,
    reliable: VecDeque<Vec<u8>>,
    reliable_bytes: usize,
    newest_state: Option<Vec<u8>>,
    closed: bool,
    accepted_at: Instant,
}

impl Peer {
    fn enqueue(&mut self, message: &Value) -> io::Result<()> {
        let mut data = serde_json::to_vec(message)
            .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
        data.push(b'\n');
        if data.len() > MAX_RELIABLE_PENDING ||
            self.reliable_bytes + data.len() > MAX_RELIABLE_PENDING {
            self.closed = true;
            return Err(io::Error::new(io::ErrorKind::InvalidData,
                "remote peer cannot keep up with authoritative replies"));
        }
        self.reliable_bytes += data.len();
        self.reliable.push_back(data);
        // A terminal result or command acknowledgement must not wait behind
        // an entire unsent, replaceable world snapshot. If the socket has
        // already written part of a JSON line, it MUST finish that line
        // before sending any other payload. Otherwise the next flush may
        // start with the oldest reliable frame, without mixing framing.
        if self.writing_state && self.written == 0 {
            // Keep the most recent unsent state for AFTER reliable replies.
            // A later snapshot may still replace it without touching TCP
            // framing or postponing a terminal receipt.
            let preempted = std::mem::take(&mut self.outgoing);
            if self.newest_state.is_none() {
                self.newest_state = Some(preempted);
            }
            self.writing_state = false;
        }
        Ok(())
    }

    /// State is lossy presentation data. Preserve the current partially
    /// transmitted line (TCP framing), but replace any *unsent* old state
    /// with the latest tick. Reply messages never participate in coalescing.
    fn enqueue_state(&mut self, state: &[u8]) -> bool {
        if state.len() + 1 > MAX_PENDING {
            self.closed = true;
            return false;
        }
        let mut data = Vec::with_capacity(state.len() + 1);
        data.extend_from_slice(state);
        data.push(b'\n');
        if self.writing_state && self.written == 0 {
            self.outgoing = data;
            return true;
        }
        self.newest_state.replace(data).is_some()
    }

    /// Returns actual state payload bytes written, not just queued traffic.
    /// A partially written JSON line is always finished before another one.
    fn flush(&mut self) -> u64 {
        let mut bytes = 0u64;
        let mut budget = WRITE_BYTES_PER_FLUSH;
        while !self.closed && budget > 0 {
            if self.written == self.outgoing.len() {
                self.outgoing.clear();
                self.written = 0;
                self.writing_state = false;
                if let Some(message) = self.reliable.pop_front() {
                    self.reliable_bytes -= message.len();
                    self.outgoing = message;
                } else if let Some(state) = self.newest_state.take() {
                    self.outgoing = state;
                    self.writing_state = true;
                } else {
                    break;
                }
            }
            let end = (self.written + budget).min(self.outgoing.len());
            match self.socket.write(&self.outgoing[self.written..end]) {
                Ok(0) => { self.closed = true; break; }
                Ok(n) => {
                    self.written += n;
                    budget -= n;
                    if self.writing_state { bytes += n as u64; }
                }
                Err(ref error) if error.kind() == io::ErrorKind::WouldBlock => break,
                Err(ref error) if error.kind() == io::ErrorKind::Interrupted => continue,
                Err(_) => { self.closed = true; break; }
            }
        }
        bytes
    }
}

#[derive(Clone, Default, Serialize, Deserialize)]
pub struct RemotePlayMeasurements {
    pub connections_accepted: u64,
    pub authenticated_peers: u64,
    pub rejected_connections: u64,
    pub orders_received: u64,
    pub orders_processed: u64,
    pub orders_rejected: u64,
    #[serde(default)]
    pub briefing_ready_acks: u64,
    #[serde(default)]
    pub briefing_rejected_acks: u64,
    #[serde(default)]
    pub briefing_wait_polls: u64,
    pub state_frames: u64,
    pub state_bytes: u64,
    pub states_superseded: u64,
    /// Actual time spent accepting/parsing/authenticating remote inputs at
    /// fixed-tick boundaries; does not include simulation or render work.
    pub input_poll_wall_seconds: f64,
    /// Count one projection/JSON encoding per distinct authenticated side,
    /// never once per cooperative peer who consumes the same bytes.
    pub snapshot_unique_encoded_bytes: u64,
    pub snapshot_projection_wall_seconds: f64,
    /// Includes only enqueue and nonblocking write attempts made during
    /// snapshot broadcast, not later terminal drain or reliable replies.
    pub snapshot_socket_wall_seconds: f64,
    /// Final side-filtered state bytes drained after the last physical tick.
    pub terminal_drain_wall_seconds: f64,
    /// Connected peers whose terminal state remained queued at the drain deadline.
    pub terminal_state_pending_peers: usize,
    /// Complete Level outcomes queued on the reliable channel after the
    /// battle finalization boundary, not inferred from presentation frames.
    pub battle_result_receipts: u64,
    pub interrupted_battle_notices: u64,
    pub disconnected_peers: u64,
    /// A previously checkpointed host incarnation was resumed on this
    /// authoritative World without reusing its dead TCP sockets.
    pub recovered_sessions: u64,
    pub recovered_peer_connections: u64,
    pub recovered_held_fire_releases: u64,
    pub held_fire_requests: u64,
    pub held_fire_rejected: u64,
    pub held_fire_disconnect_releases: u64,
    pub held_fire_no_live_weapons_releases: u64,
    pub concurrent_held_fire_peak: usize,
    /// Work for side-filtered snapshots, never physics or policy perception.
    /// Includes only distinct side snapshots, not repeated cooperative peers.
    pub fog_visibility_queries: u64,
    pub fog_visibility_candidate_checks: u64,
    pub fog_visibility_sources_indexed: u64,
    pub fog_visibility_grid_queries: u64,
    /// Source-authored shrinking vision masks, sampled only for connected sides.
    pub fog_fading_source_ticks: u64,
    pub fog_fading_sources_peak: usize,
    pub audio_events_queued: u64,
    pub audio_events_dropped: u64,
    /// A real ship death displaced one cosmetic projectile cue when a
    /// busy battle had already filled its fixed outbound event budget.
    pub ship_deaths_prioritized: u64,
    /// Presentation-only physical hits admitted and discarded by LAN cue budget.
    pub combat_impacts_queued: u64,
    pub combat_impacts_dropped: u64,
}

/// Transport ownership carried in the SAME durable host transaction as
/// the actual World. Sockets are intentionally not recoverable: a crashed
/// host must require a fresh authentication, and old held-fire controllers
/// are released through the regular authoritative input dispatcher.
#[derive(Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NativeRemotePlayRecoveryCheckpoint {
    schema_version: u32,
    host_session_id: String,
    side_credentials_sha256: [u8; 32],
    next_peer: u64,
    connected_peers: usize,
    held_fire: Vec<HeldFireLease>,
    pending_releases: Vec<RemoteOrder>,
    measurements: RemotePlayMeasurements,
}

fn credential_digest(tokens: &HashMap<i32, String>) -> [u8; 32] {
    let mut sorted = tokens.iter().collect::<Vec<_>>();
    sorted.sort_unstable_by_key(|entry| *entry.0);
    let mut digest = Sha256::new();
    for (side, secret) in sorted {
        digest.update(side.to_le_bytes());
        digest.update((secret.len() as u64).to_le_bytes());
        digest.update(secret.as_bytes());
    }
    digest.finalize().into()
}

pub struct NativeRemotePlay {
    listener: TcpListener,
    // Identifies this one authoritative host session across TCP reconnects.
    // A restarted process cannot silently claim to resume a different
    // physical World just because it uses the same player-side tokens.
    host_session_id: String,
    recovered_session: bool,
    /// A graphical local host already owns its own briefing pacing.
    /// Only dedicated LAN hosting needs remote viewer acknowledgment.
    dedicated_briefing_gate: bool,
    side_tokens: HashMap<i32, String>,
    peers: Vec<Peer>,
    next_peer: u64,
    last_state: Option<(u64, u64)>,
    // A newly authenticated player needs the next authoritative snapshot
    // immediately, even if the fixed tick is between normal state pulses.
    force_initial_state: bool,
    // Cosmetic cues only, not a duplicate gameplay event store.
    pending_audio: HashMap<i32, Vec<Value>>,
    // Each authenticated side has only presentation vision masks; no
    // second gameplay visibility or historical enemy positions are stored.
    fog_by_side: HashMap<i32, RemoteFogSight>,
    music_location: Option<NativeMusicLocation>,
    held_fire: HashMap<u64, HeldFireLease>,
    pending_releases: Vec<RemoteOrder>,
    /// Per-connection source briefing acknowledgment, never gameplay.
    briefing_ready: HashMap<u64,(u64,u64,u8)>,
    measurements: RemotePlayMeasurements,
}

impl NativeRemotePlay {
    pub fn from_environment(local_side: i32, dedicated: bool)
        -> io::Result<Option<Self>> {
        let Ok(address) = std::env::var("BEES_NATIVE_REMOTE_LISTEN") else {
            return Ok(None);
        };
        // One token keeps the original cooperative single-side mode.
        // Explicit side tokens enable independently authenticated human
        // players only where the imported Level actually grants each
        // side source player control.
        let side_tokens: HashMap<i32, String> =
            if let Ok(raw) = std::env::var("BEES_NATIVE_REMOTE_SIDE_TOKENS") {
                serde_json::from_str(&raw).map_err(|error|
                    io::Error::new(io::ErrorKind::InvalidInput,
                        format!("invalid BEES_NATIVE_REMOTE_SIDE_TOKENS: {error}")))?
            } else {
                let token = std::env::var("BEES_NATIVE_REMOTE_TOKEN").map_err(|_|
                    io::Error::new(io::ErrorKind::InvalidInput,
                        "BEES_NATIVE_REMOTE_TOKEN or BEES_NATIVE_REMOTE_SIDE_TOKENS is required"))?;
                HashMap::from([(local_side, token)])
            };
        if side_tokens.is_empty() || side_tokens.len() > 2 ||
            side_tokens.iter().any(|(&side, token)|
                !(1..=2).contains(&side) || token.len() < 24 ||
                token.len() > 256 || token.chars().any(|ch| ch.is_control())) ||
            { let unique: HashSet<_> = side_tokens.values().collect();
              unique.len() != side_tokens.len() }
        {
            return Err(io::Error::new(io::ErrorKind::InvalidInput,
                "remote player sides must be 1 or 2 with unique 24-256 character tokens"));
        }
        let mut session_nonce = [0u8; 16];
        getrandom::getrandom(&mut session_nonce)
            .map_err(|error| io::Error::new(io::ErrorKind::Other,
                format!("native multiplayer host session entropy unavailable: {error}")))?;
        let host_session_id = session_nonce.iter()
            .map(|byte| format!("{byte:02x}")).collect::<String>();
        let listener = TcpListener::bind(&address)?;
        listener.set_nonblocking(true)?;
        let mut configured_sides = side_tokens.keys().copied().collect::<Vec<_>>();
        configured_sides.sort_unstable();
        println!("{}", json!({
            "kind": "native_remote_listening",
            "address": listener.local_addr()?.to_string(),
            "max_peers": MAX_PEERS,
            "mode": if side_tokens.len() > 1 { "independent_player_sides" }
                    else { "shared_player_authority" },
            "configured_sides": configured_sides,
        }));
        Ok(Some(Self {
            listener, host_session_id, recovered_session: false,
            dedicated_briefing_gate: dedicated,
            side_tokens, peers: Vec::new(),
            next_peer: 1, last_state: None, force_initial_state: false,
            pending_audio: HashMap::new(), fog_by_side: HashMap::new(),
            music_location: None,
            held_fire: HashMap::new(), pending_releases: Vec::new(),
            briefing_ready: HashMap::new(),
            measurements: RemotePlayMeasurements::default(),
        }))
    }

    pub fn measurements(&self) -> &RemotePlayMeasurements {
        &self.measurements
    }

    /// Save only transport-side command ownership and cumulative counters;
    /// no World, policies or player permissions are duplicated here.
    pub fn recovery_checkpoint(
        &self, world_id: WorldId, revision: u64,
    ) -> NativeRemotePlayRecoveryCheckpoint {
        let mut held_fire = self.held_fire.values().copied()
            .filter(|lease| lease.world_id == world_id &&
                lease.episode_revision == revision)
            .collect::<Vec<_>>();
        held_fire.sort_unstable_by_key(|lease| lease.peer);
        NativeRemotePlayRecoveryCheckpoint {
            schema_version: 1,
            host_session_id: self.host_session_id.clone(),
            side_credentials_sha256: credential_digest(&self.side_tokens),
            next_peer: self.next_peer,
            connected_peers: self.peers.iter().filter(|peer| !peer.closed).count(),
            held_fire,
            pending_releases: self.pending_releases.iter()
                .filter(|order| order.action.episode() == (world_id, revision))
                .cloned().collect(),
            measurements: self.measurements.clone(),
        }
    }

    /// Rebind a restarted listener to the actual recovered World. The
    /// previous TCP sockets are gone; release their real live gestures at
    /// the next fixed-tick input boundary, before admitting new commands.
    pub fn restore_recovery_checkpoint(
        &mut self, saved: NativeRemotePlayRecoveryCheckpoint,
        world_id: WorldId, revision: u64,
    ) -> io::Result<()> {
        let invalid = || io::Error::new(io::ErrorKind::InvalidData,
            "native remote session does not match the recovered World and side owners");
        if saved.schema_version != 1 ||
            saved.host_session_id.len() != 32 ||
            !saved.host_session_id.bytes().all(|ch|
                ch.is_ascii_digit() || (b'a'..=b'f').contains(&ch)) ||
            saved.next_peer == 0 ||
            saved.next_peer == u64::MAX ||
            saved.connected_peers > MAX_PEERS ||
            saved.held_fire.len() > saved.connected_peers ||
            saved.side_credentials_sha256 != credential_digest(&self.side_tokens) ||
            saved.held_fire.len() > MAX_PEERS ||
            saved.pending_releases.len() > MAX_ORDERS_PER_POLL
        {
            return Err(invalid());
        }
        let mut previous = HashSet::new();
        for lease in &saved.held_fire {
            if lease.peer == 0 || lease.peer >= saved.next_peer ||
                !previous.insert(lease.peer) ||
                !self.side_tokens.contains_key(&lease.side) ||
                lease.world_id != world_id ||
                lease.episode_revision != revision
            {
                return Err(invalid());
            }
        }
        let mut released = HashSet::new();
        for order in &saved.pending_releases {
            if order.peer == 0 || order.peer >= saved.next_peer ||
                !released.insert(order.peer) || previous.contains(&order.peer) ||
                !self.side_tokens.contains_key(&order.side) ||
                order.request_id != 0 ||
                !matches!(&order.action,
                    NativePlayerInput::HeldFire(input) if
                        input.world_id == world_id &&
                        input.episode_revision == revision &&
                        input.controller_id == order.peer &&
                        matches!(&input.action, NativeHeldFireAction::End))
            {
                return Err(invalid());
            }
        }
        // Every lease represented a peer-owned active weapon at the
        // checkpoint. Its socket did not survive the process, so End must
        // be applied against the restored CombatWorld, not a fresh World.
        let mut releases = saved.pending_releases;
        for lease in saved.held_fire {
            releases.push(lease.release());
        }
        self.host_session_id = saved.host_session_id;
        self.recovered_session = true;
        self.next_peer = saved.next_peer;
        self.pending_releases = releases;
        // The recovered World retains its authored cue. The old TCP
        // peers cannot acknowledge on behalf of newly authenticated ones.
        self.briefing_ready.clear();
        self.measurements = saved.measurements;
        self.measurements.held_fire_disconnect_releases += previous.len() as u64;
        self.measurements.disconnected_peers += saved.connected_peers as u64;
        self.measurements.recovered_sessions += 1;
        self.measurements.recovered_peer_connections += saved.connected_peers as u64;
        self.measurements.recovered_held_fire_releases += previous.len() as u64;
        self.last_state = None;
        self.force_initial_state = true;
        Ok(())
    }

    /// The authored Level selects music, never a network client's request.
    pub fn set_music_location(&mut self, location: Option<NativeMusicLocation>) {
        self.music_location = location;
    }

    pub fn configured_sides(&self) -> impl Iterator<Item = i32> + '_ {
        self.side_tokens.keys().copied()
    }

    /// Poll at the authoritative world's fixed-tick boundary. An expired
    /// episode cannot retain a peer's old gesture lease. Disconnect releases
    /// are returned as canonical inputs before newly received player orders.
    pub fn poll(&mut self, world_id: WorldId, revision: u64,
                live_controllers: &[u64], briefing: Option<(i32,u8)>) -> Vec<RemoteOrder> {
        let poll_started = Instant::now();
        let mut retired_no_live = Vec::new();
        self.held_fire.retain(|&peer, lease| {
            if lease.world_id != world_id || lease.episode_revision != revision {
                return false;
            }
            if !live_controllers.contains(&peer) {
                retired_no_live.push(peer);
                return false;
            }
            true
        });
        self.measurements.held_fire_no_live_weapons_releases +=
            retired_no_live.len() as u64;
        // A still-connected player must learn that its final held weapon
        // died. Otherwise it keeps sending Aim for a retired gesture.
        // Episode turnover is already handled by the new-world snapshot.
        for peer in &mut self.peers {
            if retired_no_live.contains(&peer.id) && !peer.closed {
                let _ = peer.enqueue(&json!({
                    "kind": "held_fire_released",
                    "reason": "no_live_weapons",
                }));
            }
        }
        let mut orders = std::mem::take(&mut self.pending_releases);
        orders.retain(|order| order.action.episode() == (world_id, revision));
        // A connection flood must not monopolize the authoritative game
        // tick. Unaccepted sockets remain queued by the OS.
        for _ in 0..MAX_ACCEPTS_PER_POLL {
            let (socket, _) = match self.listener.accept() {
                Ok(accepted) => accepted,
                Err(ref error) if error.kind() == io::ErrorKind::WouldBlock => break,
                Err(_) => break,
            };
            // Command acknowledgements and side-filtered state are small
            // interactive frames. Nagle buffering here would defer replies
            // despite the client's already-low-latency socket settings.
            if self.peers.len() >= MAX_PEERS ||
                socket.set_nonblocking(true).is_err() ||
                socket.set_nodelay(true).is_err()
            {
                self.measurements.rejected_connections += 1;
                continue;
            }
            let id = self.next_peer;
            self.next_peer += 1;
            self.measurements.connections_accepted += 1;
            self.peers.push(Peer {
                id, socket, side: None, last_request_id: 0, incoming: Vec::new(),
                outgoing: Vec::new(), written: 0, writing_state: false,
                reliable: VecDeque::new(), reliable_bytes: 0,
                newest_state: None, closed: false,
                accepted_at: Instant::now(),
            });
        }

        let mut newly_authenticated = false;
        for peer in &mut self.peers {
            if peer.closed { continue; }
            let mut peer_orders = 0;
            // Never allow an unauthenticated socket to occupy a slot forever.
            if peer.side.is_none() && peer.accepted_at.elapsed().as_secs() > 10 {
                peer.closed = true;
                continue;
            }
            let mut budget = READ_BYTES_PER_POLL;
            let mut chunk = [0u8; 8192];
            while budget > 0 {
                let limit = budget.min(chunk.len());
                match peer.socket.read(&mut chunk[..limit]) {
                    Ok(0) => { peer.closed = true; break; }
                    Ok(n) => {
                        budget -= n;
                        peer.incoming.extend_from_slice(&chunk[..n]);
                        if peer.incoming.len() > MAX_LINE * 2 {
                            peer.closed = true;
                            break;
                        }
                    }
                    Err(ref e) if e.kind() == io::ErrorKind::WouldBlock => break,
                    Err(ref e) if e.kind() == io::ErrorKind::Interrupted => continue,
                    Err(_) => { peer.closed = true; break; }
                }
            }
            let mut start = 0;
            while !peer.closed {
                let Some(end) = peer.incoming[start..].iter()
                    .position(|&c| c == b'\n').map(|i| start + i) else { break };
                if end - start > MAX_LINE {
                    peer.closed = true;
                    break;
                }
                let message: Result<WireInput, _> =
                    serde_json::from_slice(&peer.incoming[start..end]);
                start = end + 1;
                match message {
                    Ok(WireInput::Hello { token }) if peer.side.is_none() => {
                        let side = self.side_tokens.iter().find_map(|(&side, secret)|
                            constant_time_matches(secret.as_bytes(), token.as_bytes())
                                .then_some(side));
                        if let Some(side) = side {
                            peer.side = Some(side);
                            newly_authenticated = true;
                            self.measurements.authenticated_peers += 1;
                            let _ = peer.enqueue(&json!({
                                "kind": "welcome", "protocol": 1,
                                "mode": "authenticated_player_side", "side": side,
                                "host_session_id": self.host_session_id,
                                "recovered_host": self.recovered_session,
                            }));
                        } else {
                            peer.closed = true;
                        }
                    }
                    Ok(WireInput::BriefingReady {
                        request_id, world_id: claimed_world,
                        episode_revision: claimed_revision, sequence,
                    }) if peer.side.is_some() => {
                        if request_id <= peer.last_request_id {
                            self.measurements.briefing_rejected_acks += 1;
                            let _ = peer.enqueue(&json!({
                                "kind":"briefing_result","request_id":request_id,
                                "processed":false,
                                "feedback":"duplicate or out-of-order briefing acknowledgment",
                            }));
                            continue;
                        }
                        peer.last_request_id = request_id;
                        if peer_orders >= MAX_ORDERS_PER_PEER_POLL {
                            peer.closed = true;
                            break;
                        }
                        peer_orders += 1;
                        let valid = claimed_world == world_id.0 &&
                            claimed_revision == revision &&
                            briefing == Some((peer.side.expect("authenticated"),sequence));
                        if valid {
                            self.briefing_ready.insert(peer.id,
                                (claimed_world,claimed_revision,sequence));
                            self.measurements.briefing_ready_acks += 1;
                        } else {
                            self.measurements.briefing_rejected_acks += 1;
                        }
                        let _ = peer.enqueue(&json!({
                            "kind":"briefing_result","request_id":request_id,
                            "processed":valid,
                            "feedback":if valid {
                                "Campaign briefing acknowledged"
                            } else { "Stale or unauthorized campaign briefing" },
                        }));
                    }
                    Ok(WireInput::Input { request_id, mut action }) if peer.side.is_some() => {
                        // No delayed/doubled request may reissue a movement
                        // or special under the same authenticated connection.
                        if request_id <= peer.last_request_id {
                            self.measurements.orders_rejected += 1;
                            let _ = peer.enqueue(&json!({
                                "kind": "input_result", "request_id": request_id,
                                "processed": false,
                                "feedback": "duplicate or out-of-order request",
                            }));
                            continue;
                        }
                        peer.last_request_id = request_id;
                        if orders.len() >= MAX_ORDERS_PER_POLL ||
                           peer_orders >= MAX_ORDERS_PER_PEER_POLL {
                            peer.closed = true;
                            break;
                        }
                        let side = peer.side.expect("checked authenticated peer");
                        // Point fire and held fire use the same peer-scoped
                        // canonical weapon ownership. Never accept a
                        // controller identity supplied by the network.
                        if let NativePlayerInput::Fire(input) = &mut action {
                            input.controller_id = peer.id;
                        }
                        if let NativePlayerInput::HeldFire(input) = &mut action {
                            // Wire-supplied controller identifiers are never
                            // trusted. The server binds gesture identity to
                            // the authenticated TCP peer before dispatch and
                            // before the deterministic player trace records it.
                            input.controller_id = peer.id;
                            self.measurements.held_fire_requests += 1;
                            let permitted = match &input.action {
                                NativeHeldFireAction::Begin { squads, target } => {
                                    if squads.is_empty() || !target.is_finite() ||
                                        input.world_id != world_id ||
                                        input.episode_revision != revision ||
                                        self.held_fire.contains_key(&peer.id) { false }
                                    else {
                                        self.held_fire.insert(peer.id, HeldFireLease {
                                            peer: peer.id, side, world_id, episode_revision: revision,
                                            begin_request_id: request_id,
                                        });
                                        self.measurements.concurrent_held_fire_peak =
                                            self.measurements.concurrent_held_fire_peak
                                                .max(self.held_fire.len());
                                        true
                                    }
                                }
                                NativeHeldFireAction::Aim { target } =>
                                    target.is_finite() && self.held_fire.get(&peer.id)
                                        .is_some_and(|lease| lease.matches(peer.id, side, input)),
                                NativeHeldFireAction::End => {
                                    if self.held_fire.get(&peer.id)
                                        .is_some_and(|lease| lease.matches(peer.id, side, input)) {
                                        self.held_fire.remove(&peer.id);
                                        true
                                    } else { false }
                                }
                            };
                            if !permitted {
                                self.measurements.orders_rejected += 1;
                                self.measurements.held_fire_rejected += 1;
                                let _ = peer.enqueue(&json!({
                                    "kind": "input_result", "request_id": request_id,
                                    "processed": false,
                                    "feedback": "held fire belongs to another player or episode",
                                }));
                                continue;
                            }
                        }
                        peer_orders += 1;
                        self.measurements.orders_received += 1;
                        orders.push(RemoteOrder {
                            peer: peer.id, side: peer.side.expect("checked authenticated peer"),
                            request_id, action,
                        });
                    }
                    _ => { peer.closed = true; break; }
                }
            }
            if start > 0 { peer.incoming.drain(..start); }
            if peer.incoming.len() > MAX_LINE { peer.closed = true; }
            if !peer.closed { self.measurements.state_bytes += peer.flush(); }
        }
        if newly_authenticated {
            self.force_initial_state = true;
        }
        self.reap();
        // A peer that was already firing when it disconnected must release
        // before another peer's new Begin at this same tick boundary. But
        // a newly admitted Begin from a peer that disconnected mid-poll
        // must run before THAT peer's release. Preserve each peer's order
        // while allowing an unrelated player to take over a freed squad.
        let mut before = Vec::new();
        let mut after = Vec::new();
        for release in self.pending_releases.drain(..).filter(|order|
            order.action.episode() == (world_id, revision)) {
            if orders.iter().any(|order|
                order.peer == release.peer &&
                matches!(&order.action, NativePlayerInput::HeldFire(input)
                    if matches!(&input.action, NativeHeldFireAction::Begin { .. }))) {
                after.push(release);
            } else {
                before.push(release);
            }
        }
        before.extend(orders);
        before.extend(after);
        self.measurements.input_poll_wall_seconds +=
            poll_started.elapsed().as_secs_f64();
        before
    }

    /// Only the already-authenticated campaign viewers can release the
    /// original physical World from its source briefing. A disconnected
    /// viewer cannot hold up the mission after their socket is reaped.
    pub fn briefing_waiting(&mut self, world_id: WorldId, revision: u64,
        owner_side: i32, sequence: u8) -> bool
    {
        let required=(world_id.0,revision,sequence);
        let mut viewers=0usize;
        let mut waiting=false;
        for peer in self.peers.iter().filter(|peer|
            !peer.closed && peer.side==Some(owner_side)) {
            viewers+=1;
            if self.briefing_ready.get(&peer.id)!=Some(&required) {
                waiting=true;
            }
        }
        let waiting=waiting||viewers==0;
        if waiting { self.measurements.briefing_wait_polls+=1; }
        waiting
    }

    /// No repeated large idle snapshots. A newly authenticated viewer
    /// receives the usual host-fog-filtered current World on connection.
    pub fn broadcast_initial_if_pending(&mut self,
        view: &NativeRenderWorldView<'_>, bounds: Aabb)
    {
        if self.force_initial_state { self.broadcast(view,bounds); }
    }

    pub fn held_fire_active(&self) -> bool { !self.held_fire.is_empty() }

    pub fn reply(&mut self, peer_id: u64, request_id: u64, result: Result<&str, &str>) {
        // Authoritative Begin rejection must revoke its provisional lease,
        // or one invalid squad selection could lock out all other clients.
        if self.held_fire.get(&peer_id).is_some_and(|lease|
            (lease.begin_request_id == request_id && result.is_err()) ||
            matches!(result, Err("No selected weapons accepted held fire"))) {
            self.held_fire.remove(&peer_id);
            self.measurements.held_fire_rejected += 1;
        }
        let Some(peer) = self.peers.iter_mut()
            .find(|peer| peer.id == peer_id && peer.side.is_some()) else { return };
        if result.is_ok() { self.measurements.orders_processed += 1; }
        else { self.measurements.orders_rejected += 1; }
        let reply = match result {
            Ok(feedback) => json!({
                "kind": "input_result", "request_id": request_id,
                "processed": true, "feedback": feedback,
            }),
            Err(message) => json!({
                "kind": "input_result", "request_id": request_id,
                "processed": false, "feedback": message,
            }),
        };
        let _ = peer.enqueue(&reply);
        self.measurements.state_bytes += peer.flush();
    }

    pub fn broadcast(&mut self, view: &NativeRenderWorldView<'_>, bounds: Aabb) {
        // A disconnected side must not retain old live vision or resurrect
        // decaying death masks when its player authenticates again much
        // later. Keep only currently authenticated, connected side caches;
        // cooperative peers of the same side continue sharing one mask.
        let sides=self.peers.iter().filter(|peer|!peer.closed)
            .filter_map(|peer|peer.side).collect::<HashSet<_>>();
        if sides.is_empty() {
            self.pending_audio.clear();
            self.fog_by_side.clear();
            return;
        }
        let episode = (view.world_id.0, view.episode_revision);
        if self.last_state.is_some_and(|previous| previous != episode) {
            self.pending_audio.clear();
            self.fog_by_side.clear();
        }
        self.fog_by_side.retain(|side,_|sides.contains(side));
        self.pending_audio.retain(|side,_|sides.contains(side));
        // Build the same side-scoped sight masks for audio and world-state
        // snapshots. Track changes every physics tick, not every fifth
        // network frame, so a death starts its fade at the true source tick.
        let mut vision = HashMap::<i32, RemoteFogSources>::new();
        for &side in &sides {
            let source = self.fog_by_side.entry(side).or_default()
                .sources(view, side, &mut self.measurements);
            vision.insert(side, source);
        }
        // This runs at each physical tick, including the four ticks with no
        // state frame. Carry short-lived sounds to the next presentation frame.
        if !view.combat.events().is_empty() {
            // Build an event-local physical lookup once across all player
            // sides, not once for each simultaneous weapon launch and peer.
            let needs_shots = view.combat.events().iter().any(|event| matches!(event,
                CombatEvent::PolicyWeaponFired { .. } |
                CombatEvent::GameWeaponFired { .. } |
                CombatEvent::FireTankExploded { .. } |
                CombatEvent::BombDetonated { .. }));
            let shot_positions: HashMap<i64, (u8, Vec2)> = if needs_shots {
                view.combat.render_projectiles()
                    .map(|(shot, body)| (
                        shot.entity_id, (shot.projectile_type, body.motion.position),
                    )).collect()
            } else { HashMap::new() };
            for &side in &sides {
                collect_remote_audio(view, side, &shot_positions,
                    vision.get(&side).expect("authenticated side has sight"),
                    self.pending_audio.entry(side).or_default(),
                    &mut self.measurements);
            }
        }
        if self.last_state == Some(episode) &&
            view.tick % STATE_EVERY_TICKS != 0 &&
            view.terminal_result.is_none() &&
            !self.force_initial_state { return; }
        self.last_state = Some(episode);
        self.force_initial_state = false;
        // Audio is lossy presentation; authoritative orders and replies keep
        // their existing reliable, bounded channel.
        let pending_audio = std::mem::take(&mut self.pending_audio);
        let music_location = self.music_location;
        let requires_briefing_ack=self.dedicated_briefing_gate &&
            view.tick==0 && view.terminal_result.is_none() &&
            view.campaign_dialogue.is_some_and(|(_,cue)|cue.is_briefing());
        let mut snapshots = HashMap::<i32, Vec<u8>>::new();
        let measurements = &mut self.measurements;
        for peer in &mut self.peers {
            let Some(side) = peer.side else { continue };
            let encoded = snapshots.entry(side).or_insert_with(|| {
                let started = Instant::now();
                let cues = pending_audio.get(&side).map(Vec::as_slice).unwrap_or(&[]);
                let payload = serde_json::to_vec(&current_state(
                    view, bounds, side, music_location, cues,
                    vision.get(&side).expect("authenticated side has sight"),
                    requires_briefing_ack && view.campaign_dialogue_side==Some(side),
                    measurements,
                )).unwrap_or_default();
                measurements.snapshot_projection_wall_seconds +=
                    started.elapsed().as_secs_f64();
                measurements.snapshot_unique_encoded_bytes += payload.len() as u64;
                payload
            });
            let socket_started = Instant::now();
            if peer.enqueue_state(encoded) {
                measurements.states_superseded += 1;
            }
            if !peer.closed {
                measurements.state_bytes += peer.flush();
            }
            measurements.snapshot_socket_wall_seconds +=
                socket_started.elapsed().as_secs_f64();
        }
        self.measurements.state_frames += 1;
        self.reap();
    }

    /// Notify only the authenticated campaign owner after the immutable
    /// battle receipt has been synced and committed. No pending, defeated,
    /// or replay-only battle can be presented as saved progress. An opposing
    /// multiplayer side cannot inspect the player's career rewards.
    pub fn publish_campaign_receipt(
        &mut self, player_side: i32, outcome: &Value, banked_minerals: Option<u64>,
    ) {
        let receipt = json!({
            "kind": "campaign_receipt",
            "campaign_outcome": outcome,
            "banked_minerals": banked_minerals,
        });
        for peer in self.peers.iter_mut().filter(|peer|
            !peer.closed && peer.side == Some(player_side)
        ) {
            let _ = peer.enqueue(&receipt);
            if !peer.closed {
                self.measurements.state_bytes += peer.flush();
            }
        }
        self.reap();
    }

    /// Deliver the Level's existing terminal result independently of lossy
    /// world snapshots. This is called only AFTER successful finalization
    /// and the durable summary commit, alongside the campaign receipt.
    /// The same actual Level result and source labels used by local play
    /// remain authoritative for both authenticated opposing commanders.
    pub fn publish_battle_result(&mut self, view: &NativeRenderWorldView<'_>) {
        let Some(result) = view.terminal_result else { return };
        for peer in self.peers.iter_mut().filter(|peer|
            !peer.closed && peer.side.is_some()
        ) {
            let _ = peer.enqueue(&json!({
                "kind": "battle_result",
                "protocol": 1,
                "viewer_side": peer.side,
                "world_id": view.world_id.0,
                "episode_revision": view.episode_revision,
                "winner_side": result.winner_side,
                "label": crate::presentation::battle_outcome_label(result),
                "reason": crate::presentation::battle_outcome_reason(view, result),
            }));
            if !peer.closed {
                self.measurements.battle_result_receipts += 1;
                self.measurements.state_bytes += peer.flush();
            }
        }
        self.reap();
    }

    /// A failed or budget-exhausted host must explicitly distinguish its
    /// interrupted lifecycle from the source Level's genuine terminal
    /// outcome. Never manufacture a winner or campaign progression.
    pub fn publish_interrupted_battle(&mut self) {
        for peer in self.peers.iter_mut().filter(|peer|
            !peer.closed && peer.side.is_some()
        ) {
            let _ = peer.enqueue(&json!({
                "kind": "battle_interrupted",
                "protocol": 1,
                "viewer_side": peer.side,
                "reason": "Host ended before an authoritative battle result",
            }));
            if !peer.closed {
                self.measurements.interrupted_battle_notices += 1;
                self.measurements.state_bytes += peer.flush();
            }
        }
        self.reap();
    }

    /// After the authoritative Level finishes, the host no longer advances
    /// ticks. Give its already-queued final world_state a bounded opportunity
    /// to leave each nonblocking socket before the transport is dropped.
    /// This does not acknowledge a campaign transaction, replay simulation,
    /// or hold up the game indefinitely for an unresponsive viewer.
    pub fn drain_terminal_output(&mut self, deadline: Duration) {
        let start = Instant::now();
        let pending = loop {
            let mut pending = 0_usize;
            for peer in &mut self.peers {
                if peer.closed || peer.side.is_none() { continue; }
                self.measurements.state_bytes += peer.flush();
                if !peer.closed && (
                    peer.written < peer.outgoing.len() ||
                    !peer.reliable.is_empty() ||
                    peer.newest_state.is_some()
                ) {
                    pending += 1;
                }
            }
            if pending == 0 || start.elapsed() >= deadline {
                break pending;
            }
            std::thread::sleep(Duration::from_millis(2));
        };
        self.measurements.terminal_drain_wall_seconds = start.elapsed().as_secs_f64();
        self.measurements.terminal_state_pending_peers = pending;
        self.reap();
    }

    fn reap(&mut self) {
        let disconnected: Vec<_> = self.held_fire.keys().copied()
            .filter(|&owner| self.peers.iter().any(|peer|
                peer.id == owner && peer.closed))
            .collect();
        for owner in disconnected {
            let lease = self.held_fire.remove(&owner)
                .expect("disconnected owner was present");
            // Queued on the authoritative thread, never on the socket.
            // Each release clears only its owner's real live weapons.
            self.pending_releases.push(lease.release());
            self.measurements.held_fire_disconnect_releases += 1;
        }
        let before = self.peers.len();
        self.peers.retain(|peer| !peer.closed);
        self.briefing_ready.retain(|peer,_|
            self.peers.iter().any(|client| client.id==*peer));
        self.measurements.disconnected_peers += (before - self.peers.len()) as u64;
    }
}

fn constant_time_matches(expected: &[u8], actual: &[u8]) -> bool {
    let mut difference = expected.len() ^ actual.len();
    for index in 0..expected.len() {
        difference |= usize::from(expected[index] != actual.get(index).copied().unwrap_or(0));
    }
    difference == 0
}

/// The local native renderer and LAN host consume the same shrinking sight
/// mask after a player-owned ship dies. Only the host determines which live
/// combat entities fall inside these masks; clients cannot revive old intel.
#[derive(Clone, Copy)]
struct RemoteFogLive {
    position: Vec2,
    radius: f32,
    seen_tick: u64,
}

#[derive(Default)]
struct RemoteFogSight {
    live: HashMap<i64, RemoteFogLive>,
    fades: Vec<NativeFogFade>,
}

impl RemoteFogSight {
    fn sources(&mut self, view: &NativeRenderWorldView<'_>, side: i32,
               measurements: &mut RemotePlayMeasurements) -> RemoteFogSources {
        if !view.player_authority.player_fog_of_war_enabled() {
            self.live.clear();
            self.fades.clear();
            return RemoteFogSources::new(Vec::new(), measurements);
        }
        for (ship, body) in view.combat.render_ships() {
            let Some(owner) = view.roster.owning_squad(ship.entity_id) else { continue };
            if !owns_player_side(view, owner, side) { continue; }
            let Some(radius) = view.player_authority.player_fog_vision_radius(ship.entity_id)
                .filter(|radius| *radius > 0.0 && radius.is_finite()) else { continue };
            if !body.motion.position.is_finite() { continue; }
            self.live.insert(ship.entity_id, RemoteFogLive {
                position: body.motion.position, radius, seen_tick: view.tick,
            });
        }
        // Exactly as the local viewer: ordinary disappearance grants no
        // extra sight. A dead original source leaves a shrinking mask only.
        self.live.retain(|&id, last| {
            if last.seen_tick == view.tick { return true; }
            if view.combat.ship(id).is_some_and(|ship| ship.vitals.is_dead()) {
                self.fades.push(NativeFogFade {
                    position: last.position, radius: last.radius,
                    born_tick: view.tick,
                });
            }
            false
        });
        self.fades.retain(|fade|
            fade.current_radius(view.tick, view.fixed_step_seconds) >= 1.5);
        measurements.fog_fading_source_ticks += self.fades.len() as u64;
        measurements.fog_fading_sources_peak =
            measurements.fog_fading_sources_peak.max(self.fades.len());
        let mut circles = Vec::with_capacity(self.live.len() + self.fades.len());
        circles.extend(self.live.values().map(|s| (s.position, s.radius)));
        circles.extend(self.fades.iter().map(|s|
            (s.position, s.current_radius(view.tick, view.fixed_step_seconds))));
        RemoteFogSources::new(circles, measurements)
    }
}

/// The remote snapshot has no camera-specific fog framebuffer: visible
/// enemy/object/shot positions must be checked in world space. This indexes
/// the SAME exact source-vision circles, once per authenticated side:
/// direct scan for small fleets; X-sorted pruning for medium fleets; and a
/// sparse 2D center grid for large fleets (including long vertical columns,
/// where X pruning alone cannot help). No new visibility rule or physics.
struct RemoteFogSources {
    sources: Vec<(Vec2, f32)>,
    largest_radius: f32,
    indexed: bool,
    grid: Option<HashMap<(i64, i64), Vec<usize>>>,
}

impl RemoteFogSources {
    fn grid_cell(point: Vec2, radius: f64) -> Option<(i64, i64)> {
        if !point.is_finite() || !radius.is_finite() || radius <= 0.0 {
            return None;
        }
        let x = (f64::from(point.x) / radius).floor();
        let y = (f64::from(point.y) / radius).floor();
        // Guard checked +/-1 neighbor lookups and extreme source coordinate
        // ratios. The X-sorted scan remains the exact conservative fallback.
        let limit = (i64::MAX / 4) as f64;
        (x.abs() < limit && y.abs() < limit)
            .then_some((x as i64, y as i64))
    }

    fn new(mut sources: Vec<(Vec2, f32)>, stats: &mut RemotePlayMeasurements) -> Self {
        // Non-finite source positions never covered a physical target in
        // the original squared-distance predicate either.
        sources.retain(|(point, _)| point.is_finite());
        let largest_radius = sources.iter().fold(0.0_f32, |radius, source|
            radius.max(source.1));
        // Extreme but finite authored sight radii can overflow f32 radius².
        // The source predicate then treats infinity-distance pairs
        // differently; preserve its behavior by using the old exact scan.
        let indexed = sources.len() > 8 &&
            (largest_radius * largest_radius).is_finite();
        if indexed {
            sources.sort_unstable_by(|left, right|
                left.0.x.total_cmp(&right.0.x));
            stats.fog_visibility_sources_indexed += sources.len() as u64;
        }
        // The grid is an optimization of the exact squared-distance
        // predicate. If that predicate overflows f32, even a far-away
        // source can compare as infinity <= infinity; spatial culling
        // would silently change the source fog rule. Use the direct scan
        // in the same cases that prohibit X-indexed pruning.
        let grid = if indexed && sources.len() > 24 {
            let mut buckets = HashMap::<(i64, i64), Vec<usize>>::new();
            let mut valid = true;
            for (index, (position, _)) in sources.iter().enumerate() {
                let Some(cell) = Self::grid_cell(*position, f64::from(largest_radius))
                else { valid = false; break };
                buckets.entry(cell).or_default().push(index);
            }
            valid.then_some(buckets)
        } else { None };
        Self { sources, largest_radius, indexed, grid }
    }

    fn covers(&self, target: Vec2, stats: &mut RemotePlayMeasurements) -> bool {
        stats.fog_visibility_queries += 1;
        if !target.is_finite() { return false; }
        if let Some(grid) = &self.grid {
            if let Some((x, y)) = Self::grid_cell(
                target, f64::from(self.largest_radius),
            ) {
                stats.fog_visibility_grid_queries += 1;
                // If a source circle covers the target, its center differs
                // by at most one cell on each axis. The nine lookups yield
                // the EXACT same distance check as a full scan, with no
                // source duplication or per-query temporary allocation.
                for dy in -1..=1 {
                    for dx in -1..=1 {
                        if let Some(indices) = grid.get(&(x + dx, y + dy)) {
                            for &index in indices {
                                let (center, radius) = self.sources[index];
                                stats.fog_visibility_candidate_checks += 1;
                                if (target - center).length_squared() <= radius * radius {
                                    return true;
                                }
                            }
                        }
                    }
                }
                return false;
            }
        }
        let candidates = if self.indexed {
            let lower = target.x - self.largest_radius;
            let upper = target.x + self.largest_radius;
            let first = self.sources.partition_point(|(center, _)| center.x < lower);
            let last = self.sources.partition_point(|(center, _)| center.x <= upper);
            &self.sources[first..last]
        } else {
            &self.sources
        };
        for &(center, radius) in candidates {
            stats.fog_visibility_candidate_checks += 1;
            if (target - center).length_squared() <= radius * radius {
                return true;
            }
        }
        false
    }
}

/// State snapshots are a read-only projection of the same live CombatWorld.
/// Source FogOfWar restricts ship coordinates to ships actually within live
/// friendly sight masks; previous discoveries alone do not reveal position.
fn owns_player_side(view: &NativeRenderWorldView<'_>, owner: SquadLifetime, side: i32) -> bool {
    view.roster.get(owner).is_some_and(|squad| squad.side == side &&
        view.player_authority.player_permissions(owner)
            .is_some_and(|permission| permission.user_controlled))
}

/// Host-only ephemeral cues, restricted to the same visibility as combat
/// geometry. Unlocalized events are deliberately not sent to opponents.
fn collect_remote_audio(
    view: &NativeRenderWorldView<'_>, side: i32,
    shots: &HashMap<i64, (u8, Vec2)>, sources: &RemoteFogSources,
    pending: &mut Vec<Value>, measurements: &mut RemotePlayMeasurements,
) {
    for event in view.combat.events() {
        let mut launched = None;
        let mut impact = None;
        let (effect, point) = match event {
            CombatEvent::Hit(hit) if hit.delta.applied_damage > 0 => {
                // No separate hit simulation on LAN clients. Send only
                // authenticated host damage with the same fog predicate as
                // visible physical ships, over the bounded cue stream.
                impact = Some((hit.projectile_id, hit.target_id, hit.delta.killed));
                ("impact", view.combat.ship_body(hit.target_id)
                    .map(|body| body.motion.position))
            },
            CombatEvent::StaticObstacleImpact { projectile_id, obstacle_id } |
            CombatEvent::AsteroidDamaged { projectile_id, obstacle_id, .. } |
            CombatEvent::MapObjectDamaged {
                projectile_id, object_id: obstacle_id, ..
            } => {
                impact = Some((*projectile_id, *obstacle_id, false));
                ("impact", view.combat.obstacle_body(*obstacle_id)
                    .map(|body| body.pose.position))
            },
            CombatEvent::ShipDied { victim_id, .. } => (
                if view.combat.ship(*victim_id)
                    .is_some_and(|ship| matches!(ship.ship_type, 7 | 10 | 11 | 17)) {
                    "huge_explosion"
                } else { "explosion" },
                view.combat.ship_body(*victim_id)
                    .map(|body| body.motion.position),
            ),
            CombatEvent::StrikerBombDropped { ship_id, .. } => (
                "striker_bomb",
                view.combat.ship_body(*ship_id).map(|body| body.motion.position),
            ),
            CombatEvent::ShipWarped { ship_id, .. } => (
                "warp",
                view.combat.ship_body(*ship_id).map(|body| body.motion.position),
            ),
            // Captures real launches even when a shot dies between two
            // five-tick presentation snapshots. The existing client sound
            // table remains the one authority for projectile sound families.
            CombatEvent::GameWeaponFired { projectile_id, .. } |
            CombatEvent::PolicyWeaponFired { projectile_id, .. } => {
                let Some(&(kind, point)) = shots.get(projectile_id) else { continue };
                launched = Some((*projectile_id, kind));
                ("projectile", Some(point))
            },
            CombatEvent::FireTankExploded { explosion_projectile_id, .. } => (
                "huge_explosion",
                shots.get(explosion_projectile_id).map(|shot| shot.1),
            ),
            CombatEvent::BombDetonated { projectile_id, .. } => (
                "huge_explosion",
                shots.get(projectile_id).map(|shot| shot.1),
            ),
            _ => continue,
        };
        let Some(point) = point.filter(|point| point.is_finite()) else { continue };
        // A player must see effects on their own last vision-providing
        // ship. Death, damage or departure may remove the live sight source
        // in this very physics tick. Authenticate ownership through the
        // source roster, never the client's old discovery or visibility data.
        // Unowned and enemy effects still require the current fog predicate.
        let affected_ship = match event {
            CombatEvent::Hit(hit) => Some(hit.target_id),
            CombatEvent::ShipDied { victim_id, .. } => Some(*victim_id),
            CombatEvent::StrikerBombDropped { ship_id, .. } |
            CombatEvent::ShipWarped { ship_id, .. } => Some(*ship_id),
            _ => None,
        };
        let own_ship_event = affected_ship
            .and_then(|id| view.roster.owning_squad(id))
            .is_some_and(|owner| owns_player_side(view, owner, side));
        if !own_ship_event && view.player_authority.player_fog_of_war_enabled() &&
            !sources.covers(point, measurements) { continue; }
        if let Some((id, _)) = launched {
            if pending.iter().any(|cue| cue["projectile_id"].as_i64() == Some(id)) {
                continue;
            }
        }
        // Impact flashes are lower priority than original sound cues; an
        // active large battle must not lose a death or launch sound simply
        // because thirty-two physical hits filled the cosmetic buffer.
        let displaced = if pending.len() == MAX_PENDING_AUDIO_EVENTS {
            if matches!(event, CombatEvent::ShipDied { .. }) {
                pending.iter().position(|cue| cue["effect"] == "impact")
                    .or_else(|| pending.iter().position(|cue|
                        cue["effect"] == "projectile"))
            } else if effect != "impact" {
                pending.iter().position(|cue| cue["effect"] == "impact")
            } else { None }
        } else { None };
        if pending.len() < MAX_PENDING_AUDIO_EVENTS || displaced.is_some() {
            let mut cue = json!({
                "effect": effect, "x": point.x, "y": point.y, "tick": view.tick,
            });
            if let Some((id, kind)) = launched {
                cue["projectile_id"] = json!(id);
                cue["projectile_type"] = json!(kind);
            }
            if let Some((projectile_id, target_id, fatal)) = impact {
                cue["combat_impact"] = json!({
                    "projectile_id": projectile_id,
                    "target_id": target_id,
                    "fatal": fatal,
                });
            }
            // The host already buffers these side-filtered audio cues
            // across its five-tick state cadence. Attach an optional
            // authenticated visual death receipt to that same bounded
            // event, rather than sampling only the latest frame (which
            // misses four out of five short-lived combat death events).
            if let CombatEvent::ShipDied { victim_id, .. } = event {
                if let (Some(ship), Some(body)) = (
                    view.combat.ship(*victim_id),
                    view.combat.ship_body(*victim_id),
                ) {
                    let radius = body.primary_bounds()
                        .map(|bounds| (bounds.max.x - bounds.min.x)
                            .max(bounds.max.y - bounds.min.y) * 0.5)
                        .unwrap_or(3.0);
                    cue["ship_death"] = json!({
                        "id": victim_id,
                        "type": ship.ship_type, "side": ship.side,
                        "x": body.motion.position.x,
                        "y": body.motion.position.y,
                        "rotation": body.motion.rotation_degrees,
                        "radius": radius,
                        "color_rgb": view.roster.owning_squad(*victim_id)
                            .and_then(|owner|
                                view.player_authority.player_squad_color_rgb(owner)),
                    });
                }
            }
            if let Some(index) = displaced {
                let removed = pending.remove(index);
                measurements.audio_events_dropped += 1;
                if removed["effect"] == "impact" {
                    measurements.combat_impacts_dropped += 1;
                }
                if matches!(event, CombatEvent::ShipDied { .. }) {
                    measurements.ship_deaths_prioritized += 1;
                }
            }
            pending.push(cue);
            measurements.audio_events_queued += 1;
            if impact.is_some() { measurements.combat_impacts_queued += 1; }
        } else {
            measurements.audio_events_dropped += 1;
            if impact.is_some() { measurements.combat_impacts_dropped += 1; }
        }
    }
}

/// Read-only projection of the authoritative primary physics hull. The
/// same bounded collider schema is used by ships and obstacles: LAN cursor
/// selection must not substitute a fixed-size point for wide live hulls.
fn wire_primary_hull(shape: &ShipShape) -> Value {
    match shape {
        ShipShape::Circle { offset, radius } =>
            json!({"kind":"circle","offset":[offset.x,offset.y],"radius":radius}),
        ShipShape::Box { offset, half_extents } =>
            json!({"kind":"box","offset":[offset.x,offset.y],
                   "half_extents":[half_extents.x,half_extents.y]}),
        ShipShape::Polygon(vertices) if vertices.len()<=64 =>
            json!({"kind":"polygon","vertices":vertices.iter()
                .map(|point|[point.x,point.y]).collect::<Vec<_>>()}),
        // The caller also emits the actual cached primary AABB for older
        // imported polygons with more vertices. Never truncate a hull.
        ShipShape::Polygon(_) => Value::Null,
    }
}

fn current_state(
    view: &NativeRenderWorldView<'_>, bounds: Aabb, side: i32,
    music_location: Option<NativeMusicLocation>, audio_events: &[Value],
    sources: &RemoteFogSources, requires_briefing_ack: bool,
    measurements: &mut RemotePlayMeasurements,
) -> Value {
    let viewer = Some(side);
    let fog = view.player_authority.player_fog_of_war_enabled();
    let mut visible_ids = HashSet::<i64>::new();
    let ships: Vec<_> = view.combat.render_ships().filter_map(|(ship, body)| {
        let owned = view.roster.owning_squad(ship.entity_id)
            .is_some_and(|owner| owns_player_side(view, owner, side));
        let visible = owned || !fog ||
            sources.covers(body.motion.position, measurements);
        if !visible { return None; }
        visible_ids.insert(ship.entity_id);
        let discovered = viewer.is_some_and(|side|
            view.combat.discovery().has(side, DiscoveryKind::Ship, ship.entity_id));
        // Presentation status is emitted only for already-visible ships;
        // it cannot bypass the host's authoritative fog or ownership.
        // Timers remain in CombatWorld, never in a remote shadow game.
        let animation = match ship.ship_type {
            4 | 9 if view.roster.owning_squad(ship.entity_id)
                .is_some_and(|owner|
                    view.commands.ship_is_mining(owner, ship.entity_id)) =>
                Some((0u8, view.tick as f32 * view.fixed_step_seconds)),
            20 => view.combat.game_warp_gate_animation_elapsed(ship.entity_id)
                .map(|elapsed| if view.combat.game_warp_gate_ready(ship.entity_id) {
                    (1u8, view.tick as f32 * view.fixed_step_seconds)
                } else { (0u8, elapsed) }),
            _ => None,
        };
        Some(json!({
            "id": ship.entity_id,
            "side": ship.side, "ship_type": ship.ship_type,
            "x": body.motion.position.x, "y": body.motion.position.y,
            "rotation": body.motion.rotation_degrees,
            "collider": wire_primary_hull(body.shape()),
            "collider_bounds": body.primary_bounds().map(|bounds|[
                [bounds.min.x,bounds.min.y],[bounds.max.x,bounds.max.y],
            ]),
            "animation_variant": animation.map(|(variant, _)| variant),
            "animation_seconds": animation.map(|(_, seconds)| seconds),
            "destination": if owned {
                view.combat.ship_game_destination(ship.entity_id)
                    .map(|destination| [destination.x, destination.y])
            } else { None },
            "health": ship.vitals.health(), "max_health": ship.vitals.max_health(),
            // Source Squad.SetColor uses a stable RGB independent of side.
            // Only already-fog-visible ships receive their visual color;
            // this is not a new command permission or roster disclosure.
            "color_rgb": view.roster.owning_squad(ship.entity_id)
                .and_then(|owner| view.player_authority.player_squad_color_rgb(owner)),
            // Allegiance is not command permission. AI-owned allies must
            // remain friendly in the remote view but never become selectable.
            "friendly": ship.side == side,
            "owned": owned, "discovered": discovered,
        }))
    }).collect();
    let obstacles: Vec<_> = view.combat.render_obstacles().filter_map(|(object, body)| {
        if fog && !sources.covers(body.pose.position, measurements) {
            return None;
        }
        // These are canonical collision/level objects, not an approximate
        // client-side resource map. Missing or depleted mining rock bodies
        // cannot appear in selectable remote objectives.
        let map = view.combat.map_object(object.entity_id);
        // The host already owns the collider used by movement, weapons
        // and navigation. Send its small render-only description so
        // remote players can see genuine walls and mining asteroid hulls,
        // not fabricated point-sized obstacles. Unusually large polygon
        // paths use bounds-only fallback to limit per-frame bandwidth.
        let hull=wire_primary_hull(body.shape());
        let collider_bounds = body.primary_bounds().map(|bounds| [
            [bounds.min.x, bounds.min.y], [bounds.max.x, bounds.max.y],
        ]);
        Some(json!({
            "id": object.entity_id,
            "kind": object.kind as u8,
            "collider": hull,
            "collider_bounds": collider_bounds,
            "mining": object.kind == ObstacleKind::MiningAsteroid,
            "x": body.pose.position.x, "y": body.pose.position.y,
            "rotation": body.pose.rotation_degrees,
            // Source-authored collision asteroid atlas identity. A remote
            // player draws this same prefab sprite, never inferred art
            // from obstacle radius, health, or user-facing map category.
            "asteroid_prefab": if object.kind == ObstacleKind::CollisionAsteroid {
                body.source_asteroid_prefab_number
            } else { None },
            "health": object.vitals.map(|v| v.health)
                .or_else(|| map.map(|v| v.health)),
        }))
    }).collect();
    // Visual-only projectiles are sampled from the authoritative physical
    // world after the completed step, never simulated by the remote client.
    // The source fog restricts shots just as it restricts enemy hulls.
    let projectiles: Vec<_> = view.combat.render_projectiles().filter_map(|(shot, body)| {
        let position = body.motion.position;
        if fog && !sources.covers(position, measurements) {
            return None;
        }
        Some(json!({
            "id": shot.entity_id,
            "type": shot.projectile_type,
            "x": position.x, "y": position.y,
            "rotation": body.rotation_degrees,
        }))
    }).collect();
    // Preserve the authoritative source registration order for LAN tab,
    // type-selection and keyboard cycling, including dynamically admitted
    // source squads. Retired lifetimes are skipped by the same live roster.
    let squads: Vec<_> = view.roster.source_order().iter().copied().filter_map(|owner| {
        let squad = view.roster.get(owner)?;
        let owned = owns_player_side(view, owner, side);
        let members: Vec<_> = squad.members.iter().copied()
            .filter(|id| owned || visible_ids.contains(id)).collect();
        if members.is_empty() { return None; }
        // Gameplay and remote HUD read the SAME live command director and
        // mutable source player preferences. No network-side squad state.
        let command = if owned { view.commands.current_kind(owner)
            .map(|kind| format!("{kind:?}")) } else { None };
        let permissions = view.player_authority.player_permissions(owner);
        Some(json!({
            "owner": owner, "side": squad.side, "owned": owned,
            "controllable": owned && permissions.is_some_and(|p| p.can_accept_input),
            // Source Ctrl+A selects only top-level player squads. A live
            // Queen/Carrier child may be individually controllable, but
            // mass selection must not silently include every dependent
            // drone in addition to its source parent.
            "source_root": owned && squad.mother_squad.is_none(),
            "locked_on": if owned { permissions.map(|p| p.locked_on) } else { None },
            "members": members,
            "source_number": if owned { squad.source_hotkey_number } else { None },
            // Source LevelInputManager wraps 1..0 against the original
            // squad count, not the number of survivors visible today.
            "original_squad_count": if owned { squad.original_squad_count } else { None },
            "command": command,
            "cease_fire": if owned { view.player_authority.player_cease_fire(owner) } else { None },
            "chasing": if owned { Some(view.player_authority.player_should_chase(owner)) } else { None },
            "match_speed": if owned {
                Some(view.player_authority.player_matched_speed(owner).is_some())
            } else { None },
            "shooting": if owned { view.player_authority.player_shooting_strategy(owner)
                .map(|strategy| strategy.id) } else { None },
        }))
    }).collect();
    json!({
        "kind": "world_state", "protocol": 1,
        "world_id": view.world_id.0,
        "episode_revision": view.episode_revision,
        "tick": view.tick, "fixed_step_seconds": view.fixed_step_seconds,
        "music_location": music_location, "audio_events": audio_events,
        "viewer_side": viewer,
        // Source-owned initial story pacing; never required for local
        // hosts or after this World has taken its first physical step.
        "briefing_requires_ack": requires_briefing_ack,
        // Only authenticated side-owned vision and its authored fading
        // masks are sent. The remote window reuses the native pixel stencil
        // instead of revealing old sprites outside the current host sight.
        "fog_enabled": fog,
        "fog_sources": if fog { sources.sources.iter().map(|(position, radius)|
            json!({"x": position.x, "y": position.y, "radius": radius})
        ).collect::<Vec<_>>() } else { Vec::new() },
        "map_bounds": [[bounds.min.x, bounds.min.y], [bounds.max.x, bounds.max.y]],
        "terminal_result": view.terminal_result.map(|result| result.winner_side),
        // Preserve the source distinction between timeout/draw, elimination
        // and successful extraction, instead of guessing from winner_side.
        "terminal_outcome": view.terminal_result.map(|result| json!({
            "winner_side": result.winner_side,
            "timed_out": result.timed_out,
            "extraction_victory": result.extraction_victory,
            "duration_seconds": result.duration_seconds,
            "bee_side_eliminated": result.bee_side_eliminated,
            "human_side_eliminated": result.human_side_eliminated,
            "label": crate::presentation::battle_outcome_label(result),
            "reason": crate::presentation::battle_outcome_reason(view, result),
        })),
        // Present the SAME authored Level cue as local play. Story text is
        // restricted to its campaign player's view, never broadcast to an
        // independently authenticated opposing side.
        "campaign_dialogue": if view.campaign_dialogue_side == Some(side) {
            // Dedicated multiplayer hosts have no local viewer. Protect
            // the source-authored story with its own immutable campaign
            // owner, never with a temporary local/remote input override.
            view.campaign_dialogue.map(|(sequence, cue)| json!({
                "sequence": sequence,
                "cue": cue,
                "line_count": crate::presentation::campaign_dialogue::lines(cue).len(),
                "lines": crate::presentation::campaign_dialogue::lines(cue),
            }))
        } else { None },
        // Mission rules and target IDs are public to both authenticated
        // sides just as in local play. Positions, health and visibility
        // still come ONLY from the host's fog-filtered ship/obstacle list.
        // Mission dialogue remains independently restricted above.
        "campaign_objective": if let Some(objective) = view.survival {
            Some(json!({
                "kind": "survival",
                "protected_ship_id": objective.protected_ship_id,
                "defending_side": objective.defending_side,
                "elapsed_seconds": objective.elapsed_seconds,
                "duration_seconds": objective.duration_seconds,
                "seconds_remaining": objective.seconds_remaining,
                "reinforcements_pending": view.reinforcement_waves_pending,
                "next_reinforcement_tick": view.next_reinforcement_tick,
                "last_reinforcement_tick": view.last_reinforcement_tick,
            }))
        } else if let Some(objective) = view.demolition {
            Some(json!({
                "kind": "demolition",
                "attacking_side": objective.attacking_side,
                "target_map_object_id": objective.target_map_object_id,
                "duration_seconds": objective.duration_seconds,
                "seconds_remaining": objective.seconds_remaining,
            }))
        } else if let Some(objective) = view.escort {
            Some(json!({
                "kind": "escort",
                "escorting_side": objective.escorting_side,
                "escorted_ship_id": objective.escorted_ship_id,
                "duration_seconds": objective.duration_seconds,
                "seconds_remaining": objective.seconds_remaining,
            }))
        } else if let Some((exiting_side, _, escort)) = view.combat.extraction_objective() {
            (!escort).then(|| json!({
                "kind": "extraction",
                "exiting_side": exiting_side,
            }))
        } else { None },
        // An authored exit is also public to both opponents in the local
        // native game; it cannot disclose a hidden hull or current position.
        "escort_destination": view.escort.and_then(|_|
            view.combat.escorted_destination_bounds().map(|bounds| [
                [bounds.min.x, bounds.min.y],
                [bounds.max.x, bounds.max.y],
            ])
        ),
        "extraction_destination": view.combat.extraction_objective()
            .filter(|(_, _, escort)| !*escort)
            .map(|(_, bounds, _)| [
                [bounds.min.x, bounds.min.y],
                [bounds.max.x, bounds.max.y],
            ]),
        // Every authenticated commander sees Level-owned wave activity,
        // without exposing reserved ship identities or mission story cues.
        "reinforcements": Some(json!({
            "pending": view.reinforcement_waves_pending,
            "next_tick": view.next_reinforcement_tick,
            "last_arrival_tick": view.last_reinforcement_tick,
        })),
        "ships": ships, "squads": squads, "obstacles": obstacles,
        "projectiles": projectiles,
    })
}
