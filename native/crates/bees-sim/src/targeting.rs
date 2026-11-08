//! Shared Weapon/Command shooting-strategy ranking. All40 source strategies
//! use the same comparator; the caller supplies actual rank data and the
//! appropriate center- or collider-distance function. No roster is copied.

use std::cmp::Ordering;
use crate::{combat_world::CombatWorld, simulation::RandomStream};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TargetRankingError { InvalidStrategy, InvalidTarget }

/// The source fields that are not available through CombatWorld's physical
/// ship/vitals. The Level supplies them from current Ship and FleetShip data.
/// Position is read directly from the current physics body; it must not
/// drift into an independently cached metadata view. Input is in
/// EnemySquad.GetShips order, before strategy sorting.
#[derive(Clone, Copy, Debug)]
pub struct NativeEnemyTargetStats {
    pub ship_id: i64,
    /// ConfigData.ShipTypeLetters A=0 through X=23 (not ShipTypes values).
    pub ship_type_letter: u8,
    pub last_killed: i32,
    pub fleet_damage_done: i64,
    pub firepower: f32,
    pub max_range: i32,
    pub speed: f32,
}

impl NativeEnemyTargetStats {
    pub fn valid(self) -> bool {
        self.ship_id > 0 && self.ship_type_letter < 24 &&
        self.firepower.is_finite() &&
        self.firepower >= 0.0 && self.max_range >= 0 &&
        self.speed.is_finite() && self.speed >= 0.0
    }
}

pub fn sort_shooting_targets(
    world: &CombatWorld, strategy_id: u8, random: &mut RandomStream,
    targets: &mut [NativeEnemyTargetStats], distance_to_target: impl Fn(i64) -> f32,
) -> Result<(), TargetRankingError> {
    if !(1..=40).contains(&strategy_id) { return Err(TargetRankingError::InvalidStrategy); }
    if targets.iter().any(|target| !target.valid() || world.ship(target.ship_id).is_none()) {
        return Err(TargetRankingError::InvalidTarget);
    }
    if strategy_id == 1 { return Ok(()); }
    if strategy_id == 2 {
        // Source Utilities.Shuffle is Fisher-Yates from the last index down.
        for index in (1..targets.len()).rev() {
            let swap = random.next_below((index + 1) as u64) as usize;
            targets.swap(index, swap);
        }
        return Ok(());
    }

    targets.sort_unstable_by(|a, b| {
        // The authored Level already supplied most ranking fields. Avoid
        // two CombatWorld ID lookups on every comparator call for strategies
        // that need only this existing per-target metadata.
        match strategy_id {
            3 => b.last_killed.cmp(&a.last_killed),
            4 => b.fleet_damage_done.cmp(&a.fleet_damage_done),
            5 | 6 => {
                let health_a = world.ship(a.ship_id)
                    .expect("preflight validated target identity").vitals.health();
                let health_b = world.ship(b.ship_id)
                    .expect("preflight validated target identity").vitals.health();
                if strategy_id == 5 {
                    health_b.cmp(&health_a)
                } else {
                    health_a.cmp(&health_b)
                }
            }
            // C# float.CompareTo treats -0.0 and +0.0 as equal.
            // Preflight has already excluded NaN and infinities.
            7 => b.firepower.partial_cmp(&a.firepower)
                .expect("validated finite firepower"),
            8 => a.firepower.partial_cmp(&b.firepower)
                .expect("validated finite firepower"),
            9 | 10 => {
                let distance_a = distance_to_target(a.ship_id);
                let distance_b = distance_to_target(b.ship_id);
                if strategy_id == 9 {
                    distance_a.total_cmp(&distance_b)
                } else {
                    distance_b.total_cmp(&distance_a)
                }
            }
            11 => b.max_range.cmp(&a.max_range),
            12 => a.max_range.cmp(&b.max_range),
            13 => b.speed.partial_cmp(&a.speed)
                .expect("validated finite speed"),
            14 => a.speed.partial_cmp(&b.speed)
                .expect("validated finite speed"),
            15 | 16 => {
                let tsv_a = world.ship(a.ship_id)
                    .expect("preflight validated target identity").vitals.tsv();
                let tsv_b = world.ship(b.ship_id)
                    .expect("preflight validated target identity").vitals.tsv();
                if strategy_id == 15 {
                    tsv_b.cmp(&tsv_a)
                } else {
                    tsv_a.cmp(&tsv_b)
                }
            }
            17..=40 => {
                let preferred = strategy_id - 17;
                match (
                    a.ship_type_letter == preferred,
                    b.ship_type_letter == preferred,
                ) {
                    (true, false) => Ordering::Less,
                    (false, true) => Ordering::Greater,
                    _ => Ordering::Equal,
                }
            }
            _ => Ordering::Equal, // preflight excludes any other ID
        }
    });
    Ok(())
}
