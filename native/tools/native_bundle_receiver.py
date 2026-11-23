#!/usr/bin/env python3
"""Authenticated streaming transport for native distributed rollout bundles.

A learner owns issued orders and the canonical collector inbox. An upload is
accepted only for an active issued collector, verified against its manifest,
checkpoint, scene contract and every captured artifact digest, and atomically
published through the same import path as shared-storage collection.

No Python objects, models or untrusted tar paths are deserialized as code.
No separate experience format or second learner implementation exists.
Use a secure private network, SSH tunnel or HTTPS reverse proxy: this
standard-library receiver serves plain HTTP, not Internet-facing TLS.
"""
import hashlib
import hmac
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import http.client
import json
import os
from pathlib import Path
import re
import ssl
import tempfile
import tarfile
import threading
import time
from urllib.parse import urlsplit

from native_training_artifacts import file_sha256, sync_directory
from transfer_policy_bundle import import_bundle_archive, completed_uploaded_bundle, BUNDLE_NAME

CHUNK = 1024 * 1024
MAX_ARCHIVE_BYTES = 8 * 1024 ** 3
# The Hub can issue sixteen collectors in one round. Leave room for
# acknowledgement GETs alongside their streaming uploads/downloads.
MAX_CLIENTS = 20
MAX_DOWNLOAD_ATTEMPTS = 4
MAX_UPLOAD_ATTEMPTS = 4
TOKEN_ENV = "BEES_NATIVE_BUNDLE_TOKEN"
CA_ENV = "BEES_NATIVE_BUNDLE_CA_FILE"
DIGEST_RE = re.compile(r"[0-9a-f]{64}\Z")


def bundle_token(token_file=None):
    if token_file is None:
        token = os.environ.get(TOKEN_ENV)
    else:
        # A Hub-owned systemd/Windows service must not expose an
        # authentication secret in frozen job arguments, process lists or
        # the coordinator. Only this machine-local operator-owned file is
        # opened by the worker after the resource lease starts.
        source = Path(token_file).absolute()
        if source.is_symlink():
            raise ValueError("native bundle token file cannot be a symlink")
        source = source.resolve(strict=True)
        stat = source.stat()
        if not source.is_file() or stat.st_size > 512 or (
                os.name == "posix" and stat.st_mode & 0o077):
            raise ValueError("native bundle token file must be a private regular file")
        token = source.read_text(encoding="ascii").strip()
    if not isinstance(token, str) or not 32 <= len(token) <= 256 or any(
            character.isspace() or ord(character) < 33 or ord(character) > 126
            for character in token):
        raise ValueError("native bundle authentication needs a 32-256 character printable ASCII secret")
    return token


def bundle_connection_class(parts):
    """HTTPS validates the original receiver hostname and certificate.

    A machine-local private CA may be selected without disabling trust
    verification or changing issued training artifacts and model lineage.
    """
    if parts.scheme == "http":
        return http.client.HTTPConnection
    ca = os.environ.get(CA_ENV)
    if ca:
        source = Path(ca).absolute()
        if source.is_symlink() or not source.is_file():
            raise ValueError("native bundle trusted CA must be a regular file")
        context = ssl.create_default_context(cafile=str(source))
        return lambda host, port, timeout: http.client.HTTPSConnection(
            host, port, timeout=timeout, context=context,
        )
    return http.client.HTTPSConnection


