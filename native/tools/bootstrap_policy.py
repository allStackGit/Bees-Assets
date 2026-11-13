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

import numpy as np

from native_training_artifacts import atomic_json, file_sha256, sync_directory
from policy_worker import checked_manifest, install_bees_actor, mlagents_behavior_spec


ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CONFIG = ROOT / "Training" / "rl_1v1_config.yaml"


def bootstrap(output: Path, trainer_config: Path, seed: int, slots: list[int],
              inference_device: str, cargo: str) -> dict:
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
    policies, _, _ = checked_manifest(manifest)
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
        "torch_version": torch.__version__,
    }
    sync_directory(output)
    atomic_json(output / "receipt.json", result)
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="new directory; existing paths are never overwritten")
    parser.add_argument("--trainer-config", type=Path, default=DEFAULT_CONFIG)
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--slots", nargs="+", type=int, default=[0, 1],
                        help="model slots sharing the initial checkpoint (default: 0 1)")
    parser.add_argument("--inference-device", default="cpu",
                        help="resident model device, e.g. cpu or cuda")
    parser.add_argument("--cargo", default="cargo",
                        help="Rust cargo executable used to generate the canonical ABI manifest")
    args = parser.parse_args()
    result = bootstrap(args.output.resolve(), args.trainer_config,
                       args.seed, args.slots, args.inference_device, args.cargo)
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main()
