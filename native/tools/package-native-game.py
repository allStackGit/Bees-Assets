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
from pathlib import Path, PurePosixPath
from urllib.parse import urlsplit

from package_policy_manifest import (
    export_model, verify_package as verify_policy_package,
    POLICY_WORKER_SOURCES, MLAGENTS_ACTOR_SOURCES,
)

ROOT = Path(__file__).resolve().parents[2]
AUDIO_SOURCE = ROOT / "native/crates/bees-host/src/game_audio.rs"
CONVERTER = ROOT / "native/tools/import-ship-sprites.py"
EXAMPLES = (
    "native-duel", "native-skirmish", "native-two-player",
    "native-wave-defense", "native-escort-convoy", "native-fire-tank-defense",
)


# These names and signatures are the native client's external atlas formats.
# Keep invocation and verification driven by one release inventory.
ARTWORK_ATLASES = (
    ("ship", "ship-sprites.rgba", b"BSS2"),
    ("beacon", "beacon-enemy.rgba", b"BSS2"),
    ("map-background", "map-backgrounds.rgba", b"BSS1"),
    ("map-fog", "map-fog.rgba", b"BSS1"),
    ("portrait", "dialogue-portraits.rgba", b"BDP1"),
    ("projectile", "projectile-sprites.rgba", b"BPS2"),
    ("projectile-impact", "projectile-impacts.rgba", b"BPS2"),
    ("asteroid", "asteroid-sprites.rgba", b"BAS1"),
    ("animation", "ship-animations.rgba", b"BSA1"),
    ("remains", "ship-remains.rgba", b"BSR1"),
    ("deck", "carrier-decks.rgba", b"BCD1"),
    ("queen", "queen-parts.rgba", b"BSS1"),
    ("effects", "ship-effects.rgba", b"BSS1"),
    ("queen-explosion", "queen-explosion.rgba", b"BSR1"),
)


# Training-capable distributions keep the exact canonical Python learner,
# native Rust workspace and authored physical assets alongside the client.
# Python, ML-Agents, PyTorch, Cargo and Node (for Hub) remain target-machine
# prerequisites; ordinary game-only distributions remain small and Python-free.
PROTECTED_SOURCE_NAMES = {
    "results", ".results", "Demonstrations", "Logs", "TrainingHistory~",
    "target", ".git", "__pycache__",
}



def validate_portable_hivemind(value: dict | None) -> None:
    """Permit native gameplay's original HiveMind connection, never a saved session.

    Network commands still run through the real native WebSocket worker and
    CombatWorld; a release carries no bearer credential or remembered server
    game identity. Apply the same contract when creating and verifying it.
    """
    if value is None:
        return
    allowed = {
        "endpoint", "user_id", "version", "auth_ticket_env",
        "allow_insecure_loopback", "reconnect_game_id",
        "issue_source_commands", "shutdown_flush_seconds",
    }
    if not isinstance(value, dict) or set(value) - allowed:
        raise ValueError("invalid native release HiveMind configuration")
    endpoint = value.get("endpoint")
    if not isinstance(endpoint, str) or not endpoint or any(
            ord(char) < 33 for char in endpoint):
        raise ValueError("native release HiveMind endpoint is invalid")
    try:
        url = urlsplit(endpoint)
        host = url.hostname
        port = url.port
    except ValueError as error:
        raise ValueError("native release HiveMind endpoint is invalid") from error
    if (not host or url.username is not None or url.password is not None or
            url.query or url.fragment or
            (port is not None and port == 0) or
            (url.path and not url.path.startswith("/"))):
        raise ValueError("native release must not contain HiveMind endpoint credentials")
    local = host.lower() in ("localhost", "127.0.0.1", "::1")
    if url.scheme != "wss" and not (
            url.scheme == "ws" and local and
            value.get("allow_insecure_loopback") is True):
        raise ValueError("native release HiveMind requires verified WSS or explicit loopback WS")
    if value.get("reconnect_game_id") is not None:
        raise ValueError("portable HiveMind games cannot reuse a server GameId")
    ticket = value.get("auth_ticket_env")
    if ticket is not None and (
            not isinstance(ticket, str) or
            re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", ticket) is None):
        raise ValueError("native release HiveMind ticket must reference an environment variable")
    if (type(value.get("user_id")) is not int or
            not 0 < value["user_id"] < 2**64 or
            type(value.get("version")) not in (float, int) or
            not 0 < value["version"] < 1e6 or
            type(value.get("issue_source_commands", False)) is not bool or
            type(value.get("allow_insecure_loopback", False)) is not bool or
            type(value.get("shutdown_flush_seconds", 5)) is not int or
            not 0 <= value.get("shutdown_flush_seconds", 5) <= 3600):
        raise ValueError("invalid native HiveMind gameplay configuration")


def executable_target(source: Path) -> tuple[str, str]:
    """Read the actual OS/CPU target, not an executable's arbitrary filename.

    A transferred native release is useful only when the visual game,
    headless rollout engine and canonical policy ABI manifest generator
    were built for the same operating system and CPU architecture.
    """
    with source.open("rb") as stream:
        header = stream.read(64)
        if header[:4] == b"\x7fELF":
            if len(header) < 20 or header[4] not in (1, 2) or header[5] not in (1, 2):
                raise ValueError(f"invalid native ELF executable header: {source}")
            byte_order = "little" if header[5] == 1 else "big"
            machine = int.from_bytes(header[18:20], byte_order)
            platform = "linux"
            architectures = {62: "x86_64", 183: "aarch64"}
        elif header[:2] == b"MZ":
            if len(header) < 64:
                raise ValueError(f"invalid native Windows executable header: {source}")
            offset = int.from_bytes(header[0x3c:0x40], "little")
            if not 64 <= offset <= 1024 * 1024:
                raise ValueError(f"invalid native PE header offset: {source}")
            stream.seek(offset)
            coff = stream.read(24)
            if len(coff) != 24 or coff[:4] != b"PE\x00\x00":
                raise ValueError(f"invalid native Windows PE executable: {source}")
            machine = int.from_bytes(coff[4:6], "little")
            platform = "windows"
            architectures = {0x8664: "x86_64", 0xAA64: "aarch64"}
        else:
            raise ValueError(f"native release requires a compiled Windows/Linux executable: {source}")
    if machine not in architectures:
        raise ValueError(f"unsupported native {platform} CPU architecture: {machine:#x}")
    return platform, architectures[machine]


def compiled_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def check_compiled_training_targets(
    game: Path, rollout: Path | None, manifest: Path | None,
    server: Path | None = None,
) -> tuple[str, str]:
    target = executable_target(game)
    if target[0] != ("windows" if game.suffix == ".exe" else "linux"):
        raise ValueError(f"native game binary name differs from executable platform: {game}")
    for label, binary in (("rollout", rollout), ("manifest", manifest),
                          ("dedicated server", server)):
        if binary is not None and executable_target(binary) != target:
            raise ValueError(
                f"native {label} binary has a different OS/CPU target from the game"
            )
    return target


def package_native_training_runtime(
    destination: Path, rollout_binary: Path | None,
    manifest_binary: Path | None,
) -> None:
    native = destination / "native"
    native.mkdir(exist_ok=True)
    for name in ("Cargo.toml", "Cargo.lock"):
        shutil.copy2(ROOT / "native" / name, native / name)
    shutil.copytree(
        ROOT / "native" / "crates", native / "crates",
        ignore=lambda folder, names: [
            name for name in names if name in PROTECTED_SOURCE_NAMES or
            name.endswith(".meta") or name.endswith(".pyc")
        ],
    )
    shutil.copytree(ROOT / "native" / "assets", native / "assets")
    shutil.copytree(ROOT / "native" / "examples", native / "examples")
    tools = native / "tools"
    tools.mkdir(exist_ok=True)
    for source in (ROOT / "native" / "tools").glob("*.py"):
        shutil.copy2(source, tools / source.name)
    training = destination / "Training"
    training.mkdir(exist_ok=True)
    for source in (ROOT / "Training").iterdir():
        if (source.is_file() and
                (source.suffix == ".js" or
                 (source.suffix == ".py" and
                  not source.name.endswith("_tests.py")) or
                 source.name in ("rl_1v1_config.yaml", "continual_learning_config.json"))):
            shutil.copy2(source, training / source.name)
    if rollout_binary is not None:
        target_name = "policy_rollout.exe" if (
            destination / "bees.exe").is_file() else "policy_rollout"
        if rollout_binary.name != target_name:
            raise ValueError(
                "native training rollout executable platform must match bees binary"
            )
        shutil.copy2(rollout_binary, destination / target_name)
    if manifest_binary is not None:
        target_name = "policy_manifest.exe" if (
            destination / "bees.exe").is_file() else "policy_manifest"
        if manifest_binary.name != target_name:
            raise ValueError(
                "native policy ABI generator platform must match bees binary"
            )
        shutil.copy2(manifest_binary, destination / target_name)


# Game-only releases can consume a deployed champion without needing
# the registry, evaluator or optimizer controller.
DEPLOYED_GAME_SOURCES = (
    "native/tools/native_deployed_game.py",
    "native/tools/native_game_registration.py",
    "native/tools/native_session_selection.py",
    "native/tools/native_curriculum.py",
    "native/tools/package_policy_manifest.py",
)


def package_policy_runtime(destination: Path) -> None:
    for name in POLICY_WORKER_SOURCES + MLAGENTS_ACTOR_SOURCES + DEPLOYED_GAME_SOURCES:
        target = destination / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / name, target)


