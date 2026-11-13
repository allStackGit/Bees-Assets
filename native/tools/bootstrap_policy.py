#!/usr/bin/env python3
"""Create a fresh native Bees v26 MA-POCA actor/critic and verified model manifest.

The same POCATrainer, policy extensions and checkpoint module names are used
by train_policy_capture.py. No Unity process, exported game scene or alternate
neural architecture is involved. The completion receipt is written last.
"""

import argparse
import json
import os
from pathlib import Path
import random
import shutil
import subprocess
import sys

import numpy as np

from native_training_artifacts import atomic_json, file_sha256, sync_directory
from policy_worker import checked_manifest, install_bees_actor, mlagents_behavior_spec


ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CONFIG = ROOT / "Training" / "rl_1v1_config.yaml"


def bootstrap(output: Path, trainer_config: Path, seed: int, slots: list[int],
              inference_device: str, cargo: str, scenario: str) -> dict:
    if output.exists():
        raise FileExistsError(f"refusing to overwrite existing output: {output}")
    if (not slots or len(slots) != len(set(slots)) or
            any(type(slot) is not int or not 0 <= slot < (1 << 32) for slot in slots)):
        raise ValueError("model slots must be distinct uint32 values")
    if not 0 <= seed < (1 << 32):
        raise ValueError("initial policy seed must be a uint32 value")
    if not inference_device:
        raise ValueError("inference device must be nonempty")

    trainer_config = trainer_config.resolve(strict=True)
    import torch
    import yaml
    from mlagents.plugins.trainer_type import register_trainer_plugins
    from mlagents.trainers.behavior_id_utils import BehaviorIdentifiers
    from mlagents.trainers.poca.trainer import POCATrainer
    from mlagents.trainers.settings import TorchSettings, TrainerSettings
    from mlagents.torch_utils import set_torch_config

    install_bees_actor()
    from bees_mlagents_ppo_compat import install_value_estimate_key_fix
    install_value_estimate_key_fix()
    set_torch_config(TorchSettings(device="cpu"))
    torch.set_default_device(None)
    register_trainer_plugins()

    behavior = BehaviorIdentifiers.from_name_behavior_id("BeesRL1v1?team=0")
    document = yaml.safe_load(trainer_config.read_text(encoding="utf-8"))
    settings = TrainerSettings.structure(
        document["behaviors"][behavior.brain_name], TrainerSettings,
    )
    network = settings.network_settings
    if (settings.trainer_type != "poca" or settings.init_path or
            settings.behavioral_cloning or network.memory is not None or
            not network.normalize or (network.hidden_units, network.num_layers) != (128, 3) or
            settings.time_horizon <= 0):
        raise ValueError("expected feed-forward structured v26 MA-POCA trainer settings")

    # Seed before either actor or critic initialization. Reproducibility is
    # pinned by the saved weight digest, not an assumption that different
    # PyTorch versions produce identical pseudorandom streams.
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    output.mkdir(parents=True)
    shutil.copyfile(trainer_config, output / "trainer-config.yaml")

    # Keep the entire native source scenario as an immutable per-run input:
    # later changes to the repository's example scenes cannot silently
    # alter the learning task for this checkpoint generation.
    example_dir = ROOT / "native" / "examples"
    source_scene = example_dir / f"native-{scenario}.policy.scene.json"
    source_run = example_dir / f"native-{scenario}.run.json"
    shutil.copyfile(source_scene, output / "scene.json")
    scene = json.loads((output / "scene.json").read_text(encoding="utf-8"))
    run = json.loads(source_run.read_text(encoding="utf-8"))
    if len(run.get("levels", [])) != 1 or run.get("native_stage") is None:
        raise ValueError("native bootstrap requires a one-Level, entirely native run configuration")
    run.update({
        "run_id": f"native-{scenario}-bootstrap-{seed}",
        "stage_name": scene["stage_name"],
        "python": str(Path(sys.executable).resolve()),
        "policy_worker": str(ROOT / "native" / "tools" / "policy_worker.py"),
        "model_manifest": "manifest.json",
    })
    level = run["levels"][0]
    level["scene"] = "scene.json"
    level["model_slots"] = {"0": slots[0], "1": slots[1] if len(slots) > 1 else slots[0]}
    level.pop("scripted_queues", None)
    with (output / "run.json").open("x", encoding="utf-8", newline="\n") as file:
        json.dump(run, file, indent=2)
        file.write("\n")
        file.flush()
        os.fsync(file.fileno())
    sync_directory(output)

    trainer = POCATrainer(
        behavior.brain_name, 100, settings, True, False, 0, str(output),
    )
    policy = trainer.create_policy(behavior, mlagents_behavior_spec())
    trainer.add_policy(behavior, policy)
    modules = {**policy.get_modules(), **trainer.optimizer.get_modules()}
    if "Policy" not in modules:
        raise RuntimeError("native MA-POCA bootstrap did not create its policy module")
    checkpoint = output / "checkpoint.pt"
    with checkpoint.open("xb") as file:
        torch.save({name: module.state_dict() for name, module in modules.items()}, file)
        file.flush()
        os.fsync(file.fileno())

    slot_file = output / "model-slots.json"
    with slot_file.open("x", encoding="utf-8", newline="\n") as file:
        json.dump([
            {"slot": slot, "backend": "mlagents",
             "path": "checkpoint.pt", "device": inference_device}
            for slot in slots
        ], file, indent=2)
        file.write("\n")
        file.flush()
        os.fsync(file.fileno())

    # Generate the frozen ABI signature from Rust itself. Never duplicate the
    # long observation/action signature in Python or the training config.
    manifest = output / "manifest.json"
    subprocess.run([
        cargo, "run", "--locked", "--quiet", "--manifest-path",
        str(ROOT / "native" / "Cargo.toml"),
        "-p", "bees-host", "--example", "policy_manifest", "--",
        str(slot_file), str(manifest),
    ], cwd=ROOT, check=True)

    # This is also the exact strict actor-loading path used by the resident
    # inference process, not just a JSON/hash format check.
    _, policies, _ = checked_manifest(manifest)
    if set(policies) != set(slots):
        raise RuntimeError("native model bootstrap failed to load every configured slot")
    checkpoint_hash = file_sha256(checkpoint)
    source_hash = file_sha256(output / "trainer-config.yaml")
    result = {
        "schema_version": 1,
        "kind": "native_bees_policy_bootstrap",
        "seed": seed,
        "slots": slots,
        "checkpoint": "checkpoint.pt",
        "checkpoint_sha256": checkpoint_hash,
        "manifest": "manifest.json",
        "manifest_sha256": file_sha256(manifest),
        "trainer_config": "trainer-config.yaml",
        "trainer_config_sha256": source_hash,
        "scenario": scenario,
        "scene": "scene.json",
        "scene_sha256": file_sha256(output / "scene.json"),
        "run": "run.json",
        "run_sha256": file_sha256(output / "run.json"),
        "torch_version": str(torch.__version__),
    }
    sync_directory(output)
    atomic_json(output / "receipt.json", result)
    return result


