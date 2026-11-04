//! The RL policy's controller-to-world contract. Both the game and native
//! training worker use these same routines. This is not a substitute for
//! gameplay-side turret mechanics or the policy observation encoder.
//!
//! Sources: RlOneVsOneAgent.OnActionReceived, ApplyMovementCommand,
//! SetCommunicationActions, ResetWeaponAimDirections and
//! RlPolicyCoordinateFrame on feat/experiment-queue-ui.

use crate::geometry::Vec2;
use crate::policy::{Action, ActionError, COMMUNICATION_ACTION_OFFSET, WEAPON_SLOTS};

pub const MOVEMENT_DEAD_ZONE_SQUARED: f32 = 0.2 * 0.2;
pub const AIM_DEAD_ZONE_SQUARED: f32 = 0.1 * 0.1;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct PolicyFrame {
    quarter_turns: u8,
}

impl PolicyFrame {
    /// Accept the same modulo-four frame values as Unity's frame helper.
    pub const fn new(quarter_turns: u8) -> Self {
        Self { quarter_turns: quarter_turns % 4 }
    }

    pub const fn quarter_turns(self) -> u8 {
        self.quarter_turns
    }

    #[inline]
    pub fn policy_to_world(self, v: Vec2) -> Vec2 {
        match self.quarter_turns {
            1 => Vec2::new(v.y, -v.x),
            2 => Vec2::new(-v.x, -v.y),
            3 => Vec2::new(-v.y, v.x),
            _ => v,
        }
    }

    #[inline]
    pub fn world_to_policy(self, v: Vec2) -> Vec2 {
        match self.quarter_turns {
            1 => Vec2::new(-v.y, v.x),
            2 => Vec2::new(-v.x, -v.y),
            3 => Vec2::new(v.y, -v.x),
            _ => v,
        }
    }
}

