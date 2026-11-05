//! Frozen v26 five-slot SELF weapon perception.
//!
//! Source: RlCombatPerception.AddWeaponSlots, AddSelfWeaponObservation
//! and GetProjectileSpeedObservation. These are distinct from the much
//! smaller five-float weapon summaries used for observed allies/enemies.
//! No weapon is synthesized for a missing mount or absent prefab.

use crate::control::PolicyFrame;
use crate::geometry::Vec2;
use crate::perception::{
    PerceptionError, SELF_WEAPONS_START, SELF_WEAPON_LEN, WEAPON_SLOTS,
    normalize_positive, weapon_type_scalar, policy_heading,
};
use crate::policy::OBSERVATION_FLOATS;

#[derive(Clone, Copy, Debug)]
pub struct TurretObservation {
    pub rotation_degrees: f32,
    pub ready_to_fire: bool,
    pub aligned_with_target: bool,
}

#[derive(Clone, Copy, Debug)]
pub struct SelfWeapon {
    pub weapon_type: u8,
    /// Actual mount position, not ship center.
    pub position: Vec2,
    pub range: f32,
    pub power: f32,
    pub rate_of_fire: f32,
    pub rotation_rate: f32,
    pub projectile_value: f32,
    /// The Projectile.Speed from the authored prefab associated with the
    /// weapon's ProjectileType. None matches missing prefab/Stage/Projectile.
    pub projectile_speed: Option<f64>,
    /// Only physical Turret weapons set this; other weapon types write
    /// HasTargetShip into the fourth turret-status channel instead.
    pub turret: Option<TurretObservation>,
    pub has_target_ship: bool,
}

fn valid(weapon: &SelfWeapon) -> bool {
    weapon_type_scalar(weapon.weapon_type).is_some() &&
        weapon.position.is_finite() &&
        [weapon.range, weapon.power, weapon.rate_of_fire,
            weapon.rotation_rate, weapon.projectile_value]
            .iter().all(|value| value.is_finite()) &&
        weapon.projectile_speed.map_or(true, |speed| speed.is_finite()) &&
        weapon.turret.map_or(true, |turret| turret.rotation_degrees.is_finite())
}

/// Each index is an actual Unity ship.Weapons slot. Missing entries (even
/// inside the populated range) emit fifteen zeros. Preserves slot identity.
pub fn write_self_weapons(
    row: &mut [f32],
    weapons: &[Option<SelfWeapon>; WEAPON_SLOTS],
    ship_position: Vec2,
    ship_longest_side: f32,
    frame: PolicyFrame,
) -> Result<(), PerceptionError> {
    if row.len() != OBSERVATION_FLOATS {
        return Err(PerceptionError::WrongRowSize { actual: row.len() });
    }
    if !ship_position.is_finite() || !ship_longest_side.is_finite() ||
        weapons.iter().flatten().any(|weapon| !valid(weapon)) {
        return Err(PerceptionError::InvalidSource);
    }
    let scale = ship_longest_side.max(1.0);
    for (slot, weapon) in weapons.iter().enumerate() {
        let start = SELF_WEAPONS_START + slot * SELF_WEAPON_LEN;
        let out = &mut row[start..start + SELF_WEAPON_LEN];
        let Some(weapon) = weapon else {
            out.fill(0.0);
            continue;
        };
        let relative = frame.world_to_policy(weapon.position - ship_position);
        let projectile_speed = weapon.projectile_speed.map_or(0.0,
            |speed| ((speed as f32) / 200.0).clamp(0.0, 1.0));
        let (turret_flag, heading_x, heading_y, ready, aligned) =
            if let Some(turret) = weapon.turret {
                let direction = policy_heading(turret.rotation_degrees, frame);
                (1.0, direction.x, direction.y,
                    if turret.ready_to_fire { 1.0 } else { 0.0 },
                    if turret.aligned_with_target { 1.0 } else { 0.0 })
            } else {
                (0.0, 0.0, 0.0,
                    if weapon.has_target_ship { 1.0 } else { 0.0 }, 0.0)
            };
        let values: [f32; SELF_WEAPON_LEN] = [
            1.0,
            weapon_type_scalar(weapon.weapon_type)
                .ok_or(PerceptionError::InvalidSource)?,
            (relative.x / scale).clamp(-1.0, 1.0),
            (relative.y / scale).clamp(-1.0, 1.0),
            normalize_positive(weapon.range, 80.0),
            normalize_positive(weapon.power, 100.0),
            normalize_positive(weapon.rate_of_fire, 5.0),
            normalize_positive(weapon.rotation_rate, 240.0),
            normalize_positive(weapon.projectile_value, 2.0),
            projectile_speed,
            turret_flag, heading_x, heading_y, ready, aligned,
        ];
        if values.iter().any(|value| !value.is_finite()) {
            return Err(PerceptionError::InvalidSource);
        }
        out.copy_from_slice(&values);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::perception::MINING_START;

    #[test]
    fn turret_and_non_turret_mounts_keep_distinct_status_channels() {
        let mut row = [f32::NAN; OBSERVATION_FLOATS];
        let weapon = SelfWeapon {
            weapon_type: 0, position: Vec2::new(3.0, 0.0),
            range: 80.0, power: 100.0, rate_of_fire: 5.0,
            rotation_rate: 240.0, projectile_value: 2.0,
            projectile_speed: Some(100.0),
            turret: Some(TurretObservation {
                rotation_degrees: 0.0,
                ready_to_fire: true, aligned_with_target: false,
            }), has_target_ship: false,
        };
        let mut weapons = [None; WEAPON_SLOTS];
        weapons[0] = Some(weapon);
        weapons[2] = Some(SelfWeapon {
            turret: None, has_target_ship: true, ..weapon
        });
        write_self_weapons(&mut row, &weapons,
            Vec2::ZERO, 2.0, PolicyFrame::new(0)).unwrap();
        let first = SELF_WEAPONS_START;
        assert_eq!(row[first], 1.0);
        assert_eq!(row[first + 2], 1.0); // relative x clamped by owner length
        assert_eq!(row[first + 9], 0.5);
        assert_eq!(row[first + 10], 1.0);
        assert_eq!(row[first + 12], 1.0);
        assert_eq!(row[first + 13], 1.0);
        assert_eq!(row[first + 14], 0.0);
        assert!(row[first + SELF_WEAPON_LEN..first + 2 * SELF_WEAPON_LEN]
            .iter().all(|value| *value == 0.0));
        let non_turret = first + 2 * SELF_WEAPON_LEN;
        assert_eq!(row[non_turret + 10], 0.0);
        assert_eq!(row[non_turret + 13], 1.0);
        assert!(row[MINING_START..].iter().all(|value| value.is_nan()));
    }
}
