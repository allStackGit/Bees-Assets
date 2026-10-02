"""Focused tests for the Bees v22 structured MA-POCA actor architecture."""

from __future__ import annotations

from types import SimpleNamespace
import unittest
from unittest import mock


from bees_mlagents_structured_policy import (
    ACTION_ENCODING_SIZE,
    ALLY_START,
    BEES_CONTINUOUS_ACTIONS,
    BEES_DISCRETE_BRANCHES,
    BEES_OBSERVATION_SIZE,
    COLLISION_START,
    ENEMY_START,
    ENTITY_BASE_SIZE,
    FACTION_INDEX,
    MAP_OBJECT_START,
    MINING_START,
    BeesStructuredActionModel,
    BeesStructuredNetworkBody,
    BeesStructuredObservationEncoder,
    reset_training_faction_rows,
    reset_training_slot_limits,
    set_training_faction_rows,
    set_training_slot_limits,
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

    def test_empty_padded_slots_skip_slot_mlps(self):
        from mlagents.torch_utils import torch

        encoder = BeesStructuredObservationEncoder(
            self._observation_specs(),
            128,
            self._network_settings().vis_encode_type,
            normalize=False,
        )
        calls = {
            "entity_base": 0,
            "weapon_common": 0,
            "self_weapon_specific": 0,
            "ally_comm": 0,
            "ally_fuse": 0,
            "mining": 0,
            "map_object": 0,
            "collision": 0,
        }

        def count(name):
            def hook(_module, _inputs, _output):
                calls[name] += 1
            return hook

        handles = [
            encoder.entity_base_encoder.register_forward_hook(count("entity_base")),
            encoder.weapon_common_encoder.register_forward_hook(count("weapon_common")),
            encoder.self_weapon_specific_encoder.register_forward_hook(
                count("self_weapon_specific")
            ),
            encoder.ally_communication_encoder.register_forward_hook(count("ally_comm")),
            encoder.ally_fuse.register_forward_hook(count("ally_fuse")),
            encoder.mining_encoder.register_forward_hook(count("mining")),
            encoder.map_object_encoder.register_forward_hook(count("map_object")),
            encoder.collision_encoder.register_forward_hook(count("collision")),
        ]
        try:
            observations = torch.zeros((2, BEES_OBSERVATION_SIZE))
            observations[0, FACTION_INDEX] = 1.0
            observations[1, FACTION_INDEX] = -1.0
            with torch.no_grad():
                encoded = encoder([observations])
        finally:
            for handle in handles:
                handle.remove()

        self.assertEqual(encoded.shape, (2, encoder.total_enc_size))
        self.assertEqual(
            calls,
            {name: 0 for name in calls},
        )

    def test_pure_faction_batch_executes_only_matching_encoder_and_trunk(self):
        from mlagents.torch_utils import torch

        body = BeesStructuredNetworkBody(
            self._observation_specs(),
            self._network_settings(),
        )
        calls = {
            "bee_encoder": 0,
            "human_encoder": 0,
            "bee_trunk": 0,
            "human_trunk": 0,
        }

        def count(name):
            def hook(_module, _inputs, _output):
                calls[name] += 1
            return hook

        handles = [
            body.bee_observation_encoder.register_forward_hook(
                count("bee_encoder")
            ),
            body.human_observation_encoder.register_forward_hook(
                count("human_encoder")
            ),
            body.bee_trunk.register_forward_hook(count("bee_trunk")),
            body.human_trunk.register_forward_hook(count("human_trunk")),
        ]
        try:
            observations = torch.zeros((3, BEES_OBSERVATION_SIZE))
            observations[:, FACTION_INDEX] = 1.0
            with torch.no_grad():
                output, _ = body([observations])
        finally:
            for handle in handles:
                handle.remove()

        self.assertEqual(output.shape, (3, ACTION_ENCODING_SIZE))
        self.assertEqual(calls["bee_encoder"], 1)
        self.assertEqual(calls["bee_trunk"], 1)
        self.assertEqual(calls["human_encoder"], 0)
        self.assertEqual(calls["human_trunk"], 0)

    def test_slot_limited_no_grad_batch_still_skips_unused_faction(self):
        from mlagents.torch_utils import torch

        body = BeesStructuredNetworkBody(
            self._observation_specs(),
            self._network_settings(),
        )
        calls = {"bee_encoder": 0, "human_encoder": 0}

        def count(name):
            def hook(_module, _inputs, _output):
                calls[name] += 1
            return hook

        handles = [
            body.bee_observation_encoder.register_forward_hook(
                count("bee_encoder")
            ),
            body.human_observation_encoder.register_forward_hook(
                count("human_encoder")
            ),
        ]
        observations = torch.zeros((3, BEES_OBSERVATION_SIZE))
        observations[:, FACTION_INDEX] = 1.0
        observations[:, ALLY_START] = 1.0
        token = set_training_slot_limits({
            "allies": 1,
            "enemies": 1,
            "entity_weapons": 1,
            "mining": 1,
            "map_objects": 1,
            "collisions": 1,
        })
        try:
            with torch.no_grad():
                output, _ = body([observations])
        finally:
            reset_training_slot_limits(token)
            for handle in handles:
                handle.remove()

        self.assertEqual(output.shape, (3, ACTION_ENCODING_SIZE))
        self.assertEqual(calls["bee_encoder"], 1)
        self.assertEqual(calls["human_encoder"], 0)

    def test_sparse_runtime_path_matches_dense_export_semantics(self):
        from mlagents.torch_utils import torch

        torch.manual_seed(7)
        body = BeesStructuredNetworkBody(
            self._observation_specs(),
            self._network_settings(),
        )
        observations = torch.randn((4, BEES_OBSERVATION_SIZE)) * 0.2
        observations[:2, FACTION_INDEX] = 1.0
        observations[2:, FACTION_INDEX] = -1.0

        with torch.no_grad():
            runtime_output, _ = body([observations])
        with mock.patch.object(
            torch.onnx,
            "is_in_onnx_export",
            return_value=True,
        ):
            dense_output, _ = body([observations])

        torch.testing.assert_close(
            runtime_output,
            dense_output,
            rtol=1.0e-5,
            atol=1.0e-6,
        )

    def test_compact_training_slots_match_full_dense_output(self):
        from mlagents.torch_utils import torch

        torch.manual_seed(11)
        body = BeesStructuredNetworkBody(
            self._observation_specs(),
            self._network_settings(),
        )
        observations = torch.zeros((4, BEES_OBSERVATION_SIZE))
        observations[:2, FACTION_INDEX] = 1.0
        observations[2:, FACTION_INDEX] = -1.0
        for start in (
            ALLY_START,
            ENEMY_START,
            MINING_START,
            MAP_OBJECT_START,
            COLLISION_START,
        ):
            observations[:, start] = 1.0
        observations[:, ALLY_START + ENTITY_BASE_SIZE] = 1.0
        observations[:, ENEMY_START + ENTITY_BASE_SIZE] = 1.0

        full_output, _ = body([observations])
        token = set_training_slot_limits({
            "allies": 1,
            "enemies": 1,
            "entity_weapons": 1,
            "mining": 1,
            "map_objects": 1,
            "collisions": 1,
        })
        try:
            compact_output, _ = body([observations])
        finally:
            reset_training_slot_limits(token)

        torch.testing.assert_close(
            compact_output,
            full_output,
            rtol=1.0e-5,
            atol=1.0e-6,
        )

    def test_gradient_update_path_avoids_sparse_nonzero_selection(self):
        from mlagents.torch_utils import torch

        body = BeesStructuredNetworkBody(
            self._observation_specs(),
            self._network_settings(),
        )
        observations = torch.zeros((4, BEES_OBSERVATION_SIZE))
        observations[:2, FACTION_INDEX] = 1.0
        observations[2:, FACTION_INDEX] = -1.0

        with mock.patch.object(
            torch,
            "nonzero",
            side_effect=AssertionError(
                "gradient structured path must not use synchronizing sparse selection"
            ),
        ):
            output, _ = body([observations])
            output.sum().backward()

        self.assertEqual(output.shape, (4, ACTION_ENCODING_SIZE))

    def test_precomputed_faction_rows_skip_unused_gradient_encoder(self):
        from mlagents.torch_utils import torch

        body = BeesStructuredNetworkBody(
            self._observation_specs(),
            self._network_settings(),
        )
        calls = {"bee_encoder": 0, "human_encoder": 0}

        def count(name):
            def hook(_module, _inputs, _output):
                calls[name] += 1
            return hook

        handles = [
            body.bee_observation_encoder.register_forward_hook(
                count("bee_encoder")
            ),
            body.human_observation_encoder.register_forward_hook(
                count("human_encoder")
            ),
        ]
        observations = torch.zeros((4, BEES_OBSERVATION_SIZE))
        observations[:, FACTION_INDEX] = 1.0
        token = set_training_faction_rows({
            "bee": (0, 1, 2, 3),
            "human": (),
            "mixed": (),
        })
        try:
            with mock.patch.object(
                torch,
                "nonzero",
                side_effect=AssertionError(
                    "precomputed gradient faction rows must not use torch.nonzero"
                ),
            ):
                output, _ = body([observations])
                output.sum().backward()
        finally:
            reset_training_faction_rows(token)
            for handle in handles:
                handle.remove()

        self.assertEqual(output.shape, (4, ACTION_ENCODING_SIZE))
        self.assertEqual(calls["bee_encoder"], 1)
        self.assertEqual(calls["human_encoder"], 0)

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
