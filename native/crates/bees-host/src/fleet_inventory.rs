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
use bees_sim::combat_world::{CombatEvent, CombatWorld};
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
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

/// Source-oriented per-battle career counters computed from real combat
/// events, after the shared reward consumer has processed that tick.
/// These are observed physical events, not inferred from PPO reward
/// totals or model decisions. Direct-hit damage uses loss of TSV,
/// consistent with Unity FleetShip.DamageDone/DamageReceived.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NativeFleetCombatStats {
    pub observed_turret_shots: u64,
    pub enemy_tsv_damage_done: u64,
    pub tsv_damage_received: u64,
    pub attributed_enemy_kills: u64,
    /// Older career receipts have no special-action observations. A
    /// false value is UNKNOWN, not proof of zero specials in that match.
    #[serde(default)]
    pub special_actions_recorded: bool,
    #[serde(default)]
    pub striker_bombs_dropped: u64,
    #[serde(default)]
    pub fire_barge_explosions: u64,
    #[serde(default)]
    pub yellow_jacket_detonations: u64,
    #[serde(default)]
    pub barge_charges_started: u64,
    #[serde(default)]
    pub scout_beacons_deployed: u64,
    #[serde(default)]
    pub queen_minions_spawned: u64,
}

/// The owning native Level constructs this from its *real* FleetShip
/// registry, including temporary/minion ships with persistent=false.
/// A killed ship's record stays available for deferred damage/economics
/// until the Level itself is retired.
#[derive(Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NativeFleetInventory {
    entries: HashMap<i64, NativeFleetShipValue>,
    /// Fatal losses for the current Level. A successful extraction or
    /// WarpGate departure is not a persistent fleet death.
    killed: HashSet<i64>,
    combat: HashMap<i64, NativeFleetCombatStats>,
    career_tracking: bool,
}

/// Durable FleetShip career/mining credit, distinct from physical hull TSV.
/// Does not serialize a second physics representation or episode reward.
#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NativeFleetInventoryCheckpoint {
    schema_version: u32,
    source: NativeFleetInventory,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeFleetRestoreError {
    InvalidVersion,
    MissingSourceShip,
    ChangedFleetOwnership,
    InvalidEntry,
    InvalidCombatCredit,
}

impl NativeFleetInventory {
    pub fn checkpoint(&self) -> NativeFleetInventoryCheckpoint {
        NativeFleetInventoryCheckpoint {
            schema_version: 1,
            source: self.clone(),
        }
    }

    /// Stage source career history at the same fixed tick as CombatWorld.
    /// Spawned minions may add genuine new nonpersistent FleetShips, but
    /// neither an original ship's persistent ownership nor its history may
    /// disappear/relabel under recovery.
    pub fn prepare_restore(
        &self, checkpoint: NativeFleetInventoryCheckpoint,
    ) -> Result<Self, NativeFleetRestoreError> {
        use NativeFleetRestoreError as Error;
        if checkpoint.schema_version != 1 {
            return Err(Error::InvalidVersion);
        }
        let restored = checkpoint.source;
        if self.career_tracking != restored.career_tracking {
            return Err(Error::ChangedFleetOwnership);
        }
        for (&ship, old) in &self.entries {
            let saved = restored.entries.get(&ship).ok_or(Error::MissingSourceShip)?;
            if old.has_persistent_fleet_value != saved.has_persistent_fleet_value {
                return Err(Error::ChangedFleetOwnership);
            }
        }
        if restored.entries.iter().any(|(&id, entry)|
            id <= 0 || entry.ship_id != id ||
            entry.minerals_mined_this_level < 0 ||
            (!self.entries.contains_key(&id) &&
                entry.has_persistent_fleet_value)
        ) {
            return Err(Error::InvalidEntry);
        }
        if restored.killed.iter().any(|id|
            !restored.entries.contains_key(id)
        ) || restored.combat.iter().any(|(id, _)|
            !restored.entries.get(id).is_some_and(|entry|
                entry.has_persistent_fleet_value
            )
        ) || (!restored.career_tracking && !restored.combat.is_empty()) {
            return Err(Error::InvalidCombatCredit);
        }
        Ok(restored)
    }

