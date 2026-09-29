"""Regression tests for immutable model registration lineage.

Run from the Bees Assets root:
    python Training\bees_continual_registration_lineage_tests.py
"""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest


MODULE_PATH = Path(__file__).with_name("bees_continual_learning.py")
SPEC = importlib.util.spec_from_file_location("bees_continual_learning", MODULE_PATH)
continual = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = continual
assert SPEC.loader is not None
SPEC.loader.exec_module(continual)


TEST_CONFIG = {
    "behavior_name": "BeesRL1v1",
    "policy_abi_version": 6,
    "observation_schema_version": 6,
    "action_schema_version": 6,
    "reward_schema_version": 1,
    "scenario_schema_version": 1,
    "promotion": {
        "min_matches_vs_champion": 10,
        "min_win_rate_vs_champion": 0.52,
        "max_critical_regressions": 0,
        "max_historical_regression": 0.15,
        "min_historical_matches_per_opponent": 5,
        "min_competency_cases": 1,
    },
    "historical_league": {
        "base_weight": 1.0,
        "weakness_trigger_regression": 0.05,
        "weakness_bonus_scale": 10.0,
        "max_weight_multiplier": 4.0,
    },
    "ingestion": {
        "max_payload_bytes": 1024 * 1024,
        "max_steps_per_match": 100,
    },
}


class RegistrationLineageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "store"
        self.artifacts = Path(self.temp.name) / "artifacts"
        self.artifacts.mkdir()
        self.store = continual.ContinualLearningStore(self.root, TEST_CONFIG)
        self.store.initialize()

        self.parent_a = self._register_unique("parent-a.onnx", b"parent-a", 10)
        self.parent_b = self._register_unique("parent-b.onnx", b"parent-b", 20)
        self.config_a = self.artifacts / "config-a.yaml"
        self.config_b = self.artifacts / "config-b.yaml"
        self.config_a.write_text("behaviors: {a: 1}\n", encoding="utf-8")
        self.config_b.write_text("behaviors: {a: 2}\n", encoding="utf-8")

    def tearDown(self):
        self.temp.cleanup()

    def _artifact(self, name: str, content: bytes) -> Path:
        path = self.artifacts / name
        path.write_bytes(content)
        return path

    def _register_unique(self, name: str, content: bytes, step: int):
        return self.store.register_model(
            self._artifact(name, content),
            training_run_id="lineage-test",
            training_step=step,
            game_build_version="test-build",
            status="candidate",
        )

    def _register_candidate(
        self,
        name: str,
        *,
        parent_model_id: str,
        training_config_path: Path,
        source_checkpoint: str,
    ):
        return self.store.register_model(
            self._artifact(name, b"same-candidate-bytes"),
            training_run_id="lineage-test",
            training_step=100,
            game_build_version="test-build",
            parent_model_id=parent_model_id,
            training_config_path=training_config_path,
            source_checkpoint=source_checkpoint,
            status="candidate",
        )

    def test_same_bytes_with_same_lineage_remain_idempotent(self):
        first = self._register_candidate(
            "candidate-a.onnx",
            parent_model_id=self.parent_a["model_id"],
            training_config_path=self.config_a,
            source_checkpoint="checkpoint-a",
        )
        second = self._register_candidate(
            "candidate-copy.onnx",
            parent_model_id=self.parent_a["model_id"],
            training_config_path=self.config_a,
            source_checkpoint="checkpoint-a",
        )

        self.assertEqual(first["model_id"], second["model_id"])

    def test_same_bytes_with_different_parent_are_rejected(self):
        self._register_candidate(
            "candidate-a.onnx",
            parent_model_id=self.parent_a["model_id"],
            training_config_path=self.config_a,
            source_checkpoint="checkpoint-a",
        )

        with self.assertRaisesRegex(continual.ValidationError, "parent_model_id"):
            self._register_candidate(
                "candidate-b.onnx",
                parent_model_id=self.parent_b["model_id"],
                training_config_path=self.config_a,
                source_checkpoint="checkpoint-a",
            )

    def test_same_bytes_with_different_training_config_are_rejected(self):
        self._register_candidate(
            "candidate-a.onnx",
            parent_model_id=self.parent_a["model_id"],
            training_config_path=self.config_a,
            source_checkpoint="checkpoint-a",
        )

        with self.assertRaisesRegex(continual.ValidationError, "training_config_hash"):
            self._register_candidate(
                "candidate-b.onnx",
                parent_model_id=self.parent_a["model_id"],
                training_config_path=self.config_b,
                source_checkpoint="checkpoint-a",
            )

    def test_same_bytes_with_different_source_checkpoint_are_rejected(self):
        self._register_candidate(
            "candidate-a.onnx",
            parent_model_id=self.parent_a["model_id"],
            training_config_path=self.config_a,
            source_checkpoint="checkpoint-a",
        )

        with self.assertRaisesRegex(continual.ValidationError, "source_checkpoint"):
            self._register_candidate(
                "candidate-b.onnx",
                parent_model_id=self.parent_a["model_id"],
                training_config_path=self.config_a,
                source_checkpoint="checkpoint-b",
            )


if __name__ == "__main__":
    unittest.main()
