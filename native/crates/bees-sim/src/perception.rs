//! Source-matched v26 perception sections, not an alternate training encoder.
//!
//! Unity: Scripts/Scenes/RlCombatPerception.cs and
//! Scripts/Scenes/RlOneVsOneAgent.cs (RlPolicyCoordinateFrame).
//! The caller supplies authoritative ship/level state. Unsupported sections
//! remain NaN in PolicyBatch, never zero-filled to fabricate a valid policy
//! observation. All functions write directly into a reusable observation row.

use crate::collision::Aabb;
use crate::control::PolicyFrame;
use crate::geometry::Vec2;
use crate::policy::{OBSERVATION_FLOATS, PERCEPTION_OBSERVATION_FLOATS};

pub const SELF_LEN: usize = 25;
pub const CAPABILITY_LEN: usize = 12;
pub const PARENT_CARRIER_LEN: usize = 41;
pub const ALLY_LEN: usize = 45;
pub const MAX_ALLIES: usize = 64;
pub const ENEMY_LEN: usize = 41;
pub const MAX_ENEMIES: usize = 64;
pub const SELF_WEAPON_LEN: usize = 15;
pub const WEAPON_SLOTS: usize = 5;
pub const MINING_LEN: usize = 7;
pub const MINING_SLOTS: usize = 8;
pub const MAP_OBJECT_LEN: usize = 12;
pub const MAP_OBJECT_SLOTS: usize = 64;
pub const ASTEROID_LEN: usize = 11;
pub const ASTEROID_SLOTS: usize = 48;
pub const OBJECTIVE_LEN: usize = 16;
pub const NAV_SIZE: usize = 21;
pub const NAV_LEN: usize = NAV_SIZE * NAV_SIZE;
pub const EXPLORATION_SIZE: usize = 16;
pub const EXPLORATION_LEN: usize = EXPLORATION_SIZE * EXPLORATION_SIZE;

pub const SELF_START: usize = 0;
pub const CAPABILITY_START: usize = SELF_START + SELF_LEN;
pub const PARENT_CARRIER_START: usize = CAPABILITY_START + CAPABILITY_LEN;
pub const ALLIES_START: usize = PARENT_CARRIER_START + PARENT_CARRIER_LEN;
pub const ENEMIES_START: usize = ALLIES_START + MAX_ALLIES * ALLY_LEN;
pub const SELF_WEAPONS_START: usize = ENEMIES_START + MAX_ENEMIES * ENEMY_LEN;
pub const MINING_START: usize = SELF_WEAPONS_START + WEAPON_SLOTS * SELF_WEAPON_LEN;
pub const MAP_OBJECTS_START: usize = MINING_START + MINING_SLOTS * MINING_LEN;
pub const ASTEROIDS_START: usize = MAP_OBJECTS_START + MAP_OBJECT_SLOTS * MAP_OBJECT_LEN;
pub const OBJECTIVE_START: usize = ASTEROIDS_START + ASTEROID_SLOTS * ASTEROID_LEN;
pub const NAV_START: usize = OBJECTIVE_START + OBJECTIVE_LEN;
pub const EXPLORATION_START: usize = NAV_START + NAV_LEN;
pub const PERCEPTION_END: usize = EXPLORATION_START + EXPLORATION_LEN;

// Compile-time parity: if any source-derived block length shifts, the
// frozen v26 observation layout can no longer silently continue compiling.
const _: [(); PERCEPTION_OBSERVATION_FLOATS] = [(); PERCEPTION_END];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PerceptionError {
    WrongRowSize { actual: usize },
    InvalidSource,
    UnsupportedShipType(u8),
}

