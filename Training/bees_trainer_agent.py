"""Persistent Bees rollout-node agent controlled by BeesServer.

The agent itself is intentionally long-lived. It holds no optimizer state. While BeesServer grants a
valid lease it reconciles this machine to the server's desired build/configuration and runs the
existing elastic-WAN actor worker. If the control lease expires, the rollout process is terminated.
When BeesServer returns, the agent revalidates/synchronizes the canonical build and starts again.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform as platform_module
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path, PurePosixPath
from typing import Any, Mapping, Optional, Sequence


DEFAULT_CONTROL_PORT = 7148
DEFAULT_HEARTBEAT_SECONDS = 5.0
DEFAULT_LEASE_SECONDS = 30.0
DEFAULT_RECONNECT_SECONDS = 5.0
CHUNK_SIZE = 1024 * 1024


class ControlError(RuntimeError):
    pass


class BuildError(RuntimeError):
    pass


def canonical_json(value: Any) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(CHUNK_SIZE), b""):
            digest.update(chunk)
    return digest.hexdigest()


def normalize_platform(value: Optional[str] = None) -> str:
    raw = (value or sys.platform).strip().lower()
    if raw.startswith("linux") or raw in {"linux-x64", "linux64"}:
        return "linux-x64"
    if raw.startswith("win") or raw in {"windows", "windows-x64", "win-x64"}:
        return "windows-x64"
    if raw.startswith("darwin") or raw in {"mac", "macos", "macos-x64"}:
        return "macos-x64"
    raise ValueError(f"Unsupported trainer platform {value!r}.")


def safe_relative_path(value: str) -> PurePosixPath:
    if not isinstance(value, str) or not value:
        raise BuildError("Build manifest file path must be a non-empty string.")
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in {"", ".", ".."} for part in path.parts):
        raise BuildError(f"Unsafe build manifest path: {value!r}")
    return path


def read_token(path: str | os.PathLike[str]) -> str:
    token = Path(path).expanduser().read_text(encoding="utf-8").strip()
    if not 32 <= len(token) <= 1024 or any(character.isspace() for character in token):
        raise ValueError("Trainer-control token must contain 32-1024 non-whitespace characters.")
    return token


def lease_expired(last_success: Optional[float], lease_seconds: float, now: Optional[float] = None) -> bool:
    if last_success is None:
        return True
    current = time.monotonic() if now is None else now
    return current - last_success >= lease_seconds


def ssh_control_command(
    executable: str,
    target: str,
    local_port: int,
    remote_port: int,
    options: Sequence[str],
) -> list[str]:
    command = [
        executable,
        "-N",
        "-o",
        "ExitOnForwardFailure=yes",
        "-o",
        "ServerAliveInterval=15",
        "-o",
        "ServerAliveCountMax=2",
        "-L",
        f"127.0.0.1:{local_port}:127.0.0.1:{remote_port}",
    ]
    for option in options:
        command.extend(["-o", option])
    command.append(target)
    return command


def build_actor_command(
    *,
    python: str,
    assets_root: Path,
    actor_id: int,
    env_count: int,
    ssh_target: str,
    env_path: Path,
    wan_auth_token_file: Path,
    broker_port: int,
    local_broker_port: int,
    local_base_port: int,
    torch_device: str,
    graphics: bool,
    ssh_executable: str,
    ssh_options: Sequence[str],
) -> list[str]:
    worker = assets_root / "Training" / "bees_elastic_wan_actor_worker.py"
    if not worker.is_file():
        raise FileNotFoundError(f"Elastic WAN actor helper does not exist: {worker}")
    command = [
        python,
        str(worker),
        f"--actor-id={actor_id}",
        f"--envs={env_count}",
        f"--ssh={ssh_target}",
        f"--env={env_path}",
        f"--auth-token-file={wan_auth_token_file}",
        f"--broker-port={broker_port}",
        f"--local-port={local_broker_port}",
        f"--local-base-port={local_base_port}",
        f"--torch-device={torch_device}",
        f"--ssh-executable={ssh_executable}",
    ]
    for option in ssh_options:
        command.extend(["--ssh-option", option])
    if graphics:
        command.append("--graphics")
    return command


class ControlClient:
    def __init__(self, base_url: str, token: str, timeout: float = 15.0):
        self.base_url = base_url.rstrip("/")
        self.token = token
        self.timeout = timeout

    def _request(self, method: str, path: str, payload: Optional[Mapping[str, Any]] = None):
        data = None
        headers = {"Authorization": f"Bearer {self.token}"}
        if payload is not None:
            data = json.dumps(payload, separators=(",", ":")).encode("utf-8")
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(
            self.base_url + path,
            data=data,
            headers=headers,
            method=method,
        )
        try:
            return urllib.request.urlopen(request, timeout=self.timeout)
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")
            raise ControlError(f"control HTTP {exc.code}: {detail[:500]}") from exc
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            raise ControlError(f"control transport unavailable: {exc}") from exc

    def heartbeat(self, payload: Mapping[str, Any]) -> Mapping[str, Any]:
        with self._request("POST", "/v1/trainers/heartbeat", payload) as response:
            value = json.load(response)
        if not isinstance(value, dict):
            raise ControlError("heartbeat response is not a JSON object")
        return value

    def manifest(self, platform: str) -> Mapping[str, Any]:
        query = urllib.parse.urlencode({"platform": platform})
        with self._request("GET", f"/v1/trainers/build/manifest?{query}") as response:
            value = json.load(response)
        if not isinstance(value, dict):
            raise ControlError("build manifest response is not a JSON object")
        return value

    def download_to(
        self,
        platform: str,
        relative_path: str,
        destination: Path,
        expected_size: int,
        expected_sha256: str,
    ) -> None:
        query = urllib.parse.urlencode({"platform": platform, "path": relative_path})
        destination.parent.mkdir(parents=True, exist_ok=True)
        digest = hashlib.sha256()
        total = 0
        with self._request("GET", f"/v1/trainers/build/file?{query}") as response, destination.open("wb") as output:
            header_hash = response.headers.get("X-Bees-Sha256")
            if header_hash and header_hash != expected_sha256:
                raise BuildError(f"Server hash header changed for {relative_path}.")
            while True:
                chunk = response.read(CHUNK_SIZE)
                if not chunk:
                    break
                output.write(chunk)
                digest.update(chunk)
                total += len(chunk)
        if total != expected_size or digest.hexdigest() != expected_sha256:
            raise BuildError(f"Downloaded trainer build file failed integrity validation: {relative_path}")


class BuildInstaller:
    def __init__(self, root: Path, platform: str, client: ControlClient):
        self.root = root.expanduser().resolve()
        self.platform = platform
        self.client = client
        self.builds = self.root / "builds"
        self.current_file = self.root / "current.json"
        self._verified_build_id: Optional[str] = None
        self.builds.mkdir(parents=True, exist_ok=True)

    def current(self) -> Optional[Mapping[str, Any]]:
        try:
            value = json.loads(self.current_file.read_text(encoding="utf-8"))
        except (FileNotFoundError, json.JSONDecodeError, OSError):
            return None
        return value if isinstance(value, dict) else None

    def _validate_manifest(self, manifest: Mapping[str, Any], expected_build_id: str) -> list[Mapping[str, Any]]:
        if manifest.get("schema_version") != 1 or manifest.get("platform") != self.platform:
            raise BuildError("Trainer build manifest schema/platform is incompatible.")
        files = manifest.get("files")
        entrypoint = manifest.get("entrypoint")
        if not isinstance(files, list) or not files or not isinstance(entrypoint, str):
            raise BuildError("Trainer build manifest is incomplete.")
        normalized_files = []
        seen = set()
        for item in files:
            if not isinstance(item, dict):
                raise BuildError("Trainer build file entry must be an object.")
            relative = safe_relative_path(item.get("path"))
            relative_text = relative.as_posix()
            if relative_text in seen:
                raise BuildError(f"Duplicate build file path: {relative_text}")
            seen.add(relative_text)
            size = item.get("size")
            sha = item.get("sha256")
            mode = item.get("mode", 0o644)
            if not isinstance(size, int) or size < 0:
                raise BuildError(f"Invalid file size for {relative_text}")
            if not isinstance(sha, str) or len(sha) != 64 or any(c not in "0123456789abcdef" for c in sha):
                raise BuildError(f"Invalid file hash for {relative_text}")
            if not isinstance(mode, int) or mode < 0 or mode > 0o777:
                raise BuildError(f"Invalid file mode for {relative_text}")
            normalized_files.append({"path": relative_text, "size": size, "sha256": sha, "mode": mode})
        if entrypoint not in seen:
            raise BuildError("Trainer build entrypoint is not included in files.")
        identity = {
            "schema_version": manifest.get("schema_version"),
            "platform": manifest.get("platform"),
            "game_build_version": manifest.get("game_build_version"),
            "entrypoint": entrypoint,
            "files": normalized_files,
        }
        manifest_sha = hashlib.sha256(canonical_json(identity).encode("utf-8")).hexdigest()
        build_id = f"build-{manifest_sha[:24]}"
        if build_id != expected_build_id or manifest.get("build_id") != expected_build_id:
            raise BuildError("Trainer build identity does not match desired build.")
        if manifest.get("manifest_sha256") != manifest_sha:
            raise BuildError("Trainer build manifest integrity check failed.")
        return normalized_files

    @staticmethod
    def _verify_directory(directory: Path, files: Sequence[Mapping[str, Any]]) -> bool:
        expected = {item["path"] for item in files}
        actual = {
            path.relative_to(directory).as_posix()
            for path in directory.rglob("*")
            if path.is_file()
        }
        if actual != expected:
            return False
        for item in files:
            candidate = directory.joinpath(*PurePosixPath(item["path"]).parts)
            try:
                if candidate.stat().st_size != item["size"] or sha256_file(candidate) != item["sha256"]:
                    return False
            except OSError:
                return False
        return True

    def _write_current(self, value: Mapping[str, Any]) -> None:
        self.root.mkdir(parents=True, exist_ok=True)
        fd, temp_name = tempfile.mkstemp(prefix="current.", suffix=".tmp", dir=str(self.root))
        try:
            with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
                json.dump(value, handle, indent=2, sort_keys=True)
                handle.write("\n")
                handle.flush()
                os.fsync(handle.fileno())
            os.replace(temp_name, self.current_file)
        finally:
            try:
                os.unlink(temp_name)
            except FileNotFoundError:
                pass

    def ensure(self, desired: Mapping[str, Any]) -> Path:
        build_id = desired.get("build_id")
        if not isinstance(build_id, str) or not build_id.startswith("build-"):
            raise BuildError("Desired trainer build has an invalid build_id.")
        manifest = self.client.manifest(self.platform)
        files = self._validate_manifest(manifest, build_id)
        final = self.builds / build_id
        entrypoint = str(manifest["entrypoint"])
        current = self.current()
        if (
            self._verified_build_id == build_id
            and current
            and current.get("build_id") == build_id
            and final.joinpath(*PurePosixPath(entrypoint).parts).is_file()
        ):
            return final.joinpath(*PurePosixPath(entrypoint).parts)

        if not final.is_dir() or not self._verify_directory(final, files):
            if final.exists():
                shutil.rmtree(final)
            staging = self.builds / f".{build_id}.staging-{os.getpid()}"
            shutil.rmtree(staging, ignore_errors=True)
            staging.mkdir(parents=True)
            try:
                for item in files:
                    destination = staging.joinpath(*PurePosixPath(item["path"]).parts)
                    self.client.download_to(
                        self.platform,
                        item["path"],
                        destination,
                        item["size"],
                        item["sha256"],
                    )
                    try:
                        destination.chmod(item["mode"])
                    except OSError:
                        pass
                if not self._verify_directory(staging, files):
                    raise BuildError("Staged trainer build failed final directory verification.")
                os.replace(staging, final)
            finally:
                shutil.rmtree(staging, ignore_errors=True)

        metadata = {
            "schema_version": 1,
            "build_id": build_id,
            "platform": self.platform,
            "manifest_sha256": manifest["manifest_sha256"],
            "entrypoint": entrypoint,
            "game_build_version": manifest.get("game_build_version"),
        }
        self._write_current(metadata)
        self._verified_build_id = build_id
        executable = final.joinpath(*PurePosixPath(entrypoint).parts)
        if not executable.is_file():
            raise BuildError(f"Installed trainer entrypoint is missing: {executable}")
        return executable


class ManagedProcess:
    def __init__(self):
        self.process: Optional[subprocess.Popen[Any]] = None

    def running(self) -> bool:
        return self.process is not None and self.process.poll() is None

    def start(self, command: Sequence[str], cwd: Path) -> None:
        if self.running():
            return
        self.process = subprocess.Popen(list(command), cwd=str(cwd))
        print(f"[Bees trainer agent] rollout process started pid={self.process.pid}.")

    def stop(self) -> None:
        process = self.process
        self.process = None
        if process is None or process.poll() is not None:
            return
        print(f"[Bees trainer agent] stopping rollout process pid={process.pid}.")
        try:
            process.terminate()
            process.wait(timeout=10)
        except Exception:
            try:
                process.kill()
                process.wait(timeout=5)
            except Exception:
                pass


class TrainerAgent:
    def __init__(self, args: argparse.Namespace):
        self.args = args
        self.stop_event = threading.Event()
        self.platform = normalize_platform(args.platform)
        self.token = read_token(args.control_token_file)
        self.control_url = args.control_url or f"http://127.0.0.1:{args.local_control_port}"
        self.client = ControlClient(self.control_url, self.token, timeout=args.control_timeout_seconds)
        self.installer = BuildInstaller(Path(args.install_root), self.platform, self.client)
        self.actor = ManagedProcess()
        self.control_tunnel: Optional[subprocess.Popen[Any]] = None
        self.last_success: Optional[float] = None
        self.lease_seconds = DEFAULT_LEASE_SECONDS
        self.applied_key: Optional[tuple[str, str, str]] = None
        self.state = "idle"

    def _ensure_control_tunnel(self) -> None:
        if self.args.control_url:
            return
        if self.control_tunnel is not None and self.control_tunnel.poll() is None:
            return
        if self.control_tunnel is not None:
            print(
                f"[Bees trainer agent] control SSH tunnel exited ({self.control_tunnel.returncode}); reconnecting.",
                file=sys.stderr,
            )
        command = ssh_control_command(
            self.args.ssh_executable,
            self.args.ssh,
            self.args.local_control_port,
            self.args.control_port,
            self.args.ssh_option,
        )
        self.control_tunnel = subprocess.Popen(command, stdin=subprocess.DEVNULL)
        deadline = time.monotonic() + self.args.tunnel_startup_seconds
        while time.monotonic() < deadline and not self.stop_event.is_set():
            if self.control_tunnel.poll() is not None:
                raise ControlError(f"control SSH tunnel exited during startup ({self.control_tunnel.returncode})")
            time.sleep(0.05)

    def _heartbeat_payload(self) -> Mapping[str, Any]:
        current = self.installer.current() or {}
        return {
            "schema_version": 1,
            "node_id": self.args.node_id,
            "role": "rollout",
            "platform": self.platform,
            "state": self.state,
            "actor_id": self.args.actor_id,
            "env_count": self.args.envs,
            "build_id": current.get("build_id"),
            "config_revision": self.applied_key[1] if self.applied_key else None,
        }

    def _actor_command(self, env_path: Path) -> list[str]:
        return build_actor_command(
            python=self.args.python,
            assets_root=Path(self.args.assets_root).expanduser().resolve(),
            actor_id=self.args.actor_id,
            env_count=self.args.envs,
            ssh_target=self.args.ssh,
            env_path=env_path,
            wan_auth_token_file=Path(self.args.wan_auth_token_file).expanduser().resolve(),
            broker_port=self.args.broker_port,
            local_broker_port=self.args.local_broker_port,
            local_base_port=self.args.local_base_port,
            torch_device=self.args.torch_device,
            graphics=self.args.graphics,
            ssh_executable=self.args.ssh_executable,
            ssh_options=self.args.ssh_option,
        )

    def _apply(self, response: Mapping[str, Any]) -> None:
        if response.get("schema_version") != 1:
            raise ControlError("Unsupported trainer-control schema.")
        epoch = response.get("server_epoch")
        lease = response.get("lease_seconds")
        desired = response.get("desired")
        if not isinstance(epoch, str) or not isinstance(lease, (int, float)) or lease <= 0 or not isinstance(desired, dict):
            raise ControlError("Trainer-control response is incomplete.")
        self.lease_seconds = float(lease)
        enabled = desired.get("training_enabled") is True
        config_revision = desired.get("config_revision")
        build = desired.get("build")
        if not enabled:
            self.actor.stop()
            self.state = "stopped-by-server"
            self.applied_key = None
            return
        if not isinstance(config_revision, str) or not isinstance(build, dict):
            self.actor.stop()
            self.state = "build-unavailable"
            raise ControlError(
                f"Server has no compatible build for {self.platform}; available={desired.get('available_platforms')}"
            )
        build_id = build.get("build_id")
        if not isinstance(build_id, str):
            raise ControlError("Desired build is missing build_id.")
        key = (epoch, config_revision, build_id)
        if self.applied_key != key:
            self.actor.stop()
            self.state = "synchronizing-build"
            env_path = self.installer.ensure(build)
            self.applied_key = key
            self.actor.start(self._actor_command(env_path), Path(self.args.assets_root).expanduser().resolve())
            self.state = "training"
            return
        if not self.actor.running():
            env_path = self.installer.ensure(build)
            self.actor.start(self._actor_command(env_path), Path(self.args.assets_root).expanduser().resolve())
        self.state = "training"

    def run(self) -> int:
        while not self.stop_event.is_set():
            try:
                self._ensure_control_tunnel()
                response = self.client.heartbeat(self._heartbeat_payload())
                self.last_success = time.monotonic()
                self._apply(response)
            except (ControlError, BuildError, OSError, ValueError) as exc:
                self.state = "control-unavailable"
                print(f"[Bees trainer agent] {exc}", file=sys.stderr)
                if self.actor.running() and lease_expired(self.last_success, self.lease_seconds):
                    print("[Bees trainer agent] server lease expired; stopping rollout training.")
                    self.actor.stop()
                    self.applied_key = None
            self.stop_event.wait(self.args.heartbeat_seconds)
        return 0

    def stop(self) -> None:
        self.stop_event.set()
        self.actor.stop()
        tunnel = self.control_tunnel
        self.control_tunnel = None
        if tunnel is not None and tunnel.poll() is None:
            try:
                tunnel.terminate()
                tunnel.wait(timeout=5)
            except Exception:
                try:
                    tunnel.kill()
                except Exception:
                    pass


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Keep one Bees elastic-WAN rollout node synchronized with BeesServer."
    )
    parser.add_argument("--actor-id", required=True, type=int)
    parser.add_argument("--envs", type=int, default=32)
    parser.add_argument("--ssh", required=True, help="SSH target for the BeesServer/central trainer machine.")
    parser.add_argument("--control-token-file", required=True)
    parser.add_argument("--wan-auth-token-file", required=True)
    parser.add_argument("--install-root", required=True, help="Local content-addressed trainer build store.")
    parser.add_argument("--node-id", default=socket.gethostname())
    parser.add_argument("--platform", default=normalize_platform())
    parser.add_argument("--assets-root", default=str(Path(__file__).resolve().parent.parent))
    parser.add_argument("--python", default=sys.executable)
    parser.add_argument("--control-url", default=None, help="Direct control URL; skips the control SSH tunnel when set.")
    parser.add_argument("--control-port", type=int, default=DEFAULT_CONTROL_PORT)
    parser.add_argument("--local-control-port", type=int, default=DEFAULT_CONTROL_PORT)
    parser.add_argument("--broker-port", type=int, default=55051)
    parser.add_argument("--local-broker-port", type=int, default=55051)
    parser.add_argument("--local-base-port", type=int, default=5005)
    parser.add_argument("--ssh-executable", default="ssh")
    parser.add_argument("--ssh-option", action="append", default=[])
    parser.add_argument("--torch-device", default="cpu")
    parser.add_argument("--graphics", action="store_true")
    parser.add_argument("--heartbeat-seconds", type=float, default=DEFAULT_HEARTBEAT_SECONDS)
    parser.add_argument("--control-timeout-seconds", type=float, default=10.0)
    parser.add_argument("--tunnel-startup-seconds", type=float, default=1.0)
    return parser


def _validate_args(args: argparse.Namespace) -> None:
    if not 0 <= args.actor_id <= 11:
        raise ValueError("--actor-id must be in 0-11.")
    if not 1 <= args.envs <= 64:
        raise ValueError("--envs must be in 1-64.")
    for name in ("control_port", "local_control_port", "broker_port", "local_broker_port", "local_base_port"):
        value = getattr(args, name)
        if not 1 <= value <= 65535:
            raise ValueError(f"--{name.replace('_', '-')} must be in 1-65535.")
    if args.local_base_port + args.envs - 1 > 65535:
        raise ValueError("Local ML-Agents worker ports would exceed 65535.")
    if args.heartbeat_seconds <= 0 or args.control_timeout_seconds <= 0 or args.tunnel_startup_seconds < 0:
        raise ValueError("Heartbeat/timeout/tunnel timings are outside valid bounds.")
    read_token(args.control_token_file)
    Path(args.wan_auth_token_file).expanduser().resolve(strict=True)
    Path(args.assets_root).expanduser().resolve(strict=True)


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _parser().parse_args(argv)
    try:
        _validate_args(args)
    except (ValueError, OSError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    agent = TrainerAgent(args)

    def request_stop(_signum: int, _frame: Any) -> None:
        agent.stop_event.set()

    old_sigint = signal.signal(signal.SIGINT, request_stop)
    old_sigterm = signal.signal(signal.SIGTERM, request_stop)
    try:
        return agent.run()
    finally:
        agent.stop()
        signal.signal(signal.SIGINT, old_sigint)
        signal.signal(signal.SIGTERM, old_sigterm)


if __name__ == "__main__":
    raise SystemExit(main())
