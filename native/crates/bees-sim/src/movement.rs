//! Port of the policy-controlled ship movement path from
//! RlOneVsOneAgent.ApplyMovementCommand + Ship.RlDirectionalMovement +
//! Utilities.TimedRotationDifference.
//!
//! This module outputs body velocity and heading; the shared physics world
//! will own actual displacement, collision response and spatial queries.
//! No separate training-only movement implementation should exist.

use crate::control::MOVEMENT_DEAD_ZONE_SQUARED;
use crate::geometry::{heading_towards, ship_velocity, timed_ship_rotation, Vec2};

/// Unity's `RlMovementDirection == 360` means halt, not a heading of zero.
pub const STOP_MOVEMENT: u16 = 360;

#[derive(Clone, Debug, PartialEq)]
pub struct ShipMotion {
    pub position: Vec2,
    pub rotation_degrees: f32,
    pub rotation_speed_degrees_per_second: f32,
    pub current_speed: f32,
    pub body_velocity: Vec2,
    pub height: f32,
    pub mobile: bool,
    pub cannot_change_movement_orders: bool,
    pub user_controlled: bool,
    pub has_target_coordinates: bool,
    pub target_coordinates: Vec2,
    pub is_policy_controlled: bool,
    pub is_moving: bool,
    pub movement_direction: u16,
}

impl ShipMotion {
    /// The caller supplies authored ship stats, including current speed
    /// (which may differ temporarily from the prefab's base speed).
    pub fn new(
        position: Vec2,
        rotation_degrees: f32,
        rotation_speed_degrees_per_second: f32,
        current_speed: f32,
        height: f32,
    ) -> Self {
        Self {
            position,
            rotation_degrees,
            rotation_speed_degrees_per_second,
            current_speed,
            body_velocity: Vec2::ZERO,
            height,
            mobile: current_speed > 0.0,
            cannot_change_movement_orders: false,
            user_controlled: false,
            has_target_coordinates: false,
            target_coordinates: Vec2::ZERO,
            is_policy_controlled: false,
            is_moving: false,
            movement_direction: STOP_MOVEMENT,
        }
    }

    /// Implements ApplyMovementCommand. Unity's Mathf.RoundToInt uses
    /// ties-to-even rounding and wraps the integer heading into [0, 359].
    pub fn accept_policy_movement(&mut self, world_movement: Vec2) {
        if !self.mobile || self.cannot_change_movement_orders {
            self.is_policy_controlled = true;
            // Retain the previously issued direction during movement locks,
            // e.g. while a special ability temporarily takes control.
            return;
        }

        self.movement_direction =
            if world_movement.length_squared() < MOVEMENT_DEAD_ZONE_SQUARED {
                STOP_MOVEMENT
            } else {
                heading_towards(world_movement)
                    .round_ties_even()
                    .rem_euclid(360.0) as u16
            };
        self.is_policy_controlled = true;
    }

    /// Unity's stationarity curriculum option stops motion immediately
    /// without disabling weapon aim, weapon fire, or communication.
    pub fn disable_policy_movement(&mut self) {
        self.movement_direction = STOP_MOVEMENT;
        self.body_velocity = Vec2::ZERO;
        self.is_moving = false;
        self.is_policy_controlled = true;
    }

    /// The movement portion of gameplay's StopMoving() contract.
    pub fn stop_moving(&mut self) {
        self.body_velocity = Vec2::ZERO;
        self.is_moving = false;
        if self.is_policy_controlled && !self.user_controlled {
            self.movement_direction = STOP_MOVEMENT;
        }
    }

    /// Called on every fixed simulation update, NOT only at PPO decision
    /// boundaries. Returns false if the ship is using the ordinary user/Hive
    /// Mind controller and must be handled by the shared gameplay movement
    /// implementation instead.
    ///
    /// Applies the same three-degree turning dead band, timed turn rate,
    /// target-coordinate suppression, and current-speed velocity as Unity.
    /// Position integration deliberately remains the physics engine's job.
    pub fn fixed_step_rl(&mut self, dt_seconds: f32) -> bool {
        if !self.is_policy_controlled || self.user_controlled {
            return false;
        }
        if self.movement_direction == STOP_MOVEMENT {
            self.body_velocity = Vec2::ZERO;
            self.is_moving = false;
            return true;
        }

        let difference = self.target_coordinates - self.position;
        if !self.has_target_coordinates || difference.length_squared().sqrt() > self.height {
            self.rotation_degrees = timed_ship_rotation(
                self.rotation_degrees,
                f32::from(self.movement_direction),
                self.rotation_speed_degrees_per_second,
                dt_seconds,
            );
        }

        self.body_velocity = ship_velocity(self.current_speed, self.rotation_degrees);
        self.is_moving = true;
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn RL_direction_is_rounded_using_bees_heading_convention() {
        let mut motion = ShipMotion::new(Vec2::ZERO, 0.0, 90.0, 6.0, 1.0);
        motion.accept_policy_movement(Vec2::new(1.0, 0.0));
        assert_eq!(motion.movement_direction, 270);
        assert!(motion.fixed_step_rl(0.02));
        assert!((motion.rotation_degrees + 1.8).abs() < 0.00001);
    }

    #[test]
    fn dead_zone_stops_without_forgetting_the_policy_controller() {
        let mut motion = ShipMotion::new(Vec2::ZERO, 0.0, 90.0, 6.0, 1.0);
        motion.accept_policy_movement(Vec2::new(1.0, 0.0));
        motion.accept_policy_movement(Vec2::new(0.1, 0.1));
        assert_eq!(motion.movement_direction, STOP_MOVEMENT);
        assert!(motion.fixed_step_rl(0.02));
        assert_eq!(motion.body_velocity, Vec2::ZERO);
        assert!(!motion.is_moving);
        assert!(motion.is_policy_controlled);
    }

    #[test]
    fn speed_changes_and_movement_lock_follow_authored_state() {
        let mut motion = ShipMotion::new(Vec2::ZERO, 0.0, 90.0, 6.0, 1.0);
        motion.accept_policy_movement(Vec2::new(0.0, 1.0));
        motion.cannot_change_movement_orders = true;
        motion.accept_policy_movement(Vec2::new(1.0, 0.0));
        assert_eq!(motion.movement_direction, 0);
        motion.current_speed = 14.0;
        motion.fixed_step_rl(0.02);
        assert!((motion.body_velocity.y - 14.0).abs() < 0.00001);
    }

    #[test]
    fn close_target_suppresses_rotation_but_still_moves() {
        let mut motion = ShipMotion::new(Vec2::ZERO, 0.0, 90.0, 6.0, 2.0);
        motion.accept_policy_movement(Vec2::new(1.0, 0.0));
        motion.has_target_coordinates = true;
        motion.target_coordinates = Vec2::new(0.5, 0.0);
        motion.fixed_step_rl(0.02);
        assert_eq!(motion.rotation_degrees, 0.0);
        assert!(motion.is_moving);
    }
}
