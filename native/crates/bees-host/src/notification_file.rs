//! A durable, source-ordered diagnostic notification destination for native
//! gameplay runs. This is intentionally NOT a PPO learner or a replacement
//! for the rendered Level's capability/presentation handler.
//!
//! The Level driver acknowledges a notification only after accept returns Ok.
//! Each record therefore reaches stable storage before its source outbox
//! entry is retired. A distinct, exclusive file is required for each run.

use std::collections::{BTreeMap, BTreeSet, VecDeque};
use std::fs::{File, OpenOptions};
use std::io::{self, BufRead, BufReader, Read, Write};
use std::path::Path;

use bees_gameplay::commands::{NativeCommandOrigin, NativeCommandSelection};
use bees_hivemind::outcomes::{OutcomeBatch, PreparedOutcomeBatch};
use bees_gameplay::level_events::{
    NativeLevelNotification, NativeLevelNotificationKind,
};
use bees_gameplay::level_host::NativeLevelNotificationSink;
use bees_gameplay::group_rewards::GroupRewardKind;
use bees_sim::simulation::WorldId;
use serde::{Deserialize, Serialize};
use serde_json::json;
use sha2::{Digest, Sha256};

/// Stable, machine-readable command identity for eventual outcome-ledger
/// replay. The server reservation IDs never come from the command's locally
/// executed strategy after a composition redirect (BombingRun/Charge).
fn command_identity(selection: &NativeCommandSelection) -> serde_json::Value {
    match selection.origin() {
        NativeCommandOrigin::HiveMind(source) => json!({
            "origin": "hivemind",
            "command_outcome_id": source.command_outcome_id,
            "shooting_outcome_id": source.shooting_outcome_id,
            "selected_strategy_id": source.server_strategy.id,
            "executed_command": format!("{:?}", selection.kind()),
            "selected_shooting_id": source.selected_shooting.id,
            "effective_shooting_id": source.effective_shooting.id,
        }),
        NativeCommandOrigin::Local(local) => json!({
            "origin": format!("{:?}", local.source),
            "local_sequence": local.sequence,
            "executed_command": format!("{:?}", selection.kind()),
            "effective_shooting_id": selection.shooting().id,
        }),
    }
}

#[derive(Clone, Debug, Default, Serialize)]
pub struct NativeDiagnosticMeasurements {
    /// Exactly one outcome per durably delivered EpisodeFinal record.
    pub episodes_finished: u64,
    pub episodes_aborted: u64,
    pub episodes_timed_out: u64,
    pub draws: u64,
    /// Attributed physical hits that have actually reached stable storage.
    /// These remain diagnostics, never optimistic StoreCommands credits.
    pub enemy_hits: u64,
    pub friendly_hits: u64,
    pub enemy_hit_tsv_loss: u64,
    pub friendly_hit_tsv_loss: u64,
    pub vision_command_credits: u64,
    pub vision_command_tsv: i64,
    /// Real damage without a valid shooter, kept out of attacker credit.
    pub unattributed_damage_events: u64,
    pub unattributed_tsv_loss: u64,
    /// Winner identities are canonical source side IDs, not model teams.
    pub victories_by_side: BTreeMap<i32, u64>,
    pub total_episode_seconds: f64,
    /// Group-only rewards, keyed by the model team in the actual episode.
    /// ELO outcome markers are not included in these PPO reward totals.
    pub immediate_reward_by_team: [f64; 2],
    pub terminal_reward_by_team: [f64; 2],
    pub elo_markers: u64,
    pub group_episodes_ended: u64,
}

impl NativeDiagnosticMeasurements {
    pub fn mean_episode_seconds(&self) -> Option<f64> {
        (self.episodes_finished != 0).then_some(
            self.total_episode_seconds / self.episodes_finished as f64
        )
    }

