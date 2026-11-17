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
use std::time::Instant;

use bees_sim::collision::Aabb;
use bees_sim::level_discovery::DiscoveryKind;
use bees_sim::obstacle::ObstacleKind;
use bees_sim::geometry::Vec2;
use bees_hivemind::session::SquadLifetime;
use serde::Deserialize;
use serde_json::{json, Value};

use crate::player_trace::NativePlayerInput;
use crate::presentation::NativeRenderWorldView;

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

#[derive(Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
enum WireInput {
    Hello { token: String },
    Input { request_id: u64, action: NativePlayerInput },
}

pub struct RemoteOrder {
    pub peer: u64,
    pub side: i32,
    pub request_id: u64,
    pub action: NativePlayerInput,
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

#[derive(Default, serde::Serialize)]
pub struct RemotePlayMeasurements {
    pub connections_accepted: u64,
    pub authenticated_peers: u64,
    pub rejected_connections: u64,
    pub orders_received: u64,
    pub orders_processed: u64,
    pub orders_rejected: u64,
    pub state_frames: u64,
    pub state_bytes: u64,
    pub states_superseded: u64,
    pub disconnected_peers: u64,
}

pub struct NativeRemotePlay {
    listener: TcpListener,
    side_tokens: HashMap<i32, String>,
    peers: Vec<Peer>,
    next_peer: u64,
    last_state: Option<(u64, u64)>,
    measurements: RemotePlayMeasurements,
}

impl NativeRemotePlay {
    pub fn from_environment(local_side: i32) -> io::Result<Option<Self>> {
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
            listener, side_tokens, peers: Vec::new(),
            next_peer: 1, last_state: None,
            measurements: RemotePlayMeasurements::default(),
        }))
    }

    pub fn measurements(&self) -> &RemotePlayMeasurements {
        &self.measurements
    }

    pub fn configured_sides(&self) -> impl Iterator<Item = i32> + '_ {
        self.side_tokens.keys().copied()
    }

    /// Poll at an authoritative tick boundary before advancing the Level.
    /// Never block simulation on a slow network or remote client.
    pub fn poll(&mut self) -> Vec<RemoteOrder> {
        // A connection flood must not monopolize the authoritative game
        // tick. Unaccepted sockets remain queued by the OS.
        for _ in 0..MAX_ACCEPTS_PER_POLL {
            let (socket, _) = match self.listener.accept() {
                Ok(accepted) => accepted,
                Err(ref error) if error.kind() == io::ErrorKind::WouldBlock => break,
                Err(_) => break,
            };
            if self.peers.len() >= MAX_PEERS || socket.set_nonblocking(true).is_err() {
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

        let mut orders = Vec::new();
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
                            self.measurements.authenticated_peers += 1;
                            let _ = peer.enqueue(&json!({
                                "kind": "welcome", "protocol": 1,
                                "mode": "authenticated_player_side", "side": side,
                            }));
                        } else {
                            peer.closed = true;
                        }
                    }
                    Ok(WireInput::Input { request_id, action }) if peer.side.is_some() => {
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
                        // Held-fire is a continuing, per-controller gesture.
                        // The present cooperative protocol has no peer-owned
                        // release lifecycle: rejecting it avoids an orphaned
                        // shared fire lock when a network peer disappears.
                        if matches!(action, NativePlayerInput::HeldFire(_)) {
                            let _ = peer.enqueue(&json!({
                                "kind": "input_result", "request_id": request_id,
                                "processed": false,
                                "feedback": "remote held fire is not supported; use point fire",
                            }));
                            self.measurements.orders_rejected += 1;
                            continue;
                        }
                        if orders.len() >= MAX_ORDERS_PER_POLL ||
                           peer_orders >= MAX_ORDERS_PER_PEER_POLL {
                            peer.closed = true;
                            break;
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
        self.reap();
        orders
    }

    pub fn reply(&mut self, peer_id: u64, request_id: u64, result: Result<&str, &str>) {
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
        if !self.peers.iter().any(|peer| peer.side.is_some()) { return; }
        let episode = (view.world_id.0, view.episode_revision);
        if self.last_state == Some(episode) &&
            view.tick % STATE_EVERY_TICKS != 0 &&
            view.terminal_result.is_none() { return; }
        self.last_state = Some(episode);
        // Encode each player's side-filtered snapshot only once, then fan it
        // out to peers on the same side. Never send another side's packet.
        let mut snapshots = HashMap::<i32, Vec<u8>>::new();
        for peer in &mut self.peers {
            let Some(side) = peer.side else { continue };
            let encoded = snapshots.entry(side).or_insert_with(||
                serde_json::to_vec(&current_state(view, bounds, side))
                    .unwrap_or_default());
            if peer.enqueue_state(encoded) {
                self.measurements.states_superseded += 1;
            }
            if !peer.closed {
                self.measurements.state_bytes += peer.flush();
            }
        }
        self.measurements.state_frames += 1;
        self.reap();
    }

    fn reap(&mut self) {
        let before = self.peers.len();
        self.peers.retain(|peer| !peer.closed);
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

/// State snapshots are a read-only projection of the same live CombatWorld.
/// Source FogOfWar restricts ship coordinates to ships actually within live
/// friendly sight masks; previous discoveries alone do not reveal position.
fn owns_player_side(view: &NativeRenderWorldView<'_>, owner: SquadLifetime, side: i32) -> bool {
    view.roster.get(owner).is_some_and(|squad| squad.side == side &&
        view.player_authority.player_permissions(owner)
            .is_some_and(|permission| permission.user_controlled))
}

fn current_state(view: &NativeRenderWorldView<'_>, bounds: Aabb, side: i32) -> Value {
    let viewer = Some(side);
    let fog = view.player_authority.player_fog_of_war_enabled();
    let sources: Vec<(Vec2, f32)> = if fog {
        view.combat.render_ships().filter_map(|(ship, body)| {
            let owner = view.roster.owning_squad(ship.entity_id)?;
            if !owns_player_side(view, owner, side) { return None; }
            let radius = view.player_authority.player_fog_vision_radius(ship.entity_id)?;
            (radius > 0.0 && radius.is_finite()).then_some((body.motion.position, radius))
        }).collect()
    } else { Vec::new() };
    let mut visible_ids = HashSet::<i64>::new();
    let ships: Vec<_> = view.combat.render_ships().filter_map(|(ship, body)| {
        let owned = view.roster.owning_squad(ship.entity_id)
            .is_some_and(|owner| owns_player_side(view, owner, side));
        let visible = owned || !fog || sources.iter().any(|&(position, radius)|
            (body.motion.position - position).length_squared() <= radius * radius);
        if !visible { return None; }
        visible_ids.insert(ship.entity_id);
        let discovered = viewer.is_some_and(|side|
            view.combat.discovery().has(side, DiscoveryKind::Ship, ship.entity_id));
        Some(json!({
            "id": ship.entity_id,
            "side": ship.side, "ship_type": ship.ship_type,
            "x": body.motion.position.x, "y": body.motion.position.y,
            "rotation": body.motion.rotation_degrees,
            "destination": if owned {
                view.combat.ship_game_destination(ship.entity_id)
                    .map(|destination| [destination.x, destination.y])
            } else { None },
            "health": ship.vitals.health(), "max_health": ship.vitals.max_health(),
            // Allegiance is not command permission. AI-owned allies must
            // remain friendly in the remote view but never become selectable.
            "friendly": ship.side == side,
            "owned": owned, "discovered": discovered,
        }))
    }).collect();
    let obstacles: Vec<_> = view.combat.render_obstacles().filter_map(|(object, body)| {
        if fog && !sources.iter().any(|&(position, radius)|
            (body.pose.position - position).length_squared() <= radius * radius) {
            return None;
        }
        // These are canonical collision/level objects, not an approximate
        // client-side resource map. Missing or depleted mining rock bodies
        // cannot appear in selectable remote objectives.
        let map = view.combat.map_object(object.entity_id);
        Some(json!({
            "id": object.entity_id,
            "kind": object.kind as u8,
            "mining": object.kind == ObstacleKind::MiningAsteroid,
            "x": body.pose.position.x, "y": body.pose.position.y,
            "health": object.vitals.map(|v| v.health)
                .or_else(|| map.map(|v| v.health)),
        }))
    }).collect();
    // Visual-only projectiles are sampled from the authoritative physical
    // world after the completed step, never simulated by the remote client.
    // The source fog restricts shots just as it restricts enemy hulls.
    let projectiles: Vec<_> = view.combat.render_projectiles().filter_map(|(shot, body)| {
        let position = body.motion.position;
        if fog && !sources.iter().any(|&(center, radius)|
            (position - center).length_squared() <= radius * radius) {
            return None;
        }
        Some(json!({
            "id": shot.entity_id,
            "type": shot.projectile_type,
            "x": position.x, "y": position.y,
            "rotation": body.rotation_degrees,
        }))
    }).collect();
    let squads: Vec<_> = view.roster.active_lifetimes().filter_map(|owner| {
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
        "viewer_side": viewer,
        "map_bounds": [[bounds.min.x, bounds.min.y], [bounds.max.x, bounds.max.y]],
        "terminal_result": view.terminal_result.map(|result| result.winner_side),
        // Present the SAME authored Level cue as local play. Story text is
        // restricted to its campaign player's view, never broadcast to an
        // independently authenticated opposing side.
        "campaign_dialogue": if view.player_authority.player_viewer_side() == Some(side) {
            // Both compiled clients share the exact authored dialogue
            // table. Send only the cue and progression identity rather
            // than retransmitting entire conversations at every tick.
            view.campaign_dialogue.map(|(sequence, cue)| json!({
                "sequence": sequence,
                "cue": cue,
                "line_count": crate::presentation::campaign_dialogue::lines(cue).len(),
            }))
        } else { None },
        // Mission timing and wave ownership come from the same native
        // Level controller as local gameplay; clients cannot schedule waves.
        "campaign_objective": if view.player_authority.player_viewer_side() == Some(side) {
            view.survival.map(|status| json!({
                "protected_ship_id": status.protected_ship_id,
                "elapsed_seconds": status.elapsed_seconds,
                "duration_seconds": status.duration_seconds,
                "seconds_remaining": status.seconds_remaining,
                "reinforcements_pending": view.reinforcement_waves_pending,
                "next_reinforcement_tick": view.next_reinforcement_tick,
                "last_reinforcement_tick": view.last_reinforcement_tick,
            }))
        } else { None },
        "ships": ships, "squads": squads, "obstacles": obstacles,
        "projectiles": projectiles,
    })
}
