"""Regression tests for ML-Agents 1.1.0 ONNX recurrent-input semantics."""

from __future__ import annotations

import contextlib
import importlib.util
from pathlib import Path
import sys
import types
import unittest


MODULE_PATH = Path(__file__).with_name("bees_continual_historical.py")
SPEC = importlib.util.spec_from_file_location("bees_continual_historical", MODULE_PATH)
historical = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = historical
assert SPEC.loader is not None
SPEC.loader.exec_module(historical)


class _FakeEngine:
    def __init__(self, *, memory_size: int, has_recurrent_input: bool = True) -> None:
        self.memory_size = memory_size
        self.has_recurrent_input = has_recurrent_input
        self.validated_specs = []

    def validate_behavior_spec(self, behavior_spec) -> None:
        self.validated_specs.append(behavior_spec)


class _FakePolicy:
    def __init__(self, seed, behavior_spec, network_settings) -> None:
        self.seed = seed
        self.behavior_spec = behavior_spec
        self.network_settings = network_settings

    def check_nan_action(self, action) -> None:
        return None


class _FakeActionInfo:
    @staticmethod
    def empty():
        return _FakeActionInfo()

    def __init__(self, *args, **kwargs) -> None:
        self.args = args
        self.kwargs = kwargs


class _FakeActionTuple:
    pass


@contextlib.contextmanager
def _patched_mlagents_runtime(engine: _FakeEngine):
    module_names = (
        "mlagents",
        "mlagents.trainers",
        "mlagents.trainers.action_info",
        "mlagents.trainers.policy",
        "mlagents_envs",
        "mlagents_envs.base_env",
        "bees_continual_evaluate",
    )
    previous = {name: sys.modules.get(name) for name in module_names}

    mlagents = types.ModuleType("mlagents")
    mlagents.__path__ = []
    trainers = types.ModuleType("mlagents.trainers")
    trainers.__path__ = []
    action_info = types.ModuleType("mlagents.trainers.action_info")
    action_info.ActionInfo = _FakeActionInfo
    policy = types.ModuleType("mlagents.trainers.policy")
    policy.Policy = _FakePolicy
    mlagents_envs = types.ModuleType("mlagents_envs")
    mlagents_envs.__path__ = []
    base_env = types.ModuleType("mlagents_envs.base_env")
    base_env.ActionTuple = _FakeActionTuple
    evaluator = types.ModuleType("bees_continual_evaluate")
    evaluator.OnnxPolicy = lambda *args, **kwargs: engine

    replacements = {
        "mlagents": mlagents,
        "mlagents.trainers": trainers,
        "mlagents.trainers.action_info": action_info,
        "mlagents.trainers.policy": policy,
        "mlagents_envs": mlagents_envs,
        "mlagents_envs.base_env": base_env,
        "bees_continual_evaluate": evaluator,
    }
    sys.modules.update(replacements)
    try:
        yield
    finally:
        for name, value in previous.items():
            if value is None:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = value


def _template(*, use_recurrent: bool = False):
    return types.SimpleNamespace(
        seed=17,
        use_recurrent=use_recurrent,
        behavior_spec=object(),
        network_settings=object(),
    )


class FrozenOnnxMemoryContractTests(unittest.TestCase):
    def test_zero_width_recurrent_placeholder_is_feed_forward(self):
        # ML-Agents 1.1.0 ModelSerializer always exports recurrent_in. For a
        # feed-forward policy its last dimension is zero, so presence alone must
        # not cause the historical bridge to reject the model as recurrent.
        engine = _FakeEngine(memory_size=0, has_recurrent_input=True)
        template = _template(use_recurrent=False)

        with _patched_mlagents_runtime(engine):
            frozen = historical._build_frozen_onnx_policy(
                template,
                Path("feed-forward.onnx"),
                "CPUExecutionProvider",
            )

        self.assertIs(frozen._engine, engine)
        self.assertEqual(engine.validated_specs, [template.behavior_spec])
        self.assertTrue(frozen.bees_batch_inference_safe)

    def test_positive_onnx_memory_is_still_rejected(self):
        engine = _FakeEngine(memory_size=128, has_recurrent_input=True)

        with _patched_mlagents_runtime(engine):
            with self.assertRaisesRegex(
                historical.HistoricalOpponentError,
                "feed-forward",
            ):
                historical._build_frozen_onnx_policy(
                    _template(use_recurrent=False),
                    Path("recurrent.onnx"),
                    "CPUExecutionProvider",
                )

        self.assertEqual(engine.validated_specs, [])

    def test_recurrent_template_policy_is_still_rejected(self):
        engine = _FakeEngine(memory_size=0, has_recurrent_input=True)

        with _patched_mlagents_runtime(engine):
            with self.assertRaisesRegex(
                historical.HistoricalOpponentError,
                "feed-forward",
            ):
                historical._build_frozen_onnx_policy(
                    _template(use_recurrent=True),
                    Path("template-recurrent.onnx"),
                    None,
                )


if __name__ == "__main__":
    unittest.main()
