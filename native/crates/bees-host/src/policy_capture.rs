use std::fs::{File, OpenOptions};
use std::io::{self, BufWriter, Read, Write};
use sha2::{Digest, Sha256};
use serde::{Serialize, Deserialize};
use std::path::Path;

use bees_gameplay::inference::{BEHAVIOR_STATS_PER_ROW, NativeLevelInferenceBatch};
use bees_gameplay::level_host::NativeTerminalObservation;
use bees_sim::policy::{CONTINUOUS_ACTIONS, DISCRETE_BRANCHES, OBSERVATION_FLOATS};

use crate::policy_process::write_floats;

/// Applied policy input/output stream. Rewards and terminal outcomes remain
/// in the paired notification journal identified by its journal ID.
/// An exact durable prefix of the existing BEESACT3 action stream.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct PolicyCaptureCheckpoint {
    schema_version: u32,
    bytes: u64,
    rows: u64,
    sha256: [u8; 32],
}

/// Digest the same binary bytes that the learner consumes, including the
/// header. Buffered writes update the hash as accepted by the buffer;
/// checkpoint() first flushes and fsyncs before publishing that digest.
struct CaptureWriter {
    inner: BufWriter<File>,
    digest: Sha256,
}

impl Write for CaptureWriter {
    fn write(&mut self, data: &[u8]) -> io::Result<usize> {
        let accepted = self.inner.write(data)?;
        self.digest.update(&data[..accepted]);
        Ok(accepted)
    }

    fn flush(&mut self) -> io::Result<()> { self.inner.flush() }
}

pub(crate) struct PolicyCapture {
    writer: CaptureWriter,
    rows: u64,
}

impl PolicyCapture {
    pub(crate) fn create(path: &Path, journal_id: &str) -> io::Result<Self> {
        let file = OpenOptions::new().create_new(true).write(true).open(path)?;
        let mut writer = CaptureWriter {
            inner: BufWriter::with_capacity(256 * 1024, file),
            digest: Sha256::new(),
        };
        writer.write_all(b"BEESACT3")?;
        writer.write_all(journal_id.as_bytes())?;
        Ok(Self { writer, rows: 0 })
    }

    /// Restore a previously acknowledged capture prefix only. Extra bytes
    /// may belong to a later physical action; never truncate or merge them
    /// silently with the World at an earlier host checkpoint.
    pub(crate) fn open_existing(
        path: &Path, journal_id: &str, saved: &PolicyCaptureCheckpoint,
    ) -> io::Result<Self> {
        if saved.schema_version != 1 || journal_id.len() != 32 || saved.bytes < 40 {
            return Err(io::Error::new(io::ErrorKind::InvalidData,
                "invalid native policy capture checkpoint"));
        }
        let mut file = File::open(path)?;
        if file.metadata()?.len() != saved.bytes {
            return Err(io::Error::new(io::ErrorKind::InvalidData,
                "native policy capture size differs from its host checkpoint"));
        }
        let mut header = [0_u8; 40];
        file.read_exact(&mut header)?;
        if &header[..8] != b"BEESACT3" || &header[8..] != journal_id.as_bytes() {
            return Err(io::Error::new(io::ErrorKind::InvalidData,
                "native policy capture does not belong to the recovered journal"));
        }
        let mut digest = Sha256::new();
        digest.update(header);
        let mut buffer = [0_u8; 64 * 1024];
        loop {
            let size = file.read(&mut buffer)?;
            if size == 0 { break; }
            digest.update(&buffer[..size]);
        }
        if <[u8; 32]>::from(digest.clone().finalize()) != saved.sha256 {
            return Err(io::Error::new(io::ErrorKind::InvalidData,
                "native policy capture differs from its committed host checkpoint"));
        }
        Ok(Self {
            writer: CaptureWriter {
                inner: BufWriter::with_capacity(256 * 1024,
                    OpenOptions::new().append(true).open(path)?),
                digest,
            },
            rows: saved.rows,
        })
    }

