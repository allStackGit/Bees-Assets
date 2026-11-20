//! Native remote battle window using the same read-only authoritative wire
//! projection as the terminal client. No gameplay/physics or model state lives
//! here. All orders use the host's existing NativePlayerInput serializer and
//! player-side permissions; the host validates them again at tick boundaries.

use std::collections::{HashMap, HashSet, VecDeque};
use std::error::Error;
use std::fs;
use std::io::{self, ErrorKind, Read, Write};
use std::net::{TcpStream, ToSocketAddrs};
use std::path::Path;
use std::time::{Duration, Instant};

use bees_gameplay::campaign_dialogue::CampaignCue;
use bees_sim::collision::{Aabb, Pose, circle_overlaps_polygon, circles_overlap};
use bees_sim::geometry::Vec2;
use bees_sim::simulation::WorldId;
use minifb::{Key, KeyRepeat, MouseButton, MouseMode, Window, WindowOptions};
use serde_json::{json, Value};

use super::{NativeCamera, NativeSoftwareRenderer, text};
use super::minimap::MiniMapLayout;
use super::tactical_hud::{HudAction, HudRect, ACTIONS, shooting_hit, shooting_rect};
use bees_hivemind::protocol::SHOOTING_STRATEGIES;
use crate::game_audio::NativeGameAudio;
use crate::unity_scene_snapshot::NativeMusicLocation;

const WIDTH: usize = 1180;
const HEIGHT: usize = 720;
const FIELD: usize = 914;
const MAX_INCOMING: usize = 4 * 1024 * 1024;
const MAX_OUTGOING: usize = 128 * 1024;
const READ_BUDGET: usize = 256 * 1024;
const TEXT: [u8; 3] = [231, 240, 250];
const MUTED: [u8; 3] = [161, 181, 201];
const GREEN: u32 = 0x69d2a1;
const RED: u32 = 0xed7e85;
const GOLD: u32 = 0xe7be69;

#[derive(Clone, Copy, PartialEq, Eq)]
enum TargetMode {
    Fire, Attack, Circle, RightSwipe, LeftSwipe, InAndOut,
    Guard, Mine, Heal, Retreat, Patrol,
}

// Same 250 ms movement cadence and captured input surface as the local
// native client. The host receives only canonical move/attack/fire inputs.
struct RemoteRightPress {
    next_move_at: Instant,
    last_move_pixel: Option<Vec2>,
    minimap: bool,
}

const RIGHT_DRAG_INTERVAL: Duration=Duration::from_millis(250);

#[derive(Default)]
struct WireCounts {
    bytes_received: u64,
    snapshots: u64,
    frames_rendered: u64,
    orders_sent: u64,
    acknowledgments: u64,
    campaign_receipts: u64,
    battle_result_receipts: u64,
    interrupted_battle_notices: u64,
    reconnect_attempts: u64,
    reconnect_transports_opened: u64,
    /// Render-only smoothing; no simulation or network side-effects.
    snapshot_pairs_interpolated: u64,
    interpolated_draw_frames: u64,
    ship_sprite_draws: u64,
    projectile_sprite_draws: u64,
    asteroid_sprite_draws: u64,
    sprite_candidate_pixels: u64,
    sprite_opaque_pixels: u64,
    sprite_blended_pixels: u64,
    recolor_cache_peak: usize,
    ship_deaths_received: u64,
    combat_impacts_received: u64,
    combat_impact_visual_draws: u64,
    combat_impact_visuals_dropped: u64,
    ship_remains_draws: u64,
    ship_remains_source_frame_draws: u64,
    ship_explosion_visual_draws: u64,
    obstacle_hull_draws: u64,
    minimap_ship_markers: u64,
    minimap_objective_markers: u64,
    minimap_camera_moves: u64,
    ship_type_multiselections: u64,
    minimap_move_orders_submitted: u64,
    right_drag_move_orders_submitted: u64,
    /// Local and LAN share the exact fog circle raster; pixels outside the
    /// host-authenticated current/decaying vision are not presented.
    fog_circle_stamps: u64,
    fog_masked_pixels: u64,
}

/// The existing native source-art renderer, reused for authenticated
/// remote snapshots. Its framebuffer is composited behind remote HUD
/// controls; it never creates CombatWorld, ShipVitals or any gameplay actor.
struct RemoteArtwork {
    raster: NativeSoftwareRenderer,
    ships: HashSet<i64>,
    projectiles: HashSet<i64>,
    asteroids: HashSet<i64>,
    shot_first_seen: HashMap<i64, (u64, u64)>,
    episode: Option<(u64, u64)>,
    fog_enabled: bool,
    fog_coverage: Vec<u8>,
}

impl RemoteArtwork {
    fn new() -> io::Result<Self> {
        let map = Aabb {
            min: Vec2::new(-1.0, -1.0),
            max: Vec2::new(1.0, 1.0),
        };
        Ok(Self {
            raster: NativeSoftwareRenderer::new(FIELD, HEIGHT, map)?,
            ships: HashSet::new(), projectiles: HashSet::new(),
            asteroids: HashSet::new(), shot_first_seen: HashMap::new(),
            episode: None, fog_enabled: false,
            fog_coverage: vec![0; FIELD * HEIGHT],
        })
    }

    /// One draw pass over currently authenticated, fog-filtered entities.
    /// IDs from a previous world or episode are never rendered or reused
    /// for projectile animation. All sprite scaling/pivots/frames use
    /// the same native source artwork already loaded for local play.
    fn paint(&mut self, state: &Value, camera: NativeCamera,
             visual: &RemoteVisual, fraction: f32, stats: &mut WireCounts,
             show_art: bool) {
        self.ships.clear();
        self.projectiles.clear();
        self.asteroids.clear();
        self.fog_enabled = state["fog_enabled"] == true;
        if self.fog_enabled {
            self.fog_coverage.fill(0);
            for source in state["fog_sources"].as_array().into_iter().flatten() {
                let (Some(point), Some(radius)) = (
                    position(source),
                    source["radius"].as_f64().map(|value| value as f32)
                        .filter(|value| value.is_finite() && *value > 0.0),
                ) else { continue };
                NativeSoftwareRenderer::reveal_fog_circle(
                    &mut self.fog_coverage, camera, FIELD, HEIGHT, point, radius,
                );
                stats.fog_circle_stamps += 1;
            }
        }
        if !show_art {
            // F6 skips the entire texture renderer (and framebuffer
            // clear/copy), retaining only the existing geometric UI.
            self.shot_first_seen.clear();
            return;
        }
        self.raster.begin_remote_art(camera);
        let episode = (
            state["world_id"].as_u64().unwrap_or(0),
            state["episode_revision"].as_u64().unwrap_or(0),
        );
        if self.episode != Some(episode) {
            self.shot_first_seen.clear();
            self.episode = Some(episode);
        }
        let tick = state["tick"].as_u64().unwrap_or(0);
        let dt = state["fixed_step_seconds"].as_f64().unwrap_or(0.0) as f32;
        for obstacle in state["obstacles"].as_array().into_iter().flatten() {
            let (Some(id), Some(prefab), Some(point)) = (
                obstacle["id"].as_i64(),
                obstacle["asteroid_prefab"].as_u64().and_then(|id| u8::try_from(id).ok()),
                visual.position(obstacle, 2, fraction),
            ) else { continue };
            let angle = visual.heading_for(obstacle, 2, fraction);
            if self.raster.remote_asteroid_art(prefab, point, angle) {
                self.asteroids.insert(id);
                stats.asteroid_sprite_draws += 1;
            }
        }
        for shot in state["projectiles"].as_array().into_iter().flatten() {
            let (Some(id), Some(kind), Some(point)) = (
                shot["id"].as_i64(),
                shot["type"].as_u64().and_then(|kind| u8::try_from(kind).ok()),
                visual.position(shot, 1, fraction),
            ) else { continue };
            let (first, last) = self.shot_first_seen.entry(id)
                .or_insert((tick, tick));
            *last = tick;
            let age = tick.saturating_sub(*first) as f32 * dt.max(0.0);
            let angle = visual.heading_for(shot, 1, fraction);
            if self.raster.remote_projectile_art(kind, point, angle, age) {
                self.projectiles.insert(id);
                stats.projectile_sprite_draws += 1;
            }
        }
        self.shot_first_seen.retain(|_, &mut (_, last)| last == tick);
        for ship in state["ships"].as_array().into_iter().flatten() {
            let (Some(id), Some(kind), Some(point)) = (
                ship["id"].as_i64(),
                ship["ship_type"].as_u64().and_then(|kind| u8::try_from(kind).ok()),
                visual.position(ship, 0, fraction),
            ) else { continue };
            let color_rgb = ship["color_rgb"].as_array()
                .filter(|rgb| rgb.len() == 3)
                .and_then(|rgb| Some([
                    u8::try_from(rgb[0].as_u64()?).ok()?,
                    u8::try_from(rgb[1].as_u64()?).ok()?,
                    u8::try_from(rgb[2].as_u64()?).ok()?,
                ]));
            let animation = ship["animation_variant"].as_u64()
                .and_then(|variant| u8::try_from(variant).ok())
                .zip(ship["animation_seconds"].as_f64().map(|seconds| seconds as f32))
                .filter(|(variant, seconds)| *variant <= 1 &&
                    seconds.is_finite() && *seconds >= 0.0);
            if self.raster.remote_ship_art(kind, point,
                visual.heading(ship, fraction), color_rgb, animation) {
                self.ships.insert(id);
                stats.ship_sprite_draws += 1;
            }
        }
        self.raster.begin_death_scene(WorldId(episode.0), episode.1);
        for cue in state["audio_events"].as_array().into_iter().flatten() {
            let impact = &cue["combat_impact"];
            if let (Some(projectile), Some(target), Some(fatal),
                    Some(x), Some(y), Some(born_tick)) = (
                impact["projectile_id"].as_i64(),
                impact["target_id"].as_i64(),
                impact["fatal"].as_bool(),
                cue["x"].as_f64().map(|v| v as f32),
                cue["y"].as_f64().map(|v| v as f32),
                cue["tick"].as_u64(),
            ) {
                let (admitted, dropped) = self.raster.record_combat_impact(
                    born_tick, projectile, target, Vec2::new(x, y), fatal,
                );
                stats.combat_impacts_received += u64::from(admitted);
                stats.combat_impact_visuals_dropped += u64::from(dropped);
            }
            let death = &cue["ship_death"];
            let (Some(id), Some(kind), Some(side), Some(x), Some(y),
                 Some(rotation), Some(radius), Some(born_tick)) = (
                death["id"].as_i64(),
                death["type"].as_u64().and_then(|v| u8::try_from(v).ok()),
                death["side"].as_i64().and_then(|v| i32::try_from(v).ok()),
                death["x"].as_f64().map(|v| v as f32),
                death["y"].as_f64().map(|v| v as f32),
                death["rotation"].as_f64().map(|v| v as f32),
                death["radius"].as_f64().map(|v| v as f32),
                cue["tick"].as_u64(),
            ) else { continue };
            let color = death["color_rgb"].as_array()
                .filter(|rgb| rgb.len() == 3)
                .and_then(|rgb| Some([
                    u8::try_from(rgb[0].as_u64()?).ok()?,
                    u8::try_from(rgb[1].as_u64()?).ok()?,
                    u8::try_from(rgb[2].as_u64()?).ok()?,
                ]));
            let (admitted, _) = self.raster.record_ship_death(
                id, born_tick, kind, side, Vec2::new(x, y),
                rotation, radius, color,
            );
            stats.ship_deaths_received += u64::from(admitted);
        }
        let (remains, flashes, source_frames) =
            self.raster.draw_ship_death_visuals(tick, dt);
        stats.ship_remains_draws += remains as u64;
        stats.ship_remains_source_frame_draws += source_frames as u64;
        stats.ship_explosion_visual_draws += flashes as u64;
        stats.combat_impact_visual_draws +=
            self.raster.draw_combat_impact_visuals(tick, dt) as u64;
        let (candidates, opaque, blended) = self.raster.remote_raster_counts();
        stats.sprite_candidate_pixels += candidates;
        stats.sprite_opaque_pixels += opaque;
        stats.sprite_blended_pixels += blended;
        stats.recolor_cache_peak = stats.recolor_cache_peak
            .max(self.raster.remote_ship_recolor_cache_size());
    }

    fn mask_field(&self, pixels: &mut [u32]) -> u64 {
        if !self.fog_enabled { return 0; }
        let mut masked = 0u64;
        for (row, sight) in pixels.chunks_exact_mut(WIDTH)
            .zip(self.fog_coverage.chunks_exact(FIELD))
        {
            for (pixel, &visible) in row[..FIELD].iter_mut().zip(sight) {
                if visible == 0 {
                    *pixel = 0x080e19;
                    masked += 1;
                }
            }
        }
        masked
    }
}

/// Presentation-only interpolation between two authenticated snapshots.
/// Old poses never create ships or reveal previously visible enemies outside
/// the latest host packet. Gameplay authority never leaves the host.
#[derive(Clone, Copy)]
struct RemotePose {
    position: Vec2,
    rotation: Option<f32>,
}

#[derive(Default)]
struct RemoteVisual {
    previous: HashMap<(u8, i64), RemotePose>,
    began: Option<Instant>,
    duration: Duration,
}

