"""Shared BeesServer training-control client and managed build installer.

The control plane is intentionally ordinary HTTPS/HTTP JSON plus authenticated artifact downloads.
Workers never trust a build merely because its build id matches: the downloaded archive is verified
against the server-advertised SHA-256 before it is extracted into a versioned directory.
"""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import socket
import stat
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path
from typing import Any, Mapping, Optional


CONTROL_SCHEMA_VERSION = 1
DEFAULT_TIMEOUT_SECONDS = 15.0


class ControlUnavailable(RuntimeError):
    pass


class ControlRejected(RuntimeError):
    pass


def load_token(path: str | os.PathLike[str]) -> str:
    value = Path(path).expanduser().read_text(encoding="utf-8").strip()
    if not value:
        raise ValueError("training-control token file is empty")
    return value


def file_sha256(path: str | os.PathLike[str]) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


class TrainingControlClient:
    def __init__(self, base_url: str, token: str, timeout: float = DEFAULT_TIMEOUT_SECONDS):
        self.base_url = base_url.rstrip("/")
        self.token = token
        self.timeout = float(timeout)
        if not self.base_url:
            raise ValueError("training-control base URL must not be empty")
        if not self.token:
            raise ValueError("training-control token must not be empty")
        if self.timeout <= 0:
            raise ValueError("training-control timeout must be positive")

    def _request(
        self,
        method: str,
        path: str,
        *,
        query: Optional[Mapping[str, object]] = None,
        payload: Optional[Mapping[str, object]] = None,
    ) -> tuple[Mapping[str, str], bytes]:
        if query:
            path += ("&" if "?" in path else "?") + urllib.parse.urlencode(
                {key: str(value) for key, value in query.items()}
            )
        body = None
        headers = {
            "Authorization": "Bearer " + self.token,
            "Accept": "application/json",
        }
        if payload is not None:
            body = (json.dumps(dict(payload), sort_keys=True) + "\n").encode("utf-8")
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(
            self.base_url + path,
            data=body,
            method=method,
            headers=headers,
        )
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                return dict(response.headers.items()), response.read()
        except urllib.error.HTTPError as exc:
            raw = exc.read().decode("utf-8", errors="replace")
            raise ControlRejected(
                f"training control rejected {method} {path}: HTTP {exc.code}: {raw}"
            ) from exc
        except (urllib.error.URLError, TimeoutError, ConnectionError, OSError) as exc:
            raise ControlUnavailable(str(exc)) from exc

    @staticmethod
    def _decode_state(body: bytes) -> Mapping[str, Any]:
        try:
            value = json.loads(body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ControlRejected("training-control response is invalid JSON") from exc
        if not isinstance(value, Mapping) or value.get("schema_version") != CONTROL_SCHEMA_VERSION:
            raise ControlRejected("training-control state schema is incompatible")
        revision = value.get("revision")
        lease_seconds = value.get("lease_seconds")
        environment_args = value.get("environment_args")
        if not isinstance(revision, int) or isinstance(revision, bool) or revision < 0:
            raise ControlRejected("training-control revision is invalid")
        if not isinstance(lease_seconds, (int, float)) or isinstance(lease_seconds, bool) or lease_seconds <= 0:
            raise ControlRejected("training-control lease_seconds is invalid")
        if not isinstance(environment_args, list) or any(
            not isinstance(item, str) for item in environment_args
        ):
            raise ControlRejected("training-control environment_args is invalid")
        if value.get("desired_mode") not in ("training", "stopped", "inference"):
            raise ControlRejected("training-control desired_mode is invalid")
        return value

    def heartbeat(self, payload: Mapping[str, object]) -> Mapping[str, Any]:
        _headers, body = self._request("POST", "/v1/heartbeat", payload=payload)
        return self._decode_state(body)

    def state(self, trainer_id: str, role: str, platform: str) -> Mapping[str, Any]:
        _headers, body = self._request(
            "GET",
            "/v1/state",
            query={"trainer_id": trainer_id, "role": role, "platform": platform},
        )
        return self._decode_state(body)

    def download_artifact(self, artifact_url: str, destination: Path) -> None:
        request = urllib.request.Request(
            self.base_url + artifact_url,
            method="GET",
            headers={"Authorization": "Bearer " + self.token},
        )
        try:
            with urllib.request.urlopen(request, timeout=max(self.timeout, 60.0)) as response:
                with destination.open("wb") as output:
                    shutil.copyfileobj(response, output, length=1024 * 1024)
                    output.flush()
                    os.fsync(output.fileno())
        except urllib.error.HTTPError as exc:
            raw = exc.read().decode("utf-8", errors="replace")
            raise ControlRejected(
                f"training-control artifact download failed: HTTP {exc.code}: {raw}"
            ) from exc
        except (urllib.error.URLError, TimeoutError, ConnectionError, OSError) as exc:
            raise ControlUnavailable(str(exc)) from exc


def _safe_zip_member(root: Path, name: str) -> Path:
    normalized = name.replace("\\", "/")
    if not normalized or normalized.startswith("/") or normalized.startswith("../"):
        raise ValueError(f"unsafe artifact member path: {name!r}")
    destination = (root / normalized).resolve()
    try:
        destination.relative_to(root.resolve())
    except ValueError as exc:
        raise ValueError(f"artifact member escapes install root: {name!r}") from exc
    return destination


class ManagedBuildStore:
    def __init__(self, root: str | os.PathLike[str]):
        self.root = Path(root).expanduser().resolve()
        self.builds = self.root / "builds"
        self.current_manifest = self.root / "current.json"
        self.builds.mkdir(parents=True, exist_ok=True)

    def current(self) -> Optional[Mapping[str, Any]]:
        try:
            value = json.loads(self.current_manifest.read_text(encoding="utf-8"))
        except FileNotFoundError:
            return None
        except json.JSONDecodeError:
            return None
        return value if isinstance(value, Mapping) else None

    @staticmethod
    def _validated_descriptor(descriptor: Mapping[str, Any]) -> dict[str, Any]:
        required = ("platform", "build_id", "archive_sha256", "artifact_url", "entrypoint")
        for key in required:
            if not isinstance(descriptor.get(key), str) or not descriptor[key]:
                raise ValueError(f"build descriptor {key} is missing")
        sha = descriptor["archive_sha256"].lower()
        if len(sha) != 64 or any(ch not in "0123456789abcdef" for ch in sha):
            raise ValueError("build descriptor archive_sha256 is invalid")
        size = descriptor.get("archive_size_bytes")
        if not isinstance(size, int) or isinstance(size, bool) or size <= 0:
            raise ValueError("build descriptor archive_size_bytes is invalid")
        result = dict(descriptor)
        result["archive_sha256"] = sha
        return result

    def ensure(self, client: TrainingControlClient, descriptor: Mapping[str, Any]) -> tuple[Path, Mapping[str, Any]]:
        descriptor = self._validated_descriptor(descriptor)
        identity = (
            descriptor["platform"] + "-" +
            descriptor["build_id"] + "-" +
            descriptor["archive_sha256"][:16]
        )
        install = self.builds / identity
        entrypoint = _safe_zip_member(install, descriptor["entrypoint"])
        manifest_path = install / ".bees-build.json"

        if manifest_path.is_file() and entrypoint.is_file():
            try:
                installed = json.loads(manifest_path.read_text(encoding="utf-8"))
            except json.JSONDecodeError:
                installed = {}
            if (
                installed.get("archive_sha256") == descriptor["archive_sha256"]
                and installed.get("build_id") == descriptor["build_id"]
                and installed.get("platform") == descriptor["platform"]
            ):
                self._set_current(descriptor, entrypoint)
                return entrypoint, descriptor

        temp_parent = Path(tempfile.mkdtemp(prefix=".bees-build-", dir=str(self.builds)))
        archive = temp_parent / "artifact.zip"
        extracted = temp_parent / "content"
        extracted.mkdir()
        try:
            client.download_artifact(descriptor["artifact_url"], archive)
            if archive.stat().st_size != descriptor["archive_size_bytes"]:
                raise ValueError("downloaded build archive size does not match server descriptor")
            actual_sha = file_sha256(archive)
            if actual_sha != descriptor["archive_sha256"]:
                raise ValueError("downloaded build archive failed SHA-256 verification")
            with zipfile.ZipFile(archive, "r") as bundle:
                for member in bundle.infolist():
                    target = _safe_zip_member(extracted, member.filename)
                    if member.is_dir():
                        target.mkdir(parents=True, exist_ok=True)
                        continue
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with bundle.open(member, "r") as source, target.open("wb") as output:
                        shutil.copyfileobj(source, output, length=1024 * 1024)
                    mode = (member.external_attr >> 16) & 0o777
                    if mode:
                        target.chmod(mode)
            candidate = _safe_zip_member(extracted, descriptor["entrypoint"])
            if not candidate.is_file():
                raise ValueError(
                    f"canonical build archive is missing entrypoint {descriptor['entrypoint']!r}"
                )
            if os.name != "nt":
                candidate.chmod(candidate.stat().st_mode | stat.S_IXUSR)
            (extracted / ".bees-build.json").write_text(
                json.dumps(descriptor, indent=2, sort_keys=True) + "\n",
                encoding="utf-8",
            )
            if install.exists():
                shutil.rmtree(install)
            os.replace(extracted, install)
        finally:
            shutil.rmtree(temp_parent, ignore_errors=True)

        entrypoint = _safe_zip_member(install, descriptor["entrypoint"])
        self._set_current(descriptor, entrypoint)
        return entrypoint, descriptor

    def _set_current(self, descriptor: Mapping[str, Any], entrypoint: Path) -> None:
        self.root.mkdir(parents=True, exist_ok=True)
        value = {
            "platform": descriptor["platform"],
            "build_id": descriptor["build_id"],
            "archive_sha256": descriptor["archive_sha256"],
            "entrypoint": str(entrypoint),
        }
        temporary = self.current_manifest.with_suffix(".tmp")
        temporary.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        os.replace(temporary, self.current_manifest)


def default_heartbeat(
    *,
    trainer_id: str,
    role: str,
    platform: str,
    process_state: str,
    applied_revision: int,
    build: Optional[Mapping[str, Any]],
    last_error: str = "",
) -> dict[str, object]:
    return {
        "trainer_id": trainer_id,
        "role": role,
        "platform": platform,
        "hostname": socket.gethostname(),
        "pid": os.getpid(),
        "process_state": process_state,
        "applied_revision": applied_revision,
        "build_id": str(build.get("build_id", "")) if build else "",
        "build_sha256": str(build.get("archive_sha256", "")) if build else "",
        "last_error": last_error,
    }
