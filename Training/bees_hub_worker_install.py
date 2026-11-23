"""Install a built Unity simulation player as an immutable local Hub release.

This never modifies a live Unity binary or starts/stops an Experiment Hub job.
An operator explicitly activates a fully verified release; enrolled agents
read that selection only on their next start.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import sys
import tempfile
import zipfile

from bees_unity_worker_identity import unity_worker_tree_sha256

SHA = re.compile(r"^[0-9a-f]{64}$")
BUILD_ID = re.compile(r"^[A-Za-z0-9._-]{1,160}$")
PLATFORMS = {
    "linux": ("LinuxPlayer", "Linux", "Bees RL Training.x86_64", "rl-linux.zip"),
    "win32": ("WindowsPlayer", "Windows", "Bees RL Training.exe", "rl-windows.zip"),
}
MAX_MANIFEST_BYTES = 32768
MAX_ARCHIVE_BYTES = 16 * 1024 ** 3
MAX_FILES = 20000
MAX_UNPACKED_BYTES = 16 * 1024 ** 3


def sha256_file(filename: Path) -> str:
    digest = hashlib.sha256()
    with filename.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1048576), b""):
            digest.update(chunk)
    return digest.hexdigest()


def platform() -> tuple[str, str, str, str]:
    value = PLATFORMS.get(sys.platform)
    if value is None:
        raise ValueError("Bees Hub Unity installation supports Linux and Windows only.")
    return value


def safe_root(location: Path) -> Path:
    if not location.is_absolute() or location == Path(location.anchor):
        raise ValueError("Use a dedicated absolute Bees Unity worker installation root.")
    if location.is_symlink() or (location.exists() and location.resolve() != location):
        raise ValueError("Bees Unity installation root cannot be redirected.")
    location.mkdir(parents=True, exist_ok=True)
    if location.resolve() != location:
        raise ValueError("Bees Unity installation root contains redirected directories.")
    return location


def manifest_record(filename: Path) -> tuple[bytes, dict, dict, str]:
    if not filename.is_absolute() or filename.is_symlink() or (
        not filename.is_file() or filename.stat().st_size > MAX_MANIFEST_BYTES
    ):
        raise ValueError("Expected an absolute regular offline Unity worker manifest.")
    raw = filename.read_bytes()
    doc = json.loads(raw)
    platform_name, folder, entrypoint, archive_name = platform()
    if not isinstance(doc, dict) or doc.get("schema_version") != 1 or (
        doc.get("kind") != "offline-unity-workers" or
        doc.get("deployment_status") != "unpublished" or
        not BUILD_ID.fullmatch(str(doc.get("build_id", ""))) or
        not isinstance(doc.get("artifacts"), list) or
        not 1 <= len(doc["artifacts"]) <= 2
    ):
        raise ValueError("Unsupported or incomplete Bees offline worker build manifest.")
    platforms = []
    for item in doc["artifacts"]:
        if not isinstance(item, dict) or (
            item.get("platform") not in ("LinuxPlayer", "WindowsPlayer") or
            not SHA.fullmatch(str(item.get("player_tree_sha256", ""))) or
            not SHA.fullmatch(str(item.get("archive_sha256", ""))) or
            type(item.get("archive_bytes")) is not int or
            not 0 < item["archive_bytes"] <= MAX_ARCHIVE_BYTES
        ):
            raise ValueError("Invalid Unity player archive or content hash in the manifest.")
        platforms.append(item["platform"])
    if len(platforms) != len(set(platforms)):
        raise ValueError("Duplicate Unity platforms in the offline worker manifest.")
    match = next((item for item in doc["artifacts"]
                  if item["platform"] == platform_name), None)
    if match is None or match.get("folder") != folder or (
        match.get("entrypoint") != entrypoint or
        match.get("archive") != archive_name or
        match.get("role") != "dedicated"
    ):
        raise ValueError("The offline manifest has no valid build for this machine OS.")
    return raw, doc, match, hashlib.sha256(raw).hexdigest()


def installed(root: Path, release: str) -> dict:
    if not SHA.fullmatch(release):
        raise ValueError("Unity release ID must be a full SHA-256.")
    platform_name, folder, entrypoint, _archive = platform()
    base = root / "releases" / release
    receipt = base / "worker-build-manifest.json"
    if not base.is_dir() or base.is_symlink() or (
        receipt.is_symlink() or not receipt.is_file()
    ):
        raise ValueError("Requested immutable Unity release is not installed.")
    raw, doc, record, sha = manifest_record(receipt)
    if sha != release:
        raise ValueError("Installed Unity manifest differs from its content address.")
    binary = base / folder / entrypoint
    if binary.is_symlink() or not binary.is_file() or binary.resolve() != binary:
        raise ValueError("Installed Unity player executable is missing or redirected.")
    tree_sha = unity_worker_tree_sha256(binary)
    if tree_sha != record["player_tree_sha256"]:
        raise ValueError("Installed Unity player content no longer matches its release.")
    return {
        "schema_version": 1,
        "release_sha256": release,
        "build_id": doc["build_id"],
        "platform": platform_name,
        "player_tree_sha256": tree_sha,
        "manifest_path": str(receipt),
        "worker_executable": str(binary),
    }


def extract_archive(archive: Path, directory: Path, entrypoint: str) -> None:
    with zipfile.ZipFile(archive) as bundle:
        members = bundle.infolist()
        if not members or len(members) > MAX_FILES:
            raise ValueError("Unity worker archive contains too many entries.")
        seen = set()
        unpacked = 0
        for item in members:
            name = item.filename
            if (not name or name.startswith("/") or
                name.endswith("/") and name.rstrip("/") == "" or
                "\\" in name or ":" in name or "\x00" in name or
                not name.isascii() or any(not 32 <= ord(ch) <= 126 for ch in name)):
                raise ValueError("Unity worker ZIP contains an unsafe filename.")
            rel = PurePosixPath(name.rstrip("/"))
            if any(part in (".", "..", "") for part in name.rstrip("/").split("/")) or (
                len(rel.parts) > 12 or name.rstrip("/") in seen
            ):
                raise ValueError("Unity worker ZIP contains traversal or duplicate members.")
            seen.add(name.rstrip("/"))
            flags = item.external_attr >> 16
            kind = stat.S_IFMT(flags)
            if kind not in (0, stat.S_IFREG, stat.S_IFDIR) or (
                item.flag_bits & 1 or item.file_size < 0
            ):
                raise ValueError("Unity worker ZIP contains a link, special file, or encrypted member.")
            if item.is_dir():
                (directory / rel).mkdir(parents=True, exist_ok=True)
                continue
            unpacked += item.file_size
            if unpacked > MAX_UNPACKED_BYTES:
                raise ValueError("Unity worker ZIP exceeds the maximum unpacked size.")
            dest = directory / rel
            dest.parent.mkdir(parents=True, exist_ok=True)
            with bundle.open(item) as source, dest.open("xb") as output:
                shutil.copyfileobj(source, output, length=1048576)
            if os.name != "nt":
                mode = (flags & 0o777) if kind else 0o644
                if rel.as_posix() == entrypoint:
                    mode |= 0o700
                dest.chmod(mode & 0o777)
        if entrypoint not in seen:
            raise ValueError("Unity worker archive does not contain the dedicated executable.")


def install(root: Path, manifest: Path, archive: Path) -> dict:
    root = safe_root(root)
    raw, _doc, record, release = manifest_record(manifest)
    if not archive.is_absolute() or archive.is_symlink() or not archive.is_file():
        raise ValueError("Unity worker ZIP must be an absolute regular local archive.")
    if (archive.stat().st_size != record["archive_bytes"] or
        sha256_file(archive) != record["archive_sha256"]):
        raise ValueError("Unity worker ZIP does not match its exact offline build manifest.")
    releases = root / "releases"
    releases.mkdir(parents=True, exist_ok=True)
    if releases.is_symlink():
        raise ValueError("Unity worker releases directory cannot be redirected.")
    destination = releases / release
    if destination.exists():
        result = installed(root, release)
        return {**result, "already_installed": True}
    stage = Path(tempfile.mkdtemp(prefix=".bees-unity-", dir=releases))
    try:
        folder = platform()[1]
        player = stage / folder
        player.mkdir()
        extract_archive(archive, player, record["entrypoint"])
        if (unity_worker_tree_sha256(player / record["entrypoint"]) !=
                record["player_tree_sha256"]):
            raise ValueError("Extracted Unity player contents differ from the offline build.")
        target = stage / "worker-build-manifest.json"
        with target.open("xb") as output:
            output.write(raw)
        if sha256_file(target) != release:
            raise ValueError("Copied build manifest differs from its source.")
        try:
            stage.rename(destination)
        except FileExistsError:
            return {**installed(root, release), "already_installed": True}
    finally:
        if stage.exists():
            shutil.rmtree(stage)
    return {**installed(root, release), "already_installed": False}


def activate(root: Path, release: str) -> dict:
    root = safe_root(root)
    record = installed(root, release)
    # The marker contains no arbitrary executable or user-supplied paths.
    # Running agents retain their already-open worker until restarted.
    selection = {
        key: record[key]
        for key in ("schema_version", "release_sha256", "build_id", "platform",
                    "player_tree_sha256")
    }
    temporary = root / (".current-" + release[:12] + "-" + str(os.getpid()) + ".json")
    try:
        with temporary.open("x", encoding="utf-8") as output:
            json.dump(selection, output, sort_keys=True)
            output.write("\n")
        if os.name != "nt":
            temporary.chmod(0o600)
        os.replace(temporary, root / "current.json")
    finally:
        temporary.unlink(missing_ok=True)
    return {**record, "activated": True, "agent_restart_required": True}


def current(root: Path) -> dict:
    root = safe_root(root)
    selection = root / "current.json"
    if selection.is_symlink() or not selection.is_file() or (
        selection.stat().st_size > 4096
    ):
        raise ValueError("No active immutable Bees Unity release is selected.")
    record = json.loads(selection.read_text(encoding="utf-8"))
    if not isinstance(record, dict) or (
        set(record) != {"schema_version", "release_sha256", "build_id",
                        "platform", "player_tree_sha256"} or
        record.get("schema_version") != 1
    ):
        raise ValueError("Installed Unity release selector is malformed.")
    result = installed(root, record["release_sha256"])
    for key in ("schema_version", "release_sha256", "build_id", "platform",
                "player_tree_sha256"):
        if record[key] != result[key]:
            raise ValueError("Activated Unity worker differs from its pinned receipt.")
    return result


def prune_verified_transfer(root: Path, manifest: Path, archive: Path,
                            release: str) -> bool:
    """Discard only this operator-owned incoming ZIP after verified install.

    No installed release, job artifacts or arbitrary supplied files qualify
    for removal; an interrupted installation leaves incoming files intact.
    """
    expected = root / "incoming" / release
    if (manifest.parent != expected or archive.parent != expected or
        expected.is_symlink() or not expected.is_dir() or
        expected.resolve() != expected or
        (root / "incoming").is_symlink()):
        raise ValueError("Source cleanup is limited to the exact verified release's incoming directory.")
    try:
        shutil.rmtree(expected)
        return True
    except OSError as exc:
        print("Bees Hub Unity staging cleanup unavailable: " + str(exc),
              file=sys.stderr)
        return False


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("install", "activate", "current"))
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--archive", type=Path)
    parser.add_argument("--release")
    parser.add_argument("--activate", action="store_true")
    parser.add_argument("--cleanup-staging", action="store_true")
    args = parser.parse_args(argv)
    try:
        if args.command == "install":
            if args.manifest is None or args.archive is None or args.release is not None:
                raise ValueError("install requires --manifest and --archive, without --release.")
            if args.cleanup_staging:
                # Validate deletion scope before performing any state changes.
                incoming = args.root / "incoming" / sha256_file(args.manifest)
                if (args.manifest.parent != incoming or
                    args.archive.parent != incoming or incoming.is_symlink() or
                    not incoming.is_dir() or incoming.resolve() != incoming):
                    raise ValueError("Cleanup may remove only the verified incoming release directory.")
            result = install(args.root, args.manifest, args.archive)
            if args.activate:
                result = activate(args.root, result["release_sha256"])
            if args.cleanup_staging:
                result["staging_cleaned"] = prune_verified_transfer(
                    args.root, args.manifest, args.archive, result["release_sha256"]
                )
        elif args.command == "activate":
            if not args.release or args.archive or args.manifest or args.activate or args.cleanup_staging:
                raise ValueError("activate requires --release only.")
            result = activate(args.root, args.release)
        else:
            if args.archive or args.manifest or args.release or args.activate or args.cleanup_staging:
                raise ValueError("current accepts only --root.")
            result = current(args.root)
    except (OSError, ValueError, KeyError, TypeError, json.JSONDecodeError,
            zipfile.BadZipFile, RuntimeError) as exc:
        print("Bees Hub Unity installation: " + str(exc), file=sys.stderr)
        return 2
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
