//! Native implementation of the dedicated RL episode's tactical TSV shaping.
//!
//! Unlike simulation rules, this is an optional consumer of the SAME combat
//! events emitted by graphical and headless gameplay. It never mutates ship
//! health, movement, kill ownership, or command outcomes.
//!
//! Sources: RlOneVsOneReward.CalculateTsvLossReward,
//! CalculateBoundedShapingIncrement and
//! RlOneVsOneEpisodeCoordinator.RecordHitWithSource /
//! RecordUnattributedTsvLoss / ApplyImmediateTsvReward.
//!
//! Terminal, economic, discovery, near-miss and timeout reward sources still
//! need to be connected to the same side-wide shaping budget before training.

use crate::combat::HitEvent;
use crate::combat_world::{CombatEvent, CombatWorld};

pub const TSV_REWARD_SCALE: f32 = 0.1;
pub const RAW_MISS_PENALTY: f32 = 0.02;
pub const RAW_NEAR_MISS_REWARD: f32 = 0.03;
pub const MAX_POSITIVE_SHAPING: f32 = 0.2;
pub const MAX_NEGATIVE_SHAPING: f32 = 0.2;

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct TacticalShaping {
    positive_raw: f64,
    negative_raw: f64,
    positive_emitted: f64,
    negative_emitted: f64,
}

impl TacticalShaping {
    pub fn positive_raw(&self) -> f64 { self.positive_raw }
    pub fn negative_raw(&self) -> f64 { self.negative_raw }
    pub fn positive_emitted(&self) -> f64 { self.positive_emitted }
    pub fn negative_emitted(&self) -> f64 { self.negative_emitted }

