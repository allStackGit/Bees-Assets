//! Contiguous, reusable inference boundary for the frozen v26 policy ABI.
//!
//! This is NOT an observation encoder or neural network. The authoritative
//! game world must produce every observation feature, and the model backend
//! must populate every action output. Unwritten values remain NaN/invalid,
//! not synthetic zero observations or silently accepted no-op actions.
//!
//! Worlds and agents retain insertion order, permitting a single GPU/CPU
//! inference batch without coupling bees-sim to a specific inference library.

use std::collections::HashSet;

use crate::perception::PerceptionError;

use crate::policy::{
    Action, ActionError, Decision, CONTINUOUS_ACTIONS, DISCRETE_BRANCHES,
    OBSERVATION_FLOATS, WEAPON_SLOTS,
};
use crate::simulation::{World, WorldId};

pub const SPECIAL_BRANCH: usize = WEAPON_SLOTS;
pub const SPECIAL_ACTIONS: usize = DISCRETE_BRANCHES[SPECIAL_BRANCH] as usize;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct PolicyAgentKey {
    pub world: WorldId,
    /// Exact episode revision at the moment observations were collected.
    pub episode: u64,
    /// Exact fixed tick. Results from an earlier decision cannot be applied
    /// when the same agent requests another decision later in the episode.
    pub tick: u64,
    pub agent: u64,
}

impl PolicyAgentKey {
    pub fn from_world<S>(world: &World<S>, agent: u64) -> Self {
        Self {
            world: world.id,
            episode: world.episode_revision(),
            tick: world.tick(),
            agent,
        }
    }

    /// The caller must check this before applying model outputs to a world.
    /// Inference completion is not guaranteed to preserve episode lifetime.
    pub fn is_current_for<S>(self, world: &World<S>) -> bool {
        self.world == world.id &&
            self.episode == world.episode_revision() &&
            self.tick == world.tick() &&
            !world.is_failed()
    }
}

/// Unity WriteDiscreteActionMask disables firing without a turret and masks
/// unavailable executable specials. "None" is always permitted.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PolicyActionMask {
    pub fire: [bool; WEAPON_SLOTS],
    pub special: [bool; SPECIAL_ACTIONS],
}

impl PolicyActionMask {
    pub fn valid(self) -> bool { self.special[0] }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PolicyBatchError {
    WrongObservationLength { actual: usize },
    Perception(PerceptionError),
    NonFiniteObservation { row: usize, feature: usize },
    DuplicateAgent(PolicyAgentKey),
    InvalidActionMask { row: usize },
    InvalidAction { row: usize, cause: ActionError },
    MaskedAction { row: usize, branch: usize, choice: u8 },
    CapacityOverflow,
    AllocationFailed,
}

/// Exact sizes for contiguous numeric inference buffers, excluding the
/// small agent-key/mask maps and inference backend's GPU allocations.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct PolicyBatchFootprint {
    pub agents: usize,
    pub observation_payload_bytes: usize,
    pub action_output_payload_bytes: usize,
    pub reserved_numeric_capacity_bytes: usize,
}

/// All arrays grow only when the maximum requested batch size grows.
/// Observation rows are row-major [agent][7743 features]; action outputs are
/// [agent][16 floats] and [agent][6 branch choices]. No per-row heap object.
#[derive(Default)]
pub struct PolicyBatch {
    keys: Vec<PolicyAgentKey>,
    seen: HashSet<PolicyAgentKey>,
    masks: Vec<PolicyActionMask>,
    observations: Vec<f32>,
    continuous: Vec<f32>,
    discrete: Vec<u8>,
}

impl PolicyBatch {
    pub fn new() -> Self { Self::default() }

