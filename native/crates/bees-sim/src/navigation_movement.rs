//! Ordinary user/Hive Mind ship steering, sharing ShipMotion/physics with RL.
//!
//! Ported from Ship.Movement.SetMovementVelocity, MoveToTargetCoordinates,
//! MergePathfindingPaths, SkipClosePathWaypoints and EndDestination.
//! This does not choose orders, target ships, squad commands or whether a
//! direct route is blocked. Those game controllers remain separate, and they
//! must supply the real ShipTurningRadius and sprite half-extents.

use std::collections::VecDeque;

use crate::geometry::{
    heading_towards, ship_velocity, timed_ship_rotation, Vec2,
};
use crate::movement::{MotionUpdate, ShipMotion};
use crate::navigation_search::NavigationPath;
use crate::timer::ScaledTimer;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct GameMovementProfile {
    /// ConfigData.ShipTurningRadius, calculated from RotationMultiplier.
    pub turning_radius: f32,
    /// Ship.GetHalfWidth/GetHalfHeight (sprite dimensions, not a guess
    /// derived from the physics hull).
    pub half_width: f32,
    pub half_height: f32,
}

impl GameMovementProfile {
    pub fn valid(self) -> bool {
        self.turning_radius.is_finite() && self.turning_radius > 0.0 &&
        self.half_width.is_finite() && self.half_width > 0.0 &&
        self.half_height.is_finite() && self.half_height > 0.0
    }

    fn waypoint_radius(self) -> f32 {
        self.turning_radius.max(self.half_width.min(self.half_height) * 0.75)
    }
}

/// Route and direct orders are per-ship. The underlying ShipMotion remains
/// the source of truth for pose, velocity, turning rate and speed.
#[derive(Clone, Debug)]
pub struct GameShipSteering {
    pub profile: GameMovementProfile,
    pub final_destination: Option<Vec2>,
    pub is_following_path: bool,
    pub remaining_egress_waypoints: usize,
    next_waypoints: VecDeque<Vec2>,
    target_direction: Option<f32>,
    deferred_direct_destination: Option<Vec2>,
    path_failures: u8,
    failed_path_retry_timer: Option<ScaledTimer>,
    /// One-second tracked A* repath gate, using the same scaled fixed step.
    tracked_replan_wait_seconds: f32,
}

impl GameShipSteering {
    pub fn new(profile: GameMovementProfile) -> Option<Self> {
        if !profile.valid() { return None; }
        Some(Self {
            profile, final_destination: None,
            is_following_path: false, remaining_egress_waypoints: 0,
            next_waypoints: VecDeque::new(), target_direction: None,
            deferred_direct_destination: None,
            path_failures: 0, failed_path_retry_timer: None,
            tracked_replan_wait_seconds: 0.0,
        })
    }

    /// Unity's tracked target orders replan a still-followed A* route at
    /// most once per second. Direct movement updates are not rate-limited.
    pub fn tracked_replan_ready(&self) -> bool {
        self.tracked_replan_wait_seconds <= 0.0
    }

    pub fn arm_tracked_replan(&mut self) {
        self.tracked_replan_wait_seconds = 1.0;
    }

    pub fn clear_tracked_replan(&mut self) {
        self.tracked_replan_wait_seconds = 0.0;
    }

    /// Direct movement order after the game controller has applied the
    /// Level bounds and checked that no pathfinding is needed.
    pub fn set_direct_destination(
        &mut self,
        motion: &mut ShipMotion,
        destination: Vec2,
    ) -> bool {
        if !motion.mobile || !destination.is_finite() ||
            (motion.is_policy_controlled && !motion.user_controlled) {
            return false;
        }
        if motion.cannot_change_movement_orders {
            self.deferred_direct_destination = Some(destination);
            return false;
        }
        self.next_waypoints.clear();
        self.remaining_egress_waypoints = 0;
        self.is_following_path = false;
        self.final_destination = Some(destination);
        self.target_direction = None;
        self.deferred_direct_destination = None;
        self.failed_path_retry_timer = None;
        self.path_failures = 0;
        motion.target_coordinates = destination;
        motion.has_target_coordinates = true;
        true
    }

    /// MoveInDirection is also a normal gameplay controller, not an RL
    /// policy action. It clears the old navigation destination.
    pub fn set_direction(&mut self, motion: &mut ShipMotion, direction: f32) -> bool {
        if !motion.mobile || motion.cannot_change_movement_orders ||
            !direction.is_finite() ||
            (motion.is_policy_controlled && !motion.user_controlled) {
            return false;
        }
        self.stop(motion);
        self.target_direction = Some(direction);
        true
    }

    /// Ship.StopMoving clears both active and queued waypoints, and
    /// immediately zeros velocity. Stopping a policy-controlled ship still
    /// uses ShipMotion's established STOP_MOVEMENT sentinel.
    pub fn stop(&mut self, motion: &mut ShipMotion) {
        self.next_waypoints.clear();
        self.target_direction = None;
        self.final_destination = None;
        self.deferred_direct_destination = None;
        self.failed_path_retry_timer = None;
        self.remaining_egress_waypoints = 0;
        self.is_following_path = false;
        self.clear_tracked_replan();
        motion.has_target_coordinates = false;
        motion.target_coordinates = Vec2::ZERO;
        motion.stop_moving();
    }

    /// Used when the game's controller hands ownership back to the neural
    /// policy. Clear only orders issued through this gameplay route
    /// controller, not arbitrary RL movement/target state.
    pub fn has_game_order(&self) -> bool {
        self.final_destination.is_some() ||
            self.target_direction.is_some() ||
            self.deferred_direct_destination.is_some() ||
            self.is_following_path ||
            self.failed_path_retry_timer.is_some() ||
            !self.next_waypoints.is_empty()
    }

