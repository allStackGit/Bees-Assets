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
from unittest import mock


COMPAT_PATH = Path(__file__).with_name("bees_mlagents_ppo_compat.py")
SPEC = importlib.util.spec_from_file_location("bees_mlagents_ppo_compat", COMPAT_PATH)
compat = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(compat)


class InactiveContinuousActionMaskTests(unittest.TestCase):
    def test_action_layout_matches_current_five_weapon_policy(self):
        self.assertEqual(compat.BEES_WEAPON_SLOTS, 5)
        self.assertEqual(compat.BEES_CONTINUOUS_ACTIONS, 16)
        self.assertEqual(compat.BEES_DISCRETE_BRANCHES, (2,) * 5 + (5,))

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
        self.assertEqual(float(activity[0].sum().item()), 8.0)
        self.assertEqual(float(activity[1].sum().item()), 14.0)
        self.assertTrue(torch.all(activity[:, :2] == 1.0))
        self.assertTrue(torch.all(activity[0, 2:4] == 1.0))
        self.assertTrue(torch.all(activity[0, 4:12] == 0.0))
        self.assertTrue(torch.all(activity[1, 2:10] == 1.0))
        self.assertTrue(torch.all(activity[1, 10:12] == 0.0))
        self.assertTrue(torch.all(activity[:, 12:16] == 1.0))

    def test_policy_dimension_mask_excludes_missing_aim_and_fire_actions(self):
        from mlagents.torch_utils import torch

        action_spec = self._bees_action_spec()
        masks = torch.ones((2, sum(compat.BEES_DISCRETE_BRANCHES)))

        # Sample 0 has one turret; sample 1 has four.
        for slot in range(1, compat.BEES_WEAPON_SLOTS):
            masks[0, slot * 2 + 1] = 0.0
        for slot in range(4, compat.BEES_WEAPON_SLOTS):
            masks[1, slot * 2 + 1] = 0.0

        dimension_mask = compat._build_bees_policy_dimension_mask(
            action_spec,
            masks,
        )

        self.assertEqual(
            dimension_mask.shape,
            (2, compat.BEES_CONTINUOUS_ACTIONS + len(compat.BEES_DISCRETE_BRANCHES)),
        )
        discrete = dimension_mask[:, compat.BEES_CONTINUOUS_ACTIONS :]
        self.assertTrue(torch.equal(
            discrete[0],
            torch.tensor([1.0, 0.0, 0.0, 0.0, 0.0, 1.0]),
        ))
        self.assertTrue(torch.equal(
            discrete[1],
            torch.tensor([1.0, 1.0, 1.0, 1.0, 0.0, 1.0]),
        ))
        self.assertEqual(float(dimension_mask[0].sum().item()), 10.0)
        self.assertEqual(float(dimension_mask[1].sum().item()), 19.0)

    def test_policy_dimension_mask_excludes_movement_for_immobile_ship(self):
        from mlagents.torch_utils import torch

        action_spec = self._bees_action_spec()
        masks = torch.ones((2, sum(compat.BEES_DISCRETE_BRANCHES)))
        movement_activity = torch.tensor([0.0, 1.0])

        dimension_mask = compat._build_bees_policy_dimension_mask(
            action_spec,
            masks,
            movement_activity=movement_activity,
        )

        self.assertTrue(torch.all(dimension_mask[0, :2] == 0.0))
        self.assertTrue(torch.all(dimension_mask[1, :2] == 1.0))
        self.assertTrue(torch.all(dimension_mask[:, 2:compat.BEES_CONTINUOUS_ACTIONS] == 1.0))

    def test_policy_dimension_mask_excludes_solo_communication(self):
        from mlagents.torch_utils import torch

        action_spec = self._bees_action_spec()
        masks = torch.ones((2, sum(compat.BEES_DISCRETE_BRANCHES)))
        communication_activity = torch.tensor([0.0, 1.0])

        dimension_mask = compat._build_bees_policy_dimension_mask(
            action_spec,
            masks,
            communication_activity=communication_activity,
        )
        communication_start = (
            compat.BEES_MOVEMENT_CONTINUOUS_ACTIONS
            + compat.BEES_WEAPON_SLOTS
            * compat.BEES_WEAPON_AIM_ACTIONS_PER_SLOT
        )

        self.assertTrue(torch.all(
            dimension_mask[
                0,
                communication_start :
                communication_start + compat.BEES_COMMUNICATION_CONTINUOUS_ACTIONS,
            ] == 0.0
        ))
        self.assertTrue(torch.all(
            dimension_mask[
                1,
                communication_start :
                communication_start + compat.BEES_COMMUNICATION_CONTINUOUS_ACTIONS,
            ] == 1.0
        ))

    def test_policy_dimension_mask_excludes_forced_noop_capability_branch(self):
        from mlagents.torch_utils import torch

        action_spec = self._bees_action_spec()
        masks = torch.ones((1, sum(compat.BEES_DISCRETE_BRANCHES)))
        special_start = sum(compat.BEES_DISCRETE_BRANCHES[:-1])
        masks[
            :,
            special_start + 1 :
            special_start + compat.BEES_DISCRETE_BRANCHES[-1],
        ] = 0.0

        dimension_mask = compat._build_bees_policy_dimension_mask(
            action_spec,
            masks,
        )

        self.assertEqual(
            float(dimension_mask[0, compat.BEES_CONTINUOUS_ACTIONS + compat.BEES_WEAPON_SLOTS].item()),
            0.0,
        )

    def test_policy_dimension_mask_excludes_fire_branches_while_healing(self):
        from mlagents.torch_utils import torch

        action_spec = self._bees_action_spec()
        masks = torch.ones((1, sum(compat.BEES_DISCRETE_BRANCHES)))
        discrete_actions = torch.zeros(
            (1, len(compat.BEES_DISCRETE_BRANCHES))
        )
        discrete_actions[0, compat.BEES_WEAPON_SLOTS] = (
            compat.BEES_HEALING_SPECIAL_ACTION
        )

        dimension_mask = compat._build_bees_policy_dimension_mask(
            action_spec,
            masks,
            discrete_actions=discrete_actions,
        )

        fire_dimensions = dimension_mask[
            0,
            compat.BEES_CONTINUOUS_ACTIONS :
            compat.BEES_CONTINUOUS_ACTIONS + compat.BEES_WEAPON_SLOTS,
        ]
        self.assertTrue(torch.all(fire_dimensions == 0.0))
        self.assertEqual(
            float(dimension_mask[
                0,
                compat.BEES_CONTINUOUS_ACTIONS + compat.BEES_WEAPON_SLOTS,
            ].item()),
            1.0,
        )

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
        from mlagents.trainers.poca.optimizer_torch import TorchPOCAOptimizer
        from mlagents.trainers.poca.trainer import POCATrainer
        from mlagents.trainers.ppo.optimizer_torch import TorchPPOOptimizer
        from mlagents.trainers.torch_entities.action_model import ActionModel
        from mlagents.trainers.torch_entities.components.bc.module import BCModule
        from mlagents.trainers.torch_entities.utils import ModelUtils

        original_forward = ActionModel.forward
        original_evaluate = ActionModel.evaluate
        original_ppo_update = TorchPPOOptimizer.update
        original_poca_update = TorchPOCAOptimizer.update
        original_poca_update_policy = POCATrainer._update_policy
        original_poca_advance = POCATrainer.advance
        original_policy_loss = ModelUtils.trust_region_policy_loss
        original_masked_mean = ModelUtils.masked_mean
        original_bc_update = BCModule._update_batch
        original_bc_loss = BCModule._behavioral_cloning_loss

        installed_original = compat.install_inactive_continuous_action_masking()
        self.assertIs(installed_original, original_forward)
        self.assertIsNot(ActionModel.forward, original_forward)
        self.assertIsNot(ActionModel.evaluate, original_evaluate)
        self.assertIsNot(TorchPPOOptimizer.update, original_ppo_update)
        self.assertIsNot(TorchPOCAOptimizer.update, original_poca_update)
        self.assertIsNot(POCATrainer._update_policy, original_poca_update_policy)
        self.assertIsNot(POCATrainer.advance, original_poca_advance)
        self.assertIsNot(ModelUtils.trust_region_policy_loss, original_policy_loss)
        self.assertIsNot(ModelUtils.masked_mean, original_masked_mean)
        self.assertIsNot(BCModule._update_batch, original_bc_update)
        self.assertIsNot(BCModule._behavioral_cloning_loss, original_bc_loss)

        compat.restore_inactive_continuous_action_masking()
        self.assertIs(ActionModel.forward, original_forward)
        self.assertIs(ActionModel.evaluate, original_evaluate)
        self.assertIs(TorchPPOOptimizer.update, original_ppo_update)
        self.assertIs(TorchPOCAOptimizer.update, original_poca_update)
        self.assertIs(POCATrainer._update_policy, original_poca_update_policy)
        self.assertIs(POCATrainer.advance, original_poca_advance)
        self.assertIs(ModelUtils.trust_region_policy_loss, original_policy_loss)
        self.assertIs(ModelUtils.masked_mean, original_masked_mean)
        self.assertIs(BCModule._update_batch, original_bc_update)
        self.assertIs(BCModule._behavioral_cloning_loss, original_bc_loss)


