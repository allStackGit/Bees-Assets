"""Focused tests for continual human-demonstration training support."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest


MODULE_PATH = Path(__file__).with_name("bees_continual_train.py")
SPEC = importlib.util.spec_from_file_location("bees_continual_train_human_demo_tests", MODULE_PATH)
wrapper = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = wrapper
assert SPEC.loader is not None
SPEC.loader.exec_module(wrapper)


class HumanDemoOptionTests(unittest.TestCase):
    def test_human_demo_flag_is_removed_from_mlagents_arguments(self):
        trainer_args, options = wrapper.extract_continual_options(
            [
                "Training/rl_1v1_config.yaml",
                "--run-id=demo-run",
                "--continual-root=F:/continual",
                "--continual-game-build=build-7",
                "--continual-human-demo-dir=F:/demos/Human",
            ]
        )

        self.assertEqual(
            trainer_args,
            ["Training/rl_1v1_config.yaml", "--run-id=demo-run"],
        )
        self.assertEqual(options.human_demo_dir, "F:/demos/Human")

    def test_human_demo_flag_requires_continual_root(self):
        with self.assertRaises(SystemExit):
            wrapper.extract_continual_options(
                ["--continual-human-demo-dir=F:/demos/Human"]
            )


class HumanImitationSettingsTests(unittest.TestCase):
    def test_settings_are_read_from_continual_config(self):
        self.assertEqual(
            wrapper.human_imitation_settings(
                {
                    "human_imitation": {
                        "strength": 0.05,
                        "steps": 500000,
                        "batch_size": 512,
                    }
                }
            ),
            (0.05, 500000, 512),
        )

    def test_invalid_settings_are_rejected(self):
        invalid = (
            {},
            {"human_imitation": {}},
            {"human_imitation": {"strength": 0, "steps": 1, "batch_size": 1}},
            {"human_imitation": {"strength": float("nan"), "steps": 1, "batch_size": 1}},
            {"human_imitation": {"strength": 0.1, "steps": 0, "batch_size": 1}},
            {"human_imitation": {"strength": 0.1, "steps": 1, "batch_size": 0}},
            {"human_imitation": {"strength": True, "steps": 1, "batch_size": 1}},
        )
        for config in invalid:
            with self.subTest(config=config):
                with self.assertRaises(SystemExit):
                    wrapper.human_imitation_settings(config)


class HumanDemoSnapshotTests(unittest.TestCase):
    def test_snapshot_is_content_addressed_and_reusable(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "Human"
            source.mkdir()
            (source / "human-s0.demo").write_bytes(b"human-demo-one")
            (source / "human-s1.demo").write_bytes(b"human-demo-two")

            first_path, first_hash, first_count = wrapper.snapshot_human_demonstrations(
                source, root / "store"
            )
            second_path, second_hash, second_count = wrapper.snapshot_human_demonstrations(
                source, root / "store"
            )

            self.assertEqual(first_path, second_path)
            self.assertEqual(first_hash, second_hash)
            self.assertEqual(first_count, 2)
            self.assertEqual(second_count, 2)
            self.assertEqual(
                (first_path / "human-s0.demo").read_bytes(), b"human-demo-one"
            )
            self.assertTrue((first_path / "manifest.json").is_file())

    def test_hivemind_files_are_rejected_from_human_dataset(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            source = Path(temp_dir)
            (source / "hivemind-s0.demo").write_bytes(b"not-human")

            with self.assertRaises(SystemExit):
                wrapper.snapshot_human_demonstrations(source, source / "store")

    def test_empty_directory_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            with self.assertRaises(SystemExit):
                wrapper.snapshot_human_demonstrations(
                    Path(temp_dir), Path(temp_dir) / "store"
                )


class HumanImitationConfigTests(unittest.TestCase):
    def test_runtime_yaml_adds_behavioral_cloning_without_mutating_source(self):
        try:
            import yaml
        except ImportError:
            self.skipTest("PyYAML is not installed in this test environment")

        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source_config = root / "base.yaml"
            source_text = (
                "behaviors:\n"
                "  BeesRL1v1:\n"
                "    trainer_type: ppo\n"
                "    hyperparameters:\n"
                "      batch_size: 512\n"
            )
            source_config.write_text(source_text, encoding="utf-8")
            demo_dir = root / "human-set"
            demo_dir.mkdir()
            (demo_dir / "human.demo").write_bytes(b"demo")

            generated = wrapper.prepare_human_imitation_config(
                source_config,
                output_root=root / "store",
                demo_directory=demo_dir,
                behavior_name="BeesRL1v1",
                strength=0.05,
                steps=500000,
                batch_size=512,
            )

            loaded = yaml.safe_load(generated.read_text(encoding="utf-8"))
            cloning = loaded["behaviors"]["BeesRL1v1"]["behavioral_cloning"]
            self.assertEqual(cloning["demo_path"], str(demo_dir.resolve()))
            self.assertEqual(cloning["strength"], 0.05)
            self.assertEqual(cloning["steps"], 500000)
            self.assertEqual(cloning["batch_size"], 512)
            self.assertEqual(source_config.read_text(encoding="utf-8"), source_text)

            replaced = wrapper.replace_training_config_argument(
                [str(source_config), "--run-id=demo-run"], generated
            )
            self.assertEqual(replaced[0], str(generated))

    def test_existing_behavioral_cloning_is_not_silently_overridden(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source_config = root / "base.yaml"
            source_config.write_text(
                "behaviors:\n"
                "  BeesRL1v1:\n"
                "    trainer_type: ppo\n"
                "    behavioral_cloning:\n"
                "      demo_path: existing\n",
                encoding="utf-8",
            )
            demo_dir = root / "human-set"
            demo_dir.mkdir()

            with self.assertRaises(SystemExit):
                wrapper.prepare_human_imitation_config(
                    source_config,
                    output_root=root / "store",
                    demo_directory=demo_dir,
                    behavior_name="BeesRL1v1",
                    strength=0.05,
                    steps=500000,
                    batch_size=512,
                )


if __name__ == "__main__":
    unittest.main()