/// Maps one of twelve equiprobable indices to the ordered distinct pair of
/// quarter-turn frames used for opposing self-play teams.
pub fn distinct_team_frames(pair_index: u8) -> (PolicyFrame, PolicyFrame) {
    let index = pair_index % 12;
    let team0 = index / 3;
    let remaining = index % 3;
    let team1 = if remaining >= team0 { remaining + 1 } else { remaining };
    (PolicyFrame::new(team0), PolicyFrame::new(team1))
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[repr(u8)]
pub enum SpecialAction {
    None = 0,
    Ship = 1,
    Mining = 2,
    Healing = 3,
    Warp = 4,
}

impl SpecialAction {
    fn from_policy(value: u8) -> Self {
        match value {
            1 => Self::Ship,
            2 => Self::Mining,
            3 => Self::Healing,
            4 => Self::Warp,
            _ => Self::None,
        }
    }
}

/// The final turret firing decision. Cooldown, alignment and actual firing
/// are still determined by the authoritative weapon mechanics, not this layer.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct WeaponRequest {
    pub aim_world: Vec2,
    pub fire_requested: bool,
    pub available: bool,
    pub fresh_aim: bool,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct DecodedPolicyAction {
    pub movement_world: Vec2,
    pub weapons: [WeaponRequest; WEAPON_SLOTS],
    pub communication: [f32; 4],
    pub special: SpecialAction,
    pub fresh_aim_slot_mask: u8,
}

/// Each controlled ship owns its aim memory. Unity retains the last aim
/// through the policy's 0.1 dead zone rather than resetting aim each step.
#[derive(Clone, Debug, PartialEq)]
pub struct PolicyControl {
    held_weapon_aim: [Vec2; WEAPON_SLOTS],
    communication: [f32; 4],
}

impl Default for PolicyControl {
    fn default() -> Self {
        Self {
            held_weapon_aim: [Vec2::ZERO; WEAPON_SLOTS],
            communication: [0.0; 4],
        }
    }
}

impl PolicyControl {
    pub fn new() -> Self {
        Self::default()
    }

    /// Bind an actual physical turret direction at spawn/rebind, exactly as
    /// Unity does. Passing zero leaves the original position-based fallback
    /// (positive X) to avoid manufacturing a policy-frame up direction.
    pub fn bind_turret_aim(&mut self, slot: usize, world_aim: Vec2) {
        if let Some(held) = self.held_weapon_aim.get_mut(slot) {
            let normalized = world_aim.normalized_or_zero();
            *held = if normalized.length_squared() < 0.000001 {
                Vec2::new(1.0, 0.0)
            } else {
                normalized
            };
        }
    }

    pub fn communication(&self) -> [f32; 4] {
        self.communication
    }

    pub fn reset_for_new_ship(&mut self) {
        *self = Self::new();
    }

    /// Validation occurs before any mutable state is updated. Callers may
    /// forward the decoded action to a gameplay controller or headless world;
    /// both use identical semantics and no allocations occur in this call.
    pub fn decode(
        &mut self,
        action: &Action,
        frame: PolicyFrame,
        available_turrets: &[bool; WEAPON_SLOTS],
    ) -> Result<DecodedPolicyAction, ActionError> {
        action.validate()?;
        let c = &action.continuous;
        let d = &action.discrete;

        let special = SpecialAction::from_policy(d[WEAPON_SLOTS]);
        let movement_world = frame.policy_to_world(Vec2::new(c[0], c[1]));
        let mut weapons = [WeaponRequest::default(); WEAPON_SLOTS];
        let mut fresh_aim_slot_mask = 0u8;
        for slot in 0..WEAPON_SLOTS {
            let base = 2 + slot * 2;
            let aim = Vec2::new(c[base], c[base + 1]);
            let fresh_aim = aim.length_squared() >= AIM_DEAD_ZONE_SQUARED;
            if fresh_aim {
                self.held_weapon_aim[slot] = frame.policy_to_world(aim.normalized_or_zero());
                fresh_aim_slot_mask |= 1 << slot;
            }
            weapons[slot] = WeaponRequest {
                aim_world: self.held_weapon_aim[slot],
                available: available_turrets[slot],
                fire_requested: available_turrets[slot]
                    && special != SpecialAction::Healing
                    && d[slot] == 1,
                fresh_aim,
            };
        }

        for index in 0..4 {
            self.communication[index] = c[COMMUNICATION_ACTION_OFFSET + index].clamp(-1.0, 1.0);
        }

        Ok(DecodedPolicyAction {
            movement_world,
            weapons,
            communication: self.communication,
            special,
            fresh_aim_slot_mask,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_frame_has_an_inverse_and_distinct_self_play_pair() {
        let v = Vec2::new(3.0, -2.0);
        for index in 0..12 {
            let (left, right) = distinct_team_frames(index);
            assert_ne!(left, right);
            assert_eq!(left.world_to_policy(left.policy_to_world(v)), v);
            assert_eq!(right.world_to_policy(right.policy_to_world(v)), v);
        }
    }

    #[test]
    fn aim_is_held_and_healing_prevents_fire_without_suppressing_movement() {
        let mut control = PolicyControl::new();
        control.bind_turret_aim(0, Vec2::new(0.0, 1.0));
        let mut action = Action::ZERO;
        action.continuous[0] = 1.0;
        action.continuous[2] = 0.05;
        action.discrete[0] = 1;
        action.discrete[5] = 3;
        let available = [true, false, false, false, false];
        let decoded = control.decode(&action, PolicyFrame::new(0), &available).unwrap();
        assert_eq!(decoded.weapons[0].aim_world, Vec2::new(0.0, 1.0));
        assert!(!decoded.weapons[0].fire_requested);
        assert_eq!(decoded.movement_world, Vec2::new(1.0, 0.0));
        assert_eq!(decoded.fresh_aim_slot_mask, 0);

        action.discrete[5] = 0;
        action.continuous[2] = 1.0;
        let decoded = control.decode(&action, PolicyFrame::new(1), &available).unwrap();
        assert_eq!(decoded.weapons[0].aim_world, Vec2::new(0.0, -1.0));
        assert!(decoded.weapons[0].fire_requested);
        assert_eq!(decoded.fresh_aim_slot_mask, 1);
    }

    #[test]
    fn invalid_action_does_not_mutate_held_state() {
        let mut control = PolicyControl::new();
        let original = control.clone();
        let mut action = Action::ZERO;
        action.continuous[2] = f32::NAN;
        assert!(control.decode(&action, PolicyFrame::new(0), &[true; 5]).is_err());
        assert_eq!(control, original);
    }
}