def package_game_library(
    destination: Path, library: Path, *,
    include_policies: bool = False, python_command: str = "python3",
    policy_device: str | None = None,
    release_root: Path | None = None,
    copied_manifests: set[str] | None = None,
) -> bool:
    """Freeze a game library and all of its native source dependencies.

    Internal scenes retain their authored names; explicitly referenced
    external scenes/Stage assets are content-addressed inside the package.
    Every Level therefore resolves only assets shipped with the release.
    The launcher uses the one stable examples/native-library.json entry.
    """
    library = library.resolve(strict=True)
    if library.suffix != ".json" or not library.is_file():
        raise ValueError("native game package requires a JSON library file")
    source_root = library.parent
    # Nested original careers still share the ONE game distribution's
    # policy worker and content-addressed models. Their scene/run links
    # remain relative to each nested catalog, but trained weights do not
    # get duplicated into examples/.
    if release_root is None:
        release_root = destination.parent
    if copied_manifests is None:
        copied_manifests = set()
    pending = [(library.name, "library")]
    copied: dict[str, str] = {}
    has_policy = False

    def catalog_config(value: str, label: str) -> str:
        """Normalize source-authored catalog links for either target OS.

        Catalog links stay within the selected library's owned JSON tree.
        Unlike a run's external source scene, an absolute/parent catalog
        link cannot be packaged under a portable, self-contained identity.
        """
        if not isinstance(value, str) or not value:
            raise ValueError(f"Invalid native {label} configuration")
        normalized = value.replace("\\", "/")
        path = PurePosixPath(normalized)
        if (path.is_absolute() or ":" in normalized or ".." in path.parts or
                len(path.parts) > 8 or len(normalized) > 255 or
                path.suffix != ".json"):
            raise ValueError(f"Unsafe native {label} configuration: {value!r}")
        return path.as_posix()

    def source_asset_path(run_source: Path, value: str, kind: str) -> Path:
        if not isinstance(value, str) or not value or "\0" in value:
            raise ValueError(f"Invalid native {kind} asset path")
        # Authored relative paths can come from Windows or Linux regardless
        # of the packaging host. Absolute Windows source references require
        # their native OS because a Unix machine cannot resolve that drive.
        path_text = value.replace("\\", "/")
        if os.name != "nt" and (re.match(r"^[A-Za-z]:/", path_text) or
                                path_text.startswith("//")):
            raise ValueError(f"Windows native {kind} asset requires a Windows source host")
        return (run_source.parent / path_text).resolve(strict=True)
    
    def game_asset(run_source: Path, run_destination: Path,
                   value: str, kind: str) -> str:
        """Copy a run's authored scene/Stage asset into the portable tree.

        User-authored campaigns can refer to absolute files from the
        training workspace. These source references must be frozen inside
        the package, not left pointing to inaccessible paths on the
        original machine. Shared in-root assets keep their original
        names; external inputs receive a content-addressed name.
        """
        file = source_asset_path(run_source, value, kind)
        if file.suffix != ".json" or not file.is_file():
            raise ValueError(f"Native {kind} must reference a JSON file")
        within_root = file.is_relative_to(source_root)
        relative = file.relative_to(source_root) if within_root else None
        if (relative is not None and len(relative.parts) <= 8 and
                len(relative.as_posix()) <= 255):
            # The ordinary library traversal still owns the exact same
            # in-root file, ensuring duplicate references cannot conflict.
            pending.append((relative.as_posix(), kind))
            packaged = destination / relative
        else:
            payload = file.read_bytes()
            digest = hashlib.sha256(payload).hexdigest()
            packaged = destination / "source-assets" / f"{digest}-{kind}.json"
            packaged.parent.mkdir(parents=True, exist_ok=True)
            if not packaged.exists():
                packaged.write_bytes(payload)
        return Path(os.path.relpath(packaged, run_destination.parent)).as_posix()

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
                if game["kind"] not in ("campaign", "challenge", "battle"):
                    raise ValueError("Unknown native game library item")
                game["config"] = catalog_config(game["config"], "game")
                pending.append(((PurePosixPath(path.parent.as_posix()) /
                                 game["config"]).as_posix(),
                                game["kind"] if game["kind"] in ("campaign", "challenge") else "run"))
        elif kind in ("campaign", "challenge"):
            for battle in data["battles"]:
                battle["config"] = catalog_config(battle["config"], "mission")
                pending.append(((PurePosixPath(path.parent.as_posix()) /
                                 battle["config"]).as_posix(), "run"))
                # BattleCatalog.configurations() includes BOTH authored
                # conditional branches. A defeat route must remain playable
                # in a portable release, not disappear during packaging.
                mission = battle.get("mission") or {}
                for outcome in ("on_victory", "on_defeat"):
                    route = mission.get(outcome)
                    if route is not None:
                        route["config"] = catalog_config(route["config"], outcome)
                        pending.append(((PurePosixPath(path.parent.as_posix()) /
                                         route["config"]).as_posix(), "run"))
        elif kind == "run":
            validate_portable_hivemind(data.get("hivemind"))
            if data.get("model_updates") or (
                    data.get("recover_notifications") or
                    data.get("recover_outcome_journals")):
                raise ValueError(
                    f"Desktop release must not depend on a prior training or server session: {label}"
                )
            manifest = data.get("model_manifest")
            if manifest:
                if not include_policies:
                    raise ValueError(
                        f"Trained battle {label} requires --include-policies"
                    )
                if not isinstance(manifest, str) or not manifest.strip():
                    raise ValueError("model_manifest must name a native manifest")
                original = source_asset_path(source, manifest, "model manifest")
                if not original.is_file():
                    raise ValueError("trained policy manifest must be a regular file")
                digest = hashlib.sha256(original.read_bytes()).hexdigest()
                models_root = release_root / "assets/policies"
                if digest not in copied_manifests:
                    models_root.mkdir(parents=True, exist_ok=True)
                    # The existing portable model exporter verifies all
                    # checkpoints and preserves exact policy slots and ABI.
                    receipt = export_model(original, models_root / digest,
                                           target_device=policy_device)
                    if receipt["input_manifest_sha256"] != digest:
                        raise ValueError(
                            "native source model manifest changed during portable export"
                        )
                    copied_manifests.add(digest)
                # One source library may nest run configs under subfolders.
                # Resolve the *packaged* worker and model relative to this
                # particular run, not always to examples/ at the top level.
                packaged_run = destination / path
                relative_to_run = lambda asset: Path(os.path.relpath(
                    asset, packaged_run.parent,
                )).as_posix()
                data["python"] = python_command
                data["policy_worker"] = relative_to_run(
                    release_root / "native/tools/policy_worker.py"
                )
                data["model_manifest"] = relative_to_run(
                    models_root / digest / "manifest.json"
                )
                data["policy_capture"] = None
                has_policy = True
            elif data.get("python") or data.get("policy_worker"):
                raise ValueError(
                    f"Release has incomplete model-worker routing: {label}"
                )
            packaged_run = destination / path
            for level in data["levels"]:
                level["scene"] = game_asset(
                    source, packaged_run, level["scene"], "scene"
                )
            if data.get("collision_matrix") and data.get("stage_palette"):
                for asset in ("collision_matrix", "stage_palette"):
                    data[asset] = game_asset(
                        source, packaged_run, data[asset], "stage_asset"
                    )
            elif data.get("collision_matrix") or data.get("stage_palette"):
                raise ValueError("native legacy Stage assets must include both matrix and palette")
        elif kind not in ("scene", "stage_asset"):
            raise ValueError(f"Unknown native package asset kind: {kind}")
        # A custom library can be named library.json, but the packaged
        # launcher has one stable entry point across Windows and Linux.
        output = (destination / "native-library.json"
                  if kind == "library" else destination / path)
        output.parent.mkdir(parents=True, exist_ok=True)
        if kind in ("run", "campaign", "challenge", "library"):
            output.write_text(
                json.dumps(data, indent=2, ensure_ascii=False) + "\n",
                encoding="utf-8",
            )
        else:
            shutil.copy2(source, output)
        copied[label] = kind
    return has_policy


