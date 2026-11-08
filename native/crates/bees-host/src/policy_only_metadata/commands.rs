//! Live command projections. No squad membership or physical state is cached here.

use super::*;
use bees_gameplay::{commands::{NativeEnemySquad, NativeCircleCommandSnapshot,
    NativeSwipeCommandSnapshot, NativeAggressiveCommandSnapshot,
    assemble_circle_snapshot_from_live_weapons, assemble_aggressive_snapshot_from_live_weapons},
    squad_geometry::live_squad_bounds_from_catalog};

impl NativePolicyOnlyMetadata {
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
}
