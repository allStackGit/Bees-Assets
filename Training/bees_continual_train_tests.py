"""Focused tests for Training/bees_continual_train.py."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import unittest


MODULE_PATH = Path(__file__).with_name("bees_continual_train.py")
SPEC = importlib.util.spec_from_file_location("bees_continual_train", MODULE_PATH)
wrapper = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = wrapper
assert SPEC.loader is not None
SPEC.loader.exec_module(wrapper)


class ContinualOptionTests(unittest.TestCase):
    def test_continual_flags_are_removed_from_mlagents_arguments(self):
        trainer_args, options = wrapper.extract_continual_options(
            [
                "Training/rl_1v1_config.yaml",
                "--run-id=bees-full-001",
                "--continual-root=F:/continual",
                "--continual-game-build",
                "build-7",
                "--bees-batch-inference",
            ]
        )
        self.assertEqual(
            trainer_args,
            [
                "Training/rl_1v1_config.yaml",
                "--run-id=bees-full-001",
                "--bees-batch-inference",
            ],
        )
        self.assertTrue(options.enabled)
        self.assertEqual(options.root, "F:/continual")
        self.assertEqual(options.game_build, "build-7")

    def test_root_requires_game_build_identity(self):
        with self.assertRaises(SystemExit):
            wrapper.extract_continual_options(["--continual-root=F:/continual"])

    def test_continual_metadata_without_root_is_rejected(self):
        with self.assertRaises(SystemExit):
            wrapper.extract_continual_options(["--continual-game-build=build-7"])

    def test_unknown_continual_flag_is_rejected(self):
        with self.assertRaises(SystemExit):
            wrapper.extract_continual_options(["--continual-magic=true"])


class RunContextTests(unittest.TestCase):
    def test_infers_run_result_directory_and_yaml(self):
        run_dir, run_id, config = wrapper.infer_run_context(
            [
                "Training/rl_1v1_config.yaml",
                "--run-id",
                "run-42",
                "--results-dir=F:/results",
                "--resume",
            ]
        )
        self.assertEqual(run_id, "run-42")
        self.assertEqual(run_dir, Path("F:/results") / "run-42")
        self.assertEqual(config, Path("Training/rl_1v1_config.yaml"))

    def test_run_id_is_required_for_candidate_identity(self):
        with self.assertRaises(SystemExit):
            wrapper.infer_run_context(["Training/rl_1v1_config.yaml"])


class StepInferenceTests(unittest.TestCase):
    def test_extracts_step_from_common_checkpoint_names(self):
        self.assertEqual(
            wrapper.infer_training_step(Path("results/run/BeesRL1v1-8249716.onnx")),
            8249716,
        )
        self.assertEqual(
            wrapper.infer_training_step(Path("results/run/step_250000/BeesRL1v1.onnx")),
            250000,
        )

    def test_unversioned_latest_model_is_not_registered_as_immutable_candidate(self):
        self.assertIsNone(
            wrapper.infer_training_step(Path("results/run/BeesRL1v1.onnx"))
        )


if __name__ == "__main__":
    unittest.main()
