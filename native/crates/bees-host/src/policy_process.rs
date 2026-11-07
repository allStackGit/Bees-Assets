//! Cross-platform, resident-process inference backend for REAL v26 weights.
//!
//! One fixed-size binary frame per entire native model batch. The child
//! process keeps the neural network resident on CPU or ROCm/CUDA. Only
//! observation and action buffers cross the process boundary; game/episode
//! state, RL reward ownership and HiveMind remain in the shared native host.
//! No fake actions, floating-point JSON or per-agent process invocation.
//!
//! A worker MUST complete a versioned v26 ABI handshake, honor every route's
//! model_slot and action mask, and return exactly the matching batch ID.
//! Any I/O/protocol error permanently invalidates this backend instance:
//! a timed-out or partial action stream is never consumed by another step.
//! The native inference layer independently validates every model action.

use std::ffi::OsStr;
use std::io::{self, BufReader, BufWriter, Read, Write};
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};

use bees_gameplay::inference::{NativePolicyInference, NativePolicyRoute};
use bees_sim::policy::{CONTINUOUS_ACTIONS, DISCRETE_BRANCHES, OBSERVATION_FLOATS};
use bees_sim::policy_batch::{PolicyActionMask, PolicyAgentKey};
use bees_sim::policy::{validate_model_abi, PolicyAbiError};

const PROTOCOL_VERSION: u32 = 1;
const REQUEST_MAGIC: &[u8; 4] = b"BINF";
const RESPONSE_MAGIC: &[u8; 4] = b"BOUT";
const READY_MAGIC: &[u8; 4] = b"BHOK";
const PER_ROW_HEADER_BYTES: usize = 16;
const DISCRETE_ACTIONS: usize = DISCRETE_BRANCHES.len();

#[derive(Debug)]
pub enum PolicyProcessError {
    Io(io::Error),
    MissingPipe(&'static str),
    InvalidHandshake,
    Abi(PolicyAbiError),
    InvalidBatch,
    WrongResponse,
    RequestSequenceExhausted,
    Unavailable,
}

impl From<io::Error> for PolicyProcessError {
    fn from(value: io::Error) -> Self { Self::Io(value) }
}

/// The child is allowed to read/write binary data only on stdin/stdout.
/// Send diagnostics to stderr. This backend holds both pipe handles for
/// its whole lifetime; no spawning or loading weights in a simulation tick.
pub struct ResidentPolicyProcess {
    child: Child,
    input: BufWriter<ChildStdin>,
    output: BufReader<ChildStdout>,
    sequence: u64,
    unavailable: bool,
    // Reused model route/action-mask packet, 16 bytes per agent.
    row_headers: Vec<u8>,
}

impl ResidentPolicyProcess {
    /// Supply a Command with script and model manifest arguments configured.
    /// The implementation forces binary pipes and inherited stderr.
    pub fn spawn(mut command: Command) -> Result<Self, PolicyProcessError> {
        command.stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit());
        let mut child = command.spawn()?;
        let input = child.stdin.take()
            .ok_or(PolicyProcessError::MissingPipe("stdin"))?;
        let output = child.stdout.take()
            .ok_or(PolicyProcessError::MissingPipe("stdout"))?;
        let mut backend = Self {
            child,
            input: BufWriter::new(input),
            output: BufReader::new(output),
            sequence: 0,
            unavailable: false,
            row_headers: Vec::new(),
        };
        backend.read_handshake()?;
        Ok(backend)
    }

    pub fn python_worker(
        python: impl AsRef<OsStr>,
        script: impl AsRef<OsStr>,
        manifest: impl AsRef<OsStr>,
    ) -> Result<Self, PolicyProcessError> {
        let mut command = Command::new(python);
        command.arg(script).arg("--manifest").arg(manifest);
        Self::spawn(command)
    }

    fn read_handshake(&mut self) -> Result<(), PolicyProcessError> {
        let mut header = [0_u8; 14];
        self.output.read_exact(&mut header)?;
        if &header[..4] != READY_MAGIC ||
            u32::from_le_bytes(header[4..8].try_into().unwrap())
                != PROTOCOL_VERSION
        {
            return Err(PolicyProcessError::InvalidHandshake);
        }
        let abi = u32::from_le_bytes(header[8..12].try_into().unwrap());
        let size = usize::from(u16::from_le_bytes(
            header[12..14].try_into().unwrap(),
        ));
        if size == 0 || size > 8192 {
            return Err(PolicyProcessError::InvalidHandshake);
        }
        let mut signature = vec![0_u8; size];
        self.output.read_exact(&mut signature)?;
        let signature = std::str::from_utf8(&signature)
            .map_err(|_| PolicyProcessError::InvalidHandshake)?;
        validate_model_abi(abi, signature).map_err(PolicyProcessError::Abi)
    }

