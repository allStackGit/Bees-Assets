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
import tempfile

import numpy as np

from native_training_artifacts import atomic_json, file_sha256, sync_directory
from policy_worker import checked_manifest, install_bees_actor, mlagents_behavior_spec


ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CONFIG = ROOT / "Training" / "rl_1v1_config.yaml"
U64 = 1 << 64


def write_native_holdout(output: Path, training_run: dict,
                         worlds: int, seed: int, episodes: int) -> None:
    """Create a fixed native Stage holdout independent of training seed shifts.

    The evaluator runs this identical physical scene with each checkpoint;
    only model_manifest changes. No Unity snapshot or second simulation
    implementation is needed.
    """
    if not 1 <= worlds <= 1024 or not 1 <= episodes <= 10000 or not 0 <= seed < U64:
        raise ValueError("invalid native holdout worlds, episodes, or seed")
    run = json.loads(json.dumps(training_run))
    if len(run.get("levels", [])) != 1:
        raise ValueError("native bootstrap holdout requires one source scene")
    original = run["levels"][0]
    if not isinstance(original.get("seed"), int) or not isinstance(
        original.get("world_id"), int
    ) or not isinstance(original.get("level_id"), int):
        raise ValueError("native holdout requires explicit source seed and Level identities")
    if not 0 < original["world_id"] <= (1 << 64) - worlds or not (
        0 < original["level_id"] <= (1 << 63) - worlds
    ):
        raise ValueError("native holdout would exhaust source Level identities")
    per_episode_budget, remainder = divmod(
        run["max_rounds"], run["episodes_per_world"],
    )
    if remainder or per_episode_budget <= 0 or per_episode_budget * episodes >= U64:
        raise ValueError("native holdout round budget is invalid")
    run["run_id"] += "-fixed-holdout"
    run["max_rounds"] = per_episode_budget * episodes
    run["episodes_per_world"] = episodes
    run["levels"] = []
    for index in range(worlds):
        level = dict(original)
        level["world_id"] += index
        level["level_id"] += index
        # The odd golden-ratio stride provides unique seeds for every world
        # over uint64 arithmetic. Unlike learner round seed offsets, these
        # never change between holdout evaluations.
        level["seed"] = (seed + index * 0x9E3779B97F4A7C15) % U64
        if level["seed"] == original["seed"]:
            raise ValueError("holdout seed must differ from the training source seed")
        run["levels"].append(level)
    with (output / "holdout-run.json").open("x", encoding="utf-8", newline="\n") as file:
        json.dump(run, file, indent=2)
        file.write("\n")
        file.flush()
        os.fsync(file.fileno())