    /// Derive one checkpoint's evaluated outcomes from two snapshots of
    /// the same durable source sink. Reject a reset or counter regression
    /// rather than manufacturing a plausible training benchmark.
    pub fn delta_since(&self, previous: &Self) -> Option<Self> {
        let mut victories_by_side = BTreeMap::new();
        for (&side, &wins) in &self.victories_by_side {
            let earlier = previous.victories_by_side
                .get(&side).copied().unwrap_or(0);
            victories_by_side.insert(side, wins.checked_sub(earlier)?);
        }
        if previous.victories_by_side.iter().any(|(&side, &wins)|
            self.victories_by_side.get(&side).copied().unwrap_or(0) < wins
        ) {
            return None;
        }
        Some(Self {
            episodes_finished:
                self.episodes_finished.checked_sub(previous.episodes_finished)?,
            episodes_aborted:
                self.episodes_aborted.checked_sub(previous.episodes_aborted)?,
            episodes_timed_out:
                self.episodes_timed_out.checked_sub(previous.episodes_timed_out)?,
            draws: self.draws.checked_sub(previous.draws)?,
            enemy_hits: self.enemy_hits.checked_sub(previous.enemy_hits)?,
            friendly_hits: self.friendly_hits.checked_sub(previous.friendly_hits)?,
            enemy_hit_tsv_loss: self.enemy_hit_tsv_loss.checked_sub(previous.enemy_hit_tsv_loss)?,
            friendly_hit_tsv_loss: self.friendly_hit_tsv_loss.checked_sub(previous.friendly_hit_tsv_loss)?,
            vision_command_credits: self.vision_command_credits.checked_sub(previous.vision_command_credits)?,
            vision_command_tsv: self.vision_command_tsv.checked_sub(previous.vision_command_tsv)?,
            unattributed_damage_events: self.unattributed_damage_events
                .checked_sub(previous.unattributed_damage_events)?,
            unattributed_tsv_loss: self.unattributed_tsv_loss
                .checked_sub(previous.unattributed_tsv_loss)?,
            victories_by_side,
            total_episode_seconds:
                self.total_episode_seconds - previous.total_episode_seconds,
            immediate_reward_by_team: std::array::from_fn(|i|
                self.immediate_reward_by_team[i] -
                    previous.immediate_reward_by_team[i]
            ),
            terminal_reward_by_team: std::array::from_fn(|i|
                self.terminal_reward_by_team[i] -
                    previous.terminal_reward_by_team[i]
            ),
            elo_markers:
                self.elo_markers.checked_sub(previous.elo_markers)?,
            group_episodes_ended: self.group_episodes_ended
                .checked_sub(previous.group_episodes_ended)?,
        })
    }

    fn record(&mut self, event: &NativeLevelNotification) {
        match &event.kind {
            NativeLevelNotificationKind::VisionCommandCredit(credit) => {
                self.vision_command_credits = self.vision_command_credits.saturating_add(1);
                self.vision_command_tsv = self.vision_command_tsv.saturating_add(i64::from(credit.tsv_delta));
            }
            NativeLevelNotificationKind::CombatHit(credit) => {
                let hit = &credit.hit;
                let tsv = u64::try_from(hit.delta.tsv_loss)
                    .expect("physical hit TSV loss cannot be negative");
                if hit.attacker_side == hit.target_side {
                    self.friendly_hits = self.friendly_hits.saturating_add(1);
                    self.friendly_hit_tsv_loss = self.friendly_hit_tsv_loss.saturating_add(tsv);
                } else {
                    self.enemy_hits = self.enemy_hits.saturating_add(1);
                    self.enemy_hit_tsv_loss = self.enemy_hit_tsv_loss.saturating_add(tsv);
                }
            }
            NativeLevelNotificationKind::UnattributedDamage { delta, .. } => {
                self.unattributed_damage_events = self.unattributed_damage_events.saturating_add(1);
                self.unattributed_tsv_loss = self.unattributed_tsv_loss.saturating_add(
                    u64::try_from(delta.tsv_loss)
                        .expect("physical damage TSV loss cannot be negative"),
                );
            }
            NativeLevelNotificationKind::EpisodeFinal(finalized) => {
                self.episodes_finished = self.episodes_finished.saturating_add(1);
                self.episodes_timed_out = self.episodes_timed_out
                    .saturating_add(u64::from(finalized.result.timed_out));
                self.total_episode_seconds +=
                    f64::from(finalized.result.duration_seconds);
                if finalized.result.winner_side == 0 {
                    self.draws = self.draws.saturating_add(1);
                } else {
                    let wins = self.victories_by_side.entry(
                        finalized.result.winner_side
                    ).or_default();
                    *wins = wins.saturating_add(1);
                }
            }
            NativeLevelNotificationKind::EpisodeAborted =>
                self.episodes_aborted = self.episodes_aborted.saturating_add(1),
            NativeLevelNotificationKind::GroupReward(reward) => {
                // These events have already passed the game-side side,
                // team and finite-reward validation before reaching here.
                let index = usize::from(reward.team_id);
                if index >= 2 { return; }
                match reward.kind {
                    GroupRewardKind::Immediate =>
                        self.immediate_reward_by_team[index] +=
                            f64::from(reward.amount),
                    GroupRewardKind::Terminal =>
                        self.terminal_reward_by_team[index] +=
                            f64::from(reward.amount),
                    GroupRewardKind::EloMarker =>
                        self.elo_markers = self.elo_markers.saturating_add(1),
                    GroupRewardKind::EndEpisode =>
                        self.group_episodes_ended =
                            self.group_episodes_ended.saturating_add(1),
                }
            }
            _ => {}
        }
    }
}


