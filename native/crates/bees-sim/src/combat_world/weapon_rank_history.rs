//! Actual source ranking history. DamageDone is TSV loss, not hit power.
//! Fleet-owner aliases are exported real Ship/FleetShip reference relationships;
//! there is no inferred fleet ID or independently credited training ledger.

use super::*;
use crate::targeting::NativeEnemyTargetStats;

#[derive(Clone, Copy, Debug)]
pub struct NativeWeaponRankSource {
    pub stats: NativeEnemyTargetStats,
    /// Existing source ship representing the shared FleetShip object at import.
    pub fleet_owner_ship_id: i64,
}

#[derive(Default)]
pub(super) struct WeaponRankHistory {
    pub(super) frame: Option<i32>,
    pub(super) ships: HashMap<i64, NativeWeaponRankSource>,
    fleet_damage: HashMap<i64, i32>,
    killers: HashMap<i64, i64>,
    pub(super) kill_revisions: HashMap<i64, u64>,
    consumed_events: usize,
}

impl CombatWorld {
    /// Enable ordinary game targeting and its source ranking history.
    /// Policy-bound mounts retain their own explicit action controller.
    pub fn enable_automatic_weapon_targeting(&mut self, source_frame: i32) -> Result<(), CombatWorldError> {
        if self.weapon_rank_history.frame.is_some() { return Err(CombatWorldError::InvalidAutomaticTargeting); }
        self.weapon_rank_history.frame = Some(source_frame);
        Ok(())
    }

    pub fn configure_weapon_rank_source(&mut self, source: NativeWeaponRankSource) -> Result<(), CombatWorldError> {
        let damage = i32::try_from(source.stats.fleet_damage_done)
            .map_err(|_| CombatWorldError::InvalidAutomaticTargeting)?;
        if !source.stats.valid() || source.fleet_owner_ship_id <= 0 || self.ship(source.stats.ship_id).is_none() ||
            self.ship(source.fleet_owner_ship_id).is_none() ||
            (source.fleet_owner_ship_id != source.stats.ship_id && !self.weapon_rank_history.ships.contains_key(&source.fleet_owner_ship_id)) ||
            self.weapon_rank_history.ships.contains_key(&source.stats.ship_id) {
            return Err(CombatWorldError::InvalidAutomaticTargeting);
        }
        let history = &mut self.weapon_rank_history;
        if history.fleet_damage.get(&source.fleet_owner_ship_id)
            .is_some_and(|&value| value != damage) {
            return Err(CombatWorldError::InvalidAutomaticTargeting);
        }
        history.fleet_damage.insert(source.fleet_owner_ship_id, damage);
        history.ships.insert(source.stats.ship_id, source);
        Ok(())
    }

    pub fn weapon_target_rank_stats(&self, ship_id: i64) -> Result<NativeEnemyTargetStats, CombatWorldError> {
        let source = self.weapon_rank_history.ships.get(&ship_id)
            .ok_or(CombatWorldError::MissingAutomaticTargetingInputs)?;
        let mut stats = source.stats;
        stats.fleet_damage_done = i64::from(self.weapon_rank_history.fleet_damage[&source.fleet_owner_ship_id]);
        Ok(stats)
    }

    pub(super) fn begin_weapon_rank_tick(&mut self) {
        let history = &mut self.weapon_rank_history;
        if let Some(frame) = &mut history.frame { *frame = frame.wrapping_add(1); }
        history.consumed_events = 0;
    }

    pub(super) fn consume_weapon_rank_events(&mut self) -> Result<(), CombatWorldError> {
        let history = &mut self.weapon_rank_history;
        let Some(frame) = history.frame else { return Ok(()) };
        for event in &self.events[history.consumed_events..] {
            match event {
                CombatEvent::Hit(hit) => {
                    let credited = if hit.attacker_side != hit.target_side { Some(hit.attacker_id) }
                        else { history.killers.get(&hit.attacker_id).copied() };
                    if let Some(source) = credited.and_then(|id| history.ships.get(&id)) {
                        let damage = history.fleet_damage.get_mut(&source.fleet_owner_ship_id)
                            .expect("registered source FleetShip history");
                        // Source FleetShip.DamageDone is an unchecked C# int.
                        *damage = damage.wrapping_add(hit.delta.tsv_loss);
                    } else if credited.is_some() {
                        return Err(CombatWorldError::MissingAutomaticTargetingInputs);
                    }
                },
                CombatEvent::ShipDied { victim_id, killer_id, .. } if *killer_id > 0 => {
                    if self.ship_by_id.get(victim_id).is_some_and(|&index| self.ships[index].ship_type == 10) {
                        history.killers.insert(*victim_id, *killer_id);
                    }
                    if let Some(source) = history.ships.get_mut(killer_id) {
                        source.stats.last_killed = frame;
                        let revision = history.kill_revisions.entry(*killer_id).or_default();
                        *revision = revision.saturating_add(1);
                    } else { return Err(CombatWorldError::MissingAutomaticTargetingInputs); }
                },
                _ => (),
            }
        }
        history.consumed_events = self.events.len();
        Ok(())
    }
}