/// Live ship and level fields corresponding, in order, to Unity's
/// AddSelfObservations. The caller must use episode-permuted identity, actual
/// effective sight range, current ship statistics, and true Level counts.
/// Never substitute the numeric runtime entity ID for `identity`.
#[derive(Clone, Copy, Debug)]
pub struct SelfSource {
    pub identity: f32,
    pub ship_type: u8,
    pub position: Vec2,
    pub level_bounds: Aabb,
    pub rotation_degrees: f32,
    pub health: i32,
    pub max_health: i32,
    pub speed: f32,
    pub current_speed: f32,
    pub linear_velocity: Vec2,
    pub rotation_speed: f32,
    pub longest_side: f32,
    pub effective_vision_range: f32,
    pub max_range: f32,
    pub firepower: f32,
    /// Ship.IsMobile && !CurrentMovementDisabledForTeam(teamId)
    pub can_move: bool,
    pub is_bomber: bool,
    pub is_carrier: bool,
    pub has_weapons: bool,
    pub has_turrets: bool,
    pub has_special: bool,
    pub special_ready: bool,
    pub live_allies_including_self: u32,
    pub live_enemies: u32,
}

/// Remaining real ship/coordinator values for Unity's AddCapabilityObservations.
/// The two leading capability values are taken from SelfSource so they cannot
/// disagree with the same features in the self-observation block.
#[derive(Clone, Copy, Debug)]
pub struct CapabilitySource {
    pub special_resource_fraction: f32,
    pub special_cooldown_fraction: f32,
    pub special_phase: f32,
    pub mining_possible: bool,
    pub healing_possible: bool,
    pub warp_possible: bool,
    pub is_carrier_child: bool,
    pub has_living_parent_carrier: bool,
    pub ship_mined_tsv: i32,
    pub team_retainable_mined_tsv: i32,
    pub side_starting_tsv: i32,
}

const SHIP_SCALARS: [u8; 24] = [
    11, 2, 19, 7, 22, 4, 15, 0, 17, 9, 23, 5,
    13, 20, 1, 16, 8, 21, 3, 18, 10, 14, 6, 12,
];
const WEAPON_SCALARS: [u8; 10] = [4, 9, 1, 7, 0, 6, 3, 8, 2, 5];

#[inline]
pub fn ship_type_scalar(ship_type: u8) -> Result<f32, PerceptionError> {
    let value = SHIP_SCALARS.get(usize::from(ship_type))
        .ok_or(PerceptionError::UnsupportedShipType(ship_type))?;
    Ok(-1.0 + 2.0 * f32::from(*value) / 23.0)
}

#[inline]
pub fn weapon_type_scalar(weapon_type: u8) -> Option<f32> {
    WEAPON_SCALARS.get(usize::from(weapon_type))
        .map(|&value| -1.0 + 2.0 * f32::from(value) / 9.0)
}

/// Port of RlCombatPerception.NormalizePositive.
#[inline]
pub fn normalize_positive(value: f32, scale: f32) -> f32 {
    let positive = value.max(0.0);
    if positive <= 0.0 { 0.0 } else { positive / (positive + scale.max(0.0001)) }
}

/// Port of RlCombatPerception.NormalizeSignedCoordinate.
#[inline]
pub fn normalize_signed_coordinate(value: f32, min: f32, max: f32) -> f32 {
    let range = max - min;
    if range <= 0.0001 { 0.0 }
    else { (((value - min) / range) * 2.0 - 1.0).clamp(-1.0, 1.0) }
}

#[inline]
fn flag(value: bool) -> f32 { if value { 1.0 } else { 0.0 } }

fn check_row(row: &[f32]) -> Result<(), PerceptionError> {
    if row.len() != OBSERVATION_FLOATS {
        return Err(PerceptionError::WrongRowSize { actual: row.len() });
    }
    Ok(())
}

#[inline]
fn policy_heading(degrees: f32, frame: PolicyFrame) -> Vec2 {
    let radians = degrees.to_radians();
    frame.world_to_policy(Vec2::new(-radians.sin(), radians.cos()))
}

