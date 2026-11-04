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

pub const TSV_REWARD_SCALE: f32 = 0.1;
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

/// Per-episode shaping state, independent per faction. Both positive and
/// negative effects for a faction share their own cap across source types.
pub struct EpisodeShaping {
    sides: [i32; 2],
    combined_starting_tsv: i32,
    accumulators: [TacticalShaping; 2],
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
        })
    }

    pub fn reset(&mut self, combined_starting_tsv: i32) {
        self.combined_starting_tsv = combined_starting_tsv.max(1);
        self.accumulators = [TacticalShaping::default(); 2];
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
        (damage_tsv_loss - if killed { mined_cargo_before.max(0) } else { 0 })
            .max(0)
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
