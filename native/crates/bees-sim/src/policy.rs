//! Frozen v26 agent-facing shape from `RlPolicySchema.cs` and
//! `RlOneVsOneAgent.cs` on the authoritative experiment branch.
//! Numerical contents and action semantics are not ported yet.

pub const BEHAVIOR_NAME: &str = "BeesRL1v1";
pub const ABI_VERSION: u32 = 26;
pub const OBSERVATION_FLOATS: usize = 7_743;
pub const CONTINUOUS_ACTIONS: usize = 16;
pub const DISCRETE_BRANCHES: [u8; 6] = [2, 2, 2, 2, 2, 5];
pub const WEAPON_SLOTS: usize = 5;
pub const COMMUNICATION_ACTION_OFFSET: usize = 12;

/// Frozen observation tail from Scripts/Scenes/RlPolicySchema.cs.
pub const PERCEPTION_OBSERVATION_FLOATS: usize = 7_722;
pub const EPISODE_PROGRESS_INDEX: usize = PERCEPTION_OBSERVATION_FLOATS;
pub const FACTION_OBSERVATION_INDEX: usize = EPISODE_PROGRESS_INDEX + 1;
pub const RESERVED_OBSERVATION_START: usize = FACTION_OBSERVATION_INDEX + 1;
pub const RESERVED_OBSERVATION_FLOATS: usize = 19;

/// Exact RlPolicySchema.Signature: the model must match semantics as well as
/// array dimensions. A different network with the same tensor shape is not
/// necessarily a compatible policy.
pub const POLICY_SIGNATURE: &str = concat!(
    "bees-rl-v26|behavior=BeesRL1v1|trainer=ma-poca|network=structured-shared-faction-conditioned-layernorm-silu-128x3|normalize=true|obs=7743|tail=episode-progress+faction+19-reserved|cont=16|disc=2x5,5|",
    "entity-encoder=shared-attention96|slot-normalization=raw-bounded-shared|weapon-encoder=shared-common32-slot64|weapon-head=shared-per-slot-faction-conditioned|team-gradient=inverse-active-group|",
    "coord-frame=team-episode-distinct-quarter-turn|weapon-aim=slotwise-xy|weapon-fire=slotwise-cease-or-fire|healing=weapon-exclusive|special-mask=executable-only|weapon-ready=rl-latched-until-fire|",
    "shiptype=fixed-scrambled-scalar24|weapontype=fixed-scrambled-scalar10|mapbits=4|shipmap=v1-0..23|weaponmap=v1-0..9|",
    "allies=64|enemies=64|weapons=5|entity-weapons=5|enemy-mounts=0|mining=8|map-objects=64|moving-asteroids=48|",
    "self=25|ship-id=episode-permuted-scalar23|capability=12-with-self-and-team-mined-value|parent-carrier=41|entity-core=15-with-mined-value|entity=41|ally=45-with-private-comm4|communication=4-continuous-private-allied|self-weapon=15|observed-weapon=5|weapon-observation=split-self-vs-observed|mining-slot=7|",
    "map-slot=12|moving-asteroid-slot=11|objective=16|grid=21x21-cell6|exploration-grid=16x16-team-shared-sight-recency|entity-order=distance,type,fleet-id,runtime-id|heading=physical-forward-negative-sin"
);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PolicyAbiError {
    VersionMismatch { actual: u32 },
    SignatureMismatch,
}