def bootstrap(output: Path, trainer_config: Path, seed: int, slots: list[int],
              inference_device: str, cargo: str, scenario: str,
              episodes_per_world: int = 1, holdout_worlds: int | None = None,
              holdout_seed: int = 918273645, holdout_episodes: int = 1) -> dict:
    if output.exists():
        raise FileExistsError(f"refusing to overwrite existing output: {output}")
    if (not slots or len(slots) != len(set(slots)) or
            any(type(slot) is not int or not 0 <= slot < (1 << 32) for slot in slots)):
        raise ValueError("model slots must be distinct uint32 values")
    if not 0 <= seed < (1 << 32):
        raise ValueError("initial policy seed must be a uint32 value")
    if not inference_device:
        raise ValueError("inference device must be nonempty")
    if type(episodes_per_world) is not int or not 1 <= episodes_per_world <= 1000000:
        raise ValueError("episodes_per_world must be between 1 and 1000000")

    # Reject stale/mismatched authored scenarios and missing Rust tooling
    # before importing heavyweight learner libraries or allocating a GPU.
    trainer_config = trainer_config.resolve(strict=True)
    if shutil.which(cargo) is None:
        raise ValueError(f"native bootstrap requires an installed Rust cargo executable: {cargo}")
    example_dir = ROOT / "native" / "examples"
    source_scene = example_dir / f"native-{scenario}.policy.scene.json"
    source_run = example_dir / f"native-{scenario}.run.json"
    scene = json.loads(source_scene.read_text(encoding="utf-8"))
    run = json.loads(source_run.read_text(encoding="utf-8"))
    if (not isinstance(scene, dict) or not isinstance(run, dict) or
            not isinstance(scene.get("stage_name"), str) or
            not scene["stage_name"] or
            not isinstance(run.get("levels"), list) or len(run["levels"]) != 1 or
            not isinstance(run.get("native_stage"), dict)):
        raise ValueError("native bootstrap requires a one-Level native-authored policy scene")
    if not isinstance(scene.get("ships"), list):
        raise ValueError("native bootstrap requires authored policy ships")
    real_teams = {}
    for ship in scene["ships"]:
        if not isinstance(ship, dict):
            raise ValueError("native bootstrap has invalid ship metadata")
        policy = ship.get("policy")
        if policy is None:
            continue
        if not isinstance(policy, dict) or type(policy.get("team_id")) is not int:
            raise ValueError("native bootstrap has an invalid policy agent")
        real_teams.setdefault(policy["team_id"], set()).add(ship.get("side"))
    if (set(real_teams) != {0, 1} or
            len(real_teams[0]) != 1 or len(real_teams[1]) != 1 or
            real_teams[0] == real_teams[1]):
        raise ValueError("native bootstrap requires authentic policy agents on distinct sides")
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
    shutil.copyfile(source_scene, output / "scene.json")
    run.update({
        "run_id": f"native-{scenario}-bootstrap-{seed}",
        "stage_name": scene["stage_name"],
        "python": str(Path(sys.executable).resolve()),
        "policy_worker": str(ROOT / "native" / "tools" / "policy_worker.py"),
        "model_manifest": "manifest.json",
    })
    # One long-lived set of arenas can produce several completed episodes
    # without creating a new rollout process or reloading model weights.
    # The native runner already increments its episode-owned RNG, team
    # permutation, model routing and reward membership on each reset.
    original_round_budget = run["max_rounds"]
    run["max_rounds"] = original_round_budget * episodes_per_world
    run["episodes_per_world"] = episodes_per_world
    if not 1 <= run["max_rounds"] <= (1 << 64) - 1:
        raise ValueError("requested episode budget exceeds native uint64 run rounds")
    level = run["levels"][0]
    level["scene"] = "scene.json"
    level["model_slots"] = {"0": slots[0], "1": slots[1] if len(slots) > 1 else slots[0]}
    level.pop("scripted_queues", None)
    with (output / "run.json").open("x", encoding="utf-8", newline="\n") as file:
        json.dump(run, file, indent=2)
        file.write("\n")
        file.flush()
        os.fsync(file.fileno())
    if holdout_worlds is not None:
        write_native_holdout(
            output, run, holdout_worlds, holdout_seed, holdout_episodes,
        )
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
        "episodes_per_world": episodes_per_world,
        "holdout_worlds": holdout_worlds,
        "holdout_seed": holdout_seed if holdout_worlds is not None else None,
        "holdout_episodes": holdout_episodes if holdout_worlds is not None else None,
        **({
            "holdout_run": "holdout-run.json",
            "holdout_run_sha256": file_sha256(output / "holdout-run.json"),
        } if holdout_worlds is not None else {}),
        "scene": "scene.json",
        "scene_sha256": file_sha256(output / "scene.json"),
        "run": "run.json",
        "run_sha256": file_sha256(output / "run.json"),
        "torch_version": str(torch.__version__),
    }
    sync_directory(output)
    atomic_json(output / "receipt.json", result)
    return result


