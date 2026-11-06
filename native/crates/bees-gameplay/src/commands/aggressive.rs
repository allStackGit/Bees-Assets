//! Unity Aggressive: reach enemies, confirm range on three consecutive
//! command polls, then hold a position if all weapon target checks pass.
//! StartCoroutine(MoveTowardsEnemiesAcrossFrames) issues one ship order per
//! simulation step. Only canonical CombatWorld movement and targeting apply.

use bees_hivemind::dispatch::ResolvedHiveMindCommand;
use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::CombatWorld;
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_sim::simulation::RandomStream;
use bees_sim::squad_movement::{
    issue_squad_move, SquadFormationMember, SquadFormationOptions,
};
use bees_sim::timer::ScaledTimer;

use crate::squads::NativeSquadRoster;
use super::targeting::{NativeAttackTarget, NativeTargetApproach};
use super::{
    CommandFinishCause, GameCommandError, NativeSquadOrder,
    NativeSwipeCommandSnapshot,
};

const CLOSE_POLL_SECONDS: f32 = 0.25;
const HOLD_RANGE_THRESHOLD: f32 = 45.0;

/// Source's live range predicates and enemy composition at an Aggressive
/// command callback. Range must come from actual Weapon.IsShipValidTarget
/// behavior, not squad-center radius approximations. Candidate ranking
/// shares NativeSwipeCommandSnapshot with Swipe and InAndOut.
pub struct NativeAggressiveCommandSnapshot {
    pub attack: NativeSwipeCommandSnapshot,
    /// Squad.AreAllSquadShipsWithinRangeOfAllOfOurSquadShips, evaluated by
    /// actual per-weapon target eligibility for every own/enemy pair.
    pub every_member_has_all_targets_in_range: bool,
    /// EnemySquad.AreAllShipsDefenseless, from live authored ship weapons.
    pub all_enemies_defenseless: bool,
}

impl NativeAggressiveCommandSnapshot {
    fn valid(&self) -> bool {
        self.attack.valid_for_combat() &&
            (!self.every_member_has_all_targets_in_range ||
                self.attack.every_member_has_target_in_range)
    }
}

pub(super) struct AggressiveRun {
    target: NativeAttackTarget,
    members: Vec<SquadFormationMember>,
    formation: SquadFormationOptions,
    approach: NativeTargetApproach,
    comfortably_in_range: bool,
    consecutive_range_polls: u8,
    has_taken_position: bool,
    close_to_target: bool,
    /// Source's in-flight coroutine. A poll never starts another coroutine
    /// until this one resumes after its final ship and completes.
    next_coroutine_member: Option<usize>,
}

impl AggressiveRun {
    pub(super) fn start(
        world: &CombatWorld,
        squad: NativeSquadOrder<'_>,
    ) -> Result<Option<Self>, GameCommandError> {
        let Some(enemy) = squad.enemy else { return Ok(None); };
        if !enemy.valid() { return Err(GameCommandError::InvalidEnemy); }
        if !enemy.is_alive { return Ok(None); }
        for member in squad.members {
            world.validate_ship_game_order(member.ship_id)?;
        }
        Ok(Some(Self {
            target: NativeAttackTarget::new(enemy.squad_id),
            members: squad.members.to_vec(),
            formation: squad.formation,
            approach: NativeTargetApproach::default(),
            comfortably_in_range: false,
            consecutive_range_polls: 0,
            has_taken_position: false,
            close_to_target: false,
            next_coroutine_member: None,
        }))
    }

    pub(super) fn enemy_squad_id(&self) -> i64 {
        self.target.squad_id()
    }

    pub(super) fn has_pending_coroutine(&self) -> bool {
        self.next_coroutine_member.is_some()
    }

    /// Unity Aggressive.Timer, including the strict transition from a
    /// 3-second recurring command timer to a quarter-second poll.
    pub(super) fn on_poll(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        timer: &mut ScaledTimer,
        owner: SquadLifetime,
        snapshot: &NativeAggressiveCommandSnapshot,
    ) -> Result<Option<CommandFinishCause>, GameCommandError> {
        if !snapshot.attack.enemy_alive {
            return Ok(Some(CommandFinishCause::EnemySquadGone));
        }
        if !snapshot.valid() {
            return Err(GameCommandError::InvalidCombatSnapshot);
        }
        self.target.validate(owner, snapshot.attack.enemy_owner)?;
        if !self.comfortably_in_range {
            if snapshot.attack.every_member_has_target_in_range {
                self.consecutive_range_polls += 1;
                if self.consecutive_range_polls == 3 {
                    self.consecutive_range_polls = 0;
                    self.comfortably_in_range = true;
                }
            } else {
                self.consecutive_range_polls = 0;
            }
            if self.next_coroutine_member.is_none() {
                self.next_coroutine_member = Some(0);
            }
            let separation = (snapshot.attack.squad_center -
                snapshot.attack.enemy_center).length_squared().sqrt();
            if !self.close_to_target &&
                separation < snapshot.attack.own_max_range * 2.0
            {
                self.close_to_target = true;
                timer.reuse(CLOSE_POLL_SECONDS, true, false);
            }
            return Ok(None);
        }

        if snapshot.every_member_has_all_targets_in_range &&
            (snapshot.attack.own_max_range >= HOLD_RANGE_THRESHOLD ||
             snapshot.all_enemies_defenseless)
        {
            if !self.has_taken_position {
                issue_squad_move(
                    world, navigation, &self.members,
                    snapshot.attack.squad_center, self.formation,
                )?;
                self.has_taken_position = true;
            }
        } else {
            self.has_taken_position = false;
            self.comfortably_in_range = false;
        }
        Ok(None)
    }

    /// StartCoroutine runs the first ship immediately, then resumes by
    /// one ship per Unity frame. Called every Level fixed step when pending;
    /// the live Level snapshot is refreshed even without a due command
    /// timer, since a target may die between yielded coroutine frames.
    pub(super) fn resume_coroutine_one_ship(
        &mut self,
        world: &mut CombatWorld,
        roster: &NativeSquadRoster,
        random: &mut RandomStream,
        owner: SquadLifetime,
        selection: &ResolvedHiveMindCommand,
        snapshot: &NativeAggressiveCommandSnapshot,
    ) -> Result<Option<CommandFinishCause>, GameCommandError> {
        let Some(index) = self.next_coroutine_member else {
            return Ok(None);
        };
        if !snapshot.attack.enemy_alive {
            self.next_coroutine_member = None;
            return Ok(None);
        }
        if !snapshot.valid() {
            return Err(GameCommandError::InvalidCombatSnapshot);
        }
        self.target.validate(owner, snapshot.attack.enemy_owner)?;
        if index == self.members.len() {
            // The coroutine resumes once more after its final yield before
            // allowing the next command timer to start another pass.
            self.next_coroutine_member = None;
            return Ok(None);
        }
        let member = self.members[index];
        if !self.approach.issue_one_tracked_approach(
            world, roster, random, member, self.formation, selection,
            snapshot.attack.enemy_owner, snapshot.attack.squad_center,
            &snapshot.attack.candidates,
        )? {
            self.next_coroutine_member = None;
            return Ok(Some(CommandFinishCause::NoEnemyTargets));
        }
        self.next_coroutine_member = Some(index + 1);
        Ok(None)
    }
}
