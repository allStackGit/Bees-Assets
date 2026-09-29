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
        special_activity = np.ones((1,), dtype=np.float32)

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
            special_activity,
        )
        changed = compat._bees_masked_behavioral_cloning_loss(
            policy,
            selected,
            SimpleNamespace(all_discrete_tensor=changed_logits),
            expert,
            weapon_activity,
            movement_activity,
            special_activity,
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

    def test_neutral_capability_frame_is_not_supervised(self):
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
        special_activity = np.ones((1,), dtype=np.float32)

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
            special_activity,
        )
        changed = compat._bees_masked_behavioral_cloning_loss(
            policy,
            selected,
            SimpleNamespace(all_discrete_tensor=changed_logits),
            expert,
            weapon_activity,
            movement_activity,
            special_activity,
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
        special_activity = np.ones((1,), dtype=np.float32)

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
            special_activity,
        )
        wrong = compat._bees_masked_behavioral_cloning_loss(
            policy,
            selected,
            SimpleNamespace(all_discrete_tensor=wrong_logits),
            expert,
            weapon_activity,
            movement_activity,
            special_activity,
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


