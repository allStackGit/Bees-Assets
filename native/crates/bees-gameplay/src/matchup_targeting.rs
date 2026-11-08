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
use bees_sim::simulation::RandomStream;

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
        self.fleet_damage_done.is_finite() && self.fleet_damage_done >= 0.0 &&
        self.health.is_finite() && self.health >= 0.0 &&
        self.firepower.is_finite() && self.firepower >= 0.0 &&
        self.max_range >= 0 &&
        self.speed.is_finite() && self.speed >= 0.0 &&
        self.tsv.is_finite() && self.tsv >= 0.0 &&
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
    if first_allied_target.is_some_and(|owner|
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
