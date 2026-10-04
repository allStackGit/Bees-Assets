"""Drive the Unity Editor with the exact policy assignments used by live training.

The observer authenticates to the loopback WAN actor broker, consumes the same published policy
snapshots as rollout workers, and samples live Torch policies through TorchPolicy.get_action().
Historical ONNX opponents remain deterministic because that is how training runs them. This process
never registers an actor, creates AgentManager trajectories, or uploads experience to the learner.
"""

from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
import sys
import tempfile
import time
from typing import Any, Dict, Mapping, Optional, Sequence, Tuple

import bees_wan_actor_training as wan
from bees_continual_evaluate import _import_mlagents, _validate_environment_behaviors
from bees_continual_historical import _build_frozen_onnx_policy
from bees_wan_actor_worker import (
    BrokerClient,
    BrokerSessionChanged,
    BrokerUnavailable,
    MAX_UNITY_SEED,
    _build_template_policy,
)


BEHAVIOR_NAME = "BeesRL1v1"
DEFAULT_POLICY_SYNC_SECONDS = 0.5


def _apply_environment_parameters(channel: Any, config: Any) -> None:
    if config is None:
        return
    if not isinstance(config, Mapping):
        raise RuntimeError(
            "Training broker environment parameters must be a mapping or null."
        )
    for key, value in config.items():
        try:
            numeric = float(value)
        except (TypeError, ValueError) as exc:
            raise RuntimeError(
                f"Training environment parameter {key!r} is not numeric: {value!r}."
            ) from exc
        channel.set_float_parameter(str(key), numeric)


def _policy_seed(run_options: Any, team: int, fallback_seed: int) -> int:
    try:
        configured = int(run_options.env_settings.seed)
    except (AttributeError, TypeError, ValueError):
        configured = int(fallback_seed)
    if configured < 0:
        configured = int(fallback_seed)
    return (configured + int(team)) & MAX_UNITY_SEED


def _write_frozen_policy(
    temporary_root: Path,
    behavior: str,
    payload: Mapping[str, Any],
) -> Path:
    model_bytes = payload.get("model_bytes")
    expected_hash = payload.get("model_sha256")
    if not isinstance(model_bytes, bytes) or not isinstance(expected_hash, str):
        raise RuntimeError(
            f"Frozen training policy {behavior!r} has a malformed ONNX payload."
        )
    actual_hash = hashlib.sha256(model_bytes).hexdigest()
    if actual_hash != expected_hash:
        raise RuntimeError(
            f"Frozen training policy {behavior!r} failed SHA-256 verification."
        )
    safe_behavior = hashlib.sha256(behavior.encode("utf-8")).hexdigest()[:12]
    model_path = temporary_root / f"{safe_behavior}-{actual_hash[:24]}.onnx"
    if model_path.exists():
        if hashlib.sha256(model_path.read_bytes()).hexdigest() != actual_hash:
            raise RuntimeError(
                f"Frozen observer policy cache conflicts for {behavior!r}."
            )
    else:
        model_path.write_bytes(model_bytes)
    return model_path


def _policy_from_payload(
    template: Any,
    behavior: str,
    payload: Mapping[str, Any],
    temporary_root: Path,
) -> Tuple[Any, str]:
    kind = payload.get("kind")
    if kind == "torch":
        weights = payload.get("weights")
        step = payload.get("step")
        if not isinstance(weights, Mapping) or not isinstance(step, int):
            raise RuntimeError(
                f"Live Torch policy {behavior!r} has a malformed broker payload."
            )
        template.load_weights(weights)
        template.set_step(step)
        return template, f"live Torch step {step} (stochastic)"

    if kind == "frozen_onnx":
        model_path = _write_frozen_policy(temporary_root, behavior, payload)
        provider = payload.get("provider")
        if provider is not None and not isinstance(provider, str):
            raise RuntimeError(
                f"Frozen training policy {behavior!r} has an invalid provider."
            )
        return (
            _build_frozen_onnx_policy(template, model_path, provider),
            "historical ONNX (deterministic, matching training)",
        )

    raise RuntimeError(
        f"Training broker published unsupported policy kind {kind!r} for {behavior!r}."
    )


def _synchronize_policies(
    *,
    client: BrokerClient,
    session_id: str,
    expected_behaviors: Sequence[str],
    templates: Mapping[str, Any],
    policies: Dict[str, Any],
    policy_versions: Dict[str, int],
    temporary_root: Path,
    state: Mapping[str, Any],
) -> None:
    remote_raw = state.get("policy_versions")
    if not isinstance(remote_raw, Mapping):
        raise RuntimeError("Training broker state is missing policy_versions.")
    remote_versions = {str(name): int(version) for name, version in remote_raw.items()}
    missing = sorted(set(expected_behaviors) - set(remote_versions))
    if missing:
        raise RuntimeError(
            "Training broker has not published all observed behaviors yet: " +
            ", ".join(missing)
        )

    for behavior in expected_behaviors:
        target_version = remote_versions[behavior]
        if policy_versions.get(behavior) == target_version:
            continue
        previous_version = policy_versions.get(behavior, -1)
        payload = client.policy(session_id, behavior, previous_version)
        if payload is None:
            policy_versions[behavior] = target_version
            continue
        template = templates[behavior]
        policy, description = _policy_from_payload(
            template,
            behavior,
            payload,
            temporary_root,
        )
        policies[behavior] = policy
        policy_versions[behavior] = target_version
        print(
            f"Observer synchronized {behavior} policy v{target_version}: {description}.",
            flush=True,
        )