def run_training_session(output: Path, rounds: int, learner_slot: int | None,
                         rollout_worlds: int | None, rollout_workers: int | None,
                         physics_workers: int | None, torch_threads: int | None,
                         optimizer_device: str, rollout_binary: Path | None) -> None:
    """Hand freshly verified artifacts to the one existing native trainer.

    The session has its own immutable recipe, model lineage, round receipts
    and restartable attempts. No second learner, scheduler or checkpoint
    publication path is introduced by the bootstrapper.
    """
    command = [
        sys.executable, str(Path(__file__).with_name("train_policy_session.py")),
        str(output / "run.json"), str(output / "checkpoint.pt"),
        str(output / "trainer-config.yaml"), str(output / "session"),
        "--rounds", str(rounds), "--device", optimizer_device,
    ]
    if learner_slot is not None:
        command.extend(["--learner-slot", str(learner_slot)])
    for option, value in (
        ("--rollout-worlds", rollout_worlds),
        ("--rollout-workers", rollout_workers),
        ("--physics-workers", physics_workers),
        ("--torch-threads", torch_threads),
    ):
        if value is not None:
            command.extend([option, str(value)])
    if rollout_binary is not None:
        command.extend(["--rollout-binary", str(rollout_binary.resolve(strict=True))])
    subprocess.run(command, cwd=ROOT, check=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="new directory; existing paths are never overwritten")
    parser.add_argument("--trainer-config", type=Path, default=DEFAULT_CONFIG)
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--slots", nargs="+", type=int, default=[0, 1],
                        help="model slots sharing the initial checkpoint (default: 0 1)")
    parser.add_argument("--inference-device", default="cpu",
                        help="resident model device, e.g. cpu or cuda")
    parser.add_argument("--scenario", choices=("duel", "skirmish"), default="duel",
                        help="copy a full native policy scene/run template into the bootstrap")
    parser.add_argument("--cargo", default="cargo",
                        help="Rust cargo executable used to generate the canonical ABI manifest")
    parser.add_argument("--train-rounds", type=int,
                        help="also execute this many native MA-POCA rounds via the existing resumable session")
    parser.add_argument("--learner-slot", type=int,
                        help="model slot to optimize; defaults to the first slot for multi-slot sessions")
    parser.add_argument("--rollout-worlds", type=int,
                        help="number of independent native simulation arenas per training round")
    parser.add_argument("--rollout-workers", type=int,
                        help="number of concurrent native rollout processes")
    parser.add_argument("--physics-workers", type=int,
                        help="CPU physics workers per rollout process")
    parser.add_argument("--torch-threads", type=int,
                        help="optimizer CPU intra-op worker threads")
    parser.add_argument("--optimizer-device", default="cpu",
                        help="MA-POCA optimizer device (cpu, cuda, cuda:0 on ROCm/CUDA)")
    parser.add_argument("--rollout-binary", type=Path,
                        help="prebuilt policy_rollout executable for Windows/Linux or multi-process rolls")
    args = parser.parse_args()
    if args.train_rounds is not None and not 1 <= args.train_rounds <= 99999:
        parser.error("--train-rounds must be between 1 and 99999")
    if args.learner_slot is not None and args.learner_slot not in args.slots:
        parser.error("--learner-slot must identify one of the configured model slots")
    for name, value, limit in (
        ("--rollout-worlds", args.rollout_worlds, 1024),
        ("--rollout-workers", args.rollout_workers, 256),
        ("--physics-workers", args.physics_workers, 128),
        ("--torch-threads", args.torch_threads, 256),
    ):
        if value is not None and not 1 <= value <= limit:
            parser.error(f"{name} must be between 1 and {limit}")
    if (args.train_rounds is None and
            any(value is not None for value in (
                args.learner_slot, args.rollout_worlds, args.rollout_workers,
                args.physics_workers, args.torch_threads, args.rollout_binary,
            ))):
        parser.error("training allocation and learner options require --train-rounds")
    if args.train_rounds is not None and args.rollout_workers is not None:
        worlds = args.rollout_worlds or 1
        if args.rollout_workers > worlds:
            parser.error("--rollout-workers cannot exceed --rollout-worlds")
        if args.rollout_workers > 1 and args.rollout_binary is None:
            parser.error("multiple rollout processes require --rollout-binary")
    if args.train_rounds is None and args.optimizer_device != "cpu":
        parser.error("--optimizer-device requires --train-rounds")
    if args.train_rounds is not None and args.rollout_binary is not None:
        if not args.rollout_binary.resolve().is_file():
            parser.error("--rollout-binary must identify an existing executable")
    result = bootstrap(args.output.resolve(), args.trainer_config,
                       args.seed, args.slots, args.inference_device, args.cargo,
                       args.scenario)
    print(json.dumps(result, sort_keys=True), flush=True)
    if args.train_rounds is not None:
        learner_slot = (args.learner_slot if args.learner_slot is not None
                        else args.slots[0] if len(args.slots) > 1 else None)
        run_training_session(
            args.output.resolve(), args.train_rounds, learner_slot,
            args.rollout_worlds, args.rollout_workers,
            args.physics_workers, args.torch_threads,
            args.optimizer_device, args.rollout_binary,
        )


if __name__ == "__main__":
    main()