class BehavioralCloningWeaponMaskTests(unittest.TestCase):
    @staticmethod
    def _policy():
        action_spec = SimpleNamespace(
            continuous_size=compat.BEES_CONTINUOUS_ACTIONS,
            discrete_branches=compat.BEES_DISCRETE_BRANCHES,
            discrete_size=len(compat.BEES_DISCRETE_BRANCHES),
        )
        return SimpleNamespace(
            behavior_spec=SimpleNamespace(action_spec=action_spec)
        )

    def test_inactive_weapon_aim_does_not_change_bc_loss(self):
        import numpy as np
        from mlagents.torch_utils import torch

        policy = self._policy()
        expert = SimpleNamespace(
            continuous_tensor=torch.zeros((1, compat.BEES_CONTINUOUS_ACTIONS)),
            discrete_tensor=torch.zeros(
                (1, len(compat.BEES_DISCRETE_BRANCHES)),
                dtype=torch.long,
            ),
        )
        logits = torch.zeros((1, sum(compat.BEES_DISCRETE_BRANCHES)))
        log_probs = SimpleNamespace(all_discrete_tensor=logits)
        activity = np.ones((1, compat.BEES_WEAPON_SLOTS), dtype=np.float32)
        activity[0, 0] = 0.0

        baseline_actions = SimpleNamespace(
            continuous_tensor=torch.zeros((1, compat.BEES_CONTINUOUS_ACTIONS))
        )
        inactive_changed = SimpleNamespace(
            continuous_tensor=baseline_actions.continuous_tensor.clone()
        )
        inactive_changed.continuous_tensor[0, 2] = 10.0
        active_changed = SimpleNamespace(
            continuous_tensor=baseline_actions.continuous_tensor.clone()
        )
        active_changed.continuous_tensor[0, 4] = 10.0

        baseline = compat._bees_masked_behavioral_cloning_loss(
            policy, baseline_actions, log_probs, expert, activity
        )
        inactive = compat._bees_masked_behavioral_cloning_loss(
            policy, inactive_changed, log_probs, expert, activity
        )
        active = compat._bees_masked_behavioral_cloning_loss(
            policy, active_changed, log_probs, expert, activity
        )

        self.assertAlmostEqual(float(baseline.item()), float(inactive.item()), places=6)
        self.assertGreater(float(active.item()), float(baseline.item()))

    def test_immobile_movement_does_not_change_bc_loss(self):
        import numpy as np
        from mlagents.torch_utils import torch

        policy = self._policy()
        expert = SimpleNamespace(
            continuous_tensor=torch.zeros((1, compat.BEES_CONTINUOUS_ACTIONS)),
            discrete_tensor=torch.zeros(
                (1, len(compat.BEES_DISCRETE_BRANCHES)),
                dtype=torch.long,
            ),
        )
        log_probs = SimpleNamespace(
            all_discrete_tensor=torch.zeros(
                (1, sum(compat.BEES_DISCRETE_BRANCHES))
            )
        )
        weapon_activity = np.ones(
            (1, compat.BEES_WEAPON_SLOTS),
            dtype=np.float32,
        )
        immobile = np.asarray([0.0], dtype=np.float32)
        mobile = np.asarray([1.0], dtype=np.float32)

        baseline_actions = SimpleNamespace(
            continuous_tensor=torch.zeros((1, compat.BEES_CONTINUOUS_ACTIONS))
        )
        changed = SimpleNamespace(
            continuous_tensor=baseline_actions.continuous_tensor.clone()
        )
        changed.continuous_tensor[0, 0] = 10.0

        baseline = compat._bees_masked_behavioral_cloning_loss(
            policy,
            baseline_actions,
            log_probs,
            expert,
            weapon_activity,
            immobile,
        )
        ignored = compat._bees_masked_behavioral_cloning_loss(
            policy,
            changed,
            log_probs,
            expert,
            weapon_activity,
            immobile,
        )
        active = compat._bees_masked_behavioral_cloning_loss(
            policy,
            changed,
            log_probs,
            expert,
            weapon_activity,
            mobile,
        )

        self.assertAlmostEqual(float(baseline.item()), float(ignored.item()), places=6)
        self.assertGreater(float(active.item()), float(baseline.item()))

    def test_healing_sample_does_not_train_weapon_fire_bc(self):
        import numpy as np
        from mlagents.torch_utils import torch

        policy = self._policy()
        expert_discrete = torch.zeros(
            (1, len(compat.BEES_DISCRETE_BRANCHES)),
            dtype=torch.long,
        )
        expert_discrete[0, compat.BEES_WEAPON_SLOTS] = (
            compat.BEES_HEALING_SPECIAL_ACTION
        )
        expert = SimpleNamespace(
            continuous_tensor=torch.zeros((1, compat.BEES_CONTINUOUS_ACTIONS)),
            discrete_tensor=expert_discrete,
        )
        selected = SimpleNamespace(
            continuous_tensor=torch.zeros((1, compat.BEES_CONTINUOUS_ACTIONS))
        )
        weapon_activity = np.ones(
            (1, compat.BEES_WEAPON_SLOTS),
            dtype=np.float32,
        )
        movement_activity = np.ones((1,), dtype=np.float32)

        baseline_logits = torch.zeros(
            (1, sum(compat.BEES_DISCRETE_BRANCHES))
        )
        changed_logits = baseline_logits.clone()
        changed_logits[0, 0] = -10.0
        changed_logits[0, 1] = 10.0

        baseline = compat._bees_masked_behavioral_cloning_loss(
            policy,
            selected,
            SimpleNamespace(all_discrete_tensor=baseline_logits),
            expert,
            weapon_activity,
            movement_activity,
        )
        changed = compat._bees_masked_behavioral_cloning_loss(
            policy,
            selected,
            SimpleNamespace(all_discrete_tensor=changed_logits),
            expert,
            weapon_activity,
            movement_activity,
        )

        self.assertAlmostEqual(float(baseline.item()), float(changed.item()), places=6)

    def test_movement_activity_uses_frozen_self_mobile_channel(self):
        import numpy as np
        from mlagents.trainers.buffer import AgentBuffer
        from mlagents.trainers.trajectory import ObsUtil

        batch = AgentBuffer()
        for mobile in (0.0, 1.0):
            observation = np.zeros(
                compat.BEES_OBSERVATION_SIZE,
                dtype=np.float32,
            )
            observation[compat.BEES_SELF_IS_MOBILE_INDEX] = mobile
            batch[ObsUtil.get_name_at(0)].append(observation)

        charging_barge = np.zeros(
            compat.BEES_OBSERVATION_SIZE,
            dtype=np.float32,
        )
        charging_barge[compat.BEES_SELF_IS_MOBILE_INDEX] = 1.0
        charging_barge[compat.BEES_SELF_SHIP_TYPE_INDEX] = (
            compat.BEES_BARGE_SHIP_TYPE_SCALAR
        )
        charging_barge[compat.BEES_CAPABILITY_SPECIAL_PHASE_INDEX] = 1.0 / 3.0
        batch[ObsUtil.get_name_at(0)].append(charging_barge)

        policy = self._policy()
        policy.behavior_spec.observation_specs = [
            SimpleNamespace(shape=(compat.BEES_OBSERVATION_SIZE,))
        ]
        activity = compat._bees_movement_activity(policy, batch)

        np.testing.assert_array_equal(
            activity,
            np.asarray([0.0, 1.0, 0.0], dtype=np.float32),
        )

    def test_neutral_capability_frame_is_not_supervised_without_action_masks(self):
        import numpy as np
        from mlagents.torch_utils import torch

        policy = self._policy()
        expert = SimpleNamespace(
            continuous_tensor=torch.zeros((1, compat.BEES_CONTINUOUS_ACTIONS)),
            discrete_tensor=torch.zeros(
                (1, len(compat.BEES_DISCRETE_BRANCHES)),
                dtype=torch.long,
            ),
        )
        selected = SimpleNamespace(
            continuous_tensor=torch.zeros((1, compat.BEES_CONTINUOUS_ACTIONS))
        )
        weapon_activity = np.ones(
            (1, compat.BEES_WEAPON_SLOTS),
            dtype=np.float32,
        )
        movement_activity = np.ones((1,), dtype=np.float32)

        baseline_logits = torch.zeros(
            (1, sum(compat.BEES_DISCRETE_BRANCHES))
        )
        changed_logits = baseline_logits.clone()
        special_start = sum(compat.BEES_DISCRETE_BRANCHES[:-1])
        changed_logits[0, special_start] = -10.0
        changed_logits[0, special_start + 1] = 10.0

        baseline = compat._bees_masked_behavioral_cloning_loss(
            policy,
            selected,
            SimpleNamespace(all_discrete_tensor=baseline_logits),
            expert,
            weapon_activity,
            movement_activity,
        )
        changed = compat._bees_masked_behavioral_cloning_loss(
            policy,
            selected,
            SimpleNamespace(all_discrete_tensor=changed_logits),
            expert,
            weapon_activity,
            movement_activity,
        )

        self.assertAlmostEqual(float(baseline.item()), float(changed.item()), places=6)

    def test_explicit_capability_event_is_supervised(self):
        import numpy as np
        from mlagents.torch_utils import torch

        policy = self._policy()
        expert_discrete = torch.zeros(
            (1, len(compat.BEES_DISCRETE_BRANCHES)),
            dtype=torch.long,
        )
        expert_discrete[0, compat.BEES_WEAPON_SLOTS] = 1
        expert = SimpleNamespace(
            continuous_tensor=torch.zeros((1, compat.BEES_CONTINUOUS_ACTIONS)),
            discrete_tensor=expert_discrete,
        )
        selected = SimpleNamespace(
            continuous_tensor=torch.zeros((1, compat.BEES_CONTINUOUS_ACTIONS))
        )
        weapon_activity = np.ones(
            (1, compat.BEES_WEAPON_SLOTS),
            dtype=np.float32,
        )
        movement_activity = np.ones((1,), dtype=np.float32)

        baseline_logits = torch.zeros(
            (1, sum(compat.BEES_DISCRETE_BRANCHES))
        )
        wrong_logits = baseline_logits.clone()
        special_start = sum(compat.BEES_DISCRETE_BRANCHES[:-1])
        wrong_logits[0, special_start] = 10.0
        wrong_logits[0, special_start + 1] = -10.0

        baseline = compat._bees_masked_behavioral_cloning_loss(
            policy,
            selected,
            SimpleNamespace(all_discrete_tensor=baseline_logits),
            expert,
            weapon_activity,
            movement_activity,
        )
        wrong = compat._bees_masked_behavioral_cloning_loss(
            policy,
            selected,
            SimpleNamespace(all_discrete_tensor=wrong_logits),
            expert,
            weapon_activity,
            movement_activity,
        )

        self.assertGreater(float(wrong.item()), float(baseline.item()))

    def test_demonstrations_do_not_supervise_private_communication(self):
        import numpy as np
        from mlagents.torch_utils import torch

        policy = self._policy()
        expert = SimpleNamespace(
            continuous_tensor=torch.zeros((1, compat.BEES_CONTINUOUS_ACTIONS)),
            discrete_tensor=torch.zeros(
                (1, len(compat.BEES_DISCRETE_BRANCHES)),
                dtype=torch.long,
            ),
        )
        log_probs = SimpleNamespace(
            all_discrete_tensor=torch.zeros(
                (1, sum(compat.BEES_DISCRETE_BRANCHES))
            )
        )
        weapon_activity = np.ones(
            (1, compat.BEES_WEAPON_SLOTS),
            dtype=np.float32,
        )
        movement_activity = np.ones((1,), dtype=np.float32)

        baseline_actions = SimpleNamespace(
            continuous_tensor=torch.zeros((1, compat.BEES_CONTINUOUS_ACTIONS))
        )
        changed_actions = SimpleNamespace(
            continuous_tensor=baseline_actions.continuous_tensor.clone()
        )
        communication_start = (
            compat.BEES_MOVEMENT_CONTINUOUS_ACTIONS
            + compat.BEES_WEAPON_SLOTS
            * compat.BEES_WEAPON_AIM_ACTIONS_PER_SLOT
        )
        changed_actions.continuous_tensor[0, communication_start] = 10.0

        baseline = compat._bees_masked_behavioral_cloning_loss(
            policy,
            baseline_actions,
            log_probs,
            expert,
            weapon_activity,
            movement_activity,
        )
        changed = compat._bees_masked_behavioral_cloning_loss(
            policy,
            changed_actions,
            log_probs,
            expert,
            weapon_activity,
            movement_activity,
        )

        self.assertAlmostEqual(float(baseline.item()), float(changed.item()), places=6)