    pub fn restore_checkpoint(
        &mut self, checkpoint: NativeFleetInventoryCheckpoint,
    ) -> Result<(), NativeFleetRestoreError> {
        let restored = self.prepare_restore(checkpoint)?;
        *self = restored;
        Ok(())
    }

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
        Ok(Self {
            entries, killed: HashSet::new(), combat: HashMap::new(),
            career_tracking: false,
        })
    }

    pub fn get(&self, ship_id: i64) -> Option<NativeFleetShipValue> {
        self.entries.get(&ship_id).copied()
    }

    pub fn was_killed(&self, ship_id: i64) -> bool {
        self.killed.contains(&ship_id)
    }

    /// Career statistics are an opt-in game feature. Headless PPO
    /// collectors keep this ledger disabled to avoid per-event HashMap
    /// traffic and unneeded allocations in the hot training loop.
    pub fn enable_career_tracking(&mut self) {
        self.career_tracking = true;
    }

    pub fn combat_stats(&self, ship_id: i64) -> Option<NativeFleetCombatStats> {
        if !self.career_tracking { return None; }
        self.entries.get(&ship_id)
            .filter(|entry| entry.has_persistent_fleet_value)?;
        let mut stats = self.combat.get(&ship_id).copied().unwrap_or_default();
        stats.special_actions_recorded = true;
        Some(stats)
    }

    /// Sparse special actions are authenticated by the same physical
    /// event buffer as combat. Only persistent FleetShips with campaign
    /// career tracking enabled have a stored row.
    fn persistent_credit_owner(&self, world: &CombatWorld, ship_id: i64) -> Option<i64> {
        // Existing rank history represents the same FleetShip object
        // shared by source Carrier/Queen/Scout children. Do not follow a
        // live replacement Carrier after its original owner dies.
        let owner = world.ship_fleet_rank_owner(ship_id).unwrap_or(ship_id);
        self.entries.get(&owner)
            .filter(|entry| entry.has_persistent_fleet_value)
            .map(|_| owner)
    }

    fn special_stats(&mut self, world: &CombatWorld, ship_id: i64)
        -> Option<&mut NativeFleetCombatStats>
    {
        let owner = self.persistent_credit_owner(world, ship_id)?;
        Some(self.combat.entry(owner).or_default())
    }

    /// One pass over the authoritative combat event buffer per completed
    /// tick. No extra full-fleet scan, independent damage ledger, or work
    /// in quiet ticks. All three source gameplay paths share these events.
    pub fn record_combat_events(&mut self, world: &CombatWorld) {
        if !self.career_tracking { return; }
        for event in world.events() {
            match event {
                CombatEvent::GameWeaponFired { ship_id, .. } |
                CombatEvent::PolicyWeaponFired { ship_id, .. } => {
                    if let Some(row) = self.special_stats(world, *ship_id) {
                        row.observed_turret_shots =
                            row.observed_turret_shots.saturating_add(1);
                    }
                }
                CombatEvent::StrikerBombDropped { ship_id, .. } => {
                    if let Some(stats) = self.special_stats(world, *ship_id) {
                        stats.striker_bombs_dropped = stats.striker_bombs_dropped.saturating_add(1);
                    }
                }
                // Also count death-triggered explosions; not every blast
                // was a player special-action input.
                CombatEvent::FireBargeDetonated { ship_id, .. } => {
                    if let Some(stats) = self.special_stats(world, *ship_id) {
                        stats.fire_barge_explosions = stats.fire_barge_explosions.saturating_add(1);
                    }
                }
                CombatEvent::YellowJacketDetonated { ship_id, .. } => {
                    if let Some(stats) = self.special_stats(world, *ship_id) {
                        stats.yellow_jacket_detonations = stats.yellow_jacket_detonations.saturating_add(1);
                    }
                }
                CombatEvent::BargeChargeStarted { ship_id } => {
                    if let Some(stats) = self.special_stats(world, *ship_id) {
                        stats.barge_charges_started = stats.barge_charges_started.saturating_add(1);
                    }
                }
                CombatEvent::ScoutBeaconDropped { scout_id, .. } => {
                    if let Some(stats) = self.special_stats(world, *scout_id) {
                        stats.scout_beacons_deployed = stats.scout_beacons_deployed.saturating_add(1);
                    }
                }
                CombatEvent::QueenMinionSpawned { queen_id, .. } => {
                    if let Some(stats) = self.special_stats(world, *queen_id) {
                        stats.queen_minions_spawned = stats.queen_minions_spawned.saturating_add(1);
                    }
                }
                CombatEvent::Hit(hit) => {
                    let loss = hit.delta.tsv_loss.max(0) as u64;
                    if loss == 0 { continue; }
                    if hit.attacker_side != hit.target_side {
                        if let Some(row) = self.special_stats(world, hit.attacker_id) {
                            row.enemy_tsv_damage_done =
                                row.enemy_tsv_damage_done.saturating_add(loss);
                        }
                    }
                    if let Some(row) = self.special_stats(world, hit.target_id) {
                        row.tsv_damage_received =
                            row.tsv_damage_received.saturating_add(loss);
                    }
                }
                CombatEvent::UnattributedDamage { target_id, delta } => {
                    if let Some(row) = self.special_stats(world, *target_id) {
                        row.tsv_damage_received = row.tsv_damage_received
                            .saturating_add(delta.tsv_loss.max(0) as u64);
                    }
                }
                CombatEvent::ShipDied { victim_id, killer_id, .. } => {
                    if killer_id == victim_id || *killer_id <= 0 {
                        continue;
                    }
                    let opposing = world.ship(*killer_id).zip(world.ship(*victim_id))
                        .is_some_and(|(killer, victim)| killer.side != victim.side);
                    if opposing {
                        if let Some(row) = self.special_stats(world, *killer_id) {
                            row.attributed_enemy_kills =
                                row.attributed_enemy_kills.saturating_add(1);
                        }
                    }
                }
                _ => {}
            }
        }
    }

    /// The same source account also credits the Fire Barge's external
    /// killer for any later explosion damage to that Barge's own side.
    /// This is an opt-in career counter, not a second combat/reward path.
    pub fn record_fire_barge_chain_damage(&mut self, killer_id: i64, tsv_loss: i32) {
        if !self.career_tracking || tsv_loss <= 0 ||
            !self.entries.get(&killer_id)
                .is_some_and(|entry| entry.has_persistent_fleet_value)
        {
            return;
        }
        let row = self.combat.entry(killer_id).or_default();
        row.enemy_tsv_damage_done = row.enemy_tsv_damage_done
            .saturating_add(tsv_loss as u64);
    }

    /// Native initial scene composition can omit ships before the Level is
    /// constructed. This removes only the episode-local FleetShip lookup,
    /// never the external player's persistent fleet/account record.
    pub fn omit_initial_ship(&mut self, ship_id: i64) -> bool {
        self.killed.remove(&ship_id);
        self.combat.remove(&ship_id);
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

    fn observe_fleet_combat_tick(&mut self, world: &CombatWorld) {
        self.record_combat_events(world);
    }

    fn observe_fire_barge_chain_damage(&mut self, killer_ship_id: i64, tsv_loss: i32) {
        self.record_fire_barge_chain_damage(killer_ship_id, tsv_loss);
    }
}
