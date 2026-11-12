//! One source-ordered combat-event handoff for a native Level.
//!
//! Call once after the shared CombatWorld's fixed step, before its event
//! buffer is cleared by the next step. This is NOT a second combat engine,
//! scheduler, reward model or a substitute for the Unity/MA-POCA adapter.
//! All mutable scratch is reused per Level, never process-global.

use bees_hivemind::session::{HiveMindSession, SquadLifetime, StrategyReceipt};
use bees_hivemind::outcomes::OutcomeBatch;
use bees_sim::combat_world::{CombatEvent, CombatWorld};
use bees_sim::combat::{DamageDelta, HitEvent};
use bees_sim::geometry::Vec2;
use bees_sim::physics::CollisionMatrix;
use bees_sim::navigation_runtime::NavigationRuntime;
use bees_sim::simulation::RandomStream;
use bees_sim::rl_shaping::{DamageRewardMetadata, RewardEmission};
use bees_sim::rl_discovery::DiscoveryKind;

use crate::command_credit::{CommandCreditError, NativeCommandCreditLedger};
use crate::commands::{
    CommandFinishCause, CommandMeasurements, FinishedNativeCommand,
    GameCommandError, NativeAggressiveCommandSnapshot,
    NativeCircleCommandSnapshot, NativeBombingTimerSnapshot,
    NativeChargeTimerSnapshot,
    NativeBombWeapon, NativeBombingActivation, NativeEnemyTargetStats,
    NativeFriendlySquad, NativeSquadOrder, NativeSquadCommandDirector,
    NativeSwipeCommandSnapshot, HiveMindCommandSettings,
    StrategyReceiptAction, StrategyReceiptFailure,
    Activation, NativeCommandSelection, NativeLocalCommandIdentity,
    NativeLocalCommandSource, NativeLocalCommandAction,
};
use bees_hivemind::protocol::StrategyIdentity;
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
    Credit(CommandCreditError),
    Group(GroupRewardError),
    InvalidGroupConfiguration,
    InvalidRetirementAcknowledgement,
    InvalidOutputAcknowledgement,
    InvalidVisionCredit,
    InvalidTargetingSelection,
    InconsistentCombatAttribution,
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

pub struct NativeLocalBombingTargets<'a> {
    pub enemy_owner: SquadLifetime,
    pub weapons: &'a [NativeBombWeapon],
    pub enemies: &'a [NativeEnemyTargetStats],
    pub is_carrier_squad: bool,
}

/// Source-side strategic attribution captured at the instant damage occurred.
/// A delayed projectile keeps its launching command's OutcomeId. Damage to a
/// defender is charged to the command active at impact, *before* death can
/// retire the squad; neither mapping is reconstructed by the notification sink.
#[derive(Clone, Copy, Debug)]
pub struct NativeCombatHit {
    pub hit: HitEvent,
    pub attacker_command_outcome_id: Option<i64>,
    pub defender_command_outcome_id: Option<i64>,
    /// Ship.LogHitStats separately rewards the killer of an exploding Fire
    /// Barge for same-side blast damage. This does not replace the Barge's
    /// own friendly-fire penalty or create another physical hit.
    pub fire_barge_killer_credit: Option<NativeFireBargeKillerCredit>,
}

#[derive(Clone, Copy, Debug)]
pub struct NativeFireBargeKillerCredit {
    pub killer_ship_id: i64,
    pub command_outcome_id: Option<i64>,
    pub tsv_delta: i32,
}

/// Strategic command credit, separate from bounded PPO discovery shaping.
/// HivemindVision.OnTriggerEnter2D / ConfigData.Gameplay constants.
#[derive(Clone, Debug)]
pub struct NativeVisionCommandCredit {
    pub observer_id: i64,
    pub target_id: i64,
    pub target_tsv: i32,
    pub owner: SquadLifetime,
    pub selection: NativeCommandSelection,
    pub tsv_delta: i32,
}

#[derive(Clone, Copy, Debug)]
struct FireBargeKiller {
    ship_id: i64,
    command_at_kill: Option<i64>,
}

#[derive(Clone, Copy, Debug)]
struct DamageCommandOwners {
    attacker: Option<i64>,
    defender: Option<i64>,
    fire_barge_killer: Option<NativeFireBargeKillerCredit>,
}

#[derive(Clone, Debug)]
pub enum NativeLevelNotificationKind {
    CampaignDialogue(crate::campaign_dialogue::CampaignCue),
    ReinforcementsArrived { ships: usize, squads: usize },
    TargetingSelected { owner: SquadLifetime, choice: crate::squad_targeting::NativeInstalledTargeting },
    SquadChange(SquadRosterChange),
    /// Sparse, source-authored capability outcomes for the native game's
    /// presentation/command adapter, kept across rollout windows until
    /// acknowledged. The Level never re-simulates these physical actions.
    Gameplay(CombatEvent),
    /// Immutable source-attributed physical impact, delivered before any
    /// same-tick command completion. The original OutcomeId remains valid
    /// after a command is superseded; a consumer must not finalize its
    /// StoreCommands reservation until delayed projectile credit is safe.
    CombatHit(NativeCombatHit),
    VisionCommandCredit(NativeVisionCommandCredit),
    /// Health/TSV loss with no validated attacker, including collisions
    /// and stale pooled shooter identities. Never give it attacker TSV.
    UnattributedDamage {
        target_id: i64, delta: DamageDelta,
        defender_command_outcome_id: Option<i64>,
    },
    FinishedCommand(FinishedNativeCommand),
    /// Final, source-accrued TSV values from one completed/aborted episode.
    /// This is a PREPARED OutcomeBatch; durable StoreCommands request hash,
    /// retry and server commit still belong to the connection.
    CommandOutcomesReady(OutcomeBatch),
    /// A failed world has no trustworthy final TSV. HiveMind origins must
    /// discard/reconcile their reservations; local origins have none.
    AbortedCommand {
        owner: SquadLifetime,
        selection: crate::commands::NativeCommandSelection,
    },
    /// Only emitted when group delivery is not configured. Never also
    /// apply this raw reward to a group-enabled PPO trajectory.
    Reward(RewardEmission),
    /// A ready-to-send group command, including terminal/ELO markers.
    GroupReward(GroupRewardEvent),
    PolicyControllerRetired { ship_id: i64, binding_revision: u64 },
    EpisodeFinal(NativeEpisodeFinal),
    /// An unfinished, canceled or irrecoverably failed episode. The
    /// transport/learner must discard its partial PPO trajectory and
    /// preserve earlier reward records only as diagnostics.
    EpisodeAborted,
}

