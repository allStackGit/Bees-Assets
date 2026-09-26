"""Plan and persist Bees training run identities from the authoritative compatibility contract."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Mapping, Optional, Sequence


SCHEMA_VERSION = 1
CONTRACT_FIELDS = (
    "behavior_name",
    "policy_abi_version",
    "policy_signature",
    "observation_schema_version",
    "action_schema_version",
    "reward_schema_version",
    "scenario_schema_version",
)


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _semantic_csharp_sha256(path: Path) -> str:
    """Hash C# code while ignoring comments and indentation-only formatting changes."""
    text = path.read_text(encoding="utf-8")
    output: list[str] = []
    index = 0
    state = "code"
    quote = ""
    pending_space = False
    while index < len(text):
        current = text[index]
        following = text[index + 1] if index + 1 < len(text) else ""

        if state == "line-comment":
            if current in "\r\n":
                state = "code"
            index += 1
            continue

        if state == "block-comment":
            if current == "*" and following == "/":
                state = "code"
                index += 2
            else:
                index += 1
            continue

        if state in ("string", "char"):
            output.append(current)
            if current == "\\" and index + 1 < len(text):
                output.append(text[index + 1])
                index += 2
                continue
            if current == quote:
                state = "code"
            index += 1
            continue

        if current == "/" and following == "/":
            pending_space = True
            state = "line-comment"
            index += 2
            continue
        if current == "/" and following == "*":
            pending_space = True
            state = "block-comment"
            index += 2
            continue
        if current.isspace():
            pending_space = True
            index += 1
            continue

        if pending_space and output and output[-1] != " ":
            output.append(" ")
        pending_space = False

        if current in ('"', "'"):
            state = "string" if current == '"' else "char"
            quote = current
        output.append(current)
        index += 1

    return _sha256_bytes("".join(output).strip().encode("utf-8"))


def _network_settings_block(text: str) -> str:
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    start = None
    indent = None
    captured: list[str] = []
    for index, line in enumerate(lines):
        match = re.match(r"^(\s*)network_settings:\s*(?:#.*)?$", line)
        if match:
            start = index
            indent = len(match.group(1))
            break
    if start is None or indent is None:
        raise ValueError("trainer config has no network_settings block")
    captured.append(lines[start].strip())
    for line in lines[start + 1 :]:
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        current_indent = len(line) - len(line.lstrip())
        if current_indent <= indent:
            break
        body = line.split("#", 1)[0].rstrip()
        if body.strip():
            captured.append(body.strip())
    if len(captured) <= 1:
        raise ValueError("trainer config network_settings block is empty")
    return "\n".join(captured) + "\n"


def contract_payload(assets_root: Path) -> dict[str, Any]:
    assets_root = assets_root.resolve()
    continual_path = assets_root / "Training" / "continual_learning_config.json"
    trainer_path = assets_root / "Training" / "rl_1v1_config.yaml"
    scenes_root = assets_root / "Scripts" / "Scenes"
    reward_path = scenes_root / "RlOneVsOneReward.cs"
    policy_path = scenes_root / "RlPolicySchema.cs"
    semantic_sources = {
        "combat_perception_source_sha256": scenes_root / "RlCombatPerception.cs",
        "agent_action_source_sha256": scenes_root / "RlOneVsOneAgent.cs",
        "episode_coordinator_source_sha256": scenes_root / "RlOneVsOneEpisodeCoordinator.cs",
        "team_exploration_source_sha256": scenes_root / "RlTeamExplorationGrid.cs",
        "episode_identity_source_sha256": scenes_root / "RlEpisodeShipIdentity.cs",
    }
    for path in (
        continual_path,
        trainer_path,
        reward_path,
        policy_path,
        *semantic_sources.values(),
    ):
        if not path.is_file():
            raise ValueError(f"training compatibility source is missing: {path}")

    continual = json.loads(continual_path.read_text(encoding="utf-8"))
    if not isinstance(continual, Mapping):
        raise ValueError("continual learning config must contain a JSON object")

    payload: dict[str, Any] = {}
    for field in CONTRACT_FIELDS:
        if field not in continual:
            raise ValueError(f"continual learning config is missing {field}")
        payload[field] = continual[field]

    trainer_text = trainer_path.read_text(encoding="utf-8")
    payload["network_settings"] = _network_settings_block(trainer_text)
    # Reward semantics are intentionally part of compatibility even if a future editor forgets to
    # increment reward_schema_version. PolicySchema source is hashed too so ABI edits cannot silently
    # reuse an optimizer lineage before its mirrored JSON signature is corrected.
    payload["reward_source_sha256"] = _semantic_csharp_sha256(reward_path)
    payload["policy_schema_source_sha256"] = _semantic_csharp_sha256(policy_path)
    for name, path in semantic_sources.items():
        payload[name] = _semantic_csharp_sha256(path)
    return payload


