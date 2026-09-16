"""Focused regression test for Bees masked continuous entropy semantics.

Run from the Bees Assets root inside the ML-Agents virtual environment:

    python Training\bees_mlagents_entropy_tests.py
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


class _FakeContinuousDistribution:
    def __init__(self, std):
        self.std = std

    def log_prob(self, actions):
        return actions.new_zeros(actions.shape)

    def entropy(self):
        return 0.5 * self.std.new_ones(self.std.shape)


class MaskedContinuousEntropyTests(unittest.TestCase):
    def test_active_continuous_entropy_is_summed_not_averaged(self):
        from mlagents.torch_utils import torch

        action_spec = SimpleNamespace(
            continuous_size=compat.BEES_CONTINUOUS_ACTIONS,
            discrete_branches=compat.BEES_DISCRETE_BRANCHES,
            discrete_size=len(compat.BEES_DISCRETE_BRANCHES),
        )
        action_model = SimpleNamespace(action_spec=action_spec)
        actions = SimpleNamespace(
            continuous_tensor=torch.zeros((1, compat.BEES_CONTINUOUS_ACTIONS)),
        )
        std = torch.ones((1, compat.BEES_CONTINUOUS_ACTIONS))
        dists = SimpleNamespace(
            continuous=_FakeContinuousDistribution(std),
            discrete=None,
        )
        masks = torch.ones((1, sum(compat.BEES_DISCRETE_BRANCHES)))

        # Leave slot 0 active and mask the other 15 weapon slots. Together with
        # the two movement dimensions this gives exactly four active continuous
        # dimensions: movement X/Y plus turret-0 aim X/Y.
        for slot in range(1, compat.BEES_WEAPON_SLOTS):
            masks[0, slot * 2 + 1] = 0.0

        _, entropy = compat._masked_action_log_probs_and_entropy(
            action_model,
            actions,
            dists,
            masks,
        )

        per_dimension = 0.5 * math.log(
            2 * math.pi * math.e + compat.ACTION_ENTROPY_EPSILON
        )
        self.assertAlmostEqual(float(entropy.item()), 4.0 * per_dimension, places=6)


if __name__ == "__main__":
    unittest.main()