/// Writes precisely the first 25 observation features. No other block is
/// changed: unported sections must stay invalid in a PolicyBatch row.
pub fn write_self(
    row: &mut [f32],
    source: SelfSource,
    frame: PolicyFrame,
) -> Result<(), PerceptionError> {
    check_row(row)?;
    let bounds = source.level_bounds;
    let finite = [
        source.identity, source.position.x, source.position.y,
        bounds.min.x, bounds.min.y, bounds.max.x, bounds.max.y,
        source.rotation_degrees, source.speed, source.current_speed,
        source.linear_velocity.x, source.linear_velocity.y,
        source.rotation_speed, source.longest_side,
        source.effective_vision_range, source.max_range, source.firepower,
    ];
    if finite.iter().any(|v| !v.is_finite()) ||
        !(-1.0..=1.0).contains(&source.identity) ||
        bounds.min.x > bounds.max.x || bounds.min.y > bounds.max.y ||
        source.max_health <= 0 || source.health < 0 ||
        source.health > source.max_health {
        return Err(PerceptionError::InvalidSource);
    }
    let kind = ship_type_scalar(source.ship_type)?;
    let position = frame.world_to_policy(Vec2::new(
        normalize_signed_coordinate(source.position.x, bounds.min.x, bounds.max.x),
        normalize_signed_coordinate(source.position.y, bounds.min.y, bounds.max.y),
    ));
    let level_size = Vec2::new(
        (bounds.max.x - bounds.min.x).max(0.0),
        (bounds.max.y - bounds.min.y).max(0.0),
    );
    let extents = if frame.quarter_turns() & 1 == 0 {
        level_size
    } else {
        Vec2::new(level_size.y, level_size.x)
    };
    let heading = policy_heading(source.rotation_degrees, frame);
    let visible_speed = if source.linear_velocity.length_squared() <= 0.0001 {
        0.0
    } else {
        source.current_speed
    };
    // This exact ordering comes from AddSelfObservations; the scalar type
    // code precedes position and the counts remain its final two fields.
    let values: [f32; SELF_LEN] = [
        source.identity, kind,
        position.x, position.y,
        normalize_positive(extents.x, 100.0),
        normalize_positive(extents.y, 100.0),
        heading.x, heading.y,
        (source.health as f32 / source.max_health as f32).clamp(0.0, 1.0),
        normalize_positive(source.speed, 20.0),
        normalize_positive(visible_speed, 20.0),
        normalize_positive(source.rotation_speed, 240.0),
        normalize_positive(source.longest_side, 10.0),
        normalize_positive(source.effective_vision_range, 80.0),
        normalize_positive(source.max_range, 80.0),
        normalize_positive(source.firepower, 200.0),
        flag(source.can_move),
        flag(source.is_bomber),
        flag(source.is_carrier),
        flag(source.has_weapons),
        flag(source.has_turrets),
        flag(source.has_special),
        flag(source.special_ready),
        normalize_positive(source.live_allies_including_self as f32, 64.0),
        normalize_positive(source.live_enemies as f32, 64.0),
    ];
    if values.iter().any(|v| !v.is_finite()) {
        return Err(PerceptionError::InvalidSource);
    }
    row[SELF_START..CAPABILITY_START].copy_from_slice(&values);
    Ok(())
}

/// Writes precisely the 12 capability observations from Unity.
pub fn write_capability(
    row: &mut [f32],
    ship: &SelfSource,
    capability: CapabilitySource,
) -> Result<(), PerceptionError> {
    check_row(row)?;
    let values: [f32; CAPABILITY_LEN] = [
        flag(ship.has_special),
        flag(ship.special_ready),
        capability.special_resource_fraction,
        capability.special_cooldown_fraction,
        capability.special_phase,
        flag(capability.mining_possible),
        flag(capability.healing_possible),
        flag(capability.warp_possible),
        flag(capability.is_carrier_child),
        flag(capability.has_living_parent_carrier),
        normalize_positive(capability.ship_mined_tsv.max(0) as f32,
            capability.side_starting_tsv.max(1) as f32),
        normalize_positive(capability.team_retainable_mined_tsv.max(0) as f32,
            capability.side_starting_tsv.max(1) as f32),
    ];
    if values.iter().any(|value| !value.is_finite()) {
        return Err(PerceptionError::InvalidSource);
    }
    row[CAPABILITY_START..PARENT_CARRIER_START].copy_from_slice(&values);
    Ok(())
}

