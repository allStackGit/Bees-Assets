"""Focused regression tests for Training/bees_mlagents_learn.py.

Run from the Bees Assets root inside the ML-Agents virtual environment:

    python Training\bees_mlagents_learn_tests.py
"""

from __future__ import annotations

import importlib.util
from pathlib import Path
import unittest

import numpy as np


LAUNCHER_PATH = Path(__file__).with_name("bees_mlagents_learn.py")
SPEC = importlib.util.spec_from_file_location("bees_mlagents_learn", LAUNCHER_PATH)
launcher = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(launcher)


class BeesOptionParsingTests(unittest.TestCase):
    def test_batch_flag_is_removed_before_mlagents_parsing(self):
        (
            trainer_args,
            torch_threads,
            batch_inference,
            cpu_inference,
        ) = launcher._extract_bees_options(
            [
                "Training/rl_1v1_config.yaml",
                "--bees-batch-inference",
                "--torch-device=cpu",
            ]
        )

        self.assertEqual(
            trainer_args,
            ["Training/rl_1v1_config.yaml", "--torch-device=cpu"],
        )
        self.assertIsNone(torch_threads)
        self.assertTrue(batch_inference)
        self.assertFalse(cpu_inference)

    def test_thread_batch_and_cpu_inference_flags_can_be_combined(self):
        (
            trainer_args,
            torch_threads,
            batch_inference,
            cpu_inference,
        ) = launcher._extract_bees_options(
            [
                "Training/rl_1v1_config.yaml",
                "--bees-torch-threads=2",
                "--bees-batch-inference",
                "--bees-cpu-inference",
                "--resume",
            ]
        )

        self.assertEqual(
            trainer_args,
            ["Training/rl_1v1_config.yaml", "--resume"],
        )
        self.assertEqual(torch_threads, 2)
        self.assertTrue(batch_inference)
        self.assertTrue(cpu_inference)


class CpuInferenceActorCacheTests(unittest.TestCase):
    @staticmethod
    def _cpu_numpy(tensor):
        return tensor.detach().cpu().numpy().copy()

    def test_replica_stays_on_cpu_and_refreshes_parameters_and_buffers(self):
        from mlagents.torch_utils import torch

        class FakeActor(torch.nn.Module):
            def __init__(self):
                super().__init__()
                self.linear = torch.nn.Linear(2, 1, bias=False)
                self.register_buffer("running", torch.zeros(2))

        class FakePolicy:
            def __init__(self):
                self.actor = FakeActor()

        policy = FakePolicy()
        cache = launcher._CpuInferenceActorCache()

        replica = cache.get("BeesRL1v1?team=0", policy)

        self.assertIsNot(replica, policy.actor)
        self.assertEqual(next(replica.parameters()).device.type, "cpu")
        original_replica_weight = self._cpu_numpy(replica.linear.weight)

        with torch.no_grad():
            policy.actor.linear.weight.add_(1.0)
            policy.actor.running = policy.actor.running + 3.0

        refreshed = cache.get("BeesRL1v1?team=0", policy)

        self.assertIs(refreshed, replica)
        self.assertFalse(
            np.array_equal(
                original_replica_weight,
                self._cpu_numpy(refreshed.linear.weight),
            )
        )
        np.testing.assert_allclose(
            self._cpu_numpy(refreshed.linear.weight),
            self._cpu_numpy(policy.actor.linear.weight),
        )
        np.testing.assert_allclose(
            self._cpu_numpy(refreshed.running),
            np.asarray([3.0, 3.0], dtype=np.float32),
        )

    def test_behavior_rebuilds_replica_when_source_actor_changes(self):
        from mlagents.torch_utils import torch

        class FakeActor(torch.nn.Module):
            def __init__(self):
                super().__init__()
                self.weight = torch.nn.Parameter(torch.ones(1))

        class FakePolicy:
            def __init__(self):
                self.actor = FakeActor()

        cache = launcher._CpuInferenceActorCache()
        first_policy = FakePolicy()
        second_policy = FakePolicy()

        first_replica = cache.get("BeesRL1v1?team=1", first_policy)
        second_replica = cache.get("BeesRL1v1?team=1", second_policy)

        self.assertIsNot(first_replica, second_replica)
        self.assertIsNot(second_replica, second_policy.actor)
        self.assertEqual(next(second_replica.parameters()).device.type, "cpu")


