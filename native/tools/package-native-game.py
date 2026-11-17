#!/usr/bin/env python3
"""Package a native Bees game with its original art and audio, without Unity.

Build the graphical binary separately, then run:
    python3 native/tools/package-native-game.py \
      --binary native/target/release/bees --output dist/bees-native

Pillow is required to convert the original tracked Unity PNGs; neither
Python nor Unity is required to play the packaged Windows/Linux game.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from package_policy_manifest import export_model

ROOT = Path(__file__).resolve().parents[2]
AUDIO_SOURCE = ROOT / "native/crates/bees-host/src/game_audio.rs"
CONVERTER = ROOT / "native/tools/import-ship-sprites.py"
EXAMPLES = ("native-duel", "native-skirmish", "native-two-player", "native-wave-defense")

POLICY_RUNTIME_MODULES = (
    "bees_mlagents_ppo_compat.py",
    "bees_mlagents_structured_policy.py",
    "bees_learner_stall_diagnostics.py",
    "bees_log_categories.py",
    "bees_mlagents_factorized_poca.py",
    "bees_process_safety.py",
)


def package_policy_runtime(destination: Path) -> None:
    worker = destination / "native/tools/policy_worker.py"
    worker.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(ROOT / "native/tools/policy_worker.py", worker)
    modules = destination / "Training"
    modules.mkdir()
    for name in POLICY_RUNTIME_MODULES:
        shutil.copy2(ROOT / "Training" / name, modules / name)


def package_game_library(
    destination: Path, library: Path, *,
    include_policies: bool = False, python_command: str = "python3",
    policy_device: str | None = None,
) -> bool:
    """Copy exactly one selected native library and its transitive JSON.

    This works for built-in Levels and newly authored user campaigns. The
    package still has one immutable examples/native-library.json entry
    point; source filenames under the selected library's content root
    retain their original relative hierarchy.
    """
    library = library.resolve(strict=True)
    if library.suffix != ".json" or not library.is_file():
        raise ValueError("native game package requires a JSON library file")
    source_root = library.parent
    pending = [(library.name, "library")]
    copied: dict[str, str] = {}
    copied_manifests: set[str] = set()
    has_policy = False
    while pending:
        relative, kind = pending.pop()
        path = Path(relative)
        if (not relative or path.is_absolute() or
                any(part in ("", ".", "..") for part in path.parts) or
                len(path.parts) > 8 or len(relative) > 255 or
                "\\" in relative or ":" in relative or path.suffix != ".json"):
            raise ValueError(f"Unsafe native game asset reference: {relative!r}")
        label = path.as_posix()
        if label in copied:
            if copied[label] != kind:
                raise ValueError(f"Native game asset has conflicting roles: {label}")
            continue
        if len(copied) >= 256:
            raise ValueError("Native game library exceeds 256 source JSON files")
        source = (source_root / path).resolve(strict=True)
        if not source.is_relative_to(source_root) or not source.is_file():
            raise ValueError(f"Native game asset escaped its content root: {label}")
        data = json.loads(source.read_text(encoding="utf-8"))
        if kind == "library":
            if data.get("library_schema") != 1 or not isinstance(data.get("games"), list):
                raise ValueError("Native game library schema is invalid")
            for game in data["games"]:
                if game["kind"] not in ("campaign", "battle"):
                    raise ValueError("Unknown native game library item")
                pending.append((str(path.parent / game["config"]),
                                game["kind"] if game["kind"] == "campaign" else "run"))
        elif kind == "campaign":
            for battle in data["battles"]:
                pending.append((str(path.parent / battle["config"]), "run"))
                route = (battle.get("mission") or {}).get("on_victory")
                if route is not None:
                    pending.append((str(path.parent / route["config"]), "run"))
        elif kind == "run":
            if data.get("hivemind") or data.get("model_updates") or (
                    data.get("recover_notifications") or
                    data.get("recover_outcome_journals")):
                raise ValueError(
                    f"Desktop release requires an offline, immutable Level: {label}"
                )
            manifest = data.get("model_manifest")
            if manifest:
                if not include_policies:
                    raise ValueError(
                        f"Trained battle {label} requires --include-policies"
                    )
                if not isinstance(manifest, str) or not manifest.strip():
                    raise ValueError("model_manifest must name a native manifest")
                original = (source.parent / manifest).resolve(strict=True)
                if not original.is_file():
                    raise ValueError("trained policy manifest must be a regular file")
                digest = hashlib.sha256(original.read_bytes()).hexdigest()
                if digest not in copied_manifests:
                    models_root = destination.parent / "assets/policies"
                    models_root.mkdir(parents=True, exist_ok=True)
                    # The existing portable model exporter verifies all
                    # checkpoints and preserves exact policy slots and ABI.
                    export_model(original, models_root / digest,
                                 target_device=policy_device)
                    copied_manifests.add(digest)
                data["python"] = python_command
                data["policy_worker"] = "../native/tools/policy_worker.py"
                data["model_manifest"] = f"../assets/policies/{digest}/manifest.json"
                data["policy_capture"] = None
                has_policy = True
            elif data.get("python") or data.get("policy_worker"):
                raise ValueError(
                    f"Release has incomplete model-worker routing: {label}"
                )
            for level in data["levels"]:
                pending.append((str(path.parent / level["scene"]), "scene"))
        elif kind != "scene":
            raise ValueError(f"Unknown native package asset kind: {kind}")
        # A custom library can be named library.json, but the packaged
        # launcher has one stable entry point across Windows and Linux.
        output = (destination / "native-library.json"
                  if kind == "library" else destination / path)
        output.parent.mkdir(parents=True, exist_ok=True)
        if kind == "run" and data.get("model_manifest"):
            output.write_text(
                json.dumps(data, indent=2, ensure_ascii=False) + "\n",
                encoding="utf-8",
            )
        else:
            shutil.copy2(source, output)
        copied[label] = kind
    return has_policy


def original_audio_paths() -> list[tuple[Path, Path]]:
    """The native client source owns the actual clip list.

    Asset references are fixed source string literals. Keep the packaged
    audio files in sync with that list, rather than maintaining a second
    potentially outdated manifest of clip names in this script.
    """
    source = AUDIO_SOURCE.read_text(encoding="utf-8")
    names = sorted(set(re.findall(r'"([^"\n]+\.mp3)"', source)))
    if len(names) < 20:
        raise ValueError("Could not extract the native game audio references")
    refs = []
    for name in names:
        relative = Path(name)
        if relative.is_absolute() or ".." in relative.parts:
            raise ValueError(f"Invalid native audio resource path: {name}")
        # The renderer also has a repository-only fallback for Titania's
        # source file. The release instead ships the single same recording
        # in its portable assets/audio/Titania directory.
        if relative == Path("Resources/Music/Titania/Titania Source.mp3"):
            continue
        if len(relative.parts) == 1:
            relative = Path("Sound Effects") / relative
        destination = Path("assets/audio") / relative
        if relative == Path("Titania/Titania Source.mp3"):
            source_path = ROOT / "Resources/Music/Titania/Titania Source.mp3"
        else:
            source_path = ROOT / "Music" / relative
        if not source_path.is_file():
            raise FileNotFoundError(f"Missing original Bees audio: {source_path}")
        refs.append((source_path, destination))
    return refs


def write_launchers(output: Path, binary_name: str) -> None:
    for name in EXAMPLES:
        if not (output / "examples" / f"{name}.run.json").is_file():
            continue
        if binary_name.endswith(".exe"):
            launcher = output / f"play-{name}.cmd"
            launcher.write_text(
                "@echo off\r\n"
                "setlocal\r\n"
                'cd /d "%~dp0"\r\n'
                'if defined LOCALAPPDATA (set "BEES_SAVE=%LOCALAPPDATA%\\BeesNative") '
                'else (set "BEES_SAVE=%USERPROFILE%\\BeesNative")\r\n'
                'if not exist "%BEES_SAVE%\\battles" mkdir "%BEES_SAVE%\\battles"\r\n'
                f'set "BEES_RUN=%BEES_SAVE%\\battles\\{name}-%RANDOM%-%RANDOM%"\r\n'
                f'"%~dp0{binary_name}" play "examples\\{name}.run.json" '
                '--output-dir "%BEES_RUN%" %*\r\n',
                encoding="utf-8",
            )
        else:
            launcher = output / f"play-{name}.sh"
            launcher.write_text(
                "#!/bin/sh\n"
                "set -eu\n"
                'cd "$(dirname "$0")"\n'
                'save="${XDG_DATA_HOME:-$HOME/.local/share}/bees-native"\n'
                'mkdir -p "$save/battles"\n'
                f'run="$save/battles/{name}-$(date +%Y%m%d-%H%M%S)-$$"\n'
                f'exec "./{binary_name}" play "examples/{name}.run.json" '
                '--output-dir "$run" "$@"\n',
                encoding="utf-8",
            )
            launcher.chmod(0o755)
    # Primary entry point: persistent player saves live outside the
    # immutable game distribution on both operating systems.
    if binary_name.endswith(".exe"):
        launcher = output / "play-bees.cmd"
        launcher.write_text(
            "@echo off\r\n"
            "setlocal\r\n"
            'cd /d "%~dp0"\r\n'
            'if defined LOCALAPPDATA (set "BEES_SAVE=%LOCALAPPDATA%\\BeesNative") '
            'else (set "BEES_SAVE=%USERPROFILE%\\BeesNative")\r\n'
            'if not exist "%BEES_SAVE%" mkdir "%BEES_SAVE%"\r\n'
            f'"%~dp0{binary_name}" library "examples\\native-library.json" '
            '--output-dir "%BEES_SAVE%" %*\r\n',
            encoding="utf-8",
        )
    else:
        launcher = output / "play-bees.sh"
        launcher.write_text(
            "#!/bin/sh\n"
            "set -eu\n"
            'cd "$(dirname "$0")"\n'
            'save="${XDG_DATA_HOME:-$HOME/.local/share}/bees-native"\n'
            'mkdir -p "$save"\n'
            f'exec "./{binary_name}" library "examples/native-library.json" '
            '--output-dir "$save" "$@"\n',
            encoding="utf-8",
        )
        launcher.chmod(0o755)


def create_manifest(output: Path) -> None:
    files = []
    for path in sorted(output.rglob("*")):
        if not path.is_file() or path.name == "manifest.json":
            continue
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        files.append({
            "path": path.relative_to(output).as_posix(),
            "bytes": path.stat().st_size,
            "sha256": digest.hexdigest(),
        })
    (output / "manifest.json").write_text(
        json.dumps({
            "format": "bees-native-game-package",
            "version": 1,
            "content": files,
        }, indent=2) + "\n",
        encoding="utf-8",
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True,
                        help="Existing graphical bees or bees.exe executable")
    parser.add_argument("--output", type=Path, required=True,
                        help="New distribution folder; will not overwrite")
    parser.add_argument("--library", type=Path,
                        default=ROOT / "native/examples/native-library.json",
                        help="native game library to package (built-in or Unity-free authored campaign)")
    parser.add_argument("--include-policies", action="store_true",
                        help="include SHA-verified model manifests, weights and the native worker")
    parser.add_argument("--python-command",
                        help="bare target-machine Python executable (python3, python, py)")
    parser.add_argument("--policy-device", choices=("cpu", "cuda"),
                        help="target inference device (cpu works on Linux/Windows, cuda needs supported torch)")
    args = parser.parse_args()
    if args.policy_device is not None and not args.include_policies:
        parser.error("--policy-device requires --include-policies")
    binary = args.binary.resolve()
    output = args.output.resolve()
    if not binary.is_file() or binary.name not in ("bees", "bees.exe"):
        parser.error("Provide a compiled native graphical bees or bees.exe binary")
    if output.exists():
        parser.error("Output already exists; native releases are immutable")
    python_command = args.python_command or (
        "python" if binary.suffix.lower() == ".exe" else "python3"
    )
    if not re.fullmatch(r"[A-Za-z0-9_.-]+", python_command):
        parser.error("--python-command must be a single executable name from PATH")
    output.parent.mkdir(parents=True, exist_ok=True)

    audio = original_audio_paths()
    staging = Path(tempfile.mkdtemp(prefix=".bees-native-", dir=output.parent))
    try:
        shutil.copy2(binary, staging / binary.name)
        assets = staging / "assets"
        assets.mkdir()
        subprocess.run([
            sys.executable, str(CONVERTER),
            "--ship-output", str(assets / "ship-sprites.rgba"),
            "--projectile-output", str(assets / "projectile-sprites.rgba"),
            "--asteroid-output", str(assets / "asteroid-sprites.rgba"),
        ], cwd=ROOT, check=True)
        for source, relative in audio:
            destination = staging / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, destination)
        examples = staging / "examples"
        examples.mkdir()
        policy_used = package_game_library(
            examples, args.library, include_policies=args.include_policies,
            python_command=python_command, policy_device=args.policy_device,
        )
        if policy_used:
            package_policy_runtime(staging)
        # Remote controllers are optional Python/Tk tools. The canonical
        # simulation core stays Rust-native; trained policy inference
        # requires local Python/NumPy and its selected model backend.
        clients = staging / "clients"
        clients.mkdir()
        for name in ("remote_play_client.py", "remote_play_window.py"):
            shutil.copy2(ROOT / "native/tools" / name, clients / name)
        write_launchers(staging, binary.name)
        create_manifest(staging)
        # Publish only a complete, freshly validated package and preserve
        # the old release when another process already owns output.
        if output.exists():
            raise FileExistsError(output)
        os.rename(staging, output)
    finally:
        if staging.exists():
            shutil.rmtree(staging)
    print(f"Packaged {len(audio)} original audio tracks, native game artwork"
          f" and {'trained-policy assets' if policy_used else 'game-only assets'}"
          f" at {output}")


if __name__ == "__main__":
    main()
