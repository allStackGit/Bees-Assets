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
from bees_continual_telemetry_adversarial import (
    encode_telemetry_pressure_for_unity,
    register_telemetry_pressure,
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
        "public_live_telemetry": {"max_batches_per_contributor": 8},
        "ingestion": {"max_payload_bytes": 1024 * 1024, "max_steps_per_match": 100},
    }


class TelemetryAdversarialPressureTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store = ContinualLearningStore(self.temp.name, _config())
        self.store.initialize()
        self.counter = 0

    def tearDown(self):
        self.temp.cleanup()

    def _seed_public_batch(self):
        self.counter += 1
        match_id = f"match-{self.counter}"
        model_id = "model-1"
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

        server_batch_id = f"rl-telemetry-{self.counter:032x}"
        provenance = (
            self.store.experience_dir
            / "raw-live"
            / "public-quarantine-provenance"
            / batch_id
            / f"{server_batch_id}.json"
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
        contributor = (
            self.store.experience_dir
            / "raw-live"
            / "public-contributors"
            / batch_id
            / f"{server_batch_id}.json"
        )
        self.store._write_json_immutable(
            contributor,
            {
                "schema_version": 1,
                "central_batch_id": batch_id,
                "server_batch_id": server_batch_id,
                "contributor_bucket": f"{self.counter:064x}",
            },
        )
        approve_public_telemetry(
            self.store,
            batch_id,
            reviewer="reviewer",
            reason="repeatable live tactic",
            tags=["reviewed"],
        )
        return batch_id

    def _selection(self, batch_ids):
        return materialize_scenario_selection(self.store, batch_ids)["selection_id"]

    def _register(self, selection_id, fraction=0.1):
        return register_telemetry_pressure(
            self.store,
            selection_id,
            bee_composition="Wasp",
            human_composition="Gunship",
            target_fraction=fraction,
            rationale="Recreate reviewed live matchup with fresh current-policy rollouts.",
            map_size=48.0,
            spawn_separation_ratio=0.5,
        )

    def test_curated_selection_registers_and_encodes_fresh_pressure(self):
        batch_id = self._seed_public_batch()
        scenario = self._register(self._selection([batch_id]))

        identity = scenario["scenario"]["identity"]
        self.assertTrue(identity["fresh_on_policy_rollouts_required"])
        self.assertFalse(identity["recorded_actions_used_as_ppo_trajectories"])
        self.assertEqual(identity["source"], "approved-public-live-telemetry-selection")

        matchups, geometry = encode_telemetry_pressure_for_unity(
            self.store, [scenario["scenario_id"]]
        )
        self.assertIn("Wasp>Gunship@0.1", matchups)
        self.assertIn(":48,0.5", geometry)

    def test_same_reviewed_pressure_is_content_addressed_and_idempotent(self):
        batch_id = self._seed_public_batch()
        selection_id = self._selection([batch_id])
        first = self._register(selection_id)
        second = self._register(selection_id)
        self.assertFalse(first["duplicate"])
        self.assertTrue(second["duplicate"])
        self.assertEqual(first["scenario_id"], second["scenario_id"])

    def test_revoking_underlying_telemetry_disables_existing_pressure(self):
        batch_id = self._seed_public_batch()
        scenario = self._register(self._selection([batch_id]))
        revoke_public_telemetry(
            self.store,
            batch_id,
            reviewer="reviewer-2",
            reason="later review invalidated this telemetry",
        )
        with self.assertRaises(ValidationError):
            encode_telemetry_pressure_for_unity(self.store, [scenario["scenario_id"]])

    def test_tampered_selection_fails_closed(self):
        batch_id = self._seed_public_batch()
        selection_id = self._selection([batch_id])
        manifest_path = (
            self.store.experience_dir
            / "raw-live"
            / "public-scenario-selections"
            / selection_id
            / "manifest.json"
        )
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["batches"][0]["tags"] = ["tampered"]
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaises(ValidationError):
            self._register(selection_id)

    def test_combined_telemetry_pressure_cannot_replace_majority_training(self):
        first = self._register(self._selection([self._seed_public_batch()]), fraction=0.3)
        second = register_telemetry_pressure(
            self.store,
            self._selection([self._seed_public_batch()]),
            bee_composition="Hornet",
            human_composition="Frigate",
            target_fraction=0.3,
            rationale="second reviewed live tactic",
            map_size=48.0,
            spawn_separation_ratio=0.5,
        )
        with self.assertRaises(ValidationError):
            encode_telemetry_pressure_for_unity(
                self.store, [first["scenario_id"], second["scenario_id"]]
            )


if __name__ == "__main__":
    unittest.main()