/// Immutable evaluation inputs for one authored episode.
#[derive(Debug, Serialize)]
pub struct EpisodeEvaluationContext {
    pub level_id: i64,
    pub seed: u64,
    pub scene_sha256: String,
    pub gameplay_config_sha256: String,
    pub bee_side: i32,
    pub human_side: i32,
    pub policy_generation: u64,
    pub policy_manifest_sha256: Option<String>,
    pub model_slots_by_team: BTreeMap<u8, u32>,
    pub authored_policy_slots_by_side: BTreeMap<i32, BTreeSet<u32>>,
    pub group_episode_number: Option<u32>,
    pub group_team_by_side: BTreeMap<i32, u8>,
    pub group_agents_by_team: BTreeMap<u8, BTreeSet<u64>>,
    pub decision_period: u32,
}

pub struct DurableDiagnosticNotifications {
    evaluation_contexts: BTreeMap<u64, (u64, EpisodeEvaluationContext)>,
    policy_capture: Option<crate::policy_capture::PolicyCapture>,
    file: File,
    run_id: String,
    journal_id: String,
    next_journal_sequence: u64,
    journal_digest: Sha256,
    pending_prepared: Vec<PreparedOutcomeBatch>,
    // Reused serialization buffer; disk sync happens per bounded chunk,
    // not per individual battle event.
    pending: Vec<u8>,
    measurements: NativeDiagnosticMeasurements,
    // Prepared only AFTER the matching journal chunk has synchronized.
    // The host moves each batch into the connection's durable retry ledger.
    prepared: VecDeque<PreparedOutcomeBatch>,
    unavailable: bool,
}

const MAX_SYNC_EVENTS: usize = 128;
const MAX_SYNC_BYTES: usize = 256 * 1024;
const MAX_RECORD_BYTES: u64 = 64 * 1024 * 1024;

