//! One source-ordered combat-event handoff for a native Level.
//!
//! Call once after the shared CombatWorld's fixed step, before its event
//! buffer is cleared by the next step. This is NOT a second combat engine,
//! scheduler, reward model or a substitute for the Unity/MA-POCA adapter.
//! All mutable scratch is reused per Level, never process-global.

use bees_hivemind::session::{HiveMindSession, SquadLifetime};
use bees_sim::combat_world::{CombatEvent, CombatWorld};
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_sim::simulation::RandomStream;
use bees_sim::rl_shaping::{DamageRewardMetadata, RewardEmission};
use bees_sim::rl_discovery::DiscoveryKind;

use crate::commands::{
    CommandFinishCause, CommandMeasurements, FinishedNativeCommand,
    GameCommandError, NativeAggressiveCommandSnapshot,
    NativeCircleCommandSnapshot, NativeFireBargeTimerSnapshot,
    NativeFriendlySquad, NativeSquadCommandDirector, NativeSwipeCommandSnapshot,
};
use bees_hivemind::dispatch::ResolvedHiveMindCommand;
use crate::episode::{
    NativeEpisodeFinal, NativeEpisodeRewardCoordinator,
    NativeEpisodeRewardError, NativeEpisodeRewardMeasurements,
};
use crate::group_rewards::{
    GroupRewardError, GroupRewardEvent, GroupRewardMeasurements,
    NativeGroupRewardDelivery,
};
use crate::squads::{
    NativeSquadRoster, SquadEventBridgeError, SquadRosterChange,
    SquadRosterError, SquadRosterMeasurements,
};

#[derive(Debug)]
pub enum NativeLevelEventError {
    MissingOrRepeatedTick { expected: u64, got: u64 },
    Roster(SquadEventBridgeError),
    Rewards(NativeEpisodeRewardError),
    Commands(GameCommandError),
    Group(GroupRewardError),
    InvalidGroupConfiguration,
    InvalidRetirementAcknowledgement,
    InvalidOutputAcknowledgement,
    OutputBackpressure { pending: usize, new_events: usize },
    FailedLevel,
}

pub struct NativeLevelTick<'a> {
    /// Processed in exact CombatWorld event order.
    pub squad_changes: &'a [SquadRosterChange],
    /// The authoritative game must settle real TSV and unmatched
    /// OutcomeIds; these command reservations are NOT silently dropped.
    pub finished_commands: &'a [FinishedNativeCommand],
    /// Pending instructions for the ONE connection-owned HiveMindSession.
    /// May include earlier ticks not yet acknowledged by the transport.
    pub pending_session_retirements: &'a [SquadLifetime],
    /// Source-order bounded shaping plus separate uncapped economics.
    /// Reward delivery to team/agent groups belongs to the host adapter.
    pub reward_emissions: &'a [RewardEmission],
    /// Optional source MA-POCA wire commands, emitted once per side.
    /// EloMarker is transport-only and must be stripped before PPO.
    pub group_events: Option<&'a [GroupRewardEvent]>,
    pub finalized: Option<NativeEpisodeFinal>,
}

/// Sparse, lossless notifications for a multi-step rollout. The event
/// contains the EXACT completed fixed tick on which it occurred.
#[derive(Clone, Debug)]
pub struct NativeLevelNotification {
    pub tick: u64,
    pub kind: NativeLevelNotificationKind,
}

#[derive(Clone, Debug)]
pub enum NativeLevelNotificationKind {
    SquadChange(SquadRosterChange),
    /// Sparse, source-authored capability outcomes for the native game's
    /// presentation/command adapter, kept across rollout windows until
    /// acknowledged. The Level never re-simulates these physical actions.
    Gameplay(CombatEvent),
    FinishedCommand(FinishedNativeCommand),
    /// A failed world has no trustworthy final TSV. Both command/shooting
    /// OutcomeIds must be explicitly discarded or reconciled, never credited.
    AbortedCommand {
        owner: SquadLifetime,
        selection: ResolvedHiveMindCommand,
    },
    /// Only emitted when group delivery is not configured. Never also
    /// apply this raw reward to a group-enabled PPO trajectory.
    Reward(RewardEmission),
    /// A ready-to-send group command, including terminal/ELO markers.
    GroupReward(GroupRewardEvent),
    EpisodeFinal(NativeEpisodeFinal),
    /// An unfinished, canceled or irrecoverably failed episode. The
    /// transport/learner must discard its partial PPO trajectory and
    /// preserve earlier reward records only as diagnostics.
    EpisodeAborted,
}

/// Prevent unbounded RAM growth when a host neglects the output queue.
/// At 1M sparse events this is far beyond a normal rollout window, but
/// still fails the episode before any events are silently dropped.
const MAX_PENDING_NOTIFICATIONS: usize = 1_048_576;

