import copy
import tempfile
import unittest
from pathlib import Path

from bees_continual_bootstrap import bootstrap_champion
from bees_continual_learning import (
    ContinualLearningStore,
    PromotionError,
    ValidationError,
    load_config,
)


class ContinualBootstrapTests(unittest.TestCase):
    def make_store(self, root):
        store = ContinualLearningStore(root, config=load_config())
        store.initialize()
        return store

    def register_candidate(self, store, root, name, payload):
        artifact = Path(root) / f"{name}.onnx"
        artifact.write_bytes(payload)
        return store.register_model(
            artifact,
            training_run_id="bootstrap-test",
            training_step=len(payload),
            game_build_version="test-build",
            status="candidate",
        )

    def test_bootstrap_establishes_first_champion_with_audit_metadata(self):
        with tempfile.TemporaryDirectory() as temp:
            store = self.make_store(temp)
            candidate = self.register_candidate(store, temp, "seed", b"seed-policy")
            candidate_path = Path(candidate["artifact_path"])

            champion = bootstrap_champion(
                store,
                candidate["model_id"],
                reason="Trusted generation-zero baseline",
            )

            self.assertEqual(champion["status"], "champion")
            self.assertEqual(store.current_champion_id(), candidate["model_id"])
            self.assertEqual(Path(champion["artifact_path"]).parent.name, "champions")
            self.assertTrue(Path(champion["artifact_path"]).is_file())
            self.assertFalse(candidate_path.exists())
            self.assertIsNone(champion["evaluation_report_id"])
            bootstrap = champion["metadata"]["champion_bootstrap"]
            self.assertEqual(bootstrap["reason"], "Trusted generation-zero baseline")
            self.assertEqual(bootstrap["source_status"], "candidate")
            self.assertTrue(bootstrap["created_at"].endswith("Z"))

    def test_bootstrap_requires_explicit_reason(self):
        with tempfile.TemporaryDirectory() as temp:
            store = self.make_store(temp)
            candidate = self.register_candidate(store, temp, "seed", b"seed-policy")

            with self.assertRaises(ValidationError):
                bootstrap_champion(store, candidate["model_id"], reason="   ")

            self.assertIsNone(store.current_champion_id())
            self.assertEqual(store.get_model(candidate["model_id"])["status"], "candidate")

    def test_bootstrap_cannot_run_after_champion_history_exists(self):
        with tempfile.TemporaryDirectory() as temp:
            store = self.make_store(temp)
            first = self.register_candidate(store, temp, "first", b"first-policy")
            second = self.register_candidate(store, temp, "second", b"second-policy-longer")
            bootstrap_champion(store, first["model_id"], reason="Initial trusted baseline")
            second_path = Path(store.get_model(second["model_id"])["artifact_path"])

            with self.assertRaises(PromotionError):
                bootstrap_champion(store, second["model_id"], reason="Attempted replacement")

            self.assertEqual(store.current_champion_id(), first["model_id"])
            self.assertEqual(store.get_model(second["model_id"])["status"], "candidate")
            self.assertTrue(second_path.is_file())

    def test_bootstrap_retires_incompatible_current_champion_and_starts_new_generation(self):
        with tempfile.TemporaryDirectory() as temp:
            current_config = load_config()
            old_config = copy.deepcopy(current_config)
            old_config["policy_abi_version"] = int(current_config["policy_abi_version"]) - 1
            old_config["policy_signature"] = "obsolete-test-policy-signature"

            old_store = ContinualLearningStore(temp, config=old_config)
            old_store.initialize()
            old_candidate = self.register_candidate(
                old_store, temp, "old-seed", b"old-seed-policy"
            )
            old_champion = bootstrap_champion(
                old_store,
                old_candidate["model_id"],
                reason="Previous compatibility baseline",
            )

            store = ContinualLearningStore(temp, config=current_config)
            store.initialize()
            self.assertEqual(store.current_champion_id(), old_champion["model_id"])
            self.assertIsNone(store.current_compatible_champion_id())

            candidate = self.register_candidate(
                store, temp, "new-seed", b"new-seed-policy"
            )
            champion = bootstrap_champion(
                store,
                candidate["model_id"],
                reason="New compatibility generation baseline",
            )

            self.assertEqual(store.current_champion_id(), candidate["model_id"])
            self.assertEqual(store.current_compatible_champion_id(), candidate["model_id"])
            self.assertEqual(store.get_model(old_champion["model_id"])["status"], "retired")
            self.assertEqual(
                champion["metadata"]["champion_bootstrap"][
                    "replaced_incompatible_champion_model_id"
                ],
                old_champion["model_id"],
            )
            with self.assertRaises(PromotionError):
                bootstrap_champion(
                    store,
                    self.register_candidate(
                        store, temp, "replacement", b"replacement-policy"
                    )["model_id"],
                    reason="Unsafe second bootstrap in the same compatibility generation",
                )

    def test_bootstrap_refuses_hidden_prior_history_even_if_state_is_corrupted(self):
        with tempfile.TemporaryDirectory() as temp:
            store = self.make_store(temp)
            first = self.register_candidate(store, temp, "first", b"first-policy")
            second = self.register_candidate(store, temp, "second", b"second-policy-longer")
            bootstrap_champion(store, first["model_id"], reason="Initial trusted baseline")

            with store._connect() as db:
                db.execute("BEGIN IMMEDIATE")
                db.execute(
                    "UPDATE models SET status='historical' WHERE model_id=?",
                    (first["model_id"],),
                )
                db.execute("DELETE FROM state")

            with self.assertRaises(PromotionError):
                bootstrap_champion(store, second["model_id"], reason="Unsafe rebootstrap")

            self.assertIsNone(store.current_champion_id())
            self.assertEqual(store.get_model(first["model_id"])["status"], "historical")
            self.assertEqual(store.get_model(second["model_id"])["status"], "candidate")


if __name__ == "__main__":
    unittest.main()