    pub(crate) fn checkpoint(&mut self) -> io::Result<PolicyCaptureCheckpoint> {
        self.writer.flush()?;
        self.writer.inner.get_ref().sync_all()?;
        Ok(PolicyCaptureCheckpoint {
            schema_version: 1,
            bytes: self.writer.inner.get_ref().metadata()?.len(),
            rows: self.rows,
            sha256: self.writer.digest.clone().finalize().into(),
        })
    }

    pub(crate) fn validate_checkpoint(
        &self, saved: &PolicyCaptureCheckpoint,
    ) -> io::Result<()> {
        if saved.schema_version != 1 || saved.rows != self.rows ||
            saved.bytes != self.writer.inner.get_ref().metadata()?.len() ||
            saved.sha256 != <[u8; 32]>::from(self.writer.digest.clone().finalize())
        {
            return Err(io::Error::new(io::ErrorKind::InvalidData,
                "native policy capture checkpoint changed"));
        }
        Ok(())
    }

    pub(crate) fn write_terminal(
        &mut self, world: u64, episode: u64, tick: u64, rows: &[NativeTerminalObservation],
    ) -> io::Result<()> {
        if rows.is_empty() { return Ok(()); }
        let count = u32::try_from(rows.len()).map_err(io::Error::other)?;
        if count > 65536 {
            return Err(io::Error::other("too many terminal policy observations"));
        }
        self.writer.write_all(&(count | (1 << 30)).to_le_bytes())?;
        self.writer.write_all(&0_u64.to_le_bytes())?;
        for row in rows {
            for field in [world, episode, tick, row.ship_id as u64, row.binding_revision, row.group_agent] {
                self.writer.write_all(&field.to_le_bytes())?;
            }
            write_floats(&mut self.writer, &row.features)?;
        }
        Ok(())
    }

    pub(crate) fn write_batch(&mut self, batch: &NativeLevelInferenceBatch) -> io::Result<()> {
        let (masks, continuous, discrete) = batch.validated_policy_outputs()
            .ok_or_else(|| io::Error::other("missing validated policy outputs"))?;
        let generation = batch.measurements().last_model_generation
            .ok_or_else(|| io::Error::other("missing applied model generation"))?;
        let rows = u32::try_from(batch.keys().len()).map_err(io::Error::other)?;
        let behavior = batch.behavior_statistics();
        let frame_rows = rows | if behavior.is_empty() { 0 } else { 1 << 31 };
        let writer = &mut self.writer;
        writer.write_all(&frame_rows.to_le_bytes())?;
        writer.write_all(&generation.to_le_bytes())?;
        for (row, key) in batch.keys().iter().enumerate() {
            let route = batch.routes()[row];
            for field in [key.world.0, key.episode, key.tick, key.agent,
                batch.binding_revisions()[row], batch.group_agent_ids()[row].unwrap_or(0)]
            {
                writer.write_all(&field.to_le_bytes())?;
            }
            writer.write_all(&route.model_slot.to_le_bytes())?;
            writer.write_all(&[route.team_id])?;
            writer.write_all(&masks[row].fire.map(u8::from))?;
            writer.write_all(&masks[row].special.map(u8::from))?;
            write_floats(writer, &batch.observations()
                [row * OBSERVATION_FLOATS..(row + 1) * OBSERVATION_FLOATS])?;
            write_floats(writer, &continuous
                [row * CONTINUOUS_ACTIONS..(row + 1) * CONTINUOUS_ACTIONS])?;
            writer.write_all(&discrete
                [row * DISCRETE_BRANCHES.len()..(row + 1) * DISCRETE_BRANCHES.len()])?;
            if !behavior.is_empty() {
                write_floats(writer, &behavior
                    [row * BEHAVIOR_STATS_PER_ROW..(row + 1) * BEHAVIOR_STATS_PER_ROW])?;
            }
        }
        self.rows += u64::from(rows);
        Ok(())
    }

    pub(crate) fn finish(mut self, expected_rows: u64) -> io::Result<u64> {
        if self.rows != expected_rows {
            return Err(io::Error::other("policy capture row count differs from applied rows"));
        }
        self.writer.write_all(&0_u32.to_le_bytes())?;
        self.writer.write_all(&self.rows.to_le_bytes())?;
        self.writer.flush()?;
        self.writer.inner.get_ref().sync_all()?;
        Ok(self.rows)
    }
}
