"""Run one native rollout and one MA-POCA update through the existing executables.

A single-round entry point for operators or the Experiment Hub, not a new
scheduler, simulator or optimizer. Each round uses fresh output paths and
never converts a failed or incomplete rollout into training data.
"""

import argparse
import copy
from contextlib import ExitStack
import hashlib
import json
import math
import os
import re
from pathlib import Path, PurePosixPath
import shutil
import signal
import subprocess
from tempfile import TemporaryDirectory, NamedTemporaryFile
import time
import traceback
import uuid

from native_mission_contract import (
    level_has_goal, verified_policy_observations, combined_policy_observations,
)
from native_training_artifacts import (
    file_sha256, check_learner, sync_directory, normalized_python_source_sha256,
    native_source_contract_sha256,
    verified_source_scene_cache, combined_source_scene_cache,
    native_manifest_model_dependencies, combined_rollout_lifecycle_metrics,
    RolloutProcessSampler,
)
from native_distributed_collection import verify_bundle, aggregate_collection_metrics
from transfer_policy_bundle import artifact_paths, verify_bundle_files
from native_gpu_allocation import (
    parse_rollout_gpus, validate_gpu_allocation, validate_leased_gpu_inference,
    rollout_worker_environment,
)


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
    if config.get("native_stage") is not None:
        if not isinstance(config["native_stage"], dict) or any(
            config.get(key) is not None for key in ("collision_matrix", "stage_palette")
        ):
            raise ValueError("native_stage cannot be combined with exported Stage asset paths")
    else:
        for key in ("collision_matrix", "stage_palette"):
            if not isinstance(config.get(key), str) or not config[key]:
                raise ValueError(f"run configuration requires {key} or native_stage")
            config[key] = resolved(base, config[key])
    if not isinstance(config.get("policy_worker"), str) or not config["policy_worker"]:
        raise ValueError("run configuration requires policy_worker")
    config["policy_worker"] = resolved(base, config["policy_worker"])
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
    if interpreter == "auto":
        config["python"] = "python" if os.name == "nt" else "python3"
    elif Path(interpreter).is_absolute() or len(Path(interpreter).parts) > 1:
        # Keep venv/bin/python as the executable, not its resolved system
        # target. Asset and model inputs still use canonical source paths.
        config["python"] = os.path.abspath(base / interpreter)
    levels = config.get("levels")
    if not isinstance(levels, list) or not levels:
        raise ValueError("run configuration requires at least one Level")
    for level in levels:
        if not isinstance(level, dict) or not isinstance(level.get("scene"), str):
            raise ValueError("each native Level requires a source scene path")
        level["scene"] = resolved(base, level["scene"])
        # A real playable policy opponent keeps its model-controlled ships
        # but intentionally uses GAME terminal rules without MA-POCA rewards.
        # Never turn those encounters into apparently valid training steps.
        scene = json.loads(Path(level["scene"]).read_text(encoding="utf-8"))
        if not isinstance(scene, dict) or scene.get("policy_gameplay") is True:
            raise ValueError(
                "native training requires a source policy episode, not a playable model-opponent game"
            )
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


def expand_rollout_worlds(config: dict, requested_worlds: int, world_start: int = 0) -> None:
    """Allocate a reproducible window of authentic source Level instances.

    Global world indices are stable across independent machine collectors.
    Index 0..source_count-1 uses the exact authored IDs and seed plans;
    later indices get noncolliding IDs and deterministic derived seeds.
    Native scene construction still remaps the source squad lifetimes.
    """
    originals = tuple(config["levels"])
    source_count = len(originals)
    if (type(requested_worlds) is not int or
            not 1 <= requested_worlds <= 1024 or
            type(world_start) is not int or world_start < 0):
        raise ValueError("rollout world window requires 1..1024 Worlds and a nonnegative start")
    if world_start == 0 and requested_worlds == source_count:
        return

    if config.get("hivemind") is not None or any(config.get(key) for key in (
        "recover_notifications", "recover_outcome_journals", "model_updates",
    )):
        raise ValueError("partitioned native rollouts require model-only, unrecovered Levels")
    for key, maximum in (("world_id", (1 << 64) - 1),
                         ("level_id", (1 << 63) - 1)):
        ids = [level.get(key) for level in originals]
        last = world_start + requested_worlds - 1
        # The source's own IDs are always reused for its first occurrence.
        # Replica IDs occupy one continuous interval above their maximum.
        extra = max(0, last - source_count + 1)
        if (any(type(value) is not int or not 1 <= value <= maximum for value in ids)
                or len(set(ids)) != source_count
                or max(ids) > maximum - extra):
            raise ValueError(f"native partition has invalid or exhausted {key} identities")

    episodes = config.get("episodes_per_world", 1)
    for level in originals:
        has_seed = type(level.get("seed")) is int and level.get("episode_seeds") is None
        has_episode_seeds = (
            level.get("seed") is None and isinstance(level.get("episode_seeds"), list)
            and len(level["episode_seeds"]) == episodes
        )
        if not has_seed and not has_episode_seeds:
            raise ValueError("partitioned Level needs an authored seed or episode seed plan")
        values = [level["seed"]] if has_seed else level["episode_seeds"]
        if any(type(seed) is not int or not 0 <= seed < (1 << 64) for seed in values):
            raise ValueError("partitioned Level seeds must be unsigned 64-bit integers")

    highest_world = max(level["world_id"] for level in originals)
    highest_level = max(level["level_id"] for level in originals)
    selected = []
    for index in range(world_start, world_start + requested_worlds):
        source_index = index % source_count
        replica = index // source_count
        clone = copy.deepcopy(originals[source_index])
        if replica:
            clone["world_id"] = highest_world + index - source_count + 1
            clone["level_id"] = highest_level + index - source_count + 1

            def derive(seed: int, episode: int) -> int:
                label = (f"bees-native-world-v1:{config['run_id']}:{source_index}:"
                         f"{replica}:{episode}:{seed}")
                return int.from_bytes(hashlib.sha256(label.encode("utf-8")).digest()[:8], "little")

            if clone.get("seed") is not None:
                clone["seed"] = derive(clone["seed"], 0)
            else:
                clone["episode_seeds"] = [
                    derive(seed, episode) for episode, seed in enumerate(clone["episode_seeds"])
                ]
        selected.append(clone)
    config["levels"] = selected


def run_stage(command, diagnostic_path):
    if diagnostic_path is None:
        subprocess.run(command, check=True)
    else:
        with diagnostic_path.open("x", encoding="utf-8") as stream:
            try:
                subprocess.run(command, check=True, stdout=stream, stderr=subprocess.STDOUT)
            except Exception:
                traceback.print_exc(file=stream)
                raise