def upload_bundle_archive(path, collector_id, base_url, token=None, archive_sha256=None):
    """Stream a verified portable archive with bounded recoverable retries.

    The collector keeps its already-completed local artifact. If a transport
    dies, query the receiver's durable receipt before another POST; never
    replay collection or accept a different bundle under the same identity.
    """
    original = Path(path).absolute()
    if original.is_symlink():
        raise ValueError("collector archive cannot be a symlink")
    archive = original.resolve(strict=True)
    if not archive.is_file():
        raise ValueError("collector archive must be a regular file")
    if not BUNDLE_NAME.fullmatch(collector_id):
        raise ValueError("invalid collector identity")
    size = archive.stat().st_size
    if not 0 < size <= MAX_ARCHIVE_BYTES:
        raise ValueError("native rollout archive exceeds streaming transfer limit")
    parts = urlsplit(base_url)
    if parts.scheme not in ("http", "https") or not parts.hostname or (
            parts.username is not None or parts.password is not None or
            parts.query or parts.fragment):
        raise ValueError("bundle receiver URL must be an http(s) endpoint without credentials or query")
    auth = bundle_token() if token is None else token
    endpoint = parts.path.rstrip("/") + "/v1/bundles/" + collector_id
    connection_class = bundle_connection_class(parts)
    started = time.perf_counter()
    sha256 = archive_sha256 or file_sha256(archive)
    if not DIGEST_RE.fullmatch(sha256):
        raise ValueError("invalid collector archive checksum")

    def published_receipt():
        # A completed import is recoverable even when its original response
        # was lost. Query first so a retry never sends an entire archive again.
        query = connection_class(parts.hostname, parts.port, timeout=60)
        try:
            query.request("GET", endpoint, headers={"Authorization": "Bearer " + auth})
            answer = query.getresponse()
            body = answer.read(64 * 1024)
            if answer.status == 404:
                return None
            if answer.status == 503:
                raise ConnectionError("native bundle receiver is at its connection limit")
            try:
                payload = json.loads(body)
            except (ValueError, UnicodeDecodeError) as exc:
                raise ValueError("invalid collector acknowledgement response") from exc
            if answer.status != 200:
                raise ValueError(f"collector acknowledgement rejected: HTTP {answer.status}: {payload.get('error')}")
            if (not isinstance(payload, dict) or
                    payload.get("collector_id") != collector_id or
                    payload.get("archive_sha256") != sha256 or
                    payload.get("archive_bytes") != size):
                raise ValueError("receiver already published a different collector archive")
            return payload
        finally:
            query.close()

    def confirmed(attempt):
        try:
            prior = published_receipt()
        except (OSError, http.client.HTTPException):
            return None
        if prior is None:
            return None
        return {**prior, "already_received": True,
                "upload_wall_seconds": time.perf_counter() - started,
                "upload_attempts": attempt, "receiver": base_url}

    def staged_offset():
        # Only this exact issued collector artifact can be resumed. Older
        # receiver builds return 501 and continue using ordinary full POST.
        query = connection_class(parts.hostname, parts.port, timeout=60)
        try:
            query.request("HEAD", endpoint, headers={
                "Authorization": "Bearer " + auth,
                "X-Archive-SHA256": sha256, "X-Archive-Bytes": str(size),
            })
            answer = query.getresponse()
            if answer.status in (404, 405, 501):
                return 0
            if answer.status in (409, 503):
                raise ConnectionError("previous collector upload is still active")
            if answer.status != 200:
                raise ValueError(f"collector resume rejected: HTTP {answer.status}")
            try:
                offset = int(answer.getheader("X-Received-Bytes", "-1"))
                remote_size = int(answer.getheader("X-Archive-Bytes", "-1"))
            except ValueError as exc:
                raise ValueError("collector resume had invalid byte counters") from exc
            if (not 0 <= offset <= size or remote_size != size or
                    answer.getheader("X-Archive-SHA256") != sha256):
                raise ValueError("collector resume has a different immutable artifact")
            return offset
        finally:
            query.close()

    prior = confirmed(0)
    if prior is not None:
        return prior

    for attempt in range(1, MAX_UPLOAD_ATTEMPTS + 1):
        if attempt > 1:
            time.sleep(min(2 ** (attempt - 2), 8))
            prior = confirmed(attempt - 1)
            if prior is not None:
                return prior
        connection = None
        try:
            # Also query on the first attempt: a restarted collector worker
            # can recover the learner's intact staged prefix without a
            # process-local transfer cursor or replaying simulation.
            offset = staged_offset()
            connection = connection_class(parts.hostname, parts.port, timeout=60)
            connection.putrequest("POST", endpoint)
            connection.putheader("Authorization", "Bearer " + auth)
            connection.putheader("Content-Type", "application/x-native-rollout-tar")
            connection.putheader("Content-Length", str(size - offset))
            connection.putheader("X-Archive-SHA256", sha256)
            connection.putheader("X-Archive-Bytes", str(size))
            connection.putheader("X-Resume-Offset", str(offset))
            connection.endheaders()
            with archive.open("rb") as stream:
                stream.seek(offset)
                while True:
                    chunk = stream.read(CHUNK)
                    if not chunk:
                        break
                    connection.send(chunk)
            response = connection.getresponse()
            body = response.read(64 * 1024)
            try:
                payload = json.loads(body)
            except (ValueError, UnicodeDecodeError):
                raise ValueError(f"invalid collector receiver response: HTTP {response.status}")
            if response.status == 503:
                raise ConnectionError("native bundle receiver is at its connection limit")
            if (response.status == 422 and offset > 0 and
                    isinstance(payload, dict) and
                    payload.get("error") == "collector archive checksum mismatch"):
                # The saved prefix could have been damaged by an unclean
                # receiver restart. It has been discarded; one bounded
                # retry may retransmit the exact original artifact.
                raise ConnectionError("staged native rollout prefix failed SHA verification")
            if (response.status == 409 and isinstance(payload, dict) and
                    payload.get("error") == "collector bundle already published or being received"):
                # The previous connection can still be finishing import.
                # Query its receipt rather than accepting a duplicate POST.
                raise ConnectionError("collector bundle import is still in flight")
            if response.status != 200:
                raise ValueError(f"collector upload rejected: HTTP {response.status}: {payload.get('error')}")
            if (not isinstance(payload, dict) or
                    payload.get("collector_id") != collector_id or
                    payload.get("archive_sha256") != sha256 or
                    payload.get("archive_bytes") != size):
                raise ValueError("receiver did not confirm this exact collector archive")
            if ("resumed_prefix_bytes" in payload and
                    payload["resumed_prefix_bytes"] != offset):
                raise ValueError("receiver did not confirm the requested resume offset")
            return {
                **payload, "upload_wall_seconds": time.perf_counter() - started,
                "upload_attempts": attempt, "receiver": base_url,
                "upload_resumed_prefix_bytes": offset,
            }
        except (OSError, http.client.HTTPException):
            prior = confirmed(attempt)
            if prior is not None:
                return prior
            if attempt == MAX_UPLOAD_ATTEMPTS:
                raise
        except ValueError:
            # A malformed/negative response is not a transient network
            # failure. Only a matching committed receipt can override it.
            prior = confirmed(attempt)
            if prior is not None:
                return prior
            raise
        finally:
            if connection is not None:
                connection.close()
    raise RuntimeError("collector upload retries exhausted")


