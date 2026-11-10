"""Run one native rollout and one MA-POCA update through the existing executables.

A single-round entry point for operators or the Experiment Hub, not a new
scheduler, simulator or optimizer. Each round uses fresh output paths and
never converts a failed or incomplete rollout into training data.
"""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import uuid

from policy_worker import file_sha256


def resolved(base: Path, value: str) -> str:
    path = Path(value)
    return str((path if path.is_absolute() else base / path).resolve())


def source_config(path: Path, manifest: Path, output: Path) -> dict:
    config = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(config, dict):
        raise ValueError("native run configuration must be an object")
    if config.get("model_updates"):
        raise ValueError("one training round cannot include scheduled model updates")
    if not isinstance(config.get("run_id"), str) or not config["run_id"].strip():
        raise ValueError("native run requires a nonempty run_id")
    base = path.parent
    for key in ("collision_matrix", "stage_palette", "policy_worker"):
        if not isinstance(config.get(key), str) or not config[key]:
            raise ValueError(f"run configuration requires {key}")
        config[key] = resolved(base, config[key])
    if (config.get("hivemind") is not None or config.get("recover_notifications")
            or config.get("recover_outcome_journals")):
        previous = config.get("connection_checkpoint")
        if not isinstance(previous, str) or not previous:
            raise ValueError("network or recovery runs require the original connection checkpoint")
        config["connection_checkpoint"] = resolved(base, previous)
    else:
        # No cross-run HiveMind history belongs to independent model-only training.
        config["connection_checkpoint"] = str(output / "connection-checkpoint.json")
    interpreter = config.get("python")
    if not isinstance(interpreter, str) or not interpreter:
        raise ValueError("run configuration requires its native policy Python interpreter")
    if Path(interpreter).is_absolute() or len(Path(interpreter).parts) > 1:
        config["python"] = resolved(base, interpreter)
    levels = config.get("levels")
    if not isinstance(levels, list) or not levels:
        raise ValueError("run configuration requires at least one Level")
    for level in levels:
        if not isinstance(level, dict) or not isinstance(level.get("scene"), str):
            raise ValueError("each native Level requires a source scene path")
        level["scene"] = resolved(base, level["scene"])
    for index, previous in enumerate(config.get("recover_notifications", [])):
        config["recover_notifications"][index] = resolved(base, previous)
    for previous in config.get("recover_outcome_journals", []):
        previous["path"] = resolved(base, previous["path"])
    config["model_manifest"] = str(manifest)
    return config


def shifted_seeds(config: dict, offset: int) -> None:
    """Shift only source episode RNG seeds, never authored entity identities."""
    if not offset:
        return
    for level in config["levels"]:
        if level.get("seed") is not None:
            fields = [level["seed"]]
            label = "seed"
        elif isinstance(level.get("episode_seeds"), list):
            fields = level["episode_seeds"]
            label = "episode_seeds"
        else:
            raise ValueError("seed offset requires authored seeds for every Level")
        if any(type(seed) is not int or not 0 <= seed < 1 << 64 for seed in fields):
            raise ValueError("seed offset requires unsigned 64-bit source seeds")
        updated = [(seed + offset) % (1 << 64) for seed in fields]
        level[label] = updated[0] if label == "seed" else updated


