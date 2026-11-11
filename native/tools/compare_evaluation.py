"""Compare completed native episodes with matching recorded evaluation inputs."""
import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path


def manifest_identity(path):
    data = path.read_bytes()
    models = {}
    for entry in json.loads(data)["models"]:
        slot = str(entry["slot"])
        digest = entry["sha256"]
        if slot in models or len(digest) != 64 or any(c not in "0123456789abcdef" for c in digest):
            raise ValueError(f"{path}: invalid or duplicate model artifact identity")
        # Preserve all execution settings when deciding whether an opponent is fixed.
        models[slot] = {key: value for key, value in entry.items() if key not in {"slot", "path"}}
    return hashlib.sha256(data).hexdigest(), models


def completed_episodes(path, focus, manifest):
    episodes = {}
    counts = Counter()
    terminal_ids = set()
    journal = None
    with path.open(encoding="utf-8") as stream:
        for line_number, line in enumerate(stream, 1):
            try:
                row = json.loads(line)
                if row["kind"] not in {"episode_final", "episode_aborted"}:
                    continue
                identity = (row["run_id"], row["journal_id"])
                if journal is not None and identity != journal:
                    raise ValueError("mixed journal identities")
                journal = identity
                terminal_id = row["world_id"], row["episode_revision"]
                if terminal_id in terminal_ids:
                    raise ValueError("duplicate terminal episode")
                terminal_ids.add(terminal_id)
                if row["kind"] == "episode_aborted":
                    counts["aborted"] += 1
                    continue
                context = row["evaluation_context"]
                side = context[f"{focus}_side"]
                other = context["human_side" if focus == "bee" else "bee_side"]
                teams = context["group_team_by_side"]
                slots = context["model_slots_by_team"]
                manifest_sha = context["policy_manifest_sha256"]
                model_identity = [manifest_sha, slots]
                if manifest is not None:
                    expected_sha, models = manifest
                    if manifest_sha != expected_sha:
                        raise ValueError("episode does not use the supplied model manifest")
                    authored = context.get("authored_policy_slots_by_side")
                    if authored is None:
                        raise ValueError("changing models requires recorded authored policy seats")
                    focus_slots = set(authored.get(str(side), ()))
                    opponent_slots = set(authored.get(str(other), ()))
                    if not focus_slots or not opponent_slots:
                        raise ValueError("fixed-opponent model comparison requires a policy controller on both sides")
                    if focus_slots & opponent_slots:
                        raise ValueError("focus and opponent must use separate model slots")
                    if any(str(slot) not in models for slot in focus_slots | opponent_slots):
                        raise ValueError("authored policy slot missing from the supplied manifest")
                    model_identity = {
                        slot: models[str(slot)] for slot in sorted(opponent_slots)
                    }
                gameplay_config = context.get("gameplay_config_sha256")
                if gameplay_config is None:
                    counts["legacy_config"] += 1
                elif (not isinstance(gameplay_config, str) or len(gameplay_config) != 64 or
                      any(c not in "0123456789abcdef" for c in gameplay_config)):
                    raise ValueError("invalid gameplay configuration digest")
                key = json.dumps([
                    context["scene_sha256"], context["seed"],
                    context["bee_side"], context["human_side"],
                    context["group_episode_number"], teams, model_identity, gameplay_config,
                ], sort_keys=True, separators=(",", ":"))
                if key in episodes:
                    raise ValueError("ambiguous repeated evaluation inputs")
                payload = row["payload"]
                winner = payload["winner_side"]
                duration = payload["duration_seconds"]
                if winner not in {0, side, other} or not math.isfinite(duration) or duration < 0:
                    raise ValueError("invalid terminal result")
                if type(payload["timed_out"]) is not bool:
                    raise ValueError("invalid timeout indicator")
                outcome = "draw" if winner == 0 else ("win" if winner == side else "loss")
                episodes[key] = (outcome, payload["timed_out"], duration)
            except (KeyError, TypeError, ValueError) as error:
                raise ValueError(f"{path}:{line_number}: {error}") from error
    return episodes, counts


