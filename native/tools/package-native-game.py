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

ROOT = Path(__file__).resolve().parents[2]
AUDIO_SOURCE = ROOT / "native/crates/bees-host/src/game_audio.rs"
CONVERTER = ROOT / "native/tools/import-ship-sprites.py"
EXAMPLES = ("native-duel", "native-skirmish", "native-two-player")
CAMPAIGN = "native-frontier-campaign"
CAMPAIGN_RUNS = ("native-campaign-assault.run.json",)


def write_game_library(output: Path) -> None:
    """One native UI entry point for persistent campaigns and quick battles."""
    catalog = {
        "library_schema": 1,
        "games": [
            {"id": "frontier", "title": "Frontier Fleet Campaign",
             "kind": "campaign", "config": CAMPAIGN + ".json"},
            {"id": "duel", "title": "Native Duel",
             "kind": "battle", "config": "native-duel.run.json"},
            {"id": "skirmish", "title": "Native Skirmish",
             "kind": "battle", "config": "native-skirmish.run.json"},
            {"id": "two-player", "title": "Native Two-Player Duel",
             "kind": "battle", "config": "native-two-player.run.json"},
        ],
    }
    (output / "native-library.json").write_text(
        json.dumps(catalog, indent=2) + "\n", encoding="utf-8",
    )



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
        if binary_name.endswith(".exe"):
            launcher = output / f"play-{name}.cmd"
            launcher.write_text(
                "@echo off\r\n"
                "cd /d \"%~dp0\"\r\n"
                f"set \"BEES_RUN=runs\\{name}-%RANDOM%-%RANDOM%\"\r\n"
                "if not exist runs mkdir runs\r\n"
                f"\"%~dp0{binary_name}\" play \"examples\\{name}.run.json\" "
                "--output-dir \"%BEES_RUN%\" %*\r\n",
                encoding="utf-8",
            )
        else:
            launcher = output / f"play-{name}.sh"
            launcher.write_text(
                "#!/bin/sh\n"
                "set -eu\n"
                'cd "$(dirname "$0")"\n'
                "mkdir -p runs\n"
                f'run="runs/{name}-$(date +%Y%m%d-%H%M%S)-$$"\n'
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
    args = parser.parse_args()
    binary = args.binary.resolve()
    output = args.output.resolve()
    if not binary.is_file() or binary.name not in ("bees", "bees.exe"):
        parser.error("Provide a compiled native graphical bees or bees.exe binary")
    if output.exists():
        parser.error("Output already exists; native releases are immutable")
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
        for name in EXAMPLES:
            for ext in ("run.json", "scene.json"):
                shutil.copy2(ROOT / "native/examples" / f"{name}.{ext}",
                             examples / f"{name}.{ext}")
        shutil.copy2(ROOT / "native/examples" / (CAMPAIGN + ".json"),
                     examples / (CAMPAIGN + ".json"))
        for name in CAMPAIGN_RUNS:
            shutil.copy2(ROOT / "native/examples" / name, examples / name)
        write_game_library(examples)
        # Remote controllers are optional Python/Tk tools. The actual
        # packaged native game and training core remain Python-free.
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
    print(f"Packaged {len(audio)} original audio tracks and native game artwork at {output}")


if __name__ == "__main__":
    main()
