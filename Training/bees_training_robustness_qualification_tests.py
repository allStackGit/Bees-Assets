"""Focused tests for the distributed-training robustness qualification gate."""

from __future__ import annotations

from pathlib import Path
import tempfile
import unittest
from unittest import mock

import bees_training_robustness_qualification as qualification


class RobustnessQualificationTests(unittest.TestCase):
    def test_focused_suite_covers_control_runtime_bootstrap_and_wan_layers(self):
        expected = {
            "bees_build_contract_tests.py",
            "bees_release_runtime_tests.py",
            "bees_bootstrap_bundle_tests.py",
            "bees_run_lifecycle_tests.py",
            "bees_archive_training_run_tests.py",
            "bees_training_control_tests.py",
            "bees_managed_remote_worker_tests.py",
            "bees_distributed_training_tests.py",
            "bees_elastic_wan_training_tests.py",
            "bees_elastic_wan_slot_safety_tests.py",
            "bees_elastic_wan_zero_local_tests.py",
            "bees_wan_actor_training_tests.py",
            "bees_continual_service_tests.py",
            "bees_continual_elastic_wan_service_tests.py",
        }
        self.assertEqual(set(qualification.FOCUSED_PYTHON_SUITES), expected)

    def test_full_python_never_recursively_runs_qualification_test_itself(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            for name in (
                "a_tests.py",
                "b_tests.py",
                "bees_training_robustness_qualification_tests.py",
            ):
                (root / name).write_text("# test\n", encoding="utf-8")

            self.assertEqual(
                qualification._python_suites(root, True),
                ("a_tests.py", "b_tests.py"),
            )

    def test_missing_optional_go_is_a_skip_not_a_false_pass_for_required_node(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            bees_root = Path(temp_dir)
            assets = bees_root / "Assets"
            training = assets / "Training"
            server = assets / "BeesServer~"
            bridge = assets / "Tools~" / "bees-tailnet-bridge"
            training.mkdir(parents=True)
            server.mkdir()
            bridge.mkdir(parents=True)
            for name in qualification.FOCUSED_PYTHON_SUITES:
                (training / name).write_text("# placeholder\n", encoding="utf-8")

            with (
                mock.patch.object(qualification.shutil, "which", return_value=None),
                mock.patch.object(qualification, "_resolve_go", return_value=None),
            ):
                checks = qualification.build_checks(
                    bees_root=bees_root,
                    assets_root=assets,
                    full_python=False,
                    skip_node=False,
                    skip_go=False,
                )

            node = next(check for check in checks if check.name == "node:training-control")
            go = next(check for check in checks if check.name == "go:tailnet-bridge")
            self.assertTrue(node.required)
            self.assertEqual(node.command, ())
            self.assertFalse(go.required)
            self.assertEqual(go.command, ())

    def test_node_qualification_includes_environment_optimizer_suite(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            bees_root = Path(temp_dir)
            assets = bees_root / "Assets"
            training = assets / "Training"
            server = assets / "BeesServer~"
            bridge = assets / "Tools~" / "bees-tailnet-bridge"
            training.mkdir(parents=True)
            (server / "test").mkdir(parents=True)
            bridge.mkdir(parents=True)
            for name in qualification.FOCUSED_PYTHON_SUITES:
                (training / name).write_text("# placeholder\n", encoding="utf-8")

            with (
                mock.patch.object(
                    qualification.shutil,
                    "which",
                    side_effect=lambda name: "/usr/bin/node" if name == "node" else None,
                ),
                mock.patch.object(qualification, "_resolve_go", return_value=None),
            ):
                checks = qualification.build_checks(
                    bees_root=bees_root,
                    assets_root=assets,
                    full_python=False,
                    skip_node=False,
                    skip_go=True,
                )

            node = next(check for check in checks if check.name == "node:training-control")
            command = " ".join(node.command)
            self.assertIn("trainingControl.module.test.js", command)
            self.assertIn("trainingControlCli.module.test.js", command)
            self.assertIn("trainingEnvOptimizer.module.test.js", command)

    def test_run_check_propagates_nonzero_exit(self):
        check = qualification.Check(
            name="example",
            command=("python", "test.py"),
            cwd=Path("."),
        )
        completed = mock.Mock(returncode=7)
        with mock.patch.object(
            qualification.subprocess,
            "run",
            return_value=completed,
        ):
            ok, _elapsed = qualification._run_check(check)
        self.assertFalse(ok)


if __name__ == "__main__":
    unittest.main()
