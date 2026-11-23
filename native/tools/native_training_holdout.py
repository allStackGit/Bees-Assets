"""Shared fixed-seed native CombatWorld holdout authoring.

No neural framework is imported. Gameplay, PPO and evaluation execute in
their existing unified native runtimes; this module only freezes the exact
source Level and independent episode seed plans.
"""

import json
import os
from pathlib import Path

U64 = 1 << 64


def distinct_physical_scenarios(training_run: dict) -> list[dict]:
    """Find exact authored gameplay scenarios, disregarding only arena RNG/IDs.

    All command, mission, model, source-scene and physical contracts remain
    part of the key. Reused by the standalone bootstrap and the original
    multi-map collection author; both require the same coverage semantics.
    """
    sources = training_run.get("levels")
    if not isinstance(sources, list) or not sources:
        raise ValueError("native source requires at least one physical Level")
    distinct = {}
    for level in sources:
        if not isinstance(level, dict):
            raise ValueError("native source contains an invalid physical Level")
        identity = json.dumps({
            key: value for key, value in level.items()
            if key not in ("world_id", "level_id", "seed", "episode_seeds")
        }, sort_keys=True, separators=(",", ":"))
        distinct.setdefault(identity, level)
    return list(distinct.values())


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
    scenarios = distinct_physical_scenarios(run)
    count = len(scenarios)
    if worlds < count or worlds % count != 0:
        raise ValueError(
            f"balanced native holdout needs at least {count} Worlds, "
            "divisible by the number of distinct source physical scenarios"
        )
    from bisect import bisect_left
    occupied = set()
    training_seed_ranges = []
    training_episode_seed_points = set()
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
        if single:
            # LevelConfig.episode_seed(n) derives seed + n (wrapping
            # uint64), so a single source seed represents its ENTIRE
            # sequence of original training episodes, not only episode 0.
            training_seed_ranges.append((
                values[0], run["episodes_per_world"],
            ))
        else:
            training_episode_seed_points.update(values)
    fixed_training_seed_points = sorted(training_episode_seed_points)
    max_world = max(row["world_id"] for row in sources)
    max_level = max(row["level_id"] for row in sources)
    if (len({row["level_id"] for row in sources}) != count or
            max_world >= U64 - (worlds - count) or
            max_level >= (1 << 63) - (worlds - count)):
        raise ValueError("native holdout World/Level identity allocation is exhausted")
    source_budget = run.get("max_rounds")
    source_episodes = run.get("episodes_per_world")
    if (type(source_budget) is not int or type(source_episodes) is not int or
            source_budget < 1 or source_episodes < 1):
        raise ValueError("native source holdout has an invalid total episode budget")
    # Original multi-episode runs can have a legitimate, indivisible
    # total tick cap. Round UP the per-episode allowance so the fixed
    # holdout never truncates the physical completion window.
    per_episode_budget = (source_budget + source_episodes - 1) // source_episodes
    if per_episode_budget * episodes >= U64:
        raise ValueError("native fixed holdout exceeds the uint64 tick budget")
    run["run_id"] += "-fixed-holdout"
    run["max_rounds"] = per_episode_budget * episodes
    run["episodes_per_world"] = episodes
    run["levels"] = []
    holdout_seed_ranges = []
    for index in range(worlds):
        original = scenarios[index % count]
        level = dict(original)
        if index >= count:
            level["world_id"] = max_world + index - count + 1
            level["level_id"] = max_level + index - count + 1
        # Each source map gets exactly worlds/count independent fixed
        # initial seeds. The golden-ratio stride creates distinct 64-bit
        # physical RNG streams without changing the actual game PRNG.
        level_seed = (seed + index * 0x9E3779B97F4A7C15) % U64
        # Entire holdout episode streams must be disjoint from original
        # physical training streams, not just their first RNG seed. Unity
        # native World implements seed + episode offset with uint64 wrap.
        # The fixed 64-bit sampler seed is independent of this World RNG.
        def overlaps(first, first_count, second, second_count):
            return (
                (first - second) % U64 < second_count or
                (second - first) % U64 < first_count
            )
        if (any(overlaps(level_seed, episodes, start, count)
                for start, count in training_seed_ranges) or
                any(overlaps(level_seed, episodes, start, episodes)
                    for start in holdout_seed_ranges)):
            raise ValueError(
                "fixed native evaluation RNG stream overlaps another "
                "evaluation or the original training episode plan"
            )
        if fixed_training_seed_points:
            final = (level_seed + episodes - 1) % U64
            index_of_seed = bisect_left(fixed_training_seed_points, level_seed)
            overlap = (
                index_of_seed < len(fixed_training_seed_points) and
                fixed_training_seed_points[index_of_seed] <= final
                if level_seed <= final else
                fixed_training_seed_points[0] <= final or
                fixed_training_seed_points[-1] >= level_seed
            )
            if overlap:
                raise ValueError("fixed holdout RNG overlaps a source training episode")
        holdout_seed_ranges.append(level_seed)
        level["seed"] = level_seed
        level.pop("episode_seeds", None)
        run["levels"].append(level)
    with (output / "holdout-run.json").open("x", encoding="utf-8", newline="\n") as file:
        json.dump(run, file, indent=2)
        file.write("\n")
        file.flush()
        os.fsync(file.fileno())
