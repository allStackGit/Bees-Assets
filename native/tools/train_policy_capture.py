"""Run one MA-POCA update on observed native decision intervals."""

import argparse
from collections import defaultdict
import hashlib
import json
from pathlib import Path

import numpy as np

from policy_worker import file_sha256, install_bees_actor, mlagents_behavior_spec
from read_policy_capture import capture_frames


def completed_rollouts(capture, journal, manifest, checkpoint):
    manifest_bytes = manifest.read_bytes()
    manifest_sha = hashlib.sha256(manifest_bytes).hexdigest()
    checkpoint_sha = file_sha256(checkpoint)
    learner_slots = {
        entry["slot"] for entry in json.loads(manifest_bytes)["models"]
        if entry["backend"] == "mlagents" and entry["sha256"] == checkpoint_sha
    }
    terminals = {}
    rewards = defaultdict(list)
    ended_teams = defaultdict(dict)
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
                if key in terminals or context["policy_manifest_sha256"] != manifest_sha:
                    raise ValueError("ambiguous terminal or mismatched rollout manifest")
                terminals[key] = row
            elif row["kind"] == "episode_aborted":
                raise ValueError("aborted episode in training capture")
            elif row["kind"] == "group_reward":
                event = row["payload"]
                if event["group_kind"] in ("immediate", "terminal"):
                    rewards[key, event["team_id"]].append((row["tick"], event["ppo_reward"]))
                elif event["group_kind"] == "end_episode":
                    team = event["team_id"]
                    if team in ended_teams[key]:
                        raise ValueError("duplicate group terminal event")
                    ended_teams[key][team] = row["tick"]

    if not terminals:
        raise ValueError("reward journal has no completed episodes")
    frames = defaultdict(dict)
    final_observations = defaultdict(dict)
    for generation, rows in capture_frames(capture, journal_id):
        if generation is None:
            for row in rows.copy():
                key = int(row["world"]), int(row["episode"])
                agent = int(row["group_agent"])
                if row["tick"] != terminals[key]["tick"] or agent in final_observations[key]:
                    raise ValueError("invalid terminal observation identity")
                final_observations[key][agent] = row
            continue
        if "continuous_log_probs" not in rows.dtype.names:
            raise ValueError("training requires captured behavior statistics")
        for row in rows.copy():
            key = int(row["world"]), int(row["episode"])
            terminal = terminals[key]
            context = terminal["evaluation_context"]
            if generation != context["policy_generation"] or row["model_slot"] not in learner_slots:
                raise ValueError("rollout includes decisions from a different behavior policy")
            tick, agent = int(row["tick"]), int(row["group_agent"])
            team = int(row["team"])
            if (tick >= terminal["tick"] or
                    row["model_slot"] != context["model_slots_by_team"][str(team)] or
                    agent not in context["group_agents_by_team"][str(team)]):
                raise ValueError("decision disagrees with its authored episode")
            frame = frames[key].setdefault(tick, {})
            if agent in frame:
                raise ValueError("duplicate agent at a decision tick")
            frame[agent] = row

    if frames.keys() != terminals.keys():
        raise ValueError("completed episode missing decisions")
    for key, ticks in frames.items():
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
        for index, (tick, frame) in enumerate(ticks, 1):
            if (tick != index * period - 1 or frame.keys() != expected.keys() or
                    any(int(row["team"]) != expected[agent] or
                        (row["ship"], row["binding_revision"]) != identities[agent]
                        for agent, row in frame.items())):
                raise ValueError("training requires a stable, synchronized MA-POCA cohort")
        if terminals[key]["tick"] - ticks[-1][0] > period:
            raise ValueError("decision capture ends before the final decision interval")
        terminal_tick = terminals[key]["tick"]
        terminal_rows = final_observations[key]
        if (ended_teams[key] != dict.fromkeys(expected.values(), terminal_tick) or
                terminal_rows.keys() != expected.keys() or
                any((row["ship"], row["binding_revision"]) != identities[agent] or
                    not np.isfinite(row["observations"]).all()
                    for agent, row in terminal_rows.items())):
            raise ValueError("training requires terminal observations for the same MA-POCA cohort")
        ticks.append((terminal_tick, terminal_rows))
        yield key, expected, ticks, {
            team: rewards[key, team] for team in set(expected.values())
        }