impl RemoteVisual {
    fn snapshot(&mut self, previous: Option<&Value>, next: &Value) -> bool {
        self.previous.clear();
        self.began = None;
        let Some(previous) = previous else { return false };
        if previous["world_id"] != next["world_id"] ||
            previous["episode_revision"] != next["episode_revision"] {
            return false;
        }
        let Some(from) = previous["tick"].as_u64() else { return false };
        let Some(to) = next["tick"].as_u64() else { return false };
        let Some(delta) = to.checked_sub(from).filter(|&ticks| ticks > 0 && ticks <= 60)
        else { return false };
        let Some(dt) = next["fixed_step_seconds"].as_f64()
            .filter(|dt| dt.is_finite() && *dt > 0.0) else { return false };
        let seconds = (delta as f64 * dt).min(0.30);
        if seconds < 0.005 || seconds > 0.30 { return false; }
        self.duration = Duration::from_secs_f64(seconds);
        for (kind, field) in [(0, "ships"), (1, "projectiles"), (2, "obstacles")] {
            for item in previous[field].as_array().into_iter().flatten() {
                if let (Some(id), Some(position)) =
                    (item["id"].as_i64(), position(item)) {
                    self.previous.insert((kind, id), RemotePose {
                        position,
                        rotation: item["rotation"].as_f64().map(|degrees| degrees as f32)
                            .filter(|value| value.is_finite()),
                    });
                }
            }
        }
        self.began = Some(Instant::now());
        true
    }

    fn fraction(&self) -> f32 {
        let Some(began) = self.began else { return 1.0 };
        (began.elapsed().as_secs_f32() / self.duration.as_secs_f32())
            .clamp(0.0, 1.0)
    }

    fn position(&self, item: &Value, kind: u8, fraction: f32) -> Option<Vec2> {
        let current = position(item)?;
        let Some(previous) = item["id"].as_i64().and_then(|id|
            self.previous.get(&(kind, id))) else { return Some(current) };
        Some(previous.position + (current - previous.position) * fraction)
    }

    fn heading_for(&self, item: &Value, kind: u8, fraction: f32) -> f32 {
        let now = item["rotation"].as_f64().unwrap_or(0.0) as f32;
        let Some(before) = item["id"].as_i64().and_then(|id|
            self.previous.get(&(kind, id))).and_then(|pose| pose.rotation)
        else { return now };
        // Shortest signed turn across 0/360, with no client steering.
        // Ships, projectiles and moving asteroid colliders all follow
        // authoritative consecutive host snapshot rotations.
        let delta = (now - before + 180.0).rem_euclid(360.0) - 180.0;
        before + delta * fraction
    }

    fn heading(&self, item: &Value, fraction: f32) -> f32 {
        self.heading_for(item, 0, fraction)
    }
}

struct PeerClient {
    socket: TcpStream,
    incoming: Vec<u8>,
    queued: VecDeque<Vec<u8>>,
    written: usize,
    queued_bytes: usize,
    next_request: u64,
    authenticated: bool,
    connected: bool,
    /// UI-only control gate, computed from the authoritative story cue
    /// and terminal result; no independent battle simulation.
    input_suppressed: bool,
    side: i32,
    host_session_id: Option<String>,
    expected_host_session_id: Option<String>,
    state: Option<Value>,
    visual: RemoteVisual,
    notice: String,
    held_fire_begin_request: Option<u64>,
    held_fire_rejected: bool,
    campaign_receipt: Option<String>,
    battle_result_receipt: Option<String>,
    metrics: WireCounts,
}

fn invalid(message: &'static str) -> io::Error {
    io::Error::new(ErrorKind::InvalidInput, message)
}

fn link_disconnected(error: &io::Error) -> bool {
    matches!(error.kind(), ErrorKind::ConnectionReset |
        ErrorKind::ConnectionAborted | ErrorKind::BrokenPipe |
        ErrorKind::NotConnected | ErrorKind::UnexpectedEof)
}

impl PeerClient {
    fn connect(address: &str, token: &str, side: i32) -> Result<Self, Box<dyn Error>> {
        let mut connection = None;
        for candidate in address.to_socket_addrs()? {
            match TcpStream::connect_timeout(&candidate, Duration::from_secs(5)) {
                Ok(socket) => { connection = Some(socket); break; }
                Err(_) => continue,
            }
        }
        let mut socket = connection.ok_or_else(|| invalid("native multiplayer server unreachable"))?;
        socket.set_nodelay(true)?;
        let mut hello = serde_json::to_vec(&json!({"kind": "hello", "token": token}))?;
        hello.push(b'\n');
        socket.write_all(&hello)?;
        socket.set_nonblocking(true)?;
        Ok(Self {
            socket, incoming: Vec::new(), queued: VecDeque::new(),
            written: 0, queued_bytes: 0, next_request: 1,
            authenticated: false, connected: true, input_suppressed: false, side,
            host_session_id: None, expected_host_session_id: None,
            state: None, visual: RemoteVisual::default(),
            notice: "Authenticating with native host".to_owned(),
            held_fire_begin_request: None, held_fire_rejected: false,
            campaign_receipt: None, battle_result_receipt: None,
            metrics: WireCounts::default(),
        })
    }

    fn poll(&mut self) -> Result<(), Box<dyn Error>> {
        let mut buffer = [0_u8; 32768];
        let mut read = 0_usize;
        while read < READ_BUDGET && self.connected {
            let limit = buffer.len().min(READ_BUDGET - read);
            match self.socket.read(&mut buffer[..limit]) {
                Ok(0) => { self.connected = false; self.notice = "Host disconnected".into(); break; }
                Ok(n) => {
                    read += n;
                    self.metrics.bytes_received += n as u64;
                    if self.incoming.len() + n > MAX_INCOMING {
                        return Err(invalid("remote world snapshot exceeds client memory limit").into());
                    }
                    self.incoming.extend_from_slice(&buffer[..n]);
                }
                Err(error) if error.kind() == ErrorKind::WouldBlock => break,
                Err(error) if error.kind() == ErrorKind::Interrupted => continue,
                Err(error) if link_disconnected(&error) => {
                    self.connected = false;
                    self.notice = "Host disconnected".into();
                    break;
                },
                Err(error) => return Err(error.into()),
            }
        }
        // Coalescing is already performed by the authoritative host. Process
        // only complete JSON lines; never render or apply half a message.
        let mut start = 0;
        for index in 0..self.incoming.len() {
            if self.incoming[index] != b'\n' { continue; }
            let message: Value = serde_json::from_slice(&self.incoming[start..index])?;
            start = index + 1;
            match message["kind"].as_str() {
                Some("welcome") => {
                    if self.authenticated {
                        return Err(invalid("native multiplayer socket reauthenticated after its session was established").into());
                    }
                    if message["protocol"].as_u64() != Some(1) ||
                        message["side"].as_i64() != Some(i64::from(self.side)) {
                        return Err(invalid("native host authenticated the wrong player side or protocol").into());
                    }
                    let host_id = message["host_session_id"].as_str()
                        .filter(|id| id.len() == 32 && id.bytes().all(|byte|
                            byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte)))
                        .ok_or_else(|| invalid("native host omitted its stable session identity"))?;
                    if self.expected_host_session_id.as_deref()
                        .is_some_and(|original| original != host_id)
                    {
                        // This is a fresh authoritative process, NOT a
                        // resumed game. Leave all old command and physical
                        // ownership behind rather than accept a substituted
                        // battle under the same side token.
                        self.connected = false;
                        self.notice = "Host restarted: original battle cannot be resumed".into();
                        let _ = self.socket.shutdown(std::net::Shutdown::Both);
                        break;
                    }
                    self.host_session_id = Some(host_id.to_owned());
                    self.authenticated = true;
                    self.notice = if message["recovered_host"] == true {
                        format!("Recovered original battle as player side {}. Unacknowledged commands were not resent.", self.side)
                    } else {
                        format!("Connected as player side {}", self.side)
                    };
                }
                Some("world_state") => {
                    if !self.authenticated || message["protocol"].as_u64() != Some(1) ||
                        message["viewer_side"].as_i64() != Some(i64::from(self.side)) {
                        return Err(invalid("native host snapshot has the wrong player side").into());
                    }
                    if let Some(previous) = &self.state {
                        if previous["world_id"] == message["world_id"] &&
                            previous["episode_revision"] == message["episode_revision"] &&
                            message["tick"].as_u64() < previous["tick"].as_u64() {
                            return Err(invalid("out-of-order authoritative world snapshot").into());
                        }
                    }
                    if self.visual.snapshot(self.state.as_ref(), &message) {
                        self.metrics.snapshot_pairs_interpolated += 1;
                    }
                    self.state = Some(message);
                    self.metrics.snapshots += 1;
                }
                Some("held_fire_released") => {
                    if message["reason"] != "no_live_weapons" {
                        return Err(invalid("unknown authoritative held-fire release reason").into());
                    }
                    self.held_fire_begin_request = None;
                    self.held_fire_rejected = true;
                    self.notice = "Manual fire ended: no surviving held weapons".into();
                }
                Some("campaign_receipt") => {
                    if !self.authenticated ||
                        message["campaign_outcome"]["player_side"].as_i64() !=
                            Some(i64::from(self.side)) {
                        return Err(invalid("campaign receipt belongs to another player").into());
                    }
                    let outcome = &message["campaign_outcome"];
                    let mission = outcome["mission_id"].as_str()
                        .filter(|mission| !mission.is_empty())
                        .ok_or_else(|| invalid("invalid campaign receipt mission"))?;
                    let success = outcome["mission_success"].as_bool()
                        .unwrap_or_else(|| outcome["winner_side"] == outcome["player_side"]);
                    let awarded_ships = outcome["awarded_ships"].as_array().map_or(0, Vec::len);
                    let awarded_squads = outcome["awarded_squads"].as_array().map_or(0, Vec::len);
                    let banked = message["banked_minerals"].as_u64()
                        .map(|amount| format!("; {amount} minerals banked"))
                        .unwrap_or_default();
                    let receipt = format!(
                        "Campaign saved: {mission} ({}); {awarded_ships} ships, {awarded_squads} squads awarded{banked}",
                        if success { "victory" } else { "defeat" },
                    );
                    self.campaign_receipt = Some(receipt.clone());
                    self.notice = receipt;
                    self.metrics.campaign_receipts += 1;
                }
                Some("battle_interrupted") => {
                    if !self.authenticated || message["protocol"].as_u64() != Some(1) ||
                        message["viewer_side"].as_i64() != Some(i64::from(self.side)) ||
                        message["reason"] != "Host ended before an authoritative battle result"
                    {
                        return Err(invalid("invalid authoritative battle interruption notice").into());
                    }
                    let notice = "Battle interrupted: no completed result".to_owned();
                    self.battle_result_receipt = Some(notice.clone());
                    self.notice = notice;
                    self.metrics.interrupted_battle_notices += 1;
                }
                Some("battle_result") => {
                    if !self.authenticated || message["protocol"].as_u64() != Some(1) ||
                        message["viewer_side"].as_i64() != Some(i64::from(self.side)) ||
                        message["world_id"].as_u64().is_none() ||
                        message["episode_revision"].as_u64().is_none() ||
                        message["winner_side"].as_i64().is_none()
                    {
                        return Err(invalid("native battle result belongs to another session or side").into());
                    }
                    let label = message["label"].as_str()
                        .filter(|text| !text.is_empty())
                        .ok_or_else(|| invalid("missing authoritative battle result label"))?;
                    let reason = message["reason"].as_str()
                        .filter(|text| !text.is_empty())
                        .ok_or_else(|| invalid("missing authoritative battle result reason"))?;
                    let text = format!("{label}: {reason}");
                    self.battle_result_receipt = Some(text.clone());
                    self.notice = text;
                    self.metrics.battle_result_receipts += 1;
                }
                Some("input_result") => {
                    self.metrics.acknowledgments += 1;
                    let request = message["request_id"].as_u64().unwrap_or(0);
                    let feedback = message["feedback"].as_str()
                        .unwrap_or("host acknowledgment");
                    if self.held_fire_begin_request == Some(request) {
                        self.held_fire_begin_request = None;
                        // A rejected Begin must not cause 30 new attempts
                        // per second while the player holds J. Require one
                        // release before requesting a different gesture.
                        if message["processed"] != true ||
                            feedback == "No selected weapons accepted held fire" {
                            self.held_fire_rejected = true;
                        }
                    }
                    self.notice = format!("Order {request}: {feedback}");
                }
                _ => return Err(invalid("unexpected native multiplayer protocol message").into()),
            }
        }
        if start > 0 { self.incoming.drain(..start); }
        // A rejected host session must not send previously queued player
        // commands to a different battle, or overwrite the reconnect
        // identity warning with a generic broken-pipe status.
        if !self.connected { return Ok(()); }
        if let Err(error) = self.flush() {
            if link_disconnected(&error) {
                self.connected = false;
                self.notice = "Host disconnected".into();
            } else { return Err(error.into()); }
        }
        Ok(())
    }

    fn send_action(&mut self, action: Value, notice: String) -> Result<(), Box<dyn Error>> {
        if !self.authenticated || !self.connected {
            return Err(invalid("not connected to native host").into());
        }
        // The local native window withholds player commands while its
        // authored campaign briefing is visible and after a terminal
        // outcome. The LAN window must obey the same presentation rule.
        // End is still allowed to release a live held-fire gesture.
        if self.input_suppressed &&
            !(action["kind"]=="held_fire" && action["input"]["action"]=="End")
        {
            return Err(invalid("orders unavailable during briefing or after battle").into());
        }
        let next = self.next_request.checked_add(1)
            .ok_or_else(|| invalid("native multiplayer request ID exhausted"))?;
        let mut packet = serde_json::to_vec(&json!({
            "kind": "input", "request_id": self.next_request, "action": action,
        }))?;
        packet.push(b'\n');
        if self.queued_bytes + packet.len() > MAX_OUTGOING {
            return Err(invalid("outgoing native orders exceed pending network budget").into());
        }
        self.queued_bytes += packet.len();
        self.queued.push_back(packet);
        self.next_request = next;
        self.metrics.orders_sent += 1;
        self.notice = notice;
        self.flush()?;
        Ok(())
    }

    fn selected_owners(state: &Value, selected: &[i64]) -> Vec<Value> {
        state["squads"].as_array().into_iter().flatten()
            .filter(|squad| squad["owned"] == true &&
                squad["controllable"] == true &&
                selected.contains(&squad["owner"]["squad_id"].as_i64().unwrap_or(-1)))
            .map(|squad| squad["owner"].clone()).collect()
    }

    fn order(&mut self, selected: &[i64], kind: &str, fields: Value) -> Result<(), Box<dyn Error>> {
        let state = self.state.as_ref().ok_or_else(||
            invalid("native battle has no snapshot"))?;
        let owners = Self::selected_owners(state, selected);
        if owners.is_empty() {
            return Err(invalid("select a live controllable friendly squad").into());
        }
        let count = owners.len();
        let mut input = json!({
            "world_id": state["world_id"],
            "episode_revision": state["episode_revision"],
            "squads": owners,
        });
        let object = fields.as_object().ok_or_else(|| invalid("invalid native action fields"))?;
        for (key, value) in object { input[key] = value.clone(); }
        self.send_action(json!({"kind": kind, "input": input}),
            format!("Sent {kind} for {count} selected squads"))
    }

    /// Only the Begin packet contains the original squad lifetimes.
    /// The server subsequently owns that gesture until End or disconnect;
    /// Aim never retransmits a new selection into the shared CombatWorld.
    fn held_fire(&mut self, selected: &[i64], event: &str, target: Option<Vec2>)
        -> Result<(), Box<dyn Error>>
    {
        let state = self.state.as_ref().ok_or_else(||
            invalid("native battle has no snapshot"))?;
        let action = match event {
            "Begin" => {
                let owners = Self::selected_owners(state, selected);
                if owners.is_empty() {
                    return Err(invalid("select a controllable squad to begin held fire").into());
                }
                let point = target.ok_or_else(|| invalid("held fire requires a target"))?;
                json!({"Begin": {"squads": owners, "target": {"x": point.x, "y": point.y}}})
            },
            "Aim" => {
                let point = target.ok_or_else(|| invalid("held fire requires a target"))?;
                json!({"Aim": {"target": {"x": point.x, "y": point.y}}})
            },
            "End" => json!("End"),
            _ => return Err(invalid("unknown held-fire gesture").into()),
        };
        let input = json!({
            "world_id": state["world_id"],
            "episode_revision": state["episode_revision"],
            "action": action,
        });
        let begin_request = (event == "Begin").then_some(self.next_request);
        self.send_action(json!({"kind": "held_fire", "input": input}),
            format!("Manual fire: {event}"))?;
        if let Some(request) = begin_request {
            self.held_fire_begin_request = Some(request);
        }
        Ok(())
    }

    fn flush(&mut self) -> io::Result<()> {
        let mut budget = 64 * 1024;
        while budget > 0 && self.connected {
            let Some(front) = self.queued.front() else { break };
            let end = (self.written + budget).min(front.len());
            match self.socket.write(&front[self.written..end]) {
                Ok(0) => { self.connected = false; self.notice = "Host disconnected".into(); break; }
                Ok(n) => {
                    budget -= n;
                    self.written += n;
                    if self.written == front.len() {
                        self.queued_bytes -= front.len();
                        self.queued.pop_front();
                        self.written = 0;
                    }
                }
                Err(error) if error.kind() == ErrorKind::WouldBlock => break,
                Err(error) if error.kind() == ErrorKind::Interrupted => continue,
                Err(error) => return Err(error),
            }
        }
        Ok(())
    }
}

