//! A durable, source-ordered diagnostic notification destination for native
//! gameplay runs. This is intentionally NOT a PPO learner or a replacement
//! for the rendered Level's capability/presentation handler.
//!
//! The Level driver acknowledges a notification only after accept returns Ok.
//! Each record therefore reaches stable storage before its source outbox
//! entry is retired. A distinct, exclusive file is required for each run.

use std::collections::BTreeMap;
use std::fs::{File, OpenOptions};
use std::io::{self, Write};
use std::path::Path;

use bees_gameplay::level_events::{
    NativeLevelNotification, NativeLevelNotificationKind,
};
use bees_gameplay::level_host::NativeLevelNotificationSink;
use bees_gameplay::group_rewards::GroupRewardKind;
use bees_sim::simulation::WorldId;
use serde::Serialize;
use serde_json::json;

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
            NativeLevelNotificationKind::CombatHit(hit) => {
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


pub struct DurableDiagnosticNotifications {
    file: File,
    run_id: String,
    // Reused serialization buffer; disk sync happens per bounded chunk,
    // not per individual battle event.
    pending: Vec<u8>,
    measurements: NativeDiagnosticMeasurements,
    unavailable: bool,
}

const MAX_SYNC_EVENTS: usize = 128;
const MAX_SYNC_BYTES: usize = 256 * 1024;

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
            file, run_id, pending: Vec::new(),
            measurements: NativeDiagnosticMeasurements::default(),
            unavailable: false,
        })
    }

    /// These are counters for notifications the driver is permitted
    /// to acknowledge, never optimistic in-memory game outcomes.
    /// The source event log remains authoritative for detailed evaluation.
    pub fn measurements(&self) -> &NativeDiagnosticMeasurements {
        &self.measurements
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
            NativeLevelNotificationKind::SquadChange(_) => "squad_change",
            NativeLevelNotificationKind::Gameplay(_) => "gameplay",
            NativeLevelNotificationKind::CombatHit(_) => "combat_hit",
            NativeLevelNotificationKind::UnattributedDamage { .. } => "unattributed_damage",
            NativeLevelNotificationKind::FinishedCommand(_) => "finished_command",
            NativeLevelNotificationKind::AbortedCommand { .. } => "aborted_command",
            NativeLevelNotificationKind::Reward(_) => "reward",
            NativeLevelNotificationKind::GroupReward(_) => "group_reward",
            NativeLevelNotificationKind::EpisodeFinal(_) => "episode_final",
            NativeLevelNotificationKind::EpisodeAborted => "episode_aborted",
        };
        // Keep exact numeric reward/terminal values machine-readable for
        // diagnostics. MA-POCA's ELO marker and EndEpisode command must
        // NEVER become PPO reward when this log is inspected or migrated.
        let payload = match &notification.kind {
            NativeLevelNotificationKind::CombatHit(hit) => Some(json!({
                "projectile_id": hit.projectile_id,
                "root_shot_id": hit.root_shot_id,
                "attacker_id": hit.attacker_id,
                "target_id": hit.target_id,
                "attacker_side": hit.attacker_side,
                "target_side": hit.target_side,
                "command_outcome_id": hit.command_outcome_id,
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
            })),
            NativeLevelNotificationKind::UnattributedDamage { target_id, delta } => Some(json!({
                "target_id": target_id,
                "health_before": delta.health_before,
                "health_after": delta.health_after,
                "tsv_before": delta.tsv_before,
                "tsv_after": delta.tsv_after,
                "applied_damage": delta.applied_damage,
                "tsv_loss": delta.tsv_loss,
                "killed": delta.killed,
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
        let record = json!({
            "schema_version": 1,
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
                NativeLevelNotificationKind::CombatHit(_) |
                NativeLevelNotificationKind::UnattributedDamage { .. } => None,
                _ => Some(format!("{:?}", notification.kind)),
            },
        });
        serde_json::to_writer(&mut self.pending, &record)
            .map_err(io::Error::other)?;
        self.pending.push(b'\n');
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
            committed += buffered;
        }
        Ok(())
    }
}
