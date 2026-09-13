"""Focused tests for automatic public live-telemetry learning orchestration."""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from bees_continual_learning import ContinualLearningStore, ValidationError
from bees_continual_public_auto import (
    _publish_state,
    load_current_state,
    process_public_learning_once,
)


def _config():
    return {
        "behavior_name": "BeesRL1v1",
        "policy_abi_version": 8,
        "observation_schema_version": 8,
        "action_schema_version": 6,
        "reward_schema_version": 2,
        "scenario_schema_version": 1,
        "promotion": {},
        "historical_league": {},
        "public_live_telemetry": {"max_batches_per_contributor": 2},
        "ingestion": {
            "max_payload_bytes": 16 * 1024 * 1024,
            "max_steps_per_match": 1000,
        },
    }


class AutomaticPublicLearningTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        self.store = ContinualLearningStore(root / "store", _config())
        self.store.initialize()
        self.quarantine = root / "quarantine"
        (self.quarantine / "incoming").mkdir(parents=True)

    def tearDown(self):
        self.temp.cleanup()

    def test_empty_quarantine_publishes_empty_offline_pressure_state(self):
        result = process_public_learning_once(self.store, self.quarantine)

        self.assertEqual(result["selected_batches"], [])
        self.assertEqual(result["scenario_ids"], [])
        self.assertEqual(result["suggestions"], 0)
        state = load_current_state(self.store)
        self.assertIsNone(state["selection_id"])
        self.assertEqual(state["source_batches"], [])
        self.assertEqual(state["scenario_ids"], [])

    def test_generation_record_is_immutable_while_current_pointer_may_be_republished(self):
        first = _publish_state(
            self.store,
            selection_id="selection-test",
            scenario_ids=("adv-" + "a" * 24,),
            source_batches=("telemetry-a", "telemetry-b"),
        )
        generation = (
            self.store.root
            / "metadata"
            / "automatic-public-learning"
            / "generations"
            / f"{first['generation_id']}.json"
        )
        original_generation = generation.read_bytes()

        second = _publish_state(
            self.store,
            selection_id="selection-test",
            scenario_ids=("adv-" + "a" * 24,),
            source_batches=("telemetry-b", "telemetry-a"),
        )

        self.assertEqual(first["generation_id"], second["generation_id"])
        self.assertEqual(generation.read_bytes(), original_generation)
        self.assertEqual(load_current_state(self.store)["generation_id"], first["generation_id"])

    def test_automatic_tactic_cap_cannot_exceed_unity_scenario_capacity(self):
        with self.assertRaisesRegex(ValidationError, "may not exceed 32"):
            process_public_learning_once(
                self.store,
                self.quarantine,
                maximum_tactic_suggestions=33,
            )

    def test_malformed_current_pointer_fails_closed(self):
        current = (
            self.store.root
            / "metadata"
            / "automatic-public-learning"
            / "current.json"
        )
        current.parent.mkdir(parents=True, exist_ok=True)
        current.write_text(json.dumps({"schema_version": 999}), encoding="utf-8")

        with self.assertRaisesRegex(ValidationError, "incompatible"):
            load_current_state(self.store)


if __name__ == "__main__":
    unittest.main()
