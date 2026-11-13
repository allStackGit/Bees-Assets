//! Source Squad.RunCommandQueue over the one native Level command director.
//!
//! Prepared mission movements are authored immutable values. The queue owns
//! only pending order and cursor; CombatWorld owns movement and the canonical
//! director owns timers, completion and command identifiers. No independent
//! scripted physics or HiveMind outcome IDs are created.

use std::collections::HashSet;

use bees_gameplay::commands::{
    Activation, GameCommandError, NativeLocalCommandAction,
    NativeLocalCommandSource,
};
use bees_gameplay::level_events::{NativeLevelEventError, NativeLevelEventProcessor};
use bees_gameplay::level_inputs::native_squad_order_from_catalog;
use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::CombatWorld;
use bees_sim::geometry::Vec2;
use bees_sim::simulation::RandomStream;
use bees_sim::squad_movement::SquadFormationOptions;

use super::NativePolicyOnlyMetadata;

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum NativeScriptedOrder {
    MoveToPoint { destination: Vec2 },
    MoveToRandom { range: f32 },
    /// The source Queue's prepared Command.EnemySquad reference. A mission
    /// must identify an actual opposing squad, never a guessed nearest side.
    Aggressive { enemy_squad_item_id: i64 },
}

impl NativeScriptedOrder {
    fn action(self) -> NativeLocalCommandAction<'static> {
        match self {
            Self::MoveToPoint { destination } =>
                NativeLocalCommandAction::MoveToPoint { destination },
            Self::MoveToRandom { range } =>
                NativeLocalCommandAction::MoveToRandom { range },
            Self::Aggressive { .. } => NativeLocalCommandAction::Aggressive,
        }
    }

    fn valid(self) -> bool {
        match self {
            Self::MoveToPoint { destination } => destination.is_finite(),
            Self::MoveToRandom { range } => range.is_finite() && range > 0.0,
            Self::Aggressive { enemy_squad_item_id } => enemy_squad_item_id > 0,
        }
    }
}

/// Captured scenario orders, keyed by the source Squad.ItemId, not the
/// recycled hotkey number. The initial order starts on the first completed
/// fixed step; delayed/cutscene-triggered orders require their actual Level
/// trigger to enqueue this plan at that boundary.
pub struct NativeScriptedQueuePlan {
    pub squad_item_id: i64,
    pub repeat: bool,
    pub orders: Vec<NativeScriptedOrder>,
}

pub(super) struct NativeScriptedQueueState {
    pub(super) owner: SquadLifetime,
    orders: Vec<NativeScriptedOrder>,
    cursor: usize,
    repeat: bool,
}