fn position(value: &Value) -> Option<Vec2> {
    let x = value["x"].as_f64()? as f32;
    let y = value["y"].as_f64()? as f32;
    (x.is_finite() && y.is_finite()).then_some(Vec2::new(x, y))
}

fn bounds(state: &Value) -> Option<Aabb> {
    let points = state["map_bounds"].as_array()?;
    if points.len() != 2 { return None; }
    let point = |p: &Value| -> Option<Vec2> {
        let coordinates = p.as_array()?;
        if coordinates.len() != 2 { return None; }
        let x = coordinates[0].as_f64()? as f32;
        let y = coordinates[1].as_f64()? as f32;
        (x.is_finite() && y.is_finite()).then_some(Vec2::new(x, y))
    };
    let min = point(&points[0])?;
    let max = point(&points[1])?;
    (min.x < max.x && min.y < max.y).then_some(Aabb {min, max})
}

fn screen(camera: NativeCamera, point: Vec2) -> (i32, i32) {
    let x = FIELD as f32 * 0.5 + (point.x - camera.center.x) * camera.pixels_per_unit;
    let y = HEIGHT as f32 * 0.5 - (point.y - camera.center.y) * camera.pixels_per_unit;
    // Keep hostile or corrupted positional data from causing unbounded draw
    // loops before the actual pixel plotter clips the viewport.
    (x.clamp(-2000.0, 2000.0) as i32, y.clamp(-2000.0, 2000.0) as i32)
}

fn plot(pixels: &mut [u32], x: i32, y: i32, color: u32) {
    if x >= 0 && x < FIELD as i32 && y >= 0 && y < HEIGHT as i32 {
        pixels[y as usize * WIDTH + x as usize] = color;
    }
}
fn line(pixels: &mut [u32], from: (i32,i32), to: (i32,i32), color: u32) {
    let (mut x, mut y) = from;
    let (end_x, end_y) = to;
    let dx = (end_x - x).abs();
    let dy = -(end_y - y).abs();
    let step_x = if x < end_x {1} else {-1};
    let step_y = if y < end_y {1} else {-1};
    let mut error = dx + dy;
    for _ in 0..8002 {
        plot(pixels, x, y, color);
        if x == end_x && y == end_y { break; }
        let doubled = error * 2;
        if doubled >= dy { error += dy; x += step_x; }
        if doubled <= dx { error += dx; y += step_y; }
    }
}
fn circle(pixels: &mut [u32], (x,y): (i32,i32), radius: i32, color: u32) {
    for dy in -radius..=radius {
        for dx in -radius..=radius {
            if dx*dx + dy*dy <= radius*radius { plot(pixels,x+dx,y+dy,color); }
        }
    }
}
fn outline(pixels: &mut [u32], x: i32,y: i32, w: i32,h: i32,color: u32) {
    line(pixels,(x,y),(x+w,y),color);
    line(pixels,(x+w,y),(x+w,y+h),color);
    line(pixels,(x+w,y+h),(x,y+h),color);
    line(pixels,(x,y+h),(x,y),color);
}

/// Read only bounded wire geometry. The host supplied the real collider
/// used by gameplay; the remote client only paints its contour.
fn point_from_wire(value: &Value) -> Option<Vec2> {
    let coordinates = value.as_array()?;
    if coordinates.len() != 2 { return None; }
    let x = coordinates[0].as_f64()? as f32;
    let y = coordinates[1].as_f64()? as f32;
    (x.is_finite() && y.is_finite()).then_some(Vec2::new(x, y))
}

/// The host sends the same bounded primary physics collider used for
/// navigation, weapon collision and map interaction. Parse it once for both
/// rendered outlines and cursor targeting. No remote simulation state.
enum RemoteObstacleHull {
    Circle { center: Vec2, radius: f32 },
    Polygon { vertices: [Vec2; 64], len: usize },
}

impl RemoteObstacleHull {
    fn overlaps_cursor(&self, point: Vec2, radius: f32) -> bool {
        match self {
            Self::Circle { center, radius: hull_radius } =>
                circles_overlap(point, radius, *center, *hull_radius),
            Self::Polygon { vertices, len } =>
                circle_overlaps_polygon(point, radius, &vertices[..*len]),
        }
    }

    fn draw(&self, pixels: &mut [u32], camera: NativeCamera, color: u32) {
        match self {
            Self::Circle { center, radius } => {
                let diameter = *radius * camera.pixels_per_unit * 2.0;
                let segments = ((diameter * 0.2) as usize).clamp(12, 64);
                let mut previous = screen(camera, *center + Vec2::new(*radius, 0.0));
                for index in 1..=segments {
                    let angle = std::f32::consts::TAU * index as f32 / segments as f32;
                    let next = screen(camera, *center +
                        Vec2::new(angle.cos(), angle.sin()) * *radius);
                    line(pixels, previous, next, color);
                    previous = next;
                }
            }
            Self::Polygon { vertices, len } => {
                for index in 0..*len {
                    line(pixels, screen(camera, vertices[index]),
                        screen(camera, vertices[(index + 1) % *len]), color);
                }
            }
        }
    }
}

fn remote_obstacle_hull(item: &Value, center: Vec2,
    rotation_degrees: f32) -> Option<RemoteObstacleHull>
{
    let pose = Pose { position: center, rotation_degrees };
    let collider = &item["collider"];
    match collider["kind"].as_str() {
        Some("circle") => {
            let offset = point_from_wire(&collider["offset"])?;
            let radius = collider["radius"].as_f64().map(|r| r as f32)
                .filter(|r| r.is_finite() && *r >= 0.0)?;
            return Some(RemoteObstacleHull::Circle {
                center: pose.transform_point(offset), radius,
            });
        }
        Some("box") => {
            let offset = point_from_wire(&collider["offset"])?;
            let half = point_from_wire(&collider["half_extents"])?;
            if half.x < 0.0 || half.y < 0.0 { return None; }
            let mut vertices = [Vec2::ZERO; 64];
            for (index, corner) in [
                Vec2::new(-half.x, -half.y), Vec2::new(half.x, -half.y),
                Vec2::new(half.x, half.y), Vec2::new(-half.x, half.y),
            ].into_iter().enumerate() {
                vertices[index] = pose.transform_point(offset + corner);
            }
            return Some(RemoteObstacleHull::Polygon { vertices, len: 4 });
        }
        Some("polygon") => {
            let points = collider["vertices"].as_array()
                .filter(|vertices| (3..=64).contains(&vertices.len()))?;
            let mut vertices = [Vec2::ZERO; 64];
            for (index, source) in points.iter().enumerate() {
                vertices[index] = pose.transform_point(point_from_wire(source)?);
            }
            return Some(RemoteObstacleHull::Polygon {
                vertices, len: points.len(),
            });
        }
        _ => {}
    }
    // The authoritative primary AABB is the existing fallback for imported
    // polygon hulls too large to transmit. It is never an invented radius.
    let bounds = item["collider_bounds"].as_array().filter(|b| b.len() == 2)?;
    let min = point_from_wire(&bounds[0])?;
    let max = point_from_wire(&bounds[1])?;
    if min.x > max.x || min.y > max.y { return None; }
    let mut vertices = [Vec2::ZERO; 64];
    vertices[..4].copy_from_slice(&[
        Vec2::new(min.x, min.y), Vec2::new(max.x, min.y),
        Vec2::new(max.x, max.y), Vec2::new(min.x, max.y),
    ]);
    Some(RemoteObstacleHull::Polygon { vertices, len: 4 })
}

/// Draws the same collider the cursor can select. A cheap authoritative AABB
/// cull avoids rasterizing large hulls outside the player's screen.
fn draw_remote_obstacle_hull(
    pixels: &mut [u32], item: &Value, camera: NativeCamera,
    center: Vec2, rotation_degrees: f32,
) -> Option<bool> {
    let bounds = item["collider_bounds"].as_array()
        .filter(|points| points.len() == 2)
        .and_then(|points| Some((
            point_from_wire(&points[0])?,
            point_from_wire(&points[1])?,
        )));
    if let Some((min, max)) = bounds {
        let top_left = screen(camera, Vec2::new(min.x, max.y));
        let bottom_right = screen(camera, Vec2::new(max.x, min.y));
        if bottom_right.0 < 0 || top_left.0 > FIELD as i32 ||
            bottom_right.1 < 0 || top_left.1 > HEIGHT as i32 {
            return Some(false);
        }
    }
    let hull = remote_obstacle_hull(item, center, rotation_degrees)?;
    let color = if item["mining"] == true { GOLD } else { 0x7d92aa };
    hull.draw(pixels, camera, color);
    Some(true)
}

/// The authenticated world_state is already filtered by the host's
/// canonical faction fog. Never query a second enemy registry, infer
/// hidden objects, or issue commands from presentation-only markers.
fn draw_remote_minimap(
    pixels: &mut [u32], state: &Value, camera: NativeCamera,
    highlighted: &HashSet<i64>, visual: &RemoteVisual, fraction: f32,
) -> (u64, u64) {
    let Some(bounds) = bounds(state) else { return (0, 0) };
    let Some(layout) = MiniMapLayout::top_right(FIELD, HEIGHT, bounds)
        else { return (0, 0) };
    for y in layout.top..layout.bottom {
        pixels[y * WIDTH + layout.left..y * WIDTH + layout.right]
            .fill(0x101c2e);
    }
    let edges = [
        (layout.plot_left, layout.plot_top, layout.plot_right, layout.plot_top),
        (layout.plot_right, layout.plot_top, layout.plot_right, layout.plot_bottom),
        (layout.plot_right, layout.plot_bottom, layout.plot_left, layout.plot_bottom),
        (layout.plot_left, layout.plot_bottom, layout.plot_left, layout.plot_top),
    ];
    for (x0, y0, x1, y1) in edges {
        line(pixels, (x0.round() as i32, y0.round() as i32),
            (x1.round() as i32, y1.round() as i32), 0x6e829e);
    }
    let target = &state["campaign_objective"];
    let objective_ship = target["protected_ship_id"].as_i64()
        .or_else(|| target["escorted_ship_id"].as_i64());
    let objective_object = target["target_map_object_id"].as_i64();
    let mut ship_markers = 0_u64;
    let mut objective_markers = 0_u64;
    for ship in state["ships"].as_array().into_iter().flatten() {
        let Some(position) = visual.position(ship, 0, fraction) else { continue };
        let Some((x, y)) = layout.project(position) else { continue };
        let id = ship["id"].as_i64().unwrap_or(-1);
        let objective = objective_ship == Some(id);
        let selected = highlighted.contains(&id);
        let center = (x.round() as i32, y.round() as i32);
        let color = if objective { GOLD }
            else if selected { 0xffffff }
            else if ship["friendly"] == true { GREEN } else { RED };
        circle(pixels, center, if objective { 4 } else if selected { 3 } else { 1 }, color);
        ship_markers += 1;
        objective_markers += u64::from(objective);
    }
    if let Some(id) = objective_object {
        for object in state["obstacles"].as_array().into_iter().flatten() {
            if object["id"].as_i64() != Some(id) { continue; }
            if let Some(position) = visual.position(object, 2, fraction) {
                if let Some((x, y)) = layout.project(position) {
                    let center = (x.round() as i32, y.round() as i32);
                    outline(pixels, center.0 - 4, center.1 - 4, 8, 8, GOLD);
                    objective_markers += 1;
                }
            }
            break;
        }
    }
    // Public objective destination: never pretend the trigger already
    // activated. The AABB is the same host-authoritative geometry visible
    // in the native local-player battlefield.
    if let Some(points) = state["escort_destination"].as_array()
            .or_else(|| state["extraction_destination"].as_array())
        .filter(|points| points.len() == 2)
    {
        if let (Some(min), Some(max)) =
            (point_from_wire(&points[0]), point_from_wire(&points[1]))
        {
            if let (Some((x0, y0)), Some((x1, y1))) =
                (layout.project(min), layout.project(max))
            {
                outline(pixels, x0.round() as i32, y1.round() as i32,
                    (x1-x0).round() as i32, (y0-y1).round() as i32, GREEN);
                objective_markers += 1;
            }
        }
    }
    let bottom_left = camera.screen_to_world(0.0, HEIGHT as f32, FIELD, HEIGHT);
    let top_right = camera.screen_to_world(FIELD as f32, 0.0, FIELD, HEIGHT);
    if let Some((x0, y0, x1, y1)) = layout.camera_rect(bottom_left, top_right) {
        outline(pixels, x0.round() as i32, y0.round() as i32,
            (x1-x0).round() as i32, (y1-y0).round() as i32, 0xe2c473);
    }
    (ship_markers, objective_markers)
}

