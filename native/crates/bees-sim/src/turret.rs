//! Base turret's RL aiming and authored targeting-timer state machine.
//!
//! From Turret.Aiming, Turret.Targeting, Turret.RlControl and
//! Utilities.TimedRotationDifference(Turret). This belongs to common gameplay
//! rules, not to a Python or headless-only training adapter.
//!
//! Specialized weapon types (e.g. FullShipTurret) still require their own
//! firing eligibility and projectile behavior ports; do not infer their
//! semantics from the default turret gate.

use crate::control::WeaponRequest;
use crate::geometry::{heading_towards, timed_turret_rotation, Vec2};

pub const TARGETING_PASSES_PER_SHOT: u8 = 3;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TurretState {
    pub rotation_degrees: f32,
    pub rotation_rate_degrees_per_second: f32,
    pub target_world: Vec2,
    pub is_rl_controlled: bool,
    pub fire_requested: bool,
    pub is_aligned_with_target: bool,
    pub targeting_passes: u8,
    pub ready_to_fire: bool,
}

impl TurretState {
    pub fn new(rotation_degrees: f32, rotation_rate_degrees_per_second: f32) -> Self {
        Self {
            rotation_degrees,
            rotation_rate_degrees_per_second,
            target_world: Vec2::ZERO,
            is_rl_controlled: false,
            fire_requested: false,
            is_aligned_with_target: false,
            targeting_passes: 0,
            ready_to_fire: false,
        }
    }

    /// Unity rotates each mounted turret's logical heading with the hull.
    /// This method consumes ShipMotion's rotation delta each fixed tick.
    #[inline]
    pub fn apply_hull_turn(&mut self, rotation_delta_degrees: f32) {
        self.rotation_degrees += rotation_delta_degrees;
    }

    /// Implements Turret.SetRlControl + RlOneVsOneAgent.ApplyWeaponCommand.
    /// The weapon range is the authored gameplay range, not a training value.
    pub fn accept_policy_request(
        &mut self,
        request: WeaponRequest,
        turret_position: Vec2,
        authored_range: f32,
    ) {
        if !request.available {
            return;
        }
        self.is_rl_controlled = true;
        self.target_world = turret_position + request.aim_world * authored_range.max(1.0);
        self.fire_requested = request.fire_requested;
    }

    /// Called every physics tick while fire is not ceased. The original
    /// turret snaps to exact alignment once inside its three-degree band.
    /// Returns false when a non-RL controller must own ordinary targeting.
    pub fn aim_fixed_step(&mut self, turret_position: Vec2, dt_seconds: f32, cease_fire: bool) -> bool {
        if !self.is_rl_controlled {
            return false;
        }
        if cease_fire {
            // Turret.FixedUpdate does not call Aim() at all while cease-fired.
            return true;
        }
        self.aim_at_point(turret_position, dt_seconds);
        true
    }

    /// Both source manual and policy aiming use the same physical rotation.
    pub fn aim_at_point(&mut self, turret_position: Vec2, dt_seconds: f32) {
        let heading = heading_towards(self.target_world - turret_position);
        let (updated, aligned) = timed_turret_rotation(
            self.rotation_degrees,
            heading,
            self.rotation_rate_degrees_per_second,
            dt_seconds,
        );
        self.rotation_degrees = updated;
        self.is_aligned_with_target = aligned;
    }

    /// Ordinary manual Turret.TargetingSequence does not latch a missed
    /// firing opportunity like RL. Every third pass resets even when unaligned.
    pub fn manual_targeting_timer_pass(&mut self, manual: bool, cease_fire: bool) -> bool {
        self.targeting_passes += 1;
        if (self.ready_to_fire && self.is_aligned_with_target) ||
            self.targeting_passes == TARGETING_PASSES_PER_SHOT {
            self.targeting_passes = 0;
            return manual && self.is_aligned_with_target && !cease_fire;
        }
        false
    }

    /// The Level scheduler calls this at the authored TargetingRate interval
    /// (RateOfFire / 3). It is NOT called once per fixed tick. `may_fire` is
    /// the weapon-specific CanAcceptRlFireRequest result: base turrets use
    /// is_aligned_with_target; FullShipTurret overrides it to true.
    ///
    /// Returns true exactly when the shared weapon subsystem should execute
    /// FireAtPoint(), including its projectile and combat telemetry effects.
    pub fn targeting_timer_pass(&mut self, may_fire: bool, cease_fire: bool) -> bool {
        if !self.is_rl_controlled {
            return false;
        }
        if !self.ready_to_fire {
            self.targeting_passes = self.targeting_passes.saturating_add(1)
                .min(TARGETING_PASSES_PER_SHOT);
            if self.targeting_passes >= TARGETING_PASSES_PER_SHOT {
                self.ready_to_fire = true;
            }
        }
        if self.ready_to_fire && self.fire_requested && may_fire && !cease_fire {
            self.ready_to_fire = false;
            self.targeting_passes = 0;
            return true;
        }
        false
    }

    /// Mirrors Turret.ClearRlControl and Turret.ClearData when reused.
    pub fn reset(&mut self, original_rotation: f32) {
        self.rotation_degrees = original_rotation;
        self.target_world = Vec2::ZERO;
        self.is_rl_controlled = false;
        self.fire_requested = false;
        self.is_aligned_with_target = false;
        self.targeting_passes = 0;
        self.ready_to_fire = false;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_ready_turret_holds_its_fire_opportunity_until_requested() {
        let mut turret = TurretState::new(0.0, 90.0);
        turret.is_rl_controlled = true;
        for _ in 0..TARGETING_PASSES_PER_SHOT {
            assert!(!turret.targeting_timer_pass(true, false));
        }
        assert!(turret.ready_to_fire);
        assert_eq!(turret.targeting_passes, TARGETING_PASSES_PER_SHOT);
        turret.fire_requested = true;
        assert!(turret.targeting_timer_pass(true, false));
        assert!(!turret.ready_to_fire);
        assert_eq!(turret.targeting_passes, 0);
    }

    #[test]
    fn cease_fire_and_alignment_gate_are_distinct() {
        let mut turret = TurretState::new(0.0, 90.0);
        turret.is_rl_controlled = true;
        turret.fire_requested = true;
        for _ in 0..TARGETING_PASSES_PER_SHOT {
            turret.targeting_timer_pass(false, false);
        }
        assert!(!turret.targeting_timer_pass(false, false));
        assert!(!turret.targeting_timer_pass(true, true));
        assert!(turret.targeting_timer_pass(true, false));
    }

    #[test]
    fn base_turret_aim_snaps_when_aligned_and_preserves_hull_turn() {
        let mut turret = TurretState::new(45.0, 90.0);
        turret.accept_policy_request(
            WeaponRequest {
                aim_world: Vec2::new(0.0, 1.0),
                available: true,
                ..WeaponRequest::default()
            },
            Vec2::ZERO,
            20.0,
        );
        assert!(turret.aim_fixed_step(Vec2::ZERO, 0.02, false));
        assert!((turret.rotation_degrees - 43.2).abs() < 0.00001);
        turret.rotation_degrees = 1.0;
        turret.aim_fixed_step(Vec2::ZERO, 0.02, false);
        assert_eq!(turret.rotation_degrees, 0.0);
        assert!(turret.is_aligned_with_target);
        turret.apply_hull_turn(10.0);
        assert_eq!(turret.rotation_degrees, 10.0);
    }
}
