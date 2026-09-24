from __future__ import annotations

import unittest

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
