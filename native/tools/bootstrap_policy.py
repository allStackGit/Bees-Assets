#!/usr/bin/env python3
"""Create a fresh native Bees v26 MA-POCA actor/critic and verified model manifest.

The same POCATrainer, policy extensions and checkpoint module names are used
by train_policy_capture.py. No Unity process, exported game scene or alternate
neural architecture is involved. The completion receipt is written last.
"""

import argparse
from contextlib import contextmanager, nullcontext
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
    """Pin independent physical holdouts for every authored source Level.

    Same Stage, CombatWorld, original ship/mission scene, group identity and
    neural model as training. Only host World identities and explicitly
    independent fixed evaluation seeds differ. A partial subset of original
    maps is NOT silently presented as an exhaustive evaluation.
    """
    if not 1 <= worlds <= 1024 or not 1 <= episodes <= 10000 or not 0 <= seed < U64:
        raise ValueError("invalid native holdout worlds, episodes, or seed")
    run = json.loads(json.dumps(training_run))
    sources = run.get("levels")
    if not isinstance(sources, list) or not sources:
        raise ValueError("fixed holdout requires authored physical source Levels")
    count = len(sources)
    if worlds < count or worlds % count != 0:
        raise ValueError(
            f"balanced native holdout must cover all {count} original physical "
            "Levels an equal number of times; increase --holdout-worlds or "
            "use an independent multi-Stage evaluation suite"
        )
    occupied = set()
    original_seeds = set()
    for source in sources:
        if (not isinstance(source, dict) or
                type(source.get("world_id")) is not int or
                type(source.get("level_id")) is not int or
                not 0 < source["world_id"] < U64 or
                not 0 < source["level_id"] < (1 << 63) or
                not isinstance(source.get("scene"), str) or
                not source["scene"] or
                source["world_id"] in occupied):
            raise ValueError("source native holdout identities are incomplete or duplicated")
        occupied.add(source["world_id"])
        single = type(source.get("seed")) is int and source.get("episode_seeds") is None
        series = (source.get("seed") is None and
                  isinstance(source.get("episode_seeds"), list) and
                  len(source["episode_seeds"]) == run["episodes_per_world"])
        if not (single or series):
            raise ValueError("source Level requires its actual independent seed plan")
        values = [source["seed"]] if single else source["episode_seeds"]
        if any(type(value) is not int or not 0 <= value < U64 for value in values):
            raise ValueError("invalid training seed plan for native physical holdout")
        original_seeds.update(values)
    max_world = max(row["world_id"] for row in sources)
    max_level = max(row["level_id"] for row in sources)
    if (len({row["level_id"] for row in sources}) != count or
            max_world >= U64 - (worlds - count) or
            max_level >= (1 << 63) - (worlds - count)):
        raise ValueError("native holdout World/Level identity allocation is exhausted")
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
        original = sources[index % count]
        level = dict(original)
        if index >= count:
            level["world_id"] = max_world + index - count + 1
            level["level_id"] = max_level + index - count + 1
        # Each source map gets exactly worlds/count independent fixed
        # initial seeds. The golden-ratio stride creates distinct 64-bit
        # physical RNG streams without changing the actual game PRNG.
        level_seed = (seed + index * 0x9E3779B97F4A7C15) % U64
        # A holdout must not evaluate the exact seed used by the original
        # training source. The trainer's later per-round shifting can
        # generate new worlds; independent benchmark source seeds remain
        # pinned regardless of that schedule.
        if level_seed in original_seeds:
            raise ValueError(
                "native fixed evaluation seed overlaps a training source seed"
            )
        level["seed"] = level_seed
        level.pop("episode_seeds", None)
        run["levels"].append(level)
    with (output / "holdout-run.json").open("x", encoding="utf-8", newline="\\n") as file:
        json.dump(run, file, indent=2)
        file.write("\\n")
        file.flush()
        os.fsync(file.fileno())


