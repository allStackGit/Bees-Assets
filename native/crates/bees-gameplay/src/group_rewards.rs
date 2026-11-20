//! One source-defined MA-POCA group reward stream per Level side.
//! Sources: RlOneVsOneAgent.IsGroupRewardRepresentative,
//! HandleTsvRewardOccurred / HandleEconomicRewardOccurred / HandleEpisodeEnded
//! and GetExplicitOutcomeMarker.
//!
//! Not an ML-Agents implementation: the native runner/learner transport
//! consumes these events, strips Elo markers from PPO reward, and completes
//! trajectories. No per-ship duplicate rewards are emitted.

use std::collections::BTreeSet;

use bees_sim::rl_shaping::RewardEmission;

use crate::episode::NativeEpisodeFinal;

const ELO_WIN: f32 = 4096.0;
const ELO_DRAW: f32 = 8192.0;
const ELO_LOSS: f32 = -4096.0;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GroupRewardError {
    InvalidSides,
    InvalidEpisode,
    InvalidAgent,
    UnknownSide,
    DuplicateAgent,
    AlreadyComplete,
    InvalidReward,
    TickExhausted,
    MissingOrRepeatedTick { expected: u64, actual: u64 },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub enum GroupRewardKind {
    /// Real tactical/economic shaping already normalized by the coordinator.
    Immediate,
    /// Transport-only self-play/ELO marker: NEVER PPO reward.
    EloMarker,
    /// Real terminal, time and retained-mining reward.
    Terminal,
    EndEpisode,
}

#[derive(Clone, Copy, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub struct GroupRewardEvent {
    pub side: i32,
    pub team_id: u8,
    pub episode_number: u32,
    pub kind: GroupRewardKind,
    pub amount: f32,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub struct GroupRewardMeasurements {
    pub registered_agents: u64,
    pub unregistered_agents: u64,
    pub group_reward_events: u64,
    pub terminal_groups: u64,
    pub skipped_without_participant: u64,
}

/// Source's alternating team indices: odd episodes give Bees team 0,
/// even episodes give Bees team 1. Human team is the opposite.
pub fn team_id_for_side(
    bee_side: i32, human_side: i32, episode_number: u32, side: i32,
) -> Result<u8, GroupRewardError> {
    if bee_side <= 0 || human_side <= 0 || bee_side == human_side {
        return Err(GroupRewardError::InvalidSides);
    }
    if episode_number == 0 {
        return Err(GroupRewardError::InvalidEpisode);
    }
    let bee_team = if episode_number % 2 == 1 { 0 } else { 1 };
    if side == bee_side {
        Ok(bee_team)
    } else if side == human_side {
        Ok(1 - bee_team)
    } else {
        Err(GroupRewardError::UnknownSide)
    }
}

/// The group membership and delivery-clock owner at a committed Level tick.
/// Sparse reward notifications remain in NativeLevelEventProcessor's
/// acknowledgement outbox; this is not a second delivery journal.
#[derive(serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NativeGroupRewardCheckpoint {
    schema_version: u32,
    sides: [i32; 2],
    episode_number: u32,
    participants: [BTreeSet<u64>; 2],
    next_tick: u64,
    completed: bool,
    measurements: GroupRewardMeasurements,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeGroupRewardRestoreError {
    InvalidVersion,
    WrongEpisode,
    InvalidClock,
    InvalidParticipants,
    InvalidMeasurements,
}

pub struct NativeGroupRewardDelivery {
    sides: [i32; 2],
    teams: [u8; 2],
    episode_number: u32,
    participants: [BTreeSet<u64>; 2],
    next_tick: u64,
    completed: bool,
    events: Vec<GroupRewardEvent>,
    measurements: GroupRewardMeasurements,
}

impl NativeGroupRewardDelivery {
    pub fn new(
        bee_side: i32, human_side: i32, episode_number: u32,
    ) -> Result<Self, GroupRewardError> {
        let bee_team = team_id_for_side(
            bee_side, human_side, episode_number, bee_side,
        )?;
        Ok(Self {
            sides: [bee_side, human_side],
            teams: [bee_team, 1 - bee_team],
            episode_number,
            participants: [BTreeSet::new(), BTreeSet::new()],
            next_tick: 1,
            completed: false,
            events: Vec::new(),
            measurements: GroupRewardMeasurements::default(),
        })
    }

    /// Capture exact group participation, team assignment identity and
    /// next expected fixed tick. The last consumed tick's events are
    /// transient scratch already copied into the Level's durable outbox.
    pub fn checkpoint(&self) -> NativeGroupRewardCheckpoint {
        NativeGroupRewardCheckpoint {
            schema_version: 1,
            sides: self.sides,
            episode_number: self.episode_number,
            participants: self.participants.clone(),
            next_tick: self.next_tick,
            completed: self.completed,
            measurements: self.measurements,
        }
    }

    /// Preflight group recovery before the outer Level commits any owner.
    /// Both rendered gameplay and headless rollouts share this contract:
    /// the exact episode/team seed and completed fixed-step clock must
    /// match the Level, never be guessed from the recovered participant IDs.
    pub fn prepare_restore(
        &self,
        checkpoint: NativeGroupRewardCheckpoint,
        completed_ticks: u64,
    ) -> Result<Self, NativeGroupRewardRestoreError> {
        use NativeGroupRewardRestoreError as Error;
        if checkpoint.schema_version != 1 {
            return Err(Error::InvalidVersion);
        }
        if checkpoint.sides != self.sides ||
            checkpoint.episode_number != self.episode_number {
            return Err(Error::WrongEpisode);
        }
        if completed_ticks.checked_add(1) != Some(checkpoint.next_tick) ||
            (!checkpoint.completed && checkpoint.next_tick == 0) ||
            (checkpoint.completed && completed_ticks == 0) {
            return Err(Error::InvalidClock);
        }
        // Source registration is scoped to a side, so the same external
        // agent number is not inherently invalid on opposing teams.
        if checkpoint.participants.iter().any(|group| group.contains(&0)) {
            return Err(Error::InvalidParticipants);
        }
        let active_count = checkpoint.participants.iter().map(|group|
            group.len() as u64
        ).sum::<u64>();
        if checkpoint.measurements.registered_agents < active_count ||
            checkpoint.measurements.unregistered_agents >
                checkpoint.measurements.registered_agents ||
            checkpoint.measurements.terminal_groups > 2 ||
            (!checkpoint.completed && checkpoint.measurements.terminal_groups != 0) {
            return Err(Error::InvalidMeasurements);
        }
        Ok(Self {
            sides: self.sides,
            teams: self.teams,
            episode_number: self.episode_number,
            participants: checkpoint.participants,
            next_tick: checkpoint.next_tick,
            completed: checkpoint.completed,
            // Group events are already published as Level notifications;
            // replaying this one-tick scratch would pay a reward twice.
            events: Vec::new(),
            measurements: checkpoint.measurements,
        })
    }

    /// Atomic owner replacement after the Level's other recovery ledgers
    /// have been validated; failed preflight preserves the live group.
    pub fn restore_checkpoint(
        &mut self,
        checkpoint: NativeGroupRewardCheckpoint,
        completed_ticks: u64,
    ) -> Result<(), NativeGroupRewardRestoreError> {
        let prepared = self.prepare_restore(checkpoint, completed_ticks)?;
        *self = prepared;
        Ok(())
    }

    pub fn measurements(&self) -> GroupRewardMeasurements {
        self.measurements
    }

    pub fn events(&self) -> &[GroupRewardEvent] {
        &self.events
    }

    pub fn completed(&self) -> bool {
        self.completed
    }

    pub fn sides(&self) -> [i32; 2] {
        self.sides
    }

    pub fn team_id(&self, side: i32) -> Option<u8> {
        self.sides.iter().position(|&value| value == side)
            .map(|index| self.teams[index])
    }

    fn side_index(&self, side: i32) -> Result<usize, GroupRewardError> {
        self.sides.iter().position(|&x| x == side)
            .ok_or(GroupRewardError::UnknownSide)
    }

    /// Registration is agent participation, not one temporary ship bind.
    /// A dead ship may leave its participating controller in the group
    /// until the side's terminal episode has been delivered.
    pub fn register_agent(
        &mut self, side: i32, agent: u64,
    ) -> Result<(), GroupRewardError> {
        if self.completed { return Err(GroupRewardError::AlreadyComplete) }
        if agent == 0 { return Err(GroupRewardError::InvalidAgent) }
        let index = self.side_index(side)?;
        if !self.participants[index].insert(agent) {
            return Err(GroupRewardError::DuplicateAgent);
        }
        self.measurements.registered_agents += 1;
        Ok(())
    }

    /// Snapshot the actual active group at an episode boundary. Authored
    /// future-wave reservations are deliberately absent; the journal
    /// records their real enrollment at physical deployment instead.
    pub fn participants_by_team(&self) -> std::collections::BTreeMap<u8, BTreeSet<u64>> {
        self.teams.into_iter().zip(self.participants.iter().cloned()).collect()
    }

    /// Constant-time membership check for the event journal's physical
    /// admission receipt. No fabricated agent or team is accepted.
    pub fn is_registered(&self, side: i32, agent: u64) -> bool {
        self.sides.iter().position(|&value| value == side)
            .is_some_and(|index| self.participants[index].contains(&agent))
    }

    pub fn unregister_agent(
        &mut self, side: i32, agent: u64,
    ) -> Result<bool, GroupRewardError> {
        let index = self.side_index(side)?;
        let removed = self.participants[index].remove(&agent);
        self.measurements.unregistered_agents += u64::from(removed);
        Ok(removed)
    }

    fn emit(&mut self, side_index: usize, kind: GroupRewardKind, amount: f32) {
        if self.participants[side_index].is_empty() {
            self.measurements.skipped_without_participant += 1;
            return;
        }
        self.events.push(GroupRewardEvent {
            side: self.sides[side_index],
            team_id: self.teams[side_index],
            episode_number: self.episode_number,
            kind, amount,
        });
        self.measurements.group_reward_events += 1;
    }

    /// Consume each authoritative completed Level tick ONCE. This
    /// includes terminal records in their exact Unity order: one ELO
    /// marker, then real terminal reward, then EndGroupEpisode.
    /// No native reward is duplicated per participating ship/agent.
    pub fn consume_tick(
        &mut self,
        completed_tick: u64,
        emissions: &[RewardEmission],
        finalized: Option<NativeEpisodeFinal>,
    ) -> Result<&[GroupRewardEvent], GroupRewardError> {
        if self.completed { return Err(GroupRewardError::AlreadyComplete) }
        if completed_tick != self.next_tick {
            return Err(GroupRewardError::MissingOrRepeatedTick {
                expected: self.next_tick, actual: completed_tick,
            });
        }
        // Validate the complete side stream before emitting a partial
        // batch; the owning Level discards a failed source frame.
        for emission in emissions {
            self.side_index(emission.side)?;
            if !emission.amount.is_finite() {
                return Err(GroupRewardError::InvalidReward);
            }
        }
        if let Some(done) = finalized {
            if ![0, self.sides[0], self.sides[1]]
                .contains(&done.result.winner_side) ||
                !done.result.duration_seconds.is_finite() ||
                done.result.duration_seconds < 0.0 ||
                [
                    done.terminal_rewards.bee_terminal,
                    done.terminal_rewards.human_terminal,
                    done.terminal_rewards.bee_time,
                    done.terminal_rewards.human_time,
                    done.economic_rewards.bee_retained_reward,
                    done.economic_rewards.human_retained_reward,
                ].iter().any(|value| !value.is_finite())
            {
                return Err(GroupRewardError::InvalidReward);
            }
        }
        // Exhaustion is checked before side effects, not after group
        // events are queued or a terminal state is committed.
        let next_tick = self.next_tick.checked_add(1)
            .ok_or(GroupRewardError::TickExhausted)?;
        self.events.clear();
        for emission in emissions {
            let index = self.side_index(emission.side)?;
            self.emit(index, GroupRewardKind::Immediate, emission.amount);
        }
        if let Some(done) = finalized {
            for index in 0..2 {
                let side = self.sides[index];
                let marker = if done.result.timed_out {
                    ELO_LOSS
                } else if done.result.winner_side == 0 {
                    ELO_DRAW
                } else if done.result.winner_side == side {
                    ELO_WIN
                } else {
                    ELO_LOSS
                };
                let terminal = if index == 0 {
                    done.terminal_rewards.bee_terminal +
                    done.terminal_rewards.bee_time +
                    done.economic_rewards.bee_retained_reward
                } else {
                    done.terminal_rewards.human_terminal +
                    done.terminal_rewards.human_time +
                    done.economic_rewards.human_retained_reward
                };
                if !self.participants[index].is_empty() {
                    self.emit(index, GroupRewardKind::EloMarker, marker);
                    self.emit(index, GroupRewardKind::Terminal, terminal);
                    self.emit(index, GroupRewardKind::EndEpisode, 0.0);
                    self.measurements.terminal_groups += 1;
                }
            }
            self.completed = true;
        }
        self.next_tick = next_tick;
        Ok(&self.events)
    }

    /// Discoveries and other source callbacks can occur between fixed
    /// steps, possibly multiple times before one physics tick. Return
    /// exactly the *new* event by value so a prior unconsumed discovery
    /// cannot be overwritten or sent a second time. The native transport
    /// dispatches this event immediately, as Unity's group callback does.
    pub fn immediate_event(
        &mut self, emission: RewardEmission,
    ) -> Result<Option<GroupRewardEvent>, GroupRewardError> {
        if self.completed { return Err(GroupRewardError::AlreadyComplete) }
        if !emission.amount.is_finite() {
            return Err(GroupRewardError::InvalidReward);
        }
        let index = self.side_index(emission.side)?;
        if self.participants[index].is_empty() {
            self.measurements.skipped_without_participant += 1;
            return Ok(None);
        }
        self.measurements.group_reward_events += 1;
        Ok(Some(GroupRewardEvent {
            side: emission.side,
            team_id: self.teams[index],
            episode_number: self.episode_number,
            kind: GroupRewardKind::Immediate,
            amount: emission.amount,
        }))
    }
}
