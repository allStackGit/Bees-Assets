"""Drive the Unity Editor RL training scene from one frozen ONNX policy snapshot.

This is intentionally inference-only. It connects to the editor through the ML-Agents low-level
API, applies the same immutable snapshot to both team behaviors, and never creates PPO trajectories,
optimizer state, checkpoints, or continual-learning records.
"""

from __future__ import annotations

import argparse
from pathlib import Path
import sys
from typing import Optional, Sequence

from bees_continual_evaluate import (
    OnnxPolicy,
    _import_mlagents,
    _validate_environment_behaviors,
)


BEHAVIOR_NAME = "BeesRL1v1"


def run_observer(
    *,
    model_path: str,
    worker_id: int = 0,
    seed: int = 0,
    timeout_wait: int = 600,
    onnx_provider: Optional[str] = None,
) -> None:
    model = Path(model_path).expanduser().resolve()
    if not model.is_file():
        raise FileNotFoundError(f"observer model does not exist: {model}")

    UnityEnvironment, ActionTuple, _ = _import_mlagents()
    policies = {
        0: OnnxPolicy(model, provider=onnx_provider),
        1: OnnxPolicy(model, provider=onnx_provider),
    }
    environment = None
    connected = False
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
        )
        environment.reset()
        teams = _validate_environment_behaviors(
            environment.behavior_specs,
            BEHAVIOR_NAME,
        )
        for team, name in teams.items():
            policies[team].validate_behavior_spec(environment.behavior_specs[name])
        connected = True
        print(
            f"Unity Editor connected. Running deterministic inference from {model.name}; "
            "stop Play mode to finish.",
            flush=True,
        )

        while True:
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
    except KeyboardInterrupt:
        print("RL visual observation interrupted.", flush=True)
    except Exception as exc:
        # Stopping Play mode closes the Editor communicator. Once a valid connection has
        # existed, communication shutdown is the normal operator-controlled end of observation.
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
        description="Drive the Bees Unity Editor training scene from a frozen ONNX snapshot."
    )
    parser.add_argument("--model", required=True)
    parser.add_argument("--worker-id", type=int, default=0)
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--timeout-wait", type=int, default=600)
    parser.add_argument("--onnx-provider")
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        run_observer(
            model_path=args.model,
            worker_id=args.worker_id,
            seed=args.seed,
            timeout_wait=args.timeout_wait,
            onnx_provider=args.onnx_provider,
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