def _wait_for_initial_policies(
    *,
    client: BrokerClient,
    session_id: str,
    control_epoch: int,
    expected_behaviors: Sequence[str],
    timeout_seconds: float,
) -> Mapping[str, Any]:
    deadline = time.monotonic() + max(1.0, float(timeout_seconds))
    while True:
        state = client.state(session_id, -1, control_epoch, 0.0)
        versions = state.get("policy_versions")
        if isinstance(versions, Mapping) and all(
            behavior in versions for behavior in expected_behaviors
        ):
            return state
        if time.monotonic() >= deadline:
            raise TimeoutError(
                "Timed out waiting for the live training broker to publish all policies."
            )
        time.sleep(0.1)


def _apply_new_control_if_available(
    *,
    client: BrokerClient,
    session_id: str,
    current_epoch: int,
    target_epoch: int,
    environment: Any,
    environment_parameters: Any,
) -> int:
    if target_epoch == current_epoch:
        return current_epoch

    control = client.control(session_id, current_epoch)
    if control is None or int(control.get("epoch", -1)) != target_epoch:
        return current_epoch

    kind = control.get("kind")
    _apply_environment_parameters(
        environment_parameters,
        control.get("config"),
    )
    if kind == "reset":
        environment.reset()
    elif kind != "parameters":
        raise RuntimeError(
            f"Training broker published unsupported control kind {kind!r}."
        )
    print(
        f"Observer applied training control epoch {target_epoch} ({kind}).",
        flush=True,
    )
    return target_epoch