def trajectories(rollouts, horizon, behavior_id):
    from mlagents.trainers.trajectory import AgentExperience, AgentStatus, Trajectory
    from mlagents.trainers.torch_entities.action_log_probs import LogProbsTuple
    from mlagents_envs.base_env import ActionTuple

    for key, teams, frames, rewards in rollouts:
        first_tick = frames[0][0]
        period = frames[1][0] - first_tick
        # The last frame contains real final observations without a new action.
        group_rewards = {team: np.zeros(len(frames) - 1) for team in rewards}
        for team, events in rewards.items():
            for tick, amount in events:
                index = (tick - first_tick - 1) // period
                if 0 <= index < len(frames) - 1:
                    group_rewards[team][index] += amount
        for start in range(0, len(frames) - 1, horizon):
            end = min(start + horizon, len(frames) - 1)
            experiences = {agent: [] for agent in teams}
            for index in range(start, end):
                frame = frames[index][1]
                done = index == len(frames) - 2
                actions = {
                    agent: ActionTuple(row["sampled_continuous"], row["discrete"].astype(np.int32))
                    for agent, row in frame.items()
                }
                for agent, row in frame.items():
                    mates = [
                        AgentStatus([frame[mate]["observations"]], 0.0, actions[mate], done)
                        for mate in teams if mate != agent and teams[mate] == teams[agent]
                    ]
                    mask = [np.array([False, not bit]) for bit in row["fire_allowed"]]
                    mask.append(row["special_allowed"] == 0)
                    previous = (frames[index - 1][1][agent]["discrete"] if index
                                else np.zeros_like(row["discrete"]))
                    experiences[agent].append(AgentExperience(
                        [row["observations"]], 0.0, done, actions[agent],
                        LogProbsTuple(row["continuous_log_probs"], row["discrete_log_probs"]),
                        mask, previous.astype(np.int32), False, None, mates,
                        float(group_rewards[teams[agent]][index]),
                    ))
            following = frames[end][1]
            for agent, steps in experiences.items():
                yield Trajectory(
                    steps, [following[agent]["observations"]],
                    [[following[mate]["observations"]] for mate in teams
                     if mate != agent and teams[mate] == teams[agent]],
                    f"{key[0]}:{key[1]}:{agent}", behavior_id,
                )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("capture", "journal", "manifest", "checkpoint", "config", "output"):
        parser.add_argument(name, type=Path)
    args = parser.parse_args()

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
    checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=True)
    rollouts = list(completed_rollouts(args.capture, args.journal, args.manifest, args.checkpoint))
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
    terminal_steps = 0
    for trajectory in trajectories(rollouts, settings.time_horizon, behavior.behavior_id):
        trainer._process_trajectory(trajectory)
        terminal_steps += int(trajectory.done_reached)
    steps = trainer.update_buffer.num_experiences
    if steps < settings.hyperparameters.batch_size:
        parser.error("capture contains fewer observed intervals than one optimizer minibatch")
    if not trainer._update_policy():
        raise RuntimeError("MA-POCA did not update its policy")
    modules = {**policy.get_modules(), **optimizer_modules}
    with (args.output / "checkpoint.pt").open("xb") as output:
        torch.save({name: module.state_dict() for name, module in modules.items()}, output)
    print(json.dumps({
        "optimizer_input_steps": steps,
        "terminal_steps": terminal_steps,
        "episodes": len(rollouts),
    }))


if __name__ == "__main__":
    main()
