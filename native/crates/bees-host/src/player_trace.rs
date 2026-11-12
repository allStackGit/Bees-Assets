//! Native player inputs shared by the client, trace recorder, and playback.

use std::collections::VecDeque;
use std::fs::File;
use std::io::{self, BufRead, BufReader};
use std::path::Path;

use bees_gameplay::player_inputs::{
    NativePlayerHeldFire, NativePlayerSquadCeaseFire, NativePlayerSquadCommands, NativePlayerSquadFire,
    NativePlayerSquadMoves, NativePlayerSquadSpecial, NativePlayerSquadMatchSpeed,
    NativePlayerSquadShooting, NativePlayerSquadLockOn,
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

pub struct NativePlayerTrace {
    pub inputs: VecDeque<NativeTraceInput>,
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
        match header {
            Some(TraceRecord::PlayerInputTrace { schema_version: 1, run_config_sha256,
                gameplay_config_sha256, policy_manifest_sha256, levels })
                if run_config_sha256 == config_sha256 &&
                    gameplay_config_sha256 == gameplay_sha256 && levels == expected_levels &&
                    policy_manifest_sha256.as_deref() == expected_model => {}
            _ => return Err(invalid("player trace does not match the offline battle configuration")),
        }
        let mut inputs = VecDeque::new();
        let mut previous = 0;
        while let Some(record) = records.next().transpose()? {
            match record {
                TraceRecord::PlayerInput { round, tick, action } if round >= previous && tick <= round => {
                    previous = round;
                    inputs.push_back(NativeTraceInput { round, tick, action });
                }
                TraceRecord::PlayerInputTraceEnd { rounds, failed_window: None, policy_actions_sha256 }
                    if rounds >= previous && policy_actions_sha256.is_some() == expected_model.is_some() => {
                    if records.next().is_some() {
                        return Err(invalid("records follow the end of the player trace"));
                    }
                    return Ok(Self { inputs, end_round: rounds, policy_actions_sha256 });
                }
                _ => return Err(invalid("invalid, out-of-order, or failed player trace")),
            }
        }
        Err(invalid("player trace is missing its final record"))
    }
}
