//! Native player inputs shared by the client, trace recorder, and playback.

use std::collections::VecDeque;
use std::fs::File;
use std::io::{self, BufRead, BufReader, Seek, SeekFrom};
use std::path::Path;

use bees_gameplay::player_inputs::{
    NativePlayerHeldFire, NativePlayerSquadCeaseFire, NativePlayerSquadCommands, NativePlayerSquadFire,
    NativePlayerSquadMoves, NativePlayerSquadSpecial, NativePlayerSquadMatchSpeed,
    NativePlayerSquadShooting, NativePlayerSquadLockOn,
    NativePlayerSquadChase,
};
use bees_sim::simulation::WorldId;
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(tag = "kind", content = "input", rename_all = "snake_case")]
pub enum NativePlayerInput {
    Move(NativePlayerSquadMoves),
    Support(NativePlayerSquadCommands),
    Fire(NativePlayerSquadFire),
    HeldFire(NativePlayerHeldFire),
    CeaseFire(NativePlayerSquadCeaseFire),
    Special(NativePlayerSquadSpecial),
    MatchSpeed(NativePlayerSquadMatchSpeed),
    Shooting(NativePlayerSquadShooting),
    LockOn(NativePlayerSquadLockOn),
    Chase(NativePlayerSquadChase),
}

impl NativePlayerInput {
    pub fn episode(&self) -> (WorldId, u64) {
        match self {
            Self::Move(input) => (input.world_id, input.episode_revision),
            Self::Support(input) => (input.world_id, input.episode_revision),
            Self::Fire(input) => (input.world_id, input.episode_revision),
            Self::HeldFire(input) => (input.world_id, input.episode_revision),
            Self::CeaseFire(input) => (input.world_id, input.episode_revision),
            Self::Special(input) => (input.world_id, input.episode_revision),
            Self::MatchSpeed(input) => (input.world_id, input.episode_revision),
            Self::Shooting(input) => (input.world_id, input.episode_revision),
            Self::LockOn(input) => (input.world_id, input.episode_revision),
            Self::Chase(input) => (input.world_id, input.episode_revision),
        }
    }
}

#[derive(Debug, PartialEq, Serialize, Deserialize)]
pub struct NativeTraceLevel {
    pub world_id: u64,
    pub level_id: i64,
    pub scene_sha256: String,
    pub seed: Option<u64>,
    pub episode_seeds: Option<Vec<u64>>,
}

#[derive(Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
enum TraceRecord {
    PlayerInputTrace {
        schema_version: u32,
        run_config_sha256: String,
        gameplay_config_sha256: String,
        policy_manifest_sha256: Option<String>,
        levels: Vec<NativeTraceLevel>,
    },
    PlayerInput { round: u64, tick: u64, action: NativePlayerInput },
    /// Version-2 optional sampled physical state. Never implies all
    /// command, random, network, or reward state is identical.
    PhysicalStateCheckpoint {
        round: u64, world_id: u64, episode_revision: u64,
        tick: u64, sha256: String,
    },
    PlayerInputTraceEnd {
        rounds: u64,
        failed_window: Option<String>,
        policy_actions_sha256: Option<String>,
    },
}

pub struct NativeTraceInput {
    pub round: u64,
    pub tick: u64,
    pub action: NativePlayerInput,
}

pub struct NativeTraceCheckpoint {
    pub round: u64,
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub tick: u64,
    pub sha256: String,
}

/// A validated trace is read only through the current battle round.
/// A first linear scan checks the complete file and its finalization before
/// gameplay startup, then seeks back to the first event. Playback retains at
/// most one lookahead record and one round of actions/checkpoints; long
/// recordings never become an unbounded in-memory input queue.
pub struct NativePlayerTrace {
    pub inputs: VecDeque<NativeTraceInput>,
    pub checkpoints: VecDeque<NativeTraceCheckpoint>,
    pub end_round: u64,
    pub policy_actions_sha256: Option<String>,
    reader: BufReader<File>,
    line: String,
    next: Option<TraceRecord>,
    reached_end: bool,
}

fn trace_error(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}

fn read_record(reader: &mut BufReader<File>, line: &mut String)
    -> io::Result<Option<TraceRecord>>
{
    line.clear();
    if reader.read_line(line)? == 0 { return Ok(None); }
    serde_json::from_str(line).map(Some).map_err(io::Error::from)
}

