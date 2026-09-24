"""Focused tests for authenticated BeesServer public demonstration quarantine ingestion."""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import sys
import tempfile
import unittest


TRAINING_DIR = Path(__file__).parent


def _load(name: str):
    path = TRAINING_DIR / f"{name}.py"
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


continual = _load("bees_continual_learning")
native = _load("bees_continual_native_demo")
contributors = _load("bees_continual_demo_contributors")
public = _load("bees_continual_public_demo")


OBSERVATION_SIZE = 4701
CONTINUOUS_ACTIONS = 34
DISCRETE_BRANCHES = [2] * 16 + [5, 65, 65, 65]


class PublicDemoQuarantineTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        config_path = TRAINING_DIR / "continual_learning_config.json"
        self.config = continual.load_config(config_path)
        self.store = continual.ContinualLearningStore(self.root / "store", self.config)
        self.store.initialize()

        artifact = self.root / "model.onnx"
        artifact.write_bytes(b"public-demo-compatible-model")
        self.model = self.store.register_model(
            artifact,
            training_run_id="public-demo-test",
            training_step=100,
            game_build_version="test-build",
        )

        self.incoming = self.root / "server" / "incoming"
        self.incoming.mkdir(parents=True)
        self.batch_id = "rl-demo-" + "a" * 32
        self.demo_path = self.incoming / f"{self.batch_id}.demo"
        self.manifest_path = self.incoming / f"{self.batch_id}.capture-manifest.json"
        self.metadata_path = self.incoming / f"{self.batch_id}.json"
        self.demo_path.write_bytes(b"native-public-demo-bytes")
        self.manifest = {
            "schemaVersion": 1,
            "behaviorName": self.config["behavior_name"],
            "policyAbiVersion": self.config["policy_abi_version"],
            "policySignature": self.config["policy_signature"],
            "observationSize": OBSERVATION_SIZE,
            "continuousActionCount": CONTINUOUS_ACTIONS,
            "discreteBranchSizes": DISCRETE_BRANCHES,
        }
        self.manifest_path.write_text(
            json.dumps(self.manifest, indent=2),
            encoding="utf-8",
        )
        self.uploader_user_id = "76561198012345678"
        self.write_metadata()

    def tearDown(self):
        self.temp.cleanup()

    @staticmethod
    def behavior_spec():
        return SimpleNamespace(
            observation_specs=[SimpleNamespace(shape=(OBSERVATION_SIZE,))],
            action_spec=SimpleNamespace(
                continuous_size=CONTINUOUS_ACTIONS,
                discrete_branches=tuple(DISCRETE_BRANCHES),
            ),
        )

    def loader(self, _path):
        return self.behavior_spec(), [object(), object(), object()], 3

    def metadata(self):
        return {
            "schemaVersion": 1,
            "batchId": self.batch_id,
            "demonstrationId": "v7-" + "b" * 24,
            "uploaderUserId": self.uploader_user_id,
            "gameBuildVersion": "2026.09.11+build",
            "source": "Human",
            "trust": "authenticated-quarantine",
            "readyForTraining": False,
            "receivedAt": "2026-09-11T19:00:00.000Z",
            "demoBytes": self.demo_path.stat().st_size,
            "demoSha256": continual.sha256_file(self.demo_path),
            "manifestSha256": continual.sha256_file(self.manifest_path),
            "manifest": self.manifest,
        }

    def write_metadata(self, overrides=None):
        value = self.metadata()
        if overrides:
            value.update(overrides)
        self.metadata_path.write_text(
            json.dumps(value, indent=2) + "\n",
            encoding="utf-8",
        )

    def ingest(self):
        return public.ingest_public_quarantine(
            self.store,
            self.metadata_path,
            model_id=self.model["model_id"],
            loader=self.loader,
        )

    def test_authenticated_quarantine_is_native_validated_but_not_approved(self):
        result = self.ingest()

        self.assertFalse(result["duplicate"])
        self.assertFalse(result["approved_for_training"])
        self.assertEqual(result["public_quarantine_batch_id"], self.batch_id)
        self.assertEqual(self.store.status()["demonstration_batches"], 1)

        provenance = Path(result["public_provenance_path"]).read_text(encoding="utf-8")
        contributor = Path(result["public_contributor_record_path"]).read_text(encoding="utf-8")
        self.assertIn('"source_trust": "authenticated-quarantine"', provenance)
        self.assertIn('"approved_for_training": false', provenance)
        self.assertNotIn(self.uploader_user_id, provenance)
        self.assertNotIn(self.uploader_user_id, contributor)
        self.assertRegex(json.loads(contributor)["contributor_bucket"], r"^[0-9a-f]{64}$")

    def test_exact_retry_is_idempotent(self):
        first = self.ingest()
        second = self.ingest()

        self.assertEqual(first["batch_id"], second["batch_id"])
        self.assertTrue(second["duplicate"])
        self.assertEqual(first["public_provenance_path"], second["public_provenance_path"])
        self.assertEqual(first["public_contributor_record_path"], second["public_contributor_record_path"])

    def test_demo_tampering_is_rejected_before_native_import(self):
        self.demo_path.write_bytes(b"tampered")

        with self.assertRaises(continual.ValidationError):
            self.ingest()
        self.assertEqual(self.store.status()["demonstration_batches"], 0)

    def test_manifest_sidecar_disagreement_is_rejected(self):
        altered = dict(self.manifest)
        altered["policyAbiVersion"] += 1
        self.write_metadata({"manifest": altered})

        with self.assertRaises(continual.ValidationError):
            self.ingest()
        self.assertEqual(self.store.status()["demonstration_batches"], 0)

    def test_quarantine_cannot_claim_training_approval(self):
        self.write_metadata({"readyForTraining": True})

        with self.assertRaises(continual.ValidationError):
            self.ingest()

    def test_non_human_or_unauthenticated_trust_state_is_rejected(self):
        for overrides in (
            {"source": "HiveMind"},
            {"trust": "client-claimed"},
        ):
            with self.subTest(overrides=overrides):
                self.write_metadata(overrides)
                with self.assertRaises(continual.ValidationError):
                    self.ingest()

    def test_metadata_filename_must_match_server_batch_id(self):
        wrong = self.incoming / "wrong-name.json"
        wrong.write_bytes(self.metadata_path.read_bytes())

        with self.assertRaises(continual.ValidationError):
            public.validate_quarantine_bundle(
                wrong,
                max_payload_bytes=self.config["ingestion"]["max_payload_bytes"],
            )


if __name__ == "__main__":
    unittest.main()
