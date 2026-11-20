//! Atomic on-disk persistence for the one shared HiveMind connection.
//!
//! The socket owner MUST persist after allocating a request hash and BEFORE
//! transmitting its request. It must also persist after settling a server
//! acknowledgement or finishing 409 reconciliation. Missing/corrupt state
//! must never be replaced on error: silently resetting the allocator could
//! reuse a previously transmitted request ID.
//!
//! A path has exactly one active writer. Cross-process coordination, sockets,
//! server authentication, outcome attribution and Level notification delivery
//! deliberately remain the responsibility of the consuming application.

use std::ffi::OsString;
use std::fs::{self, File, OpenOptions};
use std::io::{self, BufReader, BufWriter, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use bees_hivemind::connection::{
    NativeConnectionCheckpoint, NativeHiveMindConnection,
};
use bees_hivemind::protocol::HiveMindProtocolError;
use bees_gameplay::level_rules::NativeLevelWorldRecoveryCheckpoint;

static NEXT_TEMP_FILE: AtomicU64 = AtomicU64::new(0);
const MAX_TEMP_NAME_ATTEMPTS: usize = 64;

#[derive(Debug)]
pub enum CheckpointFileError {
    InvalidPath,
    TemporaryNameExhausted,
    Io(io::Error),
    Json(serde_json::Error),
    Protocol(HiveMindProtocolError),
    Entropy(getrandom::Error),
}

impl From<io::Error> for CheckpointFileError {
    fn from(error: io::Error) -> Self { Self::Io(error) }
}

impl From<serde_json::Error> for CheckpointFileError {
    fn from(error: serde_json::Error) -> Self { Self::Json(error) }
}

impl From<HiveMindProtocolError> for CheckpointFileError {
    fn from(error: HiveMindProtocolError) -> Self { Self::Protocol(error) }
}

/// Normalize only the owned checkpoint's parent directory. A symlinked
/// directory or "./" alias must not create an independent writer lock for
/// the same connection. File symlinks are forbidden: atomic rename would
/// replace the link rather than its target and split the persisted identity.
pub fn canonical_writer_checkpoint_path(
    path: PathBuf,
) -> Result<PathBuf, CheckpointFileError> {
    let file_name = path.file_name()
        .ok_or(CheckpointFileError::InvalidPath)?;
    let parent = path.parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));
    let target = fs::canonicalize(parent)?.join(file_name);
    match fs::symlink_metadata(&target) {
        Ok(metadata) if !metadata.file_type().is_file() =>
            Err(CheckpointFileError::InvalidPath),
        Ok(_) => Ok(target),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(target),
        Err(error) => Err(error.into()),
    }
}

/// One active checkpoint writer across native games and offline review.
/// Mirrors the campaign-writer lease. A crash leaves an explicit lock
/// requiring confirmation that the original process has stopped.
pub struct ConnectionCheckpointWriterLease {
    path: PathBuf,
    file: Option<File>,
}

impl ConnectionCheckpointWriterLease {
    pub fn acquire(checkpoint: &Path) -> Result<Self, CheckpointFileError> {
        if checkpoint.file_name().is_none() {
            return Err(CheckpointFileError::InvalidPath);
        }
        let mut name = OsString::from(checkpoint.as_os_str());
        name.push(".writer.lock");
        let path = PathBuf::from(name);
        let mut file = match OpenOptions::new().write(true)
            .create_new(true).open(&path)
        {
            Ok(file) => file,
            Err(error) if error.kind() == io::ErrorKind::AlreadyExists =>
                return Err(CheckpointFileError::Io(io::Error::new(
                    io::ErrorKind::AlreadyExists,
                    format!("HiveMind checkpoint is already owned ({}); verify the previous owner has stopped before removing a stale writer lock", path.display()),
                ))),
            Err(error) => return Err(error.into()),
        };
        if let Err(error) = writeln!(file, "pid={}", std::process::id())
            .and_then(|_| file.sync_all())
        {
            drop(file);
            let _ = fs::remove_file(&path);
            return Err(error.into());
        }
        Ok(Self { path, file: Some(file) })
    }
}

impl Drop for ConnectionCheckpointWriterLease {
    fn drop(&mut self) {
        drop(self.file.take());
        let _ = fs::remove_file(&self.path);
    }
}

/// A disk location, not a second connection ledger or automatic retry loop.
/// Use the same path for the full lifetime of one client identity.
#[derive(Clone, Debug)]
pub struct ConnectionCheckpointFile {
    path: PathBuf,
}

