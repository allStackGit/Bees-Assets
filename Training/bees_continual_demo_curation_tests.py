"""Focused tests for explicit public Human-demo curation and training-set materialization."""

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
train = _load("bees_continual_train")
native = _load("bees_continual_native_demo")
public = _load("bees_continual_public_demo")
curation = _load("bees_continual_demo_curation")


OBSERVATION_SIZE = 4701
CONTINUOUS_ACTIONS = 34
DISCRETE_BRANCHES = [2] * 16 + [5, 65, 65, 65]


class PublicDemoCurationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.config = continual.load_config(TRAINING_DIR / "continual_learning_config.json")
        self.store = continual.ContinualLearningStore(self.root / "store", self.config)
        self.store.initialize()

        artifact = self.root / "model.onnx"
        artifact.write_bytes(b"curation-compatible-model")
        self.model = self.store.register_model(
            artifact,
            training_run_id="curation-test",
            training_step=100,
            game_build_version="test-build",
        )
        self.incoming = self.root / "server" / "incoming"
        self.incoming.mkdir(parents=True)
        self.counter = 0

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

    def manifest(self):
        return {
            "schemaVersion": 1,
            "behaviorName": self.config["behavior_name"],
            "policyAbiVersion": self.config["policy_abi_version"],
            "policySignature": self.config["policy_signature"],
            "observationSize": OBSERVATION_SIZE,
            "continuousActionCount": CONTINUOUS_ACTIONS,
            "discreteBranchSizes": DISCRETE_BRANCHES,
        }

    def ingest_public(self, payload: bytes):
        self.counter += 1
        server_batch = f"rl-demo-{self.counter:032x}"
        demo_path = self.incoming / f"{server_batch}.demo"
        manifest_path = self.incoming / f"{server_batch}.capture-manifest.json"
        metadata_path = self.incoming / f"{server_batch}.json"
        demo_path.write_bytes(payload)
        manifest = self.manifest()
        manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        metadata = {
            "schemaVersion": 1,
            "batchId": server_batch,
            "demonstrationId": f"v7-{self.counter:024x}",
            "uploaderUserId": str(76561198012345000 + self.counter),
            "gameBuildVersion": "2026.09.11+build",
            "source": "Human",
            "trust": "authenticated-quarantine",
            "readyForTraining": False,
            "receivedAt": "2026-09-11T19:00:00.000Z",
            "demoBytes": demo_path.stat().st_size,
            "demoSha256": continual.sha256_file(demo_path),
            "manifestSha256": continual.sha256_file(manifest_path),
            "manifest": manifest,
        }
        metadata_path.write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        return public.ingest_public_quarantine(
            self.store,
            metadata_path,
            model_id=self.model["model_id"],
            loader=self.loader,
        )

    def approve(self, batch_id: str, *, reason="useful ranged play"):
        return curation.approve_public_batch(
            self.store,
            batch_id,
            reviewer="operator-test",
            reason=reason,
            quality_score=0.9,
        )

    def test_public_batch_requires_explicit_approval_before_materialization(self):
        ingested = self.ingest_public(b"public-demo-one")

        with self.assertRaises(continual.ValidationError):
            curation.materialize_approved_training_set(
                self.store,
                [ingested["batch_id"]],
            )

    def test_approval_is_immutable_and_same_decision_is_idempotent(self):
        ingested = self.ingest_public(b"public-demo-two")
        first = self.approve(ingested["batch_id"])
        second = self.approve(ingested["batch_id"])

        self.assertEqual(first, second)
        self.assertEqual(first["decision"], "approved")
        self.assertEqual(first["batch_id"], ingested["batch_id"])
        self.assertEqual(first["quality_score"], 0.9)

        with self.assertRaises(continual.ContinualLearningError):
            self.approve(ingested["batch_id"], reason="different retrospective reason")

    def test_non_public_native_batch_cannot_be_approved(self):
        policy_root = self.root / f"PolicyV{self.config['policy_abi_version']}"
        human = policy_root / "Human"
        human.mkdir(parents=True)
        demo = human / "human-local.demo"
        demo.write_bytes(b"trusted-local-demo")
        (policy_root / "capture-manifest.json").write_text(
            json.dumps(self.manifest(), indent=2),
            encoding="utf-8",
        )
        local = native.ingest_native_demonstration(
            self.store,
            demo,
            demonstration_id="trusted-local",
            model_id=self.model["model_id"],
            game_build_version="test-build",
            loader=self.loader,
        )

        with self.assertRaises(continual.ValidationError):
            self.approve(local["batch_id"])

    def test_revocation_permanently_blocks_future_materialization(self):
        ingested = self.ingest_public(b"public-demo-three")
        self.approve(ingested["batch_id"])
        curation.revoke_public_batch(
            self.store,
            ingested["batch_id"],
            reviewer="operator-test",
            reason="later review found low quality",
        )

        with self.assertRaises(continual.ValidationError):
            curation.materialize_approved_training_set(
                self.store,
                [ingested["batch_id"]],
            )
        with self.assertRaises(continual.ValidationError):
            self.approve(ingested["batch_id"])

    def test_materialized_set_contains_only_explicitly_selected_approved_batches(self):
        first = self.ingest_public(b"public-demo-four")
        second = self.ingest_public(b"public-demo-five")
        self.approve(first["batch_id"])
        self.approve(second["batch_id"])

        result = curation.materialize_approved_training_set(
            self.store,
            [first["batch_id"]],
        )
        human_dir = Path(result["human_demo_dir"])
        files = sorted(path.name for path in human_dir.glob("*.demo"))

        self.assertEqual(files, [f"{first['batch_id']}.demo"])
        self.assertEqual(result["batch_count"], 1)
        self.assertEqual(result["example_count"], first["example_count"])
        self.assertNotIn(second["batch_id"], Path(result["manifest_path"]).read_text(encoding="utf-8"))

        source, demos, _, capture, _ = train.validate_human_demonstration_directory(
            human_dir,
            self.config,
        )
        self.assertEqual(source, human_dir.resolve())
        self.assertEqual([path.name for path in demos], files)
        self.assertEqual(capture["policySignature"], self.config["policy_signature"])

    def test_training_set_identity_is_order_independent_and_idempotent(self):
        first = self.ingest_public(b"public-demo-six")
        second = self.ingest_public(b"public-demo-seven")
        self.approve(first["batch_id"])
        self.approve(second["batch_id"])

        one = curation.materialize_approved_training_set(
            self.store,
            [first["batch_id"], second["batch_id"]],
        )
        two = curation.materialize_approved_training_set(
            self.store,
            [second["batch_id"], first["batch_id"]],
        )

        self.assertEqual(one["training_set_id"], two["training_set_id"])
        self.assertEqual(one["identity_sha256"], two["identity_sha256"])
        self.assertEqual(one["human_demo_dir"], two["human_demo_dir"])

    def test_duplicate_batch_selection_is_rejected(self):
        ingested = self.ingest_public(b"public-demo-eight")
        self.approve(ingested["batch_id"])

        with self.assertRaises(continual.ValidationError):
            curation.materialize_approved_training_set(
                self.store,
                [ingested["batch_id"], ingested["batch_id"]],
            )

    def test_archive_tampering_after_approval_is_detected(self):
        ingested = self.ingest_public(b"public-demo-nine")
        self.approve(ingested["batch_id"])
        archive = Path(ingested["archive_path"])
        archive.write_bytes(b"tampered-after-approval")

        with self.assertRaises(continual.ValidationError):
            curation.materialize_approved_training_set(
                self.store,
                [ingested["batch_id"]],
            )


if __name__ == "__main__":
    unittest.main()
