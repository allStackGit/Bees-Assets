//! Optional TLS for the existing newline-delimited native multiplayer wire.
//!
//! Both headless hosts and the native rendered client use this identical
//! nonblocking transport. The physical World, command ordering, replay,
//! checkpoints and authentication remain owned by their existing modules.
//! All certificates and keys are DER; no extra PEM parser or TLS proxy is
//! required. This module never accepts invalid certificates or bypasses
//! hostname validation.
use std::env;
use std::fs;
use std::io::{self, ErrorKind, Read, Write};
use std::net::{Shutdown, TcpStream};
use std::path::Path;
use std::sync::Arc;
use rustls::{ClientConfig, ClientConnection, Connection, RootCertStore, ServerConfig, ServerConnection};
use rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer, ServerName};

const MAX_CREDENTIAL_BYTES: u64 = 64 * 1024;
const MAX_TLS_PACKET_WRITE: usize = 256 * 1024;

fn invalid(error: impl std::fmt::Display) -> io::Error {
    io::Error::new(ErrorKind::InvalidInput, error.to_string())
}

fn private_file(path: &Path, private: bool) -> io::Result<Vec<u8>> {
    let meta = fs::symlink_metadata(path)?;
    if !meta.is_file() || meta.file_type().is_symlink() ||
        meta.len() == 0 || meta.len() > MAX_CREDENTIAL_BYTES {
        return Err(invalid("native TLS material must be a bounded regular file"));
    }
    #[cfg(unix)]
    if private {
        use std::os::unix::fs::PermissionsExt;
        if meta.permissions().mode() & 0o077 != 0 {
            return Err(io::Error::new(ErrorKind::PermissionDenied,
                "native TLS private key must have owner-only permissions"));
        }
    }
    let content = fs::read(path)?;
    if content.len() as u64 != meta.len() {
        return Err(invalid("native TLS material changed while loading"));
    }
    Ok(content)
}

/// Read a self-managed server identity only when BOTH DER paths are set.
/// No TLS decision is made from an untrusted peer's Hello packet.
pub fn server_tls_from_environment() -> io::Result<Option<Arc<ServerConfig>>> {
    let certificate = env::var_os("BEES_NATIVE_REMOTE_TLS_CERT_DER");
    let key = env::var_os("BEES_NATIVE_REMOTE_TLS_KEY_DER");
    let (Some(certificate), Some(key)) = (certificate, key) else {
        if env::var_os("BEES_NATIVE_REMOTE_TLS_CERT_DER").is_some() ||
            env::var_os("BEES_NATIVE_REMOTE_TLS_KEY_DER").is_some()
        {
            return Err(invalid("native TLS requires both DER server certificate and PKCS#8 private key"));
        }
        return Ok(None);
    };
    let certificate = CertificateDer::from(private_file(Path::new(&certificate), false)?);
    let key = PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(
        private_file(Path::new(&key), true)?,
    ));
    let config = ServerConfig::builder()
        .with_no_client_auth()
        .with_single_cert(vec![certificate], key)
        .map_err(invalid)?;
    Ok(Some(Arc::new(config)))
}

/// A TLS client requires an explicitly trusted DER CA. No insecure local-
/// development switch exists; server names are checked against the actual
/// host from the supplied endpoint, including IP address SANs.
#[cfg(feature = "client")]
pub fn client_tls_from_environment() -> io::Result<Arc<ClientConfig>> {
    let authority = env::var_os("BEES_NATIVE_REMOTE_TLS_CA_DER")
        .ok_or_else(|| invalid("TLS multiplayer requires BEES_NATIVE_REMOTE_TLS_CA_DER"))?;
    let mut roots = RootCertStore::empty();
    roots.add(CertificateDer::from(
        private_file(Path::new(&authority), false)?,
    )).map_err(invalid)?;
    Ok(Arc::new(ClientConfig::builder()
        .with_root_certificates(roots)
        .with_no_client_auth()))
}

/// Shared nonblocking TLS/plaintext adapter for the original JSON framing.
/// One pending TLS record must be written to the socket before another
/// application record can be queued. This bounds per-peer ciphertext even
/// when a remote spectator stops reading large world snapshots.
pub struct RemoteSocket {
    tcp: TcpStream,
    tls: Option<Connection>,
}

impl RemoteSocket {
    pub fn is_tls(&self) -> bool { self.tls.is_some() }

    /// A terminal result is not fully delivered just because its JSON
    /// bytes entered the TLS writer. Drain the resulting ciphertext first.
    pub fn has_pending_output(&self) -> bool {
        self.tls.as_ref().is_some_and(|connection| connection.wants_write())
    }