    /// Unity ClearPreviousDesintation when an actual path request begins.
    /// The old physics velocity is deliberately retained: Unity clears
    /// movement orders but does not immediately zero Rigidbody velocity.
    pub fn await_path(&mut self, motion: &mut ShipMotion) {
        self.next_waypoints.clear();
        self.target_direction = None;
        self.deferred_direct_destination = None;
        self.failed_path_retry_timer = None;
        self.final_destination = None;
        self.remaining_egress_waypoints = 0;
        self.is_following_path = false;
        motion.has_target_coordinates = false;
    }

    /// Called only after request and lifecycle validation. The returned
    /// path excludes its original start cell, as in Unity. Empty search
    /// results cannot become valid movement orders.
    pub fn install_path(
        &mut self,
        motion: &mut ShipMotion,
        path: &NavigationPath,
    ) -> bool {
        if !motion.mobile || path.points.is_empty() ||
            (motion.is_policy_controlled && !motion.user_controlled) ||
            path.points.iter().any(|point| !point.is_finite()) {
            return false;
        }
        self.next_waypoints.clear();
        self.next_waypoints.extend(path.points.iter().copied());
        self.final_destination = path.points.last().copied();
        self.remaining_egress_waypoints = path.egress_point_count;
        self.is_following_path = true;
        self.target_direction = None;
        self.deferred_direct_destination = None;
        self.failed_path_retry_timer = None;
        self.path_failures = 0;
        self.skip_close_waypoints(motion.position);
        self.advance_waypoint(motion);
        true
    }

    /// Unity MergePathfindingPaths retries failed routes at most five
    /// times, separated by two seconds of ScaledTimer simulation time.
    pub fn failed_path(&mut self, motion: &mut ShipMotion) -> bool {
        self.stop(motion);
        if self.path_failures >= 5 {
            self.path_failures = 0;
            return false;
        }
        self.path_failures += 1;
        self.failed_path_retry_timer = Some(ScaledTimer::new(2.0, false, false));
        true
    }

    /// Returns true once on strict elapsed >2s; the Level re-runs
    /// MoveToPoint and its live box cast instead of blindly retrying A*.
    pub fn advance_failed_path_retry(&mut self, dt: f32) -> bool {
        let Some(timer) = self.failed_path_retry_timer.as_mut() else {
            return false;
        };
        let Some(due) = timer.begin_update(dt) else { return false };
        timer.finish_update(due);
        self.failed_path_retry_timer = None;
        true
    }

    pub fn has_pending_path_retry(&self) -> bool {
        self.failed_path_retry_timer.is_some()
    }

    fn skip_close_waypoints(&mut self, current_position: Vec2) {
        let radius_sq = self.profile.waypoint_radius().powi(2);
        while self.remaining_egress_waypoints == 0 &&
            self.next_waypoints.len() > 1 &&
            self.next_waypoints.front().is_some_and(|front|
                (*front - current_position).length_squared() < radius_sq) {
            self.next_waypoints.pop_front();
        }
    }

    fn advance_waypoint(&mut self, motion: &mut ShipMotion) {
        if let Some(next) = self.next_waypoints.pop_front() {
            motion.target_coordinates = next;
            motion.has_target_coordinates = true;
            if self.remaining_egress_waypoints > 0 {
                self.remaining_egress_waypoints -= 1;
            }
        } else {
            self.stop(motion);
        }
    }

    /// Called each fixed tick, before shared physics integrates position.
    /// Like Unity, the ship turns by its current RotationSpeed when outside
    /// the three-degree band, then moves at its mutable CurrentSpeed.
    /// A waypoint is consumed only within the runtime ShipTurningRadius.
    ///
    /// RL-controlled non-user ships continue through ShipMotion.fixed_step_rl
    /// instead; they must never be steered by a second training-only system.
    pub fn fixed_step(
        &mut self,
        motion: &mut ShipMotion,
        dt: f32,
    ) -> MotionUpdate {
        // Most game ships never follow a moving A* target. Keep their
        // fixed-step hot path free of needless cooldown arithmetic.
        if self.tracked_replan_wait_seconds > 0.0 {
            self.tracked_replan_wait_seconds =
                (self.tracked_replan_wait_seconds - dt).max(0.0);
        }
        if !motion.mobile ||
            (motion.is_policy_controlled && !motion.user_controlled) {
            return MotionUpdate::NotPolicyControlled;
        }
        if !motion.cannot_change_movement_orders {
            if let Some(destination) = self.deferred_direct_destination.take() {
                self.set_direct_destination(motion, destination);
            }
        }
        let desired_heading = if motion.has_target_coordinates {
            let difference = motion.target_coordinates - motion.position;
            Some(heading_towards(difference))
        } else {
            self.target_direction
        };
        let Some(desired_heading) = desired_heading else {
            return MotionUpdate::NotPolicyControlled;
        };
        let old_rotation = motion.rotation_degrees;
        motion.rotation_degrees = timed_ship_rotation(
            motion.rotation_degrees, desired_heading,
            motion.rotation_speed_degrees_per_second, dt,
        );
        motion.body_velocity = ship_velocity(motion.current_speed, motion.rotation_degrees);
        motion.is_moving = true;
        if motion.has_target_coordinates &&
            (motion.target_coordinates - motion.position).length_squared() <
                self.profile.turning_radius.powi(2) {
            self.skip_close_waypoints(motion.position);
            self.advance_waypoint(motion);
        }
        MotionUpdate::Applied {
            rotation_delta_degrees: motion.rotation_degrees - old_rotation,
        }
    }
}