def bootstrap(output: Path, trainer_config: Path, seed: int, slots: list[int],
              inference_device: str, cargo: str, scenario: str,
              episodes_per_world: int = 1, holdout_worlds: int | None = None,
              holdout_seed: int = 918273645, holdout_episodes: int = 1,
              source_scene_override: Path | None = None,
              source_run_override: Path | None = None) -> dict:
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
    # Training-enabled native game releases can ship the exact compiled
    # Rust ABI manifest generator. It uses the same bees-sim policy signature
    # as cargo's example, but does not require a compiler on the target PC.
    manifest_binary = ROOT / ("policy_manifest.exe" if os.name == "nt" else "policy_manifest")
    if not manifest_binary.is_file() and shutil.which(cargo) is None:
        raise ValueError(
            f"native bootstrap requires a packaged policy_manifest binary or Rust cargo: {cargo}"
        )
    example_dir = ROOT / "native" / "examples"
    if scenario == "custom":
        if source_scene_override is None or source_run_override is None:
            raise ValueError("custom bootstrap requires native policy scene and run inputs")
        source_scene = source_scene_override.resolve(strict=True)
        source_run = source_run_override.resolve(strict=True)
    else:
        source_scene = example_dir / f"native-{scenario}.policy.scene.json"
        source_run = example_dir / f"native-{scenario}.run.json"
    scene = json.loads(source_scene.read_text(encoding="utf-8"))
    run = json.loads(source_run.read_text(encoding="utf-8"))
    if (not isinstance(scene, dict) or not isinstance(run, dict) or
            not isinstance(scene.get("stage_name"), str) or
            not scene["stage_name"] or
            not isinstance(run.get("levels"), list) or not run["levels"] or
            len(run["levels"]) > (16384 if scenario == "custom" else 1) or
            not isinstance(run.get("native_stage"), dict)):
        raise ValueError("native bootstrap requires complete native physical Levels")
    if not isinstance(scene.get("ships"), list):
        raise ValueError("native bootstrap requires authored physical policy ships")
    source_scenes = [(source_scene, scene)]
    if scenario == "custom":
        if run.get("model_manifest") or run.get("hivemind"):
            raise ValueError("custom bootstrap cannot inherit a live model or HiveMind session")
        source_scenes = []
        immutable_scenes = {source_scene: scene}
        for index, level in enumerate(run["levels"]):
            if not isinstance(level, dict) or not isinstance(level.get("scene"), str):
                raise ValueError("each custom Level needs its physical scene")
            physical_path = (source_run.parent / level["scene"]).resolve(strict=True)
            if index == 0 and physical_path != source_scene:
                raise ValueError("first custom Level scene differs from --source-scene")
            physical = immutable_scenes.get(physical_path)
            if physical is None:
                physical = json.loads(physical_path.read_text(encoding="utf-8"))
                if not isinstance(physical, dict):
                    raise ValueError("custom native Level scene must be an object")
                immutable_scenes[physical_path] = physical
            source_scenes.append((physical_path, physical))

    expected_slots = {
        "0": slots[0], "1": slots[1] if len(slots) > 1 else slots[0],
    }
    for index, (physical_path, physical) in enumerate(source_scenes):
        if (not isinstance(physical.get("ships"), list) or
                physical.get("stage_name") != scene["stage_name"] or
                physical.get("stage_name") != run.get("stage_name")):
            raise ValueError("all original policy worlds need the same Stage and physical ship schema")
        original_authored = (
            physical.get("schema_version") == 35 and
            physical.get("native_policy_authored_episode") is True and
            physical.get("native_policy_capabilities_captured") is True
        )
        if scenario == "custom":
            if original_authored:
                def unity_vec(name):
                    item = physical.get(name)
                    if (not isinstance(item, dict) or set(item) != {"x", "y"} or
                            any(type(item[axis]) not in (int, float) or
                                not np.isfinite(item[axis]) for axis in ("x", "y"))):
                        raise ValueError(f"custom original source has invalid {name}")
                    return [item["x"], item["y"]]
                bounds = {"min": unity_vec("min"), "max": unity_vec("max")}
                source_group = physical.get("policy_episode", {}).get(
                    "group_participants"
                )
                level_group = run["levels"][index].get("groups")
                if (len(slots) != 2 or
                        physical.get("has_policy_episode") is not True or
                        (physical.get("targeting_context") or {}).get("has_player") is not False or
                        physical.get("activate_hivemind") is not False or
                        not isinstance(source_group, list) or not source_group or
                        not all(isinstance(item, dict) and
                                type(item.get("side")) is int and
                                type(item.get("agent_id")) is int and
                                type(item.get("team_id")) is int and
                                type(item.get("ship_id")) is int
                                for item in source_group) or
                        not isinstance(level_group, dict) or
                        not isinstance(level_group.get("participants"), list) or
                        level_group.get("episode_number") != physical["policy_episode"].get("episode_number") or
                        sorted((item["side"], item["agent_id"]) for item in source_group)
                        != sorted((item["side"], item["agent_id"])
                                  for item in level_group["participants"]) or
                        Path(run.get("policy_worker", "")).resolve() != (
                            ROOT / "native" / "tools" / "policy_worker.py"
                        ).resolve()):
                    raise ValueError(
                        "original fleet bootstrap requires the exact authored "
                        "physical group, independent model slots and v26 worker"
                    )
                members = {
                    (item["ship_id"], item["side"], item["team_id"])
                    for item in source_group if item["ship_id"] != 0
                }
            else:
                bounds = physical.get("bounds")
            if (not (physical.get("native_game_schema") == 1 or original_authored) or
                    not isinstance(physical.get("policy_episode"), dict) or
                    (original_authored and
                     run["levels"][index].get("model_slots") != expected_slots) or
                    run.get("fragment_spawn", {}).get("map_bounds") != bounds):
                raise ValueError(
                    "custom policy Level differs from its source map, policy "
                    "episode, frozen model slots or physical gameplay"
                )
        teams = {}
        bound = set()
        for ship in physical["ships"]:
            if not isinstance(ship, dict):
                raise ValueError("native bootstrap has invalid ship metadata")
            policy = ship.get("policy")
            if original_authored and ship.get("has_bound_policy") is True:
                if ship.get("is_rl_policy_controlled") is not True:
                    raise ValueError("original physical policy ownership is invalid")
                ship_id, side, team = (
                    ship.get("entity_id"), ship.get("side"),
                    ship.get("policy_team_id"),
                )
                if (ship_id, side, team) not in members:
                    raise ValueError("original physical policy differs from its group Agent")
                bound.add(ship_id)
                policy = {"team_id": team}
            if policy is None:
                continue
            if (not isinstance(policy, dict) or
                    type(policy.get("team_id")) is not int or
                    ship.get("side") not in (1, 2)):
                raise ValueError("native bootstrap has an invalid policy agent")
            teams.setdefault(policy["team_id"], set()).add(ship["side"])
        if (set(teams) != {0, 1} or
                len(teams[0]) != 1 or len(teams[1]) != 1 or
                teams[0] == teams[1]):
            raise ValueError(
                f"native bootstrap Level {index + 1} requires real "
                "policy agents on BOTH distinct source factions"
            )
        if original_authored and (
                bound != {item[0] for item in members} or
                len(bound) != len(members)):
            raise ValueError("original source group has unowned or repeated bound actors")
    physical_hashes = {}
    source_scene_digests = []
    for physical_path, _ in source_scenes:
        if physical_path not in physical_hashes:
            physical_hashes[physical_path] = file_sha256(physical_path)
        source_scene_digests.append(physical_hashes[physical_path])
    source_scene_digest = file_sha256(source_scene)
    source_run_digest = file_sha256(source_run)
    trainer_config_digest = file_sha256(trainer_config)
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
    if file_sha256(output / "trainer-config.yaml") != trainer_config_digest:
        raise ValueError("trainer settings changed while initializing the native model")

    # Copy every authored physical World into the same immutable
    # bootstrap. Original ship IDs, objectives, maps, reinforcement gates
    # and Squad permissions are not rebased; only the enclosing host world
    # identities and scene paths were authored earlier by the suite.
    scene_outputs = []
    staged_scene_templates = {}
    for (original_path, _), digest in zip(
        source_scenes, source_scene_digests
    ):
        name = staged_scene_templates.get(digest)
        if name is None:
            index = len(staged_scene_templates)
            name = "scene.json" if index == 0 else f"scene-{index + 1:04d}.json"
            shutil.copyfile(original_path, output / name)
            if file_sha256(output / name) != digest:
                raise ValueError("native physical source changed while initializing the model")
            staged_scene_templates[digest] = name
        scene_outputs.append(name)
    run.update({
        "run_id": f"native-{scenario}-bootstrap-{seed}",
        "stage_name": scene["stage_name"],
        # Preserve the activated interpreter executable. Resolving a
        # venv's bin/python symlink to /usr/bin/python bypasses the venv,
        # changing Torch, ROCm and MA-POCA dependencies during later
        # policy-collection subprocesses. A caller can still explicitly
        # relocate this command when packaging a Windows/Linux run.
        "python": str(Path(sys.executable).absolute()),
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
    for level, scene_name in zip(run["levels"], scene_outputs):
        level["scene"] = scene_name
        level["model_slots"] = expected_slots
        # Custom authored support squads retain their real native command
        # ownership. Shipped examples use pure policy-only fleets.
        if scenario != "custom":
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
    command = (
        [str(manifest_binary), str(slot_file), str(manifest)]
        if manifest_binary.is_file()
        else [
            cargo, "run", "--locked", "--quiet", "--manifest-path",
            str(ROOT / "native" / "Cargo.toml"),
            "-p", "bees-host", "--example", "policy_manifest", "--",
            str(slot_file), str(manifest),
        ]
    )
    subprocess.run(command, cwd=ROOT, check=True)

    if scenario == "custom" and (
        file_sha256(source_run) != source_run_digest or
        any(file_sha256(source_path) != digest
            for source_path, digest in physical_hashes.items())
    ):
        raise ValueError("original native physical scene or Run changed while bootstrapping")
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
        **({
            "source_scene_sha256": source_scene_digest,
            "source_run_sha256": source_run_digest,
            "physical_scene_templates": len(staged_scene_templates),
            "source_scenes": [
                {"source_path": str(source_path), "source_sha256": digest,
                 "scene": name, "scene_sha256": digest}
                for (source_path, _), digest, name in zip(
                    source_scenes, source_scene_digests, scene_outputs,
                )
            ],
        } if scenario == "custom" else {}),
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
                                holdout_episodes: int,
                                source_scene_override: Path | None = None,
                                source_run_override: Path | None = None) -> dict:
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
    source_receipts = []
    if scenario == "custom":
        if source_scene_override is None or source_run_override is None:
            raise ValueError("resuming custom bootstrap requires its original native inputs")
        original_run = source_run_override.resolve(strict=True)
        first_scene = source_scene_override.resolve(strict=True)
        if (file_sha256(first_scene) != receipt.get("source_scene_sha256") or
                file_sha256(original_run) != receipt.get("source_run_sha256")):
            raise ValueError("original native policy source changed since bootstrap")
        source_receipts = receipt.get("source_scenes", [])
        if source_receipts:
            original_config = json.loads(original_run.read_text(encoding="utf-8"))
            source_levels = original_config.get("levels")
            if (not isinstance(source_levels, list) or
                    len(source_receipts) != len(source_levels) or
                    source_receipts[0].get("source_path") != str(first_scene)):
                raise ValueError("resumed native source Level collection identity changed")
            # Up to 16,384 independent Worlds can share one immutable source
            # scene. Verify the FULL original and copied bytes once per
            # distinct path, never 16,384 times per training resume.
            source_digests = {}
            staged_digests = {}
            for index, (row, level) in enumerate(zip(source_receipts, source_levels)):
                if not isinstance(row, dict):
                    raise ValueError("resumed native source Level receipt is malformed")
                name = row.get("scene")
                if (not isinstance(name, str) or not name or
                        name != Path(name).name):
                    raise ValueError("resumed native physical template path is unsafe")
                expected_source = (original_run.parent / level["scene"]).resolve(strict=True)
                copied = output / name
                if (not copied.is_file() or copied.is_symlink() or
                        row.get("source_path") != str(expected_source)):
                    raise ValueError("resumed native physical source identity changed")
                if expected_source not in source_digests:
                    source_digests[expected_source] = file_sha256(expected_source)
                if copied not in staged_digests:
                    staged_digests[copied] = file_sha256(copied)
                if (row.get("source_sha256") != source_digests[expected_source] or
                        row.get("scene_sha256") != staged_digests[copied]):
                    raise ValueError(
                        "resumed original physical Level was modified or reordered"
                    )
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
    expected_scenes = (
        [entry["scene"] for entry in source_receipts] if source_receipts
        else ["scene.json"]
    )
    expected_slots = {
        "0": slots[0], "1": slots[1] if len(slots) > 1 else slots[0]
    }
    if (config.get("episodes_per_world") != episodes_per_world or
            config.get("model_manifest") != "manifest.json" or
            not isinstance(config.get("levels"), list) or
            len(config["levels"]) != len(expected_scenes) or
            any(level.get("scene") != name or
                level.get("model_slots") != expected_slots
                for level, name in zip(config["levels"], expected_scenes))):
        raise ValueError("saved native bootstrap Level ownership or model routing changed")
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
                         bundle_archive_inbox: Path | None,
                         bundle_receiver_listen: str | None,
                         collector_input_outbox: Path | None,
                         hub_dispatch_plan: Path | None, hub_dispatch_port: int,
                         hub_auto_worlds: int | None,
                         collector_timeout_seconds: int, auto_balance_collectors: bool,
                         evaluation_run_config: Path | None, evaluation_side: int | None,
                         evaluation_policy_seed: int,
                         evaluation_every: int,
                         early_stop_patience: int | None,
                         early_stop_minimum_rounds: int,
                         early_stop_minimum_extra_victories: int,
                         resume: bool = False,
                         self_play_opponent_slot: int | None = None,
                         self_play_refresh_every: int | None = None,
                         curriculum_plan: Path | None = None,
                         library_save_root: Path | None = None,
                         library_game_id: str | None = None,
                         library_game_title: str | None = None,
                         monitor_evaluation_run_configs: tuple[Path, ...] = (),
                         checkpoint_selection: str = "primary",
                         evaluation_physics_workers: int | None = None,
                         rollout_gpus: str | None = None,
                         rollout_gpu_mask: str | None = None,
                         evaluation_inference_device: str | None = None) -> None:
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
    if curriculum_plan is not None:
        command.extend(["--curriculum-plan", str(curriculum_plan.resolve(strict=True))])
    for option, value in (
        ("--rollout-worlds", rollout_worlds),
        ("--rollout-workers", rollout_workers),
        ("--physics-workers", physics_workers),
        ("--torch-threads", torch_threads),
    ):
        if value is not None:
            command.extend([option, str(value)])
    if rollout_gpus is not None:
        command.extend(["--rollout-gpus", rollout_gpus])
    if rollout_gpu_mask is not None:
        command.extend(["--rollout-gpu-mask", rollout_gpu_mask])
    if evaluation_inference_device is not None:
        command.extend(["--evaluation-inference-device", evaluation_inference_device])
    if rollout_binary is not None:
        command.extend(["--rollout-binary", str(rollout_binary.resolve(strict=True))])
    if distributed_plan is not None:
        command.extend(["--distributed-plan", str(distributed_plan.resolve(strict=True))])
    if distributed_plan is not None or hub_auto_worlds is not None:
        command.extend(["--bundle-inbox", str(bundle_inbox.resolve(strict=True)),
                        "--collector-timeout-seconds", str(collector_timeout_seconds)])
    if hub_auto_worlds is not None:
        command.extend(["--hub-auto-worlds", str(hub_auto_worlds)])
    if bundle_archive_inbox is not None:
        command.extend([
            "--bundle-archive-inbox", str(bundle_archive_inbox.resolve(strict=True)),
        ])
    if bundle_receiver_listen is not None:
        command.extend(["--bundle-receiver-listen", bundle_receiver_listen])
    if collector_input_outbox is not None:
        command.extend(["--collector-input-outbox", str(collector_input_outbox.resolve(strict=True))])
    if hub_dispatch_plan is not None:
        command.extend(["--hub-dispatch-plan", str(hub_dispatch_plan.resolve(strict=True)),
                        "--hub-dispatch-port", str(hub_dispatch_port)])
    if auto_balance_collectors:
        command.append("--auto-balance-collectors")
    if evaluation_run_config is not None:
        command.extend(["--evaluation-run-config", str(evaluation_run_config.resolve(strict=True)),
                        "--evaluation-side", str(evaluation_side),
                        "--evaluation-policy-seed", str(evaluation_policy_seed)])
    for source in monitor_evaluation_run_configs:
        command.extend(["--monitor-evaluation-run-config", str(source.resolve(strict=True))])
    if checkpoint_selection != "primary":
        command.extend(["--checkpoint-selection", checkpoint_selection])
    if evaluation_every != 1:
        command.extend(["--evaluation-every", str(evaluation_every)])
    if evaluation_physics_workers is not None:
        command.extend(["--evaluation-physics-workers", str(evaluation_physics_workers)])
    if early_stop_patience is not None:
        command.extend([
            "--early-stop-patience", str(early_stop_patience),
            "--early-stop-minimum-rounds", str(early_stop_minimum_rounds),
            "--early-stop-minimum-extra-victories",
            str(early_stop_minimum_extra_victories),
        ])
    if library_save_root is not None:
        command.extend([
            "--library-save-root", str(library_save_root.resolve(strict=True)),
            "--library-game-id", library_game_id,
        ])
        if library_game_title is not None:
            command.extend(["--library-game-title", library_game_title])
    subprocess.run(command, cwd=ROOT, check=True)