    /// Preallocate once, before a high-throughput rollout. Does not change
    /// logical row count or allocate when existing capacity is sufficient.
    pub fn reserve_agents(&mut self, total: usize) -> Result<(), PolicyBatchError> {
        let observations = total.checked_mul(OBSERVATION_FLOATS)
            .ok_or(PolicyBatchError::CapacityOverflow)?;
        let continuous = total.checked_mul(CONTINUOUS_ACTIONS)
            .ok_or(PolicyBatchError::CapacityOverflow)?;
        let discrete = total.checked_mul(DISCRETE_BRANCHES.len())
            .ok_or(PolicyBatchError::CapacityOverflow)?;
        self.keys.try_reserve(total.saturating_sub(self.keys.len()))
            .map_err(|_| PolicyBatchError::AllocationFailed)?;
        self.seen.try_reserve(total.saturating_sub(self.seen.len()))
            .map_err(|_| PolicyBatchError::AllocationFailed)?;
        self.masks.try_reserve(total.saturating_sub(self.masks.len()))
            .map_err(|_| PolicyBatchError::AllocationFailed)?;
        self.observations.try_reserve(observations.saturating_sub(self.observations.len()))
            .map_err(|_| PolicyBatchError::AllocationFailed)?;
        self.continuous.try_reserve(continuous.saturating_sub(self.continuous.len()))
            .map_err(|_| PolicyBatchError::AllocationFailed)?;
        self.discrete.try_reserve(discrete.saturating_sub(self.discrete.len()))
            .map_err(|_| PolicyBatchError::AllocationFailed)?;
        Ok(())
    }

    /// Start a new inference decision boundary. All allocations are retained.
    pub fn clear(&mut self) {
        self.keys.clear();
        self.seen.clear();
        self.masks.clear();
        self.observations.clear();
        self.continuous.clear();
        self.discrete.clear();
    }

    pub fn len(&self) -> usize { self.keys.len() }
    pub fn is_empty(&self) -> bool { self.keys.is_empty() }
    pub fn keys(&self) -> &[PolicyAgentKey] { &self.keys }
    pub fn masks(&self) -> &[PolicyActionMask] { &self.masks }
    pub fn observations(&self) -> &[f32] { &self.observations }

    /// Read this at dashboard/export boundaries without introducing per-step
    /// histograms, allocation tracing or extra copies.
    pub fn footprint(&self) -> PolicyBatchFootprint {
        let f32_bytes = std::mem::size_of::<f32>();
        let observation_payload_bytes =
            self.observations.len() * f32_bytes;
        let action_output_payload_bytes =
            self.continuous.len() * f32_bytes + self.discrete.len();
        let reserved_numeric_capacity_bytes =
            self.observations.capacity() * f32_bytes +
            self.continuous.capacity() * f32_bytes +
            self.discrete.capacity();
        PolicyBatchFootprint {
            agents: self.keys.len(),
            observation_payload_bytes,
            action_output_payload_bytes,
            reserved_numeric_capacity_bytes,
        }
    }

    pub fn push_observation(
        &mut self,
        key: PolicyAgentKey,
        mask: PolicyActionMask,
        features: &[f32],
    ) -> Result<(), PolicyBatchError> {
        if features.len() != OBSERVATION_FLOATS {
            return Err(PolicyBatchError::WrongObservationLength {
                actual: features.len(),
            });
        }
        self.push_encoded(key, mask, |row| row.copy_from_slice(features))
    }

    /// Fill the destination row directly, avoiding an intermediate 7743-float
    /// vector/copy. Every slot starts as NaN, so partially written rows fail
    /// before inference; the caller must write the COMPLETE Unity observation.
    pub fn push_encoded(
        &mut self,
        key: PolicyAgentKey,
        mask: PolicyActionMask,
        encode: impl FnOnce(&mut [f32]),
    ) -> Result<(), PolicyBatchError> {
        self.push_encoded_result(key, mask, |row| {
            encode(row);
            Ok(())
        })
    }

