//! Read-only recovery of source-authoritative terminal command outcomes.
//! The journal is immutable across process restarts; only a completed
//! CommandOutcomesReady record can be replayed. Never infer TSV from a
//! partially written episode or revive a stale live SquadLifetime.

use std::collections::BTreeMap;
use std::fs::File;
use std::io::{self, BufRead, BufReader, Read};
use std::path::Path;

use bees_hivemind::outcomes::{
    JournalOutcomeSource, OutcomeBatch, RecordedOutcomeBatch,
};
use bees_hivemind::protocol::HiveMindProtocolError;
use serde::Deserialize;

#[derive(Debug)]
pub enum JournalRecoveryError<E> {
    Io(io::Error),
    RecordTooLarge { line: u64 },
    InvalidRecord { line: u64, error: serde_json::Error },
    UnsupportedSchema { line: u64 },
    IncorrectRunId { line: u64 },
    InvalidJournalOrder { line: u64 },
    MissingTerminalBoundary { line: u64 },
    MissingOutcomes { line: u64 },
    InvalidOutcomes { line: u64, error: HiveMindProtocolError },
    Queue { line: u64, error: E },
    IncompleteLastLine { line: u64 },
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct JournalRecoveryCounts {
    pub journal_records: u64,
    pub completed_batches: u64,
    pub newly_checkpointed: u64,
    pub already_checkpointed: u64,
}

#[derive(Deserialize)]
struct JournalRecord {
    schema_version: u32,
    run_id: String,
    world_id: u64,
    episode_revision: u64,
    event_sequence: u64,
    kind: String,
    payload: Option<serde_json::Value>,
}

/// Replay one explicitly selected, previously synchronized run journal.
/// Each source position is submitted to the canonical connection owner,
/// whose checkpointed cursor makes this safe after a 200/409 acknowledgement.
/// The journal reader never deletes, truncates, or appends to the old run.
///
/// Fail closed on a malformed or partially written tail: the caller sees
/// which source line prevented complete recovery instead of silently
/// treating an incomplete episode as a successful one.
pub fn replay_command_outcomes<E>(
    path: &Path,
    expected_run_id: &str,
    mut queue: impl FnMut(&RecordedOutcomeBatch) -> Result<bool, E>,
) -> Result<JournalRecoveryCounts, JournalRecoveryError<E>> {
    if expected_run_id.trim().is_empty() {
        return Err(JournalRecoveryError::IncorrectRunId { line: 0 });
    }
    let file = File::open(path).map_err(JournalRecoveryError::Io)?;
    let mut reader = BufReader::new(file);
    let mut line = Vec::new();
    let mut counts = JournalRecoveryCounts::default();
    // Source ordering is independent per world. Cross-world journal events
    // can be interleaved, but an individual world cannot go backwards.
    let mut last_completed: BTreeMap<u64, (u64, u64)> = BTreeMap::new();
    let mut last_terminal: BTreeMap<u64, (u64, u64)> = BTreeMap::new();

    loop {
        line.clear();
        let read = (&mut reader).take(64 * 1024 * 1024 + 1).read_until(b'\n', &mut line)
            .map_err(JournalRecoveryError::Io)?;
        if read == 0 { break; }
        if read > 64 * 1024 * 1024 { return Err(JournalRecoveryError::RecordTooLarge { line: counts.journal_records + 1 }); }
        counts.journal_records += 1;
        let line_number = counts.journal_records;
        if line.last() != Some(&b'\n') {
            return Err(JournalRecoveryError::IncompleteLastLine {
                line: line_number,
            });
        }
        let record: JournalRecord = serde_json::from_slice(&line)
            .map_err(|error| JournalRecoveryError::InvalidRecord {
                line: line_number, error,
            })?;
        if record.schema_version != 1 {
            return Err(JournalRecoveryError::UnsupportedSchema {
                line: line_number,
            });
        }
        if record.run_id != expected_run_id {
            return Err(JournalRecoveryError::IncorrectRunId {
                line: line_number,
            });
        }
        if record.kind == "episode_final" ||
            record.kind == "episode_aborted"
        {
            last_terminal.insert(record.world_id, (
                record.episode_revision, record.event_sequence,
            ));
            continue;
        }
        if record.kind != "command_outcomes_ready" {
            continue;
        }
        // The producer appends one immutable prepared batch immediately
        // after the authoritative terminal/abort notification. A detached
        // outcome payload is never sufficient proof that a match completed.
        if !last_terminal.get(&record.world_id).is_some_and(|&(episode, sequence)|
            episode == record.episode_revision &&
            sequence.checked_add(1) == Some(record.event_sequence)
        ) {
            return Err(JournalRecoveryError::MissingTerminalBoundary {
                line: line_number,
            });
        }
        let position = (record.episode_revision, record.event_sequence);
        if last_completed.get(&record.world_id)
            .is_some_and(|previous| position <= *previous)
        {
            return Err(JournalRecoveryError::InvalidJournalOrder {
                line: line_number,
            });
        }
        let payload = record.payload.ok_or(
            JournalRecoveryError::MissingOutcomes { line: line_number },
        )?;
        let mut batch: OutcomeBatch = serde_json::from_value(payload)
            .map_err(|error| JournalRecoveryError::InvalidRecord {
                line: line_number, error,
            })?;
        batch.validate_journal()
            .map_err(|error| JournalRecoveryError::InvalidOutcomes {
                line: line_number, error,
            })?;
        let recorded = RecordedOutcomeBatch {
            source: JournalOutcomeSource {
                run_id: record.run_id,
                world_id: record.world_id,
                episode_revision: record.episode_revision,
                event_sequence: record.event_sequence,
            },
            batch,
        };
        let newly_checkpointed = queue(&recorded)
            .map_err(|error| JournalRecoveryError::Queue {
                line: line_number, error,
            })?;
        last_completed.insert(record.world_id, position);
        counts.completed_batches += 1;
        if newly_checkpointed {
            counts.newly_checkpointed += 1;
        } else {
            counts.already_checkpointed += 1;
        }
    }
    Ok(counts)
}
