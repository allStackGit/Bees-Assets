//! Unity RlOneVsOneEpisodeCoordinator.RecordMiningValueUpdated,
//! RecordMiningShipExit and GetRetainableMinedTsv.
//!
//! Economic rewards are linear, uncapped and separate from the tactical
//! TSV shaping budget. Source values are FleetShip.MineralsMinedThisLevel,
//! not the short-lived ship's own health, collider or mined cargo.

use std::collections::{HashMap, HashSet};

use crate::combat_world::{CombatEvent, CombatWorld};
use crate::rl_shaping::RewardEmission;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EconomicLedgerError {
    InvalidSides,
    InvalidStartingValue,
    MissingShip(i64),
    MissingAuthoredMinedValue(i64),
    InvalidAuthoredMinedValue(i64),
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct EconomicLedgerMeasurements {
    pub updates: u64,
    pub ship_exits: u64,
    pub mined_value_forfeitures: u64,
    pub enemy_mined_value_rewards: u64,
}

/// One Level/episode owns one ledger. Registered values survive ship
/// retirement for final diagnostics, just like Unity's dictionaries.
pub struct EpisodeEconomicLedger {
    sides: [i32; 2],
    starting_tsv: [i32; 2],
    mined_by_ship: HashMap<i64, (i32, i32)>,
    forfeited: HashSet<i64>,
    /// Reused for source-order EndKill returns during this combat tick.
    returned_this_tick: HashSet<i64>,
    destroyed_mined_tsv: [i64; 2],
    measurements: EconomicLedgerMeasurements,
}

impl EpisodeEconomicLedger {
    pub fn new(
        bee_side: i32, human_side: i32,
        bee_starting_tsv: i32, human_starting_tsv: i32,
    ) -> Result<Self, EconomicLedgerError> {
        if bee_side <= 0 || human_side <= 0 || bee_side == human_side {
            return Err(EconomicLedgerError::InvalidSides);
        }
        if bee_starting_tsv <= 0 || human_starting_tsv <= 0 {
            return Err(EconomicLedgerError::InvalidStartingValue);
        }
        Ok(Self {
            sides: [bee_side, human_side],
            starting_tsv: [bee_starting_tsv, human_starting_tsv],
            mined_by_ship: HashMap::new(),
            forfeited: HashSet::new(),
            returned_this_tick: HashSet::new(),
            destroyed_mined_tsv: [0, 0],
            measurements: EconomicLedgerMeasurements::default(),
        })
    }

    pub fn measurements(&self) -> EconomicLedgerMeasurements {
        self.measurements
    }

    pub fn destroyed_mined_tsv(&self, side: i32) -> Option<i64> {
        self.sides.iter().position(|&s| s == side)
            .map(|index| self.destroyed_mined_tsv[index])
    }

    /// Source skips non-positive mining updates; if a ship's live cargo
    /// later becomes zero, the last positive tracked value remains until
    /// its actual exit/forfeiture event.
    pub fn update_mined_value(
        &mut self,
        world: &CombatWorld,
        ship_id: i64,
        authored_mined_tsv: i32,
    ) -> Result<(), EconomicLedgerError> {
        let ship = world.ship(ship_id)
            .filter(|ship| !ship.vitals.is_dead())
            .ok_or(EconomicLedgerError::MissingShip(ship_id))?;
        if authored_mined_tsv < 0 {
            return Err(EconomicLedgerError::InvalidAuthoredMinedValue(ship_id));
        }
        if self.sides.contains(&ship.side) && authored_mined_tsv > 0 {
            self.mined_by_ship.insert(ship_id, (ship.side, authored_mined_tsv));
            self.forfeited.remove(&ship_id);
            self.measurements.updates += 1;
        }
        Ok(())
    }

    /// Source CalculateEconomicValueReward; intentionally NOT bounded by
    /// the common 0.2 positive tactical cap.
    pub fn economic_reward(value: i32, receiving_starting_tsv: i32) -> f32 {
        if value <= 0 || receiving_starting_tsv <= 0 {
            0.0
        } else {
            value as f32 / receiving_starting_tsv as f32
        }
    }

    /// Called when the authoritative world ends a ship's lifecycle.
    /// During a normal combat death, its mined value is forfeited once.
    /// If the ship was safely returned/warped at a normal gameplay stage
    /// boundary, the caller sets safely_retained=true.
    ///
    /// Unity attributes destroyed enemy mined TSV to the direct killer
    /// only, unlike the separate terminal death-attribution chain.
    pub fn exit_ship(
        &mut self,
        world: &CombatWorld,
        victim_id: i64,
        killer_id: i64,
        safely_retained: bool,
        authored_current_mined_tsv: i32,
    ) -> Result<Option<RewardEmission>, EconomicLedgerError> {
        let victim = world.ship(victim_id)
            .ok_or(EconomicLedgerError::MissingShip(victim_id))?;
        if authored_current_mined_tsv < 0 {
            return Err(EconomicLedgerError::InvalidAuthoredMinedValue(victim_id));
        }
        if !self.sides.contains(&victim.side) {
            return Ok(None);
        }
        let tracked = self.mined_by_ship.get(&victim_id)
            .map_or(0, |(_, value)| *value);
        let mined = tracked.max(authored_current_mined_tsv);
        if mined <= 0 {
            return Ok(None);
        }
        self.mined_by_ship.insert(victim_id, (victim.side, mined));
        self.measurements.ship_exits += 1;
        if safely_retained || !self.forfeited.insert(victim_id) {
            return Ok(None);
        }
        self.measurements.mined_value_forfeitures += 1;
        let Some(killer) = world.ship(killer_id) else {
            return Ok(None);
        };
        if killer.side == victim.side {
            return Ok(None);
        }
        let Some(index) = self.sides.iter().position(|&side| side == killer.side)
        else { return Ok(None) };
        self.destroyed_mined_tsv[index] += i64::from(mined);
        self.measurements.enemy_mined_value_rewards += 1;
        Ok(Some(RewardEmission {
            side: killer.side,
            amount: Self::economic_reward(mined, self.starting_tsv[index]),
        }))
    }

    /// Returns the accumulated tracked value for the winning side. The
    /// terminal controller itself gates these values by its actual winner,
    /// so a timeout still pays nobody.
    pub fn retainable_mined_tsv(&self, side: i32) -> i32 {
        self.mined_by_ship.iter().filter_map(|(&id, &(owner_side, tsv))|
            if owner_side == side && !self.forfeited.contains(&id) {
                Some(tsv)
            } else {
                None
            }
        ).fold(0_i32, |total, value| total.saturating_add(value))
    }

    /// Read the same immutable ShipDied events as gameplay and combat
    /// shaping. The callback supplies the authoritative FleetShip value
    /// at death, even when no prior mining update was observed by this
    /// consumer. Errors invalidate the episode's partial reward output.
    pub fn consume_ship_deaths(
        &mut self,
        world: &CombatWorld,
        events: &[CombatEvent],
        mut current_mined_tsv: impl FnMut(i64) -> Option<i32>,
        output: &mut Vec<RewardEmission>,
    ) -> Result<(), EconomicLedgerError> {
        output.clear();
        self.returned_this_tick.clear();
        for event in events {
            match event {
                CombatEvent::ShipWarped { ship_id, .. } => {
                    self.returned_this_tick.insert(*ship_id);
                }
                CombatEvent::ShipDied { victim_id, killer_id, .. } => {
                    let value = current_mined_tsv(*victim_id).ok_or(
                        EconomicLedgerError::MissingAuthoredMinedValue(*victim_id),
                    )?;
                    let returned = self.returned_this_tick.contains(victim_id);
                    if let Some(emission) = self.exit_ship(
                        world, *victim_id, *killer_id, returned, value,
                    )? {
                        if emission.amount != 0.0 {
                            output.push(emission);
                        }
                    }
                }
                _ => {}
            }
        }
        Ok(())
    }
}
