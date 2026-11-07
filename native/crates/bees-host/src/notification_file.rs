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
    // Reused serialization buffer; disk sync happens per bounded chunk,
    // not per individual battle event.
    pending: Vec<u8>,
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
        Ok(Self { file, run_id, pending: Vec::new(), unavailable: false })
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
            NativeLevelNotificationKind::FinishedCommand(_) => "finished_command",
            NativeLevelNotificationKind::AbortedCommand { .. } => "aborted_command",
            NativeLevelNotificationKind::Reward(_) => "reward",
            NativeLevelNotificationKind::GroupReward(_) => "group_reward",
            NativeLevelNotificationKind::EpisodeFinal(_) => "episode_final",
            NativeLevelNotificationKind::EpisodeAborted => "episode_aborted",
        };
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
            committed += buffered;
        }
        Ok(())
    }
}
