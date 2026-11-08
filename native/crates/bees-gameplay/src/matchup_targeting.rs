//! Unity MatchupStrategy.SortSquads on already-authorized live candidates.
//!
//! All physical state, HiveMind sighting membership, and ship statistics
//! belong to the owning native Level. The caller builds this small, ordered
//! scratch list only on a validated GetMatchupStrategy response, in the same
//! GetShipsVisibleToHiveMind / GetAllSquads order used by the source.
//! No independent visibility cache, targeting world or persistent squad index.

use std::collections::HashSet;

use bees_hivemind::protocol::{StrategyIdentity, TARGETING_STRATEGIES};
use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::{CombatWorld, CombatWorldError};
use bees_sim::ship_assets::ShipAssetCatalog;
use bees_sim::simulation::RandomStream;

use crate::squad_geometry::{
    live_squad_bounds_from_catalog, SquadGeometryError,
};
use crate::squads::NativeSquadRoster;

/// Current, genuinely observed aggregate values for one non-dead enemy squad.
/// All sums are source C# double accumulations, with Tsv and FleetShip fields
/// read from their real owners; missing authored data must be an error at
/// collection time, not silently treated as zero.
#[derive(Clone, Copy, Debug)]
pub struct NativeMatchupCandidate {
    pub owner: SquadLifetime,
    /// The largest Ship.LastKilled of members, or zero for an empty history.
    pub largest_last_killed: f64,
    pub fleet_damage_done: f64,
    pub health: f64,
    pub firepower: f64,
    /// Source uses the largest MaxRange, not the sum.
    pub max_range: i32,
    pub speed: f64,
    pub tsv: f64,
    /// Current Ship.InCombat (source timer state), not weapon range or sight.
    pub in_combat: bool,
    /// Unity Vector2.Distance(own Squad.GetPosition(), enemy.GetPosition()).
    pub distance_to_own_center: f32,
    /// Counts by ConfigData.ShipTypes, in source Type A..W correspondence.
    /// The original source Type X is not a server targeting strategy.
    pub type_counts: [u32; 23],
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MatchupSelectionError {
    InvalidStrategy,
    InvalidCandidate(SquadLifetime),
    DuplicateCandidate(SquadLifetime),
    InvalidPrioritizedSquad,
}

impl NativeMatchupCandidate {
    pub fn valid(&self) -> bool {
        self.owner.valid() &&
        self.largest_last_killed.is_finite() && self.largest_last_killed >= 0.0 &&
        // FleetShip.DamageDone is an unchecked C# int and can wrap negative.
        // Keep the source's signed ranking rather than banning that state.
        self.fleet_damage_done.is_finite() &&
        self.health.is_finite() && self.health >= 0.0 &&
        self.firepower.is_finite() && self.firepower >= 0.0 &&
        self.max_range >= 0 &&
        self.speed.is_finite() && self.speed >= 0.0 &&
        self.tsv.is_finite() &&
        self.distance_to_own_center.is_finite() &&
        self.distance_to_own_center >= 0.0
    }
}

/// Select exactly one source SquadLifetime, preserving the source's first
/// candidate on equal metrics (C# uses strict comparisons, not sorting).
/// Strategy IDs 1..40 are TARGETING_STRATEGIES, NOT SHOOTING_STRATEGIES.
///
/// `first_allied_target` must come from GameState.GetTargetedSquads(side)
/// in its own order, validated live and enemy-sided by the caller. Unity's
/// GangUp path may return that squad even if it is not in the visibility
/// candidate list. Without such a target it uses InCombat's preference.
/// If there are no visible squads, even GangUp returns None as Unity does.
pub fn select_matchup_squad(
    strategy: StrategyIdentity,
    candidates: &[NativeMatchupCandidate],
    first_allied_target: Option<SquadLifetime>,
    random: &mut RandomStream,
) -> Result<Option<SquadLifetime>, MatchupSelectionError> {
    let valid_name = strategy.id.checked_sub(1)
        .and_then(|idx| TARGETING_STRATEGIES.get(usize::from(idx)))
        .is_some_and(|&name| name == strategy.name);
    if !valid_name {
        return Err(MatchupSelectionError::InvalidStrategy);
    }
    let mut ids = HashSet::with_capacity(candidates.len());
    let mut candidate_level = None;
    for item in candidates {
        if !item.valid() {
            return Err(MatchupSelectionError::InvalidCandidate(item.owner));
        }
        if !ids.insert(item.owner) {
            return Err(MatchupSelectionError::DuplicateCandidate(item.owner));
        }
        if let Some(level_id) = candidate_level {
            if item.owner.level_id != level_id {
                return Err(MatchupSelectionError::InvalidCandidate(item.owner));
            }
        } else {
            candidate_level = Some(item.owner.level_id);
        }
    }
    if strategy.id == 15 && first_allied_target.is_some_and(|owner|
        !owner.valid() || candidate_level.is_some_and(|id| owner.level_id != id)
    ) {
        return Err(MatchupSelectionError::InvalidPrioritizedSquad);
    }
    let Some(first) = candidates.first() else { return Ok(None); };
    if strategy.id == 1 {
        let index = random.next_below(candidates.len() as u64) as usize;
        return Ok(Some(candidates[index].owner));
    }
    if strategy.id == 15 {
        if let Some(target) = first_allied_target {
            return Ok(Some(target));
        }
    }

    let mut chosen = first;
    for candidate in candidates.iter().skip(1) {
        let replace = match strategy.id {
            // Greater is better; source returns first on equality.
            2 => candidate.largest_last_killed > chosen.largest_last_killed,
            3 => candidate.fleet_damage_done > chosen.fleet_damage_done,
            4 => candidate.health < chosen.health,
            5 => candidate.health > chosen.health,
            6 => candidate.firepower > chosen.firepower,
            7 => candidate.firepower < chosen.firepower,
            8 => candidate.distance_to_own_center < chosen.distance_to_own_center,
            9 => candidate.distance_to_own_center > chosen.distance_to_own_center,
            10 => candidate.max_range > chosen.max_range,
            11 => candidate.max_range < chosen.max_range,
            12 => candidate.speed > chosen.speed,
            13 => candidate.speed < chosen.speed,
            14 | 15 => {
                // Source SelectInCombat: prioritize an in-combat squad,
                // and break an equal combat status using LOWER MaxRange.
                (candidate.in_combat && !chosen.in_combat) ||
                (candidate.in_combat == chosen.in_combat &&
                 candidate.max_range < chosen.max_range)
            }
            16 => candidate.tsv > chosen.tsv,
            17 => candidate.tsv < chosen.tsv,
            18..=40 => {
                let type_index = usize::from(strategy.id - 18);
                candidate.type_counts[type_index] > chosen.type_counts[type_index]
            }
            _ => return Err(MatchupSelectionError::InvalidStrategy),
        };
        if replace { chosen = candidate; }
    }
    Ok(Some(chosen.owner))
}


#[derive(Debug)]
pub enum LiveMatchupResolutionError {
    MissingOwner,
    StaleWorld,
    MissingAssetCatalog,
    InvalidAlliedTarget,
    MissingShip(i64),
    InvalidWeaponRank(i64),
    MissingCombatState(i64),
    SquadGeometry(SquadGeometryError),
    World(CombatWorldError),
    Selection(MatchupSelectionError),
}

impl From<SquadGeometryError> for LiveMatchupResolutionError {
    fn from(value: SquadGeometryError) -> Self { Self::SquadGeometry(value) }
}

impl From<CombatWorldError> for LiveMatchupResolutionError {
    fn from(value: CombatWorldError) -> Self { Self::World(value) }
}

/// Build a candidate ONLY from current physical and authored source data.
/// Query rank history and authored sprite bounds only when the selected
/// strategy uses them: Random or a health-only selection must not fail
/// because an unrelated ship lacks weapon-rank metadata. No missing field
/// is silently invented for a strategy that actually needs it.
fn live_candidate(
    world: &CombatWorld,
    roster: &NativeSquadRoster,
    catalog: &ShipAssetCatalog,
    target: SquadLifetime,
    own_center: Option<bees_sim::geometry::Vec2>,
    strategy_id: u8,
    needs_combat_state: bool,
    in_combat: &mut impl FnMut(i64) -> Option<bool>,
) -> Result<Option<NativeMatchupCandidate>, LiveMatchupResolutionError> {
    let squad = roster.get(target).ok_or(LiveMatchupResolutionError::MissingOwner)?;
    let mut candidate = NativeMatchupCandidate {
        owner: target,
        largest_last_killed: 0.0,
        fleet_damage_done: 0.0,
        health: 0.0,
        firepower: 0.0,
        max_range: 0,
        speed: 0.0,
        tsv: 0.0,
        in_combat: false,
        distance_to_own_center: 0.0,
        type_counts: [0; 23],
    };
    let mut living_count = 0u32;
    for &id in &squad.members {
        let ship = world.ship(id)
            .ok_or(LiveMatchupResolutionError::MissingShip(id))?;
        if ship.vitals.is_dead() { continue; }
        living_count += 1;
        // Both physical totals are cheap; the source's weapon ranking
        // history is optional for policies with no automatic weapons.
        candidate.health += f64::from(ship.vitals.health());
        candidate.tsv += f64::from(ship.vitals.tsv());
        let needs_rank = matches!(strategy_id,
            2 | 3 | 6 | 7 | 10 | 11 | 12 | 13 | 18..=40
        ) || needs_combat_state;
        if needs_rank {
            let ranking = world.weapon_target_rank_stats(id)
                .map_err(LiveMatchupResolutionError::World)?;
            if !ranking.valid() {
                return Err(LiveMatchupResolutionError::InvalidWeaponRank(id));
            }
            candidate.largest_last_killed =
                candidate.largest_last_killed.max(f64::from(ranking.last_killed));
            candidate.fleet_damage_done += ranking.fleet_damage_done as f64;
            candidate.firepower += f64::from(ranking.firepower);
            candidate.max_range = candidate.max_range.max(ranking.max_range);
            candidate.speed += f64::from(ranking.speed);
            if ranking.ship_type_letter < 23 {
                candidate.type_counts[usize::from(ranking.ship_type_letter)] += 1;
            }
        }
        if needs_combat_state {
            candidate.in_combat |= in_combat(id)
                .ok_or(LiveMatchupResolutionError::MissingCombatState(id))?;
        }
    }
    if living_count == 0 {
        return Ok(None);
    }
    if let Some(own_center) = own_center {
        let center = live_squad_bounds_from_catalog(world, squad, catalog)?.center;
        let dx = center.x - own_center.x;
        let dy = center.y - own_center.y;
        candidate.distance_to_own_center = (dx * dx + dy * dy).sqrt();
    }
    if !candidate.valid() {
        return Err(LiveMatchupResolutionError::InvalidWeaponRank(
            squad.members[0],
        ));
    }
    Ok(Some(candidate))
}

/// One complete, source-grounded matchup choice for the live native world.
/// Called after a GetMatchupStrategy response; never on the physics hot path.
/// HiveMind discovery is persistent, so opponents seen earlier remain valid
/// until actual ship removal. Distinct Squad lifetimes are deduplicated in
/// first-sighting order, as source MatchupStrategy.BuildVisibleSquadQueue.
///
/// The caller supplies the actual player-visibility rule and the first
/// active allied target for GangUp. Neither is inferred from ship faction.
/// For InCombat, and GangUp without an allied target, `in_combat` MUST
/// report Unity Ship.InCombat's current authored timer state. If that state
/// has not been imported, this returns MissingCombatState, never a guessed
/// target or fabricated learning credit.
///
/// Input `scratch` belongs to the Level's reusable activation buffer,
/// not a second squad world; all records are rebuilt on this rare server
/// reply and only the selected live SquadLifetime is returned.
pub fn select_live_matchup_squad(
    world: &CombatWorld,
    roster: &NativeSquadRoster,
    catalog: &ShipAssetCatalog,
    owner: SquadLifetime,
    strategy: StrategyIdentity,
    player_sees_all_enemies: bool,
    first_allied_target: Option<SquadLifetime>,
    mut in_combat: impl FnMut(i64) -> Option<bool>,
    random: &mut RandomStream,
    scratch: &mut Vec<NativeMatchupCandidate>,
) -> Result<Option<SquadLifetime>, LiveMatchupResolutionError> {
    scratch.clear();
    let acting = roster.get(owner).ok_or(LiveMatchupResolutionError::MissingOwner)?;
    let own_center = if matches!(strategy.id, 8 | 9) {
        Some(live_squad_bounds_from_catalog(world, acting, catalog)?.center)
    } else {
        None
    };
    if let Some(target) = first_allied_target.filter(|_| strategy.id == 15) {
        let valid = roster.get(target).is_some_and(|enemy|
            target.level_id == owner.level_id &&
            enemy.side != acting.side &&
            enemy.members.iter().any(|&id|
                world.ship(id).is_some_and(|ship| !ship.vitals.is_dead())
            )
        );
        if !valid {
            return Err(LiveMatchupResolutionError::InvalidAlliedTarget);
        }
    }
    // Source GangUp selects a previously targeted, live enemy directly
    // after confirming there is at least one visible enemy squad.
    let needs_combat = strategy.id == 14 ||
        (strategy.id == 15 && first_allied_target.is_none());
    let mut seen = HashSet::new();
    let mut append = |target: SquadLifetime,
                      scratch: &mut Vec<NativeMatchupCandidate>|
        -> Result<(), LiveMatchupResolutionError> {
        if target == owner || !seen.insert(target) { return Ok(()); }
        let Some(squad) = roster.get(target) else { return Ok(()); };
        if target.level_id != owner.level_id || squad.side == acting.side {
            return Ok(());
        }
        if let Some(candidate) = live_candidate(
            world, roster, catalog, target, own_center,
            strategy.id, needs_combat, &mut in_combat,
        )? {
            scratch.push(candidate);
        }
        Ok(())
    };
    if player_sees_all_enemies {
        for &target in roster.source_order() {
            append(target, scratch)?;
        }
    } else {
        for &ship_id in world.discovery().ship_ids_in_sighting_order(acting.side) {
            if let Some(target) = roster.owning_squad(ship_id) {
                append(target, scratch)?;
            }
        }
    }
    select_matchup_squad(strategy, scratch, first_allied_target, random)
        .map_err(LiveMatchupResolutionError::Selection)
}
