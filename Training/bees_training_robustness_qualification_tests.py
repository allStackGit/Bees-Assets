"""Focused tests for the distributed-training robustness qualification gate."""

from __future__ import annotations

from pathlib import Path
import tempfile
import unittest
from unittest import mock

import bees_training_robustness_qualification as qualification


def _write_operator_placeholders(training: Path) -> None:
    (training / "bees_operator.js").write_text("'use strict';\n", encoding="utf-8")
    operator = training / "operator"
    operator.mkdir()
    (operator / "common.js").write_text("'use strict';\n", encoding="utf-8")
    (operator / "commands.js").write_text("'use strict';\n", encoding="utf-8")


class RobustnessQualificationTests(unittest.TestCase):
    def test_focused_suite_covers_control_runtime_bootstrap_and_wan_layers(self):
        expected = {
            "bees_build_contract_tests.py",
            "bees_release_runtime_tests.py",
            "bees_bootstrap_bundle_tests.py",
            "bees_tailnet_bootstrap_tests.py",
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
            "bees_continual_release_tests.py",
            "bees_continual_competency_suite_tests.py",
            "bees_continual_evaluate_tests.py",
            "bees_continual_train_tests.py",
            "bees_mlagents_learn_tests.py",
            "bees_continual_elastic_wan_service_tests.py",
            "bees_training_robustness_qualification_tests.py",
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
            _write_operator_placeholders(training)

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

            operator = next(check for check in checks if check.name == "node:operator-syntax")
            node = next(check for check in checks if check.name == "node:training-control")
            go = next(check for check in checks if check.name == "go:tailnet-bridge")
            self.assertTrue(operator.required)
            self.assertEqual(operator.command, ())
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
            _write_operator_placeholders(training)

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

            syntax_checks = [
                check for check in checks
                if check.name.startswith("node:operator-syntax:")
            ]
            self.assertEqual(len(syntax_checks), 3)
            self.assertTrue(all(check.command[1] == "--check" for check in syntax_checks))
            checked_paths = {Path(check.command[2]).name for check in syntax_checks}
            self.assertEqual(
                checked_paths,
                {"bees_operator.js", "common.js", "commands.js"},
            )

            node = next(check for check in checks if check.name == "node:training-control")
            command = " ".join(node.command)
            self.assertIn("startServerLauncher.module.test.js", command)
            self.assertIn("trainingControl.module.test.js", command)
            self.assertIn("trainingControlCli.module.test.js", command)
            self.assertIn("trainingEnvOptimizer.module.test.js", command)

    def test_operator_js_file_discovery_requires_entrypoint_and_module_directory(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            training = Path(temp_dir)
            with self.assertRaisesRegex(ValueError, "entrypoint"):
                qualification._operator_js_files(training)

            (training / "bees_operator.js").write_text("'use strict';\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "module directory"):
                qualification._operator_js_files(training)

            operator = training / "operator"
            operator.mkdir()
            (operator / "z.js").write_text("'use strict';\n", encoding="utf-8")
            (operator / "a.js").write_text("'use strict';\n", encoding="utf-8")
            self.assertEqual(
                tuple(path.name for path in qualification._operator_js_files(training)),
                ("bees_operator.js", "a.js", "z.js"),
            )

    def test_go_qualification_uses_same_module_resolution_mode_as_bridge_build(self):
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
            _write_operator_placeholders(training)

            with (
                mock.patch.object(qualification.shutil, "which", return_value=None),
                mock.patch.object(qualification, "_resolve_go", return_value="/tool/go"),
            ):
                checks = qualification.build_checks(
                    bees_root=bees_root,
                    assets_root=assets,
                    full_python=False,
                    skip_node=True,
                    skip_go=False,
                    skip_unity=True,
                )

            go = next(check for check in checks if check.name == "go:tailnet-bridge")
            self.assertEqual(go.command, ("/tool/go", "test", "-mod=mod", "./..."))

    def test_unity_qualification_runs_foundation_editmode_and_requires_rl_contract(self):
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

            checks = qualification.build_checks(
                bees_root=bees_root,
                assets_root=assets,
                full_python=False,
                skip_node=True,
                skip_go=True,
                unity_editor="/opt/Unity/Unity",
                skip_unity=False,
            )

            unity = next(
                check for check in checks
                if check.name == "unity:bees-foundation-editmode"
            )
            command = " ".join(unity.command)
            self.assertIn("-testPlatform EditMode", command)
            self.assertIn("-testCategory BeesFoundation", command)
            self.assertEqual(
                unity.required_test_substring,
                qualification.UNITY_REQUIRED_TEST,
            )
            self.assertIsNotNone(unity.result_xml)
            self.assertIsNotNone(unity.diagnostic_log)

    def test_unity_result_validation_requires_specific_passing_rl_contract(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            result = Path(temp_dir) / "results.xml"
            result.write_text(
                '<test-run passed="1" failed="0">'
                '<test-suite>'
                '<test-case '
                'fullname="Bees.Tests.EditMode.RlPolicySchemaContractTests.'
                'ContinualLearningConfigTracksFrozenPolicyAbi" '
                'name="ContinualLearningConfigTracksFrozenPolicyAbi" '
                'result="Passed" />'
                '</test-suite>'
                '</test-run>',
                encoding="utf-8",
            )

            self.assertEqual(
                qualification._validate_unity_results(
                    result,
                    qualification.UNITY_REQUIRED_TEST,
                ),
                (True, ""),
            )

    def test_run_check_surfaces_bounded_diagnostic_log_tail_on_failure(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            log = root / "unity.log"
            result = root / "unity.xml"
            check = qualification.Check(
                name="unity-example",
                command=("unity", "-batchmode"),
                cwd=root,
                result_xml=result,
                diagnostic_log=log,
            )
            completed = mock.Mock(returncode=2)

            def run_and_write_log(*_args, **_kwargs):
                log.write_text("first\nsecond\nthird\n", encoding="utf-8")
                result.write_text(
                    '<test-run passed="1" failed="1">'
                    '<test-case fullname="Bees.Tests.ExampleFailure" result="Failed">'
                    '<failure><message>expected true but was false</message></failure>'
                    '</test-case>'
                    '</test-run>',
                    encoding="utf-8",
                )
                return completed

            with (
                mock.patch.object(
                    qualification.subprocess,
                    "run",
                    side_effect=run_and_write_log,
                ),
                mock.patch("builtins.print") as printer,
            ):
                ok, _elapsed = qualification._run_check(check)

            self.assertFalse(ok)
            output = "\n".join(
                " ".join(str(value) for value in call.args)
                for call in printer.call_args_list
            )
            self.assertIn("failed tests", output)
            self.assertIn("Bees.Tests.ExampleFailure", output)
            self.assertIn("expected true but was false", output)
            self.assertIn("tail of", output)
            self.assertIn("third", output)

    def test_unity_native_access_violation_retries_once(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            result = root / "unity.xml"
            log = root / "unity.log"
            check = qualification.Check(
                name="unity-example",
                command=("unity", "-batchmode"),
                cwd=root,
                result_xml=result,
                required_test_substring=qualification.UNITY_REQUIRED_TEST,
                diagnostic_log=log,
            )

            def run_side_effect(*_args, **_kwargs):
                call_index = run.call_count
                if call_index == 1:
                    log.write_text("native crash\n", encoding="utf-8")
                    return mock.Mock(returncode=0xC0000005)
                result.write_text(
                    '<test-run passed="1" failed="0">'
                    '<test-case '
                    'fullname="Bees.Tests.EditMode.RlPolicySchemaContractTests.'
                    'ContinualLearningConfigTracksFrozenPolicyAbi" '
                    'name="ContinualLearningConfigTracksFrozenPolicyAbi" '
                    'result="Passed" />'
                    '</test-run>',
                    encoding="utf-8",
                )
                return mock.Mock(returncode=0)

            with (
                mock.patch.object(
                    qualification.subprocess,
                    "run",
                    side_effect=run_side_effect,
                ) as run,
                mock.patch("builtins.print") as printer,
            ):
                ok, _elapsed = qualification._run_check(check)

            self.assertTrue(ok)
            self.assertEqual(run.call_count, 2)
            output = "\n".join(
                " ".join(str(value) for value in call.args)
                for call in printer.call_args_list
            )
            self.assertIn("[RETRY]", output)

    def test_unity_native_access_violation_fails_after_one_retry(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            check = qualification.Check(
                name="unity-example",
                command=("unity", "-batchmode"),
                cwd=root,
                result_xml=root / "unity.xml",
                diagnostic_log=root / "unity.log",
            )
            with mock.patch.object(
                qualification.subprocess,
                "run",
                side_effect=[
                    mock.Mock(returncode=0xC0000005),
                    mock.Mock(returncode=0xC0000005),
                ],
            ) as run:
                ok, _elapsed = qualification._run_check(check)

            self.assertFalse(ok)
            self.assertEqual(run.call_count, 2)

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
