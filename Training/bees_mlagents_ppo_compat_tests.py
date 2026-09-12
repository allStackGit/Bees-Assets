"""Focused regression tests for Training/bees_mlagents_ppo_compat.py.

Run from the Bees Assets root inside the ML-Agents virtual environment:

    python Training\bees_mlagents_ppo_compat_tests.py
"""

from __future__ import annotations

import importlib.util
import math
from pathlib import Path
import unittest


COMPAT_PATH = Path(__file__).with_name("bees_mlagents_ppo_compat.py")
SPEC = importlib.util.spec_from_file_location("bees_mlagents_ppo_compat", COMPAT_PATH)
compat = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(compat)


class ValueEstimateKeyCompatibilityTests(unittest.TestCase):
    def test_fix_separates_old_value_estimates_from_returns(self):
        from mlagents.trainers.buffer import RewardSignalKeyPrefix, RewardSignalUtil

        original = RewardSignalUtil.value_estimates_key
        try:
            installed_original = compat.install_value_estimate_key_fix()
            self.assertEqual(
                RewardSignalUtil.value_estimates_key("extrinsic"),
                (RewardSignalKeyPrefix.VALUE_ESTIMATES, "extrinsic"),
            )
            self.assertEqual(
                RewardSignalUtil.returns_key("extrinsic"),
                (RewardSignalKeyPrefix.RETURNS, "extrinsic"),
            )
            self.assertNotEqual(
                RewardSignalUtil.value_estimates_key("extrinsic"),
                RewardSignalUtil.returns_key("extrinsic"),
            )
        finally:
            if "installed_original" in locals():
                compat.restore_value_estimate_key(installed_original)
            else:
                RewardSignalUtil.value_estimates_key = staticmethod(original)

    def test_fix_is_idempotent_when_vendor_method_is_already_correct(self):
        from mlagents.trainers.buffer import RewardSignalKeyPrefix, RewardSignalUtil

        original = RewardSignalUtil.value_estimates_key

        def already_correct(name: str):
            return RewardSignalKeyPrefix.VALUE_ESTIMATES, name

        try:
            RewardSignalUtil.value_estimates_key = staticmethod(already_correct)
            installed_original = compat.install_value_estimate_key_fix()
            self.assertIsNone(installed_original)
            self.assertEqual(
                RewardSignalUtil.value_estimates_key("extrinsic"),
                (RewardSignalKeyPrefix.VALUE_ESTIMATES, "extrinsic"),
            )
        finally:
            compat.restore_continuous_sigma_guard()
            RewardSignalUtil.value_estimates_key = staticmethod(original)

    def test_fix_refuses_unknown_key_layout(self):
        from mlagents.trainers.buffer import RewardSignalKeyPrefix, RewardSignalUtil

        original = RewardSignalUtil.value_estimates_key

        def unexpected(name: str):
            return RewardSignalKeyPrefix.ADVANTAGE, name

        try:
            RewardSignalUtil.value_estimates_key = staticmethod(unexpected)
            with self.assertRaises(RuntimeError):
                compat.install_value_estimate_key_fix()
        finally:
            compat.restore_continuous_sigma_guard()
            RewardSignalUtil.value_estimates_key = staticmethod(original)


class ContinuousSigmaGuardTests(unittest.TestCase):
    def tearDown(self):
        compat.restore_continuous_sigma_guard()

    def test_unconditional_sigma_is_projected_before_forward(self):
        from mlagents.torch_utils import torch
        from mlagents.trainers.torch_entities.distributions import GaussianDistribution

        distribution = GaussianDistribution(hidden_size=4, num_outputs=2)
        with torch.no_grad():
            distribution.log_sigma.fill_(math.log(12.0))

        compat.install_continuous_sigma_guard()
        instance = distribution(torch.zeros((3, 4)))

        self.assertLessEqual(
            float(instance.std.max().item()), compat.MAX_CONTINUOUS_SIGMA + 1e-6
        )
        self.assertLessEqual(
            float(torch.exp(distribution.log_sigma).max().item()),
            compat.MAX_CONTINUOUS_SIGMA + 1e-6,
        )

    def test_conditional_sigma_output_is_bounded(self):
        from mlagents.torch_utils import torch
        from mlagents.trainers.torch_entities.distributions import GaussianDistribution

        distribution = GaussianDistribution(
            hidden_size=4,
            num_outputs=2,
            conditional_sigma=True,
        )
        with torch.no_grad():
            distribution.log_sigma.weight.zero_()
            distribution.log_sigma.bias.fill_(math.log(12.0))

        compat.install_continuous_sigma_guard()
        instance = distribution(torch.zeros((3, 4)))

        self.assertLessEqual(
            float(instance.std.max().item()), compat.MAX_CONTINUOUS_SIGMA + 1e-6
        )

    def test_restore_reinstates_vendor_forward(self):
        from mlagents.trainers.torch_entities.distributions import GaussianDistribution

        original = GaussianDistribution.forward
        installed_original = compat.install_continuous_sigma_guard()
        self.assertIs(installed_original, original)
        self.assertIsNot(GaussianDistribution.forward, original)

        compat.restore_continuous_sigma_guard(installed_original)
        self.assertIs(GaussianDistribution.forward, original)

    def test_invalid_sigma_limit_is_rejected(self):
        for invalid in (0.0, -1.0, float("inf"), float("nan")):
            with self.subTest(invalid=invalid):
                with self.assertRaises(ValueError):
                    compat.install_continuous_sigma_guard(invalid)


if __name__ == "__main__":
    unittest.main()