    pub fn plain(tcp: TcpStream) -> Self {
        Self { tcp, tls: None }
    }

    pub fn server(tcp: TcpStream, config: Arc<ServerConfig>) -> io::Result<Self> {
        let connection = ServerConnection::new(config).map_err(invalid)?;
        Ok(Self { tcp, tls: Some(Connection::Server(connection)) })
    }

    #[cfg(feature = "client")]
    pub fn client(tcp: TcpStream, config: Arc<ClientConfig>, hostname: &str)
        -> io::Result<Self>
    {
        let name = ServerName::try_from(hostname.to_owned()).map_err(invalid)?;
        let connection = ClientConnection::new(config, name).map_err(invalid)?;
        Ok(Self { tcp, tls: Some(Connection::Client(connection)) })
    }

    pub fn shutdown(&self, direction: Shutdown) -> io::Result<()> {
        self.tcp.shutdown(direction)
    }

    pub fn set_nonblocking(&self, on: bool) -> io::Result<()> {
        self.tcp.set_nonblocking(on)
    }

    /// Rendered clients complete their initial handshake off the UI thread
    /// using an existing bounded connect timeout, before sending a token.
    #[cfg(feature = "client")]
    pub fn handshake_blocking(&mut self) -> io::Result<()> {
        if let Some(tls) = self.tls.as_mut() {
            while tls.is_handshaking() {
                tls.complete_io(&mut self.tcp)?;
            }
        }
        Ok(())
    }

    fn flush_ciphertext(tcp: &mut TcpStream, tls: &mut Connection) -> io::Result<()> {
        let mut budget = MAX_TLS_PACKET_WRITE;
        while budget > 0 && tls.wants_write() {
            match tls.write_tls(tcp) {
                Ok(0) => return Err(io::Error::new(
                    ErrorKind::WriteZero, "native TLS peer stopped accepting ciphertext")),
                Ok(bytes) => budget = budget.saturating_sub(bytes),
                Err(error) if error.kind() == ErrorKind::WouldBlock => break,
                Err(error) if error.kind() == ErrorKind::Interrupted => continue,
                Err(error) => return Err(error),
            }
        }
        Ok(())
    }
}

impl Read for RemoteSocket {
    fn read(&mut self, out: &mut [u8]) -> io::Result<usize> {
        let Some(tls) = self.tls.as_mut() else { return self.tcp.read(out) };
        // Handshake writes and ACK frames must progress even if the World
        // is paused. The bounded fixed-tick poll already imposes admission
        // and timeout limits on unauthenticated connections.
        Self::flush_ciphertext(&mut self.tcp, tls)?;
        if out.is_empty() { return Ok(0); }
        match tls.reader().read(out) {
            Ok(n) if n > 0 => return Ok(n),
            Ok(0) => return Ok(0),
            Err(error) if error.kind() == ErrorKind::WouldBlock => {},
            Err(error) => return Err(error),
            _ => {},
        }
        let received = match tls.read_tls(&mut self.tcp) {
            Ok(0) => return Ok(0),
            Ok(n) => n,
            Err(error) => return Err(error),
        };
        if received > 0 {
            tls.process_new_packets()
                .map_err(|error| io::Error::new(ErrorKind::InvalidData, error))?;
            Self::flush_ciphertext(&mut self.tcp, tls)?;
        }
        tls.reader().read(out)
    }
}

impl Write for RemoteSocket {
    fn write(&mut self, data: &[u8]) -> io::Result<usize> {
        let Some(tls) = self.tls.as_mut() else { return self.tcp.write(data) };
        Self::flush_ciphertext(&mut self.tcp, tls)?;
        if tls.is_handshaking() || tls.wants_write() {
            return Err(io::Error::from(ErrorKind::WouldBlock));
        }
        if data.is_empty() { return Ok(0); }
        // The writer admits plaintext only after the preceding encrypted
        // record is drained. A socket WouldBlock AFTER acceptance must not
        // cause the authoritative caller to resend already queued bytes.
        let consumed = tls.writer().write(data)?;
        if consumed > 0 {
            let _ = Self::flush_ciphertext(&mut self.tcp, tls);
        }
        Ok(consumed)
    }

    fn flush(&mut self) -> io::Result<()> {
        let Some(tls) = self.tls.as_mut() else { return self.tcp.flush() };
        Self::flush_ciphertext(&mut self.tcp, tls)
    }
}
