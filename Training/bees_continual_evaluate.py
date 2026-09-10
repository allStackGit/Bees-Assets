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
import uuid
from dataclasses import dataclass
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
    if result.episode_number <= 0:
        raise EvaluationError("Evaluation result contained a non-positive episode number.")
    if not math.isfinite(result.duration_seconds) or result.duration_seconds < 0:
        raise EvaluationError("Evaluation result contained an invalid episode duration.")
    if result.bee_team_id == result.human_team_id:
        raise EvaluationError("Evaluation result assigned both sides to the same team.")
    if result.timed_out and result.winning_team_id != -1:
        raise EvaluationError("Timed-out evaluation result must not claim a winning team.")
    if result.winning_team_id not in (-1, result.bee_team_id, result.human_team_id):
        raise EvaluationError(
            f"Evaluation result claimed unknown winning team {result.winning_team_id}."
        )
    return result


def summarize_results(results: Sequence[EpisodeResult], candidate_team_id: int) -> MatchSummary:
    wins = losses = draws = timeouts = 0
    duration = 0.0
    seen_episodes = set()
    for result in results:
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
    return MatchSummary(
        matches=len(results),
        wins=wins,
        losses=losses,
        draws=draws,
        timeouts=timeouts,
        total_duration_seconds=duration,
    )


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
        try:
            outputs = self.session.run(requested, inputs)
        except Exception as exc:
            raise EvaluationError(f"ONNX inference failed for {self.path}: {exc}") from exc

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
        additional_args=list(env_args),
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

    return summarize_results(completed, candidate_team_id=0)


def _validate_env_args(values: Sequence[str]) -> Tuple[str, ...]:
    result = []
    for value in values:
        if not isinstance(value, str) or not value.strip():
            raise ValidationError("Evaluation env_args entries must be non-empty strings.")
        result.append(value)
    return tuple(result)


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
        name = str(item.get("name", "")).strip()
        opponent = str(item.get("opponent_model_id", "")).strip()
        if not name or not opponent:
            raise ValidationError(
                f"Competency case {index} requires name and opponent_model_id."
            )
        if name in seen:
            raise ValidationError(f"Duplicate competency case name {name!r}.")
        seen.add(name)
        matches = item.get("matches", default_matches)
        minimum = item.get("minimum")
        metric = str(item.get("metric", "score_rate"))
        if not isinstance(matches, int) or isinstance(matches, bool) or matches <= 0:
            raise ValidationError(f"Competency {name!r} matches must be a positive integer.")
        if not isinstance(minimum, (int, float)) or isinstance(minimum, bool) or not math.isfinite(float(minimum)):
            raise ValidationError(f"Competency {name!r} minimum must be finite.")
        if metric not in {"score_rate", "win_rate", "non_timeout_rate"}:
            raise ValidationError(
                f"Competency {name!r} metric {metric!r} is unsupported."
            )
        result.append(
            CompetencyCase(
                name=name,
                opponent_model_id=opponent,
                matches=matches,
                minimum=float(minimum),
                metric=metric,
                critical=bool(item.get("critical", True)),
                env_args=_validate_env_args(item.get("env_args", [])),
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

    def run(opponent_id: str, matches: int, extra_args: Sequence[str] = ()) -> MatchSummary:
        nonlocal run_number
        if opponent_id == candidate_model_id:
            raise EvaluationError("Candidate cannot be evaluated against itself.")
        _safe_model(store, opponent_id)
        opponent_path = _model_path(store, opponent_id)
        current_worker = worker_id + run_number
        current_seed = seed + run_number
        run_number += 1
        return match_runner(
            environment_path=environment_path,
            candidate_model_path=candidate_path,
            opponent_model_path=opponent_path,
            matches=matches,
            behavior_name=behavior_name,
            env_args=tuple(base_env_args) + tuple(extra_args),
            seed=current_seed,
            worker_id=current_worker,
            timeout_wait=timeout_wait,
            onnx_provider=onnx_provider,
            no_graphics=no_graphics,
            max_environment_steps_per_match=max_environment_steps_per_match,
        )

    champion_comparison: Optional[Dict[str, Any]] = None
    if champion_id is not None:
        champion_summary = run(champion_id, champion_match_count)
        champion_comparison = champion_summary.to_dict()

    historical_models = store.list_models(status="historical")
    if historical_model_ids is not None:
        requested = list(dict.fromkeys(str(value) for value in historical_model_ids))
        by_id = {model["model_id"]: model for model in historical_models}
        missing = [model_id for model_id in requested if model_id not in by_id]
        if missing:
            raise ValidationError(
                "Requested historical opponents are not historical models: "
                + ", ".join(missing)
            )
        historical_models = [by_id[model_id] for model_id in requested]

    historical_results: List[Dict[str, Any]] = []
    for historical in historical_models:
        opponent_id = historical["model_id"]
        candidate_summary = run(opponent_id, historical_match_count)
        baseline_rate: Optional[float] = None
        baseline_summary: Optional[MatchSummary] = None
        if champion_id is not None:
            champion_path = _model_path(store, champion_id)
            opponent_path = _model_path(store, opponent_id)
            current_worker = worker_id + run_number
            current_seed = seed + run_number
            run_number += 1
            baseline_summary = match_runner(
                environment_path=environment_path,
                candidate_model_path=champion_path,
                opponent_model_path=opponent_path,
                matches=historical_match_count,
                behavior_name=behavior_name,
                env_args=base_env_args,
                seed=current_seed,
                worker_id=current_worker,
                timeout_wait=timeout_wait,
                onnx_provider=onnx_provider,
                no_graphics=no_graphics,
                max_environment_steps_per_match=max_environment_steps_per_match,
            )
            baseline_rate = baseline_summary.win_rate
        entry: Dict[str, Any] = {
            "opponent_model_id": opponent_id,
            "matches": candidate_summary.matches,
            "candidate_win_rate": candidate_summary.win_rate,
            "critical": _historical_is_critical(historical),
            "candidate_summary": candidate_summary.to_dict(),
        }
        if baseline_rate is not None:
            entry["baseline_win_rate"] = baseline_rate
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
                "matches": case.matches,
                "env_args": list(case.env_args),
                "summary": summary.to_dict(),
            }
        )

    report: Dict[str, Any] = {
        "candidate_model_id": candidate_model_id,
        "champion_model_id": champion_id,
        "candidate_vs_champion": champion_comparison,
        "historical": historical_results,
        "competencies": competency_results,
        "behavior_sanity_passed": True,
        "runtime_compatible": True,
        "runtime_checks_passed": True,
        "evaluator": {
            "protocol_version": EVALUATION_PROTOCOL_VERSION,
            "behavior_name": behavior_name,
            "candidate_team_id": 0,
            "deterministic_actions": True,
            "base_env_args": list(base_env_args),
            "seed": seed,
            "match_groups": run_number,
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
        baseline = historical.get("baseline_win_rate")
        if baseline is None:
            continue
        league_updates.append(
            store.record_historical_matchup(
                current_model_id=report["candidate_model_id"],
                historical_model_id=historical["opponent_model_id"],
                current_win_rate=historical["candidate_win_rate"],
                previous_win_rate=baseline,
                matches=historical["matches"],
                metadata={
                    "source": "authoritative_candidate_evaluation",
                    "evaluation_report_id": recorded["report_id"],
                },
            )
        )
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