def verified_existing_bootstrap(output: Path, seed: int, slots: list[int],
                                scenario: str, episodes_per_world: int,
                                holdout_worlds: int | None, holdout_seed: int,
                                holdout_episodes: int) -> dict:
    """Resume only a fully committed, unchanged initial model lineage."""
    if not output.is_dir():
        raise ValueError(f"native bootstrap directory does not exist: {output}")
    receipt = json.loads((output / "receipt.json").read_text(encoding="utf-8"))
    if (not isinstance(receipt, dict) or
            receipt.get("kind") != "native_bees_policy_bootstrap" or
            receipt.get("schema_version") != 1 or
            receipt.get("seed") != seed or receipt.get("slots") != slots or
            receipt.get("scenario") != scenario or
            receipt.get("episodes_per_world") != episodes_per_world or
            receipt.get("holdout_worlds") != holdout_worlds or
            (holdout_worlds is not None and (
                receipt.get("holdout_seed") != holdout_seed or
                receipt.get("holdout_episodes") != holdout_episodes
            ))):
        raise ValueError("bootstrap receipt does not match the requested initial run")
    for name in ("checkpoint", "manifest", "trainer_config", "scene", "run"):
        expected_name = {
            "checkpoint": "checkpoint.pt",
            "manifest": "manifest.json",
            "trainer_config": "trainer-config.yaml",
            "scene": "scene.json",
            "run": "run.json",
        }[name]
        if receipt.get(name) != expected_name:
            raise ValueError(f"initial bootstrap artifact path changed: {name}")
        actual = file_sha256(output / expected_name)
        if actual != receipt.get(f"{name}_sha256"):
            raise ValueError(f"initial bootstrap artifact changed: {name}")
    if holdout_worlds is not None:
        if (receipt.get("holdout_run") != "holdout-run.json" or
                file_sha256(output / "holdout-run.json") != receipt.get("holdout_run_sha256")):
            raise ValueError("frozen native holdout configuration changed")
    config = json.loads((output / "run.json").read_text(encoding="utf-8"))
    if (config.get("episodes_per_world") != episodes_per_world or
            config.get("model_manifest") != "manifest.json" or
            len(config.get("levels", [])) != 1 or
            config["levels"][0].get("scene") != "scene.json" or
            config["levels"][0].get("model_slots") != {
                "0": slots[0], "1": slots[1] if len(slots) > 1 else slots[0]
            }):
        raise ValueError("saved native bootstrap model routing changed")
    # The resident inference loader independently verifies all model-slot
    # weights and the exact frozen v26 ABI, not just the receipt hashes.
    _, loaded, _ = checked_manifest(output / "manifest.json")
    if set(loaded) != set(slots):
        raise ValueError("saved native bootstrap model slots are incomplete")
    return receipt


