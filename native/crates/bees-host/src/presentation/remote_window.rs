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
use std::sync::OnceLock;

use bees_gameplay::campaign_dialogue::CampaignCue;
use bees_sim::collision::{Aabb, Pose, circle_overlaps_polygon, circles_overlap, polygons_overlap};
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

fn source_ship_catalog() -> &'static bees_sim::ship_assets::ShipAssetCatalog {
    static SHIPS: OnceLock<bees_sim::ship_assets::ShipAssetCatalog> = OnceLock::new();
    SHIPS.get_or_init(|| bees_sim::ship_assets::ShipAssetCatalog::embedded()
        .expect("native ship catalog validated by game startup"))
}

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
const MAX_PENDING_ORDER_TIMINGS: usize = 4096;

#[derive(Default)]
struct WireCounts {
    bytes_received: u64,
    snapshots: u64,
    frames_rendered: u64,
    orders_sent: u64,
    acknowledgments: u64,
    briefing_requests_sent: u64,
    briefing_receipts: u64,
    briefing_rejections: u64,
    orders_accepted: u64,
    orders_rejected: u64,
    /// Client wall time from submission through authoritative host receipt,
    /// not an estimate from snapshot cadence or local frame rendering.
    order_ack_latency_samples: u64,
    order_ack_latency_sum_seconds: f64,
    order_ack_latency_max_seconds: f64,
    /// INPUT receipts without a retained start timestamp, including FIFO evictions.
    order_latency_samples_lost: u64,
    pending_order_timings_peak: usize,
    /// Client-only render costs, separately measured from network polling.
    artwork_seconds: f64,
    artwork_max_seconds: f64,
    hud_seconds: f64,
    hud_max_seconds: f64,
    present_seconds: f64,
    present_max_seconds: f64,
    /// Complete nonblocking socket polling and authoritative message processing.
    network_poll_seconds: f64,
    network_poll_max_seconds: f64,
    network_poll_samples: u64,
    /// Host snapshots accepted (bytes exclude the framing newline).
    world_snapshot_bytes: u64,
    world_snapshot_max_bytes: usize,
    campaign_receipts: u64,
    battle_result_receipts: u64,
    interrupted_battle_notices: u64,
    reconnect_attempts: u64,
    reconnect_transports_opened: u64,
    /// Render-only smoothing; no simulation or network side-effects.
    snapshot_pairs_interpolated: u64,
    interpolated_draw_frames: u64,
    ship_sprite_draws: u64,
    carrier_deck_overlay_draws: u64,
    barge_charge_effect_draws: u64,
    queen_part_sprite_draws: u64,
    projectile_sprite_draws: u64,
    asteroid_sprite_draws: u64,
    asteroid_piece_sprite_draws: u64,
    asteroid_piece_fallback_draws: u64,
    asteroid_pieces_viewport_culled: u64,
    fire_tank_sprite_draws: u64,
    obstacle_debris_events_received: u64,
    obstacle_debris_sprite_draws: u64,
    obstacle_debris_culled: u64,
    map_object_health_bars: u64,
    sprite_candidate_pixels: u64,
    sprite_opaque_pixels: u64,
    sprite_blended_pixels: u64,
    recolor_cache_peak: usize,
    ship_deaths_received: u64,
    combat_impacts_received: u64,
    combat_impact_visual_draws: u64,
    healing_crosses_received: u64,
    healing_crosses_drawn: u64,
    healing_crosses_dropped: u64,
    combat_impact_visuals_dropped: u64,
    ship_remains_draws: u64,
    ship_remains_source_frame_draws: u64,
    ship_explosion_visual_draws: u64,
    queen_explosion_layer_draws: u64,
    beehive_shrinking_frame_draws: u64,
    beehive_final_blast_draws: u64,
    obstacle_hull_draws: u64,
    minimap_ship_markers: u64,
    minimap_terrain_markers: u64,
    minimap_objective_markers: u64,
    minimap_camera_moves: u64,
    /// Frames in which the actual window edge moved the presentation camera.
    camera_edge_pan_frames: u64,
    ship_type_multiselections: u64,
    selection_drag_preview_frames: u64,
    weapon_range_draws: u64,
    weapon_charge_draws: u64,
    weapon_aim_draws: u64,
    weapon_contact_draws: u64,
    weapon_obstructed_contact_draws: u64,
    command_target_draws: u64,
    striker_bomb_indicator_draws: u64,
    tactical_button_clicks: u64,
    squad_tab_clicks: u64,
    shooting_strategy_choices: u64,
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
    fire_tanks: HashSet<i64>,
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
            asteroids: HashSet::new(), fire_tanks: HashSet::new(),
            shot_first_seen: HashMap::new(),
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
        self.fire_tanks.clear();
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
            let (Some(id), Some(point)) = (
                obstacle["id"].as_i64(), visual.position(obstacle, 2, fraction),
            ) else { continue };
            let angle = visual.heading_for(obstacle, 2, fraction);
            if obstacle["map_object_kind"].as_u64() ==
                Some(bees_sim::map_object::MapObjectKind::FireTank as u64)
            {
                // New hosts disclose only the source sprite's four-stage
                // smoke state until weapon-range discovery. Old recordings
                // can still provide raw HP for the identical stage formula.
                let stage = obstacle["fire_tank_stage"].as_u64()
                    .and_then(|stage| u8::try_from(stage).ok())
                    .or_else(|| super::fire_tank_sprite_stage(
                        i32::try_from(obstacle["health"].as_i64()?).ok()?,
                        i32::try_from(obstacle["max_health"].as_i64()?).ok()?,
                    ));
                let Some(stage) = stage else { continue };
                if self.raster.draw_fire_tank_stage(id, stage,
                    Pose { position: point, rotation_degrees: angle }, tick as f32 * dt) {
                    self.fire_tanks.insert(id);
                    stats.fire_tank_sprite_draws += 1;
                }
            } else if let Some(prefab) = obstacle["asteroid_prefab"].as_u64()
                .and_then(|id| u8::try_from(id).ok()) {
                if self.raster.draw_asteroid_art(prefab, point, angle, 1.0) {
                    self.asteroids.insert(id);
                    stats.asteroid_sprite_draws += 1;
                }
            }
        }
        for piece in state["asteroid_pieces"].as_array().into_iter().flatten() {
            let (Some(prefab), Some(point), Some(opacity)) = (
                piece["prefab"].as_u64().and_then(|v| u8::try_from(v).ok()),
                visual.position(piece, 3, fraction),
                piece["opacity"].as_f64().map(|v| v as f32)
                    .filter(|v| v.is_finite() && *v > 0.0),
            ) else { continue };
            let (x,y)=screen(camera,point);
            if x<0 || x>=FIELD as i32 || y<0 || y>=HEIGHT as i32 {
                stats.asteroid_pieces_viewport_culled += 1;
                continue;
            }
            if self.raster.draw_asteroid_piece(prefab, Pose {
                position: point, rotation_degrees: visual.heading_for(piece, 3, fraction),
            }, opacity) {
                stats.asteroid_piece_sprite_draws += 1;
            } else {
                stats.asteroid_piece_fallback_draws += 1;
            }
        }
        if self.raster.begin_obstacle_debris_frame(WorldId(episode.0), episode.1, tick) {
            for cue in state["audio_events"].as_array().into_iter().flatten() {
                let debris = &cue["obstacle_debris"];
                let (Some(id), Some(origin), Some(bounds), Some(born)) = (
                    debris["id"].as_i64(), point_from_wire(&debris["origin"]),
                    debris["bounds"].as_array().filter(|v| v.len()==2),
                    cue["tick"].as_u64(),
                ) else { continue };
                let (Some(min), Some(max)) =
                    (point_from_wire(&bounds[0]), point_from_wire(&bounds[1]))
                    else { continue };
                if born<=tick && tick.saturating_sub(born) as f32 * dt < 1.8 {
                    self.raster.record_obstacle_debris(born, id, origin, Aabb { min, max });
                    stats.obstacle_debris_events_received += 1;
                }
            }
        }
        let (pieces, culled) = self.raster.draw_obstacle_debris(tick, dt);
        stats.obstacle_debris_sprite_draws += pieces.saturating_sub(culled) as u64;
        stats.obstacle_debris_culled += culled as u64;
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
            let barge_charge = ship["barge_charge"].as_array()
                .filter(|values| values.len() == 2)
                .and_then(|values| Some((
                    u8::try_from(values[0].as_u64()?).ok()?,
                    values[1].as_f64()? as f32,
                )));
            if let Some((deck, barge, queen)) = self.raster.remote_ship_art(
                kind, point, visual.heading(ship, fraction), color_rgb,
                animation, barge_charge,
            ) {
                self.ships.insert(id);
                stats.ship_sprite_draws += 1;
                stats.carrier_deck_overlay_draws += u64::from(deck);
                stats.barge_charge_effect_draws += barge as u64;
                stats.queen_part_sprite_draws += queen as u64;
            }
        }
        self.raster.begin_death_scene(WorldId(episode.0), episode.1);
        for cue in state["audio_events"].as_array().into_iter().flatten() {
            if let (Some(beehive_id), Some(source_ship_id), Some(x), Some(y), Some(born_tick)) = (
                cue["healing_cross"]["beehive_id"].as_i64(),
                cue["healing_cross"]["source_ship_id"].as_i64(),
                cue["x"].as_f64().map(|value| value as f32),
                cue["y"].as_f64().map(|value| value as f32),
                cue["tick"].as_u64(),
            ) {
                let (admitted, dropped) = self.raster.record_healing_cross(
                    born_tick, beehive_id, source_ship_id, Vec2::new(x, y),
                );
                stats.healing_crosses_received += u64::from(admitted);
                stats.healing_crosses_dropped += u64::from(dropped);
            }
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
            if kind == bees_sim::combat_world::BEEHIVE_TYPE {
                self.raster.retire_beehive_healing_crosses(id);
            }
            let (admitted, _) = self.raster.record_ship_death(
                id, born_tick, kind, side, Vec2::new(x, y),
                rotation, radius, color,
            );
            stats.ship_deaths_received += u64::from(admitted);
        }
        let (remains, flashes, source_frames, queen_layers,
            beehive_shrinking, beehive_blasts) =
            self.raster.draw_ship_death_visuals(tick, dt);
        stats.ship_remains_draws += remains as u64;
        stats.ship_remains_source_frame_draws += source_frames as u64;
        stats.ship_explosion_visual_draws += flashes as u64;
        stats.queen_explosion_layer_draws += queen_layers as u64;
        stats.beehive_shrinking_frame_draws += beehive_shrinking as u64;
        stats.beehive_final_blast_draws += beehive_blasts as u64;
        stats.combat_impact_visual_draws +=
            self.raster.draw_combat_impact_visuals(tick, dt) as u64;
        stats.healing_crosses_drawn +=
            self.raster.draw_healing_cross_visuals(tick, dt) as u64;
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
        for (kind, field) in [(0, "ships"), (1, "projectiles"), (2, "obstacles"),
            (3, "asteroid_pieces")] {
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
    last_response_request: u64,
    /// Only pending INPUT order IDs are timed. A FIFO permits amortized
    /// constant-time reconciliation with host's ordered response sequence.
    pending_order_times: VecDeque<(u64, Instant)>,
    /// Confirmed by the same authenticated input-result request stream.
    /// Never restored when the TCP connection or host identity changes.
    briefing_pending: Option<(u64,u64,u32,u64)>,
    briefing_approved: Option<(u64,u64,u32)>,
    /// A denied cue is not resent every display frame. A new World or
    /// source cue can still request its own acknowledgment.
    briefing_rejected: Option<(u64,u64,u32)>,
    authenticated: bool,
    connected: bool,
    /// The host proves this authority class in its signed-in welcome,
    /// while separate observer credentials are never promoted to command
    /// seats by locally selecting ships or changing a UI setting.
    spectator: bool,
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
    // Immutable per-FleetShip awards and retirements from the side-owned,
    // durably committed campaign receipt; never a client-side career ledger.
    campaign_reward_details: Vec<String>,
    battle_result_receipt: Option<String>,
    battle_loss_breakdown: Vec<String>,
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
    fn connect(address: &str, token: &str, side: i32,
        resume_session_id: Option<&str>, spectator: bool,
    ) -> Result<Self, Box<dyn Error>> {
        let mut connection = None;
        for candidate in address.to_socket_addrs()? {
            match TcpStream::connect_timeout(&candidate, Duration::from_secs(5)) {
                Ok(socket) => { connection = Some(socket); break; }
                Err(_) => continue,
            }
        }
        let mut socket = connection.ok_or_else(|| invalid("native multiplayer server unreachable"))?;
        socket.set_nodelay(true)?;
        let mut hello = serde_json::to_vec(&json!({
            "kind": "hello", "token": token,
            "resume_session_id": resume_session_id, "spectator": spectator,
        }))?;
        hello.push(b'\n');
        socket.write_all(&hello)?;
        socket.set_nonblocking(true)?;
        Ok(Self {
            socket, incoming: Vec::new(), queued: VecDeque::new(),
            written: 0, queued_bytes: 0, next_request: 1,
            last_response_request: 0, pending_order_times: VecDeque::new(),
            briefing_pending: None, briefing_approved: None,
            briefing_rejected: None,
            authenticated: false, connected: true,
            input_suppressed: spectator, spectator, side,
            host_session_id: None,
            expected_host_session_id: resume_session_id.map(str::to_owned),
            state: None, visual: RemoteVisual::default(),
            notice: "Authenticating with native host".to_owned(),
            held_fire_begin_request: None, held_fire_rejected: false,
            campaign_receipt: None, campaign_reward_details: Vec::new(),
            battle_result_receipt: None,
            battle_loss_breakdown: Vec::new(),
            metrics: WireCounts::default(),
        })
    }

    fn poll(&mut self) -> Result<(), Box<dyn Error>> {
        let mut buffer = [0_u8; 32768];
        let mut read = 0_usize;
        while read < READ_BUDGET && self.connected {
            let limit = buffer.len().min(READ_BUDGET - read);
            match self.socket.read(&mut buffer[..limit]) {
                Ok(0) => {
                    self.connected = false;
                    self.notice = self.battle_result_receipt.clone()
                        .unwrap_or_else(|| "Host disconnected".into());
                    break;
                }
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
                    self.notice = self.battle_result_receipt.clone()
                        .unwrap_or_else(|| "Host disconnected".into());
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
            let message_bytes=index-start;
            start = index + 1;
            match message["kind"].as_str() {
                Some("welcome") => {
                    if self.authenticated {
                        return Err(invalid("native multiplayer socket reauthenticated after its session was established").into());
                    }
                    if message["protocol"].as_u64() != Some(1) ||
                        message["side"].as_i64() != Some(i64::from(self.side)) ||
                        message["spectator"].as_bool() != Some(self.spectator) {
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
                    self.notice = if self.spectator {
                        format!("Watching side {} (read only, source fog)", self.side)
                    } else if message["recovered_host"] == true {
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
                    // World and episode identity are mandatory on the
                    // authoritative wire. Missing values must not silently
                    // reset UI state or release an active result gate.
                    let world_id = message["world_id"].as_u64()
                        .ok_or_else(|| invalid("native host snapshot has no world ID"))?;
                    let revision = message["episode_revision"].as_u64()
                        .ok_or_else(|| invalid("native host snapshot has no episode revision"))?;
                    let tick = message["tick"].as_u64()
                        .ok_or_else(|| invalid("native host snapshot has no tick"))?;
                    if !message["fixed_step_seconds"].as_f64()
                        .is_some_and(|dt| dt.is_finite() && dt > 0.0) {
                        return Err(invalid("native host snapshot has an invalid fixed step").into());
                    }
                    if let Some(previous) = &self.state {
                        if previous["world_id"].as_u64() == Some(world_id) {
                            let old_revision = previous["episode_revision"].as_u64()
                                .expect("accepted snapshots have an episode revision");
                            if revision < old_revision ||
                                (revision == old_revision &&
                                    tick < previous["tick"].as_u64()
                                        .expect("accepted snapshots have a tick")) {
                                return Err(invalid("out-of-order authoritative world snapshot").into());
                            }
                        }
                    }
                    // A completed battle's reliable receipt must not keep
                    // suppressing orders when the host publishes a new
                    // authoritative world or advances its episode revision.
                    // Do this at the same accepted snapshot boundary used
                    // by interpolation and squad-selection ownership.
                    let new_episode = self.state.as_ref().is_some_and(|previous|
                        previous["world_id"] != message["world_id"] ||
                        previous["episode_revision"] != message["episode_revision"]);
                    if new_episode {
                        self.battle_result_receipt = None;
                        self.battle_loss_breakdown.clear();
                        self.campaign_receipt = None;
                        self.campaign_reward_details.clear();
                        self.notice = "New battle started".into();
                    }
                    if self.visual.snapshot(self.state.as_ref(), &message) {
                        self.metrics.snapshot_pairs_interpolated += 1;
                    }
                    self.metrics.world_snapshot_bytes+=message_bytes as u64;
                    self.metrics.world_snapshot_max_bytes=self.metrics.world_snapshot_max_bytes.max(message_bytes);
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
                    let evacuated = outcome["evacuated_personnel"].as_u64()
                        .map(|count| format!("; {count}/60 evacuated"))
                        .unwrap_or_default();
                    let score = message["campaign_score_delta"].as_i64()
                        .map(|delta| format!("; score {delta:+}"))
                        .unwrap_or_default();
                    let receipt = format!(
                        "Campaign saved: {mission} ({}{score}); {awarded_ships} ships, {awarded_squads} squads awarded{evacuated}{banked}",
                        if success { "victory" } else { "defeat" },
                    );
                    let details=&mut self.campaign_reward_details;
                    details.clear();
                    details.push(format!(
                        "{} — {}",mission,if success {"Mission succeeded"}
                        else {"Mission failed"},
                    ));
                    if let Some(delta)=message["campaign_score_delta"].as_i64() {
                        details.push(format!("Committed score change: {delta:+}"));
                    }
                    if let Some(minerals)=message["banked_minerals"].as_u64() {
                        details.push(format!("Minerals banked: {minerals}"));
                    }
                    if let Some(people)=outcome["evacuated_personnel"].as_u64() {
                        details.push(format!("Personnel evacuated: {people}"));
                    }
                    if let Some(ships)=outcome["awarded_ships"].as_array() {
                        details.push(format!("FleetShips awarded: {}",ships.len()));
                        let catalog=source_ship_catalog();
                        for ship in ships {
                            let kind=ship["ship_type"].as_u64()
                                .and_then(|value|u8::try_from(value).ok());
                            let name=kind.and_then(|kind|catalog.by_type(kind))
                                .map_or("Unknown class",|asset|asset.name.as_str());
                            let id=ship["fleet_ship_id"].as_i64().unwrap_or(0);
                            let squad=ship["saved_squad_id"].as_i64().unwrap_or(0);
                            details.push(format!("  FleetShip #{id}: {name}  Squad #{squad}"));
                        }
                    }
                    if let Some(squads)=outcome["awarded_squads"].as_array() {
                        details.push(format!("SavedSquads awarded: {}",squads.len()));
                        for squad in squads {
                            let id=squad["saved_squad_id"].as_i64().unwrap_or(0);
                            let template=squad["template_saved_squad_id"].as_i64().unwrap_or(0);
                            details.push(format!("  Squad #{id}  Template #{template}"));
                        }
                    }
                    if let Some(retired)=outcome["retired_fleet_ids"].as_array() {
                        if !retired.is_empty() {
                            details.push(format!("Permanently retired: {} FleetShips",retired.len()));
                            for ids in retired.chunks(8) {
                                let labels=ids.iter().filter_map(Value::as_i64)
                                    .map(|id|format!("#{id}")).collect::<Vec<_>>();
                                details.push(format!("  {}",labels.join("  ")));
                            }
                        }
                    }
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
                        !message.get("winner_side").is_some_and(|winner|
                            winner.is_null()||winner.as_i64().is_some())
                    {
                        return Err(invalid("native battle result belongs to another session or side").into());
                    }
                    // Terminal receipts are reliable but belong to exactly
                    // one World episode. Never apply a late result for a
                    // previous battle to the current input and HUD gates.
                    if self.state.as_ref().is_some_and(|state|
                        state["world_id"] != message["world_id"] ||
                        state["episode_revision"] != message["episode_revision"]) {
                        return Err(invalid("battle result does not match current world episode").into());
                    }
                    let label = message["label"].as_str()
                        .filter(|text| !text.is_empty())
                        .ok_or_else(|| invalid("missing authoritative battle result label"))?;
                    let reason = message["reason"].as_str()
                        .filter(|text| !text.is_empty())
                        .ok_or_else(|| invalid("missing authoritative battle result reason"))?;
                    // Winnerless timeouts/draws are still real completed
                    // native battles; the source terminal receipt does not
                    // manufacture a winner merely to satisfy the client.
                    // The server attaches score only for its authenticated
                    // owner. No display-side guess about the opposing side.
                    let score = message["player_score"].as_i64().map(|value| {
                        let counts = &message["player_match_counts"];
                        if let (Some(lost), Some(returned), Some(kills)) = (
                            counts["ships_lost"].as_u64(),
                            counts["ships_returned"].as_u64(),
                            counts["ships_destroyed_by_player"].as_u64(),
                        ) {
                            format!(" | Score {value:+}  Lost {lost} Returned {returned} Kills {kills}")
                        } else {
                            format!(" | Score {value:+}")
                        }
                    }).unwrap_or_default();
                    let mined = message["player_match_counts"]["player_minerals_mined"]
                        .as_u64().filter(|&amount| amount != 0)
                        .map_or(String::new(), |amount| format!("  Mined {amount} TSV"));
                    // Present the original GameState per-type casualty ledger
                    // only after the authoritative result, never inferred
                    // from the client's incomplete, fog-filtered ship list.
                    self.battle_loss_breakdown.clear();
                    if let Some(losses) = message["player_match_counts"]["ships_lost_by_type"].as_array() {
                        let catalog = bees_sim::ship_assets::ShipAssetCatalog::embedded().ok();
                        let entries: Vec<String> = losses.iter().enumerate()
                            .filter_map(|(kind, count)| {
                                let amount = count.as_u64().filter(|&amount| amount > 0)?;
                                let name = catalog.as_ref()
                                    .and_then(|catalog| catalog.by_type(u8::try_from(kind).ok()?))
                                    .map_or_else(|| format!("Type {kind}"), |ship| ship.name.clone());
                                Some(format!("{name} {amount}"))
                            }).collect();
                        for chunk in entries.chunks(5) {
                            self.battle_loss_breakdown.push(format!("Losses: {}", chunk.join("  |  ")));
                        }
                    }
                    let text = format!("{label}{score}{mined}: {reason}");
                    self.battle_result_receipt = Some(text.clone());
                    self.notice = text;
                    self.metrics.battle_result_receipts += 1;
                }
                Some("briefing_result") => {
                    if !self.authenticated {
                        return Err(invalid("unauthenticated campaign briefing receipt").into());
                    }
                    let request=message["request_id"].as_u64()
                        .ok_or_else(||invalid("campaign briefing receipt has no request ID"))?;
                    let processed=message["processed"].as_bool()
                        .ok_or_else(||invalid("campaign briefing receipt has no result"))?;
                    let feedback=message["feedback"].as_str()
                        .filter(|text|!text.is_empty())
                        .ok_or_else(||invalid("campaign briefing receipt has no feedback"))?;
                    let Some((world,revision,sequence,id))=self.briefing_pending
                    else {return Err(invalid("unsolicited campaign briefing receipt").into())};
                    if request!=id {
                        return Err(invalid("campaign briefing receipt does not match its request").into());
                    }
                    self.briefing_pending=None;
                    if processed {
                        self.briefing_approved=Some((world,revision,sequence));
                        self.briefing_rejected=None;
                    } else {
                        // A rejection is authoritative for this exact cue.
                        // Retrying the same stale request every rendered frame
                        // cannot release the Level and only floods its socket.
                        self.briefing_rejected=Some((world,revision,sequence));
                        self.metrics.briefing_rejections+=1;
                    }
                    self.metrics.briefing_receipts+=1;
                    self.notice=format!("Story {request}: {feedback}");
                }
                Some("input_result") => {
                    if !self.authenticated {
                        return Err(invalid("native host acknowledged an unauthenticated order").into());
                    }
                    let request=message["request_id"].as_u64()
                        .ok_or_else(||invalid("native host acknowledgment has no request ID"))?;
                    let processed=message["processed"].as_bool()
                        .ok_or_else(||invalid("native host acknowledgment has no result"))?;
                    let feedback=message["feedback"].as_str()
                        .filter(|text|!text.is_empty())
                        .ok_or_else(||invalid("native host acknowledgment has no feedback"))?;
                    // Replies for this one TCP peer are serialized at
                    // authoritative input-tick boundaries. They must not
                    // acknowledge a future or already settled request.
                    if request<=self.last_response_request ||
                        request>=self.next_request {
                        return Err(invalid("out-of-order native host command acknowledgment").into());
                    }
                    self.last_response_request=request;
                    // Never retain input timings indefinitely if a network
                    // response was skipped or an old pending entry expired.
                    while self.pending_order_times.front()
                        .is_some_and(|(id,_)| *id < request)
                    {
                        self.pending_order_times.pop_front();
                        self.metrics.order_latency_samples_lost+=1;
                    }
                    if self.pending_order_times.front()
                        .is_some_and(|(id,_)| *id == request)
                    {
                        if let Some((_,submitted))=self.pending_order_times.pop_front() {
                            let elapsed=submitted.elapsed().as_secs_f64();
                            self.metrics.order_ack_latency_samples+=1;
                            self.metrics.order_ack_latency_sum_seconds+=elapsed;
                            self.metrics.order_ack_latency_max_seconds=
                                self.metrics.order_ack_latency_max_seconds.max(elapsed);
                        }
                    }
                    self.metrics.acknowledgments+=1;
                    if processed {self.metrics.orders_accepted+=1;}
                    else {self.metrics.orders_rejected+=1;}
                    if self.held_fire_begin_request == Some(request) {
                        self.held_fire_begin_request = None;
                        // A rejected Begin must not cause 30 new attempts
                        // per second while the player holds J. Require one
                        // release before requesting a different gesture.
                        if !processed ||
                            feedback == "No selected weapons accepted held fire" {
                            self.held_fire_rejected = true;
                        }
                    }
                    self.notice=format!("Order {request}: {feedback}");
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
        if self.spectator {
            return Err(invalid("native spectator seat has no gameplay command authority").into());
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
        let request=self.next_request;
        let submitted=Instant::now();
        self.enqueue_request(json!({"kind":"input","action":action}),notice)?;
        if self.pending_order_times.len()==MAX_PENDING_ORDER_TIMINGS {
            self.pending_order_times.pop_front();
            self.metrics.order_latency_samples_lost+=1;
        }
        self.pending_order_times.push_back((request,submitted));
        self.metrics.pending_order_timings_peak=self.metrics.pending_order_timings_peak
            .max(self.pending_order_times.len());
        self.metrics.orders_sent+=1;
        Ok(())
    }

    /// Presentation-only, per-cue acknowledgment. It never impersonates a
    /// gameplay command, advances a private clock, or changes a roster.
    fn briefing_ready(&mut self, world:u64, revision:u64, sequence:u32)
        -> Result<(),Box<dyn Error>>
    {
        if !self.authenticated || !self.connected || self.spectator {
            return Err(invalid("only an authenticated player can acknowledge a briefing").into());
        }
        let id=self.next_request;
        self.enqueue_request(json!({"kind":"briefing_ready",
            "world_id":world,"episode_revision":revision,
            "sequence":sequence}),"Campaign briefing ready".into())?;
        self.briefing_pending=Some((world,revision,sequence,id));
        self.metrics.briefing_requests_sent+=1;
        Ok(())
    }

    fn enqueue_request(&mut self, mut frame:Value, notice:String)
        -> Result<(),Box<dyn Error>>
    {
        let next=self.next_request.checked_add(1)
            .ok_or_else(|| invalid("native multiplayer request ID exhausted"))?;
        frame["request_id"]=json!(self.next_request);
        let mut packet = serde_json::to_vec(&frame)?;
        packet.push(b'\n');
        if self.queued_bytes + packet.len() > MAX_OUTGOING {
            return Err(invalid("outgoing native orders exceed pending network budget").into());
        }
        self.queued_bytes += packet.len();
        self.queued.push_back(packet);
        self.next_request = next;
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
                Ok(0) => {
                    // A closed transport is not a successfully submitted
                    // command. Surface failure to the original sender and
                    // let the next frame reset the disconnected seat.
                    self.connected=false;
                    self.notice="Host disconnected".into();
                    return Err(io::Error::new(ErrorKind::BrokenPipe,
                        "native multiplayer host closed during command delivery"));
                }
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

/// Optional source camera half-height limits from the host's validated
/// Level export. An older wire frame may omit them; it cannot authorize a
/// different zoom contract than the local native game.
fn camera_zoom_limits(state: &Value) -> Option<(f32, f32)> {
    let pair = state["camera_zoom"].as_array().filter(|values| values.len() == 2)?;
    let min = pair[0].as_f64()? as f32;
    let max = pair[1].as_f64()? as f32;
    (min.is_finite() && max.is_finite() && min > 0.0 && max >= min)
        .then_some((min, max))
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
enum RemotePrimaryHull {
    Circle { center: Vec2, radius: f32 },
    Polygon { vertices: [Vec2; 64], len: usize },
}

impl RemotePrimaryHull {
    fn overlaps_cursor(&self, point: Vec2, radius: f32) -> bool {
        match self {
            Self::Circle { center, radius: hull_radius } =>
                circles_overlap(point, radius, *center, *hull_radius),
            Self::Polygon { vertices, len } =>
                circle_overlaps_polygon(point, radius, &vertices[..*len]),
        }
    }

    fn overlaps_box(&self, bounds: Aabb) -> bool {
        let rectangle=[
            bounds.min,Vec2::new(bounds.max.x,bounds.min.y),
            bounds.max,Vec2::new(bounds.min.x,bounds.max.y),
        ];
        match self {
            Self::Circle{center,radius} =>
                circle_overlaps_polygon(*center,*radius,&rectangle),
            Self::Polygon{vertices,len} =>
                polygons_overlap(&vertices[..*len],&rectangle),
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

fn remote_collider_hull(collider: &Value, center: Vec2,
    rotation_degrees: f32) -> Option<RemotePrimaryHull>
{
    let pose = Pose { position: center, rotation_degrees };
    match collider["kind"].as_str() {
        Some("circle") => {
            let offset = point_from_wire(&collider["offset"])?;
            let radius = collider["radius"].as_f64().map(|r| r as f32)
                .filter(|r| r.is_finite() && *r >= 0.0)?;
            return Some(RemotePrimaryHull::Circle {
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
            return Some(RemotePrimaryHull::Polygon { vertices, len: 4 });
        }
        Some("polygon") => {
            let points = collider["vertices"].as_array()
                .filter(|vertices| (3..=64).contains(&vertices.len()))?;
            let mut vertices = [Vec2::ZERO; 64];
            for (index, source) in points.iter().enumerate() {
                vertices[index] = pose.transform_point(point_from_wire(source)?);
            }
            return Some(RemotePrimaryHull::Polygon {
                vertices, len: points.len(),
            });
        }
        _ => {}
    }
    None
}

fn remote_primary_hull(item: &Value, center: Vec2,
    rotation_degrees: f32) -> Option<RemotePrimaryHull>
{
    if let Some(hull) = remote_collider_hull(&item["collider"], center, rotation_degrees) {
        return Some(hull);
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
    Some(RemotePrimaryHull::Polygon { vertices, len: 4 })
}

/// Draws the same collider the cursor can select. A cheap authoritative AABB
/// cull avoids rasterizing large hulls outside the player's screen.
fn draw_remote_primary_hull_colored(
    pixels: &mut [u32], item: &Value, camera: NativeCamera,
    center: Vec2, rotation_degrees: f32, color: u32,
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
    let hull = remote_primary_hull(item, center, rotation_degrees)?;
    hull.draw(pixels, camera, color);
    Some(true)
}

fn draw_remote_primary_hull(
    pixels: &mut [u32], item: &Value, camera: NativeCamera,
    center: Vec2, rotation_degrees: f32,
) -> Option<bool> {
    let color = if item["mining"] == true { GOLD } else { 0x7d92aa };
    draw_remote_primary_hull_colored(
        pixels, item, camera, center, rotation_degrees, color,
    )
}

/// The authenticated world_state is already filtered by the host's
/// canonical faction fog. Never query a second enemy registry, infer
/// hidden objects, or issue commands from presentation-only markers.
fn draw_remote_minimap(
    pixels: &mut [u32], state: &Value, camera: NativeCamera,
    highlighted: &HashSet<i64>, visual: &RemoteVisual, fraction: f32,
) -> (u64, u64, u64) {
    let Some(bounds) = bounds(state) else { return (0, 0, 0) };
    let Some(layout) = MiniMapLayout::top_right(FIELD, HEIGHT, bounds)
        else { return (0, 0, 0) };
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
    // One physical Level's fog-filtered static objects are authoritative
    // for cover and mining. Outline large collider AABBs; deduplicate
    // subpixel terrain markers in dense asteroid fields. Neither path
    // samples undisclosed objects or constructs a second navigation map.
    let mut terrain_markers = 0_u64;
    let mut cells = HashSet::new();
    for object in state["obstacles"].as_array().into_iter().flatten() {
        let Some(point) = visual.position(object, 2, fraction) else { continue };
        let Some((x,y)) = layout.project(point) else { continue };
        let mining = object["mining"] == true;
        let color = if mining {GOLD} else {0x8195a9};
        let bounds = object["collider_bounds"].as_array()
            .filter(|bounds|bounds.len()==2)
            .and_then(|bounds|Some((
                point_from_wire(&bounds[0])?,
                point_from_wire(&bounds[1])?,
            )));
        if let Some((min,max))=bounds {
            let a=layout.project_unbounded(Vec2::new(min.x,max.y));
            let b=layout.project_unbounded(Vec2::new(max.x,min.y));
            let left=a.0.max(layout.plot_left).round() as i32;
            let top=a.1.max(layout.plot_top).round() as i32;
            let right=b.0.min(layout.plot_right).round() as i32;
            let bottom=b.1.min(layout.plot_bottom).round() as i32;
            if right-left>=4 && bottom-top>=4 {
                outline(pixels,left,top,right-left,bottom-top,color);
                terrain_markers+=1;
                continue;
            }
        }
        let cell=(x.round() as i32,y.round() as i32,mining);
        if cells.insert(cell) {
            plot(pixels,cell.0,cell.1,color);
            terrain_markers+=1;
        }
    }
    let target = &state["campaign_objective"];
    let objective_ship = target["protected_ship_id"].as_i64()
        .or_else(|| target["escorted_ship_id"].as_i64())
        .or_else(|| target["target_ship_id"].as_i64());
    // An opposing player never receives private rescue identities; a
    // visible enemy target remains highlighted only if it appears in the
    // host's normal side-filtered ship projection.
    let rescue_barges = target["barge_ids"].as_array();
    let objective_object = target["target_map_object_id"].as_i64();
    let mut ship_markers = 0_u64;
    let mut objective_markers = 0_u64;
    for ship in state["ships"].as_array().into_iter().flatten() {
        let Some(position) = visual.position(ship, 0, fraction) else { continue };
        let Some((x, y)) = layout.project(position) else { continue };
        let id = ship["id"].as_i64().unwrap_or(-1);
        let objective = objective_ship == Some(id) ||
            rescue_barges.is_some_and(|barges|
                barges.iter().any(|barge| barge.as_i64() == Some(id)));
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
    (ship_markers, terrain_markers, objective_markers)
}

fn first_selected<'a>(state: Option<&'a Value>, selected: &[i64]) -> Option<&'a Value> {
    state?.get("squads")?.as_array()?.iter().find(|squad|
        squad["controllable"] == true && squad["owner"]["squad_id"]
            .as_i64().is_some_and(|id| selected.contains(&id)))
}

/// Numeric squad IDs can be reused after a source lifetime retires.
/// Preserve the full authenticated lifetime across snapshot updates so an
/// old UI selection cannot silently become a command for a replacement.
fn selected_squad_lifetime(state: &Value, id: i64) -> Option<(i64, u64)> {
    let squad=state["squads"].as_array()?.iter().find(|s|
        s["controllable"]==true &&
        s["owner"]["squad_id"].as_i64()==Some(id))?;
    Some((squad["owner"]["level_id"].as_i64()?,
          squad["owner"]["lifecycle_id"].as_u64()?))
}

fn squad_for_ship<'a>(state: &'a Value, id: i64, side: i64) -> Option<&'a Value> {
    state["squads"].as_array()?.iter().find(|squad|
        squad["side"].as_i64() == Some(side) &&
        squad["members"].as_array().is_some_and(|members|
            members.iter().any(|member| member.as_i64() == Some(id))))
}
fn hit<'a>(state: &'a Value, camera: NativeCamera, x: i32, y: i32,
    visual: &RemoteVisual, fraction: f32,
    ship_filter: impl Fn(&Value) -> bool, obstacle_filter: impl Fn(&Value) -> bool)
    -> Option<(&'static str, &'a Value)>
{
    // A foreground ship wins over an obstacle underneath its hull.
    let cursor=camera.screen_to_world(x as f32,y as f32,FIELD,HEIGHT);
    let ship_pick_radius=18.0/camera.pixels_per_unit.max(0.001);
    let mut ship_hit=None;
    let mut best_ship=i32::MAX;
    for ship in state["ships"].as_array().into_iter().flatten() {
        if !ship_filter(ship) { continue; }
        let Some(point)=visual.position(ship,0,fraction) else {continue};
        let (sx,sy)=screen(camera,point);
        let distance=(sx-x).pow(2)+(sy-y).pow(2);
        let heading=visual.heading_for(ship,0,fraction);
        let body_hit=remote_primary_hull(ship,point,heading)
            .is_some_and(|hull|hull.overlaps_cursor(cursor,ship_pick_radius));
        // Unity CheckClickCollision admits the Beehive's authored child
        // HealCollider as a click target. This read-only geometry comes
        // from the SAME host ShipBody, not an enlarged combat collider.
        let healing_hit=remote_collider_hull(
            &ship["input_healing_zone"],point,heading,
        ).is_some_and(|hull|hull.overlaps_cursor(cursor,ship_pick_radius));
        if (body_hit||healing_hit) &&distance<best_ship {
            best_ship=distance;
            ship_hit=Some(("ship",ship));
        }
    }
    if ship_hit.is_some() {return ship_hit;}

    let pick_radius = 10.0 / camera.pixels_per_unit.max(0.001);
    let mut selected = None;
    let mut nearest = i32::MAX;
    for obj in state["obstacles"].as_array().into_iter().flatten() {
        if !obstacle_filter(obj) { continue; }
        let Some(point) = visual.position(obj, 2, fraction) else { continue };
        let overlap = remote_primary_hull(obj, point,
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

// Side-panel squad rows are the same authoritative source-order projection
// used to draw names and selection highlights. Noncontrollable squads stay
// visible in the roster but cannot enter a player command through a tab.
fn remote_squad_tab_hit(state: &Value,x: f32,y: f32, offset: usize) -> Option<i64> {
    if !x.is_finite()||!y.is_finite()||
        x<(FIELD+14) as f32||x>=(WIDTH-14) as f32 {
        return None;
    }
    state["squads"].as_array()?.iter().filter(|s|s["owned"]==true)
        .skip(offset).take(12).enumerate().find_map(|(index,squad)| {
            let top=150+index*22;
            (y>=top as f32-3.0&&y<top as f32+18.0&&
                squad["controllable"]==true)
                .then(||squad["owner"]["squad_id"].as_i64()).flatten()
        })
}

/// Only the current host-authorized and selected physical Fire Barges
/// have a source DetonationClick sound. Resolve their identities at the
/// actual user action, never with per-frame sound-side roster tracking.
fn selected_fire_barges(state: Option<&Value>, selected: &[i64]) -> usize {
    let Some(state) = state else { return 0 };
    let selected_members: HashSet<i64> =
        state["squads"].as_array().into_iter().flatten()
            .filter(|squad| squad["controllable"] == true &&
                squad["owner"]["squad_id"].as_i64()
                    .is_some_and(|id| selected.contains(&id)))
            .flat_map(|squad| squad["members"].as_array().into_iter().flatten())
            .filter_map(Value::as_i64).collect();
    state["ships"].as_array().into_iter().flatten()
        .filter(|ship| ship["owned"] == true &&
            ship["ship_type"].as_u64() == Some(10) &&
            ship["health"].as_i64().is_some_and(|health| health > 0) &&
            ship["id"].as_i64().is_some_and(|id|
                selected_members.contains(&id)))
        .count()
}

/// One dispatcher for LAN keyboard and on-screen tactical commands.
fn remote_tactical_action(
    peer: &mut PeerClient, selected: &mut Vec<i64>,
    mode: &mut Option<TargetMode>, patrol_start: &mut Option<Vec2>,
    held_fire_active: &mut bool, held_fire_target: &mut Option<Vec2>,
    shooting_menu: &mut bool, shooting_focus: &mut u8,
    action: HudAction, audio: &mut NativeGameAudio,
) -> Result<(),Box<dyn Error>> {
    // End releases a peer-owned weapon lease, not a command for the
    // currently selected squads. An eliminated squad or new terminal
    // snapshot may clear selection before the player releases J/X.
    if action==HudAction::StopFire && *held_fire_active {
        if let Err(error)=peer.held_fire(selected,"End",None) {
            let _=peer.socket.shutdown(std::net::Shutdown::Both);
            peer.connected=false;
            return Err(error);
        }
        *held_fire_active=false;
        *held_fire_target=None;
        return Ok(());
    }
    if peer.input_suppressed {
        return Err(invalid("orders unavailable during briefing or after battle").into());
    }
    if *held_fire_active && action != HudAction::Shooting {
        return Err(invalid("release manual fire before issuing another tactical order").into());
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
            // Do not leave an active hold-to-fire lease behind while the
            // local presentation focuses the strategy picker.
            if *held_fire_active {
                if let Err(error)=peer.held_fire(selected,"End",None) {
                    let _=peer.socket.shutdown(std::net::Shutdown::Both);
                    peer.connected=false;
                    return Err(error);
                }
                *held_fire_active=false;*held_fire_target=None;
            }
            *shooting_focus=first_selected(peer.state.as_ref(),selected)
                .and_then(|s|s["shooting"].as_u64())
                .unwrap_or(1).clamp(1,40) as u8;
            *shooting_menu=!*shooting_menu;
            return Ok(());
        }
        HudAction::StopFire=>peer.order(selected,"fire",json!({"target":null})),
        HudAction::Hold=>peer.order(selected,"support",json!({"action":"Hold"})),
        HudAction::CeaseFire=>peer.order(selected,"cease_fire",json!({"cease_fire":true})),
        HudAction::AttackOnSight=>peer.order(selected,"cease_fire",json!({"cease_fire":false})),
        HudAction::Beacon=>peer.order(selected,"special",json!({"kind":"DropBeacon"})),
        HudAction::Detonate=>{
            let fire_barges = selected_fire_barges(peer.state.as_ref(),selected);
            peer.order(selected,"special",json!({"kind":"Detonate"}))?;
            audio.click_fire_barge_detonation(fire_barges);
            Ok(())
        },
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
    // Source focus tracks the whole live squad's physical collider
    // envelope, not the first member's sprite centre. Construct this
    // membership index only when a squad hotkey actually requests focus.
    let members:HashSet<i64>=squad["members"].as_array()?.iter()
        .filter_map(Value::as_i64).collect();
    let mut extents:Option<Aabb>=None;
    for ship in state["ships"].as_array().into_iter().flatten() {
        if !ship["id"].as_i64().is_some_and(|id|members.contains(&id)) {continue;}
        let Some(point)=position(ship) else {continue};
        let hull=ship["collider_bounds"].as_array().filter(|bounds|bounds.len()==2)
            .and_then(|bounds|Some((
                point_from_wire(&bounds[0])?,
                point_from_wire(&bounds[1])?,
            )))
            .filter(|(min,max)|min.x<=max.x && min.y<=max.y)
            .map_or(Aabb{min:point,max:point},|(min,max)|Aabb{min,max});
        extents=Some(match extents {
            None=>hull,
            Some(previous)=>Aabb {
                min:Vec2::new(previous.min.x.min(hull.min.x),
                    previous.min.y.min(hull.min.y)),
                max:Vec2::new(previous.max.x.max(hull.max.x),
                    previous.max.y.max(hull.max.y)),
            },
        });
    }
    extents.map(|bounds|(bounds.min+bounds.max)*0.5)
}

/// The remote window presents the host's authored dialogue, never a second
/// mission progression state. Peers choose which line to read locally.
fn draw_campaign_dialogue(pixels: &mut [u32], state: &Value, line_index: usize) {
    let Ok(cue)=serde_json::from_value::<CampaignCue>(
        state["campaign_dialogue"]["cue"].clone(),
    ) else {return;};
    let count=super::campaign_dialogue::line_count(cue);
    let Some((speaker,message))=super::campaign_dialogue::visible_line(cue,line_index) else{return;};
    for scanline in pixels.chunks_exact_mut(WIDTH).skip(HEIGHT-160).take(96) {
        scanline[..FIELD].fill(0x1c3045);
    }
    let heading=format!("{}   Dialogue {}/{}",
        speaker,line_index+1,count);
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
    selected: &[i64], squad_list_offset: usize, notice: &str, side: i32, mode: Option<TargetMode>,
    patrol_start: Option<Vec2>, metrics: &WireCounts, dialogue_line: usize,
    held_fire_active: bool, shooting_menu: bool, shooting_focus: u8,
    visual: &RemoteVisual, fraction: f32,
    artwork: &RemoteArtwork, show_art: bool, show_weapon_ranges: bool,
    show_minimap: bool,
    combat_hud_draws: &mut [u64; 7], obstacle_hull_draws: &mut u64,
    map_object_health_bars: &mut u64,
    minimap_ship_markers: &mut u64, minimap_terrain_markers: &mut u64,
    minimap_objective_markers: &mut u64,
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
    text::draw_at(pixels, WIDTH, FIELD + 14, 76,
        "F1: controls and shortcuts", 1, MUTED);
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
                let id = obj["id"].as_i64().unwrap_or(-1);
                if !artwork.asteroids.contains(&id) && !artwork.fire_tanks.contains(&id) {
                    match draw_remote_primary_hull(
                        pixels, obj, camera, point,
                        visual.heading_for(obj, 2, fraction),
                    ) {
                        Some(true) => *obstacle_hull_draws += 1,
                        Some(false) => {},
                        None => {
                            let coord=screen(camera,point);
                            circle(pixels,coord,5,if obj["mining"]==true {GOLD} else {0x7d92aa});
                        }
                    }
                }
                if obj["range_visible"] == true {
                    if let (Some(hp), Some(max)) =
                        (obj["health"].as_f64(), obj["max_health"].as_f64())
                    {
                        if hp.is_finite() && max.is_finite() && max > 0.0 {
                            let (x,y)=screen(camera,point);
                            if x>=-10 && x<FIELD as i32+10 && y>=12 && y<HEIGHT as i32+12 {
                                let color=if obj["map_object_kind"].as_u64() ==
                                    Some(bees_sim::map_object::MapObjectKind::FireTank as u64)
                                    {0xe1a34b} else {0x9bd2ae};
                                line(pixels,(x-10,y-12),(x+10,y-12),0x3c2837);
                                line(pixels,(x-10,y-12),
                                    (x-10+(20.0*(hp/max).clamp(0.0,1.0)) as i32,y-12),color);
                                *map_object_health_bars+=1;
                            }
                        }
                    }
                }
            }
        }
        if !show_art {
            for piece in state["asteroid_pieces"].as_array().into_iter().flatten() {
                let (Some(point), Some(opacity)) = (visual.position(piece, 3, fraction),
                    piece["opacity"].as_f64().filter(|v| v.is_finite() && *v > 0.0))
                    else { continue };
                let (x,y)=screen(camera,point);
                if x<0 || x>=FIELD as i32 || y<0 || y>=HEIGHT as i32 { continue; }
                let color=(opacity.clamp(0.0,1.0)*160.0) as u32 * 0x010101;
                line(pixels,(x-1,y),(x+1,y),color);
                line(pixels,(x,y-1),(x,y+1),color);
            }
        }
        for shot in state["projectiles"].as_array().into_iter().flatten() {
            if !artwork.projectiles.contains(&shot["id"].as_i64().unwrap_or(-1)) {
                if let Some(point)=visual.position(shot, 1, fraction) {
                    // Preserve the canonical, mutable projectile shape:
                    // real beams extend, rockets rotate and explosions
                    // grow. Only truly shapeless projectiles get a dot.
                    // Host fog has already removed undisclosed shots.
                    if draw_remote_primary_hull_colored(
                        pixels, shot, camera, point,
                        visual.heading_for(shot, 1, fraction), 0xf8d798,
                    ).is_none() {
                        circle(pixels,screen(camera,point),2,0xf8d798);
                    }
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
        let command_targets: HashSet<i64> = state["squads"].as_array().into_iter().flatten()
            .filter(|s|s["controllable"]==true &&
                s["owner"]["squad_id"].as_i64().is_some_and(|id|selected.contains(&id)))
            .flat_map(|s|s["command_targets"].as_array().into_iter().flatten())
            .filter_map(Value::as_i64).collect();
        for ship in state["ships"].as_array().into_iter().flatten() {
            let Some(point)=visual.position(ship, 0, fraction) else { continue };
            let center=screen(camera,point);
            let selected_ship=ship["owned"]==true &&
                highlighted.contains(&ship["id"].as_i64().unwrap_or(-1));
            let visible = ship["collider_bounds"].as_array()
                .filter(|bounds| bounds.len()==2)
                .and_then(|bounds|Some((
                    point_from_wire(&bounds[0])?,
                    point_from_wire(&bounds[1])?,
                )))
                .map_or_else(|| {
                    center.0>=-24 && center.0<=FIELD as i32+24 &&
                    center.1>=-24 && center.1<=HEIGHT as i32+24
                }, |(min,max)| {
                    let a=screen(camera,Vec2::new(min.x,max.y));
                    let b=screen(camera,Vec2::new(max.x,min.y));
                    // Preserve large or irregular ship hulls whose centre
                    // is offscreen but combat geometry still intersects
                    // the camera. Margin covers visual interpolation.
                    b.0>=-24 && a.0<=FIELD as i32+24 &&
                    b.1>=-24 && a.1<=HEIGHT as i32+24
                });
            if !selected_ship && !visible { continue; }
            // Side allegiance and command rights are separate. The host
            // includes AI-controlled friendly ships but never permits a
            // remote peer to select their unowned squad.
            let friendly=ship["friendly"]==true;
            let color=if friendly {GREEN} else {RED};
            let heading=visual.heading(ship, fraction);
            let painted = artwork.ships.contains(&ship["id"].as_i64().unwrap_or(-1));
            // Authentic source artwork takes precedence. If packaged art
            // is missing, the native fallback now draws the same real
            // physics hull as hit-testing rather than a generic triangle.
            // Only malformed/legacy shapeless records retain a tiny marker.
            if !painted && draw_remote_primary_hull_colored(
                pixels,ship,camera,point,heading,color,
            ).is_none() {
                circle(pixels,center,5,color);
            }
            let health=ship["health"].as_f64().unwrap_or(0.0);
            let maximum=ship["max_health"].as_f64().unwrap_or(1.0).max(1.0);
            if health.is_finite() && maximum.is_finite() {
                line(pixels,(center.0-10,center.1+14),(center.0+10,center.1+14),0x4f6377);
                line(pixels,(center.0-10,center.1+14),
                     (center.0-10+(20.0*(health/maximum).clamp(0.0,1.0)) as i32,center.1+14),
                     color);
            }
            if command_targets.contains(&ship["id"].as_i64().unwrap_or(-1)) {
                let (x,y)=center;
                let marker=0xffba6d;
                line(pixels,(x-11,y),(x,y-11),marker);
                line(pixels,(x,y-11),(x+11,y),marker);
                line(pixels,(x+11,y),(x,y+11),marker);
                line(pixels,(x,y+11),(x-11,y),marker);
                combat_hud_draws[3]+=1;
            }
            if let Some(loaded)=ship["striker_bomb_ready"].as_bool() {
                line(pixels,(center.0+12,center.1-9),(center.0+17,center.1-9),
                    if loaded {0x87dfad} else {0x987d7d});
                combat_hud_draws[4]+=1;
            }
            if selected_ship {
                // The original game selects the actual physical ShipBody,
                // including large/rotated flagships and child hulls. The
                // highlight follows the same source shape as picking and
                // collision; old shapeless snapshots retain a small box.
                if let Some(hull)=remote_primary_hull(ship,point,heading) {
                    hull.draw(pixels,camera,0xf9f0a1);
                } else {
                    outline(pixels,center.0-15,center.1-17,30,34,0xf9f0a1);
                }
                for weapon in ship["weapons"].as_array().into_iter().flatten() {
                    let Some(slot)=weapon["slot"].as_u64().filter(|&slot|slot<5) else {continue};
                    if let Some(charge)=weapon["charge"].as_array().filter(|v|v.len()==3) {
                        if let (Some(elapsed),Some(total),Some(paused))=
                            (charge[0].as_f64(),charge[1].as_f64(),charge[2].as_bool())
                        {
                            if total>0.0 && total.is_finite() && elapsed.is_finite() {
                                let y=center.1+18+slot as i32*3;
                                let x=center.0-10;
                                line(pixels,(x,y),(x+20,y),0x34435a);
                                line(pixels,(x,y),(x+(20.0*(elapsed/total).clamp(0.0,1.0)) as i32,y),
                                    if paused {0xe1a34b} else {0x62b9ff});
                                combat_hud_draws[1]+=1;
                            }
                        }
                    }
                    if !show_weapon_ranges {continue;}
                    if let Some(range)=weapon["range"].as_array().filter(|v|v.len()==4) {
                        if let (Some(x),Some(y),Some(radius),Some(enabled))=
                            (range[0].as_f64(),range[1].as_f64(),range[2].as_f64(),range[3].as_bool())
                        {
                            let offset=Vec2::new(x as f32,y as f32);
                            let radius=radius as f32;
                            let range_center=point+offset;
                            if range_center.is_finite() && radius.is_finite() && radius>0.0 {
                                RemotePrimaryHull::Circle {center:range_center,radius}
                                    .draw(pixels,camera,if enabled {0x355b80} else {0x555555});
                                combat_hud_draws[0]+=1;
                            }
                        }
                    }
                    if let Some(origin)=point_from_wire(&weapon["origin"]) {
                        let origin=screen(camera,origin);
                        for contact in weapon["contacts"].as_array().into_iter().flatten() {
                            let Some(fields)=contact.as_array().filter(|fields|fields.len()==4) else {continue};
                            let (Some(x),Some(y),Some(blocked))=
                                (fields[0].as_f64(),fields[1].as_f64(),fields[2].as_bool())
                            else {continue};
                            let endpoint=Vec2::new(x as f32,y as f32);
                            if !endpoint.is_finite() {continue;}
                            line(pixels,origin,screen(camera,endpoint),
                                if blocked {0xe1a34b} else {0x62b9ff});
                            combat_hud_draws[5]+=1;
                            combat_hud_draws[6]+=u64::from(blocked);
                            if let Some(aim)=point_from_wire(&fields[3]) {
                                let (x,y)=screen(camera,aim);
                                line(pixels,(x-4,y),(x+4,y),0xf6df85);
                                line(pixels,(x,y-4),(x,y+4),0xf6df85);
                                combat_hud_draws[2]+=1;
                            }
                        }
                    }
                    if let Some(aim)=point_from_wire(&weapon["aim"]) {
                        let (x,y)=screen(camera,aim);
                        line(pixels,(x-4,y),(x+4,y),0xf6df85);
                        line(pixels,(x,y-4),(x,y+4),0xf6df85);
                        combat_hud_draws[2]+=1;
                    }
                }
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
        let (ships, terrain, objectives) = if show_minimap {
            draw_remote_minimap(pixels, state, camera, &highlighted, visual, fraction)
        } else { (0, 0, 0) };
        *minimap_ship_markers += ships;
        *minimap_terrain_markers += terrain;
        *minimap_objective_markers += objectives;
        text::draw_at(pixels, WIDTH, FIELD+14, 90,
            &format!("World {}  Tick {}",state["world_id"],state["tick"]),1,TEXT);
        let friendly=state["squads"].as_array().into_iter().flatten().filter(|s|s["owned"]==true).count();
        text::draw_at(pixels, WIDTH, FIELD+14, 111,
            &format!("Squads {friendly}   Selected {}  {}/{}",selected.len(),
                if friendly==0 {0} else {squad_list_offset+1},
                friendly),1,MUTED);
        if let Some(score) = state["player_score"].as_i64() {
            let counts = &state["player_match_counts"];
            let label = match (
                counts["ships_destroyed_by_player"].as_u64(),
                counts["ships_lost"].as_u64(),
                counts["ships_returned"].as_u64(),
            ) {
                (Some(kills), Some(lost), Some(returned)) =>
                    format!("Score {score:+} K{kills} L{lost} R{returned}"),
                _ => format!("Score {score:+}"),
            };
            let mined = state["player_minerals_mined"].as_u64()
                .filter(|&amount| amount != 0)
                .map_or(String::new(), |amount| format!("  M{amount}"));
            text::draw_at(pixels, WIDTH, FIELD+14, 131,
                text::fit(&format!("{label}{mined}"), WIDTH-FIELD-24), 1, [198,231,248]);
        }
        for (index,squad) in state["squads"].as_array().into_iter().flatten()
            .filter(|s|s["owned"]==true).skip(squad_list_offset).take(12).enumerate() {
            let id=squad["owner"]["squad_id"].as_i64().unwrap_or(-1);
            let number=squad["source_number"].as_u64()
                .map_or_else(||format!("ID {id}"),|number|format!("#{number}"));
            let class=squad["lead_ship_type"].as_u64()
                .and_then(|kind|u8::try_from(kind).ok())
                .and_then(|kind|source_ship_catalog().by_type(kind))
                .map_or("Unknown",|ship|ship.name.as_str());
            let label=format!("{} {} {} ({}){}",if selected.contains(&id) {">"} else {" "},
                number,class,squad["members"].as_array().map_or(0,Vec::len),
                if squad["controllable"]==true {""} else {" [orders off]"});
            text::draw_at(pixels, WIDTH, FIELD+14, 150+index*22,
                text::fit(&label,WIDTH-FIELD-24),1,
                if selected.contains(&id){TEXT}else{MUTED});
        }
        // The authoritative host supplies real FleetShip health/TSV and
        // mined cargo only for this player's owned physical hulls. Count
        // selected live ships in one pass; never derive enemy career value
        // from a visible enemy silhouette or estimate missing older fields.
        if !highlighted.is_empty() {
            let mut health=0_i64;
            let mut maximum=0_i64;
            let mut tsv=0_i64;
            let mut base_tsv=0_i64;
            let mut mined=0_i64;
            let mut owned_count=0_usize;
            let mut financials_available=false;
            for ship in state["ships"].as_array().into_iter().flatten() {
                if ship["owned"]!=true || !ship["id"].as_i64()
                    .is_some_and(|id|highlighted.contains(&id)) {continue;}
                owned_count+=1;
                health+=ship["health"].as_i64().unwrap_or(0);
                maximum+=ship["max_health"].as_i64().unwrap_or(0);
                if let (Some(value),Some(base),Some(cargo)) = (
                    ship["tsv"].as_i64(),ship["base_tsv"].as_i64(),
                    ship["mined_cargo"].as_i64(),
                ) {
                    tsv+=value;
                    base_tsv+=base;
                    mined+=cargo;
                    financials_available=true;
                }
            }
            if owned_count>0 {
                let label=if financials_available {
                    format!("HP {health}/{maximum}  TSV {tsv}/{base_tsv}  Mined {mined}")
                } else {
                    format!("{owned_count} selected ships  HP {health}/{maximum}")
                };
                text::draw_at(pixels,WIDTH,FIELD+14,405,
                    text::fit(&label,WIDTH-FIELD-24),1,[198,231,248]);
            }
        }
        if let Some(squad)=first_selected(Some(state),selected) {
            let id=squad["owner"]["squad_id"].as_i64().unwrap_or(0);
            let shooting=squad["shooting"].as_u64().unwrap_or(0);
            let command=squad["command"].as_str().unwrap_or("Idle");
            let number=squad["source_number"].as_u64()
                .map_or_else(||format!("ID {id}"),|number|format!("#{number}"));
            text::draw_at(pixels,WIDTH,FIELD+14,417,
                text::fit(&format!("{number}: {command}  Shooting {shooting}"),
                    WIDTH-FIELD-24),1,TEXT);
            let enabled=|key:&str|if squad[key]==true {"On"} else {"Off"};
            let flags=format!("Chase {} Lock {} Speed {}",
                enabled("chasing"),enabled("locked_on"),enabled("match_speed"));
            text::draw_at(pixels,WIDTH,FIELD+14,435,
                text::fit(&flags,WIDTH-FIELD-24),1,MUTED);
        }
        let outcome=state["terminal_outcome"]["label"].as_str().map(str::to_owned);
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
            if objective.get("kind").and_then(Value::as_str) == Some("barge_rescue") {
                let rescue = objective.get("rescuer_side").and_then(Value::as_i64)
                    == Some(i64::from(side));
                let text_label = if rescue {
                    objective.get("barges_remaining").and_then(Value::as_u64)
                        .map(|count| format!("Rescue the Barges  {count}/3 alive  |  Destroy all Bees"))
                        .unwrap_or_else(|| "Rescue the Barges  |  Destroy all Bees".to_owned())
                } else {
                    "Prevent the Barge rescue  |  Destroy the opposing fleet".to_owned()
                };
                text::draw_at(pixels,WIDTH,16,23,
                    text::fit(&text_label,FIELD-32),1,[255,211,130]);
            }
            if objective.get("kind").and_then(Value::as_str) == Some("pluto_reinforcements") {
                let owner = objective["player_side"].as_i64() == Some(i64::from(side));
                let label = if !owner {
                    "Defend the approaching Bee fleet"
                } else if objective["combat_released"] == true {
                    "Find and destroy the enemy ships!"
                } else if objective["scout_selected"] == true {
                    "Learn your squad controls before the enemy arrives"
                } else { "Use the Scout" };
                text::draw_at(pixels,WIDTH,16,23,
                    text::fit(label,FIELD-32),1,[255,211,130]);
            }
            if objective.get("kind").and_then(Value::as_str) == Some("pluto_anomaly") {
                let owner = objective.get("player_side").and_then(Value::as_i64)
                    == Some(i64::from(side));
                let label = if !owner {
                    "Defend the unidentified vessel near Pluto"
                } else if objective["honeybee_destroyed"] == true {
                    "First Honeybee destroyed  |  Monitor incoming enemy signals"
                } else if objective["gunship_briefed"] == true {
                    "Lieutenant Tom is engaging the unidentified Honeybee"
                } else if objective["gunship_arrived"] == true {
                    "Gunship arrived  |  Listen to Lieutenant Tom's briefing"
                } else if objective["honeybee_spotted"] == true {
                    "Unidentified vessel located  |  Stand by for Lieutenant Tom"
                } else {
                    "Scout and explore around Pluto"
                };
                text::draw_at(pixels, WIDTH, 16, 23,
                    text::fit(label, FIELD - 32), 1, [255, 211, 130]);
            }
            if objective.get("kind").and_then(Value::as_str) == Some("pluto_pushback") {
                let owner = objective.get("player_side").and_then(Value::as_i64)
                    == Some(i64::from(side));
                let label = if owner {
                    "Push back the enemy!"
                } else {
                    "Defend the Bee fleet from the attacking human ships"
                };
                text::draw_at(pixels,WIDTH,16,23,
                    text::fit(label,FIELD-32),1,[255,211,130]);
            }
            if objective.get("kind").and_then(Value::as_str) == Some("target_ship") {
                let attacker = objective.get("attacking_side")
                    .and_then(Value::as_i64) == Some(i64::from(side));
                let label = if attacker {
                    format!("Eliminate target ship #{}",
                        objective["target_ship_id"].as_i64().unwrap_or(0))
                } else {
                    "Protect the mission target from the opposing fleet".to_owned()
                };
                text::draw_at(pixels,WIDTH,16,23,
                    text::fit(&label,FIELD-32),1,[255,211,130]);
            }
            if objective.get("kind").and_then(Value::as_str) == Some("campaign_elimination") {
                let attacking = objective.get("attacking_side")
                    .and_then(Value::as_i64) == Some(i64::from(side));
                // All campaign battles use the host's physical victory
                // rules; their authored objective text is presentation
                // only. Never infer mission identity from surviving hulls.
                let label = match (state["campaign_mission"].as_str(), attacking) {
                    (Some("neptune_seize_means"), true) =>
                        "Seize Neptune's production facilities  |  Defeat the Bees",
                    (Some("neptune_seize_means"), false) =>
                        "Defend Neptune's production facilities",
                    (Some("neptune_pressing_forward"), true) =>
                        "Press forward  |  Defeat the Bee fleet",
                    (Some("neptune_pressing_forward"), false) =>
                        "Repel the advancing human fleet",
                    (_, true) => "Find and destroy all the Bees",
                    (_, false) => "Defend the Bee fleet from the human attackers",
                };
                text::draw_at(pixels,WIDTH,16,23,
                    text::fit(label,FIELD-32),1,[255,211,130]);
            }
            if objective.get("kind").and_then(Value::as_str) ==
                Some("withdrawal_pending")
            {
                let owner = objective.get("exiting_side")
                    .and_then(Value::as_i64) == Some(i64::from(side));
                let label = if owner {
                    "Mine, defend, then press F3 to open the retreat corridor"
                } else {
                    "Prevent the enemy fleet from opening its retreat corridor"
                };
                text::draw_at(pixels, WIDTH, 16, 23,
                    text::fit(label, FIELD-32), 1, [255, 211, 130]);
            }
            if objective.get("kind").and_then(Value::as_str) == Some("extraction") {
                let escaping = objective.get("exiting_side")
                    .and_then(Value::as_i64) == Some(i64::from(side));
                let retreat = objective.get("retreat_on_exit")
                    .and_then(Value::as_bool).unwrap_or(false);
                let label = match (escaping, retreat) {
                    (true, true) if objective.get("mine_and_withdraw")
                        .and_then(Value::as_bool) == Some(true) =>
                        "Survive and mine minerals  |  Retreat through the marked zone",
                    (true, true) => "Withdraw surviving fleet through the marked retreat zone",
                    (false, true) => "Intercept enemy fleet before its withdrawal",
                    (true, false) if objective["titania_minesweeper"] == true =>
                        "Reach Titania's center or clear the Bees",
                    (false, false) if objective["titania_minesweeper"] == true =>
                        "Stop the opposing fleet from reaching Titania",
                    (true, false) => "Extract your fleet to the marked exit",
                    (false, false) => "Intercept enemy fleet before extraction",
                };
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
                        // The mission identity is public to both sides.
                        // Campaign dialogue is private and may be absent on
                        // opposing peers or after an acknowledged briefing.
                        let evacuation = objective["pluto_evacuation"] == true;
                        if objective["titania_defense"] == true {
                            let shield = state["ships"].as_array().into_iter().flatten()
                                .find(|ship| ship["id"].as_i64() == Some(protected));
                            let health = if defending {
                                shield.and_then(|ship| Some((
                                    ship["health"].as_i64()?,
                                    ship["max_health"].as_i64()?,
                                ))).map(|(hp, max)|
                                    format!("  Shield {hp}/{max} HP"))
                                    .unwrap_or_default()
                            } else { String::new() };
                            if defending {
                                format!("Defend Titania{health}  A.M.I. {:.0}%  {:02}:{:02}",
                                    percent.floor(), seconds / 60, seconds % 60)
                            } else {
                                format!("Destroy Titania's defenses  {:02}:{:02}",
                                    seconds / 60, seconds % 60)
                            }
                        } else if evacuation && defending {
                            let elapsed = objective.get("elapsed_seconds")
                                .and_then(Value::as_f64)
                                .filter(|value| value.is_finite() && *value >= 0.0)
                                .unwrap_or(0.0);
                            let saved = (elapsed / 5.0).floor().clamp(0.0, 60.0) as u32;
                            let lost = state["ships"].as_array().into_iter().flatten()
                                .find(|ship| ship["id"].as_i64() == Some(protected))
                                .and_then(|ship| Some((
                                    ship["health"].as_i64()?,
                                    ship["max_health"].as_i64()?,
                                )))
                                .map_or(15, |(health, maximum)|
                                    ((maximum - health).max(0) / 200).min(15));
                            format!("Evacuated {saved}/60  Lost {lost}/15  {:02}:{:02}",
                                seconds / 60, seconds % 60)
                        } else {
                            format!("{} #{} {:02}:{:02}  {:.0}%  Waves {}",
                                if defending { "Protect" } else { "Destroy" },
                                protected, seconds / 60, seconds % 60, percent, waves)
                        }
                    },
                    "demolition" => {
                        let id = objective.get("target_map_object_id")
                            .and_then(Value::as_i64).unwrap_or(0);
                        let attacking = objective.get("attacking_side")
                            .and_then(Value::as_i64) == Some(i64::from(side));
                        let target = state["obstacles"].as_array().into_iter().flatten()
                            .find(|item| item["id"].as_i64() == Some(id));
                        let health = target.and_then(|item|
                            Some((item["health"].as_i64()?, item["max_health"].as_i64()?)))
                            .map(|(hp,max)| format!("  {hp}/{max} HP")).unwrap_or_default();
                        let name = if target.is_some_and(|item| item["map_object_kind"].as_u64()
                            == Some(bees_sim::map_object::MapObjectKind::FireTank as u64)) {
                            "Fire Tank"
                        } else { "object" };
                        format!("{} {name} #{}{}  {:02}:{:02}",
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
    if let Some(state)=state {
        draw_campaign_dialogue(pixels,state,dialogue_line);
        if state["briefing_paused"]==true {
            let is_owner=state["briefing_requires_ack"]==true;
            let line_count=state["campaign_dialogue"]["line_count"]
                .as_u64().unwrap_or(0);
            let message=if is_owner && dialogue_line<(line_count as usize) {
                "CAMPAIGN BRIEFING - complete dialogue to begin"
            }else if is_owner {
                "CAMPAIGN BRIEFING - waiting for other connected commanders"
            }else {
                "CAMPAIGN BRIEFING - waiting for the mission commander"
            };
            text::draw_at(pixels,WIDTH,16,HEIGHT-44,
                text::fit(message,FIELD-32),1,[255,221,151]);
        }
    }
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
                TargetMode::Patrol=>"Drag the PATROL area, Esc cancels",
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
    // Resolve eligible targets before comparing hull distance. A friendly
    // ship over a mining rock or a non-healer in front of a Beehive must
    // not mask the explicit command's actual target. Context clicks follow
    // the same Attack -> Retreat -> Heal -> Mining priority as local play.
    let pick = |target_mode| {
        hit(state, camera, x, y, &peer.visual, peer.visual.fraction(),
            |ship| match target_mode {
                Some(TargetMode::Attack | TargetMode::Circle | TargetMode::RightSwipe |
                    TargetMode::LeftSwipe | TargetMode::InAndOut) => ship["friendly"] == false,
                Some(TargetMode::Guard) => ship["friendly"] == true,
                Some(TargetMode::Heal) => ship["friendly"] == true &&
                    ship["ship_type"].as_u64() == Some(u64::from(bees_sim::combat_world::BEEHIVE_TYPE)),
                Some(TargetMode::Retreat) => ship["friendly"] == true &&
                    ship["ship_type"].as_u64() == Some(u64::from(bees_sim::combat_world::WARP_GATE_TYPE)),
                _ => false,
            },
            |object| match target_mode {
                Some(TargetMode::Mine) => object["mining"] == true,
                None => state["campaign_objective"]["kind"] == "demolition" &&
                    state["campaign_objective"]["attacking_side"].as_i64() == Some(i64::from(peer.side)) &&
                    object["id"] == state["campaign_objective"]["target_map_object_id"],
                _ => false,
            })
    };
    let (mode, hit) = if mode.is_none() {
        [TargetMode::Attack, TargetMode::Retreat, TargetMode::Heal, TargetMode::Mine]
            .into_iter().find_map(|candidate| pick(Some(candidate)).map(|target| (Some(candidate), Some(target))))
            .unwrap_or_else(|| (None, pick(None)))
    } else { (mode, pick(mode)) };
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
        (Some(TargetMode::Guard),Some(("ship",target))) if target["friendly"]==true => {
            let Some(squad)=squad_for_ship(state,target["id"].as_i64().unwrap_or(-1),
                target["side"].as_i64().unwrap_or(0)) else {
                return Err(invalid("friendly target has no visible squad").into())
            };
            ("support",json!({"action":{"Guard":{"target":squad["owner"]}}}))
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
            // Send the clicked identity only. The host owns live squad
            // expansion, source ordering and healing reservations.
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

/// The session ID identifies the original authoritative World across
/// transport and client-process restarts. The token remains exclusively
/// in the existing protected credential file.
fn saved_remote_session(
    path: &Path, address: &str, side: i32,
) -> Result<Option<String>, Box<dyn Error>> {
    let metadata = match fs::symlink_metadata(path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(error.into()),
    };
    if !metadata.file_type().is_file() || metadata.len() > 1024 {
        return Err(invalid("saved multiplayer session must be a regular file").into());
    }
    let value: Value = serde_json::from_slice(&fs::read(path)?)?;
    if value["schema_version"] != 1 ||
        value["kind"] != "native_multiplayer_session" ||
        value["address"].as_str().is_none() ||
        value["side"].as_i64().is_none() ||
        value["host_session_id"].as_str().is_none_or(|id|
            id.len() != 32 || !id.bytes().all(|byte|
                byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte)))
    {
        return Err(invalid("invalid saved native multiplayer session identity").into());
    }
    if value["address"].as_str() != Some(address) ||
        value["side"].as_i64() != Some(i64::from(side))
    {
        return Ok(None);
    }
    Ok(value["host_session_id"].as_str().map(str::to_owned))
}

fn persist_remote_session(
    path: &Path, address: &str, side: i32, session_id: &str,
) -> Result<(), Box<dyn Error>> {
    if session_id.len() != 32 || !session_id.bytes().all(|byte|
        byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte)) {
        return Err(invalid("invalid native host session identity").into());
    }
    let parent = path.parent().ok_or_else(||
        invalid("saved multiplayer session has no parent"))?;
    fs::create_dir_all(parent)?;
    match fs::symlink_metadata(path) {
        Ok(existing) if !existing.file_type().is_file() =>
            return Err(invalid("saved multiplayer session must be a regular file").into()),
        Ok(_) => {},
        Err(error) if error.kind() == ErrorKind::NotFound => {},
        Err(error) => return Err(error.into()),
    }
    let temporary = parent.join(format!(
        ".multiplayer-session-{}-{session_id}.tmp", std::process::id(),
    ));
    let mut file = fs::OpenOptions::new().write(true).create_new(true).open(&temporary)?;
    serde_json::to_writer(&mut file, &json!({
        "schema_version": 1,
        "kind": "native_multiplayer_session",
        "address": address,
        "side": side,
        "host_session_id": session_id,
    }))?;
    file.sync_all()?;
    drop(file);
    fs::rename(&temporary, path)?;
    #[cfg(unix)]
    fs::File::open(parent)?.sync_all()?;
    Ok(())
}

pub fn connect_remote(
    address: &str, token_file: &Path, expected_side: i32,
    session_file: Option<&Path>, audio_preferences: Option<&Path>,
    spectator: bool,
) -> Result<(), Box<dyn Error>> {
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
    let saved = session_file.map(|path|
        saved_remote_session(path, address, expected_side)
    ).transpose()?.flatten();
    let original = if let Some(id) = saved {
        // A completed World must remain reachable after the viewer exits.
        // A new match is an explicit separate choice, never an automatic
        // substitution when the original server is unavailable.
        let rows = [
            super::BattleMenuRow {
                title: "Reconnect to previous multiplayer battle",
                status: "  [Original World or committed result]",
                unlocked: true,
            },
            super::BattleMenuRow {
                title: "Join new multiplayer battle",
                status: "  [New host session]",
                unlocked: true,
            },
        ];
        let mut selected = 0;
        match super::select_library(&rows, &mut selected, false)? {
            Some(0) => Some(id),
            Some(1) => None,
            _ => return Ok(()),
        }
    } else { None };
    let mut last_persisted_session = original.clone();
    let mut peer=PeerClient::connect(
        address, token, expected_side, original.as_deref(), spectator,
    )?;
    let window_title = if spectator {
        "Bees | Native Spectator (Read Only)"
    } else { "Bees | Native Multiplayer" };
    let mut window=Window::new(window_title,WIDTH,HEIGHT,
        WindowOptions::default())?;
    window.set_target_fps(30);
    let mut pixels=vec![0;WIDTH*HEIGHT];
    let mut artwork=RemoteArtwork::new()?;
    let mut audio=NativeGameAudio::new();
    if let Some(path) = audio_preferences {
        audio.load_preferences(path.to_path_buf());
    }
    let mut soundtrack_paused=false;
    let mut last_audio_snapshot:Option<(u64,u64,u64)>=None;
    let mut camera:Option<NativeCamera>=None;
    let mut current_episode=(0_u64,0_u64);
    let mut selected:Vec<i64>=Vec::new();
    // Presentation-only spectator camera target. Scoped to the exact
    // authenticated World/episode and never sent to the game or host.
    let mut spectator_follow:Option<(u64,u64,i64)>=None;
    // One actual selection receipt per source lifetime change, not per
    // network snapshot or pointer hover. Follows the same authenticated
    // input stream as native tactical commands.
    let mut submitted_selection:Option<(u64,u64,Vec<Value>)>=None;
    let mut mode:Option<TargetMode>=None;
    let mut patrol_start:Option<Vec2>=None;
    let mut last_left=false;
    let mut last_selection_press:Option<Instant>=None;
    let mut double_left_press=false;
    let mut last_right=false;
    let mut right_press:Option<RemoteRightPress>=None;
    let mut minimap_dragging=false;
    // Pure local presentation preference, never sent to the host.
    let mut show_minimap=true;
    // Explicit local override; otherwise use source Stage/user preference.
    let mut edge_scroll_override:Option<bool>=None;
    let mut drag_start:Option<(i32,i32)>=None;
    let mut last_middle:Option<(f32,f32)>=None;
    let mut dialogue_key:Option<(u64,u64,u64)>=None;
    let mut dialogue_line=0usize;
    let mut dialogue_pause_started:Option<((u64,u64,u64),usize,Instant)>=None;
    let mut held_fire_active=false;
    let mut held_fire_target:Option<Vec2>=None;
    let mut show_art=true;
    let mut show_weapon_ranges=false;
    let mut show_diagnostics=false;
    let mut show_controls_help=false;
    let mut surrender_confirmation: Option<(String,u64,u64)> = None;
    let mut show_codex=false;
    let mut show_campaign_rewards=false;
    let mut campaign_rewards_scroll=0_usize;
    let mut codex_seen=[false;bees_sim::ship_assets::SHIP_TYPE_COUNT];
    let mut codex_selected=18_u8;
    // One authenticated host/world/episode owns each discovery set.
    let mut codex_owner:Option<(String,u64,u64)>=None;
    let mut codex_last_tick:Option<u64>=None;
    let mut sent_weapon_overlay: Option<(u64,u64,Vec<Value>)>=None;
    let mut shooting_menu=false;
    let mut shooting_focus=1_u8;
    let mut hud_press:Option<HudAction>=None;
    let mut squad_tab_press:Option<i64>=None;
    // Presentation-only viewport into the authoritative ordered squad
    // roster; no extra mutable copy of squad membership or commands.
    let mut squad_list_offset=0_usize;
    let mut shooting_press:Option<u8>=None;
    let started=Instant::now();
    let mut previous_frame=started;

    while window.is_open() {
        let now=Instant::now();
        let elapsed=now.duration_since(previous_frame).as_secs_f32().min(0.1);
        previous_frame=now;
        if surrender_confirmation.is_none() && window.is_key_pressed(Key::F1, KeyRepeat::No) && !held_fire_active {
            show_codex=false;
            show_campaign_rewards=false;
            show_controls_help=!show_controls_help;
            if show_controls_help {
                // A modal may not continue an old target gesture or turn
                // a subsequent click into an authenticated player order.
                shooting_menu=false;shooting_press=None;
                mode=None;patrol_start=None;drag_start=None;right_press=None;
                hud_press=None;squad_tab_press=None;minimap_dragging=false;
                last_selection_press=None;double_left_press=false;
            }
            last_left=window.get_mouse_down(MouseButton::Left);
            last_right=window.get_mouse_down(MouseButton::Right);
        }
        if surrender_confirmation.is_none() && window.is_key_pressed(Key::F2, KeyRepeat::No) && !held_fire_active {
            show_codex=!show_codex;
            show_campaign_rewards=false;
            show_controls_help=show_codex;
            if show_codex {
                shooting_menu=false;shooting_press=None;
                mode=None;patrol_start=None;drag_start=None;right_press=None;
                hud_press=None;squad_tab_press=None;minimap_dragging=false;
                last_selection_press=None;double_left_press=false;
            }
            last_left=window.get_mouse_down(MouseButton::Left);
            last_right=window.get_mouse_down(MouseButton::Right);
        }
        if surrender_confirmation.is_none() &&
            window.is_key_pressed(Key::F9,KeyRepeat::No) && !held_fire_active
        {
            if show_campaign_rewards {
                show_campaign_rewards=false;
                show_controls_help=false;
            } else if peer.campaign_reward_details.is_empty() || peer.spectator {
                peer.notice="No committed campaign career receipt for this player".into();
            } else {
                show_campaign_rewards=true;
                show_codex=false;
                show_controls_help=true;
                campaign_rewards_scroll=0;
                mode=None;patrol_start=None;drag_start=None;right_press=None;
                shooting_menu=false;shooting_press=None;
                hud_press=None;squad_tab_press=None;minimap_dragging=false;
                last_selection_press=None;double_left_press=false;
            }
            last_left=window.get_mouse_down(MouseButton::Left);
            last_right=window.get_mouse_down(MouseButton::Right);
        }
        if show_campaign_rewards {
            let last=peer.campaign_reward_details.len().saturating_sub(18);
            if window.is_key_pressed(Key::Up,KeyRepeat::No) {
                campaign_rewards_scroll=campaign_rewards_scroll.saturating_sub(1);
            } else if window.is_key_pressed(Key::Down,KeyRepeat::No) {
                campaign_rewards_scroll=(campaign_rewards_scroll+1).min(last);
            } else if window.is_key_pressed(Key::PageUp,KeyRepeat::No) {
                campaign_rewards_scroll=campaign_rewards_scroll.saturating_sub(18);
            } else if window.is_key_pressed(Key::PageDown,KeyRepeat::No) {
                campaign_rewards_scroll=(campaign_rewards_scroll+18).min(last);
            }
        }
        if show_codex {
            let known=codex_seen.iter().enumerate().filter_map(|(kind, seen)|
                (*seen && kind != usize::from(bees_sim::ship_assets::HUMAN_TARGET_TYPE))
                    .then_some(kind as u8)).collect::<Vec<_>>();
            if !known.is_empty() {
                let focus=known.iter().position(|&kind|kind==codex_selected).unwrap_or(0);
                if window.is_key_pressed(Key::Up,KeyRepeat::No) {
                    codex_selected=known[(focus+known.len()-1)%known.len()];
                } else if window.is_key_pressed(Key::Down,KeyRepeat::No) {
                    codex_selected=known[(focus+1)%known.len()];
                }
            }
        }
        if !show_controls_help && window.is_key_pressed(Key::F6, KeyRepeat::No) {
            if window.is_key_down(Key::LeftShift)||window.is_key_down(Key::RightShift) {
                let authored=peer.state.as_ref().and_then(|s|
                    s["camera_edge_scrolling"].as_bool()).unwrap_or(false);
                let enabled=!edge_scroll_override.unwrap_or(authored);
                edge_scroll_override=Some(enabled);
                peer.notice=if enabled {"Mouse-edge scrolling enabled".into()}
                    else {"Mouse-edge scrolling disabled".into()};
            } else {
                show_art=!show_art;
                peer.notice=if show_art {
                    "Native source sprites enabled".into()
                } else {"Fast geometric display enabled".into()};
            }
        }
        if !show_controls_help && !show_codex && !show_campaign_rewards &&
            window.is_key_pressed(Key::I, KeyRepeat::No) &&
            !window.is_key_down(Key::LeftShift) &&
            !window.is_key_down(Key::RightShift)
        {
            show_minimap=!show_minimap;
            if !show_minimap {minimap_dragging=false;}
            peer.notice=if show_minimap { "Minimap visible".into() }
                else { "Minimap hidden".into() };
        }
        if !show_controls_help && window.is_key_pressed(Key::B, KeyRepeat::No) {
            show_weapon_ranges=!show_weapon_ranges;
            peer.notice=if show_weapon_ranges {
                "Selected weapon ranges enabled".into()
            } else { "Selected weapon ranges hidden".into() };
        }
        if !show_controls_help && window.is_key_pressed(Key::F8, KeyRepeat::No) {
            show_diagnostics=!show_diagnostics;
        }
        if window.is_key_pressed(Key::F7, KeyRepeat::No) {
            peer.notice=if audio.toggle_muted() {
                "Native audio muted".into()
            } else { "Native audio enabled".into() };
        }
        let lower = window.is_key_pressed(Key::F10, KeyRepeat::No);
        let higher = window.is_key_pressed(Key::F11, KeyRepeat::No);
        if lower || higher {
            let music = window.is_key_down(Key::LeftShift) ||
                window.is_key_down(Key::RightShift);
            let level = audio.adjust_levels(music, if higher { 0.1 } else { -0.1 });
            peer.notice = format!("{} volume: {}%",
                if music { "Music" } else { "Effects" },
                (level * 100.0).round() as u32);
        }
        if !show_controls_help && window.is_key_pressed(Key::Space, KeyRepeat::No) {
            soundtrack_paused=!soundtrack_paused;
            audio.set_paused(soundtrack_paused);
            peer.notice=if soundtrack_paused {
                "Soundtrack paused".into()
            } else { "Soundtrack playing".into() };
        }
        // Backspace mirrors the local tactical client's cancellation
        // without closing the battle. Escape still exits only when no
        // targeting or shooting UI is active.
        let cancel = window.is_key_pressed(Key::Backspace, KeyRepeat::No);
        let escape = window.is_key_pressed(Key::Escape, KeyRepeat::No);
        if surrender_confirmation.is_some() && (escape || cancel) {
            surrender_confirmation=None;
        } else if escape && show_controls_help {
            show_controls_help=false;
            show_codex=false;
            show_campaign_rewards=false;
            last_left=window.get_mouse_down(MouseButton::Left);
            last_right=window.get_mouse_down(MouseButton::Right);
        } else if !show_controls_help && (cancel || escape) {
            if shooting_menu {
                shooting_menu=false;shooting_press=None;
            } else if mode.is_some() {
                mode=None;patrol_start=None;drag_start=None;right_press=None;
            } else if escape {break;}
        }
        // Reconnecting is an explicit new authenticated seat, never a
        // replay of commands or a restoration of unacknowledged gestures.
        // Dropping the previous socket makes the authoritative host release
        // the old peer's held-fire weapons at its next fixed-tick boundary.
        let mut rejoined=false;
        if surrender_confirmation.is_none() && !show_controls_help && window.is_key_pressed(Key::F5, KeyRepeat::No) {
            peer.metrics.reconnect_attempts+=1;
            let original = peer.host_session_id.as_deref()
                .or(peer.expected_host_session_id.as_deref());
            match PeerClient::connect(
                address, token, expected_side, original, spectator,
            ) {
                Ok(mut replacement) => {
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
        // Snapshot-scoped UI identity. Keep only selected lifetimes, not a
        // duplicate roster or any remote simulation state.
        let previous_snapshot=peer.metrics.snapshots;
        let previously_selected: HashMap<i64, (i64,u64)> =
            peer.state.as_ref().map(|state|
                selected.iter().filter_map(|&id|
                    selected_squad_lifetime(state,id).map(|owner|(id,owner))
                ).collect()
            ).unwrap_or_default();
        // Reset the old view BEFORE reading the new connection's first
        // welcome/snapshot. Otherwise a fast host can deliver a valid first
        // state that is immediately discarded as stale by this reset.
        if !rejoined {
            let polled_at=Instant::now();
            peer.poll()?;
            let seconds=polled_at.elapsed().as_secs_f64();
            peer.metrics.network_poll_samples+=1;
            peer.metrics.network_poll_seconds+=seconds;
            peer.metrics.network_poll_max_seconds=peer.metrics.network_poll_max_seconds.max(seconds);
        }
        // Codex discovery uses only the already-authorized, fog-filtered
        // network projection. No hidden enemy IDs/classes are requested
        // from the host, and no separate discovery or game engine is run.
        if peer.authenticated {
            if let (Some(session),Some(state)) =
                (peer.host_session_id.as_deref(),peer.state.as_ref())
            {
                if let (Some(world),Some(revision),Some(tick))=(
                    state["world_id"].as_u64(),
                    state["episode_revision"].as_u64(),
                    state["tick"].as_u64(),
                ) {
                    if codex_owner.as_ref().is_none_or(|(previous,id,rev)|
                        previous!=session || *id!=world || *rev!=revision
                    ) {
                        codex_owner=Some((session.to_owned(),world,revision));
                        codex_seen.fill(false);
                        codex_last_tick=None;
                    }
                    if state["campaign_codex_gate"] == true {
                        // Host-authenticated story progression overrides
                        // encounter discovery for campaign battles. A
                        // different faction receives no source-player
                        // Codex classes through its LAN snapshot.
                        codex_seen.fill(false);
                        for item in state["campaign_codex_types"].as_array()
                            .into_iter().flatten()
                        {
                            if let Some(kind) = item.as_u64()
                                .and_then(|kind| usize::try_from(kind).ok())
                                .filter(|&kind| kind < codex_seen.len())
                            {
                                codex_seen[kind] = true;
                            }
                        }
                    } else if codex_last_tick!=Some(tick) {
                        codex_last_tick=Some(tick);
                        for ship in state["ships"].as_array().into_iter().flatten() {
                            if let Some(kind)=ship["ship_type"].as_u64()
                                .and_then(|kind| usize::try_from(kind).ok())
                                .filter(|&kind|kind<codex_seen.len()) {
                                codex_seen[kind]=true;
                            }
                        }
                    }
                }
            }
        }
        // A welcome is the first proof of a real authoritative session.
        // Persist its ID before the viewer can lose the process, so the
        // same game (or its committed terminal receipt) remains reachable.
        if peer.authenticated {
            if let (Some(path), Some(id)) = (session_file, peer.host_session_id.as_deref()) {
                if last_persisted_session.as_deref() != Some(id) {
                    persist_remote_session(path, address, expected_side, id)?;
                    last_persisted_session = Some(id.to_owned());
                }
            }
        }
        if peer.metrics.snapshots!=previous_snapshot {
            if let Some(state)=peer.state.as_ref() {
                selected.retain(|id|
                    previously_selected.get(id).copied().is_some_and(|old|
                        selected_squad_lifetime(state,*id)==Some(old)));
            }
        }
        if rejoined || was_connected && !peer.connected {
            // A new TCP connection has a new peer ID and no retained squad
            // authority. No previous snapshot, camera selection, or
            // manual-fire gesture can cross that authentication boundary.
            peer.state=None;
            sent_weapon_overlay=None;
            peer.visual=RemoteVisual::default();
            selected.clear();
            spectator_follow=None;
            submitted_selection=None;
            mode=None;patrol_start=None;drag_start=None;
            last_left=false;last_right=false;
            last_selection_press=None;double_left_press=false;
            right_press=None;
            hud_press=None;squad_tab_press=None;shooting_press=None;
            shooting_menu=false;
            last_middle=None;minimap_dragging=false;
            camera=None;
            current_episode=(0,0);
            squad_list_offset=0;
            dialogue_key=None;dialogue_line=0;dialogue_pause_started=None;
            last_audio_snapshot=None;
            held_fire_active=false;held_fire_target=None;
            peer.held_fire_begin_request=None;
            peer.held_fire_rejected=false;
            // The remote scene renderer caches death/explosion frames keyed
            // by world identity. A new network session cannot inherit even
            // those cosmetic frames from the previous authoritative host.
            artwork=RemoteArtwork::new()?;
            audio.reset_remote_session();
            if !peer.connected && peer.battle_result_receipt.is_none() &&
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
            let first_presentation=cue.is_some()&&cue!=dialogue_key;
            if cue!=dialogue_key {
                dialogue_key=cue;dialogue_line=0;dialogue_pause_started=None;
            }
            let count=state["campaign_dialogue"]["line_count"]
                .as_u64().unwrap_or(0).min(100) as usize;
            if count>0 {
                // Source campaign dialogue uses K to skip and L to advance.
                // A skip must not activate a Beacon in the same frame.
                dialogue_skip_key=!show_controls_help&&dialogue_line<count &&
                    window.is_key_pressed(Key::K,KeyRepeat::No);
                if dialogue_skip_key {
                    dialogue_line=count;
                } else if !show_controls_help &&
                    (window.is_key_pressed(Key::L,KeyRepeat::No) ||
                    window.is_key_pressed(Key::Enter,KeyRepeat::No)) {
                    dialogue_line=(dialogue_line+1).min(count);
                }
                if !show_controls_help &&
                    window.is_key_pressed(Key::Backspace,KeyRepeat::No)
                {
                    dialogue_line=dialogue_line.saturating_sub(1);
                }
                let pause = serde_json::from_value::<CampaignCue>(
                    state["campaign_dialogue"]["cue"].clone(),
                ).ok().and_then(|cue|
                    super::campaign_dialogue::pause_seconds(cue, dialogue_line));
                if let (Some(key),Some(seconds))=(dialogue_key,pause) {
                    match dialogue_pause_started {
                        Some((prior,index,started))
                            if prior==key && index==dialogue_line => {
                            if now.duration_since(started).as_secs_f32()>=seconds {
                                dialogue_line+=1;
                                dialogue_pause_started=None;
                            }
                        }
                        _ => dialogue_pause_started=Some((key,dialogue_line,now)),
                    }
                } else {
                    dialogue_pause_started=None;
                }
                // The remote viewer hears only the first visible line
                // of a new host-owned dialogue section. No cue leaks to
                // an opposing viewer or plays when the section is skipped.
                if first_presentation&&dialogue_line<count {
                    audio.campaign_dialogue_presented();
                }
            }
            let episode=(state["world_id"].as_u64().unwrap_or(0),
                         state["episode_revision"].as_u64().unwrap_or(0));
            if let Some(map)=bounds(state) {
                if camera.is_none()||episode !=current_episode {
                    let mut fitted=NativeCamera::fit(map,FIELD,HEIGHT);
                    fitted.constrain_source_zoom(camera_zoom_limits(state),HEIGHT);
                    fitted.constrain_to_map(map,FIELD,HEIGHT);
                    camera=Some(fitted);
                    current_episode=episode;
                    squad_list_offset=0;
                    selected.clear();
                    submitted_selection=None;
                    mode=None;
                    patrol_start=None;
                    drag_start=None;
                    minimap_dragging=false;
                    right_press=None;
                    last_selection_press=None;double_left_press=false;
                    hud_press=None;squad_tab_press=None;shooting_press=None;
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
        // Keep the roster viewport valid after physical squad losses.
        let friendly_count=peer.state.as_ref().and_then(|state|state["squads"].as_array())
            .map_or(0,|squads|squads.iter().filter(|s|s["owned"]==true).count());
        squad_list_offset=squad_list_offset.min(friendly_count.saturating_sub(12));
        let terminal=peer.battle_result_receipt.is_some() ||
            peer.state.as_ref().is_some_and(|state|
                state["terminal_outcome"].is_object());
        let story_briefing=peer.state.as_ref().and_then(|state| {
            let cue=serde_json::from_value::<CampaignCue>(
                state["campaign_dialogue"]["cue"].clone()).ok()?;
            if !cue.is_briefing(){return None;}
            Some((state["world_id"].as_u64()?,
                state["episode_revision"].as_u64()?,
                u32::try_from(state["campaign_dialogue"]["sequence"].as_u64()?).ok()?))
        });
        let briefing=story_briefing.is_some() &&
            peer.state.as_ref().is_some_and(|state|
                state["campaign_dialogue"]["line_count"].as_u64()
                    .is_some_and(|count|dialogue_line<count as usize));
        // Local+LAN hosting already has a local window that paces its
        // authored briefing. Only dedicated hosts request a remote ACK,
        // explicitly marked on the side-filtered authoritative snapshot.
        let ack_required=peer.state.as_ref()
            .is_some_and(|state|state["briefing_requires_ack"]==true) &&
            story_briefing.is_some();
        // A completed K/L dialogue is not an accepted host release until
        // its separate authenticated acknowledgment returns. This prevents
        // gameplay inputs racing the paused server's final briefing tick.
        if ack_required&&!briefing&&!terminal&&!peer.spectator {
            if let Some((world,revision,sequence))=story_briefing {
                let pending=peer.briefing_pending.is_some_and(|(w,r,s,_)|
                    (w,r,s)==(world,revision,sequence));
                if peer.briefing_approved!=story_briefing &&
                    peer.briefing_rejected!=story_briefing && !pending &&
                    peer.connected && peer.authenticated
                {
                    if let Err(error)=peer.briefing_ready(world,revision,sequence) {
                        peer.notice=error.to_string();
                    }
                }
            }
        }
        // The owner and the opposing player remain unable to act until
        // the host has advanced past the same introductory World tick.
        // An acknowledged cue alone does not authorize early commands:
        // its reliable reply may precede the next lossy World snapshot.
        let host_briefing_paused=peer.state.as_ref()
            .is_some_and(|state|state["briefing_paused"]==true);
        let surrender_owner = if !terminal && !briefing && !host_briefing_paused &&
            !peer.spectator && !show_controls_help && !held_fire_active &&
            peer.connected && peer.authenticated {
            peer.state.as_ref().and_then(|state| {
                if state["can_surrender"] != true { return None; }
                Some((peer.host_session_id.clone()?, state["world_id"].as_u64()?,
                    state["episode_revision"].as_u64()?))
            })
        } else { None };
        peer.input_suppressed=peer.spectator||terminal||briefing||
            host_briefing_paused||show_controls_help;
        if !peer.input_suppressed && !held_fire_active &&
            peer.connected && peer.authenticated && window.is_active() &&
            window.is_key_pressed(Key::F3, KeyRepeat::No)
        {
            if let Some(state) = peer.state.as_ref() {
                if state["can_activate_retreat"] == true {
                    let action = json!({
                        "kind": "activate_retreat",
                        "input": {
                            "world_id": state["world_id"],
                            "episode_revision": state["episode_revision"],
                            "side": expected_side,
                        },
                    });
                    if let Err(error) = peer.send_action(
                        action, "Retreat activation submitted".into(),
                    ) {
                        peer.notice = error.to_string();
                    }
                }
            }
        }
        if let Some(owner) = surrender_confirmation.as_ref() {
            if surrender_owner.as_ref() != Some(owner) ||
                window.is_key_pressed(Key::N,KeyRepeat::No) {
                surrender_confirmation=None;
            } else if window.is_key_pressed(Key::Y,KeyRepeat::No) {
                let (_,world,revision)=owner.clone();
                surrender_confirmation=None;
                let action=json!({"kind":"surrender","input":{
                    "world_id":world,"episode_revision":revision,"side":expected_side
                }});
                if let Err(error)=peer.send_action(action,"Surrender submitted".into()) {
                    peer.notice=error.to_string();
                }
            }
        } else if window.is_active() && window.is_key_pressed(Key::F4,KeyRepeat::No) {
            surrender_confirmation=surrender_owner;
        }
        peer.input_suppressed |= surrender_confirmation.is_some();
        if peer.input_suppressed {
            // A completed battle has no further physical input boundary,
            // whereas a story briefing pauses only this commander's UI.
            // Release an existing held-fire lease during a briefing using
            // the original authenticated End input; never send Aim from a
            // terminal overlay over a stale World snapshot.
            if briefing&&!terminal&&held_fire_active {
                if let Err(error)=peer.held_fire(&selected,"End",None) {
                    let _=peer.socket.shutdown(std::net::Shutdown::Both);
                    peer.connected=false;
                    peer.notice=error.to_string();
                }
            }
            held_fire_active=false;
            held_fire_target=None;
            mode=None;
            patrol_start=None;
            drag_start=None;
            right_press=None;
        }
        if peer.input_suppressed||selected.is_empty() {
            shooting_menu=false;shooting_press=None;
            mode=None;patrol_start=None;drag_start=None;
        }
        if !window.is_active() {
            drag_start=None;patrol_start=None;right_press=None;
            hud_press=None;squad_tab_press=None;shooting_press=None;
            minimap_dragging=false;
        }
        if !show_controls_help && surrender_confirmation.is_none() {
        if let Some(mut cam)=camera {
            let authored_zoom=peer.state.as_ref().and_then(camera_zoom_limits);
            let keys=window.get_keys_pressed(KeyRepeat::No);
            let shift=window.is_key_down(Key::LeftShift)||
                window.is_key_down(Key::RightShift);
            let control=window.is_key_down(Key::LeftCtrl)||
                window.is_key_down(Key::RightCtrl);
            for key in keys {
                if shooting_menu {
                    let current=usize::from(shooting_focus.saturating_sub(1));
                    let next=match key {
                        Key::Up=>Some(current/10*10+(current%10+9)%10),
                        Key::Down=>Some(current/10*10+(current%10+1)%10),
                        Key::Left=>Some((current+30)%40),
                        Key::Right=>Some((current+10)%40),
                        _=>None,
                    };
                    if let Some(index)=next {shooting_focus=(index+1) as u8;}
                    if key==Key::Enter {
                        match peer.order(&selected,"shooting",
                            json!({"strategy_id":shooting_focus})) {
                            Ok(())=>{
                                shooting_menu=false;shooting_press=None;
                                peer.metrics.shooting_strategy_choices+=1;
                            },
                            Err(error)=>peer.notice=error.to_string(),
                        }
                    } else if matches!(key,Key::Backspace|Key::S) {
                        shooting_menu=false;shooting_press=None;
                    }
                    continue;
                }
                // The unmodified I key is a display toggle, not a squad
                // command; Shift+I remains the Circle tactical shortcut.
                if key==Key::I && !shift { continue; }
                let numbered=match key {
                    Key::Key1=>Some(1),Key::Key2=>Some(2),Key::Key3=>Some(3),
                    Key::Key4=>Some(4),Key::Key5=>Some(5),Key::Key6=>Some(6),
                    Key::Key7=>Some(7),Key::Key8=>Some(8),Key::Key9=>Some(9),
                    Key::Key0=>Some(0),_=>None,
                };
                if numbered.is_some()||key==Key::Tab||
                    (!shift&&matches!(key,Key::PageUp|Key::PageDown)) {
                    if held_fire_active { continue; }
                    if let Some(state)=peer.state.as_ref() {
                        let next=numbered.and_then(|number|numbered_squad(state,number))
                            .or_else(||(key==Key::Tab||key==Key::PageDown||key==Key::PageUp)
                                .then(||cycle_numbered_squad(state,&selected,
                                    key!=Key::PageUp)).flatten());
                        if let Some(id)=next {
                            selected.clear();selected.push(id);
                            // Numbered and Tab selection can target any
                            // live squad, including those outside the first
                            // twelve rows. Reveal its real roster row without
                            // changing the authoritative selection ordering.
                            if let Some(index)=state["squads"].as_array().into_iter()
                                .flatten().filter(|s|s["owned"]==true)
                                .position(|s|s["owner"]["squad_id"].as_i64()==Some(id)) {
                                if index<squad_list_offset {
                                    squad_list_offset=index;
                                } else if index>=squad_list_offset+12 {
                                    squad_list_offset=index+1-12;
                                }
                            }
                            if let Some(point)=focus_squad(state,id){cam.center=point;}
                            mode=None;patrol_start=None;drag_start=None;
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
                        cam.constrain_source_zoom(authored_zoom,HEIGHT);
                        cam.constrain_to_map(map,FIELD,HEIGHT);
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
                    drag_start=None;patrol_start=None;
                    if let Err(error)=remote_tactical_action(
                        &mut peer,&mut selected,&mut mode,&mut patrol_start,
                        &mut held_fire_active,&mut held_fire_target,
                        &mut shooting_menu,&mut shooting_focus,action,&mut audio,
                    ) {
                        peer.notice=error.to_string();
                    }
                }
            }
            // Same source-native camera speed in physical pixels/second,
            // independent of the remote window's rendered frame rate.
            let pan=500.0*elapsed/cam.pixels_per_unit;
            if window.is_key_down(Key::Up)||window.is_key_down(Key::Down)||
                window.is_key_down(Key::Left)||window.is_key_down(Key::Right) {
                spectator_follow=None;
            }
            if window.is_key_down(Key::Up){cam.center.y+=pan;}
            if window.is_key_down(Key::Down){cam.center.y-=pan;}
            if window.is_key_down(Key::Left){cam.center.x-=pan;}
            if window.is_key_down(Key::Right){cam.center.x+=pan;}
            let mouse=window.get_mouse_pos(MouseMode::Discard);
            let minimap = show_minimap.then(|| peer.state.as_ref().and_then(bounds)
                .and_then(|map| MiniMapLayout::top_right(FIELD, HEIGHT, map))).flatten();
            let authored_edge=peer.state.as_ref().and_then(|state|
                state["camera_edge_scrolling"].as_bool()).unwrap_or(false);
            if edge_scroll_override.unwrap_or(authored_edge) && window.is_active() &&
                !shooting_menu && !held_fire_active && !show_codex &&
                !show_campaign_rewards
            {
                // Source frame-physical edge panning. Ignore the squad HUD
                // and clickable minimap; neither is part of the battlefield.
                if let Some((x,y))=mouse.filter(|&(x,y)|
                    x>=0.0&&x<FIELD as f32&&y>=0.0&&y<HEIGHT as f32&&
                    !minimap.is_some_and(|layout|layout.world_at(x,y).is_some())) {
                    const EDGE:f32=18.0;
                    let mut horizontal=0.0;
                    let mut vertical=0.0;
                    if x<=EDGE {horizontal-=pan;}
                    else if x>=FIELD as f32-EDGE {horizontal+=pan;}
                    if y<=EDGE {vertical+=pan;}
                    else if y>=HEIGHT as f32-EDGE {vertical-=pan;}
                    if horizontal!=0.0||vertical!=0.0 {
                        spectator_follow=None;
                        cam.center+=Vec2::new(horizontal,vertical);
                        peer.metrics.camera_edge_pan_frames+=1;
                    }
                }
            }
            // Shift+wheel pans horizontally and Ctrl+wheel vertically,
            // matching the local native camera. Unmodified wheel zooms
            // at the pointer or centers on the real host-derived minimap.
            if let Some((horizontal,vertical))=window.get_scroll_wheel() {
                // Wheel over the roster scrolls actual squad rows instead
                // of zooming the unrelated battlefield camera.
                if mouse.is_some_and(|(x,y)|
                    x>=FIELD as f32 && x<WIDTH as f32 && y>=145.0 && y<412.0) {
                    if vertical>0.0 {
                        squad_list_offset=squad_list_offset
                            .saturating_sub(vertical.ceil() as usize);
                    } else if vertical<0.0 {
                        squad_list_offset=squad_list_offset
                            .saturating_add((-vertical).ceil() as usize)
                            .min(friendly_count.saturating_sub(12));
                    }
                    squad_tab_press=None;
                } else {
                spectator_follow=None;
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
                                HEIGHT as f32/2.0,FIELD,HEIGHT,authored_zoom);
                        } else if x>=0.0&&x<FIELD as f32&&y>=0.0&&y<HEIGHT as f32 {
                            cam.zoom_at(factor,x,y,FIELD,HEIGHT,authored_zoom);
                        }
                    }
                }
                cam.center+=wheel_pan;
                }
            }
            if window.is_key_down(Key::Equal){cam.zoom_at(1.035,FIELD as f32/2.0,
                HEIGHT as f32/2.0,FIELD,HEIGHT,authored_zoom);}
            if window.is_key_down(Key::Minus){cam.zoom_at(1.0/1.035,
                FIELD as f32/2.0,HEIGHT as f32/2.0,FIELD,HEIGHT,authored_zoom);}
            if let Some(map)=peer.state.as_ref().and_then(bounds) {
                cam.constrain_to_map(map,FIELD,HEIGHT);
            }
            if shooting_menu {
                // The shared native 40-strategy grid is a modal. A press
                // must be released over its original strategy before
                // sending the existing authenticated shooting input.
                let left=window.get_mouse_down(MouseButton::Left);
                if left&&!last_left&&window.is_active() {
                    shooting_press=mouse.and_then(|(x,y)|
                        shooting_hit(WIDTH,HEIGHT,x,y));
                    if let Some(id)=shooting_press {shooting_focus=id;}
                }
                if !left&&last_left {
                    let hovered=mouse.and_then(|(x,y)|
                        shooting_hit(WIDTH,HEIGHT,x,y));
                    if let Some(id)=shooting_press.take()
                        .filter(|&id|Some(id)==hovered) {
                        match peer.order(&selected,"shooting",
                            json!({"strategy_id":id})) {
                            Ok(())=>{
                                shooting_menu=false;
                                peer.metrics.shooting_strategy_choices+=1;
                            },
                            Err(error)=>peer.notice=error.to_string(),
                        }
                    }
                }
                // Hover never changes selection: keyboard arrows retain
                // their position until a deliberate mouse press.
                last_left=left;
                last_right=window.get_mouse_down(MouseButton::Right);
                hud_press=None;squad_tab_press=None;
                drag_start=None;right_press=None;
                minimap_dragging=false;
                camera=Some(cam);
            } else {
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
                && !peer.input_suppressed && !peer.held_fire_rejected && within.is_some() &&
                (held_fire_active || (!selected.is_empty() &&
                    window.is_key_pressed(Key::J,KeyRepeat::No)));
            match (held_fire_active,holding,within) {
                (false,true,Some((x,y))) => {
                    let point=cam.screen_to_world(x as f32,y as f32,FIELD,HEIGHT);
                    match peer.held_fire(&selected,"Begin",Some(point)) {
                        Ok(()) => {
                            held_fire_active=true;held_fire_target=Some(point);
                            mode=None;patrol_start=None;drag_start=None;right_press=None;
                            minimap_dragging=false;
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
            let battlefield_input = window.is_active() && !peer.input_suppressed && !held_fire_active;
            if left && !last_left && window.is_active() {
                drag_start=None;patrol_start=None;
                hud_press=mouse.and_then(|(x,y)|remote_tactical_hit(x,y));
                squad_tab_press=if hud_press.is_none() {
                    peer.state.as_ref().and_then(|state|
                        mouse.and_then(|(x,y)|remote_squad_tab_hit(state,x,y,squad_list_offset)))
                }else{None};
                minimap_dragging=(battlefield_input||peer.spectator)&&
                    mode.is_none()&&hud_press.is_none()&&
                    squad_tab_press.is_none()&&minimap_point.is_some();
            }
            if !left && last_left {
                let clicked=hud_press.take().filter(|&action|
                    mouse.and_then(|(x,y)|remote_tactical_hit(x,y))==Some(action));
                let clicked_tab=squad_tab_press.take().filter(|&id|
                    peer.state.as_ref().and_then(|state|
                        mouse.and_then(|(x,y)|remote_squad_tab_hit(state,x,y,squad_list_offset)))
                        ==Some(id));
                if let Some(id)=clicked_tab.filter(|_| battlefield_input) {
                    peer.metrics.squad_tab_clicks+=1;
                    audio.click_ui();
                    if control {
                        if let Some(index)=selected.iter().position(|&current|current==id) {
                            selected.remove(index);
                        }else{selected.push(id);}
                    }else{
                        selected.clear();selected.push(id);
                        if let Some(state)=peer.state.as_ref() {
                            if let Some(point)=focus_squad(state,id) {cam.center=point;}
                        }
                    }
                    mode=None;patrol_start=None;
                    last_selection_press=None;double_left_press=false;
                }
                if let Some(action)=clicked {
                    peer.metrics.tactical_button_clicks+=1;
                    audio.click_ui();
                    if let Err(error)=remote_tactical_action(
                        &mut peer,&mut selected,&mut mode,&mut patrol_start,
                        &mut held_fire_active,&mut held_fire_target,
                        &mut shooting_menu,&mut shooting_focus,action,&mut audio,
                    ) {
                        peer.notice=error.to_string();
                    }
                    last_selection_press=None;double_left_press=false;
                }
            }
            if left && minimap_dragging {
                spectator_follow=None;
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
                    spectator_follow=None;
                    cam.center.x-=(x-old_x)/cam.pixels_per_unit;
                    cam.center.y+=(y-old_y)/cam.pixels_per_unit;
                }
                last_middle=mouse;
            }else{last_middle=None;}
            if !left && last_left && peer.spectator &&
                window.is_active() && !minimap_release
            {
                if let (Some((x,y)),Some(state))=(within,peer.state.as_ref()) {
                    // Spectators can inspect only ships in the current
                    // host-fog-filtered physical projection. This does
                    // not create a squad selection or player input.
                    let target=hit(state,cam,x,y,&peer.visual,
                        peer.visual.fraction(),|_|true,|_|false);
                    spectator_follow=target.and_then(|(kind,ship)|
                        (kind=="ship").then(||Some((
                            state["world_id"].as_u64()?,
                            state["episode_revision"].as_u64()?,
                            ship["id"].as_i64()?,
                        ))).flatten()
                    );
                    peer.notice=if let Some((_,_,id))=spectator_follow {
                        format!("Following visible ship {id}; pan to release")
                    } else {"Spectator camera unlocked".to_owned()};
                }
            }
            if let Some((x,y))=mouse {
                let (x,y)=(x as i32,y as i32);
                if battlefield_input&&left&&!last_left&&!minimap_dragging&&
                    hud_press.is_none()&&squad_tab_press.is_none()&&within.is_some() {
                    // Source double-click is a second completed selection
                    // press within 250 ms. The release still must be prompt;
                    // a long drag cannot turn into a ship-type selection.
                    double_left_press=mode.is_none()&&last_selection_press.is_some_and(|previous|
                        now.duration_since(previous)<=Duration::from_millis(250));
                    last_selection_press=mode.is_none().then_some(now);
                    drag_start=Some((x,y));
                    if mode == Some(TargetMode::Patrol) {
                        patrol_start=Some(cam.screen_to_world(x as f32,y as f32,FIELD,HEIGHT));
                    }
                }
                if battlefield_input&&!left&&last_left&&!minimap_release&&within.is_some() {
                    if mode.is_some() && drag_start.take().is_some() {
                        if mode == Some(TargetMode::Patrol) {
                            if let Some(first)=patrol_start.take() {
                                let point=cam.screen_to_world(x as f32,y as f32,FIELD,HEIGHT);
                                let fields=json!({"action":{"Patrol":{
                                    "top_left":{"x":first.x.min(point.x),
                                                "y":first.y.max(point.y)},
                                    "bottom_right":{"x":first.x.max(point.x),
                                                    "y":first.y.min(point.y)}
                                }}});
                                if let Err(error)=peer.order(&selected,"support",fields) {
                                    peer.notice=error.to_string();
                                } else { mode=None; }
                            }
                        } else if let Err(error)=order_at(&mut peer,&selected,cam,x,y,mode) {
                            peer.notice=error.to_string();
                        } else {mode=None;}
                    } else if mode.is_none() {
                        if let Some((sx,sy))=drag_start.take() {
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
                                let rectangle=Aabb {
                                    min:cam.screen_to_world(
                                        sx.min(x) as f32,sy.max(y) as f32,FIELD,HEIGHT),
                                    max:cam.screen_to_world(
                                        sx.max(x) as f32,sy.min(y) as f32,FIELD,HEIGHT),
                                };
                                for ship in state["ships"].as_array().into_iter().flatten(){
                                    if ship["owned"]!=true {continue;}
                                    let Some(p)=peer.visual.position(
                                        ship,0,peer.visual.fraction(),
                                    ) else {continue;};
                                    if remote_primary_hull(
                                        ship,p,peer.visual.heading_for(
                                            ship,0,peer.visual.fraction()),
                                    ).is_some_and(|hull|hull.overlaps_box(rectangle)) {
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
                                |_| true, |_| true,
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
            }
            // A release outside the captured battlefield (including HUD,
            // minimap or window) never carries a corner into a later drag.
            if !left { drag_start=None;patrol_start=None; }
            if !battlefield_input { right_press=None; }
            // Capture the origin of a right-button gesture. Its minimap
            // and World coordinates must never swap halfway through a drag.
            // A right-click while aiming a support order cancels that mode,
            // as in the source local client, without issuing a new command.
            if battlefield_input&&right&&!last_right {
                if mode.is_some() {
                    mode=None;patrol_start=None;drag_start=None;right_press=None;
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
            if let Some((world,revision,id))=spectator_follow {
                let current=peer.state.as_ref().filter(|state|
                    state["world_id"].as_u64()==Some(world) &&
                    state["episode_revision"].as_u64()==Some(revision));
                let ship=current.and_then(|state|
                    state["ships"].as_array()?.iter().find(|ship|
                        ship["id"].as_i64()==Some(id)));
                if let Some(point)=ship.and_then(|ship|
                    peer.visual.position(ship,0,peer.visual.fraction())) {
                    cam.center=point;
                    if let Some(map)=current.and_then(bounds) {
                        cam.constrain_to_map(map,FIELD,HEIGHT);
                    }
                } else {
                    spectator_follow=None;
                }
            }
            last_left=left;last_right=right;
            camera=Some(cam);
            }
        }
        } else {
            // Remember button states but do not interpret any held gesture
            // as a new press when the overlay is dismissed.
            last_left=window.get_mouse_down(MouseButton::Left);
            last_right=window.get_mouse_down(MouseButton::Right);
        }
        // Unlike tactical hover/box preview, a real user selection is a
        // checkpointed game input because original Pluto II requires the
        // actual first Scout selection to unlock the combat tutorial.
        // Publish only authenticated live source lifetimes (never numeric
        // squad IDs alone), and only after the host briefing releases.
        if peer.connected && peer.authenticated && !peer.input_suppressed {
            if let Some(state)=peer.state.as_ref() {
                let request=(
                    state["world_id"].as_u64().unwrap_or(0),
                    state["episode_revision"].as_u64().unwrap_or(0),
                    PeerClient::selected_owners(state,&selected),
                );
                if submitted_selection.as_ref()!=Some(&request) {
                    if request.2.is_empty() && submitted_selection.is_none() {
                        submitted_selection=Some(request);
                    } else {
                        let action=json!({
                            "kind":"select",
                            "input":{
                                "world_id":request.0,
                                "episode_revision":request.1,
                                "squads":&request.2,
                            }
                        });
                        match peer.send_action(action,"Squad selection updated".into()) {
                            Ok(())=>submitted_selection=Some(request),
                            Err(error)=>peer.notice=error.to_string(),
                        }
                    }
                }
            }
        }
        // Interest changes only with the selected live lifetimes, toggle,
        // or episode. It never enters the authoritative input trace.
        if peer.connected && peer.authenticated && !peer.spectator {
            if let Some(state)=peer.state.as_ref() {
                let request=(
                    state["world_id"].as_u64().unwrap_or(0),
                    state["episode_revision"].as_u64().unwrap_or(0),
                    if show_weapon_ranges && !peer.input_suppressed {
                        PeerClient::selected_owners(state,&selected)
                    } else { Vec::new() },
                );
                if sent_weapon_overlay.as_ref()!=Some(&request) {
                    if request.2.is_empty() &&
                        sent_weapon_overlay.as_ref().is_none_or(|previous|previous.2.is_empty())
                    {
                        sent_weapon_overlay=Some(request);
                    } else {
                        let frame=json!({"kind":"weapon_overlay","world_id":request.0,
                            "episode_revision":request.1,"squads":&request.2});
                        let notice=peer.notice.clone();
                        match peer.enqueue_request(frame,notice) {
                            Ok(())=>sent_weapon_overlay=Some(request),
                            Err(error)=>peer.notice=error.to_string(),
                        }
                    }
                }
            }
        }
        let visual_fraction = peer.visual.fraction();
        if visual_fraction < 1.0 && !peer.visual.previous.is_empty() {
            peer.metrics.interpolated_draw_frames += 1;
        }
        let artwork_started=Instant::now();
        if let (Some(state),Some(cam))=(peer.state.as_ref(),camera) {
            artwork.paint(state,cam,&peer.visual,visual_fraction,
                &mut peer.metrics,show_art);
        }
        let artwork_seconds=artwork_started.elapsed().as_secs_f64();
        peer.metrics.artwork_seconds+=artwork_seconds;
        peer.metrics.artwork_max_seconds=peer.metrics.artwork_max_seconds.max(artwork_seconds);
        let mut hulls = 0_u64;
        let mut map_object_health_bars = 0_u64;
        let mut map_ships = 0_u64;
        let mut map_terrain = 0_u64;
        let mut map_objectives = 0_u64;
        let mut fog_masked = 0_u64;
        let mut combat_hud_draws = [0_u64; 7];
        let hud_started=Instant::now();
        draw(&mut pixels,peer.state.as_ref(),camera,&selected,squad_list_offset,&peer.notice,
            expected_side,mode,patrol_start,&peer.metrics,dialogue_line,
            held_fire_active,shooting_menu,shooting_focus,
            &peer.visual,visual_fraction,&artwork,show_art,show_weapon_ranges,
            show_minimap,
            &mut combat_hud_draws, &mut hulls, &mut map_object_health_bars,
            &mut map_ships, &mut map_terrain, &mut map_objectives,
            &mut fog_masked);
        peer.metrics.map_object_health_bars += map_object_health_bars;
        peer.metrics.weapon_range_draws += combat_hud_draws[0];
        peer.metrics.weapon_charge_draws += combat_hud_draws[1];
        peer.metrics.weapon_aim_draws += combat_hud_draws[2];
        peer.metrics.command_target_draws += combat_hud_draws[3];
        peer.metrics.striker_bomb_indicator_draws += combat_hud_draws[4];
        peer.metrics.weapon_contact_draws += combat_hud_draws[5];
        peer.metrics.weapon_obstructed_contact_draws += combat_hud_draws[6];
        // The local client shows the selection box while dragging. Draw
        // only the current screen-space intent; the real ship collisions
        // are evaluated from the host-supplied hulls on release.
        if !shooting_menu && (mode.is_none() || mode == Some(TargetMode::Patrol)) &&
            !minimap_dragging && window.is_active() && !held_fire_active &&
            window.get_mouse_down(MouseButton::Left)
        {
            if let (Some((sx,sy)),Some((mx,my)))=
                (drag_start,window.get_mouse_pos(MouseMode::Discard))
            {
                let (x,y)=(mx as i32,my as i32);
                // Patrol's first corner is in World coordinates, so zoom
                // or camera movement during the drag cannot move it.
                let (sx,sy)=patrol_start.zip(camera)
                    .map_or((sx,sy), |(point,cam)| screen(cam,point));
                if x>=0&&x<FIELD as i32&&y>=0&&y<HEIGHT as i32 &&
                    (x-sx).pow(2)+(y-sy).pow(2)>16
                {
                    outline(&mut pixels,sx.min(x),sy.min(y),
                        (sx-x).abs(),(sy-y).abs(),0xe2c473);
                    peer.metrics.selection_drag_preview_frames+=1;
                }
            }
        }
        peer.metrics.fog_masked_pixels += fog_masked;
        peer.metrics.obstacle_hull_draws += hulls;
        peer.metrics.minimap_ship_markers += map_ships;
        peer.metrics.minimap_terrain_markers += map_terrain;
        peer.metrics.minimap_objective_markers += map_objectives;
        // A terminal outcome is reliable and survives transport closure,
        // even if the replaceable final world_state could not drain.
        if let Some(result) = peer.battle_result_receipt.as_deref() {
            text::draw_at(&mut pixels, WIDTH, 10, HEIGHT-61,
                text::fit(result, FIELD-20), 1, [255, 211, 130]);
            // Up to 24 source ship classes form at most five rows of five.
            // Preserve the complete terminal casualty report above the
            // reliable battle and campaign receipts.
            for (index, line) in peer.battle_loss_breakdown.iter().rev().take(5).enumerate() {
                text::draw_at(&mut pixels, WIDTH, 10, HEIGHT-79-index*18,
                    text::fit(line, FIELD-20), 1, [255, 211, 130]);
            }
        }
        // The authoritative commit receipt outlives the last world frame and
        // socket close. A transport status line cannot hide saved progression.
        if let Some(receipt) = peer.campaign_receipt.as_deref() {
            text::draw_at(&mut pixels, WIDTH, 10, HEIGHT-43,
                text::fit(receipt, FIELD-20), 1, [255, 211, 130]);
        }
        if let Some((_,_,id))=spectator_follow {
            text::draw_at(&mut pixels,WIDTH,12,53,
                &format!("Spectator camera following visible ship #{id}"),
                1,[198,231,248]);
        }
        if surrender_confirmation.is_some() {
            NativeSoftwareRenderer::draw_surrender_panel(&mut pixels,WIDTH,HEIGHT);
        } else if show_campaign_rewards && !peer.campaign_reward_details.is_empty() {
            let total=peer.campaign_reward_details.len();
            let start=campaign_rewards_scroll.min(total.saturating_sub(18));
            let end=(start+18).min(total);
            let counter=format!("Committed lines {}–{} of {}",start+1,end,total);
            let mut rows=vec![
                "SAVED CAMPAIGN RESULT / FLEET AWARDS",
                "F9 / Esc close  |  Up/Down/PgUp/PgDn scroll",
                counter.as_str(),
                "",
            ];
            rows.extend(peer.campaign_reward_details[start..end]
                .iter().map(String::as_str));
            NativeSoftwareRenderer::draw_help_panel(
                &mut pixels,WIDTH,HEIGHT,&rows,&[1,2],
            );
        } else if show_codex {
            let catalog=source_ship_catalog();
            let known=codex_seen.iter().enumerate()
                .filter_map(|(kind,seen)| (*seen &&
                    kind!=usize::from(bees_sim::ship_assets::HUMAN_TARGET_TYPE))
                    .then_some(kind as u8)).collect::<Vec<_>>();
            let focus=known.iter().position(|&kind|kind==codex_selected).unwrap_or(0);
            let start=(focus/13)*13;
            let mut lines=vec![
                "UNITED FLEET CODEX".to_owned(),
                "F2 / Esc close   Up / Down browse".to_owned(),
                "Only source ship classes visible to this commander".to_owned(),
                " ".to_owned(),
            ];
            for &kind in known.iter().skip(start).take(13) {
                let name=catalog.by_type(kind).map(|asset|asset.name.as_str())
                    .unwrap_or("Unknown ship");
                lines.push(format!("{} {}",if kind==codex_selected {">"} else {" "},name));
            }
            if known.is_empty() {
                lines.push("No enemy or friendly ship classes discovered".to_owned());
            }
            let sample=peer.state.as_ref().and_then(|state|
                state["ships"].as_array()).and_then(|ships|ships.iter().find(|ship|
                    ship["ship_type"].as_u64()==Some(u64::from(codex_selected))
                )).map(|ship| {
                format!("Live hull: {} / {} HP",
                    ship["health"].as_i64().unwrap_or(0),
                    ship["max_health"].as_i64().unwrap_or(0))
            }).unwrap_or_else(||"No currently visible physical hull".to_owned());
            lines.push(" ".to_owned());
            lines.push(sample);
            let refs=lines.iter().map(String::as_str).collect::<Vec<_>>();
            NativeSoftwareRenderer::draw_help_panel(
                &mut pixels,WIDTH,HEIGHT,&refs,&[1,3],
            );
        } else if show_controls_help {
            const LINES: &[&str] = &[
                "BEES NATIVE / MULTIPLAYER CONTROLS",
                "F1 or Esc: close help   F2: United Fleet Codex",
                "F9: inspect the authenticated committed campaign rewards",
                "SELECTION",
                "Left click: ship   Drag: box select   Double click: ship type",
                "Ctrl+click: add/remove   Ctrl+A: all   1-0: numbered squads",
                "Tab / PgUp / PgDn: cycle original squads",
                "CAMERA / DISPLAY",
                "Right click or drag: move / attack   Arrows: pan   Wheel: zoom",
                "F: fit   B: ranges   I: map   F6: art   Shift+F6: edge pan",
                "Shift+wheel: horizontal pan   Ctrl+wheel: vertical pan",
                "COMMANDS",
                "C: hold   A: attack on sight   V: cease fire   E: chase",
                "G: guard   P: patrol   M: mine   H: heal   R: retreat",
                "S: shooting priority   O: lock   Y: match   U: free speed",
                "K: beacon   D: detonate   Q: charge",
                "T: point fire   Hold J: manual fire   X: stop fire",
                "Backspace: cancel target   Shift+T: attack ship",
                "NETWORK / AUDIO",
                "F4: surrender   F5: reconnect   F8: network measurements",
                "F7: mute   F10/F11: effects   Shift+F10/F11: music",
                "Space: pause local soundtrack (not remote battle)",
            ];
            if peer.spectator {
                // The observer may navigate the native camera, artwork,
                // local soundtrack and diagnostics, but no control exists
                // to arm a source command or release a campaign briefing.
                const OBSERVER_HELP: &[&str] = &[
                    "AUTHENTICATED SPECTATOR - READ ONLY",
                    "Side-filtered battlefield and original fog of war",
                    "CAMERA / DISPLAY",
                    "Click a visible ship: follow it  |  Pan: stop following",
                    "Click or drag minimap: reposition camera",
                    "Arrows: pan   Wheel: zoom   F: fit map",
                    "Shift+wheel: horizontal pan   Ctrl+wheel: vertical pan",
                    "B: weapon ranges   F6: source art / fast view",
                    "NETWORK / AUDIO",
                    "F5: reconnect   F8: network measurements",
                    "F7: mute   F10/F11: effects   Shift+F10/F11: music",
                    "Space: pause local soundtrack (not the live battle)",
                    "No fleet orders, surrender or campaign acknowledgments",
                    "Press F1 or Escape to close this panel",
                ];
                NativeSoftwareRenderer::draw_help_panel(
                    &mut pixels, WIDTH, HEIGHT, OBSERVER_HELP, &[2,6,10],
                );
            } else {
                NativeSoftwareRenderer::draw_help_panel(
                    &mut pixels, WIDTH, HEIGHT, LINES, &[2, 6, 10, 17],
                );
            }
        }
        if show_diagnostics && !show_controls_help {
            let frames=peer.metrics.frames_rendered.max(1) as f64;
            let polls=peer.metrics.network_poll_samples.max(1) as f64;
            let uptime=started.elapsed().as_secs_f64().max(0.001);
            let latency=if peer.metrics.order_ack_latency_samples>0 {
                format!("{:.1}",peer.metrics.order_ack_latency_sum_seconds*1000.0/
                    peer.metrics.order_ack_latency_samples as f64)
            } else {"n/a".to_owned()};
            let lines=[
                format!("F8 metrics | {:.1} FPS | {:.1} KiB/s",frames/uptime,
                    peer.metrics.bytes_received as f64/1024.0/uptime),
                format!("Art {:.2} ms | HUD {:.2} ms | present {:.2} ms",
                    peer.metrics.artwork_seconds*1000.0/frames,
                    peer.metrics.hud_seconds*1000.0/frames,
                    peer.metrics.present_seconds*1000.0/frames),
                format!("Network poll {:.2} ms | order ack {} ms | pending {}",
                    peer.metrics.network_poll_seconds*1000.0/polls,latency,
                    peer.pending_order_times.len()),
            ];
            for (index,line) in lines.iter().enumerate() {
                text::draw_at(&mut pixels,WIDTH,12,14+index*16,
                    line,1,TEXT);
            }
        }
        let hud_seconds=hud_started.elapsed().as_secs_f64();
        peer.metrics.hud_seconds+=hud_seconds;
        peer.metrics.hud_max_seconds=peer.metrics.hud_max_seconds.max(hud_seconds);
        let present_started=Instant::now();
        window.update_with_buffer(&pixels,WIDTH,HEIGHT)?;
        let present_seconds=present_started.elapsed().as_secs_f64();
        peer.metrics.present_seconds+=present_seconds;
        peer.metrics.present_max_seconds=peer.metrics.present_max_seconds.max(present_seconds);
        peer.metrics.frames_rendered += 1;
    }
    let session_seconds = started.elapsed().as_secs_f64();
    println!("{}",json!({
        "kind":"native_multiplayer_client_final",
        "side":expected_side,
        "session_wall_seconds":session_seconds,
        "bytes_received":peer.metrics.bytes_received,
        "world_snapshot_bytes":peer.metrics.world_snapshot_bytes,
        "world_snapshot_max_bytes":peer.metrics.world_snapshot_max_bytes,
        "network_poll_samples":peer.metrics.network_poll_samples,
        "network_poll_mean_milliseconds":if peer.metrics.network_poll_samples>0 {
            Some(1000.0*peer.metrics.network_poll_seconds/
                peer.metrics.network_poll_samples as f64)
        } else {None},
        "network_poll_max_milliseconds":peer.metrics.network_poll_max_seconds*1000.0,
        "snapshots_rendered":peer.metrics.snapshots,
        "frames_rendered":peer.metrics.frames_rendered,
        "rendered_fps":if session_seconds > 0.0 {
            peer.metrics.frames_rendered as f64 / session_seconds
        } else { 0.0 },
        "snapshot_pairs_interpolated":peer.metrics.snapshot_pairs_interpolated,
        "interpolated_draw_frames":peer.metrics.interpolated_draw_frames,
        "ship_sprite_draws":peer.metrics.ship_sprite_draws,
        "carrier_deck_overlay_draws":peer.metrics.carrier_deck_overlay_draws,
        "barge_charge_effect_draws":peer.metrics.barge_charge_effect_draws,
        "queen_part_sprite_draws":peer.metrics.queen_part_sprite_draws,
        "ship_deaths_received":peer.metrics.ship_deaths_received,
        "combat_impacts_received":peer.metrics.combat_impacts_received,
        "combat_impact_visual_draws":peer.metrics.combat_impact_visual_draws,
        "healing_crosses_received":peer.metrics.healing_crosses_received,
        "healing_crosses_drawn":peer.metrics.healing_crosses_drawn,
        "healing_crosses_dropped":peer.metrics.healing_crosses_dropped,
        "combat_impact_visuals_dropped":peer.metrics.combat_impact_visuals_dropped,
        "ship_remains_draws":peer.metrics.ship_remains_draws,
        "ship_remains_source_frame_draws":peer.metrics.ship_remains_source_frame_draws,
        "ship_explosion_visual_draws":peer.metrics.ship_explosion_visual_draws,
        "queen_explosion_layer_draws":peer.metrics.queen_explosion_layer_draws,
        "beehive_shrinking_frame_draws":peer.metrics.beehive_shrinking_frame_draws,
        "beehive_final_blast_draws":peer.metrics.beehive_final_blast_draws,
        "projectile_sprite_draws":peer.metrics.projectile_sprite_draws,
        "asteroid_sprite_draws":peer.metrics.asteroid_sprite_draws,
        "asteroid_piece_sprite_draws":peer.metrics.asteroid_piece_sprite_draws,
        "asteroid_piece_fallback_draws":peer.metrics.asteroid_piece_fallback_draws,
        "asteroid_pieces_viewport_culled":peer.metrics.asteroid_pieces_viewport_culled,
        "fire_tank_sprite_draws":peer.metrics.fire_tank_sprite_draws,
        "obstacle_debris_events_received":peer.metrics.obstacle_debris_events_received,
        "obstacle_debris_sprite_draws":peer.metrics.obstacle_debris_sprite_draws,
        "obstacle_debris_culled":peer.metrics.obstacle_debris_culled,
        "map_object_health_bars":peer.metrics.map_object_health_bars,
        "sprite_candidate_pixels":peer.metrics.sprite_candidate_pixels,
        "sprite_opaque_pixels":peer.metrics.sprite_opaque_pixels,
        "sprite_blended_pixels":peer.metrics.sprite_blended_pixels,
        "recolor_cache_peak":peer.metrics.recolor_cache_peak,
        "obstacle_hull_draws":peer.metrics.obstacle_hull_draws,
        "minimap_ship_markers":peer.metrics.minimap_ship_markers,
        "minimap_terrain_markers":peer.metrics.minimap_terrain_markers,
        "minimap_objective_markers":peer.metrics.minimap_objective_markers,
        "minimap_camera_moves":peer.metrics.minimap_camera_moves,
        "camera_edge_pan_frames":peer.metrics.camera_edge_pan_frames,
        "ship_type_multiselections":peer.metrics.ship_type_multiselections,
        "selection_drag_preview_frames":peer.metrics.selection_drag_preview_frames,
        "weapon_range_draws":peer.metrics.weapon_range_draws,
        "weapon_charge_draws":peer.metrics.weapon_charge_draws,
        "weapon_aim_draws":peer.metrics.weapon_aim_draws,
        "weapon_contact_draws":peer.metrics.weapon_contact_draws,
        "weapon_obstructed_contact_draws":peer.metrics.weapon_obstructed_contact_draws,
        "command_target_draws":peer.metrics.command_target_draws,
        "striker_bomb_indicator_draws":peer.metrics.striker_bomb_indicator_draws,
        "tactical_button_clicks":peer.metrics.tactical_button_clicks,
        "squad_tab_clicks":peer.metrics.squad_tab_clicks,
        "shooting_strategy_choices":peer.metrics.shooting_strategy_choices,
        "minimap_move_orders_submitted":peer.metrics.minimap_move_orders_submitted,
        "right_drag_move_orders_submitted":peer.metrics.right_drag_move_orders_submitted,
        "fog_circle_stamps":peer.metrics.fog_circle_stamps,
        "fog_masked_pixels":peer.metrics.fog_masked_pixels,
        "source_art_enabled":show_art,
        "audio":audio.measurements(),
        "orders_sent":peer.metrics.orders_sent,
        "order_acknowledgments":peer.metrics.acknowledgments,
        "briefing_requests_sent":peer.metrics.briefing_requests_sent,
        "briefing_receipts":peer.metrics.briefing_receipts,
        "briefing_rejections":peer.metrics.briefing_rejections,
        "orders_accepted":peer.metrics.orders_accepted,
        "orders_rejected":peer.metrics.orders_rejected,
        "order_ack_latency_samples":peer.metrics.order_ack_latency_samples,
        "order_ack_latency_mean_milliseconds":if peer.metrics.order_ack_latency_samples>0 {
            Some(peer.metrics.order_ack_latency_sum_seconds*1000.0/
                peer.metrics.order_ack_latency_samples as f64)
        } else {None},
        "order_ack_latency_max_milliseconds":if peer.metrics.order_ack_latency_samples>0 {
            Some(peer.metrics.order_ack_latency_max_seconds*1000.0)
        } else {None},
        "order_latency_samples_lost":peer.metrics.order_latency_samples_lost,
        "pending_order_timings_at_exit":peer.pending_order_times.len(),
        "pending_order_timings_peak":peer.metrics.pending_order_timings_peak,
        "artwork_mean_milliseconds_per_frame":if peer.metrics.frames_rendered>0 {
            Some(1000.0*peer.metrics.artwork_seconds/peer.metrics.frames_rendered as f64)
        } else {None},
        "artwork_max_milliseconds":peer.metrics.artwork_max_seconds*1000.0,
        "hud_mean_milliseconds_per_frame":if peer.metrics.frames_rendered>0 {
            Some(1000.0*peer.metrics.hud_seconds/peer.metrics.frames_rendered as f64)
        } else {None},
        "hud_max_milliseconds":peer.metrics.hud_max_seconds*1000.0,
        "present_mean_milliseconds_per_frame":if peer.metrics.frames_rendered>0 {
            Some(1000.0*peer.metrics.present_seconds/peer.metrics.frames_rendered as f64)
        } else {None},
        "present_max_milliseconds":peer.metrics.present_max_seconds*1000.0,
        "campaign_receipts":peer.metrics.campaign_receipts,
        "battle_result_receipts":peer.metrics.battle_result_receipts,
        "interrupted_battle_notices":peer.metrics.interrupted_battle_notices,
        "reconnect_attempts":peer.metrics.reconnect_attempts,
        "reconnect_transports_opened":peer.metrics.reconnect_transports_opened,
        "connection_closed":!peer.connected,
    }));
    Ok(())
}