def package_extra_career_library(
    destination: Path, library: Path, kind: str, unlock_after: str | None = None,
    *, include_policies: bool = False, python_command: str = "python3",
    policy_device: str | None = None,
    copied_manifests: set[str] | None = None,
) -> bool:
    """Include an original career and its real unlockable modes in one game.

    Unity's original Campaign authors standalone Training Room/Fish Tank
    battles alongside the campaign. Retain those authored scenes, source
    requirements and shared progression gates, not just the main mission
    list. The Challenge is one independent persistent career.
    """
    if kind not in ("campaign", "challenge"):
        raise ValueError("extra native game must be a persistent career")
    nested = destination / kind
    nested.mkdir()
    policy_used = package_game_library(
        nested, library, include_policies=include_policies,
        python_command=python_command, policy_device=policy_device,
        release_root=destination.parent, copied_manifests=copied_manifests,
    )
    source_menu = nested / "native-library.json"
    extra = json.loads(source_menu.read_text(encoding="utf-8"))
    entries = extra.get("games")
    if (extra.get("library_schema") != 1 or
            not isinstance(entries, list) or not entries or
            not all(isinstance(entry, dict) for entry in entries)):
        raise ValueError(f"extra {kind} library has no playable career")
    primary = [entry for entry in entries if entry.get("kind") == kind]
    if len(primary) != 1 or (
            kind == "challenge" and len(entries) != 1):
        raise ValueError(f"extra {kind} library must have one original career")
    # An original Campaign can author later Training Room and Fish Tank
    # battles. They must belong to that same campaign, never import an
    # unrelated entry whose ownership cannot be restored by this library.
    primary_id = primary[0]["id"]
    for entry in entries:
        if entry is primary[0]:
            continue
        gate = entry.get("unlock_after")
        playtime = entry.get("unlock_after_playtime")
        if (kind != "campaign" or entry.get("kind") != "battle" or
                (gate is None) == (playtime is None) or
                not isinstance(gate if gate is not None else playtime, dict) or
                (gate if gate is not None else playtime).get("campaign_id") != primary_id):
            raise ValueError(
                "extra campaign game needs a genuine source campaign mission or Fish Tank playtime unlock"
            )
    if unlock_after is not None:
        if kind != "challenge" or ":" not in unlock_after:
            raise ValueError("Challenge unlock requires CAMPAIGN:MISSION")
        campaign_id, mission_id = unlock_after.split(":", 1)
        if not campaign_id or not mission_id:
            raise ValueError("Challenge unlock requires CAMPAIGN:MISSION")
        primary[0]["unlock_after"] = {
            "campaign_id": campaign_id, "mission_id": mission_id,
        }
    menu_path = destination / "native-library.json"
    menu = json.loads(menu_path.read_text(encoding="utf-8"))
    staged = [entry.copy() for entry in entries]
    for entry in staged:
        if any(game["id"] == entry["id"] for game in menu["games"]):
            raise ValueError(f"native library already contains {entry['id']}")
        entry["config"] = kind + "/" + entry["config"]
        menu["games"].append(entry)
    menu_path.write_text(json.dumps(menu, indent=2, ensure_ascii=False) + "\n",
                         encoding="utf-8")
    # One final library entry controls all frozen games; leave no orphaned
    # nested menu file in the manifest's immutable source inventory.
    source_menu.unlink()
    return policy_used


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
        if len(relative.parts) == 1 and relative != Path("Moon Moon.mp3"):
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


