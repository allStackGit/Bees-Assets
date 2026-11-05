//! Native port of Training/bees_mlagents_ppo_compat.py's
//! _build_bees_policy_dimension_mask and _bees_movement_activity.
//!
//! This is the source PPO *activity* contract, not a policy, critic,
//! PPO optimizer or a substitute for group observations. The output
//! contains 16 continuous and 6 discrete weights, in the frozen action
//! ABI order. Callers must attach it to the exact sampled action row.

use crate::policy::{
    Action, ActionError, CONTINUOUS_ACTIONS, DISCRETE_BRANCHES,
    OBSERVATION_FLOATS, WEAPON_SLOTS,
};
use crate::policy_batch::{PolicyActionMask, SPECIAL_BRANCH};

pub const PPO_ACTION_DIMENSIONS: usize =
    CONTINUOUS_ACTIONS + DISCRETE_BRANCHES.len();
const SELF_SHIP_TYPE_INDEX: usize = 1;
const SELF_MOBILE_INDEX: usize = 16;
const SPECIAL_PHASE_INDEX: usize = 25 + 4;
const BARGE_SHIP_SCALAR: f32 = -1.0 / 23.0;
const HEALING_ACTION: u8 = 3;
const AIM_START: usize = 2;
const COMMUNICATION_START: usize = AIM_START + WEAPON_SLOTS * 2;
const DISCRETE_START: usize = CONTINUOUS_ACTIONS;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PolicyDimensionError {
    WrongObservationLength,
    InvalidObservation,
    InvalidMask,
    InvalidAction(ActionError),
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PolicyDimensionActivity {
    /// Nonzero action log-probability and entropy dimensions. This
    /// is a per-row optimization weight, NOT the discrete sampling
    /// mask used by the model's categorical distribution.
    pub weights: [f32; PPO_ACTION_DIMENSIONS],
}

/// `groupmate_count` is the actual number of other live MA-POCA agents
/// in this sample's active group. None preserves the source PPO-only
/// fallback where communication activity was not provided.
pub fn policy_dimension_activity(
    observation: &[f32],
    action_mask: PolicyActionMask,
    sampled_action: &Action,
    groupmate_count: Option<u32>,
) -> Result<PolicyDimensionActivity, PolicyDimensionError> {
    if observation.len() != OBSERVATION_FLOATS {
        return Err(PolicyDimensionError::WrongObservationLength);
    }
    if ![
        observation[SELF_SHIP_TYPE_INDEX],
        observation[SELF_MOBILE_INDEX],
        observation[SPECIAL_PHASE_INDEX],
    ].iter().all(|value| value.is_finite()) {
        return Err(PolicyDimensionError::InvalidObservation);
    }
    if !action_mask.valid() {
        return Err(PolicyDimensionError::InvalidMask);
    }
    sampled_action.validate().map_err(PolicyDimensionError::InvalidAction)?;
    if !action_mask.special[usize::from(
        sampled_action.discrete[SPECIAL_BRANCH],
    )] || (0..WEAPON_SLOTS).any(|slot|
        sampled_action.discrete[slot] == 1 && !action_mask.fire[slot]
    ) {
        return Err(PolicyDimensionError::InvalidMask);
    }
    let mut weights = [1.0; PPO_ACTION_DIMENSIONS];

    // Unity's CanMove and Barge charge phases 1..3 are independent
    // from an agent being present in the shared policy batch.
    let is_barge = (observation[SELF_SHIP_TYPE_INDEX] -
        BARGE_SHIP_SCALAR).abs() <= 1.0e-5;
    let movement_active = observation[SELF_MOBILE_INDEX] > 0.5 &&
        !(is_barge && observation[SPECIAL_PHASE_INDEX] > 1.0e-6);
    weights[0] = f32::from(u8::from(movement_active));
    weights[1] = weights[0];

    let suppress_fire = sampled_action.discrete[SPECIAL_BRANCH] ==
        HEALING_ACTION;
    for slot in 0..WEAPON_SLOTS {
        let active = action_mask.fire[slot];
        let value = f32::from(u8::from(active));
        weights[AIM_START + 2 * slot] = value;
        weights[AIM_START + 2 * slot + 1] = value;
        // Healing still permits aim gradients but disables actual fire
        // requests, matching the source's fire_effective mask.
        weights[DISCRETE_START + slot] =
            if suppress_fire { 0.0 } else { value };
    }

    // The source's POCA groupmate-count filter affects communication
    // only. A non-POCA PPO optimizer supplies None and retains ones.
    if let Some(mates) = groupmate_count {
        if mates == 0 {
            weights[COMMUNICATION_START..CONTINUOUS_ACTIONS].fill(0.0);
        }
    }
    // A special branch with only "none" available has zero gradient.
    weights[DISCRETE_START + SPECIAL_BRANCH] = f32::from(u8::from(
        action_mask.special[1..].iter().any(|&enabled| enabled),
    ));

    Ok(PolicyDimensionActivity { weights })
}

/// Source _poca_inverse_group_size_weight_array: distribute one active
/// group-timestep's policy weight equally across participating agents.
/// Padded, invalid rollout steps have zero weight. The caller supplies
/// the actual POCA group size minus this particular agent.
#[inline]
pub fn poca_inverse_group_sample_weight(
    groupmate_count: u32,
    valid_timestep: bool,
) -> f32 {
    if valid_timestep {
        1.0 / (groupmate_count as f32 + 1.0)
    } else {
        0.0
    }
}


/// Exact _normalize_poca_advantages input contract: one sampled advantage,
/// live POCA groupmate count and rollout validity flag per row. Numerical
/// accumulation uses f64 to reduce cancellation in large minibatches;
/// the normalized results remain f32 as in the active Python trainer.
///
/// These are REAL trainer-computed advantages, not estimates inferred from
/// native simulation rewards or policy actions.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PocaAdvantageError {
    WrongLengths {
        advantages: usize,
        groupmate_counts: usize,
        valid_timesteps: usize,
    },
    NonFiniteAdvantage { row: usize },
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct PocaAdvantageNormalization {
    /// Sum of 1 / (1 + live groupmate count) over valid rollout rows.
    /// Useful as the effective group-timestep sample weight.
    pub active_weight: f64,
    pub weighted_mean: f64,
    pub weighted_std_dev: f64,
}

/// Source _normalize_poca_advantages: standardize the entire minibatch
/// using only valid, inverse-group-size-weighted POCA observations.
/// Invalid/padded rows do not influence the moments; if any valid rows
/// exist they are still transformed (their PPO loss mask excludes them).
///
/// Two read-only passes and one in-place pass; no per-sample allocation.
/// Invalid lengths or nonfinite inputs leave the caller's buffer untouched.
pub fn poca_normalize_advantages(
    advantages: &mut [f32],
    groupmate_counts: &[u32],
    valid_timesteps: &[bool],
) -> Result<PocaAdvantageNormalization, PocaAdvantageError> {
    let len = advantages.len();
    if groupmate_counts.len() != len || valid_timesteps.len() != len {
        return Err(PocaAdvantageError::WrongLengths {
            advantages: len,
            groupmate_counts: groupmate_counts.len(),
            valid_timesteps: valid_timesteps.len(),
        });
    }
    for (row, &advantage) in advantages.iter().enumerate() {
        if !advantage.is_finite() {
            return Err(PocaAdvantageError::NonFiniteAdvantage { row });
        }
    }
    let mut total_weight = 0.0_f64;
    let mut weighted_sum = 0.0_f64;
    for ((&advantage, &mates), &valid) in advantages.iter()
        .zip(groupmate_counts).zip(valid_timesteps) {
        if valid {
            let weight = 1.0 / (f64::from(mates) + 1.0);
            total_weight += weight;
            weighted_sum += weight * f64::from(advantage);
        }
    }
    if total_weight == 0.0 {
        advantages.fill(0.0);
        return Ok(PocaAdvantageNormalization::default());
    }
    let mean = weighted_sum / total_weight;
    let mut weighted_square_deviation = 0.0_f64;
    for ((&advantage, &mates), &valid) in advantages.iter()
        .zip(groupmate_counts).zip(valid_timesteps) {
        if valid {
            let weight = 1.0 / (f64::from(mates) + 1.0);
            let centered = f64::from(advantage) - mean;
            weighted_square_deviation += weight * centered * centered;
        }
    }
    let std_dev = (weighted_square_deviation / total_weight).max(0.0).sqrt();
    let denominator = std_dev + 1.0e-10;
    for advantage in advantages.iter_mut() {
        *advantage = ((f64::from(*advantage) - mean) / denominator) as f32;
    }
    Ok(PocaAdvantageNormalization {
        active_weight: total_weight,
        weighted_mean: mean,
        weighted_std_dev: std_dev,
    })
}