impl DurableDiagnosticNotifications {
    /// Never append to an existing run or overwrite another incarnation.
    /// A restarted rollout cannot reconstruct its previous in-memory world
    /// from this log, so accidental reuse must fail rather than duplicate
    /// event identities.
    pub fn create_new(path: &Path, run_id: String) -> io::Result<Self> {
        if run_id.trim().is_empty() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput, "run_id must not be empty",
            ));
        }
        let mut random = [0_u8; 16];
        getrandom::getrandom(&mut random).map_err(io::Error::other)?;
        let journal_id: String = random.iter().map(|byte| format!("{byte:02x}")).collect();
        let file = OpenOptions::new().create_new(true).write(true).open(path)?;
        // On Unix, sync the parent entry as well as subsequent file data.
        // Otherwise power loss could forget the newly created file name
        // after the driver has acknowledged notifications inside it.
        #[cfg(unix)]
        {
            let parent = path.parent().filter(|p| !p.as_os_str().is_empty())
                .unwrap_or_else(|| Path::new("."));
            File::open(parent)?.sync_all()?;
        }
        Ok(Self {
            file, run_id, journal_id, next_journal_sequence: 0, journal_digest: Sha256::new(),
            pending_prepared: Vec::new(), pending: Vec::new(),
            measurements: NativeDiagnosticMeasurements::default(),
            evaluation_contexts: BTreeMap::new(),
            policy_capture: None,
            prepared: VecDeque::new(),
            unavailable: false,
        })
    }

    pub fn capture_policy_to(&mut self, path: &Path) -> io::Result<()> {
        if self.policy_capture.is_some() {
            return Err(io::Error::other("policy capture already configured"));
        }
        self.policy_capture = Some(crate::policy_capture::PolicyCapture::create(path, &self.journal_id)?);
        Ok(())
    }

    pub fn finish_policy_capture(&mut self, expected_rows: u64) -> io::Result<Option<u64>> {
        self.policy_capture.take().map(|capture| capture.finish(expected_rows)).transpose()
    }

    pub fn capture_terminal_policy(
        &mut self, world: WorldId, revision: u64, tick: u64,
        rows: &[bees_gameplay::level_host::NativeTerminalObservation],
    ) -> io::Result<()> {
        if let Some(capture) = &mut self.policy_capture {
            capture.write_terminal(world.0, revision, tick, rows)?;
        }
        Ok(())
    }

    /// These are counters for notifications the driver is permitted
    /// to acknowledge, never optimistic in-memory game outcomes.
    /// The source event log remains authoritative for detailed evaluation.
    pub fn measurements(&self) -> &NativeDiagnosticMeasurements {
        &self.measurements
    }

    pub fn bind_evaluation_episode(
        &mut self,
        world: WorldId,
        revision: u64,
        context: EpisodeEvaluationContext,
    ) -> io::Result<()> {
        if self.evaluation_contexts.get(&world.0)
            .is_some_and(|(previous, _)| revision <= *previous)
        {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "evaluation episode revision must increase"));
        }
        self.evaluation_contexts.insert(world.0, (revision, context));
        Ok(())
    }

    pub fn sync(&mut self) -> io::Result<()> {
        if self.unavailable {
            return Err(io::Error::other("diagnostic sink unavailable"));
        }
        self.file.sync_all()
    }

    fn encode(
        &mut self,
        world: WorldId,
        episode_revision: u64,
        event_sequence: u64,
        notification: &NativeLevelNotification,
    ) -> io::Result<()> {
        let kind = match &notification.kind {
            NativeLevelNotificationKind::TargetingSelected { .. } => "targeting_selected",
            NativeLevelNotificationKind::SquadChange(_) => "squad_change",
            NativeLevelNotificationKind::Gameplay(_) => "gameplay",
            NativeLevelNotificationKind::CombatHit(_) => "combat_hit",
            NativeLevelNotificationKind::VisionCommandCredit(_) => "vision_command_credit",
            NativeLevelNotificationKind::UnattributedDamage { .. } => "unattributed_damage",
            NativeLevelNotificationKind::FinishedCommand(_) => "finished_command",
            NativeLevelNotificationKind::CommandOutcomesReady(_) => "command_outcomes_ready",
            NativeLevelNotificationKind::AbortedCommand { .. } => "aborted_command",
            NativeLevelNotificationKind::Reward(_) => "reward",
            NativeLevelNotificationKind::GroupReward(_) => "group_reward",
            NativeLevelNotificationKind::PolicyControllerRetired { .. } => "policy_controller_retired",
            NativeLevelNotificationKind::EpisodeFinal(_) => "episode_final",
            NativeLevelNotificationKind::EpisodeAborted => "episode_aborted",
        };
        // Keep exact numeric reward/terminal values machine-readable for
        // diagnostics. MA-POCA's ELO marker and EndEpisode command must
        // NEVER become PPO reward when this log is inspected or migrated.
        let payload = match &notification.kind {
            NativeLevelNotificationKind::Gameplay(bees_sim::combat_world::CombatEvent::ShipExtracted {
                ship_id, side, preserved_tsv,
            }) => Some(json!({"event": "ship_extracted", "ship_id": ship_id,
                "side": side, "preserved_tsv": preserved_tsv})),
            NativeLevelNotificationKind::PolicyControllerRetired { ship_id, binding_revision } =>
                Some(json!({"ship_id": ship_id, "binding_revision": binding_revision})),
            NativeLevelNotificationKind::TargetingSelected { owner, choice } => Some(json!({
                "owner": {"level_id":owner.level_id,"squad_id":owner.squad_id,"lifecycle_id":owner.lifecycle_id},
                "strategy_id": choice.selection.strategy.id, "strategy_name": choice.selection.strategy.name,
                "outcome_id": choice.selection.outcome_id,
                "enemy": choice.enemy.map(|enemy| json!({"level_id":enemy.level_id,"squad_id":enemy.squad_id,"lifecycle_id":enemy.lifecycle_id})),
            })),
            NativeLevelNotificationKind::VisionCommandCredit(credit) => Some(json!({
                "observer_id": credit.observer_id,
                "target_id": credit.target_id,
                "target_tsv": credit.target_tsv,
                "owner": { "level_id": credit.owner.level_id, "squad_id": credit.owner.squad_id,
                    "lifecycle_id": credit.owner.lifecycle_id },
                "selection": command_identity(&credit.selection),
                "tsv_delta": credit.tsv_delta,
            })),
            NativeLevelNotificationKind::CombatHit(credit) => {
                let hit = &credit.hit;
                Some(json!({
                "projectile_id": hit.projectile_id,
                "root_shot_id": hit.root_shot_id,
                "attacker_id": hit.attacker_id,
                "target_id": hit.target_id,
                "attacker_side": hit.attacker_side,
                "target_side": hit.target_side,
                "command_outcome_id": hit.command_outcome_id,
                "attacker_credit_outcome_id": credit.attacker_command_outcome_id,
                "defender_credit_outcome_id": credit.defender_command_outcome_id,
                "fire_barge_killer_credit": credit.fire_barge_killer_credit.map(|value| json!({
                    "killer_ship_id": value.killer_ship_id,
                    "command_outcome_id": value.command_outcome_id,
                    "tsv_delta": value.tsv_delta,
                })),
                "source": format!("{:?}", hit.source),
                "command_tsv_delta": hit.command_tsv_delta,
                "self_inflicted": hit.self_inflicted,
                "health_before": hit.delta.health_before,
                "health_after": hit.delta.health_after,
                "tsv_before": hit.delta.tsv_before,
                "tsv_after": hit.delta.tsv_after,
                "applied_damage": hit.delta.applied_damage,
                "tsv_loss": hit.delta.tsv_loss,
                "killed": hit.delta.killed,
                }))
            },
            NativeLevelNotificationKind::UnattributedDamage {
                target_id, delta, defender_command_outcome_id,
            } => Some(json!({
                "target_id": target_id,
                "defender_credit_outcome_id": defender_command_outcome_id,
                "health_before": delta.health_before,
                "health_after": delta.health_after,
                "tsv_before": delta.tsv_before,
                "tsv_after": delta.tsv_after,
                "applied_damage": delta.applied_damage,
                "tsv_loss": delta.tsv_loss,
                "killed": delta.killed,
            })),
            NativeLevelNotificationKind::CommandOutcomesReady(batch) => Some(
                serde_json::to_value(batch).map_err(io::Error::other)?
            ),
            NativeLevelNotificationKind::FinishedCommand(finished) => Some(json!({
                "owner": {
                    "level_id": finished.owner.level_id,
                    "squad_id": finished.owner.squad_id,
                    "lifecycle_id": finished.owner.lifecycle_id,
                },
                "selection": command_identity(&finished.selection),
                "cause": format!("{:?}", finished.cause),
                "targeting_outcome_id": finished.targeting.map(|selection| selection.outcome_id),
                "targeting_targeted_enemy": finished.targeting_targeted_enemy,
                "shooting_targeted_enemy": finished.shooting_targeted_enemy,
                "capability_command_tsv": finished.capability_command_tsv,
            })),
            NativeLevelNotificationKind::AbortedCommand { owner, selection } => Some(json!({
                "owner": {
                    "level_id": owner.level_id,
                    "squad_id": owner.squad_id,
                    "lifecycle_id": owner.lifecycle_id,
                },
                "selection": command_identity(selection),
            })),
            NativeLevelNotificationKind::Reward(value) => Some(json!({
                "side": value.side, "reward": value.amount,
            })),
            NativeLevelNotificationKind::GroupReward(value) => {
                let (group_kind, ppo_reward, elo_marker) = match value.kind {
                    GroupRewardKind::Immediate =>
                        ("immediate", Some(value.amount), None),
                    GroupRewardKind::Terminal =>
                        ("terminal", Some(value.amount), None),
                    GroupRewardKind::EloMarker =>
                        ("elo_marker", None, Some(value.amount)),
                    GroupRewardKind::EndEpisode =>
                        ("end_episode", None, None),
                };
                Some(json!({
                    "side": value.side,
                    "team_id": value.team_id,
                    "episode_number": value.episode_number,
                    "group_kind": group_kind,
                    "ppo_reward": ppo_reward,
                    "elo_marker": elo_marker,
                }))
            }
            NativeLevelNotificationKind::EpisodeFinal(value) => Some(json!({
                "winner_side": value.result.winner_side,
                "timed_out": value.result.timed_out,
                "extraction_victory": value.result.extraction_victory,
                "duration_seconds": value.result.duration_seconds,
                "bee_side_eliminated": value.result.bee_side_eliminated,
                "human_side_eliminated": value.result.human_side_eliminated,
                "bee_terminal": value.terminal_rewards.bee_terminal,
                "human_terminal": value.terminal_rewards.human_terminal,
                "bee_time": value.terminal_rewards.bee_time,
                "human_time": value.terminal_rewards.human_time,
                "bee_retained_mined_tsv":
                    value.economic_rewards.bee_retained_mined_tsv,
                "human_retained_mined_tsv":
                    value.economic_rewards.human_retained_mined_tsv,
                "bee_retained_reward": value.economic_rewards.bee_retained_reward,
                "human_retained_reward": value.economic_rewards.human_retained_reward,
            })),
            _ => None,
        };
        let mut record = json!({
            "schema_version": 2,
            "journal_id": self.journal_id,
            "journal_sequence": self.next_journal_sequence,
            "run_id": self.run_id,
            "world_id": world.0,
            "episode_revision": episode_revision,
            "event_sequence": event_sequence,
            "tick": notification.tick,
            "kind": kind,
            "payload": payload,
            // CombatHit already has a full structured payload; avoid
            // duplicating every hot combat event as a debug string.
            "payload_debug": match &notification.kind {
                NativeLevelNotificationKind::TargetingSelected { .. } |
                NativeLevelNotificationKind::CombatHit(_) |
                NativeLevelNotificationKind::VisionCommandCredit(_) |
                NativeLevelNotificationKind::UnattributedDamage { .. } |
                NativeLevelNotificationKind::FinishedCommand(_) |
                NativeLevelNotificationKind::AbortedCommand { .. } |
                NativeLevelNotificationKind::CommandOutcomesReady(_) => None,
                _ => Some(format!("{:?}", notification.kind)),
            },
        });
        if matches!(kind, "episode_final" | "episode_aborted") {
            if let Some((revision, context)) = self.evaluation_contexts.get(&world.0) {
                if *revision != episode_revision {
                    return Err(io::Error::new(io::ErrorKind::InvalidData, "terminal evaluation context does not match episode revision"));
                }
                record["evaluation_context"] = serde_json::to_value(context).map_err(io::Error::other)?;
            }
        }
        let record_start = self.pending.len();
        serde_json::to_writer(&mut self.pending, &record)
            .map_err(io::Error::other)?;
        self.pending.push(b'\n');
        if (self.pending.len() - record_start) as u64 > MAX_RECORD_BYTES {
            return Err(io::Error::other("journal record too large"));
        }
        self.journal_digest.update(&self.pending[record_start..]);
        if let NativeLevelNotificationKind::CommandOutcomesReady(batch) = &notification.kind {
            self.pending_prepared.push(PreparedOutcomeBatch {
                journal_id: self.journal_id.clone(), sequence: self.next_journal_sequence,
                prefix_sha256: format!("{:x}", self.journal_digest.clone().finalize()),
                batch: batch.clone(),
            });
        }
        self.next_journal_sequence = self.next_journal_sequence.checked_add(1)
            .ok_or_else(|| io::Error::other("journal sequence exhausted"))?;
        Ok(())
    }

    fn commit_chunk(&mut self) -> io::Result<()> {
        // Any failure leaves this instance unusable. The caller must not
        // retry an ambiguous partially written frame against the same file.
        let result = self.file.write_all(&self.pending)
            .and_then(|()| self.file.sync_data());
        self.pending.clear();
        if result.is_err() { self.unavailable = true; }
        result
    }
}

