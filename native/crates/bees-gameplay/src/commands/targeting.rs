//! Unity Command.MakeTargetingQueue ordering for native combat commands.
//!
//! Ranking is performed against the one live CombatWorld and the Level's
//! authored metadata; it never invents a training-only ship inventory.
//! This is distinct from targeting/shooting OutcomeIds and damage reservations.

use std::cmp::Ordering;
use std::collections::{HashMap, VecDeque};

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

/// Unity Command.OriginalQueue/TargetingQueue and each ship's retained
/// TargetEnemyShipToFollow. Only IDs are cached; positions, vitals and
/// target eligibility always come from the live gameplay world.
#[derive(Default)]
pub struct NativeCommandTargetQueue {
    original: VecDeque<i64>,
    pending: VecDeque<i64>,
    retained_by_ship: HashMap<i64, i64>,
}

impl NativeCommandTargetQueue {
    /// Initialize from MakeTargetingQueue's exact first order. The caller
    /// supplies IDs from sort_native_command_targets, not numeric ID order.
    pub fn new(sorted_enemy_ids: &[i64]) -> Self {
        let original: VecDeque<_> = sorted_enemy_ids.iter().copied().collect();
        Self {
            pending: original.clone(),
            original,
            retained_by_ship: HashMap::new(),
        }
    }

    pub fn clear(&mut self) {
        self.original.clear();
        self.pending.clear();
        self.retained_by_ship.clear();
    }

    /// One Ship.SetAndGetTargetEnemy call. If the ship's prior target still
    /// lives, it remains locked. Otherwise commands consume a shared FIFO,
    /// rebuilding it from the current authored order only when exhausted.
    /// Unity rebuilds OriginalQueue on a dead dequeue but leaves the current
    /// TargetingQueue intact; preserve that distinction. Rebuild is lazy:
    /// Random consumes no RNG when a live ship already retains its target
    /// or the queue has further valid entries.
    pub fn select_for_ship(
        &mut self,
        ship_id: i64,
        live_enemy_count: usize,
        mut rebuild_original: impl FnMut(&mut VecDeque<i64>)
            -> Result<(), GameCommandError>,
        mut is_live_enemy: impl FnMut(i64) -> bool,
    ) -> Result<Option<i64>, GameCommandError> {
        if let Some(&retained) = self.retained_by_ship.get(&ship_id) {
            if is_live_enemy(retained) {
                return Ok(Some(retained));
            }
            self.retained_by_ship.remove(&ship_id);
        }
        // Unity uses max(EnemySquad.GetShips().Count, 10). Never spin
        // indefinitely when all candidates have just died.
        for _ in 0..live_enemy_count.max(10) {
            if self.pending.is_empty() {
                self.original.clear();
                rebuild_original(&mut self.original)?;
                self.pending.extend(self.original.iter().copied());
                if self.pending.is_empty() {
                    return Ok(None);
                }
            }
            let candidate = self.pending.pop_front()
                .expect("nonempty after refill");
            if is_live_enemy(candidate) {
                self.retained_by_ship.insert(ship_id, candidate);
                return Ok(Some(candidate));
            }
            // C# RebuildOriginalTargetingQueue, not RebuildTargetingQueues.
            self.original.clear();
            rebuild_original(&mut self.original)?;
        }
        Ok(None)
    }
}
