//! Shared StrikerBomb two-stage fuse and delayed-damage lifecycle.
//!
//! Source: Scripts/Entities/Projectiles/StrikerBomb.cs.
//! The first timer fires after strictly more than 1.5 scaled seconds. The
//! bomb then deactivates its presentation but remains owned and unpooled for
//! another 0.5 seconds. Only the second callback may apply the damage.
//! An invalid/dead contacted ship cancels the bomb on FixedUpdate instead.
//!
//! Each bomb carries the contacted ship's unique runtime ID and the firing
//! Striker's persistent FleetShip ID to prevent attribution to pooled wrappers.
//! The world controls whether these identities are still valid.

use crate::timer::ScaledTimer;
use crate::geometry::Vec2;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BombStage {
    Fuse,
    PendingDamage,
    Finished,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BombAction {
    None,
    Detonated,
    ApplyDamageAndEnd,
    EndWithoutDamage,
}

pub struct StrikerBombFuse {
    contacted_ship_id: i64,
    shooter_fleet_ship_id: i64,
    local_attachment_offset: Vec2,
    timer: ScaledTimer,
    stage: BombStage,
}

impl StrikerBombFuse {
    pub fn new(contacted_ship_id: i64, shooter_fleet_ship_id: i64) -> Self {
        Self {
            contacted_ship_id,
            shooter_fleet_ship_id,
            local_attachment_offset: Vec2::ZERO,
            timer: ScaledTimer::new(1.5, false, false),
            stage: BombStage::Fuse,
        }
    }

    pub fn contacted_ship_id(&self) -> i64 {
        self.contacted_ship_id
    }

    pub fn shooter_fleet_ship_id(&self) -> i64 {
        self.shooter_fleet_ship_id
    }

    pub fn stage(&self) -> BombStage {
        self.stage
    }

    /// StrikerBomb.Setup reparents the bomb transform to the contacted ship.
    /// The offset is computed in that target's local frame at launch, then
    /// transformed with its current heading every subsequent game tick.
    pub fn set_attachment_offset(&mut self, local_offset: Vec2) {
        self.local_attachment_offset = local_offset;
    }

    pub fn local_attachment_offset(&self) -> Vec2 {
        self.local_attachment_offset
    }

    pub fn advance(&mut self, dt: f32, original_target_alive: bool) -> BombAction {
        if self.stage == BombStage::Finished {
            return BombAction::None;
        }
        // StrikerBomb.FixedUpdate cancels on the death/reuse of the target
        // even when the damage timer has already started.
        if !original_target_alive {
            self.stage = BombStage::Finished;
            self.timer.cancel();
            return BombAction::EndWithoutDamage;
        }
        let Some(due) = self.timer.begin_update(dt) else {
            return BombAction::None;
        };
        self.timer.finish_update(due);
        match self.stage {
            BombStage::Fuse => {
                self.stage = BombStage::PendingDamage;
                // Matches _damageTimer.Reuse(.5f, DamageAndKill).
                self.timer.reuse(0.5, false, false);
                BombAction::Detonated
            }
            BombStage::PendingDamage => {
                self.stage = BombStage::Finished;
                self.timer.cancel();
                BombAction::ApplyDamageAndEnd
            }
            BombStage::Finished => BombAction::None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn two_stage_bomb_keeps_its_original_target_and_delayed_damage() {
        let mut bomb = StrikerBombFuse::new(7, 100);
        assert_eq!(bomb.advance(1.5, true), BombAction::None);
        assert_eq!(bomb.advance(0.01, true), BombAction::Detonated);
        assert_eq!(bomb.stage(), BombStage::PendingDamage);
        assert_eq!(bomb.advance(0.5, true), BombAction::None);
        assert_eq!(bomb.advance(0.01, true), BombAction::ApplyDamageAndEnd);
        assert_eq!(bomb.advance(5.0, true), BombAction::None);
    }

    #[test]
    fn a_dying_target_cancels_delayed_damage() {
        let mut bomb = StrikerBombFuse::new(7, 100);
        assert_eq!(bomb.advance(1.51, true), BombAction::Detonated);
        assert_eq!(bomb.advance(0.1, false), BombAction::EndWithoutDamage);
        assert_eq!(bomb.advance(10.0, true), BombAction::None);
    }
}
