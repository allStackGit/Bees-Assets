//! Shared, source-timed HiveMind command admission for rendered and headless
//! native Levels. The launcher supplies its already authenticated socket
//! worker and setup readiness; no command, hash, socket or game state is
//! owned here. Each entry point uses the same canonical host command owner.
//!
//! Source Unity ScaledTimer fires when elapsed > length, not at equality.
//! Keep a fractional second schedule rather than rounding every 0.25s
//! pulse to a fixed number of steps, which would drift on 0.02s worlds.

use std::collections::HashSet;
use std::io::{self, ErrorKind};

use bees_gameplay::inference::NativePolicyInference;
use bees_gameplay::level_host::{NativeLevelActivationContext,
    NativeLevelNotificationSink, NativeLevelObservationContext};
use bees_gameplay::level_rules::NativeLevelRewardMetadata;
use bees_gameplay::squads::SquadRosterError;
use bees_hivemind::session::SquadLifetime;

use crate::runtime::{NativeHostRuntime, NativeSourceCommandPulse};
use crate::socket_worker::NativeSocketWorker;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize)]
pub struct NativeSourceScheduleMeasurements {
    /// Real source timer beats, not per-step or per-ship work.
    pub due_beats: u64,
    /// Due beats which had a connected transport and an active Level.
    pub ready_beats: u64,
    /// Beats missed while setup/transport was not ready.
    pub transport_blocked_beats: u64,
    /// Due beats discarded because this Level is inactive or complete.
    pub inactive_level_beats: u64,
    /// Additional periods skipped when a caller advances past a pulse.
    pub skipped_overdue_beats: u64,
    /// Episode-boundary clock reinitializations.
    pub episode_clock_resets: u64,
}

#[derive(Clone, Copy, Debug)]
struct SourceCommandClock {
    revision: u64,
    next_due_tick: u64,
    next_due_seconds: f64,
}

/// Registration and its timer always move together, including append-only
/// runtime enrollment and per-episode reset. The connection still owns
/// all request IDs and the Level owns each SquadLifetime.
#[derive(Clone, Copy, Debug)]
struct SourceWorldClock {
    level_id: i32,
    fixed_dt: f32,
    clock: Option<SourceCommandClock>,
}

fn invalid(message: &'static str) -> io::Error {
    io::Error::new(ErrorKind::InvalidInput, message)
}

fn source_command_ticks(seconds: f64, dt: f32) -> io::Result<u64> {
    if !dt.is_finite() || dt <= 0.0 || !seconds.is_finite() || seconds < 0.0 {
        return Err(invalid("invalid source HiveMind timer configuration"));
    }
    let ticks = (seconds / f64::from(dt)).floor() + 1.0;
    if ticks >= u64::MAX as f64 {
        return Err(invalid("source HiveMind timer exceeds fixed-step clock range"));
    }
    Ok(ticks as u64)
}

/// Per-World source clock and reusable due set, independent of transport.
/// A caller may own one scheduler per native game/trainer session; no
/// scheduler is installed in CombatWorld and pure CPU PPO stays unchanged.
pub struct NativeSourceCommandScheduler {
    worlds: Vec<SourceWorldClock>,
    due_levels: HashSet<i64>,
    measurements: NativeSourceCommandPulse,
    schedule_measurements: NativeSourceScheduleMeasurements,
}

impl NativeSourceCommandScheduler {
    pub fn new(level_ids: &[i32], fixed_dt_by_world: &[f32]) -> io::Result<Self> {
        if fixed_dt_by_world.is_empty() || level_ids.len() != fixed_dt_by_world.len() {
            return Err(invalid("source HiveMind scheduler needs matching nonempty Level registrations"));
        }
        let mut known_levels = HashSet::with_capacity(level_ids.len());
        let mut worlds = Vec::with_capacity(level_ids.len());
        for (&level_id, &fixed_dt) in level_ids.iter().zip(fixed_dt_by_world) {
            if level_id <= 0 || !known_levels.insert(level_id) {
                return Err(invalid("source HiveMind scheduler requires unique positive Level IDs"));
            }
            source_command_ticks(0.25, fixed_dt)?;
            worlds.push(SourceWorldClock { level_id, fixed_dt, clock: None });
        }
        Ok(Self {
            worlds,
            due_levels: HashSet::new(),
            measurements: NativeSourceCommandPulse::default(),
            schedule_measurements: NativeSourceScheduleMeasurements::default(),
        })
    }