def check_learner(manifest: Path, checkpoint: Path, slot: int | None) -> None:
    record = json.loads(manifest.read_text(encoding="utf-8"))
    if record.get("format") != "bees-native-model-v1" or not isinstance(record.get("models"), list):
        raise ValueError("native training requires a generated model manifest")
    digest = file_sha256(checkpoint)
    models = record["models"]
    matching = {
        item["slot"] for item in models
        if item["backend"] == "mlagents" and item["sha256"] == digest
    }
    if slot is None:
        if not matching or matching != {item["slot"] for item in models}:
            raise ValueError("mixed-checkpoint training requires --learner-slot")
    elif slot not in matching:
        raise ValueError("learner slot must identify the supplied input checkpoint")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run_config", type=Path)
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("trainer_config", type=Path)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("--model-manifest", type=Path,
                        help="override the run configuration's manifest for the next round")
    parser.add_argument("--learner-slot", type=int)
    parser.add_argument("--device", default="cpu")
    parser.add_argument(
        "--seed-offset", type=int, default=0,
        help="unsigned offset to all authored episode seeds for a new reproducible round",
    )
    parser.add_argument("--rollout-binary", type=Path,
                        help="use a compiled policy_rollout binary instead of cargo run")
    args = parser.parse_args()
    if not 0 <= args.seed_offset < (1 << 64):
        parser.error("--seed-offset must fit uint64")

    run_path = args.run_config.resolve(strict=True)
    checkpoint = args.checkpoint.resolve(strict=True)
    trainer_config = args.trainer_config.resolve(strict=True)
    run_document = json.loads(run_path.read_text(encoding="utf-8"))
    if not isinstance(run_document, dict):
        parser.error("run configuration must be an object")
    manifest_value = args.model_manifest or run_document.get("model_manifest")
    if not manifest_value:
        parser.error("run configuration needs model_manifest or --model-manifest")
    manifest = Path(manifest_value)
    if not manifest.is_absolute():
        manifest = run_path.parent / manifest
    manifest = manifest.resolve(strict=True)
    check_learner(manifest, checkpoint, args.learner_slot)
    if args.rollout_binary is None and shutil.which("cargo") is None:
        parser.error("cargo was not found; provide --rollout-binary")
    if args.rollout_binary is not None:
        binary = args.rollout_binary.resolve(strict=True)
        command = [str(binary)]
    else:
        project = Path(__file__).resolve().parents[1] / "Cargo.toml"
        command = [
            "cargo", "run", "--release", "--manifest-path", str(project),
            "-p", "bees-host", "--example", "policy_rollout", "--",
        ]

    output = args.output_dir.resolve()
    config = source_config(run_path, manifest, output)
    shifted_seeds(config, args.seed_offset)
    output.mkdir(parents=True, exist_ok=False)
    config["run_id"] = f"{config['run_id']}-{uuid.uuid4().hex}"
    config["notifications"] = str(output / "events.jsonl")
    config["policy_capture"] = str(output / "decisions.bin")
    config_path = output / "run.json"
    with config_path.open("x", encoding="utf-8") as stream:
        json.dump(config, stream, indent=2)
        stream.write("\n")

    print(json.dumps({"kind": "native_training_round_started", "run_config": str(config_path),
                      "seed_offset": args.seed_offset,
                      "input_manifest_sha256": file_sha256(manifest),
                      "input_checkpoint_sha256": file_sha256(checkpoint)}), flush=True)
    subprocess.run([*command, str(config_path)], check=True)
    learner_dir = output / "learner"
    train_command = [
        config["python"], str(Path(__file__).with_name("train_policy_capture.py")),
        str(output / "decisions.bin"), str(output / "events.jsonl"),
        str(manifest), str(checkpoint), str(trainer_config), str(learner_dir),
        "--device", args.device,
    ]
    if args.learner_slot is not None:
        train_command += ["--learner-slot", str(args.learner_slot)]
    subprocess.run(train_command, check=True)
    next_checkpoint = learner_dir / "checkpoint.pt"
    next_manifest = learner_dir / "manifest.json"
    metrics = json.loads((learner_dir / "metrics.json").read_text(encoding="utf-8"))
    input_checkpoint_sha = file_sha256(checkpoint)
    input_manifest_sha = file_sha256(manifest)
    checkpoint_sha = file_sha256(next_checkpoint)
    manifest_sha = file_sha256(next_manifest)
    if (not isinstance(metrics, dict) or metrics.get("schema_version") != 1 or
            metrics.get("source") != "bees-native-ma-poca-v1" or
            metrics.get("input_checkpoint_sha256") != input_checkpoint_sha or
            metrics.get("input_manifest_sha256") != input_manifest_sha or
            metrics.get("next_checkpoint_sha256") != checkpoint_sha or
            metrics.get("next_manifest_sha256") != manifest_sha or
            metrics.get("learner_slot") != args.learner_slot or
            type(metrics.get("optimizer_input_steps")) is not int or
            metrics["optimizer_input_steps"] < 1 or
            type(metrics.get("episodes")) is not int or metrics["episodes"] < 1):
        raise ValueError("native learner completion receipt disagrees with its model artifacts")
    result = {
        "schema_version": 1,
        "kind": "native_training_round_complete",
        "run_id": config["run_id"],
        "seed_offset": args.seed_offset,
        "run_config": str(config_path),
        "run_config_sha256": file_sha256(config_path),
        "input_checkpoint_sha256": input_checkpoint_sha,
        "input_manifest_sha256": input_manifest_sha,
        "capture": str(output / "decisions.bin"),
        "journal": str(output / "events.jsonl"),
        "learner_metrics": metrics,
        "next_checkpoint": str(next_checkpoint),
        "next_checkpoint_sha256": checkpoint_sha,
        "next_manifest": str(next_manifest),
        "next_manifest_sha256": manifest_sha,
    }
    # The result is the only handoff marker for schedulers. An interrupted
    # optimizer never publishes it, even when some output files exist.
    with (output / "round-result.json").open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    if os.name == "posix":
        directory = os.open(output, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    print(json.dumps(result), flush=True)


if __name__ == "__main__":
    main()
