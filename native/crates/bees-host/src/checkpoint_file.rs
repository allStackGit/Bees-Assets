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

static NEXT_TEMP_FILE: AtomicU64 = AtomicU64::new(0);
const MAX_TEMP_NAME_ATTEMPTS: usize = 64;

#[derive(Debug)]
pub enum CheckpointFileError {
    InvalidPath,
    TemporaryNameExhausted,
    Io(io::Error),
    Json(serde_json::Error),
    Protocol(HiveMindProtocolError),
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
        let parent = self.path.parent()
            .filter(|parent| !parent.as_os_str().is_empty())
            .unwrap_or_else(|| Path::new("."));
        let name = self.path.file_name().ok_or(CheckpointFileError::InvalidPath)?;
        let (temp_path, file) = create_unique_temporary_file(parent, name)?;
        let mut renamed = false;
        let result = (|| -> Result<(), CheckpointFileError> {
            let mut writer = BufWriter::new(file);
            connection.write_checkpoint(&mut writer)?;
            writer.flush()?;
            let file = writer.into_inner()
                .map_err(|error| error.into_error())?;
            file.sync_all()?;
            drop(file);
            fs::rename(&temp_path, &self.path)?;
            renamed = true;
            // The rename's directory entry also needs to survive a crash
            // on Unix. On Windows, portable std::fs has no such API.
            #[cfg(unix)]
            File::open(parent)?.sync_all()?;
            Ok(())
        })();
        if !renamed {
            // Preserve the earlier committed checkpoint after any failure.
            // Best effort: a stale temporary file is never a valid restore.
            let _ = fs::remove_file(&temp_path);
        }
        result
    }
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
