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
    InvalidStep,
}

#[derive(Clone, Copy, Debug, PartialEq)]
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
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct EpisodeTerminalRewards {
    pub bee_terminal: f32,
    pub human_terminal: f32,
    pub bee_time: f32,
    pub human_time: f32,
}

/// Source GetRetainedMiningTsvForWinningSide and
/// RlOneVsOneReward.CalculateEconomicValueReward. This economic channel is
/// uncapped and separate from the 0.2 tactical-shaping budget.
#[derive(Clone, Copy, Debug, PartialEq)]
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

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct EpisodeTerminalMeasurements {
    pub ticks_observed: u64,
    pub elimination_checks: u64,
    pub eliminations: u64,
    pub timeouts: u64,
    pub draws: u64,
}

/// Per-side RecordShipDeathAttribution from the Unity coordinator.
/// The last death on a side is authoritative when that side is eliminated.
/// A same-side explosive can inherit an opposing original killer through
/// up to 16 killer links, while a Yellow Jacket's own detonation is always
/// a non-opponent loss even if its historical gameplay killer is an enemy.
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

pub struct NativeEpisodeTerminal {
    bee_side: i32,
    human_side: i32,
    timeout_seconds: f32,
    elapsed_seconds: f32,
    completed: Option<EpisodeTerminalResult>,
    measurements: EpisodeTerminalMeasurements,
}

impl NativeEpisodeTerminal {
    pub fn new(
        bee_side: i32, human_side: i32, timeout_seconds: f32,
    ) -> Result<Self, EpisodeTerminalError> {
        if bee_side <= 0 || human_side <= 0 || bee_side == human_side {
            return Err(EpisodeTerminalError::InvalidSides);
        }
        if !timeout_seconds.is_finite() || timeout_seconds <= 0.0 {
            return Err(EpisodeTerminalError::InvalidTimeout);
        }
        Ok(Self {
            bee_side, human_side, timeout_seconds,
            elapsed_seconds: 0.0, completed: None,
            measurements: EpisodeTerminalMeasurements::default(),
        })
    }

    pub fn result(&self) -> Option<EpisodeTerminalResult> {
        self.completed
    }

    pub fn elapsed_seconds(&self) -> f32 {
        self.elapsed_seconds
    }

    /// Source Level.GetNormalizedRlEpisodeProgress: timeout timer
    /// elapsed / length, clamped to [0,1]. This native controller is
    /// constructed when the timeout is armed, starting at elapsed=0.
    pub fn normalized_episode_progress(&self) -> f32 {
        (self.elapsed_seconds / self.timeout_seconds).clamp(0.0, 1.0)
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
            self.timeout_seconds.max(1.0)).clamp(0.0, 1.0)
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
        self.elapsed_seconds =
            (self.elapsed_seconds + dt_seconds).min(self.timeout_seconds);
        self.measurements.ticks_observed += 1;
        let bee_dead = world.living_mobile_ships_on_side(self.bee_side) == 0;
        let human_dead = world.living_mobile_ships_on_side(self.human_side) == 0;
        let extraction_outcome = world.extraction_outcome();
        if squad_was_retired || extraction_outcome.is_some() {
            self.measurements.elimination_checks += 1;
            let elimination = || if bee_dead || human_dead {
                Some((if bee_dead == human_dead { 0 } else if bee_dead {
                    self.human_side
                } else {
                    self.bee_side
                }, false))
            } else { None };
            if let Some((winner, extraction_victory)) = extraction_outcome.or_else(elimination) {
                let outcome = EpisodeTerminalResult {
                    winner_side: winner,
                    timed_out: false,
                    extraction_victory,
                    duration_seconds: self.elapsed_seconds,
                    bee_side_eliminated: bee_dead,
                    human_side_eliminated: human_dead,
                };
                self.completed = Some(outcome);
                self.measurements.eliminations += 1;
                self.measurements.draws += u64::from(winner == 0);
                world.frozen_after_elimination = true;
                return Ok(Some(outcome));
            }
        }
        if self.elapsed_seconds >= self.timeout_seconds {
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