def rollout_metrics(summary_path, config, manifest_sha):
    summary = json.loads(summary_path.read_text(encoding="utf-8"))
    expected_episodes = len(config["levels"]) * config.get("episodes_per_world", 1)
    if (summary["kind"] != "run_final" or summary["run_id"] != config["run_id"] or
            summary["policy_manifest_sha256"] != manifest_sha or
            summary["completed_episodes"] != expected_episodes or
            any(summary[key] != 0 for key in ("aborted_worlds", "failed_worlds", "running_worlds")) or
            summary["failed_window"] is not None or summary["cleanup_errors"]):
        raise ValueError("native rollout summary does not identify a completed training rollout")
    throughput = summary["throughput"]
    counts = {key: throughput[key] for key in ("successful_world_steps", "successful_policy_rows")}
    times = {key: throughput[key] for key in (
        "wall_seconds", "startup_wall_seconds", "execution_wall_seconds", "finalization_wall_seconds",
    )}
    if (any(type(value) is not int or value < 1 for value in counts.values()) or
            summary["captured_policy_rows"] != counts["successful_policy_rows"] or
            any(type(value) not in (float, int) or not math.isfinite(value) or value < 0
                for value in times.values()) or times["wall_seconds"] <= 0 or
            times["execution_wall_seconds"] <= 0):
        raise ValueError("native rollout summary contains invalid work or timing")
    metrics = {
        **counts, **times,
        "worlds": len(config["levels"]),
        "episodes": expected_episodes,
        # Overall wall includes child startup/finalization. The execution
        # denominator isolates actual simulator work on this shard.
        "world_steps_per_wall_second": counts["successful_world_steps"] / times["wall_seconds"],
        "policy_rows_per_wall_second": counts["successful_policy_rows"] / times["wall_seconds"],
        "world_steps_per_execution_second":
            counts["successful_world_steps"] / times["execution_wall_seconds"],
        "policy_rows_per_execution_second":
            counts["successful_policy_rows"] / times["execution_wall_seconds"],
    }
    parsed_scene = verified_source_scene_cache(
        summary.get("source_scene_template_cache"),
        len(config["levels"]), expected_episodes,
    )
    if parsed_scene is not None:
        metrics["source_scene_template_cache"] = parsed_scene
    # Actual reinforcement and policy enrollment events are recorded by
    # the shared runtime, never inferred from authored plans or reserved
    # entity IDs. An older runner's missing metric remains genuinely absent.
    arrivals = summary.get("reinforcement_arrivals")
    if arrivals is not None:
        keys = ("waves", "squads", "ships", "policy_agents")
        if (not isinstance(arrivals, dict) or
                any(type(arrivals.get(key)) is not int or arrivals[key] < 0
                    for key in keys) or
                arrivals["policy_agents"] > arrivals["ships"]):
            raise ValueError("native rollout has invalid reinforcement admissions")
        metrics["reinforcement_arrivals"] = {
            key: arrivals[key] for key in keys
        }
        grouped = arrivals.get("group_agents")
        if grouped is not None:
            if (type(grouped) is not int or grouped < 0 or
                    grouped > arrivals["policy_agents"]):
                raise ValueError("native rollout has invalid MA-POCA reinforcement admissions")
            metrics["reinforcement_arrivals"]["group_agents"] = grouped
    wakeups = summary.get("reinforcement_wakeup_work")
    if wakeups is not None:
        keys = ("idle_ticks_skipped", "wakeup_scans")
        if (not isinstance(wakeups, dict) or
                any(type(wakeups.get(key)) is not int or wakeups[key] < 0
                    for key in keys) or
                sum(wakeups[key] for key in keys) >
                counts["successful_world_steps"]):
            raise ValueError("native rollout has inconsistent reinforcement wakeup measurements")
        metrics["reinforcement_wakeup_work"] = {
            key: wakeups[key] for key in keys
        }
    queens = summary.get("queen_minion_controllers")
    if queens is not None:
        keys = ("policy_minions_bound", "ungrouped_policy_minions_bound",
                "ordinary_minions_preserved")
        if (not isinstance(queens, dict) or
                any(type(queens.get(key)) is not int or queens[key] < 0
                    for key in keys) or
                queens["ungrouped_policy_minions_bound"] >
                queens["policy_minions_bound"]):
            raise ValueError("native rollout has invalid dynamic Queen controller measurements")
        metrics["queen_minion_controllers"] = {
            key: queens[key] for key in keys
        }
        reclaimed = queens.get("retired_policy_routes_reclaimed")
        if reclaimed is not None:
            if (type(reclaimed) is not int or reclaimed < 0 or
                    reclaimed > queens["policy_minions_bound"]):
                raise ValueError("invalid reclaimed dynamic policy route count")
            metrics["queen_minion_controllers"]["retired_policy_routes_reclaimed"] = reclaimed
        recycled = queens.get("recycled_group_agent_bindings")
        if recycled is not None:
            if (type(recycled) is not int or recycled < 0 or
                    recycled > queens["policy_minions_bound"]):
                raise ValueError("invalid recycled native MA-POCA controller count")
            metrics["queen_minion_controllers"]["recycled_group_agent_bindings"] = recycled
    borders = summary.get("map_border_ships")
    if borders is not None:
        keys = ("entries", "movement_stops", "override_skips",
                "lethal_hits", "lethal_kills",
                "confinement_position_corrections",
                "confinement_velocity_clamps",
                "confinement_unfittable_hulls",
                "confinement_missing_profiles", "completed_episodes")
        if (not isinstance(borders, dict) or
                any(type(borders.get(key)) is not int or borders[key] < 0
                    for key in keys) or
                borders["completed_episodes"] != expected_episodes or
                any(borders[key] > borders["entries"] for key in (
                    "movement_stops", "override_skips", "lethal_hits",
                    "lethal_kills")) or
                borders["lethal_kills"] > borders["lethal_hits"]):
            raise ValueError("native rollout contains inconsistent physical border events")
        metrics["map_border_ships"] = {
            key: borders[key] for key in keys
        }
    ship_slots = summary.get("ship_slot_work")
    if ship_slots is not None:
        keys = ("integrated_bodies", "inactive_ship_slots_skipped",
                "inactive_ship_grid_slots_skipped")
        optional = ("inactive_obstacle_slots_skipped",
                    "inactive_obstacle_grid_slots_skipped")
        if isinstance(ship_slots, dict) and any(key in ship_slots for key in optional):
            keys += optional
        if isinstance(ship_slots, dict) and "inactive_projectile_slots_skipped" in ship_slots:
            keys += ("inactive_projectile_slots_skipped",)
        if (not isinstance(ship_slots, dict) or
                any(type(ship_slots.get(key)) is not int or ship_slots[key] < 0
                    for key in keys)):
            raise ValueError("native rollout contains inconsistent active body slot work")
        metrics["ship_slot_work"] = {key: ship_slots[key] for key in keys}
    # Preserve actual model-step CPU utilization reported by the native
    # simulator, not just the requested per-process thread limit. These
    # counters distinguish real parallel rounds from Stage-order fallbacks.
    physics_ints = (
        "requested_physics_workers", "parallel_physics_rounds",
        "parallel_stage_id_serialized_rounds",
        "parallel_stage_id_serialized_arenas",
        "peak_parallel_physics_workers",
    )
    requested = config.get("physics_workers", 1)
    if type(requested) is not int or not 1 <= requested <= 128:
        raise ValueError("invalid native physics worker allocation")
    metrics["requested_physics_worker_slots"] = requested
    has_physics = any(key in throughput for key in (
        *physics_ints, "parallel_worker_cpu_wall_seconds_sum",
    ))
    if has_physics:
        physics = {key: throughput.get(key) for key in physics_ints}
        if (any(type(value) is not int or value < 0 for value in physics.values()) or
                physics["requested_physics_workers"] != requested or
                physics["peak_parallel_physics_workers"] > min(requested, len(config["levels"])) or
                any(physics[key] > summary["rounds"] for key in (
                    "parallel_physics_rounds", "parallel_stage_id_serialized_rounds",
                ))):
            raise ValueError("native rollout summary has invalid physical worker measurements")
        worker_seconds = throughput.get("parallel_worker_cpu_wall_seconds_sum")
        if (type(worker_seconds) not in (float, int) or
                not math.isfinite(worker_seconds) or worker_seconds < 0):
            raise ValueError("native rollout summary has invalid physical worker timing")
        metrics.update(physics)
        metrics["parallel_worker_cpu_wall_seconds_sum"] = worker_seconds
        # The direct/queued split was introduced after the original
        # physical-worker counters. Do not invent historical values.
        dispatch_keys = ("parallel_physics_direct_rounds", "parallel_physics_queued_rounds")
        if any(key in throughput for key in dispatch_keys):
            if (any(type(throughput.get(key)) is not int or throughput[key] < 0
                    for key in dispatch_keys) or
                    sum(throughput[key] for key in dispatch_keys) !=
                    physics["parallel_physics_rounds"]):
                raise ValueError("native rollout physics dispatch counters are inconsistent")
            metrics.update({key: throughput[key] for key in dispatch_keys})
    elif requested != 1 or "physics_workers" in config:
        # Explicit physical parallelism must be acknowledged by a runner
        # that implements it; old binaries may be used only for untouched
        # single-worker configurations.
        raise ValueError("native runner did not report requested physical worker allocation")
    # Native escort/survival/demolition policies must actually receive the
    # authored goal through the v26 reserved channels. Refuse a successful
    # physical rollout from an old binary that still emitted blind PPO rows.
    has_native_mission = any(level_has_goal(level) for level in config["levels"])
    observation_work = verified_policy_observations(
        summary.get("policy_observations"), require_goal=has_native_mission,
    )
    if observation_work is not None:
        metrics["policy_observations"] = observation_work
    model_timing = summary.get("sampled_model_timing")
    if model_timing is not None:
        calls, rows, seconds = (model_timing[key] for key in ("calls", "rows", "wall_seconds"))
        if (type(calls) is not int or type(rows) is not int or
                not 0 < calls <= rows <= counts["successful_policy_rows"] or
                type(seconds) not in (float, int) or not math.isfinite(seconds) or seconds < 0):
            raise ValueError("native rollout summary contains invalid sampled model timing")
        metrics["sampled_model_timing"] = model_timing
    return metrics


def prepare_rollout(config, directory, *, resume=False):
    original_id = config["run_id"]
    config["notifications"] = str(directory / "events.jsonl")
    config["policy_capture"] = str(directory / "decisions.bin")
    config["rollout_summary"] = str(directory / "rollout-summary.json")
    config_path = directory / "run.json"
    if resume and config_path.exists():
        if config_path.is_symlink() or not config_path.is_file():
            raise ValueError("previous native rollout config is not an owned file")
        previous = json.loads(config_path.read_text(encoding="utf-8"))
        previous_id = previous.get("run_id") if isinstance(previous, dict) else None
        if (not isinstance(previous_id, str) or
                re.fullmatch(re.escape(original_id) + r"-[0-9a-f]{32}", previous_id) is None or
                previous != {**config, "run_id": previous_id}):
            raise ValueError("previous native rollout is not the same immutable collector shard")
        config["run_id"] = previous_id
        return config_path
    config["run_id"] = f"{original_id}-{uuid.uuid4().hex}"
    with config_path.open("x", encoding="utf-8") as stream:
        json.dump(config, stream, indent=2)
        stream.write("\n")
    return config_path


