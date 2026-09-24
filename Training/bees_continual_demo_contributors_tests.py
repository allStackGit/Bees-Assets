"""Focused tests for privacy-safe public demonstration contributor grouping."""

from __future__ import annotations

import importlib.util
from pathlib import Path
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


class PublicDemoContributorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        config = continual.load_config(TRAINING_DIR / "continual_learning_config.json")
        self.store = continual.ContinualLearningStore(Path(self.temp.name) / "store", config)
        self.store.initialize()

    def tearDown(self):
        self.temp.cleanup()

    def test_same_user_has_stable_store_local_bucket(self):
        user = "76561198012345678"
        first = contributors.contributor_bucket(self.store, user)
        second = contributors.contributor_bucket(self.store, user)
        other = contributors.contributor_bucket(self.store, "76561198012345679")

        self.assertEqual(first, second)
        self.assertNotEqual(first, other)
        self.assertRegex(first, r"^[0-9a-f]{64}$")
        key_path = self.store.root / "metadata" / "public-demo-contributor.key"
        self.assertTrue(key_path.is_file())
        self.assertNotIn(user.encode("ascii"), key_path.read_bytes())

    def test_contributor_record_does_not_store_raw_user_id(self):
        user = "76561198012345678"
        record = contributors.write_public_contributor_record(
            self.store,
            central_batch_id="demo-" + "a" * 24,
            server_batch_id="rl-demo-" + "b" * 32,
            uploader_user_id=user,
        )
        text = Path(record["path"]).read_text(encoding="utf-8")

        self.assertNotIn(user, text)
        self.assertIn(record["contributor_bucket"], text)

    def test_missing_key_with_existing_records_fails_closed(self):
        contributors.write_public_contributor_record(
            self.store,
            central_batch_id="demo-" + "a" * 24,
            server_batch_id="rl-demo-" + "b" * 32,
            uploader_user_id="76561198012345678",
        )
        key_path = self.store.root / "metadata" / "public-demo-contributor.key"
        key_path.unlink()

        with self.assertRaises(continual.ContinualLearningError):
            contributors.contributor_bucket(self.store, "76561198012345679")


if __name__ == "__main__":
    unittest.main()