def run_training_session(output: Path, rounds: int, learner_slot: int | None,
                         rollout_worlds: int | None, rollout_workers: int | None,
                         physics_workers: int | None, torch_threads: int | None,
                         optimizer_device: str, rollout_binary: Path | None,
                         distributed_plan: Path | None, bundle_inbox: Path | None,
                         collector_timeout_seconds: int,
                         evaluation_run_config: Path | None, evaluation_side: int | None,
                         evaluation_policy_seed: int,
                         resume: bool = False,
                         self_play_opponent_slot: int | None = None,
                         self_play_refresh_every: int | None = None) -> None:
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
    if resume:
        command.append("--resume")
    if learner_slot is not None:
        command.extend(["--learner-slot", str(learner_slot)])
    if self_play_opponent_slot is not None:
        command.extend([
            "--self-play-opponent-slot", str(self_play_opponent_slot),
            "--self-play-refresh-every", str(self_play_refresh_every),
        ])
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
    if distributed_plan is not None:
        command.extend(["--distributed-plan", str(distributed_plan.resolve(strict=True)),
                        "--bundle-inbox", str(bundle_inbox.resolve(strict=True)),
                        "--collector-timeout-seconds", str(collector_timeout_seconds)])
    if evaluation_run_config is not None:
        command.extend(["--evaluation-run-config", str(evaluation_run_config.resolve(strict=True)),
                        "--evaluation-side", str(evaluation_side),
                        "--evaluation-policy-seed", str(evaluation_policy_seed)])
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
    parser.add_argument("--episodes-per-world", type=int, default=1,
                        help="complete this many episodes per persistent native arena in each collection")
    parser.add_argument("--cargo", default="cargo",
                        help="Rust cargo executable used to generate the canonical ABI manifest")
    parser.add_argument("--train-rounds", type=int,
                        help="also execute this many native MA-POCA rounds via the existing resumable session")
    parser.add_argument("--resume", action="store_true",
                        help="continue from an unchanged completed bootstrap and existing round receipts")
    parser.add_argument("--learner-slot", type=int,
                        help="model slot to optimize; defaults to the first slot for multi-slot sessions")
    parser.add_argument("--self-play-opponent-slot", type=int,
                        help="opponent model slot to promote from the learner's completed checkpoints")
    parser.add_argument("--self-play-refresh-every", type=int,
                        help="successful PPO rounds between opponent promotions")
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
    parser.add_argument("--distributed-plan", type=Path,
                        help="existing native collection plan for Experiment Hub multi-machine rounds")
    parser.add_argument("--bundle-inbox", type=Path,
                        help="existing writable directory for distributed collection receipts")
    parser.add_argument("--collector-timeout-seconds", type=int, default=0)
    parser.add_argument("--holdout-worlds", type=int,
                        help="generate this many fixed-seed native policy holdout arenas")
    parser.add_argument("--holdout-seed", type=int, default=918273645,
                        help="independent uint64 holdout world seed (default: 918273645)")
    parser.add_argument("--holdout-episodes", type=int, default=1,
                        help="episodes per native holdout arena (default: 1)")
    parser.add_argument("--evaluation-run-config", type=Path,
                        help="existing holdout native Levels evaluated after each finished learner update")
    parser.add_argument("--evaluation-side", type=int, choices=(1, 2),
                        help="source side to measure during holdout evaluation")
    parser.add_argument("--evaluation-policy-seed", type=int, default=0,
                        help="fixed nonnegative int63 holdout inference sampling seed")
    args = parser.parse_args()
    if args.train_rounds is not None and not 1 <= args.train_rounds <= 99999:
        parser.error("--train-rounds must be between 1 and 99999")
    if args.resume and args.train_rounds is None:
        parser.error("--resume requires --train-rounds")
    if not 1 <= args.episodes_per_world <= 1000000:
        parser.error("--episodes-per-world must be between 1 and 1000000")
    if args.learner_slot is not None and args.learner_slot not in args.slots:
        parser.error("--learner-slot must identify one of the configured model slots")
    if (args.self_play_opponent_slot is None) != (args.self_play_refresh_every is None):
        parser.error("self-play needs both --self-play-opponent-slot and --self-play-refresh-every")
    chosen_learner = (
        args.learner_slot if args.learner_slot is not None
        else args.slots[0] if len(args.slots) > 1 else None
    )
    if args.self_play_opponent_slot is not None and (
        args.train_rounds is None or chosen_learner is None or
        args.self_play_opponent_slot == chosen_learner or
        args.self_play_opponent_slot not in args.slots or
        not 1 <= args.self_play_refresh_every <= 99999
    ):
        parser.error("self-play requires training rounds, a distinct opponent model slot, and a positive refresh interval")
    if (args.distributed_plan is None) != (args.bundle_inbox is None):
        parser.error("distributed sessions require both --distributed-plan and --bundle-inbox")
    if args.collector_timeout_seconds < 0:
        parser.error("--collector-timeout-seconds must be nonnegative")
    if args.holdout_worlds is not None and not 1 <= args.holdout_worlds <= 1024:
        parser.error("--holdout-worlds must be between 1 and 1024")
    if not 0 <= args.holdout_seed < U64 or not 1 <= args.holdout_episodes <= 10000:
        parser.error("--holdout-seed must be uint64 and --holdout-episodes must be 1..10000")
    if args.holdout_worlds is None and (
        args.holdout_seed != 918273645 or args.holdout_episodes != 1
    ):
        parser.error("--holdout-seed and --holdout-episodes require --holdout-worlds")
    if args.holdout_worlds is not None and args.evaluation_run_config is not None:
        parser.error("choose generated --holdout-worlds or an existing --evaluation-run-config")
    if (args.holdout_worlds is None and args.evaluation_run_config is None) != (
        args.evaluation_side is None
    ):
        parser.error("holdout evaluation requires --evaluation-side and a native holdout source")
    if not 0 <= args.evaluation_policy_seed < (1 << 63):
        parser.error("--evaluation-policy-seed must be nonnegative int63")
    if (args.evaluation_policy_seed != 0 and args.evaluation_run_config is None
            and args.holdout_worlds is None):
        parser.error("--evaluation-policy-seed requires an evaluation run")
    if args.train_rounds is None and any((
        args.distributed_plan is not None, args.bundle_inbox is not None,
        args.holdout_worlds is not None, args.evaluation_run_config is not None,
        args.evaluation_side is not None,
        args.evaluation_policy_seed != 0, args.collector_timeout_seconds != 0,
    )):
        parser.error("distributed collection and evaluation require --train-rounds")
    if args.distributed_plan is not None and any(
        value is not None for value in (
            args.rollout_worlds, args.rollout_workers, args.physics_workers,
        )
    ):
        parser.error("distributed resource allocations belong to --distributed-plan")
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
    output = args.output.resolve()
    if args.resume:
        result = verified_existing_bootstrap(
            output, args.seed, args.slots, args.scenario, args.episodes_per_world,
            args.holdout_worlds, args.holdout_seed, args.holdout_episodes,
        )
    else:
        if output.exists():
            raise FileExistsError(
                f"native bootstrap destination already exists: {output}; "
                "use --resume only for completed bootstrap receipts"
            )
        if not output.parent.is_dir():
            raise ValueError("native bootstrap output parent must already exist")
        # Model initialization, ABI manifest generation and inference loading
        # can each fail. Never expose a half-built run or checkpoint under
        # the final experiment path. Stage on the same filesystem so the
        # complete, fsynced directory is published with one atomic rename.
        # All generated references are relative to the completed directory,
        # and the resident model loader verifies them before publication.
        with tempfile.TemporaryDirectory(
            prefix=f".{output.name}.bootstrap-", dir=output.parent,
        ) as temporary:
            staged = Path(temporary) / "initialized"
            result = bootstrap(
                staged, args.trainer_config, args.seed, args.slots,
                args.inference_device, args.cargo, args.scenario,
                args.episodes_per_world, args.holdout_worlds,
                args.holdout_seed, args.holdout_episodes,
            )
            if output.exists():
                raise FileExistsError(
                    f"native bootstrap destination appeared during initialization: {output}"
                )
            os.rename(staged, output)
            sync_directory(output.parent)
    print(json.dumps(result, sort_keys=True), flush=True)
    if args.train_rounds is not None:
        learner_slot = chosen_learner
        session = output / "session"
        if args.resume and session.exists() and not session.is_dir():
            raise ValueError("existing native training session is not a directory")
        run_training_session(
            output, args.train_rounds, learner_slot,
            args.rollout_worlds, args.rollout_workers,
            args.physics_workers, args.torch_threads,
            args.optimizer_device, args.rollout_binary,
            args.distributed_plan, args.bundle_inbox,
            args.collector_timeout_seconds,
            (output / "holdout-run.json" if args.holdout_worlds is not None
             else args.evaluation_run_config),
            args.evaluation_side, args.evaluation_policy_seed,
            resume=args.resume and session.is_dir(),
            self_play_opponent_slot=args.self_play_opponent_slot,
            self_play_refresh_every=args.self_play_refresh_every,
        )


if __name__ == "__main__":
    main()