pub fn validate_model_abi(
    version: u32,
    signature: &str,
) -> Result<(), PolicyAbiError> {
    if version != ABI_VERSION {
        return Err(PolicyAbiError::VersionMismatch { actual: version });
    }
    if signature != POLICY_SIGNATURE {
        return Err(PolicyAbiError::SignatureMismatch);
    }
    Ok(())
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ObservationTailError {
    WrongLength { actual: usize },
    NonFiniteProgress,
    InvalidFaction,
}

/// Unity RlOneVsOneAgent.CollectPolicyObservations appends episode progress,
/// the signed faction discriminator, then nineteen literal zeroes. This
/// writes only the tail; the 7722 perception features MUST come from the
/// authoritative RlCombatPerception port, never a placeholder.
pub fn write_active_observation_tail(
    observation: &mut [f32],
    normalized_progress: f32,
    is_bee_side: bool,
) -> Result<(), ObservationTailError> {
    if observation.len() != OBSERVATION_FLOATS {
        return Err(ObservationTailError::WrongLength {
            actual: observation.len(),
        });
    }
    if !normalized_progress.is_finite() {
        return Err(ObservationTailError::NonFiniteProgress);
    }
    observation[EPISODE_PROGRESS_INDEX] = normalized_progress;
    observation[FACTION_OBSERVATION_INDEX] =
        if is_bee_side { 1.0 } else { -1.0 };
    observation[RESERVED_OBSERVATION_START..].fill(0.0);
    Ok(())
}

/// Unity's inactive MA-POCA observation is zero everywhere except the
/// faction discriminator. A zero faction is used only when configuration
/// is unavailable, exactly as in AddInactivePolicyObservations.
pub fn write_inactive_observation(
    observation: &mut [f32],
    faction: f32,
) -> Result<(), ObservationTailError> {
    if observation.len() != OBSERVATION_FLOATS {
        return Err(ObservationTailError::WrongLength {
            actual: observation.len(),
        });
    }
    if faction != 1.0 && faction != -1.0 && faction != 0.0 {
        return Err(ObservationTailError::InvalidFaction);
    }
    observation.fill(0.0);
    observation[FACTION_OBSERVATION_INDEX] = faction;
    Ok(())
}


/// Canonical transport shape. Constructed on the stack only at boundaries;
/// future rollout batches should use contiguous, reusable buffers.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Action {
    pub continuous: [f32; CONTINUOUS_ACTIONS],
    pub discrete: [u8; DISCRETE_BRANCHES.len()],
}

impl Action {
    pub const ZERO: Self = Self {
        continuous: [0.0; CONTINUOUS_ACTIONS],
        discrete: [0; DISCRETE_BRANCHES.len()],
    };

    /// Validate data entering a simulation, not on every physics update.
    pub fn validate(&self) -> Result<(), ActionError> {
        for (index, value) in self.continuous.iter().enumerate() {
            if !value.is_finite() {
                return Err(ActionError::NonFinite { index });
            }
        }
        for (index, (value, count)) in self.discrete
            .iter()
            .zip(DISCRETE_BRANCHES)
            .enumerate()
        {
            if *value >= count {
                return Err(ActionError::DiscreteOutOfRange { index, value: *value, count });
            }
        }
        Ok(())
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ActionError {
    NonFinite { index: usize },
    DiscreteOutOfRange { index: usize, value: u8, count: u8 },
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Decision {
    pub agent_id: u64,
    pub action: Action,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn active_and_inactive_observation_tails_match_v26_contract() {
        assert_eq!(RESERVED_OBSERVATION_START + RESERVED_OBSERVATION_FLOATS,
            OBSERVATION_FLOATS);
        let mut observation = [f32::NAN; OBSERVATION_FLOATS];
        write_active_observation_tail(&mut observation, 0.75, true).unwrap();
        assert!(observation[..PERCEPTION_OBSERVATION_FLOATS]
            .iter().all(|value| value.is_nan()));
        assert_eq!(observation[EPISODE_PROGRESS_INDEX], 0.75);
        assert_eq!(observation[FACTION_OBSERVATION_INDEX], 1.0);
        assert!(observation[RESERVED_OBSERVATION_START..]
            .iter().all(|value| *value == 0.0));
        write_inactive_observation(&mut observation, -1.0).unwrap();
        assert!(observation.iter().enumerate().all(|(index, &value)|
            value == if index == FACTION_OBSERVATION_INDEX { -1.0 } else { 0.0 }
        ));
    }

    #[test]
    fn model_signature_must_match_frozen_v26_identity() {
        assert_eq!(validate_model_abi(ABI_VERSION, POLICY_SIGNATURE), Ok(()));
        assert_eq!(validate_model_abi(ABI_VERSION - 1, POLICY_SIGNATURE),
            Err(PolicyAbiError::VersionMismatch { actual: ABI_VERSION - 1 }));
        assert_eq!(validate_model_abi(ABI_VERSION, "shape-only compatibility"),
            Err(PolicyAbiError::SignatureMismatch));
    }
}