class BehavioralCloningBufferContractTests(unittest.TestCase):
    def test_sparse_capability_bc_does_not_depend_on_demo_action_masks(self):
        self.assertFalse(
            hasattr(compat, "_bees_bc_special_activity"),
            "ML-Agents 1.1.0 demonstration buffers do not preserve ACTION_MASK.",
        )


class PocaAdvantageNormalizationTests(unittest.TestCase):
    def test_advantage_normalization_uses_inverse_group_size_weights(self):
        import numpy as np
        from mlagents.trainers.buffer import AgentBuffer, BufferKey
        from mlagents.trainers.trajectory import GroupObsUtil

        batch = AgentBuffer()
        # Advantages 0 and 2 are single-agent timesteps; 10 is from a
        # three-agent timestep and should carry only one-third weight.
        for advantage, groupmates in (
            (0.0, []),
            (10.0, [
                np.asarray([1.0], dtype=np.float32),
                np.asarray([2.0], dtype=np.float32),
            ]),
            (2.0, []),
        ):
            batch[BufferKey.ADVANTAGES].append(advantage)
            batch[BufferKey.MASKS].append(1.0)
            batch[GroupObsUtil.get_name_at(0)].append(groupmates)

        policy = SimpleNamespace(
            behavior_spec=SimpleNamespace(observation_specs=[object()])
        )
        normalized = compat._normalize_poca_advantages(policy, batch)
        weights = np.asarray([1.0, 1.0 / 3.0, 1.0], dtype=np.float32)

        self.assertAlmostEqual(
            float(np.sum(weights * normalized) / np.sum(weights)),
            0.0,
            places=6,
        )
        self.assertAlmostEqual(
            float(
                np.sum(weights * normalized * normalized)
                / np.sum(weights)
            ),
            1.0,
            places=5,
        )


