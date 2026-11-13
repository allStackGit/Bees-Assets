//! Live command projections. No squad membership or physical state is cached here.

use super::*;
use bees_gameplay::{commands::{NativeEnemySquad, NativeCircleCommandSnapshot,
    NativeSwipeCommandSnapshot, NativeAggressiveCommandSnapshot,
    assemble_circle_snapshot_from_live_weapons, assemble_aggressive_snapshot_from_live_weapons},
    squad_geometry::live_squad_bounds_from_catalog};

impl NativePolicyOnlyMetadata {
    /// Exact source Weapon.IsShipValidTarget for imported ordinary turrets:
    /// source collider membership, lazy target-bounds latch and obstacle
    /// linecast are all owned by CombatWorld. Unsupported native special
    /// weapons have no equivalent source validity query yet; fail closed.
    pub(super) fn source_ship_valid_target(
        &self, world: &mut CombatWorld, attacker: i64, target: i64,
    ) -> Result<bool, GameCommandError> {
        let source = self.authored_ships.get(&attacker)
            .ok_or(GameCommandError::MissingCombatSnapshot)?;
        if world.special_weapon_spec(attacker).is_some() {
            return Err(GameCommandError::MissingCombatSnapshot);
        }
        for slot in 0..5 {
            if world.weapon_range(attacker, slot)?.is_some() {
                if world.is_weapon_ship_target_valid(attacker, slot, target)? {
                    return Ok(true);
                }
            } else if source.observed_weapons[slot].is_some() {
                return Err(GameCommandError::MissingCombatSnapshot);
            }
        }
        Ok(false)
    }

    pub(super) fn live_bombing_snapshot(&self, world: &CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, enemy: SquadLifetime, need_retarget: bool,
    ) -> Result<bees_gameplay::commands::NativeBombingTimerSnapshot, GameCommandError> {
        let own = roster.get(owner).ok_or(GameCommandError::InvalidSquad)?;
        let enemy_alive = roster.get(enemy).is_some();
        let mut nearby_targets = Vec::new();
        for &id in &own.members {
            if let Some(target) = world.game_bomb_current_target(id) {
                if world.bomb_target_is_nearby(id, target)? { nearby_targets.push((id, target)); }
            }
        }
        let mut retarget_weapons = Vec::new(); let mut retarget_enemies = Vec::new();
        if enemy_alive && need_retarget {
            self.collect_bomb_activation(world, roster, owner, enemy, &mut retarget_weapons, &mut retarget_enemies)?;
        }
        Ok(bees_gameplay::commands::NativeBombingTimerSnapshot { enemy_owner: enemy, enemy_alive,
            nearby_targets, retarget_weapons, retarget_enemies })
    }

    pub(super) fn live_charge_snapshot(&self, world: &CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, enemy: SquadLifetime, need_retarget: bool,
    ) -> Result<bees_gameplay::commands::NativeChargeTimerSnapshot, GameCommandError> {
        let own = roster.get(owner).ok_or(GameCommandError::InvalidSquad)?;
        let enemy_alive = roster.get(enemy).is_some();
        let mut eligible_barges = Vec::new();
        for &id in &own.members {
            if let Some(target) = world.game_bomb_current_target(id) {
                if world.barge_target_within_charging_range(id, target)? { eligible_barges.push(id); }
            }
        }
        eligible_barges.sort_unstable();
        let mut retarget_weapons = Vec::new(); let mut retarget_enemies = Vec::new();
        if enemy_alive && need_retarget {
            self.collect_bomb_activation(world, roster, owner, enemy, &mut retarget_weapons, &mut retarget_enemies)?;
        }
        Ok(bees_gameplay::commands::NativeChargeTimerSnapshot { enemy_owner: enemy, enemy_alive,
            eligible_barges, retarget_weapons, retarget_enemies })
    }

    fn command_center(&self, world: &CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime,
    ) -> Result<Vec2, GameCommandError> {
        let squad = roster.get(owner).ok_or(GameCommandError::InvalidSquad)?;
        live_squad_bounds_from_catalog(world, squad, &self.ship_catalog)
            .map(|bounds| bounds.center).map_err(GameCommandError::SquadGeometry)
    }

