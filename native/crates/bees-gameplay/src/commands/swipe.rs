//! Unity SwipeSquad (RightSwipe/LeftSwipe) movement and timer phases.
//! Per-weapon shooting remains CombatWorld-owned; target ranking and squad
//! movement use shared native gameplay rather than a training-only model.

use std::collections::{HashMap, VecDeque};
use std::f32::consts::{FRAC_PI_4, FRAC_PI_2, PI};

use bees_hivemind::dispatch::{LocalCommandKind, ResolvedHiveMindCommand};
use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::{CombatWorld, GamePointOrder};
use bees_sim::geometry::Vec2;
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_sim::simulation::RandomStream;
use bees_sim::squad_movement::{
    issue_squad_move, SquadFormationMember, SquadFormationOptions,
};

use crate::squads::NativeSquadRoster;
use super::{
    sort_native_command_targets, CommandFinishCause, GameCommandError,
    NativeCommandTargetQueue, NativeEnemyTargetStats, NativeSquadOrder,
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

pub(super) struct SwipeRun {
    enemy_squad_id: i64,
    enemy_lifetime: Option<SquadLifetime>,
    kind: LocalCommandKind,
    members: Vec<SquadFormationMember>,
    formation: SquadFormationOptions,
    reached_enemy: bool,
    swipe_destination: Vec2,
    target_queue: Option<NativeCommandTargetQueue>,
    /// Only callback scratch. No additional permanent ship/physics state.
    candidate_positions: HashMap<i64, Vec2>,
    ranked_ids: Vec<i64>,
    ranked_candidates: Vec<NativeEnemyTargetStats>,
    pending_moves: Vec<(i64, GamePointOrder)>,
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
            target_queue: None,
            candidate_positions: HashMap::new(),
            ranked_ids: Vec::new(),
            ranked_candidates: Vec::new(),
            pending_moves: Vec::new(),
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
            self.candidate_positions.clear();
            for candidate in &snapshot.candidates {
                self.candidate_positions.insert(candidate.ship_id, candidate.position);
            }
            if self.target_queue.is_none() {
                // Command.Setup builds the first queue once. Do not consume
                // a Random shuffle on every later timed approach callback.
                self.ranked_candidates.clear();
                self.ranked_candidates.extend_from_slice(&snapshot.candidates);
                sort_native_command_targets(
                    world, roster, snapshot.enemy_owner, snapshot.squad_center,
                    selection.effective_shooting, random, &mut self.ranked_candidates,
                )?;
                self.ranked_ids.clear();
                self.ranked_ids.extend(
                    self.ranked_candidates.iter().map(|candidate| candidate.ship_id)
                );
                self.target_queue = Some(NativeCommandTargetQueue::new(
                    &self.ranked_ids,
                ));
            }
            let queue = self.target_queue.as_mut()
                .expect("initialized above");
            let ranked_candidates = &mut self.ranked_candidates;
            self.pending_moves.clear();
            for member in &self.members {
                let target_id = queue.select_for_ship(
                    member.ship_id, snapshot.candidates.len(),
                    |output: &mut VecDeque<i64>| {
                        ranked_candidates.clear();
                        ranked_candidates.extend_from_slice(&snapshot.candidates);
                        sort_native_command_targets(
                            world, roster, snapshot.enemy_owner,
                            snapshot.squad_center, selection.effective_shooting,
                            random, ranked_candidates,
                        )?;
                        output.extend(
                            ranked_candidates.iter().map(|candidate| candidate.ship_id)
                        );
                        Ok(())
                    },
                    |id| world.ship(id).is_some_and(|ship|
                        !ship.vitals.is_dead() &&
                        roster.owning_squad(id) == Some(snapshot.enemy_owner)
                    ),
                )?;
                let Some(target_id) = target_id else {
                    return Ok(Some(CommandFinishCause::NoEnemyTargets));
                };
                let Some(&destination) = self.candidate_positions.get(&target_id) else {
                    return Err(GameCommandError::InvalidCombatSnapshot);
                };
                world.validate_ship_game_order(member.ship_id)?;
                self.pending_moves.push((member.ship_id, GamePointOrder {
                    destination,
                    ship_clearance: member.clearance,
                    map_bounds: self.formation.map_bounds,
                    has_obstacles: self.formation.has_obstacles,
                    can_override_bounds: self.formation.can_override_bounds,
                    newly_found_obstacle: false,
                }));
            }
            // All target reservations and movement profiles preflight before
            // issuing the first command; no half-issued formation on failure.
            for &(ship_id, order) in &self.pending_moves {
                world.move_ship_to_tracked_point(ship_id, order)?;
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
