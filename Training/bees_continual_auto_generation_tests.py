"""Focused tests for bounded continuous automatic-learning generations."""

from __future__ import annotations

import unittest

import bees_continual_auto_train as auto_train


class AutomaticLearningGenerationOptionTests(unittest.TestCase):
    def test_generation_id_is_consumed_by_wrapper_not_forwarded_to_mlagents(self):
        cleaned, options = auto_train.extract_automatic_public_options(
            [
                "Training/rl_1v1_config.yaml",
                "--continual-public-telemetry-quarantine=D:/quarantine",
                "--continual-public-generation-id=generation-000123",
                "--run-id=continuous",
            ]
        )
        self.assertEqual(options.generation_id, "generation-000123")
        self.assertEqual(
            cleaned,
            ["Training/rl_1v1_config.yaml", "--run-id=continuous"],
        )

    def test_generation_id_may_be_omitted_for_legacy_one_shot_runs(self):
        _cleaned, options = auto_train.extract_automatic_public_options(
            ["--continual-public-telemetry-quarantine=D:/quarantine"]
        )
        self.assertIsNone(options.generation_id)

    def test_generation_id_rejects_empty_or_duplicate_values(self):
        with self.assertRaises(SystemExit):
            auto_train.extract_automatic_public_options(
                [
                    "--continual-public-telemetry-quarantine=D:/quarantine",
                    "--continual-public-generation-id=",
                ]
            )
        with self.assertRaises(SystemExit):
            auto_train.extract_automatic_public_options(
                [
                    "--continual-public-telemetry-quarantine=D:/quarantine",
                    "--continual-public-generation-id=a",
                    "--continual-public-generation-id=b",
                ]
            )


if __name__ == "__main__":
    unittest.main()
