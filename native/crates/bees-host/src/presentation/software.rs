//! Small, window-independent tactical renderer of authoritative live geometry.
//! Owns only pixels/camera/selection; it never mutates or steps a battle.

use std::collections::{HashMap, HashSet};
use std::io::{self, Write};
use std::sync::{Arc, OnceLock};
use bees_sim::collision::{Aabb, Pose};
use bees_sim::geometry::Vec2;
use bees_sim::physics::ShipShape;
use bees_sim::simulation::{RandomStream, WorldId};
use super::{NativeRenderWorldView, NativeSquadSelection,
    battle_outcome_label, battle_outcome_reason, text};
use super::minimap::MiniMapLayout;
use super::tactical_hud;

#[derive(Clone, Copy, Debug)]
pub struct NativeCamera {
    pub center: Vec2,
    pub pixels_per_unit: f32,
}

impl NativeCamera {
    pub fn fit(bounds: Aabb, width: usize, height: usize) -> Self {
        Self {
            center: (bounds.min + bounds.max) * 0.5,
            pixels_per_unit: ((width as f32 / (bounds.max.x - bounds.min.x).max(1.0))
                .min(height as f32 / (bounds.max.y - bounds.min.y).max(1.0)) * 0.9)
                .clamp(0.001, 1000.0),
        }
    }

    pub fn screen_to_world(self, x: f32, y: f32, width: usize, height: usize) -> Vec2 {
        self.center + Vec2::new(x - width as f32 * 0.5, height as f32 * 0.5 - y)
            * (1.0 / self.pixels_per_unit)
    }

    /// Convert the source's orthographic half-height limits into native
    /// pixels per world unit using the current viewport height.
    pub fn constrain_source_zoom(
        &mut self, limits: Option<(f32, f32)>, height: usize,
    ) -> bool {
        let Some((min, max)) = limits else { return false };
        if !min.is_finite() || !max.is_finite() ||
            min <= 0.0 || max < min || height == 0 { return false; }
        let low = height as f32 / (2.0 * max);
        let high = height as f32 / (2.0 * min);
        if !low.is_finite() || !high.is_finite() || low <= 0.0 {
            return false;
        }
        let previous = self.pixels_per_unit;
        self.pixels_per_unit = previous.clamp(low, high);
        self.pixels_per_unit != previous
    }

    pub fn zoom_at(
        &mut self, factor: f32, x: f32, y: f32, width: usize, height: usize,
        authored: Option<(f32, f32)>,
    ) -> bool {
        if !factor.is_finite() || factor <= 0.0 { return false; }
        let before = self.screen_to_world(x, y, width, height);
        self.pixels_per_unit = (self.pixels_per_unit * factor).clamp(0.001, 1000.0);
        let constrained = self.constrain_source_zoom(authored, height);
        self.center += before - self.screen_to_world(x, y, width, height);
        constrained
    }

    /// Source LevelInputManager.MaintainScrollBoundary uses the map extent
    /// minus the camera's orthographic half-width and half-height. If the
    /// viewport is wider than the map, center that axis instead of asking
    /// f32::clamp to use reversed bounds. This is presentation-only.
    pub fn constrain_to_map(
        &mut self, map: Aabb, width: usize, height: usize,
    ) -> bool {
        if !self.center.is_finite() || !self.pixels_per_unit.is_finite() ||
            self.pixels_per_unit <= 0.0 { return false; }
        let half_width = width as f32 * 0.5 / self.pixels_per_unit;
        let half_height = height as f32 * 0.5 / self.pixels_per_unit;
        let constrain = |value: f32, low: f32, high: f32, half: f32| {
            let min = low + half;
            let max = high - half;
            if min > max { (low + high) * 0.5 }
            else { value.clamp(min, max) }
        };
        let before = self.center;
        self.center.x = constrain(before.x, map.min.x, map.max.x, half_width);
        self.center.y = constrain(before.y, map.min.y, map.max.y, half_height);
        self.center != before
    }
}

#[derive(Clone, Copy, Debug, Default, serde::Serialize)]
pub struct NativeRenderCounts {
    pub ships: usize,
    /// Source PNG sprites rendered instead of the fallback hull outline.
    pub ship_sprite_draws: usize,
    /// Frames from original Carpenter, Factory and WarpGate clips.
    pub ship_animated_sprite_draws: usize,
    pub ship_animation_clips_loaded: usize,
    /// Actual rendered source-color fleet sprites, not plain faction outlines.
    pub custom_ship_color_draws: usize,
    /// One-time source sprite recolor variants built this frame.
    pub ship_color_cache_misses: usize,
    pub ship_color_cache_variants: usize,
    /// Number of visible sprite-pixel candidates inspected by the software
    /// rasterizer, including ships, shots, debris and special effects.
    pub sprite_raster_candidate_pixels: u64,
    /// Actual fully opaque samples that skipped destination blending.
    pub sprite_raster_opaque_pixels: u64,
    /// Actual translucent samples that required destination blending.
    pub sprite_raster_blended_pixels: u64,
    pub ship_fallback_hulls: usize,
    /// Source CombatEvent::ShipDied events admitted this frame.
    pub ship_deaths_observed: usize,
    /// Rendered short explosion flashes and five-second ship remains.
    pub ship_explosion_visual_draws: usize,
    pub ship_remains_draws: usize,
    /// Original imported source ship-remains frames, excluding fallback art.
    pub ship_remains_source_frame_draws: usize,
    pub ship_remains_art_classes_loaded: usize,
    pub ship_remains_color_cache_variants: usize,
    /// Oldest effects evicted to keep large battles bounded.
    pub ship_death_visuals_dropped: usize,
    /// Source CombatEvent::Hit feedback from real physical damage, presentation only.
    pub combat_impacts_observed: usize,
    pub combat_impact_visual_draws: usize,
    pub combat_impact_visuals_dropped: usize,
    /// Loaded source-art types, not the number of active ship entities.
    pub ship_art_types_loaded: usize,
    pub asteroid_sprite_draws: usize,
    pub asteroid_piece_sprite_draws: usize,
    pub asteroid_fallback_hulls: usize,
    pub asteroid_art_types_loaded: usize,
    pub projectile_sprite_draws: usize,
    /// Real Rocket / Fire Barge explosion sprite draws (a subset of projectiles).
    pub projectile_explosion_sprite_draws: usize,
    pub projectile_fallback_hulls: usize,
    pub projectile_art_types_loaded: usize,
    pub obstacles: usize,
    pub culled_ships: usize,
    pub culled_obstacles: usize,
    pub projectiles: usize,
    pub pieces: usize,
    pub culled_projectiles: usize,
    pub culled_pieces: usize,
    pub range_queries: usize,
    /// Enemy ships excluded by the simulator's existing weapon-range
    /// contact cache, without inspecting their hulls in the renderer.
    pub range_candidates_culled: usize,
    pub range_contacts: usize,
    pub aim_points: usize,
    pub automatic_targets: usize,
    pub missing_aim_data: usize,
    pub range_overlaps: usize,
    pub obstructed_ranges: usize,
    pub missing_range_data: usize,
    pub minimap_ship_markers: usize,
    pub minimap_visible_enemy_markers: usize,
    pub minimap_extraction_exit_markers: usize,
    pub numbered_squad_rows: usize,
    pub special_ready_ships: usize,
    /// Real game/policy Striker loaded indicators drawn for visible hulls.
    pub striker_bomb_indicators: usize,
    pub tactical_buttons_drawn: usize,
    pub selected_command_rows: usize,
    pub command_target_markers: usize,
    /// Presentation-only pixel coverage; headless physics does no work.
    pub fog_sources: usize,
    pub fog_fading_sources: usize,
    pub fog_occluded_pixels: usize,
    /// One non-stencil, fully exposed viewport this frame.
    pub fog_full_viewport_skips: usize,
    /// One direct dark fill with no player sight sources remaining.
    pub fog_empty_viewport_fills: usize,
}

pub struct NativeSoftwareRenderer {
    width: usize,
    height: usize,
    pixels: Vec<u32>,
    /// Allocated only for fog-enabled graphical Levels and reused by frame.
    fog_coverage: Vec<u8>,
    /// UI lifecycle only: Unity FogOfWarVision remains after a ship death,
    /// shrinking its mask by 0.8 every 0.1 simulation seconds.
    fog_context: Option<(WorldId, u64, Option<i32>)>,
    fog_render_pass: u64,
    fog_live: HashMap<i64, FogLiveSource>,
    fog_fades: Vec<FogFade>,
    pending_fog_fades: Vec<FogFade>,
    pub camera: NativeCamera,
    /// Selection is scoped to a WorldId AND episode revision by the client.
    pub selected_ship: Option<i64>,
    pub selection: NativeSquadSelection,
    // Reused only for large selections; small squads need no hash allocation.
    selected_squad_lookup: HashSet<bees_hivemind::session::SquadLifetime>,
    /// Reused UI dedup for actual player Aggressive/BombingRun targets.
    /// Filled from the live command director before the existing ship
    /// render pass, avoiding any additional ship loop or copied roster.
    command_target_ship_ids: HashSet<i64>,
    /// Cache exact source-mask recolors by (ship class, SavedSquad RGB).
    /// Shared physical templates and PPO workers never allocate here.
    custom_ship_art: HashMap<(u8, [u8; 3]), Arc<NativeSprite>>,
    /// Recolor variants of source clips, owned only by the visible UI.
    animated_ship_art: HashMap<(u8, u8, usize, [u8; 3]), Arc<NativeSprite>>,
    /// Frames and recolor of actual 20-frame source ShipRemains, never
    /// retained by the physical world or headless learner.
    colored_ship_remains: HashMap<(u8, usize, [u8; 3]), Arc<NativeSprite>>,
    sprite_raster_candidate_pixels: u64,
    sprite_raster_opaque_pixels: u64,
    sprite_raster_blended_pixels: u64,
    pub selection_box: Option<Aabb>,
    /// Optional inspection only. Disabled means no range or linecast work.
    pub show_weapon_ranges: bool,
    /// Presentation-only tactical overview, never used by headless worlds.
    pub show_minimap: bool,
    /// Only affects the window; training runs keep the same headless world.
    pub show_shooting_menu: bool,
    pub focused_shooting_strategy: u8,
    pub active_tactical_action: Option<super::HudAction>,
    pub locked_selection_all: bool,
    pub chasing_selection_all: bool,
    pub matched_speed_selection_all: bool,
    pub ceasefire_selection_all: bool,
    pub attack_on_sight_selection_all: bool,
    pub hovered_tactical_action: Option<super::HudAction>,
    debris_frame: Option<(WorldId, u64, u64)>,
    obstacle_debris: Vec<ObstacleDebris>,
    /// Optional source-identified death presentation, bounded independently
    /// of the underlying CombatWorld's entity and training lifetimes.
    death_owner: Option<(WorldId, u64)>,
    death_effects: Vec<ShipDeathVisual>,
    impact_effects: Vec<CombatImpactVisual>,
    /// Cosmetic first-seen ticks belong to the displayed world and
    /// episode. No world model state or RNG changes when sprites animate.
    projectile_art_owner: Option<(WorldId, u64)>,
    projectile_art_ticks: HashMap<i64, (u64, u64)>,
}

#[derive(Clone, Copy)]
struct FogLiveSource {
    position: Vec2,
    radius: f32,
    seen_pass: u64,
}

#[derive(Clone, Copy)]
struct FogFade {
    position: Vec2,
    radius: f32,
    born_tick: u64,
}

impl FogFade {
    fn current_radius(self, tick: u64, dt: f32) -> f32 {
        let elapsed = tick.saturating_sub(self.born_tick) as f32 * dt;
        let phases = (elapsed / 0.1).floor().max(0.0);
        self.radius * 0.8f32.powf(phases)
    }
}

/// The source ShipRemains is visible for five simulated seconds. This
/// read-only snapshot is captured only from an authenticated ShipDied event,
/// never from absence in a rendered roster (fog or extraction also hides ships).
#[derive(Clone, Copy)]
struct ShipDeathVisual {
    ship_id: i64,
    born_tick: u64,
    ship_type: u8,
    position: Vec2,
    rotation_degrees: f32,
    radius: f32,
    side: i32,
    color_rgb: Option<[u8; 3]>,
}

/// No additional damage or physics: an ephemeral rendering of an actual hit.
#[derive(Clone, Copy)]
struct CombatImpactVisual {
    born_tick: u64,
    projectile_id: i64,
    target_id: i64,
    position: Vec2,
    fatal: bool,
}

struct ObstacleDebris {
    born: u64,
    position: Vec2,
    velocity: Vec2,
    rotation: f32,
    spin: f32,
    lifetime: f32,
    scale: f32,
    sprite: usize,
}

struct NativeSprite {
    width: usize,
    height: usize,
    pixels: Vec<u8>,
    /// Alpha-only visible rectangle in source pixel coordinates.
    /// Computed once on load; ship and projectile rasterization never
    /// rescans transparent padding on every rendered frame.
    painted_bounds: Option<(usize, usize, usize, usize)>,
}

impl NativeSprite {
    fn new(width: usize, height: usize, pixels: Vec<u8>) -> Self {
        let mut left = width;
        let mut top = height;
        let mut right = 0;
        let mut bottom = 0;
        for (i, color) in pixels.chunks_exact(4).enumerate() {
            if color[3] == 0 { continue; }
            let x = i % width;
            let y = i / width;
            left = left.min(x);
            top = top.min(y);
            right = right.max(x + 1);
            bottom = bottom.max(y + 1);
        }
        let painted_bounds = (left < right && top < bottom)
            .then_some((left, top, right, bottom));
        Self { width, height, pixels, painted_bounds }
    }
}

struct SpriteAtlas {
    pixels_per_unit: f32,
    sprites: Vec<NativeSprite>,
}

impl SpriteAtlas {
    fn decode(data: &[u8]) -> Self {
        assert_eq!(&data[..4], b"BDS2");
        let read_u16 = |bytes: &[u8]| usize::from(u16::from_le_bytes([bytes[0], bytes[1]]));
        let pixels_per_unit = read_u16(&data[4..]) as f32;
        let count = read_u16(&data[6..]);
        let mut rest = &data[8..];
        let sprites = (0..count).map(|_| {
            let width = read_u16(rest);
            let height = read_u16(&rest[2..]);
            rest = &rest[4..];
            let size = width * height * 4;
            let mut pixels = Vec::with_capacity(size);
            while pixels.len() < size {
                let length = read_u16(rest);
                assert!(length > 0 && pixels.len() + length * 4 <= size);
                for _ in 0..length { pixels.extend_from_slice(&rest[2..6]); }
                rest = &rest[6..];
            }
            NativeSprite::new(width, height, pixels)
        }).collect();
        assert!(rest.is_empty() && count > 0 && pixels_per_unit > 0.0);
        Self { pixels_per_unit, sprites }
    }
}

/// Decoder for bounded operator-generated sprite atlases. The original
/// PNGs are converted once, never decoded by a physics or PPO worker.
fn source_art_u16(bytes: &[u8], at: &mut usize) -> Option<usize> {
    let end = (*at).checked_add(2)?;
    let raw = bytes.get(*at..end)?;
    *at = end;
    Some(usize::from(u16::from_le_bytes([raw[0], raw[1]])))
}

fn source_art_sprite(
    bytes: &[u8], at: &mut usize, width: usize, height: usize,
) -> io::Result<NativeSprite> {
    let invalid = || io::Error::new(io::ErrorKind::InvalidData,
        "invalid native source sprite data");
    let expected = width.checked_mul(height)
        .and_then(|pixels| pixels.checked_mul(4))
        .ok_or_else(invalid)?;
    let mut pixels = Vec::with_capacity(expected);
    while pixels.len() < expected {
        let end = (*at).checked_add(6).ok_or_else(invalid)?;
        let run = bytes.get(*at..end).ok_or_else(invalid)?;
        *at = end;
        let length = usize::from(u16::from_le_bytes([run[0], run[1]]));
        let to_write = length.checked_mul(4).ok_or_else(invalid)?;
        if length == 0 || pixels.len() + to_write > expected {
            return Err(invalid());
        }
        for _ in 0..length { pixels.extend_from_slice(&run[2..6]); }
    }
    Ok(NativeSprite::new(width, height, pixels))
}

const SOURCE_SHIP_TYPES: usize = 24;
const SOURCE_PROJECTILE_TYPES: usize = 16;
const SOURCE_ASTEROID_PREFABS: usize = 29; // zero is unused

struct SourceShipArt {
    pixels_per_unit: f32,
    sprite: NativeSprite,
    /// Exact ConfigData.ChangeableShipColors selection, prepared by the
    /// offline converter. No source-color comparisons occur per game frame.
    recolor_mask: Option<Vec<u8>>,
}

impl SourceShipArt {
    fn recolored(&self, color: [u8; 3]) -> NativeSprite {
        let mut pixels = self.sprite.pixels.clone();
        if let Some(mask) = &self.recolor_mask {
            for (rgba, &colored) in pixels.chunks_exact_mut(4).zip(mask) {
                if colored != 0 {
                    rgba[..3].copy_from_slice(&color);
                }
            }
        }
        NativeSprite::new(self.sprite.width, self.sprite.height, pixels)
    }
}

