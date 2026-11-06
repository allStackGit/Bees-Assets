//! Unity Command.MakeTargetingQueue ordering for native combat commands.
//!
//! Ranking is performed against the one live CombatWorld and the Level's
//! authored metadata; it never invents a training-only ship inventory.
//! This is distinct from targeting/shooting OutcomeIds and damage reservations.

use std::cmp::Ordering;

use bees_hivemind::protocol::{StrategyIdentity, SHOOTING_STRATEGIES};
use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::CombatWorld;
use bees_sim::geometry::Vec2;
use bees_sim::simulation::RandomStream;

use crate::squads::NativeSquadRoster;
use super::GameCommandError;

/// The source fields that are not available through CombatWorld's physical
/// ship/vitals. The Level supplies them from current Ship and FleetShip data.
/// Input is in EnemySquad.GetShips order, before strategy sorting.
#[derive(Clone, Copy, Debug)]
pub struct NativeEnemyTargetStats {
    pub ship_id: i64,
    /// ConfigData.ShipTypeLetters A=0 through X=23 (not ShipTypes values).
    pub ship_type_letter: u8,
    pub position: Vec2,
    pub last_killed: i32,
    pub fleet_damage_done: i64,
    pub firepower: f32,
    pub max_range: i32,
    pub speed: f32,
}

impl NativeEnemyTargetStats {
    fn valid(self) -> bool {
        self.ship_id > 0 && self.ship_type_letter < 24 &&
        self.position.is_finite() && self.firepower.is_finite() &&
        self.firepower >= 0.0 && self.max_range >= 0 &&
        self.speed.is_finite() && self.speed >= 0.0
    }
}

/// Sort without copying the authoritative enemy roster. Position/Firepower,
/// LastKilled and FleetShip.DamageDone are authored Level values; Health and
/// Tsv come directly from the real physical world after its current step.
/// All 40 server-selected shooting strategies share this ordering function.
///
/// Unity List.Sort is not stable. Equal-ranked enemies have no contractual
/// order; native uses an unstable comparison sort likewise. Random uses the
/// world-local seeded stream, not Unity's process-global System.Random.
pub fn sort_native_command_targets(
    world: &CombatWorld,
    roster: &NativeSquadRoster,
    enemy_owner: SquadLifetime,
    squad_center: Vec2,
    strategy: StrategyIdentity,
    random: &mut RandomStream,
    targets: &mut [NativeEnemyTargetStats],
) -> Result<(), GameCommandError> {
    let index = usize::from(strategy.id).checked_sub(1)
        .ok_or(GameCommandError::InvalidCombatSnapshot)?;
    if SHOOTING_STRATEGIES.get(index).copied() != Some(strategy.name) ||
        !squad_center.is_finite()
    {
        return Err(GameCommandError::InvalidCombatSnapshot);
    }
    let enemy = roster.get(enemy_owner).ok_or(GameCommandError::InvalidEnemy)?;
    if targets.len() != enemy.members.len() || targets.is_empty() {
        return Err(GameCommandError::InvalidCombatSnapshot);
    }
    for (target, &ship_id) in targets.iter().zip(&enemy.members) {
        if !target.valid() || target.ship_id != ship_id ||
            !world.ship(ship_id).is_some_and(|ship|
                ship.side == enemy.side && !ship.vitals.is_dead()) ||
            !world.ship_body(ship_id).is_some_and(|body| body.active)
        {
            return Err(GameCommandError::InvalidCombatSnapshot);
        }
    }
    if strategy.id == 1 { return Ok(()); }
    if strategy.id == 2 {
        // Source Utilities.Shuffle is Fisher-Yates from the last index down.
        for index in (1..targets.len()).rev() {
            let swap = random.next_below((index + 1) as u64) as usize;
            targets.swap(index, swap);
        }
        return Ok(());
    }

    targets.sort_unstable_by(|a, b| {
        let ship_a = world.ship(a.ship_id)
            .expect("preflight validated target identity");
        let ship_b = world.ship(b.ship_id)
            .expect("preflight validated target identity");
        match strategy.id {
            3 => b.last_killed.cmp(&a.last_killed),
            4 => b.fleet_damage_done.cmp(&a.fleet_damage_done),
            5 => ship_b.vitals.health().cmp(&ship_a.vitals.health()),
            6 => ship_a.vitals.health().cmp(&ship_b.vitals.health()),
            7 => b.firepower.total_cmp(&a.firepower),
            8 => a.firepower.total_cmp(&b.firepower),
            9 | 10 => {
                let distance_a = (a.position - squad_center)
                    .length_squared().sqrt();
                let distance_b = (b.position - squad_center)
                    .length_squared().sqrt();
                if strategy.id == 9 {
                    distance_a.total_cmp(&distance_b)
                } else {
                    distance_b.total_cmp(&distance_a)
                }
            }
            11 => b.max_range.cmp(&a.max_range),
            12 => a.max_range.cmp(&b.max_range),
            13 => b.speed.total_cmp(&a.speed),
            14 => a.speed.total_cmp(&b.speed),
            15 => ship_b.vitals.tsv().cmp(&ship_a.vitals.tsv()),
            16 => ship_a.vitals.tsv().cmp(&ship_b.vitals.tsv()),
            17..=40 => {
                let preferred = strategy.id - 17;
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