def download_input_archive(base_url, collector_id, destination, token=None):
    """Fetch immutable inputs, resuming a SHA-pinned prefix across restarts.

    The published archive is never made visible before full verification.
    Older learners lacking input HEAD continue using temporary, in-process
    ranged downloads without relying on any new wire schema.
    """
    if not BUNDLE_NAME.fullmatch(collector_id):
        raise ValueError("invalid native collector identity")
    parts = urlsplit(base_url)
    if parts.scheme not in ("http", "https") or not parts.hostname or (
            parts.username is not None or parts.password is not None or
            parts.query or parts.fragment):
        raise ValueError("bundle receiver URL must be an http(s) endpoint without query")
    target = Path(destination).absolute()
    parent = target.parent.resolve(strict=True)
    if target.exists() or target.is_symlink():
        raise FileExistsError(f"native collector input archive already exists: {target}")
    connection_class = bundle_connection_class(parts)
    endpoint = parts.path.rstrip("/") + "/v1/inputs/" + collector_id
    auth = bundle_token() if token is None else token
    started = time.perf_counter()
    size = digest = None

    # The authoritative issued archive is immutable. Discover its physical
    # SHA and length before looking up any surviving local prefix.
    query = None
    try:
        query = connection_class(parts.hostname, parts.port, timeout=60)
        query.request("HEAD", endpoint, headers={"Authorization": "Bearer " + auth})
        answer = query.getresponse()
        if answer.status == 200:
            size = int(answer.getheader("Content-Length", "0"))
            digest = answer.getheader("X-Archive-SHA256", "")
            if (not 0 < size <= MAX_ARCHIVE_BYTES or
                    not isinstance(digest, str) or not DIGEST_RE.fullmatch(digest)):
                raise ValueError("learner input archive HEAD has invalid identity")
        elif answer.status == 404:
            raise LookupError("collector input archive has no issued work order")
        elif answer.status not in (405, 501, 503):
            raise ValueError(f"collector input identity rejected: HTTP {answer.status}")
    except (OSError, http.client.HTTPException):
        # HEAD discovery is optional for interoperability. Existing GET
        # retries still diagnose offline receivers and incomplete streams.
        size = digest = None
    finally:
        if query is not None:
            query.close()

    temporary = None
    persistent = size is not None
    discard_partial = False
    successful = False
    initial_prefix = 0
    attempts = 0
    resumed_prefix_bytes = 0
    prefix_rehash_bytes = 0
    hasher = hashlib.sha256()
    received = 0
    try:
        if persistent:
            seed = f"{collector_id}:{digest}:{size}"
            name = hashlib.sha256(seed.encode("ascii")).hexdigest()
            temporary = parent / (".native-input-download-" + name + ".partial")
            if temporary.is_symlink():
                raise ValueError("native collector input staging file is a symlink")
            if temporary.exists():
                stat = temporary.stat()
                if not temporary.is_file() or (
                        os.name == "posix" and stat.st_mode & 0o077):
                    raise ValueError("native collector input stage must be a private regular file")
                if stat.st_size > size:
                    temporary.unlink()
                else:
                    with temporary.open("rb") as stream:
                        while block := stream.read(CHUNK):
                            hasher.update(block)
                            received += len(block)
                    initial_prefix = received
                    prefix_rehash_bytes = received
            if not temporary.exists():
                fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
                os.close(fd)
        else:
            with tempfile.NamedTemporaryFile(mode="w+b", dir=parent,
                    prefix=".native-input-download-", suffix=".partial",
                    delete=False) as output:
                temporary = Path(output.name)

        with temporary.open("r+b") as output:
            output.seek(received)
            for attempt in range(MAX_DOWNLOAD_ATTEMPTS):
                if size is not None and received == size:
                    if hasher.hexdigest() == digest:
                        break
                    if initial_prefix and attempt + 1 < MAX_DOWNLOAD_ATTEMPTS:
                        # A crash may have left a malformed local prefix.
                        # Discard it once and fetch a clean immutable copy.
                        output.seek(0)
                        output.truncate()
                        output.flush()
                        hasher = hashlib.sha256()
                        received = 0
                        initial_prefix = 0
                    else:
                        discard_partial = True
                        raise ValueError("collector input archive checksum differs from learner receipt")
                attempts = attempt + 1
                connection = None
                try:
                    connection = connection_class(parts.hostname, parts.port, timeout=60)
                    headers = {"Authorization": "Bearer " + auth}
                    if received:
                        resumed_prefix_bytes = max(resumed_prefix_bytes, received)
                        headers["Range"] = f"bytes={received}-"
                    connection.request("GET", endpoint, headers=headers)
                    response = connection.getresponse()
                    expected_status = 206 if received else 200
                    if response.status == 503:
                        raise ConnectionError("learner input receiver is at its connection limit")
                    if response.status != expected_status:
                        discard_partial = True
                        reason = response.read(4096)
                        raise ValueError(
                            f"collector input download rejected: HTTP {response.status}: {reason!r}"
                        )
                    length = int(response.getheader("Content-Length", "0"))
                    response_digest = response.getheader("X-Archive-SHA256", "")
                    if size is None:
                        if not 0 < length <= MAX_ARCHIVE_BYTES or not DIGEST_RE.fullmatch(response_digest):
                            discard_partial = True
                            raise ValueError("learner returned an invalid collector archive identity")
                        size, digest = length, response_digest
                    elif (response_digest != digest or
                            length != size - received or
                            (received and response.getheader("Content-Range") !=
                             f"bytes {received}-{size - 1}/{size}")):
                        discard_partial = True
                        raise ValueError("learner changed collector inputs during resumed download")
                    while received < size:
                        block = response.read(min(CHUNK, size - received))
                        if not block:
                            break
                        output.write(block)
                        hasher.update(block)
                        received += len(block)
                    if received == size and hasher.hexdigest() == digest:
                        break
                except (OSError, http.client.HTTPException):
                    if attempt + 1 == MAX_DOWNLOAD_ATTEMPTS:
                        raise
                finally:
                    if connection is not None:
                        connection.close()
            if size is None or received != size:
                raise ValueError("learner returned truncated collector inputs after bounded retries")
            if hasher.hexdigest() != digest:
                discard_partial = True
                raise ValueError("collector input archive checksum differs from learner receipt")
            output.flush()
            os.fsync(output.fileno())
        os.link(temporary, target)
        sync_directory(parent)
        successful = True
    finally:
        # Preserve a private SHA-named prefix after transient network errors;
        # reject changed identities and remove only completed or invalid
        # staging. The learner's issued work order is still authoritative.
        if temporary is not None and (successful or discard_partial or not persistent):
            temporary.unlink(missing_ok=True)
    return {
        "kind": "native_collector_inputs_downloaded",
        "collector_id": collector_id, "archive": str(target),
        "archive_bytes": size, "archive_sha256": digest,
        "wall_seconds": time.perf_counter() - started,
        "download_attempts": attempts,
        "resumed_prefix_bytes": resumed_prefix_bytes,
        "recovered_prefix_rehash_bytes": prefix_rehash_bytes,
    }


class LimitedHTTPServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address, handler):
        super().__init__(address, handler)
        self.slots = threading.BoundedSemaphore(MAX_CLIENTS)

    def process_request(self, request, client_address):
        if not self.slots.acquire(blocking=False):
            # Reject excess accepted sockets with a machine-readable
            # transient response instead of silently resetting the client.
            try:
                request.sendall(
                    b"HTTP/1.1 503 Service Unavailable\r\n"
                    b"Content-Length: 0\r\n"
                    b"Retry-After: 2\r\n"
                    b"Connection: close\r\n\r\n"
                )
            except OSError:
                pass
            finally:
                request.close()
            return
        try:
            super().process_request(request, client_address)
        except BaseException:
            self.slots.release()
            raise

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self.slots.release()


class BundleReceiver:
    """Context-managed receiver; each training round activates exact orders."""

    def __init__(self, listen, inbox, token=None, on_event=None,
                 tls_cert=None, tls_key=None):
        if ":" not in listen:
            raise ValueError("bundle listen address must be HOST:PORT")
        host, port_string = listen.rsplit(":", 1)
        try:
            port = int(port_string)
        except ValueError as exc:
            raise ValueError("bundle listener needs a numeric port") from exc
        if not host or not 0 < port <= 65535:
            raise ValueError("bundle listener needs a host and nonzero port")
        self.inbox = Path(inbox).resolve(strict=True)
        if not self.inbox.is_dir():
            raise ValueError("bundle receiver inbox must already exist")
        self.token = bundle_token() if token is None else token
        self.on_event = on_event
        self.active_orders = None
        self.input_archives = {}
        self.inflight = set()
        # A collector's partially transferred bytes have one immutable
        # SHA/length identity and one private temp file. Only the holder
        # of its existing active work order may append further bytes.
        self.staged_uploads = {}
        self.input_bytes_served = 0
        self.upload_bytes_received = 0
        self.upload_wire_bytes_received = 0
        self.upload_resumed_prefix_bytes = 0
        self.staged_prefix_rehash_bytes = 0
        self.upload_and_verify_seconds = 0.0
        self.completed_uploads = 0
        self.lock = threading.Lock()
        self.server = LimitedHTTPServer((host, port), _receiver_handler(self))
        self.tls_enabled = tls_cert is not None
        if (tls_cert is None) != (tls_key is None):
            self.server.server_close()
            raise ValueError("native bundle TLS requires both certificate and private key")
        if tls_cert is not None:
            certificate = Path(tls_cert).absolute()
            key = Path(tls_key).absolute()
            if (certificate.is_symlink() or key.is_symlink() or
                    not certificate.is_file() or not key.is_file()):
                self.server.server_close()
                raise ValueError("native TLS certificate/key must be regular files")
            if os.name == "posix" and key.stat().st_mode & 0o077:
                self.server.server_close()
                raise ValueError("native TLS private key must not be group/world accessible")
            try:
                context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
                context.minimum_version = ssl.TLSVersion.TLSv1_2
                context.options |= ssl.OP_NO_COMPRESSION
                context.load_cert_chain(certfile=str(certificate), keyfile=str(key))
                # The HTTP handlers and their bounded artifact staging stay
                # unchanged; only transport is encrypted.
                self.server.socket = context.wrap_socket(
                    self.server.socket, server_side=True,
                )
            except (OSError, ValueError, ssl.SSLError):
                self.server.server_close()
                raise
        self.worker = None

    def __enter__(self):
        self.worker = threading.Thread(target=self.server.serve_forever, daemon=True,
                                       name="native-bundle-receiver")
        self.worker.start()
        if self.on_event:
            self.on_event("native_bundle_receiver_listening", {
                "address": f"{self.server.server_address[0]}:{self.server.server_address[1]}",
                "max_concurrent_uploads": MAX_CLIENTS,
                "max_concurrent_requests": MAX_CLIENTS,
                "transport": "https" if self.tls_enabled else "http_private_network",
            })
        return self

    def __exit__(self, _kind, _error, _traceback):
        self.server.shutdown()
        self.server.server_close()
        if self.worker is not None:
            self.worker.join()

    def transfer_metrics(self):
        with self.lock:
            return {
                "collector_input_bytes_served": self.input_bytes_served,
                "collector_output_bytes_received": self.upload_bytes_received,
                "collector_output_wire_bytes_received": self.upload_wire_bytes_received,
                "collector_output_resumed_prefix_bytes": self.upload_resumed_prefix_bytes,
                "collector_staged_prefix_rehash_bytes": self.staged_prefix_rehash_bytes,
                "collector_upload_and_verify_wall_seconds": self.upload_and_verify_seconds,
                "collector_uploads_received": self.completed_uploads,
            }

    def record_transfer(self, *, input_bytes=0, upload_bytes=0, upload_seconds=0.0,
                        upload_wire_bytes=0, resumed_prefix_bytes=0):
        with self.lock:
            self.input_bytes_served += input_bytes
            self.upload_bytes_received += upload_bytes
            self.upload_wire_bytes_received += upload_wire_bytes
            self.upload_resumed_prefix_bytes += resumed_prefix_bytes
            self.upload_and_verify_seconds += upload_seconds
            if upload_bytes:
                self.completed_uploads += 1

    def activate(self, orders, input_archives=None):
        if orders.get("kind") != "native_distributed_collection_orders":
            raise ValueError("bundle receiver needs issued collection orders")
        archives = input_archives or {}
        valid = {job["id"] for job in orders["jobs"]}
        if set(archives) - valid:
            raise ValueError("collector input archive was not issued in this round")
        with self.lock:
            if self.inflight:
                raise ValueError("cannot replace issued native round during active uploads")
            staged = list(self.staged_uploads.values())
            self.staged_uploads.clear()
            self.active_orders = orders
            self.input_archives = dict(archives)
            self.input_bytes_served = 0
            self.upload_bytes_received = 0
            self.upload_wire_bytes_received = 0
            self.upload_resumed_prefix_bytes = 0
            self.staged_prefix_rehash_bytes = 0
            self.upload_and_verify_seconds = 0.0
            self.completed_uploads = 0
        for state in staged:
            state["path"].unlink(missing_ok=True)

    def deactivate(self):
        with self.lock:
            if self.inflight:
                raise ValueError("cannot deactivate issued native round during active uploads")
            staged = list(self.staged_uploads.values())
            self.staged_uploads.clear()
            self.active_orders = None
            self.input_archives.clear()
        for state in staged:
            state["path"].unlink(missing_ok=True)

    def prepared_input(self, collector_id):
        with self.lock:
            if self.active_orders is None or not any(
                    job["id"] == collector_id for job in self.active_orders["jobs"]):
                raise LookupError("collector inputs are not currently issued")
            published = self.input_archives.get(collector_id)
            if published is None:
                raise LookupError("collector inputs have not been exported")
            file, digest, expected_size = published
        file = Path(file)
        if (file.is_symlink() or not file.is_file() or
                file.stat().st_size != expected_size or
                not 0 < expected_size <= MAX_ARCHIVE_BYTES or
                not DIGEST_RE.fullmatch(digest)):
            raise ValueError("published collector input archive was redirected or changed")
        return file, digest, expected_size

    def completed_upload(self, collector_id):
        with self.lock:
            orders = self.active_orders
            jobs = [] if orders is None else [
                job for job in orders["jobs"] if job["id"] == collector_id
            ]
            if len(jobs) != 1:
                raise LookupError("collector has no active issued work order")
            return completed_uploaded_bundle(self.inbox, jobs[0], orders)

    def claim(self, collector_id):
        with self.lock:
            orders = self.active_orders
            jobs = [] if orders is None else [
                job for job in orders["jobs"] if job["id"] == collector_id
            ]
            if len(jobs) != 1:
                raise LookupError("collector has no active issued work order")
            job = jobs[0]
            if not BUNDLE_NAME.fullmatch(job["bundle_directory"]):
                raise ValueError("issued collector has an unsafe bundle destination")
            destination = self.inbox / job["bundle_directory"]
            if destination.exists() or collector_id in self.inflight:
                raise FileExistsError("collector bundle already published or being received")
            self.inflight.add(collector_id)
            return orders, job

    def release(self, collector_id):
        with self.lock:
            self.inflight.discard(collector_id)

    def _issued_stage_path(self, collector_id, digest, total):
        # The issued order already binds a job-specific collection digest.
        # Derive a private, deterministic prefix path from that identity
        # and the exact archive SHA/size. No second checkpoint format or
        # untrusted client-selected filename is introduced.
        if self.active_orders is None:
            raise LookupError("collector has no active issued work order")
        jobs = [job for job in self.active_orders["jobs"]
                if job["id"] == collector_id]
        if len(jobs) != 1 or not isinstance(jobs[0].get("collection_id"), str):
            raise LookupError("collector has no issued collection identity")
        seed = (jobs[0]["collection_id"] + ":" + digest + ":" + str(total))
        stage_name = hashlib.sha256(seed.encode("ascii")).hexdigest()
        return self.inbox / (".native-bundle-upload-" + stage_name + ".partial")

    def upload_offset(self, collector_id, digest, total):
        """Report committed-to-staging bytes only after a prior POST exits."""
        with self.lock:
            if collector_id in self.inflight:
                raise FileExistsError("collector upload is still streaming")
            path = self._issued_stage_path(collector_id, digest, total)
            state = self.staged_uploads.get(collector_id)
            if state is not None and (
                    state["digest"] != digest or state["total"] != total or
                    state["path"] != path):
                raise ValueError("collector upload identity changed during resume")
            if path.is_symlink():
                raise ValueError("staged collector file is a symlink")
            if not path.exists():
                return None
            if not path.is_file():
                raise ValueError("staged collector file is not regular")
            received = path.stat().st_size
            if received > total and state is None:
                # An interrupted, uncommitted prefix cannot exceed the
                # pinned archive length. Restart cleanly rather than
                # stranding an unattended collector on a corrupt offset.
                path.unlink()
                return None
            if received > total or (state is not None and
                                    received != state["received"]):
                raise ValueError("staged collector bytes were modified")
            return received

    def prepare_upload(self, collector_id, digest, total, offset):
        """One active POST owns a staging file and its incremental digest."""
        with self.lock:
            if collector_id not in self.inflight:
                raise ValueError("collector upload was not admitted")
            path = self._issued_stage_path(collector_id, digest, total)
            state = self.staged_uploads.get(collector_id)
        if state is None:
            # The active collector claim prevents concurrent appends to
            # this file. Never hold the global receiver lock while hashing
            # a multi-GB recovered prefix: other machines must continue
            # uploading, downloading, and reporting their work.
            if path.is_symlink():
                raise ValueError("staged collector file is a symlink")
            if path.exists():
                if not path.is_file() or path.stat().st_size > total:
                    raise ValueError("invalid recovered native upload prefix")
                hasher = hashlib.sha256()
                received = 0
                with path.open("rb") as stream:
                    while chunk := stream.read(CHUNK):
                        hasher.update(chunk)
                        received += len(chunk)
            else:
                if offset:
                    raise ValueError("collector resume has no matching staged prefix")
                with path.open("xb"):
                    pass
                hasher, received = hashlib.sha256(), 0
            state = {
                "path": path, "digest": digest, "total": total,
                "received": received, "hasher": hasher,
            }
            with self.lock:
                if collector_id not in self.inflight:
                    raise ValueError("collector claim ended during staged upload recovery")
                self.staged_uploads[collector_id] = state
                if received:
                    self.staged_prefix_rehash_bytes += received
        if (state["digest"] != digest or state["total"] != total or
                state["received"] != offset or state["path"] != path or
                path.is_symlink() or not path.is_file() or
                path.stat().st_size != offset):
            raise ValueError("collector upload offset or immutable identity differs")
        return state

    def discard_upload(self, collector_id):
        with self.lock:
            state = self.staged_uploads.pop(collector_id, None)
        if state is not None:
            state["path"].unlink(missing_ok=True)