/// One byte per pixel, RLE compressed: 1 means source artwork may
/// substitute SavedSquad.Color while preserving each pixel's alpha.
fn source_art_color_mask(bytes: &[u8], at: &mut usize, count: usize)
    -> io::Result<Option<Vec<u8>>>
{
    let invalid = || io::Error::new(io::ErrorKind::InvalidData,
        "invalid native source squad color mask");
    let mut mask = Vec::with_capacity(count);
    let mut has_color = false;
    while mask.len() < count {
        let end = (*at).checked_add(3).ok_or_else(invalid)?;
        let run = bytes.get(*at..end).ok_or_else(invalid)?;
        *at = end;
        let length = usize::from(u16::from_le_bytes([run[0], run[1]]));
        if length == 0 || length > count - mask.len() || run[2] > 1 {
            return Err(invalid());
        }
        has_color |= run[2] != 0;
        mask.resize(mask.len() + length, run[2]);
    }
    Ok(has_color.then_some(mask))
}

/// The conversion format is deliberately small and independently validated.
/// A malformed or absent optional artwork file can never change combat or
/// prevent the tactical client from rendering its physical hull fallback.
struct SourceShipAtlas {
    sprites: [Option<SourceShipArt>; SOURCE_SHIP_TYPES],
    loaded: usize,
}

impl SourceShipAtlas {
    fn decode(bytes: &[u8]) -> io::Result<Self> {
        let invalid = || io::Error::new(io::ErrorKind::InvalidData,
            "invalid native ship sprite atlas");
        let source_color_masks = match bytes.get(..4) {
            Some(magic) if magic == b"BSS2" => true,
            Some(magic) if magic == b"BSS1" => false, // earlier non-recolorable atlas
            _ => return Err(invalid()),
        };
        let mut at = 4usize;
        let count = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
        if count > SOURCE_SHIP_TYPES { return Err(invalid()); }
        let mut sprites = std::array::from_fn(|_| None);
        for _ in 0..count {
            let ty = usize::from(*bytes.get(at).ok_or_else(invalid)?);
            at += 1;
            let ppu = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
            let width = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
            let height = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
            if ty >= SOURCE_SHIP_TYPES || sprites[ty].is_some() ||
                ppu == 0 || width == 0 || height == 0 ||
                width > 2048 || height > 2048
            {
                return Err(invalid());
            }
            let sprite = source_art_sprite(bytes, &mut at, width, height)?;
            let recolor_mask = if source_color_masks {
                source_art_color_mask(bytes, &mut at, width * height)?
            } else { None };
            sprites[ty] = Some(SourceShipArt {
                pixels_per_unit: ppu as f32, sprite, recolor_mask,
            });
        }
        if at != bytes.len() { return Err(invalid()); }
        Ok(Self { sprites, loaded: count })
    }
}

/// Optional original animated ship art. The offline importer preserves
/// source clip FPS, Unity pixels-per-unit and exact recolorable pixels.
/// Two variants per ship type permit the physically gated WarpGate opening
/// to switch to its original ready loop without changing game state.
struct SourceShipAnimationArt {
    frames_per_second: f32,
    frames: Vec<SourceShipArt>,
}

/// Common decoder for living ship clips and death remains. Keeping the
/// same pixels-per-unit and source recolor mask rules avoids drift between
/// ordinary and destroyed source sprites.
fn decode_ship_clip(
    bytes: &[u8], at: &mut usize, ppu: usize, fps: usize,
    frame_count: usize, total_pixels: &mut usize,
) -> io::Result<SourceShipAnimationArt> {
    let invalid = || io::Error::new(io::ErrorKind::InvalidData,
        "invalid native animated ship source frame");
    if ppu == 0 || !(1..=120).contains(&fps) ||
        !(1..=64).contains(&frame_count) {
        return Err(invalid());
    }
    let mut frames = Vec::with_capacity(frame_count);
    for _ in 0..frame_count {
        let width = source_art_u16(bytes, at).ok_or_else(invalid)?;
        let height = source_art_u16(bytes, at).ok_or_else(invalid)?;
        if !(1..=2048).contains(&width) || !(1..=2048).contains(&height) {
            return Err(invalid());
        }
        *total_pixels = total_pixels.checked_add(width * height)
            .ok_or_else(invalid)?;
        if *total_pixels > 16_777_216 { return Err(invalid()); }
        frames.push(SourceShipArt {
            pixels_per_unit: ppu as f32,
            sprite: source_art_sprite(bytes, at, width, height)?,
            recolor_mask: source_art_color_mask(bytes, at, width * height)?,
        });
    }
    Ok(SourceShipAnimationArt { frames_per_second: fps as f32, frames })
}

struct SourceShipAnimationAtlas {
    clips: [Option<SourceShipAnimationArt>; SOURCE_SHIP_TYPES * 2],
    loaded: usize,
}

impl SourceShipAnimationAtlas {
    fn decode(bytes: &[u8]) -> io::Result<Self> {
        let invalid = || io::Error::new(io::ErrorKind::InvalidData,
            "invalid native animated ship artwork");
        if bytes.get(..4) != Some(&b"BSA1"[..]) { return Err(invalid()); }
        let mut at = 4usize;
        let count = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
        if count > SOURCE_SHIP_TYPES * 2 { return Err(invalid()); }
        let mut clips = std::array::from_fn(|_| None);
        let mut pixels_total = 0usize;
        for _ in 0..count {
            let ty = usize::from(*bytes.get(at).ok_or_else(invalid)?);
            at += 1;
            let variant = usize::from(*bytes.get(at).ok_or_else(invalid)?);
            at += 1;
            let ppu = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
            let fps = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
            let frames = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
            if ty >= SOURCE_SHIP_TYPES || variant > 1 || ppu == 0 ||
                !(1..=120).contains(&fps) || !(1..=64).contains(&frames) ||
                clips[ty * 2 + variant].is_some() {
                return Err(invalid());
            }
            clips[ty * 2 + variant] = Some(decode_ship_clip(
                bytes, &mut at, ppu, fps, frames, &mut pixels_total,
            )?);
        }
        if at != bytes.len() { return Err(invalid()); }
        Ok(Self { clips, loaded: count })
    }
}

/// Lazy source ShipRemains atlas. Separate from living sprite animation
/// data so 20-frame wreck sheets consume no memory until actual combat
/// destruction is displayed; old Windows/Linux PCs keep small 1v1 loads.
struct SourceShipRemainsAtlas {
    clips: [Option<SourceShipAnimationArt>; SOURCE_SHIP_TYPES],
    loaded: usize,
}

impl SourceShipRemainsAtlas {
    fn decode(bytes: &[u8]) -> io::Result<Self> {
        let invalid = || io::Error::new(io::ErrorKind::InvalidData,
            "invalid source ship remains atlas");
        if bytes.get(..4) != Some(&b"BSR1"[..]) { return Err(invalid()); }
        let mut at = 4usize;
        let count = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
        if count > SOURCE_SHIP_TYPES { return Err(invalid()); }
        let mut clips = std::array::from_fn(|_| None);
        let mut total_pixels = 0;
        for _ in 0..count {
            let kind = usize::from(*bytes.get(at).ok_or_else(invalid)?);
            at += 1;
            let ppu = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
            let fps = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
            let frames = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
            if kind >= SOURCE_SHIP_TYPES || clips[kind].is_some() {
                return Err(invalid());
            }
            clips[kind] = Some(decode_ship_clip(
                bytes, &mut at, ppu, fps, frames, &mut total_pixels,
            )?);
        }
        if at != bytes.len() { return Err(invalid()); }
        Ok(Self { clips, loaded: count })
    }
}

/// Real projectile sprites may have multiple source-authored animation
/// slices. The global tick only determines cosmetic animation phase;
/// projectile lifetime, hit geometry and root-shot identity stay in CombatWorld.
struct SourceProjectileArt {
    pixels_per_unit: f32,
    frames: Vec<NativeSprite>,
}

struct SourceProjectileAtlas {
    sprites: [Option<SourceProjectileArt>; SOURCE_PROJECTILE_TYPES],
    loaded: usize,
}

impl SourceProjectileAtlas {
    fn decode(bytes: &[u8]) -> io::Result<Self> {
        let invalid = || io::Error::new(io::ErrorKind::InvalidData,
            "invalid native projectile sprite atlas");
        if bytes.get(..4) != Some(&b"BPS1"[..]) { return Err(invalid()); }
        let mut at = 4usize;
        let count = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
        if count > SOURCE_PROJECTILE_TYPES { return Err(invalid()); }
        let mut sprites = std::array::from_fn(|_| None);
        let mut all_pixels = 0usize;
        for _ in 0..count {
            let kind = usize::from(*bytes.get(at).ok_or_else(invalid)?);
            at += 1;
            let ppu = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
            let frame_count = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
            if kind >= SOURCE_PROJECTILE_TYPES || sprites[kind].is_some() ||
                ppu == 0 || !(1..=64).contains(&frame_count) {
                return Err(invalid());
            }
            let mut frames = Vec::with_capacity(frame_count);
            for _ in 0..frame_count {
                let width = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
                let height = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
                if width == 0 || height == 0 || width > 2048 || height > 2048 {
                    return Err(invalid());
                }
                all_pixels = all_pixels.checked_add(width * height).ok_or_else(invalid)?;
                if all_pixels > 16_777_216 { return Err(invalid()); }
                frames.push(source_art_sprite(bytes, &mut at, width, height)?);
            }
            sprites[kind] = Some(SourceProjectileArt {
                pixels_per_unit: ppu as f32, frames,
            });
        }
        if at != bytes.len() { return Err(invalid()); }
        Ok(Self { sprites, loaded: count })
    }
}

/// Load optional packed art once when the software renderer first needs
/// it. Packaged games need only the small .rgba outputs, not Pillow,
/// Unity, or the source PNGs.
fn source_art_path(filename: &str) -> Option<std::path::PathBuf> {
    let requested = std::env::var_os("BEES_NATIVE_ASSETS")
        .map(|dir| std::path::PathBuf::from(dir).join(filename));
    if let Some(requested) = requested {
        return requested.is_file().then_some(requested);
    }
    let mut candidates = vec![
        std::path::PathBuf::from("native/assets").join(filename),
        std::path::PathBuf::from("assets").join(filename),
    ];
    if let Ok(exe) = std::env::current_exe() {
        if let Some(parent) = exe.parent() {
            candidates.push(parent.join("assets").join(filename));
        }
    }
    candidates.push(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../assets").join(filename));
    candidates.into_iter().find(|path| path.is_file())
}

fn load_source_art<T>(filename: &str, decoder: fn(&[u8]) -> io::Result<T>) -> Option<T> {
    let path = source_art_path(filename)?;
    match std::fs::read(&path).and_then(|bytes| decoder(&bytes)) {
        Ok(atlas) => Some(atlas),
        Err(error) => {
            eprintln!("Ignoring invalid native artwork at {}: {error}", path.display());
            None
        }
    }
}

fn source_ship_art() -> Option<&'static SourceShipAtlas> {
    static ATLAS: OnceLock<Option<SourceShipAtlas>> = OnceLock::new();
    ATLAS.get_or_init(|| load_source_art(
        "ship-sprites.rgba", SourceShipAtlas::decode,
    )).as_ref()
}

fn source_ship_animation_art() -> Option<&'static SourceShipAnimationAtlas> {
    static ATLAS: OnceLock<Option<SourceShipAnimationAtlas>> = OnceLock::new();
    ATLAS.get_or_init(|| load_source_art(
        "ship-animations.rgba", SourceShipAnimationAtlas::decode,
    )).as_ref()
}

static SOURCE_SHIP_REMAINS_ATLAS: OnceLock<Option<SourceShipRemainsAtlas>> =
    OnceLock::new();

fn source_ship_remains_art() -> Option<&'static SourceShipRemainsAtlas> {
    SOURCE_SHIP_REMAINS_ATLAS.get_or_init(|| load_source_art(
        "ship-remains.rgba", SourceShipRemainsAtlas::decode,
    )).as_ref()
}

fn loaded_ship_remains_art_classes() -> usize {
    // A per-frame gauge must not accidentally decode every 20-frame
    // source wreck sheet at game startup before a single ship dies.
    SOURCE_SHIP_REMAINS_ATLAS.get()
        .and_then(Option::as_ref).map_or(0, |atlas| atlas.loaded)
}

fn source_projectile_art() -> Option<&'static SourceProjectileAtlas> {
    static ATLAS: OnceLock<Option<SourceProjectileAtlas>> = OnceLock::new();
    ATLAS.get_or_init(|| load_source_art(
        "projectile-sprites.rgba", SourceProjectileAtlas::decode,
    )).as_ref()
}

/// Original Asteroid 1..28 PNGs, indexed by the same prefab numbers
/// as the physical collision catalog. No visual-only asteroid inventory.
struct SourceAsteroidArt {
    pixels_per_unit: f32,
    sprite: NativeSprite,
}

struct SourceAsteroidAtlas {
    sprites: [Option<SourceAsteroidArt>; SOURCE_ASTEROID_PREFABS],
    loaded: usize,
}

impl SourceAsteroidAtlas {
    fn decode(bytes: &[u8]) -> io::Result<Self> {
        let invalid = || io::Error::new(io::ErrorKind::InvalidData,
            "invalid native asteroid sprite atlas");
        if bytes.get(..4) != Some(&b"BAS1"[..]) { return Err(invalid()); }
        let mut at = 4usize;
        let count = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
        if count > 28 { return Err(invalid()); }
        let mut sprites = std::array::from_fn(|_| None);
        for _ in 0..count {
            let prefab = usize::from(*bytes.get(at).ok_or_else(invalid)?);
            at += 1;
            let ppu = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
            let width = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
            let height = source_art_u16(bytes, &mut at).ok_or_else(invalid)?;
            if prefab == 0 || prefab >= SOURCE_ASTEROID_PREFABS ||
                sprites[prefab].is_some() || ppu == 0 ||
                width == 0 || height == 0 || width > 2048 || height > 2048 {
                return Err(invalid());
            }
            let sprite = source_art_sprite(bytes, &mut at, width, height)?;
            sprites[prefab] = Some(SourceAsteroidArt {
                pixels_per_unit: ppu as f32, sprite,
            });
        }
        if at != bytes.len() { return Err(invalid()); }
        Ok(Self { sprites, loaded: count })
    }
}

fn source_asteroid_art() -> Option<&'static SourceAsteroidAtlas> {
    static ATLAS: OnceLock<Option<SourceAsteroidAtlas>> = OnceLock::new();
    ATLAS.get_or_init(|| load_source_art(
        "asteroid-sprites.rgba", SourceAsteroidAtlas::decode,
    )).as_ref()
}

fn fire_tank_debris_atlas() -> &'static SpriteAtlas {
    static ATLAS: OnceLock<SpriteAtlas> = OnceLock::new();
    ATLAS.get_or_init(|| SpriteAtlas::decode(include_bytes!("../../../../assets/fire-tank-debris.rgba")))
}

