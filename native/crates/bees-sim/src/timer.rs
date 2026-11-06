//! Portable representation of the existing ScaledTimer update contract.
//!
//! The caller supplies elapsed simulation time instead of reading a global
//! Unity clock. This is shared game logic used by gameplay and training.
//! No callbacks are stored in timers: owners retain their own action data.
//!
//! A callback can reuse the timer. The two-phase API preserves Unity's
//! generation guard: the completed old interval must not be subtracted
//! from a newly reused timer.

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ScaledTimer {
    pub length_seconds: f32,
    pub elapsed_seconds: f32,
    pub recurring: bool,
    pub canceled: bool,
    pub start_immediate: bool,
    generation: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum DueKind {
    Immediate,
    Elapsed,
}

#[must_use = "finish the timer interval after dispatching its callback"]
#[derive(Clone, Copy, Debug)]
pub struct TimerDue {
    generation: u64,
    completed_length_seconds: f32,
    kind: DueKind,
}

impl ScaledTimer {
    pub fn new(length_seconds: f32, recurring: bool, start_immediate: bool) -> Self {
        Self {
            length_seconds,
            elapsed_seconds: 0.0,
            recurring,
            canceled: false,
            start_immediate,
            generation: 0,
        }
    }

    pub fn reuse(&mut self, length_seconds: f32, recurring: bool, start_immediate: bool) {
        self.length_seconds = length_seconds;
        self.elapsed_seconds = 0.0;
        self.recurring = recurring;
        self.canceled = false;
        self.start_immediate = start_immediate;
        self.generation = self.generation.wrapping_add(1);
    }

    pub fn cancel(&mut self) {
        self.canceled = true;
    }

    /// Read-only preview of begin_update's callback condition. Callers can
    /// avoid preparing costly command inputs on frames when a timer will not
    /// run. This must use exactly the same strict expiry and immediate
    /// semantics as begin_update, without advancing elapsed time.
    pub fn will_fire(&self, delta_seconds: f32) -> bool {
        !self.canceled && (self.start_immediate ||
            self.elapsed_seconds + delta_seconds > self.length_seconds)
    }

    /// Each caller supplies the same clock delta for gameplay and headless
    /// operation. Unity used Time.deltaTime and strict elapsed > length,
    /// with at most one callback per Update invocation.
    pub fn begin_update(&mut self, delta_seconds: f32) -> Option<TimerDue> {
        if self.canceled {
            return None;
        }
        self.elapsed_seconds += delta_seconds;
        if self.start_immediate {
            self.start_immediate = false;
            self.elapsed_seconds = 0.0;
            return Some(TimerDue {
                generation: self.generation,
                completed_length_seconds: self.length_seconds,
                kind: DueKind::Immediate,
            });
        }
        if self.elapsed_seconds > self.length_seconds {
            return Some(TimerDue {
                generation: self.generation,
                completed_length_seconds: self.length_seconds,
                kind: DueKind::Elapsed,
            });
        }
        None
    }

    /// Invoke the timer owner's callback between begin_update and finish_update.
    /// Callbacks may invoke timer.reuse, which starts a distinct generation.
    pub fn finish_update(&mut self, completed: TimerDue) {
        if completed.kind == DueKind::Elapsed && self.generation == completed.generation {
            self.elapsed_seconds -= completed.completed_length_seconds;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn immediate_timer_runs_without_double_interval_delay() {
        let mut timer = ScaledTimer::new(1.0, true, true);
        let due = timer.begin_update(0.02).unwrap();
        timer.finish_update(due);
        assert_eq!(timer.elapsed_seconds, 0.0);
        assert!(timer.begin_update(0.5).is_none());
        let due = timer.begin_update(0.6).unwrap();
        timer.finish_update(due);
        assert!((timer.elapsed_seconds - 0.1).abs() < 0.000001);
    }

    #[test]
    fn strict_expiry_and_reuse_generation_are_preserved() {
        let mut timer = ScaledTimer::new(0.5, true, false);
        assert!(timer.begin_update(0.5).is_none());
        let due = timer.begin_update(0.01).unwrap();
        timer.reuse(2.0, true, false);
        timer.finish_update(due);
        assert_eq!(timer.elapsed_seconds, 0.0);
        assert_eq!(timer.length_seconds, 2.0);
    }

    #[test]
    fn cancel_prevents_callback() {
        let mut timer = ScaledTimer::new(0.0, false, false);
        timer.cancel();
        assert!(timer.begin_update(1.0).is_none());
    }
}
