"""Run one MA-POCA update on observed native decision intervals."""

import argparse
from bisect import bisect_right
from collections import defaultdict, deque
import hashlib
import json
import os
from pathlib import Path
import time

import numpy as np

from policy_worker import file_sha256, install_bees_actor, mlagents_behavior_spec, stream_sha256
from read_policy_capture import capture_frames


# Non-learner controllers need only lifetime/side validation. This
# packed representation retains named field access without one Python
# dict (and three boxed integers) per opponent decision.
OPPONENT_IDENTITY = np.dtype([
    ("ship", "<u8"), ("binding_revision", "<u8"), ("team", "u1"),
])


def completed_rollouts(capture, journal, manifest, manifest_sha, checkpoint_sha, learner_slot=None, stats=None):
    models = {entry["slot"]: entry for entry in manifest["models"]}
    matching = {
        slot for slot, entry in models.items()
        if entry["backend"] == "mlagents" and entry["sha256"] == checkpoint_sha
    }
    if learner_slot is None:
        if not matching or matching != models.keys():
            raise ValueError("mixed-checkpoint captures require an explicit learner slot")
        learner_slots = matching
    else:
        if learner_slot not in matching:
            raise ValueError("learner slot must match the optimizer input checkpoint")
        learner_slots = {learner_slot}
    has_opponent_slots = learner_slots != set(models)
    terminals = {}
    all_terminals = {}
    rewards = defaultdict(list)
    ended_teams = defaultdict(dict)
    retirements = defaultdict(dict)
    journal_id = None
    with journal.open(encoding="utf-8") as stream:
        for sequence, line in enumerate(stream):
            row = json.loads(line)
            if journal_id is None:
                journal_id = row["journal_id"]
            if row["journal_id"] != journal_id or row["journal_sequence"] != sequence:
                raise ValueError("mixed or incomplete reward journal")
            key = row["world_id"], row["episode_revision"]
            if row["kind"] == "episode_final":
                context = row["evaluation_context"]
                if key in all_terminals:
                    raise ValueError("duplicate episode terminal")
                all_terminals[key] = row
                if context["policy_manifest_sha256"] == manifest_sha:
                    terminals[key] = row
            elif row["kind"] == "episode_aborted":
                raise ValueError("aborted episode in training capture")
            elif row["kind"] == "policy_controller_retired":
                payload = row["payload"]
                controller = payload["ship_id"], payload["binding_revision"]
                if controller in retirements[key]:
                    raise ValueError("duplicate policy controller retirement")
                retirements[key][controller] = row["tick"]
            elif row["kind"] == "group_reward":
                event = row["payload"]
                if event["group_kind"] in ("immediate", "terminal"):
                    rewards[key, event["team_id"]].append((row["tick"], event["ppo_reward"]))
                elif event["group_kind"] == "end_episode":
                    team = event["team_id"]
                    if team in ended_teams[key]:
                        raise ValueError("duplicate group terminal event")
                    ended_teams[key][team] = row["tick"]

    if not all_terminals:
        raise ValueError("reward journal has no completed episodes")
    if not terminals:
        raise ValueError("capture contains no completed episodes for the selected policy manifest")
    # Keep only active episodes in memory. The writer emits a final
    # observation frame for each completed world before recycling it.
    # Preserve the original first-decision episode order for optimizer input.
    frames = {}
    boundaries = defaultdict(dict)
    first_seen = deque()
    ready = set()
    completed = set()
    queued_decisions = 0
    all_episodes_match = len(terminals) == len(all_terminals)
    for generation, rows in capture_frames(capture, journal_id):
        # Retain full observations only for the selected learner. Opposing
        # controllers still need their identity and team verified at every
        # decision, but their 7,743-float observations never reach MA-POCA.
        # A selected-only NumPy copy keeps the bulk allocation advantage.
        learner_rows = None
        opponent_rows = None
        if generation is not None and all_episodes_match and has_opponent_slots:
            selected = np.isin(rows["model_slot"], list(learner_slots))
            learner_rows = iter(rows[selected])
            # Project only the three opponent fields BEFORE copying. Never
            # materialize rows[~selected]: that would copy wide observations.
            opponent_identities = np.empty(
                len(rows) - np.count_nonzero(selected), dtype=OPPONENT_IDENTITY,
            )
            for name in OPPONENT_IDENTITY.names:
                opponent_identities[name] = rows[name][~selected]
            opponent_rows = iter(opponent_identities)
            owned_frame = None
        else:
            owned_frame = rows.copy() if all_episodes_match else None
        source_rows = owned_frame if owned_frame is not None else rows
        if generation is None:
            for row in source_rows:
                key = int(row["world"]), int(row["episode"])
                if key not in all_terminals:
                    raise ValueError("policy boundary belongs to an unknown completed episode")
                if key in completed:
                    raise ValueError("duplicate policy episode boundary")
                if key not in terminals:
                    continue
                tick, agent = int(row["tick"]), int(row["group_agent"])
                frame = boundaries[key].setdefault(tick, {})
                if not 0 < tick <= terminals[key]["tick"] or agent in frame:
                    raise ValueError("invalid policy boundary observation identity")
                frame[agent] = row if owned_frame is not None else row.copy()
                if tick == terminals[key]["tick"]:
                    ready.add(key)
        else:
            if "continuous_log_probs" not in rows.dtype.names:
                raise ValueError("training requires captured behavior statistics")
            for row in source_rows:
                key = int(row["world"]), int(row["episode"])
                terminal = all_terminals.get(key)
                if terminal is None:
                    raise ValueError("policy decision belongs to an unknown completed episode")
                context = terminal["evaluation_context"]
                if generation != context["policy_generation"]:
                    raise ValueError("rollout includes decisions outside the recorded policy generation")
                if key not in terminals:
                    continue
                if key in completed or key in ready:
                    raise ValueError("policy decision follows its completed boundary")
                if row["model_slot"] not in models:
                    raise ValueError("rollout includes an unknown model slot")
                tick, agent = int(row["tick"]), int(row["group_agent"])
                team = int(row["team"])
                if (tick >= terminal["tick"] or
                        row["model_slot"] != context["model_slots_by_team"][str(team)] or
                        agent not in context["group_agents_by_team"][str(team)]):
                    raise ValueError("decision disagrees with its authored episode")
                if key not in frames:
                    frames[key] = {}
                    first_seen.append(key)
                frame = frames[key].setdefault(tick, {})
                if agent in frame:
                    raise ValueError("duplicate agent at a decision tick")
                if int(row["model_slot"]) in learner_slots:
                    frame[agent] = (
                        next(learner_rows) if learner_rows is not None else
                        row if owned_frame is not None else row.copy()
                    )
                else:
                    # The validator uses these exact fields for opponents;
                    # only learner-side rows are fed to the group critic.
                    frame[agent] = (
                        next(opponent_rows) if opponent_rows is not None else
                        {"ship": int(row["ship"]),
                         "binding_revision": int(row["binding_revision"]),
                         "team": team}
                    )
                    if stats is not None:
                        stats["compact_opponent_decisions"] = (
                            stats.get("compact_opponent_decisions", 0) + 1
                        )
                queued_decisions += 1

        if stats is not None:
            stats["peak_inflight_decisions"] = max(
                stats.get("peak_inflight_decisions", 0), queued_decisions,
            )
            stats["peak_inflight_episodes"] = max(
                stats.get("peak_inflight_episodes", 0), len(frames),
            )

        # A completed episode can be handed to MA-POCA and released before
        # the rest of a long capture is loaded. Do not reorder episodes:
        # keep the learner's trajectory input sequence stable.
        while first_seen and first_seen[0] in ready:
            key = first_seen.popleft()
            ready.remove(key)
            ticks = frames.pop(key)
            queued_decisions -= sum(len(frame) for frame in ticks.values())
            episode_boundaries = boundaries.pop(key, {})
            completed.add(key)
            yield _validate_completed_episode(
                key, ticks, episode_boundaries, terminals, ended_teams,
                retirements, rewards, learner_slots, journal_id,
            )

    if completed != terminals.keys():
        raise ValueError("completed episode missing decisions or policy boundary")