def write_launchers(output: Path, binary_name: str,
                    server_binary: str | None = None) -> None:
    windows_start = (
        "@echo off\n"
        "setlocal EnableExtensions DisableDelayedExpansion\n"
        # pushd maps an installation on a Windows network share for the
        # lifetime of the launcher, including relative game/tool paths.
        'pushd "%~dp0" || exit /b 1\n'
        'if defined LOCALAPPDATA (set "BEES_SAVE=%LOCALAPPDATA%\\BeesNative") '
        'else (set "BEES_SAVE=%USERPROFILE%\\BeesNative")\n'
    )
    windows_finish = 'set "BEES_STATUS=%ERRORLEVEL%"\npopd\nexit /b %BEES_STATUS%\n'
    for name in EXAMPLES:
        if not (output / "examples" / f"{name}.run.json").is_file():
            continue
        if binary_name.endswith(".exe"):
            launcher = output / f"play-{name}.cmd"
            launcher.write_text(
                windows_start +
                'if not exist "%BEES_SAVE%\\battles" mkdir "%BEES_SAVE%\\battles"\n'
                f'set "BEES_RUN=%BEES_SAVE%\\battles\\{name}-%RANDOM%-%RANDOM%"\n'
                f'".\\{binary_name}" play "examples\\{name}.run.json" '
                '--output-dir "%BEES_RUN%" %*\n' + windows_finish,
                encoding="utf-8", newline="\r\n",
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
                encoding="utf-8", newline="\n",
            )
            launcher.chmod(0o755)
    # Primary entry point: persistent player saves live outside the
    # immutable game distribution on both operating systems.
    if binary_name.endswith(".exe"):
        launcher = output / "play-bees.cmd"
        launcher.write_text(
            windows_start +
            'if not exist "%BEES_SAVE%" mkdir "%BEES_SAVE%"\n'
            f'".\\{binary_name}" library "examples\\native-library.json" '
            '--output-dir "%BEES_SAVE%" %*\n' + windows_finish,
            encoding="utf-8", newline="\r\n",
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
            encoding="utf-8", newline="\n",
        )
        launcher.chmod(0o755)


    # A remote commander or view-only spectator uses the SAME graphical
    # Rust client and private token-file contract, not the optional Python
    # administration tools. Never copy or embed a player's credential.
    if binary_name.endswith(".exe"):
        launcher = output / "join-bees.cmd"
        launcher.write_text(
            windows_start +
            'if "%~1"=="" (echo Usage: %~nx0 HOST:PORT TOKEN_FILE SIDE [--spectate] 1>&2 & popd & exit /b 2)\n' +
            'if not exist "%~2" (echo A private player token file is required 1>&2 & popd & exit /b 2)\n' +
            'if not "%~3"=="1" if not "%~3"=="2" (echo SIDE must be 1 or 2 1>&2 & popd & exit /b 2)\n' +
            'if "%~4"=="--spectate" (\n' +
            f'  ".\\{binary_name}" connect "%~1" --token-file "%~2" --side "%~3" --spectate\n' +
            ') else (\n' +
            f'  ".\\{binary_name}" connect "%~1" --token-file "%~2" --side "%~3"\n' +
            ')\n' + windows_finish,
            encoding="utf-8", newline="\r\n",
        )
    else:
        launcher = output / "join-bees.sh"
        launcher.write_text(
            "#!/bin/sh\n"
            "set -eu\n"
            'cd "$(dirname "$0")"\n'
            'if [ "$#" -lt 3 ]; then\n'
            '  echo "Usage: $0 HOST:PORT TOKEN_FILE SIDE [--spectate]" >&2\n'
            '  exit 2\n'
            'fi\n'
            'address="$1"; token="$2"; side="$3"; shift 3\n'
            f'exec "./{binary_name}" connect "$address" '
            '--token-file "$token" --side "$side" "$@"\n',
            encoding="utf-8", newline="\n",
        )
        launcher.chmod(0o755)


    if server_binary is not None:
        # Stable save directories permit the existing authoritative
        # checkpoint/session-recovery owner to re-open the SAME battle.
        # Administrators select a fresh output directory for a new match.
        # Secrets and listen addresses are supplied ONLY at runtime.
        if binary_name.endswith(".exe"):
            launcher = output / "serve-bees.cmd"
            launcher.write_text(
                windows_start +
                'if not defined BEES_NATIVE_REMOTE_LISTEN (echo Set BEES_NATIVE_REMOTE_LISTEN 1>&2 & popd & exit /b 2)\n' +
                'if not defined BEES_NATIVE_REMOTE_SIDE_TOKENS if not defined BEES_NATIVE_REMOTE_SIDE_TOKENS_FILE (echo Set commander token JSON or BEES_NATIVE_REMOTE_SIDE_TOKENS_FILE 1>&2 & popd & exit /b 2)\n' +
                'if "%~1"=="" (echo Usage: %~nx0 PACKAGED-RUN-JSON 1>&2 & popd & exit /b 2)\n' +
                'if not "%~2"=="" (echo Expected exactly one run JSON path 1>&2 & popd & exit /b 2)\n' +
                'if not exist "%~1" (echo Missing native run configuration 1>&2 & popd & exit /b 2)\n' +
                'if not defined BEES_NATIVE_SERVER_OUTPUT_DIR set "BEES_NATIVE_SERVER_OUTPUT_DIR=%BEES_SAVE%\\servers\\hosted"\n' +
                'if not exist "%BEES_NATIVE_SERVER_OUTPUT_DIR%" mkdir "%BEES_NATIVE_SERVER_OUTPUT_DIR%"\n' +
                f'".\\{server_binary}" serve "%~1" ' +
                '--output-dir "%BEES_NATIVE_SERVER_OUTPUT_DIR%"\n' + windows_finish,
                encoding="utf-8", newline="\r\n",
            )
        else:
            launcher = output / "serve-bees.sh"
            launcher.write_text(
                "#!/bin/sh\n"
                "set -eu\n"
                'cd "$(dirname "$0")"\n'
                ': "${BEES_NATIVE_REMOTE_LISTEN:?Set BEES_NATIVE_REMOTE_LISTEN to the host TCP address}"\n'
                'if [ -z "${BEES_NATIVE_REMOTE_SIDE_TOKENS:-}" ] && [ -z "${BEES_NATIVE_REMOTE_SIDE_TOKENS_FILE:-}" ]; then\n'
                '  echo "Set BEES_NATIVE_REMOTE_SIDE_TOKENS_FILE or BEES_NATIVE_REMOTE_SIDE_TOKENS" >&2\n'
                '  exit 2\n'
                'fi\n'
                'save="${XDG_DATA_HOME:-$HOME/.local/share}/bees-native"\n'
                'if [ "$#" -ne 1 ] || [ ! -f "$1" ]; then\n'
                '  printf "Usage: %s PACKAGED-RUN-JSON\\n" "$0" >&2\n'
                '  exit 2\n'
                'fi\n'
                'run="${BEES_NATIVE_SERVER_OUTPUT_DIR:-$save/servers/hosted}"\n'
                'mkdir -p "$run"\n'
                f'exec "./{server_binary}" serve "$1" ' +
                '--output-dir "$run"\n',
                encoding="utf-8", newline="\n",
            )
            launcher.chmod(0o755)

        # The original headless mission command owns the career ledger,
        # FleetShips, unlocks, source worlds and authoritative battle receipt.
        # A stable per-catalog output path retains progression after a
        # finished mission or a recovered interrupted match.
        if binary_name.endswith(".exe"):
            launcher = output / "serve-bees-campaign.cmd"
            launcher.write_text(
                windows_start +
                'if not defined BEES_NATIVE_REMOTE_LISTEN (echo Set BEES_NATIVE_REMOTE_LISTEN 1>&2 & popd & exit /b 2)\n' +
                'if not defined BEES_NATIVE_REMOTE_SIDE_TOKENS if not defined BEES_NATIVE_REMOTE_SIDE_TOKENS_FILE (echo Set commander credentials 1>&2 & popd & exit /b 2)\n' +
                'if "%~1"=="" (echo Usage: %~nx0 PACKAGED-CAMPAIGN-JSON MISSION-ID 1>&2 & popd & exit /b 2)\n' +
                'if "%~2"=="" (echo A campaign mission ID is required 1>&2 & popd & exit /b 2)\n' +
                'if not "%~3"=="" (echo Expected one catalog and mission ID 1>&2 & popd & exit /b 2)\n' +
                'if not exist "%~1" (echo Native campaign catalog not found 1>&2 & popd & exit /b 2)\n' +
                'if not defined BEES_NATIVE_SERVER_OUTPUT_DIR set "BEES_NATIVE_SERVER_OUTPUT_DIR=%BEES_SAVE%\\campaigns\\%~n1"\n' +
                'if not exist "%BEES_NATIVE_SERVER_OUTPUT_DIR%" mkdir "%BEES_NATIVE_SERVER_OUTPUT_DIR%"\n' +
                f'".\\{server_binary}" campaign "%~1" ' +
                '--output-dir "%BEES_NATIVE_SERVER_OUTPUT_DIR%" --mission "%~2" --serve\n' +
                windows_finish,
                encoding="utf-8", newline="\r\n",
            )
        else:
            launcher = output / "serve-bees-campaign.sh"
            launcher.write_text(
                "#!/bin/sh\n"
                "set -eu\n"
                'cd "$(dirname "$0")"\n'
                ': "\${BEES_NATIVE_REMOTE_LISTEN:?Set native LAN listen address}"\n'
                'if [ -z "\${BEES_NATIVE_REMOTE_SIDE_TOKENS:-}" ] && [ -z "\${BEES_NATIVE_REMOTE_SIDE_TOKENS_FILE:-}" ]; then\n'
                '  echo "Set native commander credential JSON or private token file" >&2\n'
                '  exit 2\n'
                'fi\n'
                'if [ "$#" -ne 2 ] || [ ! -f "$1" ]; then\n'
                '  echo "Usage: $0 PACKAGED-CAMPAIGN-JSON MISSION-ID" >&2\n'
                '  exit 2\n'
                'fi\n'
                'catalog="$1"; mission="$2"\n'
                'save="\${XDG_DATA_HOME:-$HOME/.local/share}/bees-native"\n'
                'default="$save/campaigns/$(basename "$catalog" .json)"\n'
                'run="\${BEES_NATIVE_SERVER_OUTPUT_DIR:-$default}"\n'
                'mkdir -p "$run"\n'
                f'exec "./{server_binary}" campaign "$catalog" ' +
                '--output-dir "$run" --mission "$mission" --serve\n',
                encoding="utf-8", newline="\n",
            )
            launcher.chmod(0o755)