    pub fn measurements(&self) -> NativeSourceCommandPulse {
        self.measurements
    }

    pub fn schedule_measurements(&self) -> NativeSourceScheduleMeasurements {
        self.schedule_measurements
    }

    pub fn registered_worlds(&self) -> usize {
        self.worlds.len()
    }

    /// The shared native host can add an independently seeded Level
    /// in-place. Admit its source clock AFTER successful host registration;
    /// all existing worlds preserve their absolute timer deadlines.
    /// No scheduler-side copy of the Level or its commands is created.
    pub fn register_added_world<M, A, B, S>(
        &mut self,
        host: &NativeHostRuntime<M, A, B, S>,
        index: usize,
        level_id: i32,
    ) -> io::Result<()>
    where
        M: NativeLevelRewardMetadata + NativeLevelActivationContext +
            NativeLevelObservationContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
        B: NativePolicyInference,
        S: NativeLevelNotificationSink,
    {
        if index != self.worlds.len() ||
            host.simulation().world_count() != index + 1 {
            return Err(invalid("source HiveMind scheduler requires append-only registration"));
        }
        if level_id <= 0 || self.worlds.iter().any(|world| world.level_id == level_id) {
            return Err(invalid("source HiveMind scheduler requires unique positive Level IDs"));
        }
        let fixed_dt = host.simulation().world_fixed_step_seconds(index)
            .ok_or_else(|| invalid("added native Level has no fixed-step clock"))?;
        source_command_ticks(0.25, fixed_dt)?;
        self.worlds.push(SourceWorldClock { level_id, fixed_dt, clock: None });
        Ok(())
    }

    fn ensure_clock(
        &mut self, index: usize, revision: u64, first_delay: i32,
        roster_order: &[SquadLifetime],
    ) -> io::Result<()> {
        if self.worlds[index].clock.is_none_or(|clock| clock.revision != revision) {
            // Validate this source Level's owner once per episode, not on
            // every fixed step. A misplaced registration must not issue
            // a targeting request for another world's SquadLifetime.
            let level_id = i64::from(self.worlds[index].level_id);
            if roster_order.iter().any(|owner| owner.level_id != level_id) {
                return Err(invalid("source HiveMind scheduler Level ID does not match its squads"));
            }
            self.schedule_measurements.episode_clock_resets += 1;
            // Unity SetupHivemind's initial one-shot timer is
            // (InitialDelay - 0.25) followed by recurring quarter seconds.
            // Even a zero initial delay starts at the first timer callback.
            let initial = f64::from(first_delay.max(0)).max(0.25);
            let fixed_dt = self.worlds[index].fixed_dt;
            let next_due_tick = source_command_ticks(initial, fixed_dt)?;
            self.worlds[index].clock = Some(SourceCommandClock {
                revision, next_due_tick, next_due_seconds: initial,
            });
        }
        Ok(())
    }

