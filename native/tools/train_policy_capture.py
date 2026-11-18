"""Run one MA-POCA update on observed native decision intervals."""

import argparse
from array import array
from bisect import bisect_left, bisect_right, insort
from collections import defaultdict, deque
import hashlib
import json
import os
import shutil
from pathlib import Path
import time

import numpy as np

from policy_worker import file_sha256, install_bees_actor, mlagents_behavior_spec, stream_sha256
from read_policy_capture import capture_frames
from native_training_outcomes import accumulate_training_outcome, record_combat_hit


# Non-learner controllers need only lifetime/side validation. This
# packed representation retains named field access without one Python
# dict (and three boxed integers) per opponent decision.
OPPONENT_IDENTITY = np.dtype([
    ("ship", "<u8"), ("binding_revision", "<u8"), ("team", "u1"),
])


class GroupRewardSeries:
    """Compact source-order group rewards with float64 journal precision.

    Every reward from a completed world is needed to attribute PPO group
    returns. Two typed arrays avoid one Python tuple and two boxed objects
    per event; maintain the original timestamp and amount iteration contract.
    """
    __slots__ = ("ticks", "amounts")

    def __init__(self):
        self.ticks = array("Q")
        self.amounts = array("d")

    def add(self, tick, amount):
        self.ticks.append(tick)
        self.amounts.append(amount)

    def __iter__(self):
        return zip(self.ticks, self.amounts)

    def prefix(self):
        # Cumulative group rewards are float64 in the original learner.
        # Take a borrowed numeric view; neither source array is modified
        # once an episode has completed.
        cumulative = np.empty(len(self.amounts) + 1, dtype=np.float64)
        cumulative[0] = 0.0
        np.cumsum(np.frombuffer(self.amounts, dtype=np.float64), out=cumulative[1:])
        return self.ticks, cumulative


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
    rewards = defaultdict(GroupRewardSeries)
    ended_teams = defaultdict(dict)
    retirements = defaultdict(dict)
    admissions = defaultdict(dict)
    # Separate O(1) binding-identity index; a long Queen wave must not
    # rescan every prior newborn for each new real admission.
    admitted_bindings = defaultdict(set)
    # Source combat hits are logged once per physical hit. Retain
    # aggregates, not individual hit events, until their episode closes.
    combat_by_episode = defaultdict(lambda: defaultdict(lambda: defaultdict(int)))
    journal_id = None
    run_id = None
    world_sequences = {}
    with journal.open(encoding="utf-8") as stream:
        for sequence, line in enumerate(stream):
            row = json.loads(line)
            if journal_id is None:
                journal_id = row["journal_id"]
                run_id = row["run_id"]
            if (row.get("schema_version") != 2 or
                    not isinstance(run_id, str) or not run_id.strip() or
                    row["journal_id"] != journal_id or row["run_id"] != run_id or
                    type(row["journal_sequence"]) is not int or
                    row["journal_sequence"] != sequence):
                raise ValueError("mixed or incomplete reward journal")
            world, revision = row["world_id"], row["episode_revision"]
            previous = world_sequences.get(world)
            if (type(world) is not int or type(revision) is not int or
                    (previous is not None and revision < previous[0])):
                raise ValueError("invalid or retired world episode in reward journal")
            same_episode = previous is not None and revision == previous[0]
            expected = previous[1] if same_episode else 0
            tick = row["tick"]
            if (type(row["event_sequence"]) is not int or row["event_sequence"] != expected or
                    type(tick) is not int or tick < 0 or
                    (same_episode and tick < previous[2])):
                raise ValueError("missing, repeated, or out-of-order world notification")
            world_sequences[world] = (revision, expected + 1, tick)
            key = world, revision
            # The source Level writes EpisodeFinal after all combat and
            # group rewards. Only the immediate command-credit settlement
            # batch may follow, exactly one event later at the same tick.
            final = all_terminals.get(key)
            if row["kind"] == "command_outcomes_ready":
                if (final is None or row["event_sequence"] != final["event_sequence"] + 1 or
                        tick != final["tick"]):
                    raise ValueError("outcome batch is detached from the episode terminal")
            elif final is not None:
                raise ValueError("notification follows the completed episode terminal")
            if row["kind"] == "episode_final":
                context = row["evaluation_context"]
                if key in all_terminals:
                    raise ValueError("duplicate episode terminal")
                all_terminals[key] = row
                if context["policy_manifest_sha256"] == manifest_sha:
                    terminals[key] = row
            elif row["kind"] == "episode_aborted":
                raise ValueError("aborted episode in training capture")
            elif row["kind"] == "policy_controller_admitted":
                payload = row["payload"]
                agent = payload.get("group_agent")
                ship = payload.get("ship_id")
                revision = payload.get("binding_revision")
                side = payload.get("side")
                if (any(type(value) is not int or value <= 0
                        for value in (agent, ship, revision, side)) or
                        tick == 0 or agent in admissions[key] or
                        (ship, revision) in admitted_bindings[key]):                    raise ValueError("repeated or invalid native policy enrollment")
                admissions[key][agent] = (ship, revision, side, tick)
                admitted_bindings[key].add((ship, revision))
            elif row["kind"] == "policy_controller_retired":
                payload = row["payload"]
                controller = payload["ship_id"], payload["binding_revision"]
                if controller in retirements[key]:
                    raise ValueError("duplicate policy controller retirement")
                retirements[key][controller] = row["tick"]
            elif row["kind"] == "combat_hit":
                record_combat_hit(combat_by_episode[key], row["payload"])
            elif row["kind"] == "group_reward":
                event = row["payload"]
                if event["group_kind"] in ("immediate", "terminal"):
                    rewards[key, event["team_id"]].add(row["tick"], event["ppo_reward"])
                elif event["group_kind"] == "end_episode":
                    team = event["team_id"]
                    if team in ended_teams[key]:
                        raise ValueError("duplicate group terminal event")
                    ended_teams[key][team] = row["tick"]

    if not all_terminals:
        raise ValueError("reward journal has no completed episodes")
    # The terminal's immutable evaluation context contains the *initial*
    # physically registered cohort. The journal alone authorizes future
    # participants; an authored reinforcement reservation is not an agent.
    for episode_key, entrants in admissions.items():
        final = all_terminals.get(episode_key)
        if final is None:
            raise ValueError("native policy admission belongs to an unfinished episode")
        context = final["evaluation_context"]
        groups = context["group_agents_by_team"]
        original = {int(agent) for members in groups.values() for agent in members}
        for agent, (ship, revision, side, joined) in entrants.items():
            team = context["group_team_by_side"].get(str(side))
            if (team is None or str(team) not in groups or
                    agent in original or joined > final["tick"] or
                    (ship, revision) in retirements[episode_key] and
                    retirements[episode_key][ship, revision] < joined):
                raise ValueError("native policy admission violates episode group or physical time")
            original.add(agent)
            groups[str(team)].append(agent)
        for members in groups.values():
            members.sort()
    if not terminals:
        raise ValueError("capture contains no completed episodes for the selected policy manifest")
    # The journal parser has already checked the nonnegative, nondecreasing
    # source ticks for every world and forbids events after EpisodeFinal.
    # Validate each team's owner and numeric amounts without rebuilding one
    # Python reward tuple per event; bisect_right uses the original tick order.
    reward_events = 0
    reward_payload_bytes = 0
    for (episode_key, team), events in rewards.items():
        final = all_terminals.get(episode_key)
        if final is None:
            raise ValueError("group reward belongs to an unfinished episode")
        groups = final["evaluation_context"]["group_agents_by_team"]
        if str(team) not in groups:
            raise ValueError("group reward belongs to an unknown episode team")
        if (events.ticks and events.ticks[-1] > final["tick"]) or not np.isfinite(
            np.frombuffer(events.amounts, dtype=np.float64)
        ).all():
            raise ValueError("invalid or out-of-order group reward journal")
        reward_events += len(events.ticks)
        reward_payload_bytes += (
            len(events.ticks) * events.ticks.itemsize +
            len(events.amounts) * events.amounts.itemsize
        )
    if stats is not None:
        stats["reward_journal_events"] = stats.get("reward_journal_events", 0) + reward_events
        # Logical numeric payload, not Python allocator or peak RSS usage.
        stats["reward_journal_payload_bytes"] = (
            stats.get("reward_journal_payload_bytes", 0) + reward_payload_bytes
        )
    # Resolve learner ownership only for episodes whose terminal boundaries
    # are being processed, rather than retaining IDs for the whole run.
    learner_agents_by_episode = {}
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
        if (generation is not None and all_episodes_match and
                has_opponent_slots and len(rows) > 3):
            selected = (
                rows["model_slot"] == next(iter(learner_slots))
                if len(learner_slots) == 1 else
                np.isin(rows["model_slot"], list(learner_slots))
            )
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
            # Avoid mask/repacking overhead for 1v1 and other tiny mixed
            # frames. Copy only the learner row when it is retained.
            copy_full_frame = all_episodes_match and not has_opponent_slots
            owned_frame = rows.copy() if copy_full_frame else None
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
                # Every boundary (including an opponent retirement) must
                # provide a finite observation, even if the learner will
                # not retain those features for optimization.
                if not np.isfinite(row["observations"]).all():
                    raise ValueError("nonfinite policy boundary observation")
                if key not in learner_agents_by_episode:
                    context = terminals[key]["evaluation_context"]
                    learner_agents_by_episode[key] = {
                        int(member)
                        for team, members in context["group_agents_by_team"].items()
                        if context["model_slots_by_team"][team] in learner_slots
                        for member in members
                    }
                if agent in learner_agents_by_episode[key]:
                    frame[agent] = row if owned_frame is not None else row.copy()
                else:
                    frame[agent] = {
                        "ship": int(row["ship"]),
                        "binding_revision": int(row["binding_revision"]),
                    }
                    if stats is not None:
                        stats["compact_opponent_boundaries"] = (
                            stats.get("compact_opponent_boundaries", 0) + 1
                        )
                if tick == terminals[key]["tick"]:
                    ready.add(key)
        else:
            # An inference batch containing no ML-Agents controllers uses a
            # normal action frame without behavior probabilities. Preserve
            # those genuine opponent decisions; only ML-Agents slots must
            # carry sampled policy data for PPO.
            has_behavior = "continuous_log_probs" in rows.dtype.names
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
                if models[int(row["model_slot"])]["backend"] == "mlagents" and not has_behavior:
                    raise ValueError("ML-Agents decision lacks captured behavior statistics")
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
            rollout = _validate_completed_episode(
                key, ticks, episode_boundaries, terminals, ended_teams,
                retirements, rewards, learner_agents_by_episode.pop(key), journal_id,
                admissions.get(key, {}),
            )
            if stats is not None:
                if key in admissions:
                    enrolled = admissions[key]
                    decided = {agent for frame in ticks.values() for agent in frame}
                    stats["dynamic_policy_episodes"] = (
                        stats.get("dynamic_policy_episodes", 0) + 1
                    )
                    stats["dynamic_policy_admissions"] = (
                        stats.get("dynamic_policy_admissions", 0) + len(enrolled)
                    )
                    stats["dynamic_policy_admissions_without_decisions"] = (
                        stats.get("dynamic_policy_admissions_without_decisions", 0) +
                        len(enrolled.keys() - decided)
                    )
                    stats["dynamic_policy_decision_phases_max"] = max(
                        stats.get("dynamic_policy_decision_phases_max", 0),
                        len({tick % terminals[key]["evaluation_context"]["decision_period"]
                             for tick in ticks}),
                    )
                accumulate_training_outcome(
                    stats, terminals[key], rollout[5], learner_slots,
                    combat_by_episode.get(key, {}),
                )
            # The rollout retains its own reward-list references. Drop the
            # journal's completed-episode indices before ingesting the next
            # episode, without weakening post-terminal identity checks.
            combat_by_episode.pop(key, None)
            ended_teams.pop(key, None)
            retirements.pop(key, None)
            # All future identity checks for this episode have already
            # completed; release the per-episode admission index too.
            admitted_bindings.pop(key, None)
            admissions.pop(key, None)
            for team in set(rollout[1].values()):
                rewards.pop((key, team), None)
            yield rollout

    if completed != terminals.keys():
        raise ValueError("completed episode missing decisions or policy boundary")


