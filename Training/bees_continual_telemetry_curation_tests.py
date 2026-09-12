from __future__ import annotations

import json
import sqlite3
import tempfile
import unittest
from pathlib import Path

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
        "ingestion": {"max_payload_bytes": 1024 * 1024, "max_steps_per_match": 100},
    }


class PublicTelemetryCurationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = ContinualLearningStore(self.temp.name, _config())
        self.store.initialize()

    def tearDown(self):
        self.temp.cleanup()

    def _seed_public_batch(self, match_id="match-1", model_id="model-1"):
        payload = {
            "match_id": match_id,
            "model_id": model_id,
            "mode": "campaign",
            "result": "bee_win",
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
                (batch_id, match_id, model_id, "2026-09-12T00:00:00Z", payload_hash, str(archive)),
            )
            db.commit()
        finally:
            db.close()

        provenance = (
            self.store.experience_dir
            / "raw-live"
            / "public-quarantine-provenance"
            / batch_id
            / "server-batch.json"
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
        return batch_id, archive

    def test_approval_and_selection_remain_off_policy(self):
        batch_id, _ = self._seed_public_batch()
        approval = approve_public_telemetry(
            self.store,
            batch_id,
            reviewer="reviewer",
            reason="interesting long-range behavior",
            tags=["kiting", "long-range"],
        )
        self.assertTrue(approval["approved_for_scenario_mining"])
        self.assertFalse(approval["approved_for_on_policy_rl"])

        selection = materialize_scenario_selection(self.store, [batch_id])
        self.assertEqual(selection["purpose"], "offline-scenario-mining")
        self.assertFalse(selection["approved_for_on_policy_rl"])
        self.assertEqual(selection["batches"][0]["batch_id"], batch_id)

    def test_missing_public_provenance_fails_closed(self):
        batch_id, _ = self._seed_public_batch()
        provenance_dir = (
            self.store.experience_dir
            / "raw-live"
            / "public-quarantine-provenance"
            / batch_id
        )
        for path in provenance_dir.glob("*.json"):
            path.unlink()
        with self.assertRaises(ValidationError):
            approve_public_telemetry(
                self.store,
                batch_id,
                reviewer="reviewer",
                reason="should fail",
            )

    def test_archive_tampering_fails_closed(self):
        batch_id, archive = self._seed_public_batch()
        archive.write_text(json.dumps({"match_id": "changed", "model_id": "model-1"}), encoding="utf-8")
        with self.assertRaises(ValidationError):
            approve_public_telemetry(
                self.store,
                batch_id,
                reviewer="reviewer",
                reason="should fail",
            )

    def test_revocation_blocks_future_selection(self):
        batch_id, _ = self._seed_public_batch()
        approve_public_telemetry(
            self.store,
            batch_id,
            reviewer="reviewer",
            reason="initial approval",
        )
        revoke_public_telemetry(
            self.store,
            batch_id,
            reviewer="reviewer-2",
            reason="later found unusable",
        )
        with self.assertRaises(ValidationError):
            materialize_scenario_selection(self.store, [batch_id])


if __name__ == "__main__":
    unittest.main()
