"""Focused tests for elastic WAN actor-slot ownership and liveness."""

from __future__ import annotations

import unittest
from types import SimpleNamespace

import bees_elastic_wan_slot_safety as slot_safety
import bees_elastic_wan_training as elastic


class FakeObservationSpec:
    shape = (4,)
    dimension_property = ()
    observation_type = "DEFAULT"


class FakeActionSpec:
    continuous_size = 2
    discrete_branches = (3, 2)


class MismatchedActionSpec:
    continuous_size = 3
    discrete_branches = (3, 2)


class FakeBehaviorSpec:
    observation_specs = (FakeObservationSpec(),)
    action_spec = FakeActionSpec()


class MismatchedBehaviorSpec:
    observation_specs = (FakeObservationSpec(),)
    action_spec = MismatchedActionSpec()


class SlotSafetyTests(unittest.TestCase):
    def _unreferenced_broker(self):
        options = elastic.ElasticWanOptions(
            max_actors=12,
            auth_token_file="unused",
            actor_lease_seconds=120.0,
        )
        run_options = SimpleNamespace(
            checkpoint_settings=SimpleNamespace(run_id="slot-test")
        )
        broker = slot_safety.SlotSafeElasticWanBroker(
            options,
            run_options,
            "0123456789abcdef0123456789abcdef",
            local_envs=32,
        )
        broker.initialize_control({})
        return broker

    def _broker(self):
        broker = self._unreferenced_broker()
        specs = {"BeesRL1v1?team=0": FakeBehaviorSpec()}
        broker.set_reference_behavior_specs(specs)
        return broker, specs

    def test_same_process_can_refresh_its_slot(self):
        broker, specs = self._broker()
        payload = {
            "actor_id": 2,
            "actor_instance_id": "a" * 32,
            "env_count": 16,
            "control_epoch": 1,
            "behavior_specs": specs,
        }
        broker.register_actor(payload)
        broker.register_actor(payload)
        self.assertEqual(broker.active_actor_snapshot(), {2: 16})

    def test_second_live_process_cannot_take_an_owned_slot(self):
        broker, specs = self._broker()
        broker.register_actor(
            {
                "actor_id": 2,
                "actor_instance_id": "a" * 32,
                "env_count": 16,
                "control_epoch": 1,
                "behavior_specs": specs,
            }
        )
        with self.assertRaisesRegex(ValueError, "already owned"):
            broker.register_actor(
                {
                    "actor_id": 2,
                    "actor_instance_id": "b" * 32,
                    "env_count": 16,
                    "control_epoch": 1,
                    "behavior_specs": specs,
                }
            )

    def test_authenticated_control_ack_refreshes_actor_lease(self):
        broker, specs = self._broker()
        broker.register_actor(
            {
                "actor_id": 1,
                "actor_instance_id": "a" * 32,
                "env_count": 8,
                "control_epoch": 1,
                "behavior_specs": specs,
            }
        )
        before = broker._registrations[1]["last_seen"]
        broker.acknowledge_reset(
            {
                "actor_id": 1,
                "control_epoch": 1,
            }
        )
        self.assertGreaterEqual(broker._registrations[1]["last_seen"], before)

    def test_exeter_local_behavior_specs_reject_incompatible_early_actor(self):
        broker = self._unreferenced_broker()
        remote_specs = {"BeesRL1v1?team=0": MismatchedBehaviorSpec()}
        broker.register_actor(
            {
                "actor_id": 0,
                "actor_instance_id": "a" * 32,
                "env_count": 4,
                "control_epoch": 1,
                "behavior_specs": remote_specs,
            }
        )
        with self.assertRaisesRegex(RuntimeError, "differ from Exeter"):
            broker.set_reference_behavior_specs(
                {"BeesRL1v1?team=0": FakeBehaviorSpec()}
            )


if __name__ == "__main__":
    unittest.main()