fn first_selected<'a>(state: Option<&'a Value>, selected: &[i64]) -> Option<&'a Value> {
    state?.get("squads")?.as_array()?.iter().find(|squad|
        squad["controllable"] == true && squad["owner"]["squad_id"]
            .as_i64().is_some_and(|id| selected.contains(&id)))
}

fn squad_for_ship<'a>(state: &'a Value, id: i64, side: i64) -> Option<&'a Value> {
    state["squads"].as_array()?.iter().find(|squad|
        squad["side"].as_i64() == Some(side) &&
        squad["members"].as_array().is_some_and(|members|
            members.iter().any(|member| member.as_i64() == Some(id))))
}
fn hit<'a>(state: &'a Value, camera: NativeCamera, x: i32, y: i32,
    visual: &RemoteVisual, fraction: f32)
    -> Option<(&'static str, &'a Value)>
{
    // A foreground ship wins over an obstacle underneath its hull.
    let mut ship_hit = None;
    let mut best_ship = 18 * 18;
    for ship in state["ships"].as_array().into_iter().flatten() {
        let Some(point) = visual.position(ship, 0, fraction) else { continue };
        let (sx, sy) = screen(camera, point);
        let distance = (sx - x).pow(2) + (sy - y).pow(2);
        if distance < best_ship {
            best_ship = distance;
            ship_hit = Some(("ship", ship));
        }
    }
    if ship_hit.is_some() { return ship_hit; }

    let cursor = camera.screen_to_world(x as f32, y as f32, FIELD, HEIGHT);
    let pick_radius = 10.0 / camera.pixels_per_unit.max(0.001);
    let mut selected = None;
    let mut nearest = i32::MAX;
    for obj in state["obstacles"].as_array().into_iter().flatten() {
        let Some(point) = visual.position(obj, 2, fraction) else { continue };
        let overlap = remote_obstacle_hull(obj, point,
            visual.heading_for(obj, 2, fraction))
            .map_or_else(|| {
                // Invalid/missing hull: preserve the existing point picker.
                let (sx, sy) = screen(camera, point);
                (sx - x).pow(2) + (sy - y).pow(2) < 18 * 18
            }, |hull| hull.overlaps_cursor(cursor, pick_radius));
        if !overlap { continue; }
        let (sx, sy) = screen(camera, point);
        let distance = (sx - x).pow(2) + (sy - y).pow(2);
        if distance < nearest {
            selected = Some(("obstacle", obj));
            nearest = distance;
        }
    }
    selected
}

// A compact three-column layout uses the existing canonical 24-action
// registry. Drawing and hit-testing share these exact rectangles; none of
// these buttons owns gameplay state.
fn remote_tactical_rect(action: HudAction) -> Option<HudRect> {
    let index=ACTIONS.iter().position(|&entry|entry==action)?;
    let left=FIELD+14+(index%3)*83;
    let top=455+(index/3)*27;
    Some(HudRect {left,top,right:left+77,bottom:top+23})
}

fn remote_tactical_hit(x: f32,y: f32) -> Option<HudAction> {
    ACTIONS.iter().copied().find(|&action|
        remote_tactical_rect(action).is_some_and(|rect|rect.contains(x,y)))
}

/// One dispatcher for LAN keyboard and on-screen tactical commands.
fn remote_tactical_action(
    peer: &mut PeerClient, selected: &mut Vec<i64>,
    mode: &mut Option<TargetMode>, patrol_start: &mut Option<Vec2>,
    held_fire_active: &mut bool, held_fire_target: &mut Option<Vec2>,
    shooting_menu: &mut bool, shooting_focus: &mut u8,
    action: HudAction,
) -> Result<(),Box<dyn Error>> {
    if peer.input_suppressed && action!=HudAction::StopFire {
        return Err(invalid("orders unavailable during briefing or after battle").into());
    }
    if action==HudAction::SelectAll {
        if let Some(state)=peer.state.as_ref() {
            *selected=state["squads"].as_array().into_iter().flatten()
                .filter(|s|s["controllable"]==true&&s["source_root"]==true)
                .filter_map(|s|s["owner"]["squad_id"].as_i64()).collect();
        }
        *mode=None;*patrol_start=None;
        return Ok(());
    }
    if selected.is_empty() {
        return Err(invalid("select a live controllable friendly squad").into());
    }
    let target=match action {
        HudAction::Attack=>Some(TargetMode::Attack),
        HudAction::Circle=>Some(TargetMode::Circle),
        HudAction::RightSwipe=>Some(TargetMode::RightSwipe),
        HudAction::LeftSwipe=>Some(TargetMode::LeftSwipe),
        HudAction::InAndOut=>Some(TargetMode::InAndOut),
        HudAction::Guard=>Some(TargetMode::Guard),
        HudAction::Patrol=>Some(TargetMode::Patrol),
        HudAction::Mine=>Some(TargetMode::Mine),
        HudAction::Heal=>Some(TargetMode::Heal),
        HudAction::Retreat=>Some(TargetMode::Retreat),
        HudAction::PointFire=>Some(TargetMode::Fire),
        _=>None,
    };
    if let Some(target)=target {
        *mode=Some(target);
        *patrol_start=None;
        peer.notice=action.hint().to_owned();
        return Ok(());
    }
    *mode=None;
    *patrol_start=None;
    match action {
        HudAction::Shooting=>{
            *shooting_focus=first_selected(peer.state.as_ref(),selected)
                .and_then(|s|s["shooting"].as_u64())
                .unwrap_or(1).clamp(1,40) as u8;
            *shooting_menu=!*shooting_menu;
            return Ok(());
        }
        HudAction::StopFire if *held_fire_active=>{
            if let Err(error)=peer.held_fire(selected,"End",None) {
                let _=peer.socket.shutdown(std::net::Shutdown::Both);
                peer.connected=false;
                return Err(error);
            }
            *held_fire_active=false;*held_fire_target=None;
            return Ok(());
        }
        HudAction::StopFire=>peer.order(selected,"fire",json!({"target":null})),
        HudAction::Hold=>peer.order(selected,"support",json!({"action":"Hold"})),
        HudAction::CeaseFire=>peer.order(selected,"cease_fire",json!({"cease_fire":true})),
        HudAction::AttackOnSight=>peer.order(selected,"cease_fire",json!({"cease_fire":false})),
        HudAction::Beacon=>peer.order(selected,"special",json!({"kind":"DropBeacon"})),
        HudAction::Detonate=>peer.order(selected,"special",json!({"kind":"Detonate"})),
        HudAction::Charge=>peer.order(selected,"special",json!({"kind":"Charge"})),
        HudAction::MatchSpeed=>peer.order(selected,"match_speed",json!({"match_speed":true})),
        HudAction::FreeSpeed=>peer.order(selected,"match_speed",json!({"match_speed":false})),
        HudAction::Chase=>peer.order(selected,"chase",json!({"chasing":true})),
        HudAction::LockOn=>{
            let all_locked=selected.iter().all(|id|
                peer.state.as_ref().is_some_and(|state|
                    state["squads"].as_array().into_iter().flatten().any(|s|
                        s["controllable"]==true&&s["locked_on"]==true&&
                        s["owner"]["squad_id"].as_i64()==Some(*id))));
            peer.order(selected,"lock_on",json!({"locked":!all_locked}))
        }
        _=>return Err(invalid("unrecognized native tactical action").into()),
    }
}

fn source_numbered_squads(state: &Value) -> Vec<(u64, i64)> {
    let mut numbered: Vec<_> = state["squads"].as_array().into_iter().flatten()
        .filter(|squad| squad["controllable"]==true)
        .filter_map(|squad| Some((
            squad["source_number"].as_u64()?,
            squad["owner"]["squad_id"].as_i64()?,
        ))).collect();
    numbered.sort_unstable_by_key(|&(number, _)| number);
    numbered
}

/// Unity numbered shortcuts wrap against the authored original side count,
/// rather than renumbering after squad casualties.
fn numbered_squad(state: &Value, key: u64) -> Option<i64> {
    let first=state["squads"].as_array()?.iter()
        .find(|s| s["owned"]==true && s["source_number"].as_u64().is_some())?;
    let count=first["original_squad_count"].as_u64()?;
    if count==0 {return None;}
    let input=if key==0 {10} else {key};
    let target=(input-1)%count+1;
    source_numbered_squads(state).into_iter()
        .find(|&(number,_)|number==target).map(|(_,id)|id)
}

fn cycle_numbered_squad(state: &Value, selected: &[i64], forward: bool) -> Option<i64> {
    let numbered=source_numbered_squads(state);
    if numbered.is_empty(){return None;}
    let current=selected.first().and_then(|id|
        numbered.iter().position(|&(_,candidate)|candidate==*id));
    let next=match (current,forward) {
        (Some(index),true)=>(index+1)%numbered.len(),
        (Some(index),false)=>(index+numbered.len()-1)%numbered.len(),
        (None,true)=>0,
        (None,false)=>numbered.len()-1,
    };
    Some(numbered[next].1)
}

/// Double-click selects every controllable squad containing a surviving
/// ship of this source type, in the host's canonical squad order. The LAN
/// window only projects authenticated roster membership already on the
/// wire; the host validates every later command against those lifetimes.
fn squads_with_ship_type(state: &Value, ship_type: u64) -> Vec<i64> {
    let matching: HashSet<i64> = state["ships"].as_array().into_iter().flatten()
        .filter(|ship|ship["owned"]==true &&
            ship["ship_type"].as_u64()==Some(ship_type))
        .filter_map(|ship|ship["id"].as_i64()).collect();
    state["squads"].as_array().into_iter().flatten()
        .filter(|squad|squad["controllable"]==true &&
            squad["members"].as_array().is_some_and(|members|
                members.iter().any(|id|id.as_i64()
                    .is_some_and(|id|matching.contains(&id)))))
        .filter_map(|squad|squad["owner"]["squad_id"].as_i64()).collect()
}

fn focus_squad(state: &Value, squad_id: i64) -> Option<Vec2> {
    let squad=state["squads"].as_array()?.iter().find(|s|
        s["controllable"]==true && s["owner"]["squad_id"].as_i64()==Some(squad_id))?;
    let ship_id=squad["members"].as_array()?.iter().find_map(Value::as_i64)?;
    state["ships"].as_array()?.iter().find(|ship|
        ship["id"].as_i64()==Some(ship_id)).and_then(position)
}

/// The remote window presents the host's authored dialogue, never a second
/// mission progression state. Peers choose which line to read locally.
fn draw_campaign_dialogue(pixels: &mut [u32], state: &Value, line_index: usize) {
    let Ok(cue)=serde_json::from_value::<CampaignCue>(
        state["campaign_dialogue"]["cue"].clone(),
    ) else {return;};
    let lines=super::campaign_dialogue::lines(cue);
    let Some(&(speaker,message))=lines.get(line_index) else{return;};
    for scanline in pixels.chunks_exact_mut(WIDTH).skip(HEIGHT-160).take(96) {
        scanline[..FIELD].fill(0x1c3045);
    }
    let heading=format!("{}   Dialogue {}/{}",
        speaker,line_index+1,lines.len());
    text::draw_at(pixels,WIDTH,15,HEIGHT-152,
        text::fit(&heading,FIELD-30),1,[251,217,146]);
    let mut rest=message;
    for index in 0..3 {
        if rest.is_empty(){break;}
        let fit=text::fit(rest,FIELD-36);
        let piece=if fit.len()<rest.len(){
            fit.rsplit_once(' ').map_or(fit,|(head,_)|head)
        }else{fit};
        let piece=if piece.is_empty(){fit}else{piece};
        text::draw_at(pixels,WIDTH,15,HEIGHT-127+index*19,piece,1,TEXT);
        rest=rest[piece.len()..].trim_start();
    }
    text::draw_at(pixels,WIDTH,15,HEIGHT-72,
        "L next   K skip   Backspace previous",1,MUTED);
}

