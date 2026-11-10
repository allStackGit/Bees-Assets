use std::fs::{File, OpenOptions};
use std::io::{self, BufWriter, Write};
use std::path::Path;

use bees_gameplay::inference::{BEHAVIOR_STATS_PER_ROW, NativeLevelInferenceBatch};
use bees_sim::policy::{CONTINUOUS_ACTIONS, DISCRETE_BRANCHES, OBSERVATION_FLOATS};

use crate::policy_process::write_floats;

/// Applied policy input/output stream. Rewards and terminal outcomes remain
/// in the paired notification journal identified by its journal ID.
pub(crate) struct PolicyCapture {
    writer: BufWriter<File>,
    rows: u64,
}

impl PolicyCapture {
    pub(crate) fn create(path: &Path, journal_id: &str) -> io::Result<Self> {
        let file = OpenOptions::new().create_new(true).write(true).open(path)?;
        let mut writer = BufWriter::with_capacity(256 * 1024, file);
        writer.write_all(b"BEESACT2")?;
        writer.write_all(journal_id.as_bytes())?;
        Ok(Self { writer, rows: 0 })
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
        self.writer.get_ref().sync_all()?;
        Ok(self.rows)
    }
}