@contextmanager
def exclusive_native_training_owner(output: Path):
    """One live bootstrap/session writer per immutable model lineage.

    A kernel-held lock releases on process exit, including power loss or a
    crashed optimizer. Keep its inode across attempts: unlinking the file
    would allow two processes to lock different inodes for the same model.
    This guards BOTH atomic model bootstrap and all optimizer rounds, unlike
    a lock acquired only after the model directory has been published.
    """
    if not output.parent.is_dir():
        raise ValueError("native training output parent directory does not exist")
    lock_path = output.parent / f".{output.name}.native-training.lock"
    with lock_path.open("a+b") as owner:
        if os.name == "nt":
            import msvcrt
            owner.seek(0)
            if not owner.read(1):
                owner.seek(0)
                owner.write(b"\0")
                owner.flush()
            owner.seek(0)
            try:
                msvcrt.locking(owner.fileno(), msvcrt.LK_NBLCK, 1)
            except OSError as error:
                raise RuntimeError(
                    f"native training model is already owned by another live process: {output}"
                ) from error
            try:
                yield
            finally:
                owner.seek(0)
                msvcrt.locking(owner.fileno(), msvcrt.LK_UNLCK, 1)
        else:
            import fcntl
            try:
                fcntl.flock(owner.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            except OSError as error:
                raise RuntimeError(
                    f"native training model is already owned by another live process: {output}"
                ) from error
            try:
                yield
            finally:
                fcntl.flock(owner.fileno(), fcntl.LOCK_UN)



def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="new directory; existing paths are never overwritten")
    parser.add_argument("--trainer-config", type=Path, default=DEFAULT_CONFIG)
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--slots", nargs="+", type=int, default=[0, 1],
                        help="model slots sharing the initial checkpoint (default: 0 1)")
    parser.add_argument("--inference-device", default="cpu",
                        help="resident model device, e.g. cpu or cuda")
    parser.add_argument("--scenario", choices=("duel", "skirmish", "custom"), default="duel",
                        help="copy a shipped or explicit native policy scene/run template")
    parser.add_argument("--source-scene", type=Path,
                        help="custom native_game_schema 1 policy scene")
    parser.add_argument("--source-run", type=Path,
                        help="paired custom native stage and run configuration")
    parser.add_argument("--episodes-per-world", type=int, default=1,
                        help="complete this many episodes per persistent native arena in each collection")
    parser.add_argument("--cargo", default="cargo",
                        help="Rust cargo executable used to generate the canonical ABI manifest")
    parser.add_argument("--train-rounds", type=int,
                        help="also execute this many native MA-POCA rounds via the existing resumable session")
    parser.add_argument("--resume", action="store_true",
                        help="continue from an unchanged completed bootstrap and existing round receipts")
    parser.add_argument("--curriculum-plan", type=Path,
                        help="existing pinned native curriculum Stage plan, applied between optimizer rounds")
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
    parser.add_argument("--rollout-gpus",
                        help="physical GPU ordinals assigned round-robin to native inference processes")
    parser.add_argument("--rollout-gpu-mask", choices=(
        "CUDA_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES", "HIP_VISIBLE_DEVICES"),
        help="inference-process GPU isolation; ROCR_VISIBLE_DEVICES for ROCm Linux")
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
    parser.add_argument("--bundle-archive-inbox", type=Path,
                        help="optional incoming collector tar directory; imports after verified upload")
    parser.add_argument("--bundle-receiver-listen",
                        help="bind an authenticated native streaming rollout receiver on HOST:PORT")
    parser.add_argument("--collector-input-outbox", type=Path,
                        help="existing directory for automatically prepared portable collector input archives")
    parser.add_argument("--hub-dispatch-plan", type=Path,
                        help="preapproved Hub machine placement and unattended collector starts for issued native rounds")
    parser.add_argument("--hub-dispatch-port", type=int, default=7148,
                        help="loopback Hub operator API port")
    parser.add_argument("--hub-auto-worlds", type=int,
                        help="derive one persistent source arena plan from the fixed Hub CPU resource assignments and this many worlds per round")
    parser.add_argument("--collector-timeout-seconds", type=int, default=0)
    parser.add_argument("--auto-balance-collectors", action="store_true",
                        help="conservatively rebalance native distributed arena windows from verified collector throughput")
    parser.add_argument("--holdout-worlds", type=int,
                        help="generate this many fixed-seed native policy holdout arenas")
    parser.add_argument("--holdout-seed", type=int, default=918273645,
                        help="independent uint64 holdout world seed (default: 918273645)")
    parser.add_argument("--holdout-episodes", type=int, default=1,
                        help="episodes per native holdout arena (default: 1)")
    parser.add_argument("--evaluation-run-config", type=Path,
                        help="existing holdout native Levels evaluated after each finished learner update")
    parser.add_argument("--monitor-evaluation-run-config", type=Path, action="append", default=[],
                        help="extra independent native holdout suites (repeatable, requires primary holdout)")
    parser.add_argument("--checkpoint-selection", choices=("primary", "mean", "worst", "mean-scene", "worst-scene"), default="primary",
                        help="optional model selection by primary, mean or worst independent holdout")
    parser.add_argument("--evaluation-side", type=int, choices=(1, 2),
                        help="source side to measure during holdout evaluation")
    parser.add_argument("--evaluation-policy-seed", type=int, default=0,
                        help="fixed nonnegative int63 holdout inference sampling seed")
    parser.add_argument("--evaluation-every", type=int, default=1,
                        help="fixed native holdout cadence in completed PPO rounds (default: 1)")
    parser.add_argument("--evaluation-inference-device", choices=("cpu", "cuda:0"),
                        help="pin fixed holdout and monitor inference to CPU or a visible GPU")
    parser.add_argument("--evaluation-physics-workers", type=int,
                        help="CPU physics workers in each independent holdout, not training rollout workers")
    parser.add_argument("--early-stop-patience", type=int,
                        help="stop when this many fixed-reference holdout rounds show no meaningful victory gain")
    parser.add_argument("--early-stop-minimum-rounds", type=int, default=3,
                        help="minimum completed PPO rounds before early stopping")
    parser.add_argument("--early-stop-minimum-extra-victories", type=int, default=1,
                        help="minimum extra fixed-holdout wins qualifying as a learning improvement")
    parser.add_argument("--library-save-root", type=Path,
                        help="existing native game save directory for verified playable checkpoints")
    parser.add_argument("--library-game-id",
                        help="stable native game gallery identity across learner rounds")
    parser.add_argument("--library-game-title",
                        help="native game gallery title (default: native run ID)")
    args = parser.parse_args()
    if (args.library_save_root is None) != (args.library_game_id is None):
        parser.error("--library-save-root and --library-game-id must be used together")
    if args.library_game_title is not None and args.library_game_id is None:
        parser.error("--library-game-title requires --library-game-id")
    if args.library_game_id is not None:
        limit = 35 if args.holdout_worlds is not None or args.evaluation_run_config is not None else 40
        if (args.train_rounds is None or
                not 1 <= len(args.library_game_id) <= limit or
                any(character not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_"
                    for character in args.library_game_id) or
                not args.library_save_root.is_dir()):
            parser.error("gallery publication requires training, an existing save directory and valid native game ID")
    if args.library_game_title is not None and (
            not 1 <= len(args.library_game_title) <= 120 or
            any(ord(character) < 32 for character in args.library_game_title)):
        parser.error("gallery title must be 1..120 printable characters")
    custom_inputs = args.source_scene is not None and args.source_run is not None
    any_custom_input = args.source_scene is not None or args.source_run is not None
    if (args.scenario == "custom" and not custom_inputs) or (
            args.scenario != "custom" and any_custom_input):
        parser.error("--scenario custom requires both --source-scene and --source-run; shipped scenarios use neither")
    if custom_inputs:
        # Saved in-game fleet scenarios carry a complete, atomic preparation
        # receipt. Verify it even on resume when the game's UI invokes the
        # existing bootstrap directly rather than rerunning the converter.
        prepared = args.source_run.resolve(strict=True).parent
        if (prepared / "training-source.json").exists():
            from prepare_fleet_training import verify_prepared_source
            verify_prepared_source(prepared)
            if (args.source_scene.resolve(strict=True) !=
                    (prepared / "battle.policy.scene.json").resolve(strict=True) or
                    args.source_run.resolve(strict=True) !=
                    (prepared / "battle.policy.run.json").resolve(strict=True)):
                parser.error("saved native fleet bootstrap must use the verified policy scene/run pair")
    if args.train_rounds is not None and not 1 <= args.train_rounds <= 99999:
        parser.error("--train-rounds must be between 1 and 99999")
    if args.resume and args.train_rounds is None:
        parser.error("--resume requires --train-rounds")
    if args.curriculum_plan is not None and args.train_rounds is None:
        parser.error("--curriculum-plan requires --train-rounds")
    if not 1 <= args.evaluation_every <= 99999 or (
            args.evaluation_every != 1 and
            (args.holdout_worlds is None and args.evaluation_run_config is None)):
        parser.error("--evaluation-every requires a holdout and a positive round interval")
    if args.early_stop_patience is not None:
        if (args.train_rounds is None or args.evaluation_side is None or
                (args.holdout_worlds is None and args.evaluation_run_config is None) or
                not 1 <= args.early_stop_patience <= 99999 or
                not 1 <= args.early_stop_minimum_rounds <= args.train_rounds or
                not 1 <= args.early_stop_minimum_extra_victories <= 1000000):
            parser.error("early stopping requires training, holdout evaluation and positive thresholds")
    elif (args.early_stop_minimum_rounds != 3 or
          args.early_stop_minimum_extra_victories != 1):
        parser.error("early-stop thresholds require --early-stop-patience")
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
    if args.hub_auto_worlds is not None and (
            args.hub_dispatch_plan is None or args.distributed_plan is not None or
            not 2 <= args.hub_auto_worlds <= 16384):
        parser.error("--hub-auto-worlds requires a Hub dispatch plan, no separate collector plan, and 2..16384 total Worlds")
    # Validate the exact Hub operator approval and authenticated receiver
    # before importing PyTorch or creating an expensive partial model. The
    # unchanged native planner remains the sole resource-lease authority.
    if args.hub_auto_worlds is not None:
        from native_distributed_collection import derive_hub_resource_plan
        plan = json.loads(args.hub_dispatch_plan.read_text(encoding="utf-8"))
        derive_hub_resource_plan(plan, args.hub_auto_worlds)
        if shutil.which("node") is None or not (
                ROOT / "Training" / "experiment_hub_native_round_dispatch.js").is_file():
            parser.error("native Hub collection requires Node.js and the existing Hub dispatch bridge")
        from native_bundle_receiver import bundle_token
        bundle_token()
    distributed = args.distributed_plan is not None or args.hub_auto_worlds is not None
    if distributed != (args.bundle_inbox is not None):
        parser.error("distributed sessions require a collector plan or --hub-auto-worlds and --bundle-inbox")
    if args.bundle_archive_inbox is not None and (
            not distributed or not args.bundle_archive_inbox.is_dir()):
        parser.error("--bundle-archive-inbox requires distributed collection and an existing directory")
    if args.bundle_receiver_listen is not None and not distributed:
        parser.error("--bundle-receiver-listen requires distributed collection")
    if args.collector_input_outbox is not None and (
            not distributed or not args.collector_input_outbox.is_dir()):
        parser.error("--collector-input-outbox requires distributed collection and an existing directory")
    if args.hub_dispatch_plan is not None and (
            not distributed or not args.hub_dispatch_plan.is_file() or
            args.bundle_receiver_listen is None or args.collector_input_outbox is None):
        parser.error("--hub-dispatch-plan requires an existing Hub plan, distributed collector inputs and receiver")
    if not 1 <= args.hub_dispatch_port <= 65535:
        parser.error("--hub-dispatch-port must be a valid port")
    if args.collector_timeout_seconds < 0:
        parser.error("--collector-timeout-seconds must be nonnegative")
    if args.auto_balance_collectors and not distributed:
        parser.error("--auto-balance-collectors requires distributed collection")
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
    if args.evaluation_physics_workers is not None and (
            args.train_rounds is None or
            args.holdout_worlds is None and args.evaluation_run_config is None or
            not 1 <= args.evaluation_physics_workers <= 128):
        parser.error("--evaluation-physics-workers requires training, a holdout and 1..128 workers")
    if args.monitor_evaluation_run_config and (
            args.train_rounds is None or
            args.holdout_worlds is None and args.evaluation_run_config is None):
        parser.error("extra monitoring suites require training and a primary holdout")
    if len(args.monitor_evaluation_run_config) > 16:
        parser.error("no more than 16 independent native monitoring suites")
    if args.checkpoint_selection in ("mean", "worst") and not args.monitor_evaluation_run_config:
        parser.error("multi-suite model selection requires extra monitoring suites")
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
        args.bundle_archive_inbox is not None,
        args.bundle_receiver_listen is not None,
        args.collector_input_outbox is not None,
        args.hub_dispatch_plan is not None,
        args.hub_auto_worlds is not None,
        args.holdout_worlds is not None, args.evaluation_run_config is not None,
        bool(args.monitor_evaluation_run_config),
        args.checkpoint_selection != "primary",
        args.evaluation_physics_workers is not None,
        args.evaluation_inference_device is not None,
        args.evaluation_side is not None,
        args.evaluation_policy_seed != 0, args.collector_timeout_seconds != 0,
    )):
        parser.error("distributed collection and evaluation require --train-rounds")
    if distributed and any(
        value is not None for value in (
            args.rollout_worlds, args.rollout_workers, args.physics_workers,
            args.rollout_gpus, args.rollout_gpu_mask,
        )
    ):
        parser.error("distributed resource allocations belong to the fixed native source plan")
    if args.rollout_gpus is not None:
        from native_gpu_allocation import parse_rollout_gpus
        try:
            parse_rollout_gpus(args.rollout_gpus)
        except ValueError as error:
            parser.error(str(error))
    elif args.rollout_gpu_mask is not None:
        parser.error("--rollout-gpu-mask requires --rollout-gpus")
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
                args.rollout_gpus, args.rollout_gpu_mask, args.curriculum_plan,
            ))):
        parser.error("training allocation and learner options require --train-rounds")
    # A training-enabled native game distribution places the compiled
    # physics rollout beside the game. Use that exact executable for
    # collection and fixed-seed holdouts, including Hub sessions where
    # remote collection workers are separately owned by Experiment Hub.
    # Only new sessions auto-select it: a resume must preserve the original
    # session's explicit (or Cargo-based) execution recipe.
    if args.train_rounds is not None and args.rollout_binary is None and not args.resume:
        candidate = ROOT / ("policy_rollout.exe" if os.name == "nt" else "policy_rollout")
        if candidate.is_file():
            args.rollout_binary = candidate
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
    owner = exclusive_native_training_owner(output) if args.train_rounds is not None else nullcontext()
    with owner:
        if args.resume:
            result = verified_existing_bootstrap(
                output, args.seed, args.slots, args.scenario, args.episodes_per_world,
                args.holdout_worlds, args.holdout_seed, args.holdout_episodes,
                args.source_scene, args.source_run,
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
                    args.source_scene, args.source_run,
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
                args.bundle_archive_inbox,
                args.bundle_receiver_listen,
                args.collector_input_outbox,
                args.hub_dispatch_plan, args.hub_dispatch_port,
                args.hub_auto_worlds,
                args.collector_timeout_seconds, args.auto_balance_collectors,
                (output / "holdout-run.json" if args.holdout_worlds is not None
                 else args.evaluation_run_config),
                args.evaluation_side, args.evaluation_policy_seed,
                args.evaluation_every, args.early_stop_patience, args.early_stop_minimum_rounds,
                args.early_stop_minimum_extra_victories,
                resume=args.resume and session.is_dir(),
                self_play_opponent_slot=args.self_play_opponent_slot,
                self_play_refresh_every=args.self_play_refresh_every,
                curriculum_plan=args.curriculum_plan,
                library_save_root=args.library_save_root,
                library_game_id=args.library_game_id,
                library_game_title=args.library_game_title,
                monitor_evaluation_run_configs=tuple(args.monitor_evaluation_run_config),
                checkpoint_selection=args.checkpoint_selection,
                evaluation_physics_workers=args.evaluation_physics_workers,
                evaluation_inference_device=args.evaluation_inference_device,
                rollout_gpus=args.rollout_gpus,
                rollout_gpu_mask=args.rollout_gpu_mask,
            )


if __name__ == "__main__":
    main()
