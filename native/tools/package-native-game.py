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

from package_policy_manifest import export_model, verify_package as verify_policy_package

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



# Training-capable distributions keep the exact canonical Python learner,
# native Rust workspace and authored physical assets alongside the client.
# Python, ML-Agents, PyTorch, Cargo and Node (for Hub) remain target-machine
# prerequisites; ordinary game-only distributions remain small and Python-free.
PROTECTED_SOURCE_NAMES = {
    "results", ".results", "Demonstrations", "Logs", "TrainingHistory~",
    "target", ".git", "__pycache__",
}


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
                 source.name == "rl_1v1_config.yaml")):
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
    pending = [(library.name, "library")]
    copied: dict[str, str] = {}
    copied_manifests: set[str] = set()
    has_policy = False

    def game_asset(run_source: Path, run_destination: Path,
                   value: str, kind: str) -> str:
        """Copy a run's authored scene/Stage asset into the portable tree.

        User-authored campaigns can refer to absolute files from the
        training workspace. These source references must be frozen inside
        the package, not left pointing to inaccessible paths on the
        original machine. Shared in-root assets keep their original
        names; external inputs receive a content-addressed name.
        """
        if not isinstance(value, str) or not value or "\\" in value:
            raise ValueError(f"Invalid native {kind} asset path")
        file = (run_source.parent / value).resolve(strict=True)
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
                models_root = destination.parent / "assets/policies"
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
                    destination.parent / "native/tools/policy_worker.py"
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
        if kind == "run":
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


def create_manifest(output: Path, *, include_training: bool = False) -> None:
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
    (output / "manifest.json").write_text(
        json.dumps({
            "format": "bees-native-game-package",
            "version": 1,
            "training_runtime": include_training,
            "content": files,
        }, indent=2) + "\n",
        encoding="utf-8",
    )


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

    if not any((directory / name).is_file() for name in ("bees", "bees.exe")):
        raise ValueError("native release is missing the game executable")
    if manifest.get("training_runtime", False) not in (True, False):
        raise ValueError("invalid native training distribution marker")
    training_runtime = manifest.get("training_runtime", False)
    if training_runtime:
        required = (
            "native/Cargo.toml", "native/Cargo.lock",
            "native/crates/bees-host/Cargo.toml",
            "native/crates/bees-host/examples/policy_manifest.rs",
            "native/tools/bootstrap_policy.py",
            "native/tools/prepare_fleet_training.py",
            "native/tools/train_policy_round.py",
            "native/tools/train_policy_session.py",
            "native/tools/native_training_artifacts.py",
            "native/tools/author-battle.py",
            "native/examples/native-skirmish.policy.scene.json",
            "native/assets/box-obstacle-colliders.json",
            "native/assets/polygon-obstacle-colliders.json",
            "Training/rl_1v1_config.yaml",
            "Training/bees_mlagents_ppo_compat.py",
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
            if not isinstance(data, dict) or data.get("library_schema") != 1:
                raise ValueError("invalid packaged native library")
            for item in data["games"]:
                game_kind = item["kind"]
                if game_kind not in ("campaign", "battle"):
                    raise ValueError("unsupported packaged native game kind")
                pending.append((
                    owned_link(file, item["config"], "game configuration"),
                    "campaign" if game_kind == "campaign" else "run",
                ))
        elif kind == "campaign":
            for item in data["battles"]:
                pending.append((owned_link(file, item["config"], "mission"), "run"))
                route = (item.get("mission") or {}).get("on_victory")
                if route is not None:
                    pending.append((
                        owned_link(file, route["config"], "victory route"), "run",
                    ))
        elif kind == "run":
            count_runs += 1
            if data.get("hivemind") or data.get("model_updates") or data.get("policy_capture"):
                raise ValueError("packaged native game contains a live training or Hub dependency")
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
                model = owned_link(file, data["model_manifest"], "policy manifest")
                if model.name != "manifest.json" or not model.parent.is_relative_to(
                        directory / "assets/policies"):
                    raise ValueError("packaged run uses an external model manifest")
                if model.parent not in policies:
                    verify_policy_package(model.parent)
                    policies.add(model.parent)
        elif kind not in ("scene", "stage_asset"):
            raise ValueError("unknown packaged native JSON dependency")
    return {
        "kind": "bees_native_game_package_verified",
        "files": len(expected), "bytes": total_bytes,
        "battles": count_runs, "portable_models": len(policies),
        "training_runtime": training_runtime,
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
    parser.add_argument("--include-policies", action="store_true",
                        help="include SHA-verified model manifests, weights and the native worker")
    parser.add_argument("--include-training", action="store_true",
                        help="ship the canonical native Rust workspace, Python learner and Hub bridge for Train Saved Fleet")
    parser.add_argument("--rollout-binary", type=Path,
                        help="optional prebuilt policy_rollout for the target game platform (requires --include-training)")
    parser.add_argument("--manifest-binary", type=Path,
                        help="optional prebuilt Rust policy_manifest ABI generator for compiler-free initialization")
    parser.add_argument("--python-command",
                        help="bare target-machine Python executable (python3, python, py)")
    parser.add_argument("--policy-device", choices=("cpu", "cuda"),
                        help="target inference device (cpu works on Linux/Windows, cuda needs supported torch)")
    args = parser.parse_args()
    if args.verify is not None:
        if (args.binary is not None or args.output is not None or
                args.include_policies or args.include_training or
                args.rollout_binary is not None or args.manifest_binary is not None or
                args.policy_device is not None or args.python_command is not None):
            parser.error("--verify accepts only the completed distribution directory")
        print(json.dumps(verify_native_package(args.verify)), flush=True)
        return
    if args.binary is None or args.output is None:
        parser.error("create a native release with --binary and --output")
    if args.policy_device is not None and not args.include_policies:
        parser.error("--policy-device requires --include-policies")
    if (args.rollout_binary is not None or args.manifest_binary is not None) and not args.include_training:
        parser.error("prebuilt training executables require --include-training")
    binary = args.binary.resolve()
    rollout_binary = args.rollout_binary.resolve(strict=True) if args.rollout_binary else None
    manifest_binary = args.manifest_binary.resolve(strict=True) if args.manifest_binary else None
    if any(path is not None and not path.is_file()
           for path in (rollout_binary, manifest_binary)):
        parser.error("training executables must be existing compiled files")
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
        write_launchers(staging, binary.name)
        create_manifest(staging, include_training=args.include_training)
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
