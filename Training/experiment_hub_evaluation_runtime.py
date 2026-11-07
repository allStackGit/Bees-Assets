#!/usr/bin/env python3
"""Describe the local evaluation method so a suite can pin its runtime SHA-256."""

import argparse
from contextlib import contextmanager
import hashlib
import importlib.metadata
import importlib.util
import json
import os
from pathlib import Path
import platform
import sys
import tomllib

from experiment_hub_eval_io import SHA256, read_bounded, suite_digest


def file_digest(file):
    with Path(file).open("rb") as stream:
        before = os.fstat(stream.fileno())
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
        after = os.fstat(stream.fileno())
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise ValueError("Evaluation runtime changed while being fingerprinted.")
    return digest


def source_tree(package):
    spec = importlib.util.find_spec(package)
    if spec is None or not spec.submodule_search_locations:
        raise ValueError(f"Expected an installed source package: {package}")
    root = Path(next(iter(spec.submodule_search_locations)))
    sources = {file.relative_to(root).as_posix(): file_digest(file)
               for file in sorted(root.rglob("*.py"))}
    if not sources:
        raise ValueError(f"Evaluation package source is unavailable: {package}")
    return sources


def describe(project, project_root=None):
    files = {}
    training = Path(__file__).resolve().parent
    evaluator = training / f"experiment_hub_{project}_evaluate.py"
    for file in (Path(__file__), evaluator, training / "experiment_hub_eval_io.py"):
        files[file.name] = file_digest(file)
    packages = ["numpy", "torch"]
    sources = {}
    if project == "courier":
        if project_root is None:
            raise ValueError("Courier runtime description requires --project-root.")
        for name in ("courier_env.py", "courier_safe_ppo.py"):
            files[name] = file_digest(project_root / name)
        packages += ["gymnasium", "stable_baselines3", "sb3_contrib"]
    elif project == "tinybrains":
        from ants_mapoca import runtime

        files["experiment_hub_tinybrains_runtime.py"] = file_digest(training / "experiment_hub_tinybrains_runtime.py")
        files["tinybrains-cli"] = file_digest(runtime.CLI)
        for name in ("model.onnx", "manifest.json"):
            files["nano/" + name] = file_digest(runtime.STARTER / "models/nano-bc" / name)
        for package in ("ants_mapoca", "tb_baselines"):
            sources[package] = source_tree(package)
        packages += ["onnx", "onnxruntime"]
        registry = Path(os.environ.get("TINYBRAINS_REGISTRY", runtime.ROOT / "games.toml"))
        if not registry.is_absolute():
            registry = runtime.ROOT / registry
        raw = read_bounded(registry, 1024 * 1024)
        files["games.toml"] = hashlib.sha256(raw).hexdigest()
        game = tomllib.loads(raw.decode("utf-8"))["games"]["ants"]
        if "path" in game:
            cartridge = Path(game["path"])
            if not cartridge.is_absolute():
                cartridge = registry.parent / cartridge
        else:
            archive = game["artifacts"]["sha256"]
            if not isinstance(archive, str) or not archive.startswith("sha256:") or not SHA256.fullmatch(archive[7:]):
                raise ValueError("Expected a pinned TinyBrains cartridge archive.")
            home = Path(os.environ.get("TINYBRAINS_HOME", runtime.ROOT / "cache"))
            if not home.is_absolute():
                home = runtime.ROOT / home
            cartridge = home / "cartridges" / archive[7:]
        # Fingerprint the actual cached/local game and board bytes, not just
        # the release identifier. Do not download or change the engine cache.
        for name in ("cartridge.json", "plugin.json", "plugin.toml", "tb-ants.wasm"):
            files["cartridge/" + name] = file_digest(cartridge / name)
        maps = sorted((cartridge / "maps").glob("*.json"))
        if not maps:
            raise ValueError("TinyBrains evaluation maps are unavailable locally.")
        for file in maps:
            files["cartridge/maps/" + file.name] = file_digest(file)
        for label, file in (("export-heldout", runtime.ROOT / "evidence/heldout.json"),
                            ("export-reference", cartridge / "reference/observations.json")):
            files[label] = file_digest(file) if file.exists() else None
        if files["export-heldout"] is None:
            for file in sorted((runtime.ROOT / "evidence/reference-tensors").glob("case-*/observation.json")):
                files["export-reference-tensors/" + file.parent.name] = file_digest(file)
    else:
        raise ValueError("Unsupported evaluation project.")
    try:
        versions = {name: importlib.metadata.version(name) for name in packages}
    except importlib.metadata.PackageNotFoundError as exc:
        raise ValueError(f"Missing evaluation dependency: {exc.name}") from exc
    return {"schema_version": 1, "project": project,
            "python": platform.python_version(), "implementation": platform.python_implementation(),
            "platform": platform.system(), "machine": platform.machine(),
            "packages": versions,
            "files": files, "sources": sources}


@contextmanager
def pinned_runtime(project, suite, project_root=None):
    """Reject a different method before loading a model or publishing a result."""
    expected = suite.get("runtime_sha256")
    if expected is None:
        yield
        return
    if suite_digest(describe(project, project_root)) != expected:
        raise ValueError("Evaluation runtime differs from the suite's pinned SHA-256.")
    yield
    if suite_digest(describe(project, project_root)) != expected:
        raise ValueError("Evaluation runtime changed during evaluation; no receipt published.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", required=True, choices=("tinybrains", "courier"))
    parser.add_argument("--project-root", type=Path)
    parser.add_argument("--engine-path", type=Path)
    args = parser.parse_args()
    try:
        if args.project == "tinybrains":
            from experiment_hub_tinybrains_runtime import configure_engine
            configure_engine(args.engine_path)
        elif args.engine_path is not None:
            raise ValueError("--engine-path applies only to TinyBrains.")
        manifest = describe(args.project, args.project_root)
        print(json.dumps({"runtime_sha256": suite_digest(manifest), "runtime": manifest},
                         indent=2, sort_keys=True))
        return 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as exc:
        print(f"Evaluation runtime description failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
