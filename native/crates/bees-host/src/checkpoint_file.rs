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
use std::fs::{self, File, OpenOptions, TryLockError};
use std::io::{self, BufReader, BufWriter, Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use bees_hivemind::connection::{
    NativeConnectionCheckpoint, NativeHiveMindConnection,
};
use bees_hivemind::protocol::HiveMindProtocolError;
use bees_gameplay::level_rules::NativeLevelWorldRecoveryCheckpoint;
use crate::runtime::NativeHostRecoveryCheckpoint;

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

/// Cross-platform, crash-released writer ownership for HiveMind and native
/// Worlds. The stable lock inode is NEVER removed: deleting a locked file
/// could allow another process to lock a replacement inode concurrently.
/// File::try_lock uses OS locks on Windows and Unix (Rust 1.89+).
///
/// The old create_new sentinel cannot share ownership safely with OS locks.
/// Leave a permanent, versioned sentinel at the historical filename to
/// prevent older binaries from opening a parallel unprotected writer.
/// A legacy pid= sentinel requires explicit one-time owner confirmation.
pub struct ConnectionCheckpointWriterLease {
    _file: File,
}

impl ConnectionCheckpointWriterLease {
    pub fn acquire(checkpoint: &Path) -> Result<Self, CheckpointFileError> {
        if checkpoint.file_name().is_none() {
            return Err(CheckpointFileError::InvalidPath);
        }
        let mut name = OsString::from(checkpoint.as_os_str());
        name.push(".writer.lease");
        let lease_path = PathBuf::from(name);
        match fs::symlink_metadata(&lease_path) {
            Ok(metadata) if !metadata.file_type().is_file() =>
                return Err(CheckpointFileError::InvalidPath),
            Ok(_) => {},
            Err(error) if error.kind() == io::ErrorKind::NotFound => {},
            Err(error) => return Err(error.into()),
        }
        // This file persists after normal exit and after crashes. Opening
        // read+write also permits a real exclusive LockFileEx on Windows.
        let file = OpenOptions::new().read(true).write(true).create(true)
            .open(&lease_path)?;
        match file.try_lock() {
            Ok(()) => {},
            Err(TryLockError::WouldBlock) =>
                return Err(CheckpointFileError::Io(io::Error::new(
                    io::ErrorKind::WouldBlock,
                    format!("native checkpoint already has an active writer: {}",
                        checkpoint.display()),
                ))),
            Err(TryLockError::Error(error)) => return Err(error.into()),
        }

        let mut marker_name = OsString::from(checkpoint.as_os_str());
        marker_name.push(".writer.lock");
        let marker_path = PathBuf::from(marker_name);
        const OS_OWNED_MARKER: &str = "native-os-writer-v1\n";
        match fs::symlink_metadata(&marker_path) {
            Ok(metadata) if !metadata.file_type().is_file() =>
                return Err(CheckpointFileError::InvalidPath),
            Ok(_) => {
                let mut marker = String::new();
                File::open(&marker_path)?.take(64).read_to_string(&mut marker)?;
                if marker != OS_OWNED_MARKER {
                    return Err(CheckpointFileError::Io(io::Error::new(
                        io::ErrorKind::AlreadyExists,
                        format!(
                            "legacy HiveMind checkpoint writer marker ({}) requires verification that the old process stopped before its one-time removal",
                            marker_path.display(),
                        ),
                    )));
                }
            },
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                let mut marker = OpenOptions::new().write(true).create_new(true)
                    .open(&marker_path)?;
                marker.write_all(OS_OWNED_MARKER.as_bytes())?;
                marker.sync_all()?;
            },
            Err(error) => return Err(error.into()),
        }
        Ok(Self { _file: file })
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

/// Exclusive, crash-safe writer for the complete host cohort (all native
/// Worlds, policy driver and exact network/model binding). Never overwrite
/// a corrupt checkpoint with fresh gameplay during recovery.
pub struct NativeHostCheckpointFile {
    path: PathBuf,
    _writer_lease: ConnectionCheckpointWriterLease,
}

impl NativeHostCheckpointFile {
    pub fn open(path: impl Into<PathBuf>) -> Result<Self, CheckpointFileError> {
        let path = canonical_writer_checkpoint_path(path.into())?;
        let writer_lease = ConnectionCheckpointWriterLease::acquire(&path)?;
        Ok(Self { path, _writer_lease: writer_lease })
    }

    pub fn path(&self) -> &Path { &self.path }

    pub fn load(&self) -> Result<Option<NativeHostRecoveryCheckpoint>, CheckpointFileError> {
        match File::open(&self.path) {
            Ok(file) => Ok(Some(serde_json::from_reader(BufReader::new(file))?)),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(None),
            Err(error) => Err(error.into()),
        }
    }

    pub fn persist(
        &self, checkpoint: &NativeHostRecoveryCheckpoint,
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