impl NativeLevelNotificationSink for DurableDiagnosticNotifications {
    type Error = io::Error;

    fn captures_policy_boundaries(&self) -> bool { self.policy_capture.is_some() }

    fn accept_policy_boundary(
        &mut self, world: WorldId, revision: u64, tick: u64,
        rows: &[bees_gameplay::level_host::NativeTerminalObservation],
    ) -> io::Result<()> {
        self.capture_terminal_policy(world, revision, tick, rows)
    }

    fn accept_applied_policy_batch(
        &mut self, batch: &bees_gameplay::inference::NativeLevelInferenceBatch,
    ) -> io::Result<()> {
        if let Some(capture) = &mut self.policy_capture {
            capture.write_batch(batch)?;
        }
        Ok(())
    }

    fn first_prepared_outcomes(&self) -> Option<&PreparedOutcomeBatch> {
        self.prepared.front()
    }

    fn prepared_outcomes_queued(&mut self) {
        self.prepared.pop_front();
    }

    fn accept(
        &mut self,
        world: WorldId,
        episode_revision: u64,
        event_sequence: u64,
        notification: &NativeLevelNotification,
    ) -> io::Result<()> {
        self.accept_batch(
            world, episode_revision, event_sequence,
            std::slice::from_ref(notification),
        ).map_err(|(_, error)| error)
    }