def _validate_completed_episode(
    key, episode_ticks, episode_boundaries, terminals, ended_teams,
    retirements, rewards, learner_slots, journal_id,
):
    """Preserve source cohort, retirement, group reward and terminal checks."""
    ticks = episode_ticks
    context = terminals[key]["evaluation_context"]
    period = context["decision_period"]
    ticks = list(ticks.items())
    expected = {
        agent: int(team)
        for team, agents in context["group_agents_by_team"].items()
        for agent in agents
    }
    if not ticks or period <= 0:
        raise ValueError("episode has no observed decision interval")
    identities = {
        agent: (int(row["ship"]), int(row["binding_revision"]))
        for agent, row in ticks[0][1].items()
    }
    terminal = terminals[key]
    terminal_tick = terminal["tick"]
    bee_team = context["group_team_by_side"][str(context["bee_side"])]
    closing = dict.fromkeys(expected, terminal_tick)
    early = {}
    for agent, controller in identities.items():
        retired_at = retirements[key].get(controller)
        if retired_at is None:
            continue
        eliminated = terminal["payload"][
            "bee_side_eliminated" if expected[agent] == bee_team else "human_side_eliminated"
        ]
        if retired_at != terminal_tick or not eliminated:
            closing[agent] = retired_at
            early[agent] = retired_at
    for index, (tick, frame) in enumerate(ticks, 1):
        active = {agent for agent, end in closing.items() if tick < end}
        if (tick != index * period - 1 or frame.keys() != active or
                any(int(row["team"]) != expected[agent] or
                    (row["ship"], row["binding_revision"]) != identities.get(agent)
                    for agent, row in frame.items())):
            raise ValueError("decision cohort disagrees with recorded controller retirements")
    last_decision = {}
    for tick, frame in ticks:
        last_decision.update(dict.fromkeys(frame, tick))
    if (last_decision.keys() != expected.keys() or
            any(not 0 < end - last_decision[agent] <= period for agent, end in closing.items())):
        raise ValueError("controller is missing its final decision interval")
    expected_boundaries = {}
    for end in set(early.values()) | {terminal_tick}:
        expected_boundaries[end] = {
            agent for agent, close in closing.items() if close >= end
        }
    if (ended_teams[key] != dict.fromkeys(expected.values(), terminal_tick) or
            episode_boundaries.keys() != expected_boundaries.keys()):
        raise ValueError("missing MA-POCA terminal or retirement boundary")
    for end, frame in episode_boundaries.items():
        if (frame.keys() != expected_boundaries[end] or
                any((row["ship"], row["binding_revision"]) != identities.get(agent) or
                    not np.isfinite(row["observations"]).all()
                    for agent, row in frame.items())):
            raise ValueError("policy boundary observations disagree with controller lifetimes")
    learning_agents = {
        agent for agent, team in expected.items()
        if context["model_slots_by_team"][str(team)] in learner_slots
    }
    if not learning_agents:
        raise ValueError("completed episode has no decisions from the learner slot")
    return (journal_id, *key), expected, ticks, closing, episode_boundaries, {
        team: rewards[key, team] for team in set(expected.values())
    }, learning_agents


