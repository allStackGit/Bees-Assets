"""Focused tests for operator-reviewed live-telemetry tactic registration."""

from __future__ import annotations

import sqlite3
import tempfile
import unittest

from bees_continual_learning import (
    ContinualLearningStore,
    ValidationError,
    canonical_json,
    sha256_bytes,
)
from bees_continual_telemetry_curation import (
    approve_public_telemetry,
    materialize_scenario_selection,
    revoke_public_telemetry,
)
from bees_continual_telemetry_mine import MIN_TACTIC_RECORDS, mine_telemetry_selection
from bees_continual_telemetry_review import review_and_register_telemetry_tactic


OBSERVATION_SIZE = 4722
FIRST_ENEMY_SLOT_INDEX = 29 + 12 + 19 + 64 * 19
FIRST_ENEMY_X_INDEX = FIRST_ENEMY_SLOT_INDEX + 1
ENEMY_SHIP_BIT_START = FIRST_ENEMY_SLOT_INDEX + 13


def _config():
    return {
        "behavior_name": "BeesRL1v1",
        "policy_abi_version": 8,
        "observation_schema_version": 8,
        "action_schema_version": 8,
        "reward_schema_version": 8,
        "scenario_schema_version": 1,
        "promotion": {},
        "historical_league": {},
        "ingestion": {"max_payload_bytes": 16 * 1024 * 1024, "max_steps_per_match": 1000},
    }


def _enum_bits(values, start, number):
    for bit in range(6):
        values[start + bit] = 1.0 if number & (1 << bit) else 0.0


def _positive_normalized(value, scale):
    return value / (scale + value)


def _signed_distance(value):
    magnitude = abs(value)
    encoded = magnitude / (40.0 + magnitude)
    return -encoded if value < 0 else encoded


def _observation(self_ship=21, enemy_ship=13):
    values = [0.0] * OBSERVATION_SIZE
    _enum_bits(values, 0, self_ship)
    values[6] = 0.4
    values[7] = 0.0
    values[8] = _positive_normalized(40.0, 100.0)
    values[9] = _positive_normalized(40.0, 100.0)
    values[18] = _positive_normalized(30.0, 80.0)
    values[FIRST_ENEMY_SLOT_INDEX] = 1.0
    values[FIRST_ENEMY_X_INDEX] = _signed_distance(20.0)
    _enum_bits(values, ENEMY_SHIP_BIT_START, enemy_ship)
    return values


def _step(decision_index):
    continuous = [0.0] * 34
    continuous[0] = 0.5
    discrete = [0] * 20
    discrete[0] = 1
    return {
        "agent_key": "opaque-agent",
        "decision_index": decision_index,
        "observation": _observation(),
        "continuous_action": continuous,
        "discrete_action": discrete,
    }


class TelemetryTacticReviewTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = ContinualLearningStore(self.temp.name, _config())
        self.store.initialize()
        self.counter = 0

    def tearDown(self):
        self.temp.cleanup()

    def _batch(self):
        self.counter += 1
        payload = {
            "match_id": f"match-{self.counter}",
            "model_id": "model-1",
            "mode": "campaign",
            "result": "bee_win",
            "steps": [_step(index) for index in range(MIN_TACTIC_RECORDS)],
        }
        payload_hash = sha256_bytes(canonical_json(payload).encode("utf-8"))
        batch_id = f"telemetry-{payload_hash[:24]}"
        archive = self.store.experience_dir / "raw-live" / f"{batch_id}.json"
        self.store._write_json_immutable(archive, payload)
        db = sqlite3.connect(str(self.store.db_path))
        try:
            db.execute("PRAGMA foreign_keys=OFF")
            db.execute(
                "INSERT INTO telemetry_batches(batch_id,match_id,model_id,created_at,payload_sha256,archive_path,trusted_for_on_policy_rl) VALUES(?,?,?,?,?,?,0)",
                (batch_id, payload["match_id"], "model-1", "2026-09-12T00:00:00Z", payload_hash, str(archive)),
            )
            db.commit()
        finally:
            db.close()
        provenance = self.store.experience_dir / "raw-live" / "public-quarantine-provenance" / batch_id / f"server-{self.counter}.json"
        self.store._write_json_immutable(
            provenance,
            {
                "central_batch_id": batch_id,
                "source_trust": "authenticated-quarantine",
                "strict_live_schema_validated": True,
                "trusted_for_on_policy_rl": False,
            },
        )
        approve_public_telemetry(
            self.store,
            batch_id,
            reviewer="reviewer",
            reason="candidate repeated tactic",
        )
        return batch_id

    def _selection_and_signature(self):
        first = self._batch()
        second = self._batch()
        selection = materialize_scenario_selection(self.store, [first, second])
        report = mine_telemetry_selection(self.store, selection["selection_id"])
        self.assertEqual(len(report["suggestions"]), 1)
        return first, selection["selection_id"], report["suggestions"][0]["signature"]

    def test_explicit_side_orientation_registers_expected_pressure(self):
        _, selection_id, signature = self._selection_and_signature()
        result = review_and_register_telemetry_tactic(
            self.store,
            selection_id,
            signature,
            self_side="bee",
            target_fraction=0.1,
            rationale="Train fresh counterplay against reviewed tactic.",
            geometry_candidate_index=0,
        )
        self.assertEqual(result["bee_composition"], ["Wasp"])
        self.assertEqual(result["human_composition"], ["Gunship"])
        self.assertTrue(result["fresh_on_policy_rollouts_required"])
        self.assertFalse(result["recorded_actions_used_as_ppo_trajectories"])
        identity = result["registration"]["scenario"]["identity"]
        self.assertEqual(identity["bee_composition"], ["Wasp"])
        self.assertEqual(identity["human_composition"], ["Gunship"])
        self.assertIn("geometry", identity)

    def test_human_orientation_reverses_perspective(self):
        _, selection_id, signature = self._selection_and_signature()
        result = review_and_register_telemetry_tactic(
            self.store,
            selection_id,
            signature,
            self_side="human",
            target_fraction=0.1,
            rationale="Operator confirmed the recorded perspective was Human.",
        )
        self.assertEqual(result["bee_composition"], ["Gunship"])
        self.assertEqual(result["human_composition"], ["Wasp"])

    def test_unknown_signature_or_invalid_side_fails_closed(self):
        _, selection_id, signature = self._selection_and_signature()
        with self.assertRaises(ValidationError):
            review_and_register_telemetry_tactic(
                self.store,
                selection_id,
                "not-a-current-suggestion",
                self_side="bee",
                target_fraction=0.1,
                rationale="should fail",
            )
        with self.assertRaises(ValidationError):
            review_and_register_telemetry_tactic(
                self.store,
                selection_id,
                signature,
                self_side="unknown",
                target_fraction=0.1,
                rationale="should fail",
            )

    def test_invalid_geometry_candidate_fails_closed(self):
        _, selection_id, signature = self._selection_and_signature()
        with self.assertRaises(ValidationError):
            review_and_register_telemetry_tactic(
                self.store,
                selection_id,
                signature,
                self_side="bee",
                target_fraction=0.1,
                rationale="should fail",
                geometry_candidate_index=99,
            )

    def test_revocation_after_review_source_selection_blocks_registration(self):
        first, selection_id, signature = self._selection_and_signature()
        revoke_public_telemetry(
            self.store,
            first,
            reviewer="reviewer-2",
            reason="source later invalidated",
        )
        with self.assertRaises(ValidationError):
            review_and_register_telemetry_tactic(
                self.store,
                selection_id,
                signature,
                self_side="bee",
                target_fraction=0.1,
                rationale="should fail",
            )


if __name__ == "__main__":
    unittest.main()