def compatibility_key(payload: Mapping[str, Any]) -> str:
    canonical = json.dumps(dict(payload), sort_keys=True, separators=(",", ":"), ensure_ascii=True)
    return _sha256_bytes(canonical.encode("utf-8"))


def contract_fingerprint(assets_root: Path) -> dict[str, Any]:
    payload = contract_payload(assets_root)
    return {
        "schema_version": SCHEMA_VERSION,
        "compatibility_key": compatibility_key(payload),
        "contract": payload,
    }


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _run_id(payload: Mapping[str, Any], key: str, now: datetime) -> str:
    abi = int(payload["policy_abi_version"])
    reward = int(payload["reward_schema_version"])
    scenario = int(payload["scenario_schema_version"])
    stamp = now.strftime("%Y%m%dT%H%M%SZ")
    return f"bees-v{abi}-r{reward}-s{scenario}-{stamp}-{key[:8]}"


def _load_state(path: Path) -> Optional[dict[str, Any]]:
    if not path.is_file():
        return None
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict) or value.get("schema_version") != SCHEMA_VERSION:
        raise ValueError(f"training run lifecycle state is incompatible: {path}")
    if not isinstance(value.get("run_id"), str) or not value["run_id"]:
        raise ValueError(f"training run lifecycle state has no run_id: {path}")
    if not isinstance(value.get("compatibility_key"), str) or len(value["compatibility_key"]) != 64:
        raise ValueError(f"training run lifecycle state has invalid compatibility_key: {path}")
    return value


def plan_run(
    assets_root: Path,
    state_path: Path,
    now: Optional[datetime] = None,
    *,
    force_new: bool = False,
) -> dict[str, Any]:
    now = now or _utc_now()
    payload = contract_payload(assets_root)
    key = compatibility_key(payload)
    previous = _load_state(state_path)
    incompatible = previous is not None and (
        previous["compatibility_key"] != key or force_new
    )
    new_run = previous is None or incompatible
    run_id = _run_id(payload, key, now) if new_run else str(previous["run_id"])
    return {
        "schema_version": SCHEMA_VERSION,
        "planned_utc": now.isoformat(),
        "run_id": run_id,
        "previous_run_id": str(previous["run_id"]) if previous else None,
        "compatibility_key": key,
        "previous_compatibility_key": (
            str(previous["compatibility_key"]) if previous else None
        ),
        "incompatible": incompatible,
        "new_run": new_run,
        "forced_new_run": bool(force_new),
        "contract": payload,
    }


def _atomic_json(path: Path, value: Mapping[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=str(path.parent))
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(dict(value), handle, indent=2, sort_keys=True)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        try:
            os.unlink(temporary)
        except FileNotFoundError:
            pass


def commit_plan(state_path: Path, plan: Mapping[str, Any]) -> dict[str, Any]:
    if plan.get("schema_version") != SCHEMA_VERSION:
        raise ValueError("run plan schema is incompatible")
    now = _utc_now().isoformat()
    existing = _load_state(state_path)
    created = (
        existing.get("created_utc")
        if existing and existing.get("run_id") == plan.get("run_id")
        else now
    )
    state = {
        "schema_version": SCHEMA_VERSION,
        "run_id": str(plan["run_id"]),
        "compatibility_key": str(plan["compatibility_key"]),
        "contract": dict(plan["contract"]),
        "created_utc": created,
        "last_build_utc": now,
    }
    _atomic_json(state_path, state)
    return state


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)

    plan = sub.add_parser("plan")
    plan.add_argument("--assets-root", required=True)
    plan.add_argument("--state", required=True)
    plan.add_argument("--out", required=True)
    plan.add_argument(
        "--force-new",
        action="store_true",
        help="Create a new run even when the compatibility contract is unchanged.",
    )

    commit = sub.add_parser("commit")
    commit.add_argument("--state", required=True)
    commit.add_argument("--plan", required=True)

    fingerprint = sub.add_parser("fingerprint")
    fingerprint.add_argument("--assets-root", required=True)
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _parser().parse_args(argv)
    if args.command == "plan":
        value = plan_run(
            Path(args.assets_root),
            Path(args.state),
            force_new=bool(args.force_new),
        )
        _atomic_json(Path(args.out), value)
        print(json.dumps(value, sort_keys=True))
        return 0
    if args.command == "commit":
        plan = json.loads(Path(args.plan).read_text(encoding="utf-8"))
        value = commit_plan(Path(args.state), plan)
        print(json.dumps(value, sort_keys=True))
        return 0
    if args.command == "fingerprint":
        value = contract_fingerprint(Path(args.assets_root))
        print(json.dumps(value, sort_keys=True))
        return 0
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