impl NativePolicyOnlyMetadata {
    /// Authenticate the whole plan before entering scene construction.
    /// Only initially deployed squads are eligible. Deferred reinforcements
    /// must be enrolled after their actual source deployment, not assigned
    /// commands before their physical ship identities exist.
    pub fn stage_scripted_queues(
        &mut self,
        squads: &[crate::scene_builder::NativeAuthoredSquad],
        plans: Vec<NativeScriptedQueuePlan>,
    ) -> Result<(), &'static str> {
        let mut seen = HashSet::new();
        let mut staged = Vec::with_capacity(plans.len());
        for plan in plans {
            if plan.squad_item_id <= 0 || !seen.insert(plan.squad_item_id) ||
                plan.orders.is_empty() || plan.orders.len() > 256 ||
                plan.orders.iter().any(|order| !order.valid())
            {
                return Err("invalid scripted command plan");
            }
            let squad = squads.iter().find(|squad|
                squad.owner.squad_id == plan.squad_item_id
            ).ok_or("scripted commands require an initially deployed squad")?;
            if !self.player.commands.contains_key(&squad.owner) ||
                squad.members.iter().any(|id| !self.player.ships.contains_key(id))
            {
                return Err("scripted command source lacks formation or command settings");
            }
            for order in &plan.orders {
                if let NativeScriptedOrder::Aggressive { enemy_squad_item_id } = order {
                    if !squads.iter().any(|enemy|
                        enemy.owner.squad_id == *enemy_squad_item_id &&
                        enemy.owner.level_id == squad.owner.level_id &&
                        enemy.side != squad.side
                    ) {
                        return Err("scripted Aggressive needs an authenticated opposing squad");
                    }
                }
            }
            staged.push(NativeScriptedQueueState {
                owner: squad.owner, orders: plan.orders,
                cursor: 0, repeat: plan.repeat,
            });
        }
        // NativeAuthoredLevel.squads is the source's actual Level.State.Squads
        // registration order. Preserve it across independent queue pulses.
        staged.sort_by_key(|state| squads.iter()
            .position(|squad| squad.owner == state.owner)
            .expect("validated live authored squad"));
        self.scripted_queues = staged;
        Ok(())
    }

    pub(super) fn advance_source_scripted(
        &mut self, world: &mut CombatWorld,
        events: &mut NativeLevelEventProcessor, random: &mut RandomStream,
        completed_tick: u64,
    ) -> Result<u64, NativeLevelEventError> {
        if self.scripted_queues.is_empty() { return Ok(0); }
        let mut started = 0;
        let mut index = 0;
        while index < self.scripted_queues.len() {
            let owner = self.scripted_queues[index].owner;
            if events.roster().get(owner).is_none() {
                self.scripted_queues.remove(index);
                continue;
            }
            if events.commands().has_active(owner) {
                index += 1;
                continue;
            }
            let state = &self.scripted_queues[index];
            if state.cursor == state.orders.len() {
                if !state.repeat {
                    self.scripted_queues.remove(index);
                    continue;
                }
                self.scripted_queues[index].cursor = 0;
            }
            let order = self.scripted_queues[index].orders[self.scripted_queues[index].cursor];
            let source = *self.player.commands.get(&owner)
                .ok_or(NativeLevelEventError::Commands(GameCommandError::MissingCombatSnapshot))?;
            let enemy = if let NativeScriptedOrder::Aggressive { enemy_squad_item_id } = order {
                let enemy_owner = events.roster().source_order().iter()
                    .copied().find(|candidate|
                        candidate.squad_id == enemy_squad_item_id
                    );
                let Some(enemy_owner) = enemy_owner else {
                    // Unity Aggressive's first timer would finish when
                    // EnemySquad has already been killed. Skip its now
                    // unavailable prepared order without credit or a
                    // second physical action.
                    self.scripted_queues[index].cursor += 1;
                    index += 1;
                    continue;
                };
                let enemy = self.live_enemy(world, events.roster(), owner, enemy_owner)
                    .map_err(NativeLevelEventError::Commands)?;
                self.live_aggressive_snapshot(
                    world, events.roster(), owner, enemy_owner, false,
                ).map_err(NativeLevelEventError::Commands)?;
                Some(enemy)
            } else { None };
            let squad = events.roster().get(owner)
                .ok_or(NativeLevelEventError::Commands(GameCommandError::InvalidSquad))?;
            let formation = SquadFormationOptions {
                map_bounds: self.bounds, has_obstacles: self.player.has_obstacles,
                can_override_bounds: false,
            };
            let order_view = native_squad_order_from_catalog(
                world, squad, &self.ship_catalog, formation, enemy, None,
                |id| self.player.ships.get(&id).map(|source| source.offset),
                |id| self.player.ships.get(&id).map(|source| source.clearance),
                &mut self.scripted_members,
            ).map_err(NativeLevelEventError::Commands)?;
            // Refresh once for the first source movement on this tick.
            // All later queue members reuse this exact world-owned grid.
            if started == 0 {
                world.refresh_world_navigation(
                    &mut self.navigation, true, self.activate_collision_asteroids,
                ).map_err(|error| NativeLevelEventError::Commands(
                    GameCommandError::World(error),
                ))?;
            }
            let result = events.activate_local_command(
                completed_tick, world, &self.navigation, random, order_view, &[],
                source.settings, NativeLocalCommandSource::Scripted,
                order.action(), source.shooting, None,
            )?;
            if !matches!(result, Activation::Applied { .. }) {
                return Err(NativeLevelEventError::Commands(GameCommandError::MissingCombatSnapshot));
            }
            self.scripted_queues[index].cursor += 1;
            started += 1;
            index += 1;
        }
        Ok(started)
    }
}
