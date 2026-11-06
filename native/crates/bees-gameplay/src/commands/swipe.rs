//! Unity SwipeSquad (RightSwipe/LeftSwipe) movement and timer phases.
//! Per-weapon shooting remains CombatWorld-owned; target ranking and squad
//! movement use shared native gameplay rather than a training-only model.

use std::f32::consts::{FRAC_PI_4, FRAC_PI_2, PI};

use bees_hivemind::dispatch::{LocalCommandKind, ResolvedHiveMindCommand};
use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::CombatWorld;
use bees_sim::geometry::Vec2;
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_sim::simulation::RandomStream;
use bees_sim::squad_movement::{
    issue_squad_move, SquadFormationMember, SquadFormationOptions,
};

use crate::squads::NativeSquadRoster;
use super::targeting::NativeTargetApproach;
use super::{
    assemble_circle_snapshot_from_live_weapons,
    CommandFinishCause, GameCommandError,
    NativeEnemyTargetStats, NativeSquadOrder,
};

/// Fresh Unity Squad/Ship metrics for one SwipeSquadTimer. Candidate stats
/// must come from the original live EnemySquad in GetShips order. The Level
/// uses each real Weapon.IsShipValidTarget to compute the same squad-level
/// range condition as CircleSquad. Candidate stats are needed only while
/// approaching: subsequent swipe/arrival polls can leave them empty.
pub struct NativeSwipeCommandSnapshot {
    pub enemy_owner: SquadLifetime,
    pub enemy_alive: bool,
    pub squad_center: Vec2,
    pub enemy_center: Vec2,
    pub own_max_range: f32,
    pub enemy_max_range: f32,
    pub every_member_has_target_in_range: bool,
    pub candidates: Vec<NativeEnemyTargetStats>,
}

impl NativeSwipeCommandSnapshot {
    fn valid(&self) -> bool {
        self.enemy_owner.valid() && self.squad_center.is_finite() &&
        self.enemy_center.is_finite() && self.own_max_range.is_finite() &&
        self.own_max_range >= 0.0 && self.enemy_max_range.is_finite() &&
        self.enemy_max_range >= 0.0
    }
}

/// Assemble SwipeSquad's shared range predicate from the same real
/// Ship.IsShipWithinRange logic as CircleSquad, without a duplicate
/// implementation or a center-distance approximation. Remaining metrics
/// come from the current authored Level and FleetShip, not copied physics.
pub fn assemble_swipe_snapshot_from_live_weapons(
    world: &CombatWorld,
    roster: &NativeSquadRoster,
    owner: SquadLifetime,
    enemy_owner: SquadLifetime,
    squad_center: Vec2,
    enemy_center: Vec2,
    enemy_max_range: f32,
    candidates: Vec<NativeEnemyTargetStats>,
    ship_max_range: impl FnMut(i64) -> Result<f32, GameCommandError>,
    weapon_valid_target: impl FnMut(i64, i64)
        -> Result<bool, GameCommandError>,
) -> Result<NativeSwipeCommandSnapshot, GameCommandError> {
    let common = assemble_circle_snapshot_from_live_weapons(
        world, roster, owner, enemy_owner, squad_center, enemy_center,
        ship_max_range, weapon_valid_target,
    )?;
    if !enemy_max_range.is_finite() || enemy_max_range < 0.0 {
        return Err(GameCommandError::InvalidCombatSnapshot);
    }
    Ok(NativeSwipeCommandSnapshot {
        enemy_owner,
        enemy_alive: common.enemy_alive,
        squad_center: common.squad_center,
        enemy_center: common.enemy_center,
        own_max_range: common.own_max_range,
        enemy_max_range,
        every_member_has_target_in_range:
            common.every_member_has_target_in_range,
        candidates,
    })
}

