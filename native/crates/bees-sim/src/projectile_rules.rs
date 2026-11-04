//! Gameplay projectile behavior shared by clients and headless workers.
//!
//! Ported directly from PowerShot, LaserBeam, SplitterShot, Rocket and
//! RocketExplosion. This layer returns effects to the owning gameplay world;
//! it does not create an alternate trainer-specific damage implementation.

use std::collections::HashSet;

use crate::damage::can_explosion_damage;
use crate::geometry::Vec2;

/// Penetration budget for a Flagship PowerShot or a continuous LaserBeam.
/// The source records the hit target *before* mutating the remaining power.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PiercingType {
    PowerShot,
    LaserBeam,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PiercingContact {
    /// Projectile.ShipCollision stores the pre-contact Power for damage
    /// application, even when ContactTarget reduces the remaining budget.
    pub damage_requested: i32,
    pub remaining_power: i32,
    pub ends_projectile: bool,
}

pub struct PiercingShot {
    kind: PiercingType,
    remaining_power: i32,
    ships_hit: HashSet<i64>,
}

impl PiercingShot {
    pub fn new(kind: PiercingType, power: i32) -> Self {
        Self { kind, remaining_power: power, ships_hit: HashSet::new() }
    }

    pub fn remaining_power(&self) -> i32 {
        self.remaining_power
    }

