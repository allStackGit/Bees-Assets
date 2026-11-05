//! Source-matched v26 observed ship/weapon blocks for native inference.
//!
//! Source: RlCombatPerception.AddEntityObservation, AddObservedWeaponObservation,
//! AddParentCarrierObservations, AddAllySlots, AddEntitySlots, and
//! SortShipsForObservation. Inputs are authoritative live Level entries.
//!
//! "Visible enemy" in Unity is faction-wide, ever-discovered VisionCache;
//! callers MUST supply that actual registry, not nearby or line-of-sight
//! guesses. Slots are sorted by distance, ship type, fleet id, runtime id.

use crate::control::PolicyFrame;
use crate::geometry::Vec2;
use crate::perception::{
    ALLIES_START, ALLY_LEN, ENEMIES_START, ENEMY_LEN, MAX_ALLIES, MAX_ENEMIES,
    PARENT_CARRIER_LEN, PARENT_CARRIER_START, PerceptionError, normalize_positive,
    ship_type_scalar, weapon_type_scalar, policy_heading, squash_signed_distance,
};
use crate::policy::OBSERVATION_FLOATS;

const OBSERVED_WEAPON_LEN: usize = 5;
const OBSERVED_WEAPON_SLOTS: usize = 5;
const ENTITY_CORE_LEN: usize = 16;
 #[derive(Clone, Copy, Debug)]
pub struct ObservedWeapon {
    pub weapon_type: u8,
    pub range: f32,
    pub power: f32,
    pub rate_of_fire: f32,
}

/// A live ship with its actual type, stats, weapon mounts and identity.
/// The caller owns the exact fleet, visibility, side and communication data.
/// None for a weapon slot means no physical weapon in that exact slot.
#[derive(Clone, Copy, Debug)]
pub struct ObservedShip {
    pub entity_id: i64,
    pub fleet_ship_id: Option<i64>,
    pub side: i32,
    pub ship_type: u8,
    pub identity: f32,
    pub position: Vec2,
    pub rotation_degrees: f32,
    pub health: i32,
    pub max_health: i32,
    pub speed: f32,
    pub current_speed: f32,
    pub linear_velocity: Vec2,
    pub longest_side: f32,
    pub max_range: f32,
    pub firepower: f32,
    pub is_mobile: bool,
    pub is_bomber: bool,
    pub is_alive: bool,
    pub mined_tsv: i32,
    pub weapons: [Option<ObservedWeapon>; OBSERVED_WEAPON_SLOTS],
    /// Unity's private allied communication register, if present.
    pub communication: Option<[f32; 4]>,
}

fn observed_ship_valid(ship: &ObservedShip) -> bool {
    ship.entity_id > 0 &&
    ship.identity.is_finite() && (-1.0..=1.0).contains(&ship.identity) &&
    ship.position.is_finite() && ship.rotation_degrees.is_finite() &&
    ship.max_health > 0 && ship.health >= 0 && ship.health <= ship.max_health &&
    [ship.speed, ship.current_speed, ship.linear_velocity.x,
        ship.linear_velocity.y, ship.longest_side, ship.max_range, ship.firepower]
        .iter().all(|v| v.is_finite()) &&
    ship.communication.map_or(true, |values| values.iter().all(|v| v.is_finite())) &&
    ship.weapons.iter().flatten().all(|weapon| {
        weapon_type_scalar(weapon.weapon_type).is_some() &&
        [weapon.range, weapon.power, weapon.rate_of_fire]
            .iter().all(|v| v.is_finite())
    }) &&
    ship_type_scalar(ship.ship_type).is_ok()
}

fn validate_row(row: &[f32]) -> Result<(), PerceptionError> {
    if row.len() != OBSERVATION_FLOATS {
        return Err(PerceptionError::WrongRowSize { actual: row.len() });
    }
    Ok(())
}

