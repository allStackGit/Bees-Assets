//! Unity InAndOut: attack using the shared retained-target queue, then
//! issue one formation return order and finish after all ships arrive.
//! This does not duplicate targeting, navigation, or weapon firing.

use bees_hivemind::dispatch::ResolvedHiveMindCommand;
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
    choose_random_coordinate, CommandFinishCause, GameCommandError,
    NativeSquadOrder, NativeSwipeCommandSnapshot,
};

pub(super) struct InAndOutRun {
    enemy_squad_id: i64,
    enemy_lifetime: Option<SquadLifetime>,
    members: Vec<SquadFormationMember>,
    formation: SquadFormationOptions,
    return_point: Vec2,
    reached_enemy: bool,
    approach: NativeTargetApproach,
}

impl InAndOutRun {
    pub(super) fn start(
        world: &CombatWorld,
        random: &mut RandomStream,
        squad: NativeSquadOrder<'_>,
    ) -> Result<Option<Self>, GameCommandError> {
        let Some(enemy) = squad.enemy else { return Ok(None); };
        if !enemy.valid() { return Err(GameCommandError::InvalidEnemy); }
        if !enemy.is_alive { return Ok(None); }
        for member in squad.members {
            world.validate_ship_game_order(member.ship_id)?;
        }
        // Unity Execute calls RandomCoordinate immediately, before the
        // recurring timer. The optional 50-unit close-range branch uses
        // exactly enemy.MaxRange and Squad.DistanceToPoint(enemy.GetPosition).
        let distance = (squad.squad_center - enemy.position)
            .length_squared().sqrt();
        let (origin, max_distance, min_distance) =
            if distance > enemy.max_range && distance < 50.0 {
                (squad.squad_center, 45.0, 0.0)
            } else {
                (enemy.position, enemy.max_range + 45.0,
                    enemy.max_range + 10.0)
            };
        let return_point = choose_random_coordinate(
            random, origin, squad.formation.map_bounds,
            max_distance, min_distance,
        );
        Ok(Some(Self {
            enemy_squad_id: enemy.squad_id,
            enemy_lifetime: None,
            members: squad.members.to_vec(),
            formation: squad.formation,
            return_point,
            reached_enemy: false,
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
        snapshot: NativeSwipeCommandSnapshot,
    ) -> Result<Option<CommandFinishCause>, GameCommandError> {
        if !snapshot.enemy_alive {
            return Ok(Some(CommandFinishCause::EnemySquadGone));
        }
        if !snapshot.valid_for_combat() ||
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

        if !self.reached_enemy {
            if !snapshot.every_member_has_target_in_range {
                if !self.approach.issue_tracked_approach(
                    world, roster, random, &self.members, self.formation,
                    selection, snapshot.enemy_owner, snapshot.squad_center,
                    &snapshot.candidates,
                )? {
                    return Ok(Some(CommandFinishCause::NoEnemyTargets));
                }
            } else {
                self.reached_enemy = true;
                // Command.SetAndMove records the requested ReturnPoint.
                // Squad.Move may adjust individual navigation destinations,
                // but does not rewrite Command.GetDestination.
                issue_squad_move(
                    world, navigation, &self.members,
                    self.return_point, self.formation,
                )?;
            }
            return Ok(None);
        }

        if self.members.iter().all(|member|
            world.ship_body(member.ship_id).is_some_and(|body|
                body.active && !body.motion.has_target_coordinates
            )
        ) {
            return Ok(Some(CommandFinishCause::ReturnedToStart));
        }
        Ok(None)
    }
}
