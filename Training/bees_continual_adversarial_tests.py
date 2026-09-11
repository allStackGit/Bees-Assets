"""Focused tests for player-derived adversarial matchup pressure."""

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
contributors = _load("bees_continual_demo_contributors")
public = _load("bees_continual_public_demo")
curation = _load("bees_continual_demo_curation")
adversarial = _load("bees_continual_adversarial")


OBSERVATION_SIZE = 4701
CONTINUOUS_ACTIONS = 34
DISCRETE_BRANCHES = [2] * 16 + [5, 65, 65, 65]


class AdversarialScenarioTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.config = continual.load_config(TRAINING_DIR / "continual_learning_config.json")
        self.store = continual.ContinualLearningStore(self.root / "store", self.config)
        self.store.initialize()

        artifact = self.root / "model.onnx"
        artifact.write_bytes(b"adversarial-compatible-model")
        self.model = self.store.register_model(
            artifact,
            training_run_id="adversarial-test",
            training_step=100,
            game_build_version="test-build",
        )

        self.incoming = self.root / "server" / "incoming"
        self.incoming.mkdir(parents=True)
        self.batch_counter = 0

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

    def ingest_public(self, uploader="76561198012345678"):
        self.batch_counter += 1
        server_batch = "rl-demo-" + f"{self.batch_counter:032x}"
        demo_path = self.incoming / f"{server_batch}.demo"
        manifest_path = self.incoming / f"{server_batch}.capture-manifest.json"
        metadata_path = self.incoming / f"{server_batch}.json"
        demo_path.write_bytes(f"public-demo-{self.batch_counter}".encode("utf-8"))
        manifest = {
            "schemaVersion": 1,
            "behaviorName": self.config["behavior_name"],
            "policyAbiVersion": self.config["policy_abi_version"],
            "policySignature": self.config["policy_signature"],
            "observationSize": OBSERVATION_SIZE,
            "continuousActionCount": CONTINUOUS_ACTIONS,
            "discreteBranchSizes": DISCRETE_BRANCHES,
        }
        manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        metadata = {
            "schemaVersion": 1,
            "batchId": server_batch,
            "demonstrationId": "v7-" + f"{self.batch_counter:024x}",
            "uploaderUserId": uploader,
            "gameBuildVersion": "2026.09.11+build",
            "source": "Human",
            "trust": "authenticated-quarantine",
            "readyForTraining": False,
            "receivedAt": "2026-09-11T20:00:00.000Z",
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

    def approve(self, batch_id, score=0.9):
        return curation.approve_public_batch(
            self.store,
            batch_id,
            reviewer="test-reviewer",
            reason="Observed repeatable long-range pressure tactic.",
            quality_score=score,
        )

    def register(self, batches, bee="Wasp", human="Gunship", fraction=0.1):
        return adversarial.register_player_derived_scenario(
            self.store,
            batches,
            bee_composition=bee,
            human_composition=human,
            target_fraction=fraction,
            rationale="Repeated player tactic requiring fresh counter-training.",
        )

    def test_approved_public_batches_register_immutable_scenario(self):
        ingested = self.ingest_public()
        self.approve(ingested["batch_id"])

        first = self.register([ingested["batch_id"]])
        second = self.register([ingested["batch_id"]])

        self.assertFalse(first["duplicate"])
        self.assertTrue(second["duplicate"])
        self.assertEqual(first["scenario_id"], second["scenario_id"])
        scenario_text = Path(first["path"]).read_text(encoding="utf-8")
        self.assertNotIn("76561198012345678", scenario_text)
        self.assertIn('"source": "approved-public-human-demonstrations"', scenario_text)

    def test_unapproved_or_revoked_source_is_rejected(self):
        unapproved = self.ingest_public()
        with self.assertRaises(continual.ValidationError):
            self.register([unapproved["batch_id"]])

        approved = self.ingest_public()
        self.approve(approved["batch_id"])
        curation.revoke_public_batch(
            self.store,
            approved["batch_id"],
            reviewer="test-reviewer",
            reason="Tactic label was invalidated.",
        )
        with self.assertRaises(continual.ValidationError):
            self.register([approved["batch_id"]])

    def test_encoding_is_deterministic_and_carries_named_pressure(self):
        first = self.ingest_public()
        second = self.ingest_public()
        self.approve(first["batch_id"])
        self.approve(second["batch_id"])
        scenario_a = self.register([first["batch_id"]], bee="Wasp", human="Gunship", fraction=0.1)
        scenario_b = self.register([second["batch_id"]], bee="Hornet", human="Frigate", fraction=0.2)

        encoded = adversarial.encode_scenarios_for_unity(
            self.store,
            [scenario_b["scenario_id"], scenario_a["scenario_id"]],
        )

        expected_ids = sorted([scenario_a["scenario_id"], scenario_b["scenario_id"]])
        self.assertTrue(encoded.startswith(expected_ids[0] + ":"))
        self.assertIn("Wasp>Gunship@0.1", encoded)
        self.assertIn("Hornet>Frigate@0.2", encoded)

    def test_selected_scenarios_must_share_team_size(self):
        first = self.ingest_public()
        second = self.ingest_public()
        self.approve(first["batch_id"])
        self.approve(second["batch_id"])
        one = self.register([first["batch_id"]], bee="Wasp", human="Gunship")
        two = self.register(
            [second["batch_id"]],
            bee="Wasp,Hornet",
            human="Gunship,Frigate",
        )

        with self.assertRaises(continual.ValidationError):
            adversarial.encode_scenarios_for_unity(
                self.store,
                [one["scenario_id"], two["scenario_id"]],
            )

    def test_combined_pressure_cannot_replace_majority_baseline_training(self):
        first = self.ingest_public()
        second = self.ingest_public()
        self.approve(first["batch_id"])
        self.approve(second["batch_id"])
        scenario_a = self.register([first["batch_id"]], fraction=0.3)
        scenario_b = self.register([second["batch_id"]], bee="Hornet", human="Frigate", fraction=0.3)

        with self.assertRaises(continual.ValidationError):
            adversarial.encode_scenarios_for_unity(
                self.store,
                [scenario_a["scenario_id"], scenario_b["scenario_id"]],
            )

    def test_invalid_fraction_or_asymmetric_composition_fails_closed(self):
        ingested = self.ingest_public()
        self.approve(ingested["batch_id"])
        for value in (0, -0.1, 0.5001, float("nan")):
            with self.subTest(value=value):
                with self.assertRaises(continual.ValidationError):
                    self.register([ingested["batch_id"]], fraction=value)
        with self.assertRaises(continual.ValidationError):
            self.register(
                [ingested["batch_id"]],
                bee="Wasp,Hornet",
                human="Gunship",
            )


if __name__ == "__main__":
    unittest.main()