def trajectories(rollouts, horizon, behavior_id):
    from mlagents.trainers.trajectory import AgentExperience, AgentStatus, Trajectory
    from mlagents.trainers.torch_entities.action_log_probs import LogProbsTuple
    from mlagents_envs.base_env import ActionTuple

    for key, teams, frames, closing, boundaries, rewards, learning_agents in rollouts:
        terminal_tick = max(boundaries)
        reward_prefix = {}
        for team, events in rewards.items():
            reward_prefix[team] = ([tick for tick, _ in events],
                                   np.cumsum([0.0] + [amount for _, amount in events]))
        frames = frames + [(terminal_tick, boundaries[terminal_tick])]
        experiences = {agent: [] for agent in learning_agents}
        previous = {}
        initial_action = np.zeros(6, dtype=np.int32)
        for (tick, frame), (next_tick, following) in zip(frames, frames[1:]):
            actions = {
                agent: ActionTuple(row["sampled_continuous"], row["discrete"].astype(np.int32))
                for agent, row in frame.items() if agent in learning_agents
            }
            # Keep the tiny 1v1 path allocation-free. For large mixed-team
            # batches, index the frame once instead of scanning every enemy
            # for each MA-POCA groupmate query. Preserve frame order.
            members_by_team = None
            if len(frame) > 4:
                members_by_team = defaultdict(list)
                for member in frame:
                    members_by_team[teams[member]].append(member)
            # Group rewards depend on team and interval, not on the
            # individual policy controller. Cache only for larger active
            # cohorts; 1v1 avoids the per-frame dictionary allocation.
            interval_rewards = {} if len(actions) > 4 else None
            for agent in actions:
                row = frame[agent]
                end = min(next_tick, closing[agent])
                done = end == closing[agent]
                team = teams[agent]
                mates = (
                    [mate for mate in members_by_team[team] if mate != agent]
                    if members_by_team is not None else
                    [mate for mate in frame if mate != agent and teams[mate] == team]
                )
                group_status = [
                    AgentStatus([frame[mate]["observations"]], 0.0, actions[mate], closing[mate] <= end)
                    for mate in mates
                ]
                mask = [np.array([False, not bit]) for bit in row["fire_allowed"]]
                mask.append(row["special_allowed"] == 0)
                if interval_rewards is None:
                    times, amounts = reward_prefix[team]
                    reward = float(amounts[bisect_right(times, end)] -
                                   amounts[bisect_right(times, tick)])
                else:
                    interval = (team, end)
                    reward = interval_rewards.get(interval)
                    if reward is None:
                        times, amounts = reward_prefix[team]
                        reward = float(amounts[bisect_right(times, end)] -
                                       amounts[bisect_right(times, tick)])
                        interval_rewards[interval] = reward
                steps = experiences[agent]
                steps.append(AgentExperience(
                    [row["observations"]], 0.0, done, actions[agent],
                    LogProbsTuple(row["continuous_log_probs"], row["discrete_log_probs"]),
                    mask, previous.get(agent, initial_action),
                    False, None, group_status,
                    reward,
                ))
                previous[agent] = actions[agent].discrete
                if done or len(steps) == horizon:
                    observation_frame = boundaries[end] if done else following
                    yield Trajectory(
                        steps, [observation_frame[agent]["observations"]],
                        [[observation_frame[mate]["observations"]] for mate in mates
                         if closing[mate] > end or end == terminal_tick],
                        f"{key[0]}:{key[1]}:{key[2]}:{agent}", behavior_id,
                    )
                    experiences[agent] = []


