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
        trainer_args, torch_threads, batch_inference = launcher._extract_bees_options(
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

    def test_thread_flag_and_batch_flag_can_be_combined(self):
        trainer_args, torch_threads, batch_inference = launcher._extract_bees_options(
            [
                "Training/rl_1v1_config.yaml",
                "--bees-torch-threads=2",
                "--bees-batch-inference",
                "--resume",
            ]
        )

        self.assertEqual(
            trainer_args,
            ["Training/rl_1v1_config.yaml", "--resume"],
        )
        self.assertEqual(torch_threads, 2)
        self.assertTrue(batch_inference)


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

    def test_same_behavior_workers_are_evaluated_once_and_split_back(self):
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
            info = payload[behavior]
            self.assertEqual(info.agent_ids, [expected_agent])
            np.testing.assert_array_equal(
                info.action.continuous,
                np.asarray([[expected_action]], dtype=np.float32),
            )
            np.testing.assert_array_equal(
                info.env_action.continuous,
                np.asarray([[expected_action + 100.0]], dtype=np.float32),
            )
            np.testing.assert_array_equal(
                info.outputs["entropy"],
                np.asarray([expected_action + 0.25], dtype=np.float32),
            )
            np.testing.assert_array_equal(
                info.outputs["memory_out"],
                np.asarray(
                    [[expected_action, expected_action + 1.0]],
                    dtype=np.float32,
                ),
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
        info = worker.sent[0][1][behavior]
        self.assertEqual(info.agent_ids, [])
        self.assertEqual(info.outputs, {})


if __name__ == "__main__":
    unittest.main()
