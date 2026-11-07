//! Unity Command.MakeTargetingQueue ordering for native combat commands.
//!
//! Ranking is performed against the one live CombatWorld and the Level's
//! authored metadata; it never invents a training-only ship inventory.
//! This is distinct from targeting/shooting OutcomeIds and damage reservations.

use std::cmp::Ordering;
use std::collections::{HashMap, VecDeque};

use super::NativeCommandSelection;
use bees_hivemind::protocol::{StrategyIdentity, SHOOTING_STRATEGIES};
use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::{CombatWorld, GamePointOrder};
use bees_sim::geometry::Vec2;
use bees_sim::simulation::RandomStream;
use bees_sim::squad_movement::{SquadFormationMember, SquadFormationOptions};

use crate::squads::NativeSquadRoster;
use super::GameCommandError;

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
    fn valid(self) -> bool {
        self.ship_id > 0 && self.ship_type_letter < 24 &&
        self.firepower.is_finite() &&
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
    sort_native_target_list(
        world, roster, enemy_owner, squad_center, strategy, random,
        targets,
        |id| {
            let position = world.ship_body(id)
                .expect("validated living command target body")
                .motion.position;
            (position - squad_center).length_squared().sqrt()
        },
    )
}

/// Single source implementation of all 40 shooting-strategy rankings.
/// Command sorting measures ship centers; Bomb sorting supplies the
/// existing physics collider's closest-point distance instead.
fn sort_native_target_list(
    world: &CombatWorld,
    roster: &NativeSquadRoster,
    enemy_owner: SquadLifetime,
    squad_center: Vec2,
    strategy: StrategyIdentity,
    random: &mut RandomStream,
    targets: &mut [NativeEnemyTargetStats],
    distance_to_target: impl Fn(i64) -> f32,
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
        // The authored Level already supplied most ranking fields. Avoid
        // two CombatWorld ID lookups on every comparator call for strategies
        // that need only this existing per-target metadata.
        match strategy.id {
            3 => b.last_killed.cmp(&a.last_killed),
            4 => b.fleet_damage_done.cmp(&a.fleet_damage_done),
            5 | 6 => {
                let health_a = world.ship(a.ship_id)
                    .expect("preflight validated target identity").vitals.health();
                let health_b = world.ship(b.ship_id)
                    .expect("preflight validated target identity").vitals.health();
                if strategy.id == 5 {
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
                if strategy.id == 9 {
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
                if strategy.id == 15 {
                    tsv_b.cmp(&tsv_a)
                } else {
                    tsv_a.cmp(&tsv_b)
                }
            }
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

/// Bomb.MakeSortedTargetingList uses the same shooting strategies as
/// Command.MakeTargetingQueue, but Weapon.DistanceTo measures the exact
/// closest point on the enemy ship's authored Collider2D. Only the two
/// distance strategies need this one-time geometry projection.
pub fn sort_native_game_bomb_targets(
    world: &mut CombatWorld,
    roster: &NativeSquadRoster,
    enemy_owner: SquadLifetime,
    weapon_origin: Vec2,
    strategy: StrategyIdentity,
    random: &mut RandomStream,
    targets: &mut [NativeEnemyTargetStats],
) -> Result<(), GameCommandError> {
    if !weapon_origin.is_finite() {
        return Err(GameCommandError::InvalidCombatSnapshot);
    }
    if !matches!(strategy.id, 9 | 10) {
        return sort_native_command_targets(
            world, roster, enemy_owner, weapon_origin, strategy, random, targets,
        );
    }
    let mut distances = HashMap::with_capacity(targets.len());
    for target in targets.iter() {
        let point = world.ship_hull_closest_point(target.ship_id, weapon_origin)
            .ok_or(GameCommandError::InvalidCombatSnapshot)?;
        let distance = (point - weapon_origin).length_squared().sqrt();
        if !distance.is_finite() {
            return Err(GameCommandError::InvalidCombatSnapshot);
        }
        distances.insert(target.ship_id, distance);
    }
    sort_native_target_list(
        world, roster, enemy_owner, weapon_origin, strategy, random,
        targets, |id| distances[&id],
    )
}

/// Unity BombingRun.GetTarget and Charge.GetTargetShip: all living
/// opponents are eligible Bomb targets regardless of weapon range. Prefer the first ordered ship
/// whose outgoing damage is not fully reserved; otherwise select a
/// uniformly random ship from that same list. Bomb.SetTargetShip owns
/// the reservation in CombatWorld, not a second gameplay ledger.
/// Caller supplies live GetShips-ordered candidate metadata and an
/// authored, globally unique Bomb weapon ID.
pub fn select_native_game_bomb_target(
    world: &mut CombatWorld,
    roster: &NativeSquadRoster,
    bomber_id: i64,
    bomb_weapon_id: i64,
    bomb_power: i32,
    enemy_owner: SquadLifetime,
    strategy: StrategyIdentity,
    random: &mut RandomStream,
    candidates: &mut [NativeEnemyTargetStats],
) -> Result<Option<i64>, GameCommandError> {
    if candidates.is_empty() { return Ok(None); }
    let bomber = world.ship(bomber_id).ok_or(GameCommandError::InvalidSquad)?;
    if bomber.vitals.is_dead() || !matches!(bomber.ship_type, 0 | 10 | 19 | 22) {
        return Err(GameCommandError::InvalidSquad);
    }
    let origin = world.ship_body(bomber_id)
        .ok_or(GameCommandError::InvalidSquad)?.motion.position;
    sort_native_game_bomb_targets(
        world, roster, enemy_owner, origin, strategy, random, candidates,
    )?;
    let side = world.ship(bomber_id)
        .ok_or(GameCommandError::InvalidSquad)?.side;
    let mut chosen = None;
    for target in candidates.iter() {
        if world.target_damage_status(side, target.ship_id)?
            .can_select_target() {
            chosen = Some(target.ship_id);
            break;
        }
    }
    let chosen = chosen.unwrap_or_else(|| {
        let index = random.next_below(candidates.len() as u64) as usize;
        candidates[index].ship_id
    });
    world.assign_game_bomb_target(bomber_id, bomb_weapon_id, chosen, bomb_power)?;
    Ok(Some(chosen))
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
        Self::from_sorted_ids(sorted_enemy_ids.iter().copied())
    }

    /// Construct from an already-ranked iterator, avoiding a temporary
    /// ID vector when a command has just ranked enemy target records.
    pub fn from_sorted_ids(ids: impl IntoIterator<Item = i64>) -> Self {
        let original: VecDeque<_> = ids.into_iter().collect();
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

/// Shared Unity Ship.SetAndGetTargetEnemy approach for SwipeSquad and
/// InAndOut. A single source-owned queue and retained ship targets;
/// no duplicate target ranking or movement implementation.
#[derive(Default)]
pub(super) struct NativeTargetApproach {
    target_queue: Option<NativeCommandTargetQueue>,
    ranked_candidates: Vec<NativeEnemyTargetStats>,
}

impl NativeTargetApproach {
    /// Apply each member's current tracked target from the canonical world.
    /// False means Unity's "no more enemy ships to target" completion.
    pub(super) fn issue_tracked_approach(
        &mut self,
        world: &mut CombatWorld,
        roster: &NativeSquadRoster,
        random: &mut RandomStream,
        members: &[SquadFormationMember],
        formation: SquadFormationOptions,
        selection: &NativeCommandSelection,
        enemy_owner: SquadLifetime,
        squad_center: Vec2,
        candidates: &[NativeEnemyTargetStats],
    ) -> Result<bool, GameCommandError> {
        // Unity's MoveTowardsEnemiesTracked invokes
        // Ship.MoveToTrackedPoint immediately after each successful
        // SetAndGetTargetEnemy. A subsequent ship without a target ends
        // the command but does not undo preceding ships' move orders.
        // Preserve that order without a duplicate command-side batch.
        for &member in members {
            if !self.issue_one_tracked_approach(
                world, roster, random, member, formation,
                selection, enemy_owner, squad_center, candidates,
            )? {
                return Ok(false);
            }
        }
        Ok(true)
    }

    /// Aggressive issues one tracked ship order per Unity coroutine frame.
    /// Reuse exactly the FIFO, retained target identity and live target
    /// ranking used by Swipe and InAndOut, but do not issue a full formation.
    pub(super) fn issue_one_tracked_approach(
        &mut self,
        world: &mut CombatWorld,
        roster: &NativeSquadRoster,
        random: &mut RandomStream,
        member: SquadFormationMember,
        formation: SquadFormationOptions,
        selection: &NativeCommandSelection,
        enemy_owner: SquadLifetime,
        squad_center: Vec2,
        candidates: &[NativeEnemyTargetStats],
    ) -> Result<bool, GameCommandError> {
        let Some(order) = self.tracked_order_for_member(
            world, roster, random, member, formation,
            selection, enemy_owner, squad_center, candidates,
        )? else {
            return Ok(false);
        };
        world.move_ship_to_tracked_point(member.ship_id, order)?;
        Ok(true)
    }

    fn tracked_order_for_member(
        &mut self,
        world: &CombatWorld,
        roster: &NativeSquadRoster,
        random: &mut RandomStream,
        member: SquadFormationMember,
        formation: SquadFormationOptions,
        selection: &NativeCommandSelection,
        enemy_owner: SquadLifetime,
        squad_center: Vec2,
        candidates: &[NativeEnemyTargetStats],
    ) -> Result<Option<GamePointOrder>, GameCommandError> {
        if self.target_queue.is_none() {
            self.ranked_candidates.clear();
            self.ranked_candidates.extend_from_slice(candidates);
            sort_native_command_targets(
                world, roster, enemy_owner, squad_center,
                selection.effective_shooting, random, &mut self.ranked_candidates,
            )?;
            self.target_queue = Some(NativeCommandTargetQueue::from_sorted_ids(
                self.ranked_candidates.iter().map(|candidate| candidate.ship_id),
            ));
        }
        let queue = self.target_queue.as_mut().expect("created above");
        let ranked_candidates = &mut self.ranked_candidates;
        let target_id = queue.select_for_ship(
            member.ship_id, candidates.len(),
            |output: &mut VecDeque<i64>| {
                ranked_candidates.clear();
                ranked_candidates.extend_from_slice(candidates);
                sort_native_command_targets(
                    world, roster, enemy_owner, squad_center,
                    selection.effective_shooting, random, ranked_candidates,
                )?;
                output.extend(
                    ranked_candidates.iter().map(|candidate| candidate.ship_id),
                );
                Ok(())
            },
            // Unity Ship.HasTargetEnemyShipToFollow checks only
            // whether the retained target is non-null and still alive.
            // It does NOT revoke a ship target when that ship changes
            // squads. The freshly rebuilt queue is independently
            // restricted to the actual current enemy squad above.
            |id| world.ship(id).is_some_and(|ship|
                !ship.vitals.is_dead()
            ),
        )?;
        let Some(target_id) = target_id else { return Ok(None); };
        let destination = world.ship_body(target_id)
            .ok_or(GameCommandError::InvalidCombatSnapshot)?
            .motion.position;
        world.validate_ship_game_order(member.ship_id)?;
        Ok(Some(GamePointOrder {
            destination,
            ship_clearance: member.clearance,
            map_bounds: formation.map_bounds,
            has_obstacles: formation.has_obstacles,
            can_override_bounds: formation.can_override_bounds,
            newly_found_obstacle: false,
        }))
    }
}

/// The real enemy lifecycle is captured AT command activation. If the
/// Level reuses that numeric squad ID before the first timer callback,
/// the new squad is never silently adopted. No delayed, mutable binding.
pub(super) struct NativeAttackTarget {
    lifetime: SquadLifetime,
}

impl NativeAttackTarget {
    pub(super) fn new(lifetime: SquadLifetime) -> Self {
        Self { lifetime }
    }

    pub(super) fn squad_id(&self) -> i64 {
        self.lifetime.squad_id
    }

    pub(super) fn validate(
        &self, owner: SquadLifetime, enemy_owner: SquadLifetime,
    ) -> Result<(), GameCommandError> {
        if !enemy_owner.valid() || enemy_owner.level_id != owner.level_id ||
            enemy_owner.squad_id != self.lifetime.squad_id
        {
            return Err(GameCommandError::InvalidCombatSnapshot);
        }
        if self.lifetime != enemy_owner {
            return Err(GameCommandError::InvalidEnemy);
        }
        Ok(())
    }
}