    /// Tick one source timer per registered World, then ask the ONE host
    /// command owner to emit requests for due Levels. Transport setup
    /// readiness is passed by the caller; missed beats are not replayed
    /// in a burst after an outage.
    pub fn issue_due<M, A, B, S>(
        &mut self,
        host: &mut NativeHostRuntime<M, A, B, S>,
        worker: &NativeSocketWorker,
        setup_complete: bool,
    ) -> io::Result<()>
    where
        M: NativeLevelRewardMetadata + NativeLevelActivationContext +
            NativeLevelObservationContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
        B: NativePolicyInference,
        S: NativeLevelNotificationSink,
    {
        if host.simulation().world_count() != self.worlds.len() {
            return Err(invalid("source HiveMind scheduler World count changed"));
        }
        self.due_levels.clear();
        let ready = setup_complete && worker.ready();
        for index in 0..self.worlds.len() {
            let world = host.simulation().world(index)
                .ok_or_else(|| invalid("registered native Level disappeared"))?;
            // A Stage that has disabled HiveMind does not have a source
            // command timer. Do not require an initial delay, construct
            // a dormant clock, or consume a quarter-second callback for it.
            match world.state.metadata.source_hivemind_active() {
                Some(false) => continue,
                Some(true) => {},
                None => return Err(invalid("source HiveMind authorization missing from Level")),
            }
            let initial = world.state.metadata.source_hivemind_initial_delay_seconds()
                .ok_or_else(|| invalid("source HiveMind delay missing from Level"))?;
            let tick = world.tick();
            self.ensure_clock(index, world.episode_revision(), initial,
                world.state.events.roster().source_order())?;
            let level_id = self.worlds[index].level_id;
            let fixed_dt = self.worlds[index].fixed_dt;
            let clock = self.worlds[index].clock.as_mut().expect("initialized source clock");
            if tick >= clock.next_due_tick {
                self.schedule_measurements.due_beats += 1;
                let active = !world.is_failed() && !world.state.events.is_failed() &&
                    !world.state.events.completed() && !world.state.events.was_aborted();
                if ready && active {
                    self.schedule_measurements.ready_beats += 1;
                    self.due_levels.insert(i64::from(level_id));
                } else if !ready {
                    self.schedule_measurements.transport_blocked_beats += 1;
                } else {
                    self.schedule_measurements.inactive_level_beats += 1;
                }
                // Accumulate the original fractional timer duration and
                // skip overdue callbacks without duplicating requests.
                let elapsed = tick as f64 * f64::from(fixed_dt);
                let overdue = (elapsed - clock.next_due_seconds).max(0.0);
                let periods = (overdue / 0.25).floor() + 1.0;
                self.schedule_measurements.skipped_overdue_beats +=
                    (periods - 1.0).min(u64::MAX as f64) as u64;
                clock.next_due_seconds += periods * 0.25;
                clock.next_due_tick = source_command_ticks(
                    clock.next_due_seconds, fixed_dt,
                )?.max(tick.saturating_add(1));
            }
        }
        if self.due_levels.is_empty() { return Ok(()); }
        let result = host.issue_due_source_targeting(
            worker, usize::MAX, |id| self.due_levels.contains(&id),
        ).map_err(|error| io::Error::other(format!(
            "source HiveMind command pulse: {error:?}",
        )))?;
        self.measurements.eligible += result.eligible;
        self.measurements.issued += result.issued;
        self.measurements.out_of_bounds += result.out_of_bounds;
        self.measurements.home_moves += result.home_moves;
        self.measurements.home_unreachable += result.home_unreachable;
        self.measurements.unauthored_minions += result.unauthored_minions;
        self.measurements.enemy_type_scans += result.enemy_type_scans;
        self.measurements.enemy_type_scan_reuses += result.enemy_type_scan_reuses;
        Ok(())
    }

    /// Cap a synchronous inference window at the next source command
    /// boundary. Graphical windows are already one fixed tick at a time;
    /// a headless host must use this limit to preserve real-time command
    /// sequencing instead of stepping over the due timer.
    pub fn rounds_until_next_pulse<M, A, B, S>(
        &mut self,
        host: &NativeHostRuntime<M, A, B, S>,
        setup_complete: bool,
    ) -> io::Result<u64>
    where
        M: NativeLevelRewardMetadata + NativeLevelActivationContext +
            NativeLevelObservationContext,
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
        B: NativePolicyInference,
        S: NativeLevelNotificationSink,
    {
        if !setup_complete { return Ok(u64::MAX); }
        if host.simulation().world_count() != self.worlds.len() {
            return Err(invalid("source HiveMind scheduler World count changed"));
        }
        let mut limit = u64::MAX;
        for index in 0..self.worlds.len() {
            let world = host.simulation().world(index)
                .ok_or_else(|| invalid("registered native Level disappeared"))?;
            if world.is_failed() || world.state.events.is_failed() ||
                world.state.events.completed() || world.state.events.was_aborted() ||
                world.state.metadata.source_hivemind_active() != Some(true) {
                continue;
            }
            let initial = world.state.metadata.source_hivemind_initial_delay_seconds()
                .ok_or_else(|| invalid("source HiveMind delay missing from Level"))?;
            self.ensure_clock(index, world.episode_revision(), initial,
                world.state.events.roster().source_order())?;
            let due = self.worlds[index].clock.expect("initialized source clock").next_due_tick;
            limit = limit.min(due.saturating_sub(world.tick()).max(1));
        }
        Ok(limit)
    }
}
