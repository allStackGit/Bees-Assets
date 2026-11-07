//! A durable, source-ordered diagnostic notification destination for native
//! gameplay runs. This is intentionally NOT a PPO learner or a replacement
//! for the rendered Level's capability/presentation handler.
//!
//! The Level driver acknowledges a notification only after accept returns Ok.
//! Each record therefore reaches stable storage before its source outbox
//! entry is retired. A distinct, exclusive file is required for each run.

use std::fs::{File, OpenOptions};
use std::io::{self, Write};
use std::path::Path;

use bees_gameplay::level_events::{
    NativeLevelNotification, NativeLevelNotificationKind,
};
use bees_gameplay::level_host::NativeLevelNotificationSink;
use bees_sim::simulation::WorldId;
use serde_json::json;

pub struct DurableDiagnosticNotifications {
    file: File,
    run_id: String,
}

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
        Ok(Self { file, run_id })
    }

    pub fn sync(&mut self) -> io::Result<()> {
        self.file.sync_all()
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
        let kind = match &notification.kind {
            NativeLevelNotificationKind::SquadChange(_) => "squad_change",
            NativeLevelNotificationKind::Gameplay(_) => "gameplay",
            NativeLevelNotificationKind::FinishedCommand(_) => "finished_command",
            NativeLevelNotificationKind::AbortedCommand { .. } => "aborted_command",
            NativeLevelNotificationKind::Reward(_) => "reward",
            NativeLevelNotificationKind::GroupReward(_) => "group_reward",
            NativeLevelNotificationKind::EpisodeFinal(_) => "episode_final",
            NativeLevelNotificationKind::EpisodeAborted => "episode_aborted",
        };
        // The payload is diagnostic Debug text, not a machine-readable
        // training reward transport. Event identity, type, tick and full
        // diagnostic payload remain available without adding a second
        // serialization model to the authoritative gameplay types.
        let record = json!({
            "schema_version": 1,
            "run_id": self.run_id,
            "world_id": world.0,
            "episode_revision": episode_revision,
            "event_sequence": event_sequence,
            "tick": notification.tick,
            "kind": kind,
            "payload_debug": format!("{:?}", notification.kind),
        });
        // Encode before writing so serialization failures never leave a
        // fragment that looks like a committed record on disk.
        let mut bytes = serde_json::to_vec(&record).map_err(io::Error::other)?;
        bytes.push(b'\n');
        self.file.write_all(&bytes)?;
        // The Level driver may now acknowledge this exact prefix. Do not
        // buffer uncommitted records across a call to accept.
        self.file.sync_data()
    }
}
