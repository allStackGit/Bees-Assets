//! Opt-in authenticated LAN co-op transport for the authoritative native Level.
//!
//! Remote peers observe the host's currently authorized player side and submit
//! the SAME typed NativePlayerInput as the local window and replay. This file
//! owns only nonblocking sockets and bounded wire buffers: no remote physics,
//! command director, policy state, or shadow squad selection.
//!
//! Protocol: newline-delimited JSON. Hello {kind:"hello", token}; then
//! {kind:"input", request_id:u64, action:NativePlayerInput}. Host replies
//! with kind:"input_result" and broadcasts kind:"world_state". The remote
//! interface shares the local player's authority (co-op, not opposing sides).
use std::collections::HashSet;
use std::io::{self, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::time::Instant;

use bees_sim::collision::Aabb;
use bees_sim::level_discovery::DiscoveryKind;
use bees_sim::obstacle::ObstacleKind;
use bees_sim::geometry::Vec2;
use serde::Deserialize;
use serde_json::{json, Value};

use crate::player_trace::NativePlayerInput;
use crate::presentation::NativeRenderWorldView;

const MAX_PEERS: usize = 4;
const MAX_ACCEPTS_PER_POLL: usize = 8;
const MAX_ORDERS_PER_PEER_POLL: usize = 8;
const MAX_LINE: usize = 64 * 1024;
const MAX_PENDING: usize = 4 * 1024 * 1024;
const READ_BYTES_PER_POLL: usize = 128 * 1024;
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
    pub request_id: u64,
    pub action: NativePlayerInput,
}

struct Peer {
    id: u64,
    socket: TcpStream,
    authenticated: bool,
    incoming: Vec<u8>,
    outgoing: Vec<u8>,
    written: usize,
    closed: bool,
    accepted_at: Instant,
}

impl Peer {
    fn enqueue(&mut self, message: &Value) -> io::Result<()> {
        let data = serde_json::to_vec(message)
            .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
        if self.outgoing.len().saturating_sub(self.written) +
            data.len() + 1 > MAX_PENDING {
            self.closed = true;
            return Err(io::Error::new(io::ErrorKind::InvalidData,
                "remote peer cannot keep up with world snapshots"));
        }
        if self.written == self.outgoing.len() {
            self.outgoing.clear();
            self.written = 0;
        } else if self.written > MAX_PENDING / 2 {
            self.outgoing.drain(..self.written);
            self.written = 0;
        }
        self.outgoing.extend_from_slice(&data);
        self.outgoing.push(b'\n');
        Ok(())
    }

    fn flush(&mut self) {
        while self.written < self.outgoing.len() {
            match self.socket.write(&self.outgoing[self.written..]) {
                Ok(0) => { self.closed = true; break; }
                Ok(n) => self.written += n,
                Err(ref error) if error.kind() == io::ErrorKind::WouldBlock => break,
                Err(ref error) if error.kind() == io::ErrorKind::Interrupted => continue,
                Err(_) => { self.closed = true; break; }
            }
        }
        if self.written == self.outgoing.len() {
            self.outgoing.clear();
            self.written = 0;
        }
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
    pub disconnected_peers: u64,
}

pub struct NativeRemotePlay {
    listener: TcpListener,
    token: String,
    peers: Vec<Peer>,
    next_peer: u64,
    last_state: Option<(u64, u64)>,
    measurements: RemotePlayMeasurements,
}

impl NativeRemotePlay {
    pub fn from_environment() -> io::Result<Option<Self>> {
        let Ok(address) = std::env::var("BEES_NATIVE_REMOTE_LISTEN") else {
            return Ok(None);
        };
        let token = std::env::var("BEES_NATIVE_REMOTE_TOKEN").map_err(|_|
            io::Error::new(io::ErrorKind::InvalidInput,
                "BEES_NATIVE_REMOTE_TOKEN must be set for remote play"))?;
        if token.len() < 24 || token.len() > 256 ||
            token.chars().any(|ch| ch.is_control()) {
            return Err(io::Error::new(io::ErrorKind::InvalidInput,
                "remote play requires a nonempty 24-256 character non-control token"));
        }
        let listener = TcpListener::bind(&address)?;
        listener.set_nonblocking(true)?;
        println!("{}", json!({
            "kind": "native_remote_listening",
            "address": listener.local_addr()?.to_string(),
            "max_peers": MAX_PEERS,
            "mode": "shared_player_authority",
        }));
        Ok(Some(Self {
            listener, token, peers: Vec::new(),
            next_peer: 1, last_state: None,
            measurements: RemotePlayMeasurements::default(),
        }))
    }

