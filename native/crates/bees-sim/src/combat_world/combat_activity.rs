//! Source Ship.SetCombatTimer / CombatTimer, shared by every native weapon.
use super::*;

#[derive(Clone, Copy, Debug)]
pub struct NativeCombatActivity {
    pub enabled: bool,
    pub window_seconds: f32,
    pub elapsed_seconds: Option<f32>,
}
impl NativeCombatActivity {
    pub fn valid(self) -> bool {
        self.window_seconds.is_finite() && self.window_seconds > 0.0 &&
            self.elapsed_seconds.is_none_or(|elapsed| elapsed.is_finite() && elapsed >= 0.0)
    }
}
impl CombatWorld {
    pub fn configure_combat_activity(&mut self, ship_id: i64, source: NativeCombatActivity)
        -> Result<(), CombatWorldError>
    {
        if !source.valid() || !self.ship_by_id.contains_key(&ship_id) {
            return Err(CombatWorldError::MissingAutomaticTargetingInputs);
        }
        self.combat_activity.insert(ship_id, source);
        Ok(())
    }

    /// None means the actual source timer metadata was not supplied.
    pub fn ship_in_combat(&self, ship_id: i64) -> Option<bool> {
        let activity = self.combat_activity.get(&ship_id)?;
        Some(activity.elapsed_seconds.is_some() && self.ship(ship_id)
            .is_some_and(|ship| !ship.vitals.is_dead()))
    }

    pub(super) fn advance_combat_activity(&mut self, dt: f32) {
        for state in self.combat_activity.values_mut() {
            if let Some(elapsed) = &mut state.elapsed_seconds {
                *elapsed += dt;
                // ScaledTimer.Update uses strict >, not >=.
                if *elapsed > state.window_seconds { state.elapsed_seconds = None; }
            }
        }
    }

    pub(super) fn record_combat_activity(&mut self, ship_id: i64) {
        if let Some(state) = self.combat_activity.get_mut(&ship_id) {
            if state.enabled { state.elapsed_seconds = Some(0.0); }
        }
    }
}