/// Writes the full 41-float ship summary, preserving the source's unusual
/// ordering: presence then identity, 14 further core fields, then five
/// five-float weapon summaries. No extra "enemy weapon mounts" are invented.
fn write_ship(
    output: &mut [f32],
    ship: ObservedShip,
    observer_position: Vec2,
    frame: PolicyFrame,
    side_starting_tsv: i32,
) -> Result<(), PerceptionError> {
    if output.len() != ENEMY_LEN ||
        !observed_ship_valid(&ship) || !observer_position.is_finite() {
        return Err(PerceptionError::InvalidSource);
    }
    let relative = frame.world_to_policy(ship.position - observer_position);
    let heading = policy_heading(ship.rotation_degrees, frame);
    let observed_speed = if ship.linear_velocity.length_squared() <= 0.0001 {
        0.0
    } else { ship.current_speed };
    let scalar = ship_type_scalar(ship.ship_type)?;
    let mined = normalize_positive(ship.mined_tsv.max(0) as f32,
        side_starting_tsv.max(1) as f32);
    let core: [f32; ENTITY_CORE_LEN] = [
        1.0, ship.identity,
        squash_signed_distance(relative.x),
        squash_signed_distance(relative.y),
        heading.x, heading.y,
        (ship.health as f32 / ship.max_health as f32).clamp(0.0, 1.0),
        normalize_positive(ship.speed, 20.0),
        normalize_positive(observed_speed, 20.0),
        normalize_positive(ship.longest_side, 10.0),
        normalize_positive(ship.max_range, 80.0),
        normalize_positive(ship.firepower, 200.0),
        if ship.is_mobile { 1.0 } else { 0.0 },
        if ship.is_bomber { 1.0 } else { 0.0 },
        mined,
        scalar,
    ];
    if core.iter().any(|v| !v.is_finite()) {
        return Err(PerceptionError::InvalidSource);
    }
    output[..ENTITY_CORE_LEN].copy_from_slice(&core);
    let weapon_section = &mut output[ENTITY_CORE_LEN..];
    for (slot, weapon) in ship.weapons.iter().enumerate() {
        let start = slot * OBSERVED_WEAPON_LEN;
        let values = if let Some(weapon) = weapon {
            [
                1.0,
                weapon_type_scalar(weapon.weapon_type)
                    .ok_or(PerceptionError::InvalidSource)?,
                normalize_positive(weapon.range, 80.0),
                normalize_positive(weapon.power, 100.0),
                normalize_positive(weapon.rate_of_fire, 5.0),
            ]
        } else {
            [0.0; OBSERVED_WEAPON_LEN]
        };
        weapon_section[start..start + OBSERVED_WEAPON_LEN]
            .copy_from_slice(&values);
    }
    Ok(())
}

/// Port of Unity's closest-first sort. The scratch owns a grow-once Vec;
/// no allocation occurs after the largest prior decision candidate set.
#[derive(Default)]
pub struct EntitySlotEncoder {
    sorted: Vec<ObservedShip>,
}

impl EntitySlotEncoder {
    pub fn new() -> Self { Self::default() }

    fn prepare(&mut self, ships: &[ObservedShip], position: Vec2) {
        self.sorted.clear();
        self.sorted.extend_from_slice(ships);
        self.sorted.sort_unstable_by(|left, right| {
            let left_distance = (left.position - position).length_squared();
            let right_distance = (right.position - position).length_squared();
            left_distance.total_cmp(&right_distance)
                .then_with(|| left.ship_type.cmp(&right.ship_type))
                .then_with(|| left.fleet_ship_id.unwrap_or(i64::MAX)
                    .cmp(&right.fleet_ship_id.unwrap_or(i64::MAX)))
                .then_with(|| left.entity_id.cmp(&right.entity_id))
        });
    }

    /// The caller provides the true same-side Level registry of allies;
    /// Unity excludes the observing ship and dead allies before sorting.
    pub fn write_allies(
        &mut self,
        row: &mut [f32],
        candidates: &[ObservedShip],
        own_ship_id: i64,
        own_side: i32,
        observer_position: Vec2,
        frame: PolicyFrame,
        side_starting_tsv: i32,
    ) -> Result<usize, PerceptionError> {
        validate_row(row)?;
        if !observer_position.is_finite() ||
            candidates.iter().any(|ship| !observed_ship_valid(ship)) {
            return Err(PerceptionError::InvalidSource);
        }
        self.prepare(candidates, observer_position);
        let mut used = 0;
        for &ship in &self.sorted {
            if !ship.is_alive || ship.entity_id == own_ship_id ||
                ship.side != own_side {
                continue;
            }
            if used == MAX_ALLIES { break; }
            let start = ALLIES_START + used * ALLY_LEN;
            write_ship(&mut row[start..start + ENEMY_LEN],
                ship, observer_position, frame, side_starting_tsv)?;
            let communication = ship.communication.unwrap_or([0.0; 4]);
            row[start + ENEMY_LEN..start + ALLY_LEN]
                .copy_from_slice(&communication);
            used += 1;
        }
        row[ALLIES_START + used * ALLY_LEN..ENEMIES_START].fill(0.0);
        Ok(used)
    }