def _validate_completed_episode(
    key, episode_ticks, episode_boundaries, terminals, ended_teams,
    retirements, rewards, learning_agents, journal_id, admissions=None,
):
    if admissions:
        return _validate_dynamic_episode(
            key, episode_ticks, episode_boundaries, terminals, ended_teams,
            retirements, rewards, learning_agents, journal_id, admissions,
        )
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
                any((row["ship"], row["binding_revision"]) != identities.get(agent)
                    for agent, row in frame.items())):
            raise ValueError("policy boundary observations disagree with controller lifetimes")
    if not learning_agents:
        raise ValueError("completed episode has no decisions from the learner slot")
    return (journal_id, *key), expected, ticks, closing, episode_boundaries, {
        team: rewards[key, team] for team in set(expected.values())
    }, learning_agents


def _validate_dynamic_episode(
    key, episode_ticks, episode_boundaries, terminals, ended_teams,
    retirements, rewards, learning_agents, journal_id, admissions,
):
    """Validate the *real* variable cohort and each controller's own clock.

    Queen children and reinforcement policies join after tick zero. Their
    DecisionRequester clocks start at physical binding, not at the world's
    first inference tick. The journal, not the finished group size, proves
    those admission times and source controller identities.
    """
    final = terminals[key]
    context = final["evaluation_context"]
    terminal_tick = final["tick"]
    period = context["decision_period"]
    teams = {
        int(agent): int(team)
        for team, members in context["group_agents_by_team"].items()
        for agent in members
    }
    if period <= 0 or not episode_ticks or not teams:
        raise ValueError("dynamic MA-POCA episode has no decision interval")
    if len(teams) != sum(len(members) for members in
                        context["group_agents_by_team"].values()):
        raise ValueError("duplicate native policy group identity")
    starts = {agent: (admissions[agent][3] if agent in admissions else 0)
              for agent in teams}
    ticks = list(episode_ticks.items())
    if any(a >= b for (a, _), (b, _) in zip(ticks, ticks[1:])):
        raise ValueError("non-monotonic dynamic policy decision ticks")

    by_agent = defaultdict(list)
    identities = {}
    for tick, frame in ticks:
        if tick < 0 or tick >= terminal_tick:
            raise ValueError("dynamic decision outside completed episode")
        for agent, row in frame.items():
            if agent not in teams or tick < starts[agent]:
                raise ValueError("decision by a not-yet-admitted native controller")
            identity = int(row["ship"]), int(row["binding_revision"])
            if agent in admissions and identity != admissions[agent][:2]:
                raise ValueError("dynamic policy binding differs from admission receipt")
            if agent in identities and identities[agent] != identity:
                raise ValueError("reused policy group identity changed physical binding")
            if int(row["team"]) != teams[agent]:
                raise ValueError("dynamic policy decision changed team")
            identities[agent] = identity
            by_agent[agent].append(tick)

    # A controller can die before its first neural decision. Its authentic
    # terminal/retirement boundary still identifies the physical binding.
    # Read it before determining whether a decision was due.
    for frame in episode_boundaries.values():
        for agent, row in frame.items():
            if agent not in teams:
                raise ValueError("unknown controller in dynamic policy boundary")
            identity = int(row["ship"]), int(row["binding_revision"])
            if agent in admissions and identity != admissions[agent][:2]:
                raise ValueError("dynamic policy boundary disagrees with enrollment")
            if agent in identities and identities[agent] != identity:
                raise ValueError("policy identity changed across decision and boundary")
            identities[agent] = identity
    # A controller can be born and die before requesting its first action.
    # It still participates in the group but contributes no PPO trajectory.
    closing = {}
    for agent in teams:
        if agent in admissions:
            identities.setdefault(agent, admissions[agent][:2])
        end = retirements[key].get(identities[agent], terminal_tick) if agent in identities else terminal_tick
        start = starts[agent]
        if not start <= end <= terminal_tick:
            raise ValueError("policy controller retired before joining its group")
        closing[agent] = end
        decisions = by_agent.get(agent, ())
        first_due = start + period - 1
        if not decisions:
            if end > first_due:
                raise ValueError("active group agent omitted its due policy decision")
            continue
        if decisions[0] != first_due or decisions[-1] >= end or (
            any(b - a != period for a, b in zip(decisions, decisions[1:]))
        ) or not 0 < end - decisions[-1] <= period:
            raise ValueError("dynamic policy decision schedule differs from binding clock")

    # A terminal boundary and one full-cohort snapshot at each retirement
    # are emitted by the shared Level, even when controllers have independent
    # inference phases. Never invent inactive observations in Python.
    closing_ticks = {end for end in closing.values() if end < terminal_tick}
    required_ticks = closing_ticks | {terminal_tick}
    if episode_boundaries.keys() != required_ticks:
        raise ValueError("dynamic policy boundary misses a retirement or terminal")
    for tick, frame in episode_boundaries.items():
        active = {agent for agent in teams if starts[agent] <= tick <= closing[agent]}
        if frame.keys() != active:
            raise ValueError("dynamic policy boundary disagrees with physical cohort")
        for agent, row in frame.items():
            identity = int(row["ship"]), int(row["binding_revision"])
            if agent in identities and identity != identities[agent]:
                raise ValueError("dynamic policy terminal changed physical controller")
            identities.setdefault(agent, identity)

    expected_teams = set(teams.values())
    if ended_teams[key] != dict.fromkeys(expected_teams, terminal_tick):
        raise ValueError("native dynamic group did not end both physical teams")
    if not any(agent in learning_agents for agent in by_agent):
        raise ValueError("native dynamic episode contains no learner decision")
    return (
        (journal_id, *key), teams, ticks, closing, episode_boundaries,
        {team: rewards[key, team] for team in expected_teams},
        learning_agents, starts,
    )