def combined_rollout_metrics(shards, wall_seconds):
    if not math.isfinite(wall_seconds) or wall_seconds <= 0:
        raise ValueError("parallel native rollout has invalid wall timing")
    metrics = {
        "workers": len(shards),
        "wall_seconds": wall_seconds,
        **{key: sum(shard[key] for shard in shards)
           for key in ("successful_world_steps", "successful_policy_rows", "worlds", "episodes")},
    }
    # The parallel wall denominator includes launch/join overhead and can
    # be compared against a one-process run without adding together
    # overlapping worker execution times.
    metrics["world_steps_per_wall_second"] = metrics["successful_world_steps"] / wall_seconds
    metrics["policy_rows_per_wall_second"] = metrics["successful_policy_rows"] / wall_seconds
    # Summary-only imbalance evidence. No per-world/per-frame logging or
    # scheduler feedback; each shard keeps its frozen share of Levels.
    metrics.update({
        "worker_execution_min_seconds": min(shard["execution_wall_seconds"] for shard in shards),
        "worker_execution_max_seconds": max(shard["execution_wall_seconds"] for shard in shards),
        "worker_world_steps_min": min(shard["successful_world_steps"] for shard in shards),
        "worker_world_steps_max": max(shard["successful_world_steps"] for shard in shards),
        "worker_world_steps_per_execution_second_min":
            min(shard["world_steps_per_execution_second"] for shard in shards),
        "worker_world_steps_per_execution_second_max":
            max(shard["world_steps_per_execution_second"] for shard in shards),
        "requested_physics_worker_slots":
            sum(shard["requested_physics_worker_slots"] for shard in shards),
        "shards_with_physics_worker_measurements":
            sum("parallel_physics_rounds" in shard for shard in shards),
    })
    if all("policy_observations" in shard for shard in shards):
        metrics["policy_observations"] = combined_policy_observations(
            [shard["policy_observations"] for shard in shards]
        )
    if all("parallel_physics_rounds" in shard for shard in shards):
        metrics.update({
            "parallel_physics_rounds":
                sum(shard["parallel_physics_rounds"] for shard in shards),
            "parallel_stage_id_serialized_rounds":
                sum(shard["parallel_stage_id_serialized_rounds"] for shard in shards),
            "parallel_stage_id_serialized_arenas":
                sum(shard["parallel_stage_id_serialized_arenas"] for shard in shards),
            "parallel_worker_cpu_wall_seconds_sum":
                sum(shard["parallel_worker_cpu_wall_seconds_sum"] for shard in shards),
            "peak_parallel_physics_workers":
                max(shard["peak_parallel_physics_workers"] for shard in shards),
        })
    border_keys = ("entries", "movement_stops", "override_skips",
                   "lethal_hits", "lethal_kills",
                   "confinement_position_corrections",
                   "confinement_velocity_clamps",
                   "confinement_unfittable_hulls",
                   "confinement_missing_profiles", "completed_episodes")
    if all("map_border_ships" in shard for shard in shards):
        metrics["map_border_ships"] = {
            key: sum(shard["map_border_ships"][key] for shard in shards)
            for key in border_keys
        }
    slot_keys = ("integrated_bodies", "inactive_ship_slots_skipped",
                 "inactive_ship_grid_slots_skipped")
    if all("ship_slot_work" in shard for shard in shards):
        metrics["ship_slot_work"] = {
            key: sum(shard["ship_slot_work"][key] for shard in shards)
            for key in slot_keys
        }
        # Old binaries did not yet track pooled asteroid slots. Do not
        # fabricate zeros for the missing physical evidence.
        obstacle_keys = ("inactive_obstacle_slots_skipped",
                         "inactive_obstacle_grid_slots_skipped")
        if all(all(key in shard["ship_slot_work"] for key in obstacle_keys)
               for shard in shards):
            metrics["ship_slot_work"].update({
                key: sum(shard["ship_slot_work"][key] for shard in shards)
                for key in obstacle_keys
            })
        # A round may combine old and new collectors. Missing values mean
        # unmeasured, never a fabricated zero for those older binaries.
        projectile_key = "inactive_projectile_slots_skipped"
        if all(projectile_key in shard["ship_slot_work"] for shard in shards):
            metrics["ship_slot_work"][projectile_key] = sum(
                shard["ship_slot_work"][projectile_key] for shard in shards)
    # Mixed historical collector binaries may report only the older
    # parallel-physics totals. Preserve unknown dispatch provenance as
    # absent, never treat missing direct rounds as zero.
    dispatch_keys = ("parallel_physics_direct_rounds", "parallel_physics_queued_rounds")
    if all(all(key in shard for key in dispatch_keys) for shard in shards):
        metrics.update({key: sum(shard[key] for shard in shards) for key in dispatch_keys})
    scene_cache = combined_source_scene_cache(shards)
    if scene_cache is not None:
        metrics["source_scene_template_cache"] = scene_cache
    # Retain per-process evidence so a resource scheduler can distinguish
    # an overloaded process, a scene-dependent straggler and accelerator
    # inference time without requiring frame-level logs or guessed CPU use.
    metrics["worker_shards"] = [
        {
            "worker": index,
            "worlds": shard["worlds"],
            "episodes": shard["episodes"],
            "world_steps": shard["successful_world_steps"],
            "policy_rows": shard["successful_policy_rows"],
            "wall_seconds": shard["wall_seconds"],
            "execution_wall_seconds": shard["execution_wall_seconds"],
            "world_steps_per_execution_second":
                shard["world_steps_per_execution_second"],
            "policy_rows_per_execution_second":
                shard["policy_rows_per_execution_second"],
            "requested_physics_workers": shard["requested_physics_worker_slots"],
            **({"reinforcement_arrivals": shard["reinforcement_arrivals"]}
               if "reinforcement_arrivals" in shard else {}),
            **({"reinforcement_wakeup_work": shard["reinforcement_wakeup_work"]}
               if "reinforcement_wakeup_work" in shard else {}),
            **({"queen_minion_controllers": shard["queen_minion_controllers"]}
               if "queen_minion_controllers" in shard else {}),
            **({key: shard[key] for key in (
                "parallel_physics_rounds",
                "parallel_physics_direct_rounds",
                "parallel_physics_queued_rounds",
                "parallel_stage_id_serialized_rounds",
                "parallel_stage_id_serialized_arenas",
                "peak_parallel_physics_workers",
                "parallel_worker_cpu_wall_seconds_sum",
            ) if key in shard} if "parallel_physics_rounds" in shard else {}),
            **({"sampled_model_timing": shard["sampled_model_timing"]}
               if "sampled_model_timing" in shard else {}),
            **({"policy_observations": shard["policy_observations"]}
               if "policy_observations" in shard else {}),
            **({"source_scene_template_cache": shard["source_scene_template_cache"]}
               if "source_scene_template_cache" in shard else {}),
        }
        for index, shard in enumerate(shards)
    ]
    metrics.update(combined_rollout_lifecycle_metrics(shards))
    samples = [shard["sampled_model_timing"] for shard in shards if "sampled_model_timing" in shard]
    metrics["shards_with_sampled_model_timing"] = len(samples)
    # Sum inference timing only when every owned rollout process supplied
    # the same counter. A partial sample remains visible per shard rather
    # than masquerading as complete GPU/CPU inference time.
    if len(samples) == len(shards):
        metrics["sampled_model_timing"] = {
            key: sum(sample[key] for sample in samples) for key in ("calls", "rows", "wall_seconds")
        }
    return metrics


def stop_parallel_rollout_workers(processes):
    """Retire the owned native process trees after any failed collection.

    On POSIX the Rust group leader may already have exited nonzero while
    its Python inference descendant is still running. A parent-only live
    list misses that child. Each rollout was launched with a dedicated
    session ID equal to its PID, so terminate the *owned process group*
    even when the leader has already exited.
    """
    live = [process for process in processes if process.poll() is None]
    if os.name == "nt":
        if not live:
            return
        # Windows taskkill /T traverses a still-live Rust process tree.
        for process in live:
            try:
                result = subprocess.run(
                    ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    check=False, timeout=5,
                )
                if result.returncode != 0 and process.poll() is None:
                    process.kill()
            except (OSError, subprocess.TimeoutExpired):
                if process.poll() is None:
                    try:
                        process.kill()
                    except ProcessLookupError:
                        pass
    else:
        groups = []
        for process in processes:
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                continue
            groups.append(process.pid)
        # Give children a short interval to release GPU/IPC resources.
        # Waiting only for the parent is insufficient after a Rust crash.
        if groups:
            deadline = time.monotonic() + 1.0
            while time.monotonic() < deadline:
                if not any(process.poll() is None for process in live):
                    time.sleep(0.2)
                    break
                time.sleep(0.05)
            for group in groups:
                try:
                    os.killpg(group, signal.SIGKILL)
                except ProcessLookupError:
                    pass
    for process in live:
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            # Cancellation remains a failure; never publish a receipt.
            pass


def verified_gpu_forwards(log_path):
    """Inspect genuine resident-model forward counters from the child log.

    The Python model worker emits its counters after every 2,048 batches
    and again when a complete native rollout shuts down. A GPU request
    without completed GPU actions must never be reported as GPU training.
    """
    evidence = None
    with log_path.open("r", encoding="utf-8", errors="replace") as stream:
        for line in stream:
            if ('"native_policy_inference_efficiency"' not in line and
                    '"native_rollout_worker_restart"' not in line):
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                continue
            if row.get("kind") == "native_rollout_worker_restart":
                evidence = None
                continue
            if row.get("kind") != "native_policy_inference_efficiency":
                continue
            calls = row.get("gpu_forward_calls")
            processed = row.get("gpu_forward_rows")
            total = row.get("agent_rows")
            if (type(calls) is not int or type(processed) is not int or
                    type(total) is not int or not 0 <= calls <= processed <= total):
                continue
            if calls > 0 and processed > 0 and (
                    evidence is None or processed >= evidence["agent_rows"]
            ):
                pci = row.get("gpu_pci_address")
                if pci is not None and (
                        not isinstance(pci, str) or
                        not re.fullmatch(r"[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]", pci)):
                    continue
                evidence = {
                    "model_forwards": calls,
                    "agent_rows": processed,
                    "final_counters": row.get("final") is True,
                    **({"gpu_pci_address": pci} if pci else {}),
                }
    if evidence is None:
        raise ValueError(
            f"native GPU allocation produced no verified GPU model forwards: {log_path}"
        )
    return evidence