// The same quarter-turn permutation is correct for the odd local 21x21
// navigation grid and the even global 16x16 exploration grid. Precompute
// both as static arrays at compile time, as the Unity encoder does at load.
const fn permute_grid_index(index: usize, size: usize, turns: usize) -> usize {
    let x = index % size;
    let y = index / size;
    let (world_x, world_y) = match turns % 4 {
        1 => (y, size - 1 - x),
        2 => (size - 1 - x, size - 1 - y),
        3 => (size - 1 - y, x),
        _ => (x, y),
    };
    world_y * size + world_x
}

const fn grid_maps<const N: usize>(size: usize) -> [[usize; N]; 4] {
    let mut maps = [[0usize; N]; 4];
    let mut turns = 0;
    while turns < 4 {
        let mut index = 0;
        while index < N {
            maps[turns][index] = permute_grid_index(index, size, turns);
            index += 1;
        }
        turns += 1;
    }
    maps
}

const NAV_MAPS: [[usize; NAV_LEN]; 4] = grid_maps::<NAV_LEN>(NAV_SIZE);
const EXPLORATION_MAPS: [[usize; EXPLORATION_LEN]; 4] =
    grid_maps::<EXPLORATION_LEN>(EXPLORATION_SIZE);

/// Source-defined permanent objective reservation. This block is currently
/// ALWAYS zero in Unity; unlike unported features, filling it is valid.
pub fn write_reserved_objectives(row: &mut [f32]) -> Result<(), PerceptionError> {
    check_row(row)?;
    row[OBJECTIVE_START..NAV_START].fill(0.0);
    Ok(())
}

/// Pass the actual 21x21 local occupancy already computed by Level geometry.
/// Input indexing is world-grid order; output is policy-grid order.
pub fn write_navigation(
    row: &mut [f32],
    occupancy: &[f32; NAV_LEN],
    frame: PolicyFrame,
) -> Result<(), PerceptionError> {
    check_row(row)?;
    if occupancy.iter().any(|value| !value.is_finite()) {
        return Err(PerceptionError::InvalidSource);
    }
    let indices = &NAV_MAPS[usize::from(frame.quarter_turns())];
    for (slot, &source) in indices.iter().enumerate() {
        row[NAV_START + slot] = occupancy[source];
    }
    Ok(())
}

