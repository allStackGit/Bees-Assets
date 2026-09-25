"""Focused regression tests for elastic WAN learner-only (zero local Unity envs) mode."""

from __future__ import annotations

import os
import unittest
from unittest import mock
from types import SimpleNamespace

import bees_continual_elastic_wan_auto_train as elastic_auto
import bees_continual_elastic_wan_service as elastic_service
import bees_elastic_wan_actor_session as actor_session
import bees_elastic_wan_actor_worker as actor_worker
import bees_elastic_wan_training as elastic
import bees_elastic_wan_zero_local as zero_local


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


class ZeroLocalArgumentTests(unittest.TestCase):
    def test_elastic_service_normalizes_explicit_zero_only_for_base_parser(self):
        normalized, zero_local_requested = elastic_service._normalize_zero_local_num_envs(
            ["--root=x", "--num-envs=0", "--once"]
        )
        self.assertTrue(zero_local_requested)
        self.assertIn("--num-envs=1", normalized)
        self.assertNotIn("--num-envs=0", normalized)

    def test_mlagents_boundary_rewrites_zero_but_preserves_zero_local_intent(self):
        normalized, zero_local_requested = (
            elastic_auto._normalize_zero_local_num_envs_for_mlagents(
                ["config.yaml", "--num-envs=0", "--resume"]
            )
        )
        self.assertTrue(zero_local_requested)
        self.assertEqual(
            normalized,
            ["config.yaml", "--num-envs=1", "--resume"],
        )

    def test_auto_trainer_passes_zero_local_intent_behind_mlagents_parser(self):
        options = elastic.ElasticWanOptions(max_actors=12, auth_token_file="unused")
        patch_token = object()
        with (
            mock.patch.object(
                elastic_auto.elastic,
                "extract_elastic_wan_options",
                return_value=(["config.yaml", "--num-envs=0"], options),
            ),
            mock.patch.object(
                elastic_auto.policy_transport,
                "install_portable_policy_transport",
                return_value="policy-original",
            ),
            mock.patch.object(
                elastic_auto.slot_safety,
                "install_slot_safety",
                return_value="broker-original",
            ),
            mock.patch.object(
                elastic_auto.zero_local,
                "install_elastic_wan_env_manager",
                return_value=patch_token,
            ) as install_manager,
            mock.patch.object(
                elastic_auto.zero_local,
                "restore_elastic_wan_env_manager",
            ),
            mock.patch.object(
                elastic_auto.slot_safety,
                "restore_slot_safety",
            ),
            mock.patch.object(
                elastic_auto.policy_transport,
                "restore_portable_policy_transport",
            ),
            mock.patch.object(
                elastic_auto.continual_auto,
                "main",
                return_value=0,
            ) as continual_main,
        ):
            self.assertEqual(elastic_auto.main([]), 0)

        install_manager.assert_called_once_with(
            options,
            force_zero_local=True,
        )
        continual_main.assert_called_once_with(
            ["config.yaml", "--num-envs=1"]
        )

    def test_nonzero_num_envs_is_not_rewritten(self):
        normalized, zero_local_requested = elastic_service._normalize_zero_local_num_envs(
            ["--num-envs", "32"]
        )
        self.assertFalse(zero_local_requested)
        self.assertEqual(normalized, ["--num-envs", "32"])

    def test_duplicate_num_envs_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "only once"):
            elastic_service._normalize_zero_local_num_envs(
                ["--num-envs=0", "--num-envs=32"]
            )

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

    def test_actor_topology_accepts_zero_local_envs_when_remote_envs_exist(self):
        session = actor_session.ElasticActorSession.__new__(actor_session.ElasticActorSession)
        session.manager = SimpleNamespace(agent_managers={})
        session.total_envs = 1
        session.topology_epoch = -1
        session._rollout_horizons = {}
        session._apply_live_rollout_horizons(
            {"local_envs": 0, "remote_envs": 8, "topology_epoch": 1}
        )
        self.assertEqual(session.total_envs, 8)
        self.assertEqual(session.topology_epoch, 1)


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
        with mock.patch.dict(
            os.environ,
            {
                elastic.BUILD_ID_ENV: "zero-local-build",
                elastic.RUN_ID_ENV: "zero-local-test",
                elastic.COMPATIBILITY_KEY_ENV: "e" * 64,
            },
            clear=False,
        ):
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
                **broker.release_identity,
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

    def test_blocking_zero_local_batch_counts_as_learner_consumed(self):
        broker = self._broker()
        specs = {"BeesRL1v1?team=0": FakeBehaviorSpec()}
        broker.register_actor(
            {
                **broker.release_identity,
                "actor_id": 0,
                "env_count": 8,
                "control_epoch": 1,
                "behavior_specs": specs,
            }
        )
        broker._trajectory_batches.put_nowait(
            {
                "actor_id": 0,
                "policy_versions": {},
                "control_epoch": broker.control_epoch,
                "trajectories": [object()],
                "step_count": 19,
            }
        )
        manager = zero_local.ZeroLocalElasticWanEnvManagerMixin.__new__(
            zero_local.ZeroLocalElasticWanEnvManagerMixin
        )
        manager._bees_wan_broker = broker

        batch = manager._wait_for_current_remote_batch()
        self.assertEqual(batch["step_count"], 19)
        state = broker.wait_state(
            broker._policy_epoch,
            broker.control_epoch,
            0.0,
        )
        self.assertEqual(state["consumed_steps_by_actor"]["0"], 19)

    def test_stale_release_cannot_seed_zero_local_behavior_specs(self):
        broker = self._broker()
        specs = {"BeesRL1v1?team=0": FakeBehaviorSpec()}
        payload = {
            **broker.release_identity,
            "actor_id": 0,
            "env_count": 8,
            "control_epoch": 1,
            "behavior_specs": specs,
        }
        payload["compatibility_key"] = "f" * 64
        with self.assertRaisesRegex(ValueError, "release identity"):
            broker.register_actor(payload)
        self.assertEqual(broker.active_actor_snapshot(), {})

    def test_first_remote_behavior_specs_are_pinned_after_discovery(self):
        broker = self._broker()
        specs = {"BeesRL1v1?team=0": FakeBehaviorSpec()}
        broker.register_actor(
            {
                **broker.release_identity,
                "actor_id": 0,
                "env_count": 8,
                "control_epoch": 1,
                "behavior_specs": specs,
            }
        )

        manager = zero_local.ZeroLocalElasticWanEnvManagerMixin.__new__(
            zero_local.ZeroLocalElasticWanEnvManagerMixin
        )
        manager._bees_wan_broker = broker
        manager._behavior_discovery_step()

        # Simulate every original remote actor disappearing after the central trainer exists.
        broker._registrations.clear()
        with self.assertRaisesRegex(ValueError, "differ from Exeter"):
            broker.register_actor(
                {
                    **broker.release_identity,
                    "actor_id": 1,
                    "env_count": 4,
                    "control_epoch": 1,
                    "behavior_specs": {"BeesRL1v1?team=0": MismatchedBehaviorSpec()},
                }
            )

    def test_zero_local_actor_worker_ids_begin_at_zero_without_overlap(self):
        options = elastic.ElasticWanOptions(max_actors=12, auth_token_file="unused")
        self.assertEqual(elastic.actor_worker_ids(options, 0, 3, 0), (0, 1, 2))
        self.assertEqual(elastic.actor_worker_ids(options, 1, 2, 0), (64, 65))


if __name__ == "__main__":
    unittest.main()
