#!/usr/bin/env python3
"""Build a Unity-free native game release in one command.

Runs the established Rust workspace and package-native-game.py pipelines.
The first binary has the original native graphical client; the second is
built WITHOUT that feature for display-independent, authoritative hosting.
Both are compiled from the same HEAD and native crates. All copied assets,
authored campaigns and optional policies are verified by the one packager.

Examples:
  python native/tools/build-native-release.py --output dist/bees-native
  python native/tools/build-native-release.py --output dist/bees-native \
      --include-training -- --include-policies

Use -- to pass advanced package-native-game.py options, such as
--campaign-library, --challenge-library and --policy-device.
"""
from __future__ import annotations

import argparse
import os
import re
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[2]
WORKSPACE = ROOT / "native"
PACKAGER = WORKSPACE / "tools" / "package-native-game.py"
CARGO = WORKSPACE / "Cargo.toml"
LOCK = WORKSPACE / "Cargo.lock"


def package_arguments(argv: list[str]) -> tuple[list[str], list[str]]:
    """Advanced packager flags cannot replace compiled identity or output."""
    if "--" not in argv:
        return argv, []
    index = argv.index("--")
    extra = argv[index + 1:]
    prohibited = (
        "--binary", "--output", "--verify",
        "--server-binary", "--rollout-binary",
        "--manifest-binary", "--include-training",
    )
    if any(item == option or item.startswith(option + "=")
           for item in extra for option in prohibited):
        raise ValueError(
            "the native release builder owns output and compiled artifacts; "
            "do not override them through package passthrough"
        )
    return argv[:index], extra


def release_target(triple: str | None) -> tuple[str | None, str]:
    """Resolve the executable suffix independently of the build host.

    The packager checks the compiled PE/ELF machine and architecture; this
    narrow validation prevents silently selecting a host executable when a
    cross-target Cargo artifact is missing.
    """
    if triple is None:
        return None, ".exe" if os.name == "nt" else ""
    if not re.fullmatch(r"[A-Za-z0-9_]+(?:-[A-Za-z0-9_]+){2,4}", triple):
        raise ValueError("invalid Rust target triple")
    architecture = triple.split("-", 1)[0]
    if architecture not in ("x86_64", "aarch64"):
        raise ValueError("native releases support x86_64 and aarch64 targets")
    if triple in (
        f"{architecture}-pc-windows-gnu",
        f"{architecture}-pc-windows-msvc",
    ):
        return triple, ".exe"
    if triple in (
        f"{architecture}-unknown-linux-gnu",
        f"{architecture}-unknown-linux-musl",
    ):
        return triple, ""
    raise ValueError("native releases require a supported Windows or Linux Rust target")


def cargo_build(cargo: str, target: Path, triple: str | None, *flags: str) -> None:
    command = [
        cargo, "build", "--release", "--locked",
        "-p", "bees-host", *flags,
    ]
    if triple is not None:
        command.extend(("--target", triple))
    environment = os.environ.copy()
    environment["CARGO_TARGET_DIR"] = str(target)
    print("Native build:", " ".join(command), flush=True)
    subprocess.run(command, cwd=WORKSPACE, env=environment, check=True)


def copy_binary(target: Path, location: Path, name: str, triple: str | None) -> Path:
    executable = target / triple / "release" / name if triple else target / "release" / name
    if executable.is_symlink() or not executable.is_file():
        raise ValueError(f"native build did not produce {name}")
    location.mkdir(parents=True, exist_ok=True)
    published = location / name
    shutil.copy2(executable, published)
    return published


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True,
                        help="new immutable native game distribution; never overwritten")
    parser.add_argument("--cargo", default="cargo",
                        help="Cargo executable in PATH, or an explicit executable path")
    parser.add_argument("--target", metavar="RUST_TRIPLE",
                        help="compile a Windows/Linux x86_64 or aarch64 release for this Rust target; requires an installed toolchain and cross-linker")
    parser.add_argument("--target-dir", type=Path,
                        help="persistent Cargo cache location, shared across client/server builds")
    parser.add_argument("--include-training", action="store_true",
                        help="also compile and include the canonical native rollout and model ABI")
    parser.add_argument("--without-server", action="store_true",
                        help="build a graphical-only release without dedicated multiplayer")
    try:
        builder_args, extra = package_arguments(
            sys.argv[1:] if argv is None else argv
        )
    except ValueError as error:
        parser.error(str(error))
    args = parser.parse_args(builder_args)
    try:
        triple, suffix = release_target(args.target)
    except ValueError as error:
        parser.error(str(error))
    if not CARGO.is_file() or not LOCK.is_file() or not PACKAGER.is_file():
        parser.error("the canonical native workspace, lockfile or packager is missing")
    output = args.output.resolve()
    if output.exists() or output.is_symlink():
        parser.error("native release destination already exists")
    output.parent.mkdir(parents=True, exist_ok=True)
    target = (
        args.target_dir or Path(os.environ.get("CARGO_TARGET_DIR", WORKSPACE / "target"))
    ).resolve()
    target.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(
        prefix=".bees-native-built-", dir=output.parent,
    ) as staging_name:
        stage = Path(staging_name)
        cargo_build(args.cargo, target, triple, "--bin", "bees", "--features", "client")
        graphical = copy_binary(target, stage / "game", "bees" + suffix, triple)

        # Cargo feature unification may reuse a target artifact filename.
        # Copy the graphical binary BEFORE the separate no-client rebuild,
        # retaining incremental compiler caches in one target directory.
        headless = None
        if not args.without_server:
            cargo_build(args.cargo, target, triple, "--bin", "bees", "--no-default-features")
            headless = copy_binary(target, stage / "server", "bees" + suffix, triple)

        manifest = rollout = None
        if args.include_training:
            cargo_build(
                args.cargo, target, triple, "--no-default-features",
                "--example", "policy_rollout", "--example", "policy_manifest",
            )
            rollout = copy_binary(target, stage / "training", "policy_rollout" + suffix, triple)
            manifest = copy_binary(target, stage / "training", "policy_manifest" + suffix, triple)

        package = [
            sys.executable, str(PACKAGER),
            "--binary", str(graphical),
            "--output", str(output),
        ]
        if headless is not None:
            package.extend(("--server-binary", str(headless)))
        if args.include_training:
            package.extend((
                "--include-training",
                "--rollout-binary", str(rollout),
                "--manifest-binary", str(manifest),
            ))
        package.extend(extra)
        print("Native package:", " ".join(package), flush=True)
        subprocess.run(package, cwd=ROOT, check=True)

    # The packager independently verifies the complete asset/model inventory
    # before publishing. Do not claim runtime qualification or launch a game.
    print(f"Native release published: {output}", flush=True)


if __name__ == "__main__":
    main()
