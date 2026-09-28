from __future__ import annotations

import tempfile
import unittest
from types import SimpleNamespace
from unittest import mock

import bees_continual_elastic_wan_service as service
import bees_continual_wan_service as wan_service


class ContinualElasticWanServiceTests(unittest.TestCase):
    def test_wan_flags_are_inserted_before_mlagents_environment_args(self):
        command = [
            "python",
            "train.py",
            "--resume",
            "--env-args",
            "--rl-map-size=128",
            "--rl-obstacles=1",
        ]
        result = service.insert_wan_args_before_environment_args(
            command,
            ["--bees-wan-actors=12", "--bees-wan-broker-port=55051"],
        )
        marker = result.index("--env-args")
        self.assertEqual(
            result[marker - 2 : marker],
            ["--bees-wan-actors=12", "--bees-wan-broker-port=55051"],
        )
        self.assertEqual(
            result[marker + 1 :],
            ["--rl-map-size=128", "--rl-obstacles=1"],
        )

    def test_elastic_wrapper_forwards_force_fresh_to_base_training_command(self):
        actor_options = SimpleNamespace(
            enabled=True,
            max_actors=12,
            min_actors=1,
            broker_port=55051,
            auth_token_file="wan.token",
            max_queued_batches=32,
            actor_lease_seconds=120.0,
        )
        options = SimpleNamespace(
            assets_root=service.Path("B:/Bees/Assets"),
            runtime_training_root=service.Path("B:/Bees/Runtime/Releases/build/Training"),
        )
        captured = {}

        def base_training_command(_options, index, *, resume, force_fresh=False):
            captured["index"] = index
            captured["resume"] = resume
            captured["force_fresh"] = force_fresh
            return ["python", "base-train.py", "--force"]

        def run_service(_options):
            command = service.service.training_command(
                options,
                0,
                resume=False,
                force_fresh=True,
            )
            self.assertIn(
                "bees_continual_elastic_wan_auto_train.py",
                command[1],
            )
            return 0

        with (
            mock.patch.object(
                service.elastic,
                "extract_elastic_wan_options",
                return_value=([], actor_options),
            ),
            mock.patch.object(
                service,
                "parse_elastic_service_options",
                return_value=options,
            ),
            mock.patch(
                "bees_wan_actor_training.load_auth_token",
                return_value="token",
            ),
            mock.patch.object(
                service.service,
                "training_command",
                side_effect=base_training_command,
            ),
            mock.patch.object(
                service.service,
                "run_service",
                side_effect=run_service,
            ),
        ):
            self.assertEqual(service.main([]), 0)

        self.assertEqual(
            captured,
            {"index": 0, "resume": False, "force_fresh": True},
        )


    def test_wan_wrapper_forwards_fresh_retry_and_uses_pinned_runtime_root(self):
        actor_options = SimpleNamespace(
            enabled=True,
            actor_count=1,
            envs_per_actor=2,
            min_actors=1,
            broker_port=55051,
            auth_token_file="wan.token",
            max_queued_batches=32,
        )
        options = SimpleNamespace(
            assets_root=service.Path("B:/Bees/Assets"),
            runtime_training_root=service.Path("B:/Bees/Runtime/Releases/build/Training"),
        )
        captured = {}

        def base_training_command(_options, index, *, resume, force_fresh=False):
            captured["index"] = index
            captured["resume"] = resume
            captured["force_fresh"] = force_fresh
            return ["python", "base-train.py", "--force"] if force_fresh else ["python", "base-train.py"]

        def run_service(_options):
            captured["command"] = wan_service.service.training_command(
                options,
                0,
                resume=False,
                force_fresh=True,
            )
            return 0

        with (
            mock.patch.object(
                wan_service.wan,
                "extract_wan_actor_options",
                return_value=([], actor_options),
            ),
            mock.patch.object(wan_service.wan, "load_auth_token", return_value="token"),
            mock.patch.object(wan_service.service, "parse_options", return_value=options),
            mock.patch.object(
                wan_service.service,
                "training_command",
                side_effect=base_training_command,
            ),
            mock.patch.object(
                wan_service.service,
                "run_service",
                side_effect=run_service,
            ),
        ):
            self.assertEqual(wan_service.main([]), 0)

        self.assertEqual(
            {key: captured[key] for key in ("index", "resume", "force_fresh")},
            {"index": 0, "resume": False, "force_fresh": True},
        )
        self.assertEqual(
            captured["command"][1],
            str(options.runtime_training_root / "bees_continual_wan_auto_train.py"),
        )
        self.assertIn("--force", captured["command"])
        self.assertIn("--bees-wan-actors=1", captured["command"])

    def test_exact_central_operator_option_shape_parses_without_argparse_exit(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = service.Path(temp_dir)
            assets = root / "Assets"
            runtime = root / "ReleaseTraining"
            project = root / "Bees"
            training_root = root / "TrainingState"
            telemetry = training_root / "Telemetry"
            models = training_root / "Models"
            for directory in (assets, runtime, project):
                directory.mkdir(parents=True, exist_ok=True)

            environment = root / "Bees RL Training.exe"
            unity = root / "Unity.exe"
            trainer = runtime / "rl_1v1_config.yaml"
            continual = runtime / "continual_learning_config.json"
            auth = root / "wan.token"
            for file_path in (environment, unity, trainer, continual, auth):
                file_path.write_text("placeholder\n", encoding="utf-8")

            argv = [
                "--root", str(training_root),
                "--assets-root", str(assets),
                "--runtime-training-root", str(runtime),
                "--training-env", str(environment),
                "--telemetry-quarantine", str(telemetry),
                "--model-distribution-root", str(models),
                "--game-build-version", "build-123",
                "--run-id", "bees-v20-test",
                "--trainer-config", str(trainer),
                "--continual-config", str(continual),
                "--unity-editor", str(unity),
                "--unity-project-root", str(project),
                "--generation-steps", "1000000",
                "--num-envs", "0",
                "--platform", "WindowsPlayer",
                "--bees-wan-actors", "12",
                "--bees-wan-min-actors", "0",
                "--bees-wan-broker-port", "55051",
                "--bees-wan-auth-token-file", str(auth),
            ]

            service_args, actor_options = service.elastic.extract_elastic_wan_options(argv)
            options = service.parse_elastic_service_options(service_args)

            self.assertEqual(options.root, training_root.resolve())
            self.assertEqual(options.assets_root, assets.resolve())
            self.assertEqual(options.runtime_training_root, runtime.resolve())
            self.assertEqual(options.training_env, environment.resolve())
            self.assertEqual(options.unity_editor, unity.resolve())
            self.assertEqual(options.unity_project_root, project.resolve())
            self.assertEqual(options.run_id, "bees-v20-test")
            self.assertEqual(options.num_envs, 0)
            self.assertEqual(options.platform, "WindowsPlayer")
            self.assertEqual(actor_options.max_actors, 12)
            self.assertEqual(actor_options.min_actors, 0)
            self.assertEqual(actor_options.broker_port, 55051)
            self.assertEqual(
                service.Path(actor_options.auth_token_file).resolve(),
                auth.resolve(),
            )


    def test_wan_flags_append_when_no_environment_args_exist(self):
        self.assertEqual(
            service.insert_wan_args_before_environment_args(
                ["python", "train.py"],
                ["--bees-wan-actors=12"],
            ),
            ["python", "train.py", "--bees-wan-actors=12"],
        )


if __name__ == "__main__":
    unittest.main()
