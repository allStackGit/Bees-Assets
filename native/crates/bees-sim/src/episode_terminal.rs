//! Source dedicated RL termination: GameState.IsSideKilled,
//! CaptureEliminationStateFromShips and RlOneVsOneEpisodeCoordinator
//! CompleteElimination / CompleteTimeout / DetermineWinner.
//!
//! Optional RL/episode controller over the SAME CombatWorld as gameplay.
//! Terminal rewards and group delivery are separate later integration.

use std::collections::{HashMap, HashSet};

use crate::combat_world::{CombatEvent, CombatWorld};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EpisodeTerminalError {
    InvalidSides,
    InvalidTimeout,
    InvalidSurvivalObjective,
    InvalidDemolitionObjective,
    InvalidEscortObjective,
    InvalidStep,
}

#[derive(Clone, Copy, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct EpisodeTerminalResult {
    pub winner_side: i32,
    pub timed_out: bool,
    pub extraction_victory: bool,
    pub duration_seconds: f32,
    /// Frozen at termination, just like the Unity GameState snapshot.
    pub bee_side_eliminated: bool,
    pub human_side_eliminated: bool,
}

/// Exactly RlOneVsOneReward.CalculateTerminalReward and
/// CalculateTimePenalty, before economic or tactical shaping.
#[derive(Clone, Copy, Debug, Default, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct EpisodeTerminalRewards {
    pub bee_terminal: f32,
    pub human_terminal: f32,
    pub bee_time: f32,
    pub human_time: f32,
}

