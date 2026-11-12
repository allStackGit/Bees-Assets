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
use bees_gameplay::level_host::NativeLevelNotificationSink;
use bees_gameplay::squads::SquadRosterError;
use bees_hivemind::session::SquadLifetime;

use crate::policy_only_metadata::NativePolicyOnlyMetadata;
use crate::runtime::{NativeHostRuntime, NativeSourceCommandPulse};
use crate::socket_worker::NativeSocketWorker;

#[derive(Clone, Copy, Debug)]
struct SourceCommandClock {
    revision: u64,
    next_due_tick: u64,
    next_due_seconds: f64,
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
    fixed_dt_by_world: Vec<f32>,
    clocks: Vec<Option<SourceCommandClock>>,
    due_levels: HashSet<i64>,
    measurements: NativeSourceCommandPulse,
}

impl NativeSourceCommandScheduler {
    pub fn new(fixed_dt_by_world: &[f32]) -> io::Result<Self> {
        if fixed_dt_by_world.is_empty() {
            return Err(invalid("source HiveMind scheduler needs at least one Level"));
        }
        for &dt in fixed_dt_by_world {
            source_command_ticks(0.25, dt)?;
        }
        Ok(Self {
            fixed_dt_by_world: fixed_dt_by_world.to_vec(),
            clocks: vec![None; fixed_dt_by_world.len()],
            due_levels: HashSet::new(),
            measurements: NativeSourceCommandPulse::default(),
        })
    }

    pub fn measurements(&self) -> NativeSourceCommandPulse {
        self.measurements
    }

    fn ensure_clock(
        &mut self, index: usize, revision: u64, first_delay: i32,
    ) -> io::Result<()> {
        if self.clocks[index].is_none_or(|clock| clock.revision != revision) {
            // Unity SetupHivemind's initial one-shot timer is
            // (InitialDelay - 0.25) followed by recurring quarter seconds.
            // Even a zero initial delay starts at the first timer callback.
            let initial = f64::from(first_delay.max(0)).max(0.25);
            self.clocks[index] = Some(SourceCommandClock {
                revision,
                next_due_tick: source_command_ticks(initial, self.fixed_dt_by_world[index])?,
                next_due_seconds: initial,
            });
        }
        Ok(())
    }

    /// Tick one source timer per registered World, then ask the ONE host
    /// command owner to emit requests for due Levels. Transport setup
    /// readiness is passed by the caller; missed beats are not replayed
    /// in a burst after an outage.
    pub fn issue_due<A, B, S>(
        &mut self,
        host: &mut NativeHostRuntime<NativePolicyOnlyMetadata, A, B, S>,
        worker: &NativeSocketWorker,
        level_ids: &[i32],
        setup_complete: bool,
    ) -> io::Result<()>
    where
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
        B: NativePolicyInference,
        S: NativeLevelNotificationSink,
    {
        if level_ids.len() != self.clocks.len() ||
            host.simulation().world_count() != self.clocks.len() {
            return Err(invalid("source HiveMind scheduler World count changed"));
        }
        self.due_levels.clear();
        let ready = setup_complete && worker.ready();
        for index in 0..self.clocks.len() {
            let world = host.simulation().world(index)
                .ok_or_else(|| invalid("registered native Level disappeared"))?;
            let initial = world.state.metadata.hivemind_initial_delay_seconds
                .ok_or_else(|| invalid("source HiveMind delay missing from Level"))?;
            let tick = world.tick();
            self.ensure_clock(index, world.episode_revision(), initial)?;
            let clock = self.clocks[index].as_mut().expect("initialized source clock");
            if tick >= clock.next_due_tick {
                if ready && world.state.metadata.hivemind_active == Some(true) &&
                    !world.is_failed() && !world.state.events.is_failed() &&
                    !world.state.events.completed() && !world.state.events.was_aborted()
                {
                    self.due_levels.insert(i64::from(level_ids[index]));
                }
                // Accumulate the original fractional timer duration and
                // skip overdue callbacks without duplicating requests.
                let elapsed = tick as f64 * f64::from(self.fixed_dt_by_world[index]);
                let overdue = (elapsed - clock.next_due_seconds).max(0.0);
                let periods = (overdue / 0.25).floor() + 1.0;
                clock.next_due_seconds += periods * 0.25;
                clock.next_due_tick = source_command_ticks(
                    clock.next_due_seconds, self.fixed_dt_by_world[index],
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
        Ok(())
    }

    /// Cap a synchronous inference window at the next source command
    /// boundary. Graphical windows are already one fixed tick at a time;
    /// a headless host must use this limit to preserve real-time command
    /// sequencing instead of stepping over the due timer.
    pub fn rounds_until_next_pulse<A, B, S>(
        &mut self,
        host: &NativeHostRuntime<NativePolicyOnlyMetadata, A, B, S>,
        setup_complete: bool,
    ) -> io::Result<u64>
    where
        A: FnMut(SquadLifetime, i64) -> Result<SquadLifetime, SquadRosterError>,
        B: NativePolicyInference,
        S: NativeLevelNotificationSink,
    {
        if !setup_complete { return Ok(u64::MAX); }
        if host.simulation().world_count() != self.clocks.len() {
            return Err(invalid("source HiveMind scheduler World count changed"));
        }
        let mut limit = u64::MAX;
        for index in 0..self.clocks.len() {
            let world = host.simulation().world(index)
                .ok_or_else(|| invalid("registered native Level disappeared"))?;
            if world.is_failed() || world.state.events.is_failed() ||
                world.state.events.completed() || world.state.events.was_aborted() ||
                world.state.metadata.hivemind_active != Some(true) {
                continue;
            }
            let initial = world.state.metadata.hivemind_initial_delay_seconds
                .ok_or_else(|| invalid("source HiveMind delay missing from Level"))?;
            self.ensure_clock(index, world.episode_revision(), initial)?;
            let due = self.clocks[index].expect("initialized source clock").next_due_tick;
            limit = limit.min(due.saturating_sub(world.tick()).max(1));
        }
        Ok(limit)
    }
}
