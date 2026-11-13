//! Source Squad.RunCommandQueue over the one native Level command director.
//!
//! Prepared mission movements and targeting are immutable source values. The queue owns
//! only pending order and cursor; CombatWorld owns movement and the canonical
//! director owns timers, completion and command identifiers. No independent
//! scripted physics or HiveMind outcome IDs are created.

use std::collections::HashSet;
use std::sync::Arc;

use bees_gameplay::commands::{
    Activation, GameCommandError, NativeCommandOrigin, NativeScoutingContext,
    NativeLocalCommandAction, NativeLocalCommandSource,
};
use bees_gameplay::level_events::{
    NativeLevelEventError, NativeLevelEventProcessor, NativeLocalBombingTargets,
};
use bees_gameplay::campaign_dialogue::CampaignDialogueSpec;
use bees_gameplay::level_inputs::{
    collect_live_friendlies_from_catalog, native_squad_order_from_catalog,
};
use bees_gameplay::squads::NativeSquadRoster;
use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::CombatWorld;
use bees_sim::geometry::Vec2;
use bees_sim::simulation::RandomStream;
use bees_sim::squad_movement::SquadFormationOptions;

use super::{NativePolicyOnlyMetadata, NativeMissionCue};
use super::mission_cues::NativeMissionEvent;

#[derive(Clone, Debug, PartialEq)]
pub enum NativeScriptedOrder {
    MoveToPoint { destination: Vec2 },
    MoveToRandom { range: f32 },
    /// Prepared source EnemySquad identity. Never guess an opponent.
    Aggressive { enemy_squad_item_id: i64 },
    TargetEnemy { enemy_squad_item_id: i64 },
    BombingRun { enemy_squad_item_id: i64 },
    Charge { enemy_squad_item_id: i64 },
    /// Prepared source Squad.GuardedSquad identity.
    Guard { friendly_squad_item_id: i64 },
    Patrol { top_left: Vec2, bottom_right: Vec2 },
    Hold,
    ClosestFriendly,
    /// Genuine source Squad.MaxSight and ConfigData.HumanSide.
    Scouting { max_sight: f32, human_side: i32 },
    Mining { asteroid_id: i64 },
    Heal { beehive_ids: Arc<[i64]> },
    FullRetreat { warp_gate_id: i64 },
}

impl NativeScriptedOrder {
    fn action(&self, target: Option<SquadLifetime>) -> NativeLocalCommandAction<'_> {
        match *self {
            Self::MoveToPoint { destination } =>
                NativeLocalCommandAction::MoveToPoint { destination },
            Self::MoveToRandom { range } =>
                NativeLocalCommandAction::MoveToRandom { range },
            Self::Aggressive { .. } => NativeLocalCommandAction::Aggressive,
            Self::TargetEnemy { .. } => NativeLocalCommandAction::TargetEnemy {
                target: target.expect("authenticated current enemy squad"),
            },
            Self::BombingRun { .. } => NativeLocalCommandAction::BombingRun,
            Self::Charge { .. } => NativeLocalCommandAction::Charge,
            Self::Guard { .. } => NativeLocalCommandAction::Guard {
                target: target.expect("authenticated current friendly squad"),
            },
            Self::Patrol { top_left, bottom_right } =>
                NativeLocalCommandAction::Patrol { top_left, bottom_right },
            Self::Hold => NativeLocalCommandAction::Hold,
            Self::ClosestFriendly => NativeLocalCommandAction::ClosestFriendly,
            Self::Scouting { .. } => NativeLocalCommandAction::Scouting,
            Self::Mining { asteroid_id } =>
                NativeLocalCommandAction::Mining { asteroid_id },
            Self::Heal { ref beehive_ids } =>
                NativeLocalCommandAction::Heal { beehive_ids },
            Self::FullRetreat { warp_gate_id } =>
                NativeLocalCommandAction::FullRetreat { warp_gate_id },
        }
    }

    fn valid(&self) -> bool {
        match *self {
            Self::MoveToPoint { destination } => destination.is_finite(),
            Self::MoveToRandom { range } => range.is_finite(),
            Self::Aggressive { enemy_squad_item_id } |
            Self::TargetEnemy { enemy_squad_item_id } |
            Self::BombingRun { enemy_squad_item_id } |
            Self::Charge { enemy_squad_item_id } => enemy_squad_item_id > 0,
            Self::Guard { friendly_squad_item_id } => friendly_squad_item_id > 0,
            Self::Patrol { top_left, bottom_right } =>
                top_left.is_finite() && bottom_right.is_finite(),
            Self::Hold | Self::ClosestFriendly => true,
            Self::Scouting { max_sight, human_side } =>
                max_sight.is_finite() && max_sight >= 0.0 && human_side > 0,
            Self::Mining { asteroid_id } => asteroid_id > 0,
            Self::Heal { ref beehive_ids } =>
                !beehive_ids.is_empty() && beehive_ids.iter().all(|id| *id > 0),
            Self::FullRetreat { warp_gate_id } => warp_gate_id > 0,
        }
    }

    fn target_squad(&self) -> Option<(i64, bool)> {
        match *self {
            Self::Aggressive { enemy_squad_item_id } |
            Self::TargetEnemy { enemy_squad_item_id } |
            Self::BombingRun { enemy_squad_item_id } |
            Self::Charge { enemy_squad_item_id } =>
                Some((enemy_squad_item_id, true)),
            Self::Guard { friendly_squad_item_id } =>
                Some((friendly_squad_item_id, false)),
            _ => None,
        }
    }
}