fn draw(pixels: &mut [u32], state: Option<&Value>, camera: Option<NativeCamera>,
    selected: &[i64], notice: &str, side: i32, mode: Option<TargetMode>,
    patrol_start: Option<Vec2>, metrics: &WireCounts, dialogue_line: usize,
    held_fire_active: bool, shooting_menu: bool, shooting_focus: u8,
    visual: &RemoteVisual, fraction: f32,
    artwork: &RemoteArtwork, show_art: bool, obstacle_hull_draws: &mut u64,
    minimap_ship_markers: &mut u64, minimap_objective_markers: &mut u64,
    fog_masked_pixels: &mut u64) {
    // F6 avoids allocating or clearing a second per-frame art layer,
    // while normal mode reuses the exact local game sprite raster.
    if show_art {
        for (line, sprites) in pixels.chunks_exact_mut(WIDTH)
            .zip(artwork.raster.pixels().chunks_exact(FIELD)) {
            line[..FIELD].copy_from_slice(sprites);
            line[FIELD..].fill(0x152439);
        }
    } else {
        for line in pixels.chunks_exact_mut(WIDTH) {
            line[..FIELD].fill(0x080f1a);
            line[FIELD..].fill(0x152439);
        }
    }
    text::draw_at(pixels, WIDTH, FIELD + 14, 18, "BEES NATIVE", 2, TEXT);
    text::draw_at(pixels, WIDTH, FIELD + 14, 57,
        &format!("Remote player side {side}"), 1, MUTED);
    if let (Some(state), Some(camera)) = (state,camera) {
        if let Some(map) = bounds(state) {
            let a=screen(camera,map.min);
            let b=screen(camera,map.max);
            outline(pixels,a.0,b.1,b.0-a.0,a.1-b.1,0x395772);
        }
        if let Some(corner)=patrol_start {
            outline(pixels,screen(camera,corner).0-7,
                screen(camera,corner).1-7,14,14,0xf7d68f);
        }
        for obj in state["obstacles"].as_array().into_iter().flatten() {
            if let Some(point)=visual.position(obj, 2, fraction) {
                if artwork.asteroids.contains(&obj["id"].as_i64().unwrap_or(-1)) {
                    continue;
                }
                match draw_remote_obstacle_hull(
                    pixels, obj, camera, point,
                    visual.heading_for(obj, 2, fraction),
                ) {
                    Some(true) => *obstacle_hull_draws += 1,
                    Some(false) => {}, // collider wholly offscreen
                    None => {
                        let coord=screen(camera,point);
                        circle(pixels,coord,5,if obj["mining"]==true {GOLD} else {0x7d92aa});
                    }
                }
            }
        }
        for shot in state["projectiles"].as_array().into_iter().flatten() {
            if !artwork.projectiles.contains(&shot["id"].as_i64().unwrap_or(-1)) {
                if let Some(point)=visual.position(shot, 1, fraction) {
                    circle(pixels,screen(camera,point),2,0xf8d798);
                }
            }
        }
        // Build selected ship membership once per rendered snapshot.
        // Avoid an O(ships * squads * members) roster scan on large battles.
        let highlighted: HashSet<i64> = state["squads"].as_array().into_iter().flatten()
            .filter(|s|s["controllable"]==true &&
                s["owner"]["squad_id"].as_i64().is_some_and(|id|selected.contains(&id)))
            .flat_map(|s|s["members"].as_array().into_iter().flatten())
            .filter_map(Value::as_i64).collect();
        for ship in state["ships"].as_array().into_iter().flatten() {
            let Some(point)=visual.position(ship, 0, fraction) else { continue };
            let center=screen(camera,point);
            let selected_ship=ship["owned"]==true &&
                highlighted.contains(&ship["id"].as_i64().unwrap_or(-1));
            if !selected_ship && (center.0 < -24 || center.0 > FIELD as i32 + 24 ||
                center.1 < -24 || center.1 > HEIGHT as i32 + 24) {
                // The host still supplied the authoritative entity; only
                // drawing work outside the actual viewport is skipped.
                continue;
            }
            // Side allegiance and command rights are separate. The host
            // includes AI-controlled friendly ships but never permits a
            // remote peer to select their unowned squad.
            let friendly=ship["friendly"]==true;
            let color=if friendly {GREEN} else {RED};
            let angle=visual.heading(ship, fraction).to_radians();
            let (sin,cos)=angle.sin_cos();
            let painted = artwork.ships.contains(&ship["id"].as_i64().unwrap_or(-1));
            // When genuine source artwork is installed, do not redraw the
            // approximation on top. Missing sprites retain recognizable
            // geometric fallback. Selected ship HUD is unchanged.
            if !painted {
                let tip=(center.0+(cos*12.0) as i32,center.1-(sin*12.0) as i32);
                let left=(center.0+((-cos*7.0-sin*7.0) as i32),
                          center.1-((-sin*7.0+cos*7.0) as i32));
                let right=(center.0+((-cos*7.0+sin*7.0) as i32),
                           center.1-((-sin*7.0-cos*7.0) as i32));
                line(pixels,tip,left,color);
                line(pixels,left,right,color);
                line(pixels,right,tip,color);
            }
            let health=ship["health"].as_f64().unwrap_or(0.0);
            let maximum=ship["max_health"].as_f64().unwrap_or(1.0).max(1.0);
            if health.is_finite() && maximum.is_finite() {
                line(pixels,(center.0-10,center.1+14),(center.0+10,center.1+14),0x4f6377);
                line(pixels,(center.0-10,center.1+14),
                     (center.0-10+(20.0*(health/maximum).clamp(0.0,1.0)) as i32,center.1+14),
                     color);
            }
            if selected_ship {
                outline(pixels,center.0-15,center.1-17,30,34,0xf9f0a1);
                if let Some(destination)=ship["destination"].as_array() {
                    if destination.len()==2 {
                        if let (Some(x),Some(y))=(destination[0].as_f64(),destination[1].as_f64()) {
                            let dest=Vec2::new(x as f32,y as f32);
                            if dest.is_finite() {line(pixels,center,screen(camera,dest),0x2d6070);}
                        }
                    }
                }
            }
        }
        // Mask both source artwork and geometric fallbacks using the
        // local native renderer's exact circle raster. In particular,
        // remembered death sprites cannot outlive the authenticated
        // shrinking sight mask. HUD, dialogue and minimap are drawn after.
        *fog_masked_pixels += artwork.mask_field(pixels);
        // Objective markers use only the host's already-fog-filtered
        // ship/obstacle projection. A remote peer cannot look up a target
        // whose coordinates were withheld by the host.
        if let Some(objective) = state["campaign_objective"].as_object() {
            let kind = objective.get("kind").and_then(Value::as_str)
                .unwrap_or("survival");
            let (key, source) = match kind {
                "survival" => ("protected_ship_id", "ships"),
                "escort" => ("escorted_ship_id", "ships"),
                "demolition" => ("target_map_object_id", "obstacles"),
                _ => ("", ""),
            };
            if let Some(target_id) = objective.get(key).and_then(Value::as_i64) {
                if let Some(target) = state[source].as_array().into_iter().flatten()
                    .find(|item| item["id"].as_i64() == Some(target_id))
                {
                    let category = if source == "ships" { 0 } else { 2 };
                    if let Some(point) = visual.position(target, category, fraction) {
                        let center = screen(camera, point);
                        outline(pixels, center.0 - 20, center.1 - 22,
                            40, 44, GOLD);
                    }
                }
            }
        }
        // A convoy exit is public only to its authenticated commander.
        // Render its exact physical AABB even before host activation,
        // never a locally invented pathfinding or trigger position.
        if let Some(points) = state["escort_destination"].as_array()
            .or_else(|| state["extraction_destination"].as_array())
            .filter(|points| points.len() == 2)
        {
            if let (Some(min), Some(max)) =
                (point_from_wire(&points[0]), point_from_wire(&points[1]))
            {
                if min.x < max.x && min.y < max.y {
                    let a = screen(camera, Vec2::new(min.x, max.y));
                    let b = screen(camera, Vec2::new(max.x, min.y));
                    outline(pixels, a.0, a.1, b.0-a.0, b.1-a.1, GREEN);
                }
            }
        }
        let (ships, objectives) = draw_remote_minimap(
            pixels, state, camera, &highlighted, visual, fraction,
        );
        *minimap_ship_markers += ships;
        *minimap_objective_markers += objectives;
        text::draw_at(pixels, WIDTH, FIELD+14, 90,
            &format!("World {}  Tick {}",state["world_id"],state["tick"]),1,TEXT);
        let friendly=state["squads"].as_array().into_iter().flatten().filter(|s|s["owned"]==true).count();
        text::draw_at(pixels, WIDTH, FIELD+14, 111,
            &format!("Squads {friendly}   Selected {}",selected.len()),1,MUTED);
        for (index,squad) in state["squads"].as_array().into_iter().flatten()
            .filter(|s|s["owned"]==true).take(12).enumerate() {
            let id=squad["owner"]["squad_id"].as_i64().unwrap_or(-1);
            let label=format!("{} ID {}: {} ships{}",if selected.contains(&id) {">"} else {" "},
                id,squad["members"].as_array().map_or(0,Vec::len),
                if squad["controllable"]==true {""} else {" (orders off)"});
            text::draw_at(pixels, WIDTH, FIELD+14, 150+index*22,
                text::fit(&label,WIDTH-FIELD-24),1,
                if selected.contains(&id){TEXT}else{MUTED});
        }
        if let Some(squad)=first_selected(Some(state),selected) {
            let id=squad["owner"]["squad_id"].as_i64().unwrap_or(0);
            let shooting=squad["shooting"].as_u64().unwrap_or(0);
            text::draw_at(pixels,WIDTH,FIELD+14,417,
                &format!("Squad {id}  Shooting {shooting}"),1,TEXT);
            let enabled=|key:&str|if squad[key]==true {"On"} else {"Off"};
            let flags=format!("Chase {} Lock {} Speed {}",
                enabled("chasing"),enabled("locked_on"),enabled("match_speed"));
            text::draw_at(pixels,WIDTH,FIELD+14,435,
                text::fit(&flags,WIDTH-FIELD-24),1,MUTED);
        }
        let outcome=state["terminal_outcome"]["label"].as_str().map(str::to_owned)
            .or_else(||(!state["terminal_result"].is_null()).then(||
                format!("Winner side {}",state["terminal_result"])));
        if let Some(outcome)=outcome {
            let reason = state["terminal_outcome"]["reason"].as_str()
                .unwrap_or("Battle finished");
            let seconds = state["terminal_outcome"]["duration_seconds"]
                .as_f64().filter(|duration| duration.is_finite() && *duration >= 0.0)
                .unwrap_or(0.0);
            let label = format!("Result: {outcome}  /  {reason}  /  {seconds:.1}s");
            text::draw_at(pixels,WIDTH,16,20,
                text::fit(&label,FIELD-32),2,[255,210,129]);
        } else if let Some(objective)=state["campaign_objective"].as_object() {
            if objective.get("kind").and_then(Value::as_str) == Some("extraction") {
                let escaping = objective.get("exiting_side")
                    .and_then(Value::as_i64) == Some(i64::from(side));
                let label = if escaping {
                    "Extract your fleet to the marked exit"
                } else { "Intercept enemy fleet before extraction" };
                text::draw_at(pixels,WIDTH,16,23,
                    text::fit(label,FIELD-32),1,[255,211,130]);
            }
            let remaining = objective.get("seconds_remaining")
                .and_then(Value::as_f64).filter(|v| v.is_finite() && *v >= 0.0);
            let duration = objective.get("duration_seconds")
                .and_then(Value::as_f64).filter(|v| v.is_finite() && *v > 0.0);
            if let (Some(remaining), Some(duration)) = (remaining, duration) {
                let objective_kind = objective.get("kind").and_then(Value::as_str)
                    .unwrap_or("survival");
                let seconds = remaining.ceil().min(duration.ceil()) as u64;
                let name = match objective_kind {
                    "survival" => {
                        let percent = 100.0 * (1.0 - remaining / duration).clamp(0.0, 1.0);
                        let waves = state["reinforcements"]["pending"].as_u64().unwrap_or(0);
                        let protected = objective.get("protected_ship_id")
                            .and_then(Value::as_i64).unwrap_or(0);
                        let defending = objective.get("defending_side")
                            .and_then(Value::as_i64) == Some(i64::from(side));
                        format!("{} #{} {:02}:{:02}  {:.0}%  Waves {}",
                            if defending { "Protect" } else { "Destroy" },
                            protected, seconds / 60, seconds % 60, percent, waves)
                    },
                    "demolition" => {
                        let id = objective.get("target_map_object_id")
                            .and_then(Value::as_i64).unwrap_or(0);
                        let attacking = objective.get("attacking_side")
                            .and_then(Value::as_i64) == Some(i64::from(side));
                        let health = state["obstacles"].as_array().into_iter().flatten()
                            .find(|item| item["id"].as_i64() == Some(id))
                            .and_then(|item| item["health"].as_i64())
                            .map(|hp| format!("  {hp} HP")).unwrap_or_default();
                        format!("{} object #{}{}  {:02}:{:02}",
                            if attacking { "Destroy" } else { "Defend" },
                            id, health, seconds / 60, seconds % 60)
                    },
                    "escort" => {
                        let id = objective.get("escorted_ship_id")
                            .and_then(Value::as_i64).unwrap_or(0);
                        let health = state["ships"].as_array().into_iter().flatten()
                            .find(|item| item["id"].as_i64() == Some(id))
                            .and_then(|item| item["health"].as_i64())
                            .map(|hp| format!("  {hp} HP")).unwrap_or_default();
                        let escorting = objective.get("escorting_side")
                            .and_then(Value::as_i64) == Some(i64::from(side));
                        format!("{} ship #{}{}  {:02}:{:02}",
                            if escorting { "Escort" } else { "Intercept" },
                            id, health, seconds / 60, seconds % 60)
                    },
                    _ => String::new(),
                };
                if !name.is_empty() {
                    text::draw_at(pixels,WIDTH,16,23,
                        text::fit(&name,FIELD-32),1,[255,211,130]);
                }
            }
        } else if let Some(waves)=state["reinforcements"].as_object() {
            // Use only the host's side-authorized Level wave state, not
            // locally inferred ship counts or a client-side timer.
            let pending=waves.get("pending").and_then(Value::as_u64).unwrap_or(0);
            let tick=state["tick"].as_u64().unwrap_or(0);
            let dt=state["fixed_step_seconds"].as_f64().unwrap_or(0.0);
            let recent=waves.get("last_arrival_tick").and_then(Value::as_u64)
                .is_some_and(|arrival|tick>=arrival &&
                    dt.is_finite()&&dt>0.0 &&
                    (tick-arrival) as f64*dt<3.0);
            let status=if recent {
                Some("Reinforcements arrived".to_owned())
            }else if pending==0 {
                None
            }else if let Some(next)=waves.get("next_tick").and_then(Value::as_u64) {
                let seconds=((next.saturating_sub(tick) as f64)*dt).max(0.0);
                Some(format!("Reinforcements: {pending} waves  Next {:.0}s",seconds))
            }else {
                Some(format!("Reinforcements: {pending} waves  Awaiting mission event"))
            };
            if let Some(status)=status {
                text::draw_at(pixels,WIDTH,16,23,
                    text::fit(&status,FIELD-32),1,[255,211,130]);
            }
        }
    } else {
        text::draw_at(pixels,WIDTH,30,30,"Waiting for authoritative battle state",2,TEXT);
    }
    if let Some(state)=state {draw_campaign_dialogue(pixels,state,dialogue_line);}
    if held_fire_active {
        text::draw_at(pixels,WIDTH,15,HEIGHT-44,
            "MANUAL FIRE - release J to stop",1,[255,221,151]);
    }
    for &action in &ACTIONS {
        let Some(rect)=remote_tactical_rect(action) else{continue};
        let active=match action {
            HudAction::Attack=>mode==Some(TargetMode::Attack),
            HudAction::Guard=>mode==Some(TargetMode::Guard),
            HudAction::Patrol=>mode==Some(TargetMode::Patrol),
            HudAction::Mine=>mode==Some(TargetMode::Mine),
            HudAction::Heal=>mode==Some(TargetMode::Heal),
            HudAction::Retreat=>mode==Some(TargetMode::Retreat),
            HudAction::Circle=>mode==Some(TargetMode::Circle),
            HudAction::RightSwipe=>mode==Some(TargetMode::RightSwipe),
            HudAction::LeftSwipe=>mode==Some(TargetMode::LeftSwipe),
            HudAction::InAndOut=>mode==Some(TargetMode::InAndOut),
            HudAction::PointFire=>mode==Some(TargetMode::Fire),
            _=>false,
        };
        let color=if active{0x3d635d}else{0x244056};
        for line in pixels.chunks_exact_mut(WIDTH)
            .skip(rect.top).take(rect.bottom-rect.top) {
            line[rect.left..rect.right].fill(color);
        }
        outline(pixels,rect.left as i32,rect.top as i32,
            (rect.right-rect.left) as i32,(rect.bottom-rect.top) as i32,
            if active{GOLD}else{0x456782});
        text::draw_at(pixels,WIDTH,rect.left+4,rect.top+7,
            action.label(),1,if active{TEXT}else{MUTED});
    }
    if let Some(mode)=mode {
        text::draw_at(pixels,WIDTH,15,HEIGHT-55,
            match mode {
                TargetMode::Fire=>"Click map to FIRE, Esc cancels",
                TargetMode::Attack=>"Click visible enemy squad to ATTACK",
                TargetMode::Circle=>"Click enemy squad to CIRCLE",
                TargetMode::RightSwipe=>"Click enemy squad for RIGHT SWIPE",
                TargetMode::LeftSwipe=>"Click enemy squad for LEFT SWIPE",
                TargetMode::InAndOut=>"Click enemy squad for IN & OUT",
                TargetMode::Guard=>"Click friendly squad to GUARD",
                TargetMode::Mine=>"Click mining asteroid to MINE",
                TargetMode::Heal=>"Click friendly Beehive to HEAL",
                TargetMode::Retreat=>"Click friendly WarpGate to RETREAT",
                TargetMode::Patrol=>"Click first / second PATROL corner",
            },1,[255,221,151]);
    }
    if shooting_menu {
        for (index,name) in SHOOTING_STRATEGIES.iter().enumerate() {
            let Some(rect)=shooting_rect(WIDTH,HEIGHT,index) else{continue};
            let focused=index+1==usize::from(shooting_focus);
            for line in pixels.chunks_exact_mut(WIDTH)
                .skip(rect.top).take(rect.bottom-rect.top) {
                line[rect.left..rect.right].fill(if focused{0x45675f}else{0x182c44});
            }
            outline(pixels,rect.left as i32,rect.top as i32,
                (rect.right-rect.left) as i32,(rect.bottom-rect.top) as i32,
                if focused{GOLD}else{0x456782});
            let label=format!("{:02} {}",index+1,name);
            text::draw_at(pixels,WIDTH,rect.left+7,rect.top+7,
                text::fit(&label,rect.right-rect.left-14),1,
                if focused{TEXT}else{MUTED});
        }
    }
    text::draw_at(pixels,WIDTH,10,HEIGHT-25,text::fit(notice,FIELD-20),1,TEXT);
    text::draw_at(pixels,WIDTH,FIELD+14,HEIGHT-24,
        &format!("RX {} KiB  updates {}",metrics.bytes_received/1024,metrics.snapshots),1,MUTED);
}