    pub fn measurements(&self) -> &RemotePlayMeasurements {
        &self.measurements
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
                id, socket, authenticated: false, incoming: Vec::new(),
                outgoing: Vec::new(), written: 0, closed: false,
                accepted_at: Instant::now(),
            });
        }

        let mut orders = Vec::new();
        for peer in &mut self.peers {
            if peer.closed { continue; }
            let mut peer_orders = 0;
            // Never allow an unauthenticated socket to occupy a slot forever.
            if !peer.authenticated && peer.accepted_at.elapsed().as_secs() > 10 {
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
                    Ok(WireInput::Hello { token }) if !peer.authenticated &&
                        constant_time_matches(self.token.as_bytes(), token.as_bytes()) => {
                        peer.authenticated = true;
                        self.measurements.authenticated_peers += 1;
                        let _ = peer.enqueue(&json!({
                            "kind": "welcome", "protocol": 1, "mode": "cooperative",
                        }));
                    }
                    Ok(WireInput::Input { request_id, action }) if peer.authenticated => {
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
                            peer: peer.id, request_id, action,
                        });
                    }
                    _ => { peer.closed = true; break; }
                }
            }
            if start > 0 { peer.incoming.drain(..start); }
            if peer.incoming.len() > MAX_LINE { peer.closed = true; }
            if !peer.closed { peer.flush(); }
        }
        self.reap();
        orders
    }

    pub fn reply(&mut self, peer_id: u64, request_id: u64, result: Result<&str, &str>) {
        let Some(peer) = self.peers.iter_mut()
            .find(|peer| peer.id == peer_id && peer.authenticated) else { return };
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
        peer.flush();
    }

    pub fn broadcast(&mut self, view: &NativeRenderWorldView<'_>, bounds: Aabb) {
        if !self.peers.iter().any(|peer| peer.authenticated) { return; }
        let episode = (view.world_id.0, view.episode_revision);
        if self.last_state == Some(episode) &&
            view.tick % STATE_EVERY_TICKS != 0 &&
            view.terminal_result.is_none() { return; }
        self.last_state = Some(episode);
        let state = current_state(view, bounds);
        let Ok(encoded) = serde_json::to_vec(&state) else { return };
        if encoded.len() > MAX_PENDING / 2 { return; }
        for peer in &mut self.peers {
            if !peer.authenticated { continue; }
            if peer.outgoing.len().saturating_sub(peer.written) + encoded.len() > MAX_PENDING {
                // Drop a lagging observer rather than accumulating unbounded
                // snapshots or slowing the authoritative fixed-step loop.
                peer.closed = true;
                continue;
            }
            if peer.written == peer.outgoing.len() {
                peer.outgoing.clear();
                peer.written = 0;
            }
            peer.outgoing.extend_from_slice(&encoded);
            peer.outgoing.push(b'\n');
            peer.flush();
            self.measurements.state_bytes += encoded.len() as u64;
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
fn current_state(view: &NativeRenderWorldView<'_>, bounds: Aabb) -> Value {
    let viewer = view.player_authority.player_viewer_side();
    let fog = view.player_authority.player_fog_of_war_enabled();
    let sources: Vec<(Vec2, f32)> = if fog {
        view.combat.render_ships().filter_map(|(ship, body)| {
            let owner = view.roster.owning_squad(ship.entity_id)?;
            if !view.is_local_player_squad(owner) { return None; }
            let radius = view.player_authority.player_fog_vision_radius(ship.entity_id)?;
            (radius > 0.0 && radius.is_finite()).then_some((body.motion.position, radius))
        }).collect()
    } else { Vec::new() };
    let mut visible_ids = HashSet::<i64>::new();
    let ships: Vec<_> = view.combat.render_ships().filter_map(|(ship, body)| {
        let owned = view.roster.owning_squad(ship.entity_id)
            .is_some_and(|owner| view.is_local_player_squad(owner));
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
            "destination": view.combat.ship_game_destination(ship.entity_id)
                .map(|destination| [destination.x, destination.y]),
            "health": ship.vitals.health(), "max_health": ship.vitals.max_health(),
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
        let owned = view.is_local_player_squad(owner);
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
            "members": members, "source_number": if owned { squad.source_hotkey_number } else { None },
            "command": command,
            "cease_fire": if owned { view.player_authority.player_cease_fire(owner) } else { None },
            "chasing": if owned { Some(view.player_authority.player_should_chase(owner)) } else { None },
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
        "ships": ships, "squads": squads, "obstacles": obstacles,
        "projectiles": projectiles,
    })
}
