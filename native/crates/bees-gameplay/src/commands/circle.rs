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

use bees_hivemind::session::SquadLifetime;
use crate::squads::NativeSquadRoster;
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
    /// Exact target lifetime, not a pooled squad's reused numeric ID.
    pub enemy_owner: SquadLifetime,
    pub enemy_alive: bool,
    pub squad_center: Vec2,
    pub enemy_center: Vec2,
    pub own_max_range: f32,
    pub every_member_has_target_in_range: bool,
}

impl NativeCircleCommandSnapshot {
    pub fn valid(self) -> bool {
        self.enemy_owner.valid() &&
            self.squad_center.is_finite() &&
            self.enemy_center.is_finite() &&
            self.own_max_range.is_finite() &&
            self.own_max_range >= 0.0
    }
}

/// Assemble the exact squad-level range predicate from the existing live
/// roster. Unity's AreSomeSquadShipsWithinRangeOfAllOfOurSquadShips
/// requires EACH own ship to have ANY enemy ship which passes its real
/// Weapon.IsShipValidTarget check. This is NOT distance-to-center testing.
/// The callback must use the authored Weapon.IsShipValidTarget behavior:
/// RangeCollider's live ShipsWithinRange contact registry and unobstructed
/// linecast to the target collider (including subclass overrides). It must
/// NOT substitute a center-distance/radius comparison. No allocation or
/// extra physics view is created.
///
/// The enemy lifetime must be the original command target, not a new
/// squad which reused its numeric ID. Callers supply actual GetPosition
/// bounds centers and original Ship.MaxRange values on each poll.
pub fn assemble_circle_snapshot_from_live_weapons(
    world: &CombatWorld,
    roster: &NativeSquadRoster,
    owner: SquadLifetime,
    enemy_owner: SquadLifetime,
    squad_center: Vec2,
    enemy_center: Vec2,
    mut ship_max_range: impl FnMut(i64) -> Result<f32, GameCommandError>,
    mut weapon_valid_target: impl FnMut(i64, i64)
        -> Result<bool, GameCommandError>,
) -> Result<NativeCircleCommandSnapshot, GameCommandError> {
    let squad = roster.get(owner).ok_or(GameCommandError::InvalidSquad)?;
    if squad.members.is_empty() || owner.level_id != enemy_owner.level_id {
        return Err(GameCommandError::InvalidSquad);
    }
    let Some(enemy) = roster.get(enemy_owner) else {
        return Ok(NativeCircleCommandSnapshot {
            enemy_owner,
            enemy_alive: false,
            squad_center,
            enemy_center,
            own_max_range: 0.0,
            every_member_has_target_in_range: false,
        });
    };
    if squad.side == enemy.side || enemy.members.is_empty() {
        return Err(GameCommandError::InvalidEnemy);
    }

    let mut own_max_range = 0.0_f32;
    let mut every_member_has_target_in_range = true;
    for &own_id in &squad.members {
        let own_ship = world.ship(own_id)
            .ok_or(GameCommandError::InvalidSquad)?;
        if own_ship.vitals.is_dead() || own_ship.side != squad.side ||
            !world.ship_body(own_id).is_some_and(|body| body.active)
        {
            return Err(GameCommandError::InvalidSquad);
        }
        let range = ship_max_range(own_id)?;
        if !range.is_finite() || range < 0.0 {
            return Err(GameCommandError::InvalidCombatSnapshot);
        }
        own_max_range = own_max_range.max(range);
        // Retain the range validation for every member, even if an
        // earlier member has no target; don't silently accept bad stats.
        if !every_member_has_target_in_range {
            continue;
        }
        let mut has_target = false;
        for &enemy_id in &enemy.members {
            let enemy_ship = world.ship(enemy_id)
                .ok_or(GameCommandError::InvalidEnemy)?;
            if enemy_ship.vitals.is_dead() || enemy_ship.side != enemy.side ||
                !world.ship_body(enemy_id).is_some_and(|body| body.active)
            {
                return Err(GameCommandError::InvalidEnemy);
            }
            if weapon_valid_target(own_id, enemy_id)? {
                has_target = true;
                break;
            }
        }
        if !has_target {
            every_member_has_target_in_range = false;
        }
    }
    Ok(NativeCircleCommandSnapshot {
        enemy_owner,
        enemy_alive: true,
        squad_center,
        enemy_center,
        own_max_range,
        every_member_has_target_in_range,
    })
}

pub(super) struct CircleRun {
    enemy_squad_id: i64,
    enemy_lifetime: Option<SquadLifetime>,
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
            enemy_lifetime: None,
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
        owner: SquadLifetime,
        snapshot: NativeCircleCommandSnapshot,
    ) -> Result<Option<CommandFinishCause>, GameCommandError> {
        // Unity checks EnemySquad.IsDead before reading position/range.
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
