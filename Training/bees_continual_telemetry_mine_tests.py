"""Focused tests for review-only tactical mining from curated public live telemetry."""

from __future__ import annotations

import sqlite3
import tempfile
import unittest
from typing import Mapping

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
from bees_continual_telemetry_mine import (
    MIN_TACTIC_RECORDS,
    mine_telemetry_selection,
)


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


def _step(agent_key, decision_index, *, self_ship=21, enemy_ship=13):
    continuous = [0.0] * 34
    continuous[0] = 0.5
    discrete = [0] * 20
    discrete[0] = 1
    return {
        "agent_key": agent_key,
        "decision_index": decision_index,
        "observation": _observation(self_ship, enemy_ship),
        "continuous_action": continuous,
        "discrete_action": discrete,
    }


def _assert_no_raw_step_keys(testcase, value):
    forbidden = {"observation", "continuous_action", "discrete_action"}
    if isinstance(value, Mapping):
        testcase.assertTrue(forbidden.isdisjoint(value.keys()))
        for nested in value.values():
            _assert_no_raw_step_keys(testcase, nested)
    elif isinstance(value, (list, tuple)):
        for nested in value:
            _assert_no_raw_step_keys(testcase, nested)


class PublicTelemetryMiningTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = ContinualLearningStore(self.temp.name, _config())
        self.store.initialize()
        self.counter = 0

    def tearDown(self):
        self.temp.cleanup()

    def _seed_public_batch(
        self,
        *,
        record_count=MIN_TACTIC_RECORDS,
        self_ship=21,
        enemy_ship=13,
        agent_count=1,
    ):
        self.counter += 1
        match_id = f"match-{self.counter}"
        model_id = "model-1"
        steps = [
            _step(
                f"opaque-agent-{agent_index}",
                index,
                self_ship=self_ship,
                enemy_ship=enemy_ship,
            )
            for agent_index in range(agent_count)
            for index in range(record_count)
        ]
        payload = {
            "match_id": match_id,
            "model_id": model_id,
            "mode": "campaign",
            "result": "bee_win",
            "steps": steps,
        }
        payload_hash = sha256_bytes(canonical_json(payload).encode("utf-8"))
        batch_id = f"telemetry-{payload_hash[:24]}"
        archive = self.store.experience_dir / "raw-live" / f"{batch_id}.json"
        self.store._write_json_immutable(archive, payload)

        db = sqlite3.connect(str(self.store.db_path))
        try:
            db.execute("PRAGMA foreign_keys=OFF")
            db.execute(
                """
                INSERT INTO telemetry_batches(
                    batch_id, match_id, model_id, created_at, payload_sha256,
                    archive_path, trusted_for_on_policy_rl
                ) VALUES(?,?,?,?,?,?,0)
                """,
                (
                    batch_id,
                    match_id,
                    model_id,
                    "2026-09-12T00:00:00Z",
                    payload_hash,
                    str(archive),
                ),
            )
            db.commit()
        finally:
            db.close()

        provenance = (
            self.store.experience_dir
            / "raw-live"
            / "public-quarantine-provenance"
            / batch_id
            / f"server-{self.counter}.json"
        )
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
            reviewer="test-reviewer",
            reason="Repeated tactical behavior worth offline review.",
            tags=["candidate-tactic"],
        )
        return batch_id

    def _selection(self, batch_ids):
        return materialize_scenario_selection(self.store, batch_ids)["selection_id"]

    def test_repeated_signature_produces_review_only_suggestion_without_raw_trajectory(self):
        first = self._seed_public_batch()
        second = self._seed_public_batch()
        report = mine_telemetry_selection(
            self.store,
            self._selection([first, second]),
            minimum_occurrences=2,
        )

        self.assertFalse(report["authoritative"])
        self.assertFalse(report["approved_for_on_policy_rl"])
        self.assertFalse(report["recorded_actions_used_as_ppo_trajectories"])
        self.assertTrue(report["requires_operator_review"])
        self.assertTrue(report["requires_side_mapping"])
        self.assertEqual(report["analyzed_batches"], 2)
        self.assertEqual(report["analyzed_agent_streams"], 2)
        self.assertEqual(len(report["suggestions"]), 1)
        suggestion = report["suggestions"][0]
        self.assertEqual(suggestion["occurrence_count"], 2)
        self.assertEqual(suggestion["agent_stream_count"], 2)
        self.assertEqual(suggestion["self_ship_name"], "Wasp")
        self.assertEqual(suggestion["first_enemy_ship_name"], "Gunship")
        self.assertTrue(suggestion["geometry_candidates"])
        self.assertEqual(
            set(suggestion["sources"][0]),
            {"batch_id", "agent_stream_index"},
        )
        _assert_no_raw_step_keys(self, report)

    def test_single_occurrence_is_not_suggested_at_default_repeat_threshold(self):
        batch_id = self._seed_public_batch()
        report = mine_telemetry_selection(self.store, self._selection([batch_id]))
        self.assertEqual(report["analyzed_agent_streams"], 1)
        self.assertEqual(report["suggestions"], [])

    def test_multiple_agents_in_one_match_do_not_self_confirm_repetition(self):
        batch_id = self._seed_public_batch(agent_count=2)
        report = mine_telemetry_selection(
            self.store,
            self._selection([batch_id]),
            minimum_occurrences=2,
        )
        self.assertEqual(report["analyzed_agent_streams"], 2)
        self.assertEqual(report["suggestions"], [])

    def test_revocation_after_selection_fails_closed(self):
        first = self._seed_public_batch()
        second = self._seed_public_batch()
        selection_id = self._selection([first, second])
        revoke_public_telemetry(
            self.store,
            first,
            reviewer="second-reviewer",
            reason="Later review found the telemetry unusable.",
        )
        with self.assertRaises(ValidationError):
            mine_telemetry_selection(self.store, selection_id)

    def test_short_stream_is_skipped_not_promoted_to_tactic(self):
        batch_id = self._seed_public_batch(record_count=MIN_TACTIC_RECORDS - 1)
        report = mine_telemetry_selection(
            self.store,
            self._selection([batch_id]),
            minimum_occurrences=1,
        )
        self.assertEqual(report["analyzed_agent_streams"], 0)
        self.assertEqual(report["skipped_short_agent_streams"], 1)
        self.assertEqual(report["suggestions"], [])

    def test_distinct_signatures_are_ordered_deterministically(self):
        a1 = self._seed_public_batch(self_ship=21, enemy_ship=13)
        a2 = self._seed_public_batch(self_ship=21, enemy_ship=13)
        b1 = self._seed_public_batch(self_ship=15, enemy_ship=12)
        b2 = self._seed_public_batch(self_ship=15, enemy_ship=12)
        report = mine_telemetry_selection(
            self.store,
            self._selection([b2, a2, b1, a1]),
            minimum_occurrences=2,
        )
        signatures = [item["signature"] for item in report["suggestions"]]
        self.assertEqual(signatures, sorted(signatures))


if __name__ == "__main__":
    unittest.main()