class StructuredTrainingSlotLimitTests(unittest.TestCase):
    def test_slot_limits_keep_highest_live_prefix_across_agent_and_groupmate(self):
        import numpy as np
        from mlagents.trainers.buffer import AgentBuffer
        from mlagents.trainers.trajectory import GroupObsUtil, ObsUtil
        from bees_mlagents_structured_policy import (
            ALLY_SIZE,
            ALLY_START,
            BEES_OBSERVATION_SIZE,
            ENEMY_SIZE,
            ENEMY_START,
        )

        batch = AgentBuffer()
        current = np.zeros(BEES_OBSERVATION_SIZE, dtype=np.float32)
        current[ALLY_START + ALLY_SIZE] = 1.0
        groupmate = np.zeros(BEES_OBSERVATION_SIZE, dtype=np.float32)
        groupmate[ENEMY_START + 2 * ENEMY_SIZE] = 1.0
        batch[ObsUtil.get_name_at(0)].append(current)
        batch[GroupObsUtil.get_name_at(0)].append([groupmate])

        policy = SimpleNamespace(
            behavior_spec=SimpleNamespace(observation_specs=[object()])
        )
        limits = compat._structured_training_slot_limits(policy, batch)

        self.assertEqual(limits["allies"], 2)
        self.assertEqual(limits["enemies"], 3)


class PocaBatchedTrajectoryEvaluationTests(unittest.TestCase):
    def test_batch_evaluates_current_and_bootstrap_values_without_grad(self):
        import numpy as np
        from mlagents.torch_utils import torch
        from mlagents.trainers.buffer import AgentBuffer
        from mlagents.trainers.trajectory import GroupObsUtil, ObsUtil

        buffers = []
        for values in ((1.0, 2.0), (3.0,)):
            buffer = AgentBuffer()
            for value in values:
                observation = np.zeros(
                    compat.BEES_OBSERVATION_SIZE,
                    dtype=np.float32,
                )
                observation[0] = value
                buffer[ObsUtil.get_name_at(0)].append(observation)
                buffer[GroupObsUtil.get_name_at(0)].append([])
            buffers.append(buffer)

        trajectories = []
        for value in (10.0, 20.0):
            observation = np.zeros(
                compat.BEES_OBSERVATION_SIZE,
                dtype=np.float32,
            )
            observation[0] = value
            trajectories.append(
                SimpleNamespace(
                    next_obs=[observation],
                    next_group_obs=[],
                )
            )

        calls = []

        class FakeCritic:
            def critic_pass(
                self,
                all_obs,
                memories=None,
                sequence_length=1,
            ):
                values = all_obs[0][0][:, 0]
                calls.append(
                    ("critic", int(values.shape[0]), torch.is_grad_enabled())
                )
                return {"extrinsic": values.clone()}, None

            def baseline(
                self,
                current_obs,
                groupmate_obs_and_actions,
                memories=None,
                sequence_length=1,
            ):
                values = current_obs[0][:, 0]
                calls.append(
                    ("baseline", int(values.shape[0]), torch.is_grad_enabled())
                )
                return {"extrinsic": values + 100.0}, None

        trainer = SimpleNamespace(
            policy=SimpleNamespace(
                use_recurrent=False,
                behavior_spec=SimpleNamespace(
                    observation_specs=[object()]
                ),
            ),
            optimizer=SimpleNamespace(critic=FakeCritic()),
        )

        merged = compat._merge_agent_buffers(buffers)
        values, baselines, next_values = (
            compat._evaluate_poca_trajectory_batch(
                trainer,
                merged,
                trajectories,
            )
        )

        np.testing.assert_allclose(
            values["extrinsic"],
            np.asarray([1.0, 2.0, 3.0], dtype=np.float32),
        )
        np.testing.assert_allclose(
            baselines["extrinsic"],
            np.asarray([101.0, 102.0, 103.0], dtype=np.float32),
        )
        np.testing.assert_allclose(
            next_values["extrinsic"],
            np.asarray([10.0, 20.0], dtype=np.float32),
        )
        self.assertEqual(
            calls,
            [
                ("critic", 3, False),
                ("baseline", 3, False),
                ("critic", 2, False),
            ],
        )