fn policy_retirement(event: &CombatEvent) -> Option<NativeLevelNotificationKind> {
    if let CombatEvent::ShipDied {
        victim_id, policy_binding_revision: Some(binding_revision), ..
    } = *event {
        return Some(NativeLevelNotificationKind::PolicyControllerRetired {
            ship_id: victim_id, binding_revision,
        });
    }
    None
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
        CombatEvent::ShipWarped { .. } |
        CombatEvent::ShipExtracted { .. }
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
/// Per-episode admissions observed at the same one-way Level event
/// publication boundary as the durable ReinforcementsArrived notification.
/// Unlike the sparse notification outbox, these counters survive ACKs.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize)]
pub struct NativeReinforcementArrivals {
    pub waves: u64,
    pub squads: u64,
    pub ships: u64,
}

impl NativeReinforcementArrivals {
    pub fn merge(&mut self, other: Self) {
        self.waves += other.waves;
        self.squads += other.squads;
        self.ships += other.ships;
    }
}

pub struct NativeLevelEventProcessor {
    roster: NativeSquadRoster,
    commands: NativeSquadCommandDirector,
    /// Persistent until acknowledged by the connection-owned transport.
    /// Never discard squad retirement obligations at the next fixed tick.
    pending_session_retirements: Vec<SquadLifetime>,
    /// Sparse output outbox survives multiple fixed ticks. No per-tick
    /// arena snapshots and no duplicated per-agent group rewards.
    pending_notifications: Vec<NativeLevelNotification>,
    /// Stable zero-based offset of the first pending event in this episode.
    /// A successful prefix acknowledgement advances this cursor; a rejected
    /// delivery never changes the identities of undelivered notifications.
    acknowledged_notification_count: u64,
    peak_pending_notifications: usize,
    reinforcement_arrivals: NativeReinforcementArrivals,
    /// Last canonical admission tick, independent of notification ACKs.
    /// The native UI can show transient feedback without storing gameplay
    /// state or replaying the durable outbox.
    last_reinforcement_tick: Option<u64>,
    episode: NativeEpisodeRewardCoordinator,
    group_delivery: Option<NativeGroupRewardDelivery>,
    next_tick: u64,
    next_local_command_sequence: u64,
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
    /// Core-physics damage owners are snapshotted before death retirement.
    /// Reused every tick, never retained as a parallel command ledger.
    core_damage_owners: Vec<DamageCommandOwners>,
    /// Immutable death-time credit, retained while the dead ship's blast can
    /// still cause damage. Physical ownership and fleet credit stay in sim.
    fire_barge_killers: std::collections::HashMap<i64, FireBargeKiller>,
    credit: NativeCommandCreditLedger,
}

impl NativeLevelEventProcessor {
    pub fn new(episode: NativeEpisodeRewardCoordinator) -> Self {
        Self {
            roster: NativeSquadRoster::new(),
            commands: NativeSquadCommandDirector::new(),
            pending_session_retirements: Vec::new(),
            pending_notifications: Vec::new(),
            acknowledged_notification_count: 0,
            peak_pending_notifications: 0,
            reinforcement_arrivals: NativeReinforcementArrivals::default(),
            last_reinforcement_tick: None,
            episode,
            group_delivery: None,
            next_tick: 1,
            next_local_command_sequence: 0,
            last_advanced_command_tick: None,
            last_published_timed_tick: None,
            failed: false,
            failed_commands_drained: false,
            last_finalized: None,
            changes: Vec::new(),
            finished: Vec::new(),
            timed_finished: Vec::new(),
            core_damage_owners: Vec::new(),
            fire_barge_killers: std::collections::HashMap::new(),
            credit: NativeCommandCreditLedger::new(),
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

    fn active_server_command_for_ship(&self, ship_id: i64) -> Option<i64> {
        self.roster.owning_squad(ship_id).and_then(|owner| {
            self.commands.current_server_outcome(owner).or_else(|| {
                // Timed command callbacks can finish an order in the same
                // callback that dealt damage. Its completion has moved to
                // timed_finished but its impact still belongs to that order.
                self.timed_finished.iter().find(|finished| finished.owner == owner)
                    .and_then(|finished| finished.selection.server_outcome())
            })
        })
    }

    fn observe_fire_barge_death(&mut self, world: &CombatWorld, event: &CombatEvent) {
        if let CombatEvent::ShipDied { victim_id, killer_id, .. } = event {
            if *killer_id > 0 && world.ship(*victim_id).is_some_and(|ship| ship.ship_type == 10) {
                // FireBarge.Kill snapshots the killer's ACTIVE HiveMind
                // command, not the command that launched the lethal shot.
                self.fire_barge_killers.insert(*victim_id, FireBargeKiller {
                    ship_id: *killer_id,
                    command_at_kill: self.active_server_command_for_ship(*killer_id),
                });
            }
        }
    }

    fn damage_command_owners(&self, event: &CombatEvent) -> Option<DamageCommandOwners> {
        match event {
            CombatEvent::Hit(hit) => Some(DamageCommandOwners {
                attacker: if hit.command_outcome_id > 0 {
                    Some(hit.command_outcome_id)
                } else {
                    // Unity's synchronous/no-projectile credit falls back to
                    // the attacker command active at the physical impact.
                    self.active_server_command_for_ship(hit.attacker_id)
                },
                defender: self.active_server_command_for_ship(hit.target_id),
                fire_barge_killer: if hit.attacker_side == hit.target_side {
                    self.fire_barge_killers.get(&hit.attacker_id).map(|killer| NativeFireBargeKillerCredit {
                        killer_ship_id: killer.ship_id,
                        // A zero source KillerCommandOutcomeId falls back to
                        // the killer's command active at the later impact.
                        command_outcome_id: killer.command_at_kill.or_else(||
                            self.active_server_command_for_ship(killer.ship_id)),
                        tsv_delta: hit.delta.tsv_loss,
                    })
                } else { None },
            }),
            CombatEvent::UnattributedDamage { target_id, .. } =>
                Some(DamageCommandOwners {
                    attacker: None,
                    defender: self.active_server_command_for_ship(*target_id),
                    fire_barge_killer: None,
                }),
            _ => None,
        }
    }

    pub fn has_active_commands(&self) -> bool {
        self.commands.active_count() > 0
    }

    pub fn active_command_count(&self) -> usize {
        self.commands.active_count()
    }

    /// Source mining enrollment belongs to the one command director.
    /// Only its real hull-contact transitions can populate this view.
    pub fn collect_mining_squad_counts(
        &self, output: &mut std::collections::HashMap<i64, u32>,
    ) {
        self.commands.collect_mining_squad_counts(output);
    }

    pub fn needs_bomber_contacts(&self, world: &CombatWorld) -> bool {
        self.commands.needs_bomber_contacts(world)
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
        let first_new = self.pending_notifications.len();
        for owner in self.roster.active_lifetimes() {
            self.pending_session_retirements.push(owner);
        }
        for (owner, selection) in self.commands.take_failed_commands() {
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
        Self::account_new_notifications(
            &mut self.pending_notifications, &mut self.credit,
            &mut self.failed, &mut self.peak_pending_notifications,
            first_new, self.next_tick.saturating_sub(1),
        )?;
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

    pub fn commands(&self) -> &NativeSquadCommandDirector { &self.commands }

    /// Source FinalizeUserCommand, retaining the real outcome/reservation
    /// payload in the same durable outbox as timed command completion.
    pub fn finalize_local_command(
        &mut self, tick: u64, world: &mut CombatWorld, owner: SquadLifetime,
    ) -> Result<(), NativeLevelEventError> {
        if self.is_failed() || self.completed() {
            return Err(NativeLevelEventError::FailedLevel);
        }
        Self::reserve_output_records(
            &mut self.pending_notifications, &mut self.failed, 1,
        )?;
        let first_new = self.pending_notifications.len();
        let finished = self.commands.supersede_for_local_command(world, owner)
            .map_err(|error| {
                self.failed = true;
                NativeLevelEventError::Commands(error)
            })?;
        if let Some(finished) = finished {
            self.pending_notifications.push(NativeLevelNotification {
                tick, kind: NativeLevelNotificationKind::FinishedCommand(finished),
            });
            Self::update_output_peak(
                &mut self.peak_pending_notifications, self.pending_notifications.len(),
            );
        }
        Self::account_new_notifications(
            &mut self.pending_notifications, &mut self.credit,
            &mut self.failed, &mut self.peak_pending_notifications,
            first_new, tick,
        )?;
        Ok(())
    }

    /// Unity Squad.StopChasing: Hold cancels only an active Aggressive
    /// command. Other commands remain in place, and Hold never installs
    /// a second command or changes physical movement state directly.
    pub fn stop_player_chasing(
        &mut self, tick: u64, world: &mut CombatWorld, owner: SquadLifetime,
    ) -> Result<bool, NativeLevelEventError> {
        if self.is_failed() || self.completed() {
            return Err(NativeLevelEventError::FailedLevel);
        }
        if !self.commands.is_chasing_command(owner) {
            return Ok(false);
        }
        self.finalize_local_command(tick, world, owner)?;
        Ok(true)
    }

    pub(crate) fn fail_player_input(&mut self) { self.failed = true; }

    /// Local commands acquire a Level-owned identity, never a server
    /// reservation. Superseded outcomes join the one durable outbox before
    /// returning; callers must not separately settle them a second time.
    pub fn activate_local_command(
        &mut self, tick: u64, world: &mut CombatWorld,
        navigation: &NavigationRuntime, random: &mut RandomStream,
        squad: NativeSquadOrder<'_>, friendlies: &[NativeFriendlySquad],
        settings: HiveMindCommandSettings, source: NativeLocalCommandSource,
        action: NativeLocalCommandAction<'_>, shooting: StrategyIdentity,
        bombing: Option<NativeLocalBombingTargets<'_>>,
    ) -> Result<Activation, NativeLevelEventError> {
        if self.is_failed() || self.completed() || tick != self.next_tick.saturating_sub(1) {
            return Err(NativeLevelEventError::FailedLevel);
        }
        if !self.roster.get(squad.owner).is_some_and(|entry|
            entry.members.len() == squad.members.len() &&
            entry.members.iter().zip(squad.members).all(|(&id, member)| id == member.ship_id)) {
            return Err(NativeLevelEventError::Commands(GameCommandError::InvalidSquad));
        }
        let selection = NativeCommandSelection::local(NativeLocalCommandIdentity {
            source, sequence: self.next_local_command_sequence,
        }, action.kind(), shooting).map_err(NativeLevelEventError::Commands)?;
        let next_sequence = self.next_local_command_sequence.checked_add(1)
            .ok_or(NativeLevelEventError::Commands(GameCommandError::LocalCommandIdExhausted))?;
        self.next_local_command_sequence = next_sequence;
        // Source UI Hold ends chasing; it is not a request to install the
        // AI/scripted Hold command. Unsupported input has no side effects.
        if matches!(action, NativeLocalCommandAction::Hold) && source == NativeLocalCommandSource::Player {
            return Ok(Activation::Unsupported(selection));
        }
        // MakeUserCommand finalizes first, including when the chosen target
        // has vanished and Execute cannot install a replacement. Unlike AI
        // strategy admission this must not retain the previous command.
        self.finalize_local_command(tick, world, squad.owner)?;
        if !self.pending_session_retirements.contains(&squad.owner) {
            self.pending_session_retirements.push(squad.owner);
        }
        let bombing_input = bombing.map(|input| NativeBombingActivation {
            roster: &self.roster, enemy_owner: input.enemy_owner,
            is_carrier_squad: input.is_carrier_squad,
            weapons: input.weapons, enemies: input.enemies,
        });
        self.commands.activate_local_selection(
            world, navigation, random, squad, friendlies, settings, selection, action, bombing_input,
        ).map_err(|error| {
            self.failed = true;
            NativeLevelEventError::Commands(error)
        })
    }

    /// Preserve an old command's finalization inside the Level outbox
    /// before a new accepted HiveMind selection replaces it. The raw
    /// command director returns superseded as a value, but that value must
    /// not disappear at the asynchronous server-reply boundary.
    fn preflight_strategy_supersession(
        &mut self, receipt: &StrategyReceipt,
    ) -> Result<(), StrategyReceiptFailure> {
        if let StrategyReceipt::Accepted { owner, selection, .. } = receipt {
            if self.commands.has_active(*owner) {
                if self.pending_notifications.len() >= MAX_PENDING_NOTIFICATIONS {
                    self.failed = true;
                    return Err(StrategyReceiptFailure {
                        selection: selection.clone(),
                        cause: crate::commands::StrategyReceiptFailureCause::OutputBackpressure,
                    });
                }
                self.pending_notifications.reserve(1);
            }
        }
        Ok(())
    }

    fn publish_superseded_strategy(
        &mut self, owner: SquadLifetime, mut action: StrategyReceiptAction,
        accepted_selection: Option<bees_hivemind::protocol::ValidatedStrategySelection>,
    ) -> Result<StrategyReceiptAction, StrategyReceiptFailure> {
        if let StrategyReceiptAction::BanAndDiscardReservations { strategies, .. } = &action {
            let Some(bans) = self.roster.command_bans_mut(owner) else {
                return Err(StrategyReceiptFailure {
                    selection: accepted_selection.expect("accepted ban reply"),
                    cause: crate::commands::StrategyReceiptFailureCause::InvalidSquad(GameCommandError::MissingCombatSnapshot),
                });
            };
            for &kind in strategies { if !bans.contains(&kind) { bans.push(kind); } }
        }
        if let StrategyReceiptAction::Applied(Activation::Applied {
            superseded, ..
        }) = &mut action {
            if let Some(finished) = superseded.take() {
                if let Err(error) = self.credit.finish(&finished) {
                    self.failed = true;
                    return Err(StrategyReceiptFailure {
                        selection: accepted_selection
                            .expect("an applied strategy came from an accepted reply"),
                        cause: crate::commands::StrategyReceiptFailureCause::Credit(error),
                    });
                }
                // No second settlement owner is returned to the socket:
                // the Level outbox now carries the completed predecessor.
                self.pending_notifications.push(NativeLevelNotification {
                    tick: self.next_tick.saturating_sub(1),
                    kind: NativeLevelNotificationKind::FinishedCommand(finished),
                });
                Self::update_output_peak(
                    &mut self.peak_pending_notifications,
                    self.pending_notifications.len(),
                );
            }
        }
        Ok(action)
    }

    /// Apply any non-bomb strategy receipt to the SAME owning Level,
    /// preserving the same terminal/abort discard behavior as the Bomb
    /// activation path. The connection still owns all OutcomeIds.
    pub fn apply_strategy_receipt(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        random: &mut RandomStream,
        squad: NativeSquadOrder<'_>,
        friendlies: &[NativeFriendlySquad],
        settings: HiveMindCommandSettings,
        receipt: StrategyReceipt,
    ) -> Result<StrategyReceiptAction, StrategyReceiptFailure> {
        if self.is_failed() || self.completed() {
            let ids = match receipt {
                StrategyReceipt::Retired { reservation_ids, .. } =>
                    reservation_ids,
                StrategyReceipt::Accepted { selection, .. } =>
                    vec![selection.command_outcome_id, selection.shooting_outcome_id],
            };
            return Ok(StrategyReceiptAction::DiscardReservations(ids));
        }
        self.preflight_strategy_supersession(&receipt)?;
        let source = match &receipt {
            StrategyReceipt::Accepted { selection, .. } => Some(selection.clone()),
            _ => None,
        };
        let owner = squad.owner;
        let action = self.commands.apply_strategy_receipt(
            world, navigation, random, squad, friendlies, settings, receipt,
        )?;
        self.publish_superseded_strategy(owner, action, source)
    }

    /// Apply a HiveMind receipt using this Level's SAME live roster,
    /// command director and CombatWorld. The network/Level adapter must
    /// supply the real, ordered Bomb weapons, enemy target metadata,
    /// and Unity Squad.IsCarrierSquad. The latter controls the source
    /// defenseless-Striker ban, never inferred from a ship-type guess.
    /// No separate squad roster or reservation ledger is created.
    pub fn apply_strategy_receipt_with_bombing(
        &mut self,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        random: &mut RandomStream,
        squad: NativeSquadOrder<'_>,
        friendlies: &[NativeFriendlySquad],
        settings: HiveMindCommandSettings,
        receipt: StrategyReceipt,
        enemy_owner: SquadLifetime,
        weapons: &[NativeBombWeapon],
        enemies: &[NativeEnemyTargetStats],
        is_carrier_squad: bool,
    ) -> Result<StrategyReceiptAction, StrategyReceiptFailure> {
        // A late server reply after a terminal/aborted match may only
        // discard its reservations. Never rearm ship commands or bombs.
        if self.is_failed() || self.completed() {
            let ids = match receipt {
                StrategyReceipt::Retired { reservation_ids, .. } =>
                    reservation_ids,
                StrategyReceipt::Accepted { selection, .. } =>
                    vec![selection.command_outcome_id, selection.shooting_outcome_id],
            };
            return Ok(StrategyReceiptAction::DiscardReservations(ids));
        }
        self.preflight_strategy_supersession(&receipt)?;
        let bombing = NativeBombingActivation {
            roster: &self.roster, enemy_owner, is_carrier_squad,
            weapons, enemies,
        };
        let source = match &receipt {
            StrategyReceipt::Accepted { selection, .. } => Some(selection.clone()),
            _ => None,
        };
        let owner = squad.owner;
        let action = self.commands.apply_strategy_receipt_with_bombing(
            world, navigation, random, squad, friendlies,
            settings, receipt, Some(bombing),
        )?;
        self.publish_superseded_strategy(owner, action, source)
    }

    /// Called after the owning world's fixed step but before
    /// process_completed_tick consumes that tick's combat events. Match
    /// Unity's Striker/YellowJacket.OnTriggerEnter2D without synthesizing
    /// a second contact cache, command ID or training-only bomb model.
    ///
    /// The command authorizes HasDroppedBomb, while CombatWorld owns
    /// IsBombReady, selected Bomb target and the exact hull overlap.
    /// Unsupported command compositions remain inert, not successful.
    pub fn deliver_commanded_bomber_contact(
        &mut self,
        completed_tick: u64, world: &mut CombatWorld,
        bomber_id: i64, contacted_target_id: i64,
        collisions: &CollisionMatrix,
    ) -> Result<bool, NativeLevelEventError> {
        if self.is_failed() || self.completed() {
            return Err(NativeLevelEventError::FailedLevel);
        }
        if completed_tick != self.next_tick {
            self.failed = true;
            return Err(NativeLevelEventError::MissingOrRepeatedTick {
                expected: self.next_tick, got: completed_tick,
            });
        }
        let Some(owner) = self.roster.owning_squad(bomber_id)
        else { return Ok(false) };
        let Some(command_outcome_id) = self.commands
            .bombing_contact_outcome(owner, bomber_id)
        else { return Ok(false) };
        let outcome = match world.ship(bomber_id).map(|ship| ship.ship_type) {
            Some(19) => world.drop_game_striker_bomb_on_contact(
                bomber_id, contacted_target_id,
                command_outcome_id, collisions,
            ),
            Some(22) => world.detonate_game_yellow_jacket_on_contact(
                bomber_id, contacted_target_id, command_outcome_id, collisions,
            ),
            _ => return Ok(false),
        };
        match outcome {
            Ok(delivered) => {
                if delivered && world.ship(bomber_id).is_some_and(|ship|
                    ship.ship_type == 19,
                ) {
                    let recorded = self.commands.record_bombing_striker_drop(
                        owner, bomber_id,
                    );
                    debug_assert!(recorded, "delivered Striker must belong to active run");
                }
                Ok(delivered)
            },
            Err(error) => {
                self.failed = true;
                Err(NativeLevelEventError::Commands(
                    GameCommandError::World(error),
                ))
            }
        }
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

    pub(crate) fn queue_campaign_dialogue(
        &mut self, tick: u64, cue: crate::campaign_dialogue::CampaignCue,
    ) -> Result<(), NativeLevelEventError> {
        Self::reserve_output_records(&mut self.pending_notifications, &mut self.failed, 1)?;
        self.pending_notifications.push(NativeLevelNotification {
            tick, kind: NativeLevelNotificationKind::CampaignDialogue(cue),
        });
        Ok(())
    }

    pub fn record_reinforcements(&mut self, tick: u64, ships: usize, squads: usize)
        -> Result<(), NativeLevelEventError>
    {
        Self::reserve_output_records(&mut self.pending_notifications, &mut self.failed, 1)?;
        self.pending_notifications.push(NativeLevelNotification {
            tick, kind: NativeLevelNotificationKind::ReinforcementsArrived { ships, squads },
        });
        // Count only committed event publications, not attempted admissions.
        // No replay scan or additional per-step instrumentation is needed.
        self.reinforcement_arrivals.waves += 1;
        self.reinforcement_arrivals.squads += squads as u64;
        self.reinforcement_arrivals.ships += ships as u64;
        self.last_reinforcement_tick = Some(tick);
        Ok(())
    }

    pub fn reinforcement_arrivals(&self) -> NativeReinforcementArrivals {
        self.reinforcement_arrivals
    }

    pub fn last_reinforcement_tick(&self) -> Option<u64> {
        self.last_reinforcement_tick
    }

    /// The first pending event's stable, zero-based sequence within this
    /// episode. For each following event add its index in the returned slice.
    /// The host's run incarnation, WorldId and episode revision provide the
    /// remaining durable identity. This sequence is deliberately NOT a
    /// second notification registry or a wire-level OutcomeId.
    pub fn pending_notification_sequence(&self) -> u64 {
        self.acknowledged_notification_count
    }

    pub fn acknowledge_notifications(
        &mut self, count: usize,
    ) -> Result<(), NativeLevelEventError> {
        if count > self.pending_notifications.len() {
            return Err(NativeLevelEventError::InvalidOutputAcknowledgement);
        }
        let next = u64::try_from(count).ok()
            .and_then(|count| self.acknowledged_notification_count.checked_add(count))
            .ok_or(NativeLevelEventError::InvalidOutputAcknowledgement)?;
        self.pending_notifications.drain(..count);
        self.acknowledged_notification_count = next;
        Ok(())
    }

    /// Consume only the newly emitted authoritative notification suffix.
    /// This one command-scoped projection works across rollover windows:
    /// acknowledgements may remove the outbox prefix, but do not remove
    /// accrued credit for an already-finished command with delayed shots.
    fn account_new_notifications(
        pending: &mut Vec<NativeLevelNotification>,
        credit: &mut NativeCommandCreditLedger,
        failed: &mut bool,
        peak: &mut usize,
        first: usize, tick: u64,
    ) -> Result<(), NativeLevelEventError> {
        let mut terminal = None;
        let mut accounting_error = None;
        for notification in &pending[first..] {
            let result = match &notification.kind {
                NativeLevelNotificationKind::CombatHit(hit_credit) =>
                    credit.record_hit(
                        hit_credit.attacker_command_outcome_id,
                        hit_credit.defender_command_outcome_id,
                        hit_credit.hit.command_tsv_delta, hit_credit.hit.delta.tsv_loss,
                    ).and_then(|()| {
                        if let Some(killer) = &hit_credit.fire_barge_killer_credit {
                            credit.record_hit(killer.command_outcome_id, None, killer.tsv_delta, 0)
                        } else { Ok(()) }
                    }),
                NativeLevelNotificationKind::VisionCommandCredit(sighting) =>
                    credit.record_sighting(sighting.selection.server_outcome(), sighting.tsv_delta),
                NativeLevelNotificationKind::UnattributedDamage {
                    delta, defender_command_outcome_id, ..
                } => credit.record_unattributed(
                    *defender_command_outcome_id, delta.tsv_loss,
                ),
                NativeLevelNotificationKind::FinishedCommand(finished) =>
                    credit.finish(finished),
                NativeLevelNotificationKind::AbortedCommand { selection, .. } =>
                    credit.abort(selection),
                NativeLevelNotificationKind::EpisodeFinal(_) => {
                    terminal = Some(true);
                    Ok(())
                }
                NativeLevelNotificationKind::EpisodeAborted => {
                    terminal = Some(false);
                    Ok(())
                }
                _ => Ok(()),
            };
            if let Err(error) = result {
                accounting_error = Some(error);
                break;
            }
        }
        if let Some(error) = accounting_error {
            // The input slice borrow is now over, so the failed suffix
            // can be discarded without risking contradictory EpisodeFinal.
            pending.truncate(first);
            *failed = true;
            return Err(NativeLevelEventError::Credit(error));
        }
        if let Some(successful) = terminal {
            // Preflight the entire terminal publication before consuming
            // the credit projection. A failed append must never strand the
            // already-closed OutcomeBatch outside a durable notification.
            if let Err(error) = Self::reserve_output_records(
                pending, failed, 1,
            ) {
                pending.truncate(first);
                return Err(error);
            }
            let batch = match credit.close_episode(successful) {
                Ok(batch) => batch,
                Err(error) => {
                    pending.truncate(first);
                    *failed = true;
                    return Err(NativeLevelEventError::Credit(error));
                }
            };
            if !batch.is_empty() {
                pending.push(NativeLevelNotification {
                    tick, kind: NativeLevelNotificationKind::CommandOutcomesReady(batch),
                });
                Self::update_output_peak(
                    peak,
                    pending.len(),
                );
            }
        }
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

    pub fn install_targeting(
        &mut self, tick: u64, owner: SquadLifetime,
        selection: bees_hivemind::protocol::ValidatedTargetingSelection, enemy: Option<SquadLifetime>,
    ) -> Result<(), NativeLevelEventError> {
        if self.is_failed() || self.completed() { return Err(NativeLevelEventError::FailedLevel); }
        if tick != self.next_tick.saturating_sub(1) || self.roster.get(owner).is_none() {
            return Err(NativeLevelEventError::InvalidTargetingSelection);
        }
        Self::reserve_output_records(&mut self.pending_notifications, &mut self.failed, 1)?;
        self.credit.register_targeting(selection.outcome_id).map_err(|error| {
            self.failed = true; NativeLevelEventError::Credit(error)
        })?;
        let choice = crate::squad_targeting::NativeInstalledTargeting { selection, enemy };
        self.roster.install_targeting(owner, choice).expect("prevalidated canonical owner");
        self.pending_notifications.push(NativeLevelNotification { tick,
            kind: NativeLevelNotificationKind::TargetingSelected { owner, choice } });
        Self::update_output_peak(&mut self.peak_pending_notifications, self.pending_notifications.len());
        Ok(())
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
        self.record_verified_discovery_at_tick(
            self.next_tick.saturating_sub(1),
            side, kind, entity_id, authored_value,
        )
    }

    /// A collider sighting occurring DURING a fixed physics tick receives
    /// that actual tick, not the previous completed tick. The immutable
    /// first-sighting fact is already validated by CombatWorld's single
    /// discovery cache and is recorded here exactly once for reward and
    /// potential Scouting command completion.
    pub fn record_verified_vision_sighting(
        &mut self, tick: u64, sighting: bees_sim::vision::VisionFirstSighting,
        kind: DiscoveryKind, authored_value: i32,
        reward_squad: Option<SquadLifetime>, target_ship_tsv: Option<i32>,
    ) -> Result<(), NativeLevelEventError> {
        if tick != self.next_tick && tick != self.next_tick.saturating_sub(1) {
            return Err(NativeLevelEventError::MissingOrRepeatedTick {
                expected: self.next_tick, got: tick,
            });
        }
        let is_ship = kind == DiscoveryKind::EnemyShip;
        if sighting.observer_id <= 0 ||
            is_ship != (sighting.kind == bees_sim::level_discovery::DiscoveryKind::Ship) ||
            is_ship != target_ship_tsv.is_some() || target_ship_tsv.is_some_and(|tsv| tsv < 0) {
            return Err(NativeLevelEventError::InvalidVisionCredit);
        }
        let credit = if let (true, Some(owner), Some(target_tsv)) = (is_ship, reward_squad, target_ship_tsv) {
            if self.roster.get(owner).is_some_and(|squad| squad.side == sighting.side) {
                self.commands.current_selection(owner).map(|selection| NativeVisionCommandCredit {
                    observer_id: sighting.observer_id, target_id: sighting.target_id, target_tsv,
                    owner, selection: selection.clone(),
                    // C# casts the clamped float to int (truncation), not rounding.
                    tsv_delta: (target_tsv as f32 * 0.1_f32).clamp(3.0, 40.0) as i32,
                })
            } else { None }
        } else { None };
        if credit.is_some() {
            // Reserve reward + command credit before discovery freshness changes.
            Self::reserve_output_records(&mut self.pending_notifications, &mut self.failed, 2)?;
        }
        let first = self.record_verified_discovery_at_tick(
            tick, sighting.side, kind, sighting.target_id, authored_value,
        )?;
        let first_new = self.pending_notifications.len();
        if first && kind == DiscoveryKind::EnemyShip {
            if let Some(credit) = credit {
                self.pending_notifications.push(NativeLevelNotification {
                    tick, kind: NativeLevelNotificationKind::VisionCommandCredit(credit),
                });
                Self::update_output_peak(&mut self.peak_pending_notifications, self.pending_notifications.len());
            }
            if let Some(squad) = reward_squad {
                self.commands.record_first_ship_sighting(squad, sighting.side);
            }
        }
        Self::account_new_notifications(
            &mut self.pending_notifications, &mut self.credit,
            &mut self.failed, &mut self.peak_pending_notifications, first_new, tick,
        )?;
        Ok(())
    }

    fn record_verified_discovery_at_tick(
        &mut self, tick: u64, side: i32, kind: DiscoveryKind,
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
        self.queue_immediate_emission_at_tick(emission, tick)?;
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
        self.queue_immediate_emission_at_tick(
            emission, self.next_tick.saturating_sub(1),
        )
    }

    fn queue_immediate_emission_at_tick(
        &mut self, emission: Option<RewardEmission>, tick: u64,
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
                tick,
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
    pub fn survival_status(&self) -> Option<bees_sim::episode_terminal::SurvivalStatus> {
        self.episode.survival_status()
    }

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
            |_, _, _, _| Err(GameCommandError::MissingCombatSnapshot),
            |_, _, _, _, _| Err(GameCommandError::MissingCombatSnapshot),
            |_, _, _, _, _| Err(GameCommandError::MissingCombatSnapshot),
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
            SquadLifetime, SquadLifetime, &mut CombatWorld, &NativeSquadRoster,
        ) -> Result<NativeCircleCommandSnapshot, GameCommandError>,
        swipe_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld, &NativeSquadRoster, bool,
        ) -> Result<NativeSwipeCommandSnapshot, GameCommandError>,
        aggressive_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld, &NativeSquadRoster, bool,
        ) -> Result<NativeAggressiveCommandSnapshot, GameCommandError>,
    ) -> Result<(), NativeLevelEventError> {
        self.advance_timed_commands_with_bombing(
            completed_tick, world, navigation, friendlies, random, dt,
            circle_snapshot, swipe_snapshot, aggressive_snapshot,
            |_, _, _, _, _| Err(GameCommandError::MissingCombatSnapshot),
            |_, _, _, _, _| Err(GameCommandError::MissingCombatSnapshot),
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
            SquadLifetime, SquadLifetime, &mut CombatWorld, &NativeSquadRoster,
        ) -> Result<NativeCircleCommandSnapshot, GameCommandError>,
        swipe_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld, &NativeSquadRoster, bool,
        ) -> Result<NativeSwipeCommandSnapshot, GameCommandError>,
        aggressive_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld, &NativeSquadRoster, bool,
        ) -> Result<NativeAggressiveCommandSnapshot, GameCommandError>,
        bombing_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &CombatWorld, &NativeSquadRoster, bool,
        ) -> Result<NativeBombingTimerSnapshot, GameCommandError>,
        charge_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &CombatWorld, &NativeSquadRoster, bool,
        ) -> Result<NativeChargeTimerSnapshot, GameCommandError>,
    ) -> Result<(), NativeLevelEventError> {
        self.advance_timed_commands_with_scouting_center(
            completed_tick, world, navigation, friendlies, random, dt,
            circle_snapshot, swipe_snapshot, aggressive_snapshot,
            bombing_snapshot, charge_snapshot,
            |_, _, _| Err(GameCommandError::MissingScoutingContext),
        )
    }

    /// Level-only source geometry is requested solely when a Scouting
    /// command needs a new random destination. No fake hull-derived
    /// Squad.GetPosition is allowed in the native game.
    pub fn advance_timed_commands_with_scouting_center(
        &mut self,
        completed_tick: u64,
        world: &mut CombatWorld,
        navigation: &NavigationRuntime,
        friendlies: &[NativeFriendlySquad],
        random: &mut RandomStream,
        dt: f32,
        circle_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld, &NativeSquadRoster,
        ) -> Result<NativeCircleCommandSnapshot, GameCommandError>,
        swipe_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld, &NativeSquadRoster, bool,
        ) -> Result<NativeSwipeCommandSnapshot, GameCommandError>,
        aggressive_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &mut CombatWorld, &NativeSquadRoster, bool,
        ) -> Result<NativeAggressiveCommandSnapshot, GameCommandError>,
        bombing_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &CombatWorld, &NativeSquadRoster, bool,
        ) -> Result<NativeBombingTimerSnapshot, GameCommandError>,
        charge_snapshot: impl FnMut(
            SquadLifetime, SquadLifetime, &CombatWorld, &NativeSquadRoster, bool,
        ) -> Result<NativeChargeTimerSnapshot, GameCommandError>,
        scouting_center: impl FnMut(
            SquadLifetime, &CombatWorld, &crate::squads::NativeSquadEntry,
        ) -> Result<Vec2, GameCommandError>,
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
        let roster = &self.roster;
        let mut circle_snapshot = circle_snapshot;
        let mut swipe_snapshot = swipe_snapshot;
        let mut aggressive_snapshot = aggressive_snapshot;
        let mut bombing_snapshot = bombing_snapshot;
        let mut charge_snapshot = charge_snapshot;
        if let Err(error) = self.commands.advance_into_with_scouting_center(
            world, navigation, Some(&self.roster), friendlies, random, dt,
            |owner, enemy, world| circle_snapshot(owner, enemy, world, roster),
            |owner, enemy, world, need| swipe_snapshot(owner, enemy, world, roster, need),
            |owner, enemy, world, need| aggressive_snapshot(owner, enemy, world, roster, need),
            |owner, enemy, world, need| bombing_snapshot(owner, enemy, world, roster, need),
            |owner, enemy, world, need| charge_snapshot(owner, enemy, world, roster, need), scouting_center,
            &mut self.timed_finished,
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
        let (capability_count, minion_count, combat_damage_count) = events.iter()
            .fold((0usize, 0usize, 0usize), |(caps, minions, damage), event| (
                caps + usize::from(requires_level_capability_delivery(event))
                    + usize::from(policy_retirement(event).is_some()),
                minions + usize::from(matches!(event, CombatEvent::ScoutBeaconDropped { .. })),
                damage + usize::from(matches!(event,
                    CombatEvent::Hit(_) | CombatEvent::UnattributedDamage { .. })),
            ));
        Self::reserve_output_records(
            &mut self.pending_notifications, &mut self.failed,
            capability_count.saturating_add(minion_count)
                .saturating_add(combat_damage_count)
                .saturating_add(self.timed_finished.len()),
        )?;
        let first_new = self.pending_notifications.len();
        // Source Mining.Mine mutates the actual rock and fleet before
        // SetFinalize publishes its command outcome. Preserve that
        // relative order even across large asynchronous rollout windows.
        for event in events {
            self.observe_fire_barge_death(world, event);
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
            if let CombatEvent::Hit(hit) = event {
                let owners = self.damage_command_owners(event)
                    .expect("Hit event always has damage ownership");
                self.pending_notifications.push(NativeLevelNotification {
                    tick: completed_tick,
                    kind: NativeLevelNotificationKind::CombatHit(NativeCombatHit {
                        hit: *hit,
                        attacker_command_outcome_id: owners.attacker,
                        defender_command_outcome_id: owners.defender,
                        fire_barge_killer_credit: owners.fire_barge_killer,
                    }),
                });
            } else if let CombatEvent::UnattributedDamage { target_id, delta } = event {
                let owners = self.damage_command_owners(event)
                    .expect("UnattributedDamage event always has ownership");
                self.pending_notifications.push(NativeLevelNotification {
                    tick: completed_tick,
                    kind: NativeLevelNotificationKind::UnattributedDamage {
                        target_id: *target_id, delta: *delta,
                        defender_command_outcome_id: owners.defender,
                    },
                });
            }
            if requires_level_capability_delivery(event) {
                self.pending_notifications.push(NativeLevelNotification {
                    tick: completed_tick,
                    kind: NativeLevelNotificationKind::Gameplay(event.clone()),
                });
            }
            if let Some(kind) = policy_retirement(event) {
                self.pending_notifications.push(NativeLevelNotification {
                    tick: completed_tick, kind,
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
        Self::account_new_notifications(
            &mut self.pending_notifications, &mut self.credit,
            &mut self.failed, &mut self.peak_pending_notifications,
            first_new, completed_tick,
        )?;
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
        // Record ownership before roster reconciliation removes commands
        // of ships killed by this very same physical event batch. The
        // retained shooter ID from a projectile is authoritative even if
        // its owner was retired in an earlier tick.
        self.core_damage_owners.clear();
        for event in world.events() {
            self.observe_fire_barge_death(world, event);
            if let Some(owners) = self.damage_command_owners(event) {
                self.core_damage_owners.push(owners);
            }
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
        let (capability_notifications, combat_damage_notifications) = world.events()
            .iter().fold((0usize, 0usize), |(caps, damage), event| (
                caps + usize::from(requires_level_capability_delivery(event))
                    + usize::from(policy_retirement(event).is_some()),
                damage + usize::from(matches!(event,
                    CombatEvent::Hit(_) | CombatEvent::UnattributedDamage { .. })),
            ));
        if combat_damage_notifications != self.core_damage_owners.len() {
            self.failed = true;
            return Err(NativeLevelEventError::InconsistentCombatAttribution);
        }
        let new_count = self.changes.len()
            .saturating_add(self.finished.len())
            .saturating_add(capability_notifications)
            .saturating_add(combat_damage_notifications)
            .saturating_add(reward_count)
            .saturating_add(usize::from(reward.finalized.is_some()));
        // Only the notification outbox is mutated here: the reward and
        // group slices remain borrowed from other independent fields.
        Self::reserve_output_records(
            &mut self.pending_notifications, &mut self.failed, new_count,
        )?;
        let first_new = self.pending_notifications.len();
        for &change in &self.changes {
            self.pending_notifications.push(NativeLevelNotification {
                tick: completed_tick,
                kind: NativeLevelNotificationKind::SquadChange(change),
            });
        }
        // Physical hits and successful capabilities occurred before the
        // roster/command finalization callbacks. Keep their source ordering
        // in the durable outbox so a sink never settles a same-tick command
        // before observing the damage/mining that caused its completion.
        // A delayed projectile can still arrive in a LATER tick; finishing
        // the command is not a safe StoreCommands commit by itself.
        let mut damage_index = 0usize;
        for event in world.events() {
            if let CombatEvent::Hit(hit) = event {
                let owners = self.core_damage_owners[damage_index];
                damage_index += 1;
                self.pending_notifications.push(NativeLevelNotification {
                    tick: completed_tick,
                    kind: NativeLevelNotificationKind::CombatHit(NativeCombatHit {
                        hit: *hit,
                        attacker_command_outcome_id: owners.attacker,
                        defender_command_outcome_id: owners.defender,
                        fire_barge_killer_credit: owners.fire_barge_killer,
                    }),
                });
            } else if let CombatEvent::UnattributedDamage { target_id, delta } = event {
                let owners = self.core_damage_owners[damage_index];
                damage_index += 1;
                self.pending_notifications.push(NativeLevelNotification {
                    tick: completed_tick,
                    kind: NativeLevelNotificationKind::UnattributedDamage {
                        target_id: *target_id, delta: *delta,
                        defender_command_outcome_id: owners.defender,
                    },
                });
            } else if let Some(kind) = policy_retirement(event) {
                self.pending_notifications.push(NativeLevelNotification {
                    tick: completed_tick, kind,
                });
            } else if requires_level_capability_delivery(event) {
                self.pending_notifications.push(NativeLevelNotification {
                    tick: completed_tick,
                    kind: NativeLevelNotificationKind::Gameplay((*event).clone()),
                });
            }
        }
        for finished in &self.finished {
            self.pending_notifications.push(NativeLevelNotification {
                tick: completed_tick,
                kind: NativeLevelNotificationKind::FinishedCommand(
                    finished.clone(),
                ),
            });
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
        Self::account_new_notifications(
            &mut self.pending_notifications, &mut self.credit,
            &mut self.failed, &mut self.peak_pending_notifications,
            first_new, completed_tick,
        )?;
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