def _dynamic_trajectories(rollout, horizon, behavior_id):
    """Reconstruct real per-controller decision intervals for late agents.

    A Queen child or deployed reinforcement has its own fixed decision
    phase. A global-frame zip would truncate its action every time another
    agent requests a decision. Keep the unchanged, faster common aligned
    path for episodes with no late admissions.
    """
    from mlagents.trainers.trajectory import AgentExperience, AgentStatus, Trajectory
    from mlagents.trainers.torch_entities.action_log_probs import LogProbsTuple
    from mlagents_envs.base_env import ActionTuple

    key, teams, frames, closing, boundaries, rewards, learning_agents, starts = rollout
    terminal_tick = max(boundaries)
    prefixes = {team: events.prefix() for team, events in rewards.items()}
    decision_rows = defaultdict(list)
    decision_ticks = defaultdict(list)
    for tick, frame in frames:
        for agent, row in frame.items():
            if agent in learning_agents:
                decision_ticks[agent].append(tick)
                decision_rows[agent].append(row)

    latest = {}
    next_index = defaultdict(int)
    experiences = defaultdict(list)
    previous = {}
    initial_action = np.zeros(6, dtype=np.int32)
    # Maintain only *living* learning teammates in source group-ID order.
    # A Queen can create thousands of sequential minions without thousands
    # of concurrent ships: rescanning every historical member at each
    # decision would make trajectory assembly quadratic in total births.
    entering = sorted((starts[agent], agent, team) for agent, team in teams.items()
                      if agent in learning_agents)
    leaving = sorted((closing[agent], agent, team) for agent, team in teams.items()
                     if agent in learning_agents)
    entry_ticks = [row[0] for row in entering]
    team_members = defaultdict(list)
    entered = left = 0

    for tick, frame in frames:
        while entered < len(entering) and entering[entered][0] <= tick:
            _, agent, team = entering[entered]
            if closing[agent] > tick:
                insort(team_members[team], agent)
            entered += 1
        while left < len(leaving) and leaving[left][0] <= tick:
            _, agent, team = leaving[left]
            members = team_members[team]
            index = bisect_left(members, agent)
            if index < len(members) and members[index] == agent:
                members.pop(index)
            left += 1
        latest.update(frame)
        # Multiple same-phase agents share the identical teammate state.
        # Cache only for this decision tick; older frames are never held
        # as a second cross-episode observation store.
        group_status_cache = {}
        for agent, row in frame.items():
            if agent not in learning_agents:
                continue
            slot = next_index[agent]
            next_index[agent] += 1
            times_for_agent = decision_ticks[agent]
            next_tick = (
                times_for_agent[slot + 1]
                if slot + 1 < len(times_for_agent) else closing[agent]
            )
            done = next_tick == closing[agent]
            team = teams[agent]
            members = team_members[team]
            actions = ActionTuple(
                row["sampled_continuous"], row["discrete"].astype(np.int32),
            )
            cache_key = team, next_tick
            statuses = group_status_cache.get(cache_key)
            if statuses is None:
                statuses = {
                    mate: AgentStatus(
                        [latest[mate]["observations"]], 0.0,
                        ActionTuple(
                            latest[mate]["sampled_continuous"],
                            latest[mate]["discrete"].astype(np.int32),
                        ), closing[mate] <= next_tick,
                    )
                    for mate in members if
                    starts[mate] <= tick < closing[mate] and mate in latest
                }
                group_status_cache[cache_key] = statuses
            group_status = [
                statuses[mate] for mate in members
                if mate != agent and mate in statuses
            ]
            masks = [np.array([False, not bit]) for bit in row["fire_allowed"]]
            masks.append(row["special_allowed"] == 0)
            reward_ticks, reward_amounts = prefixes[team]
            reward = float(
                reward_amounts[bisect_right(reward_ticks, next_tick)] -
                reward_amounts[bisect_right(reward_ticks, tick)]
            )
            steps = experiences[agent]
            steps.append(AgentExperience(
                [row["observations"]], 0.0, done, actions,
                LogProbsTuple(
                    row["continuous_log_probs"], row["discrete_log_probs"],
                ), masks, previous.get(agent, initial_action), False, None,
                group_status, reward,
            ))
            previous[agent] = actions.discrete
            if not done and len(steps) < horizon:
                continue

            next_obs = (
                boundaries[next_tick][agent]["observations"]
                if done else decision_rows[agent][slot + 1]["observations"]
            )
            next_group = []
            ending_frame = boundaries.get(next_tick) if done else None
            # Most next actions are only one policy period away. Start from
            # the currently live cohort and add only real admissions in
            # (tick, next_tick], rather than scanning every historical child.
            future = [
                mate for mate in members if
                next_tick == terminal_tick or closing[mate] > next_tick
            ]
            for _, mate, mate_team in entering[
                bisect_right(entry_ticks, tick):bisect_right(entry_ticks, next_tick)
            ]:
                if mate_team == team and (
                    next_tick == terminal_tick or closing[mate] > next_tick
                ):
                    future.append(mate)
            future.sort()
            for mate in future:
                if mate == agent:
                    continue
                if ending_frame is not None:
                    observation = ending_frame.get(mate)
                else:
                    indices = decision_ticks[mate]
                    found = bisect_right(indices, next_tick) - 1
                    observation = (decision_rows[mate][found]
                                   if found >= 0 else None)
                if observation is not None:
                    next_group.append([observation["observations"]])
            yield Trajectory(
                steps, [next_obs], next_group,
                f"{key[0]}:{key[1]}:{key[2]}:{agent}", behavior_id,
            )
            experiences[agent] = []


