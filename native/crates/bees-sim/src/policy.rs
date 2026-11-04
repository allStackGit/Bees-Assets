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