class PocaLearnerOptimizationOptionTests(unittest.TestCase):
    def tearDown(self):
        compat.configure_poca_learner_optimizations()

    def test_advanced_stream_options_force_sync_cleanup(self):
        options = compat.configure_poca_learner_optimizations(
            sync_cleanup=False,
            stream_shards=2,
            minibatch_prefetch=True,
            critic_baseline_overlap=True,
            cuda_graphs=False,
        )
        self.assertTrue(options.effective_sync_cleanup)
        self.assertEqual(options.stream_shards, 2)
        self.assertTrue(options.minibatch_prefetch)
        self.assertTrue(options.critic_baseline_overlap)

    def test_stream_shards_are_limited_to_supported_experiments(self):
        with self.assertRaisesRegex(ValueError, "1, 2, or 4"):
            compat.configure_poca_learner_optimizations(stream_shards=3)

    def test_shard_row_remapping_preserves_local_indices(self):
        import numpy as np

        remapped = compat._poca_remap_rows(
            np.asarray([0, 3, 5, 7, 9], dtype=np.int64),
            4,
            8,
        )
        np.testing.assert_array_equal(
            remapped,
            np.asarray([1, 3], dtype=np.int64),
        )


    def test_cuda_graph_requires_both_faction_parameter_paths(self):
        compat.configure_poca_learner_optimizations(cuda_graphs=True)

        self.assertTrue(
            compat._poca_cuda_graph_minibatch_eligible(
                {
                    "faction_rows": {
                        "bee": (0,),
                        "human": (1,),
                        "mixed": (),
                    }
                }
            )
        )
        self.assertTrue(
            compat._poca_cuda_graph_minibatch_eligible(
                {
                    "faction_rows": {
                        "bee": (),
                        "human": (),
                        "mixed": (0,),
                    }
                }
            )
        )
        self.assertFalse(
            compat._poca_cuda_graph_minibatch_eligible(
                {
                    "faction_rows": {
                        "bee": (0,),
                        "human": (),
                        "mixed": (),
                    }
                }
            )
        )


class PocaVariabilityProfilerTests(unittest.TestCase):
    def test_nvidia_smi_sample_parser_accepts_numeric_and_na_fields(self):
        sample = compat._poca_parse_nvidia_smi_sample(
            "37, 12, 1024, 6144, 61, 82.5, 1740, 4001, P2"
        )
        self.assertEqual(sample["gpu_util"], 37.0)
        self.assertEqual(sample["memory_used_mib"], 1024.0)
        self.assertEqual(sample["power_w"], 82.5)
        self.assertEqual(sample["pstate"], "P2")

        sample = compat._poca_parse_nvidia_smi_sample(
            "0, 0, 512, 6144, 45, N/A, 300, 405, P8"
        )
        self.assertIsNone(sample["power_w"])
        self.assertEqual(sample["graphics_clock_mhz"], 300.0)

    def test_percentile_interpolates_small_samples(self):
        self.assertEqual(compat._poca_percentile([], 50), 0.0)
        self.assertEqual(compat._poca_percentile([2.0], 90), 2.0)
        self.assertAlmostEqual(
            compat._poca_percentile([1.0, 2.0, 3.0, 4.0], 50),
            2.5,
        )

    def test_timing_recorder_keeps_per_minibatch_samples(self):
        compat._POCA_TIMING_STATE.timing_totals = {}
        compat._POCA_TIMING_STATE.timing_counts = {}
        compat._POCA_TIMING_STATE.timing_samples = {}
        try:
            compat._poca_record_timing("minibatch_total", 1.0)
            compat._poca_record_timing("minibatch_total", 3.0)
            distribution = compat._poca_timing_distribution(
                "minibatch_total"
            )
            self.assertEqual(distribution["p50"], 2.0)
            self.assertEqual(distribution["p90"], 2.8)
            self.assertEqual(distribution["max"], 3.0)
        finally:
            compat._POCA_TIMING_STATE.timing_totals = None
            compat._POCA_TIMING_STATE.timing_counts = None
            compat._POCA_TIMING_STATE.timing_samples = None


class PocaCudaTimingTests(unittest.TestCase):
    def tearDown(self):
        compat._POCA_TIMING_STATE.timing_totals = None
        compat._POCA_TIMING_STATE.timing_counts = None
        compat._POCA_TIMING_STATE.timing_samples = None
        compat._POCA_TIMING_STATE.cuda_events = None

    def test_cuda_timing_records_elapsed_after_single_flush(self):
        from mlagents.torch_utils import torch

        start_event = mock.Mock()
        end_event = mock.Mock()
        start_event.elapsed_time.return_value = 125.0

        compat._POCA_TIMING_STATE.timing_totals = {}
        compat._POCA_TIMING_STATE.timing_counts = {}
        compat._POCA_TIMING_STATE.cuda_events = []

        with (
            mock.patch.object(torch.cuda, "is_available", return_value=True),
            mock.patch.object(
                torch.cuda,
                "Event",
                side_effect=[start_event, end_event],
            ),
            mock.patch.object(torch.cuda, "synchronize") as synchronize,
        ):
            marker = compat._poca_cuda_timing_begin("critic_pass")
            compat._poca_cuda_timing_end(marker)

            start_event.record.assert_called_once_with()
            end_event.record.assert_called_once_with()
            synchronize.assert_not_called()

            compat._poca_flush_cuda_timings()

        synchronize.assert_called_once_with()
        start_event.elapsed_time.assert_called_once_with(end_event)
        self.assertAlmostEqual(
            compat._poca_average_timing("cuda_critic_pass"),
            0.125,
        )
        self.assertEqual(compat._POCA_TIMING_STATE.cuda_events, [])

    def test_cuda_timing_is_disabled_when_cuda_is_unavailable(self):
        from mlagents.torch_utils import torch

        compat._POCA_TIMING_STATE.timing_totals = {}
        compat._POCA_TIMING_STATE.timing_counts = {}
        compat._POCA_TIMING_STATE.cuda_events = []

        with (
            mock.patch.object(torch.cuda, "is_available", return_value=False),
            mock.patch.object(torch.cuda, "Event") as event,
        ):
            marker = compat._poca_cuda_timing_begin("critic_pass")

        self.assertIsNone(marker)
        event.assert_not_called()
        self.assertEqual(compat._POCA_TIMING_STATE.cuda_events, [])