    fn squad_range(&self, world: &CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime,
    ) -> Result<f32, GameCommandError> {
        let squad = roster.get(owner).ok_or(GameCommandError::InvalidSquad)?;
        squad.members.iter().try_fold(0.0_f32, |range, &id|
            Ok(range.max(world.weapon_target_rank_stats(id)?.max_range as f32)))
    }

    pub(super) fn live_enemy(&self, world: &CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, enemy: SquadLifetime,
    ) -> Result<NativeEnemySquad, GameCommandError> {
        let own = roster.get(owner).ok_or(GameCommandError::InvalidSquad)?;
        let target = roster.get(enemy).ok_or(GameCommandError::InvalidEnemy)?;
        if owner.level_id != enemy.level_id || own.side == target.side || target.members.is_empty() {
            return Err(GameCommandError::InvalidEnemy);
        }
        Ok(NativeEnemySquad { owner: enemy, is_alive: true,
            position: self.command_center(world, roster, enemy)?,
            max_range: self.squad_range(world, roster, enemy)?,
        })
    }

    fn ship_has_target(&self, world: &mut CombatWorld, own: i64, enemy: i64)
        -> Result<bool, GameCommandError>
    {
        let source = self.authored_ships.get(&own).ok_or(GameCommandError::MissingCombatSnapshot)?;
        for slot in 0..5 {
            if world.special_weapon_spec(own).is_some_and(|spec| spec.slot == slot) {
                return Ok(world.ship(enemy).is_some_and(|ship| !ship.vitals.is_dead()));
            }
            // Unsupported Bomb/Charge subclasses cannot masquerade as unarmed
            // or use turret eligibility. Their real adapter remains required.
            if source.observed_weapons[slot].is_some() && world.weapon_range(own, slot)?.is_none() {
                return Err(GameCommandError::MissingCombatSnapshot);
            }
            if world.is_weapon_ship_target_valid(own, slot, enemy)? { return Ok(true); }
        }
        Ok(false)
    }

    pub(super) fn live_circle_snapshot(&self, world: &mut CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, enemy: SquadLifetime,
    ) -> Result<NativeCircleCommandSnapshot, GameCommandError> {
        let center = self.command_center(world, roster, owner)?;
        // A retired exact lifetime is terminal; never look up a replacement by
        // numeric Squad ID. Its center is not consumed by the command.
        let enemy_center = if roster.get(enemy).is_some() {
            self.command_center(world, roster, enemy)?
        } else { Vec2::ZERO };
        assemble_circle_snapshot_from_live_weapons(world, roster, owner, enemy, center, enemy_center,
            |world, id| Ok(world.weapon_target_rank_stats(id)?.max_range as f32),
            |world, own, enemy| self.ship_has_target(world, own, enemy))
    }

    pub(super) fn live_swipe_snapshot(&self, world: &mut CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, enemy: SquadLifetime, need_candidates: bool,
    ) -> Result<NativeSwipeCommandSnapshot, GameCommandError> {
        let common = self.live_circle_snapshot(world, roster, owner, enemy)?;
        let mut candidates = Vec::new();
        let enemy_max_range = if common.enemy_alive {
            if need_candidates {
                for &id in &roster.get(enemy).expect("live exact enemy").members {
                    candidates.push(world.weapon_target_rank_stats(id)?);
                }
            }
            self.squad_range(world, roster, enemy)?
        } else { 0.0 };
        Ok(NativeSwipeCommandSnapshot {
            enemy_owner: enemy, enemy_alive: common.enemy_alive,
            squad_center: common.squad_center, enemy_center: common.enemy_center,
            own_max_range: common.own_max_range, enemy_max_range,
            every_member_has_target_in_range: common.every_member_has_target_in_range,
            candidates,
        })
    }

    pub(super) fn live_aggressive_snapshot(&self, world: &mut CombatWorld, roster: &NativeSquadRoster,
        owner: SquadLifetime, enemy: SquadLifetime, need_all: bool,
    ) -> Result<NativeAggressiveCommandSnapshot, GameCommandError> {
        let attack = self.live_swipe_snapshot(world, roster, owner, enemy, true)?;
        let defenseless = if attack.enemy_alive && need_all {
            Some(roster.get(enemy).expect("live exact enemy").members.iter().try_fold(true, |value, &id|
                Ok::<_, GameCommandError>(value && world.weapon_target_rank_stats(id)?.firepower == 0.0))?)
        } else { None };
        assemble_aggressive_snapshot_from_live_weapons(world, roster, owner, attack, need_all, defenseless,
            |world, own, enemy| self.ship_has_target(world, own, enemy))
    }