/// Source GetRetainedMiningTsvForWinningSide and
/// RlOneVsOneReward.CalculateEconomicValueReward. This economic channel is
/// uncapped and separate from the 0.2 tactical-shaping budget.
#[derive(Clone, Copy, Debug, Default, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct EpisodeEconomicRewards {
    pub bee_retained_mined_tsv: i32,
    pub human_retained_mined_tsv: i32,
    pub bee_retained_reward: f32,
    pub human_retained_reward: f32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EpisodeEconomicError {
    InvalidAuthoredEconomicValue,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct EpisodeTerminalMeasurements {
    pub ticks_observed: u64,
    pub elimination_checks: u64,
    pub eliminations: u64,
    pub timeouts: u64,
    pub draws: u64,
    pub demolition_successes: u64,
    pub demolition_failures: u64,
    pub escorts_delivered: u64,
    pub escorts_lost: u64,
}

/// Per-side RecordShipDeathAttribution from the Unity coordinator.
/// The last death on a side is authoritative when that side is eliminated.
/// A same-side explosive can inherit an opposing original killer through
/// up to 16 killer links, while a Yellow Jacket's own detonation is always
/// a non-opponent loss even if its historical gameplay killer is an enemy.
#[derive(Clone, serde::Serialize, serde::Deserialize)]
pub struct EpisodeDeathAttribution {
    sides: [i32; 2],
    /// Historical owner links for every dead ship this episode.
    killer_by_ship: HashMap<i64, i64>,
    explicit_self_detonations: HashSet<i64>,
    last_opponent_caused: [Option<bool>; 2],
    deaths_recorded: u64,
    ancestry_links_followed: u64,
}

impl EpisodeDeathAttribution {
    pub fn new(
        bee_side: i32, human_side: i32,
    ) -> Result<Self, EpisodeTerminalError> {
        if bee_side <= 0 || human_side <= 0 || bee_side == human_side {
            return Err(EpisodeTerminalError::InvalidSides);
        }
        Ok(Self {
            sides: [bee_side, human_side],
            killer_by_ship: HashMap::new(),
            explicit_self_detonations: HashSet::new(),
            last_opponent_caused: [None, None],
            deaths_recorded: 0,
            ancestry_links_followed: 0,
        })
    }

    pub fn last_opponent_caused(&self, side: i32) -> Option<bool> {
        self.sides.iter().position(|&entry| entry == side)
            .and_then(|i| self.last_opponent_caused[i])
    }

    pub fn deaths_recorded(&self) -> u64 {
        self.deaths_recorded
    }

    pub fn ancestry_links_followed(&self) -> u64 {
        self.ancestry_links_followed
    }

    /// Consume each authoritative combat event once, in source order,
    /// after the fixed tick and BEFORE the next tick clears its events.
    /// No training-only damage or death reason is inferred here.
    pub fn record_combat_events(
        &mut self, world: &CombatWorld, events: &[CombatEvent],
    ) {
        for event in events {
            match event {
                CombatEvent::YellowJacketDetonated { ship_id, .. } => {
                    self.explicit_self_detonations.insert(*ship_id);
                }
                CombatEvent::ShipDied { victim_id, killer_id, .. } => {
                    let Some(victim) = world.ship(*victim_id) else {
                        continue;
                    };
                    let explicitly_self = self.explicit_self_detonations
                        .remove(victim_id);
                    let mut effective_killer = *killer_id;
                    if !explicitly_self {
                        // Source follows same-side killer ancestry at most
                        // 16 links. External enemy ownership wins over a
                        // same-side chain; cyclic/self links terminate.
                        for _ in 0..16 {
                            let Some(killer) = world.ship(effective_killer)
                            else { break };
                            if killer.side != victim.side { break; }
                            let Some(&parent_killer) =
                                self.killer_by_ship.get(&effective_killer)
                            else { break };
                            if parent_killer <= 0 ||
                                parent_killer == effective_killer {
                                break;
                            }
                            effective_killer = parent_killer;
                            self.ancestry_links_followed += 1;
                        }
                    }
                    let opponent_caused = !explicitly_self &&
                        world.ship(effective_killer).is_some_and(|killer|
                            killer.side != victim.side &&
                            self.sides.contains(&killer.side)
                        );
                    if let Some(index) = self.sides.iter()
                        .position(|&side| side == victim.side) {
                        self.last_opponent_caused[index] =
                            Some(opponent_caused);
                        self.deaths_recorded += 1;
                    }
                    self.killer_by_ship.insert(*victim_id, *killer_id);
                }
                _ => {}
            }
        }
    }
}

#[derive(Clone, Copy, Debug, serde::Serialize, serde::Deserialize)]
pub struct SurvivalObjective {
    pub defending_side: i32,
    pub protected_ship_id: i64,
    pub seconds: f32,
}

#[derive(Clone, Copy, Debug)]
pub struct SurvivalStatus {
    pub defending_side: i32,
    pub protected_ship_id: i64,
    pub seconds_remaining: f32,
    pub duration_seconds: f32,
    pub elapsed_seconds: f32,
}

#[derive(Clone, serde::Serialize, serde::Deserialize)]
struct ArmedSurvival {
    objective: SurvivalObjective,
    deadline_tick: u64,
}

/// An authored physical MapObject demolition contract. The attacker wins
/// only when the canonical combat world retires the exact target object;
/// the defender wins on the deadline or final attacker elimination.
#[derive(Clone, Copy, Debug, serde::Serialize, serde::Deserialize)]
pub struct DemolitionObjective {
    pub attacking_side: i32,
    pub target_map_object_id: i64,
    pub seconds: f32,
}

#[derive(Clone, serde::Serialize, serde::Deserialize)]
struct ArmedDemolition {
    objective: DemolitionObjective,
    deadline_tick: u64,
}

/// Read-only objective timer for native game presentation. It does not own
/// or copy target health; that remains on the single CombatMapObject.
#[derive(Clone, Copy, Debug)]
pub struct DemolitionStatus {
    pub attacking_side: i32,
    pub target_map_object_id: i64,
    pub seconds_remaining: f32,
    pub duration_seconds: f32,
}

/// Delivery requires one authored mobile ship to reach the canonical
/// extraction collider, not the first member of its faction to escape.
#[derive(Clone, Copy, Debug, serde::Serialize, serde::Deserialize)]
pub struct EscortObjective {
    pub escorting_side: i32,
    pub escorted_ship_id: i64,
    pub seconds: f32,
}

#[derive(Clone, serde::Serialize, serde::Deserialize)]
struct ArmedEscort {
    objective: EscortObjective,
    deadline_tick: u64,
}

#[derive(Clone, Copy, Debug)]
pub struct EscortStatus {
    pub escorting_side: i32,
    pub escorted_ship_id: i64,
    pub seconds_remaining: f32,
    pub duration_seconds: f32,
}

/// Finished native authored mission, distinct from ordinary faction victory.
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub enum EpisodeMissionKind {
    Survival,
    Demolition,
    Escort,
    Extraction,
}

impl EpisodeMissionKind {
    /// Stable reserved-channel order, shared with source PerceptionWork.
    pub fn index(self) -> usize {
        match self {
            Self::Survival => 0,
            Self::Demolition => 1,
            Self::Escort => 2,
            Self::Extraction => 3,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Self::Survival => "survival",
            Self::Demolition => "demolition",
            Self::Escort => "escort",
            Self::Extraction => "extraction",
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct EpisodeMissionOutcome {
    pub kind: EpisodeMissionKind,
    pub objective_side: i32,
    /// Completion of the physical goal, not merely the goal side winning
    /// through an unrelated opponent-elimination path.
    pub completed: bool,
}

#[derive(Clone, serde::Serialize, serde::Deserialize)]
pub struct NativeEpisodeTerminal {
    bee_side: i32,
    human_side: i32,
    timeout_seconds: Option<f32>,
    survival: Option<ArmedSurvival>,
    demolition: Option<ArmedDemolition>,
    escort: Option<ArmedEscort>,
    elapsed_seconds: f32,
    completed: Option<EpisodeTerminalResult>,
    /// A SquadRetired event would have eliminated an active side, but
    /// the source Stage still reserves future reinforcement hulls.
    /// Recheck precisely once those reserved ships actually deploy.
    deferred_elimination: bool,
    measurements: EpisodeTerminalMeasurements,
}

impl NativeEpisodeTerminal {
    pub fn new(
        bee_side: i32, human_side: i32, timeout_seconds: f32,
    ) -> Result<Self, EpisodeTerminalError> {
        if !timeout_seconds.is_finite() || timeout_seconds <= 0.0 {
            return Err(EpisodeTerminalError::InvalidTimeout);
        }
        Self::with_timeout(bee_side, human_side, Some(timeout_seconds))
    }

    pub fn new_game(bee_side: i32, human_side: i32) -> Result<Self, EpisodeTerminalError> {
        Self::with_timeout(bee_side, human_side, None)
    }

    fn with_timeout(
        bee_side: i32, human_side: i32, timeout_seconds: Option<f32>,
    ) -> Result<Self, EpisodeTerminalError> {
        if bee_side <= 0 || human_side <= 0 || bee_side == human_side {
            return Err(EpisodeTerminalError::InvalidSides);
        }
        Ok(Self {
            bee_side, human_side, timeout_seconds, survival: None, demolition: None,
            escort: None,
            elapsed_seconds: 0.0, completed: None,
            deferred_elimination: false,
            measurements: EpisodeTerminalMeasurements::default(),
        })
    }

    pub fn arm_survival(
        &mut self, objective: SurvivalObjective, fixed_dt_seconds: f32,
    ) -> Result<(), EpisodeTerminalError> {
        let ticks = (objective.seconds / fixed_dt_seconds).ceil();
        // A policy episode also owns a safety timeout and reward progress.
        // Survival remains the *physical* terminal authority as long as its
        // complete fixed-step deadline fits within that policy episode.
        // Gameplay with no policy timeout keeps the identical survival rules.
        if self.timeout_seconds.is_some_and(|timeout|
            (ticks as f64) * (fixed_dt_seconds as f64) > timeout as f64
        ) || self.survival.is_some() || self.demolition.is_some() ||
            self.escort.is_some() || self.measurements.ticks_observed != 0 ||
            ![self.bee_side, self.human_side].contains(&objective.defending_side) ||
            objective.protected_ship_id <= 0 ||
            !objective.seconds.is_finite() || objective.seconds <= 0.0 ||
            !fixed_dt_seconds.is_finite() || fixed_dt_seconds <= 0.0 ||
            !ticks.is_finite() || ticks < 1.0 || ticks >= u64::MAX as f32
        {
            return Err(EpisodeTerminalError::InvalidSurvivalObjective);
        }
        self.survival = Some(ArmedSurvival { objective, deadline_tick: ticks as u64 });
        Ok(())
    }

    /// Arm before the first tick, sharing the existing physical episode
    /// budget and termination rules with normal gameplay and MA-POCA.
    pub fn arm_demolition(
        &mut self, objective: DemolitionObjective, fixed_dt_seconds: f32,
    ) -> Result<(), EpisodeTerminalError> {
        let ticks = (objective.seconds / fixed_dt_seconds).ceil();
        if self.timeout_seconds.is_some_and(|timeout|
            (ticks as f64) * (fixed_dt_seconds as f64) > timeout as f64
        ) || self.survival.is_some() || self.demolition.is_some() ||
            self.escort.is_some() || self.measurements.ticks_observed != 0 ||
            ![self.bee_side, self.human_side].contains(&objective.attacking_side) ||
            objective.target_map_object_id <= 0 ||
            !objective.seconds.is_finite() || objective.seconds <= 0.0 ||
            !fixed_dt_seconds.is_finite() || fixed_dt_seconds <= 0.0 ||
            !ticks.is_finite() || ticks < 1.0 || ticks >= u64::MAX as f32
        {
            return Err(EpisodeTerminalError::InvalidDemolitionObjective);
        }
        self.demolition = Some(ArmedDemolition { objective, deadline_tick: ticks as u64 });
        Ok(())
    }

    pub fn arm_escort(
        &mut self, objective: EscortObjective, fixed_dt_seconds: f32,
    ) -> Result<(), EpisodeTerminalError> {
        let ticks = (objective.seconds / fixed_dt_seconds).ceil();
        if self.timeout_seconds.is_some_and(|timeout|
            (ticks as f64) * (fixed_dt_seconds as f64) > timeout as f64
        ) || self.survival.is_some() || self.demolition.is_some() ||
            self.escort.is_some() || self.measurements.ticks_observed != 0 ||
            ![self.bee_side, self.human_side].contains(&objective.escorting_side) ||
            objective.escorted_ship_id <= 0 ||
            !objective.seconds.is_finite() || objective.seconds <= 0.0 ||
            !fixed_dt_seconds.is_finite() || fixed_dt_seconds <= 0.0 ||
            !ticks.is_finite() || ticks < 1.0 || ticks >= u64::MAX as f32
        {
            return Err(EpisodeTerminalError::InvalidEscortObjective);
        }
        self.escort = Some(ArmedEscort { objective, deadline_tick: ticks as u64 });
        Ok(())
    }

    pub fn result(&self) -> Option<EpisodeTerminalResult> {
        self.completed
    }

    /// Read the terminal result's cause using the same physical objective
    /// state that decided the match. A generic extraction faction can win
    /// through elimination without actually delivering a ship, so its
    /// physical goal is counted only on extraction_victory.
    pub fn mission_outcome(
        &self, world: &CombatWorld, result: EpisodeTerminalResult,
    ) -> Option<EpisodeMissionOutcome> {
        if let Some(escort) = &self.escort {
            return Some(EpisodeMissionOutcome {
                kind: EpisodeMissionKind::Escort,
                objective_side: escort.objective.escorting_side,
                completed: result.extraction_victory,
            });
        }
        if let Some(demolition) = &self.demolition {
            return Some(EpisodeMissionOutcome {
                kind: EpisodeMissionKind::Demolition,
                objective_side: demolition.objective.attacking_side,
                completed: result.winner_side == demolition.objective.attacking_side,
            });
        }
        if let Some(survival) = &self.survival {
            return Some(EpisodeMissionOutcome {
                kind: EpisodeMissionKind::Survival,
                objective_side: survival.objective.defending_side,
                completed: result.winner_side == survival.objective.defending_side,
            });
        }
        world.extraction_objective().map(|(objective_side, _, _)| EpisodeMissionOutcome {
            kind: EpisodeMissionKind::Extraction,
            objective_side,
            completed: result.extraction_victory,
        })
    }

    pub fn survival_status(&self) -> Option<SurvivalStatus> {
        self.survival.as_ref().map(|survival| SurvivalStatus {
            defending_side: survival.objective.defending_side,
            protected_ship_id: survival.objective.protected_ship_id,
            duration_seconds: survival.objective.seconds,
            elapsed_seconds: self.elapsed_seconds,
            seconds_remaining: (survival.objective.seconds - self.elapsed_seconds).max(0.0),
        })
    }

    pub fn demolition_status(&self) -> Option<DemolitionStatus> {
        self.demolition.as_ref().map(|demolition| DemolitionStatus {
            attacking_side: demolition.objective.attacking_side,
            target_map_object_id: demolition.objective.target_map_object_id,
            duration_seconds: demolition.objective.seconds,
            seconds_remaining: (demolition.objective.seconds - self.elapsed_seconds).max(0.0),
        })
    }

    pub fn escort_status(&self) -> Option<EscortStatus> {
        self.escort.as_ref().map(|escort| EscortStatus {
            escorting_side: escort.objective.escorting_side,
            escorted_ship_id: escort.objective.escorted_ship_id,
            duration_seconds: escort.objective.seconds,
            seconds_remaining: (escort.objective.seconds - self.elapsed_seconds).max(0.0),
        })
    }

    pub fn elapsed_seconds(&self) -> f32 {
        self.elapsed_seconds
    }

    /// Source Level.GetNormalizedRlEpisodeProgress: timeout timer
    /// elapsed / length, clamped to [0,1]. This native controller is
    /// constructed when the timeout is armed, starting at elapsed=0.
    pub fn normalized_episode_progress(&self) -> f32 {
        // Source RL observation progress always uses the configured policy
        // episode timeout, even if a separate survival objective decides
        // the physical result sooner. Game-only Levels have no RL timer.
        self.timeout_seconds.map_or(0.0, |timeout|
            (self.elapsed_seconds / timeout).clamp(0.0, 1.0)
        )
    }

    pub fn measurements(&self) -> EpisodeTerminalMeasurements {
        self.measurements
    }

    /// Reward arithmetic only: the full RL coordinator still owns ship
    /// death-cause attribution, economic rewards, tactical shaping and
    /// group reward delivery. The two booleans must be supplied from the
    /// actual RecordShipDeathAttribution equivalent, including recursive
    /// Fire Barge chain ownership and explicit self-detonations.
    ///
    /// Do not infer them from CombatEvent::ShipDied.killer_id alone.
    pub fn terminal_rewards(
        &self,
        bee_non_opponent_elimination: bool,
        human_non_opponent_elimination: bool,
    ) -> Option<EpisodeTerminalRewards> {
        let outcome = self.completed?;
        let timeout = self.timeout_seconds?;
        const WIN: f32 = 1.0;
        const LOSS: f32 = -1.0;
        const TIMEOUT: f32 = -1.1;
        const MAX_TIME_PENALTY: f32 = 0.01;

        let terminal = |side: i32, non_opponent: bool| {
            let non_opponent_loss = !outcome.timed_out &&
                outcome.winner_side != 0 &&
                outcome.winner_side != side && non_opponent;
            if outcome.timed_out || non_opponent_loss {
                TIMEOUT
            } else if outcome.winner_side == 0 {
                0.0
            } else if outcome.winner_side == side {
                WIN
            } else {
                LOSS
            }
        };
        let time_penalty = -(outcome.duration_seconds /
            timeout.max(1.0)).clamp(0.0, 1.0)
            * MAX_TIME_PENALTY;
        Some(EpisodeTerminalRewards {
            bee_terminal: terminal(self.bee_side,
                bee_non_opponent_elimination),
            human_terminal: terminal(self.human_side,
                human_non_opponent_elimination),
            bee_time: if outcome.winner_side == self.bee_side {
                time_penalty
            } else { 0.0 },
            human_time: if outcome.winner_side == self.human_side {
                time_penalty
            } else { 0.0 },
        })
    }

    /// Apply the exact source's "last death non-opponent caused" condition
    /// without asking a Level caller to guess a Boolean. Full reward
    /// delivery still belongs to the MA-POCA/game episode adapter.
    pub fn terminal_rewards_from_attribution(
        &self, attribution: &EpisodeDeathAttribution,
    ) -> Option<EpisodeTerminalRewards> {
        let result = self.completed?;
        if attribution.sides != [self.bee_side, self.human_side] {
            return None;
        }
        let non_opponent_loss = |side: i32, eliminated: bool| {
            !result.timed_out && result.winner_side != 0 &&
            result.winner_side != side && eliminated &&
            attribution.last_opponent_caused(side) == Some(false)
        };
        self.terminal_rewards(
            non_opponent_loss(self.bee_side, result.bee_side_eliminated),
            non_opponent_loss(self.human_side, result.human_side_eliminated),
        )
    }

    /// A source Level supplies actual FleetShip mined TSV retained on the
    /// surviving side; no CombatWorld collider or temporary cargo estimate
    /// is used here. Losing sides and timeout draws retain no terminal
    /// mining bonus. Enemy-miner destruction rewards occur at kill time and
    /// belong to a separate economic-event consumer.
    pub fn terminal_economic_rewards(
        &self,
        bee_starting_tsv: i32,
        human_starting_tsv: i32,
        bee_retainable_mined_tsv: i32,
        human_retainable_mined_tsv: i32,
    ) -> Result<Option<EpisodeEconomicRewards>, EpisodeEconomicError> {
        if bee_starting_tsv < 0 || human_starting_tsv < 0 ||
            bee_retainable_mined_tsv < 0 ||
            human_retainable_mined_tsv < 0 {
            return Err(EpisodeEconomicError::InvalidAuthoredEconomicValue);
        }
        let Some(outcome) = self.completed else { return Ok(None) };
        let bee_retained =
            if outcome.winner_side == self.bee_side {
                bee_retainable_mined_tsv
            } else { 0 };
        let human_retained =
            if outcome.winner_side == self.human_side {
                human_retainable_mined_tsv
            } else { 0 };
        let reward = |tsv: i32, starting: i32| -> f32 {
            if tsv <= 0 || starting <= 0 {
                0.0
            } else {
                tsv as f32 / starting as f32
            }
        };
        Ok(Some(EpisodeEconomicRewards {
            bee_retained_mined_tsv: bee_retained,
            human_retained_mined_tsv: human_retained,
            bee_retained_reward: reward(bee_retained, bee_starting_tsv),
            human_retained_reward: reward(human_retained, human_starting_tsv),
        }))
    }

    /// Call at the end of each authoritative fixed gameplay tick after
    /// processing its SquadRetired lifecycle transitions. A squad death
    /// causes the Unity Level to examine IsSideKilled; a stationary side
    /// must NOT instantly end a match merely because it began with no
    /// mobile ships (the Level has not yet killed its squad).
    ///
    /// If both sides lost mobile ships during the same tick, their
    /// elimination snapshot is a draw. Elimination takes priority over
    /// timeout only when the Level already handled a final squad death.
    /// The terminal flag prevents next-tick post-game combat mutations.
    pub fn observe_after_step(
        &mut self,
        world: &mut CombatWorld,
        dt_seconds: f32,
        squad_was_retired: bool,
    ) -> Result<Option<EpisodeTerminalResult>, EpisodeTerminalError> {
        if !dt_seconds.is_finite() || dt_seconds <= 0.0 {
            return Err(EpisodeTerminalError::InvalidStep);
        }
        if self.completed.is_some() {
            return Ok(None);
        }
        self.measurements.ticks_observed += 1;
        self.elapsed_seconds = if self.survival.is_some() || self.demolition.is_some() ||
            self.escort.is_some() {
            // The authored survival clock starts at tick zero, alongside the waves.
            self.measurements.ticks_observed as f32 * dt_seconds
        } else {
            let elapsed = self.elapsed_seconds + dt_seconds;
            self.timeout_seconds.map_or(elapsed, |timeout| elapsed.min(timeout))
        };
        let bee_dead = world.living_mobile_ships_on_side(self.bee_side) == 0;
        let human_dead = world.living_mobile_ships_on_side(self.human_side) == 0;
        if let Some(survival) = &self.survival {
            let objective = survival.objective;
            let defender_is_bee = objective.defending_side == self.bee_side;
            let (defenders_dead, attacker_side) = if defender_is_bee {
                (bee_dead, self.human_side)
            } else {
                (human_dead, self.bee_side)
            };
            let target_dead = world.ship(objective.protected_ship_id)
                .is_none_or(|ship| ship.vitals.is_dead());
            // The target is the mission's durable defense objective. A
            // scheduled reserve wave can still protect it even while every
            // *deployed* mobile defender has been killed. Treat reserve
            // identity as authoritative, exactly as demolition and escort
            // already do, instead of ending a multi-wave defense early.
            let defenders_eliminated = defenders_dead &&
                !world.has_reserved_future_ships_on_side(objective.defending_side);
            let winner = if target_dead || defenders_eliminated {
                attacker_side
            } else if self.measurements.ticks_observed >= survival.deadline_tick {
                objective.defending_side
            } else {
                return Ok(None);
            };
            let outcome = EpisodeTerminalResult {
                winner_side: winner,
                timed_out: false,
                extraction_victory: false,
                duration_seconds: self.elapsed_seconds,
                bee_side_eliminated: bee_dead,
                human_side_eliminated: human_dead,
            };
            self.completed = Some(outcome);
            world.frozen_after_elimination = true;
            return Ok(Some(outcome));
        }

        if let Some(demolition) = &self.demolition {
            let objective = demolition.objective;
            let attacking_is_bee = objective.attacking_side == self.bee_side;
            let (attacker_dead, defending_side) = if attacking_is_bee {
                (bee_dead, self.human_side)
            } else {
                (human_dead, self.bee_side)
            };
            // A last-ship death cannot deny a same-tick destruction victory,
            // and reserved reinforcement hulls still belong to the attacker.
            let target_destroyed = world.map_object(objective.target_map_object_id).is_none();
            let winner = if target_destroyed {
                objective.attacking_side
            } else if (attacker_dead &&
                !world.has_reserved_future_ships_on_side(objective.attacking_side)) ||
                self.measurements.ticks_observed >= demolition.deadline_tick
            {
                defending_side
            } else {
                return Ok(None);
            };
            let outcome = EpisodeTerminalResult {
                winner_side: winner,
                timed_out: false,
                extraction_victory: false,
                duration_seconds: self.elapsed_seconds,
                bee_side_eliminated: bee_dead,
                human_side_eliminated: human_dead,
            };
            self.completed = Some(outcome);
            self.measurements.demolition_successes += u64::from(target_destroyed);
            self.measurements.demolition_failures += u64::from(!target_destroyed);
            world.frozen_after_elimination = true;
            return Ok(Some(outcome));
        }

        if let Some(escort) = &self.escort {
            let objective = escort.objective;
            let (escort_dead, opponent) = if objective.escorting_side == self.bee_side {
                (bee_dead, self.human_side)
            } else { (human_dead, self.bee_side) };
            // An exiting ship retires from the physical roster; delivery
            // takes precedence over the resulting ordinary death snapshot.
            let delivered = world.escort_delivery_confirmed(objective.escorted_ship_id);
            let lost = world.ship(objective.escorted_ship_id)
                .is_none_or(|ship| ship.vitals.is_dead());
            let winner = if delivered {
                objective.escorting_side
            } else if lost ||
                (escort_dead &&
                    !world.has_reserved_future_ships_on_side(objective.escorting_side)) ||
                self.measurements.ticks_observed >= escort.deadline_tick
            {
                opponent
            } else { return Ok(None) };
            let outcome = EpisodeTerminalResult {
                winner_side: winner, timed_out: false,
                extraction_victory: delivered, duration_seconds: self.elapsed_seconds,
                bee_side_eliminated: bee_dead, human_side_eliminated: human_dead,
            };
            self.completed = Some(outcome);
            self.measurements.escorts_delivered += u64::from(delivered);
            self.measurements.escorts_lost += u64::from(!delivered);
            world.frozen_after_elimination = true;
            return Ok(Some(outcome));
        }

        let extraction_outcome = world.extraction_outcome();
        // The physics world knows which faction owns every reserved future
        // hull. Delaying elimination for the *opponent's* reinforcement
        // would keep a defeated faction in an unending game if the later
        // wave requires a ship-death trigger that can no longer occur.
        // Source survival and extraction retain their independent terminal
        // conditions; only ordinary SquadRetired elimination is deferred.
        let bee_waiting = world.has_reserved_future_ships_on_side(self.bee_side);
        let human_waiting = world.has_reserved_future_ships_on_side(self.human_side);
        let bee_eliminated = bee_dead && !bee_waiting;
        let human_eliminated = human_dead && !human_waiting;
        if squad_was_retired || self.deferred_elimination || extraction_outcome.is_some() {
            self.measurements.elimination_checks += 1;
            // Recheck on subsequent ticks only while a currently empty
            // faction waits for its real reserved hulls. Once they arrive,
            // retire this pending check without requiring another death.
            self.deferred_elimination =
                (bee_dead && bee_waiting) || (human_dead && human_waiting);
            let elimination = || if bee_eliminated || human_eliminated {
                Some((if bee_eliminated == human_eliminated { 0 }
                    else if bee_eliminated { self.human_side }
                    else { self.bee_side }, false))
            } else { None };
            if let Some((winner, extraction_victory)) = extraction_outcome.or_else(elimination) {
                let outcome = EpisodeTerminalResult {
                    winner_side: winner,
                    timed_out: false,
                    extraction_victory,
                    duration_seconds: self.elapsed_seconds,
                    bee_side_eliminated: bee_eliminated,
                    human_side_eliminated: human_eliminated,
                };
                self.completed = Some(outcome);
                self.measurements.eliminations += 1;
                self.measurements.draws += u64::from(winner == 0);
                world.frozen_after_elimination = true;
                return Ok(Some(outcome));
            }
        }
        if self.timeout_seconds.is_some_and(|timeout| self.elapsed_seconds >= timeout) {
            let outcome = EpisodeTerminalResult {
                winner_side: 0, timed_out: true, extraction_victory: false,
                duration_seconds: self.elapsed_seconds,
                bee_side_eliminated: bee_dead,
                human_side_eliminated: human_dead,
            };
            self.completed = Some(outcome);
            self.measurements.timeouts += 1;
            self.measurements.draws += 1;
            world.frozen_after_elimination = true;
            return Ok(Some(outcome));
        }
        Ok(None)
    }
}