def run_observer(
    *,
    broker_port: int,
    token_file: str,
    run_id: str,
    worker_id: int = 0,
    seed: int = 0,
    timeout_wait: int = 600,
    policy_sync_seconds: float = DEFAULT_POLICY_SYNC_SECONDS,
) -> None:
    token = wan.load_auth_token(token_file)
    client = BrokerClient("127.0.0.1", int(broker_port), token)
    session = client.session()
    session_id = str(session.get("session_id") or "").strip()
    broker_run_id = str(session.get("run_id") or "").strip()
    run_options = session.get("run_options")
    if not session_id or run_options is None:
        raise RuntimeError("Training broker session is missing policy/run configuration.")
    if broker_run_id != str(run_id).strip():
        raise RuntimeError(
            f"Training broker run changed: expected {run_id!r}, active {broker_run_id!r}."
        )

    initial_control = client.control(session_id, -1)
    if initial_control is None:
        raise RuntimeError("Training broker has no active environment control record.")
    control_epoch = int(initial_control.get("epoch", 0))
    control_kind = initial_control.get("kind")
    if control_kind not in ("reset", "parameters"):
        raise RuntimeError(
            f"Training broker has unsupported initial control kind {control_kind!r}."
        )

    UnityEnvironment, _ActionTuple, _ = _import_mlagents()
    from mlagents_envs.side_channel.environment_parameters_channel import (
        EnvironmentParametersChannel,
    )

    environment_parameters = EnvironmentParametersChannel()
    _apply_environment_parameters(
        environment_parameters,
        initial_control.get("config"),
    )

    environment = None
    connected = False
    with tempfile.TemporaryDirectory(prefix="bees-observe-policy-") as temporary:
        temporary_root = Path(temporary)
        try:
            print(
                "Waiting for the Unity Editor ML-Agents connection on the default editor port...",
                flush=True,
            )
            environment = UnityEnvironment(
                file_name=None,
                worker_id=int(worker_id),
                seed=int(seed),
                no_graphics=False,
                timeout_wait=int(timeout_wait),
                side_channels=[environment_parameters],
            )
            environment.reset()
            teams = _validate_environment_behaviors(
                environment.behavior_specs,
                BEHAVIOR_NAME,
            )
            expected_behaviors = [teams[team] for team in sorted(teams)]

            templates: Dict[str, Any] = {}
            policies: Dict[str, Any] = {}
            policy_versions: Dict[str, int] = {}
            for team, behavior in teams.items():
                templates[behavior] = _build_template_policy(
                    behavior,
                    environment.behavior_specs[behavior],
                    run_options,
                    seed=_policy_seed(run_options, team, seed),
                )

            state = _wait_for_initial_policies(
                client=client,
                session_id=session_id,
                control_epoch=control_epoch,
                expected_behaviors=expected_behaviors,
                timeout_seconds=timeout_wait,
            )
            while int(state.get("control_epoch", control_epoch)) != control_epoch:
                target_control = int(state.get("control_epoch", control_epoch))
                updated = _apply_new_control_if_available(
                    client=client,
                    session_id=session_id,
                    current_epoch=control_epoch,
                    target_epoch=target_control,
                    environment=environment,
                    environment_parameters=environment_parameters,
                )
                if updated == control_epoch:
                    state = client.state(
                        session_id,
                        int(state.get("policy_epoch", -1)),
                        control_epoch,
                        0.0,
                    )
                    continue
                control_epoch = updated
                state = client.state(session_id, -1, control_epoch, 0.0)

            _synchronize_policies(
                client=client,
                session_id=session_id,
                expected_behaviors=expected_behaviors,
                templates=templates,
                policies=policies,
                policy_versions=policy_versions,
                temporary_root=temporary_root,
                state=state,
            )
            policy_epoch = int(state.get("policy_epoch", -1))
            connected = True
            print(
                "Unity Editor connected. Sampling live training policies; no observer experience " +
                "is registered with or uploaded to PPO. Stop Play mode to finish.",
                flush=True,
            )

            last_sync = time.monotonic()
            broker_warning = None
            while True:
                now = time.monotonic()
                if now - last_sync >= max(0.05, float(policy_sync_seconds)):
                    last_sync = now
                    try:
                        state = client.state(
                            session_id,
                            policy_epoch,
                            control_epoch,
                            0.0,
                        )
                        target_control = int(
                            state.get("control_epoch", control_epoch)
                        )
                        updated_control = _apply_new_control_if_available(
                            client=client,
                            session_id=session_id,
                            current_epoch=control_epoch,
                            target_epoch=target_control,
                            environment=environment,
                            environment_parameters=environment_parameters,
                        )
                        if updated_control != control_epoch:
                            control_epoch = updated_control
                            state = client.state(
                                session_id,
                                -1,
                                control_epoch,
                                0.0,
                            )

                        _synchronize_policies(
                            client=client,
                            session_id=session_id,
                            expected_behaviors=expected_behaviors,
                            templates=templates,
                            policies=policies,
                            policy_versions=policy_versions,
                            temporary_root=temporary_root,
                            state=state,
                        )
                        policy_epoch = int(state.get("policy_epoch", policy_epoch))
                        broker_warning = None
                    except BrokerUnavailable as exc:
                        message = str(exc)
                        if message != broker_warning:
                            print(
                                "Training broker temporarily unavailable; continuing with the last " +
                                f"synchronized policies: {message}",
                                file=sys.stderr,
                                flush=True,
                            )
                            broker_warning = message

                for team in sorted(teams):
                    behavior = teams[team]
                    decision_steps, _terminal_steps = environment.get_steps(behavior)
                    if len(decision_steps) == 0:
                        continue
                    policy = policies.get(behavior)
                    if policy is None:
                        raise RuntimeError(
                            f"Observer has no synchronized policy for {behavior!r}."
                        )
                    action_info = policy.get_action(
                        decision_steps,
                        worker_id=int(worker_id),
                    )
                    actions = (
                        action_info.env_action
                        if action_info.env_action is not None
                        else action_info.action
                    )
                    environment.set_actions(behavior, actions)
                environment.step()

        except KeyboardInterrupt:
            print("RL visual observation interrupted.", flush=True)
        except BrokerSessionChanged:
            print(
                "Training broker session changed; ending this observation so a new command can " +
                "bind to the new authoritative learner session.",
                flush=True,
            )
        except Exception as exc:
            if connected and type(exc).__module__.startswith("mlagents_envs"):
                print(
                    f"Unity Editor observation ended: {type(exc).__name__}: {exc}",
                    flush=True,
                )
                return
            raise
        finally:
            if environment is not None:
                try:
                    environment.close()
                except Exception:
                    pass


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Drive the Bees Unity Editor from the live training broker using rollout-style "
            "policy sampling without contributing trajectories."
        )
    )
    parser.add_argument("--broker-port", type=int, required=True)
    parser.add_argument("--token-file", required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--worker-id", type=int, default=0)
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--timeout-wait", type=int, default=600)
    parser.add_argument(
        "--policy-sync-seconds",
        type=float,
        default=DEFAULT_POLICY_SYNC_SECONDS,
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        run_observer(
            broker_port=args.broker_port,
            token_file=args.token_file,
            run_id=args.run_id,
            worker_id=args.worker_id,
            seed=args.seed,
            timeout_wait=args.timeout_wait,
            policy_sync_seconds=args.policy_sync_seconds,
        )
        return 0
    except Exception as exc:
        print(
            f"RL visual observer failed: {type(exc).__name__}: {exc}",
            file=sys.stderr,
            flush=True,
        )
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
