"""Explicit operator-driven transfer of one verified Bees Unity build to Hub machines.

This is not a Hub job action. The operator supplies a private, approved SSH
inventory, Tailscale address and strict host-key pins for each machine. Only
the requested Unity platform archive, manifest and immutable installer are
transferred. Each target independently verifies the build before activation.
Active Hub services are never restarted or modified by this command.
"""
from __future__ import annotations

import argparse
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import subprocess
import sys

from bees_release_runtime import verify_runtime

SHA = re.compile(r"^[0-9a-f]{64}$")
ID = re.compile(r"^[a-z][a-z0-9_-]{0,47}$")
BUILD_ID = re.compile(r"^[A-Za-z0-9._-]{1,160}$")
USER = re.compile(r"^[a-z_][a-z0-9_-]{0,31}$")
SAFE_PATH = re.compile(r"^[A-Za-z0-9_./:\\-]{2,256}$")
SCHEMA_FIELDS = {
    "machine_id", "platform", "tailnet_ip", "ssh_user", "ssh_port",
    "ssh_identity_file", "ssh_known_hosts_file", "python_path",
    "install_root",
}
PLATFORM_ARCHIVE = {"linux": ("LinuxPlayer", "rl-linux.zip"),
                    "windows": ("WindowsPlayer", "rl-windows.zip")}
PLATFORM_PLAYER = {
    "LinuxPlayer": ("Linux", "Bees RL Training.x86_64", "rl-linux.zip"),
    "WindowsPlayer": ("Windows", "Bees RL Training.exe", "rl-windows.zip"),
}
MAX_MANIFEST = 32768
MAX_ARCHIVE = 16 * 1024 ** 3


def sha256_file(file: Path) -> str:
    digest = hashlib.sha256()
    with file.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1048576), b""):
            digest.update(chunk)
    return digest.hexdigest()


def absolute_local(value: str, label: str) -> Path:
    file = Path(value)
    if not file.is_absolute() or file.is_symlink() or (
        file.resolve() != file or not file.is_file()
    ):
        raise ValueError(label + " needs a real absolute local file.")
    return file


def remote_path(value: object, platform: str) -> str:
    if not isinstance(value, str) or not SAFE_PATH.fullmatch(value) or (
        "\\" in value or "//" in value or ".." in value.split("/")
    ):
        raise ValueError("Remote Python and install paths need absolute, safe ASCII names without spaces.")
    if platform == "linux":
        if not value.startswith("/") or value == "/":
            raise ValueError("Linux remote path must be absolute, below the root directory.")
    else:
        if not re.match(r"^[A-Za-z]:/[A-Za-z0-9_./-]+$", value):
            raise ValueError("Windows remote path must be a C:/...-style absolute path.")
    return value.rstrip("/")


def remote_identity(raw: object) -> dict:
    if not isinstance(raw, dict) or set(raw) != SCHEMA_FIELDS:
        raise ValueError("Each machine needs a fixed ID, platform, Tailnet SSH login, Python and install root.")
    platform = raw["platform"]
    if platform not in PLATFORM_ARCHIVE or not ID.fullmatch(str(raw["machine_id"])) or (
        not USER.fullmatch(str(raw["ssh_user"]))
    ):
        raise ValueError("Invalid enrolled machine, platform or SSH account.")
    try:
        ip = ipaddress.ip_address(raw["tailnet_ip"])
        if ip not in ipaddress.ip_network("100.64.0.0/10") or (
            str(ip) != raw["tailnet_ip"]
        ):
            raise ValueError("Noncanonical private Tailnet IP")
    except (ValueError, TypeError) as exc:
        raise ValueError("Bees worker distribution requires a canonical Tailscale IPv4.") from exc
    port = raw["ssh_port"]
    if type(port) is not int or not 1 <= port <= 65535:
        raise ValueError("Invalid machine OpenSSH port.")
    key = absolute_local(raw["ssh_identity_file"], "OpenSSH identity")
    known = absolute_local(raw["ssh_known_hosts_file"], "Pinned known_hosts")
    key_stat = key.stat()
    if (key_stat.st_uid != os.getuid() or key_stat.st_mode & 0o077 or
        not 16 <= key_stat.st_size <= 32768 or
        not 40 <= known.stat().st_size <= 1048576):
        raise ValueError("Bees distributor requires an owner-private SSH key and trusted known_hosts.")
    python = remote_path(raw["python_path"], platform)
    root = remote_path(raw["install_root"], platform)
    if root == python or python.startswith(root + "/"):
        raise ValueError("Remote Python must not live inside the changeable Unity install root.")
    return {**raw, "python_path": python, "install_root": root}


def ssh_base(machine: dict, command: str) -> list[str]:
    return [
        "ssh", "-T", "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes",
        "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=10",
        "-o", "UserKnownHostsFile=" + machine["ssh_known_hosts_file"],
        "-i", machine["ssh_identity_file"], "-p", str(machine["ssh_port"]),
        machine["ssh_user"] + "@" + machine["tailnet_ip"], command,
    ]