    /// Returns None if this ship was already contacted by this projectile.
    /// The caller is responsible for invoking the full gameplay damage/TSV
    /// machinery exactly once when a new contact is accepted.
    pub fn contact_ship(&mut self, ship_id: i64, target_health: i32) -> Option<PiercingContact> {
        if !self.ships_hit.insert(ship_id) {
            return None;
        }
        let requested = self.remaining_power;
        let threshold = match self.kind {
            PiercingType::PowerShot => target_health,
            PiercingType::LaserBeam => target_health / 2,
        };
        let power_loss = threshold.clamp(0, self.remaining_power.max(0));
        let ends_projectile = self.remaining_power <= threshold;
        if !ends_projectile {
            self.remaining_power -= power_loss;
        }
        Some(PiercingContact {
            damage_requested: requested,
            remaining_power: self.remaining_power,
            ends_projectile,
        })
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SplitFragment {
    pub angle_radians: f32,
    pub power: i32,
    /// The initially contacted ship must be ignored by each child shot.
    pub ignore_ship_id: Option<i64>,
}

/// SplitterShot.Split: spawn four offset directions first (±30°, ±45°);
/// further authored SplitCount values reuse the initial shot heading.
/// Only split when DistanceFromStart <= Range - 5.
/// Child shots have the weapon's full range and power / 1.5, and inherit
/// the parent shot attribution in ProjectileMotion.
pub fn split_fragments(
    count: usize,
    parent_angle_radians: f32,
    distance_from_start: f32,
    parent_range: f32,
    weapon_power: i32,
    contacted_ship_id: Option<i64>,
) -> impl Iterator<Item = SplitFragment> {
    const OFFSETS_DEGREES: [f32; 4] = [30.0, -30.0, 45.0, -45.0];
    let enabled = distance_from_start <= parent_range - 5.0;
    (0..if enabled { count } else { 0 }).map(move |index| {
        let offset = OFFSETS_DEGREES.get(index).copied().unwrap_or(0.0);
        SplitFragment {
            angle_radians: (parent_angle_radians.to_degrees() + offset).to_radians(),
            power: (weapon_power as f32 / 1.5) as i32,
            ignore_ship_id: contacted_ship_id,
        }
    })
}

/// Rocket.IncreaseSpeed is called by a repeating 0.1-second ScaledTimer.
/// This is NOT a velocity update every simulation tick.
pub fn rocket_accelerated_velocity(current: Vec2, max_speed: f32) -> Vec2 {
    let accelerated = current * 1.5;
    let magnitude_squared = accelerated.length_squared();
    if max_speed <= 0.0 {
        return Vec2::ZERO;
    }
    if magnitude_squared > max_speed * max_speed {
        accelerated.normalized_or_zero() * max_speed
    } else {
        accelerated
    }
}

/// Tracks distinct ships/obstacles in the same explosion. A rocket explosion
/// does not die on impact and may affect several targets on the same tick.
pub struct ExplosionContacts {
    pub harmless: bool,
    ships_hit: HashSet<i64>,
    obstacles_hit: HashSet<i64>,
}

impl ExplosionContacts {
    pub fn new() -> Self {
        Self {
            harmless: false,
            ships_hit: HashSet::new(),
            obstacles_hit: HashSet::new(),
        }
    }

    pub fn accept_ship(
        &mut self,
        ship_id: i64,
        shooter_side: i32,
        shooter_is_fire_barge: bool,
        target_side: i32,
        is_fire_tank_explosion: bool,
        target_is_dead: bool,
    ) -> bool {
        if !can_explosion_damage(
            shooter_side,
            shooter_is_fire_barge,
            target_side,
            is_fire_tank_explosion,
            target_is_dead,
            self.harmless,
            self.ships_hit.contains(&ship_id),
        ) {
            return false;
        }
        self.ships_hit.insert(ship_id);
        true
    }

    /// Map borders and dead obstacles never enter the hit set; all others
    /// are contacted once, including static and collision asteroids.
    pub fn accept_obstacle(
        &mut self,
        obstacle_id: i64,
        obstacle_is_dead: bool,
        obstacle_is_map_border: bool,
    ) -> bool {
        if obstacle_is_dead || obstacle_is_map_border {
            return false;
        }
        self.obstacles_hit.insert(obstacle_id)
    }

    pub fn clear_for_reuse(&mut self) {
        self.harmless = false;
        self.ships_hit.clear();
        self.obstacles_hit.clear();
    }
}

impl Default for ExplosionContacts {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn flagship_power_shot_retains_damage_budget_after_piercing() {
        let mut shot = PiercingShot::new(PiercingType::PowerShot, 10);
        assert_eq!(shot.contact_ship(1, 3), Some(PiercingContact {
            damage_requested: 10, remaining_power: 7, ends_projectile: false,
        }));
        assert_eq!(shot.contact_ship(1, 3), None);
        assert_eq!(shot.contact_ship(2, 15), Some(PiercingContact {
            damage_requested: 7, remaining_power: 7, ends_projectile: true,
        }));
    }

    #[test]
    fn beam_uses_half_target_health_for_budget_consumption() {
        let mut beam = PiercingShot::new(PiercingType::LaserBeam, 10);
        assert_eq!(beam.contact_ship(1, 9), Some(PiercingContact {
            damage_requested: 10, remaining_power: 6, ends_projectile: false,
        }));
        assert_eq!(beam.contact_ship(2, 12), Some(PiercingContact {
            damage_requested: 6, remaining_power: 6, ends_projectile: true,
        }));
    }

    #[test]
    fn splitter_child_angles_power_and_ignored_target_are_preserved() {
        let children: Vec<_> = split_fragments(4, 0.0, 2.0, 20.0, 15, Some(42)).collect();
        assert_eq!(children.len(), 4);
        assert_eq!(children.iter().map(|child| child.power).collect::<Vec<_>>(), [10; 4]);
        assert!(children.iter().all(|child| child.ignore_ship_id == Some(42)));
        assert!((children[0].angle_radians.to_degrees() - 30.0).abs() < 0.00001);
        assert!((children[1].angle_radians.to_degrees() + 30.0).abs() < 0.00001);
        assert!(split_fragments(4, 0.0, 16.0, 20.0, 15, None).next().is_none());
    }

    #[test]
    fn explosions_hit_distinct_targets_without_dying_or_repeating() {
        let mut contacts = ExplosionContacts::new();
        assert!(contacts.accept_ship(1, 1, false, 2, false, false));
        assert!(contacts.accept_ship(2, 1, false, 2, false, false));
        assert!(!contacts.accept_ship(1, 1, false, 2, false, false));
        assert!(!contacts.accept_obstacle(3, true, false));
        assert!(!contacts.accept_obstacle(3, false, true));
        assert!(contacts.accept_obstacle(3, false, false));
        assert!(!contacts.accept_obstacle(3, false, false));
    }
}
