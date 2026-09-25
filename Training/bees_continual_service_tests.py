"""Focused tests for the autonomous continual-learning service orchestration."""

from __future__ import annotations

import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import bees_continual_service as service


class ContinualServiceTests(unittest.TestCase):
    def test_rewrite_max_steps_requires_one_behavior_target(self):
        source = "behaviors:\n  BeesRL1v1:\n    max_steps: 2000000000\n"
        self.assertIn("max_steps: 1250000", service.rewrite_max_steps(source, 1_250_000))
        with self.assertRaises(ValueError):
            service.rewrite_max_steps("behaviors: {}\n", 10)
        with self.assertRaises(ValueError):
            service.rewrite_max_steps("max_steps: 1\nmax_steps: 2\n", 10)

    def test_managed_stop_interrupts_training_child_group_for_final_save(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            options = self._options(root)
            fake = mock.Mock()
            fake.pid = 6161
            fake.poll.side_effect = [None, 0, 0]
            fake.wait.return_value = 0

            with (
                mock.patch.object(service.os, "name", "posix"),
                mock.patch.object(service, "_managed_stop_requested", side_effect=[False, True]),
                mock.patch.object(service.subprocess, "Popen", return_value=fake) as popen,
                mock.patch.object(service.os, "killpg") as killpg,
                mock.patch.object(service.time, "sleep"),
            ):
                with self.assertRaises(KeyboardInterrupt):
                    service._run_managed_subprocess(["python", "trainer.py"], options)

            self.assertTrue(popen.call_args.kwargs["start_new_session"])
            killpg.assert_called_once_with(6161, service.signal.SIGINT)

    def test_generation_targets_are_cumulative_for_resume_lineage(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            options = self._options(Path(temp_dir), generation_steps=250_000)
            self.assertEqual(service.generation_id(0), "generation-00000000")
            self.assertEqual(service.generation_target_steps(options, 0), 250_000)
            self.assertEqual(service.generation_target_steps(options, 3), 1_000_000)

    def test_failed_first_start_does_not_force_resume_without_run_data(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            options = self._options(Path(temp_dir))
            self.assertFalse(
                service.should_resume_training(
                    options,
                    generation_index=0,
                    previously_started=True,
                )
            )

    def test_first_generation_requires_real_checkpoint_before_resume(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            options = self._options(Path(temp_dir))
            run_dir = options.root / "trainer-results" / options.run_id
            run_dir.mkdir(parents=True)
            self.assertFalse(
                service.should_resume_training(
                    options,
                    generation_index=0,
                    previously_started=True,
                )
            )
            checkpoint = run_dir / "BeesRL1v1" / "checkpoint.pt"
            checkpoint.parent.mkdir()
            checkpoint.write_bytes(b"checkpoint")
            self.assertTrue(
                service.should_resume_training(
                    options,
                    generation_index=0,
                    previously_started=True,
                )
            )

    def test_later_generations_require_persistent_checkpoint_lineage(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            options = self._options(Path(temp_dir))
            with self.assertRaisesRegex(RuntimeError, "checkpoint is missing"):
                service.should_resume_training(
                    options,
                    generation_index=1,
                    previously_started=False,
                )
            checkpoint = (
                options.root
                / "trainer-results"
                / options.run_id
                / "BeesRL1v1"
                / "checkpoint.pt"
            )
            checkpoint.parent.mkdir(parents=True)
            checkpoint.write_bytes(b"checkpoint")
            self.assertTrue(
                service.should_resume_training(
                    options,
                    generation_index=1,
                    previously_started=False,
                )
            )

    def test_training_command_reuses_run_id_and_changes_only_generation_target(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            options = self._options(root, generation_steps=100)
            command0 = service.training_command(options, 0, resume=False)
            command1 = service.training_command(options, 1, resume=True)
            command0_retry = service.training_command(
                options,
                0,
                resume=False,
                force_fresh=True,
            )

            self.assertIn("--run-id=continuous-test", command0)
            self.assertIn("--run-id=continuous-test", command1)
            self.assertNotIn("--resume", command0)
            self.assertIn("--resume", command1)
            self.assertIn("--force", command0_retry)
            self.assertNotIn("--resume", command0_retry)
            self.assertIn("--continual-public-generation-id=generation-00000000", command0)
            self.assertIn("--continual-public-generation-id=generation-00000001", command1)
            self.assertIn("max_steps: 100", Path(command0[2]).read_text(encoding="utf-8"))
            self.assertIn("max_steps: 200", Path(command1[2]).read_text(encoding="utf-8"))

    def test_training_command_appends_server_environment_args_last(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            options = self._options(root)
            options = service.ServiceOptions(
                **{
                    **options.__dict__,
                    "environment_args": (
                        "--rl-map-size=128",
                        "--rl-bee-ship-types=Wasp,Hornet",
                    ),
                }
            )
            command = service.training_command(options, 0, resume=False)
            marker = command.index("--env-args")
            self.assertEqual(
                command[marker + 1 :],
                [
                    "--rl-map-size=128",
                    "--rl-bee-ship-types=Wasp,Hornet",
                ],
            )

    def test_environment_args_json_validation(self):
        self.assertEqual(
            service.parse_environment_args_json('["--rl-map-size=64"]'),
            ("--rl-map-size=64",),
        )
        with self.assertRaises(ValueError):
            service.parse_environment_args_json('{"not":"a-list"}')
        with self.assertRaises(ValueError):
            service.parse_environment_args_json('[""]')

    def test_state_rejects_run_id_change_and_preserves_phase(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            options = self._options(root)
            state = service.load_state(options)
            state["generation_index"] = 7
            state["phase"] = "release"
            state["training_started"] = True
            service.save_state(options, state)
            loaded = service.load_state(options)
            self.assertEqual(loaded["generation_index"], 7)
            self.assertEqual(loaded["phase"], "release")
            self.assertTrue(loaded["training_started"])

            changed = service.ServiceOptions(**{**options.__dict__, "run_id": "different"})
            with self.assertRaises(ValueError):
                service.load_state(changed)

    def test_release_and_hot_publish_commands_keep_validation_boundaries(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            options = self._options(Path(temp_dir))
            release = service.release_command(options)
            self.assertTrue(any("bees_continual_release.py" in item for item in release))
            self.assertIn("--once", release)
            self.assertIn("--no-graphics", release)

            stage = service.stage_command(options)
            self.assertTrue(any("bees_continual_unity_bundle.py" in item for item in stage))
            unity = service.unity_build_command(options, options.root / "hot")
            self.assertIn("RlLivePolicyHotBundleBuilder.BuildFromCommandLine", unity)
            publish = service.hot_publish_command(options, options.root / "metadata.json")
            self.assertTrue(any("bees_continual_hot_bundle.py" in item for item in publish))
            self.assertIn(f"--distribution-root={options.model_distribution_root}", publish)

    def test_current_deployment_reader_requires_canonical_identity(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            options = self._options(Path(temp_dir))
            pointer = options.root / "deployment" / "current-deployment.json"
            pointer.parent.mkdir(parents=True)
            pointer.write_text(
                json.dumps({"identity": {"deployment_id": "deploy-" + "a" * 24}}),
                encoding="utf-8",
            )
            self.assertEqual(service.current_deployment_id(options), "deploy-" + "a" * 24)
            pointer.write_text(json.dumps({"identity": {"deployment_id": "latest"}}), encoding="utf-8")
            with self.assertRaises(ValueError):
                service.current_deployment_id(options)

    def _options(self, root: Path, *, generation_steps: int = 1_000) -> service.ServiceOptions:
        assets = root / "Assets"
        training = assets / "Training"
        training.mkdir(parents=True, exist_ok=True)
        trainer_config = training / "rl_1v1_config.yaml"
        trainer_config.write_text(
            "behaviors:\n  BeesRL1v1:\n    trainer_type: ppo\n    max_steps: 2000000000\n",
            encoding="utf-8",
        )
        continual_config = training / "continual_learning_config.json"
        continual_config.write_text("{}\n", encoding="utf-8")
        training_env = root / "Bees RL Training.exe"
        training_env.write_bytes(b"x")
        unity = root / "Unity.exe"
        unity.write_bytes(b"x")
        project = root / "Project"
        project.mkdir()
        quarantine = root / "quarantine"
        quarantine.mkdir()
        distribution = root / "distribution"
        distribution.mkdir()
        store = root / "continual"
        store.mkdir()
        return service.ServiceOptions(
            root=store,
            assets_root=assets,
            training_env=training_env,
            telemetry_quarantine=quarantine,
            model_distribution_root=distribution,
            game_build_version="test-build",
            unity_editor=unity,
            unity_project_root=project,
            trainer_config=trainer_config,
            continual_config=continual_config,
            competency_suite=None,
            python_executable="python",
            run_id="continuous-test",
            generation_steps=generation_steps,
            num_envs=2,
            platform="WindowsPlayer",
            retry_seconds=1.0,
            once=True,
        )


if __name__ == "__main__":
    unittest.main()