def run(command: list[str], label: str, timeout: int = 300) -> str:
    result = subprocess.run(
        command, capture_output=True, text=True, encoding="utf-8",
        errors="replace", timeout=timeout, check=False,
    )
    if result.returncode != 0:
        raise RuntimeError(label + " failed: " +
                           (result.stderr or result.stdout)[-900:])
    return result.stdout


def scp(machine: dict, files: list[Path], destination: str) -> None:
    base = [
        "scp", "-q", "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes",
        "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=10",
        "-o", "UserKnownHostsFile=" + machine["ssh_known_hosts_file"],
        "-i", machine["ssh_identity_file"], "-P", str(machine["ssh_port"]),
    ]
    target = machine["ssh_user"] + "@" + machine["tailnet_ip"] + ":" + destination
    run(base + [str(file) for file in files] + [target], "Upload Unity player archives",
        timeout=1800)


def source_manifest(filename: Path) -> tuple[dict, str]:
    if filename.name != "worker-build-manifest.json" or (
        not filename.is_absolute() or filename.is_symlink() or
        not filename.is_file() or filename.stat().st_size > MAX_MANIFEST
    ):
        raise ValueError("Build manifest must be a small real absolute file.")
    raw = filename.read_bytes()
    record = json.loads(raw)
    if not isinstance(record, dict) or (
        record.get("schema_version") != 1 or
        record.get("kind") != "offline-unity-workers" or
        record.get("deployment_status") != "unpublished" or
        not BUILD_ID.fullmatch(str(record.get("build_id", ""))) or
        not isinstance(record.get("artifacts"), list) or
        not 1 <= len(record["artifacts"]) <= 2
    ):
        raise ValueError("Expected one offline Bees Unity worker build manifest.")
    platforms = []
    for item in record["artifacts"]:
        if isinstance(item, dict):
            platforms.append(item.get("platform"))
        if not isinstance(item, dict) or (
            item.get("platform") not in PLATFORM_PLAYER or
            (item.get("folder"), item.get("entrypoint"), item.get("archive")) !=
                PLATFORM_PLAYER.get(item.get("platform")) or
            item.get("role") != "dedicated" or
            not SHA.fullmatch(str(item.get("player_tree_sha256", ""))) or
            not SHA.fullmatch(str(item.get("archive_sha256", ""))) or
            type(item.get("archive_bytes")) is not int or
            not 0 < item["archive_bytes"] <= MAX_ARCHIVE
        ):
            raise ValueError("Invalid Unity build archive in release manifest.")
    if len(set(platforms)) != len(platforms):
        raise ValueError("Offline worker manifest duplicates one Unity target.")
    runtime = record.get("runtime")
    if runtime is not None and (
        not isinstance(runtime, dict) or
        set(runtime) != {"archive", "archive_sha256", "archive_bytes", "runtime_version"} or
        runtime["archive"] != "bees-runtime.zip" or
        not SHA.fullmatch(str(runtime["archive_sha256"])) or
        not SHA.fullmatch(str(runtime["runtime_version"])) or
        type(runtime["archive_bytes"]) is not int or
        not 0 < runtime["archive_bytes"] <= 512 * 1024 ** 2
    ):
        raise ValueError("Frozen Bees runtime metadata does not match its build manifest.")
    return record, hashlib.sha256(raw).hexdigest()


def stage_remote(machine: dict, target: str) -> None:
    if machine["platform"] == "linux":
        command = "mkdir -p " + target
    else:
        command = ('powershell.exe -NoProfile -NonInteractive -Command ' +
                   '"New-Item -ItemType Directory -Force -Path ' +
                   chr(39) + target + chr(39) + ' | Out-Null"')
    run(ssh_base(machine, command), "Create private Unity transfer staging directory")