pub(super) struct SwipeRun {
    enemy_squad_id: i64,
    enemy_lifetime: Option<SquadLifetime>,
    kind: LocalCommandKind,
    members: Vec<SquadFormationMember>,
    formation: SquadFormationOptions,
    reached_enemy: bool,
    swipe_destination: Vec2,
    approach: NativeTargetApproach,
}

impl SwipeRun {
    pub(super) fn start(
        world: &CombatWorld,
        squad: NativeSquadOrder<'_>,
        kind: LocalCommandKind,
    ) -> Result<Option<Self>, GameCommandError> {
        if !matches!(kind, LocalCommandKind::RightSwipe |
            LocalCommandKind::LeftSwipe) {
            return Err(GameCommandError::InvalidSettings);
        }
        let Some(enemy) = squad.enemy else { return Ok(None) };
        if !enemy.valid() { return Err(GameCommandError::InvalidEnemy); }
        if !enemy.is_alive { return Ok(None); }
        for member in squad.members {
            world.validate_ship_game_order(member.ship_id)?;
        }
        Ok(Some(Self {
            enemy_squad_id: enemy.squad_id,
            enemy_lifetime: None,
            kind,
            members: squad.members.to_vec(),
            formation: squad.formation,
            reached_enemy: false,
            swipe_destination: Vec2::ZERO,
            approach: NativeTargetApproach::default(),
        }))
    }

    pub(super) fn enemy_squad_id(&self) -> i64 {
        self.enemy_squad_id
    }

    pub(super) fn on_poll(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        roster: &NativeSquadRoster,
        random: &mut RandomStream,
        owner: SquadLifetime,
        selection: &ResolvedHiveMindCommand,
        mut snapshot: NativeSwipeCommandSnapshot,
    ) -> Result<Option<CommandFinishCause>, GameCommandError> {
        if !snapshot.enemy_alive {
            return Ok(Some(CommandFinishCause::EnemySquadGone));
        }
        if !snapshot.valid() ||
            snapshot.enemy_owner.level_id != owner.level_id ||
            snapshot.enemy_owner.squad_id != self.enemy_squad_id {
            return Err(GameCommandError::InvalidCombatSnapshot);
        }
        if let Some(original) = self.enemy_lifetime {
            if original != snapshot.enemy_owner {
                return Err(GameCommandError::InvalidEnemy);
            }
        } else {
            self.enemy_lifetime = Some(snapshot.enemy_owner);
        }

        if !self.reached_enemy && !snapshot.every_member_has_target_in_range {
            if !self.approach.issue_tracked_approach(
                world, roster, random, &self.members, self.formation,
                selection, snapshot.enemy_owner, snapshot.squad_center,
                &snapshot.candidates,
            )? {
                return Ok(Some(CommandFinishCause::NoEnemyTargets));
            }
            return Ok(None);
        }

        if self.swipe_destination == Vec2::ZERO {
            self.reached_enemy = true;
            let delta = snapshot.squad_center - snapshot.enemy_center;
            let mut angle = delta.x.atan2(delta.y);
            if self.kind == LocalCommandKind::RightSwipe {
                angle += FRAC_PI_4;
                if angle > PI { angle -= 2.0 * PI; }
            } else {
                angle -= FRAC_PI_4;
                if angle < -PI { angle += 2.0 * PI; }
            }
            let distance = (snapshot.enemy_max_range * 2.0)
                .max(snapshot.own_max_range - 2.0);
            let circle_angle = -angle - FRAC_PI_2;
            let destination = snapshot.enemy_center + Vec2::new(
                circle_angle.cos() * distance,
                circle_angle.sin() * distance,
            );
            issue_squad_move(
                world, navigation, &self.members, destination, self.formation,
            )?;
            self.swipe_destination = destination;
            return Ok(None);
        }

        let all_arrived = self.members.iter().all(|member|
            world.ship_body(member.ship_id).is_some_and(|body|
                body.active && !body.motion.has_target_coordinates
            )
        );
        if all_arrived {
            return Ok(Some(CommandFinishCause::SwipeFinished));
        }
        Ok(None)
    }
}