def trajectories(rollouts, horizon, behavior_id):
    from mlagents.trainers.trajectory import AgentExperience, AgentStatus, Trajectory
    from mlagents.trainers.torch_entities.action_log_probs import LogProbsTuple
    from mlagents_envs.base_env import ActionTuple

    for rollout in rollouts:
        if len(rollout) == 8:
            yield from _dynamic_trajectories(rollout, horizon, behavior_id)
            continue
        key, teams, frames, closing, boundaries, rewards, learning_agents = rollout
        terminal_tick = max(boundaries)
        reward_prefix = {}
        for team, events in rewards.items():
            reward_prefix[team] = events.prefix()
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
            # AgentStatus is an immutable ML-Agents named tuple. Share its
            # observation/action references within this decision interval
            # rather than building an O(team_size**2) number of identical
            # status objects for each agent's centralized critic input.
            group_status_cache = {} if len(actions) > 4 else None
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
                if group_status_cache is None:
                    group_status = [
                        AgentStatus([frame[mate]["observations"]], 0.0, actions[mate], closing[mate] <= end)
                        for mate in mates
                    ]
                else:
                    interval = (team, end)
                    statuses = group_status_cache.get(interval)
                    if statuses is None:
                        statuses = {
                            member: AgentStatus(
                                [frame[member]["observations"]], 0.0,
                                actions[member], closing[member] <= end,
                            )
                            for member in members_by_team[team]
                        }
                        group_status_cache[interval] = statuses
                    # Retain the authored teammate order and exclude self.
                    # The status records are immutable, but each experience
                    # still owns a distinct group list for ML-Agents.
                    group_status = [statuses[mate] for mate in mates]
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