def distribute(manifest: Path, inventory: Path, selected: set[str],
               apply: bool, activate: bool) -> list[dict]:
    doc, digest = source_manifest(manifest)
    machines = json.loads(absolute_local(str(inventory), "Machine inventory").read_text(
        encoding="utf-8"
    ))
    if not isinstance(machines, dict) or (
        set(machines) != {"schema_version", "machines"} or
        machines["schema_version"] != 1 or
        not isinstance(machines["machines"], list) or
        not 1 <= len(machines["machines"]) <= 32
    ):
        raise ValueError("Bees Unity machine inventory must have a bounded machine list.")
    # A targeted rollout should not depend on local SSH credentials for
    # unrelated machines, but inventory IDs must remain unique and valid.
    rows = machines["machines"]
    if any(not isinstance(entry, dict) or
           not ID.fullmatch(str(entry.get("machine_id", ""))) for entry in rows):
        raise ValueError("Bees Unity machine inventory contains an invalid machine ID.")
    ids = [entry["machine_id"] for entry in rows]
    if len(set(ids)) != len(ids):
        raise ValueError("Bees Unity machine inventory contains duplicate machine IDs.")
    if selected and not selected.issubset(set(ids)):
        raise ValueError("Selected Bees Unity machine is absent from the operator inventory.")
    machine_rows = [remote_identity(entry) for entry in rows
                    if not selected or entry["machine_id"] in selected]
    source_dir = manifest.parent
    scripts = [
        absolute_local(str(Path(__file__).resolve().with_name(name)),
                       "Native Unity installer")
        for name in ("bees_hub_worker_install.py", "bees_unity_worker_identity.py",
                     "bees_release_runtime.py")
    ]
    runtime = doc.get("runtime")
    source_runtime = source_dir / "bees-runtime.zip"
    if runtime:
        absolute_local(str(source_runtime), "Frozen Bees Python runtime ZIP")
        if (source_runtime.stat().st_size != runtime["archive_bytes"] or
            sha256_file(source_runtime) != runtime["archive_sha256"]):
            raise ValueError("Frozen Bees Python training runtime changed after packaging.")
        # Do the full payload/source-identity verification locally before any
        # remote activation, instead of finding an invalid runtime on machine N.
        verified = verify_runtime(
            source_runtime,
            expected_sha256=runtime["archive_sha256"],
            expected_version=runtime["runtime_version"],
            expected_build_id=doc["build_id"],
        )
        if verified["source_commit"] != doc.get("source_commit"):
            raise ValueError("Frozen Bees runtime and Unity source revisions differ.")
    # Validate every selected target and every required local archive before
    # changing any machine. A missing Windows ZIP must not leave Linux machines
    # on a newly activated, only partially distributed release.
    plan = []
    prepared = []
    for machine in machine_rows:
        if selected and machine["machine_id"] not in selected:
            continue
        platform_name, archive_name = PLATFORM_ARCHIVE[machine["platform"]]
        match = next((item for item in doc["artifacts"]
                      if item["platform"] == platform_name), None)
        if match is None or match.get("archive") != archive_name:
            raise ValueError("Built Unity release lacks " + platform_name +
                             " for " + machine["machine_id"])
        archive = absolute_local(str(source_dir / archive_name), "Unity build ZIP")
        if archive.stat().st_size != match["archive_bytes"] or (
            sha256_file(archive) != match["archive_sha256"]
        ):
            raise ValueError("Built Unity archive changed before distribution: " +
                             archive_name)
        staging = machine["install_root"] + "/incoming/" + digest
        prepared.append((machine, staging, archive, match))
        plan.append({
            "machine_id": machine["machine_id"],
            "platform": machine["platform"],
            "tailnet_ip": machine["tailnet_ip"],
            "release_sha256": digest,
            "archive": archive_name,
            "transfer_bytes": (archive.stat().st_size +
                               (runtime["archive_bytes"] if runtime else 0) +
                               manifest.stat().st_size +
                               sum(script.stat().st_size for script in scripts)),
            "activate": activate,
            "agent_restart_required": activate,
        })
    if not apply:
        return plan
    for index, (machine, staging, archive, match) in enumerate(prepared):
        stage_remote(machine, staging)
        scp(machine, [*scripts, manifest, archive] +
            ([source_runtime] if runtime else []), staging + "/")
        install = (
            machine["python_path"] + " -B " + staging +
            "/bees_hub_worker_install.py install --root " +
            machine["install_root"] + " --manifest " + staging +
            "/worker-build-manifest.json --archive " + staging + "/" +
            archive_name + (" --activate" if activate else "") +
            " --cleanup-staging"
        )
        output = run(ssh_base(machine, install), "Install verified Unity player on " +
                     machine["machine_id"], timeout=1800)
        try:
            remote = json.loads(output.strip().splitlines()[-1])
        except (ValueError, IndexError) as exc:
            raise RuntimeError("Remote Bees Unity installer returned no verified receipt.") from exc
        if (remote.get("release_sha256") != digest or
            remote.get("platform") != platform_name or
            remote.get("player_tree_sha256") != match["player_tree_sha256"] or
            runtime and (remote.get("runtime_sha256") != runtime["archive_sha256"] or
                         remote.get("runtime_version") != runtime["runtime_version"]) or
            activate and remote.get("activated") is not True):
            raise RuntimeError("Remote Bees Unity installation receipt does not match built release.")
        plan[index]["staging_cleaned"] = remote.get("staging_cleaned") is True
    return plan


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--inventory", type=Path, required=True)
    parser.add_argument("--machine", action="append", default=[])
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--activate", action="store_true")
    args = parser.parse_args(argv)
    if args.activate and not args.apply:
        parser.error("--activate requires --apply.")
    try:
        results = distribute(
            args.manifest, args.inventory, set(args.machine),
            args.apply, args.activate,
        )
    except (OSError, ValueError, RuntimeError, TypeError, KeyError,
            subprocess.TimeoutExpired) as exc:
        print("Bees Hub Unity distribution: " + str(exc), file=sys.stderr)
        return 2
    print(json.dumps({"applied": args.apply, "machines": results}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
