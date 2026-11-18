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
import tempfile
import tarfile
import threading
import time
from urllib.parse import urlsplit

from native_training_artifacts import file_sha256, sync_directory
from transfer_policy_bundle import import_bundle_archive, completed_uploaded_bundle, BUNDLE_NAME

CHUNK = 1024 * 1024
MAX_ARCHIVE_BYTES = 8 * 1024 ** 3
MAX_CLIENTS = 4
TOKEN_ENV = "BEES_NATIVE_BUNDLE_TOKEN"
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


def upload_bundle_archive(path, collector_id, base_url, token=None, archive_sha256=None):
    """Stream a verified portable archive without reading it into RAM."""
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
    connection_class = http.client.HTTPSConnection if parts.scheme == "https" else http.client.HTTPConnection
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

    prior = published_receipt()
    if prior is not None:
        return {**prior, "already_received": True,
                "upload_wall_seconds": time.perf_counter() - started,
                "receiver": base_url}

    connection = connection_class(parts.hostname, parts.port, timeout=60)
    try:
        connection.putrequest("POST", endpoint)
        connection.putheader("Authorization", "Bearer " + auth)
        connection.putheader("Content-Type", "application/x-native-rollout-tar")
        connection.putheader("Content-Length", str(size))
        connection.putheader("X-Archive-SHA256", sha256)
        connection.endheaders()
        with archive.open("rb") as stream:
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
        if response.status != 200:
            raise ValueError(f"collector upload rejected: HTTP {response.status}: {payload.get('error')}")
        if (payload.get("collector_id") != collector_id or
                payload.get("archive_sha256") != sha256 or
                payload.get("archive_bytes") != size):
            raise ValueError("receiver did not confirm this exact collector archive")
        return {
            **payload, "upload_wall_seconds": time.perf_counter() - started,
            "receiver": base_url,
        }
    except (OSError, ValueError, http.client.HTTPException):
        # Upload may already have committed when the response connection
        # died. Check its durable receipt once, never publish twice.
        try:
            prior = published_receipt()
        except (OSError, ValueError, http.client.HTTPException):
            prior = None
        if prior is not None:
            return {**prior, "already_received": True,
                    "upload_wall_seconds": time.perf_counter() - started,
                    "receiver": base_url}
        raise
    finally:
        connection.close()


def download_input_archive(base_url, collector_id, destination, token=None):
    """Fetch one issued worker's immutable inputs with bounded memory."""
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
    connection_class = http.client.HTTPSConnection if parts.scheme == "https" else http.client.HTTPConnection
    endpoint = parts.path.rstrip("/") + "/v1/inputs/" + collector_id
    auth = bundle_token() if token is None else token
    started = time.perf_counter()
    temporary = None
    connection = connection_class(parts.hostname, parts.port, timeout=60)
    try:
        connection.request("GET", endpoint, headers={"Authorization": "Bearer " + auth})
        response = connection.getresponse()
        if response.status != 200:
            reason = response.read(4096)
            raise ValueError(f"collector input download rejected: HTTP {response.status}: {reason!r}")
        size = int(response.getheader("Content-Length", "0"))
        digest = response.getheader("X-Archive-SHA256", "")
        if not 0 < size <= MAX_ARCHIVE_BYTES or not DIGEST_RE.fullmatch(digest):
            raise ValueError("learner returned an invalid collector archive identity")
        hasher = hashlib.sha256()
        count = 0
        with tempfile.NamedTemporaryFile(mode="w+b", dir=parent,
                prefix=".native-input-download-", suffix=".partial",
                delete=False) as output:
            temporary = Path(output.name)
            while count < size:
                block = response.read(min(CHUNK, size - count))
                if not block:
                    raise ValueError("learner returned truncated collector inputs")
                hasher.update(block)
                output.write(block)
                count += len(block)
            output.flush()
            os.fsync(output.fileno())
        if count != size or hasher.hexdigest() != digest:
            raise ValueError("collector input archive checksum differs from learner receipt")
        os.link(temporary, target)
        sync_directory(parent)
    finally:
        connection.close()
        if temporary is not None:
            temporary.unlink(missing_ok=True)
    return {
        "kind": "native_collector_inputs_downloaded",
        "collector_id": collector_id, "archive": str(target),
        "archive_bytes": size, "archive_sha256": digest,
        "wall_seconds": time.perf_counter() - started,
    }


class LimitedHTTPServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address, handler):
        super().__init__(address, handler)
        self.slots = threading.BoundedSemaphore(MAX_CLIENTS)

    def process_request(self, request, client_address):
        if not self.slots.acquire(blocking=False):
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

    def __init__(self, listen, inbox, token=None, on_event=None):
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
        self.input_bytes_served = 0
        self.upload_bytes_received = 0
        self.upload_and_verify_seconds = 0.0
        self.completed_uploads = 0
        self.lock = threading.Lock()
        self.server = LimitedHTTPServer((host, port), _receiver_handler(self))
        self.worker = None

    def __enter__(self):
        self.worker = threading.Thread(target=self.server.serve_forever, daemon=True,
                                       name="native-bundle-receiver")
        self.worker.start()
        if self.on_event:
            self.on_event("native_bundle_receiver_listening", {
                "address": f"{self.server.server_address[0]}:{self.server.server_address[1]}",
                "max_concurrent_uploads": MAX_CLIENTS,
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
                "collector_upload_and_verify_wall_seconds": self.upload_and_verify_seconds,
                "collector_uploads_received": self.completed_uploads,
            }

    def record_transfer(self, *, input_bytes=0, upload_bytes=0, upload_seconds=0.0):
        with self.lock:
            self.input_bytes_served += input_bytes
            self.upload_bytes_received += upload_bytes
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
            self.active_orders = orders
            self.input_archives = dict(archives)
            self.input_bytes_served = 0
            self.upload_bytes_received = 0
            self.upload_and_verify_seconds = 0.0
            self.completed_uploads = 0

    def deactivate(self):
        with self.lock:
            self.active_orders = None
            self.input_archives.clear()

    def prepared_input(self, collector_id):
        with self.lock:
            if self.active_orders is None or not any(
                    job["id"] == collector_id for job in self.active_orders["jobs"]):
                raise LookupError("collector inputs are not currently issued")
            published = self.input_archives.get(collector_id)
            if published is None:
                raise LookupError("collector inputs have not been exported")
            return published

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
                file = Path(file)
                if file.is_symlink() or not file.is_file() or (
                        file.stat().st_size != expected_size or
                        not 0 < expected_size <= MAX_ARCHIVE_BYTES or
                        not DIGEST_RE.fullmatch(digest)):
                    raise ValueError("published collector input archive was redirected or changed")
                with file.open("rb") as stream:
                    self.connection.settimeout(60)
                    self.send_response(200)
                    self.send_header("Content-Type", "application/x-native-collector-inputs-tar")
                    self.send_header("Content-Length", str(expected_size))
                    self.send_header("X-Archive-SHA256", digest)
                    self.send_header("Connection", "close")
                    self.end_headers()
                    self.close_connection = True
                    remaining = expected_size
                    while remaining:
                        data = stream.read(min(CHUNK, remaining))
                        if not data:
                            break
                        self.wfile.write(data)
                        remaining -= len(data)
                if remaining == 0:
                    owner.record_transfer(input_bytes=expected_size)
                    if owner.on_event:
                        owner.on_event("native_collector_inputs_served", {
                            "collector_id": collector_id, "archive_bytes": expected_size,
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
            except ValueError:
                count = 0
            digest = self.headers.get("X-Archive-SHA256", "")
            if not 0 < count <= MAX_ARCHIVE_BYTES or not DIGEST_RE.fullmatch(digest):
                self.respond(413, {"error": "invalid archive size or checksum"})
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
            temporary = None
            try:
                self.connection.settimeout(60)
                hasher = hashlib.sha256()
                with tempfile.NamedTemporaryFile(mode="w+b", dir=owner.inbox,
                    prefix=".native-bundle-upload-", suffix=".partial",
                    delete=False) as stream:
                    temporary = Path(stream.name)
                    remaining = count
                    while remaining:
                        data = self.rfile.read(min(CHUNK, remaining))
                        if not data:
                            raise ValueError("truncated native rollout archive")
                        stream.write(data)
                        hasher.update(data)
                        remaining -= len(data)
                    stream.flush()
                    os.fsync(stream.fileno())
                if hasher.hexdigest() != digest:
                    raise ValueError("collector archive checksum mismatch")
                import_bundle_archive(temporary, job, orders, owner.inbox,
                                      archive_sha256=digest, archive_bytes=count)
                result = {
                    **completed_uploaded_bundle(owner.inbox, job, orders),
                    "receive_and_verify_wall_seconds": time.perf_counter() - started,
                }
                owner.record_transfer(upload_bytes=count,
                                      upload_seconds=result["receive_and_verify_wall_seconds"])
                if owner.on_event:
                    owner.on_event("native_bundle_received", result)
                self.respond(200, result)
            except (ValueError, OSError, EOFError, KeyError, TypeError, tarfile.TarError) as error:
                self.respond(422, {"error": str(error)})
            finally:
                if temporary is not None:
                    temporary.unlink(missing_ok=True)
                owner.release(collector_id)

    return Handler
