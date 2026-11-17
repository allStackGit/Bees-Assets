"""Publish a verified Experiment Hub native learner round as a playable game.

A Hub round produces an immutable trained checkpoint and manifest but does
not necessarily run inside a long native training session. This adapter uses
the same native session checkpoint publisher and authored source-run
contracts, so a real physical Level becomes playable without a new engine.

Usage:
    python native/tools/publish_round_playable.py source-run.json \
        hub-artifacts/round-result.json new-playable-directory
"""

import argparse
import json
from pathlib import Path

from native_session_selection import publish_latest_playable
from native_training_artifacts import file_sha256


def publish(source_run: Path, round_receipt: Path, output: Path):
    source_run = source_run.resolve(strict=True)
    round_receipt = round_receipt.resolve(strict=True)
    output = output.resolve()
    receipt = json.loads(round_receipt.read_text(encoding="utf-8"))
    if (not isinstance(receipt, dict) or
            receipt.get("schema_version") != 1 or
            receipt.get("kind") != "native_training_round_complete" or
            receipt.get("source_run_config_sha256") != file_sha256(source_run)):
        raise ValueError(
            "Hub round does not match the supplied original physical source run"
        )
    if not isinstance(receipt.get("learner_metrics"), dict):
        raise ValueError("Hub native training round omitted learner verification")

    def artifact(field):
        value = receipt.get(field)
        if not isinstance(value, str) or not value:
            raise ValueError(f"Hub native round omitted {field}")
        path = Path(value).resolve(strict=True)
        if not path.is_file() or file_sha256(path) != receipt.get(field + "_sha256"):
            raise ValueError(f"Hub native training artifact changed: {field}")
        return path

    checkpoint = artifact("next_checkpoint")
    manifest = artifact("next_manifest")
    learner_slot = receipt["learner_metrics"].get("learner_slot")
    if learner_slot is not None and (type(learner_slot) is not int or learner_slot < 0):
        raise ValueError("invalid Hub learner slot")
    # Verify both artifacts BEFORE creating output. The common publisher
    # validates checkpoint/model-slot linkage, stage source, absolute scene
    # dependencies and writes the same SHA-bound read-only game receipt.
    source = {"path": str(source_run), "sha256": file_sha256(source_run)}
    recipe = {
        "run_config": source,
        "initial_checkpoint": {"path": str(checkpoint),
                               "sha256": receipt["next_checkpoint_sha256"]},
        "initial_manifest": {"path": str(manifest),
                             "sha256": receipt["next_manifest_sha256"]},
        "learner_slot": learner_slot,
    }
    if output.exists():
        raise FileExistsError("new native playable export directory already exists")
    output.mkdir(parents=True)
    return publish_latest_playable(
        output, recipe, [(round_receipt.parent, receipt)], manifest, checkpoint,
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_run", type=Path)
    parser.add_argument("round_receipt", type=Path)
    parser.add_argument("output_dir", type=Path)
    args = parser.parse_args()
    print(json.dumps(publish(args.source_run, args.round_receipt, args.output_dir)))


if __name__ == "__main__":
    main()
