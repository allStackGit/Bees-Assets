//! Source CircleSquad command's movement phases. Combat targeting truth
//! comes from the live Level's weapon-validity snapshot, not a range
//! approximation or a second training-only weapon model.

use std::f32::consts::{FRAC_PI_2, PI};

use bees_sim::combat_world::CombatWorld;
use bees_sim::geometry::Vec2;
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_sim::squad_movement::{
    issue_squad_tracked_move, SquadFormationMember, SquadFormationOptions,
};
use bees_sim::timer::ScaledTimer;

use super::{CommandFinishCause, GameCommandError, NativeSquadOrder};

const CIRCLE_POLL_SECONDS: f32 = 0.25;
const CIRCLE_ADVANCE_RADIANS: f32 = 0.06 * PI;
const MIN_DESTINATION_SEPARATION: f32 = 1.25;

/// A live source-Level projection at this exact timer callback. The Level
/// evaluates Ship.IsShipWithinRange via its real weapon target eligibility:
/// every living own ship must have at least one valid enemy-ship target.
/// Squad center and MaxRange are the actual Unity Squad values.
#[derive(Clone, Copy, Debug)]
pub struct NativeCircleCommandSnapshot {
    pub enemy_alive: bool,
    pub squad_center: Vec2,
    pub enemy_center: Vec2,
    pub own_max_range: f32,
    pub every_member_has_target_in_range: bool,
}

impl NativeCircleCommandSnapshot {
    pub fn valid(self) -> bool {
        self.squad_center.is_finite() &&
            self.enemy_center.is_finite() &&
            self.own_max_range.is_finite() &&
            self.own_max_range >= 0.0
    }
}

pub(super) struct CircleRun {
    enemy_squad_id: i64,
    members: Vec<SquadFormationMember>,
    formation: SquadFormationOptions,
    reached_enemy: bool,
    ideal_distance: Option<f32>,
    last_destination: Vec2,
}

impl CircleRun {
    pub(super) fn start(
        world: &CombatWorld,
        squad: NativeSquadOrder<'_>,
    ) -> Result<Option<Self>, GameCommandError> {
        let Some(enemy) = squad.enemy else {
            return Ok(None);
        };
        if !enemy.valid() {
            return Err(GameCommandError::InvalidEnemy);
        }
        if !enemy.is_alive {
            return Ok(None);
        }
        for member in squad.members {
            world.validate_ship_game_order(member.ship_id)?;
        }
        Ok(Some(Self {
            enemy_squad_id: enemy.squad_id,
            members: squad.members.to_vec(),
            formation: squad.formation,
            reached_enemy: false,
            ideal_distance: None,
            last_destination: Vec2::ZERO,
        }))
    }

    pub(super) fn enemy_squad_id(&self) -> i64 {
        self.enemy_squad_id
    }

    /// Exactly one Unity CircleSquad.Timer callback. The caller owns the
    /// existing ScaledTimer and its two-phase update; reusing it here
    /// correctly changes 3s polls to 0.25s polls after first contact.
    pub(super) fn on_poll(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        timer: &mut ScaledTimer,
        snapshot: NativeCircleCommandSnapshot,
    ) -> Result<Option<CommandFinishCause>, GameCommandError> {
        // Unity checks EnemySquad.IsDead before reading position/range.
        if !snapshot.enemy_alive {
            return Ok(Some(CommandFinishCause::EnemySquadGone));
        }
        if !snapshot.valid() {
            return Err(GameCommandError::InvalidCombatSnapshot);
        }
        if !self.reached_enemy && !snapshot.every_member_has_target_in_range {
            issue_squad_tracked_move(
                world, navigation, &self.members,
                snapshot.enemy_center, self.formation,
            )?;
            return Ok(None);
        }

        if self.ideal_distance.is_none() {
            let distance = (snapshot.enemy_center - snapshot.squad_center)
                .length_squared().sqrt();
            self.ideal_distance = Some(distance.max(snapshot.own_max_range * 0.5));
        }
        if !self.reached_enemy {
            timer.reuse(CIRCLE_POLL_SECONDS, true, false);
            self.reached_enemy = true;
        }

        // Utilities.AngleBetweenPoints(enemy, own) = atan2(ex - sx, ey - sy).
        // Squad.CirclePoint converts that to (-angle - PI/2).
        let to_own = snapshot.enemy_center - snapshot.squad_center;
        let mut angle = to_own.x.atan2(to_own.y) + CIRCLE_ADVANCE_RADIANS;
        let radius = self.ideal_distance.expect("assigned above");
        let mut destination = circle_point(snapshot.enemy_center, angle, radius);
        let mut loops = 0;
        while (destination - self.last_destination).length_squared().sqrt()
            < MIN_DESTINATION_SEPARATION && loops < 100
        {
            loops += 1;
            angle += CIRCLE_ADVANCE_RADIANS;
            destination = circle_point(snapshot.enemy_center, angle, radius);
        }
        issue_squad_tracked_move(
            world, navigation, &self.members, destination, self.formation,
        )?;
        self.last_destination = destination;
        Ok(None)
    }
}

fn circle_point(center: Vec2, angle: f32, distance: f32) -> Vec2 {
    let transformed = -angle - FRAC_PI_2;
    center + Vec2::new(
        transformed.cos() * distance,
        transformed.sin() * distance,
    )
}