def create_manifest(
    output: Path, *, include_training: bool = False, audio_paths: list[Path] | None = None,
    dedicated_server: bool = False,
) -> None:
    files = []
    for path in sorted(output.rglob("*")):
        if not path.is_file() or path == output / "manifest.json":
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
    manifest = {
        "format": "bees-native-game-package",
        "version": 1,
        "training_runtime": include_training,
        "content": files,
        **({"dedicated_server": True} if dedicated_server else {}),
    }
    if audio_paths is not None:
        manifest["presentation_version"] = 2
        manifest["presentation_audio"] = [path.as_posix() for path in audio_paths]
    (output / "manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8",
    )


def verify_presentation_assets(directory: Path, manifest: dict, inventory: dict) -> None:
    # Earlier release manifests carry only the general file inventory.
    if "presentation_audio" not in manifest:
        return
    presentation_version = manifest.get("presentation_version", 1)
    if type(presentation_version) is not int or presentation_version not in (1, 2):
        raise ValueError("unsupported native presentation inventory")
    audio = manifest["presentation_audio"]
    if (not isinstance(audio, list) or len(audio) < 20 or
            any(not isinstance(name, str) or not name.startswith("assets/audio/") or
                not name.endswith(".mp3") or name not in inventory or inventory[name][0] == 0
                for name in audio) or len(set(audio)) != len(audio)):
        raise ValueError("native release is missing required audio tracks")
    for _, filename, signature in ARTWORK_ATLASES:
        name = f"assets/{filename}"
        if filename == "projectile-impacts.rgba" and name not in inventory and presentation_version == 1:
            continue  # Earlier releases predate source impact artwork.
        if name not in inventory or inventory[name][0] < 7:
            raise ValueError(f"native release is missing required artwork: {name}")
        with (directory / name).open("rb") as atlas:
            header = atlas.read(6)
        legacy_projectiles = signature == b"BPS2" and header[:4] == b"BPS1"
        if ((header[:4] != signature and not legacy_projectiles) or
                int.from_bytes(header[4:], "little") == 0):
            raise ValueError(f"native release has an invalid artwork header: {name}")