class PocaGpuCachePromotionTests(unittest.TestCase):
    def test_cache_size_counts_nested_tensors_once(self):
        from mlagents.torch_utils import torch

        tensor = torch.zeros((4, 8), dtype=torch.float32)
        cache = {
            "a": tensor,
            "nested": [tensor, torch.zeros((2,), dtype=torch.int64)],
        }
        self.assertEqual(
            compat._poca_tensor_cache_nbytes(cache),
            4 * 8 * 4 + 2 * 8,
        )

    def test_unindexed_cuda_device_uses_current_device_index_for_mem_info(self):
        from mlagents.torch_utils import torch

        cache = {
            "current_obs": [torch.zeros((2, 3), dtype=torch.float32)],
            "storage": "cpu",
        }

        def fake_move(value, device):
            self.assertEqual(device, torch.device("cuda", 0))
            return dict(value)

        with (
            mock.patch(
                "mlagents.torch_utils.default_device",
                return_value=torch.device("cuda"),
            ),
            mock.patch.object(
                torch.cuda,
                "is_available",
                return_value=True,
            ),
            mock.patch.object(
                torch.cuda,
                "current_device",
                return_value=0,
            ),
            mock.patch.object(
                torch.cuda,
                "mem_get_info",
                return_value=(6 * 1024**3, 6 * 1024**3),
            ) as mem_get_info,
            mock.patch.object(
                torch.cuda,
                "memory_allocated",
                return_value=512 * 1024**2,
            ),
            mock.patch.object(
                torch.cuda,
                "memory_reserved",
                return_value=1024 * 1024**2,
            ),
            mock.patch.object(
                compat,
                "_poca_move_cache_tensors",
                side_effect=fake_move,
            ),
        ):
            promoted, info = compat._promote_poca_update_tensor_cache(cache)

        mem_get_info.assert_called_once_with(0)
        self.assertEqual(promoted["storage"], "cuda")
        self.assertEqual(info["storage"], "cuda")

    def test_allocator_reusable_memory_allows_gpu_cache_when_driver_free_is_zero(self):
        from mlagents.torch_utils import torch

        cache = {
            "current_obs": [
                torch.zeros((256, 256), dtype=torch.float32)
            ],
            "storage": "cpu",
        }

        with (
            mock.patch(
                "mlagents.torch_utils.default_device",
                return_value=torch.device("cuda"),
            ),
            mock.patch.object(torch.cuda, "is_available", return_value=True),
            mock.patch.object(torch.cuda, "current_device", return_value=0),
            mock.patch.object(
                torch.cuda,
                "mem_get_info",
                return_value=(0, 6 * 1024**3),
            ),
            mock.patch.object(
                torch.cuda,
                "memory_allocated",
                return_value=2 * 1024**3,
            ),
            mock.patch.object(
                torch.cuda,
                "memory_reserved",
                return_value=5 * 1024**3,
            ),
            mock.patch.object(
                compat,
                "_poca_move_cache_tensors",
                side_effect=lambda value, _device: dict(value),
            ),
        ):
            promoted, info = compat._promote_poca_update_tensor_cache(cache)

        self.assertEqual(promoted["storage"], "cuda")
        self.assertEqual(info["storage"], "cuda")
        self.assertEqual(info["driver_free_before"], 0)
        self.assertEqual(info["allocator_reusable_before"], 3 * 1024**3)
        self.assertEqual(info["free_before"], 3 * 1024**3)

    def test_packed_group_cache_promotes_after_fixed_cache_when_vram_permits(self):
        import numpy as np
        from mlagents.torch_utils import torch

        group = compat._PocaPackedGroupObs(
            [
                [
                    compat._PocaPackedGroupPosition(
                        np.asarray([0], dtype=np.int32),
                        torch.zeros((1, 4), dtype=torch.float32),
                    )
                ]
            ],
            4 * 4,
        )
        cache = {
            "current_obs": [torch.zeros((2, 3), dtype=torch.float32)],
            "groupmate_obs": group,
            "storage": "cpu",
        }

        with (
            mock.patch(
                "mlagents.torch_utils.default_device",
                return_value=torch.device("cuda"),
            ),
            mock.patch.object(torch.cuda, "is_available", return_value=True),
            mock.patch.object(torch.cuda, "current_device", return_value=0),
            mock.patch.object(
                torch.cuda,
                "mem_get_info",
                side_effect=[
                    (6 * 1024**3, 6 * 1024**3),
                    (5 * 1024**3, 6 * 1024**3),
                ],
            ),
            mock.patch.object(
                torch.cuda,
                "memory_allocated",
                return_value=512 * 1024**2,
            ),
            mock.patch.object(
                torch.cuda,
                "memory_reserved",
                return_value=1024 * 1024**2,
            ),
            mock.patch.object(
                compat,
                "_poca_move_cache_tensors",
                side_effect=lambda value, _device: dict(value),
            ),
            mock.patch.object(
                compat,
                "_move_poca_packed_group_obs",
                return_value=group,
            ) as move_group,
        ):
            promoted, info = compat._promote_poca_update_tensor_cache(cache)

        self.assertEqual(promoted["storage"], "cuda")
        self.assertEqual(info["group_storage"], "cuda")
        self.assertEqual(info["group_bytes"], 16)
        move_group.assert_called_once()

    def test_group_cache_stays_cpu_without_displacing_fixed_gpu_cache(self):
        import numpy as np
        from mlagents.torch_utils import torch

        group = compat._PocaPackedGroupObs(
            [
                [
                    compat._PocaPackedGroupPosition(
                        np.asarray([0], dtype=np.int32),
                        torch.zeros((1, 4), dtype=torch.float32),
                    )
                ]
            ],
            3 * 1024**3,
        )
        cache = {
            "current_obs": [torch.zeros((2, 3), dtype=torch.float32)],
            "groupmate_obs": group,
            "storage": "cpu",
        }

        with (
            mock.patch(
                "mlagents.torch_utils.default_device",
                return_value=torch.device("cuda"),
            ),
            mock.patch.object(torch.cuda, "is_available", return_value=True),
            mock.patch.object(torch.cuda, "current_device", return_value=0),
            mock.patch.object(
                torch.cuda,
                "mem_get_info",
                side_effect=[
                    (6 * 1024**3, 6 * 1024**3),
                    (1024**3, 6 * 1024**3),
                ],
            ),
            mock.patch.object(
                torch.cuda,
                "memory_allocated",
                return_value=512 * 1024**2,
            ),
            mock.patch.object(
                torch.cuda,
                "memory_reserved",
                return_value=1024 * 1024**2,
            ),
            mock.patch.object(
                compat,
                "_poca_move_cache_tensors",
                side_effect=lambda value, _device: dict(value),
            ),
            mock.patch.object(
                compat,
                "_move_poca_packed_group_obs",
            ) as move_group,
        ):
            promoted, info = compat._promote_poca_update_tensor_cache(cache)

        self.assertEqual(promoted["storage"], "cuda")
        self.assertIs(promoted["groupmate_obs"], group)
        self.assertEqual(info["group_storage"], "cpu")
        move_group.assert_not_called()

    def test_cpu_device_keeps_cache_on_cpu(self):
        from mlagents.torch_utils import torch

        cache = {
            "current_obs": [torch.zeros((2, 3), dtype=torch.float32)],
            "storage": "cpu",
        }
        with mock.patch(
            "mlagents.torch_utils.default_device",
            return_value=torch.device("cpu"),
        ):
            promoted, info = compat._promote_poca_update_tensor_cache(cache)

        self.assertIs(promoted, cache)
        self.assertEqual(info["storage"], "cpu")
        self.assertEqual(info["bytes"], 2 * 3 * 4)