# The serialized identity is constructed in completed_episodes above. These
# prefixes identify where paired comparison stops matching without requiring
# operators to inspect huge per-episode keys or relax strict matching.
def aggregate_completed_episodes(paths, focus, manifest):
    """Combine independently journaled native shards without losing pair identity.

    Each file retains its own run/journal identity validation. Repeated
    evaluation inputs across files are ambiguous, not extra samples.
    """
    episodes = {}
    counts = Counter()
    for path in paths:
        shard, shard_counts = completed_episodes(path, focus, manifest)
        if episodes.keys() & shard.keys():
            raise ValueError(f"{path}: duplicate evaluation inputs across journal files")
        episodes.update(shard)
        counts.update(shard_counts)
    return episodes, counts


MATCH_FIELDS = (
    "scene_sha256", "seed", "bee_side", "human_side",
    "group_episode_number", "group_team_by_side",
    "model_identity", "gameplay_config_sha256",
)


def matching_input_prefixes(reference, candidate):
    left = [json.loads(key) for key in reference]
    right = [json.loads(key) for key in candidate]
    matches = {}
    for width, field in enumerate(MATCH_FIELDS, 1):
        left_keys = {
            json.dumps(parts[:width], sort_keys=True, separators=(",", ":"))
            for parts in left
        }
        right_keys = {
            json.dumps(parts[:width], sort_keys=True, separators=(",", ":"))
            for parts in right
        }
        matches[field] = len(left_keys & right_keys)
    return matches


def compare(reference, candidate):
    shared = reference.keys() & candidate.keys()
    transitions = Counter()
    score = {"loss": 0.0, "draw": 0.5, "win": 1.0}
    reference_score = candidate_score = 0.0
    duration_delta = 0.0
    timeout_delta = 0
    for key in sorted(shared):
        before, after = reference[key], candidate[key]
        transitions[f"{before[0]}_to_{after[0]}"] += 1
        reference_score += score[before[0]]
        candidate_score += score[after[0]]
        timeout_delta += int(after[1]) - int(before[1])
        duration_delta += after[2] - before[2]
    count = len(shared)
    return {
        "matched_episodes": count,
        "unmatched_reference_episodes": len(reference) - count,
        "unmatched_candidate_episodes": len(candidate) - count,
        "matching_input_prefixes": matching_input_prefixes(reference, candidate),
        "outcome_transitions": dict(transitions),
        "reference_mean_score": reference_score / count if count else None,
        "candidate_mean_score": candidate_score / count if count else None,
        "mean_score_delta": (candidate_score - reference_score) / count if count else None,
        "timeout_count_delta": timeout_delta if count else None,
        "mean_episode_seconds_delta": duration_delta / count if count else None,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reference", type=Path)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("--focus-side", choices=("bee", "human"), required=True)
    parser.add_argument("--reference-manifest", type=Path)
    parser.add_argument("--candidate-manifest", type=Path)
    parser.add_argument("--reference-extra", type=Path, action="append", default=[],
                        help="additional reference shard/journal; repeat for each file")
    parser.add_argument("--candidate-extra", type=Path, action="append", default=[],
                        help="additional candidate shard/journal; repeat for each file")
    args = parser.parse_args()
    paths = (args.reference_manifest, args.candidate_manifest)
    if (paths[0] is None) != (paths[1] is None):
        parser.error("supply both model manifests or omit both")
    try:
        manifests = [manifest_identity(path) if path else None for path in paths]
        reference, reference_counts = aggregate_completed_episodes(
            [args.reference, *args.reference_extra], args.focus_side, manifests[0])
        candidate, candidate_counts = aggregate_completed_episodes(
            [args.candidate, *args.candidate_extra], args.focus_side, manifests[1])
        report = compare(reference, candidate)
    except (OSError, KeyError, TypeError, ValueError) as error:
        parser.error(str(error))
    report.update(
        focus_side=args.focus_side,
        reference_journal_files=1 + len(args.reference_extra),
        candidate_journal_files=1 + len(args.candidate_extra),
        model_matching="fixed_opponent_artifact" if paths[0] else "same_manifest_and_slots",
        reference_aborted_episodes=reference_counts["aborted"],
        candidate_aborted_episodes=candidate_counts["aborted"],
        reference_episodes_without_gameplay_config=reference_counts["legacy_config"],
        candidate_episodes_without_gameplay_config=candidate_counts["legacy_config"],
    )
    print(json.dumps(report, indent=2, sort_keys=True, allow_nan=False))
    return 0 if report["matched_episodes"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
