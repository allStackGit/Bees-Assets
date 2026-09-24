"""Focused tests for native ML-Agents demonstration ingestion."""

from __future__ import annotations

import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest

from bees_continual_learning import (
    CompatibilityError,
    ContinualLearningError,
    ContinualLearningStore,
    ValidationError,
    load_config,
    sha256_file,
)
from bees_continual_native_demo import ingest_native_demonstration


OBSERVATION_SIZE = 4701
CONTINUOUS_ACTIONS = 34
DISCRETE_BRANCHES = [2] * 16 + [5, 65, 65, 65]


class NativeDemoIngestionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.config = load_config(Path(__file__).with_name("continual_learning_config.json"))
        self.config = json.loads(json.dumps(self.config))
        self.store = ContinualLearningStore(self.root / "store", self.config)
        self.store.initialize()

        artifact = self.root / "model.onnx"
        artifact.write_bytes(b"compatible-model")
        self.model = self.store.register_model(
            artifact,
            training_run_id="native-demo-test",
            training_step=100,
            game_build_version="test-build",
        )

        self.policy_dir = self.root / f"PolicyV{self.config['policy_abi_version']}"
        self.human_dir = self.policy_dir / "Human"
        self.human_dir.mkdir(parents=True)
        self.capture_manifest = {
            "schemaVersion": 1,
            "behaviorName": self.config["behavior_name"],
            "policyAbiVersion": self.config["policy_abi_version"],
            "policySignature": self.config["policy_signature"],
            "observationSize": OBSERVATION_SIZE,
            "continuousActionCount": CONTINUOUS_ACTIONS,
            "discreteBranchSizes": DISCRETE_BRANCHES,
        }
        self.manifest_path = self.policy_dir / "capture-manifest.json"
        self.write_manifest()
        self.demo = self.human_dir / "human-s0.demo"
        self.demo.write_bytes(b"native-demo-bytes")

    def tearDown(self):
        self.temp.cleanup()

    def write_manifest(self):
        self.manifest_path.write_text(
            json.dumps(self.capture_manifest, indent=2),
            encoding="utf-8",
        )

    @staticmethod
    def behavior_spec(
        *,
        observation_size=OBSERVATION_SIZE,
        continuous_actions=CONTINUOUS_ACTIONS,
        discrete_branches=DISCRETE_BRANCHES,
    ):
        return SimpleNamespace(
            observation_specs=[SimpleNamespace(shape=(observation_size,))],
            action_spec=SimpleNamespace(
                continuous_size=continuous_actions,
                discrete_branches=tuple(discrete_branches),
            ),
        )

    def loader(self, _path):
        return self.behavior_spec(), [object(), object(), object()], 3

    def ingest(self, demo_id="native-demo-1", **kwargs):
        return ingest_native_demonstration(
            self.store,
            self.demo,
            demonstration_id=demo_id,
            model_id=kwargs.pop("model_id", self.model["model_id"]),
            game_build_version=kwargs.pop("game_build_version", "test-build"),
            loader=kwargs.pop("loader", self.loader),
            **kwargs,
        )

    def test_native_demo_is_archived_with_manifest_and_provenance(self):
        result = self.ingest()

        self.assertFalse(result["duplicate"])
        self.assertEqual(result["example_count"], 2)
        demo_archive = Path(result["archive_path"])
        manifest_archive = Path(result["capture_manifest_path"])
        metadata_archive = Path(result["metadata_path"])
        self.assertEqual(demo_archive.read_bytes(), self.demo.read_bytes())
        self.assertEqual(manifest_archive.read_bytes(), self.manifest_path.read_bytes())

        metadata = json.loads(metadata_archive.read_text(encoding="utf-8"))
        self.assertEqual(metadata["demonstration_id"], "native-demo-1")
        self.assertEqual(metadata["model_id"], self.model["model_id"])
        self.assertEqual(metadata["native_demo"]["record_count"], 3)
        self.assertEqual(metadata["native_demo"]["trainable_example_count"], 2)
        self.assertEqual(metadata["native_demo"]["sha256"], sha256_file(self.demo))
        self.assertNotIn("source_name", metadata["native_demo"])
        self.assertEqual(
            metadata["capture_manifest"]["sha256"], sha256_file(self.manifest_path)
        )
        self.assertEqual(metadata["capture_manifest"]["metadata"], self.capture_manifest)
        self.assertTrue(
            metadata["payload_sha256"].startswith(result["batch_id"].removeprefix("demo-"))
        )
        self.assertEqual(self.store.status()["demonstration_batches"], 1)

    def test_exact_native_demo_ingestion_is_idempotent(self):
        first = self.ingest()
        second = self.ingest()

        self.assertFalse(first["duplicate"])
        self.assertTrue(second["duplicate"])
        self.assertEqual(first["batch_id"], second["batch_id"])
        self.assertEqual(self.store.status()["demonstration_batches"], 1)

    def test_retry_filename_does_not_change_batch_identity(self):
        first = self.ingest()
        retry = self.human_dir / "retry-upload.demo"
        retry.write_bytes(self.demo.read_bytes())

        second = ingest_native_demonstration(
            self.store,
            retry,
            demonstration_id="native-demo-1",
            model_id=self.model["model_id"],
            game_build_version="test-build",
            loader=self.loader,
        )

        self.assertTrue(second["duplicate"])
        self.assertEqual(first["batch_id"], second["batch_id"])

    def test_same_content_with_new_id_is_deduplicated_to_original_batch(self):
        first = self.ingest("native-demo-original")
        second = self.ingest("native-demo-retry-id")

        self.assertTrue(second["duplicate"])
        self.assertEqual(first["batch_id"], second["batch_id"])
        self.assertEqual(second["demonstration_id"], "native-demo-original")
        self.assertEqual(self.store.status()["demonstration_batches"], 1)

    def test_same_demonstration_id_with_changed_bytes_is_rejected(self):
        self.ingest()
        self.demo.write_bytes(b"different-native-demo")

        with self.assertRaises(ValidationError):
            self.ingest()
        self.assertEqual(self.store.status()["demonstration_batches"], 1)

    def test_observation_shape_mismatch_is_rejected_before_archival(self):
        def wrong_loader(_path):
            return self.behavior_spec(observation_size=17), [object(), object()], 2

        with self.assertRaises(CompatibilityError):
            self.ingest(loader=wrong_loader)
        self.assertEqual(self.store.status()["demonstration_batches"], 0)

    def test_action_shape_mismatch_is_rejected_before_archival(self):
        cases = (
            self.behavior_spec(continuous_actions=2),
            self.behavior_spec(discrete_branches=[2, 5]),
        )
        for behavior_spec in cases:
            with self.subTest(action_spec=behavior_spec.action_spec):
                def wrong_loader(_path, spec=behavior_spec):
                    return spec, [object(), object()], 2

                with self.assertRaises(CompatibilityError):
                    self.ingest(loader=wrong_loader)
        self.assertEqual(self.store.status()["demonstration_batches"], 0)

    def test_capture_manifest_signature_mismatch_is_validation_error(self):
        self.capture_manifest["policySignature"] = "stale-signature"
        self.write_manifest()

        with self.assertRaises(ValidationError):
            self.ingest()
        self.assertEqual(self.store.status()["demonstration_batches"], 0)

    def test_hivemind_named_demo_is_never_accepted_as_human(self):
        hive_demo = self.human_dir / "hivemind-cap-s0.demo"
        hive_demo.write_bytes(b"hive-demo")

        with self.assertRaises(ValidationError):
            ingest_native_demonstration(
                self.store,
                hive_demo,
                demonstration_id="hive",
                model_id=self.model["model_id"],
                game_build_version="test-build",
                loader=self.loader,
            )

    def test_record_count_limit_is_enforced(self):
        self.store.config["ingestion"]["max_steps_per_match"] = 2

        with self.assertRaises(ValidationError):
            self.ingest()

    def test_payload_size_limit_includes_capture_manifest(self):
        self.store.config["ingestion"]["max_payload_bytes"] = self.demo.stat().st_size

        with self.assertRaises(ValidationError):
            self.ingest()

    def test_parser_metadata_count_must_match_parsed_records(self):
        def mismatched_loader(_path):
            return self.behavior_spec(), [object(), object()], 3

        with self.assertRaises(ValidationError):
            self.ingest(loader=mismatched_loader)

    def test_unknown_model_context_is_rejected_without_archiving(self):
        with self.assertRaises(ContinualLearningError):
            self.ingest(model_id="unknown-model")
        native_dir = self.store.experience_dir / "human-demos" / "native"
        self.assertFalse(native_dir.exists())


if __name__ == "__main__":
    unittest.main()