/// Unity uses a team-shared global visibility grid with per-cell most recent
/// sight progress. None means the coordinator has NO exploration snapshot;
/// the source-defined output is then 256 zeros. Values < 0 mean unseen.
pub fn write_exploration(
    row: &mut [f32],
    last_seen_progress: Option<&[f32; EXPLORATION_LEN]>,
    episode_progress: f32,
    frame: PolicyFrame,
) -> Result<(), PerceptionError> {
    check_row(row)?;
    if !episode_progress.is_finite() ||
        last_seen_progress.is_some_and(|values|
            values.iter().any(|value| !value.is_finite())) {
        return Err(PerceptionError::InvalidSource);
    }
    let out = &mut row[EXPLORATION_START..PERCEPTION_END];
    if let Some(last_seen) = last_seen_progress {
        let indices = &EXPLORATION_MAPS[usize::from(frame.quarter_turns())];
        for (slot, &index) in indices.iter().enumerate() {
            let observed = last_seen[index];
            out[slot] = if observed < 0.0 { 0.0 }
                else { (1.0 - (episode_progress - observed).max(0.0))
                    .clamp(0.0, 1.0) };
        }
    } else {
        out.fill(0.0);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn source() -> SelfSource {
        SelfSource {
            identity: -0.25, ship_type: 0,
            position: Vec2::new(20.0, 30.0),
            level_bounds: Aabb {
                min: Vec2::new(-50.0, -100.0),
                max: Vec2::new(50.0, 100.0),
            },
            rotation_degrees: 0.0,
            health: 25, max_health: 100,
            speed: 20.0, current_speed: 10.0,
            linear_velocity: Vec2::ZERO,
            rotation_speed: 120.0, longest_side: 10.0,
            effective_vision_range: 80.0,
            max_range: 80.0, firepower: 200.0,
            can_move: true, is_bomber: false,
            is_carrier: false, has_weapons: true,
            has_turrets: true, has_special: false,
            special_ready: false,
            live_allies_including_self: 3, live_enemies: 2,
        }
    }

    #[test]
    fn frozen_layout_and_source_matched_self_normalization() {
        assert_eq!(SELF_START, 0);
        assert_eq!(CAPABILITY_START, 25);
        assert_eq!(PARENT_CARRIER_START, 37);
        assert_eq!(ALLIES_START, 78);
        assert_eq!(ENEMIES_START, 2958);
        assert_eq!(SELF_WEAPONS_START, 5582);
        assert_eq!(MINING_START, 5657);
        assert_eq!(MAP_OBJECTS_START, 5713);
        assert_eq!(ASTEROIDS_START, 6481);
        assert_eq!(OBJECTIVE_START, 7009);
        assert_eq!(NAV_START, 7025);
        assert_eq!(EXPLORATION_START, 7466);
        assert_eq!(PERCEPTION_END, 7722);

        let mut row = [f32::NAN; OBSERVATION_FLOATS];
        write_self(&mut row, source(), PolicyFrame::new(1)).unwrap();
        assert_eq!(row[0], -0.25);
        assert_eq!(row[1], -1.0 + 22.0 / 23.0);
        assert_eq!(row[2], -0.3); // -normalize(y), frame 1
        assert!((row[3] - 0.4).abs() < 1e-6); // normalize(x), frame 1
        assert!((row[4] - 2.0 / 3.0).abs() < 1e-6); // height 200 / (200+100)
        assert_eq!(row[6], -1.0); // +Y physical forward -> -X in frame 1
        assert!(row[7].abs() < 1e-6);
        assert_eq!(row[8], 0.25);
        assert_eq!(row[9], 0.5);
        assert_eq!(row[10], 0.0); // stopped despite positive CurrentSpeed
        assert_eq!(row[16], 1.0);
        assert_eq!(row[22], 0.0);
        assert!(row[CAPABILITY_START..].iter().all(|x| x.is_nan()));
    }

    #[test]
    fn capabilities_share_source_flags_and_mined_economy_normalization() {
        let mut row = [f32::NAN; OBSERVATION_FLOATS];
        write_capability(&mut row, &source(), CapabilitySource {
            special_resource_fraction: 0.5,
            special_cooldown_fraction: 0.25,
            special_phase: 0.75,
            mining_possible: true, healing_possible: false,
            warp_possible: false, is_carrier_child: false,
            has_living_parent_carrier: false,
            ship_mined_tsv: 100,
            team_retainable_mined_tsv: 300,
            side_starting_tsv: 100,
        }).unwrap();
        assert_eq!(row[CAPABILITY_START], 0.0);
        assert_eq!(row[CAPABILITY_START + 5], 1.0);
        assert_eq!(row[CAPABILITY_START + 10], 0.5);
        assert_eq!(row[CAPABILITY_START + 11], 0.75);
    }

    #[test]
    fn both_grid_sizes_rotate_without_changing_world_cells() {
        for frame_index in 0..4 {
            let frame = PolicyFrame::new(frame_index);
            let mut row = [f32::NAN; OBSERVATION_FLOATS];
            let mut navigation = [0.0f32; NAV_LEN];
            for (index, value) in navigation.iter_mut().enumerate() {
                *value = index as f32;
            }
            write_navigation(&mut row, &navigation, frame).unwrap();
            for index in 0..NAV_LEN {
                assert_eq!(row[NAV_START + index],
                    navigation[permute_grid_index(index, NAV_SIZE,
                        usize::from(frame_index))]);
            }
            let mut last_seen = [-1.0f32; EXPLORATION_LEN];
            last_seen[0] = 0.75;
            write_exploration(&mut row, Some(&last_seen), 1.0, frame).unwrap();
            assert_eq!(row[EXPLORATION_START..PERCEPTION_END]
                .iter().filter(|&&value| value > 0.0).count(), 1);
            assert!(row[EXPLORATION_START..PERCEPTION_END]
                .iter().all(|value| *value == 0.0 || *value == 0.75));
            write_reserved_objectives(&mut row).unwrap();
            assert!(row[OBJECTIVE_START..NAV_START]
                .iter().all(|value| *value == 0.0));
            assert!(row[ENEMIES_START..SELF_WEAPONS_START]
                .iter().all(|value| value.is_nan()));
        }
    }
}