fn order_at(peer: &mut PeerClient, selected: &[i64], camera: NativeCamera,
    x: i32,y: i32, mode: Option<TargetMode>) -> Result<(),Box<dyn Error>> {
    if x<0||x>=FIELD as i32||y<0||y>=HEIGHT as i32{return Ok(())}
    let Some(state)=peer.state.as_ref() else{return Ok(())};
    let hit=hit(state,camera,x,y,&peer.visual,peer.visual.fraction());
    let action=match (mode,hit) {
        (Some(TargetMode::Attack | TargetMode::Circle | TargetMode::RightSwipe |
              TargetMode::LeftSwipe | TargetMode::InAndOut),Some(("ship",target))) |
        (None,Some(("ship",target))) if target["friendly"]==false => {
            let Some(squad)=squad_for_ship(state,target["id"].as_i64().unwrap_or(-1),
                target["side"].as_i64().unwrap_or(0)) else {
                return Err(invalid("enemy target has no visible squad").into())
            };
            let target = &squad["owner"];
            let action = match mode {
                Some(TargetMode::Circle) => json!({"Circle":{"target":target}}),
                Some(TargetMode::RightSwipe) => json!({"RightSwipe":{"target":target}}),
                Some(TargetMode::LeftSwipe) => json!({"LeftSwipe":{"target":target}}),
                Some(TargetMode::InAndOut) => json!({"InAndOut":{"target":target}}),
                _ => json!({"Attack":{"target":target}}),
            };
            ("support",json!({"action":action}))
        }
        (Some(TargetMode::Guard),Some(("ship",target))) |
        (None,Some(("ship",target))) if target["friendly"]==true => {
            let Some(squad)=squad_for_ship(state,target["id"].as_i64().unwrap_or(-1),
                target["side"].as_i64().unwrap_or(0)) else {
                return Err(invalid("friendly target has no visible squad").into())
            };
            if mode.is_none() && squad["owner"]["squad_id"].as_i64()
                .is_some_and(|id| selected.contains(&id))
            {
                // A context click on one's already-selected formation is a
                // destination order, not a request to guard itself.
                let point = camera.screen_to_world(x as f32,y as f32,FIELD,HEIGHT);
                ("move",json!({"destination":{"x":point.x,"y":point.y}}))
            } else {
                ("support",json!({"action":{"Guard":{"target":squad["owner"]}}}))
            }
        }
        (Some(TargetMode::Mine),Some(("obstacle",target))) |
        (None,Some(("obstacle",target))) if target["mining"]==true => {
            ("support",json!({"action":{"Mining":{"asteroid_id":target["id"]}}}))
        }
        (None,Some(("obstacle",target))) if
            state["campaign_objective"]["kind"] == "demolition" &&
            state["campaign_objective"]["attacking_side"].as_i64() ==
                Some(i64::from(peer.side)) &&
            target["id"] == state["campaign_objective"]["target_map_object_id"] => {
            // Only fog-visible physical target colliders arrive on the
            // network. Use the authoritative bounds rather than trusting
            // a guessed location or changing the regular point-fire API.
            let points=target["collider_bounds"].as_array()
                .filter(|points|points.len()==2)
                .ok_or_else(||invalid("demolition target lacks verified collider bounds"))?;
            let min=point_from_wire(&points[0])
                .ok_or_else(||invalid("demolition target bounds are invalid"))?;
            let max=point_from_wire(&points[1])
                .ok_or_else(||invalid("demolition target bounds are invalid"))?;
            let point=(min+max)*0.5;
            ("fire",json!({"target":{"x":point.x,"y":point.y}}))
        }
        (Some(TargetMode::Heal),Some(("ship",target))) if target["friendly"]==true => {
            ("support",json!({"action":{"Heal":{"beehive_ids":[target["id"]]}}}))
        }
        (Some(TargetMode::Retreat),Some(("ship",target))) if target["friendly"]==true => {
            ("support",json!({"action":{"FullRetreat":{"warp_gate_id":target["id"]}}}))
        }
        (Some(TargetMode::Attack|TargetMode::Circle|TargetMode::RightSwipe|
              TargetMode::LeftSwipe|TargetMode::InAndOut|TargetMode::Guard|
              TargetMode::Mine|TargetMode::Heal|TargetMode::Retreat|
              TargetMode::Patrol),_) =>
            return Err(invalid("select a matching visible target").into()),
        (Some(TargetMode::Fire),_) => {
            let point=camera.screen_to_world(x as f32,y as f32,FIELD,HEIGHT);
            ("fire",json!({"target":{"x":point.x,"y":point.y}}))
        }
        (None,_) => {
            let point=camera.screen_to_world(x as f32,y as f32,FIELD,HEIGHT);
            ("move",json!({"destination":{"x":point.x,"y":point.y}}))
        }
    };
    peer.order(selected,action.0,action.1)
}

