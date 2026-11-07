//! Unity Aggressive: reach enemies, confirm range on three consecutive
//! command polls, then hold a position if all weapon target checks pass.
//! StartCoroutine(MoveTowardsEnemiesAcrossFrames) is modeled as one ship
//! order per simulation step. Unity resumes on render frames instead, so
//! variable-render-rate timing parity still needs runtime qualification.
//! Only canonical CombatWorld movement and targeting apply.

use super::NativeCommandSelection;
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
    /// None until the hold-position phase actually needs the expensive
    /// all-own/all-enemy weapon eligibility cross-product.
    pub every_member_has_all_targets_in_range: Option<bool>,
    /// Required only when all targets are eligible and own range <45.
    pub all_enemies_defenseless: Option<bool>,
}

impl NativeAggressiveCommandSnapshot {
    fn valid(&self) -> bool {
        self.attack.valid_for_combat() &&
            (!self.every_member_has_all_targets_in_range.unwrap_or(false) ||
                self.attack.every_member_has_target_in_range)
    }
}

/// Compute Unity's stronger all-targets predicate from the same live
/// Weapon.IsShipValidTarget callback used by Swipe and Circle. The Level
/// must supply enemy_defenseless from real authored ship composition.
/// Returning false on a missing target is legitimate; substituting
/// estimated center-distance or an assumed defenseless flag is not.
pub fn assemble_aggressive_snapshot_from_live_weapons(
    world: &CombatWorld,
    roster: &NativeSquadRoster,
    owner: SquadLifetime,
    attack: NativeSwipeCommandSnapshot,
    evaluate_all_targets: bool,
    all_enemies_defenseless: Option<bool>,
    mut weapon_valid_target: impl FnMut(i64, i64)
        -> Result<bool, GameCommandError>,
) -> Result<NativeAggressiveCommandSnapshot, GameCommandError> {
    if !attack.valid_for_combat() || owner.level_id != attack.enemy_owner.level_id {
        return Err(GameCommandError::InvalidCombatSnapshot);
    }
    if !attack.enemy_alive || !evaluate_all_targets {
        // The initial approach and every yielded coroutine step need only
        // the shared Swipe-style predicate. Do not scan all weapon pairs.
        return Ok(NativeAggressiveCommandSnapshot {
            attack,
            every_member_has_all_targets_in_range: None,
            all_enemies_defenseless: None,
        });
    }
    let own = roster.get(owner).ok_or(GameCommandError::InvalidSquad)?;
    let enemy = roster.get(attack.enemy_owner)
        .ok_or(GameCommandError::InvalidEnemy)?;
    if own.side == enemy.side || own.members.is_empty() ||
        enemy.members.is_empty() {
        return Err(GameCommandError::InvalidEnemy);
    }
    let mut every_member_has_all_targets_in_range = true;
    'own_ships: for &own_id in &own.members {
        let own_ship = world.ship(own_id).ok_or(GameCommandError::InvalidSquad)?;
        if own_ship.vitals.is_dead() || own_ship.side != own.side ||
            !world.ship_body(own_id).is_some_and(|body| body.active)
        {
            return Err(GameCommandError::InvalidSquad);
        }
        for &enemy_id in &enemy.members {
            let target = world.ship(enemy_id).ok_or(GameCommandError::InvalidEnemy)?;
            if target.vitals.is_dead() || target.side != enemy.side ||
                !world.ship_body(enemy_id).is_some_and(|body| body.active)
            {
                return Err(GameCommandError::InvalidEnemy);
            }
            if !weapon_valid_target(own_id, enemy_id)? {
                every_member_has_all_targets_in_range = false;
                break 'own_ships;
            }
        }
    }
    Ok(NativeAggressiveCommandSnapshot {
        attack,
        every_member_has_all_targets_in_range:
            Some(every_member_has_all_targets_in_range),
        all_enemies_defenseless,
    })
}

pub(super) struct AggressiveRun {
    target: NativeAttackTarget,
    pub(super) members: Vec<SquadFormationMember>,
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
            target: NativeAttackTarget::new(enemy.owner),
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

    /// After its final yielded ship order, Unity's IEnumerator resumes
    /// just to clear its scratch list and coroutine handle. No target
    /// lookup or live weapon snapshot occurs on that last frame.
    pub(super) fn coroutine_needs_target(&self) -> bool {
        self.next_coroutine_member.is_some_and(|index|
            index < self.members.len()
        )
    }

    pub(super) fn needs_all_target_range(&self) -> bool {
        self.comfortably_in_range
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

        let all_targets = snapshot.every_member_has_all_targets_in_range
            .ok_or(GameCommandError::MissingCombatSnapshot)?;
        let defenseless = if all_targets &&
            snapshot.attack.own_max_range < HOLD_RANGE_THRESHOLD
        {
            snapshot.all_enemies_defenseless
                .ok_or(GameCommandError::MissingCombatSnapshot)?
        } else {
            false
        };
        if all_targets &&
            (snapshot.attack.own_max_range >= HOLD_RANGE_THRESHOLD || defenseless)
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
        selection: &NativeCommandSelection,
        snapshot: Option<&NativeAggressiveCommandSnapshot>,
    ) -> Result<Option<CommandFinishCause>, GameCommandError> {
        let Some(index) = self.next_coroutine_member else {
            return Ok(None);
        };
        if index == 0 {
            // Source MoveTowardsEnemiesAcrossFrames re-reads GetShips()
            // whenever BeginMoveTowardsEnemies starts a NEW coroutine.
            // The roster owns live membership and preserves GetShips order;
            // the activation-time member list would otherwise still issue
            // tracked orders to ships that died in an earlier run.
            let live = roster.get(owner).ok_or(GameCommandError::InvalidSquad)?;
            self.members.retain(|member|
                roster.owning_squad(member.ship_id) == Some(owner) &&
                world.ship(member.ship_id)
                    .is_some_and(|ship| !ship.vitals.is_dead())
            );
            // A newly added ship needs its authored formation/clearance
            // refreshed by the Level; do not silently omit it.
            if live.members.len() != self.members.len() ||
                !live.members.iter().zip(&self.members).all(|(&id, member)|
                    id == member.ship_id
                )
            {
                return Err(GameCommandError::InvalidSquad);
            }
        }
        if index == self.members.len() {
            // The final post-yield resume does not access EnemySquad,
            // current targets, or weapon data in Unity.
            self.next_coroutine_member = None;
            return Ok(None);
        }
        let snapshot = snapshot.ok_or(GameCommandError::MissingCombatSnapshot)?;
        if !snapshot.attack.enemy_alive {
            self.next_coroutine_member = None;
            return Ok(None);
        }
        if !snapshot.valid() {
            return Err(GameCommandError::InvalidCombatSnapshot);
        }
        self.target.validate(owner, snapshot.attack.enemy_owner)?;
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