def run_parallel_rollouts(shards, command, manifest_sha, diagnostics,
                          gpu_ids=None, gpu_mask=None, leased_gpu_inference=False,
                          leased_gpu_count=1, resume=False):
    """Supervise one immutable rollout subprocess per process allocation.

    Any failed exit OR invalid completed summary makes the collection
    unsalvageable; stop all remaining workers rather than burning their
    CPU/GPU budget and accepting an incomplete aggregate.
    """
    started = time.perf_counter()
    processes = [None] * len(shards)
    metrics = {}
    gpu_proofs = {}
    gpu_requested = gpu_ids is not None or leased_gpu_inference
    settled = set()
    resources = RolloutProcessSampler(simulator_via_cargo=command[0] == "cargo")
    with ExitStack() as files:
        try:
            for index, (shard, config_path) in enumerate(shards):
                previous_summary = Path(shard["rollout_summary"])
                if resume and previous_summary.exists():
                    metrics[index] = rollout_metrics(previous_summary, shard, manifest_sha)
                    if gpu_requested:
                        gpu_proofs[index] = verified_gpu_forwards(
                            config_path.with_name("rollout.log")
                        )
                    settled.add(index)
                    continue
                log_path = config_path.with_name("rollout.log")
                log = (files.enter_context(
                    log_path.open("a" if resume else "x", encoding="utf-8")
                ) if diagnostics or gpu_requested else None)
                if resume and log is not None:
                    log.write('{"kind":"native_rollout_worker_restart"}\n')
                    log.flush()
                processes[index] = subprocess.Popen(
                    [*command, str(config_path)],
                    stdout=log, stderr=subprocess.STDOUT if log is not None else None,
                    env=rollout_worker_environment(
                        gpu_ids, gpu_mask, index, leased_gpu_inference,
                        leased_gpu_count,
                    ),
                    start_new_session=os.name != "nt",
                    creationflags=(subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0),
                )
            while len(metrics) != len(shards):
                resources.sample(processes, settled)
                for index, (shard, _) in enumerate(shards):
                    if index in settled:
                        continue
                    returncode = processes[index].poll()
                    if returncode is None:
                        continue
                    if returncode != 0:
                        raise subprocess.CalledProcessError(
                            returncode, processes[index].args,
                        )
                    metrics[index] = rollout_metrics(
                        Path(shard["rollout_summary"]), shard, manifest_sha,
                    )
                    if gpu_requested:
                        gpu_proofs[index] = verified_gpu_forwards(
                            shards[index][1].with_name("rollout.log")
                        )
                    # Clean each completed group immediately. A fast worker
                    # can finish long before slower environments; waiting
                    # until the last sibling completes could allow its PID
                    # to be recycled and endanger an unrelated process group.
                    if os.name != "nt":
                        stop_parallel_rollout_workers([processes[index]])
                    settled.add(index)
                if len(metrics) != len(shards):
                    time.sleep(0.1)
            if len(shards) == 1:
                # Preserve the historical one-process metric receipt layout.
                result = metrics[0]
            else:
                # A retried collector has work executed by earlier process
                # incarnations. Using only this invocation's wall clock would
                # inflate aggregate SPS, especially if all shards finished
                # before artifact delivery failed. Sum measured shard wall
                # times as a conservative bound when overlap is unknown.
                resumed_wall = (sum(item["wall_seconds"] for item in metrics.values())
                                if resume else time.perf_counter() - started)
                result = combined_rollout_metrics(
                    [metrics[index] for index in range(len(shards))],
                    resumed_wall,
                )
                if resume:
                    result["wall_time_basis"] = "summed_shard_incarnations_conservative"
            observations = resources.summary(resume)
            if observations is not None:
                result["resource_observations"] = observations
            if gpu_requested:
                # Every allocated process must have actually forwarded at
                # least one action batch on a GPU, not merely loaded weights.
                addresses = {proof["gpu_pci_address"] for proof in gpu_proofs.values()
                             if "gpu_pci_address" in proof}
                if leased_gpu_count == 2 and leased_gpu_inference and (
                        len(addresses) != 2 or any(
                            "gpu_pci_address" not in proof for proof in gpu_proofs.values()
                        )):
                    raise ValueError(
                        "dual-GPU lease did not execute real model forwards on two distinct HIP PCI devices"
                    )
                # Per-device useful work is counted from each completed
                # immutable simulation shard, not inferred from GPU %
                # utilization. Multiple actors sharing a device aggregate
                # only their authentic world/policy samples.
                by_device = {}
                for index, proof in gpu_proofs.items():
                    pci = proof.get("gpu_pci_address")
                    if pci is None:
                        continue  # CUDA/Windows has no HIP PCI attestation.
                    work = by_device.setdefault(pci, {
                        "gpu_pci_address": pci, "rollout_processes": 0,
                        "model_forwards": 0, "gpu_agent_rows": 0,
                        "successful_world_steps": 0, "successful_policy_rows": 0,
                    })
                    work["rollout_processes"] += 1
                    work["model_forwards"] += proof["model_forwards"]
                    work["gpu_agent_rows"] += proof["agent_rows"]
                    work["successful_world_steps"] += metrics[index]["successful_world_steps"]
                    work["successful_policy_rows"] += metrics[index]["successful_policy_rows"]
                result["gpu_inference"] = {
                    "verified_workers": len(gpu_proofs),
                    "workers_with_final_counters": sum(
                        int(p["final_counters"]) for p in gpu_proofs.values()),
                    # A restored host may have performed earlier model work
                    # in a terminated process. These counters cover only
                    # the final invocation of each worker, not prior work.
                    "resumed_collection": resume,
                    "model_forwards": sum(p["model_forwards"] for p in gpu_proofs.values()),
                    "agent_rows": sum(p["agent_rows"] for p in gpu_proofs.values()),
                    **({"gpu_pci_addresses": sorted(addresses)} if addresses else {}),
                    **({"per_device": [by_device[pci] for pci in sorted(by_device)]}
                       if by_device else {}),
                }
            return result
        except BaseException:
            # Only recently observed or still-running groups belong here.
            # Never revisit an early-completed process group: its PID may
            # eventually be reused while a much slower worker is running.
            stop_parallel_rollout_workers([
                process for index, process in enumerate(processes)
                if index not in settled and process is not None
            ])
            raise