pub fn connect_remote(address: &str, token_file: &Path, expected_side: i32)
    -> Result<(), Box<dyn Error>> {
    if !(1..=2).contains(&expected_side) {
        return Err(invalid("remote player side must be 1 or 2").into());
    }
    let link=fs::symlink_metadata(token_file)?;
    if link.file_type().is_symlink()||!link.is_file()||link.len()>512 {
        return Err(invalid("remote token must be a private regular file").into());
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        if link.permissions().mode() & 0o077 != 0 {
            return Err(invalid("remote player token must have private file permissions").into());
        }
    }
    let secret=fs::read_to_string(token_file)?;
    let token=secret.trim();
    if !(24..=256).contains(&token.len()) ||token.chars().any(char::is_control) {
        return Err(invalid("invalid native multiplayer token").into());
    }
    let mut peer=PeerClient::connect(address,token,expected_side)?;
    let mut window=Window::new("Bees | Native Multiplayer",WIDTH,HEIGHT,
        WindowOptions::default())?;
    window.set_target_fps(30);
    let mut pixels=vec![0;WIDTH*HEIGHT];
    let mut artwork=RemoteArtwork::new()?;
    let mut audio=NativeGameAudio::new();
    let mut soundtrack_paused=false;
    let mut last_audio_snapshot:Option<(u64,u64,u64)>=None;
    let mut camera:Option<NativeCamera>=None;
    let mut current_episode=(0_u64,0_u64);
    let mut selected:Vec<i64>=Vec::new();
    let mut mode:Option<TargetMode>=None;
    let mut patrol_start:Option<Vec2>=None;
    let mut last_left=false;
    let mut last_selection_press:Option<Instant>=None;
    let mut double_left_press=false;
    let mut last_right=false;
    let mut right_press:Option<RemoteRightPress>=None;
    let mut minimap_dragging=false;
    let mut drag_start:Option<(i32,i32)>=None;
    let mut last_middle:Option<(f32,f32)>=None;
    let mut dialogue_key:Option<(u64,u64,u64)>=None;
    let mut dialogue_line=0usize;
    let mut held_fire_active=false;
    let mut held_fire_target:Option<Vec2>=None;
    let mut show_art=true;
    let mut shooting_menu=false;
    let mut shooting_focus=1_u8;
    let mut hud_press:Option<HudAction>=None;
    let mut squad_tab_press:Option<i64>=None;
    let started=Instant::now();
    let mut previous_frame=started;

    while window.is_open() {
        let now=Instant::now();
        let elapsed=now.duration_since(previous_frame).as_secs_f32().min(0.1);
        previous_frame=now;
        if window.is_key_pressed(Key::F6, KeyRepeat::No) {
            show_art=!show_art;
            peer.notice=if show_art {
                "Native source sprites enabled".into()
            } else {
                "Fast geometric display enabled".into()
            };
        }
        if window.is_key_pressed(Key::F7, KeyRepeat::No) {
            peer.notice=if audio.toggle_muted() {
                "Native audio muted".into()
            } else { "Native audio enabled".into() };
        }
        if window.is_key_pressed(Key::Space, KeyRepeat::No) {
            soundtrack_paused=!soundtrack_paused;
            audio.set_paused(soundtrack_paused);
            peer.notice=if soundtrack_paused {
                "Soundtrack paused".into()
            } else { "Soundtrack playing".into() };
        }
        if window.is_key_pressed(Key::Escape, KeyRepeat::No) {
            if mode.is_some() {
                mode=None;patrol_start=None;drag_start=None;
            } else {break;}
        }
        // Reconnecting is an explicit new authenticated seat, never a
        // replay of commands or a restoration of unacknowledged gestures.
        // Dropping the previous socket makes the authoritative host release
        // the old peer's held-fire weapons at its next fixed-tick boundary.
        let mut rejoined=false;
        if window.is_key_pressed(Key::F5, KeyRepeat::No) {
            peer.metrics.reconnect_attempts+=1;
            match PeerClient::connect(address,token,expected_side) {
                Ok(mut replacement) => {
                    replacement.expected_host_session_id = peer.host_session_id.clone()
                        .or_else(|| peer.expected_host_session_id.clone());
                    replacement.metrics=std::mem::take(&mut peer.metrics);
                    replacement.metrics.reconnect_transports_opened+=1;
                    peer=replacement;
                    rejoined=true;
                },
                Err(error) => {
                    peer.notice=format!("Reconnect unavailable: {error}");
                },
            }
        }
        let was_connected=peer.connected;
        // Reset the old view BEFORE reading the new connection's first
        // welcome/snapshot. Otherwise a fast host can deliver a valid first
        // state that is immediately discarded as stale by this reset.
        if !rejoined { peer.poll()?; }
        if rejoined || was_connected && !peer.connected {
            // A new TCP connection has a new peer ID and no retained squad
            // authority. No previous snapshot, camera selection, or
            // manual-fire gesture can cross that authentication boundary.
            peer.state=None;
            peer.visual=RemoteVisual::default();
            selected.clear();
            mode=None;patrol_start=None;drag_start=None;
            last_left=false;last_right=false;
            last_selection_press=None;double_left_press=false;
            right_press=None;
            hud_press=None;squad_tab_press=None;
            shooting_menu=false;
            last_middle=None;minimap_dragging=false;
            camera=None;
            current_episode=(0,0);
            dialogue_key=None;dialogue_line=0;
            last_audio_snapshot=None;
            held_fire_active=false;held_fire_target=None;
            peer.held_fire_begin_request=None;
            peer.held_fire_rejected=false;
            // The remote scene renderer caches death/explosion frames keyed
            // by world identity. A new network session cannot inherit even
            // those cosmetic frames from the previous authoritative host.
            artwork=RemoteArtwork::new()?;
            audio.select_location(None);
            if !peer.connected &&
                peer.notice != "Host restarted: original battle cannot be resumed" {
                peer.notice="Host disconnected; press F5 to reconnect".into();
            }
        }
        if peer.held_fire_rejected {
            held_fire_active = false;
            held_fire_target = None;
        }
        if !window.is_key_down(Key::J) {
            peer.held_fire_rejected = false;
        }
        // A newly delivered campaign cue resets this presentation-only
        // dialogue cursor BEFORE K's meaning is determined.
        let mut dialogue_skip_key=false;
        if let Some(state)=peer.state.as_ref() {
            let cue=state["campaign_dialogue"]["sequence"].as_u64().map(|sequence|(
                state["world_id"].as_u64().unwrap_or(0),
                state["episode_revision"].as_u64().unwrap_or(0),sequence,
            ));
            if cue!=dialogue_key {dialogue_key=cue;dialogue_line=0;}
            let count=state["campaign_dialogue"]["line_count"]
                .as_u64().unwrap_or(0).min(100) as usize;
            if count>0 {
                // Source campaign dialogue uses K to skip and L to advance.
                // A skip must not activate a Beacon in the same frame.
                dialogue_skip_key=dialogue_line<count &&
                    window.is_key_pressed(Key::K,KeyRepeat::No);
                if dialogue_skip_key {
                    dialogue_line=count;
                } else if window.is_key_pressed(Key::L,KeyRepeat::No) ||
                    window.is_key_pressed(Key::Enter,KeyRepeat::No) {
                    dialogue_line=(dialogue_line+1).min(count);
                }
                if window.is_key_pressed(Key::Backspace,KeyRepeat::No) {
                    dialogue_line=dialogue_line.saturating_sub(1);
                }
            }
            let episode=(state["world_id"].as_u64().unwrap_or(0),
                         state["episode_revision"].as_u64().unwrap_or(0));
            if let Some(map)=bounds(state) {
                if camera.is_none()||episode !=current_episode {
                    camera=Some(NativeCamera::fit(map,FIELD,HEIGHT));
                    current_episode=episode;
                    selected.clear();
                    mode=None;
                    patrol_start=None;
                    minimap_dragging=false;
                    right_press=None;
                    last_selection_press=None;double_left_press=false;
                    hud_press=None;squad_tab_press=None;
                    shooting_menu=false;
                    // The server forgets this lease at the same authoritative
                    // episode boundary; the client never aims old ships.
                    held_fire_active=false;
                    held_fire_target=None;
                }
            }
            selected.retain(|id|state["squads"].as_array().into_iter().flatten()
                .any(|s|s["controllable"]==true &&
                    s["owner"]["squad_id"].as_i64()==Some(*id)));
            let key=(episode.0,episode.1,state["tick"].as_u64().unwrap_or(0));
            if last_audio_snapshot != Some(key) {
                if let Some(cam)=camera {
                    let location: Option<NativeMusicLocation> =
                        serde_json::from_value(state["music_location"].clone())
                            .ok().flatten();
                    audio.select_location(location);
                    // Sounds are bounded to the actual player viewport,
                    // even when more map geometry is authorized by fog.
                    let visible=Aabb {
                        min:cam.screen_to_world(0.0,HEIGHT as f32,FIELD,HEIGHT),
                        max:cam.screen_to_world(FIELD as f32,0.0,FIELD,HEIGHT),
                    };
                    audio.observe_remote(state,visible);
                }
                last_audio_snapshot=Some(key);
            }
        }
        peer.input_suppressed=peer.state.as_ref().is_some_and(|state| {
            !state["terminal_result"].is_null() ||
                (state["campaign_dialogue"]["line_count"].as_u64()
                    .is_some_and(|count|dialogue_line<count as usize) &&
                serde_json::from_value::<CampaignCue>(
                    state["campaign_dialogue"]["cue"].clone(),
                ).ok().is_some_and(CampaignCue::is_briefing))
        });
        if let Some(mut cam)=camera {
            let keys=window.get_keys_pressed(KeyRepeat::No);
            let shift=window.is_key_down(Key::LeftShift)||
                window.is_key_down(Key::RightShift);
            let control=window.is_key_down(Key::LeftCtrl)||
                window.is_key_down(Key::RightCtrl);
            for key in keys {
                let numbered=match key {
                    Key::Key1=>Some(1),Key::Key2=>Some(2),Key::Key3=>Some(3),
                    Key::Key4=>Some(4),Key::Key5=>Some(5),Key::Key6=>Some(6),
                    Key::Key7=>Some(7),Key::Key8=>Some(8),Key::Key9=>Some(9),
                    Key::Key0=>Some(0),_=>None,
                };
                if numbered.is_some()||key==Key::Tab||
                    (!shift&&matches!(key,Key::PageUp|Key::PageDown)) {
                    if let Some(state)=peer.state.as_ref() {
                        let next=numbered.and_then(|number|numbered_squad(state,number))
                            .or_else(||(key==Key::Tab||key==Key::PageDown||key==Key::PageUp)
                                .then(||cycle_numbered_squad(state,&selected,
                                    key!=Key::PageUp)).flatten());
                        if let Some(id)=next {
                            selected.clear();selected.push(id);
                            if let Some(point)=focus_squad(state,id){cam.center=point;}
                            mode=None;patrol_start=None;
                            peer.notice=format!("Selected source squad {id}");
                        } else {
                            peer.notice="No surviving authored numbered squad for that key".into();
                        }
                    }
                    continue;
                }
                // Keyboard and on-screen buttons share one typed command
                // dispatcher. Source local controls retain their meanings.
                if key==Key::F {
                    if let Some(map)=peer.state.as_ref().and_then(bounds) {
                        cam=NativeCamera::fit(map,FIELD,HEIGHT);
                    }
                    continue;
                }
                if shift&&matches!(key,Key::PageUp|Key::PageDown) {
                    let current=first_selected(peer.state.as_ref(),&selected)
                        .and_then(|s|s["shooting"].as_u64()).unwrap_or(1).clamp(1,40);
                    let next=if key==Key::PageUp{(current+1).min(40)}
                        else{current.saturating_sub(1).max(1)};
                    if let Err(error)=peer.order(&selected,"shooting",
                        json!({"strategy_id":next})) {
                        peer.notice=error.to_string();
                    }
                    continue;
                }
                let action=match key {
                    Key::A if control=>Some(HudAction::SelectAll),
                    Key::A=>Some(HudAction::AttackOnSight),
                    Key::V=>Some(HudAction::CeaseFire),
                    Key::T if shift=>Some(HudAction::Attack),
                    Key::T=>Some(HudAction::PointFire),
                    Key::I if shift=>Some(HudAction::Circle),
                    Key::O if shift=>Some(HudAction::LeftSwipe),
                    Key::U if shift=>Some(HudAction::RightSwipe),
                    Key::N if shift=>Some(HudAction::InAndOut),
                    Key::G=>Some(HudAction::Guard),
                    Key::M=>Some(HudAction::Mine),
                    Key::H=>Some(HudAction::Heal),
                    Key::R=>Some(HudAction::Retreat),
                    Key::P=>Some(HudAction::Patrol),
                    Key::C=>Some(HudAction::Hold),
                    Key::E=>Some(HudAction::Chase),
                    Key::O=>Some(HudAction::LockOn),
                    Key::Y=>Some(HudAction::MatchSpeed),
                    Key::U=>Some(HudAction::FreeSpeed),
                    Key::K if dialogue_skip_key=>None,
                    Key::K=>Some(HudAction::Beacon),
                    Key::D=>Some(HudAction::Detonate),
                    Key::Q=>Some(HudAction::Charge),
                    Key::X=>Some(HudAction::StopFire),
                    Key::S=>Some(HudAction::Shooting),
                    _=>None,
                };
                if let Some(action)=action {
                    if let Err(error)=remote_tactical_action(
                        &mut peer,&mut selected,&mut mode,&mut patrol_start,
                        &mut held_fire_active,&mut held_fire_target,
                        &mut shooting_menu,&mut shooting_focus,action,
                    ) {
                        peer.notice=error.to_string();
                    }
                }
            }
            // Same source-native camera speed in physical pixels/second,
            // independent of the remote window's rendered frame rate.
            let pan=500.0*elapsed/cam.pixels_per_unit;
            if window.is_key_down(Key::Up){cam.center.y+=pan;}
            if window.is_key_down(Key::Down){cam.center.y-=pan;}
            if window.is_key_down(Key::Left){cam.center.x-=pan;}
            if window.is_key_down(Key::Right){cam.center.x+=pan;}
            let mouse=window.get_mouse_pos(MouseMode::Discard);
            let minimap = peer.state.as_ref().and_then(bounds)
                .and_then(|map| MiniMapLayout::top_right(FIELD, HEIGHT, map));
            // Shift+wheel pans horizontally and Ctrl+wheel vertically,
            // matching the local native camera. Unmodified wheel zooms
            // at the pointer or centers on the real host-derived minimap.
            if let Some((horizontal,vertical))=window.get_scroll_wheel() {
                let wheel_pixels=120.0/cam.pixels_per_unit;
                let mut wheel_pan=Vec2::new(
                    horizontal.clamp(-10.0,10.0)*wheel_pixels,0.0);
                if shift {
                    wheel_pan.x-=vertical.clamp(-10.0,10.0)*wheel_pixels;
                } else if control {
                    wheel_pan.y+=vertical.clamp(-10.0,10.0)*wheel_pixels;
                } else if vertical!=0.0 {
                    if let Some((x,y))=mouse {
                        let factor=1.15_f32.powf(vertical.clamp(-10.0,10.0));
                        if let Some(point)=minimap.and_then(|map|map.world_at(x,y)) {
                            cam.center=point;
                            cam.zoom_at(factor,FIELD as f32/2.0,
                                HEIGHT as f32/2.0,FIELD,HEIGHT,None);
                        } else if x>=0.0&&x<FIELD as f32&&y>=0.0&&y<HEIGHT as f32 {
                            cam.zoom_at(factor,x,y,FIELD,HEIGHT,None);
                        }
                    }
                }
                cam.center+=wheel_pan;
            }
            if window.is_key_down(Key::Equal){cam.zoom_at(1.035,FIELD as f32/2.0,
                HEIGHT as f32/2.0,FIELD,HEIGHT,None);}
            if window.is_key_down(Key::Minus){cam.zoom_at(1.0/1.035,
                FIELD as f32/2.0,HEIGHT as f32/2.0,FIELD,HEIGHT,None);}
            if let Some(map)=peer.state.as_ref().and_then(bounds) {
                cam.constrain_to_map(map,FIELD,HEIGHT);
            }
            let within=mouse.and_then(|(x,y)| {
                (x>=0.0 && x<FIELD as f32 && y>=0.0 && y<HEIGHT as f32 &&
                    !minimap.is_some_and(|layout| layout.world_at(x, y).is_some()))
                    .then_some((x as i32,y as i32))
            });
            // Begin is an edge-triggered gesture, just as on the local
            // native commander. Keeping J held after X cancels fire must
            // not immediately begin another weapon lease in this frame.
            // An already active gesture still sends Aim and End as usual.
            let holding=window.is_active() && window.is_key_down(Key::J)
                && !peer.held_fire_rejected && mode.is_none() && within.is_some() &&
                (held_fire_active || (!selected.is_empty() &&
                    window.is_key_pressed(Key::J,KeyRepeat::No)));
            match (held_fire_active,holding,within) {
                (false,true,Some((x,y))) => {
                    let point=cam.screen_to_world(x as f32,y as f32,FIELD,HEIGHT);
                    match peer.held_fire(&selected,"Begin",Some(point)) {
                        Ok(()) => {
                            held_fire_active=true;held_fire_target=Some(point);
                        },
                        Err(error) => peer.notice=error.to_string(),
                    }
                },
                (true,true,Some((x,y))) => {
                    let point=cam.screen_to_world(x as f32,y as f32,FIELD,HEIGHT);
                    // Camera movement also changes the world aim when the
                    // mouse has not moved. Skip truly unchanged targets.
                    if !held_fire_target.is_some_and(|prior| prior.unity_equal(point)) {
                        match peer.held_fire(&selected,"Aim",Some(point)) {
                            Ok(()) => held_fire_target=Some(point),
                            Err(error) => peer.notice=error.to_string(),
                        }
                    }
                },
                (true,false,_) => {
                    if let Err(error)=peer.held_fire(&selected,"End",None) {
                        // If a bounded outgoing queue cannot transmit End,
                        // close the socket to trigger the host's authoritative
                        // disconnect release. Never leave a stale fire lock.
                        let _=peer.socket.shutdown(std::net::Shutdown::Both);
                        peer.connected=false;
                        peer.notice=error.to_string();
                    }
                    held_fire_active=false;held_fire_target=None;
                },
                _ => {},
            }
            let left=window.get_mouse_down(MouseButton::Left);
            let right=window.get_mouse_down(MouseButton::Right);
            let middle=window.get_mouse_down(MouseButton::Middle);
            let minimap_point = mouse.and_then(|(x, y)|
                minimap.and_then(|layout| layout.world_at(x, y)));
            if left && !last_left {
                minimap_dragging = minimap_point.is_some();
            }
            if left && minimap_dragging {
                if let Some(point) = minimap_point {
                    if !cam.center.unity_equal(point) {
                        cam.center = point;
                        peer.metrics.minimap_camera_moves += 1;
                    }
                    if let Some(map) = peer.state.as_ref().and_then(bounds) {
                        cam.constrain_to_map(map, FIELD, HEIGHT);
                    }
                }
            }
            let minimap_release = !left && last_left && minimap_dragging;
            if !left { minimap_dragging = false; }
            if minimap_release { drag_start = None; }
            if middle {
                if let (Some((x,y)),Some((old_x,old_y)))=(mouse,last_middle) {
                    cam.center.x-=(x-old_x)/cam.pixels_per_unit;
                    cam.center.y+=(y-old_y)/cam.pixels_per_unit;
                }
                last_middle=mouse;
            }else{last_middle=None;}
            if let Some((x,y))=mouse {
                let (x,y)=(x as i32,y as i32);
                if left&&!last_left&&!minimap_dragging&&
                    x>=0&&x<FIELD as i32&&y>=0&&y<HEIGHT as i32 {
                    // Source double-click is a second completed selection
                    // press within 250 ms. The release still must be prompt;
                    // a long drag cannot turn into a ship-type selection.
                    double_left_press=last_selection_press.is_some_and(|previous|
                        now.duration_since(previous)<=Duration::from_millis(250));
                    last_selection_press=Some(now);
                    drag_start=Some((x,y));
                }
                if !left&&last_left&&!minimap_release {
                    if mode.is_some() {
                        drag_start=None;
                        if x>=0 && x<FIELD as i32 && y>=0 && y<HEIGHT as i32 {
                            if mode == Some(TargetMode::Patrol) {
                                let point=cam.screen_to_world(x as f32,y as f32,FIELD,HEIGHT);
                                if let Some(first)=patrol_start {
                                    let fields=json!({"action":{"Patrol":{
                                        "top_left":{"x":first.x.min(point.x),
                                                    "y":first.y.max(point.y)},
                                        "bottom_right":{"x":first.x.max(point.x),
                                                        "y":first.y.min(point.y)}
                                    }}});
                                    if let Err(error)=peer.order(&selected,"support",fields) {
                                        peer.notice=error.to_string();
                                    } else {
                                        mode=None;
                                        patrol_start=None;
                                    }
                                } else {
                                    patrol_start=Some(point);
                                    peer.notice="Choose the second patrol corner".into();
                                }
                            } else if let Err(error)=order_at(&mut peer,&selected,cam,x,y,mode) {
                                peer.notice=error.to_string();
                            } else {mode=None;}
                        }
                    } else if let Some((sx,sy))=drag_start.take() {
                        if let Some(state)=peer.state.as_ref() {
                            let additive=window.is_key_down(Key::LeftShift)||
                                window.is_key_down(Key::RightShift)||
                                window.is_key_down(Key::LeftCtrl)||
                                window.is_key_down(Key::RightCtrl);
                            let mut additions=Vec::new();
                            let dragging=(x-sx).abs()+(y-sy).abs()>8;
                            if dragging {
                                last_selection_press=None;
                                double_left_press=false;
                                for ship in state["ships"].as_array().into_iter().flatten(){
                                    if ship["owned"]!=true {continue;}
                                    let Some(p)=peer.visual.position(
                                        ship, 0, peer.visual.fraction(),
                                    ) else {continue;};
                                    let (px,py)=screen(cam,p);
                                    if px>=sx.min(x)&&px<=sx.max(x)&&
                                        py>=sy.min(y)&&py<=sy.max(y) {
                                        if let Some(squad)=squad_for_ship(state,
                                            ship["id"].as_i64().unwrap_or(-1),
                                            ship["side"].as_i64().unwrap_or(0))
                                            .filter(|squad| squad["controllable"]==true) {
                                            if let Some(id)=squad["owner"]["squad_id"].as_i64(){
                                                if !additions.contains(&id){additions.push(id);}
                                            }
                                        }
                                    }
                                }
                            } else if let Some(("ship",ship))=hit(
                                state,cam,x,y,&peer.visual,peer.visual.fraction(),
                            ) {
                                if ship["owned"]==true {
                                    if double_left_press && !additive &&
                                        last_selection_press.is_some_and(|pressed|
                                            now.duration_since(pressed)<=Duration::from_millis(250))
                                    {
                                        if let Some(ship_type)=ship["ship_type"].as_u64() {
                                            additions=squads_with_ship_type(state,ship_type);
                                            peer.metrics.ship_type_multiselections+=1;
                                        }
                                    } else if let Some(squad)=squad_for_ship(state,
                                        ship["id"].as_i64().unwrap_or(-1),
                                        ship["side"].as_i64().unwrap_or(0))
                                        .filter(|squad| squad["controllable"]==true) {
                                        if let Some(id)=squad["owner"]["squad_id"].as_i64() {
                                            additions.push(id);
                                        }
                                    }
                                }
                            }
                            double_left_press=false;
                            if !additive {selected.clear();}
                            for id in additions {
                                if !selected.contains(&id){selected.push(id);}
                            }
                        }
                    }
                }
            }
            // Capture the origin of a right-button gesture. Its minimap
            // and World coordinates must never swap halfway through a drag.
            // A right-click while aiming a support order cancels that mode,
            // as in the source local client, without issuing a new command.
            if right&&!last_right {
                if mode.is_some() {
                    mode=None;patrol_start=None;right_press=None;
                    peer.notice="Target selection cancelled".into();
                } else if !selected.is_empty() {
                    if let Some((x,y))=mouse {
                        let on_minimap=minimap_point.is_some();
                        if on_minimap||(x>=0.0&&x<FIELD as f32&&
                            y>=0.0&&y<HEIGHT as f32) {
                            right_press=Some(RemoteRightPress {
                                next_move_at:now+RIGHT_DRAG_INTERVAL,
                                last_move_pixel:None,minimap:on_minimap,
                            });
                        }
                    }
                }
            }
            if right {
                if let (Some(press),Some((x,y)))=(right_press.as_mut(),mouse) {
                    let pixel=Vec2::new(x,y);
                    if now>=press.next_move_at &&
                        !press.last_move_pixel.is_some_and(|last|
                            (pixel-last).length_squared()<=25.0)
                    {
                        let destination=if press.minimap {minimap_point}
                            else if minimap_point.is_none()&&x>=0.0&&
                                x<FIELD as f32&&y>=0.0&&y<HEIGHT as f32
                            {Some(cam.screen_to_world(x,y,FIELD,HEIGHT))}
                            else {None};
                        if let Some(point)=destination {
                            press.next_move_at=now+RIGHT_DRAG_INTERVAL;
                            press.last_move_pixel=Some(pixel);
                            match peer.order(&selected,"move",json!({
                                "destination":{"x":point.x,"y":point.y}
                            })) {
                                Ok(())=>peer.metrics.right_drag_move_orders_submitted+=1,
                                Err(error)=>peer.notice=error.to_string(),
                            }
                        }
                    }
                }
            } else if last_right {
                if let (Some(press),Some((x,y)))=(right_press.take(),mouse) {
                    if press.minimap {
                        if let Some(point)=minimap_point {
                            match peer.order(&selected,"move",json!({
                                "destination":{"x":point.x,"y":point.y}
                            })) {
                                Ok(())=>peer.metrics.minimap_move_orders_submitted+=1,
                                Err(error)=>peer.notice=error.to_string(),
                            }
                        }
                    } else if minimap_point.is_none()&&x>=0.0&&
                        x<FIELD as f32&&y>=0.0&&y<HEIGHT as f32
                    {
                        if let Err(error)=order_at(&mut peer,&selected,cam,
                            x as i32,y as i32,None) {
                            peer.notice=error.to_string();
                        }
                    }
                }
            }
            last_left=left;last_right=right;
            camera=Some(cam);
        }
        let visual_fraction = peer.visual.fraction();
        if visual_fraction < 1.0 && !peer.visual.previous.is_empty() {
            peer.metrics.interpolated_draw_frames += 1;
        }
        if let (Some(state),Some(cam))=(peer.state.as_ref(),camera) {
            artwork.paint(state,cam,&peer.visual,visual_fraction,
                &mut peer.metrics,show_art);
        }
        let mut hulls = 0_u64;
        let mut map_ships = 0_u64;
        let mut map_objectives = 0_u64;
        let mut fog_masked = 0_u64;
        draw(&mut pixels,peer.state.as_ref(),camera,&selected,&peer.notice,
            expected_side,mode,patrol_start,&peer.metrics,dialogue_line,
            held_fire_active,shooting_menu,shooting_focus,
            &peer.visual,visual_fraction,&artwork,show_art,
            &mut hulls, &mut map_ships, &mut map_objectives, &mut fog_masked);
        peer.metrics.fog_masked_pixels += fog_masked;
        peer.metrics.obstacle_hull_draws += hulls;
        peer.metrics.minimap_ship_markers += map_ships;
        peer.metrics.minimap_objective_markers += map_objectives;
        // A terminal outcome is reliable and survives transport closure,
        // even if the replaceable final world_state could not drain.
        if let Some(result) = peer.battle_result_receipt.as_deref() {
            text::draw_at(&mut pixels, WIDTH, 10, HEIGHT-61,
                text::fit(result, FIELD-20), 1, [255, 211, 130]);
        }
        // The authoritative commit receipt outlives the last world frame and
        // socket close. A transport status line cannot hide saved progression.
        if let Some(receipt) = peer.campaign_receipt.as_deref() {
            text::draw_at(&mut pixels, WIDTH, 10, HEIGHT-43,
                text::fit(receipt, FIELD-20), 1, [255, 211, 130]);
        }
        window.update_with_buffer(&pixels,WIDTH,HEIGHT)?;
        peer.metrics.frames_rendered += 1;
    }
    let session_seconds = started.elapsed().as_secs_f64();
    println!("{}",json!({
        "kind":"native_multiplayer_client_final",
        "side":expected_side,
        "session_wall_seconds":session_seconds,
        "bytes_received":peer.metrics.bytes_received,
        "snapshots_rendered":peer.metrics.snapshots,
        "frames_rendered":peer.metrics.frames_rendered,
        "rendered_fps":if session_seconds > 0.0 {
            peer.metrics.frames_rendered as f64 / session_seconds
        } else { 0.0 },
        "snapshot_pairs_interpolated":peer.metrics.snapshot_pairs_interpolated,
        "interpolated_draw_frames":peer.metrics.interpolated_draw_frames,
        "ship_sprite_draws":peer.metrics.ship_sprite_draws,
        "ship_deaths_received":peer.metrics.ship_deaths_received,
        "combat_impacts_received":peer.metrics.combat_impacts_received,
        "combat_impact_visual_draws":peer.metrics.combat_impact_visual_draws,
        "combat_impact_visuals_dropped":peer.metrics.combat_impact_visuals_dropped,
        "ship_remains_draws":peer.metrics.ship_remains_draws,
        "ship_remains_source_frame_draws":peer.metrics.ship_remains_source_frame_draws,
        "ship_explosion_visual_draws":peer.metrics.ship_explosion_visual_draws,
        "projectile_sprite_draws":peer.metrics.projectile_sprite_draws,
        "asteroid_sprite_draws":peer.metrics.asteroid_sprite_draws,
        "sprite_candidate_pixels":peer.metrics.sprite_candidate_pixels,
        "sprite_opaque_pixels":peer.metrics.sprite_opaque_pixels,
        "sprite_blended_pixels":peer.metrics.sprite_blended_pixels,
        "recolor_cache_peak":peer.metrics.recolor_cache_peak,
        "obstacle_hull_draws":peer.metrics.obstacle_hull_draws,
        "minimap_ship_markers":peer.metrics.minimap_ship_markers,
        "minimap_objective_markers":peer.metrics.minimap_objective_markers,
        "minimap_camera_moves":peer.metrics.minimap_camera_moves,
        "ship_type_multiselections":peer.metrics.ship_type_multiselections,
        "minimap_move_orders_submitted":peer.metrics.minimap_move_orders_submitted,
        "right_drag_move_orders_submitted":peer.metrics.right_drag_move_orders_submitted,
        "fog_circle_stamps":peer.metrics.fog_circle_stamps,
        "fog_masked_pixels":peer.metrics.fog_masked_pixels,
        "source_art_enabled":show_art,
        "audio":audio.measurements(),
        "orders_sent":peer.metrics.orders_sent,
        "order_acknowledgments":peer.metrics.acknowledgments,
        "campaign_receipts":peer.metrics.campaign_receipts,
        "battle_result_receipts":peer.metrics.battle_result_receipts,
        "interrupted_battle_notices":peer.metrics.interrupted_battle_notices,
        "reconnect_attempts":peer.metrics.reconnect_attempts,
        "reconnect_transports_opened":peer.metrics.reconnect_transports_opened,
        "connection_closed":!peer.connected,
    }));
    Ok(())
}