    /// "visible_enemies" MUST be supplied from the faction-wide cache of
    /// ever-discovered live enemies. Do not infer it from current range.
    pub fn write_enemies(
        &mut self,
        row: &mut [f32],
        visible_enemies: &[ObservedShip],
        own_side: i32,
        observer_position: Vec2,
        frame: PolicyFrame,
        side_starting_tsv: i32,
    ) -> Result<usize, PerceptionError> {
        validate_row(row)?;
        if !observer_position.is_finite() ||
            visible_enemies.iter().any(|ship| !observed_ship_valid(ship)) {
            return Err(PerceptionError::InvalidSource);
        }
        self.prepare(visible_enemies, observer_position);
        let mut used = 0;
        for &ship in &self.sorted {
            if !ship.is_alive || ship.side == own_side {
                continue;
            }
            if used == MAX_ENEMIES { break; }
            let start = ENEMIES_START + used * ENEMY_LEN;
            write_ship(&mut row[start..start + ENEMY_LEN],
                ship, observer_position, frame, side_starting_tsv)?;
            used += 1;
        }
        row[ENEMIES_START + used * ENEMY_LEN..ENEMIES_START + MAX_ENEMIES * ENEMY_LEN]
            .fill(0.0);
        Ok(used)
    }
}

/// Parent-carrier slot is 41 real entity features if a living parent exists,
/// and precisely 41 source-defined zeros otherwise.
pub fn write_parent_carrier(
    row: &mut [f32],
    live_parent: Option<ObservedShip>,
    observer_position: Vec2,
    frame: PolicyFrame,
    side_starting_tsv: i32,
) -> Result<(), PerceptionError> {
    validate_row(row)?;
    let out = &mut row[PARENT_CARRIER_START..PARENT_CARRIER_START + PARENT_CARRIER_LEN];
    if let Some(parent) = live_parent {
        if !parent.is_alive { return Err(PerceptionError::InvalidSource); }
        write_ship(out, parent, observer_position, frame, side_starting_tsv)?;
    } else {
        out.fill(0.0);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::perception::{PARENT_CARRIER_START, CAPABILITY_START};

    fn ship(id: i64, side: i32, x: f32) -> ObservedShip {
        ObservedShip {
            entity_id: id, fleet_ship_id: Some(id), side,
            ship_type: 0, identity: id as f32 / 100.0,
            position: Vec2::new(x, 0.0),
            rotation_degrees: 0.0,
            health: 25, max_health: 100,
            speed: 10.0, current_speed: 10.0,
            linear_velocity: Vec2::new(1.0, 0.0),
            longest_side: 4.0, max_range: 60.0,
            firepower: 100.0, is_mobile: true, is_bomber: false,
            is_alive: true, mined_tsv: 0,
            weapons: [None; OBSERVED_WEAPON_SLOTS],
            communication: None,
        }
    }

    #[test]
    fn friendly_and_enemy_slots_preserve_nearest_order_and_missing_slot_zeroes() {
        let mut row = [f32::NAN; OBSERVATION_FLOATS];
        let mut writer = EntitySlotEncoder::new();
        let me = ship(1, 1, 0.0);
        let mut ally = ship(2, 1, 10.0);
        ally.weapons[1] = Some(ObservedWeapon {
            weapon_type: 0, range: 80.0, power: 100.0,
            rate_of_fire: 5.0,
        });
        ally.communication = Some([0.1, -0.2, 0.3, -0.4]);
        assert_eq!(writer.write_allies(
            &mut row, &[me, ally], 1, 1, me.position,
            PolicyFrame::new(0), 1000,
        ).unwrap(), 1);
        assert_eq!(row[ALLIES_START], 1.0);
        assert_eq!(row[ALLIES_START + 1], ally.identity);
        assert!((row[ALLIES_START + 2] - 0.2).abs() < 1e-6);
        assert_eq!(row[ALLIES_START + ENTITY_CORE_LEN + OBSERVED_WEAPON_LEN], 1.0);
        assert_eq!(row[ALLIES_START + ENEMY_LEN], 0.1);
        assert_eq!(row[ALLIES_START + ALLY_LEN], 0.0);
        assert_eq!(row[CAPABILITY_START].is_nan(), true);

        let far = ship(3, 2, 30.0);
        let near = ship(4, 2, -5.0);
        assert_eq!(writer.write_enemies(&mut row,
            &[far, near], 1, me.position, PolicyFrame::new(0), 1000,
        ).unwrap(), 2);
        assert_eq!(row[ENEMIES_START + 1], near.identity);
        assert_eq!(row[ENEMIES_START + ENEMY_LEN + 1], far.identity);
        assert_eq!(row[ENEMIES_START + 2 * ENEMY_LEN], 0.0);
        write_parent_carrier(&mut row, None, me.position,
            PolicyFrame::new(0), 1000).unwrap();
        assert_eq!(row[PARENT_CARRIER_START], 0.0);
    }
}
