"""Focused tests for Training/bees_continual_train.py."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import tempfile
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


class HistoricalTrainingSettingsTests(unittest.TestCase):
    def test_settings_use_configured_ratio_provider_and_mlagents_seed(self):
        ratio, provider, seed = wrapper.historical_training_settings(
            {
                "historical_league": {
                    "training_ratio": 0.2,
                    "training_onnx_provider": "CPUExecutionProvider",
                }
            },
            ["Training/rl_1v1_config.yaml", "--seed=36"],
        )
        self.assertEqual(ratio, 0.2)
        self.assertEqual(provider, "CPUExecutionProvider")
        self.assertEqual(seed, 36)

    def test_missing_historical_training_settings_disable_bridge(self):
        ratio, provider, seed = wrapper.historical_training_settings(
            {"historical_league": {}},
            ["Training/rl_1v1_config.yaml"],
        )
        self.assertEqual(ratio, 0.0)
        self.assertIsNone(provider)
        self.assertEqual(seed, 0)

    def test_invalid_historical_training_settings_are_rejected(self):
        cases = (
            {"historical_league": {"training_ratio": -0.1}},
            {"historical_league": {"training_ratio": 1.1}},
            {"historical_league": {"training_ratio": float("nan")}},
            {"historical_league": {"training_ratio": True}},
            {"historical_league": {"training_onnx_provider": ""}},
        )
        for config in cases:
            with self.subTest(config=config):
                with self.assertRaises(SystemExit):
                    wrapper.historical_training_settings(config, [])


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


class _FakeStore:
    def __init__(self):
        self.registrations = []

    def register_model(self, path, **kwargs):
        self.registrations.append((Path(path), kwargs))
        return {"model_id": f"model-{kwargs['training_step']}"}


class CandidateMonitorTests(unittest.TestCase):
    def make_monitor(self, root: Path, store: _FakeStore) -> wrapper.CandidateMonitor:
        return wrapper.CandidateMonitor(
            store,
            results_run_dir=root,
            run_id="resume-run",
            game_build="build-7",
            training_config=Path("Training/rl_1v1_config.yaml"),
            parent_model_id="champion-1",
            interval_seconds=1.0,
        )

    def test_preexisting_resume_checkpoints_are_not_backfilled(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            old_checkpoint = root / "BeesRL1v1-10000.onnx"
            old_checkpoint.write_bytes(b"old")
            store = _FakeStore()
            monitor = self.make_monitor(root, store)

            monitor.prime_existing()
            monitor.scan_once()
            monitor.scan_once()

            self.assertEqual(store.registrations, [])

    def test_new_checkpoint_after_baseline_is_registered_when_stable(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            old_checkpoint = root / "BeesRL1v1-10000.onnx"
            old_checkpoint.write_bytes(b"old")
            store = _FakeStore()
            monitor = self.make_monitor(root, store)
            monitor.prime_existing()

            new_checkpoint = root / "BeesRL1v1-20000.onnx"
            new_checkpoint.write_bytes(b"new")
            monitor.scan_once()
            self.assertEqual(store.registrations, [])
            monitor.scan_once()

            self.assertEqual(len(store.registrations), 1)
            registered_path, metadata = store.registrations[0]
            self.assertEqual(registered_path, new_checkpoint)
            self.assertEqual(metadata["training_step"], 20000)
            self.assertEqual(metadata["parent_model_id"], "champion-1")

    def test_changed_preexisting_checkpoint_becomes_eligible_again(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            checkpoint = root / "BeesRL1v1-30000.onnx"
            checkpoint.write_bytes(b"old")
            store = _FakeStore()
            monitor = self.make_monitor(root, store)
            monitor.prime_existing()

            checkpoint.write_bytes(b"replacement-checkpoint")
            monitor.scan_once()
            monitor.scan_once()

            self.assertEqual(len(store.registrations), 1)
            self.assertEqual(store.registrations[0][1]["training_step"], 30000)


if __name__ == "__main__":
    unittest.main()