class WorkerTimerSamplerTests(unittest.TestCase):
    def test_unsampled_steps_accumulate_until_sample_boundary(self):
        roots_returned = []
        resets = []

        def get_root():
            roots_returned.append("root")
            return "root"

        def reset():
            resets.append(True)

        sampler = launcher._WorkerTimerSampler(get_root, reset, 3)
        results = []
        for _ in range(6):
            results.append(sampler.get_timer_root())
            sampler.reset_timers()

        self.assertEqual(results, [None, None, "root", None, None, "root"])
        self.assertEqual(roots_returned, ["root", "root"])
        self.assertEqual(len(resets), 2)


class BatchedInferenceTests(unittest.TestCase):
    def setUp(self):
        from mlagents.trainers.subprocess_env_manager import SubprocessEnvManager

        self.SubprocessEnvManager = SubprocessEnvManager
        self.original_queue_steps = launcher._install_batched_inference()

    def tearDown(self):
        self.SubprocessEnvManager._queue_steps = self.original_queue_steps

    @staticmethod
    def _decision_steps(agent_id: int, observation: float):
        from mlagents_envs.base_env import DecisionSteps

        return DecisionSteps(
            obs=[np.asarray([[observation]], dtype=np.float32)],
            reward=np.asarray([0.0], dtype=np.float32),
            agent_id=np.asarray([agent_id], dtype=np.int32),
            action_mask=None,
            group_id=np.asarray([0], dtype=np.int32),
            group_reward=np.asarray([0.0], dtype=np.float32),
        )

    @staticmethod
    def _empty_decision_steps():
        from mlagents_envs.base_env import DecisionSteps

        return DecisionSteps(
            obs=[np.zeros((0, 1), dtype=np.float32)],
            reward=np.zeros(0, dtype=np.float32),
            agent_id=np.zeros(0, dtype=np.int32),
            action_mask=None,
            group_id=np.zeros(0, dtype=np.int32),
            group_reward=np.zeros(0, dtype=np.float32),
        )

    @staticmethod
    def _worker(worker_id: int, behavior_steps):
        from mlagents.trainers.env_manager import EnvironmentStep

        class FakeWorker:
            def __init__(self):
                self.worker_id = worker_id
                self.waiting = False
                self.previous_step = EnvironmentStep(
                    {
                        behavior_name: (decision_steps, None)
                        for behavior_name, decision_steps in behavior_steps.items()
                    },
                    worker_id,
                    {},
                    {},
                )
                self.previous_all_action_info = {}
                self.sent = []

            def send(self, command, payload):
                self.sent.append((command, payload))

        return FakeWorker()

    @staticmethod
    def _policy():
        from mlagents.trainers.policy.torch_policy import TorchPolicy
        from mlagents.trainers.torch_entities.action_log_probs import LogProbsTuple
        from mlagents_envs.base_env import ActionSpec, ActionTuple, BehaviorSpec

        class FakeTorchPolicy(TorchPolicy):
            def __init__(self):
                self.behavior_spec = BehaviorSpec([], ActionSpec.create_continuous(1))
                self.evaluate_calls = []
                self.saved_memories = []
                self.checked_actions = []

            def evaluate(self, decision_requests, global_agent_ids):
                self.evaluate_calls.append(
                    (decision_requests, list(global_agent_ids))
                )
                values = decision_requests.obs[0].astype(np.float32, copy=True)
                action = ActionTuple(continuous=values)
                env_action = ActionTuple(continuous=values + 100.0)
                log_probs = LogProbsTuple(
                    continuous=np.full(values.shape, -0.5, dtype=np.float32)
                )
                return {
                    "action": action,
                    "env_action": env_action,
                    "log_probs": log_probs,
                    "entropy": values[:, 0] + 0.25,
                    "memory_out": np.concatenate([values, values + 1.0], axis=1),
                }

            def save_memories(self, global_agent_ids, memory_matrix):
                self.saved_memories.append(
                    (list(global_agent_ids), memory_matrix.copy())
                )

            def check_nan_action(self, action):
                self.checked_actions.append(action)

        return FakeTorchPolicy()

    def test_same_behavior_workers_are_evaluated_once_and_ipc_is_slim(self):
        from mlagents.trainers.behavior_id_utils import get_global_agent_id
        from mlagents.trainers.subprocess_env_manager import EnvironmentCommand

        behavior = "BeesRL1v1?team=0"
        worker0 = self._worker(0, {behavior: self._decision_steps(11, 1.0)})
        worker1 = self._worker(1, {behavior: self._decision_steps(22, 2.0)})
        policy = self._policy()

        class FakeManager:
            pass

        manager = FakeManager()
        manager.env_workers = [worker0, worker1]
        manager.policies = {behavior: policy}

        self.SubprocessEnvManager._queue_steps(manager)

        self.assertEqual(len(policy.evaluate_calls), 1)
        batched_steps, global_agent_ids = policy.evaluate_calls[0]
        np.testing.assert_array_equal(
            batched_steps.obs[0],
            np.asarray([[1.0], [2.0]], dtype=np.float32),
        )
        self.assertEqual(
            global_agent_ids,
            [get_global_agent_id(0, 11), get_global_agent_id(1, 22)],
        )

        for worker, expected_agent, expected_action in (
            (worker0, 11, 1.0),
            (worker1, 22, 2.0),
        ):
            self.assertTrue(worker.waiting)
            self.assertEqual(len(worker.sent), 1)
            command, payload = worker.sent[0]
            self.assertEqual(command, EnvironmentCommand.STEP)

            trainer_info = worker.previous_all_action_info[behavior]
            self.assertEqual(trainer_info.agent_ids, [expected_agent])
            np.testing.assert_array_equal(
                trainer_info.action.continuous,
                np.asarray([[expected_action]], dtype=np.float32),
            )
            np.testing.assert_array_equal(
                trainer_info.outputs["entropy"],
                np.asarray([expected_action + 0.25], dtype=np.float32),
            )
            np.testing.assert_array_equal(
                trainer_info.outputs["memory_out"],
                np.asarray(
                    [[expected_action, expected_action + 1.0]],
                    dtype=np.float32,
                ),
            )

            ipc_info = payload[behavior]
            self.assertEqual(ipc_info.agent_ids, [expected_agent])
            self.assertEqual(ipc_info.action, [])
            self.assertEqual(ipc_info.outputs, {})
            np.testing.assert_array_equal(
                ipc_info.env_action.continuous,
                np.asarray([[expected_action + 100.0]], dtype=np.float32),
            )

        self.assertEqual(len(policy.saved_memories), 1)
        self.assertEqual(len(policy.checked_actions), 1)

    def test_distinct_self_play_behaviors_are_not_merged(self):
        team0 = "BeesRL1v1?team=0"
        team1 = "BeesRL1v1?team=1"
        worker0 = self._worker(
            0,
            {
                team0: self._decision_steps(11, 1.0),
                team1: self._decision_steps(12, 10.0),
            },
        )
        worker1 = self._worker(
            1,
            {
                team0: self._decision_steps(21, 2.0),
                team1: self._decision_steps(22, 20.0),
            },
        )
        team0_policy = self._policy()
        team1_policy = self._policy()

        class FakeManager:
            pass

        manager = FakeManager()
        manager.env_workers = [worker0, worker1]
        manager.policies = {
            team0: team0_policy,
            team1: team1_policy,
        }

        self.SubprocessEnvManager._queue_steps(manager)

        self.assertEqual(len(team0_policy.evaluate_calls), 1)
        self.assertEqual(len(team1_policy.evaluate_calls), 1)
        np.testing.assert_array_equal(
            team0_policy.evaluate_calls[0][0].obs[0],
            np.asarray([[1.0], [2.0]], dtype=np.float32),
        )
        np.testing.assert_array_equal(
            team1_policy.evaluate_calls[0][0].obs[0],
            np.asarray([[10.0], [20.0]], dtype=np.float32),
        )

    def test_empty_worker_behavior_keeps_empty_action_info(self):
        behavior = "BeesRL1v1?team=0"
        worker = self._worker(0, {behavior: self._empty_decision_steps()})
        policy = self._policy()

        class FakeManager:
            pass

        manager = FakeManager()
        manager.env_workers = [worker]
        manager.policies = {behavior: policy}

        self.SubprocessEnvManager._queue_steps(manager)

        self.assertEqual(len(policy.evaluate_calls), 0)
        self.assertEqual(worker.previous_all_action_info[behavior].agent_ids, [])
        info = worker.sent[0][1][behavior]
        self.assertEqual(info.agent_ids, [])
        self.assertEqual(info.outputs, {})


