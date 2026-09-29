"""Focused tests for automatic public-learning trainer argument routing."""

from __future__ import annotations

import unittest

from bees_continual_auto_train import (
    MAX_BATCHES_FLAG,
    MAX_SUGGESTIONS_FLAG,
    MIN_CONTRIBUTORS_FLAG,
    MIN_OCCURRENCES_FLAG,
    QUARANTINE_FLAG,
    TARGET_FRACTION_FLAG,
    WATCH_SECONDS_FLAG,
    extract_automatic_public_options,
)


class AutomaticPublicTrainerOptionTests(unittest.TestCase):
    def test_options_are_removed_without_reordering_trainer_arguments(self):
        cleaned, options = extract_automatic_public_options(
            [
                "trainer.yaml",
                "--run-id",
                "auto-public-001",
                f"{QUARANTINE_FLAG}=D:/BeesRlTelemetry",
                WATCH_SECONDS_FLAG,
                "15",
                MAX_BATCHES_FLAG,
                "64",
                MAX_SUGGESTIONS_FLAG,
                "12",
                MIN_OCCURRENCES_FLAG,
                "3",
                MIN_CONTRIBUTORS_FLAG,
                "3",
                TARGET_FRACTION_FLAG,
                "0.08",
                "--num-envs",
                "8",
            ]
        )

        self.assertEqual(
            cleaned,
            ["trainer.yaml", "--run-id", "auto-public-001", "--num-envs", "8"],
        )
        self.assertEqual(options.quarantine_root, "D:/BeesRlTelemetry")
        self.assertEqual(options.watch_seconds, 15.0)
        self.assertEqual(options.maximum_selection_batches, 64)
        self.assertEqual(options.maximum_tactic_suggestions, 12)
        self.assertEqual(options.minimum_occurrences, 3)
        self.assertEqual(options.minimum_contributors, 3)
        self.assertAlmostEqual(options.total_target_fraction, 0.08)

    def test_quarantine_root_is_required(self):
        with self.assertRaisesRegex(SystemExit, QUARANTINE_FLAG):
            extract_automatic_public_options(["trainer.yaml", "--run-id", "missing-root"])

    def test_watch_interval_must_be_positive(self):
        with self.assertRaisesRegex(SystemExit, WATCH_SECONDS_FLAG):
            extract_automatic_public_options(
                [
                    "trainer.yaml",
                    QUARANTINE_FLAG,
                    "D:/BeesRlTelemetry",
                    WATCH_SECONDS_FLAG,
                    "0",
                ]
            )

    def test_duplicate_automatic_option_fails_closed(self):
        with self.assertRaisesRegex(SystemExit, "may be specified only once"):
            extract_automatic_public_options(
                [
                    "trainer.yaml",
                    QUARANTINE_FLAG,
                    "D:/first",
                    QUARANTINE_FLAG,
                    "D:/second",
                ]
            )


if __name__ == "__main__":
    unittest.main()
