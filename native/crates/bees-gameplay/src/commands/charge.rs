//! Scripted Barge Charge command, using one shared physical Barge engine.
//!
//! Unity sources: Commands/Charge.Execute/Timer and Barge.ChargeForward.
//! The Level supplies the original Charge.ShipsWithinRange, rotation,
//! and unobstructed-sight eligibility; this module does not invent range.

use bees_hivemind::dispatch::ResolvedHiveMindCommand;
use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::CombatWorld;
use bees_sim::simulation::RandomStream;
use bees_sim::squad_movement::{SquadFormationMember, SquadFormationOptions};

use crate::squads::NativeSquadRoster;
use super::{CommandFinishCause, GameCommandError, NativeEnemyTargetStats, NativeSquadOrder};
use super::bombing::{
    NativeBombWeapon, assign_initial_charge_targets,
    retarget_selected_game_bomb, send_bomber_to_selected_target,
    release_superseded_bomb_targets, has_live_game_bomb_target,
};
use super::targeting::NativeAttackTarget;

/// Verified by the owning Level's real Barge Charge weapon and visibility
/// cache, not a proximity approximation or unauthenticated server claim.
pub struct NativeChargeTimerSnapshot {
    pub enemy_owner: SquadLifetime,
    pub enemy_alive: bool,
    /// Every ID must be a Barge whose original Charge.TargetShip is
    /// facing, unobstructed and in its source ShipsWithinRange lookup.
    pub eligible_barges: Vec<i64>,
    /// GetShips-ordered current enemy and weapon metadata, required only
    /// when need_retarget was requested at this command timer boundary.
    pub retarget_enemies: Vec<NativeEnemyTargetStats>,
    pub retarget_weapons: Vec<NativeBombWeapon>,
}

pub struct NativeChargeRun {
    target: NativeAttackTarget,
    pub(super) members: Vec<SquadFormationMember>,
    formation: SquadFormationOptions,
    ranked: Vec<NativeEnemyTargetStats>,
}

impl NativeChargeRun {
    pub fn start(
        world: &mut CombatWorld,
        roster: &NativeSquadRoster,
        random: &mut RandomStream,
        squad: NativeSquadOrder<'_>,
        enemy_owner: SquadLifetime,
        selection: &ResolvedHiveMindCommand,
        weapons: &[NativeBombWeapon],
        enemies: &[NativeEnemyTargetStats],
    ) -> Result<Option<Self>, GameCommandError> {
        // Charge.SetFinalize must be able to stop every surviving Barge,
        // even if the immediate command poll never ran. Reject an
        // unconfigured/policy-owned ship before Bomb target assignments.
        for member in squad.members {
            if !world.ship(member.ship_id)
                .is_some_and(|ship| ship.ship_type == 0) {
                return Err(GameCommandError::InvalidSquad);
            }
            world.validate_ship_game_order(member.ship_id)?;
        }
        let mut target = NativeAttackTarget::new(enemy_owner);
        target.validate(squad.owner, enemy_owner)?;
        if !assign_initial_charge_targets(
            world, roster, random, squad, enemy_owner,
            selection, weapons, enemies,
        )? {
            return Ok(None);
        }
        Ok(Some(Self {
            target,
            members: squad.members.to_vec(),
            formation: squad.formation,
            ranked: Vec::new(),
        }))
    }

    pub fn enemy_squad_id(&self) -> i64 {
        self.target.squad_id()
    }

    pub fn needs_retarget(&self, world: &CombatWorld) -> bool {
        self.members.iter().any(|member| {
            let Some(ship) = world.ship(member.ship_id) else {
                return false;
            };
            !ship.vitals.is_dead() &&
                world.game_barge_charge_completed(member.ship_id).is_none() &&
                !has_live_game_bomb_target(world, member.ship_id)
        })
    }

    /// Unity Charge.Timer applies the same command to each Barge in
    /// GetShips order. Physical wind-up/contact/recoil/cooldown proceeds
    /// independently each fixed tick in CombatWorld.
    pub fn on_poll(
        &mut self,
        world: &mut CombatWorld,
        owner: SquadLifetime,
        snapshot: &NativeChargeTimerSnapshot,
        selection: &ResolvedHiveMindCommand,
        roster: &NativeSquadRoster,
        random: &mut RandomStream,
    ) -> Result<Option<CommandFinishCause>, GameCommandError> {
        self.target.validate(owner, snapshot.enemy_owner)?;
        if !snapshot.enemy_alive {
            return Ok(Some(CommandFinishCause::EnemySquadGone));
        }
        let mut live_count = 0_usize;
        let mut completed_count = 0_usize;
        for &member in &self.members {
            let Some(ship) = world.ship(member.ship_id) else {
                return Err(GameCommandError::InvalidSquad);
            };
            if ship.vitals.is_dead() { continue }
            live_count += 1;
            match world.game_barge_charge_completed(member.ship_id) {
                Some(true) => { completed_count += 1; continue }
                Some(false) => continue,
                None => {}
            }
            let prior_target_live =
                has_live_game_bomb_target(world, member.ship_id);
            if !retarget_selected_game_bomb(
                world, roster, random, member.ship_id,
                snapshot.enemy_owner, selection,
                &snapshot.retarget_weapons, &snapshot.retarget_enemies,
                &mut self.ranked,
            )? {
                return Ok(Some(CommandFinishCause::NoEnemyTargets));
            }
            // Source Charge.Timer picks a replacement target during this
            // callback, but first movement toward it waits until the next
            // callback. Eligibility also belongs to the old target.
            if !prior_target_live {
                continue;
            }
            if snapshot.eligible_barges.binary_search(
                &member.ship_id,
            ).is_ok() {
                world.try_start_game_barge_charge(
                    member.ship_id, selection.command_outcome_id,
                    self.formation.map_bounds,
                    self.formation.can_override_bounds,
                )?;
            } else {
                send_bomber_to_selected_target(
                    world, member, self.formation,
                )?;
            }
        }
        Ok((live_count > 0 && live_count == completed_count)
            .then_some(CommandFinishCause::ChargeCompleted))
    }

    /// Unity Charge.SetFinalize cancels every currently-owned coroutine.
    /// A valid new Bomb command may have already retargeted the same
    /// weapon, so preserve leases for members retained by that new command.
    pub fn release_superseded(
        &self, world: &mut CombatWorld, retained_ids: &[i64],
    ) {
        for member in &self.members {
            world.reset_game_barge_charge(member.ship_id);
        }
        release_superseded_bomb_targets(
            world, &self.members, retained_ids,
        );
    }

    /// Charge.SetFinalize stops every surviving Barge, including those
    /// still pursuing and those that never qualified to start a charge.
    /// This is distinct from supersession: that path must preserve the
    /// replacement command's newly installed movement order.
    pub fn release_all(
        &self, world: &mut CombatWorld,
    ) -> Result<(), GameCommandError> {
        for member in &self.members {
            world.reset_game_barge_charge(member.ship_id);
            if world.ship(member.ship_id).is_some_and(|ship|
                !ship.vitals.is_dead()
            ) && world.ship_body(member.ship_id).is_some_and(|body|
                body.active
            ) {
                world.stop_ship_game_movement(member.ship_id)?;
            }
            world.release_game_bomb_target_for_ship(member.ship_id);
        }
        Ok(())
    }
}
