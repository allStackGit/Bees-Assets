"""Authoritative offline evaluator for Bees continual-learning RL candidates.

Runs two immutable ONNX policies against each other through the dedicated ML-Agents
combat executable. Candidate promotion is based on real episode outcomes emitted by
RlOneVsOneEvaluationSideChannel, never on shaped PPO reward.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
import time
import uuid
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any, Callable, Dict, Iterable, List, Mapping, Optional, Sequence, Tuple

import numpy as np

from bees_continual_learning import (
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    load_config,
    sha256_file,
)

EVALUATION_CHANNEL_ID = uuid.UUID("7ca0e8e5-47f7-49ce-b44a-738ae7f1ad15")
EVALUATION_PROTOCOL_VERSION = 1
EVALUATION_MODE_FLAG = "--bees-rl-evaluator"
TEAM_PATTERN = re.compile(r"(?:\?|&)team=(\d+)(?:&|$)")
DEFAULT_MAX_ENVIRONMENT_STEPS_PER_MATCH = 200_000


class EvaluationError(ContinualLearningError):
    """Raised when an authoritative candidate evaluation cannot be completed safely."""


@dataclass(frozen=True)
class EpisodeResult:
    episode_number: int
    bee_team_id: int
    human_team_id: int
    winning_side: int
    winning_team_id: int
    timed_out: bool
    duration_seconds: float
    bee_starting_tsv: int
    bee_final_tsv: int
    human_starting_tsv: int
    human_final_tsv: int
    bee_shots: int
    bee_hits: int
    bee_damage: int
    human_shots: int
    human_hits: int
    human_damage: int


@dataclass(frozen=True)
class MatchSummary:
    matches: int
    wins: int
    losses: int
    draws: int
    timeouts: int
    total_duration_seconds: float
    candidate_starting_tsv: int = 0
    candidate_final_tsv: int = 0
    candidate_shots: int = 0
    candidate_hits: int = 0
    candidate_damage: int = 0
    opponent_starting_tsv: int = 0
    opponent_final_tsv: int = 0
    opponent_shots: int = 0
    opponent_hits: int = 0
    opponent_damage: int = 0
    candidate_inference_calls: int = 0
    candidate_inference_total_seconds: float = 0.0
    candidate_inference_max_seconds: float = 0.0
    opponent_inference_calls: int = 0
    opponent_inference_total_seconds: float = 0.0
    opponent_inference_max_seconds: float = 0.0
    telemetry_validated: bool = False

    @property
    def score_rate(self) -> float:
        if self.matches <= 0:
            return 0.0
        return (self.wins + 0.5 * self.draws) / self.matches

    @property
    def win_rate(self) -> float:
        return self.wins / self.matches if self.matches > 0 else 0.0

    @property
    def timeout_rate(self) -> float:
        return self.timeouts / self.matches if self.matches > 0 else 0.0

    @property
    def average_duration_seconds(self) -> float:
        return self.total_duration_seconds / self.matches if self.matches > 0 else 0.0

    @property
    def candidate_inference_average_milliseconds(self) -> float:
        if self.candidate_inference_calls <= 0:
            return 0.0
        return 1000.0 * self.candidate_inference_total_seconds / self.candidate_inference_calls

    @property
    def candidate_inference_max_milliseconds(self) -> float:
        return 1000.0 * self.candidate_inference_max_seconds

    @property
    def opponent_inference_average_milliseconds(self) -> float:
        if self.opponent_inference_calls <= 0:
            return 0.0
        return 1000.0 * self.opponent_inference_total_seconds / self.opponent_inference_calls

    @property
    def opponent_inference_max_milliseconds(self) -> float:
        return 1000.0 * self.opponent_inference_max_seconds

    def to_dict(self) -> Dict[str, Any]:
        return {
            "matches": self.matches,
            "wins": self.wins,
            "losses": self.losses,
            "draws": self.draws,
            "timeouts": self.timeouts,
            "score_rate": self.score_rate,
            "win_rate": self.win_rate,
            "timeout_rate": self.timeout_rate,
            "average_duration_seconds": self.average_duration_seconds,
            "candidate_starting_tsv": self.candidate_starting_tsv,
            "candidate_final_tsv": self.candidate_final_tsv,
            "candidate_shots": self.candidate_shots,
            "candidate_hits": self.candidate_hits,
            "candidate_damage": self.candidate_damage,
            "opponent_starting_tsv": self.opponent_starting_tsv,
            "opponent_final_tsv": self.opponent_final_tsv,
            "opponent_shots": self.opponent_shots,
            "opponent_hits": self.opponent_hits,
            "opponent_damage": self.opponent_damage,
            "candidate_inference_calls": self.candidate_inference_calls,
            "candidate_inference_average_milliseconds": self.candidate_inference_average_milliseconds,
            "candidate_inference_max_milliseconds": self.candidate_inference_max_milliseconds,
            "opponent_inference_calls": self.opponent_inference_calls,
            "opponent_inference_average_milliseconds": self.opponent_inference_average_milliseconds,
            "opponent_inference_max_milliseconds": self.opponent_inference_max_milliseconds,
            "telemetry_validated": self.telemetry_validated,
        }


@dataclass(frozen=True)
class CompetencyCase:
    name: str
    opponent_model_id: str
    matches: int
    minimum: float
    metric: str
    critical: bool
    env_args: Tuple[str, ...]


def _validate_episode_result(result: EpisodeResult) -> None:
    if result.episode_number <= 0:
        raise EvaluationError("Evaluation result contained a non-positive episode number.")
    if not math.isfinite(result.duration_seconds) or result.duration_seconds < 0:
        raise EvaluationError("Evaluation result contained an invalid episode duration.")
    if {result.bee_team_id, result.human_team_id} != {0, 1}:
        raise EvaluationError(
            "Evaluation result must assign Bees and Humans to teams 0 and 1 exactly."
        )
    if result.winning_team_id not in (-1, result.bee_team_id, result.human_team_id):
        raise EvaluationError(
            f"Evaluation result claimed unknown winning team {result.winning_team_id}."
        )
    if result.timed_out:
        if result.winning_team_id != -1:
            raise EvaluationError("Timed-out evaluation result must not claim a winning team.")
        if result.winning_side != 0:
            raise EvaluationError("Timed-out evaluation result must have winning side 0.")
    elif result.winning_team_id == -1 and result.winning_side != 0:
        raise EvaluationError("Drawn evaluation result must have winning side 0.")
    elif result.winning_team_id != -1 and result.winning_side == 0:
        raise EvaluationError("Winning evaluation result must identify a nonzero winning side.")

    for label, value in (
        ("bee_starting_tsv", result.bee_starting_tsv),
        ("human_starting_tsv", result.human_starting_tsv),
    ):
        if not isinstance(value, int) or isinstance(value, bool) or value <= 0:
            raise EvaluationError(f"Evaluation result {label} must be a positive integer.")
    for label, value in (
        ("bee_final_tsv", result.bee_final_tsv),
        ("human_final_tsv", result.human_final_tsv),
        ("bee_shots", result.bee_shots),
        ("bee_hits", result.bee_hits),
        ("bee_damage", result.bee_damage),
        ("human_shots", result.human_shots),
        ("human_hits", result.human_hits),
        ("human_damage", result.human_damage),
    ):
        if not isinstance(value, int) or isinstance(value, bool) or value < 0:
            raise EvaluationError(
                f"Evaluation result {label} must be a non-negative integer."
            )
    for side, hits, damage in (
        ("bee", result.bee_hits, result.bee_damage),
        ("human", result.human_hits, result.human_damage),
    ):
        if (hits == 0) != (damage == 0):
            raise EvaluationError(
                f"Evaluation result {side} hit/damage counters are inconsistent."
            )


def parse_episode_message(message: Any) -> EpisodeResult:
    """Decode one result-channel message using the exact Unity write order."""
    version = message.read_int32()
    if version != EVALUATION_PROTOCOL_VERSION:
        raise EvaluationError(
            f"Unsupported RL evaluation protocol {version}; "
            f"expected {EVALUATION_PROTOCOL_VERSION}."
        )
    result = EpisodeResult(
        episode_number=message.read_int32(),
        bee_team_id=message.read_int32(),
        human_team_id=message.read_int32(),
        winning_side=message.read_int32(),
        winning_team_id=message.read_int32(),
        timed_out=message.read_bool(),
        duration_seconds=float(message.read_float32()),
        bee_starting_tsv=message.read_int32(),
        bee_final_tsv=message.read_int32(),
        human_starting_tsv=message.read_int32(),
        human_final_tsv=message.read_int32(),
        bee_shots=message.read_int32(),
        bee_hits=message.read_int32(),
        bee_damage=message.read_int32(),
        human_shots=message.read_int32(),
        human_hits=message.read_int32(),
        human_damage=message.read_int32(),
    )
    _validate_episode_result(result)
    return result


def summarize_results(results: Sequence[EpisodeResult], candidate_team_id: int) -> MatchSummary:
    if candidate_team_id not in (0, 1):
        raise EvaluationError("Candidate team id must be 0 or 1.")
    wins = losses = draws = timeouts = 0
    duration = 0.0
    candidate_starting_tsv = candidate_final_tsv = 0
    candidate_shots = candidate_hits = candidate_damage = 0
    opponent_starting_tsv = opponent_final_tsv = 0
    opponent_shots = opponent_hits = opponent_damage = 0
    seen_episodes = set()
    for result in results:
        _validate_episode_result(result)
        if result.episode_number in seen_episodes:
            raise EvaluationError(f"Duplicate evaluation episode {result.episode_number}.")
        seen_episodes.add(result.episode_number)
        if candidate_team_id not in (result.bee_team_id, result.human_team_id):
            raise EvaluationError(
                f"Candidate team {candidate_team_id} was absent from episode {result.episode_number}."
            )
        duration += result.duration_seconds
        if result.timed_out:
            timeouts += 1
            draws += 1
        elif result.winning_team_id == candidate_team_id:
            wins += 1
        elif result.winning_team_id == -1:
            draws += 1
        else:
            losses += 1

        candidate_is_bee = result.bee_team_id == candidate_team_id
        if candidate_is_bee:
            candidate_starting_tsv += result.bee_starting_tsv
            candidate_final_tsv += result.bee_final_tsv
            candidate_shots += result.bee_shots
            candidate_hits += result.bee_hits
            candidate_damage += result.bee_damage
            opponent_starting_tsv += result.human_starting_tsv
            opponent_final_tsv += result.human_final_tsv
            opponent_shots += result.human_shots
            opponent_hits += result.human_hits
            opponent_damage += result.human_damage
        else:
            candidate_starting_tsv += result.human_starting_tsv
            candidate_final_tsv += result.human_final_tsv
            candidate_shots += result.human_shots
            candidate_hits += result.human_hits
            candidate_damage += result.human_damage
            opponent_starting_tsv += result.bee_starting_tsv
            opponent_final_tsv += result.bee_final_tsv
            opponent_shots += result.bee_shots
            opponent_hits += result.bee_hits
            opponent_damage += result.bee_damage

    return MatchSummary(
        matches=len(results),
        wins=wins,
        losses=losses,
        draws=draws,
        timeouts=timeouts,
        total_duration_seconds=duration,
        candidate_starting_tsv=candidate_starting_tsv,
        candidate_final_tsv=candidate_final_tsv,
        candidate_shots=candidate_shots,
        candidate_hits=candidate_hits,
        candidate_damage=candidate_damage,
        opponent_starting_tsv=opponent_starting_tsv,
        opponent_final_tsv=opponent_final_tsv,
        opponent_shots=opponent_shots,
        opponent_hits=opponent_hits,
        opponent_damage=opponent_damage,
        telemetry_validated=True,
    )


def _validate_match_summary(
    summary: MatchSummary,
    *,
    expected_matches: int,
) -> MatchSummary:
    if not isinstance(summary, MatchSummary):
        raise EvaluationError(
            f"Match runner returned {type(summary).__name__}; expected MatchSummary."
        )
    if summary.matches != expected_matches:
        raise EvaluationError(
            f"Match runner completed {summary.matches} matches; requested {expected_matches}."
        )
    for label, value in (
        ("matches", summary.matches),
        ("wins", summary.wins),
        ("losses", summary.losses),
        ("draws", summary.draws),
        ("timeouts", summary.timeouts),
        ("candidate_inference_calls", summary.candidate_inference_calls),
        ("opponent_inference_calls", summary.opponent_inference_calls),
    ):
        if not isinstance(value, int) or isinstance(value, bool) or value < 0:
            raise EvaluationError(f"Match summary {label} must be a non-negative integer.")
    if summary.wins + summary.losses + summary.draws != summary.matches:
        raise EvaluationError("Match summary outcome counts do not equal matches.")
    if summary.timeouts > summary.draws:
        raise EvaluationError("Match summary timeouts cannot exceed draws.")
    for label, value in (
        ("total_duration_seconds", summary.total_duration_seconds),
        ("candidate_inference_total_seconds", summary.candidate_inference_total_seconds),
        ("candidate_inference_max_seconds", summary.candidate_inference_max_seconds),
        ("opponent_inference_total_seconds", summary.opponent_inference_total_seconds),
        ("opponent_inference_max_seconds", summary.opponent_inference_max_seconds),
    ):
        if (
            not isinstance(value, (int, float))
            or isinstance(value, bool)
            or not math.isfinite(float(value))
            or value < 0
        ):
            raise EvaluationError(f"Match summary {label} must be finite and non-negative.")
    for label, value in (
        ("candidate_starting_tsv", summary.candidate_starting_tsv),
        ("candidate_final_tsv", summary.candidate_final_tsv),
        ("candidate_shots", summary.candidate_shots),
        ("candidate_hits", summary.candidate_hits),
        ("candidate_damage", summary.candidate_damage),
        ("opponent_starting_tsv", summary.opponent_starting_tsv),
        ("opponent_final_tsv", summary.opponent_final_tsv),
        ("opponent_shots", summary.opponent_shots),
        ("opponent_hits", summary.opponent_hits),
        ("opponent_damage", summary.opponent_damage),
    ):
        if not isinstance(value, int) or isinstance(value, bool) or value < 0:
            raise EvaluationError(
                f"Match summary {label} must be a non-negative integer."
            )
    if summary.candidate_inference_max_seconds > summary.candidate_inference_total_seconds:
        raise EvaluationError("Candidate inference max latency exceeds total inference time.")
    if summary.opponent_inference_max_seconds > summary.opponent_inference_total_seconds:
        raise EvaluationError("Opponent inference max latency exceeds total inference time.")
    if summary.candidate_inference_calls == 0 and (
        summary.candidate_inference_total_seconds > 0 or summary.candidate_inference_max_seconds > 0
    ):
        raise EvaluationError("Candidate inference timing exists without inference calls.")
    if summary.opponent_inference_calls == 0 and (
        summary.opponent_inference_total_seconds > 0 or summary.opponent_inference_max_seconds > 0
    ):
        raise EvaluationError("Opponent inference timing exists without inference calls.")
    if summary.telemetry_validated:
        if summary.matches > 0 and (
            summary.candidate_starting_tsv <= 0 or summary.opponent_starting_tsv <= 0
        ):
            raise EvaluationError(
                "Validated match summary must retain positive starting TSV evidence."
            )
        for side, hits, damage in (
            ("candidate", summary.candidate_hits, summary.candidate_damage),
            ("opponent", summary.opponent_hits, summary.opponent_damage),
        ):
            if (hits == 0) != (damage == 0):
                raise EvaluationError(
                    f"Validated match summary {side} hit/damage counters are inconsistent."
                )
    return summary


def _runtime_latency_evidence(
    summaries: Sequence[MatchSummary],
    max_inference_batch_milliseconds: Optional[float],
) -> Dict[str, Any]:
    if max_inference_batch_milliseconds is None:
        return {
            "configured": False,
            "passed": False,
            "max_inference_batch_milliseconds": None,
            "observed_candidate_max_milliseconds": None,
            "candidate_inference_calls": 0,
        }
    threshold = float(max_inference_batch_milliseconds)
    if not math.isfinite(threshold) or threshold <= 0:
        raise ValidationError(
            "promotion.max_inference_batch_milliseconds must be a positive finite number."
        )
    calls = sum(summary.candidate_inference_calls for summary in summaries)
    observed = max(
        (summary.candidate_inference_max_milliseconds for summary in summaries),
        default=0.0,
    )
    evidence_available = bool(summaries) and calls > 0
    return {
        "configured": True,
        "passed": evidence_available and observed <= threshold,
        "max_inference_batch_milliseconds": threshold,
        "observed_candidate_max_milliseconds": observed if evidence_available else None,
        "candidate_inference_calls": calls,
    }


def behavior_base_name(behavior_name: str) -> str:
    return behavior_name.split("?", 1)[0]


def behavior_team_id(behavior_name: str) -> int:
    match = TEAM_PATTERN.search(behavior_name)
    if match is None:
        raise EvaluationError(f"Behavior {behavior_name!r} does not expose a team id.")
    return int(match.group(1))


def build_allow_action_mask(decision_steps: Any, discrete_branches: Sequence[int]) -> np.ndarray:
    """Convert ML-Agents block masks (True=blocked) to ONNX allow masks (1=allowed)."""
    agent_count = len(decision_steps)
    total = sum(int(value) for value in discrete_branches)
    if total == 0:
        return np.zeros((agent_count, 0), dtype=np.float32)
    masks = getattr(decision_steps, "action_mask", None)
    if masks is None:
        return np.ones((agent_count, total), dtype=np.float32)
    if len(masks) != len(discrete_branches):
        raise EvaluationError(
            f"Action mask has {len(masks)} branches; expected {len(discrete_branches)}."
        )
    converted = []
    for index, (mask, branch_size) in enumerate(zip(masks, discrete_branches)):
        array = np.asarray(mask, dtype=np.bool_)
        expected_shape = (agent_count, int(branch_size))
        if array.shape != expected_shape:
            raise EvaluationError(
                f"Action mask branch {index} shape {array.shape} != {expected_shape}."
            )
        converted.append(np.logical_not(array).astype(np.float32, copy=False))
    return np.concatenate(converted, axis=1)


def _import_mlagents() -> Tuple[Any, Any, Any]:
    try:
        from mlagents_envs.base_env import ActionTuple
        from mlagents_envs.environment import UnityEnvironment
        from mlagents_envs.side_channel.side_channel import SideChannel
    except ImportError as exc:
        raise EvaluationError(
            "ML-Agents environment packages are required. Run this evaluator inside "
            "the same .venv-mlagents environment used for Bees training."
        ) from exc
    return UnityEnvironment, ActionTuple, SideChannel


def _create_result_channel() -> Any:
    _, _, SideChannel = _import_mlagents()

    class ResultChannel(SideChannel):
        def __init__(self) -> None:
            super().__init__(EVALUATION_CHANNEL_ID)
            self.results: List[EpisodeResult] = []
            self.error: Optional[BaseException] = None

        def on_message_received(self, msg: Any) -> None:
            if self.error is not None:
                return
            try:
                self.results.append(parse_episode_message(msg))
            except BaseException as exc:
                self.error = exc

        def drain(self) -> List[EpisodeResult]:
            if self.error is not None:
                error = self.error
                self.error = None
                raise EvaluationError(f"Invalid Unity evaluation result: {error}") from error
            values = self.results
            self.results = []
            return values

    return ResultChannel()


class OnnxPolicy:
    """Small deterministic ONNX inference adapter for one immutable ML-Agents policy."""

    def __init__(self, model_path: os.PathLike[str] | str, *, provider: Optional[str] = None) -> None:
        self.path = Path(model_path).expanduser().resolve()
        if not self.path.is_file():
            raise EvaluationError(f"ONNX policy does not exist: {self.path}")
        try:
            import onnxruntime as ort
        except ImportError as exc:
            raise EvaluationError(
                "onnxruntime is required for frozen-policy evaluation. Install it in "
                "the Bees ML-Agents virtual environment."
            ) from exc

        providers = [provider] if provider else ["CPUExecutionProvider"]
        try:
            self.session = ort.InferenceSession(str(self.path), providers=providers)
        except Exception as exc:
            raise EvaluationError(f"Could not load ONNX policy {self.path}: {exc}") from exc

        self.input_info = {item.name: item for item in self.session.get_inputs()}
        self.output_names = {item.name for item in self.session.get_outputs()}
        self.obs_names = sorted(
            (name for name in self.input_info if name.startswith("obs_")),
            key=lambda name: int(name.split("_", 1)[1]),
        )
        if not self.obs_names or "action_masks" not in self.input_info:
            missing = []
            if not self.obs_names:
                missing.append("obs_*")
            if "action_masks" not in self.input_info:
                missing.append("action_masks")
            raise EvaluationError(
                f"ONNX policy {self.path} is missing ML-Agents inputs: {missing}."
            )
        if "deterministic_continuous_actions" not in self.output_names:
            raise EvaluationError(
                f"ONNX policy {self.path} lacks deterministic_continuous_actions."
            )
        if "deterministic_discrete_actions" not in self.output_names:
            raise EvaluationError(
                f"ONNX policy {self.path} lacks deterministic_discrete_actions."
            )

        self.has_recurrent_input = "recurrent_in" in self.input_info
        self.memory_size = 0
        if self.has_recurrent_input:
            memory_shape = self.input_info["recurrent_in"].shape
            if len(memory_shape) != 3:
                raise EvaluationError(
                    f"ONNX recurrent_in shape {memory_shape!r} is not [batch, sequence, memory]."
                )
            memory_dim = memory_shape[-1]
            if not isinstance(memory_dim, int):
                raise EvaluationError(
                    f"ONNX recurrent memory dimension must be static; got {memory_dim!r}."
                )
            self.memory_size = memory_dim
        if self.memory_size > 0 and "recurrent_out" not in self.output_names:
            raise EvaluationError(
                f"{self.path.name} uses recurrent memory but lacks recurrent_out."
            )
        self.memories: Dict[int, np.ndarray] = {}
        self._validated_signature: Optional[Tuple[Any, ...]] = None
        self.inference_calls = 0
        self.inference_total_seconds = 0.0
        self.inference_max_seconds = 0.0

    def _signature(self, behavior_spec: Any) -> Tuple[Any, ...]:
        observation_shapes = tuple(tuple(spec.shape) for spec in behavior_spec.observation_specs)
        action_spec = behavior_spec.action_spec
        return (
            observation_shapes,
            int(action_spec.continuous_size),
            tuple(int(value) for value in action_spec.discrete_branches),
        )

    def validate_behavior_spec(self, behavior_spec: Any) -> None:
        signature = self._signature(behavior_spec)
        if self._validated_signature == signature:
            return
        observation_shapes = signature[0]
        if len(observation_shapes) != len(self.obs_names):
            raise EvaluationError(
                f"{self.path.name} has {len(self.obs_names)} observation inputs but "
                f"environment exposes {len(observation_shapes)}."
            )
        for index, (shape, input_name) in enumerate(zip(observation_shapes, self.obs_names)):
            onnx_shape = self.input_info[input_name].shape
            expected_tail = tuple(shape)
            actual_tail = tuple(onnx_shape[1:])
            if any(
                isinstance(actual, int) and actual != expected
                for actual, expected in zip(actual_tail, expected_tail)
            ) or len(actual_tail) != len(expected_tail):
                raise EvaluationError(
                    f"{self.path.name} observation {index} shape {actual_tail} "
                    f"does not match environment {expected_tail}."
                )

        action_spec = behavior_spec.action_spec
        continuous_shape = self._output_tail("deterministic_continuous_actions")
        if continuous_shape and continuous_shape[-1] != int(action_spec.continuous_size):
            raise EvaluationError(
                f"{self.path.name} continuous action size {continuous_shape[-1]} != "
                f"{action_spec.continuous_size}."
            )
        discrete_shape = self._output_tail("deterministic_discrete_actions")
        if discrete_shape and discrete_shape[-1] != int(action_spec.discrete_size):
            raise EvaluationError(
                f"{self.path.name} discrete branch count {discrete_shape[-1]} != "
                f"{action_spec.discrete_size}."
            )
        self._validated_signature = signature

    def _output_tail(self, name: str) -> Tuple[int, ...]:
        info = next(item for item in self.session.get_outputs() if item.name == name)
        return tuple(value for value in info.shape[1:] if isinstance(value, int))

    def forget(self, agent_ids: Iterable[int]) -> None:
        for agent_id in agent_ids:
            self.memories.pop(int(agent_id), None)

    def actions(self, decision_steps: Any, behavior_spec: Any, ActionTuple: Any) -> Any:
        self.validate_behavior_spec(behavior_spec)
        agent_count = len(decision_steps)
        if agent_count == 0:
            return ActionTuple()

        agent_ids = [int(value) for value in decision_steps.agent_id]
        inputs: Dict[str, np.ndarray] = {}
        for name, observation in zip(self.obs_names, decision_steps.obs):
            inputs[name] = np.asarray(observation, dtype=np.float32)

        branches = tuple(int(value) for value in behavior_spec.action_spec.discrete_branches)
        inputs["action_masks"] = build_allow_action_mask(decision_steps, branches)
        if self.has_recurrent_input:
            if self.memory_size > 0:
                memories = [
                    self.memories.get(
                        agent_id,
                        np.zeros((1, self.memory_size), dtype=np.float32),
                    )
                    for agent_id in agent_ids
                ]
                inputs["recurrent_in"] = np.stack(memories, axis=0)
            else:
                inputs["recurrent_in"] = np.zeros((agent_count, 1, 0), dtype=np.float32)

        requested = [
            "deterministic_continuous_actions",
            "deterministic_discrete_actions",
        ]
        if self.memory_size > 0:
            requested.append("recurrent_out")
        started = time.perf_counter()
        try:
            outputs = self.session.run(requested, inputs)
        except Exception as exc:
            raise EvaluationError(f"ONNX inference failed for {self.path}: {exc}") from exc
        elapsed = time.perf_counter() - started
        self.inference_calls += 1
        self.inference_total_seconds += elapsed
        self.inference_max_seconds = max(self.inference_max_seconds, elapsed)

        continuous = np.asarray(outputs[0], dtype=np.float32)
        discrete = np.asarray(outputs[1], dtype=np.int32)
        if continuous.shape != (agent_count, behavior_spec.action_spec.continuous_size):
            raise EvaluationError(
                f"Continuous action output shape {continuous.shape} is invalid."
            )
        if discrete.shape != (agent_count, behavior_spec.action_spec.discrete_size):
            raise EvaluationError(
                f"Discrete action output shape {discrete.shape} is invalid."
            )

        if self.memory_size > 0:
            recurrent = np.asarray(outputs[2], dtype=np.float32)
            expected = (agent_count, 1, self.memory_size)
            if recurrent.shape != expected:
                raise EvaluationError(
                    f"Recurrent output shape {recurrent.shape} != expected {expected}."
                )
            for index, agent_id in enumerate(agent_ids):
                self.memories[agent_id] = recurrent[index]

        return ActionTuple(continuous=continuous, discrete=discrete)


def _validate_environment_behaviors(
    behavior_specs: Mapping[str, Any],
    expected_behavior_name: str,
) -> Dict[int, str]:
    teams: Dict[int, str] = {}
    for name in behavior_specs:
        if behavior_base_name(name) != expected_behavior_name:
            continue
        team = behavior_team_id(name)
        if team in teams and teams[team] != name:
            raise EvaluationError(f"Multiple behaviors claim team {team}: {teams[team]!r}, {name!r}.")
        teams[team] = name
    if set(teams) != {0, 1}:
        raise EvaluationError(
            f"Expected {expected_behavior_name!r} teams 0 and 1; found {sorted(teams)}."
        )
    return teams


def _evaluation_environment_args(env_args: Sequence[str]) -> List[str]:
    result = list(env_args)
    if not any(
        isinstance(value, str)
        and value.strip().casefold() == EVALUATION_MODE_FLAG.casefold()
        for value in result
    ):
        result.append(EVALUATION_MODE_FLAG)
    return result


def run_match_group(
    *,
    environment_path: os.PathLike[str] | str,
    candidate_model_path: os.PathLike[str] | str,
    opponent_model_path: os.PathLike[str] | str,
    matches: int,
    behavior_name: str,
    env_args: Sequence[str],
    seed: int,
    worker_id: int,
    timeout_wait: int,
    onnx_provider: Optional[str],
    no_graphics: bool = True,
    max_environment_steps_per_match: int = DEFAULT_MAX_ENVIRONMENT_STEPS_PER_MATCH,
) -> MatchSummary:
    if matches <= 0:
        raise EvaluationError("Match group must request at least one match.")
    if max_environment_steps_per_match <= 0:
        raise EvaluationError("max_environment_steps_per_match must be positive.")

    UnityEnvironment, ActionTuple, _ = _import_mlagents()
    channel = _create_result_channel()
    candidate = OnnxPolicy(candidate_model_path, provider=onnx_provider)
    opponent = OnnxPolicy(opponent_model_path, provider=onnx_provider)
    environment = UnityEnvironment(
        file_name=str(Path(environment_path)),
        worker_id=worker_id,
        seed=seed,
        no_graphics=no_graphics,
        timeout_wait=timeout_wait,
        additional_args=_evaluation_environment_args(env_args),
        side_channels=[channel],
    )

    completed: List[EpisodeResult] = []
    environment_steps = 0
    try:
        environment.reset()
        teams = _validate_environment_behaviors(environment.behavior_specs, behavior_name)
        policies = {0: candidate, 1: opponent}
        for team, name in teams.items():
            policies[team].validate_behavior_spec(environment.behavior_specs[name])

        limit = matches * max_environment_steps_per_match
        while len(completed) < matches:
            for team, name in teams.items():
                decision_steps, terminal_steps = environment.get_steps(name)
                policies[team].forget(getattr(terminal_steps, "agent_id", ()))
                if len(decision_steps) > 0:
                    actions = policies[team].actions(
                        decision_steps,
                        environment.behavior_specs[name],
                        ActionTuple,
                    )
                    environment.set_actions(name, actions)
            environment.step()
            environment_steps += 1
            completed.extend(channel.drain())
            if len(completed) > matches:
                raise EvaluationError(
                    f"Unity emitted {len(completed)} results for a {matches}-match group."
                )
            if environment_steps > limit:
                raise EvaluationError(
                    f"Evaluation exceeded {limit} Unity steps before completing "
                    f"{matches} matches."
                )
    finally:
        environment.close()

    summary = replace(
        summarize_results(completed, candidate_team_id=0),
        candidate_inference_calls=candidate.inference_calls,
        candidate_inference_total_seconds=candidate.inference_total_seconds,
        candidate_inference_max_seconds=candidate.inference_max_seconds,
        opponent_inference_calls=opponent.inference_calls,
        opponent_inference_total_seconds=opponent.inference_total_seconds,
        opponent_inference_max_seconds=opponent.inference_max_seconds,
    )
    return _validate_match_summary(summary, expected_matches=matches)


def _validate_env_args(values: Sequence[str]) -> Tuple[str, ...]:
    result = []
    for value in values:
        if not isinstance(value, str) or not value.strip():
            raise ValidationError("Evaluation env_args entries must be non-empty strings.")
        result.append(value)
    return tuple(result)


def _env_arg_name(value: str) -> Optional[str]:
    stripped = value.strip()
    if not stripped.startswith("--"):
        return None
    return stripped.split("=", 1)[0].casefold()


def _merge_env_args(
    base_env_args: Sequence[str],
    override_env_args: Sequence[str],
) -> Tuple[str, ...]:
    """Merge case-specific Unity args without leaving conflicting base options behind."""
    base = list(_validate_env_args(base_env_args))
    overrides = list(_validate_env_args(override_env_args))
    override_names = {
        name
        for name in (_env_arg_name(value) for value in overrides)
        if name is not None
    }
    suppressed_base_names = set(override_names)

    fixed_map_size = "--rl-map-size"
    ranged_map_size = {"--rl-map-size-min", "--rl-map-size-max"}
    if fixed_map_size in override_names:
        suppressed_base_names.update(ranged_map_size)
    elif override_names & ranged_map_size:
        suppressed_base_names.add(fixed_map_size)

    merged: List[str] = []
    index = 0
    while index < len(base):
        value = base[index]
        name = _env_arg_name(value)
        if name in suppressed_base_names:
            index += 1
            if (
                "=" not in value
                and index < len(base)
                and _env_arg_name(base[index]) is None
            ):
                index += 1
            continue
        merged.append(value)
        index += 1

    merged.extend(overrides)
    return tuple(merged)


def load_competency_suite(
    path: Optional[os.PathLike[str] | str],
    *,
    default_matches: int,
) -> List[CompetencyCase]:
    if path is None:
        return []
    source = Path(path)
    try:
        raw = json.loads(source.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ValidationError(f"Competency suite not found: {source}") from exc
    except json.JSONDecodeError as exc:
        raise ValidationError(f"Invalid competency suite JSON {source}: {exc}") from exc
    if not isinstance(raw, dict) or raw.get("schema_version") != 1:
        raise ValidationError("Competency suite must be an object with schema_version=1.")
    cases = raw.get("cases")
    if not isinstance(cases, list):
        raise ValidationError("Competency suite cases must be a list.")

    result: List[CompetencyCase] = []
    seen = set()
    for index, item in enumerate(cases):
        if not isinstance(item, dict):
            raise ValidationError(f"Competency case {index} must be an object.")
        name_value = item.get("name")
        opponent_value = item.get("opponent_model_id")
        if not isinstance(name_value, str) or not name_value.strip():
            raise ValidationError(f"Competency case {index} requires a non-empty string name.")
        if not isinstance(opponent_value, str) or not opponent_value.strip():
            raise ValidationError(
                f"Competency case {index} requires a non-empty string opponent_model_id."
            )
        name = name_value.strip()
        opponent = opponent_value.strip()
        if name in seen:
            raise ValidationError(f"Duplicate competency case name {name!r}.")
        seen.add(name)
        matches = item.get("matches", default_matches)
        minimum = item.get("minimum")
        metric = item.get("metric", "score_rate")
        critical = item.get("critical", True)
        env_args = item.get("env_args", [])
        if not isinstance(matches, int) or isinstance(matches, bool) or matches <= 0:
            raise ValidationError(f"Competency {name!r} matches must be a positive integer.")
        if (
            not isinstance(minimum, (int, float))
            or isinstance(minimum, bool)
            or not math.isfinite(float(minimum))
            or not 0 <= float(minimum) <= 1
        ):
            raise ValidationError(f"Competency {name!r} minimum must be in [0,1].")
        if not isinstance(metric, str) or metric not in {"score_rate", "win_rate", "non_timeout_rate"}:
            raise ValidationError(
                f"Competency {name!r} metric {metric!r} is unsupported."
            )
        if not isinstance(critical, bool):
            raise ValidationError(f"Competency {name!r} critical must be boolean.")
        if not isinstance(env_args, list):
            raise ValidationError(f"Competency {name!r} env_args must be a list.")
        result.append(
            CompetencyCase(
                name=name,
                opponent_model_id=opponent,
                matches=matches,
                minimum=float(minimum),
                metric=metric,
                critical=critical,
                env_args=_validate_env_args(env_args),
            )
        )
    return result


def competency_score(summary: MatchSummary, metric: str) -> float:
    if metric == "score_rate":
        return summary.score_rate
    if metric == "win_rate":
        return summary.win_rate
    if metric == "non_timeout_rate":
        return 1.0 - summary.timeout_rate
    raise ValidationError(f"Unknown competency metric {metric!r}.")


def _historical_is_critical(model: Mapping[str, Any]) -> bool:
    metadata = model.get("metadata")
    if isinstance(metadata, dict) and bool(metadata.get("critical_regression")):
        return True
    tags = metadata.get("tags", []) if isinstance(metadata, dict) else []
    return isinstance(tags, list) and "critical" in tags


def _model_path(store: ContinualLearningStore, model_id: str) -> Path:
    model = store.get_model(model_id)
    path = Path(model["artifact_path"])
    if not path.is_file():
        raise EvaluationError(
            f"Registered model {model_id} is missing its artifact: {path}"
        )
    expected_sha256 = str(model.get("artifact_sha256", "")).strip().lower()
    if not expected_sha256:
        raise EvaluationError(
            f"Registered model {model_id} is missing its artifact SHA-256."
        )
    actual_sha256 = sha256_file(path).lower()
    if actual_sha256 != expected_sha256:
        raise EvaluationError(
            f"Registered model {model_id} failed SHA-256 integrity verification."
        )
    return path


def _safe_model(store: ContinualLearningStore, model_id: str) -> Dict[str, Any]:
    model = store.get_model(model_id)
    expected = store.compatibility.to_dict()
    actual = {key: model.get(key) for key in expected}
    if actual != expected:
        raise EvaluationError(
            f"Model {model_id} compatibility {actual} does not match {expected}."
        )
    return model


MatchRunner = Callable[..., MatchSummary]


def evaluate_candidate(
    store: ContinualLearningStore,
    *,
    candidate_model_id: str,
    environment_path: os.PathLike[str] | str,
    env_args: Sequence[str] = (),
    competency_suite: Optional[os.PathLike[str] | str] = None,
    champion_matches: Optional[int] = None,
    historical_matches: Optional[int] = None,
    competency_default_matches: Optional[int] = None,
    historical_model_ids: Optional[Sequence[str]] = None,
    seed: int = 0,
    worker_id: int = 0,
    timeout_wait: int = 300,
    onnx_provider: Optional[str] = None,
    no_graphics: bool = True,
    max_environment_steps_per_match: int = DEFAULT_MAX_ENVIRONMENT_STEPS_PER_MATCH,
    match_runner: MatchRunner = run_match_group,
) -> Dict[str, Any]:
    store.initialize()
    candidate = _safe_model(store, candidate_model_id)
    if candidate["status"] not in ("candidate", "rejected"):
        raise EvaluationError(
            f"Model {candidate_model_id} has status {candidate['status']}; "
            "candidate/rejected status is required for evaluation."
        )
    candidate_path = _model_path(store, candidate_model_id)

    promotion = store.config["promotion"]
    latency_threshold_raw = promotion.get("max_inference_batch_milliseconds")
    if latency_threshold_raw is None:
        latency_threshold = None
    elif (
        not isinstance(latency_threshold_raw, (int, float))
        or isinstance(latency_threshold_raw, bool)
        or not math.isfinite(float(latency_threshold_raw))
        or float(latency_threshold_raw) <= 0
    ):
        raise ValidationError(
            "promotion.max_inference_batch_milliseconds must be a positive finite number."
        )
    else:
        latency_threshold = float(latency_threshold_raw)

    champion_match_count = (
        int(champion_matches)
        if champion_matches is not None
        else int(promotion["min_matches_vs_champion"])
    )
    historical_match_count = (
        int(historical_matches)
        if historical_matches is not None
        else int(promotion["min_historical_matches_per_opponent"])
    )
    competency_match_count = (
        int(competency_default_matches)
        if competency_default_matches is not None
        else historical_match_count
    )
    for label, value in (
        ("champion_matches", champion_match_count),
        ("historical_matches", historical_match_count),
        ("competency_default_matches", competency_match_count),
    ):
        if value <= 0:
            raise ValidationError(f"{label} must be positive.")

    base_env_args = _validate_env_args(env_args)
    behavior_name = store.compatibility.behavior_name
    champion_id = store.current_champion_id()
    run_number = 0
    completed_match_groups = 0
    validated_summaries: List[MatchSummary] = []
    candidate_runtime_summaries: List[MatchSummary] = []
    authoritative_runner = match_runner is run_match_group

    def accept_summary(
        summary: MatchSummary,
        matches: int,
        *,
        candidate_is_evaluated_model: bool,
    ) -> MatchSummary:
        nonlocal completed_match_groups
        validated = _validate_match_summary(summary, expected_matches=matches)
        completed_match_groups += 1
        validated_summaries.append(validated)
        if candidate_is_evaluated_model:
            candidate_runtime_summaries.append(validated)
        return validated

    def run(opponent_id: str, matches: int, extra_args: Sequence[str] = ()) -> MatchSummary:
        nonlocal run_number
        if opponent_id == candidate_model_id:
            raise EvaluationError("Candidate cannot be evaluated against itself.")
        _safe_model(store, opponent_id)
        opponent_path = _model_path(store, opponent_id)
        current_worker = worker_id + run_number
        current_seed = seed + run_number
        run_number += 1
        summary = match_runner(
            environment_path=environment_path,
            candidate_model_path=candidate_path,
            opponent_model_path=opponent_path,
            matches=matches,
            behavior_name=behavior_name,
            env_args=_merge_env_args(base_env_args, extra_args),
            seed=current_seed,
            worker_id=current_worker,
            timeout_wait=timeout_wait,
            onnx_provider=onnx_provider,
            no_graphics=no_graphics,
            max_environment_steps_per_match=max_environment_steps_per_match,
        )
        return accept_summary(
            summary,
            matches,
            candidate_is_evaluated_model=True,
        )

    champion_comparison: Optional[Dict[str, Any]] = None
    if champion_id is not None:
        champion_summary = run(champion_id, champion_match_count)
        champion_comparison = champion_summary.to_dict()

    expected_compatibility = store.compatibility.to_dict()
    historical_models = [
        model
        for model in store.list_models(status="historical")
        if all(model.get(key) == value for key, value in expected_compatibility.items())
    ]
    if historical_model_ids is not None:
        requested = list(dict.fromkeys(str(value) for value in historical_model_ids))
        by_id = {model["model_id"]: model for model in historical_models}
        missing = [model_id for model_id in requested if model_id not in by_id]
        if missing:
            raise ValidationError(
                "Requested historical opponents are not compatible historical models: "
                + ", ".join(missing)
            )
        historical_models = [by_id[model_id] for model_id in requested]

    historical_results: List[Dict[str, Any]] = []
    for historical in historical_models:
        opponent_id = historical["model_id"]
        paired_seed = seed + run_number
        candidate_summary = run(opponent_id, historical_match_count)
        baseline_win_rate: Optional[float] = None
        baseline_score_rate: Optional[float] = None
        baseline_summary: Optional[MatchSummary] = None
        if champion_id is not None:
            champion_path = _model_path(store, champion_id)
            opponent_path = _model_path(store, opponent_id)
            current_worker = worker_id + run_number
            run_number += 1
            baseline_summary = accept_summary(
                match_runner(
                    environment_path=environment_path,
                    candidate_model_path=champion_path,
                    opponent_model_path=opponent_path,
                    matches=historical_match_count,
                    behavior_name=behavior_name,
                    env_args=base_env_args,
                    seed=paired_seed,
                    worker_id=current_worker,
                    timeout_wait=timeout_wait,
                    onnx_provider=onnx_provider,
                    no_graphics=no_graphics,
                    max_environment_steps_per_match=max_environment_steps_per_match,
                ),
                historical_match_count,
                candidate_is_evaluated_model=False,
            )
            baseline_win_rate = baseline_summary.win_rate
            baseline_score_rate = baseline_summary.score_rate
        entry: Dict[str, Any] = {
            "opponent_model_id": opponent_id,
            "matches": candidate_summary.matches,
            "candidate_win_rate": candidate_summary.win_rate,
            "candidate_score_rate": candidate_summary.score_rate,
            "critical": _historical_is_critical(historical),
            "candidate_summary": candidate_summary.to_dict(),
        }
        if baseline_score_rate is not None:
            entry["baseline_win_rate"] = baseline_win_rate
            entry["baseline_score_rate"] = baseline_score_rate
            entry["champion_baseline_summary"] = baseline_summary.to_dict()
        historical_results.append(entry)

    competencies = load_competency_suite(
        competency_suite,
        default_matches=competency_match_count,
    )
    competency_results: List[Dict[str, Any]] = []
    for case in competencies:
        summary = run(case.opponent_model_id, case.matches, case.env_args)
        score = competency_score(summary, case.metric)
        competency_results.append(
            {
                "name": case.name,
                "opponent_model_id": case.opponent_model_id,
                "metric": case.metric,
                "score": score,
                "minimum": case.minimum,
                "critical": case.critical,
                "matches": summary.matches,
                "env_args": list(case.env_args),
                "summary": summary.to_dict(),
            }
        )

    all_match_groups_completed = run_number > 0 and completed_match_groups == run_number
    all_authoritative_telemetry_validated = (
        all_match_groups_completed
        and bool(validated_summaries)
        and all(summary.telemetry_validated for summary in validated_summaries)
    )
    latency_evidence = _runtime_latency_evidence(
        candidate_runtime_summaries,
        latency_threshold,
    )
    runtime_compatible = authoritative_runner and all_match_groups_completed
    runtime_checks_passed = (
        authoritative_runner
        and all_match_groups_completed
        and bool(latency_evidence["passed"])
    )
    behavior_sanity_passed = authoritative_runner and all_authoritative_telemetry_validated

    report: Dict[str, Any] = {
        "candidate_model_id": candidate_model_id,
        "champion_model_id": champion_id,
        "candidate_vs_champion": champion_comparison,
        "historical": historical_results,
        "competencies": competency_results,
        "behavior_sanity_passed": behavior_sanity_passed,
        "runtime_compatible": runtime_compatible,
        "runtime_checks_passed": runtime_checks_passed,
        "evaluator": {
            "protocol_version": EVALUATION_PROTOCOL_VERSION,
            "behavior_name": behavior_name,
            "candidate_team_id": 0,
            "deterministic_actions": True,
            "base_env_args": list(base_env_args),
            "seed": seed,
            "match_groups": run_number,
            "completed_match_groups": completed_match_groups,
            "authoritative_match_runner": authoritative_runner,
            "authoritative_telemetry_validated": all_authoritative_telemetry_validated,
            "artifact_integrity_verified": True,
            "compatibility_metadata_verified": True,
            "runtime_latency": latency_evidence,
        },
    }
    return report


def evaluate_and_record(
    store: ContinualLearningStore,
    **kwargs: Any,
) -> Dict[str, Any]:
    report = evaluate_candidate(store, **kwargs)
    recorded = store.record_evaluation(report)
    league_updates = []
    for historical in report["historical"]:
        baseline = historical.get("baseline_score_rate")
        if baseline is None:
            continue
        tags = [
            "authoritative_candidate_evaluation",
            f"evaluation:{recorded['report_id']}",
        ]
        update = {
            "current_model_id": report["candidate_model_id"],
            "opponent_model_id": historical["opponent_model_id"],
            "current_win_rate": historical["candidate_score_rate"],
            "previous_win_rate": baseline,
            "match_count": historical["matches"],
            "tags": tags,
        }
        store.record_historical_matchup(**update)
        league_updates.append(update)
    return {"report": report, "recorded": recorded, "league_updates": league_updates}


def _load_env_args_file(path: Optional[str]) -> List[str]:
    if not path:
        return []
    source = Path(path)
    try:
        value = json.loads(source.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ValidationError(f"Environment args file not found: {source}") from exc
    except json.JSONDecodeError as exc:
        raise ValidationError(f"Invalid environment args JSON {source}: {exc}") from exc
    if not isinstance(value, list):
        raise ValidationError("Environment args file must contain a JSON array of strings.")
    return list(_validate_env_args(value))


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Evaluate an immutable Bees RL candidate against the current champion, "
            "historical policies, and permanent competency cases."
        )
    )
    parser.add_argument("--store", required=True, help="Continual-learning store root.")
    parser.add_argument("--candidate", required=True, help="Registered candidate model id.")
    parser.add_argument("--env", required=True, help="Dedicated RL Unity executable.")
    parser.add_argument("--config", help="Optional continual-learning config JSON.")
    parser.add_argument(
        "--competency-suite",
        help=(
            "Permanent competency-suite JSON. Promotion remains blocked when the configured "
            "minimum competency count is not satisfied."
        ),
    )
    parser.add_argument("--champion-matches", type=int)
    parser.add_argument("--historical-matches", type=int)
    parser.add_argument("--competency-default-matches", type=int)
    parser.add_argument(
        "--historical-opponent",
        action="append",
        dest="historical_opponents",
        help="Evaluate only this historical model id; repeat to select multiple.",
    )
    parser.add_argument(
        "--env-arg",
        action="append",
        default=[],
        help="Unity environment argument; repeat for each token.",
    )
    parser.add_argument(
        "--env-args-file",
        help="JSON array of additional Unity environment argument tokens.",
    )
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--worker-id", type=int, default=0)
    parser.add_argument("--timeout-wait", type=int, default=300)
    parser.add_argument("--onnx-provider")
    parser.add_argument(
        "--max-environment-steps-per-match",
        type=int,
        default=DEFAULT_MAX_ENVIRONMENT_STEPS_PER_MATCH,
    )
    parser.add_argument(
        "--graphics",
        action="store_true",
        help="Run the evaluation executable with graphics instead of headless.",
    )
    parser.add_argument(
        "--promote-if-qualified",
        action="store_true",
        help="Promote the candidate atomically if the recorded evaluation passes.",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        config = load_config(args.config) if args.config else load_config()
        store = ContinualLearningStore(args.store, config=config)
        env_args = _load_env_args_file(args.env_args_file) + list(args.env_arg)
        value = evaluate_and_record(
            store,
            candidate_model_id=args.candidate,
            environment_path=args.env,
            env_args=env_args,
            competency_suite=args.competency_suite,
            champion_matches=args.champion_matches,
            historical_matches=args.historical_matches,
            competency_default_matches=args.competency_default_matches,
            historical_model_ids=args.historical_opponents,
            seed=args.seed,
            worker_id=args.worker_id,
            timeout_wait=args.timeout_wait,
            onnx_provider=args.onnx_provider,
            no_graphics=not args.graphics,
            max_environment_steps_per_match=args.max_environment_steps_per_match,
        )
        recorded = value["recorded"]
        if args.promote_if_qualified and recorded["passed"]:
            value["promoted"] = store.promote(args.candidate, recorded["report_id"])
        elif args.promote_if_qualified:
            value["promoted"] = None
        print(json.dumps(value, indent=2, sort_keys=True))
        return 0 if recorded["passed"] else 2
    except (ContinualLearningError, ValueError, OSError) as exc:
        print(f"Evaluation failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