impl NativeSoftwareRenderer {
    pub fn new(width: usize, height: usize, bounds: Aabb) -> io::Result<Self> {
        if width == 0 || height == 0 || width > 8192 || height > 8192 ||
            !bounds.min.is_finite() || !bounds.max.is_finite() ||
            bounds.min.x >= bounds.max.x || bounds.min.y >= bounds.max.y {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "invalid viewport or bounds"));
        }
        Ok(Self { width, height, pixels: vec![0; width * height],
            fog_coverage: Vec::new(), fog_context: None, fog_render_pass: 0,
            fog_live: HashMap::new(), fog_fades: Vec::new(),
            pending_fog_fades: Vec::new(),
            camera: NativeCamera::fit(bounds, width, height), selected_ship: None,
            selection: NativeSquadSelection::default(),
            selected_squad_lookup: HashSet::new(),
            command_target_ship_ids: HashSet::new(),
            custom_ship_art: HashMap::new(), animated_ship_art: HashMap::new(),
            colored_ship_remains: HashMap::new(),
            sprite_raster_candidate_pixels: 0,
            sprite_raster_opaque_pixels: 0,
            sprite_raster_blended_pixels: 0,
            selection_box: None, show_weapon_ranges: false,
            show_minimap: true, show_shooting_menu: false,
            focused_shooting_strategy: 1,
            active_tactical_action: None, locked_selection_all: false,
            chasing_selection_all: false,
            matched_speed_selection_all: false,
            ceasefire_selection_all: false,
            attack_on_sight_selection_all: false,
            hovered_tactical_action: None,
            debris_frame: None, obstacle_debris: Vec::new(),
            death_owner: None, death_effects: Vec::new(),
            impact_effects: Vec::new(),
            projectile_art_owner: None, projectile_art_ticks: HashMap::new() })
    }

    /// A source-scoped gameplay death is rendered from its exact physical
    /// last pose, not from a missing ship or separate cosmetic simulation.
    /// Remote snapshots use the same method after host-side fog filtering.
    pub(super) fn begin_death_scene(&mut self, world: WorldId, revision: u64) {
        let owner = (world, revision);
        if self.death_owner != Some(owner) {
            self.death_owner = Some(owner);
            self.death_effects.clear();
            self.impact_effects.clear();
        }
    }

    #[allow(clippy::too_many_arguments)]
    pub(super) fn record_ship_death(&mut self,
        ship_id: i64, born_tick: u64, ship_type: u8, side: i32,
        position: Vec2, rotation_degrees: f32, radius: f32,
        color_rgb: Option<[u8; 3]>,
    ) -> (bool, bool) {
        if !position.is_finite() || !rotation_degrees.is_finite() ||
            !radius.is_finite() || radius <= 0.0 ||
            self.death_effects.iter().any(|effect|
                effect.ship_id == ship_id && effect.born_tick == born_tick)
        {
            return (false, false);
        }
        let dropped = if self.death_effects.len() == 512 {
            self.death_effects.remove(0);
            true
        } else { false };
        self.death_effects.push(ShipDeathVisual {
            ship_id, born_tick, ship_type, side, position,
            rotation_degrees, radius: radius.clamp(1.0, 100.0),
            color_rgb,
        });
        (true, dropped)
    }

    /// Capture only genuine positive-damage events. Multiple drawing passes
    /// over one tick must not restart an effect, and old episodes cannot leak.
    pub(super) fn record_combat_impact(&mut self,
        born_tick: u64, projectile_id: i64, target_id: i64,
        position: Vec2, fatal: bool,
    ) -> (bool, bool) {
        if !position.is_finite() || self.impact_effects.iter().any(|fx|
            fx.born_tick == born_tick && fx.projectile_id == projectile_id &&
            fx.target_id == target_id) {
            return (false, false);
        }
        let dropped = if self.impact_effects.len() == 512 {
            self.impact_effects.remove(0);
            true
        } else { false };
        self.impact_effects.push(CombatImpactVisual {
            born_tick, projectile_id, target_id, position, fatal,
        });
        (true, dropped)
    }

    /// A short fixed-step flash shared by local and authenticated LAN views.
    /// Nothing here changes collision, combat RNG or training state.
    pub(super) fn draw_combat_impact_visuals(&mut self,
        tick: u64, fixed_dt: f32,
    ) -> usize {
        if !fixed_dt.is_finite() || fixed_dt <= 0.0 { return 0; }
        self.impact_effects.retain(|fx| tick >= fx.born_tick &&
            (tick - fx.born_tick) as f32 * fixed_dt < 0.3);
        let mut drawn = 0;
        for i in 0..self.impact_effects.len() {
            let fx = self.impact_effects[i];
            let progress = ((tick - fx.born_tick) as f32 * fixed_dt / 0.3)
                .clamp(0.0, 1.0);
            let radius = 0.7 + progress * (if fx.fatal { 4.0 } else { 2.4 });
            let (x, y) = self.project(fx.position);
            let pixels = radius * self.camera.pixels_per_unit;
            if x < -pixels || y < -pixels ||
                x > self.width as f32 + pixels ||
                y > self.height as f32 + pixels {
                continue;
            }
            self.shape(&ShipShape::Circle { offset: Vec2::ZERO, radius },
                Pose { position: fx.position, rotation_degrees: 0.0 },
                if fx.fatal { 0xff8e77 } else { 0xffe19c });
            drawn += 1;
        }
        drawn
    }

    /// No physics, RNG, allocation or enemy discovery is performed here.
    /// The ordinary post-render fog stencil masks every visual sample.
    /// A paused simulation keeps its remains indefinitely until time runs.
    /// Source ShipRemains.Place uses the final physical ship pose and
    /// animates its real sprite slices before Kill at five world seconds.
    /// The original 20-slice clips stop on their last sprite; no timer,
    /// corpse collider or new actor is added to physics or training.
    pub(super) fn draw_ship_death_visuals(
        &mut self, tick: u64, fixed_dt: f32,
    ) -> (usize, usize, usize) {
        if !fixed_dt.is_finite() || fixed_dt <= 0.0 {
            return (0, 0, 0);
        }
        self.death_effects.retain(|fx|
            tick >= fx.born_tick &&
            (tick - fx.born_tick) as f32 * fixed_dt < 5.0);
        let mut remains_drawn = 0;
        let mut flashes_drawn = 0;
        let mut source_frames_drawn = 0;
        let authored_remains = if self.death_effects.is_empty() {
            None
        } else {
            source_ship_remains_art()
        };
        for index in 0..self.death_effects.len() {
            let fx = self.death_effects[index];
            let seconds = (tick - fx.born_tick) as f32 * fixed_dt;
            // Beacon, Fire Barge and HumanTarget have no source remains.
            // The Fire Barge's real physical explosion continues independently.
            let has_corpse = !matches!(fx.ship_type, 1 | 10 | 23);
            if has_corpse {
                let clip = authored_remains
                    .and_then(|atlas| atlas.clips.get(usize::from(fx.ship_type)))
                    .and_then(Option::as_ref);
                let original = clip.map(|clip| {
                    let frame = ((seconds * clip.frames_per_second).floor() as usize)
                        .min(clip.frames.len() - 1);
                    (frame, &clip.frames[frame])
                });
                let art = original.map(|(_, art)| art)
                    .or_else(|| source_ship_art()
                        .and_then(|atlas| atlas.sprites.get(usize::from(fx.ship_type)))
                        .and_then(Option::as_ref));
                let tinted = art.and_then(|art| art.recolor_mask.as_ref()
                    .and_then(|_| fx.color_rgb.map(|rgb| (art, rgb))))
                    .map(|(art, rgb)| {
                        if let Some((frame, _)) = original {
                            let key = (fx.ship_type, frame, rgb);
                            if !self.colored_ship_remains.contains_key(&key) {
                                // One-time color cache; never allow an old
                                // 128-ship multiplayer match to accumulate
                                // unbounded colored corpse images.
                                if self.colored_ship_remains.len() >= 256 {
                                    self.colored_ship_remains.clear();
                                }
                                self.colored_ship_remains.insert(
                                    key, Arc::new(art.recolored(rgb)),
                                );
                            }
                            Arc::clone(self.colored_ship_remains.get(&key)
                                .expect("installed source wreck recolor"))
                        } else {
                            let key = (fx.ship_type, rgb);
                            if !self.custom_ship_art.contains_key(&key) {
                                if self.custom_ship_art.len() >= 96 {
                                    self.custom_ship_art.clear();
                                }
                                self.custom_ship_art.insert(
                                    key, Arc::new(art.recolored(rgb)),
                                );
                            }
                            Arc::clone(self.custom_ship_art.get(&key)
                                .expect("installed fallback ship recolor"))
                        }
                    });
                let sprite_drawn = art.is_some_and(|art| {
                    let sprite = tinted.as_deref().unwrap_or(&art.sprite);
                    self.draw_sprite(sprite, fx.position,
                        fx.rotation_degrees.to_radians(),
                        self.camera.pixels_per_unit / art.pixels_per_unit,
                        // Source sprite animation is opaque until its
                        // real 5s removal; only fallback stills fade.
                        if original.is_some() { 1.0 }
                        else { 0.6 * (1.0 - seconds / 5.0).clamp(0.0, 1.0) })
                });
                if sprite_drawn && original.is_some() {
                    source_frames_drawn += 1;
                }
                if !sprite_drawn {
                    // Missing optional atlas should never erase the
                    // authoritative destruction marker.
                    self.cross(fx.position, fx.radius.min(4.0), 0x66717d);
                }
                remains_drawn += 1;
            }
            if seconds < 0.4 {
                let progress = seconds / 0.4;
                let radius = fx.radius * (0.75 + 1.5 * progress);
                self.shape(&ShipShape::Circle {
                    offset: Vec2::ZERO, radius,
                }, Pose { position: fx.position, rotation_degrees: 0.0 },
                if fx.side == 1 { 0xffca81 } else { 0xffa281 });
                flashes_drawn += 1;
            }
        }
        (remains_drawn, flashes_drawn, source_frames_drawn)
    }

    pub fn size(&self) -> (usize, usize) { (self.width, self.height) }
    pub fn pixels(&self) -> &[u32] { &self.pixels }

    /// Remote clients draw the host's authenticated physical snapshots
    /// through the EXACT SAME sprite atlas, pivot, PPU and rasterizer as
    /// local gameplay. Only pixels are produced; no shadow physics,
    /// collisions, policy state, or second ship asset definitions exist.
    #[cfg(feature = "client")]
    pub(super) fn begin_remote_art(&mut self, camera: NativeCamera) {
        self.camera = camera;
        self.sprite_raster_candidate_pixels = 0;
        self.sprite_raster_opaque_pixels = 0;
        self.sprite_raster_blended_pixels = 0;
        self.pixels.fill(0x080f1a);
    }

    #[cfg(feature = "client")]
    pub(super) fn remote_raster_counts(&self) -> (u64, u64, u64) {
        (
            self.sprite_raster_candidate_pixels,
            self.sprite_raster_opaque_pixels,
            self.sprite_raster_blended_pixels,
        )
    }

    #[cfg(feature = "client")]
    pub(super) fn remote_ship_recolor_cache_size(&self) -> usize {
        self.custom_ship_art.len() + self.animated_ship_art.len()
    }

    #[cfg(feature = "client")]
    pub(super) fn remote_ship_art(
        &mut self, ship_type: u8, point: Vec2, rotation_degrees: f32,
        color_rgb: Option<[u8; 3]>, animation: Option<(u8, f32)>,
    ) -> bool {
        // Authenticated host packets select a presentation clip only for
        // already visible ships. Both local and remote use the *same*
        // native original-art decoder and exact source squad recolor mask.
        let animated = animation.and_then(|(variant, elapsed)| {
            if variant > 1 || !elapsed.is_finite() || elapsed < 0.0 {
                return None;
            }
            let art = source_ship_animation_art()?
                .clips.get(usize::from(ship_type) * 2 + usize::from(variant))
                .and_then(Option::as_ref)?;
            let frame = ((elapsed * art.frames_per_second).floor() as usize)
                % art.frames.len();
            Some((art, variant, frame))
        });
        let Some(art) = animated
            .map(|(clip, _, frame)| &clip.frames[frame])
            .or_else(|| source_ship_art()
                .and_then(|atlas| atlas.sprites.get(usize::from(ship_type)))
                .and_then(Option::as_ref))
        else { return false };
        let recolored = color_rgb.filter(|_| art.recolor_mask.is_some())
            .map(|rgb| {
                if let Some((_, variant, frame)) = animated {
                    let key = (ship_type, variant, frame, rgb);
                    if !self.animated_ship_art.contains_key(&key) {
                        if self.animated_ship_art.len() >= 512 {
                            self.animated_ship_art.clear();
                        }
                        self.animated_ship_art.insert(
                            key, Arc::new(art.recolored(rgb)),
                        );
                    }
                    Arc::clone(self.animated_ship_art.get(&key)
                        .expect("installed animated native squad recolor"))
                } else {
                    let key = (ship_type, rgb);
                    if !self.custom_ship_art.contains_key(&key) {
                        if self.custom_ship_art.len() >= 96 {
                            self.custom_ship_art.clear();
                        }
                        self.custom_ship_art.insert(
                            key, Arc::new(art.recolored(rgb)),
                        );
                    }
                    Arc::clone(self.custom_ship_art.get(&key)
                        .expect("installed static native squad recolor"))
                }
            });
        let sprite = recolored.as_deref().unwrap_or(&art.sprite);
        self.draw_sprite(
            sprite, point, rotation_degrees.to_radians(),
            self.camera.pixels_per_unit / art.pixels_per_unit, 1.0,
        )
    }

    #[cfg(feature = "client")]
    pub(super) fn remote_projectile_art(
        &mut self, shot_type: u8, point: Vec2, rotation_degrees: f32,
        elapsed_seconds: f32,
    ) -> bool {
        let Some(art) = source_projectile_art()
            .and_then(|atlas| atlas.sprites.get(usize::from(shot_type)))
            .and_then(Option::as_ref)
        else { return false };
        let frame = (elapsed_seconds.max(0.0) * 8.0).floor() as usize;
        let index = if matches!(shot_type, 13 | 14) {
            frame.min(art.frames.len() - 1)
        } else {
            frame % art.frames.len()
        };
        self.draw_sprite(
            &art.frames[index], point, rotation_degrees.to_radians(),
            self.camera.pixels_per_unit / art.pixels_per_unit, 1.0,
        )
    }

    #[cfg(feature = "client")]
    pub(super) fn remote_asteroid_art(
        &mut self, prefab: u8, point: Vec2, rotation_degrees: f32,
    ) -> bool {
        let Some(art) = source_asteroid_art()
            .and_then(|atlas| atlas.sprites.get(usize::from(prefab)))
            .and_then(Option::as_ref)
        else { return false };
        self.draw_sprite(
            &art.sprite, point, rotation_degrees.to_radians(),
            self.camera.pixels_per_unit / art.pixels_per_unit, 1.0,
        )
    }

    /// Resize presentation storage only. Preserve camera world scale and
    /// center, so exposing more pixels shows more of the same battlefield.
    /// Never allocate a new simulation or touch the authoritative world.
    pub fn resize(&mut self, width: usize, height: usize) -> io::Result<bool> {
        if width == self.width && height == self.height { return Ok(false); }
        if width == 0 || height == 0 || width > 8192 || height > 8192 {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "invalid viewport size"));
        }
        self.pixels.resize(width * height, 0);
        if !self.fog_coverage.is_empty() {
            self.fog_coverage.resize(width * height, 0);
        }
        self.width = width;
        self.height = height;
        self.selection_box = None;
        if tactical_hud::shooting_rect(width, height, 0).is_none() {
            self.show_shooting_menu = false;
        }
        Ok(true)
    }

    /// The same coordinate frame used by drawing and click/drag navigation.
    /// Returns None when the overview is hidden or the window is too small.
    pub fn minimap_world_at(&self, bounds: Aabb, x: f32, y: f32) -> Option<Vec2> {
        if self.show_shooting_menu { return None; }
        self.show_minimap.then(|| MiniMapLayout::new(self.width, self.height, bounds))
            .flatten()?.world_at(x, y)
    }

    /// UI-only action hit-test; the client converts intent into the exact
    /// same typed player inputs used by keyboard shortcuts and replay.
    pub fn tactical_action_at(&self, x: f32, y: f32) -> Option<super::HudAction> {
        if self.show_shooting_menu { return None; }
        let action = tactical_hud::hit(self.width, self.height, x, y)?;
        (action == super::HudAction::SelectAll ||
            !self.selection.squads().is_empty()).then_some(action)
    }

    /// Pick a live, user-controllable source squad by the exact tab drawn in
    /// this same render frame, not from a guessed row/squad index.
    pub fn squad_tab_at(&self, view: &NativeRenderWorldView<'_>,
        x: f32, y: f32,
    ) -> Option<bees_hivemind::session::SquadLifetime> {
        if self.show_shooting_menu { return None; }
        let mut row = 0usize;
        for &owner in view.roster.source_order() {
            let Some(squad) = view.roster.get(owner) else { continue };
            let Some(number) = squad.source_hotkey_number else { continue };
            if number > 10 || !view.is_local_player_squad(owner) {
                continue;
            }
            let Some(&ship_id) = squad.members.first() else { continue };
            let Some(ship) = view.combat.ship(ship_id) else { continue };
            let key = if number == 10 { 0 } else { number };
            let label = format!("{key}: type {} / {} ships",
                ship.ship_type, squad.members.len());
            let rect = tactical_hud::squad_tab_rect(
                self.width, self.height, row, text::HEIGHT,
                text::width(&label, 1))?;
            if rect.contains(x, y) { return Some(owner); }
            row += 1;
            if row == 10 { break; }
        }
        None
    }

    pub fn shooting_strategy_at(&self, x: f32, y: f32) -> Option<u8> {
        if !self.show_shooting_menu { return None; }
        tactical_hud::shooting_hit(self.width, self.height, x, y)
    }

    /// Reuse the player squad's actual current ownership, and the source
    /// FogOfWarVision mask radius (half its 2x sight-range diameter). A policy-controlled or enemy hull can never
    /// reveal the local player's map merely because its geometry is nearby.
    fn fog_source_radius(view: &NativeRenderWorldView<'_>, ship_id: i64) -> Option<f32> {
        let owner = view.roster.owning_squad(ship_id)?;
        if !view.is_local_player_squad(owner) { return None; }
        view.player_authority.player_fog_vision_radius(ship_id)
            .filter(|radius| radius.is_finite() && *radius > 0.0)
    }

    /// UI-only hit testing must not choose a ship or obstacle hidden by
    /// the source fog mask. The world continues simulating every entity.
    fn fog_visible_at(&self, view: &NativeRenderWorldView<'_>, point: Vec2) -> bool {
        if !view.player_authority.player_fog_of_war_enabled() { return true; }
        if !point.is_finite() { return false; }
        if view.combat.render_ships().any(|(ship, body)| {
            let Some(radius) = Self::fog_source_radius(view, ship.entity_id)
                else { return false };
            let delta = body.motion.position - point;
            delta.length_squared() <= radius * radius
        }) {
            return true;
        }
        // This is the same transient SpriteMask fade the renderer paints.
        // Never let a fade from a previous world/episode grant visibility.
        self.fog_context == Some((view.world_id, view.episode_revision,
            view.player_authority.player_viewer_side())) &&
            self.fog_fades.iter().any(|fade| {
                let radius = fade.current_radius(view.tick, view.fixed_step_seconds);
                radius >= 1.5 &&
                    (fade.position - point).length_squared() <= radius * radius
            })
    }

    /// Rasterize overlapping source sight discs into one reusable screen
    /// mask. Each observer costs one square root per covered scanline, not
    /// one geometry test for every pixel or other ship. No fog work happens
    /// in the headless simulation or in levels whose source disables fog.
    /// The inexpensive fast path for a source mask covering all four
    /// viewport corners. Only one fully covering disc is needed; unions
    /// of smaller discs take the regular stencil path.
    fn fog_covers_viewport(&self, position: Vec2, source_radius: f32) -> bool {
        let radius = source_radius * self.camera.pixels_per_unit;
        if !radius.is_finite() || radius <= 0.0 { return false; }
        let (x, y) = self.project(position);
        if !x.is_finite() || !y.is_finite() { return false; }
        let dx = x.abs().max((self.width as f32 - x).abs());
        let dy = y.abs().max((self.height as f32 - y).abs());
        radius * radius >= dx * dx + dy * dy
    }

    /// Fill only visible scanline intervals. Keeping this helper free of
    /// renderer borrows allows iterating the reusable live-source map
    /// without allocating/copying it into a second visual registry.
    fn reveal_fog_circle(
        stencil: &mut [u8], camera: NativeCamera, width: usize, height: usize,
        position: Vec2, source_radius: f32,
    ) {
        let radius = source_radius * camera.pixels_per_unit;
        if !radius.is_finite() || radius <= 0.0 { return; }
        let cx = (position.x - camera.center.x) * camera.pixels_per_unit + width as f32 * 0.5;
        let cy = (camera.center.y - position.y) * camera.pixels_per_unit + height as f32 * 0.5;
        if !cx.is_finite() || !cy.is_finite() ||
            cx + radius < 0.0 || cx - radius >= width as f32 ||
            cy + radius < 0.0 || cy - radius >= height as f32 {
            return;
        }
        let first_y = (cy - radius).floor().max(0.0) as usize;
        let last_y = (cy + radius).ceil().min(height as f32 - 1.0) as usize;
        let radius_squared = radius * radius;
        for y in first_y..=last_y {
            let dy = y as f32 + 0.5 - cy;
            if dy.abs() > radius { continue; }
            let half_width = (radius_squared - dy * dy).max(0.0).sqrt();
            if cx + half_width < 0.0 || cx - half_width >= width as f32 {
                continue;
            }
            let first_x = (cx - half_width).ceil().max(0.0) as usize;
            let last_x = (cx + half_width).floor()
                .min(width as f32 - 1.0) as usize;
            if first_x <= last_x {
                let row = y * width;
                stencil[row + first_x..=row + last_x].fill(1);
            }
        }
    }

    fn apply_player_fog(&mut self, view: &NativeRenderWorldView<'_>,
        counts: &mut NativeRenderCounts,
    ) {
        if !view.player_authority.player_fog_of_war_enabled() {
            self.fog_live.clear();
            self.fog_fades.clear();
            self.fog_context = None;
            return;
        }
        // A viewer-side switch in the same Level/episode must not inherit
        // another player's dying-ship reveal masks.
        let context = (view.world_id, view.episode_revision,
            view.player_authority.player_viewer_side());
        if self.fog_context != Some(context) {
            self.fog_live.clear();
            self.fog_fades.clear();
            self.fog_context = Some(context);
            self.fog_render_pass = 0;
        }
        self.fog_render_pass = self.fog_render_pass.wrapping_add(1);
        if self.fog_render_pass == 0 {
            self.fog_render_pass = 1;
            self.fog_live.clear();
        }

        // Minimap visibility reuses this buffer after the main viewport,
        // including when one mask covers everything or none remains.
        self.fog_coverage.resize(self.pixels.len(), 0);
        let mut full_viewport = false;
        for (ship, body) in view.combat.render_ships() {
            let Some(radius) = Self::fog_source_radius(view, ship.entity_id)
                else { continue };
            counts.fog_sources += 1;
            self.fog_live.insert(ship.entity_id, FogLiveSource {
                position: body.motion.position, radius,
                seen_pass: self.fog_render_pass,
            });
            full_viewport |= self.fog_covers_viewport(body.motion.position, radius);
        }
        self.pending_fog_fades.clear();
        let pass = self.fog_render_pass;
        self.fog_live.retain(|&id, last| {
            if last.seen_pass == pass { return true; }
            if view.combat.ship(id).is_some_and(|ship| ship.vitals.is_dead()) {
                self.pending_fog_fades.push(FogFade {
                    position: last.position, radius: last.radius,
                    born_tick: view.tick,
                });
            }
            false
        });
        self.fog_fades.append(&mut self.pending_fog_fades);
        self.fog_fades.retain(|fade|
            fade.current_radius(view.tick, view.fixed_step_seconds) >= 1.5);
        for fade in &self.fog_fades {
            counts.fog_fading_sources += 1;
            full_viewport |= self.fog_covers_viewport(fade.position,
                fade.current_radius(view.tick, view.fixed_step_seconds));
        }
        if full_viewport {
            // Nothing is obscured; skip the O(screen pixels) fill, raster,
            // and masking pass entirely. Minimap visibility is still
            // computed from actual source discs.
            counts.fog_full_viewport_skips = 1;
            return;
        }
        if counts.fog_sources == 0 && counts.fog_fading_sources == 0 {
            // An empty player fleet has no sight. Clearing the final image
            // directly avoids two full-resolution stencil passes.
            self.pixels.fill(0x080e19);
            counts.fog_occluded_pixels = self.pixels.len();
            counts.fog_empty_viewport_fills = 1;
            return;
        }

        self.fog_coverage.fill(0);
        let stencil = &mut self.fog_coverage;
        let camera = self.camera;
        let width = self.width;
        let height = self.height;
        for source in self.fog_live.values() {
            Self::reveal_fog_circle(stencil, camera, width, height,
                source.position, source.radius);
        }
        for fade in &self.fog_fades {
            Self::reveal_fog_circle(stencil, camera, width, height,
                fade.position, fade.current_radius(view.tick, view.fixed_step_seconds));
        }
        for (pixel, visible) in self.pixels.iter_mut().zip(stencil.iter()) {
            if *visible == 0 {
                *pixel = 0x080e19;
                counts.fog_occluded_pixels += 1;
            }
        }
    }

    /// Use the authoritative hull and healing-zone colliders for clicks,
    /// retaining screen-space tolerance for small ships. Large hull edges
    /// must remain selectable even when their centers are offscreen.
    /// This read-only UI query never changes gameplay collision state.
    pub fn pick_ship(&self, view: &NativeRenderWorldView<'_>, x: f32, y: f32) -> Option<i64> {
        if !x.is_finite() || !y.is_finite() { return None; }
        let point = self.camera.screen_to_world(x, y, self.width, self.height);
        if !self.fog_visible_at(view, point) { return None; }
        let radius = 18.0 / self.camera.pixels_per_unit;
        let padding = Vec2::new(radius, radius);
        let bounds = Aabb { min: point - padding, max: point + padding };
        view.combat.physics().input_ship_ids_overlapping_box(bounds).ok()?
            .filter_map(|id| {
                let ship = view.combat.ship(id)?;
                if ship.vitals.is_dead() { return None; }
                let position = view.combat.ship_body(id)?.motion.position;
                let (sx, sy) = self.project(position);
                let distance_squared = (sx - x).powi(2) + (sy - y).powi(2);
                Some((distance_squared, id))
            })
            .min_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)))
            .map(|(_, id)| id)
    }

    /// Target selection uses cached authored hull overlap, with a small screen
    /// tolerance. This emits only identities; the host revalidates every action.
    pub fn pick_support_action(&self, view: &NativeRenderWorldView<'_>,
        mode: super::NativeSupportMode, point: Vec2, patrol_start: Option<Vec2>,
    ) -> Option<bees_gameplay::player_inputs::NativePlayerSupportAction> {
        use super::NativeSupportMode as Mode;
        use bees_gameplay::player_inputs::NativePlayerSupportAction as Action;
        if !point.is_finite() { return None; }
        if mode == Mode::Patrol {
            let start = patrol_start.filter(|start| start.is_finite())?;
            return Some(Action::Patrol {
                top_left: Vec2::new(start.x.min(point.x), start.y.max(point.y)),
                bottom_right: Vec2::new(start.x.max(point.x), start.y.min(point.y)),
            });
        }
        if !self.fog_visible_at(view, point) { return None; }
        let radius = 5.0 / self.camera.pixels_per_unit;
        let bounds = Aabb { min: point - Vec2::new(radius, radius),
            max: point + Vec2::new(radius, radius) };
        if mode == Mode::Mining {
            let id = view.combat.physics().obstacle_ids_overlapping_box(bounds).ok()?
                .filter_map(|id| view.combat.mining_asteroid_position(id)
                    .map(|position| ((position - point).length_squared(), id)))
                .min_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)))?.1;
            return Some(Action::Mining { asteroid_id: id });
        }
        let selected_side = self.selection.squads().first()
            .and_then(|owner| view.roster.get(*owner)).map(|squad| squad.side);
        let id = view.combat.physics().input_ship_ids_overlapping_box(bounds).ok()?
            .filter_map(|id| {
                let ship = view.combat.ship(id)?;
                if ship.vitals.is_dead() || selected_side.is_none() ||
                    selected_side.is_some_and(|side|
                        if matches!(mode, Mode::Attack | Mode::Circle | Mode::RightSwipe |
                            Mode::LeftSwipe | Mode::InAndOut) {
                            ship.side == side
                        } else {
                            ship.side != side
                        }) ||
                    (mode == Mode::Heal && ship.ship_type != bees_sim::combat_world::BEEHIVE_TYPE) ||
                    (mode == Mode::FullRetreat && ship.ship_type != bees_sim::combat_world::WARP_GATE_TYPE) {
                    return None;
                }
                let position = view.combat.ship_body(id)?.motion.position;
                Some(((position - point).length_squared(), id))
            }).min_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)))?.1;
        match mode {
            Mode::Attack => Some(Action::Attack { target: view.roster.owning_squad(id)? }),
            Mode::Circle => Some(Action::Circle { target: view.roster.owning_squad(id)? }),
            Mode::RightSwipe => Some(Action::RightSwipe { target: view.roster.owning_squad(id)? }),
            Mode::LeftSwipe => Some(Action::LeftSwipe { target: view.roster.owning_squad(id)? }),
            Mode::InAndOut => Some(Action::InAndOut { target: view.roster.owning_squad(id)? }),
            Mode::Guard => Some(Action::Guard { target: view.roster.owning_squad(id)? }),
            // Source input sends every Beehive in the clicked squad, in
            // GetShips order, rather than silently restricting it to one hull.
            Mode::Heal => Some(Action::Heal { beehive_ids: view.roster.get(view.roster.owning_squad(id)?)?
                .members.iter().copied().filter(|id| view.combat.ship(*id).is_some_and(|ship|
                    !ship.vitals.is_dead() && ship.ship_type == bees_sim::combat_world::BEEHIVE_TYPE))
                .collect() }),
            Mode::FullRetreat => Some(Action::FullRetreat { warp_gate_id: id }),
            _ => None,
        }
    }

    /// A demolition objective is a real, destructible MapObject. Interpret
    /// a right-click on its collider as the standard point-fire action,
    /// instead of ordering selected attackers to drive into its hitbox.
    /// Other objects and ground retain existing context-click behavior.
    pub fn pick_demolition_fire_point(
        &self, view: &NativeRenderWorldView<'_>, point: Vec2,
    ) -> Option<Vec2> {
        let objective = view.demolition?;
        if !point.is_finite() || self.camera.pixels_per_unit <= 0.0 ||
            self.selection.squads().is_empty() ||
            self.selection.squads().iter().any(|&owner|
                view.roster.get(owner).is_none_or(|squad|
                    squad.side != objective.attacking_side))
        {
            return None;
        }
        let id = objective.target_map_object_id;
        let target = view.combat.map_object(id)?;
        if target.health <= 0 { return None; }
        let body = view.combat.obstacle_body(id).filter(|body| body.active)?;
        let bounds = body.primary_bounds()?;
        let tolerance = 6.0 / self.camera.pixels_per_unit;
        if point.x < bounds.min.x - tolerance ||
            point.x > bounds.max.x + tolerance ||
            point.y < bounds.min.y - tolerance ||
            point.y > bounds.max.y + tolerance
        {
            return None;
        }
        Some((bounds.min + bounds.max) * 0.5)
    }

    /// Right-click uses the same target resolution as the explicit tools.
    /// Ground movement is the caller's fallback when no command target is hit.
    pub fn pick_right_click_action(
        &self, view: &NativeRenderWorldView<'_>, point: Vec2,
    ) -> Option<bees_gameplay::player_inputs::NativePlayerSupportAction> {
        use super::NativeSupportMode as Mode;
        if self.selection.squads().is_empty() { return None; }
        [Mode::Attack, Mode::FullRetreat, Mode::Heal, Mode::Mining].into_iter()
            .find_map(|mode| self.pick_support_action(view, mode, point, None))
    }

    pub fn render(&mut self, view: &NativeRenderWorldView<'_>, bounds: Aabb) -> NativeRenderCounts {
        self.selection.synchronize(view);
        // Selected squad count can approach ship count in large battles.
        // Keep 1v1 allocation-free and reuse the large-selection lookup
        // rather than allocating a new hash table every rendered frame.
        let many_selected = self.selection.squads().len() > 8;
        self.selected_squad_lookup.clear();
        if many_selected {
            self.selected_squad_lookup.extend(self.selection.squads().iter().copied());
        }
        // The fixed software viewport is axis-aligned in world space.
        // Margin preserves center markers, health and selected-ship UI at
        // its edges. Cached physical hull bounds stay authoritative.
        let pad = Vec2::new(24.0, 24.0) * (1.0 / self.camera.pixels_per_unit);
        let viewport = Aabb {
            min: self.camera.screen_to_world(
                0.0, self.height as f32, self.width, self.height,
            ) - pad,
            max: self.camera.screen_to_world(
                self.width as f32, 0.0, self.width, self.height,
            ) + pad,
        };
        self.pixels.fill(0x080e19);
        self.sprite_raster_candidate_pixels = 0;
        self.sprite_raster_opaque_pixels = 0;
        self.sprite_raster_blended_pixels = 0;
        let mut counts = NativeRenderCounts::default();
        self.shape(&ShipShape::Box { offset: (bounds.min + bounds.max) * 0.5,
            half_extents: (bounds.max - bounds.min) * 0.5 },
            Pose { position: Vec2::ZERO, rotation_degrees: 0.0 }, 0x33455e);
        if let Some(zone) = view.combat.active_extraction_bounds()
            .or_else(|| view.combat.extraction_objective().map(|(_, bounds, _)| bounds))
        {
            self.shape(&ShipShape::Box { offset: (zone.min + zone.max) * 0.5,
                half_extents: (zone.max - zone.min) * 0.5 },
                Pose { position: Vec2::ZERO, rotation_degrees: 0.0 }, 0x75dbab);
        }
        let asteroid_art = source_asteroid_art();
        counts.asteroid_art_types_loaded = asteroid_art.map_or(0, |art| art.loaded);
        for (obstacle, body) in view.combat.render_obstacles() {
            counts.obstacles += 1;
            if body.primary_bounds().is_some_and(|hull| !hull.overlaps(viewport)) {
                counts.culled_obstacles += 1;
                continue;
            }
            let object = if obstacle.kind == bees_sim::obstacle::ObstacleKind::MapObject {
                view.combat.map_object(obstacle.entity_id)
            } else { None };
            let visible = view.combat.range_visible_map_object_ids().contains(&obstacle.entity_id);
            let is_fire_tank = object.is_some_and(|object| object.kind == bees_sim::map_object::MapObjectKind::FireTank);
            if let Some(tank) = object.filter(|_| is_fire_tank) {
                self.draw_fire_tank(tank, body.pose, view.tick as f32 * view.fixed_step_seconds);
            } else if let Some(art) = asteroid_art
                .filter(|_| obstacle.kind == bees_sim::obstacle::ObstacleKind::CollisionAsteroid)
                .and_then(|atlas| body.source_asteroid_prefab_number
                    .and_then(|prefab| atlas.sprites.get(usize::from(prefab))))
                .and_then(Option::as_ref)
            {
                if self.draw_sprite(&art.sprite, body.pose.position,
                    body.pose.rotation_degrees.to_radians(),
                    self.camera.pixels_per_unit / art.pixels_per_unit, 1.0) {
                    counts.asteroid_sprite_draws += 1;
                } else {
                    counts.asteroid_fallback_hulls += 1;
                    self.shape(body.shape(), body.pose, 0x758496);
                }
            } else {
                if obstacle.kind == bees_sim::obstacle::ObstacleKind::CollisionAsteroid {
                    counts.asteroid_fallback_hulls += 1;
                }
                self.shape(body.shape(), body.pose, if visible { 0x9bd2ae } else { 0x758496 });
            }
            if let Some(object) = object.filter(|_| visible) {
                let color = if is_fire_tank { 0xe1a34b } else { 0x9bd2ae };
                let (x, y) = self.project(body.pose.position);
                let health = (object.health as f32 / object.max_health as f32).clamp(0.0, 1.0);
                self.line(x - 10.0, y - 12.0, x + 10.0, y - 12.0, 0x3c2837);
                self.line(x - 10.0, y - 12.0, x - 10.0 + 20.0 * health, y - 12.0, color);
                if view.demolition.is_some_and(|objective|
                    objective.target_map_object_id == object.entity_id
                ) {
                    // Use the source's discovered MapObject visibility; no
                    // objective overlay can leak a fog-hidden collider.
                    let marker = 0xffd57a;
                    self.line(x - 16.0, y - 18.0, x - 5.0, y - 18.0, marker);
                    self.line(x + 5.0, y - 18.0, x + 16.0, y - 18.0, marker);
                    self.line(x - 16.0, y - 18.0, x - 16.0, y + 9.0, marker);
                    self.line(x + 16.0, y - 18.0, x + 16.0, y + 9.0, marker);
                }
            }
        }
        for piece in view.combat.render_asteroid_pieces() {
            counts.pieces += 1;
            let position = piece.pose.position;
            if position.x < viewport.min.x || position.x > viewport.max.x ||
                position.y < viewport.min.y || position.y > viewport.max.y
            {
                counts.culled_pieces += 1;
                continue;
            }
            let art = asteroid_art
                .and_then(|atlas| atlas.sprites.get(usize::from(piece.prototype.prefab_number)))
                .and_then(Option::as_ref);
            if art.is_some_and(|art| self.draw_sprite(
                &art.sprite, piece.pose.position, piece.pose.rotation_degrees.to_radians(),
                self.camera.pixels_per_unit / art.pixels_per_unit,
                piece.sprite_opacity(),
            )) {
                counts.asteroid_piece_sprite_draws += 1;
            } else {
                let c = (piece.sprite_opacity() * 160.0) as u32;
                self.cross(position, 1.0, c * 0x010101);
            }
        }
        self.render_obstacle_debris(view, &mut counts);
        let ship_art = source_ship_art();
        let ship_animations = source_ship_animation_art();
        counts.ship_art_types_loaded = ship_art.map_or(0, |atlas| atlas.loaded);
        counts.ship_animation_clips_loaded =
            ship_animations.map_or(0, |atlas| atlas.loaded);
        self.command_target_ship_ids.clear();
        for &owner in self.selection.squads() {
            let Some(selection) = view.commands.current_selection(owner) else { continue };
            if !matches!(selection.origin(),
                bees_gameplay::commands::NativeCommandOrigin::Local(local)
                if local.source == bees_gameplay::commands::NativeLocalCommandSource::Player
            ) || !matches!(selection.kind(),
                bees_hivemind::dispatch::LocalCommandKind::Aggressive |
                bees_hivemind::dispatch::LocalCommandKind::BombingRun)
            {
                continue;
            }
            let Some(enemy) = view.commands.selected_enemy(owner)
                .and_then(|owner| view.roster.get(owner)) else { continue };
            self.command_target_ship_ids.extend(enemy.members.iter().copied());
        }
        for (ship, body) in view.combat.render_ships() {
            counts.ships += 1;
            let selected_ship = view.roster.owning_squad(ship.entity_id).is_some_and(|owner|
                if many_selected { self.selected_squad_lookup.contains(&owner) }
                else { self.selection.squads().contains(&owner) }
            );
            let position = body.motion.position;
            let center_visible = position.x >= viewport.min.x &&
                position.x <= viewport.max.x &&
                position.y >= viewport.min.y &&
                position.y <= viewport.max.y;
            // Selected offscreen ships still draw their movement and target
            // lines, which may cross the camera. Never cull those commands.
            if !selected_ship && !center_visible &&
                body.primary_bounds().is_some_and(|hull| !hull.overlaps(viewport))
            {
                counts.culled_ships += 1;
                continue;
            }
            let color = side_color(ship.side);
            let pose = Pose { position: body.motion.position,
                rotation_degrees: body.motion.rotation_degrees };
            // Only presentation pixels change: the exact ship collider is
            // still used for collision, selection, range and fog. The
            // source PNG's own Unity PPU controls its rendered world size.
            let owner = view.roster.owning_squad(ship.entity_id);
            // Mining sprites begin only after the source MiningRun
            // registered this ship's true asteroid collider contact.
            // WarpGate sprites use the source opening/readiness clock;
            // no renderer timer can cause a ship to warp.
            let visual_phase = match ship.ship_type {
                4 | 9 if owner.is_some_and(|squad|
                    view.commands.ship_is_mining(squad, ship.entity_id)) =>
                    Some((0usize, None)),
                20 => view.combat.game_warp_gate_animation_elapsed(ship.entity_id)
                    .map(|elapsed| (usize::from(
                        view.combat.game_warp_gate_ready(ship.entity_id)
                    ), Some(elapsed))),
                _ => None,
            };
            let animated = visual_phase.and_then(|(variant, elapsed)| {
                let atlas = ship_animations?;
                let clip = atlas.clips.get(usize::from(ship.ship_type) * 2 + variant)
                    .and_then(Option::as_ref)?;
                let index = if let Some(opening) = elapsed.filter(|_| variant == 0) {
                    ((opening * clip.frames_per_second).floor() as usize)
                        .min(clip.frames.len() - 1)
                } else {
                    ((view.tick as f32 * view.fixed_step_seconds
                        * clip.frames_per_second).floor() as usize) % clip.frames.len()
                };
                Some((clip, variant, index))
            });
            let art = animated
                .map(|(clip, _, frame)| &clip.frames[frame])
                .or_else(|| ship_art
                    .and_then(|atlas| atlas.sprites.get(usize::from(ship.ship_type)))
                    .and_then(Option::as_ref));
            // Each live squad retains its genuine custom color. Animated
            // frames use the same prepared source mask as the static atlas.
            let custom_rgb = owner
                .and_then(|owner| view.player_authority.player_squad_color_rgb(owner));
            let tinted = art.and_then(|art| art.recolor_mask.as_ref()
                .and_then(|_| custom_rgb.map(|rgb| (art, rgb))))
                .map(|(art, rgb)| {
                    if let Some((_, variant, frame)) = animated {
                        let key = (ship.ship_type, variant as u8, frame, rgb);
                        if !self.animated_ship_art.contains_key(&key) {
                            if self.animated_ship_art.len() >= 512 {
                                self.animated_ship_art.clear();
                            }
                            self.animated_ship_art.insert(
                                key, Arc::new(art.recolored(rgb)),
                            );
                            counts.ship_color_cache_misses += 1;
                        }
                        Arc::clone(self.animated_ship_art.get(&key)
                            .expect("just inserted animated source recolor"))
                    } else {
                        let key = (ship.ship_type, rgb);
                        if !self.custom_ship_art.contains_key(&key) {
                            if self.custom_ship_art.len() >= 96 {
                                self.custom_ship_art.clear();
                            }
                            self.custom_ship_art.insert(
                                key, Arc::new(art.recolored(rgb)),
                            );
                            counts.ship_color_cache_misses += 1;
                        }
                        Arc::clone(self.custom_ship_art.get(&key)
                            .expect("just inserted static source recolor"))
                    }
                });
            let drew_art = art.is_some_and(|art| {
                let sprite = tinted.as_deref().unwrap_or(&art.sprite);
                self.draw_sprite(sprite, pose.position, pose.rotation_degrees.to_radians(),
                    self.camera.pixels_per_unit / art.pixels_per_unit, 1.0)
            });
            if drew_art {
                counts.ship_sprite_draws += 1;
                counts.ship_animated_sprite_draws += usize::from(animated.is_some());
                counts.custom_ship_color_draws += usize::from(tinted.is_some());
            } else {
                counts.ship_fallback_hulls += 1;
            }
            // Keep geometry visible for selected ships and when artwork
            // is absent; do not redraw outlines over every rendered sprite.
            if !drew_art || selected_ship {
                self.shape(body.shape(), pose, color);
                self.cross(body.motion.position, 1.5, color);
            }
            let (x, y) = self.project(body.motion.position);
            if view.survival.is_some_and(|survival| survival.protected_ship_id == ship.entity_id) {
                let marker = 0xffd873;
                self.line(x - 5.0, y - 21.0, x, y - 16.0, marker);
                self.line(x, y - 16.0, x + 5.0, y - 21.0, marker);
                self.line(x + 5.0, y - 21.0, x, y - 26.0, marker);
                self.line(x, y - 26.0, x - 5.0, y - 21.0, marker);
            }
            if self.command_target_ship_ids.contains(&ship.entity_id) {
                // Source Squad.MarkTargets shows each surviving enemy
                // ship targeted by a player Aggressive/BombingRun order.
                // Draw in the existing culled ship pass and use actual
                // physical positions; do not maintain a target overlay
                // whose contents can outlive a command or squad.
                let marker = 0xffba6d;
                self.line(x - 11.0, y, x, y - 11.0, marker);
                self.line(x, y - 11.0, x + 11.0, y, marker);
                self.line(x + 11.0, y, x, y + 11.0, marker);
                self.line(x, y + 11.0, x - 11.0, y, marker);
                counts.command_target_markers += 1;
            }
            // Source Striker.LoadedIndicator follows its persistent
            // ship-owned Bomb readiness, not the current command or
            // selection. The same post-pass fog mask hides enemy markers
            // outside the local player's authentic vision.
            if ship.ship_type == 19 {
                if let Some(loaded) = view.combat.game_striker_bomb_ready(ship.entity_id)
                    .or_else(|| view.combat.policy_striker_bomb_ready(ship.entity_id)) {
                    self.line(x + 12.0, y - 9.0, x + 17.0, y - 9.0,
                        if loaded { 0x87dfad } else { 0x987d7d });
                    counts.striker_bomb_indicators += 1;
                }
            }
            let health = ship.vitals.health() as f32 / ship.vitals.max_health() as f32;
            self.line(x - 10.0, y - 12.0, x + 10.0, y - 12.0, 0x3c2837);
            self.line(x - 10.0, y - 12.0, x - 10.0 + 20.0 * health, y - 12.0, color);
            if selected_ship {
                self.line(x - 14.0, y - 17.0, x + 14.0, y - 17.0, 0xffffff);
                self.line(x - 14.0, y + 14.0, x + 14.0, y + 14.0, 0xffffff);
                for slot in 0..5 {
                    if let Ok(Some(charge)) = view.combat.weapon_charge(ship.entity_id, slot) {
                        let progress = (charge.elapsed_seconds / charge.event_seconds).clamp(0.0, 1.0);
                        let cy = y + 18.0 + slot as f32 * 3.0;
                        self.line(x - 10.0, cy, x + 10.0, cy, 0x34435a);
                        self.line(x - 10.0, cy, x - 10.0 + 20.0 * progress, cy,
                            if charge.paused { 0xe1a34b } else { 0x62b9ff });
                    }
                }
                if self.show_weapon_ranges {
                    for slot in 0..5 {
                        let range = match view.combat.weapon_range(ship.entity_id, slot) {
                            Ok(Some(range)) => range,
                            Ok(None) => continue,
                            Err(_) => { counts.missing_range_data += 1; continue; },
                        };
                        if let Ok(Some(automatic)) = view.combat.weapon_automatic_status(ship.entity_id, slot) {
                            counts.automatic_targets += usize::from(
                                automatic.target_ship.is_some() || automatic.asteroid_aim_point.is_some(),
                            );
                            if let Some(point) = automatic.asteroid_aim_point {
                                self.cross(point, 2.5, 0xf6df85);
                                counts.aim_points += 1;
                            }
                        }
                        // The same post-physics RangeCollider contact list
                        // used by automatic firing is already indexed by
                        // weapon. Do not scan the whole fleet again for each
                        // selected turret merely to render its range overlay.
                        let contacts = match view.combat.weapon_range_contacts(ship.entity_id, slot) {
                            Ok(Some(contacts)) => contacts,
                            _ => { counts.missing_range_data += 1; continue; },
                        };
                        counts.range_contacts += contacts.ship_ids.len();
                        self.shape(&ShipShape::Circle { offset: Vec2::ZERO, radius: range.radius },
                            Pose { position: range.center, rotation_degrees: 0.0 },
                            if range.enabled { 0x355b80 } else { 0x555555 });
                        let Some(has_obstacles) = view.player_authority.weapon_obstacles_enabled()
                            else { counts.missing_range_data += 1; continue; };
                        // An inactive range cannot draw a valid contact.
                        if !range.enabled { continue; }
                        let enemies = view.combat.total_living_ships()
                            .saturating_sub(view.combat.living_ships_on_side(ship.side));
                        counts.range_candidates_culled +=
                            enemies.saturating_sub(contacts.ship_ids.len());
                        for &target_id in contacts.ship_ids {
                            counts.range_queries += 1;
                            match view.combat.query_weapon_target_geometry(ship.entity_id, slot,
                                target_id, view.collision_matrix, has_obstacles) {
                                Ok(Some(geometry)) if geometry.enemy_hull_overlaps_range => {
                                    counts.range_overlaps += 1;
                                    match view.combat.weapon_target_point(ship.entity_id, slot, target_id) {
                                        Ok(point) => { self.cross(point, 2.5, 0xf6df85); counts.aim_points += 1; },
                                        Err(_) => counts.missing_aim_data += 1,
                                    }
                                    counts.obstructed_ranges += usize::from(geometry.line_obstructed);
                                    let (ox, oy) = self.project(range.origin);
                                    let (tx, ty) = self.project(geometry.closest_hull_point);
                                    self.line(ox, oy, tx, ty,
                                        if geometry.line_obstructed { 0xe1a34b } else { 0x62b9ff });
                                },
                                Ok(_) => (),
                                Err(_) => counts.missing_range_data += 1,
                            }
                        }
                    }
                }
                if let Some(destination) = view.combat.ship_game_destination(ship.entity_id) {
                    let (tx, ty) = self.project(destination);
                    self.line(x, y, tx, ty, 0x526a8a);
                    self.cross(destination, 3.0, 0xddeaff);
                }
            }
        }
        self.begin_death_scene(view.world_id, view.episode_revision);
        for event in view.combat.events() {
            if let bees_sim::combat_world::CombatEvent::Hit(hit) = event {
                if hit.delta.applied_damage > 0 {
                    if let Some(body) = view.combat.ship_body(hit.target_id) {
                        let (admitted, dropped) = self.record_combat_impact(
                            view.tick, hit.projectile_id, hit.target_id,
                            body.motion.position, hit.delta.killed,
                        );
                        counts.combat_impacts_observed += usize::from(admitted);
                        counts.combat_impact_visuals_dropped += usize::from(dropped);
                    }
                }
                continue;
            }
            // Asteroids, static walls and destructible MapObjects share the
            // canonical obstacle collider. Its identity lookup is O(1), not
            // a scan over every obstacle per impact in a large battle.
            let obstacle_impact = match event {
                bees_sim::combat_world::CombatEvent::StaticObstacleImpact {
                    projectile_id, obstacle_id,
                } | bees_sim::combat_world::CombatEvent::AsteroidDamaged {
                    projectile_id, obstacle_id, ..
                } | bees_sim::combat_world::CombatEvent::MapObjectDamaged {
                    projectile_id, object_id: obstacle_id, ..
                } => Some((*projectile_id, *obstacle_id)),
                _ => None,
            };
            if let Some((projectile_id, obstacle_id)) = obstacle_impact {
                if let Some(body) = view.combat.obstacle_body(obstacle_id) {
                    let (admitted, dropped) = self.record_combat_impact(
                        view.tick, projectile_id, obstacle_id,
                        body.pose.position, false,
                    );
                    counts.combat_impacts_observed += usize::from(admitted);
                    counts.combat_impact_visuals_dropped += usize::from(dropped);
                }
                continue;
            }
            let bees_sim::combat_world::CombatEvent::ShipDied { victim_id, .. } = event
                else { continue };
            let (Some(ship), Some(body)) = (
                view.combat.ship(*victim_id), view.combat.ship_body(*victim_id),
            ) else { continue };
            let radius = body.primary_bounds().map(|bounds|
                (bounds.max.x - bounds.min.x)
                    .max(bounds.max.y - bounds.min.y) * 0.5
            ).unwrap_or(3.0);
            let custom_color = view.roster.owning_squad(*victim_id)
                .and_then(|owner|
                    view.player_authority.player_squad_color_rgb(owner));
            let (observed, dropped) = self.record_ship_death(
                *victim_id, view.tick, ship.ship_type, ship.side,
                body.motion.position, body.motion.rotation_degrees,
                radius, custom_color,
            );
            counts.ship_deaths_observed += usize::from(observed);
            counts.ship_death_visuals_dropped += usize::from(dropped);
        }
        let projectile_art = source_projectile_art();
        counts.projectile_art_types_loaded = projectile_art.map_or(0, |atlas| atlas.loaded);
        let artwork_world = (view.world_id, view.episode_revision);
        if self.projectile_art_owner != Some(artwork_world) {
            self.projectile_art_owner = Some(artwork_world);
            self.projectile_art_ticks.clear();
        }
        for (projectile, body) in view.combat.render_projectiles() {
            counts.projectiles += 1;
            let position = body.motion.position;
            let center_visible = position.x >= viewport.min.x &&
                position.x <= viewport.max.x &&
                position.y >= viewport.min.y &&
                position.y <= viewport.max.y;
            let outside_hull = match body.shape() {
                None => true,
                Some(_) => body.primary_bounds()
                    .is_some_and(|hull| !hull.overlaps(viewport)),
            };
            // The script-entity center marker is real UI even when its
            // collider is displaced, so require both to be offscreen.
            if !center_visible && outside_hull {
                counts.culled_projectiles += 1;
                continue;
            }
            let art = projectile_art
                .and_then(|atlas| atlas.sprites.get(usize::from(projectile.projectile_type)))
                .and_then(Option::as_ref);
            let drew_art = if let Some(art) = art {
                // A newly created projectile always begins at its first
                // authored frame. Stable live entity IDs and the current
                // physics tick own animation age, not an arbitrary
                // entity-ID modulo a whole sprite sheet.
                let (born, seen) = self.projectile_art_ticks
                    .entry(projectile.entity_id)
                    .or_insert((view.tick, view.tick));
                *seen = view.tick;
                let age = view.tick.saturating_sub(*born) as f32
                    * view.fixed_step_seconds;
                let elapsed_frame = (age * 8.0).floor() as usize;
                let index = if matches!(projectile.projectile_type, 13 | 14) {
                    elapsed_frame.min(art.frames.len() - 1)
                } else {
                    elapsed_frame % art.frames.len()
                };
                self.draw_sprite(&art.frames[index], position,
                    body.rotation_degrees.to_radians(),
                    self.camera.pixels_per_unit / art.pixels_per_unit, 1.0)
            } else { false };
            if drew_art {
                counts.projectile_sprite_draws += 1;
                if matches!(projectile.projectile_type, 13 | 14) {
                    counts.projectile_explosion_sprite_draws += 1;
                }
            } else {
                counts.projectile_fallback_hulls += 1;
                if let Some(shape) = body.shape() {
                    self.shape(shape, Pose { position,
                        rotation_degrees: body.rotation_degrees }, 0xffd580);
                }
                // Collider-less script entities remain inspectable.
                self.cross(position, 1.0, 0xffd580);
            }
        }
        // Reclaim expired cosmetic records when projectiles die or leave
        // view; retained battles with millions of shots cannot accumulate
        // stale animation state over time.
        self.projectile_art_ticks.retain(|_, &mut (_, seen)| seen == view.tick);
        let (remains, explosions, source_frames) =
            self.draw_ship_death_visuals(view.tick, view.fixed_step_seconds);
        counts.ship_remains_draws = remains;
        counts.ship_explosion_visual_draws = explosions;
        counts.ship_remains_source_frame_draws = source_frames;
        counts.ship_remains_art_classes_loaded =
            loaded_ship_remains_art_classes();
        counts.ship_remains_color_cache_variants = self.colored_ship_remains.len();
        counts.combat_impact_visual_draws =
            self.draw_combat_impact_visuals(view.tick, view.fixed_step_seconds);
        counts.sprite_raster_candidate_pixels = self.sprite_raster_candidate_pixels;
        counts.sprite_raster_opaque_pixels = self.sprite_raster_opaque_pixels;
        counts.sprite_raster_blended_pixels = self.sprite_raster_blended_pixels;
        counts.ship_color_cache_variants = self.custom_ship_art.len();
        // Only presentation pixels are masked. Commands, physics, learning
        // and the source-owned visibility caches remain unchanged.
        self.apply_player_fog(view, &mut counts);
        if let Some(bounds) = self.selection_box {
            self.shape(&ShipShape::Box { offset: (bounds.min + bounds.max) * 0.5,
                half_extents: (bounds.max - bounds.min) * 0.5 },
                Pose { position: Vec2::ZERO, rotation_degrees: 0.0 }, 0xddeaff);
        }
        if let Some(result) = view.terminal_result {
            self.draw_battle_result(view, result);
        } else if let Some(survival) = view.survival {
            let seconds = survival.seconds_remaining.ceil() as u64;
            let label = match view.combat.ship(survival.protected_ship_id) {
                Some(target) => format!(
                    "{}  {}/{} HP  |  Defend {:02}:{:02}",
                    if target.ship_type == bees_sim::ship_assets::HUMAN_TARGET_TYPE {
                        "Planetary Shield"
                    } else { "Protected ship" },
                    target.vitals.health(), target.vitals.max_health(),
                    seconds / 60, seconds % 60,
                ),
                None => format!(
                    "Protected target lost  |  {:02}:{:02}",
                    seconds / 60, seconds % 60,
                ),
            };
            text::draw_centered(&mut self.pixels, self.width, 8, &label, 1, [239, 247, 255]);
        } else if let Some(escort) = view.escort {
            let remaining = escort.seconds_remaining.ceil() as u64;
            let health = view.combat.ship(escort.escorted_ship_id).map_or(
                "  Lost".to_owned(), |ship|
                    format!("  {}/{} HP", ship.vitals.health(), ship.vitals.max_health()),
            );
            let label = format!(
                "Escort ship #{} to extraction{}  |  {:02}:{:02}",
                escort.escorted_ship_id, health, remaining / 60, remaining % 60,
            );
            text::draw_centered(&mut self.pixels, self.width, 8, &label, 1, [239, 247, 255]);
        } else if let Some(demolition) = view.demolition {
            let seconds = demolition.seconds_remaining.ceil() as u64;
            let target = view.combat.map_object(demolition.target_map_object_id);
            let is_defender = view.player_authority.player_viewer_side()
                .is_some_and(|side| side != demolition.attacking_side);
            let objective = target.map_or("Target destroyed", |object|
                match (is_defender, object.kind) {
                    (true, bees_sim::map_object::MapObjectKind::FireTank) => "Protect Fire Tank",
                    (true, _) => "Protect MapObject",
                    (false, bees_sim::map_object::MapObjectKind::FireTank) => "Destroy Fire Tank",
                    (false, _) => "Destroy MapObject",
                }
            );
            let health = target.map_or(String::new(), |object|
                format!("  {}/{} HP", object.health, object.max_health)
            );
            let label = format!(
                "{} #{}{}  |  Deadline {:02}:{:02}",
                objective, demolition.target_map_object_id, health,
                seconds / 60, seconds % 60,
            );
            text::draw_centered(&mut self.pixels, self.width, 8, &label, 1, [239, 247, 255]);
        } else if let Some((exiting_side, _, escort)) = view.combat.extraction_objective() {
            if !escort {
                let label = if view.player_authority.player_viewer_side()
                    == Some(exiting_side) {
                    "Extract your mobile fleet to the marked exit"
                } else { "Intercept enemy fleet before it extracts" };
                text::draw_centered(&mut self.pixels, self.width, 8, label, 1, [239, 247, 255]);
            }
        }
        // The durable Level notification records arrivals exactly once.
        // A short visual acknowledgement survives an outbox ACK, but
        // vanishes naturally with the canonical simulation clock; paused
        // games do not consume its time and no frame timer is allocated.
        if view.terminal_result.is_none() {
            let y = if view.survival.is_some() || view.demolition.is_some() ||
                view.escort.is_some() || view.combat.extraction_objective().is_some() {
                8 + text::HEIGHT + 4
            } else { 8 };
            let recently_arrived = view.last_reinforcement_tick
                .is_some_and(|tick| view.tick >= tick &&
                    (view.tick - tick) as f64 *
                        f64::from(view.fixed_step_seconds) < 3.0);
            if recently_arrived {
                text::draw_centered(&mut self.pixels, self.width, y,
                    "Reinforcements arrived", 1, [239, 196, 129]);
            } else if let Some(next_tick) = view.next_reinforcement_tick {
                let remaining_ticks = next_tick.saturating_sub(view.tick);
                let seconds = ((remaining_ticks as f64) *
                    f64::from(view.fixed_step_seconds)).ceil() as u64;
                let label = format!(
                    "Reinforcements: {} wave{}  |  Next {:02}:{:02}",
                    view.reinforcement_waves_pending,
                    if view.reinforcement_waves_pending == 1 { "" } else { "s" },
                    seconds / 60, seconds % 60,
                );
                text::draw_centered(&mut self.pixels, self.width, y,
                    &label, 1, [239, 196, 129]);
            } else if view.reinforcement_waves_pending > 0 {
                // Event-gated waves have no invented countdown. The
                // original ship-death/campaign trigger alone controls
                // their physical arrival, including during a pause.
                let label = format!(
                    "Reinforcements: {} wave{}  |  Awaiting mission event",
                    view.reinforcement_waves_pending,
                    if view.reinforcement_waves_pending == 1 { "" } else { "s" },
                );
                text::draw_centered(&mut self.pixels, self.width, y,
                    &label, 1, [239, 196, 129]);
            }
        }
        if self.show_minimap {
            let (ships, enemies, escort_exits) = self.draw_minimap(view, bounds);
            counts.minimap_ship_markers = ships;
            counts.minimap_visible_enemy_markers = enemies;
            counts.minimap_extraction_exit_markers = escort_exits;
        }
        counts.numbered_squad_rows = self.draw_squad_shortcuts(view);
        counts.special_ready_ships = self.draw_special_readiness(view);
        self.chasing_selection_all = !self.selection.squads().is_empty() &&
            self.selection.squads().iter().all(|&owner|
                view.player_authority.player_should_chase(owner));
        self.matched_speed_selection_all = !self.selection.squads().is_empty() &&
            self.selection.squads().iter().all(|&owner|
                view.player_authority.player_matched_speed(owner).is_some());
        self.ceasefire_selection_all = !self.selection.squads().is_empty() &&
            self.selection.squads().iter().all(|&owner|
                view.player_authority.player_cease_fire(owner) == Some(true));
        self.attack_on_sight_selection_all = !self.selection.squads().is_empty() &&
            self.selection.squads().iter().all(|&owner|
                view.player_authority.player_cease_fire(owner) == Some(false));
        self.locked_selection_all = !self.selection.squads().is_empty() &&
            self.selection.squads().iter().all(|&owner|
                view.player_authority.player_permissions(owner)
                    .is_some_and(|permission| permission.locked_on));
        counts.tactical_buttons_drawn = self.draw_tactical_bar();
        counts.selected_command_rows = self.draw_selected_command_status(view);
        self.draw_selected_priority(view);
        if self.show_shooting_menu { self.draw_shooting_menu(view); }
        counts
    }

    /// Stamp one vision disc into the already allocated main-screen fog
    /// buffer, restricted to the small fixed minimap plot. This avoids
    /// O(player ships x enemy ships) queries for marker visibility, and
    /// allocates no second minimap visibility state.
    fn stamp_minimap_fog(&mut self, layout: MiniMapLayout,
        position: Vec2, source_radius: f32,
    ) {
        if !position.is_finite() || source_radius <= 0.0 { return; }
        let (cx, cy) = layout.project_unbounded(position);
        let radius = source_radius * layout.scale;
        if !radius.is_finite() || radius <= 0.0 || !cx.is_finite() || !cy.is_finite() {
            return;
        }
        if cx + radius < layout.plot_left || cx - radius > layout.plot_right ||
            cy + radius < layout.plot_top || cy - radius > layout.plot_bottom {
            return;
        }
        let top = (cy - radius).ceil().max(layout.plot_top.ceil()) as usize;
        let bottom = (cy + radius).floor()
            .min(layout.plot_bottom.floor()) as usize;
        if top > bottom { return; }
        let radius_sq = radius * radius;
        for y in top..=bottom {
            let dy = y as f32 - cy;
            if dy.abs() > radius { continue; }
            let width = (radius_sq - dy * dy).max(0.0).sqrt();
            if cx + width < layout.plot_left || cx - width > layout.plot_right {
                continue;
            }
            let left = (cx - width).ceil().max(layout.plot_left.ceil()) as usize;
            let right = (cx + width).floor()
                .min(layout.plot_right.floor()) as usize;
            if left <= right {
                let row = y * self.width;
                self.fog_coverage[row + left..=row + right].fill(1);
            }
        }
    }

    /// The physical minimap draws live player ships and currently visible
    /// opponents, rather than silently hiding every enemy. Source fog
    /// limits enemy markers using the same live player masks and fading
    /// vision used by the main viewport. When source fog is OFF, all live
    /// ships are visible as in the unmasked tactical battlefield.
    fn draw_minimap(&mut self, view: &NativeRenderWorldView<'_>, bounds: Aabb)
        -> (usize, usize, usize)
    {
        let Some(layout) = MiniMapLayout::new(self.width, self.height, bounds)
            else { return (0, 0, 0) };
        let mut markers = 0usize;
        let mut enemies = 0usize;
        for y in layout.top..layout.bottom {
            self.pixels[y * self.width + layout.left
                ..y * self.width + layout.right].fill(0x101c2e);
        }
        let edge = 0x6e829e;
        let x0 = layout.plot_left;
        let y0 = layout.plot_top;
        let x1 = layout.plot_right;
        let y1 = layout.plot_bottom;
        self.line(x0, y0, x1, y0, edge);
        self.line(x1, y0, x1, y1, edge);
        self.line(x1, y1, x0, y1, edge);
        self.line(x0, y1, x0, y0, edge);

        let fog = view.player_authority.player_fog_of_war_enabled();
        if fog {
            // The same allocation already holds the main-view fog stencil.
            // It has already been used for that viewport's pixel mask; only
            // its minimap panel region needs resetting here.
            for y in layout.top..layout.bottom {
                let row = y * self.width;
                self.fog_coverage[row + layout.left..row + layout.right].fill(0);
            }
            for (ship, body) in view.combat.render_ships() {
                let Some(radius) = Self::fog_source_radius(view, ship.entity_id)
                    else { continue };
                self.stamp_minimap_fog(layout, body.motion.position, radius);
            }
            for index in 0..self.fog_fades.len() {
                let fade = self.fog_fades[index];
                self.stamp_minimap_fog(layout, fade.position,
                    fade.current_radius(view.tick, view.fixed_step_seconds));
            }
        }

        for (ship, body) in view.combat.render_ships() {
            let owner = view.roster.owning_squad(ship.entity_id);
            let controlled = owner.is_some_and(|id| view.is_local_player_squad(id));
            let Some((x, y)) = layout.project(body.motion.position)
                else { continue };
            let px = x.round() as i32;
            let py = y.round() as i32;
            if !controlled && fog &&
                self.fog_coverage.get(py as usize * self.width + px as usize) != Some(&1) {
                // Never reveal the position of a concealed enemy or an
                // unrelated player's units in a masked local viewport.
                continue;
            }
            markers += 1;
            // Non-commandable same-side escorts remain friendly; only
            // opposite-side units count as tactical enemy sightings.
            if view.player_authority.player_viewer_side()
                .is_some_and(|side| ship.side != side) {
                enemies += 1;
            }
            let selected = controlled && owner.is_some_and(|owner|
                if self.selection.squads().len() > 8 {
                    self.selected_squad_lookup.contains(&owner)
                } else {
                    self.selection.squads().contains(&owner)
                }
            );
            let color = if selected { 0xffffff } else { side_color(ship.side) };
            let radius = if selected { 3 } else { 1 };
            for yy in (py - radius)..=(py + radius) {
                for xx in (px - radius)..=(px + radius) {
                    if xx >= layout.plot_left.floor() as i32 &&
                        xx <= layout.plot_right.ceil() as i32 &&
                        yy >= layout.plot_top.floor() as i32 &&
                        yy <= layout.plot_bottom.ceil() as i32 &&
                        xx >= 0 && yy >= 0 &&
                        (xx as usize) < self.width && (yy as usize) < self.height
                    {
                        self.pixels[yy as usize * self.width + xx as usize] = color;
                    }
                }
            }
        }
        // The demolition objective is an actual map collider, not a
        // separately stored waypoint. Draw it only after the same native
        // fog stencil used for enemy ship markers has been evaluated.
        if let Some(demolition) = view.demolition {
            for (obstacle, body) in view.combat.render_obstacles() {
                if obstacle.entity_id != demolition.target_map_object_id ||
                    obstacle.kind != bees_sim::obstacle::ObstacleKind::MapObject
                {
                    continue;
                }
                if let Some((x, y)) = layout.project(body.pose.position) {
                    let px = x.round() as usize;
                    let py = y.round() as usize;
                    if !fog || self.fog_coverage.get(py * self.width + px) == Some(&1) {
                        let radius = 4.0;
                        let color = 0xffd57a;
                        self.line(x - radius, y - radius, x + radius, y - radius, color);
                        self.line(x + radius, y - radius, x + radius, y + radius, color);
                        self.line(x + radius, y + radius, x - radius, y + radius, color);
                        self.line(x - radius, y + radius, x - radius, y - radius, color);
                    }
                }
                break;
            }
        }
        // The exit is a known authored mission destination, not a
        // hidden enemy unit. Show it before physics activates its trigger
        // so the player can actually command the convoy across the map.
        let mut escort_exits = 0usize;
        if let Some((_, exit, _)) = view.combat.extraction_objective() {
            if let (Some((x0, y0)), Some((x1, y1))) = (
                layout.project(exit.min), layout.project(exit.max),
            ) {
                let color = 0x75dbab;
                self.line(x0, y1, x1, y1, color);
                self.line(x1, y1, x1, y0, color);
                self.line(x1, y0, x0, y0, color);
                self.line(x0, y0, x0, y1, color);
                escort_exits = 1;
            }
        }
        let bottom_left = self.camera.screen_to_world(
            0.0, self.height as f32, self.width, self.height);
        let top_right = self.camera.screen_to_world(
            self.width as f32, 0.0, self.width, self.height);
        if let Some((x0, y0, x1, y1)) = layout.camera_rect(bottom_left, top_right) {
            let color = 0xe2c473;
            self.line(x0, y0, x1, y0, color);
            self.line(x1, y0, x1, y1, color);
            self.line(x1, y1, x0, y1, color);
            self.line(x0, y1, x0, y0, color);
        }
        (markers, enemies, escort_exits)
    }

    /// A lightweight source-squad tab strip. Shortcut numbers come from the
    /// actual schema-29 Unity export, not an index assigned by the renderer.
    /// It is omitted for legacy scenes lacking authoritative squad numbers.
    /// Only user-controlled ships are listed; enemy roster data stays private.
    fn draw_squad_shortcuts(&mut self, view: &NativeRenderWorldView<'_>) -> usize {
        if self.width < 560 || self.height < 360 { return 0; }
        let mut row = 0usize;
        for &owner in view.roster.source_order() {
            let Some(squad) = view.roster.get(owner) else { continue };
            let Some(number) = squad.source_hotkey_number else { continue };
            if number > 10 || !view.is_local_player_squad(owner) {
                continue;
            }
            let Some(&ship_id) = squad.members.first() else { continue };
            let Some(ship) = view.combat.ship(ship_id) else { continue };
            let selected = if self.selection.squads().len() > 8 {
                self.selected_squad_lookup.contains(&owner)
            } else {
                self.selection.squads().contains(&owner)
            };
            let key = if number == 10 { 0 } else { number };
            let label = format!("{key}: type {} / {} ships",
                ship.ship_type, squad.members.len());
            let Some(rect) = tactical_hud::squad_tab_rect(
                self.width, self.height, row, text::HEIGHT,
                text::width(&label, 1)) else { break };
            for scanline in rect.top..rect.bottom {
                let start = scanline * self.width + rect.left;
                self.pixels[start..start + rect.right - rect.left].fill(
                    if selected { 0x35465d } else { 0x142338 });
            }
            text::draw_at(&mut self.pixels, self.width, 14,
                rect.top + 2,
                &label, 1, if selected { [255, 241, 194] }
                    else { [182, 211, 239] });
            row += 1;
            if row == 10 { break; }
        }
        row
    }

    /// Inspect only selected, currently authorized squads. The availability
    /// indicators are sourced from the same combat capability state and
    /// fleet Bomb registration consumed by the player input plan.
    fn draw_special_readiness(&mut self, view: &NativeRenderWorldView<'_>) -> usize {
        if self.width < 560 || self.height < 320 ||
            self.selection.squads().is_empty() { return 0; }
        let mut scouts = 0usize;
        let mut scouts_ready = 0usize;
        // Striker.LoadedIndicator is a live read-only status, not an
        // executable special: show it for selected local Carrier children.
        let mut strikers = 0usize;
        let mut strikers_ready = 0usize;
        let mut barges = 0usize;
        let mut barges_ready = 0usize;
        let mut charge_barges = 0usize;
        let mut charges_ready = 0usize;
        for &owner in self.selection.squads() {
            if !view.is_local_player_squad(owner) { continue; }
            let can_issue = view.player_authority.player_permissions(owner)
                .is_some_and(|permission|
                    permission.can_accept_input && !permission.locked_on);
            let Some(squad) = view.roster.get(owner) else { continue };
            for &ship_id in &squad.members {
                let Some(ship) = view.combat.ship(ship_id) else { continue };
                let Some(body) = view.combat.ship_body(ship_id) else { continue };
                if ship.vitals.is_dead() || !body.active ||
                    body.motion.is_policy_controlled ||
                    view.combat.policy_frame(ship_id).is_some() { continue; }
                match ship.ship_type {
                    19 => {
                        if let Some(ready) = view.combat.game_striker_bomb_ready(ship_id)
                            .or_else(|| view.combat.policy_striker_bomb_ready(ship_id)) {
                            strikers += 1;
                            strikers_ready += usize::from(ready);
                        }
                    }
                    18 if can_issue && view.combat.game_scout_configured(ship_id) => {
                        scouts += 1;
                        scouts_ready += usize::from(
                            view.combat.player_scout_beacon_ready(ship_id));
                    }
                    10 if can_issue && view.combat.configured_fire_barge_bomb_power(ship_id)
                        .is_some() => {
                        barges += 1;
                        barges_ready += usize::from(
                            view.combat.player_fire_barge_detonation_ready(ship_id));
                    }
                    0 if can_issue => {
                        // Legacy snapshots intentionally lack authenticated
                        // CanOverrideBounds and player Charge configuration.
                        // Do not advertise an unsupported ability.
                        if view.combat.player_game_barge_configured(ship_id) {
                            charge_barges += 1;
                            charges_ready += usize::from(
                                view.combat.player_game_barge_charge_ready(ship_id));
                        }
                    }
                    _ => {}
                }
            }
        }
        let rows = usize::from(scouts > 0) + usize::from(barges > 0) +
            usize::from(charge_barges > 0) + usize::from(strikers > 0);
        if rows == 0 { return 0; }
        let x = self.width - 268;
        let line_height = text::HEIGHT + 9;
        let panel_height = rows * line_height + 12;
        let y = self.height.saturating_sub(panel_height + 5);
        for scanline in y.saturating_sub(5)..(y + panel_height).min(self.height) {
            self.pixels[scanline * self.width + x - 7
                ..scanline * self.width + x + 256].fill(0x15243a);
        }
        // Only draw capabilities actually present in the selection, and
        // calculate the panel height from the same rows. A Striker-only
        // squad must not reserve three empty special-action lines.
        let mut row = 0;
        if scouts > 0 {
            let label = format!("K: Beacons ready {scouts_ready}/{scouts}");
            text::draw_at(&mut self.pixels, self.width, x, y + row * line_height,
                &label, 1, if scouts_ready > 0 { [178, 230, 188] }
                else { [136, 151, 172] });
            row += 1;
        }
        if barges > 0 {
            let label = format!("D: Fire Barges ready {barges_ready}/{barges}");
            text::draw_at(&mut self.pixels, self.width, x, y + row * line_height,
                &label, 1, if barges_ready > 0 { [255, 202, 148] }
                else { [136, 151, 172] });
            row += 1;
        }
        if charge_barges > 0 {
            let label = format!("Q: Barge charge ready {charges_ready}/{charge_barges}");
            text::draw_at(&mut self.pixels, self.width, x, y + row * line_height,
                &label, 1, if charges_ready > 0 { [244, 211, 142] }
                else { [136, 151, 172] });
            row += 1;
        }
        if strikers > 0 {
            let label = format!("Strikers loaded {strikers_ready}/{strikers}");
            text::draw_at(&mut self.pixels, self.width, x, y + row * line_height,
                &label, 1, if strikers_ready > 0 { [178, 230, 188] }
                else { [136, 151, 172] });
        }
        scouts_ready + barges_ready + charges_ready + strikers_ready
    }

    fn draw_tactical_bar(&mut self) -> usize {
        let has_selection = !self.selection.squads().is_empty();
        let mut drawn = 0usize;
        for (index, action) in tactical_hud::ACTIONS.iter().copied().enumerate() {
            let Some(rect) = tactical_hud::action_rect(self.width, self.height, index)
                else { return drawn };
            let background = if !has_selection &&
                action != tactical_hud::HudAction::SelectAll {
                0x172334
            } else if self.show_shooting_menu && action == tactical_hud::HudAction::Shooting {
                0x456c78
            } else if action == tactical_hud::HudAction::CeaseFire &&
                self.ceasefire_selection_all {
                0x456c78
            } else if action == tactical_hud::HudAction::AttackOnSight &&
                self.attack_on_sight_selection_all {
                0x456c78
            } else if action == tactical_hud::HudAction::MatchSpeed &&
                self.matched_speed_selection_all {
                0x456c78
            } else if action == tactical_hud::HudAction::FreeSpeed &&
                !has_selection {
                0x172334
            } else if action == tactical_hud::HudAction::Chase &&
                self.chasing_selection_all {
                0x456c78
            } else if action == tactical_hud::HudAction::LockOn &&
                !self.selection.squads().is_empty() &&
                self.locked_selection_all {
                0x456c78
            } else if self.active_tactical_action == Some(action) {
                0x456c78
            } else if self.hovered_tactical_action == Some(action) {
                0x394e65
            } else if matches!(action,
                tactical_hud::HudAction::Detonate |
                tactical_hud::HudAction::StopFire
            ) { 0x503a38 } else { 0x23334a };
            for y in rect.top..rect.bottom {
                self.pixels[y * self.width + rect.left
                    ..y * self.width + rect.right].fill(background);
            }
            let label = action.label();
            let x = rect.left + (rect.right - rect.left)
                .saturating_sub(text::width(label, 1)) / 2;
            let y = rect.top + (rect.bottom - rect.top)
                .saturating_sub(text::HEIGHT) / 2;
            let tone = if has_selection ||
                action == tactical_hud::HudAction::SelectAll {
                [232, 241, 251]
            } else { [101, 114, 132] };
            text::draw_at(&mut self.pixels, self.width, x, y,
                label, 1, tone);
            drawn += 1;
        }
        drawn
    }

    /// Selected-squad command status is borrowed from the same canonical
    /// director as timed AI command execution, weapon strategy selection
    /// and durable outcome settlement. Avoid a second UI status machine.
    fn draw_selected_command_status(&mut self, view: &NativeRenderWorldView<'_>) -> usize {
        if self.selection.squads().is_empty() ||
            self.width < 750 || self.height < 400 {
            return 0;
        }
        let left = self.width - 318;
        let mut row = 0usize;
        for &owner in self.selection.squads() {
            let Some(squad) = view.roster.get(owner) else { continue };
            if !view.is_local_player_squad(owner) { continue; }
            let mut label = squad.source_hotkey_number
                .map_or_else(|| "Squad".to_owned(),
                    |number| format!("Squad {number}"));
            if let Some(parent) = view.carrier_squad_parent(owner) {
                match parent {
                    Some(id) if view.combat.ship(id).is_some_and(|ship|
                        !ship.vitals.is_dead()) =>
                        label.push_str(&format!(" C:{id}")),
                    Some(_) => label.push_str(" C:dead"),
                    None => label.push_str(" C:orphan"),
                }
            }
            let command = view.commands.current_kind(owner)
                .map_or_else(|| "Idle".to_owned(), |kind| format!("{kind:?}"));
            let status = if let Some(enemy) = view.commands.selected_enemy(owner)
                .and_then(|enemy| view.roster.get(enemy))
            {
                format!("{label}: {command} (enemy side {})", enemy.side)
            } else {
                format!("{label}: {command}")
            };
            let y = 16 + row * (text::HEIGHT + 9);
            if y + text::HEIGHT + 10 >= self.height / 2 { break; }
            for pixel_y in (y - 3)..(y + text::HEIGHT + 6) {
                self.pixels[pixel_y * self.width + left
                    ..pixel_y * self.width + left + 306].fill(0x17283e);
            }
            let status = if status.len() > 44 { &status[..44] } else { &status };
            text::draw_at(&mut self.pixels, self.width,
                left + 8, y, status, 1, [221, 236, 249]);
            row += 1;
            if row == 8 { break; }
        }
        row
    }

    /// The live source Squad.GetShootingStrategy, including user changes
    /// since export. The corresponding weapon targeting is read from this
    /// metadata whenever the squad has no active command.
    fn draw_selected_priority(&mut self, view: &NativeRenderWorldView<'_>) {
        if self.selection.squads().is_empty() || self.width < 520 ||
            self.height < 430 { return; }
        let first = self.selection.squads().first().and_then(|&owner|
            view.player_authority.player_shooting_strategy(owner));
        let label = match first {
            Some(shooting) if self.selection.squads().iter().all(|&owner|
                view.player_authority.player_shooting_strategy(owner) == Some(shooting)
            ) => format!("Shooting priority: {} (S to choose)", shooting.name),
            Some(_) => "Shooting priority: mixed (S to choose)".to_owned(),
            None => "Shooting priority unavailable".to_owned(),
        };
        // Unlike the client-only hover feedback, this renderer method
        // also builds in headless-only configurations. The glyph renderer
        // already clips to the framebuffer without client-only text::fit.
        let w = text::width(&label, 1);
        let x = self.width.saturating_sub(w) / 2;
        text::draw_at(&mut self.pixels, self.width, x,
            self.height - 190, &label, 1, [175, 207, 227]);
        let mut count = 0usize;
        let mut first_speed = None;
        let mut same_speed = true;
        for &owner in self.selection.squads() {
            if let Some(speed) = view.player_authority.player_matched_speed(owner) {
                count += 1;
                if let Some(first) = first_speed {
                    same_speed &= speed == first;
                } else {
                    first_speed = Some(speed);
                }
            }
        }
        if let Some(speed) = first_speed {
            let value = if count == self.selection.squads().len() && same_speed {
                format!("Matched speed: {speed:.2}")
            } else {
                format!("{count}/{} squads have matched speed",
                    self.selection.squads().len())
            };
            let x = self.width.saturating_sub(text::width(&value, 1)) / 2;
            text::draw_at(&mut self.pixels, self.width,
                x, self.height - 216, &value, 1, [188, 217, 255]);
        }
        let cease_count = self.selection.squads().iter().filter(|&&owner|
            view.player_authority.player_cease_fire(owner) == Some(true)).count();
        if cease_count > 0 {
            let label = format!("{cease_count}/{} squads set to Cease Fire",
                self.selection.squads().len());
            let x = self.width.saturating_sub(text::width(&label, 1)) / 2;
            text::draw_at(&mut self.pixels, self.width,
                x, self.height - 229, &label, 1, [255, 201, 182]);
        }
        let chase_count = self.selection.squads().iter().filter(|&&owner|
            view.player_authority.player_should_chase(owner)).count();
        if chase_count > 0 {
            let label = format!("{chase_count}/{} squads chasing (E to enable, C to hold)",
                self.selection.squads().len());
            let x = self.width.saturating_sub(text::width(&label, 1)) / 2;
            text::draw_at(&mut self.pixels, self.width,
                x, self.height - 203, &label, 1, [197, 222, 165]);
        }
        let locked_count = self.selection.squads().iter().filter(|&&owner|
            view.player_authority.player_permissions(owner)
                .is_some_and(|permission| permission.locked_on)
        ).count();
        if locked_count > 0 {
            let label = format!("{locked_count}/{} squads locked on (O to toggle)",
                self.selection.squads().len());
            let x = self.width.saturating_sub(text::width(&label, 1)) / 2;
            text::draw_at(&mut self.pixels, self.width,
                x, self.height - 176, &label, 1, [255, 205, 132]);
        }
    }

    /// Present the canonical 40-entry source registry. Current selection
    /// is read from the SAME live metadata used by automatic weapon inputs;
    /// different selected strategies produce no misleading highlighted value.
    fn draw_shooting_menu(&mut self, view: &NativeRenderWorldView<'_>) {
        // Cover the battlefield under the modal without creating another
        // surface, renderer or dynamically allocated panel bitmap.
        let Some(first) = tactical_hud::shooting_rect(self.width, self.height, 0)
            else { return };
        let Some(last) = tactical_hud::shooting_rect(self.width, self.height, 39)
            else { return };
        let left = first.left.saturating_sub(12);
        let right = (last.right + 12).min(self.width);
        let top = first.top.saturating_sub(27);
        let bottom = (last.bottom + 12).min(self.height);
        for y in top..bottom {
            self.pixels[y * self.width + left
                ..y * self.width + right].fill(0x101d2e);
        }
        text::draw_at(&mut self.pixels, self.width, left + 12,
            top + 6, "SHOOTING PRIORITY - arrows + Enter or click / S or Esc cancels",
            1, [225, 235, 250]);
        let selected = self.selection.squads().iter()
            .filter_map(|&owner| view.player_authority.player_shooting_strategy(owner))
            .map(|value| value.id)
            .fold(None, |current, value| match current {
                None => Some(value),
                Some(other) if other == value => Some(value),
                _ => Some(0),
            });
        for (index, &name) in bees_hivemind::protocol::SHOOTING_STRATEGIES.iter().enumerate() {
            let Some(rect) = tactical_hud::shooting_rect(self.width, self.height, index)
                else { return };
            let active = selected == Some((index + 1) as u8);
            let focused = self.focused_shooting_strategy == (index + 1) as u8;
            for y in rect.top..rect.bottom {
                self.pixels[y * self.width + rect.left
                    ..y * self.width + rect.right].fill(
                        if focused { 0x546d83 }
                        else if active { 0x476b70 } else { 0x23344d });
            }
            let label = format!("{:>2}: {name}", index + 1);
            text::draw_at(&mut self.pixels, self.width,
                rect.left + 8, rect.top + 5, &label, 1,
                if active { [247, 255, 204] }
                else if focused { [255, 255, 255] }
                else { [227, 239, 252] });
        }
    }

    /// Keep the current command prompt, feedback, and hovered action
    /// explanation visible inside the actual framebuffer. Window managers
    /// truncate long title strings, particularly over remote desktop.
    /// This remains optional presentation work; the headless simulator does
    /// not allocate or render a second command message.
    #[cfg(feature = "client")]
    pub fn draw_tactical_feedback(&mut self, feedback: &str) {
        if self.width < 520 || self.height < 250 ||
            self.show_shooting_menu { return; }
        let hint = self.hovered_tactical_action
            .map(tactical_hud::HudAction::hint)
            .unwrap_or(feedback);
        if hint.is_empty() { return; }
        let message = text::fit(hint, self.width.saturating_sub(72).min(760));
        let w = (text::width(message, 1) + 20).min(self.width);
        let left = (self.width - w) / 2;
        // The 24-action strip now occupies five rows starting at
        // height-164; place the transient feedback ABOVE the entire panel,
        // not across its first row where it blocks tactical button labels.
        let top = if self.height >= 430 { self.height - 196 }
            else { self.height - 42 };
        for y in top..(top + text::HEIGHT + 12).min(self.height) {
            self.pixels[y * self.width + left
                ..y * self.width + left + w].fill(0x16263a);
        }
        text::draw_at(&mut self.pixels, self.width,
            left + 10, top + 6, message, 1, [231, 237, 248]);
    }

    /// F8 toggles measurements on the actual player window. Keep a small,
    /// fixed top-right readout separate from the authoritative combat world,
    /// its simulation RNG and recorded player actions. The HUD consumes only
    /// completed tick and finished framebuffer timings, never a target FPS.
    #[cfg(feature = "client")]
    pub fn draw_live_performance(&mut self, label: &str) {
        if self.width < 460 || self.height < 200 { return; }
        let label = text::fit(label, self.width.saturating_sub(36));
        let width = text::width(label, 1) + 16;
        let left = self.width.saturating_sub(width + 12);
        let top = 12_usize;
        let bottom = (top + text::HEIGHT + 12).min(self.height);
        for y in top..bottom {
            self.pixels[y * self.width + left
                ..y * self.width + left + width].fill(0x16263a);
        }
        text::draw_at(&mut self.pixels, self.width,
            left + 8, top + 6, label, 1, [235, 244, 252]);
    }

    fn draw_battle_result(
        &mut self, view: &NativeRenderWorldView<'_>, result: bees_sim::episode_terminal::EpisodeTerminalResult,
    ) {
        let title = battle_outcome_label(result);
        let reason = battle_outcome_reason(view, result);
        let detail = format!("{reason}  /  {:.2} s", result.duration_seconds);
        let context = format!("World {}  /  Episode {}", view.world_id.0, view.episode_revision + 1);
        let title_scale = if self.width >= 400 && self.height >= 176 { 2 } else { 1 };
        let top = if self.height >= 200 { 20 } else { 0 };
        let width = (text::width(&title, title_scale).max(text::width(&detail, 1))
            .max(text::width(&context, 1)) + 40).min(self.width);
        let left = (self.width - width) / 2;
        let panel_height = (title_scale + 2) * text::HEIGHT + 20;
        for y in top..(top + panel_height).min(self.height) {
            let start = y * self.width + left;
            self.pixels[start..start + width].fill(if y < top + 2 { 0xe1a34b } else { 0x152133 });
        }
        text::draw_centered(&mut self.pixels, self.width, top + 8, &context, 1, [155, 178, 206]);
        let title_top = top + 8 + text::HEIGHT;
        text::draw_centered(&mut self.pixels, self.width, title_top, &title, title_scale, [239, 247, 255]);
        text::draw_centered(&mut self.pixels, self.width, title_top + title_scale * text::HEIGHT,
            &detail, 1, [194, 211, 230]);
    }

    #[cfg(feature = "client")]
    pub(crate) fn draw_campaign_dialogue(&mut self, speaker: &str, message: &str) {
        let panel_width = self.width.min(680);
        let max_text_width = panel_width.saturating_sub(32).max(1);
        let mut rest = message;
        let mut lines = Vec::new();
        while !rest.is_empty() {
            let prefix = text::fit(rest, max_text_width);
            if prefix.is_empty() { break; }
            let length = if prefix.len() == rest.len() { prefix.len() }
                else { prefix.rfind(' ').filter(|&index| index != 0).unwrap_or(prefix.len()) };
            lines.push(&rest[..length]);
            rest = rest[length..].trim_start();
        }
        let panel_height = ((lines.len() + 2) * text::HEIGHT + 22).min(self.height);
        let top = self.height - panel_height;
        let left = (self.width - panel_width) / 2;
        for y in top..self.height {
            let color = if y == top { 0x75dbab } else { 0x111e2e };
            self.pixels[y * self.width + left..y * self.width + left + panel_width].fill(color);
        }
        text::draw_centered(&mut self.pixels, self.width, top + 6, speaker, 1, [117, 219, 171]);
        let mut y = top + 9 + text::HEIGHT;
        for line in lines {
            text::draw_centered(&mut self.pixels, self.width, y, line, 1, [239, 247, 255]);
            y += text::HEIGHT;
        }
        text::draw_centered(&mut self.pixels, self.width, y + 4, "L continue  /  K dismiss", 1, [155, 178, 206]);
    }

    fn render_obstacle_debris(
        &mut self, view: &NativeRenderWorldView<'_>, counts: &mut NativeRenderCounts,
    ) {
        use bees_sim::combat_world::CombatEvent;
        let frame = (view.world_id, view.episode_revision, view.tick);
        if self.debris_frame != Some(frame) {
            if self.debris_frame.is_some_and(|previous| (previous.0, previous.1) != (frame.0, frame.1)) {
                self.obstacle_debris.clear();
            }
            self.debris_frame = Some(frame);
            for event in view.combat.events() {
                let CombatEvent::FireTankObstacleBrokenApart { obstacle_id, origin, bounds, .. } = *event else { continue };
                // Fire Tank prefab defaults. This stream belongs exclusively
                // to the tactical renderer and never touches world RNG.
                let mut random = RandomStream::new((obstacle_id as u64).wrapping_mul(397) ^
                    u64::from(origin.x.to_bits()) ^ u64::from(origin.y.to_bits()));
                let mut unit = || (random.next_u64() >> 40) as f32 / 16_777_216.0;
                let sprite_count = fire_tank_debris_atlas().sprites.len();
                for _ in 0..24 {
                    let sprite = (unit() * sprite_count as f32) as usize;
                    let position = bounds.min + Vec2::new(
                        (bounds.max.x - bounds.min.x) * unit(),
                        (bounds.max.y - bounds.min.y) * unit(),
                    );
                    let radial = position - origin;
                    let direction = if radial.length_squared() < 0.01 {
                        let angle = unit() * std::f32::consts::TAU;
                        Vec2::new(angle.cos(), angle.sin())
                    } else {
                        radial * radial.length_squared().sqrt().recip()
                    };
                    let spread = Vec2::new(unit() * 2.0 - 1.0, unit() * 2.0 - 1.0) * 0.35;
                    let direction = direction + spread;
                    let velocity = direction * ((4.0 + 7.0 * unit()) * direction.length_squared().sqrt().recip());
                    self.obstacle_debris.push(ObstacleDebris {
                        born: view.tick, position, velocity,
                        rotation: unit() * 360.0, spin: (2.0 * unit() - 1.0) * 360.0,
                        lifetime: 1.5 * (0.8 + 0.4 * unit()), scale: 3.0 + 3.0 * unit(),
                        sprite,
                    });
                }
            }
        }
        let age = |born| view.tick.saturating_sub(born) as f32 * view.fixed_step_seconds;
        self.obstacle_debris.retain(|piece| age(piece.born) < piece.lifetime);
        for index in 0..self.obstacle_debris.len() {
            let piece = &self.obstacle_debris[index];
            let seconds = age(piece.born);
            let position = piece.position + piece.velocity * (-(-1.25 * seconds).exp_m1() / 1.25);
            counts.pieces += 1;
            let alpha = ((1.0 - seconds / piece.lifetime) / 0.4).min(1.0);
            let angle = (piece.rotation + piece.spin * seconds).to_radians();
            let atlas = fire_tank_debris_atlas();
            let scale = piece.scale * self.camera.pixels_per_unit / atlas.pixels_per_unit;
            if !self.draw_sprite(&atlas.sprites[piece.sprite], position, angle, scale, alpha) {
                counts.culled_pieces += 1;
            }
        }
    }

    fn draw_fire_tank(&mut self, tank: &bees_sim::map_object::CombatMapObject, pose: Pose, seconds: f32) {
        static BODY: OnceLock<SpriteAtlas> = OnceLock::new();
        static SMOKE: OnceLock<SpriteAtlas> = OnceLock::new();
        let body = BODY.get_or_init(|| SpriteAtlas::decode(include_bytes!("../../../../assets/fire-tank-body.rgba")));
        const STAGES: usize = 4;
        let lost = i64::from(tank.max_health) - i64::from(tank.health.max(0));
        let stage = ((lost * STAGES as i64) / i64::from(tank.max_health))
            .clamp(0, STAGES as i64 - 1) as usize;
        // Stable cosmetic variation across frames, independent of gameplay RNG.
        let variant = tank.entity_id as usize % (body.sprites.len() / STAGES);
        let angle = pose.rotation_degrees.to_radians();
        self.draw_sprite(&body.sprites[variant * STAGES + stage], pose.position, angle,
            self.camera.pixels_per_unit / body.pixels_per_unit, 1.0);
        if stage == 0 { return; }

        let smoke = SMOKE.get_or_init(|| SpriteAtlas::decode(include_bytes!("../../../../assets/fire-tank-smoke.rgba")));
        let frame = (seconds * 5.0) as usize;
        // CanisterBomb.SmokePositions, in prefab-local coordinates.
        let offsets = [Vec2::new(2.25, -1.2), Vec2::new(-2.65, 1.4), Vec2::new(-5.1, -0.2)];
        for (plume, &offset) in offsets[..stage].iter().enumerate() {
            self.draw_sprite(&smoke.sprites[(frame + plume) % smoke.sprites.len()],
                pose.transform_point(offset), angle, self.camera.pixels_per_unit / smoke.pixels_per_unit, 1.0);
        }
    }

    fn draw_sprite(&mut self, sprite: &NativeSprite, position: Vec2, angle: f32, scale: f32, alpha: f32) -> bool {
        let (center_x, center_y) = self.project(position);
        let (sin, cos) = angle.sin_cos();
        let Some((px0, py0, px1, py1)) = sprite.painted_bounds else {
            return false;
        };
        let half_w = sprite.width as f32 * 0.5;
        let half_h = sprite.height as f32 * 0.5;
        // Rotate the NONTRANSPARENT source bounds using the same
        // transformation as the unchanged inverse texture sampler below.
        // The sprite's original center/pivot is preserved even if almost
        // its entire bitmap is transparent padding.
        let alpha_cx = (px0 + px1) as f32 * 0.5 - half_w;
        let alpha_cy = (py0 + py1) as f32 * 0.5 - half_h;
        let half_alpha_w = (px1 - px0) as f32 * 0.5;
        let half_alpha_h = (py1 - py0) as f32 * 0.5;
        let bounds_x = center_x + scale * (alpha_cx * cos + alpha_cy * sin);
        let bounds_y = center_y + scale * (-alpha_cx * sin + alpha_cy * cos);
        let extent_x = scale * (half_alpha_w * cos.abs() + half_alpha_h * sin.abs());
        let extent_y = scale * (half_alpha_w * sin.abs() + half_alpha_h * cos.abs());
        let left = (bounds_x - extent_x).floor().clamp(0.0, self.width as f32) as usize;
        let right = (bounds_x + extent_x).ceil().clamp(0.0, self.width as f32) as usize;
        let top = (bounds_y - extent_y).floor().clamp(0.0, self.height as f32) as usize;
        let bottom = (bounds_y + extent_y).ceil().clamp(0.0, self.height as f32) as usize;
        if left >= right || top >= bottom { return false; }
        self.sprite_raster_candidate_pixels = self.sprite_raster_candidate_pixels
            .saturating_add(((right - left) as u64).saturating_mul((bottom - top) as u64));
        // Affine inverse transform: evaluate row origins once, then one
        // scaled horizontal offset per pixel. The old raster path divided
        // by scale for EVERY pixel, even for large transparent sprite areas.
        // Compute offsets from the row origin (not accumulated additions)
        // to avoid growing rounding error across wide source sprites.
        let inv_scale = scale.recip();
        let dx_left = (left as f32 + 0.5 - center_x) * inv_scale;
        let du = cos * inv_scale;
        let dv = sin * inv_scale;
        for y in top..bottom {
            let dy = (y as f32 + 0.5 - center_y) * inv_scale;
            let u_left = dx_left * cos - dy * sin + half_w;
            let v_left = dx_left * sin + dy * cos + half_h;
            let row = y * self.width;
            for x in left..right {
                let delta = (x - left) as f32;
                let u = (u_left + delta * du).floor() as isize;
                let v = (v_left + delta * dv).floor() as isize;
                if u < 0 || v < 0 || u as usize >= sprite.width || v as usize >= sprite.height { continue; }
                let offset = (v as usize * sprite.width + u as usize) * 4;
                let rgba = &sprite.pixels[offset..offset + 4];
                let opacity = (f32::from(rgba[3]) * alpha).round() as u32;
                if opacity == 0 { continue; }
                let pixel = &mut self.pixels[row + x];
                // Most interior ship pixels are fully opaque. They should
                // not pay three divide-by-255 blends per sprite layer.
                *pixel = if opacity == 255 {
                    self.sprite_raster_opaque_pixels += 1;
                    (u32::from(rgba[0]) << 16) |
                    (u32::from(rgba[1]) << 8) |
                    u32::from(rgba[2])
                } else {
                    self.sprite_raster_blended_pixels += 1;
                    text::blend(*pixel, [rgba[0], rgba[1], rgba[2]], opacity)
                };
            }
        }
        true
    }

    /// Portable capture for diagnostics, using exactly the window's pixel buffer.
    pub fn write_ppm(&self, mut writer: impl Write) -> io::Result<()> {
        write!(writer, "P6\n{} {}\n255\n", self.width, self.height)?;
        let mut row = Vec::with_capacity(self.width * 3);
        for pixels in self.pixels.chunks(self.width) {
            row.clear();
            for &p in pixels { row.extend_from_slice(&[(p >> 16) as u8, (p >> 8) as u8, p as u8]); }
            writer.write_all(&row)?;
        }
        Ok(())
    }

    fn project(&self, point: Vec2) -> (f32, f32) {
        ((point.x - self.camera.center.x) * self.camera.pixels_per_unit + self.width as f32 * 0.5,
         (self.camera.center.y - point.y) * self.camera.pixels_per_unit + self.height as f32 * 0.5)
    }

    fn cross(&mut self, point: Vec2, radius: f32, color: u32) {
        let (x, y) = self.project(point);
        self.line(x - radius, y, x + radius, y, color);
        self.line(x, y - radius, x, y + radius, color);
    }

    fn shape(&mut self, shape: &ShipShape, pose: Pose, color: u32) {
        // Match Pose::transform_point exactly, but compute the rotation
        // once per shape instead of twice per edge. Large authored polygon
        // hulls and weapon-range circles can have dozens of segments.
        let (sin, cos) = pose.rotation_degrees.to_radians().sin_cos();
        let world_point = |point: Vec2| Vec2::new(
            pose.position.x + point.x * cos - point.y * sin,
            pose.position.y + point.x * sin + point.y * cos,
        );
        let mut edge = |a, b| {
            let (x0, y0) = self.project(world_point(a));
            let (x1, y1) = self.project(world_point(b));
            self.line(x0, y0, x1, y1, color);
        };
        match shape {
            ShipShape::Polygon(vertices) => {
                if let Some(&last) = vertices.last() {
                    let mut previous = last;
                    for &point in vertices.iter() { edge(previous, point); previous = point; }
                }
            }
            ShipShape::Box { offset: o, half_extents: h } => {
                let corners = [*o + Vec2::new(-h.x, -h.y), *o + Vec2::new(h.x, -h.y),
                    *o + Vec2::new(h.x, h.y), *o + Vec2::new(-h.x, h.y)];
                for i in 0..4 { edge(corners[i], corners[(i + 1) % 4]); }
            }
            ShipShape::Circle { offset, radius } => {
                // All circles use the identical 32-segment tactical outline.
                // Calculate unit vectors once, not 32 trigonometric calls for
                // every projectile, ship and inspection range in every frame.
                const SEGMENTS: usize = 32;
                static UNIT_CIRCLE: OnceLock<[Vec2; SEGMENTS + 1]> = OnceLock::new();
                let vertices = UNIT_CIRCLE.get_or_init(|| std::array::from_fn(|i| {
                    let (sin, cos) = (std::f32::consts::TAU * i as f32
                        / SEGMENTS as f32).sin_cos();
                    Vec2::new(cos, sin)
                }));
                for pair in vertices.windows(2) {
                    edge(*offset + pair[0] * *radius,
                         *offset + pair[1] * *radius);
                }
            }
        }
    }

    fn line(&mut self, x0: f32, y0: f32, x1: f32, y1: f32, color: u32) {
        // Liang-Barsky clipping bounds raster work even for far-off-map geometry.
        if ![x0, y0, x1, y1].iter().all(|x| x.is_finite()) { return; }
        let dx = x1 - x0;
        let dy = y1 - y0;
        let mut lo = 0.0_f32;
        let mut hi = 1.0_f32;
        for (p, q) in [(-dx, x0), (dx, self.width as f32 - 1.0 - x0),
            (-dy, y0), (dy, self.height as f32 - 1.0 - y0)] {
            if p == 0.0 { if q < 0.0 { return; } }
            else if p < 0.0 { lo = lo.max(q / p); }
            else { hi = hi.min(q / p); }
        }
        if lo > hi { return; }
        // Clip in world-to-screen float space once. The source path
        // divided and interpolated two floats for *every pixel* along
        // every ship hull, projectile, circle and tactical command line.
        // Integer Bresenham gives contiguous clipped outlines with only
        // additions and comparisons in the inner loop; it is independent
        // of simulation timing, physics and neural inference.
        let max_x = self.width as i32 - 1;
        let max_y = self.height as i32 - 1;
        let mut x = (x0 + dx * lo).round() as i32;
        let mut y = (y0 + dy * lo).round() as i32;
        let x_end = ((x0 + dx * hi).round() as i32).clamp(0, max_x);
        let y_end = ((y0 + dy * hi).round() as i32).clamp(0, max_y);
        x = x.clamp(0, max_x);
        y = y.clamp(0, max_y);
        let delta_x = (x_end - x).abs();
        let delta_y = -(y_end - y).abs();
        let step_x = if x < x_end { 1 } else { -1 };
        let step_y = if y < y_end { 1 } else { -1 };
        let mut error = delta_x + delta_y;
        loop {
            self.pixels[y as usize * self.width + x as usize] = color;
            if x == x_end && y == y_end { break; }
            let doubled = error * 2;
            if doubled >= delta_y { error += delta_y; x += step_x; }
            if doubled <= delta_x { error += delta_x; y += step_y; }
        }
    }
}

fn side_color(side: i32) -> u32 {
    const COLORS: [u32; 6] = [0x62d6ff, 0xff778e, 0x8ee6a1, 0xd3a0ff, 0xffd166, 0xe4ecfa];
    COLORS[side.rem_euclid(COLORS.len() as i32) as usize]
}
