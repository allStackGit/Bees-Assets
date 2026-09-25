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
MANAGED_WORKER = ROOT / "Training" / "bees_managed_remote_worker.py"
ELASTIC_WORKER = ROOT / "Training" / "bees_elastic_wan_actor_worker.py"
CLUSTER = ROOT / "Training" / "bees.cluster.json"
REMOTE_REQUIREMENTS = ROOT / "Training" / "bees_remote_requirements.txt"
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
        self.assertEqual(functions.count("Ensure-TailnetIdentity"), 1)

    def test_operator_replaces_every_generated_launcher_placeholder(self):
        operator = OPERATOR.read_text(encoding="utf-8")
        for template_path in (WINDOWS_TEMPLATE, LINUX_TEMPLATE):
            template = template_path.read_text(encoding="utf-8")
            placeholders = sorted(set(PLACEHOLDER.findall(template)))
            self.assertTrue(placeholders, template_path.name)
            missing = [p for p in placeholders if f"'{p}'=" not in operator]
            self.assertEqual(missing, [], template_path.name)

    def test_launchers_are_single_platform_files_not_numbered_slots(self):
        operator = OPERATOR.read_text(encoding="utf-8")
        self.assertIn("'bees-remote-worker.ps1'", operator)
        self.assertIn("'bees-remote-worker.sh'", operator)
        self.assertNotIn("bees-remote-worker-N", operator)
        self.assertNotIn("__BEES_ACTOR_ID__", operator)
        self.assertNotIn("__BEES_ENVS__", operator)

    def test_linux_launcher_uses_real_lf_characters_not_literal_backslash_n(self):
        operator = OPERATOR.read_text(encoding="utf-8")
        self.assertIn('$linuxBody=$linuxBody.Replace("`r`n","`n").Replace("`r","`n")', operator)
        self.assertIn('$linuxWrapper=$linuxWrapper.Replace("`r`n","`n").Replace("`r","`n")', operator)
        self.assertIn('StartsWith("#!/usr/bin/env bash`n")', operator)
        self.assertNotIn('[regex]::Replace($linuxWrapper,"\\r\\n","\\n")', operator)
        self.assertNotIn('[regex]::Replace($linuxBody,"\\r\\n","\\n")', operator)

    def test_central_agent_restart_requests_checkpoint_before_force_kill(self):
        operator = OPERATOR.read_text(encoding="utf-8")
        self.assertIn("function Stop-CentralAgentGracefully", operator)
        self.assertIn("'--shutdown-request-file',$CentralAgentShutdownRequestPath", operator)
        self.assertIn("graceful_checkpoint_shutdown=$true", operator)
        self.assertIn("checkpointing before restarting the managed central agent", operator)
        self.assertNotIn("Stop-ProcessTree ([int]$existing.pid)", operator)
        self.assertIn("AddSeconds(180)", operator)
        self.assertIn("Refusing to stop BeesServer while checkpoint/log preservation is incomplete.", operator)
        self.assertIn("Refusing to stop BeesServer because checkpoint completion cannot be coordinated.", operator)

    def test_remote_runtime_pins_pkg_resources_provider_and_validates_imports(self):
        requirements = REMOTE_REQUIREMENTS.read_text(encoding="utf-8")
        windows = WINDOWS_TEMPLATE.read_text(encoding="utf-8")
        linux = LINUX_TEMPLATE.read_text(encoding="utf-8")
        managed = MANAGED_WORKER.read_text(encoding="utf-8")

        self.assertIn("setuptools>=41,<82", requirements)
        for source in (windows, linux, managed):
            self.assertIn("pkg_resources", source)
        self.assertIn("Remote Python dependency validation failed", windows)
        self.assertIn("remote Python dependency validation failed", linux)
        self.assertIn("_python_remote_dependencies_ok", managed)

    def test_remote_launcher_describes_cpu_and_ram_environment_default(self):
        windows = WINDOWS_TEMPLATE.read_text(encoding="utf-8")
        linux = LINUX_TEMPLATE.read_text(encoding="utf-8")
        expected = "environment count defaults automatically from available CPU and RAM (maximum 64)."
        stale = "environment count defaults to 4x available CPU threads (maximum 64)."
        for source in (windows, linux):
            self.assertIn(expected, source)
            self.assertNotIn(stale, source)

    def test_remote_launchers_detach_supervisor_and_expose_graceful_stop(self):
        windows = WINDOWS_TEMPLATE.read_text(encoding="utf-8")
        linux = LINUX_TEMPLATE.read_text(encoding="utf-8")
        managed = MANAGED_WORKER.read_text(encoding="utf-8")

        self.assertIn("[ValidateSet('start','stop')]", windows)
        self.assertIn("Start-Process -FilePath $venvPython", windows)
        self.assertIn("remote-worker.pid", windows)
        self.assertIn("remote-worker.stop", windows)
        self.assertNotIn("Ctrl+C stops this worker.", windows)

        self.assertIn('COMMAND="start"', linux)
        self.assertIn('nohup "$VENV_PYTHON" -u', linux)
        self.assertIn("remote-worker.pid", linux)
        self.assertIn("remote-worker.stop", linux)
        self.assertNotIn("Ctrl+C stops this worker.", linux)

        self.assertIn('REMOTE_STOP_REQUEST_FILE = "remote-worker.stop"', managed)
        self.assertIn("_watch_shutdown_request", managed)

    def test_cluster_uses_tailnet_without_ssh_settings(self):
        config = json.loads(CLUSTER.read_text(encoding="utf-8"))
        self.assertEqual(config["remoteTransport"], "tailnet")
        self.assertEqual(config["tailnetLearnerName"], "bees-learner")
        self.assertEqual(config["tailnetBootstrapPort"], 7151)
        for obsolete in (
            "trainingSshUser",
            "remoteSshTarget",
            "remoteSshPort",
            "tailnetSshPort",
            "tailnetLocalSshPort",
        ):
            self.assertNotIn(obsolete, config)

    def test_training_path_contains_no_ssh_or_scp_dependency(self):
        for path in (
            OPERATOR,
            WINDOWS_TEMPLATE,
            LINUX_TEMPLATE,
            MANAGED_WORKER,
            ELASTIC_WORKER,
        ):
            source = path.read_text(encoding="utf-8").lower()
            self.assertNotIn("openssh", source, path.name)
            self.assertNotIn("scp", source, path.name)
            self.assertNotIn("--ssh", source, path.name)
            self.assertNotIn("ssh_command(", source, path.name)
            self.assertNotIn("shutil.which(\"ssh\")", source, path.name)

    def test_operator_server_uses_regular_game_development_port(self):
        source = OPERATOR.read_text(encoding="utf-8")
        config = (ROOT / "Scripts" / "ConfigData.cs").read_text(encoding="utf-8")
        self.assertIn("$GameplayServerPort=7146", source)
        self.assertIn("DevelopmentPort = 7146", config)
        self.assertIn("([string]$GameplayServerPort)", source)

    def test_generated_workers_self_update_runtime_and_gate_release_readiness(self):
        operator = OPERATOR.read_text(encoding="utf-8")
        worker = MANAGED_WORKER.read_text(encoding="utf-8")
        windows = WINDOWS_TEMPLATE.read_text(encoding="utf-8")
        linux = LINUX_TEMPLATE.read_text(encoding="utf-8")
        self.assertIn("runtime-ready-build.txt", worker)
        self.assertIn("--runtime-ready-file", worker)
        self.assertIn("latest-training-release.json", worker)
        self.assertIn("--runtime-archive", windows)
        self.assertIn("--bootstrap-token-file", windows)
        self.assertIn("--runtime-archive", linux)
        self.assertIn("--bootstrap-token-file", linux)
        self.assertIn("'--release',$LatestReleasePath", operator)
        self.assertIn("'--windows-bridge',[string]$bridges.distribution_windows", operator)
        self.assertIn("'--linux-bridge',[string]$bridges.distribution_linux", operator)

    def test_tailnet_helper_exposes_private_gateway_bootstrap_and_multi_forward(self):
        source = TAILNET_MAIN.read_text(encoding="utf-8")
        for command in ('case "auth":', 'case "gateway":', 'case "fetch":', 'case "forward-multi":'):
            self.assertIn(command, source)
        self.assertIn("tailscale.com/tsnet", source)
        self.assertIn("TailscaleIPs()", source)
        self.assertIn('r.URL.Path != "/bootstrap"', source)
        self.assertIn("ConstantTimeCompare", source)


if __name__ == "__main__":
    unittest.main()