def bundle_captures(receipt_path, manifest_sha, checkpoint_sha, learner_slot, expected_sha=None):
    receipt_path = receipt_path.resolve(strict=True)
    root = receipt_path.parent
    content = receipt_path.read_bytes()
    if expected_sha is not None and hashlib.sha256(content).hexdigest() != expected_sha:
        raise ValueError("rollout bundle receipt differs from the approved digest")
    receipt = json.loads(content)
    if (receipt.get("schema_version") != 1 or receipt.get("kind") != "native_rollout_bundle" or
            receipt.get("input_manifest_sha256") != manifest_sha or
            receipt.get("input_checkpoint_sha256") != checkpoint_sha or
            receipt.get("learner_slot") != learner_slot or not receipt.get("captures")):
        raise ValueError("native rollout bundle does not match the learner inputs")
    for entry in receipt["captures"]:
        files = []
        for key in ("policy_capture", "notifications"):
            record = entry[key]
            relative = Path(record["path"])
            artifact = (root / relative).resolve(strict=True)
            if (relative.is_absolute() or not artifact.is_relative_to(root) or
                    not artifact.is_file() or file_sha256(artifact) != record["sha256"]):
                raise ValueError(f"native rollout bundle has invalid {key} artifact")
            files.append(artifact)
        yield tuple(files)


