"""Focused source-contract tests for the bundled Bees tailnet bootstrap."""

from __future__ import annotations

import json
import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
OPERATOR = ROOT / "bees.ps1"
WINDOWS_TEMPLATE = ROOT / "Training" / "bees_remote_bootstrap.ps1"
LINUX_TEMPLATE = ROOT / "Training" / "bees_remote_bootstrap.sh"
CLUSTER = ROOT / "Training" / "bees.cluster.json"
TAILNET_MAIN = ROOT / "Tools~" / "bees-tailnet-bridge" / "main.go"
PLACEHOLDER = re.compile(r"__BEES_[A-Z0-9_]+__")


class TailnetBootstrapSourceTests(unittest.TestCase):
    def test_operator_script_has_one_command_dispatch_and_unique_functions(self):
        source = OPERATOR.read_text(encoding="utf-8")
        self.assertEqual(source.count("switch($Command)"), 1)

        functions = re.findall(r"(?m)^function\s+([A-Za-z0-9_-]+)", source)
        duplicates = sorted({name for name in functions if functions.count(name) > 1})
        self.assertEqual(duplicates, [])
        self.assertEqual(functions.count("Prepare-RemoteBootstrap"), 1)
        self.assertEqual(functions.count("Start-TailnetGatewayIfNeeded"), 1)

    def test_operator_replaces_every_generated_launcher_placeholder(self):
        operator = OPERATOR.read_text(encoding="utf-8")
        for template_path in (WINDOWS_TEMPLATE, LINUX_TEMPLATE):
            template = template_path.read_text(encoding="utf-8")
            placeholders = sorted(set(PLACEHOLDER.findall(template)))
            self.assertTrue(placeholders, template_path.name)
            missing = [
                placeholder
                for placeholder in placeholders
                if f"'{placeholder}'=" not in operator
            ]
            self.assertEqual(missing, [], template_path.name)

    def test_launchers_are_single_platform_files_not_numbered_slots(self):
        operator = OPERATOR.read_text(encoding="utf-8")
        self.assertIn("'bees-remote-worker.ps1'", operator)
        self.assertIn("'bees-remote-worker.sh'", operator)
        self.assertNotIn("bees-remote-worker-N", operator)
        self.assertNotIn("__BEES_ACTOR_ID__", operator)
        self.assertNotIn("__BEES_ENVS__", operator)

    def test_cluster_uses_embedded_tailnet_transport(self):
        config = json.loads(CLUSTER.read_text(encoding="utf-8"))
        self.assertEqual(config["remoteTransport"], "tailnet")
        self.assertEqual(config["tailnetLearnerName"], "bees-learner")
        self.assertEqual(config["trainingSshUser"], "beestraining")
        self.assertEqual(config["remoteSshTarget"], "192.168.36.3")
        self.assertGreater(config["tailnetSshPort"], 0)
        self.assertGreater(config["tailnetLocalSshPort"], 0)

    def test_dedicated_ssh_user_is_read_only_and_admin_token_is_blocked(self):
        source = OPERATOR.read_text(encoding="utf-8")
        self.assertIn("function Ensure-TrainingSshAccess", source)
        self.assertIn("function Set-TrainingSshFileAcl", source)
        self.assertIn("foreach($path in @($runtimeZip,$WorkerTokenPath,$WanTokenPath))", source)
        self.assertIn("Set-TrainingSshFileAcl $AdminTokenPath $identity $false", source)
        self.assertIn("Ensure-TrainingSshAccess $config", source)
        self.assertIn("[Security.AccessControl.FileSystemRights]::Delete", source)
        self.assertIn("[Security.AccessControl.FileSystemRights]::TakeOwnership", source)

    def test_tailnet_helper_exposes_required_modes(self):
        source = TAILNET_MAIN.read_text(encoding="utf-8")
        for command in ('case "auth":', 'case "serve":', 'case "forward":'):
            self.assertIn(command, source)
        self.assertIn("tailscale.com/tsnet", source)
        self.assertIn("TailscaleIPs()", source)


if __name__ == "__main__":
    unittest.main()
