"""Focused tests for the Bees v21 structured MA-POCA actor architecture."""

from __future__ import annotations

from types import SimpleNamespace
import unittest


from bees_mlagents_structured_policy import (
    ACTION_ENCODING_SIZE,
    BEES_CONTINUOUS_ACTIONS,
    BEES_DISCRETE_BRANCHES,
    BEES_OBSERVATION_SIZE,
    FACTION_INDEX,
    BeesStructuredActionModel,
    BeesStructuredNetworkBody,
    BeesStructuredObservationEncoder,
)


class StructuredPolicyArchitectureTests(unittest.TestCase):
    @staticmethod
    def _network_settings():
        from mlagents.trainers.settings import NetworkSettings

        return NetworkSettings(
            normalize=False,
            hidden_units=128,
            num_layers=3,
        )

    @staticmethod
    def _observation_specs():
        return [SimpleNamespace(shape=(BEES_OBSERVATION_SIZE,))]

    @staticmethod
    def _action_spec():
        from mlagents_envs.base_env import ActionSpec

        return ActionSpec(
            continuous_size=BEES_CONTINUOUS_ACTIONS,
            discrete_branches=BEES_DISCRETE_BRANCHES,
        )

    @staticmethod
    def _has_nonzero_gradient(module):
        return any(
            parameter.grad is not None
            and bool((parameter.grad != 0).any().item())
            for parameter in module.parameters()
        )

    def test_structured_encoder_preserves_weapon_slots_and_faction(self):
        from mlagents.torch_utils import torch

        encoder = BeesStructuredObservationEncoder(
            self._observation_specs(),
            384,
            self._network_settings().vis_encode_type,
            normalize=False,
        )
        observations = torch.zeros((2, BEES_OBSERVATION_SIZE))
        observations[0, FACTION_INDEX] = 1.0
        observations[1, FACTION_INDEX] = -1.0

        encoded = encoder([observations])

        self.assertEqual(encoded.shape, (2, encoder.total_enc_size))
        self.assertEqual(encoded.shape[1], 833)
        self.assertEqual(float(encoded[0, -1].item()), 1.0)
        self.assertEqual(float(encoded[1, -1].item()), -1.0)

    def test_bee_and_human_actor_encoders_and_trunks_are_independent(self):
        from mlagents.torch_utils import torch

        body = BeesStructuredNetworkBody(
            self._observation_specs(),
            self._network_settings(),
        )
        self.assertIsNot(
            body.bee_observation_encoder,
            body.human_observation_encoder,
        )
        self.assertIsNot(body.bee_trunk, body.human_trunk)

        observations = torch.zeros((1, BEES_OBSERVATION_SIZE))
        observations[:, FACTION_INDEX] = 1.0
        output, _ = body([observations])
        self.assertEqual(output.shape, (1, ACTION_ENCODING_SIZE))

        output.sum().backward()
        self.assertTrue(self._has_nonzero_gradient(body.bee_observation_encoder))
        self.assertTrue(self._has_nonzero_gradient(body.bee_trunk))
        self.assertFalse(self._has_nonzero_gradient(body.human_observation_encoder))
        self.assertFalse(self._has_nonzero_gradient(body.human_trunk))

    def test_weapon_action_head_is_shared_across_slots_but_not_factions(self):
        from mlagents.torch_utils import torch

        model = BeesStructuredActionModel(
            384,
            self._action_spec(),
            deterministic=True,
        )
        self.assertIsNot(model.bee_weapon_aim, model.human_weapon_aim)
        self.assertIsNot(model.bee_weapon_fire, model.human_weapon_fire)

        encoded = torch.zeros((1, ACTION_ENCODING_SIZE))
        encoded[:, -1] = 1.0
        masks = torch.ones((1, sum(BEES_DISCRETE_BRANCHES)))
        dists = model._get_dists(encoded, masks)

        self.assertEqual(dists.continuous.mean.shape, (1, 16))
        self.assertEqual(len(dists.discrete), 6)

        objective = dists.continuous.mean.sum()
        for distribution in dists.discrete:
            objective = objective + distribution.logits.sum()
        objective.backward()

        bee_modules = (
            model.bee_movement,
            model.bee_communication,
            model.bee_weapon_aim,
            model.bee_weapon_fire,
            model.bee_special,
        )
        human_modules = (
            model.human_movement,
            model.human_communication,
            model.human_weapon_aim,
            model.human_weapon_fire,
            model.human_special,
        )
        self.assertTrue(any(self._has_nonzero_gradient(module) for module in bee_modules))
        self.assertFalse(any(self._has_nonzero_gradient(module) for module in human_modules))


if __name__ == "__main__":
    unittest.main()