    /// Unity Squad.CheckChase: one source-timed decision every second,
    /// only for live user squads whose ShouldChase is set. Choose the
    /// nearest enemy squad BEFORE checking visibility; do not fall back
    /// to the second-nearest unseen target or use persistent HiveMind
    /// discovery as live human sight. The resulting user command uses
    /// the existing authoritative Level event/command activation path.
    pub(super) fn advance_source_chase(
        &mut self,
        world: &mut CombatWorld,
        events: &mut bees_gameplay::level_events::NativeLevelEventProcessor,
        random: &mut bees_sim::simulation::RandomStream,
        completed_tick: u64,
        dt_seconds: f32,
    ) -> Result<u64, bees_gameplay::level_events::NativeLevelEventError> {
        use bees_gameplay::{
            commands::{NativeLocalCommandAction, NativeLocalCommandSource},
            level_events::{NativeLevelEventError, NativeLocalBombingTargets},
            level_inputs::native_squad_order_from_catalog,
        };
        use bees_hivemind::dispatch::SquadCommandComposition;
        let err = NativeLevelEventError::Commands;
        if !dt_seconds.is_finite() || dt_seconds <= 0.0 {
            return Err(err(GameCommandError::InvalidSettings));
        }
        self.chase_elapsed_seconds += dt_seconds;
        // Unity ScaledTimer.Update fires only when Elapsed > Length,
        // then preserves the remainder of the recurring interval. Its
        // Squad.Setup call used isRecurring=true, not startImmediate.
        if self.chase_elapsed_seconds <= 1.0 { return Ok(0); }
        self.chase_elapsed_seconds %= 1.0;
        self.chase_measurements.timer_callbacks += 1;
        if self.player.chasing.is_empty() ||
            !self.player.chasing.iter().any(|&owner|
                events.roster().get(owner).is_some() &&
                !events.commands().is_chasing_command(owner) &&
                !self.scripted_queues.iter().any(|queue| queue.owner == owner) &&
                self.player.squads.get(&owner)
                    .is_some_and(|p| p.user_controlled && p.can_accept_input))
        {
            return Ok(0);
        }

        // Compute only one center per live squad per due callback. Unity's
        // Squad.DistanceToPoint compares these authored sprite AABB centers
        // (not pathfinder endpoints, physical collider radii or sight memory).
        let roster = events.roster();
        let mut centers = Vec::with_capacity(roster.source_order().len());
        for &owner in roster.source_order() {
            let Some(squad) = roster.get(owner) else { continue };
            let center = live_squad_bounds_from_catalog(
                world, squad, &self.ship_catalog,
            ).map_err(|error| err(GameCommandError::SquadGeometry(error)))?.center;
            centers.push((owner, squad.side, center));
        }
        let mut chosen = Vec::new();
        for &(owner, side, origin) in &centers {
            if !self.player.chasing.contains(&owner) ||
                events.commands().is_chasing_command(owner) ||
                self.scripted_queues.iter().any(|queue| queue.owner == owner) ||
                !self.player.squads.get(&owner).is_some_and(|p|
                    p.user_controlled && p.can_accept_input)
            { continue; }
            self.chase_measurements.eligible_squads += 1;
            let mut nearest = None;
            let mut nearest_distance = f32::INFINITY;
            for &(enemy_owner, enemy_side, enemy_center) in &centers {
                if side == enemy_side { continue; }
                let distance = (origin - enemy_center).length_squared();
                if distance < nearest_distance {
                    nearest_distance = distance;
                    nearest = Some(enemy_owner);
                }
            }
            let Some(enemy_owner) = nearest else { continue };
            let ours = roster.get(owner).ok_or_else(||
                err(GameCommandError::InvalidSquad))?;
            let enemy = roster.get(enemy_owner).ok_or_else(||
                err(GameCommandError::InvalidEnemy))?;
            let mut visible = false;
            'sight: for &observer_id in &ours.members {
                let source_sight = self.effective_vision.get(&observer_id)
                    .and_then(|vision| vision.source_sight)
                    .ok_or_else(|| err(GameCommandError::MissingCombatSnapshot))?;
                let observer = world.ship_body(observer_id)
                    .ok_or_else(|| err(GameCommandError::InvalidSquad))?;
                if !observer.active { continue; }
                let observer_position = observer.motion.position;
                for &target_id in &enemy.members {
                    let Some(target) = world.ship_body(target_id)
                        .filter(|body| body.active) else { continue };
                    self.chase_measurements.sight_pairs_checked += 1;
                    let can_see = if source_sight > 0 {
                        (observer_position - target.motion.position).length_squared()
                            <= (source_sight as f32).powi(2)
                    } else {
                        // Source Ship.CanSeeShip falls back to actual
                        // Weapon.IsShipValidTarget when Sight is zero.
                        self.source_ship_valid_target(world, observer_id, target_id)
                            .map_err(&err)?
                    };
                    if can_see { visible = true; break 'sight; }
                }
            }
            if visible {
                self.chase_measurements.visible_targets += 1;
                chosen.push((owner, enemy_owner));
            }
        }
        if chosen.is_empty() { return Ok(0); }
        world.refresh_world_navigation(
            &mut self.navigation, true, self.activate_collision_asteroids,
        ).map_err(|error| err(error.into()))?;

        let mut started = 0u64;
        let mut members = Vec::new();
        let mut bomb_weapons = Vec::new();
        let mut bomb_enemies = Vec::new();
        for (owner, enemy_owner) in chosen {
            let roster = events.roster();
            let squad = roster.get(owner).ok_or_else(||
                err(GameCommandError::InvalidSquad))?;
            let source = *self.player.commands.get(&owner)
                .ok_or_else(|| err(GameCommandError::MissingCombatSnapshot))?;
            let enemy = self.live_enemy(world, roster, owner, enemy_owner).map_err(&err)?;
            let composition = SquadCommandComposition::from_unity_ship_types(
                squad.members.iter().filter_map(|&ship_id|
                    world.ship(ship_id).map(|ship| i32::from(ship.ship_type)))
            );
            let action = if composition.only_barges { NativeLocalCommandAction::Charge }
                else if composition.only_bombers { NativeLocalCommandAction::BombingRun }
                else { NativeLocalCommandAction::TargetEnemy { target: enemy_owner } };
            bomb_weapons.clear();
            bomb_enemies.clear();
            let carrier = if composition.only_barges || composition.only_bombers {
                Some(self.collect_bomb_activation(
                    world, roster, owner, enemy_owner,
                    &mut bomb_weapons, &mut bomb_enemies,
                ).map_err(&err)?)
            } else {
                // Player UserAggressive checks the authentic current
                // attack snapshot before superseding the old command.
                self.live_aggressive_snapshot(world, roster, owner, enemy_owner, false)
                    .map_err(&err)?;
                None
            };
            let order = native_squad_order_from_catalog(
                world, squad, &self.ship_catalog,
                self.player_formation_options(), Some(enemy), None,
                |id| self.player.ships.get(&id).map(|f| f.offset),
                |id| self.player.ships.get(&id).map(|f| f.clearance),
                &mut members,
            ).map_err(&err)?;
            let bombing = carrier.map(|is_carrier_squad| NativeLocalBombingTargets {
                enemy_owner, weapons: &bomb_weapons, enemies: &bomb_enemies,
                is_carrier_squad,
            });
            let activation = events.activate_local_command(
                completed_tick, world, &self.navigation, random,
                order, &[], source.settings, NativeLocalCommandSource::Player,
                action, source.shooting, bombing,
            )?;
            // Source MakeUserCommand finalizes its predecessor before
            // attempting replacement, even if activation is unavailable.
            // Command.SetFinalize releases Lock On in that transaction.
            if let Some(permission) = self.player.squads.get_mut(&owner) {
                permission.locked_on = false;
            }
            if matches!(activation, bees_gameplay::commands::Activation::Applied { .. }) {
                started += 1;
            }
        }
        self.chase_measurements.commands_started += started;
        Ok(started)
    }
}
