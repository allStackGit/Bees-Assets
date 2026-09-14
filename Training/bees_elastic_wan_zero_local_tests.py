"""Focused regression tests for elastic WAN learner-only (zero local Unity envs) mode."""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

import bees_continual_elastic_wan_service as elastic_service
import bees_elastic_wan_actor_worker as actor_worker
import bees_elastic_wan_training as elastic


class FakeObservationSpec:
    shape = (4,)
    dimension_property = ()
    observation_type = "DEFAULT"


class FakeActionSpec:
    continuous_size = 2
    discrete_branches = (3, 2)


class FakeBehaviorSpec:
    observation_specs = (FakeObservationSpec(),)
    action_spec = FakeActionSpec()


class ZeroLocalArgumentTests(unittest.TestCase):
    def test_elastic_service_normalizes_explicit_zero_only_for_base_parser(self):
        normalized, zero_local = elastic_service._normalize_zero_local_num_envs(
            ["--root=x", "--num-envs=0", "--once"]
        )
        self.assertTrue(zero_local)
        self.assertIn("--num-envs=1", normalized)
        self.assertNotIn("--num-envs=0", normalized)

    def test_nonzero_num_envs_is_not_rewritten(self):
        normalized, zero_local = elastic_service._normalize_zero_local_num_envs(
            ["--num-envs", "32"]
        )
        self.assertFalse(zero_local)
        self.assertEqual(normalized, ["--num-envs", "32"])

    def test_remote_actor_accepts_worker_base_zero(self):
        session = {
            "max_actors": 12,
            "max_envs_per_actor": 64,
            "remote_worker_base": 0,
            "worker_stride": 64,
            "capacity_envs": 768,
        }
        compatible, worker_offset, capacity = actor_worker._elastic_session(
            session,
            actor_id=0,
            env_count=17,
        )
        self.assertEqual(worker_offset, 0)
        self.assertEqual(capacity, 768)
        self.assertEqual(compatible["envs_per_actor"], 17)


class ZeroLocalBrokerTests(unittest.TestCase):
    def _broker(self):
        options = elastic.ElasticWanOptions(
            max_actors=12,
            min_actors=0,
            auth_token_file="unused",
            actor_lease_seconds=120.0,
        )
        run_options = SimpleNamespace(
            checkpoint_settings=SimpleNamespace(run_id="zero-local-test")
        )
        broker = elastic.ElasticWanBroker(
            options,
            run_options,
            "0123456789abcdef0123456789abcdef",
            local_envs=0,
        )
        broker.initialize_control({})
        return broker

    def test_zero_local_broker_uses_first_remote_behavior_specs(self):
        broker = self._broker()
        specs = {"BeesRL1v1?team=0": FakeBehaviorSpec()}
        broker.register_actor(
            {
                "actor_id": 0,
                "env_count": 8,
                "control_epoch": 1,
                "behavior_specs": specs,
            }
        )
        self.assertEqual(set(broker.merged_behavior_specs()), set(specs))
        self.assertEqual(broker.active_actor_snapshot(), {0: 8})
        session = broker.session_payload()
        self.assertEqual(session["remote_worker_base"], 0)
        self.assertEqual(session["capacity_envs"], 12 * 64)

    def test_zero_local_actor_worker_ids_begin_at_zero_without_overlap(self):
        options = elastic.ElasticWanOptions(max_actors=12, auth_token_file="unused")
        self.assertEqual(elastic.actor_worker_ids(options, 0, 3, 0), (0, 1, 2))
        self.assertEqual(elastic.actor_worker_ids(options, 1, 2, 0), (64, 65))


if __name__ == "__main__":
    unittest.main()
