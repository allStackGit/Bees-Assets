"""Focused regression tests for Training/bees_mlagents_ppo_compat.py.

Run from the Bees Assets root inside the ML-Agents virtual environment:

    python Training\bees_mlagents_ppo_compat_tests.py
"""

from __future__ import annotations

import importlib.util
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
            if 'installed_original' in locals():
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
            RewardSignalUtil.value_estimates_key = staticmethod(original)


if __name__ == "__main__":
    unittest.main()