    /// Source encoders can fail without panicking or leaving a partial batch
    /// row behind. This keeps full-game data validation separate from model
    /// execution; invalid observations never reach the GPU.
    pub fn push_encoded_result(
        &mut self,
        key: PolicyAgentKey,
        mask: PolicyActionMask,
        encode: impl FnOnce(&mut [f32]) -> Result<(), PolicyBatchError>,
    ) -> Result<(), PolicyBatchError> {
        let row = self.keys.len();
        if !mask.valid() {
            return Err(PolicyBatchError::InvalidActionMask { row });
        }
        if self.seen.contains(&key) {
            return Err(PolicyBatchError::DuplicateAgent(key));
        }
        let start = self.observations.len();
        let end = start.checked_add(OBSERVATION_FLOATS)
            .ok_or(PolicyBatchError::CapacityOverflow)?;
        self.observations.resize(end, f32::NAN);
        if let Err(error) = encode(&mut self.observations[start..end]) {
            self.observations.truncate(start);
            return Err(error);
        }
        if let Some(feature) = self.observations[start..end]
            .iter().position(|value| !value.is_finite()) {
            self.observations.truncate(start);
            return Err(PolicyBatchError::NonFiniteObservation { row, feature });
        }
        self.keys.push(key);
        self.seen.insert(key);
        self.masks.push(mask);
        // Invalid sentinels make stale or partially written inference
        // responses impossible to accidentally interpret as actions.
        self.continuous.extend(std::iter::repeat(f32::NAN)
            .take(CONTINUOUS_ACTIONS));
        self.discrete.extend(std::iter::repeat(u8::MAX)
            .take(DISCRETE_BRANCHES.len()));
        Ok(())
    }

    /// The inference implementation can write into these contiguous arrays
    /// directly, including through a platform-specific accelerator adapter.
    /// No GPU, ROCm, CUDA, ONNX or operating-system dependency belongs here.
    pub fn output_buffers_mut(&mut self) -> (&mut [f32], &mut [u8]) {
        (self.continuous.as_mut_slice(), self.discrete.as_mut_slice())
    }

    /// Zero-copy inference input/output handoff. Split field borrows
    /// permit an accelerator model to read the frozen observation batch
    /// while writing both contiguous action arrays in the same call.
    pub fn inference_buffers_mut(
        &mut self,
    ) -> (&[f32], &mut [f32], &mut [u8]) {
        (
            self.observations.as_slice(),
            self.continuous.as_mut_slice(),
            self.discrete.as_mut_slice(),
        )
    }

    /// Validate every model result before publishing ANY decisions. This
    /// checks both the frozen action ABI and Unity's live discrete masks.
    pub fn validate_outputs(&self) -> Result<(), PolicyBatchError> {
        for (row, mask) in self.masks.iter().enumerate() {
            let mut action = Action::ZERO;
            let first_cont = row * CONTINUOUS_ACTIONS;
            let first_disc = row * DISCRETE_BRANCHES.len();
            action.continuous.copy_from_slice(
                &self.continuous[first_cont..first_cont + CONTINUOUS_ACTIONS],
            );
            action.discrete.copy_from_slice(
                &self.discrete[first_disc..first_disc + DISCRETE_BRANCHES.len()],
            );
            action.validate().map_err(|cause|
                PolicyBatchError::InvalidAction { row, cause })?;
            for slot in 0..WEAPON_SLOTS {
                if action.discrete[slot] == 1 && !mask.fire[slot] {
                    return Err(PolicyBatchError::MaskedAction {
                        row, branch: slot, choice: 1,
                    });
                }
            }
            let special = action.discrete[SPECIAL_BRANCH];
            if !mask.special[usize::from(special)] {
                return Err(PolicyBatchError::MaskedAction {
                    row, branch: SPECIAL_BRANCH, choice: special,
                });
            }
        }
        Ok(())
    }

