//! Source dedicated RL termination: GameState.IsSideKilled,
//! CaptureEliminationStateFromShips and RlOneVsOneEpisodeCoordinator
//! CompleteElimination / CompleteTimeout / DetermineWinner.
//!
//! Optional RL/episode controller over the SAME CombatWorld as gameplay.
//! Terminal rewards and group delivery are separate later integration.

use crate::combat_world::CombatWorld;

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
    pub duration_seconds: f32,
    /// Frozen at termination, just like the Unity GameState snapshot.
    pub bee_side_eliminated: bool,
    pub human_side_eliminated: bool,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct EpisodeTerminalMeasurements {
    pub ticks_observed: u64,
    pub elimination_checks: u64,
    pub eliminations: u64,
    pub timeouts: u64,
    pub draws: u64,
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

    pub fn measurements(&self) -> EpisodeTerminalMeasurements {
        self.measurements
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
        if squad_was_retired {
            self.measurements.elimination_checks += 1;
            if bee_dead || human_dead {
                let winner = if bee_dead == human_dead { 0 } else if bee_dead {
                    self.human_side
                } else {
                    self.bee_side
                };
                let outcome = EpisodeTerminalResult {
                    winner_side: winner,
                    timed_out: false,
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
                winner_side: 0, timed_out: true,
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
