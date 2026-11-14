//! Authoritative per-Level FleetShip persistence-facing values.
//!
//! CombatWorld remains the source of physical health/cargo, while the
//! persistent FleetShip owner alone owns MineralsMinedThisLevel and whether
//! damage changes a real long-term fleet. These two domains must not be
//! conflated; the common reward coordinator demands the values by ship ID.
//! One registration per actual source ship, no per-step scans or telemetry.

use std::collections::{HashMap, HashSet};
use bees_gameplay::level_rules::NativeLevelRewardMetadata;
use bees_sim::rl_shaping::DamageRewardMetadata;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeFleetShipValue {
    pub ship_id: i64,
    pub has_persistent_fleet_value: bool,
    pub minerals_mined_this_level: i32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeFleetInventoryError {
    InvalidShipValue,
    DuplicateShip(i64),
    UnknownShip(i64),
    InvalidMiningCredit,
    MiningCreditOverflow,
}

/// The owning native Level constructs this from its *real* FleetShip
/// registry, including temporary/minion ships with persistent=false.
/// A killed ship's record stays available for deferred damage/economics
/// until the Level itself is retired.
pub struct NativeFleetInventory {
    entries: HashMap<i64, NativeFleetShipValue>,
    /// Fatal losses for the current Level. A successful extraction or
    /// WarpGate departure is not a persistent fleet death.
    killed: HashSet<i64>,
}

impl NativeFleetInventory {
    pub fn from_source(
        values: impl IntoIterator<Item = NativeFleetShipValue>,
    ) -> Result<Self, NativeFleetInventoryError> {
        let mut entries = HashMap::new();
        for value in values {
            if value.ship_id <= 0 || value.minerals_mined_this_level < 0 {
                return Err(NativeFleetInventoryError::InvalidShipValue);
            }
            if entries.insert(value.ship_id, value).is_some() {
                return Err(NativeFleetInventoryError::DuplicateShip(value.ship_id));
            }
        }
        Ok(Self { entries, killed: HashSet::new() })
    }

    pub fn get(&self, ship_id: i64) -> Option<NativeFleetShipValue> {
        self.entries.get(&ship_id).copied()
    }

    pub fn was_killed(&self, ship_id: i64) -> bool {
        self.killed.contains(&ship_id)
    }

    /// Native initial scene composition can omit ships before the Level is
    /// constructed. This removes only the episode-local FleetShip lookup,
    /// never the external player's persistent fleet/account record.
    pub fn omit_initial_ship(&mut self, ship_id: i64) -> bool {
        self.killed.remove(&ship_id);
        self.entries.remove(&ship_id).is_some()
    }

    pub fn register_spawned_ship(
        &mut self,
        source: NativeFleetShipValue,
    ) -> Result<(), NativeFleetInventoryError> {
        if source.ship_id <= 0 || source.minerals_mined_this_level < 0 {
            return Err(NativeFleetInventoryError::InvalidShipValue);
        }
        if self.entries.contains_key(&source.ship_id) {
            return Err(NativeFleetInventoryError::DuplicateShip(source.ship_id));
        }
        self.entries.insert(source.ship_id, source);
        Ok(())
    }

    /// Unity Ship.LogKilledStats clears temporary per-Level mining credit on
    /// death; only surviving saved ships contribute it at SaveAndEnd.
    /// Keep the dead FleetShip record for attribution and future diagnostics.
    pub fn retire_mined_credit(&mut self, ship_id: i64) {
        if let Some(value) = self.entries.get_mut(&ship_id) {
            value.minerals_mined_this_level = 0;
            self.killed.insert(ship_id);
        }
    }

    pub fn credit(
        &mut self,
        ship_id: i64,
        positive_amount: i32,
    ) -> Result<i32, NativeFleetInventoryError> {
        if positive_amount <= 0 {
            return Err(NativeFleetInventoryError::InvalidMiningCredit);
        }
        let value = self.entries.get_mut(&ship_id)
            .ok_or(NativeFleetInventoryError::UnknownShip(ship_id))?;
        let total = value.minerals_mined_this_level
            .checked_add(positive_amount)
            .ok_or(NativeFleetInventoryError::MiningCreditOverflow)?;
        value.minerals_mined_this_level = total;
        Ok(total)
    }
}

/// A standalone authoritative FleetShip owner can implement the exact
/// reward/terminal adapter. More featureful rendered Levels may embed this
/// inventory in their own metadata and forward these three methods.
impl NativeLevelRewardMetadata for NativeFleetInventory {
    fn damage_metadata(&self, ship_id: i64) -> Option<DamageRewardMetadata> {
        let value = self.get(ship_id)?;
        Some(DamageRewardMetadata {
            has_persistent_fleet_value: value.has_persistent_fleet_value,
            mined_cargo_before: value.minerals_mined_this_level,
        })
    }

    fn current_mined_tsv(&self, ship_id: i64) -> Option<i32> {
        self.get(ship_id).map(|value| value.minerals_mined_this_level)
    }

    fn credit_mined_minerals(&mut self, ship_id: i64, amount: i32) -> Option<i32> {
        self.credit(ship_id, amount).ok()
    }

    fn retire_mined_credit_on_death(&mut self, ship_id: i64) {
        self.retire_mined_credit(ship_id);
    }
}
