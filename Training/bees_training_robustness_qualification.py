"""Local qualification gate for Bees distributed-training robustness.

This intentionally exercises control/runtime/bootstrap orchestration without starting Unity gameplay
or touching production database state. It is safe to run before a build/start/new-run operation.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
from typing import Sequence
import xml.etree.ElementTree as ET


FOCUSED_PYTHON_SUITES = (
    "bees_build_contract_tests.py",
    "bees_release_runtime_tests.py",
    "bees_bootstrap_bundle_tests.py",
    "bees_run_lifecycle_tests.py",
    "bees_archive_training_run_tests.py",
    "bees_training_control_tests.py",
    "bees_managed_remote_worker_tests.py",
    "bees_distributed_training_tests.py",
    "bees_elastic_wan_training_tests.py",
    "bees_elastic_wan_slot_safety_tests.py",
    "bees_elastic_wan_zero_local_tests.py",
    "bees_wan_actor_training_tests.py",
    "bees_continual_service_tests.py",
    "bees_continual_train_tests.py",
    "bees_mlagents_learn_tests.py",
    "bees_continual_elastic_wan_service_tests.py",
)

UNITY_REQUIRED_TEST = (
    "RlPolicySchemaContractTests.ContinualLearningConfigTracksFrozenPolicyAbi"
)


@dataclass(frozen=True)
class Check:
    name: str
    command: tuple[str, ...]
    cwd: Path
    required: bool = True
    result_xml: Path | None = None
    required_test_substring: str | None = None


def _resolve_go(bees_root: Path) -> str | None:
    installed = shutil.which("go")
    if installed:
        return installed
    toolchains = bees_root / "Runtime" / "Toolchains"
    if not toolchains.is_dir():
        return None
    executable = "go.exe" if sys.platform.startswith("win") else "go"
    candidates = sorted(
        toolchains.glob(f"go*/go/bin/{executable}"),
        key=lambda path: path.parent.parent.parent.name,
        reverse=True,
    )
    return str(candidates[0]) if candidates else None


def _python_suites(training_root: Path, full_python: bool) -> tuple[str, ...]:
    if not full_python:
        return FOCUSED_PYTHON_SUITES
    return tuple(
        path.name
        for path in sorted(training_root.glob("*_tests.py"))
        if path.name != "bees_training_robustness_qualification_tests.py"
    )


def build_checks(
    *,
    bees_root: Path,
    assets_root: Path,
    full_python: bool,
    skip_node: bool,
    skip_go: bool,
    unity_editor: str | None = None,
    skip_unity: bool = False,
) -> list[Check]:
    training_root = assets_root / "Training"
    server_root = assets_root / "BeesServer~"
    bridge_root = assets_root / "Tools~" / "bees-tailnet-bridge"

    checks: list[Check] = []
    for name in _python_suites(training_root, full_python):
        path = training_root / name
        if not path.is_file():
            raise ValueError(f"required robustness test file is missing: {path}")
        checks.append(
            Check(
                name=f"python:{name}",
                command=(sys.executable, str(path)),
                cwd=training_root,
            )
        )

    if not skip_node:
        node = shutil.which("node")
        if not node:
            checks.append(
                Check(
                    name="node:training-control",
                    command=(),
                    cwd=server_root,
                    required=True,
                )
            )
        else:
            checks.append(
                Check(
                    name="node:training-control",
                    command=(
                        node,
                        "--test",
                        str(server_root / "test" / "startServerLauncher.module.test.js"),
                        str(server_root / "test" / "trainingControl.module.test.js"),
                        str(server_root / "test" / "trainingControlCli.module.test.js"),
                        str(server_root / "test" / "trainingEnvOptimizer.module.test.js"),
                    ),
                    cwd=server_root,
                )
            )

    if not skip_unity:
        unity_results = bees_root / "Logs" / "TrainingQualification" / "BeesFoundationEditMode.xml"
        unity_log = bees_root / "Logs" / "TrainingQualification" / "BeesFoundationEditMode.log"
        checks.append(
            Check(
                name="unity:bees-foundation-editmode",
                command=(
                    (
                        str(unity_editor),
                        "-batchmode",
                        "-nographics",
                        "-projectPath",
                        str(bees_root),
                        "-runTests",
                        "-testPlatform",
                        "EditMode",
                        "-testCategory",
                        "BeesFoundation",
                        "-testResults",
                        str(unity_results),
                        "-logFile",
                        str(unity_log),
                    )
                    if unity_editor
                    else ()
                ),
                cwd=bees_root,
                result_xml=unity_results,
                required_test_substring=UNITY_REQUIRED_TEST,
            )
        )

    if not skip_go:
        go = _resolve_go(bees_root)
        if go:
            checks.append(
                Check(
                    name="go:tailnet-bridge",
                    command=(go, "test", "./..."),
                    cwd=bridge_root,
                )
            )
        else:
            checks.append(
                Check(
                    name="go:tailnet-bridge",
                    command=(),
                    cwd=bridge_root,
                    required=False,
                )
            )

    return checks


def _validate_unity_results(
    result_path: Path,
    required_test_substring: str | None,
) -> tuple[bool, str]:
    if not result_path.is_file():
        return False, f"Unity test results were not written: {result_path}"
    try:
        root = ET.parse(result_path).getroot()
    except (OSError, ET.ParseError) as exc:
        return False, f"Unity test results are unreadable: {exc}"

    try:
        failed = int(root.attrib.get("failed", "-1"))
        passed = int(root.attrib.get("passed", "0"))
    except ValueError:
        return False, "Unity test result totals are malformed"
    if failed != 0 or passed <= 0:
        return False, f"Unity EditMode suite reported passed={passed} failed={failed}"

    if required_test_substring:
        matched = False
        for case in root.findall(".//test-case"):
            identity = " ".join(
                value
                for value in (
                    case.attrib.get("fullname"),
                    case.attrib.get("name"),
                )
                if value
            )
            if (
                required_test_substring in identity and
                case.attrib.get("result") == "Passed"
            ):
                matched = True
                break
        if not matched:
            return False, (
                "Unity EditMode results did not contain the required passing RL contract test: "
                + required_test_substring
            )
    return True, ""


def _run_check(check: Check) -> tuple[bool, float]:
    if not check.command:
        if check.required:
            print(f"[FAIL] {check.name}: required executable is unavailable")
            return False, 0.0
        print(f"[SKIP] {check.name}: executable is unavailable")
        return True, 0.0

    if check.result_xml is not None:
        check.result_xml.parent.mkdir(parents=True, exist_ok=True)
        try:
            check.result_xml.unlink()
        except FileNotFoundError:
            pass

    print(f"[RUN ] {check.name}", flush=True)
    started = time.monotonic()
    completed = subprocess.run(
        list(check.command),
        cwd=str(check.cwd),
        check=False,
    )
    elapsed = time.monotonic() - started
    if completed.returncode == 0:
        if check.result_xml is not None:
            valid, detail = _validate_unity_results(
                check.result_xml,
                check.required_test_substring,
            )
            if not valid:
                print(f"[FAIL] {check.name}: {detail} ({elapsed:.1f}s)", flush=True)
                return False, elapsed
        print(f"[PASS] {check.name} ({elapsed:.1f}s)", flush=True)
        return True, elapsed
    print(
        f"[FAIL] {check.name}: exit={completed.returncode} ({elapsed:.1f}s)",
        flush=True,
    )
    return False, elapsed


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Run the local distributed-training robustness qualification gate "
            "without starting/stopping the live cluster."
        )
    )
    parser.add_argument("--bees-root", required=True)
    parser.add_argument("--assets-root", required=True)
    parser.add_argument(
        "--full-python",
        action="store_true",
        help="run every Training/*_tests.py suite instead of the focused robustness set",
    )
    parser.add_argument("--skip-node", action="store_true")
    parser.add_argument("--skip-go", action="store_true")
    parser.add_argument(
        "--unity-editor",
        default=None,
        help="path to the Unity Editor executable used for EditMode qualification",
    )
    parser.add_argument("--skip-unity", action="store_true")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    bees_root = Path(args.bees_root).expanduser().resolve()
    assets_root = Path(args.assets_root).expanduser().resolve()
    if not bees_root.is_dir():
        print(f"qualification error: Bees root is missing: {bees_root}", file=sys.stderr)
        return 2
    if not assets_root.is_dir() or assets_root.parent != bees_root:
        print(
            f"qualification error: Assets root is invalid for {bees_root}: {assets_root}",
            file=sys.stderr,
        )
        return 2

    try:
        unity_editor = None
        if not args.skip_unity:
            explicit_unity = args.unity_editor or os.environ.get("BEES_UNITY_EDITOR")
            if explicit_unity:
                candidate = Path(explicit_unity).expanduser().resolve()
                if candidate.is_file():
                    unity_editor = str(candidate)
            if unity_editor is None:
                discovered = shutil.which("Unity") or shutil.which("Unity.exe")
                if discovered:
                    unity_editor = discovered
        checks = build_checks(
            bees_root=bees_root,
            assets_root=assets_root,
            full_python=bool(args.full_python),
            skip_node=bool(args.skip_node),
            skip_go=bool(args.skip_go),
            unity_editor=unity_editor,
            skip_unity=bool(args.skip_unity),
        )
    except ValueError as exc:
        print(f"qualification error: {exc}", file=sys.stderr)
        return 2

    failures: list[str] = []
    started = time.monotonic()
    for check in checks:
        ok, _elapsed = _run_check(check)
        if not ok:
            failures.append(check.name)

    elapsed = time.monotonic() - started
    if failures:
        print(
            f"Robustness qualification FAILED after {elapsed:.1f}s: "
            + ", ".join(failures),
            file=sys.stderr,
        )
        return 1

    print(
        f"Robustness qualification PASSED: {len(checks)} checks in {elapsed:.1f}s.",
        flush=True,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
