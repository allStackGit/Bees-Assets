"""Shared BeesServer training-control client and managed build installer.

The control plane is intentionally ordinary HTTPS/HTTP JSON plus authenticated artifact downloads.
Workers never trust a build merely because its build id matches: the downloaded archive is verified
against the server-advertised SHA-256 before it is extracted into a versioned directory.
"""

from __future__ import annotations

import hashlib
import math
import json
import os
import re
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


CONTROL_SCHEMA_VERSION = 5
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
        if not math.isfinite(self.timeout) or self.timeout <= 0:
            raise ValueError("training-control timeout must be finite and positive")

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
        canonical_build_id = value.get("canonical_build_id")
        desired_build_id = value.get("desired_build_id", canonical_build_id)
        run_id = value.get("run_id", "")
        compatibility_key = value.get("compatibility_key", "")
        worker_env_count = value.get("worker_env_count")
        env_optimizer = value.get("env_optimizer")
        if not isinstance(revision, int) or isinstance(revision, bool) or revision < 0:
            raise ControlRejected("training-control revision is invalid")
        if (
            not isinstance(lease_seconds, (int, float))
            or isinstance(lease_seconds, bool)
            or not math.isfinite(lease_seconds)
            or lease_seconds <= 0
        ):
            raise ControlRejected("training-control lease_seconds is invalid")
        if not isinstance(environment_args, list) or any(
            not isinstance(item, str) for item in environment_args
        ):
            raise ControlRejected("training-control environment_args is invalid")
        if not isinstance(canonical_build_id, str) or not isinstance(desired_build_id, str):
            raise ControlRejected("training-control build identity is invalid")
        if not isinstance(run_id, str) or not isinstance(compatibility_key, str):
            raise ControlRejected("training-control run identity is invalid")
        if worker_env_count is not None and (
            not isinstance(worker_env_count, int)
            or isinstance(worker_env_count, bool)
            or not 1 <= worker_env_count <= 64
        ):
            raise ControlRejected("training-control worker_env_count is invalid")
        if env_optimizer is not None and not isinstance(env_optimizer, Mapping):
            raise ControlRejected("training-control env_optimizer is malformed")
        if value.get("desired_mode") not in ("training", "stopped", "inference"):
            raise ControlRejected("training-control desired_mode is invalid")
        build = value.get("build")
        if build is not None:
            if not isinstance(build, Mapping):
                raise ControlRejected("training-control build descriptor is malformed")
            if not desired_build_id or build.get("build_id") != desired_build_id:
                raise ControlRejected(
                    "training-control build does not match desired_build_id"
                )
        prepare_build = value.get("prepare_build")
        if prepare_build is not None and not isinstance(prepare_build, Mapping):
            raise ControlRejected("training-control prepare_build descriptor is malformed")
        pending = value.get("pending_release")
        if pending is not None and not isinstance(pending, Mapping):
            raise ControlRejected("training-control pending_release is malformed")
        return value

    def heartbeat(self, payload: Mapping[str, object]) -> Mapping[str, Any]:
        _headers, body = self._request("POST", "/v1/heartbeat", payload=payload)
        return self._decode_state(body)

    def upload_log_chunk(
        self,
        *,
        trainer_id: str,
        run_id: str,
        relative_path: str,
        offset: int,
        data: bytes,
        reset: bool = False,
    ) -> int:
        query = urllib.parse.urlencode({
            "trainer_id": trainer_id,
            "run_id": run_id,
            "path": relative_path,
            "offset": int(offset),
            "reset": "1" if reset else "0",
        })
        request = urllib.request.Request(
            self.base_url + "/v1/log?" + query,
            data=data,
            method="POST",
            headers={
                "Authorization": "Bearer " + self.token,
                "Content-Type": "application/octet-stream",
                "Accept": "application/json",
            },
        )
        try:
            with urllib.request.urlopen(request, timeout=max(self.timeout, 30.0)) as response:
                value = json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            raw = exc.read().decode("utf-8", errors="replace")
            if exc.code == 409:
                try:
                    value = json.loads(raw)
                except json.JSONDecodeError:
                    value = {}
                expected = value.get("expected_offset")
                if isinstance(expected, int) and expected >= 0:
                    return -expected - 1
            raise ControlRejected(
                f"training log upload failed: HTTP {exc.code}: {raw}"
            ) from exc
        except (urllib.error.URLError, TimeoutError, ConnectionError, OSError) as exc:
            raise ControlUnavailable(str(exc)) from exc
        next_offset = value.get("next_offset") if isinstance(value, Mapping) else None
        if not isinstance(next_offset, int) or next_offset < 0:
            raise ControlRejected("training log upload returned invalid next_offset")
        return next_offset


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
    MAX_RETAINED_BUILDS = 4

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
        required = ("role", "platform", "build_id", "archive_sha256", "artifact_url", "entrypoint")
        for key in required:
            if not isinstance(descriptor.get(key), str) or not descriptor[key]:
                raise ValueError(f"build descriptor {key} is missing")
        if descriptor["role"] not in ("dedicated", "full-game"):
            raise ValueError("build descriptor role is invalid")
        for key in ("platform", "build_id"):
            if re.fullmatch(r"[A-Za-z0-9._-]+", descriptor[key]) is None:
                raise ValueError(f"build descriptor {key} contains unsafe characters")
        if not descriptor["artifact_url"].startswith("/v1/artifact/"):
            raise ValueError("build descriptor artifact_url is outside the control service")
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
        return self._materialize(client, descriptor, activate=True)

    def prepare(self, client: TrainingControlClient, descriptor: Mapping[str, Any]) -> tuple[Path, Mapping[str, Any]]:
        return self._materialize(client, descriptor, activate=False)

    def is_prepared(self, descriptor: Mapping[str, Any]) -> bool:
        descriptor = self._validated_descriptor(descriptor)
        identity = (
            descriptor["role"] + "-" +
            descriptor["platform"] + "-" +
            descriptor["build_id"] + "-" +
            descriptor["archive_sha256"][:16]
        )
        install = self.builds / identity
        entrypoint = _safe_zip_member(install, descriptor["entrypoint"])
        manifest_path = install / ".bees-build.json"
        if not manifest_path.is_file() or not entrypoint.is_file():
            return False
        try:
            installed = json.loads(manifest_path.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            return False
        if not isinstance(installed, Mapping):
            return False
        return (
            installed.get("archive_sha256") == descriptor["archive_sha256"]
            and installed.get("role") == descriptor["role"]
            and installed.get("build_id") == descriptor["build_id"]
            and installed.get("platform") == descriptor["platform"]
        )

    def _materialize(
        self,
        client: TrainingControlClient,
        descriptor: Mapping[str, Any],
        *,
        activate: bool,
    ) -> tuple[Path, Mapping[str, Any]]:
        descriptor = self._validated_descriptor(descriptor)
        identity = (
            descriptor["role"] + "-" +
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
            if not isinstance(installed, Mapping):
                installed = {}
            if (
                installed.get("archive_sha256") == descriptor["archive_sha256"]
                and installed.get("role") == descriptor["role"]
                and installed.get("build_id") == descriptor["build_id"]
                and installed.get("platform") == descriptor["platform"]
            ):
                if activate:
                    self._set_current(descriptor, entrypoint)
                self._prune({install})
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
        if activate:
            self._set_current(descriptor, entrypoint)
        self._prune({install})
        return entrypoint, descriptor

    def _prune(self, preserve: set[Path]) -> None:
        keep = {path.resolve() for path in preserve}
        current = self.current()
        if current:
            raw_entrypoint = str(current.get("entrypoint", "")).strip()
            if raw_entrypoint:
                try:
                    relative = Path(raw_entrypoint).resolve().relative_to(self.builds)
                    if relative.parts:
                        keep.add((self.builds / relative.parts[0]).resolve())
                except (OSError, ValueError):
                    pass

        candidates = []
        try:
            children = list(self.builds.iterdir())
        except OSError:
            return
        for child in children:
            if child.is_symlink() or not child.is_dir() or child.name.startswith("."):
                continue
            if not (child / ".bees-build.json").is_file():
                continue
            try:
                modified = child.stat().st_mtime_ns
            except OSError:
                continue
            resolved_child = child.resolve()
            try:
                resolved_child.relative_to(self.builds.resolve())
            except ValueError:
                continue
            if resolved_child.parent != self.builds.resolve():
                continue
            candidates.append((modified, resolved_child))
        candidates.sort(reverse=True)
        keep.update(path for _, path in candidates[: self.MAX_RETAINED_BUILDS])

        for _, path in candidates:
            if path in keep:
                continue
            try:
                shutil.rmtree(path)
            except OSError:
                # A previous build can still be briefly open during a compatible cutover.
                # Retention is best-effort and will retry after the next materialization.
                continue

    def _set_current(self, descriptor: Mapping[str, Any], entrypoint: Path) -> None:
        self.root.mkdir(parents=True, exist_ok=True)
        value = {
            "role": descriptor["role"],
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
    prepared_build_id: str = "",
    preparation_error: str = "",
    last_error: str = "",
    metrics: Optional[Mapping[str, object]] = None,
    worker_capacity: Optional[Mapping[str, object]] = None,
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
        "prepared_build_id": str(prepared_build_id or ""),
        "preparation_error": str(preparation_error or ""),
        "last_error": last_error,
        "metrics": dict(metrics or {}),
        "worker_capacity": dict(worker_capacity or {}),
    }