/// Preserve only events with a real Level-side capability consequence.
/// Projectile/impact/ship events are already consumed by the appropriate
/// combat, squad, and reward paths; copying them all here would make
/// high-throughput RL rollouts allocate on every busy physics tick.
fn requires_level_capability_delivery(event: &CombatEvent) -> bool {
    matches!(event,
        CombatEvent::CapabilityMined { .. } |
        CombatEvent::MiningAsteroidDepleted { .. } |
        CombatEvent::CapabilityHealed { .. } |
        CombatEvent::ScoutBeaconDropped { .. } |
        CombatEvent::ShipWarped { .. }
    )
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeLevelEventMeasurements {
    pub squads: SquadRosterMeasurements,
    pub commands: CommandMeasurements,
    pub rewards: NativeEpisodeRewardMeasurements,
    pub group_delivery: Option<GroupRewardMeasurements>,
    pub ticks_integrated: u64,
    pub pending_session_retirements: usize,
    pub pending_notifications: usize,
    pub peak_pending_notifications: usize,
}

/// Owns the authoritative native Level's squad/command/episode state.
    /// HiveMindSession belongs to the WebSocket transport and may serve
    /// several Levels; this processor only publishes retirement messages.
/// It does not own a second CombatWorld: the caller passes the same world
/// used by graphics and the Simulation fixed-step rules.
pub struct NativeLevelEventProcessor {
    roster: NativeSquadRoster,
    commands: NativeSquadCommandDirector,
    /// Persistent until acknowledged by the connection-owned transport.
    /// Never discard squad retirement obligations at the next fixed tick.
    pending_session_retirements: Vec<SquadLifetime>,
    /// Sparse output outbox survives multiple fixed ticks. No per-tick
    /// arena snapshots and no duplicated per-agent group rewards.
    pending_notifications: Vec<NativeLevelNotification>,
    peak_pending_notifications: usize,
    episode: NativeEpisodeRewardCoordinator,
    group_delivery: Option<NativeGroupRewardDelivery>,
    next_tick: u64,
    last_advanced_command_tick: Option<u64>,
    /// Timed callbacks and their sparse notifications form one delivery
    /// boundary; never publish a phase twice after an acknowledgement.
    last_published_timed_tick: Option<u64>,
    failed: bool,
    failed_commands_drained: bool,
    last_finalized: Option<NativeEpisodeFinal>,
    changes: Vec<SquadRosterChange>,
    finished: Vec<FinishedNativeCommand>,
    /// Reused for timer completions; death-retirement output remains intact.
    timed_finished: Vec<FinishedNativeCommand>,
}

impl NativeLevelEventProcessor {
    pub fn new(episode: NativeEpisodeRewardCoordinator) -> Self {
        Self {
            roster: NativeSquadRoster::new(),
            commands: NativeSquadCommandDirector::new(),
            pending_session_retirements: Vec::new(),
            pending_notifications: Vec::new(),
            peak_pending_notifications: 0,
            episode,
            group_delivery: None,
            next_tick: 1,
            last_advanced_command_tick: None,
            last_published_timed_tick: None,
            failed: false,
            failed_commands_drained: false,
            last_finalized: None,
            changes: Vec::new(),
            finished: Vec::new(),
            timed_finished: Vec::new(),
        }
    }

    /// Setup and live command activation use the same objects, not
    /// independently reconstructed per-tick registries.
    pub fn roster_mut(&mut self) -> &mut NativeSquadRoster {
        &mut self.roster
    }

    pub fn roster(&self) -> &NativeSquadRoster {
        &self.roster
    }

    pub fn has_active_commands(&self) -> bool {
        self.commands.active_count() > 0
    }

    pub fn active_command_count(&self) -> usize {
        self.commands.active_count()
    }

    /// A terminal result is not safely delivered until process_completed_tick
    /// has finished publishing it. A failure after reward computation may
    /// leave episode.result() populated with no durable EpisodeFinal.
    pub fn terminal_was_published(&self) -> bool {
        self.last_finalized.is_some()
    }

    /// Deliberately canceled or failed Level whose incomplete trajectory
    /// has been marked for discard. Its combat state is never stepped again.
    pub fn was_aborted(&self) -> bool {
        self.failed_commands_drained
    }

    /// Even if all squads have died, a started failed episode must be
    /// explicitly aborted so the learner observes EpisodeAborted. The
    /// unstarted, empty world may be replaced without a fake episode.
    pub fn has_unresolved_episode_lifecycle(&self) -> bool {
        !self.terminal_was_published() &&
            !self.failed_commands_drained &&
            (self.next_tick > 1 ||
             self.roster.measurements().squads_registered > 0)
    }

    /// Deliberately stop an unfinished Level without inventing a terminal
    /// result or reward. This uses the same abort transaction as failures,
    /// retiring live squad lifetimes and preserving command reservations
    /// for explicit discard/reconciliation. It also disarms future steps.
    pub(crate) fn cancel_live_level(&mut self)
        -> Result<usize, NativeLevelEventError>
    {
        if self.terminal_was_published() {
            return Err(NativeLevelEventError::FailedLevel);
        }
        let aborted = self.drain_failed_level_reservations()?;
        self.failed = true;
        Ok(aborted)
    }

    /// Called only after the owning World has been marked failed. This
    /// cannot recover partial physics: it preserves server reservations
    /// as explicit abort notifications and queues every still-live squad
    /// lifecycle for connection retirement. A second call is inert.
    pub(crate) fn drain_failed_level_reservations(
        &mut self,
    ) -> Result<usize, NativeLevelEventError> {
        if self.failed_commands_drained {
            return Ok(0);
        }
        let active = self.commands.active_count();
        // An abort must be visible even when no HiveMind command exists:
        // otherwise an empty outbox could be mistaken for a valid rollout.
        Self::reserve_output_records(
            &mut self.pending_notifications, &mut self.failed,
            active.saturating_add(1),
        )?;
        for owner in self.roster.active_lifetimes() {
            self.pending_session_retirements.push(owner);
        }
        for (owner, selection) in self.commands.take_failed_command_reservations() {
            self.pending_notifications.push(NativeLevelNotification {
                tick: self.next_tick.saturating_sub(1),
                kind: NativeLevelNotificationKind::AbortedCommand {
                    owner, selection,
                },
            });
        }
        self.pending_notifications.push(NativeLevelNotification {
            tick: self.next_tick.saturating_sub(1),
            kind: NativeLevelNotificationKind::EpisodeAborted,
        });
        Self::update_output_peak(
            &mut self.peak_pending_notifications,
            self.pending_notifications.len(),
        );
        self.failed_commands_drained = true;
        Ok(active)
    }

    pub fn needs_live_command_friendlies(&self) -> bool {
        self.commands.needs_live_friendlies()
    }

    pub fn needs_live_command_friendlies_on_update(&self, dt: f32) -> bool {
        self.commands.needs_live_friendlies_on_update(dt)
    }

    pub fn commands_mut(&mut self) -> &mut NativeSquadCommandDirector {
        &mut self.commands
    }

    /// All pending retirements, including ones from earlier ticks. The
    /// shared connection must call HiveMindSession::retire_squad for
    /// each, then acknowledge the processed prefix only after success.
    pub fn pending_session_retirements(&self) -> &[SquadLifetime] {
        &self.pending_session_retirements
    }

    pub fn acknowledge_session_retirements(
        &mut self, count: usize,
    ) -> Result<(), NativeLevelEventError> {
        if count > self.pending_session_retirements.len() {
            return Err(NativeLevelEventError::InvalidRetirementAcknowledgement);
        }
        self.pending_session_retirements.drain(..count);
        Ok(())
    }

    /// Called by the ONE connection owner between simulation windows,
    /// after accessing this Level through Simulation::world_mut.
    /// HiveMindSession::retire_squad is synchronous and infallible:
    /// pending late replies remain explicit discard obligations, while
    /// this Level's outbox is cleared only as each retirement is applied.
    /// This does not transmit a server message or settle OutcomeIds.
    pub fn flush_retirements_to_connection(
        &mut self,
        connection: &mut HiveMindSession,
    ) -> usize {
        let count = self.pending_session_retirements.len();
        for squad in self.pending_session_retirements.drain(..) {
            connection.retire_squad(squad);
        }
        count
    }

    /// Read events in order after any number of fixed steps, including
    /// across parallel rollout windows. Transport/PPO consumers must
    /// acknowledge only the prefix they durably accepted.
    pub fn pending_notifications(&self) -> &[NativeLevelNotification] {
        &self.pending_notifications
    }

    pub fn acknowledge_notifications(
        &mut self, count: usize,
    ) -> Result<(), NativeLevelEventError> {
        if count > self.pending_notifications.len() {
            return Err(NativeLevelEventError::InvalidOutputAcknowledgement);
        }
        self.pending_notifications.drain(..count);
        Ok(())
    }

    fn reserve_output_records(
        pending: &mut Vec<NativeLevelNotification>,
        failed: &mut bool,
        count: usize,
    ) -> Result<(), NativeLevelEventError> {
        let Some(total) = pending.len().checked_add(count) else {
            *failed = true;
            return Err(NativeLevelEventError::OutputBackpressure {
                pending: pending.len(), new_events: count,
            });
        };
        if total > MAX_PENDING_NOTIFICATIONS {
            *failed = true;
            return Err(NativeLevelEventError::OutputBackpressure {
                pending: pending.len(), new_events: count,
            });
        }
        pending.reserve(count);
        Ok(())
    }

    fn update_output_peak(peak: &mut usize, size: usize) {
        *peak = (*peak).max(size);
    }

    pub fn episode_mut(&mut self) -> &mut NativeEpisodeRewardCoordinator {
        &mut self.episode
    }

    /// Opt in to native MA-POCA delivery before the first fixed tick.
    /// Graphical non-RL Levels may leave the adapter unconfigured.
    /// Agent participation is registered directly on this group object
    /// as controllers bind/unbind during the episode.
    pub fn configure_group_delivery(
        &mut self,
        delivery: NativeGroupRewardDelivery,
    ) -> Result<(), NativeLevelEventError> {
        if self.next_tick != 1 || self.group_delivery.is_some() ||
            delivery.sides() != self.episode.sides() {
            return Err(NativeLevelEventError::InvalidGroupConfiguration);
        }
        self.group_delivery = Some(delivery);
        Ok(())
    }

    pub fn group_delivery_mut(&mut self) -> Option<&mut NativeGroupRewardDelivery> {
        self.group_delivery.as_mut()
    }

    /// Source-verified HiveMind sighting between fixed ticks. It shares
    /// the tactical shaping budget with combat, but delivery has ONE
    /// authoritative path: pending_notifications(). The host drains
    /// the outbox immediately after this callback for Unity parity.
    /// Returning a second reward here would permit duplicate PPO credit.
    pub fn record_verified_discovery(
        &mut self, side: i32, kind: DiscoveryKind,
        entity_id: i64, authored_value: i32,
    ) -> Result<(), NativeLevelEventError> {
        self.record_verified_discovery_with_freshness(
            side, kind, entity_id, authored_value,
        ).map(|_| ())
    }

    /// One bounded discovery output path for ordinary observations and
    /// Scouting completion. 'First' is independent of the reward amount:
    /// a capped shaping budget cannot suppress a real first sighting.
    fn record_verified_discovery_with_freshness(
        &mut self, side: i32, kind: DiscoveryKind,
        entity_id: i64, authored_value: i32,
    ) -> Result<bool, NativeLevelEventError> {
        if self.is_failed() || self.completed() {
            return Err(NativeLevelEventError::FailedLevel);
        }
        Self::reserve_output_records(
            &mut self.pending_notifications, &mut self.failed, 1,
        )?;
        let (first, emission) = match self.episode
            .record_verified_discovery_with_freshness(
                side, kind, entity_id, authored_value,
            ) {
            Ok(value) => value,
            Err(error) => {
                self.failed = true;
                return Err(NativeLevelEventError::Rewards(error));
            }
        };
        self.queue_immediate_emission(emission)?;
        Ok(first)
    }

    /// Unity HivemindVision gives Scouting its five-second completion
    /// countdown only on the first side-wide enemy-ship sighting. The
    /// caller supplies the actual reward Squad lifetime, which is the
    /// MotherSquad for a Scout-spawned Beacon. The command TSV attribution
    /// still belongs to the Level's own shooting/vision outcome ledger.
    /// Call this instead of record_verified_discovery for that sighting;
    /// submitting both would duplicate its reward-delivery transaction.
    pub fn record_verified_scouting_sighting(
        &mut self, side: i32, observed_ship_id: i64,
        authored_value: i32, reward_squad: SquadLifetime,
    ) -> Result<bool, NativeLevelEventError> {
        let first = self.record_verified_discovery_with_freshness(
            side, DiscoveryKind::EnemyShip,
            observed_ship_id, authored_value,
        )?;
        Ok(first && self.commands.record_first_ship_sighting(
            reward_squad, side,
        ))
    }

    /// Called after the authoritative FleetShip.MineralsMinedThisLevel
    /// update and before the episode consumes combat events this tick.
    /// Only the real persistent value may enter the economic ledger;
    /// no duplicate tactical capability reward is emitted for mining.
    pub fn record_mined_value(
        &mut self, world: &CombatWorld, ship_id: i64, mined_total: i32,
    ) -> Result<(), NativeLevelEventError> {
        if self.is_failed() || self.completed() {
            return Err(NativeLevelEventError::FailedLevel);
        }
        if let Err(error) = self.episode.update_mined_value(
            world, ship_id, mined_total,
        ) {
            self.failed = true;
            return Err(NativeLevelEventError::Rewards(error));
        }
        Ok(())
    }

    /// Only completed Beehive healing or WarpGate returns use this
    /// immediate tactical shaping path. Mining uses record_mined_value
    /// and the separate economic ledger, never duplicate tactical credit.
    /// The TSV value is an authoritative post-action result.
    pub fn record_successful_capability_outcome(
        &mut self, side: i32, tsv_value: i32,
    ) -> Result<(), NativeLevelEventError> {
        if self.is_failed() || self.completed() {
            return Err(NativeLevelEventError::FailedLevel);
        }
        Self::reserve_output_records(
            &mut self.pending_notifications, &mut self.failed, 1,
        )?;
        let emission = match self.episode
            .record_successful_capability_outcome(side, tsv_value)
        {
            Ok(value) => value,
            Err(error) => {
                self.failed = true;
                return Err(NativeLevelEventError::Rewards(error));
            }
        };
        self.queue_immediate_emission(emission)
    }

    /// The only immediate-reward transport entry. Source rewards are
    /// emitted through either configured group delivery OR raw side
    /// reward notifications, never through both.
    fn queue_immediate_emission(
        &mut self, emission: Option<RewardEmission>,
    ) -> Result<(), NativeLevelEventError> {
        let group_event = if let (Some(event), Some(group)) = (
            emission, self.group_delivery.as_mut(),
        ) {
            match group.immediate_event(event) {
                Ok(value) => value,
                Err(error) => {
                    self.failed = true;
                    return Err(NativeLevelEventError::Group(error));
                }
            }
        } else {
            None
        };
        let notification = if let Some(event) = group_event {
            Some(NativeLevelNotificationKind::GroupReward(event))
        } else if self.group_delivery.is_none() {
            emission.map(NativeLevelNotificationKind::Reward)
        } else {
            None
        };
        if let Some(kind) = notification {
            self.pending_notifications.push(NativeLevelNotification {
                tick: self.next_tick.saturating_sub(1),
                kind,
            });
            Self::update_output_peak(
                &mut self.peak_pending_notifications,
                self.pending_notifications.len(),
            );
        }
        Ok(())
    }

    pub fn is_failed(&self) -> bool {
        self.failed || self.roster.is_failed() || self.episode.is_failed()
    }

    pub fn completed(&self) -> bool {
        self.episode.result().is_some()
    }

    /// Canonical episode Bee/Human side IDs for all training adapters.
    pub fn sides(&self) -> [i32; 2] {
        self.episode.sides()
    }

    /// Returns the real alternating self-play team when MA-POCA group
    /// delivery is configured. Standalone graphical inference without
    /// groups must provide its policy route explicitly.
    pub fn policy_team_id(&self, side: i32) -> Option<u8> {
        self.group_delivery.as_ref().and_then(
            |group| group.team_id(side),
        )
    }

    /// The same elapsed/timeout value used for terminal and exploration
    /// observations. No separately maintained training timer.
    pub fn normalized_episode_progress(&self) -> f32 {
        self.episode.normalized_episode_progress()
    }

    /// Immutable view after Simulation::step_world; no per-frame copies.
    /// Must be consumed before the next successful world step.
    pub fn latest_squad_changes(&self) -> &[SquadRosterChange] {
        &self.changes
    }

    pub fn latest_finished_commands(&self) -> &[FinishedNativeCommand] {
        &self.finished
    }

    pub fn latest_reward_emissions(&self) -> &[RewardEmission] {
        self.episode.latest_emissions()
    }

    pub fn latest_finalized_episode(&self) -> Option<NativeEpisodeFinal> {
        self.last_finalized
    }

    /// The multi-world Simulation.step_world returns StepCounts, so
    /// adapters retrieve the current completed tick's group commands
    /// here before allowing the next world step to clear scratch.
    pub fn latest_group_events(&self) -> Option<&[GroupRewardEvent]> {
        self.group_delivery.as_ref().map(
            NativeGroupRewardDelivery::events,
        )
    }

    /// Unity scaled timer callbacks are advanced on every Level tick,
    /// not only when a policy batch requests a new action. The caller
    /// supplies the freshly refreshed Level.State.Squads projection and
    /// the real authored NavigationRuntime. The command director may
    /// issue new waypoint/Guard speed orders, but never simulate physics.
    ///
    /// This must run only after the current combat events were reconciled.
    /// A terminal match does not execute further gameplay commands.
    pub fn advance_timed_commands(
        &mut self,
        completed_tick: u64,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        friendlies: &[NativeFriendlySquad],
        random: &mut RandomStream,
        dt: f32,
    ) -> Result<(), NativeLevelEventError> {
        self.advance_timed_commands_with_combat(
            completed_tick, world, navigation, friendlies, random, dt,
            |_, _, _| Err(GameCommandError::MissingCombatSnapshot),
            |_, _, _, _| Err(GameCommandError::MissingCombatSnapshot),
            |_, _, _, _| Err(GameCommandError::MissingCombatSnapshot),
        )
    }

    /// All movement-attack timers share this same Level-owned roster,
    /// combat world, navigation, and retained delivery outbox.
    pub fn advance_timed_commands_with_combat(
        &mut self,
        completed_tick: u64,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        friendlies: &[NativeFriendlySquad],
        random: &mut RandomStream,
        dt: f32,
        circle_snapshot: impl FnMut(
            SquadLifetime, i64, &CombatWorld,
        ) -> Result<NativeCircleCommandSnapshot, GameCommandError>,
        swipe_snapshot: impl FnMut(
            SquadLifetime, i64, &CombatWorld, bool,
        ) -> Result<NativeSwipeCommandSnapshot, GameCommandError>,
        aggressive_snapshot: impl FnMut(
            SquadLifetime, i64, &CombatWorld, bool,
        ) -> Result<NativeAggressiveCommandSnapshot, GameCommandError>,
    ) -> Result<(), NativeLevelEventError> {
        self.advance_timed_commands_with_bombing(
            completed_tick, world, navigation, friendlies, random, dt,
            circle_snapshot, swipe_snapshot, aggressive_snapshot,
            |_, _, _| Err(GameCommandError::MissingCombatSnapshot),
        )
    }

    /// One Level-owned command update, including authored bomber proximity
    /// projection. Legacy callers remain fail-closed for BombingRun.
    pub fn advance_timed_commands_with_bombing(
        &mut self,
        completed_tick: u64,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        friendlies: &[NativeFriendlySquad],
        random: &mut RandomStream,
        dt: f32,
        circle_snapshot: impl FnMut(
            SquadLifetime, i64, &CombatWorld,
        ) -> Result<NativeCircleCommandSnapshot, GameCommandError>,
        swipe_snapshot: impl FnMut(
            SquadLifetime, i64, &CombatWorld, bool,
        ) -> Result<NativeSwipeCommandSnapshot, GameCommandError>,
        aggressive_snapshot: impl FnMut(
            SquadLifetime, i64, &CombatWorld, bool,
        ) -> Result<NativeAggressiveCommandSnapshot, GameCommandError>,
        bombing_snapshot: impl FnMut(
            SquadLifetime, i64, &CombatWorld,
        ) -> Result<NativeFireBargeTimerSnapshot, GameCommandError>,
    ) -> Result<(), NativeLevelEventError> {
        if self.is_failed() {
            return Err(NativeLevelEventError::FailedLevel);
        }
        // This method is a separate Level callback boundary; enforce
        // once-per-step ordering here too, not only in the caller's
        // Simulation/GameRules integration.
        if completed_tick.checked_add(1) != Some(self.next_tick) ||
            self.last_advanced_command_tick == Some(completed_tick) {
            self.failed = true;
            return Err(NativeLevelEventError::MissingOrRepeatedTick {
                expected: self.next_tick.saturating_sub(1),
                got: completed_tick,
            });
        }
        if self.completed() || self.commands.active_count() == 0 {
            return Ok(());
        }
        if let Err(error) = self.commands.advance_into_with_bombing(
            world, navigation, Some(&self.roster), friendlies, random, dt,
            circle_snapshot, swipe_snapshot, aggressive_snapshot,
            bombing_snapshot, &mut self.timed_finished,
        ) {
            self.failed = true;
            return Err(NativeLevelEventError::Commands(error));
        }
        self.last_advanced_command_tick = Some(completed_tick);
        Ok(())
    }

    /// Publish one post-combat timed-command phase after the Level has
    /// durably credited any mined minerals. Only newly generated sparse
    /// gameplay events are forwarded, followed by command completions.
    /// The bounded outbox retains them across rollout windows until
    /// acknowledged. Reject duplicate publication of the same tick.
    pub fn publish_timed_command_events(
        &mut self, completed_tick: u64, world: &CombatWorld,
        events: &[CombatEvent],
        mut allocate_minion: impl FnMut(SquadLifetime, i64)
            -> Result<SquadLifetime, SquadRosterError>,
    ) -> Result<(), NativeLevelEventError> {
        if self.is_failed() || self.completed() {
            return Err(NativeLevelEventError::FailedLevel);
        }
        if completed_tick.checked_add(1) != Some(self.next_tick) ||
            self.last_advanced_command_tick != Some(completed_tick) ||
            self.last_published_timed_tick == Some(completed_tick) {
            self.failed = true;
            return Err(NativeLevelEventError::MissingOrRepeatedTick {
                expected: self.next_tick.saturating_sub(1),
                got: completed_tick,
            });
        }
        let capability_count = events.iter()
            .filter(|event| requires_level_capability_delivery(event))
            .count();
        let minion_count = events.iter().filter(|event|
            matches!(event, CombatEvent::ScoutBeaconDropped { .. })
        ).count();
        Self::reserve_output_records(
            &mut self.pending_notifications, &mut self.failed,
            capability_count.saturating_add(minion_count)
                .saturating_add(self.timed_finished.len()),
        )?;
        // Source Mining.Mine mutates the actual rock and fleet before
        // SetFinalize publishes its command outcome. Preserve that
        // relative order even across large asynchronous rollout windows.
        for event in events {
            if let CombatEvent::ScoutBeaconDropped {
                scout_id, beacon_id, ..
            } = event {
                // Source Scout.DropBeacon creates the minion Squad in
                // the same callback, before the next fixed tick. Without
                // immediate registration the next roster coverage check
                // would fail after CombatWorld clears the event buffer.
                let change = (|| -> Result<SquadRosterChange, SquadRosterError> {
                    let mother = self.roster.owning_squad(*scout_id)
                        .ok_or(SquadRosterError::MissingScout(*scout_id))?;
                    let owner = allocate_minion(mother, *beacon_id)?;
                    self.roster.register_scout_beacon(world, event, owner)
                })();
                let change = match change {
                    Ok(value) => value,
                    Err(err) => {
                        self.failed = true;
                        return Err(NativeLevelEventError::Roster(
                            SquadEventBridgeError::Roster(err),
                        ));
                    }
                };
                self.pending_notifications.push(NativeLevelNotification {
                    tick: completed_tick,
                    kind: NativeLevelNotificationKind::SquadChange(change),
                });
                self.changes.push(change);
            }
            if requires_level_capability_delivery(event) {
                self.pending_notifications.push(NativeLevelNotification {
                    tick: completed_tick,
                    kind: NativeLevelNotificationKind::Gameplay(event.clone()),
                });
            }
        }
        for finished in &self.timed_finished {
            self.pending_notifications.push(NativeLevelNotification {
                tick: completed_tick,
                kind: NativeLevelNotificationKind::FinishedCommand(
                    finished.clone(),
                ),
            });
        }
        Self::update_output_peak(
            &mut self.peak_pending_notifications,
            self.pending_notifications.len(),
        );
        self.finished.append(&mut self.timed_finished);
        self.last_published_timed_tick = Some(completed_tick);
        Ok(())
    }

    pub fn measurements(&self) -> NativeLevelEventMeasurements {
        NativeLevelEventMeasurements {
            squads: self.roster.measurements(),
            commands: self.commands.measurements(),
            rewards: self.episode.measurements(),
            group_delivery: self.group_delivery.as_ref().map(
                NativeGroupRewardDelivery::measurements,
            ),
            ticks_integrated: self.next_tick - 1,
            pending_session_retirements: self.pending_session_retirements.len(),
            pending_notifications: self.pending_notifications.len(),
            peak_pending_notifications: self.peak_pending_notifications,
        }
    }

    /// `completed_tick` must equal the owning world's completed fixed
    /// step count: 1, 2, 3... per episode. This rejects replay of the
    /// same events, lost steps and accidental cross-episode reuse.
    ///
    /// The minion allocator supplies a real Level-owned SquadLifetime,
    /// rather than a training-only guessed squad identity. Both metadata
    /// providers read the Level's actual persistent FleetShip values.
    /// A callback failure invalidates this Level's remaining episode,
    /// because a partial combat-event batch cannot be replayed safely.
    pub fn process_completed_tick(
        &mut self,
        world: &mut CombatWorld,
        completed_tick: u64,
        dt_seconds: f32,
        allocate_minion: impl FnMut(SquadLifetime, i64)
            -> Result<SquadLifetime, SquadRosterError>,
        damage_metadata: impl FnMut(i64) -> Option<DamageRewardMetadata>,
        mined_tsv: impl FnMut(i64) -> Option<i32>,
    ) -> Result<NativeLevelTick<'_>, NativeLevelEventError> {
        if self.is_failed() {
            return Err(NativeLevelEventError::FailedLevel);
        }
        if completed_tick != self.next_tick {
            self.failed = true;
            return Err(NativeLevelEventError::MissingOrRepeatedTick {
                expected: self.next_tick, got: completed_tick,
            });
        }
        // A queued game WarpGate return now became a real ShipWarped
        // followed by ShipDied. Capture FullRetreat's Striker/Drone TSV
        // adjustment BEFORE roster death retirement removes the command.
        // This is the same authoritative event store used by rewards.
        let roster = &self.roster;
        self.commands.record_warp_events(world, |ship_id|
            roster.owning_squad(ship_id));
        // The roster may create/destroy minions and retire completed
        // commands; the episode consumer sees the same authoritative
        // physical world and the source-order immutable event buffer.
        if let Err(error) = self.roster.reconcile_combat_tick(
            world, &mut self.commands,
            allocate_minion, &mut self.changes, &mut self.finished,
        ) {
            self.failed = true;
            return Err(NativeLevelEventError::Roster(error));
        }
        // Preserve retirements across multi-step parallel rollout
        // windows. The shared WebSocket connection may not run until
        // after an entire world window completes. Late replies are
        // rejected by the connection's live-owner validation meanwhile.
        for &change in &self.changes {
            if let SquadRosterChange::SquadRetired { squad } = change {
                self.pending_session_retirements.push(squad);
            }
        }
        let reward = match self.episode.consume_combat_tick(
            world, dt_seconds, &self.changes, damage_metadata, mined_tsv,
        ) {
            Ok(result) => result,
            Err(error) => {
                self.failed = true;
                return Err(NativeLevelEventError::Rewards(error));
            }
        };
        // The group's side/episode membership is registered by the
        // actual policy controllers. This single event boundary delivers
        // immediate combat/economic shaping and terminal rewards without
        // duplicating them for each participating agent.
        let group_events = if let Some(group) = self.group_delivery.as_mut() {
            match group.consume_tick(
                completed_tick, reward.emissions, reward.finalized,
            ) {
                Ok(events) => Some(events),
                Err(error) => {
                    self.failed = true;
                    return Err(NativeLevelEventError::Group(error));
                }
            }
        } else {
            None
        };
        if reward.finalized.is_some() {
            // Unity LevelOver finalizes all surviving HiveMind orders.
            // Elimination/timeout does not necessarily kill every squad:
            // leave real outcomes in the ordered notification outbox and
            // retire every remaining session lifetime before episode reset.
            // Never synthesize a physical SquadRetired or ship death.
            for owner in self.roster.active_lifetimes() {
                self.pending_session_retirements.push(owner);
                match self.commands.retire_squad(world, owner) {
                    Ok(Some(mut command)) => {
                        command.cause = CommandFinishCause::LevelEnded;
                        self.finished.push(command);
                    }
                    Ok(None) => {}
                    Err(error) => {
                        self.failed = true;
                        return Err(NativeLevelEventError::Commands(error));
                    }
                }
            }
        }
        // Copy only sparse, meaningful notifications into a persistent
        // per-Level outbox. Quiet steps allocate and record nothing.
        // Group-enabled runners receive ONLY GroupReward wire events,
        // not duplicated raw side rewards.
        let reward_count = group_events.map_or(
            reward.emissions.len(), |events| events.len(),
        );
        let capability_notifications = world.events().iter()
            .filter(|event| requires_level_capability_delivery(event))
            .count();
        let new_count = self.changes.len()
            .saturating_add(self.finished.len())
            .saturating_add(capability_notifications)
            .saturating_add(reward_count)
            .saturating_add(usize::from(reward.finalized.is_some()));
        // Only the notification outbox is mutated here: the reward and
        // group slices remain borrowed from other independent fields.
        Self::reserve_output_records(
            &mut self.pending_notifications, &mut self.failed, new_count,
        )?;
        for &change in &self.changes {
            self.pending_notifications.push(NativeLevelNotification {
                tick: completed_tick,
                kind: NativeLevelNotificationKind::SquadChange(change),
            });
        }
        for finished in &self.finished {
            self.pending_notifications.push(NativeLevelNotification {
                tick: completed_tick,
                kind: NativeLevelNotificationKind::FinishedCommand(
                    finished.clone(),
                ),
            });
        }
        // Keep source event details for UI effects, resource diagnostics
        // and mining-target cleanup. The owning Level determines which
        // live command needs finalization; no synthetic command is created.
        for event in world.events() {
            if requires_level_capability_delivery(event) {
                self.pending_notifications.push(NativeLevelNotification {
                    tick: completed_tick,
                    kind: NativeLevelNotificationKind::Gameplay((*event).clone()),
                });
            }
        }
        if let Some(group) = group_events {
            for &event in group {
                self.pending_notifications.push(NativeLevelNotification {
                    tick: completed_tick,
                    kind: NativeLevelNotificationKind::GroupReward(event),
                });
            }
        } else {
            for &event in reward.emissions {
                self.pending_notifications.push(NativeLevelNotification {
                    tick: completed_tick,
                    kind: NativeLevelNotificationKind::Reward(event),
                });
            }
        }
        if let Some(finalized) = reward.finalized {
            self.pending_notifications.push(NativeLevelNotification {
                tick: completed_tick,
                kind: NativeLevelNotificationKind::EpisodeFinal(finalized),
            });
        }
        Self::update_output_peak(
            &mut self.peak_pending_notifications,
            self.pending_notifications.len(),
        );
        self.next_tick = self.next_tick.checked_add(1)
            .expect("monotonic native Level tick exhausted");
        self.last_finalized = reward.finalized;
        Ok(NativeLevelTick {
            squad_changes: &self.changes,
            finished_commands: &self.finished,
            pending_session_retirements: &self.pending_session_retirements,
            reward_emissions: reward.emissions,
            group_events,
            finalized: reward.finalized,
        })
    }
}