impl ConnectionCheckpointFile {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }

    pub fn path(&self) -> &Path { &self.path }

    /// Load an existing durable allocator and StoreCommands retry ledger.
    /// None means no committed checkpoint exists. Invalid JSON, a wrong
    /// schema, or an inconsistent request-hash owner is an ERROR, not None.
    /// In-flight strategy/targeting correlations intentionally do not revive.
    pub fn load(&self) -> Result<Option<NativeHiveMindConnection>, CheckpointFileError> {
        match File::open(&self.path) {
            Ok(file) => {
                let snapshot: NativeConnectionCheckpoint =
                    serde_json::from_reader(BufReader::new(file))?;
                Ok(Some(NativeHiveMindConnection::restore(snapshot)?))
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(None),
            Err(error) => Err(error.into()),
        }
    }

    /// Resume an existing identity or initialize and commit a fresh one.
    /// The caller must supply a cryptographically random valid 21-bit
    /// client prefix for new identities; it is ignored when resuming.
    /// Never use this to recover from a malformed checkpoint.
    pub fn open_or_initialize(
        &self,
        fresh_client_prefix: u32,
    ) -> Result<NativeHiveMindConnection, CheckpointFileError> {
        if let Some(connection) = self.load()? {
            return Ok(connection);
        }
        let connection = NativeHiveMindConnection::new(fresh_client_prefix)?;
        self.persist(&connection)?;
        Ok(connection)
    }

    /// Write the connection's canonical checkpoint without cloning its
    /// pending StoreCommands ledger. A successful return means the new
    /// JSON was flushed and synced and the same-directory rename succeeded.
    /// Unix also syncs the parent directory; Rust std cannot provide a
    /// portable Windows directory fsync, so power-loss guarantees remain
    /// filesystem/OS dependent. No request may be sent if this fails.
    pub fn persist(
        &self,
        connection: &NativeHiveMindConnection,
    ) -> Result<(), CheckpointFileError> {
        persist_atomic(&self.path, |writer| {
            connection.write_checkpoint(writer)?;
            Ok(())
        })
    }
}

/// The owning World and native Level have one durable fixed-step snapshot.
/// This is deliberately not a second combat journal. The host must also
/// recover the connection, resident model and authored source metadata
/// at the same boundary before it resumes simulation or accepts input.
#[derive(Clone, Debug)]
pub struct NativeLevelWorldCheckpointFile {
    path: PathBuf,
}

impl NativeLevelWorldCheckpointFile {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }

    pub fn path(&self) -> &Path { &self.path }

    /// A malformed or incomplete committed checkpoint is an error, not a
    /// new episode. The same exact writer-lease convention as HiveMind's
    /// checkpoint prevents competing native process owners.
    pub fn load(&self)
        -> Result<Option<NativeLevelWorldRecoveryCheckpoint>, CheckpointFileError>
    {
        match File::open(&self.path) {
            Ok(file) => Ok(Some(serde_json::from_reader(BufReader::new(file))?)),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(None),
            Err(error) => Err(error.into()),
        }
    }

    /// Flush, fsync and atomically rename the SAME World+Level ownership
    /// projection. Saving the model and connection is a distinct host
    /// responsibility; neither is silently inferred from this snapshot.
    pub fn persist(
        &self, checkpoint: &NativeLevelWorldRecoveryCheckpoint,
    ) -> Result<(), CheckpointFileError> {
        persist_atomic(&self.path, |writer| {
            serde_json::to_writer(writer, checkpoint)?;
            Ok(())
        })
    }
}

/// One durable checkpoint write path for both network connection identity
/// and native World+Level state. No extra file rewriting strategy or
/// duplicate temporary-file allocator exists for gameplay persistence.
fn persist_atomic(
    path: &Path,
    write: impl FnOnce(&mut BufWriter<File>) -> Result<(), CheckpointFileError>,
) -> Result<(), CheckpointFileError> {
    let parent = path.parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));
    let name = path.file_name().ok_or(CheckpointFileError::InvalidPath)?;
    let (temp_path, file) = create_unique_temporary_file(parent, name)?;
    let mut renamed = false;
    let result = (|| -> Result<(), CheckpointFileError> {
        let mut writer = BufWriter::new(file);
        write(&mut writer)?;
        writer.flush()?;
        let file = writer.into_inner()
            .map_err(|error| error.into_error())?;
        file.sync_all()?;
        drop(file);
        fs::rename(&temp_path, path)?;
        renamed = true;
        #[cfg(unix)]
        File::open(parent)?.sync_all()?;
        Ok(())
    })();
    if !renamed {
        let _ = fs::remove_file(&temp_path);
    }
    result
}

fn create_unique_temporary_file(
    parent: &Path,
    checkpoint_name: &std::ffi::OsStr,
) -> Result<(PathBuf, File), CheckpointFileError> {
    for _ in 0..MAX_TEMP_NAME_ATTEMPTS {
        let sequence = NEXT_TEMP_FILE.fetch_add(1, Ordering::Relaxed);
        let mut name = OsString::from(".");
        name.push(checkpoint_name);
        name.push(format!(".{}.{}.tmp", std::process::id(), sequence));
        let path = parent.join(name);
        match OpenOptions::new().write(true).create_new(true).open(&path) {
            Ok(file) => return Ok((path, file)),
            Err(error) if error.kind() == io::ErrorKind::AlreadyExists => continue,
            Err(error) => return Err(error.into()),
        }
    }
    Err(CheckpointFileError::TemporaryNameExhausted)
}
