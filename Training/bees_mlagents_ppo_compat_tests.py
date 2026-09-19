"""Focused regression tests for Training/bees_mlagents_ppo_compat.py.

Run from the Bees Assets root inside the ML-Agents virtual environment:

    python Training\bees_mlagents_ppo_compat_tests.py
"""

from __future__ import annotations

import importlib.util
import math
from pathlib import Path
from types import SimpleNamespace
import unittest


COMPAT_PATH = Path(__file__).with_name("bees_mlagents_ppo_compat.py")
SPEC = importlib.util.spec_from_file_location("bees_mlagents_ppo_compat", COMPAT_PATH)
compat = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(compat)


class InactiveContinuousActionMaskTests(unittest.TestCase):
    def tearDown(self):
        compat.restore_inactive_continuous_action_masking()

    @staticmethod
    def _bees_action_spec():
        return SimpleNamespace(
            continuous_size=compat.BEES_CONTINUOUS_ACTIONS,
            discrete_branches=compat.BEES_DISCRETE_BRANCHES,
            discrete_size=len(compat.BEES_DISCRETE_BRANCHES),
        )

    def test_existing_weapon_fire_masks_select_matching_aim_pairs(self):
        from mlagents.torch_utils import torch

        action_spec = self._bees_action_spec()
        masks = torch.ones((2, sum(compat.BEES_DISCRETE_BRANCHES)))

        # Sample 0 has one turret. Sample 1 has four turrets.
        for slot in range(1, compat.BEES_WEAPON_SLOTS):
            masks[0, slot * 2 + 1] = 0.0
        for slot in range(4, compat.BEES_WEAPON_SLOTS):
            masks[1, slot * 2 + 1] = 0.0

        reference = torch.ones((2, compat.BEES_CONTINUOUS_ACTIONS))
        activity = compat._build_bees_continuous_activity_mask(
            action_spec,
            masks,
            reference,
        )

        self.assertIsNotNone(activity)
        self.assertEqual(float(activity[0].sum().item()), 4.0)
        self.assertEqual(float(activity[1].sum().item()), 10.0)
        self.assertTrue(torch.all(activity[:, :2] == 1.0))
        self.assertTrue(torch.all(activity[0, 2:4] == 1.0))
        self.assertTrue(torch.all(activity[0, 4:] == 0.0))
        self.assertTrue(torch.all(activity[1, 2:10] == 1.0))
        self.assertTrue(torch.all(activity[1, 10:] == 0.0))

    def test_policy_loss_ignores_masked_dimensions_but_uses_active_ones(self):
        from mlagents.torch_utils import torch

        advantages = torch.tensor([1.0])
        old_log_probs = torch.zeros((1, 4))
        loss_masks = torch.tensor([True])
        dimension_mask = torch.tensor([[1.0, 0.0, 1.0, 0.0]])

        baseline = torch.tensor([[0.05, 0.0, 0.10, 0.0]])
        inactive_changed = torch.tensor([[0.05, 8.0, 0.10, -8.0]])
        active_changed = torch.tensor([[0.15, 8.0, 0.10, -8.0]])

        baseline_loss = compat._trust_region_policy_loss_with_dimension_mask(
            advantages,
            baseline,
            old_log_probs,
            loss_masks,
            0.2,
            dimension_mask,
        )
        inactive_loss = compat._trust_region_policy_loss_with_dimension_mask(
            advantages,
            inactive_changed,
            old_log_probs,
            loss_masks,
            0.2,
            dimension_mask,
        )
        active_loss = compat._trust_region_policy_loss_with_dimension_mask(
            advantages,
            active_changed,
            old_log_probs,
            loss_masks,
            0.2,
            dimension_mask,
        )

        self.assertAlmostEqual(
            float(baseline_loss.item()),
            float(inactive_loss.item()),
            places=6,
        )
        self.assertNotAlmostEqual(
            float(baseline_loss.item()),
            float(active_loss.item()),
            places=6,
        )

    def test_non_bees_action_shape_is_not_masked(self):
        from mlagents.torch_utils import torch

        action_spec = SimpleNamespace(
            continuous_size=4,
            discrete_branches=(2,),
            discrete_size=1,
        )
        activity = compat._build_bees_continuous_activity_mask(
            action_spec,
            torch.ones((1, 2)),
            torch.ones((1, 4)),
        )
        self.assertIsNone(activity)

    def test_install_and_restore_patch_action_model_and_ppo_loss(self):
        from mlagents.trainers.ppo.optimizer_torch import TorchPPOOptimizer
        from mlagents.trainers.torch_entities.action_model import ActionModel
        from mlagents.trainers.torch_entities.utils import ModelUtils

        original_forward = ActionModel.forward
        original_evaluate = ActionModel.evaluate
        original_update = TorchPPOOptimizer.update
        original_policy_loss = ModelUtils.trust_region_policy_loss

        installed_original = compat.install_inactive_continuous_action_masking()
        self.assertIs(installed_original, original_forward)
        self.assertIsNot(ActionModel.forward, original_forward)
        self.assertIsNot(ActionModel.evaluate, original_evaluate)
        self.assertIsNot(TorchPPOOptimizer.update, original_update)
        self.assertIsNot(ModelUtils.trust_region_policy_loss, original_policy_loss)

        compat.restore_inactive_continuous_action_masking()
        self.assertIs(ActionModel.forward, original_forward)
        self.assertIs(ActionModel.evaluate, original_evaluate)
        self.assertIs(TorchPPOOptimizer.update, original_update)
        self.assertIs(ModelUtils.trust_region_policy_loss, original_policy_loss)


class FixedBetaCompatibilityTests(unittest.TestCase):
    def test_adaptive_beta_patch_is_not_present(self):
        self.assertFalse(hasattr(compat, "AdaptiveExplorationController"))
        self.assertFalse(hasattr(compat, "install_adaptive_exploration"))


class ValueEstimateKeyCompatibilityTests(unittest.TestCase):
    def tearDown(self):
        compat.restore_inactive_continuous_action_masking()
        compat.restore_continuous_sigma_guard()

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
            compat.restore_value_estimate_key(None)
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
            compat.restore_value_estimate_key(None)
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