    /// The output vector is unchanged on validation failure. Results stay in
    /// insertion order, with explicit world+agent identity for dispatch.
    pub fn collect_decisions(
        &self,
        output: &mut Vec<(PolicyAgentKey, Decision)>,
    ) -> Result<(), PolicyBatchError> {
        self.validate_outputs()?;
        output.clear();
        output.reserve(self.keys.len());
        for (row, &key) in self.keys.iter().enumerate() {
            let mut action = Action::ZERO;
            let first_cont = row * CONTINUOUS_ACTIONS;
            let first_disc = row * DISCRETE_BRANCHES.len();
            action.continuous.copy_from_slice(
                &self.continuous[first_cont..first_cont + CONTINUOUS_ACTIONS],
            );
            action.discrete.copy_from_slice(
                &self.discrete[first_disc..first_disc + DISCRETE_BRANCHES.len()],
            );
            output.push((key, Decision { agent_id: key.agent, action }));
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn complete_observations_and_masked_actions_are_required() {
        let mut batch = PolicyBatch::new();
        batch.reserve_agents(2).unwrap();
        let key = PolicyAgentKey {
            world: WorldId(3), episode: 0, tick: 0, agent: 9,
        };
        let mask = PolicyActionMask {
            fire: [true, false, false, false, false],
            special: [true, false, false, false, false],
        };
        assert!(matches!(
            batch.push_encoded(key, mask, |row| row[0] = 1.0),
            Err(PolicyBatchError::NonFiniteObservation { feature: 1, .. })
        ));
        assert!(batch.is_empty());
        batch.push_encoded(key, mask, |row| row.fill(0.0)).unwrap();
        assert_eq!(batch.observations().len(), OBSERVATION_FLOATS);
        let footprint = batch.footprint();
        assert_eq!(footprint.agents, 1);
        assert_eq!(footprint.observation_payload_bytes, OBSERVATION_FLOATS * 4);
        assert_eq!(footprint.action_output_payload_bytes,
            CONTINUOUS_ACTIONS * 4 + DISCRETE_BRANCHES.len());
        assert!(footprint.reserved_numeric_capacity_bytes >=
            footprint.observation_payload_bytes +
            footprint.action_output_payload_bytes);
        assert!(matches!(
            batch.push_encoded(key, mask, |row| row.fill(0.0)),
            Err(PolicyBatchError::DuplicateAgent(_))
        ));
        assert!(batch.validate_outputs().is_err());
        let (continuous, discrete) = batch.output_buffers_mut();
        continuous.fill(0.0);
        discrete.fill(0);
        discrete[1] = 1;
        assert!(matches!(
            batch.validate_outputs(),
            Err(PolicyBatchError::MaskedAction { branch: 1, .. })
        ));
        batch.output_buffers_mut().1[1] = 0;
        let mut output = Vec::new();
        batch.collect_decisions(&mut output).unwrap();
        assert_eq!(output.len(), 1);
        assert_eq!(output[0].0, key);
        assert_eq!(output[0].1.agent_id, 9);
        batch.clear();
        assert!(batch.is_empty());
    }

    #[test]
    fn delayed_decision_cannot_cross_episode_or_tick_boundaries() {
        let mut world = World::new(WorldId(42), 123, ());
        let old = PolicyAgentKey::from_world(&world, 8);
        assert!(old.is_current_for(&world));
        world.reset_episode(456, ());
        assert!(!old.is_current_for(&world));
        let new_key = PolicyAgentKey::from_world(&world, 8);
        assert_ne!(old, new_key);
        assert!(new_key.is_current_for(&world));
        let another_world = World::new(WorldId(43), 456, ());
        assert!(!new_key.is_current_for(&another_world));
    }

    #[test]
    fn independent_worlds_keep_distinct_agent_keys() {
        let mut batch = PolicyBatch::new();
        let mask = PolicyActionMask {
            fire: [false; WEAPON_SLOTS],
            special: [true, false, false, false, false],
        };
        for world in [WorldId(1), WorldId(2)] {
            batch.push_encoded(PolicyAgentKey {
                world, episode: 0, tick: 0, agent: 17,
            },
                mask, |row| row.fill(0.0)).unwrap();
        }
        assert_eq!(batch.len(), 2);
        assert_eq!(batch.observations().len(), OBSERVATION_FLOATS * 2);
        let (continuous, discrete) = batch.output_buffers_mut();
        continuous.fill(0.0);
        discrete.fill(0);
        let mut decisions = Vec::new();
        batch.collect_decisions(&mut decisions).unwrap();
        assert_eq!(decisions[0].0.world, WorldId(1));
        assert_eq!(decisions[1].0.world, WorldId(2));
    }
}