def validate_packaged_library(data: dict) -> list[dict]:
    """Apply the desktop library's entry contract to the delivered menu."""
    if (not isinstance(data, dict) or set(data) != {"library_schema", "games"} or
            type(data["library_schema"]) is not int or data["library_schema"] != 1 or
            not isinstance(data["games"], list) or not 1 <= len(data["games"]) <= 100):
        raise ValueError("packaged native library requires 1..100 entries and schema 1")
    seen = set()
    for item in data["games"]:
        if not isinstance(item, dict) or set(item) - {
            "id", "kind", "title", "config", "unlock_after", "unlock_after_playtime",
        }:
            raise ValueError("invalid packaged native library entry")
        game_id, kind, title, config = (item.get(key) for key in ("id", "kind", "title", "config"))
        if (not isinstance(game_id, str) or re.fullmatch(r"[A-Za-z0-9_-]{1,48}", game_id) is None or
                game_id in seen or kind not in ("battle", "campaign", "challenge") or
                not isinstance(title, str) or not title.strip() or len(title.encode("utf-8")) > 120 or
                not isinstance(config, str) or not config or ":" in config or "\\" in config or
                config.startswith(("/", "./")) or ".." in PurePosixPath(config).parts or
                PurePosixPath(config).suffix != ".json"):
            raise ValueError("invalid or duplicate packaged native library entry")
        seen.add(game_id)
    return data["games"]


def validate_packaged_unlocks(games: list[dict], campaign_missions: dict[str, set[str]]) -> None:
    """Resolve every lock against the shipped campaign and its mission IDs."""
    for game in games:
        mission = game.get("unlock_after")
        playtime = game.get("unlock_after_playtime")
        if playtime is not None:
            if (mission is not None or game["kind"] != "battle" or
                    not isinstance(playtime, dict) or set(playtime) != {"campaign_id", "seconds"} or
                    not isinstance(playtime["campaign_id"], str) or
                    playtime["campaign_id"] not in campaign_missions or
                    type(playtime["seconds"]) is not int or playtime["seconds"] != 1800):
                raise ValueError(f"invalid packaged Fish Tank playtime unlock for {game['id']}")
        if mission is not None:
            if (game["kind"] not in ("battle", "challenge") or not isinstance(mission, dict) or
                    set(mission) != {"campaign_id", "mission_id"} or
                    not isinstance(mission["campaign_id"], str) or
                    not isinstance(mission["mission_id"], str) or not mission["mission_id"].strip() or
                    mission["mission_id"] not in campaign_missions.get(mission["campaign_id"], ())):
                raise ValueError(f"unknown packaged campaign mission unlock for {game['id']}")