    /// Same asymptotic reward function used by the Unity coordinator.
    /// Positive and negative budgets remain independent. The method is
    /// generic so all other tactical events can share this single budget
    /// rather than opening independent category-specific reward caps.
    pub fn apply(&mut self, raw_signed: f32) -> f32 {
        if !raw_signed.is_finite() || raw_signed == 0.0 {
            return 0.0;
        }
        let (raw_total, emitted, maximum, sign) = if raw_signed > 0.0 {
            (&mut self.positive_raw, &mut self.positive_emitted,
                f64::from(MAX_POSITIVE_SHAPING), 1.0f32)
        } else {
            (&mut self.negative_raw, &mut self.negative_emitted,
                f64::from(MAX_NEGATIVE_SHAPING), -1.0f32)
        };
        let added = f64::from(raw_signed.abs());
        let bounded_increment = maximum * maximum * added /
            ((maximum + *raw_total) * (maximum + *raw_total + added));
        *raw_total += added;
        *emitted += bounded_increment;
        sign * bounded_increment.max(0.0) as f32
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct RewardEmission {
    pub side: i32,
    pub amount: f32,
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct HitShaping {
    pub attacker: Option<RewardEmission>,
    pub victim: Option<RewardEmission>,
}

/// The same authoritative Level metadata used by Unity for hit rewards.
/// Persistent-fleet TSV is not equivalent to a ship collider or its
/// temporary mined cargo, so callers must supply these values explicitly.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DamageRewardMetadata {
    pub has_persistent_fleet_value: bool,
    pub mined_cargo_before: i32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DamageShapingError {
    MissingTarget(i64),
    MissingTargetMetadata(i64),
    InvalidTargetMetadata(i64),
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DamageShapingMeasurements {
    pub attributed_hits: u64,
    pub unattributed_damage: u64,
    pub projectile_roots_completed: u64,
    pub projectile_roots_missed: u64,
    pub projectile_roots_near_missed: u64,
    pub capability_outcomes: u64,
    pub emissions: u64,
}

/// Per-episode shaping state, independent per faction. Both positive and
/// negative effects for a faction share their own cap across source types.
pub struct EpisodeShaping {
    sides: [i32; 2],
    combined_starting_tsv: i32,
    accumulators: [TacticalShaping; 2],
    damage_measurements: DamageShapingMeasurements,
}

impl EpisodeShaping {
    pub fn new(
        bee_side: i32,
        human_side: i32,
        combined_starting_tsv: i32,
    ) -> Result<Self, &'static str> {
        if bee_side <= 0 || human_side <= 0 || bee_side == human_side {
            return Err("episode sides must be distinct positive identifiers");
        }
        Ok(Self {
            sides: [bee_side, human_side],
            combined_starting_tsv: combined_starting_tsv.max(1),
            accumulators: [TacticalShaping::default(); 2],
            damage_measurements: DamageShapingMeasurements::default(),
        })
    }

    pub fn reset(&mut self, combined_starting_tsv: i32) {
        self.combined_starting_tsv = combined_starting_tsv.max(1);
        self.accumulators = [TacticalShaping::default(); 2];
        self.damage_measurements = DamageShapingMeasurements::default();
    }

    pub fn damage_measurements(&self) -> DamageShapingMeasurements {
        self.damage_measurements
    }

    pub fn for_side(&self, side: i32) -> Option<&TacticalShaping> {
        self.sides.iter().position(|value| *value == side)
            .map(|index| &self.accumulators[index])
    }

    /// Used for misses, discovery and hazards as well as TSV. This is the
    /// same shared budget, never a new allowance per reward category.
    pub fn add_raw_for_side(&mut self, side: i32, raw: f32) -> Option<RewardEmission> {
        let index = self.sides.iter().position(|value| *value == side)?;
        Some(RewardEmission {
            side,
            amount: self.accumulators[index].apply(raw),
        })
    }

    /// Source RecordSuccessfulCapabilityOutcome for successful mining,
    /// Beehive healing and WarpGate exit. Caller supplies the actual
    /// positive fleet TSV change or preserved TSV from gameplay; this
    /// method neither mutates the world nor creates capability events.
    /// Positive and negative TSV sources share the SAME shaping cap.
    pub fn record_successful_capability_outcome(
        &mut self, side: i32, tsv_value: i32,
    ) -> Option<RewardEmission> {
        if tsv_value <= 0 || !self.sides.contains(&side) {
            return None;
        }
        self.damage_measurements.capability_outcomes += 1;
        let raw = (tsv_value as f32 /
            self.combined_starting_tsv as f32) * TSV_REWARD_SCALE;
        self.add_raw_for_side(side, raw)
    }

    /// Unity distinguishes fleet damage from lost mined cargo. A lethal hit
    /// removes cargo from TSV, but its economic reward is a different source.
    pub fn persistent_tsv_loss(
        &self,
        damage_tsv_loss: i32,
        mined_cargo_before: i32,
        killed: bool,
        has_persistent_fleet_value: bool,
    ) -> i32 {
        if !has_persistent_fleet_value { return 0; }
        damage_tsv_loss.saturating_sub(
            if killed { mined_cargo_before.max(0) } else { 0 }
        ).max(0)
    }

    /// Reproduces RecordHitWithSource: enemy damage rewards the attacker
    /// and penalizes the victim. Friendly fire is a victim penalty only,
    /// so the same-side attacker credit cannot cancel the loss signal.
    /// Self-inflicted damage is treated as unattributed for RL purposes.
    pub fn record_hit(
        &mut self,
        hit: &HitEvent,
        has_persistent_fleet_value: bool,
        target_mined_cargo_before: i32,
    ) -> HitShaping {
        // RecordHitWithSource returns without shaping unless BOTH ships
        // belong to the current two-sided training matchup.
        if !self.sides.contains(&hit.attacker_side) ||
            !self.sides.contains(&hit.target_side) {
            return HitShaping::default();
        }
        let tsv = self.persistent_tsv_loss(
            hit.delta.tsv_loss,
            target_mined_cargo_before,
            hit.delta.killed,
            has_persistent_fleet_value,
        );
        if tsv == 0 { return HitShaping::default(); }
        let raw = (tsv as f32 / self.combined_starting_tsv as f32)
            * TSV_REWARD_SCALE;
        let attacker = if hit.attacker_side != hit.target_side && !hit.self_inflicted {
            self.add_raw_for_side(hit.attacker_side, raw)
        } else {
            None
        };
        let victim = self.add_raw_for_side(hit.target_side, -raw);
        HitShaping { attacker, victim }
    }

    /// Consume the *same* CombatWorld event queue used by the renderer
    /// and physical combat, including ordered projectile-root outcomes.
    /// Only events with real TSV loss need authored fleet metadata,
    /// avoiding any lookup on harmless/zero-damage hits. The caller supplies
    /// source FleetShip values (never guessed from ShipVitals) and a reusable
    /// emissions buffer for the MA-POCA reward adapter.
    ///
    /// An error invalidates the partial emissions and episode. The caller
    /// must not continue a partially processed tick or emit training samples.
    pub fn consume_combat_shaping_events(
        &mut self,
        world: &CombatWorld,
        events: &[CombatEvent],
        mut metadata: impl FnMut(i64) -> Option<DamageRewardMetadata>,
        emissions: &mut Vec<RewardEmission>,
    ) -> Result<(), DamageShapingError> {
        emissions.clear();
        for event in events {
            match event {
                CombatEvent::Hit(hit) if hit.delta.tsv_loss > 0 => {
                    let target = world.ship(hit.target_id)
                        .ok_or(DamageShapingError::MissingTarget(hit.target_id))?;
                    if target.side != hit.target_side {
                        return Err(DamageShapingError::InvalidTargetMetadata(
                            hit.target_id,
                        ));
                    }
                    let entry = metadata(hit.target_id)
                        .ok_or(DamageShapingError::MissingTargetMetadata(
                            hit.target_id,
                        ))?;
                    if entry.mined_cargo_before < 0 {
                        return Err(DamageShapingError::InvalidTargetMetadata(
                            hit.target_id,
                        ));
                    }
                    self.damage_measurements.attributed_hits += 1;
                    let rewards = self.record_hit(
                        hit, entry.has_persistent_fleet_value,
                        entry.mined_cargo_before,
                    );
                    if let Some(attacker) = rewards.attacker {
                        if attacker.amount != 0.0 {
                            emissions.push(attacker);
                            self.damage_measurements.emissions += 1;
                        }
                    }
                    if let Some(victim) = rewards.victim {
                        if victim.amount != 0.0 {
                            emissions.push(victim);
                            self.damage_measurements.emissions += 1;
                        }
                    }
                }
                CombatEvent::UnattributedDamage { target_id, delta }
                    if delta.tsv_loss > 0 => {
                    let target = world.ship(*target_id)
                        .ok_or(DamageShapingError::MissingTarget(*target_id))?;
                    let entry = metadata(*target_id)
                        .ok_or(DamageShapingError::MissingTargetMetadata(
                            *target_id,
                        ))?;
                    if entry.mined_cargo_before < 0 {
                        return Err(DamageShapingError::InvalidTargetMetadata(
                            *target_id,
                        ));
                    }
                    self.damage_measurements.unattributed_damage += 1;
                    if let Some(reward) = self.record_unattributed_loss(
                        target.side, delta.tsv_loss, entry.mined_cargo_before,
                        delta.killed, entry.has_persistent_fleet_value,
                    ) {
                        if reward.amount != 0.0 {
                            emissions.push(reward);
                            self.damage_measurements.emissions += 1;
                        }
                    }
                }
                CombatEvent::CapabilityHealed {
                    side, tsv_gain, ..
                } => {
                    if let Some(reward) = self
                        .record_successful_capability_outcome(*side, *tsv_gain)
                    {
                        if reward.amount != 0.0 {
                            emissions.push(reward);
                            self.damage_measurements.emissions += 1;
                        }
                    }
                }
                CombatEvent::ProjectileRootCompleted(root) => {
                    self.damage_measurements.projectile_roots_completed += 1;
                    if !root.enemy_contact {
                        self.damage_measurements.projectile_roots_missed += 1;
                        self.damage_measurements.projectile_roots_near_missed +=
                            u64::from(root.near_enemy);
                        if let Some(reward) = self.record_resolved_projectile_miss(
                            root.shooter_side, root.near_enemy,
                        ) {
                            if reward.amount != 0.0 {
                                emissions.push(reward);
                                self.damage_measurements.emissions += 1;
                            }
                        }
                    }
                }
                _ => {}
            }
        }
        Ok(())
    }

    /// Source RecordResolvedProjectileMiss. The physical projectile-root
    /// tracker calls exactly once per resolved root, never per fragment.
    pub fn record_resolved_projectile_miss(
        &mut self, side: i32, near: bool,
    ) -> Option<RewardEmission> {
        self.add_raw_for_side(
            side, if near { RAW_NEAR_MISS_REWARD } else { -RAW_MISS_PENALTY },
        )
    }

    /// Port of RecordUnattributedTsvLoss. The source only emits a negative
    /// casualty-preservation signal for the damaged side.
    pub fn record_unattributed_loss(
        &mut self,
        victim_side: i32,
        tsv_loss: i32,
        mined_cargo_before: i32,
        killed: bool,
        has_persistent_fleet_value: bool,
    ) -> Option<RewardEmission> {
        let tsv = self.persistent_tsv_loss(
            tsv_loss, mined_cargo_before, killed, has_persistent_fleet_value,
        );
        if tsv <= 0 { return None; }
        let raw = (tsv as f32 / self.combined_starting_tsv as f32)
            * TSV_REWARD_SCALE;
        self.add_raw_for_side(victim_side, -raw)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::combat::{DamageDelta, DamageSource, HitEvent};

    #[test]
    fn shaping_is_bounded_independently_by_sign() {
        let mut shaping = TacticalShaping::default();
        for _ in 0..1000 {
            assert!(shaping.apply(0.01) > 0.0);
            assert!(shaping.apply(-0.01) < 0.0);
        }
        assert!(shaping.positive_emitted() < f64::from(MAX_POSITIVE_SHAPING));
        assert!(shaping.negative_emitted() < f64::from(MAX_NEGATIVE_SHAPING));
    }

    #[test]
    fn fatal_hit_excludes_mined_cargo_from_tactical_reward() {
        let mut rewards = EpisodeShaping::new(1, 2, 2000).unwrap();
        let hit = HitEvent::new(
            7, 7, 3, 5, 1, 2, 99, DamageSource::Gun,
            DamageDelta {
                health_before: 20, health_after: 0, tsv_before: 630,
                tsv_after: 0, applied_damage: 20, tsv_loss: 630, killed: true,
            },
            false,
        );
        let emission = rewards.record_hit(&hit, true, 30);
        assert!(emission.attacker.unwrap().amount > 0.0);
        assert!(emission.victim.unwrap().amount < 0.0);
        assert_eq!(rewards.persistent_tsv_loss(630, 30, true, true), 600);
        assert_eq!(rewards.persistent_tsv_loss(630, 30, true, false), 0);
    }
}
