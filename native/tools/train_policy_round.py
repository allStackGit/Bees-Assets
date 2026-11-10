"""Run one native rollout and one MA-POCA update through the existing executables.

A single-round entry point for operators or the Experiment Hub, not a new
scheduler, simulator or optimizer. Each round uses fresh output paths and
never converts a failed or incomplete rollout into training data.
"""

import argparse
import json
from pathlib import Path
import shutil
import subprocess
import uuid

from policy_worker import file_sha256


def resolved(base: Path, value: str) -> str:
    path = Path(value)
    return str((path if path.is_absolute() else base / path).resolve())


def source_config(path: Path, manifest: Path) -> dict:
    config = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(config, dict):
        raise ValueError("native run configuration must be an object")
    if config.get("model_updates"):
        raise ValueError("one training round cannot include scheduled model updates")
    if not isinstance(config.get("run_id"), str) or not config["run_id"].strip():
        raise ValueError("native run requires a nonempty run_id")
    base = path.parent
    for key in ("collision_matrix", "stage_palette", "policy_worker",
                "connection_checkpoint"):
        if not isinstance(config.get(key), str) or not config[key]:
            raise ValueError(f"run configuration requires {key}")
        config[key] = resolved(base, config[key])
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
    parser.add_argument("--rollout-binary", type=Path,
                        help="use a compiled policy_rollout binary instead of cargo run")
    args = parser.parse_args()

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

    config = source_config(run_path, manifest)
    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=False)
    config["run_id"] = f"{config['run_id']}-{uuid.uuid4().hex}"
    config["notifications"] = str(output / "events.jsonl")
    config["policy_capture"] = str(output / "decisions.bin")
    config_path = output / "run.json"
    with config_path.open("x", encoding="utf-8") as stream:
        json.dump(config, stream, indent=2)
        stream.write("\n")

    print(json.dumps({"kind": "native_training_round_started", "run_config": str(config_path),
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
    print(json.dumps({
        "kind": "native_training_round_complete",
        "run_id": config["run_id"],
        "next_checkpoint": str(next_checkpoint),
        "next_checkpoint_sha256": file_sha256(next_checkpoint),
        "next_manifest": str(next_manifest),
        "next_manifest_sha256": file_sha256(next_manifest),
    }), flush=True)


if __name__ == "__main__":
    main()