    fn accept_batch(
        &mut self,
        world: WorldId,
        episode_revision: u64,
        first_sequence: u64,
        notifications: &[NativeLevelNotification],
    ) -> Result<(), (usize, io::Error)> {
        if self.unavailable {
            return Err((0, io::Error::other("diagnostic sink unavailable")));
        }
        let mut committed = 0usize;
        while committed < notifications.len() {
            self.pending.clear();
            let mut buffered = 0usize;
            for notification in &notifications[committed..] {
                let sequence = first_sequence + (committed + buffered) as u64;
                if let Err(error) = self.encode(
                    world, episode_revision, sequence, notification,
                ) {
                    self.unavailable = true;
                    return Err((committed, error));
                }
                buffered += 1;
                if buffered == MAX_SYNC_EVENTS ||
                    self.pending.len() >= MAX_SYNC_BYTES
                {
                    break;
                }
            }
            if let Err(error) = self.commit_chunk() {
                return Err((committed, error));
            }
            // A chunk becomes visible to evaluation counters only AFTER
            // the file has synchronized. This matches the exact durable
            // prefix acknowledged by the Level event outbox.
            for event in &notifications[committed..committed + buffered] {
                self.measurements.record(event);
            }
            self.prepared.extend(self.pending_prepared.drain(..));
            committed += buffered;
        }
        Ok(())
    }
}

