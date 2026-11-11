"""Aggregate native Bees learning outcomes without coupling metrics to MA-POCA.

These totals use authored side/team identities from the reward journal. A
learner-versus-opponent result exists only when their policy slots differ.
"""

import numpy as np


def record_combat_hit(combat_by_side, hit):
    """Accumulate the durable physical combat event; never retain hit rows."""
    attacker = int(hit["attacker_side"])
    target = int(hit["target_side"])
    damage = int(hit["applied_damage"])
    tsv_loss = int(hit["tsv_loss"])
    if damage < 0 or tsv_loss < 0:
        raise ValueError("invalid negative combat hit measurement")
    kind = "friendly" if attacker == target else "enemy"
    counts = combat_by_side[attacker]
    counts[f"{kind}_hits"] += 1
    counts[f"{kind}_damage"] += damage
    counts[f"{kind}_tsv_loss"] += tsv_loss
    if hit["killed"]:
        counts[f"{kind}_kills"] += 1


def accumulate_training_outcome(
    stats, terminal, group_rewards, learner_slots, combat_by_side,
):
    """Summarize authored outcomes by policy owner, never by fixed faction.

    Training wins/losses are meaningful only when exactly one side uses
    the learner checkpoint. Self-play is counted separately rather than
    mislabelled as either a learner win or an opponent loss.
    """
    context = terminal["evaluation_context"]
    payload = terminal["payload"]
    bee_side, human_side = int(context["bee_side"]), int(context["human_side"])
    if bee_side == human_side:
        raise ValueError("training outcome has identical factions")
    team_by_side = context["group_team_by_side"]
    slots_by_team = context["model_slots_by_team"]
    bee_team = int(team_by_side[str(bee_side)])
    human_team = int(team_by_side[str(human_side)])
    if bee_team == human_team:
        raise ValueError("training outcome has duplicate model teams")
    bee_learner = slots_by_team[str(bee_team)] in learner_slots
    human_learner = slots_by_team[str(human_team)] in learner_slots
    if not (bee_learner or human_learner):
        raise ValueError("training outcome contains no learner policy")

    winner = int(payload["winner_side"])
    if winner not in (0, bee_side, human_side):
        raise ValueError("training outcome names an unregistered winner")
    duration = float(payload["duration_seconds"])
    if not np.isfinite(duration) or duration < 0:
        raise ValueError("training outcome has invalid duration")
    values = {}
    for name in (
        "bee_retained_mined_tsv", "human_retained_mined_tsv",
        "bee_retained_reward", "human_retained_reward",
    ):
        value = float(payload[name])
        if not np.isfinite(value):
            raise ValueError(f"training outcome has invalid {name}")
        values[name] = value

    counts = stats.get("training_outcomes")
    if counts is None:
        counts = {
            "episodes": 0, "head_to_head_episodes": 0,
            "self_play_episodes": 0, "learner_wins": 0,
            "learner_losses": 0, "learner_draws": 0,
            "learner_as_bee_episodes": 0, "learner_as_human_episodes": 0,
            "bee_wins": 0, "human_wins": 0, "draws": 0,
            "timed_out_episodes": 0, "total_episode_seconds": 0.0,
            "bee_retained_mined_tsv": 0.0, "human_retained_mined_tsv": 0.0,
            "bee_retained_reward": 0.0, "human_retained_reward": 0.0,
            "bee_ppo_reward_sum": 0.0, "human_ppo_reward_sum": 0.0,
            "learner_retained_mined_tsv": 0.0, "opponent_retained_mined_tsv": 0.0,
            "learner_retained_reward": 0.0, "opponent_retained_reward": 0.0,
            "learner_ppo_reward_sum": 0.0, "opponent_ppo_reward_sum": 0.0,
            **{
                f"{owner}_{name}": 0
                for owner in ("bee", "human", "learner", "opponent")
                for name in (
                    "enemy_hits", "enemy_damage", "enemy_tsv_loss", "enemy_kills",
                    "friendly_hits", "friendly_damage", "friendly_tsv_loss",
                    "friendly_kills",
                )
            },
        }
        stats["training_outcomes"] = counts
    counts["episodes"] += 1
    counts["total_episode_seconds"] += duration
    counts["timed_out_episodes"] += int(payload["timed_out"])
    counts["bee_wins"] += int(winner == bee_side)
    counts["human_wins"] += int(winner == human_side)
    counts["draws"] += int(winner == 0)
    for name, value in values.items():
        counts[name] += value
    # Side totals remain meaningful even in self-play, when "opponent"
    # cannot be defined as a distinct policy checkpoint.
    ppo_by_side = {
        bee_side: sum(float(amount) for _, amount in group_rewards[bee_team]),
        human_side: sum(float(amount) for _, amount in group_rewards[human_team]),
    }
    if any(not np.isfinite(value) for value in ppo_by_side.values()):
        raise ValueError("training outcome has nonfinite PPO group reward")
    counts["bee_ppo_reward_sum"] += ppo_by_side[bee_side]
    counts["human_ppo_reward_sum"] += ppo_by_side[human_side]
    for side, owner in ((bee_side, "bee"), (human_side, "human")):
        for name, value in combat_by_side.get(side, {}).items():
            counts[f"{owner}_{name}"] += value

    if bee_learner and human_learner:
        counts["self_play_episodes"] += 1
        return
    counts["head_to_head_episodes"] += 1
    counts["learner_as_bee_episodes"] += int(bee_learner)
    counts["learner_as_human_episodes"] += int(human_learner)
    learner_side = bee_side if bee_learner else human_side
    opponent_side = human_side if bee_learner else bee_side
    counts["learner_wins"] += int(winner == learner_side)
    counts["learner_draws"] += int(winner == 0)
    counts["learner_losses"] += int(winner not in (0, learner_side))
    counts["learner_ppo_reward_sum"] += ppo_by_side[learner_side]
    counts["opponent_ppo_reward_sum"] += ppo_by_side[opponent_side]
    for side, owner in ((learner_side, "learner"), (opponent_side, "opponent")):
        for name, value in combat_by_side.get(side, {}).items():
            counts[f"{owner}_{name}"] += value
    for kind in ("retained_mined_tsv", "retained_reward"):
        counts[f"learner_{kind}"] += values[
            f"{'bee' if bee_learner else 'human'}_{kind}"
        ]
        counts[f"opponent_{kind}"] += values[
            f"{'human' if bee_learner else 'bee'}_{kind}"
        ]