impl NativePlayerTrace {
    /// Validate the complete recorded sequence *before* accepting any
    /// gameplay inputs, without allocating one Vec per trace line.
    /// Backward-compatible schema1 traces have no physical checkpoints;
    /// schema2 requires at least one and retains each exact world identity.
    pub fn read(
        path: &Path, config_sha256: &str, gameplay_sha256: &str, expected_levels: &[NativeTraceLevel],
        expected_model: Option<&str>,
    ) -> io::Result<Self> {
        let mut reader = BufReader::new(File::open(path)?);
        let mut line = String::new();
        let schema_version = match read_record(&mut reader, &mut line)? {
            Some(TraceRecord::PlayerInputTrace { schema_version, run_config_sha256,
                gameplay_config_sha256, policy_manifest_sha256, levels })
                if (schema_version == 1 || schema_version == 2) &&
                    run_config_sha256 == config_sha256 &&
                    gameplay_config_sha256 == gameplay_sha256 && levels == expected_levels &&
                    policy_manifest_sha256.as_deref() == expected_model => schema_version,
            _ => return Err(trace_error(
                "player trace does not match the offline battle configuration",
            )),
        };
        let data_start = reader.stream_position()?;
        let mut previous_round = 0;
        let mut saw_checkpoint = false;
        loop {
            match read_record(&mut reader, &mut line)? {
                Some(TraceRecord::PlayerInput { round, tick, .. })
                    if round >= previous_round && tick <= round => {
                    previous_round = round;
                }
                Some(TraceRecord::PhysicalStateCheckpoint {
                    round, world_id, tick, sha256, ..
                }) if schema_version == 2 && round >= previous_round && tick <= round &&
                    sha256.len() == 64 &&
                    sha256.bytes().all(|byte| byte.is_ascii_hexdigit()) &&
                    expected_levels.iter().any(|level| level.world_id == world_id) => {
                    saw_checkpoint = true;
                    previous_round = round;
                }
                Some(TraceRecord::PlayerInputTraceEnd {
                    rounds, failed_window: None, policy_actions_sha256,
                }) if rounds >= previous_round &&
                    policy_actions_sha256.is_some() == expected_model.is_some() => {
                    if read_record(&mut reader, &mut line)?.is_some() {
                        return Err(trace_error(
                            "records follow the end of the player trace",
                        ));
                    }
                    if schema_version == 2 && !saw_checkpoint {
                        return Err(trace_error(
                            "version-2 player trace has no physical state checkpoints",
                        ));
                    }
                    reader.seek(SeekFrom::Start(data_start))?;
                    return Ok(Self {
                        inputs: VecDeque::new(),
                        checkpoints: VecDeque::new(),
                        end_round: rounds,
                        policy_actions_sha256,
                        reader,
                        line: String::new(),
                        next: None,
                        reached_end: false,
                    });
                }
                Some(_) => return Err(trace_error(
                    "invalid, out-of-order, or failed player trace",
                )),
                None => return Err(trace_error(
                    "player trace is missing its final record",
                )),
            }
        }
    }

    /// Load only the events occurring at the caller's current physical
    /// round. The launcher must consume them before advancing simulation.
    /// Reading forward beyond this boundary would pre-allocate huge
    /// traces and lose the precise input/observation ordering.
    pub fn load_round(&mut self, round: u64) -> io::Result<()> {
        if self.inputs.front().is_some_and(|entry| entry.round < round) ||
            self.checkpoints.front().is_some_and(|entry| entry.round < round) {
            return Err(trace_error("unconsumed native trace events before round"));
        }
        if self.reached_end { return Ok(()); }
        loop {
            let record = match self.next.take() {
                Some(record) => record,
                None => read_record(&mut self.reader, &mut self.line)?
                    .ok_or_else(|| trace_error(
                        "player trace truncated after validation",
                    ))?,
            };
            let event_round = match &record {
                TraceRecord::PlayerInput { round, .. } |
                TraceRecord::PhysicalStateCheckpoint { round, .. } => *round,
                TraceRecord::PlayerInputTraceEnd { .. } => {
                    self.reached_end = true;
                    break;
                }
                TraceRecord::PlayerInputTrace { .. } =>
                    return Err(trace_error("unexpected player trace header")),
            };
            if event_round > round {
                self.next = Some(record);
                break;
            }
            if event_round < round {
                return Err(trace_error("player trace event missed its assigned round"));
            }
            match record {
                TraceRecord::PlayerInput { round, tick, action } =>
                    self.inputs.push_back(NativeTraceInput { round, tick, action }),
                TraceRecord::PhysicalStateCheckpoint {
                    round, world_id, episode_revision, tick, sha256,
                } => self.checkpoints.push_back(NativeTraceCheckpoint {
                    round, world_id: WorldId(world_id), episode_revision, tick, sha256,
                }),
                _ => unreachable!("validated trace event"),
            }
        }
        Ok(())
    }
}