class PocaBusyTelemetryTests(unittest.TestCase):
    def test_busy_counter_accumulates_update_wall_time(self):
        before = compat.poca_update_busy_seconds_total()
        compat._record_poca_update_busy_seconds(1.25)
        self.assertAlmostEqual(
            compat.poca_update_busy_seconds_total(),
            before + 1.25,
        )


class PocaTrajectoryGroupTensorTests(unittest.TestCase):
    def test_compact_transfer_matches_stock_group_padding(self):
        import numpy as np
        from mlagents.torch_utils import torch
        from mlagents.trainers.buffer import AgentBuffer
        from mlagents.trainers.trajectory import GroupObsUtil

        buffer = AgentBuffer()
        entries = (
            [np.asarray([1.0, 2.0, 3.0, 4.0], dtype=np.float32)],
            [],
            [
                np.asarray([5.0, 6.0, 7.0, 8.0], dtype=np.float32),
                np.asarray([9.0, 10.0, 11.0, 12.0], dtype=np.float32),
            ],
        )
        for entry in entries:
            buffer[GroupObsUtil.get_name_at(0)].append(list(entry))

        policy = SimpleNamespace(
            behavior_spec=SimpleNamespace(
                observation_specs=[
                    SimpleNamespace(shape=(4,))
                ]
            )
        )
        counts = np.asarray([1, 0, 2], dtype=np.int32)
        actual = compat._poca_group_obs_tensors_from_buffer(
            policy,
            buffer,
            counts,
            torch.device("cpu"),
        )
        stock = GroupObsUtil.from_buffer(buffer, 1)

        self.assertEqual(len(actual), len(stock))
        for position in range(len(stock)):
            expected = np.asarray(stock[position][0], dtype=np.float32)
            observed = actual[position][0].detach().cpu().numpy()
            np.testing.assert_allclose(
                observed,
                expected,
                equal_nan=True,
            )