    fn exchange(
        &mut self,
        routes: &[NativePolicyRoute],
        observations: &[f32],
        masks: &[PolicyActionMask],
        continuous: &mut [f32],
        discrete: &mut [u8],
    ) -> Result<(), PolicyProcessError> {
        let rows = routes.len();
        let rows32 = u32::try_from(rows)
            .map_err(|_| PolicyProcessError::InvalidBatch)?;
        if observations.len() != rows.checked_mul(OBSERVATION_FLOATS)
                .ok_or(PolicyProcessError::InvalidBatch)? ||
            masks.len() != rows ||
            continuous.len() != rows.checked_mul(CONTINUOUS_ACTIONS)
                .ok_or(PolicyProcessError::InvalidBatch)? ||
            discrete.len() != rows.checked_mul(DISCRETE_ACTIONS)
                .ok_or(PolicyProcessError::InvalidBatch)?
        {
            return Err(PolicyProcessError::InvalidBatch);
        }
        if rows == 0 {
            return Ok(());
        }
        let seq = self.sequence.checked_add(1)
            .ok_or(PolicyProcessError::RequestSequenceExhausted)?;
        self.sequence = seq;
        self.input.write_all(REQUEST_MAGIC)?;
        self.input.write_all(&PROTOCOL_VERSION.to_le_bytes())?;
        self.input.write_all(&seq.to_le_bytes())?;
        self.input.write_all(&rows32.to_le_bytes())?;
        // One contiguous 16-byte route/mask header per agent. No per-row
        // write calls or heap allocation after the maximum batch grows.
        let header_bytes = rows.checked_mul(PER_ROW_HEADER_BYTES)
            .ok_or(PolicyProcessError::InvalidBatch)?;
        self.row_headers.resize(header_bytes, 0);
        for (index, (route, mask)) in routes.iter().zip(masks).enumerate() {
            if !mask.valid() {
                return Err(PolicyProcessError::InvalidBatch);
            }
            let header = &mut self.row_headers[
                index * PER_ROW_HEADER_BYTES..(index + 1) * PER_ROW_HEADER_BYTES
            ];
            header[..4].copy_from_slice(&route.model_slot.to_le_bytes());
            header[4] = route.team_id;
            for (output, &enabled) in header[5..10].iter_mut().zip(mask.fire.iter()) {
                *output = u8::from(enabled);
            }
            for (output, &enabled) in header[10..15].iter_mut().zip(mask.special.iter()) {
                *output = u8::from(enabled);
            }
            header[15] = 0;
        }
        self.input.write_all(&self.row_headers)?;
        write_floats(&mut self.input, observations)?;
        self.input.flush()?;

        let mut response = [0_u8; 20];
        self.output.read_exact(&mut response)?;
        if &response[0..4] != RESPONSE_MAGIC ||
            u32::from_le_bytes(response[4..8].try_into().unwrap())
                != PROTOCOL_VERSION ||
            u64::from_le_bytes(response[8..16].try_into().unwrap()) != seq ||
            u32::from_le_bytes(response[16..20].try_into().unwrap()) != rows32
        {
            return Err(PolicyProcessError::WrongResponse);
        }
        read_floats(&mut self.output, continuous)?;
        self.output.read_exact(discrete)?;
        Ok(())
    }

    pub fn is_available(&self) -> bool { !self.unavailable }
}

impl NativePolicyInference for ResidentPolicyProcess {
    type Error = PolicyProcessError;

    fn forward(
        &mut self,
        keys: &[PolicyAgentKey],
        routes: &[NativePolicyRoute],
        observations: &[f32],
        action_masks: &[PolicyActionMask],
        continuous: &mut [f32],
        discrete: &mut [u8],
    ) -> Result<(), Self::Error> {
        if self.unavailable {
            return Err(PolicyProcessError::Unavailable);
        }
        if keys.len() != routes.len() {
            return Err(PolicyProcessError::InvalidBatch);
        }
        // A malformed response or partial write poisons the stream.
        // Recreate the backend and model session; do not retry the tick.
        match self.exchange(
            routes, observations, action_masks, continuous, discrete,
        ) {
            Ok(()) => Ok(()),
            Err(error) => {
                self.unavailable = true;
                Err(error)
            }
        }
    }
}

impl Drop for ResidentPolicyProcess {
    fn drop(&mut self) {
        // The subprocess may be blocked on a partial frame or accelerator.
        // Do not leave orphaned policy workers when the game shuts down.
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

// Little-endian x86-64/ARM64 has a zero-copy, aligned slice view. Big
// endian hosts transcode floats rather than quietly corrupting a model.
fn write_floats<W: Write>(writer: &mut W, values: &[f32])
    -> io::Result<()>
{
    #[cfg(target_endian = "little")]
    {
        writer.write_all(bytemuck::cast_slice(values))
    }
    #[cfg(target_endian = "big")]
    {
        for value in values {
            writer.write_all(&value.to_le_bytes())?;
        }
        Ok(())
    }
}

fn read_floats<R: Read>(reader: &mut R, values: &mut [f32])
    -> io::Result<()>
{
    #[cfg(target_endian = "little")]
    {
        reader.read_exact(bytemuck::cast_slice_mut(values))
    }
    #[cfg(target_endian = "big")]
    {
        let mut bytes = [0_u8; 4];
        for value in values {
            reader.read_exact(&mut bytes)?;
            *value = f32::from_le_bytes(bytes);
        }
        Ok(())
    }
}
