//! Compact policy action playback for an offline battle trace.

use std::fs::{File, OpenOptions};
use std::io::{self, BufReader, BufWriter, Read, Seek, Write};
use std::path::Path;

use bees_gameplay::inference::NativePolicyRoute;
use bees_sim::policy_batch::{PolicyActionMask, PolicyAgentKey};
use sha2::{Digest, Sha256};

use crate::policy_process::{read_floats, write_floats};

const MAGIC: &[u8; 8] = b"BEESPLY1";

fn batch_digest(
    keys: &[PolicyAgentKey], routes: &[NativePolicyRoute], observations: &[f32],
    masks: &[PolicyActionMask],
) -> [u8; 32] {
    let mut hash = Sha256::new();
    for ((key, route), mask) in keys.iter().zip(routes).zip(masks) {
        for field in [key.world.0, key.episode, key.tick, key.agent] {
            hash.update(field.to_le_bytes());
        }
        hash.update(route.model_slot.to_le_bytes());
        hash.update([route.team_id]);
        hash.update(mask.fire.map(u8::from));
        hash.update(mask.special.map(u8::from));
    }
    #[cfg(target_endian = "little")]
    hash.update(bytemuck::cast_slice::<f32, u8>(observations));
    #[cfg(target_endian = "big")]
    for value in observations { hash.update(value.to_le_bytes()); }
    hash.finalize().into()
}

fn file_digest(reader: &mut impl Read) -> io::Result<String> {
    let mut hash = Sha256::new();
    io::copy(reader, &mut hash)?;
    Ok(format!("{:x}", hash.finalize()))
}

pub struct PolicyActionRecorder(BufWriter<File>);

impl PolicyActionRecorder {
    pub fn create(path: &Path) -> io::Result<Self> {
        let file = OpenOptions::new().read(true).write(true).create_new(true).open(path)?;
        let mut writer = BufWriter::new(file);
        writer.write_all(MAGIC)?;
        Ok(Self(writer))
    }

    pub fn record(
        &mut self, keys: &[PolicyAgentKey], routes: &[NativePolicyRoute],
        observations: &[f32], masks: &[PolicyActionMask], continuous: &[f32], discrete: &[u8],
    ) -> io::Result<()> {
        self.0.write_all(&batch_digest(keys, routes, observations, masks))?;
        write_floats(&mut self.0, continuous)?;
        self.0.write_all(discrete)
    }

    pub fn finish(mut self) -> io::Result<String> {
        self.0.flush()?;
        self.0.get_ref().sync_all()?;
        let mut file = self.0.into_inner().map_err(|error| error.into_error())?;
        file.rewind()?;
        file_digest(&mut file)
    }
}

pub struct PolicyActionPlayback(BufReader<File>);

impl PolicyActionPlayback {
    pub fn open(path: &Path, expected_sha256: &str) -> io::Result<Self> {
        let mut file = File::open(path)?;
        if file_digest(&mut file)? != expected_sha256 {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "policy action trace digest differs"));
        }
        file.rewind()?;
        let mut reader = BufReader::new(file);
        let mut magic = [0; MAGIC.len()];
        reader.read_exact(&mut magic)?;
        if &magic != MAGIC {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "invalid policy action trace header"));
        }
        Ok(Self(reader))
    }

    pub fn apply(
        &mut self, keys: &[PolicyAgentKey], routes: &[NativePolicyRoute],
        observations: &[f32], masks: &[PolicyActionMask], continuous: &mut [f32], discrete: &mut [u8],
    ) -> io::Result<()> {
        let mut expected = [0; 32];
        self.0.read_exact(&mut expected)?;
        if expected != batch_digest(keys, routes, observations, masks) {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "policy playback diverged at an observation batch"));
        }
        read_floats(&mut self.0, continuous)?;
        self.0.read_exact(discrete)
    }

    pub fn finish(&mut self) -> io::Result<()> {
        if self.0.read(&mut [0_u8; 1])? != 0 {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "policy playback left recorded actions unused"));
        }
        Ok(())
    }
}