def bundle_captures(receipt_path, manifest_sha, checkpoint_sha, learner_slot,
                    expected_sha=None, partitions=None):
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
    # Native collectors allocate independent contiguous global world-index
    # windows from the same source run. Never double-train an overlapping
    # window just because the physical collectors generated different
    # per-execution run UUIDs. Older receipts without partition metadata
    # retain their original supported import contract.
    partition = receipt.get("partition")
    if partition is not None:
        if not isinstance(partition, dict):
            raise ValueError("native rollout partition must be an object")
        source = partition.get("source_run_id")
        start = partition.get("world_start")
        count = partition.get("world_count")
        if (not isinstance(source, str) or not source.strip() or
                type(start) is not int or start < 0 or
                type(count) is not int or not 1 <= count <= 1024 or
                start + count > (1 << 63)):
            raise ValueError("native rollout partition has invalid source or world range")
        if receipt.get("rollout_worlds") != count:
            raise ValueError("native rollout partition does not match its collected world count")
        if partitions is not None:
            for prior_start, prior_end in partitions[source]:
                if start < prior_end and prior_start < start + count:
                    raise ValueError("overlapping native rollout partitions from the same source run")
            partitions[source].append((start, start + count))
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
    process_cpu_started_at = time.process_time()
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("capture", "journal"):
        parser.add_argument(name, type=Path, nargs="?")
    for name in ("manifest", "checkpoint", "config", "output"):
        parser.add_argument(name, type=Path)
    parser.add_argument("--learner-slot", type=int, help="train only on this manifest slot")
    parser.add_argument("--refresh-opponent-slot", type=int,
                        help="publish the newly trained checkpoint as this opponent slot for the next round")
    parser.add_argument(
        "--torch-threads", type=int,
        help="fixed CPU intra-op thread count for the optimizer, independent of rollout workers",
    )
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

    if args.torch_threads is not None and not 1 <= args.torch_threads <= 256:
        parser.error("--torch-threads must be between 1 and 256")
    import torch
    if args.torch_threads is not None:
        torch.set_num_threads(args.torch_threads)
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
    if args.refresh_opponent_slot is not None:
        if (args.learner_slot is None or
                args.refresh_opponent_slot == args.learner_slot or
                not 0 <= args.refresh_opponent_slot < (1 << 32)):
            parser.error("opponent refresh requires a distinct explicit uint32 learner and opponent slot")
        candidates = [
            item for item in primary_manifest.get("models", [])
            if item.get("slot") == args.refresh_opponent_slot
        ]
        if len(candidates) != 1 or candidates[0].get("backend") != "mlagents":
            parser.error("refreshed opponent must be an existing MA-POCA model slot")
    with args.checkpoint.open("rb") as source:
        input_sha = stream_sha256(source)
        source.seek(0)
        checkpoint = torch.load(source, map_location="cpu", weights_only=True)
        source.seek(0)
        if stream_sha256(source) != input_sha:
            raise ValueError("optimizer input checkpoint changed while loading")
    partitions = defaultdict(list)
    for receipt, digest in bundles:
        inputs.extend(
            (capture, journal, primary_path)
            for capture, journal in bundle_captures(
                receipt, primary_manifest_sha, input_sha, args.learner_slot, digest,
                partitions=partitions,
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
    processing_cpu_started = time.process_time()
    terminal_steps = 0
    episode_keys = set()
    opponent_steps = 0
    capture_stats = {}
    capture_read_wall_seconds = 0.0
    trajectory_ingest_wall_seconds = 0.0
    capture_read_cpu_seconds = 0.0
    trajectory_ingest_cpu_seconds = 0.0
    for capture, journal, path in inputs:
        manifest, manifest_sha = manifests[path]
        rollouts = iter(completed_rollouts(
            capture, journal, manifest, manifest_sha, input_sha, args.learner_slot,
            stats=capture_stats,
        ))
        # Measure at episode boundaries rather than on every decision.
        # The capture phase includes journal parsing, frame decoding and
        # cohort validation; the other phase includes trajectory assembly
        # and ML-Agents buffer ingestion. The existing total includes both.
        while True:
            capture_started = time.perf_counter()
            capture_cpu_started = time.process_time()
            try:
                rollout = next(rollouts)
            except StopIteration:
                capture_read_wall_seconds += time.perf_counter() - capture_started
                capture_read_cpu_seconds += time.process_time() - capture_cpu_started
                break
            capture_read_wall_seconds += time.perf_counter() - capture_started
            capture_read_cpu_seconds += time.process_time() - capture_cpu_started
            ingest_started = time.perf_counter()
            ingest_cpu_started = time.process_time()
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
            trajectory_ingest_wall_seconds += time.perf_counter() - ingest_started
            trajectory_ingest_cpu_seconds += time.process_time() - ingest_cpu_started
    steps = trainer.update_buffer.num_experiences
    if steps < settings.hyperparameters.batch_size:
        parser.error("capture contains fewer observed intervals than one optimizer minibatch")
    processing_wall_seconds = time.perf_counter() - processing_started
    processing_cpu_seconds = time.process_time() - processing_cpu_started
    update_started = time.perf_counter()
    update_cpu_started = time.process_time()
    if not trainer._update_policy():
        raise RuntimeError("MA-POCA did not update its policy")
    if device.type == "cuda":
        torch.cuda.synchronize(device)
    update_wall_seconds = time.perf_counter() - update_started
    update_cpu_seconds = time.process_time() - update_cpu_started
    # PyTorch allocator peaks describe this process, not total device VRAM.
    # Read once per round, never in the simulation or optimizer hot loop.
    cuda_metrics = {}
    if device.type == "cuda":
        cuda_metrics = {
            "torch_cuda_device_name": torch.cuda.get_device_name(device),
            "torch_cuda_process_peak_allocated_bytes": torch.cuda.max_memory_allocated(device),
            "torch_cuda_process_peak_reserved_bytes": torch.cuda.max_memory_reserved(device),
        }
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

    # Default to pinned frozen opponents. An explicitly scheduled self-play
    # round can replace ONE chosen opponent with the just-completed learner
    # checkpoint. Both slots then share immutable weights until the next
    # scheduled promotion. The next native rollout uses the same model worker
    # and source physics; no alternate self-play actor is introduced.
    manifest = primary_manifest
    if args.learner_slot is None:
        learner_slots = {
            entry["slot"] for entry in manifest["models"]
            if entry["backend"] == "mlagents" and entry["sha256"] == input_sha
        }
    else:
        learner_slots = {args.learner_slot}
    next_sha = file_sha256(next_checkpoint)
    # A published checkpoint lineage must be transferable to a different
    # machine (and still runnable after old attempt directories are moved).
    # Source opponent paths can point into older rounds or even other Windows
    # volumes; snapshot the verified frozen bytes beside this manifest.
    # Content-addressed hard links avoid both duplicate disk storage and
    # expensive reads on the common same-filesystem path. A cross-volume
    # copy is measured and verified, never silently substituted.
    frozen_artifacts = {}
    frozen_snapshot_copies = 0
    frozen_snapshot_copy_bytes = 0
    frozen_snapshot_started = time.perf_counter()
    for entry in manifest["models"]:
        if (entry["slot"] in learner_slots or
                entry["slot"] == args.refresh_opponent_slot):
            entry["path"] = next_checkpoint.name
            entry["sha256"] = next_sha
            continue
        expected = entry["sha256"]
        if (not isinstance(expected, str) or len(expected) != 64 or
                any(char not in "0123456789abcdef" for char in expected)):
            raise ValueError("frozen opponent has an invalid checkpoint digest")
        old_artifact = (args.manifest.parent / entry["path"]).resolve(strict=True)
        if not old_artifact.is_file() or file_sha256(old_artifact) != expected:
            raise ValueError(f"opponent artifact changed: slot {entry['slot']}")
        if expected not in frozen_artifacts:
            directory = args.output / "opponents"
            directory.mkdir(exist_ok=True)
            destination = directory / (expected + ".model")
            if destination.exists():
                raise ValueError("frozen opponent output was already published")
            try:
                os.link(old_artifact, destination)
            except OSError:
                shutil.copyfile(old_artifact, destination)
                frozen_snapshot_copies += 1
                frozen_snapshot_copy_bytes += destination.stat().st_size
            # Hard links and copied files are both pinned to the manifest
            # bytes, including if an external source is modified while read.
            if file_sha256(destination) != expected:
                raise ValueError(f"frozen opponent bytes changed during publication: {entry['slot']}")
            frozen_artifacts[expected] = destination
        entry["path"] = "opponents/" + expected + ".model"
    frozen_snapshot_wall_seconds = time.perf_counter() - frozen_snapshot_started
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
        "refreshed_opponent_slot": args.refresh_opponent_slot,
        "refreshed_opponent_from_checkpoint_sha256":
            next_sha if args.refresh_opponent_slot is not None else None,
        "next_checkpoint": str(next_checkpoint.resolve()),
        "next_checkpoint_sha256": next_sha,
        "next_manifest": str(next_manifest.resolve()),
        "next_manifest_sha256": file_sha256(next_manifest),
        "frozen_opponent_snapshots": len(frozen_artifacts),
        "frozen_opponent_snapshot_fallback_copies": frozen_snapshot_copies,
        "frozen_opponent_snapshot_copy_bytes": frozen_snapshot_copy_bytes,
        "frozen_opponent_snapshot_wall_seconds": frozen_snapshot_wall_seconds,
        "optimizer_device": str(device),
        "optimizer_torch_intraop_threads": torch.get_num_threads(),
        "optimizer_torch_interop_threads": torch.get_num_interop_threads(),
        **cuda_metrics,
        "optimizer_input_steps": steps,
        "trajectory_processing_wall_seconds": processing_wall_seconds,
        "trajectory_processing_cpu_seconds": processing_cpu_seconds,
        "capture_read_and_validation_wall_seconds": capture_read_wall_seconds,
        "capture_read_and_validation_cpu_seconds": capture_read_cpu_seconds,
        "trajectory_assembly_and_ingest_wall_seconds": trajectory_ingest_wall_seconds,
        "trajectory_ingest_cpu_seconds": trajectory_ingest_cpu_seconds,
        "optimizer_update_wall_seconds": update_wall_seconds,
        "optimizer_update_cpu_seconds": update_cpu_seconds,
        "optimizer_steps_per_update_second": steps / update_wall_seconds if update_wall_seconds else None,
        "total_wall_seconds": time.perf_counter() - started_at,
        "total_cpu_seconds": time.process_time() - process_cpu_started_at,
        "terminal_steps": terminal_steps,
        "episodes": len(episode_keys),
        "opponent_steps": opponent_steps,
        "capture_peak_inflight_decisions": capture_stats.get("peak_inflight_decisions", 0),
        "capture_peak_inflight_episodes": capture_stats.get("peak_inflight_episodes", 0),
        "capture_compact_opponent_decisions": capture_stats.get("compact_opponent_decisions", 0),
        "capture_compact_opponent_boundaries": capture_stats.get("compact_opponent_boundaries", 0),
        "dynamic_policy_episodes": capture_stats.get("dynamic_policy_episodes", 0),
        "dynamic_policy_admissions": capture_stats.get("dynamic_policy_admissions", 0),
        "dynamic_policy_admissions_without_decisions":
            capture_stats.get("dynamic_policy_admissions_without_decisions", 0),
        "dynamic_policy_decision_phases_max":
            capture_stats.get("dynamic_policy_decision_phases_max", 0),
        "reward_journal_events": capture_stats.get("reward_journal_events", 0),
        "reward_journal_payload_bytes": capture_stats.get("reward_journal_payload_bytes", 0),
        "training_outcomes": {
            **capture_stats.get("training_outcomes", {}),
            "mean_episode_seconds": (
                capture_stats["training_outcomes"]["total_episode_seconds"] /
                capture_stats["training_outcomes"]["episodes"]
            ) if capture_stats.get("training_outcomes") else None,
            "head_to_head_win_rate": (
                capture_stats["training_outcomes"]["learner_wins"] /
                capture_stats["training_outcomes"]["head_to_head_episodes"]
            ) if (capture_stats.get("training_outcomes", {}).get("head_to_head_episodes", 0)) else None,
        },
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