def verify_native_package(directory: Path) -> dict:
    """Verify every released byte and that game/model links are self-contained.

    This is a portable transfer-integrity check, not a cryptographic
    signature: the independent release manifest must come from a trusted
    distributor. It does not start physics, a model worker, or Unity.
    """
    if directory.is_symlink():
        raise ValueError("native game release must not be a symlink")
    directory = directory.resolve(strict=True)
    receipt = directory / "manifest.json"
    if not receipt.is_file() or receipt.is_symlink():
        raise ValueError("native release is missing its completion manifest")
    manifest = json.loads(receipt.read_text(encoding="utf-8"))
    if (not isinstance(manifest, dict) or
            manifest.get("format") != "bees-native-game-package" or
            manifest.get("version") != 1 or
            not isinstance(manifest.get("content"), list)):
        raise ValueError("unsupported native game release manifest")

    expected = {}
    total_bytes = 0
    for item in manifest["content"]:
        if not isinstance(item, dict):
            raise ValueError("invalid native release inventory item")
        name, size, digest = (
            item.get("path"), item.get("bytes"), item.get("sha256")
        )
        if (not isinstance(name, str) or not name or "\\" in name or
                ":" in name or
                Path(name).is_absolute() or ".." in Path(name).parts or
                Path(name) == Path("manifest.json") or
                name in expected or type(size) is not int or size < 0 or
                not isinstance(digest, str) or
                re.fullmatch(r"[0-9a-f]{64}", digest) is None):
            raise ValueError("invalid or repeated native package asset entry")
        expected[name] = (size, digest)
        total_bytes += size
    actual = set()
    for file in directory.rglob("*"):
        if file.is_symlink():
            raise ValueError(f"native package contains a symbolic link: {file}")
        if file.is_file() and file != receipt:
            actual.add(file.relative_to(directory).as_posix())
    if actual != set(expected):
        raise ValueError("native release inventory does not match its files")
    for name, (size, digest) in expected.items():
        file = directory / name
        if file.stat().st_size != size:
            raise ValueError(f"native release asset length changed: {name}")
        sha = hashlib.sha256()
        with file.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                sha.update(block)
        if sha.hexdigest() != digest:
            raise ValueError(f"native release asset hash changed: {name}")

    verify_presentation_assets(directory, manifest, expected)
    if not any((directory / name).is_file() for name in ("bees", "bees.exe")):
        raise ValueError("native release is missing the game executable")
    game = directory / ("bees.exe" if (directory / "bees.exe").is_file() else "bees")
    binary_suffix = ".exe" if game.suffix == ".exe" else ""
    rollout = directory / ("policy_rollout" + binary_suffix)
    generator = directory / ("policy_manifest" + binary_suffix)
    headless_name = "bees-server" + binary_suffix
    headless = directory / headless_name
    dedicated_server = manifest.get("dedicated_server", False)
    if type(dedicated_server) is not bool:
        raise ValueError("native release dedicated-server marker must be boolean")
    if dedicated_server != headless.is_file():
        raise ValueError("native release dedicated-server executable differs from manifest")
    check_compiled_training_targets(
        game, rollout if rollout.is_file() else None,
        generator if generator.is_file() else None,
        headless if dedicated_server else None,
    )
    if dedicated_server:
        launcher_name = ("serve-bees.cmd" if binary_suffix
                         else "serve-bees.sh")
        if launcher_name not in expected:
            raise ValueError("native release has no restartable dedicated host launcher")
        campaign_launcher = ("serve-bees-campaign.cmd" if binary_suffix
                             else "serve-bees-campaign.sh")
        if campaign_launcher not in expected:
            raise ValueError("native release lacks headless campaign hosting entry point")
        if compiled_sha256(game) == compiled_sha256(headless):
            raise ValueError("dedicated server must be a separately compiled native binary")
    if manifest.get("training_runtime", False) not in (True, False):
        raise ValueError("invalid native training distribution marker")
    training_runtime = manifest.get("training_runtime", False)
    if training_runtime:
        required = (
            "native/Cargo.toml", "native/Cargo.lock",
            "native/crates/bees-host/Cargo.toml",
            "native/crates/bees-host/src/lib.rs",
            "native/crates/bees-host/examples/policy_manifest.rs",
            "native/crates/bees-host/examples/policy_rollout.rs",
            "native/crates/bees-gameplay/src/lib.rs",
            "native/crates/bees-hivemind/src/lib.rs",
            "native/crates/bees-sim/src/lib.rs",
            "native/tools/bootstrap_policy.py",
            "native/tools/prepare_fleet_training.py",
            "native/tools/train_policy_round.py",
            "native/tools/train_policy_session.py",
            "native/tools/evaluate_policy.py",
            "native/tools/native_distributed_collection.py",
            "native/tools/native_gpu_allocation.py",
            "native/tools/run_distributed_collector.py",
            "native/tools/native_bundle_receiver.py",
            "native/tools/native_training_artifacts.py",
            "native/tools/author-battle.py",
            "native/tools/carrier_family.py",
            "native/examples/native-skirmish.policy.scene.json",
            "native/assets/box-obstacle-colliders.json",
            "native/assets/polygon-obstacle-colliders.json",
            "Training/rl_1v1_config.yaml",
            "Training/bees_mlagents_ppo_compat.py",
            "Training/bees_mlagents_factorized_poca.py",
            "Training/bees_mlagents_structured_policy.py",
            "Training/experiment_hub_native_round_dispatch.js",
            "Training/experiment_hub_resources.js",
            "Training/experiment_hub_native_portable_adapter.js",
        )
        if any(name not in expected for name in required):
            raise ValueError("native training release lacks canonical runtime inputs")
        optional_rollout = "policy_rollout.exe" if (
            directory / "bees.exe").is_file() else "policy_rollout"
        if optional_rollout in expected and not (directory / optional_rollout).is_file():
            raise ValueError("native training rollout executable is missing")
        optional_manifest = "policy_manifest.exe" if (
            directory / "bees.exe").is_file() else "policy_manifest"
        if optional_manifest in expected and not (directory / optional_manifest).is_file():
            raise ValueError("native training policy ABI generator is missing")

    def owned_link(owner: Path, relative: str, name: str) -> Path:
        if (not isinstance(relative, str) or not relative or
                Path(relative).is_absolute() or "\\" in relative or
                ":" in relative):
            raise ValueError(f"invalid packaged {name} path")
        asset = (owner.parent / relative).resolve(strict=True)
        if not asset.is_relative_to(directory) or not asset.is_file():
            raise ValueError(f"packaged {name} escapes the game distribution")
        return asset

    pending = [(directory / "examples/native-library.json", "library")]
    inventory_paths = {directory / name for name in expected}
    seen = {}
    policies = set()
    count_runs = 0
    games = []
    campaign_paths = {}
    catalog_missions = {}
    while pending:
        file, kind = pending.pop()
        prior = seen.get(file)
        if prior is not None:
            if prior != kind:
                raise ValueError("native packaged asset has inconsistent roles")
            continue
        if file not in inventory_paths:
            raise ValueError("native packaged game asset is missing from inventory")
        seen[file] = kind
        data = json.loads(file.read_text(encoding="utf-8"))
        if kind == "library":
            games = validate_packaged_library(data)
            for item in games:
                game_kind = item["kind"]
                source = owned_link(file, item["config"], "game configuration")
                if game_kind == "campaign":
                    campaign_paths[item["id"]] = source
                pending.append((source, game_kind if game_kind != "battle" else "run"))
        elif kind in ("campaign", "challenge"):
            missions = set()
            for item in data["battles"]:
                pending.append((owned_link(file, item["config"], "mission"), "run"))
                mission = item.get("mission") or {}
                if isinstance(mission.get("id"), str) and mission["id"].strip():
                    missions.add(mission["id"])
                for outcome in ("on_victory", "on_defeat"):
                    route = mission.get(outcome)
                    if route is not None:
                        pending.append((
                            owned_link(file, route["config"], outcome + " route"), "run",
                        ))
            catalog_missions[file] = missions
        elif kind == "run":
            count_runs += 1
            validate_portable_hivemind(data.get("hivemind"))
            if (data.get("model_updates") or data.get("policy_capture") or
                    data.get("recover_notifications") or
                    data.get("recover_outcome_journals")):
                raise ValueError("packaged native game depends on training or prior session state")
            for level in data["levels"]:
                pending.append((owned_link(file, level["scene"], "scene"), "scene"))
            if data.get("collision_matrix") or data.get("stage_palette"):
                if not data.get("collision_matrix") or not data.get("stage_palette"):
                    raise ValueError("partial source Stage asset configuration")
                for name in ("collision_matrix", "stage_palette"):
                    pending.append((owned_link(file, data[name], name), "stage_asset"))
            if data.get("model_manifest"):
                worker = owned_link(file, data["policy_worker"], "policy worker")
                if worker != directory / "native/tools/policy_worker.py":
                    raise ValueError("packaged run names an external policy worker")
                if any(name not in expected for name in
                       POLICY_WORKER_SOURCES + MLAGENTS_ACTOR_SOURCES + DEPLOYED_GAME_SOURCES):
                    raise ValueError("native policy release lacks required inference modules")
                model = owned_link(file, data["model_manifest"], "policy manifest")
                if model.name != "manifest.json" or not model.parent.is_relative_to(
                        directory / "assets/policies"):
                    raise ValueError("packaged run uses an external model manifest")
                if model.parent not in policies:
                    verify_policy_package(model.parent)
                    policies.add(model.parent)
        elif kind not in ("scene", "stage_asset"):
            raise ValueError("unknown packaged native JSON dependency")
    validate_packaged_unlocks(games, {
        game_id: catalog_missions[path] for game_id, path in campaign_paths.items()
    })
    return {
        "kind": "bees_native_game_package_verified",
        "files": len(expected), "bytes": total_bytes,
        "battles": count_runs, "portable_models": len(policies),
        "training_runtime": training_runtime,
        "dedicated_server": dedicated_server,
        "compiled_training_executables": (
            {
                "policy_manifest": (
                    "policy_manifest.exe" if (directory / "bees.exe").is_file()
                    else "policy_manifest"
                ) in expected,
                "policy_rollout": (
                    "policy_rollout.exe" if (directory / "bees.exe").is_file()
                    else "policy_rollout"
                ) in expected,
            } if training_runtime else None
        ),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path,
                        help="Existing graphical bees or bees.exe executable")
    parser.add_argument("--output", type=Path,
                        help="New distribution folder; will not overwrite")
    parser.add_argument("--verify", type=Path,
                        help="verify the file hashes and all game/model dependencies of a transferred native release")
    parser.add_argument("--library", type=Path,
                        default=ROOT / "native/examples/native-library.json",
                        help="native game library to package (built-in or Unity-free authored campaign)")
    parser.add_argument("--campaign-library", type=Path,
                        help="additional original authored Campaign library to ship beside the normal games")
    parser.add_argument("--challenge-library", type=Path,
                        help="additional original ten-stage Challenge library to ship beside the normal games")
    parser.add_argument("--challenge-unlock-after",
                        help="main packaged CAMPAIGN:MISSION that unlocks the additional Challenge")
    parser.add_argument("--include-policies", action="store_true",
                        help="include SHA-verified model manifests, weights and the native worker")
    parser.add_argument("--include-training", action="store_true",
                        help="ship the canonical native Rust workspace, Python learner and Hub bridge for Train Saved Fleet")
    parser.add_argument("--rollout-binary", type=Path,
                        help="optional prebuilt policy_rollout for the target game platform (requires --include-training)")
    parser.add_argument("--manifest-binary", type=Path,
                        help="optional prebuilt Rust policy_manifest ABI generator for compiler-free initialization")
    parser.add_argument("--server-binary", type=Path,
                        help="optional headless bees executable built for the same OS/CPU, shipped as bees-server")
    parser.add_argument("--python-command",
                        help="bare target-machine Python executable (python3, python, py)")
    parser.add_argument("--policy-device", choices=("cpu", "cuda"),
                        help="target inference device (cpu works on Linux/Windows, cuda needs supported torch)")
    args = parser.parse_args()
    if args.verify is not None:
        if (args.binary is not None or args.output is not None or
                args.include_policies or args.include_training or
                args.rollout_binary is not None or args.manifest_binary is not None or
                args.server_binary is not None or args.policy_device is not None or args.python_command is not None or
                args.campaign_library is not None or
                args.challenge_library is not None or args.challenge_unlock_after is not None):
            parser.error("--verify accepts only the completed distribution directory")
        print(json.dumps(verify_native_package(args.verify)), flush=True)
        return
    if args.binary is None or args.output is None:
        parser.error("create a native release with --binary and --output")
    if args.challenge_unlock_after and args.challenge_library is None:
        parser.error("--challenge-unlock-after requires --challenge-library")
    if args.policy_device is not None and not args.include_policies:
        parser.error("--policy-device requires --include-policies")
    if (args.rollout_binary is not None or args.manifest_binary is not None) and not args.include_training:
        parser.error("prebuilt training executables require --include-training")
    binary = args.binary.resolve()
    rollout_binary = args.rollout_binary.resolve(strict=True) if args.rollout_binary else None
    manifest_binary = args.manifest_binary.resolve(strict=True) if args.manifest_binary else None
    server_binary = args.server_binary.resolve(strict=True) if args.server_binary else None
    if any(path is not None and not path.is_file()
           for path in (rollout_binary, manifest_binary, server_binary)):
        parser.error("training executables must be existing compiled files")
    output = args.output.resolve()
    if not binary.is_file() or binary.name not in ("bees", "bees.exe"):
        parser.error("Provide a compiled native graphical bees or bees.exe binary")
    check_compiled_training_targets(binary, rollout_binary, manifest_binary, server_binary)
    if server_binary is not None and (
            server_binary.name not in ("bees", "bees.exe") or
            compiled_sha256(binary) == compiled_sha256(server_binary)):
        parser.error("--server-binary requires a separate headless native bees build")
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
        server_name = "bees-server.exe" if binary.suffix == ".exe" else "bees-server"
        if server_binary is not None:
            shutil.copy2(server_binary, staging / server_name)
            if binary.suffix != ".exe":
                (staging / server_name).chmod(0o755)
        assets = staging / "assets"
        assets.mkdir()
        converter_args = [sys.executable, str(CONVERTER)]
        for option, filename, _ in ARTWORK_ATLASES:
            converter_args.extend((f"--{option}-output", str(assets / filename)))
        subprocess.run(converter_args, cwd=ROOT, check=True)
        for source, relative in audio:
            destination = staging / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, destination)
        examples = staging / "examples"
        examples.mkdir()
        exported_manifests: set[str] = set()
        policy_used = package_game_library(
            examples, args.library, include_policies=args.include_policies,
            python_command=python_command, policy_device=args.policy_device,
            copied_manifests=exported_manifests,
        )
        if args.campaign_library is not None:
            campaign_policy_used = package_extra_career_library(
                examples, args.campaign_library, "campaign",
                include_policies=args.include_policies,
                python_command=python_command, policy_device=args.policy_device,
                copied_manifests=exported_manifests,
            )
            policy_used |= campaign_policy_used
        if args.challenge_library is not None:
            challenge_policy_used = package_extra_career_library(
                examples, args.challenge_library, "challenge",
                args.challenge_unlock_after,
                include_policies=args.include_policies,
                python_command=python_command, policy_device=args.policy_device,
                copied_manifests=exported_manifests,
            )
            policy_used |= challenge_policy_used
        if policy_used:
            package_policy_runtime(staging)
        if args.include_training:
            package_native_training_runtime(staging, rollout_binary, manifest_binary)
        # The integrity checker travels with the immutable distribution,
        # including game-only builds. It is optional at PLAY time; copying
        # these tools does not introduce Python into native game execution.
        tools_dir = staging / "native/tools"
        tools_dir.mkdir(parents=True, exist_ok=True)
        for name in ("package-native-game.py", "package_policy_manifest.py"):
            shutil.copy2(ROOT / "native/tools" / name, tools_dir / name)
        # Remote controllers are optional Python/Tk tools. The canonical
        # simulation core stays Rust-native; trained policy inference
        # requires local Python/NumPy and its selected model backend.
        clients = staging / "clients"
        clients.mkdir()
        for name in ("remote_play_client.py", "remote_play_window.py"):
            shutil.copy2(ROOT / "native/tools" / name, clients / name)
        write_launchers(staging, binary.name,
                        server_name if server_binary is not None else None)
        create_manifest(staging, include_training=args.include_training,
                        audio_paths=[relative for _, relative in audio],
                        dedicated_server=server_binary is not None)
        # A release is only discoverable once all playable scenes, model
        # assets and optional learner/source dependencies have passed the
        # same complete inventory verifier used by a receiving computer.
        receipt = verify_native_package(staging)
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
          f"{', native MA-POCA training runtime' if args.include_training else ''}"
          f" at {output}: {receipt['files']} verified files, "
          f"{receipt['bytes']} bytes")


if __name__ == "__main__":
    main()
