from __future__ import annotations

import unittest
from types import SimpleNamespace
from unittest import mock

import bees_continual_elastic_wan_service as service


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
