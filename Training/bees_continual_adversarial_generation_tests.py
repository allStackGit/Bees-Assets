"""Focused regression tests for continuous-training pressure generations."""

from __future__ import annotations

from pathlib import Path
import tempfile
import unittest

from bees_continual_learning import ContinualLearningStore, ValidationError, load_config
from bees_continual_adversarial_train import record_run_selection


TRAINING_DIR = Path(__file__).parent


class AdversarialTrainingGenerationTests(unittest.TestCase):
    def test_same_optimizer_run_can_freeze_different_pressure_in_distinct_generations(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            store = ContinualLearningStore(
                Path(temp_dir) / "store",
                load_config(TRAINING_DIR / "continual_learning_config.json"),
            )
            store.initialize()

            first = record_run_selection(
                store,
                run_id="continuous-lineage",
                generation_id="generation-000001",
                scenario_ids=["adv-aaaaaaaaaaaaaaaaaaaaaaaa"],
                encoded="adv-aaaaaaaaaaaaaaaaaaaaaaaa:Wasp>Gunship@0.1",
            )
            second = record_run_selection(
                store,
                run_id="continuous-lineage",
                generation_id="generation-000002",
                scenario_ids=["adv-bbbbbbbbbbbbbbbbbbbbbbbb"],
                encoded="adv-bbbbbbbbbbbbbbbbbbbbbbbb:Hornet>Frigate@0.1",
            )

            self.assertNotEqual(first, second)
            self.assertTrue(first.is_file())
            self.assertTrue(second.is_file())

    def test_same_generation_remains_immutable(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            store = ContinualLearningStore(
                Path(temp_dir) / "store",
                load_config(TRAINING_DIR / "continual_learning_config.json"),
            )
            store.initialize()
            kwargs = {
                "run_id": "continuous-lineage",
                "generation_id": "generation-000003",
                "scenario_ids": ["adv-aaaaaaaaaaaaaaaaaaaaaaaa"],
                "encoded": "adv-aaaaaaaaaaaaaaaaaaaaaaaa:Wasp>Gunship@0.1",
            }

            first = record_run_selection(store, **kwargs)
            self.assertEqual(first, record_run_selection(store, **kwargs))
            with self.assertRaises(ValidationError):
                record_run_selection(
                    store,
                    run_id="continuous-lineage",
                    generation_id="generation-000003",
                    scenario_ids=["adv-bbbbbbbbbbbbbbbbbbbbbbbb"],
                    encoded="adv-bbbbbbbbbbbbbbbbbbbbbbbb:Hornet>Frigate@0.1",
                )

    def test_legacy_one_selection_per_run_contract_is_unchanged(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            store = ContinualLearningStore(
                Path(temp_dir) / "store",
                load_config(TRAINING_DIR / "continual_learning_config.json"),
            )
            store.initialize()
            record_run_selection(
                store,
                run_id="legacy-run",
                scenario_ids=["adv-aaaaaaaaaaaaaaaaaaaaaaaa"],
                encoded="adv-aaaaaaaaaaaaaaaaaaaaaaaa:Wasp>Gunship@0.1",
            )
            with self.assertRaises(ValidationError):
                record_run_selection(
                    store,
                    run_id="legacy-run",
                    scenario_ids=["adv-bbbbbbbbbbbbbbbbbbbbbbbb"],
                    encoded="adv-bbbbbbbbbbbbbbbbbbbbbbbb:Hornet>Frigate@0.1",
                )


if __name__ == "__main__":
    unittest.main()