def publish_receipt(output, filename, receipt, *, announce=True):
    # A prior collector may have died after fsyncing the staging receipt
    # but before its atomic rename. Each publication uses a new private
    # temporary name: an orphaned staging file must never block recovery or
    # be mistaken for a verified completed training artifact.
    pending = None
    try:
        with NamedTemporaryFile(mode="w", encoding="utf-8",
                prefix="."+filename+".", suffix=".pending",
                dir=output, delete=False) as stream:
            pending = Path(stream.name)
            json.dump(receipt, stream, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        destination = output / filename
        try:
            # Immutable, create-only publication mirrors the native host's
            # run_final transaction. A retry cannot rewrite an already
            # acknowledged collection or optimizer result.
            os.link(pending, destination)
            sync_directory(output)
        except FileExistsError:
            if (destination.is_symlink() or not destination.is_file() or
                    destination.stat().st_size > 2 * 1024 * 1024 or
                    json.loads(destination.read_text(encoding="utf-8")) != receipt):
                raise ValueError(
                    f"completed native training receipt differs from the prior committed artifact: {destination}"
                )
            # A prior incarnation may have linked the identical receipt but
            # died before synchronizing its parent directory.
            sync_directory(output)
    finally:
        if pending is not None:
            pending.unlink(missing_ok=True)
    if announce:
        print(json.dumps(receipt), flush=True)


def deliver_bundle(output, inbox, receipt, delivery_name=None, *, preserve_delivery=False):
    # A Hub job's artifact root is its immutable job UUID. A separately
    # pinned distributed work order may name the inbox delivery without
    # changing that Hub-owned root or any resource/owner identity.
    inbox = Path(inbox)
    if inbox.is_symlink() or not inbox.is_dir():
        raise ValueError("native rollout inbox must be an owned directory")
    destination = inbox / (delivery_name or output.name)
    if destination.exists() or destination.is_symlink():
        raise FileExistsError(f"rollout inbox already contains {destination.name}")
    # Shared-directory delivery uses the SAME bounded, portable artifact
    # names and SHA-256 contract as authenticated tar upload. Never copy
    # a collector-authored ../ path or follow a captured source symlink.
    artifacts = artifact_paths(receipt)
    source_root = Path(output).resolve(strict=True)
    started = time.perf_counter()
    transferred = 0
    with TemporaryDirectory(prefix=".rollout-", dir=inbox) as temporary:
        stage = Path(temporary) / "bundle"
        stage.mkdir()
        for name, expected_digest in sorted(artifacts.items()):
            relative = Path(*PurePosixPath(name).parts)
            source_path = source_root / relative
            if (source_path.is_symlink() or not source_path.is_file() or
                    not source_path.resolve(strict=True).is_relative_to(source_root)):
                raise ValueError(f"rollout capture escaped its bundle: {name}")
            target = stage / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            digest = hashlib.sha256()
            with source_path.open("rb") as source, target.open("xb") as sink:
                for chunk in iter(lambda: source.read(1024 * 1024), b""):
                    digest.update(chunk)
                    sink.write(chunk)
                    transferred += len(chunk)
                sink.flush()
                os.fsync(sink.fileno())
            if digest.hexdigest() != expected_digest:
                raise ValueError(f"rollout artifact changed before inbox publication: {name}")
            if target.parent != stage:
                sync_directory(target.parent)
        if preserve_delivery:
            # A replacement Hub job must republish EXACTLY the bytes of
            # the original accepted rollout receipt. Changing copy timing
            # changes the archive digest, which the learner will correctly
            # reject after a lost upload acknowledgement.
            previous = receipt.get("delivery")
            if (not isinstance(previous, dict) or
                    previous.get("bytes") != transferred or
                    not isinstance(previous.get("copy_wall_seconds"), (int, float)) or
                    not math.isfinite(previous["copy_wall_seconds"]) or
                    previous["copy_wall_seconds"] < 0):
                raise ValueError("completed native collection lacks original delivery identity")
        else:
            receipt["delivery"] = {
                "bytes": transferred,
                "copy_wall_seconds": time.perf_counter() - started,
            }
        publish_receipt(stage, "rollout-bundle.json", receipt, announce=False)
        stage.rename(destination)
        sync_directory(inbox)
    return destination


def optimizer_attempt_identity(args, trainer_config, result):
    """Immutable input, GPU placement and rank identity for a completed
    optimizer whose final Hub receipt was interrupted. No trainer state is
    inferred from an uncommitted partial checkpoint.
    """
    return {
        "schema_version": 1,
        "kind": "native_optimizer_attempt",
        "run_id": result["run_id"],
        "run_config_sha256": result["run_config_sha256"],
        "source_run_config_sha256": result["source_run_config_sha256"],
        "input_checkpoint_sha256": result["input_checkpoint_sha256"],
        "input_manifest_sha256": result["input_manifest_sha256"],
        "trainer_sha256": file_sha256(trainer_config),
        "rollout_receipts": [
            [item["sha256"], Path(item["path"]).parent.name]
            for item in result["rollout_bundles"]
        ],
        "collection_orders_sha256": result.get("collection_orders_sha256"),
        "device": args.device,
        "learner_gpus": args.learner_gpus,
        "learner_nodes": args.learner_nodes,
        "learner_node_rank": args.learner_node_rank,
        "learner_backend": args.learner_backend,
        "learner_group_id": args.learner_group_id,
        "learner_slot": args.learner_slot,
        "refresh_opponent_slot": args.refresh_opponent_slot,
        "torch_threads": args.torch_threads,
    }


def prepare_optimizer_attempt(args, output, trainer_config, result):
    receipt = output / "optimizer-attempt.json"
    if args.resume_optimizer:
        if receipt.is_symlink() or not receipt.is_file():
            raise ValueError("no immutable optimizer attempt to finalize")
        saved = json.loads(receipt.read_text(encoding="utf-8"))
        if not isinstance(saved, dict) or not isinstance(saved.get("run_id"), str):
            raise ValueError("invalid saved native optimizer attempt")
        result["run_id"] = saved["run_id"]
        if saved != optimizer_attempt_identity(args, trainer_config, result):
            raise ValueError("native optimizer inputs, rank or GPU allocation changed")
    else:
        publish_receipt(
            output, receipt.name,
            optimizer_attempt_identity(args, trainer_config, result),
            announce=False,
        )
    # The attempt is the immutable optimizer owner, not an advisory launch
    # hint. Bind the final rank/round receipt to the exact bytes which
    # authorized this checkpoint generation, including on a recovered run.
    result["optimizer_attempt_sha256"] = file_sha256(receipt)


def require_optimizer_attempt(output, result, trainer_config):
    expected = result.get("optimizer_attempt_sha256")
    if expected is None:
        # Ordinary one-machine rollouts have no separate optimizer attempt:
        # their own run receipt already owns the model generation.
        return
    receipt = output / "optimizer-attempt.json"
    if (receipt.is_symlink() or not receipt.is_file() or
            file_sha256(receipt) != expected):
        raise ValueError("optimizer attempt identity changed before final checkpoint publication")
    attempt = json.loads(receipt.read_text(encoding="utf-8"))
    if (attempt.get("kind") != "native_optimizer_attempt" or
            attempt.get("run_id") != result.get("run_id") or
            attempt.get("input_manifest_sha256") != result.get("input_manifest_sha256") or
            attempt.get("input_checkpoint_sha256") != result.get("input_checkpoint_sha256") or
            attempt.get("learner_group_id") != result.get("optimizer_group_id") or
            attempt.get("collection_orders_sha256") != result.get("collection_orders_sha256") or
            attempt.get("trainer_sha256") != file_sha256(trainer_config) or
            attempt.get("run_config_sha256") != file_sha256(Path(result["run_config"])) or
            attempt.get("rollout_receipts") != [
                [item["sha256"], Path(item["path"]).parent.name]
                for item in result.get("rollout_bundles", [])
            ]):
        raise ValueError("optimizer completion no longer belongs to the original attempt")
    # An external collector or Hub work order may still be present while
    # gradients run. Preserve the exact verified bytes through publication
    # on every node, not only when rank zero finally writes the model.
    if "collection_orders" in result and (
            file_sha256(Path(result["collection_orders"])) !=
            attempt["collection_orders_sha256"]
    ):
        raise ValueError("original optimizer collection order changed during learning")
    for bundle in result.get("rollout_bundles", []):
        if file_sha256(Path(bundle["path"])) != bundle["sha256"]:
            raise ValueError("original optimizer rollout receipts changed during learning")


def optimize(args, output, python, manifest, checkpoint, trainer_config, result, rollout_args):
    # Pin every model consumed by the native learner, including opponent slots.
    # Imported rollout bundles bypass local collection, so this boundary must
    # independently check their model inputs as well.
    model_dependencies = native_manifest_model_dependencies(
        manifest, os.environ.get("BEES_NATIVE_MODEL_STORE"),
    )
    learner_dir = output / "learner"
    primary = [result["capture"], result["journal"]] if "capture" in result else []
    train_command = [
        python, str(Path(__file__).with_name("train_policy_capture.py")),
        *primary, str(manifest), str(checkpoint), str(trainer_config), str(learner_dir),
        "--device", args.device, "--learner-gpus", str(args.learner_gpus),
        "--learner-nodes", str(args.learner_nodes),
        "--learner-node-rank", str(args.learner_node_rank),
        "--learner-backend", args.learner_backend,
        *([] if args.learner_group_id is None else [
            "--learner-group-id", args.learner_group_id,
        ]),
        *([] if args.torch_threads is None else [
            "--torch-threads", str(max(1, args.torch_threads // args.learner_gpus))
        ]),
        *rollout_args,
    ]
    if args.learner_slot is not None:
        train_command += ["--learner-slot", str(args.learner_slot)]
    if args.refresh_opponent_slot is not None:
        train_command += ["--refresh-opponent-slot", str(args.refresh_opponent_slot)]
    if args.learner_gpus * args.learner_nodes > 1:
        # Static node ranks preserve checkpoint ownership across restarts.
        # The caller owns network reachability and supervises one torchrun
        # process per machine; no rank launches another machine's process.
        launcher = (["--standalone", "--nnodes=1"]
                    if args.learner_nodes == 1 else [
                        "--nnodes", str(args.learner_nodes),
                        "--node-rank", str(args.learner_node_rank),
                        "--master-addr", args.learner_master_addr,
                        "--master-port", str(args.learner_master_port),
                    ])
        train_command = [
            python, "-m", "torch.distributed.run",
            *launcher, "--nproc-per-node", str(args.learner_gpus),
            *train_command[1:],
        ]
    if args.resume_optimizer:
        # Reattach only to a fully committed synchronized output. A rank
        # that crashed mid-gradient update must not continue Adam from a
        # partially saved tensor set; a new authorized group attempt is
        # required instead.
        eligible = ([learner_dir / "node-result.json"] if args.learner_node_rank
                    else [learner_dir / "checkpoint.pt",
                          learner_dir / "manifest.json",
                          learner_dir / "metrics.json"])
        if any(item.is_symlink() or not item.is_file() for item in eligible):
            raise ValueError(
                "native optimizer did not commit its complete rank output; "
                "restart the synchronized group as a new attempt"
            )
        # The normal capture learner verifies files when loading and again
        # before committing its update. Recovery skips that subprocess, so
        # recheck the original completed collector inputs before finalizing.
        for bundle in result.get("rollout_bundles", []):
            verify_bundle_files(Path(bundle["path"]))
    else:
        run_stage(train_command, output / "optimizer.log" if args.stage_diagnostics else None)
    # A successful optimizer subprocess is not enough if source weights or
    # their resolved paths changed while gradients were being computed.
    if native_manifest_model_dependencies(
        manifest, os.environ.get("BEES_NATIVE_MODEL_STORE"),
    ) != model_dependencies:
        raise ValueError("native optimizer model paths changed during learning")
    if args.learner_node_rank != 0:
        worker_file = learner_dir / "node-result.json"
        worker = json.loads(worker_file.read_text(encoding="utf-8"))
        rows = worker.get("gpu_per_device")
        if (worker.get("schema_version") != 1 or
                worker.get("kind") != "native_distributed_optimizer_node_complete" or
                worker.get("node_rank") != args.learner_node_rank or
                worker.get("node_count") != args.learner_nodes or
                worker.get("optimizer_group_id") != args.learner_group_id or
                worker.get("local_gpu_workers") != args.learner_gpus or
                worker.get("global_gpu_workers") != args.learner_nodes * args.learner_gpus or
                worker.get("input_checkpoint_sha256") != result["input_checkpoint_sha256"] or
                worker.get("input_manifest_sha256") != result["input_manifest_sha256"] or
                not isinstance(worker.get("global_input_sha256"), str) or
                re.fullmatch(r"[a-f0-9]{64}", worker["global_input_sha256"]) is None or
                not isinstance(worker.get("model_consensus_sha256"), str) or
                re.fullmatch(r"[a-f0-9]{64}", worker["model_consensus_sha256"]) is None or
                not isinstance(rows, list) or len(rows) != args.learner_gpus or
                any(row.get("rank") != args.learner_node_rank * args.learner_gpus + index or
                    row.get("local_rank") != index or
                    row.get("node_rank") != args.learner_node_rank
                    for index, row in enumerate(rows)) or
                file_sha256(checkpoint) != result["input_checkpoint_sha256"] or
                file_sha256(manifest) != result["input_manifest_sha256"]):
            raise ValueError("native optimizer worker has no agreed multi-machine model and input identity")
        require_optimizer_attempt(output, result, trainer_config)
        publish_receipt(output, "round-worker-result.json", {
            **result,
            **worker,
            # The worker's own finalizer is a separate immutable commit
            # after its GPU ranks reach consensus. Bind the Hub-facing
            # completion to those exact bytes, not only copied JSON fields.
            "optimizer_node_receipt_sha256": file_sha256(worker_file),
            "schema_version": 1,
            "kind": "native_distributed_optimizer_worker_complete",
        })
        return
    next_checkpoint = learner_dir / "checkpoint.pt"
    next_manifest = learner_dir / "manifest.json"
    metrics = json.loads((learner_dir / "metrics.json").read_text(encoding="utf-8"))
    input_checkpoint_sha = result["input_checkpoint_sha256"]
    input_manifest_sha = result["input_manifest_sha256"]
    if (file_sha256(checkpoint) != input_checkpoint_sha or
            file_sha256(manifest) != input_manifest_sha):
        raise ValueError("round input checkpoint or manifest changed after training began")
    for bundle in result.get("rollout_bundles", []):
        if file_sha256(Path(bundle["path"])) != bundle["sha256"]:
            raise ValueError("rollout bundle receipt changed during training")
    checkpoint_sha = file_sha256(next_checkpoint)
    manifest_sha = file_sha256(next_manifest)
    if (not isinstance(metrics, dict) or metrics.get("schema_version") != 1 or
            metrics.get("source") != "bees-native-ma-poca-v1" or
            metrics.get("input_checkpoint_sha256") != input_checkpoint_sha or
            metrics.get("input_manifest_sha256") != input_manifest_sha or
            metrics.get("next_checkpoint_sha256") != checkpoint_sha or
            metrics.get("next_manifest_sha256") != manifest_sha or
            metrics.get("learner_slot") != args.learner_slot or
            metrics.get("refreshed_opponent_slot") != args.refresh_opponent_slot or
            type(metrics.get("optimizer_input_steps")) is not int or
            metrics["optimizer_input_steps"] < 1 or
            type(metrics.get("episodes")) is not int or metrics["episodes"] < 1 or
            metrics.get("optimizer_gpu_workers") != (
                args.learner_gpus * args.learner_nodes if args.device.startswith("cuda") else 0
            ) or
            metrics.get("optimizer_gpu_nodes") != (
                args.learner_nodes if args.device.startswith("cuda") else 0
            ) or
            metrics.get("optimizer_gpu_local_workers") != (
                args.learner_gpus if args.device.startswith("cuda") else 0
            ) or
            (args.learner_gpus * args.learner_nodes > 1 and (
                type(metrics.get("optimizer_synchronized_minibatches")) is not int or
                metrics["optimizer_synchronized_minibatches"] < 1 or
                metrics.get("optimizer_masked_loss_normalizations") !=
                    metrics["optimizer_synchronized_minibatches"] or
                not isinstance(metrics.get("optimizer_gpu_model_consensus_sha256"), str) or
                re.fullmatch(r"[a-f0-9]{64}",
                    metrics["optimizer_gpu_model_consensus_sha256"]) is None or
                not isinstance(metrics.get("optimizer_gpu_per_device"), list) or
                len(metrics["optimizer_gpu_per_device"]) != args.learner_gpus * args.learner_nodes
            )) or
            (args.learner_nodes > 1 and (
                metrics.get("optimizer_group_id") != args.learner_group_id or
                not isinstance(metrics.get("optimizer_global_input_sha256"), str) or
                re.fullmatch(r"[a-f0-9]{64}",
                    metrics["optimizer_global_input_sha256"]) is None or
                any(row.get("rank") != rank or
                    row.get("node_rank") != rank // args.learner_gpus or
                    row.get("local_rank") != rank % args.learner_gpus
                    for rank, row in enumerate(metrics["optimizer_gpu_per_device"]))
            ))):
        raise ValueError("native learner completion receipt disagrees with its model artifacts or actual GPU ranks")
    require_optimizer_attempt(output, result, trainer_config)
    completed = {
        "schema_version": 1,
        "kind": "native_training_round_complete",
        **result,
        # Metrics are the learner's final durable group-wide commit. A
        # subsequent Hub continuation must be able to verify the source
        # bytes independently of the surrounding round receipt.
        "optimizer_metrics_sha256": file_sha256(learner_dir / "metrics.json"),
        "learner_metrics": metrics,
        "next_checkpoint": str(next_checkpoint),
        "next_checkpoint_sha256": checkpoint_sha,
        "next_manifest": str(next_manifest),
        "next_manifest_sha256": manifest_sha,
    }
    publish_receipt(output, "round-result.json", completed)
    release_completed_round(args, output, trainer_config, completed)


def release_completed_round(args, output, trainer_config, completed):
    if args.continual_plan is None:
        return
    from native_continual_session import complete_generations
    complete_generations(output, {
        "continual_release": args.continual_plan,
        "learner_slot": args.learner_slot,
        "trainer_config": {"path": str(trainer_config)},
    }, [(output, completed)], final=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run_config", type=Path)
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("trainer_config", type=Path)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("--continual-release-plan", type=Path,
                        help="release the committed learner checkpoint within this job allocation")
    parser.add_argument("--resume-continual-release", action="store_true",
                        help="resume only the authorized release of an already committed round")
    parser.add_argument("--model-manifest", type=Path,
                        help="override the run configuration's manifest for the next round")
    parser.add_argument("--learner-slot", type=int)
    parser.add_argument("--refresh-opponent-slot", type=int,
                        help="also publish this opponent slot using the successful learner checkpoint")
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--learner-gpus", type=int, choices=(1, 2), default=1,
                        help="synchronous MA-POCA optimizer GPU workers on this machine")
    parser.add_argument("--learner-nodes", type=int, default=1,
                        help="number of machines in the synchronous native learner")
    parser.add_argument("--learner-node-rank", type=int, default=0,
                        help="this machine's fixed zero-based rank in the learner")
    parser.add_argument("--learner-master-addr",
                        help="trusted reachable torchrun rendezvous host for multiple machines")
    parser.add_argument("--learner-master-port", type=int,
                        help="trusted torchrun rendezvous port for multiple machines")
    parser.add_argument("--learner-backend", choices=("auto", "nccl", "gloo"),
                        default="auto", help="shared GPU gradient collective backend")
    parser.add_argument("--learner-group-id",
                        help="immutable Hub resource-bundle identity for the optimizer group")
    parser.add_argument("--torch-threads", type=int,
                        help="fixed optimizer intra-op threads, not rollout parallelism")
    parser.add_argument("--rollout-workers", type=int,
                        help="fixed number of parallel native rollout processes")
    parser.add_argument("--physics-workers", type=int,
                        help="CPU physics threads per rollout process (1..128); shared model inference remains batched")
    parser.add_argument("--rollout-gpus",
                        help="comma-separated physical GPUs shared round-robin among rollout processes")
    parser.add_argument("--rollout-gpu-mask", choices=(
        "CUDA_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES", "HIP_VISIBLE_DEVICES"),
        help="GPU isolation selector; default CUDA_VISIBLE_DEVICES (ROCR for ROCm Linux)")
    parser.add_argument("--leased-gpu-inference", action="store_true",
                        help="use an externally supervised exclusive GPU for native inference")
    parser.add_argument("--leased-gpu-count", type=int, default=1,
                        help="number of externally verified exclusive ROCm GPUs (1 or 2)")
    parser.add_argument("--rollout-worlds", type=int,
                        help="independent native Worlds in this collector's reproducible window")
    parser.add_argument("--rollout-world-start", type=int,
                        help="zero-based first global World index for a distributed collection")
    parser.add_argument(
        "--seed-offset", type=int, default=0,
        help="unsigned offset to all authored episode seeds for a new reproducible round",
    )
    parser.add_argument("--rollout-binary", type=Path,
                        help="use a compiled policy_rollout binary instead of cargo run")
    parser.add_argument("--stage-diagnostics", action="store_true",
                        help="capture stage output in separate files for supervised execution")
    parser.add_argument("--collect-only", action="store_true",
                        help="publish a portable rollout bundle without running the optimizer")
    parser.add_argument("--resume-rollout", action="store_true",
                        help="continue the exact previously allocated distributed collector shard")
    parser.add_argument("--resume-optimizer", action="store_true",
                        help="finalize an already committed distributed optimizer rank without rerunning gradients")
    parser.add_argument("--bundle-inbox", type=Path,
                        help="also deliver the collected bundle to an existing shared inbox")
    parser.add_argument("--collection-id",
                        help="pin this collection to one distributed round work order")
    parser.add_argument("--bundle-delivery-name",
                        help="work-order inbox directory; local Hub job artifacts retain their original UUID")
    parser.add_argument("--pinned-rollout-bundle", nargs=2, action="append", default=[],
                        metavar=("RECEIPT", "SHA256"), help="supply an approved receipt to an empty bundle template")
    parser.add_argument("--collection-orders", type=Path,
                        help="verify every imported bundle against one distributed session order")
    args = parser.parse_args()
    if args.resume_rollout and (not args.collect_only or args.collection_id is None):
        parser.error("--resume-rollout requires one pinned distributed collection")
    if args.resume_optimizer and (
            args.collect_only or args.resume_rollout or
            args.learner_nodes * args.learner_gpus < 2):
        parser.error("--resume-optimizer requires a distributed optimizer with completed rank outputs")
    if args.collection_id is not None and (
        len(args.collection_id) != 64 or any(
            char not in "0123456789abcdef" for char in args.collection_id
        )
    ):
        parser.error("--collection-id must be a lowercase SHA-256 hex work-order identity")
    if args.collection_id is not None and not args.collect_only:
        parser.error("--collection-id requires --collect-only")
    if args.bundle_delivery_name is not None and (
        args.collection_id is None or args.bundle_inbox is None or
        re.fullmatch(r"[A-Za-z0-9_-]{1,96}", args.bundle_delivery_name) is None
    ):
        parser.error("--bundle-delivery-name requires a pinned collection, inbox and safe name")
    if args.torch_threads is not None and not 1 <= args.torch_threads <= 256:
        parser.error("--torch-threads must be between 1 and 256")
    if not 1 <= args.learner_nodes <= 8 or not 0 <= args.learner_node_rank < args.learner_nodes:
        parser.error("native optimizer requires 1–8 machines and a valid fixed node rank")
    if args.learner_gpus * args.learner_nodes > 1 and (
            args.collect_only or args.device not in ("cuda", "cuda:0")):
        parser.error("distributed MA-POCA requires optimizer execution on CUDA/ROCm")
    if args.learner_nodes > 1:
        if (not isinstance(args.learner_group_id, str) or
                re.fullmatch(r"[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}",
                             args.learner_group_id) is None):
            parser.error("multi-machine optimizer requires the immutable Hub bundle identity")
        if (not args.learner_master_addr or
                re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9.-]{0,252}",
                             args.learner_master_addr) is None or
                args.learner_master_port is None or
                not 1 <= args.learner_master_port <= 65535 or
                args.learner_backend == "auto"):
            parser.error("multi-machine optimizer requires a trusted rendezvous and an explicit common backend")
    elif (args.learner_master_addr is not None or args.learner_master_port is not None
          or args.learner_group_id is not None):
        parser.error("single-machine optimizer uses torchrun standalone rendezvous")
    if args.torch_threads is not None and args.torch_threads < args.learner_gpus:
        parser.error("optimizer CPU threads must accommodate every GPU learner")
    if args.rollout_workers is not None and not 1 <= args.rollout_workers <= 256:
        parser.error("--rollout-workers must be between 1 and 256")
    if args.physics_workers is not None and not 1 <= args.physics_workers <= 128:
        parser.error("--physics-workers must be between 1 and 128")
    try:
        rollout_gpus = parse_rollout_gpus(args.rollout_gpus)
    except ValueError as error:
        parser.error(str(error))
    if args.leased_gpu_inference and (
            rollout_gpus is not None or args.rollout_gpu_mask is not None):
        parser.error("supervised GPU leases and standalone GPU allocations are exclusive")
    if args.leased_gpu_count not in (1, 2) or (
            args.leased_gpu_count != 1 and not args.leased_gpu_inference):
        parser.error("two leased GPUs require explicitly authorized GPU inference")
    if not 0 <= args.seed_offset < (1 << 64):
        parser.error("--seed-offset must fit uint64")
    inbox = args.bundle_inbox.resolve(strict=True) if args.bundle_inbox else None
    if inbox is not None and (not args.collect_only or not inbox.is_dir()):
        parser.error("--bundle-inbox requires --collect-only and an existing directory")

    run_path = args.run_config.resolve(strict=True)
    checkpoint = args.checkpoint.resolve(strict=True)
    trainer_config = args.trainer_config.resolve(strict=True)
    run_bytes = run_path.read_bytes()
    run_document = json.loads(run_bytes)
    if not isinstance(run_document, dict):
        parser.error("run configuration must be an object")
    if args.learner_nodes > 1 and "rollout_bundles" not in run_document:
        parser.error("multi-machine optimizer requires replicated, digest-pinned native rollout bundles")
    if args.pinned_rollout_bundle and run_document.get("rollout_bundles") != []:
        parser.error("--pinned-rollout-bundle requires an empty rollout_bundles template")
    if args.collection_orders is not None and run_document.get("rollout_bundles") != []:
        parser.error("--collection-orders requires the empty imported-bundle template")
    manifest_value = args.model_manifest or run_document.get("model_manifest")
    if not manifest_value:
        parser.error("run configuration needs model_manifest or --model-manifest")
    manifest = Path(manifest_value)
    if not manifest.is_absolute():
        manifest = run_path.parent / manifest
    manifest = manifest.resolve(strict=True)
    input_checkpoint_sha = check_learner(manifest, checkpoint, args.learner_slot)
    if args.refresh_opponent_slot is not None:
        if (args.learner_slot is None or
                args.refresh_opponent_slot == args.learner_slot or
                not 0 <= args.refresh_opponent_slot < (1 << 32)):
            parser.error("opponent refresh requires distinct explicit learner and uint32 opponent slots")
        prior_models = json.loads(manifest.read_text(encoding="utf-8"))["models"]
        if not any(
            entry.get("slot") == args.refresh_opponent_slot and
            entry.get("backend") in ("mlagents", "onnx")
            for entry in prior_models
        ):
            parser.error("opponent refresh requires an existing native opponent slot")
    input_manifest_sha = file_sha256(manifest)
    output = args.output_dir.resolve()
    result = {
        "seed_offset": args.seed_offset,
        **({"optimizer_group_id": args.learner_group_id}
           if args.learner_group_id is not None else {}),
        "source_run_config_sha256": hashlib.sha256(run_bytes).hexdigest(),
        "input_checkpoint_sha256": input_checkpoint_sha,
        "input_manifest_sha256": input_manifest_sha,
    }
    args.continual_plan = None
    if args.resume_continual_release and args.continual_release_plan is None:
        parser.error("release-only recovery requires the original continual release plan")
    if args.continual_release_plan is not None:
        if args.collect_only or args.learner_node_rank != 0 or args.learner_slot is None:
            parser.error("only the checkpoint-owning learner may release a native generation")
        if args.resume_continual_release and args.resume_optimizer:
            parser.error("release-only and optimizer recovery are distinct boundaries")
        from native_continual_session import load_plan
        args.continual_plan = load_plan(args.continual_release_plan, checkpoint, None,
                                       standalone_round=True)
        if (args.continual_plan["evaluation_device"] == "cuda:0" and
                not args.device.startswith("cuda")):
            parser.error("GPU release evaluation requires a GPU learner allocation")
        result["continual_release_plan"] = args.continual_plan
        result["continual_trainer_sha256"] = file_sha256(trainer_config)
        if args.continual_plan["historical_training_ratio"] > 0:
            if args.refresh_opponent_slot is None:
                parser.error("Hub continual historical league requires the existing self-play opponent refresh slot")
            result["hub_historical_selection"] = True
        if args.resume_continual_release:
            from train_policy_session import verify_round
            record, _, _ = verify_round(output, input_manifest_sha, input_checkpoint_sha,
                                        args.learner_slot, args.refresh_opponent_slot)
            if (record.get("seed_offset") != args.seed_offset or
                    record.get("optimizer_group_id") != args.learner_group_id or
                    record.get("continual_release_plan") != args.continual_plan or
                    record.get("continual_trainer_sha256") != result["continual_trainer_sha256"]):
                raise ValueError("release recovery differs from the original authorized plan or trainer")
            if "rollout_bundles" in run_document:
                bundles = run_document["rollout_bundles"] or [
                    {"path": path, "sha256": digest}
                    for path, digest in args.pinned_rollout_bundle
                ]
                pinned = [{"path": str(resolved(run_path.parent, bundle["path"])),
                           "sha256": bundle["sha256"]} for bundle in bundles]
                if (record["run_config_sha256"] != hashlib.sha256(run_bytes).hexdigest() or
                        record.get("rollout_bundles") != pinned):
                    raise ValueError("release recovery belongs to a different bundle round")
            elif record["source_run_config_sha256"] != result["source_run_config_sha256"]:
                raise ValueError("release recovery belongs to a different source round")
            require_optimizer_attempt(output, record, trainer_config)
            release_completed_round(args, output, trainer_config, record)
            return
    if "rollout_bundles" in run_document:
        if (args.rollout_workers is not None or args.physics_workers is not None or
                rollout_gpus is not None or args.rollout_gpu_mask is not None or
                args.leased_gpu_inference or args.rollout_worlds is not None or
                args.rollout_world_start is not None or
                "rollout_world_start" in run_document or "rollout_worlds" in run_document):
            parser.error("imported rollout bundles cannot allocate local Worlds or workers")
        if (args.collect_only or args.seed_offset or run_document.get("levels") or
                any(run_document.get(key) for key in (
                    "hivemind", "model_updates", "recover_notifications", "recover_outcome_journals",
                ))):
            parser.error("bundle rounds use completed captures without live Levels or seed shifts")
        bundles = run_document["rollout_bundles"] or [
            {"path": path, "sha256": digest} for path, digest in args.pinned_rollout_bundle
        ]
        if not isinstance(bundles, list) or not bundles:
            parser.error("rollout_bundles must list at least one pinned receipt")
        run_id = run_document.get("run_id")
        python = run_document.get("python")
        if not isinstance(run_id, str) or not run_id.strip() or not isinstance(python, str) or not python:
            parser.error("bundle rounds require a run ID and Python interpreter")
        pinned = []
        rollout_args = []
        for bundle in bundles:
            path = resolved(run_path.parent, bundle["path"])
            digest = file_sha256(Path(path))
            if digest != bundle["sha256"]:
                raise ValueError("rollout bundle receipt differs from the approved digest")
            pinned.append({"path": path, "sha256": digest})
            rollout_args.extend(["--pinned-rollout-bundle", path, digest])
        if args.collection_orders is not None:
            order_path = args.collection_orders.resolve(strict=True)
            orders = json.loads(order_path.read_text(encoding="utf-8"))
            if (orders.get("kind") != "native_distributed_collection_orders" or
                    orders.get("schema_version") != 1 or
                    orders.get("input_checkpoint_sha256") != input_checkpoint_sha or
                    orders.get("input_manifest_sha256") != input_manifest_sha or
                    orders.get("learner_slot") != args.learner_slot or
                    len(orders.get("jobs", [])) != len(pinned) or
                    any(Path(bundle["path"]).parent.name != job["bundle_directory"]
                        for bundle, job in zip(pinned, orders["jobs"]))):
                raise ValueError("pinned distributed collection does not match the training round")
            collections = [
                verify_bundle(Path(bundle["path"]), job, orders)
                for bundle, job in zip(pinned, orders["jobs"])
            ]
            if any(bundle["sha256"] != source["sha256"]
                   for bundle, source in zip(pinned, collections)):
                raise ValueError("imported native rollout receipt differs from its pinned SHA-256")
            result["collection_orders"] = str(order_path)
            # In an imported round, the actual environment is the pinned
            # collector source, not this empty rollout-bundle template.
            result["source_run_config_sha256"] = orders["source_run_config_sha256"]
            result["collection_orders_sha256"] = file_sha256(order_path)
            result["rollout_metrics"] = aggregate_collection_metrics(collections)
        output.mkdir(parents=True, exist_ok=args.resume_optimizer)
        result.update(
            run_id=f"{run_id}-{uuid.uuid4().hex}",
            run_config=str(run_path),
            run_config_sha256=hashlib.sha256(run_bytes).hexdigest(),
            rollout_bundles=pinned,
        )
        prepare_optimizer_attempt(args, output, trainer_config, result)
        optimize(args, output, python, manifest, checkpoint, trainer_config, result, rollout_args)
        return
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

    config = source_config(run_path, manifest, output)
    # Check the actual bytes selected by the resident policy worker, not
    # only the manifest or learner checkpoint. Physical collectors can
    # otherwise publish valid-looking experience after a model file
    # changes during the rollout. This cost occurs at launch/finalization,
    # never in the authoritative simulation's fixed-step hot path.
    model_dependencies = native_manifest_model_dependencies(
        manifest, os.environ.get("BEES_NATIVE_MODEL_STORE"),
    )
    # One-shot build evidence for a collector. Native runners naturally
    # differ between Windows and Linux, but every collector in one update
    # must use identical policy-worker implementation bytes.
    runner_sha = (file_sha256(binary)
                  if args.collect_only and args.rollout_binary is not None else None)
    policy_worker_path = Path(config["policy_worker"])
    policy_worker_sha = (normalized_python_source_sha256(policy_worker_path)
                         if args.collect_only else None)
    source_contract_sha = (native_source_contract_sha256(run_path)
                           if args.collect_only else None)
    legacy_source_sha = (native_source_contract_sha256(
        run_path, include_allocation=True,
    ) if args.collect_only else None)
    shifted_seeds(config, args.seed_offset)
    authored_level_count = len(config["levels"])
    template_worlds = config.pop("rollout_worlds", None)
    template_start = config.pop("rollout_world_start", 0)
    requested_worlds = args.rollout_worlds if args.rollout_worlds is not None else template_worlds
    world_start = args.rollout_world_start if args.rollout_world_start is not None else template_start
    if requested_worlds is not None or world_start != 0:
        expand_rollout_worlds(
            config, requested_worlds if requested_worlds is not None else authored_level_count,
            world_start,
        )
    template_workers = config.pop("rollout_workers", 1)
    # A GPU allocation needs at least one independently batched resident
    # model process per assigned GPU. Keep a larger authored worker budget,
    # but never leave explicitly selected GPUs idle by default.
    workers = (args.rollout_workers if args.rollout_workers is not None else
               max(template_workers, len(rollout_gpus or ()),
                   args.leased_gpu_count if args.leased_gpu_inference else 1))
    physics_workers = (args.physics_workers if args.physics_workers is not None
                       else config.get("physics_workers", 1))
    if type(physics_workers) is not int or not 1 <= physics_workers <= 128:
        parser.error("physics_workers must be an integer between 1 and 128")
    # Preserve legacy run JSON for an unchanged single-worker allocation:
    # older compiled native runners reject unknown configuration keys.
    # Explicit operator settings still require the new runner contract.
    if args.physics_workers is not None or "physics_workers" in config:
        config["physics_workers"] = physics_workers
    levels = config["levels"]
    if type(world_start) is not int or world_start < 0:
        parser.error("rollout_world_start must be a nonnegative integer")
    if type(workers) is not int or not 1 <= workers <= min(len(levels), 256):
        parser.error("rollout_workers must be between one and the number of Levels (maximum 256)")
    if args.leased_gpu_inference and workers < args.leased_gpu_count:
        parser.error("each leased GPU needs at least one native rollout process")
    try:
        gpu_mask = validate_gpu_allocation(
            rollout_gpus, args.rollout_gpu_mask, workers, manifest,
        )
        if args.leased_gpu_inference:
            validate_leased_gpu_inference(manifest)
    except ValueError as error:
        parser.error(str(error))
    gpu_allocation = (None if rollout_gpus is None else {
        "visibility_environment": gpu_mask,
        "physical_gpu_ids": list(rollout_gpus),
        "worker_gpu_ids": [rollout_gpus[i % len(rollout_gpus)] for i in range(workers)],
    })
    if workers > 1 or args.collect_only:
        if args.rollout_binary is None or config.get("hivemind") is not None or any(
            config.get(key) for key in ("recover_notifications", "recover_outcome_journals")
        ):
            parser.error("parallel or collection-only rollouts require a compiled binary and model-only Levels")
        for field in ("world_id", "level_id"):
            identities = [level.get(field) for level in levels]
            if (any(type(identity) is not int or identity <= 0 for identity in identities)
                    or len(set(identities)) != len(levels)):
                parser.error(f"parallel rollouts require distinct positive {field} values")
    # Rotate worker assignment separately for each authored Level, but
    # advance one shared worker cursor across sources. This guarantees
    # that both total world counts and each source's replica counts differ
    # by at most one across processes. Preserve the original Level order
    # within every shard for deterministic source-order processing.
    assignments = [None] * len(levels)
    next_worker = 0
    for source in range(authored_level_count):
        first = (source - world_start) % authored_level_count
        for level_index in range(first, len(levels), authored_level_count):
            assignments[level_index] = next_worker % workers
            next_worker += 1
    buckets = [[] for _ in range(workers)]
    for index, level in enumerate(levels):
        buckets[assignments[index]].append(level)

    output.mkdir(parents=True, exist_ok=args.resume_rollout)
    shards = []
    for index, allocated_levels in enumerate(buckets):
        directory = output if workers == 1 else output / f"rollout-{index}"
        shard = {**config, "levels": allocated_levels}
        if workers > 1:
            directory.mkdir(exist_ok=args.resume_rollout)
            shard["connection_checkpoint"] = str(directory / "connection-checkpoint.json")
        if args.collect_only:
            # Every native worker owns one authoritative World/model/event
            # checkpoint, never a separate training-only simulation.
            shard["host_recovery_checkpoint"] = str(directory / "native-host-checkpoint.json")
        shards.append((shard, prepare_rollout(shard, directory, resume=args.resume_rollout)))
    first, config_path = shards[0]

    print(json.dumps({"kind": "native_training_round_started", "run_config": str(config_path),
                      "rollout_worlds": len(levels), "rollout_world_start": world_start,
                      "rollout_processes": workers,
                      "physics_workers_per_process": physics_workers,
                      **({"gpu_allocation": gpu_allocation} if gpu_allocation else {}),
                      **({"leased_gpu_inference": True,
                          "leased_gpu_count": args.leased_gpu_count}
                         if args.leased_gpu_inference else {}),
                      "seed_offset": args.seed_offset,
                      "input_manifest_sha256": input_manifest_sha,
                      "input_checkpoint_sha256": input_checkpoint_sha}), flush=True)
    rollout = run_parallel_rollouts(
        shards, command, input_manifest_sha, args.stage_diagnostics,
        rollout_gpus, gpu_mask, args.leased_gpu_inference,
        args.leased_gpu_count, resume=args.resume_rollout,
    )
    if native_manifest_model_dependencies(
        manifest, os.environ.get("BEES_NATIVE_MODEL_STORE"),
    ) != model_dependencies:
        raise ValueError("native rollout model paths changed during collection")
    if args.collect_only:
        captures = []
        for shard, _ in shards:
            files = {}
            for key in ("policy_capture", "notifications"):
                artifact = Path(shard[key])
                files[key] = {
                    "path": artifact.relative_to(output).as_posix(),
                    "sha256": file_sha256(artifact),
                }
            captures.append(files)
        if (file_sha256(checkpoint) != input_checkpoint_sha or
                file_sha256(manifest) != input_manifest_sha or
                file_sha256(binary) != runner_sha or
                normalized_python_source_sha256(policy_worker_path) != policy_worker_sha or
                native_source_contract_sha256(run_path) != source_contract_sha or
                native_source_contract_sha256(
                    run_path, include_allocation=True,
                ) != legacy_source_sha):
            raise ValueError("rollout input model, source content or runtime changed during collection")
        receipt = {
            "schema_version": 1,
            "kind": "native_rollout_bundle",
            "input_checkpoint_sha256": input_checkpoint_sha,
            "input_manifest_sha256": input_manifest_sha,
            "learner_slot": args.learner_slot,
            "run_config_sha256": hashlib.sha256(run_bytes).hexdigest(),
            "source_run_contract_sha256": source_contract_sha,
            "source_run_contract_sha256_v1": legacy_source_sha,
            "seed_offset": args.seed_offset,
            "native_runner_sha256": runner_sha,
            "policy_worker_sha256": policy_worker_sha,
            **({"collection_id": args.collection_id} if args.collection_id is not None else {}),
            "rollout_worlds": len(levels),
            "rollout_processes": workers,
            "physics_workers_per_process": physics_workers,
            **({"gpu_allocation": gpu_allocation} if gpu_allocation else {}),
            **({"leased_gpu_inference": True,
                "leased_gpu_count": args.leased_gpu_count}
               if args.leased_gpu_inference else {}),
            "partition": {
                "source_run_id": config["run_id"],
                "world_start": world_start,
                "world_count": len(levels),
            },
            "captures": captures,
            "rollout_metrics": rollout,
        }
        if inbox is not None:
            destination = deliver_bundle(output, inbox, receipt, args.bundle_delivery_name)
            print(json.dumps({"kind": "native_rollout_delivered", "directory": str(destination)}), flush=True)
        publish_receipt(output, "rollout-bundle.json", receipt)
        return
    rollout_args = []
    for shard, _ in shards[1:]:
        rollout_args += ["--rollout", shard["policy_capture"], shard["notifications"], str(manifest)]
    result.update(
        run_id=first["run_id"],
        rollout_worlds=len(levels),
        rollout_world_start=world_start,
        rollout_processes=workers,
        physics_workers_per_process=physics_workers,
        run_config=str(config_path),
        run_config_sha256=file_sha256(config_path),
        capture=first["policy_capture"],
        journal=first["notifications"],
        rollout_metrics=rollout,
    )
    if gpu_allocation is not None:
        result["gpu_allocation"] = gpu_allocation
    if args.leased_gpu_inference:
        result["leased_gpu_inference"] = True
        result["leased_gpu_count"] = args.leased_gpu_count
    if workers > 1:
        result["rollout_shards"] = [
            {"run_config": str(path), "run_config_sha256": file_sha256(path),
             "capture": shard["policy_capture"], "journal": shard["notifications"]}
            for shard, path in shards
        ]
    optimize(args, output, config["python"], manifest, checkpoint, trainer_config, result, rollout_args)


if __name__ == "__main__":
    main()
