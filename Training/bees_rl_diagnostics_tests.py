"""Focused tests for Training/bees_rl_diagnostics.py.

Run from the Bees Assets root inside the ML-Agents virtual environment:

    python Training\bees_rl_diagnostics_tests.py
"""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import unittest

import numpy as np


TRAINING_DIR = Path(__file__).parent
if str(TRAINING_DIR) not in sys.path:
    sys.path.insert(0, str(TRAINING_DIR))

DIAGNOSTICS_PATH = TRAINING_DIR / "bees_rl_diagnostics.py"
SPEC = importlib.util.spec_from_file_location("bees_rl_diagnostics", DIAGNOSTICS_PATH)
diagnostics = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(diagnostics)


class DiagnosticOptionParsingTests(unittest.TestCase):
    def test_diagnostic_flags_are_removed_before_launcher_parsing(self):
        trainer_args, fresh_optimizer, every = diagnostics._extract_diagnostic_options(
            [
                "Training/rl_1v1_config.yaml",
                "--resume",
                "--bees-fresh-optimizer-state",
                "--bees-diag-every=7",
                "--bees-batch-inference",
            ]
        )

        self.assertEqual(
            trainer_args,
            [
                "Training/rl_1v1_config.yaml",
                "--resume",
                "--bees-batch-inference",
            ],
        )
        self.assertTrue(fresh_optimizer)
        self.assertEqual(every, 7)

    def test_diagnostic_every_accepts_separate_value(self):
        trainer_args, fresh_optimizer, every = diagnostics._extract_diagnostic_options(
            [
                "Training/rl_1v1_config.yaml",
                "--bees-diag-every",
                "3",
            ]
        )

        self.assertEqual(trainer_args, ["Training/rl_1v1_config.yaml"])
        self.assertFalse(fresh_optimizer)
        self.assertEqual(every, 3)

    def test_diagnostic_every_rejects_non_positive_value(self):
        with self.assertRaises(SystemExit):
            diagnostics._extract_diagnostic_options(
                ["Training/rl_1v1_config.yaml", "--bees-diag-every=0"]
            )


class SamplingTests(unittest.TestCase):
    def test_first_samples_are_always_logged_then_periodic_sampling_takes_over(self):
        every = 20
        for index in range(1, diagnostics.INITIAL_DIAGNOSTIC_SAMPLES + 1):
            self.assertTrue(diagnostics._should_sample(index, every))

        self.assertFalse(
            diagnostics._should_sample(
                diagnostics.INITIAL_DIAGNOSTIC_SAMPLES + 1,
                every,
            )
        )
        self.assertTrue(diagnostics._should_sample(20, every))
        self.assertTrue(diagnostics._should_sample(40, every))


class ArrayStatsTests(unittest.TestCase):
    def test_array_stats_reports_finite_and_nonfinite_values(self):
        summary = diagnostics._array_stats(
            np.asarray([1.0, 2.0, np.nan, np.inf], dtype=np.float32)
        )

        self.assertIn("n=4", summary)
        self.assertIn("finite=2", summary)
        self.assertIn("nonfinite=2", summary)
        self.assertIn("min=1", summary)
        self.assertIn("max=2", summary)


if __name__ == "__main__":
    unittest.main()