/// Captured scenario orders, keyed by the source Squad.ItemId, not the
/// recycled hotkey number. The initial order starts on the first completed
/// fixed step; delayed/cutscene-triggered orders require their actual Level
/// trigger to enqueue this plan at that boundary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum NativeScriptedActivation {
    /// Source Squad.RunCommandQueue called at Level start.
    Immediate,
    /// A source-authored elapsed Level time, never elapsed worker wall time.
    AfterSeconds(f32),
    /// Run as soon as the specified deferred source squad enters the roster.
    OnDeployment,
    /// Wait for the authenticated reinforcement entry movement to finish
    /// before running a mission command that would replace that movement.
    OnArrival,
    /// Subscribe to the same-tick authored campaign notification.
    /// A cue is latched once even if the intended squad deploys later.
    OnCampaignCue(NativeMissionCue),
    /// A canonical CombatEvent::ShipDied from this world's authored
    /// squad roster. The trigger is a real event, not an absent-ship poll.
    OnShipDestroyed(i64),
}

pub struct NativeScriptedQueuePlan {
    pub squad_item_id: i64,
    pub repeat: bool,
    pub activation: NativeScriptedActivation,
    pub orders: Vec<NativeScriptedOrder>,
}

pub(super) struct NativeScriptedQueueState {
    pub(super) owner: SquadLifetime,
    orders: Vec<NativeScriptedOrder>,
    cursor: usize,
    repeat: bool,
    ready_tick: u64,
    on_deployment: bool,
    wait_for_entry: bool,
    required_event: Option<NativeMissionEvent>,
    event_armed: bool,
    /// A dormant source trigger does not suppress HiveMind commands before
    /// the mission event actually claims this live squad. Once claimed,
    /// a late server reply cannot take ownership back.
    pub(super) claimed: bool,
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
        deferred: &HashSet<i64>,
        with_entry_order: &HashSet<i64>,
        known_ships: &HashSet<i64>,
        dt_seconds: f32,
        campaign: Option<CampaignDialogueSpec>,
    ) -> Result<(), &'static str> {
        if !dt_seconds.is_finite() || dt_seconds <= 0.0 {
            return Err("scripted queue requires the source Level fixed-step duration");
        }
        if !self.scripted_queues.is_empty() {
            return Err("initial mission queues were already installed");
        }
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
                if let Some((target_id, enemy)) = order.target_squad() {
                    if !squads.iter().any(|target|
                        target.owner.squad_id == target_id &&
                        target.owner.level_id == squad.owner.level_id &&
                        (target.side != squad.side) == enemy &&
                        (enemy || target.owner != squad.owner)
                    ) {
                        return Err("scripted command needs an authenticated live friendly or opposing squad");
                    }
                }
            }
            let is_deferred = deferred.contains(&plan.squad_item_id);
            if (is_deferred && !matches!(plan.activation,
                    NativeScriptedActivation::OnDeployment |
                    NativeScriptedActivation::OnArrival |
                    NativeScriptedActivation::OnCampaignCue(_) |
                    NativeScriptedActivation::OnShipDestroyed(_))) ||
                (!is_deferred && matches!(plan.activation,
                    NativeScriptedActivation::OnDeployment |
                    NativeScriptedActivation::OnArrival)) {
                return Err("deferred scripted squads require a deployment, arrival or campaign cue gate");
            }
            if matches!(plan.activation, NativeScriptedActivation::OnArrival) &&
                !with_entry_order.contains(&plan.squad_item_id) {
                return Err("on_arrival requires a deferred squad with a real entry movement");
            }
            let required_event = match plan.activation {
                NativeScriptedActivation::OnCampaignCue(cue) => {
                    let event = NativeMissionEvent::CampaignCue(cue);
                    if !event.valid_for(campaign, |_| false) {
                        return Err("scripted campaign cue is not available in this Level");
                    }
                    Some(event)
                }
                NativeScriptedActivation::OnShipDestroyed(ship_id) => {
                    let event = NativeMissionEvent::ShipDestroyed(ship_id);
                    if !event.valid_for(campaign, |id| known_ships.contains(&id)) {
                        return Err("scripted ship-death trigger needs an authored source ship");
                    }
                    Some(event)
                }
                _ => None,
            };
            let ready_tick = match plan.activation {
                NativeScriptedActivation::Immediate |
                NativeScriptedActivation::OnDeployment |
                NativeScriptedActivation::OnArrival |
                NativeScriptedActivation::OnCampaignCue(_) |
                NativeScriptedActivation::OnShipDestroyed(_) => 1,
                NativeScriptedActivation::AfterSeconds(seconds) => {
                    let tick = (f64::from(seconds) / f64::from(dt_seconds)).floor() + 1.0;
                    if !seconds.is_finite() || seconds <= 0.0 ||
                        !tick.is_finite() || tick >= u64::MAX as f64 {
                        return Err("scripted after_seconds must be finite, positive and representable");
                    }
                    tick as u64
                }
            };
            staged.push(NativeScriptedQueueState {
                owner: squad.owner, orders: plan.orders,
                cursor: 0, repeat: plan.repeat, ready_tick,
                on_deployment: is_deferred,
                wait_for_entry: matches!(plan.activation, NativeScriptedActivation::OnArrival),
                required_event, event_armed: required_event.is_none(),
                claimed: matches!(plan.activation, NativeScriptedActivation::Immediate),
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

    /// Called by an authentic native mission trigger after its squad has
    /// actually joined the canonical roster. Unlike a static Level-config
    /// plan, this can arm a scripted sequence after dialogue, reinforcements
    /// or any other Level-owned event. It never invents trigger conditions,
    /// moves ships early, or cancels the command already running.
    ///
    /// The owner is resolved from a current SquadLifetime, not a hotkey or
    /// stale pooled squad object. The first order starts at the next native
    /// completed fixed-tick boundary through advance_source_scripted.
    pub fn enqueue_live_scripted_queue(
        &mut self,
        roster: &NativeSquadRoster,
        plan: NativeScriptedQueuePlan,
    ) -> Result<(), &'static str> {
        if plan.squad_item_id <= 0 || plan.orders.is_empty() ||
            plan.orders.len() > 256 || plan.orders.iter().any(|order| !order.valid()) ||
            !matches!(plan.activation, NativeScriptedActivation::Immediate) {
            return Err("invalid live scripted command plan");
        }
        let owner = roster.source_order().iter().copied()
            .find(|owner| owner.squad_id == plan.squad_item_id)
            .ok_or("live scripted queue requires a registered source squad")?;
        let squad = roster.get(owner).ok_or("missing scripted source squad")?;
        if self.scripted_queues.iter().any(|queue| queue.owner == owner) {
            return Err("a scripted queue already owns this squad");
        }
        if !self.player.commands.contains_key(&owner) ||
            squad.members.iter().any(|id| !self.player.ships.contains_key(id))
        {
            return Err("live scripted command source lacks formation or settings");
        }
        for order in &plan.orders {
            if let Some((target_id, enemy)) = order.target_squad() {
                if !roster.source_order().iter().copied().any(|target_owner|
                    target_owner.level_id == owner.level_id &&
                    target_owner.squad_id == target_id &&
                    (enemy || target_owner != owner) &&
                    roster.get(target_owner).is_some_and(|target|
                        (target.side != squad.side) == enemy)
                ) {
                    return Err("live scripted command needs a real friendly or opposing squad");
                }
            }
        }
        // Insertion is in actual Level.State.Squads order, not numeric ID
        // order or whichever mission trigger happened to fire first.
        let source_order = roster.source_order();
        let destination = source_order.iter().position(|candidate| *candidate == owner)
            .expect("registered source owner");
        let insert_at = self.scripted_queues.partition_point(|queue|
            source_order.iter().position(|candidate| *candidate == queue.owner)
                .is_some_and(|index| index < destination)
        );
        self.scripted_queues.insert(insert_at, NativeScriptedQueueState {
            owner, orders: plan.orders, cursor: 0, repeat: plan.repeat,
            ready_tick: 1, on_deployment: false, wait_for_entry: false,
            required_event: None, event_armed: true, claimed: true,
        });
        Ok(())
    }

    pub(super) fn current_scripted_queue_measurements(
        &self, tick: u64, events: &NativeLevelEventProcessor,
    ) -> bees_gameplay::level_rules::NativeScriptedQueueMeasurements {
        let roster = events.roster();
        let mut counts = self.scripted_counts;
        counts.active_queues = self.scripted_queues.len();
        for state in &self.scripted_queues {
            counts.pending_orders += state.orders.len().saturating_sub(state.cursor);
            if !state.event_armed {
                match state.required_event {
                    Some(NativeMissionEvent::CampaignCue(_)) =>
                        counts.waiting_for_campaign_cue += 1,
                    Some(NativeMissionEvent::ShipDestroyed(_)) =>
                        counts.waiting_for_ship_death += 1,
                    None => unreachable!("ungated queue has no source event"),
                }
            } else if roster.get(state.owner).is_none() {
                counts.waiting_for_deployment += 1;
            } else if tick < state.ready_tick {
                counts.waiting_for_start_time += 1;
            } else if state.wait_for_entry {
                counts.waiting_for_arrival += 1;
            } else if events.commands().current_selection(state.owner)
                .is_some_and(|selection| matches!(selection.origin(),
                    NativeCommandOrigin::Local(identity)
                        if identity.source == NativeLocalCommandSource::Scripted
                )) {
                counts.executing_queues += 1;
            } else {
                counts.ready_queues += 1;
            }
        }
        counts
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
            if !self.scripted_queues[index].event_armed {
                let expected = self.scripted_queues[index].required_event
                    .expect("unarmed queues always wait for a source event");
                if expected.fired(completed_tick, world, events) {
                    // Latch the authentic event before checking deployment:
                    // a ship can die before a reserved squad arrives.
                    self.scripted_queues[index].event_armed = true;
                } else {
                    index += 1;
                    continue;
                }
            }
            if events.roster().get(owner).is_none() {
                if self.scripted_queues[index].on_deployment {
                    // The source squad is still in a pending wave; no
                    // physical ships or command lifetime exist yet.
                    index += 1;
                    continue;
                }
                self.scripted_queues.remove(index);
                self.scripted_counts.queues_cancelled += 1;
                continue;
            }
            if completed_tick < self.scripted_queues[index].ready_tick {
                index += 1;
                continue;
            }
            if self.scripted_queues[index].wait_for_entry {
                // Deployment starts an actual physical entry move. Claim
                // command authority now, but do not replace the movement
                // until the source formation arrives.
                self.scripted_queues[index].claimed = true;
                let squad = events.roster().get(owner)
                    .ok_or(NativeLevelEventError::Commands(GameCommandError::InvalidSquad))?;
                let mut pending = false;
                for &ship_id in &squad.members {
                    if world.ship_body(ship_id).is_none() {
                        return Err(NativeLevelEventError::Commands(
                            GameCommandError::InvalidSquad,
                        ));
                    }
                    pending |= world.ship_has_pending_game_movement(ship_id);
                }
                if pending {
                    index += 1;
                    continue;
                }
                self.scripted_queues[index].wait_for_entry = false;
            }
            // The gate has fired for this registered squad. Source
            // RunCommandQueue takes ownership at the trigger boundary,
            // even if a prior HiveMind command is still running.
            self.scripted_queues[index].claimed = true;
            let script_running = events.commands().current_selection(owner)
                .is_some_and(|selection| matches!(selection.origin(),
                    NativeCommandOrigin::Local(identity)
                    if identity.source == NativeLocalCommandSource::Scripted
                ));
            if script_running {
                // Our previous order still owns this command's timer.
                index += 1;
                continue;
            }
            let state = &self.scripted_queues[index];
            if state.cursor == state.orders.len() {
                if !state.repeat {
                    self.scripted_queues.remove(index);
                    self.scripted_counts.queues_completed += 1;
                    continue;
                }
                self.scripted_queues[index].cursor = 0;
                self.scripted_counts.queues_repeated += 1;
            }
            let order = self.scripted_queues[index].orders[
                self.scripted_queues[index].cursor
            ].clone();
            let source = *self.player.commands.get(&owner)
                .ok_or(NativeLevelEventError::Commands(GameCommandError::MissingCombatSnapshot))?;
            // Every prepared target is re-resolved against the current
            // roster lifetime. A destroyed squad cannot be replaced by an
            // unrelated new squad with the same hotkey or source item ID.
            let linked_target = if let Some((item_id, enemy_side)) = order.target_squad() {
                let own_side = events.roster().get(owner)
                    .ok_or(NativeLevelEventError::Commands(GameCommandError::InvalidSquad))?
                    .side;
                let target = events.roster().source_order().iter().copied()
                    .find(|candidate| candidate.squad_id == item_id &&
                        candidate.level_id == owner.level_id &&
                        (enemy_side || *candidate != owner) &&
                        events.roster().get(*candidate).is_some_and(|entry|
                            (entry.side != own_side) == enemy_side &&
                            !entry.members.is_empty()));
                let Some(target) = target else {
                    // A prepared Guard or enemy order whose actual target
                    // retired completes without inventing a substitute.
                    self.scripted_queues[index].cursor += 1;
                    self.scripted_counts.orders_skipped_target_gone += 1;
                    index += 1;
                    continue;
                };
                Some(target)
            } else { None };
            let enemy = if matches!(order,
                NativeScriptedOrder::Aggressive { .. } |
                NativeScriptedOrder::TargetEnemy { .. }
            ) {
                let target = linked_target.expect("validated enemy target");
                let enemy = self.live_enemy(world, events.roster(), owner, target)
                    .map_err(NativeLevelEventError::Commands)?;
                self.live_aggressive_snapshot(
                    world, events.roster(), owner, target, false,
                ).map_err(NativeLevelEventError::Commands)?;
                Some(enemy)
            } else { None };
            let squad = events.roster().get(owner)
                .ok_or(NativeLevelEventError::Commands(GameCommandError::InvalidSquad))?;
            let formation = SquadFormationOptions {
                map_bounds: self.bounds, has_obstacles: self.player.has_obstacles,
                can_override_bounds: false,
            };
            let scouting = if let NativeScriptedOrder::Scouting {
                max_sight, human_side,
            } = order {
                Some(NativeScoutingContext { max_sight, human_side })
            } else { None };
            let order_view = native_squad_order_from_catalog(
                world, squad, &self.ship_catalog, formation, enemy, scouting,
                |id| self.player.ships.get(&id).map(|source| source.offset),
                |id| self.player.ships.get(&id).map(|source| source.clearance),
                &mut self.scripted_members,
            ).map_err(NativeLevelEventError::Commands)?;
            // Refresh the same authoritative clearance used by normal
            // player/AI commands, once for all scripted activations on this
            // tick. Empty native arenas retain their already-initialized
            // static grid; a present static collider may have moved/died.
            if started == 0 {
                let has_static = world.static_navigation_obstacles().next().is_some();
                let rebuild_static = has_static ||
                    self.navigation.static_obstacles_present() != Some(false);
                world.refresh_world_navigation(
                    &mut self.navigation, rebuild_static, self.activate_collision_asteroids,
                ).map_err(|error| NativeLevelEventError::Commands(
                    GameCommandError::World(error),
                ))?;
            }
            // Guard's explicit target must be located in the current
            // Level.State.Squads order with its real command/guard relation,
            // exactly as the ordinary player/HiveMind Guard implementation.
            // Non-Guard scripted commands skip this projection entirely.
            self.scripted_friendlies.clear();
            if matches!(order,
                NativeScriptedOrder::Guard { .. } |
                NativeScriptedOrder::ClosestFriendly
            ) {
                collect_live_friendlies_from_catalog(
                    world, events.roster(), &self.ship_catalog,
                    events.roster().source_order(),
                    |candidate| Ok(events.commands()
                        .current_kind_and_guarded_target(candidate)),
                    &mut self.scripted_friendlies,
                ).map_err(NativeLevelEventError::Commands)?;
            }
            let result = events.activate_local_command(
                completed_tick, world, &self.navigation, random, order_view,
                &self.scripted_friendlies,
                source.settings, NativeLocalCommandSource::Scripted,
                order.action(linked_target), source.shooting, None,
            )?;
            match result {
                Activation::Applied { .. } => {
                    self.scripted_queues[index].cursor += 1;
                    self.scripted_counts.commands_started += 1;
                    started += 1;
                }
                Activation::Unavailable(_) => {
                    // The actual mining resource, warp gate, squad or
                    // other source target can retire between queue setup
                    // and execution. Skip without fabricating a command
                    // lifetime or failing an otherwise healthy episode.
                    self.scripted_queues[index].cursor += 1;
                    self.scripted_counts.orders_skipped_unavailable += 1;
                }
                _ => return Err(NativeLevelEventError::Commands(
                    GameCommandError::MissingCombatSnapshot,
                )),
            }
            index += 1;
        }
        Ok(started)
    }
}