class FastEnvManagerTests(unittest.TestCase):
    def setUp(self):
        from mlagents.trainers.env_manager import EnvManager
        from mlagents.trainers.subprocess_env_manager import SubprocessEnvManager

        self.EnvManager = EnvManager
        self.SubprocessEnvManager = SubprocessEnvManager
        (
            self.original_step,
            self.original_process_step_infos,
        ) = launcher._install_fast_env_manager()

    def tearDown(self):
        self.SubprocessEnvManager._step = self.original_step
        self.EnvManager._process_step_infos = self.original_process_step_infos

    def test_blocking_first_result_then_drains_ready_workers(self):
        from queue import Empty
        from types import SimpleNamespace

        from mlagents.trainers.subprocess_env_manager import (
            EnvironmentCommand,
            EnvironmentResponse,
        )

        responses = [
            EnvironmentResponse(EnvironmentCommand.STEP, 0, "worker-0"),
            EnvironmentResponse(EnvironmentCommand.STEP, 1, "worker-1"),
        ]

        class FakeQueue:
            def __init__(self):
                self.values = list(responses)
                self.blocking_get_calls = 0
                self.nonblocking_get_calls = 0

            def get(self, timeout=None):
                self.blocking_get_calls += 1
                if not self.values:
                    raise Empty()
                return self.values.pop(0)

            def get_nowait(self):
                self.nonblocking_get_calls += 1
                if not self.values:
                    raise Empty()
                return self.values.pop(0)

        class FakeManager:
            def __init__(self):
                self.step_queue = FakeQueue()
                self.env_workers = [
                    SimpleNamespace(waiting=True),
                    SimpleNamespace(waiting=True),
                ]
                self.queue_steps_calls = 0

            def _queue_steps(self):
                self.queue_steps_calls += 1

            def _restart_failed_workers(self, step):
                raise AssertionError("No worker should fail in this test")

            @staticmethod
            def _postprocess_steps(worker_steps):
                return worker_steps

        manager = FakeManager()
        result = self.SubprocessEnvManager._step(manager)

        self.assertEqual(manager.step_queue.blocking_get_calls, 1)
        self.assertGreaterEqual(manager.step_queue.nonblocking_get_calls, 1)
        self.assertEqual(manager.queue_steps_calls, 1)
        self.assertFalse(manager.env_workers[0].waiting)
        self.assertFalse(manager.env_workers[1].waiting)
        self.assertEqual(result, responses)


if __name__ == "__main__":
    unittest.main()
