"""Focused tests for paired player-derived adversarial scenario evaluation."""

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
evaluate = _load("bees_continual_evaluate")
adversarial_evaluate = _load("bees_continual_adversarial_evaluate")


OBSERVATION_SIZE = 4701
CONTINUOUS_ACTIONS = 34
DISCRETE_BRANCHES = [2] * 16 + [5, 65, 65, 65]


class AdversarialEvaluationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.config = continual.load_config(TRAINING_DIR / "continual_learning_config.json")
        self.store = continual.ContinualLearningStore(self.root / "store", self.config)
        self.store.initialize()

        self.models = {}
        for name in ("capture", "candidate", "baseline", "opponent"):
            artifact = self.root / f"{name}.onnx"
            artifact.write_bytes(name.encode("ascii"))
            self.models[name] = self.store.register_model(
                artifact,
                training_run_id=f"{name}-run",
                training_step=100,
                game_build_version="test-build",
            )

        self.incoming = self.root / "server" / "incoming"
        self.incoming.mkdir(parents=True)
        self.demo_path = self.incoming / ("rl-demo-" + "a" * 32 + ".demo")
        self.manifest_path = self.incoming / ("rl-demo-" + "a" * 32 + ".capture-manifest.json")
        self.metadata_path = self.incoming / ("rl-demo-" + "a" * 32 + ".json")
        self.demo_path.write_bytes(b"adversarial-evaluation-public-demo")
        self.manifest = {
            "schemaVersion": 1,
            "behaviorName": self.config["behavior_name"],
            "policyAbiVersion": self.config["policy_abi_version"],
            "policySignature": self.config["policy_signature"],
            "observationSize": OBSERVATION_SIZE,
            "continuousActionCount": CONTINUOUS_ACTIONS,
            "discreteBranchSizes": DISCRETE_BRANCHES,
        }
        self.manifest_path.write_text(json.dumps(self.manifest, indent=2), encoding="utf-8")
        self.metadata_path.write_text(
            json.dumps(
                {
                    "schemaVersion": 1,
                    "batchId": "rl-demo-" + "a" * 32,
                    "demonstrationId": "v7-" + "b" * 24,
                    "uploaderUserId": "76561198012345678",
                    "gameBuildVersion": "2026.09.11+build",
                    "source": "Human",
                    "trust": "authenticated-quarantine",
                    "readyForTraining": False,
                    "receivedAt": "2026-09-11T20:00:00.000Z",
                    "demoBytes": self.demo_path.stat().st_size,
                    "demoSha256": continual.sha256_file(self.demo_path),
                    "manifestSha256": continual.sha256_file(self.manifest_path),
                    "manifest": self.manifest,
                },
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )
        ingested = public.ingest_public_quarantine(
            self.store,
            self.metadata_path,
            model_id=self.models["capture"]["model_id"],
            loader=self.loader,
        )
        self.source_batch_id = ingested["batch_id"]
        curation.approve_public_batch(
            self.store,
            self.source_batch_id,
            reviewer="test-reviewer",
            reason="Repeatable matchup tactic.",
            quality_score=0.9,
        )
        self.scenario = adversarial.register_player_derived_scenario(
            self.store,
            [self.source_batch_id],
            bee_composition="Wasp",
            human_composition="Gunship",
            target_fraction=0.2,
            rationale="Repeatable matchup tactic.",
        )

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

    def fake_runner(self, calls):
        def run(**kwargs):
            model_bytes = Path(kwargs["candidate_model_path"]).read_bytes()
            wins = 7 if model_bytes == b"candidate" else 5
            calls.append(dict(kwargs))
            matches = kwargs["matches"]
            return evaluate.MatchSummary(
                matches=matches,
                wins=wins,
                losses=matches - wins,
                draws=0,
                timeouts=0,
                total_duration_seconds=float(matches * 10),
            )
        return run

    def test_paired_evaluation_reports_candidate_improvement_on_exact_matchup(self):
        calls = []
        report = adversarial_evaluate.evaluate_adversarial_scenarios(
            self.store,
            candidate_model_id=self.models["candidate"]["model_id"],
            baseline_model_id=self.models["baseline"]["model_id"],
            opponent_model_id=self.models["opponent"]["model_id"],
            environment_path=self.root / "fake-env",
            scenario_ids=[self.scenario["scenario_id"]],
            matches_per_scenario=10,
            base_env_args=["--rl-matchup-mode=sampled", "--rl-map-size=96"],
            seed=36,
            match_runner=self.fake_runner(calls),
        )

        self.assertEqual(len(calls), 2)
        self.assertEqual(calls[0]["seed"], calls[1]["seed"])
        self.assertIn("--rl-matchup-mode=fixed", calls[0]["env_args"])
        self.assertIn("--rl-ships-per-side=1", calls[0]["env_args"])
        self.assertIn("--rl-bee-ship-types=Wasp", calls[0]["env_args"])
        self.assertIn("--rl-human-ship-types=Gunship", calls[0]["env_args"])
        self.assertNotIn("--rl-matchup-mode=sampled", calls[0]["env_args"])
        self.assertIn("--rl-map-size=96", calls[0]["env_args"])

        scenario = report["scenarios"][0]
        self.assertAlmostEqual(scenario["candidate_score_rate"], 0.7)
        self.assertAlmostEqual(scenario["baseline_score_rate"], 0.5)
        self.assertAlmostEqual(scenario["score_rate_delta"], 0.2)
        self.assertTrue(scenario["improved"])
        self.assertAlmostEqual(
            report["aggregate"]["pressure_weighted_score_rate_delta"],
            0.2,
        )
        self.assertFalse(report["promotion_eligible"])
        self.assertTrue(Path(report["report_path"]).is_file())

    def test_revoked_scenario_source_blocks_diagnostic_evaluation(self):
        curation.revoke_public_batch(
            self.store,
            self.source_batch_id,
            reviewer="test-reviewer",
            reason="Later review invalidated this tactic source.",
        )
        calls = []
        with self.assertRaises(continual.ValidationError):
            adversarial_evaluate.evaluate_adversarial_scenarios(
                self.store,
                candidate_model_id=self.models["candidate"]["model_id"],
                baseline_model_id=self.models["baseline"]["model_id"],
                opponent_model_id=self.models["opponent"]["model_id"],
                environment_path=self.root / "fake-env",
                scenario_ids=[self.scenario["scenario_id"]],
                matches_per_scenario=10,
                match_runner=self.fake_runner(calls),
            )
        self.assertEqual(calls, [])

    def test_duplicate_scenario_selection_is_rejected_before_matches_run(self):
        calls = []
        with self.assertRaises(continual.ValidationError):
            adversarial_evaluate.evaluate_adversarial_scenarios(
                self.store,
                candidate_model_id=self.models["candidate"]["model_id"],
                baseline_model_id=self.models["baseline"]["model_id"],
                opponent_model_id=self.models["opponent"]["model_id"],
                environment_path=self.root / "fake-env",
                scenario_ids=[self.scenario["scenario_id"], self.scenario["scenario_id"]],
                matches_per_scenario=10,
                match_runner=self.fake_runner(calls),
            )
        self.assertEqual(calls, [])

    def test_diagnostic_rejects_training_pressure_flag_in_base_environment(self):
        calls = []
        with self.assertRaises(continual.ValidationError):
            adversarial_evaluate.evaluate_adversarial_scenarios(
                self.store,
                candidate_model_id=self.models["candidate"]["model_id"],
                baseline_model_id=self.models["baseline"]["model_id"],
                opponent_model_id=self.models["opponent"]["model_id"],
                environment_path=self.root / "fake-env",
                scenario_ids=[self.scenario["scenario_id"]],
                matches_per_scenario=10,
                base_env_args=["--bees-adversarial-matchups=manual"],
                match_runner=self.fake_runner(calls),
            )
        self.assertEqual(calls, [])


if __name__ == "__main__":
    unittest.main()