/// Recovery imports only complete, newline-terminated records from a stopped
/// run. It never reconstructs a world or appends to the previous incarnation.
#[derive(Clone, Debug, Default, Serialize)]
pub struct OutcomeJournalRecovery {
    pub complete_records: u64,
    pub queued_batches: u64,
    pub previously_queued_batches: u64,
    pub incomplete_tail: bool,
}

#[derive(Debug)]
pub enum OutcomeJournalRecoveryError {
    Journal(io::Error),
    Connection(crate::durable_connection::DurableConnectionError),
}

impl From<io::Error> for OutcomeJournalRecoveryError {
    fn from(error: io::Error) -> Self { Self::Journal(error) }
}

/// One append-only journal must always be replayed from its beginning into
/// its original connection checkpoint, in source order. The caller must keep
/// exclusive ownership of both files. Already imported prefixes remain known
/// after 200 acknowledgement or terminal 409 reconciliation.
pub fn recover_command_outcomes(
    path: &Path,
    connection: &mut crate::durable_connection::DurableHiveMindConnection,
) -> Result<OutcomeJournalRecovery, OutcomeJournalRecoveryError> {
    #[derive(Deserialize)]
    struct Row {
        schema_version: u32,
        journal_id: String,
        journal_sequence: u64,
        run_id: String,
        world_id: u64,
        episode_revision: u64,
        event_sequence: u64,
        kind: String,
        payload: serde_json::Value,
    }
    fn invalid(message: &str) -> io::Error {
        io::Error::new(io::ErrorKind::InvalidData, message)
    }
    let mut reader = BufReader::new(File::open(path)?);
    let mut line = Vec::new();
    let mut identity = None;
    let mut digest = Sha256::new();
    let mut terminal = BTreeMap::<u64, (u64, u64)>::new();
    let mut sequences = BTreeMap::<u64, (u64, u64)>::new();
    let mut recovery = OutcomeJournalRecovery::default();
    loop {
        line.clear();
        // Bound malformed input without retaining a whole journal in memory.
        let bytes = (&mut reader).take(MAX_RECORD_BYTES + 1).read_until(b'\n', &mut line)?;
        if bytes == 0 { break; }
        if bytes as u64 > MAX_RECORD_BYTES { return Err(invalid("journal record too large").into()); }
        if !line.ends_with(b"\n") {
            recovery.incomplete_tail = true;
            break;
        }
        digest.update(&line);
        let row: Row = serde_json::from_slice(&line).map_err(|error| invalid(&error.to_string()))?;
        if row.schema_version != 2 || !PreparedOutcomeBatch::valid_journal_id(&row.journal_id) ||
            row.run_id.trim().is_empty() || row.journal_sequence != recovery.complete_records {
            return Err(invalid("unsupported journal or inconsistent append sequence").into());
        }
        let row_identity = (row.journal_id.clone(), row.run_id);
        if identity.as_ref().is_some_and(|original| original != &row_identity) {
            return Err(invalid("mixed journal incarnations").into());
        }
        identity = Some(row_identity);
        // Retain one cursor per world, not one allocation per training episode.
        let (revision, sequence) = sequences.entry(row.world_id)
            .or_insert((row.episode_revision, 0));
        if row.episode_revision < *revision {
            return Err(invalid("repeated retired world episode").into());
        }
        if row.episode_revision > *revision {
            *revision = row.episode_revision;
            *sequence = 0;
        }
        if *sequence != row.event_sequence {
            return Err(invalid("missing or repeated world notification").into());
        }
        *sequence = sequence.checked_add(1).ok_or_else(|| invalid("event sequence exhausted"))?;
        if let Some((sequence, original_digest)) = connection.connection().journal_cursor(&row.journal_id) {
            if row.journal_sequence == sequence && format!("{:x}", digest.clone().finalize()) != original_digest {
                return Err(invalid("journal differs from its checkpointed prefix").into());
            }
        }
        if matches!(row.kind.as_str(), "episode_final" | "episode_aborted") {
            terminal.insert(row.world_id, (row.episode_revision, row.event_sequence));
        }
        if row.kind == "command_outcomes_ready" {
            if !terminal.get(&row.world_id).is_some_and(|&(revision, sequence)|
                revision == row.episode_revision && sequence.checked_add(1) == Some(row.event_sequence)) {
                return Err(invalid("outcomes are detached from the authoritative terminal boundary").into());
            }
            let batch: OutcomeBatch = serde_json::from_value(row.payload)
                .map_err(|error| invalid(&error.to_string()))?;
            if batch.is_empty() { return Err(invalid("empty journal outcome batch").into()); }
            let queued = connection.queue_prepared_outcomes(&PreparedOutcomeBatch {
                journal_id: row.journal_id, sequence: row.journal_sequence,
                prefix_sha256: format!("{:x}", digest.clone().finalize()), batch,
            }).map_err(OutcomeJournalRecoveryError::Connection)?;
            if queued { recovery.queued_batches += 1; }
            else { recovery.previously_queued_batches += 1; }
        }
        recovery.complete_records += 1;
    }
    if let Some((id, _)) = identity {
        if connection.connection().journal_cursor(&id)
            .is_some_and(|(sequence, _)| sequence >= recovery.complete_records) {
            return Err(invalid("journal is shorter than its checkpointed prefix").into());
        }
    }
    Ok(recovery)
}