def _receiver_handler(owner):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, format, *args):
            # Authentication secrets are never logged. The collector emits
            # its own measurement receipt after publication.
            return

        def respond(self, status, payload):
            data = json.dumps(payload, separators=(",", ":"), sort_keys=True).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(data)
            self.close_connection = True

        def do_HEAD(self):
            if not hmac.compare_digest(
                    self.headers.get("Authorization", ""), "Bearer " + owner.token):
                self.respond(401, {"error": "unauthorized"})
                return
            input_prefix = "/v1/inputs/"
            if self.path.startswith(input_prefix):
                collector_id = self.path[len(input_prefix):]
                try:
                    if not BUNDLE_NAME.fullmatch(collector_id):
                        raise LookupError("unknown collector input")
                    _, digest, total = owner.prepared_input(collector_id)
                    self.send_response(200)
                    self.send_header("X-Archive-SHA256", digest)
                    self.send_header("Accept-Ranges", "bytes")
                    self.send_header("Content-Length", str(total))
                except LookupError:
                    self.send_response(404)
                    self.send_header("Content-Length", "0")
                except (OSError, ValueError, TypeError, KeyError):
                    self.send_response(409)
                    self.send_header("Content-Length", "0")
                self.send_header("Connection", "close")
                self.end_headers()
                self.close_connection = True
                return
            prefix = "/v1/bundles/"
            collector_id = self.path[len(prefix):] if self.path.startswith(prefix) else ""
            if not BUNDLE_NAME.fullmatch(collector_id):
                self.respond(404, {"error": "unknown bundle endpoint"})
                return
            try:
                digest = self.headers.get("X-Archive-SHA256", "")
                total = int(self.headers.get("X-Archive-Bytes", "0"))
                if not DIGEST_RE.fullmatch(digest) or not 0 < total <= MAX_ARCHIVE_BYTES:
                    raise ValueError("invalid immutable upload identity")
                offset = owner.upload_offset(collector_id, digest, total)
                if offset is None:
                    self.send_response(404)
                else:
                    self.send_response(200)
                    self.send_header("X-Received-Bytes", str(offset))
                    self.send_header("X-Archive-SHA256", digest)
                    self.send_header("X-Archive-Bytes", str(total))
            except LookupError:
                self.send_response(404)
            except FileExistsError:
                self.send_response(409)
            except (ValueError, OSError):
                self.send_response(422)
            self.send_header("Content-Length", "0")
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True

        def do_GET(self):
            if not hmac.compare_digest(
                    self.headers.get("Authorization", ""), "Bearer " + owner.token):
                self.respond(401, {"error": "unauthorized"})
                return
            upload_prefix = "/v1/bundles/"
            if self.path.startswith(upload_prefix):
                collector_id = self.path[len(upload_prefix):]
                if not BUNDLE_NAME.fullmatch(collector_id):
                    self.respond(404, {"error": "unknown collector upload endpoint"})
                    return
                try:
                    receipt = owner.completed_upload(collector_id)
                except LookupError as error:
                    self.respond(404, {"error": str(error)})
                    return
                except (ValueError, OSError, KeyError, TypeError) as error:
                    self.respond(409, {"error": str(error)})
                    return
                self.respond(200, receipt)
                return
            prefix = "/v1/inputs/"
            collector_id = self.path[len(prefix):] if self.path.startswith(prefix) else ""
            if not BUNDLE_NAME.fullmatch(collector_id):
                self.respond(404, {"error": "unknown collector input endpoint"})
                return
            try:
                file, digest, expected_size = owner.prepared_input(collector_id)
                offset = 0
                requested = self.headers.get("Range")
                if requested is not None:
                    match = re.fullmatch(r"bytes=([0-9]+)-", requested)
                    if match is None or match.group(1) != str(int(match.group(1))):
                        self.respond(416, {"error": "invalid collector byte range"})
                        return
                    offset = int(match.group(1))
                    if not 0 < offset < expected_size:
                        self.respond(416, {"error": "collector byte range outside archive"})
                        return
                sent = 0
                remaining = expected_size - offset
                try:
                    with file.open("rb") as stream:
                        stream.seek(offset)
                        self.connection.settimeout(60)
                        self.send_response(206 if offset else 200)
                        self.send_header("Content-Type", "application/x-native-collector-inputs-tar")
                        self.send_header("Content-Length", str(remaining))
                        self.send_header("X-Archive-SHA256", digest)
                        if offset:
                            self.send_header("Content-Range",
                                             f"bytes {offset}-{expected_size - 1}/{expected_size}")
                        self.send_header("Connection", "close")
                        self.end_headers()
                        self.close_connection = True
                        while remaining:
                            data = stream.read(min(CHUNK, remaining))
                            if not data:
                                break
                            self.wfile.write(data)
                            sent += len(data)
                            remaining -= len(data)
                finally:
                    # Count actual transmitted bytes, including partial retries;
                    # not hypothetical full archives or overlapping worker time.
                    owner.record_transfer(input_bytes=sent)
                if remaining == 0 and owner.on_event:
                    owner.on_event("native_collector_inputs_served", {
                        "collector_id": collector_id, "archive_bytes": sent,
                        "range_offset": offset,
                    })
            except LookupError as error:
                self.respond(404, {"error": str(error)})
            except ValueError as error:
                self.respond(409, {"error": str(error)})
            except (BrokenPipeError, ConnectionResetError, TimeoutError, OSError):
                self.close_connection = True

        def do_POST(self):
            # Authenticate before accepting or persisting ANY archive bytes.
            token = self.headers.get("Authorization", "")
            if not hmac.compare_digest(token, "Bearer " + owner.token):
                self.respond(401, {"error": "unauthorized"})
                return
            prefix = "/v1/bundles/"
            collector_id = self.path[len(prefix):] if self.path.startswith(prefix) else ""
            if not BUNDLE_NAME.fullmatch(collector_id):
                self.respond(404, {"error": "unknown bundle endpoint"})
                return
            try:
                count = int(self.headers.get("Content-Length", ""))
                offset = int(self.headers.get("X-Resume-Offset", "0"))
                total = int(self.headers.get("X-Archive-Bytes", str(count)))
            except ValueError:
                count, offset, total = -1, -1, -1
            digest = self.headers.get("X-Archive-SHA256", "")
            if (not 0 < total <= MAX_ARCHIVE_BYTES or
                    not 0 <= offset <= total or count != total - offset or
                    not DIGEST_RE.fullmatch(digest)):
                self.respond(413, {"error": "invalid archive range, size or checksum"})
                return
            try:
                orders, job = owner.claim(collector_id)
            except LookupError as error:
                self.respond(404, {"error": str(error)})
                return
            except FileExistsError as error:
                self.respond(409, {"error": str(error)})
                return
            except ValueError as error:
                self.respond(422, {"error": str(error)})
                return
            started = time.perf_counter()
            wire_bytes = 0
            staged = None
            completed = False
            try:
                self.connection.settimeout(60)
                staged = owner.prepare_upload(collector_id, digest, total, offset)
                with staged["path"].open("ab") as stream:
                    remaining = count
                    while remaining:
                        data = self.rfile.read(min(CHUNK, remaining))
                        if not data:
                            raise ConnectionError("truncated native rollout archive")
                        stream.write(data)
                        staged["hasher"].update(data)
                        staged["received"] += len(data)
                        wire_bytes += len(data)
                        remaining -= len(data)
                    stream.flush()
                    os.fsync(stream.fileno())
                if staged["received"] != total or staged["hasher"].hexdigest() != digest:
                    # Full-length mismatches cannot ever become valid by
                    # resending a suffix; discard them for a fresh upload.
                    owner.discard_upload(collector_id)
                    raise ValueError("collector archive checksum mismatch")
                import_bundle_archive(
                    staged["path"], job, orders, owner.inbox,
                    archive_sha256=digest, archive_bytes=total,
                )
                result = {
                    **completed_uploaded_bundle(owner.inbox, job, orders),
                    "receive_and_verify_wall_seconds": time.perf_counter() - started,
                    "resumed_prefix_bytes": offset,
                }
                completed = True
                owner.record_transfer(
                    upload_bytes=total,
                    upload_seconds=result["receive_and_verify_wall_seconds"],
                    resumed_prefix_bytes=offset,
                )
                owner.discard_upload(collector_id)
                if owner.on_event:
                    owner.on_event("native_bundle_received", result)
                self.respond(200, result)
            except (ConnectionError, BrokenPipeError, ConnectionResetError, TimeoutError):
                # Preserve only an immutable digest-pinned prefix. A retried
                # POST must present the exact stored byte count and identity.
                self.close_connection = True
            except (ValueError, OSError, EOFError, KeyError, TypeError,
                    tarfile.TarError) as error:
                # Import or identity failures must not poison future
                # uploads under the same collector ID.
                if staged is not None:
                    owner.discard_upload(collector_id)
                try:
                    self.respond(422, {"error": str(error)})
                except OSError:
                    self.close_connection = True
            finally:
                owner.record_transfer(upload_wire_bytes=wire_bytes)
                owner.release(collector_id)

    return Handler