class PocaTensorCacheTests(unittest.TestCase):
    def test_cached_minibatch_selects_requested_rows_without_repadding(self):
        import numpy as np
        from mlagents.torch_utils import torch
        from mlagents.trainers.buffer import (
            AgentBuffer,
            BufferKey,
            RewardSignalKeyPrefix,
            RewardSignalUtil,
        )
        from mlagents.trainers.trajectory import GroupObsUtil, ObsUtil
        from mlagents.trainers.torch_entities.components.reward_providers.extrinsic_reward_provider import (
            ExtrinsicRewardProvider,
        )
        from bees_mlagents_structured_policy import FACTION_INDEX

        original_value_key = RewardSignalUtil.value_estimates_key
        RewardSignalUtil.value_estimates_key = staticmethod(
            lambda name: (RewardSignalKeyPrefix.VALUE_ESTIMATES, name)
        )
        try:
            buffer = AgentBuffer()
            for row in range(3):
                observation = np.zeros(
                    compat.BEES_OBSERVATION_SIZE,
                    dtype=np.float32,
                )
                observation[0] = float(row + 1)
                observation[compat.BEES_SELF_IS_MOBILE_INDEX] = 1.0
                observation[FACTION_INDEX] = (
                    1.0 if row != 1 else -1.0
                )
                buffer[ObsUtil.get_name_at(0)].append(observation)
                groupmates = []
                if row == 0:
                    mate = np.zeros(
                        compat.BEES_OBSERVATION_SIZE,
                        dtype=np.float32,
                    )
                    mate[0] = 10.0
                    groupmates = [mate]
                elif row == 1:
                    mate = np.zeros(
                        compat.BEES_OBSERVATION_SIZE,
                        dtype=np.float32,
                    )
                    mate[0] = 20.0
                    groupmates = [mate]
                else:
                    first = np.zeros(
                        compat.BEES_OBSERVATION_SIZE,
                        dtype=np.float32,
                    )
                    second = np.zeros(
                        compat.BEES_OBSERVATION_SIZE,
                        dtype=np.float32,
                    )
                    first[0] = 30.0
                    second[0] = 31.0
                    groupmates = [first, second]
                buffer[GroupObsUtil.get_name_at(0)].append(groupmates)
                buffer[BufferKey.CONTINUOUS_ACTION].append(
                    np.full(
                        compat.BEES_CONTINUOUS_ACTIONS,
                        float(row),
                        dtype=np.float32,
                    )
                )
                buffer[BufferKey.DISCRETE_ACTION].append(
                    np.zeros(
                        len(compat.BEES_DISCRETE_BRANCHES),
                        dtype=np.int64,
                    )
                )
                buffer[BufferKey.ACTION_MASK].append(
                    np.ones(
                        sum(compat.BEES_DISCRETE_BRANCHES),
                        dtype=np.float32,
                    )
                )
                buffer[BufferKey.MASKS].append(1.0)
                buffer[BufferKey.CONTINUOUS_LOG_PROBS].append(
                    np.full(
                        compat.BEES_CONTINUOUS_ACTIONS,
                        row + 0.1,
                        dtype=np.float32,
                    )
                )
                buffer[BufferKey.DISCRETE_LOG_PROBS].append(
                    np.full(
                        len(compat.BEES_DISCRETE_BRANCHES),
                        row + 0.2,
                        dtype=np.float32,
                    )
                )
                buffer[BufferKey.ADVANTAGES].append(float(row))
                buffer[
                    RewardSignalUtil.value_estimates_key("extrinsic")
                ].append(float(row + 10))
                buffer[
                    RewardSignalUtil.returns_key("extrinsic")
                ].append(float(row + 20))
                buffer[
                    RewardSignalUtil.baseline_estimates_key("extrinsic")
                ].append(float(row + 30))

            provider = ExtrinsicRewardProvider(
                SimpleNamespace(),
                SimpleNamespace(gamma=1.0, strength=1.0),
            )
            policy = SimpleNamespace(
                sequence_length=1,
                behavior_spec=SimpleNamespace(
                    observation_specs=[
                        SimpleNamespace(
                            shape=(compat.BEES_OBSERVATION_SIZE,)
                        )
                    ],
                    action_spec=SimpleNamespace(
                        continuous_size=compat.BEES_CONTINUOUS_ACTIONS,
                        discrete_branches=compat.BEES_DISCRETE_BRANCHES,
                        discrete_size=len(compat.BEES_DISCRETE_BRANCHES),
                    ),
                ),
            )
            optimizer = SimpleNamespace(
                policy=policy,
                reward_signals={"extrinsic": provider},
            )

            cache = compat._build_poca_update_tensor_cache(
                optimizer,
                buffer,
            )
            self.assertIsNotNone(cache)
            selected = compat._select_poca_update_tensor_cache(
                cache,
                np.asarray([2, 0], dtype=np.int64),
            )

            np.testing.assert_allclose(
                selected["current_obs"][0][:, 0].detach().cpu().numpy(),
                np.asarray([3.0, 1.0], dtype=np.float32),
            )
            np.testing.assert_allclose(
                selected["advantages"].detach().cpu().numpy(),
                np.asarray([2.0, 0.0], dtype=np.float32),
            )
            self.assertEqual(
                tuple(selected["old_log_probs"].shape),
                (
                    2,
                    compat.BEES_CONTINUOUS_ACTIONS
                    + len(compat.BEES_DISCRETE_BRANCHES),
                ),
            )
            self.assertEqual(
                tuple(selected["discrete_actions"].shape),
                (2, len(compat.BEES_DISCRETE_BRANCHES)),
            )
            np.testing.assert_array_equal(
                selected["groupmate_counts"],
                np.asarray([2, 1], dtype=np.int32),
            )
            self.assertEqual(len(selected["groupmate_obs"]), 2)
            self.assertEqual(len(selected["groupmate_obs"][0]), 1)
            first_groupmate = (
                selected["groupmate_obs"][0][0][:, 0]
                .detach()
                .cpu()
                .numpy()
            )
            second_groupmate = (
                selected["groupmate_obs"][1][0][:, 0]
                .detach()
                .cpu()
                .numpy()
            )
            np.testing.assert_allclose(
                first_groupmate,
                np.asarray([30.0, 10.0], dtype=np.float32),
            )
            self.assertEqual(second_groupmate[0], 31.0)
            self.assertTrue(np.isnan(second_groupmate[1]))
            self.assertIsInstance(
                cache["groupmate_obs"],
                compat._PocaPackedGroupObs,
            )
            self.assertEqual(
                compat._poca_group_obs_cache_nbytes(
                    cache["groupmate_obs"]
                ),
                4 * compat.BEES_OBSERVATION_SIZE * 4,
            )
            with mock.patch.object(
                compat,
                "POCA_PACKED_GROUP_CACHE_MAX_BYTES",
                1,
            ):
                fallback_group_obs = compat._build_poca_group_obs_cache(
                    policy,
                    buffer,
                    np.asarray([1, 1, 2], dtype=np.int32),
                )
            self.assertIsInstance(
                fallback_group_obs,
                compat._PocaRaggedGroupObs,
            )
            np.testing.assert_array_equal(
                selected["faction_rows"]["bee"],
                np.asarray([0, 1], dtype=np.int64),
            )
            self.assertEqual(
                selected["faction_rows"]["human"].size,
                0,
            )
            self.assertEqual(
                selected["faction_rows"]["mixed"].size,
                0,
            )
            self.assertTrue(
                torch.all(selected["movement_activity"] == 1.0)
            )
        finally:
            RewardSignalUtil.value_estimates_key = staticmethod(
                original_value_key
            )


class PocaCommunicationActivityTests(unittest.TestCase):
    def test_communication_is_active_only_with_groupmates(self):
        import numpy as np
        from mlagents.trainers.buffer import AgentBuffer
        from mlagents.trainers.trajectory import GroupObsUtil

        batch = AgentBuffer()
        for groupmate_values in (
            [],
            [np.asarray([1.0], dtype=np.float32)],
            [
                np.asarray([2.0], dtype=np.float32),
                np.asarray([3.0], dtype=np.float32),
            ],
        ):
            batch[GroupObsUtil.get_name_at(0)].append(groupmate_values)

        policy = SimpleNamespace(
            behavior_spec=SimpleNamespace(observation_specs=[object()])
        )
        activity = compat._poca_communication_activity(
            policy,
            batch,
            3,
        )

        np.testing.assert_array_equal(
            activity,
            np.asarray([0.0, 1.0, 1.0], dtype=np.float32),
        )


class PocaGroupSizeWeightTests(unittest.TestCase):
    def test_inverse_group_size_uses_exact_groupmate_presence(self):
        import numpy as np
        from mlagents.torch_utils import torch
        from mlagents.trainers.buffer import AgentBuffer, BufferKey
        from mlagents.trainers.trajectory import GroupObsUtil

        batch = AgentBuffer()
        for groupmate_values in (
            [],
            [np.asarray([1.0], dtype=np.float32)],
            [
                np.asarray([2.0], dtype=np.float32),
                np.asarray([3.0], dtype=np.float32),
            ],
        ):
            batch[GroupObsUtil.get_name_at(0)].append(groupmate_values)
            batch[BufferKey.MASKS].append(1.0)

        policy = SimpleNamespace(
            behavior_spec=SimpleNamespace(observation_specs=[object()])
        )
        reference = torch.ones((3, 1))
        weights = compat._poca_inverse_group_size_weights(
            policy,
            batch,
            reference,
        )

        np.testing.assert_allclose(
            weights.detach().cpu().numpy(),
            np.asarray([1.0, 0.5, 1.0 / 3.0], dtype=np.float32),
            rtol=1e-6,
            atol=1e-6,
        )


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


