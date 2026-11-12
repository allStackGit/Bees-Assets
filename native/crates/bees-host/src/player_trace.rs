//! Native player inputs shared by the client, trace recorder, and playback.

use std::collections::VecDeque;
use std::fs::File;
use std::io::{self, BufRead, BufReader};
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

pub struct NativePlayerTrace {
    pub inputs: VecDeque<NativeTraceInput>,
    pub checkpoints: VecDeque<NativeTraceCheckpoint>,
    pub end_round: u64,
    pub policy_actions_sha256: Option<String>,
}

impl NativePlayerTrace {
    pub fn read(
        path: &Path, config_sha256: &str, gameplay_sha256: &str, expected_levels: &[NativeTraceLevel],
        expected_model: Option<&str>,
    ) -> io::Result<Self> {
        let invalid = |message: &str| io::Error::new(io::ErrorKind::InvalidData, message);
        let mut records = BufReader::new(File::open(path)?).lines()
            .map(|line| serde_json::from_str::<TraceRecord>(&line?).map_err(io::Error::from));
        let header = records.next().transpose()?;
        let schema_version = match header {
            Some(TraceRecord::PlayerInputTrace { schema_version, run_config_sha256,
                gameplay_config_sha256, policy_manifest_sha256, levels })
                if (schema_version == 1 || schema_version == 2) &&
                    run_config_sha256 == config_sha256 &&
                    gameplay_config_sha256 == gameplay_sha256 && levels == expected_levels &&
                    policy_manifest_sha256.as_deref() == expected_model => schema_version,
            _ => return Err(invalid("player trace does not match the offline battle configuration")),
        };
        let mut inputs = VecDeque::new();
        let mut checkpoints = VecDeque::new();
        let mut previous = 0;
        while let Some(record) = records.next().transpose()? {
            match record {
                TraceRecord::PlayerInput { round, tick, action } if round >= previous && tick <= round => {
                    previous = round;
                    inputs.push_back(NativeTraceInput { round, tick, action });
                }
                TraceRecord::PhysicalStateCheckpoint {
                    round, world_id, episode_revision, tick, sha256,
                } if schema_version == 2 && round >= previous && tick <= round &&
                    sha256.len() == 64 &&
                    sha256.bytes().all(|byte| byte.is_ascii_hexdigit()) &&
                    expected_levels.iter().any(|level| level.world_id == world_id) => {
                    previous = round;
                    checkpoints.push_back(NativeTraceCheckpoint {
                        round, world_id: WorldId(world_id),
                        episode_revision, tick, sha256,
                    });
                }
                TraceRecord::PlayerInputTraceEnd { rounds, failed_window: None, policy_actions_sha256 }
                    if rounds >= previous && policy_actions_sha256.is_some() == expected_model.is_some() => {
                    if records.next().is_some() {
                        return Err(invalid("records follow the end of the player trace"));
                    }
                    if schema_version == 2 && checkpoints.is_empty() {
                        return Err(invalid("version-2 player trace has no physical state checkpoints"));
                    }
                    return Ok(Self { inputs, checkpoints, end_round: rounds, policy_actions_sha256 });
                }
                _ => return Err(invalid("invalid, out-of-order, or failed player trace")),
            }
        }
        Err(invalid("player trace is missing its final record"))
    }
}