def main():
    started_at = time.perf_counter()
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("capture", "journal"):
        parser.add_argument(name, type=Path, nargs="?")
    for name in ("manifest", "checkpoint", "config", "output"):
        parser.add_argument(name, type=Path)
    parser.add_argument("--learner-slot", type=int, help="train only on this manifest slot")
    parser.add_argument(
        "--device", default="cpu",
        help="optimizer device: cpu, cuda, or cuda:N (ROCm PyTorch also uses cuda)",
    )
    parser.add_argument(
        "--rollout", type=Path, nargs=3, action="append", default=[],
        metavar=("CAPTURE", "JOURNAL", "MANIFEST"),
        help="add another completed capture sampled from the same learner checkpoint",
    )
    parser.add_argument("--rollout-bundle", type=Path, action="append", default=[],
                        help="import a completed collection receipt and its captures")
    parser.add_argument("--pinned-rollout-bundle", nargs=2, action="append", default=[],
                        metavar=("RECEIPT", "SHA256"), help="import a receipt with an approved digest")
    args = parser.parse_args()
    bundles = [(path, None) for path in args.rollout_bundle]
    bundles.extend((Path(path), digest) for path, digest in args.pinned_rollout_bundle)
    if (args.capture is None) != (args.journal is None) or (
            args.capture is None and not (bundles or args.rollout)):
        parser.error("supply a capture and journal or completed rollout inputs")

    import torch
    import yaml
    from mlagents.plugins.trainer_type import register_trainer_plugins
    from mlagents.trainers.behavior_id_utils import BehaviorIdentifiers
    from mlagents.trainers.poca.trainer import POCATrainer
    from mlagents.trainers.settings import TorchSettings, TrainerSettings
    from mlagents.torch_utils import set_torch_config

    try:
        device = torch.device(args.device)
    except (TypeError, RuntimeError) as error:
        parser.error(f"invalid optimizer device: {error}")
    if device.type not in ("cpu", "cuda"):
        parser.error("optimizer device must be cpu, cuda, or cuda:N")
    if device.type == "cuda" and (
        not torch.cuda.is_available() or
        (device.index is not None and device.index >= torch.cuda.device_count())
    ):
        parser.error(f"optimizer device {device} is not available")
    install_bees_actor()
    from bees_mlagents_ppo_compat import install_value_estimate_key_fix
    install_value_estimate_key_fix()
    set_torch_config(TorchSettings(device=str(device)))
    # ML-Agents selects CUDA at import on GPU hosts. Its CPU setting does
    # not clear that PyTorch default, so implicit critic tensors can still
    # land on CUDA. Reset it for CPU and retain the explicit CUDA index.
    torch.set_default_device(device if device.type == "cuda" else None)
    register_trainer_plugins()

    behavior = BehaviorIdentifiers.from_name_behavior_id("BeesRL1v1?team=0")
    settings = TrainerSettings.structure(
        yaml.safe_load(args.config.read_text(encoding="utf-8"))["behaviors"][behavior.brain_name],
        TrainerSettings,
    )
    network = settings.network_settings
    if (settings.trainer_type != "poca" or settings.init_path or settings.behavioral_cloning or
            network.memory is not None or not network.normalize or
            (network.hidden_units, network.num_layers) != (128, 3) or settings.time_horizon <= 0):
        parser.error("expected a feed-forward structured v26 MA-POCA trainer configuration")
    primary_path = args.manifest.resolve(strict=True)
    direct_inputs = [(args.capture, args.journal, primary_path)] if args.capture else []
    inputs = [
        (capture, journal, manifest.resolve(strict=True))
        for capture, journal, manifest in [*direct_inputs, *args.rollout]
    ]
    manifests = {}
    for path in [primary_path, *(path for _, _, path in inputs)]:
        if path not in manifests:
            content = path.read_bytes()
            manifests[path] = json.loads(content), hashlib.sha256(content).hexdigest()
    primary_manifest, primary_manifest_sha = manifests[primary_path]
    with args.checkpoint.open("rb") as source:
        input_sha = stream_sha256(source)
        source.seek(0)
        checkpoint = torch.load(source, map_location="cpu", weights_only=True)
        source.seek(0)
        if stream_sha256(source) != input_sha:
            raise ValueError("optimizer input checkpoint changed while loading")
    for receipt, digest in bundles:
        inputs.extend(
            (capture, journal, primary_path)
            for capture, journal in bundle_captures(
                receipt, primary_manifest_sha, input_sha, args.learner_slot, digest,
            )
        )
    args.output.mkdir()
    trainer = POCATrainer(behavior.brain_name, 100, settings, True, False, 0, str(args.output))
    policy = trainer.create_policy(behavior, mlagents_behavior_spec())
    for name, module in policy.get_modules().items():
        module.load_state_dict(checkpoint[name])
    trainer.add_policy(behavior, policy)
    optimizer_modules = trainer.optimizer.get_modules()
    if any(name in checkpoint for name in optimizer_modules):
        for name, module in optimizer_modules.items():
            module.load_state_dict(checkpoint[name])
    processing_started = time.perf_counter()
    terminal_steps = 0
    episode_keys = set()
    opponent_steps = 0
    capture_stats = {}
    for capture, journal, path in inputs:
        manifest, manifest_sha = manifests[path]
        for rollout in completed_rollouts(
            capture, journal, manifest, manifest_sha, input_sha, args.learner_slot,
            stats=capture_stats,
        ):
            key, _, ticks, _, _, _, learning_agents = rollout
            if key in episode_keys:
                parser.error("the same completed episode was supplied more than once")
            episode_keys.add(key)
            opponent_steps += sum(
                sum(agent not in learning_agents for agent in frame)
                for _, frame in ticks
            )
            for trajectory in trajectories((rollout,), settings.time_horizon, behavior.behavior_id):
                trainer._process_trajectory(trajectory)
                terminal_steps += int(trajectory.done_reached)
    steps = trainer.update_buffer.num_experiences
    if steps < settings.hyperparameters.batch_size:
        parser.error("capture contains fewer observed intervals than one optimizer minibatch")
    processing_wall_seconds = time.perf_counter() - processing_started
    update_started = time.perf_counter()
    if not trainer._update_policy():
        raise RuntimeError("MA-POCA did not update its policy")
    if device.type == "cuda":
        torch.cuda.synchronize(device)
    update_wall_seconds = time.perf_counter() - update_started
    # Bundle receipts pin the artifact digests, not only their own JSON.
    # Files may change while an update runs on a different worker; refuse
    # to publish learned weights if any input no longer matches its pin.
    for receipt, digest in bundles:
        for _ in bundle_captures(
            receipt, primary_manifest_sha, input_sha, args.learner_slot, digest,
        ):
            pass
    modules = {**policy.get_modules(), **optimizer_modules}
    next_checkpoint = args.output / "checkpoint.pt"
    with next_checkpoint.open("xb") as output:
        torch.save({name: module.state_dict() for name, module in modules.items()}, output)
        output.flush()
        os.fsync(output.fileno())

    # Keep the source ABI and every opponent slot. Only update the learner.
    manifest = primary_manifest
    if args.learner_slot is None:
        learner_slots = {
            entry["slot"] for entry in manifest["models"]
            if entry["backend"] == "mlagents" and entry["sha256"] == input_sha
        }
    else:
        learner_slots = {args.learner_slot}
    next_sha = file_sha256(next_checkpoint)
    for entry in manifest["models"]:
        if entry["slot"] in learner_slots:
            entry["path"] = next_checkpoint.name
            entry["sha256"] = next_sha
        else:
            old_artifact = (args.manifest.parent / entry["path"]).resolve()
            if file_sha256(old_artifact) != entry["sha256"]:
                raise ValueError(f"opponent artifact changed: slot {entry['slot']}")
            try:
                entry["path"] = Path(os.path.relpath(
                    old_artifact, args.output.resolve(),
                )).as_posix()
            except ValueError:
                # Relative paths cannot span separate Windows drives.
                entry["path"] = str(old_artifact)
    next_manifest = args.output / "manifest.json"
    with next_manifest.open("x", encoding="utf-8") as output:
        json.dump(manifest, output, indent=2)
        output.write("\n")
        output.flush()
        os.fsync(output.fileno())
    metrics = {
        "schema_version": 1,
        "source": "bees-native-ma-poca-v1",
        "input_checkpoint_sha256": input_sha,
        "input_manifest_sha256": primary_manifest_sha,
        "learner_slot": args.learner_slot,
        "next_checkpoint": str(next_checkpoint.resolve()),
        "next_checkpoint_sha256": next_sha,
        "next_manifest": str(next_manifest.resolve()),
        "next_manifest_sha256": file_sha256(next_manifest),
        "optimizer_device": str(device),
        "optimizer_input_steps": steps,
        "trajectory_processing_wall_seconds": processing_wall_seconds,
        "optimizer_update_wall_seconds": update_wall_seconds,
        "optimizer_steps_per_update_second": steps / update_wall_seconds if update_wall_seconds else None,
        "total_wall_seconds": time.perf_counter() - started_at,
        "terminal_steps": terminal_steps,
        "episodes": len(episode_keys),
        "opponent_steps": opponent_steps,
        "capture_peak_inflight_decisions": capture_stats.get("peak_inflight_decisions", 0),
        "capture_peak_inflight_episodes": capture_stats.get("peak_inflight_episodes", 0),
        "capture_compact_opponent_decisions": capture_stats.get("compact_opponent_decisions", 0),
    }
    pending_metrics = args.output / "metrics.json.pending"
    with pending_metrics.open("x", encoding="utf-8") as output:
        json.dump(metrics, output, indent=2)
        output.write("\n")
        output.flush()
        os.fsync(output.fileno())
    os.replace(pending_metrics, args.output / "metrics.json")
    if os.name == "posix":
        directory = os.open(args.output, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    print(json.dumps(metrics))


if __name__ == "__main__":
    main()
