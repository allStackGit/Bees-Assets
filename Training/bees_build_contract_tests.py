"""Focused source-contract tests for command-line Unity training builds."""

from __future__ import annotations

import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
BUILD_SCRIPT = ROOT / "Editor" / "BeesCommandLineBuild.cs"
OPERATOR_SCRIPT = ROOT / "bees.ps1"
REMOTE_BOOTSTRAP_SCRIPT = ROOT / "Training" / "bees_remote_bootstrap.ps1"


class BeesCommandLineBuildSourceTests(unittest.TestCase):
    def test_linux_rl_uses_dedicated_server_subtarget(self):
        source = BUILD_SCRIPT.read_text(encoding="utf-8")
        linux = re.search(
            r"public static void BuildLinuxRl\(\).*?\n\s*\}",
            source,
            re.DOTALL,
        )
        self.assertIsNotNone(linux)
        self.assertIn("BuildTarget.StandaloneLinux64", linux.group(0))
        self.assertIn("StandaloneBuildSubtarget.Server", linux.group(0))
        self.assertIn("subtarget = (int)subtarget", source)

    def test_windows_rl_remains_regular_player_subtarget(self):
        source = BUILD_SCRIPT.read_text(encoding="utf-8")
        windows = re.search(
            r"public static void BuildWindowsRl\(\).*?\n\s*\}",
            source,
            re.DOTALL,
        )
        self.assertIsNotNone(windows)
        self.assertIn("BuildTarget.StandaloneWindows64", windows.group(0))
        self.assertNotIn("StandaloneBuildSubtarget.Server", windows.group(0))
        self.assertIn(
            "StandaloneBuildSubtarget subtarget = StandaloneBuildSubtarget.Player",
            source,
        )


    def test_windows_remote_bootstrap_normalizes_single_python_launcher_result(self):
        source = REMOTE_BOOTSTRAP_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("Set-StrictMode -Version Latest", source)
        self.assertIn("$launcher=@(Resolve-PythonLauncher)", source)
        self.assertIn("$launcherExe=$launcher[0]", source)
        self.assertIn("if($launcher.Count -gt 1)", source)
        self.assertNotIn("$launcher=Resolve-PythonLauncher\n", source)

    def test_operator_hashes_actual_server_and_training_runtime_bytes(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("Get-GitTreeSha", source)
        self.assertIn(
            "$serverSourceHash=Get-WorkingTreeContentSha256 'BeesServer~'",
            source,
        )
        self.assertIn(
            "ls-files --cached --others --exclude-standard",
            source,
        )
        self.assertIn("sha256='missing'", source)
        self.assertIn("$trainingSourceHash=Get-TrainingRuntimeSourceHash", source)
        self.assertIn("$runtimeVersion=Get-DirectoryContentSha256 $staging", source)
        self.assertIn(
            "Get-FileHash -LiteralPath $filePath -Algorithm SHA256",
            source,
        )
        self.assertIn(
            "Get-ChildItem -LiteralPath $sourceRoot -Filter '*.py' -File",
            source,
        )

        prepare = source.index("function Prepare-RemoteBootstrap")
        copied_runtime = source.index(
            "Copy-Item -LiteralPath $RemoteRequirementsPath",
            prepare,
        )
        staged_hash = source.index(
            "$runtimeVersion=Get-DirectoryContentSha256 $staging",
            prepare,
        )
        self.assertLess(copied_runtime, staged_hash)

    def test_operator_reinstalls_server_dependencies_when_package_identity_changes(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        self.assertIn(
            "$ServerDependencyStampPath=Join-Path $RuntimeRoot "
            "'bees-server-dependencies.sha256'",
            source,
        )
        self.assertIn("function Get-BeesServerDependencyHash", source)
        self.assertIn("name='package.json'", source)
        self.assertIn("name='package-lock.json'", source)
        self.assertIn(
            "$installedDependencyHash -ne $dependencyHash",
            source,
        )

        start = source.index("function Start-BeesServerIfNeeded")
        remove_stamp = source.index(
            "Remove-Item -LiteralPath $ServerDependencyStampPath",
            start,
        )
        npm_ci = source.index("Invoke-Checked $npm @('ci') $ServerRoot", start)
        write_stamp = source.index(
            "$dependencyHash | Set-Content -LiteralPath "
            "$ServerDependencyStampPath",
            start,
        )
        self.assertLess(remove_stamp, npm_ci)
        self.assertLess(npm_ci, write_stamp)


    def test_operator_never_kills_a_managed_process_by_pid_alone(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("function Stop-ProcessTree([int]$Id)", source)
        self.assertEqual(source.count("& taskkill /PID"), 1)
        self.assertIn("function Stop-ManagedProcessTree", source)
        self.assertIn("function Test-ManagedProcessIdentity", source)
        self.assertIn("process_start_utc", source)
        self.assertIn("executable_path", source)
        self.assertIn("function Get-ObjectPropertyValue", source)
        self.assertIn(
            "$processStartUtc=Get-ObjectPropertyValue $State 'process_start_utc'",
            source,
        )
        self.assertIn(
            "$executablePath=Get-ObjectPropertyValue $State 'executable_path'",
            source,
        )
        self.assertIn(
            "([string]$current.process_start_utc) -ne "
            "([string]$processStartUtc)",
            source,
        )
        self.assertIn("[StringComparison]::OrdinalIgnoreCase", source)
        self.assertIn(
            "The PID may have been reused.",
            source,
        )

        stop_helper = source.index("function Stop-ManagedProcessTree")
        taskkill = source.index("& taskkill /PID")
        self.assertLess(stop_helper, taskkill)

    def test_operator_status_shows_remote_wan_traffic(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        status = re.search(
            r"function Get-StatusFrameLines.*?function Initialize-LiveStatusRegion",
            source,
            re.DOTALL,
        )
        self.assertIsNotNone(status)
        block = status.group(0)
        self.assertIn(
            "Get-ObjectPropertyValue $throughput 'network_sent_bytes_total'",
            block,
        )
        self.assertIn(
            "Get-ObjectPropertyValue $throughput 'network_received_bytes_total'",
            block,
        )
        self.assertIn(
            "Get-ObjectPropertyValue $throughput 'network_mib_per_s'",
            block,
        )
        self.assertIn("SentGiB", block)
        self.assertIn("RecvGiB", block)
        self.assertIn("'MiB/s'", block)
        self.assertIn("/1GB", block)
        self.assertIn("'OptExp/s'", block)
        self.assertIn("LearnerAvgStep/s", block)
        self.assertIn("LearnerLiveStep/s", block)
        self.assertIn("OptExp/s is the last per-worker optimizer consumption sample", block)

    def test_operator_persists_identity_for_every_managed_process_owner(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        self.assertIn(
            "$TailnetGatewayStatePath=Join-Path $TailnetRoot "
            "'gateway-state.json'",
            source,
        )
        self.assertGreaterEqual(source.count("process_start_utc=[string]"), 3)
        self.assertGreaterEqual(source.count("executable_path=[string]"), 3)
        self.assertIn(
            "Stop-ManagedProcessTree $gatewayState $bridge "
            "'embedded tailnet gateway'",
            source,
        )
        self.assertIn(
            "Stop-ManagedProcessTree $managedState $node 'BeesServer'",
            source,
        )
        self.assertIn(
            "Test-ManagedProcessIdentity $existing $Python",
            source,
        )

    def test_legacy_pid_only_state_fails_closed_instead_of_being_killed(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        identity = re.search(
            r"function Test-ManagedProcessIdentity.*?\n\}",
            source,
            re.DOTALL,
        )
        self.assertIsNotNone(identity)
        self.assertNotIn("$State.process_start_utc", identity.group(0))
        self.assertNotIn("$State.executable_path", identity.group(0))
        self.assertIn(
            "Get-ObjectPropertyValue $State 'process_start_utc'",
            identity.group(0),
        )
        self.assertIn(
            "legacy PID-only state and cannot be proven safe to kill automatically",
            source,
        )
        self.assertIn(
            "PID-only ownership cannot exclude PID reuse",
            source,
        )
        self.assertIn(
            "Central learner PID $legacyPid is from legacy PID-only state",
            source,
        )


    def test_forced_new_run_intent_is_persisted_before_server_cutover(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Invoke-Start")
        invoke_start = source[start:]

        forced_plan = invoke_start.index("$forcedPlan=New-TrainingRunPlan")
        save_release = invoke_start.index("Save-LatestRelease $release", forced_plan)
        commit_plan = invoke_start.index("Commit-TrainingRunPlan $python", save_release)
        stage_release = invoke_start.index("$staged=Stage-Release", commit_plan)

        self.assertLess(forced_plan, save_release)
        self.assertLess(save_release, commit_plan)
        self.assertLess(commit_plan, stage_release)
        self.assertIn(
            "Ensure-RunLifecycleMatchesRelease $python $release",
            invoke_start,
        )

    def test_release_wait_requires_build_run_and_compatibility_identity(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Wait-ReleaseRollout")
        end = source.index("function Invoke-Build", start)
        wait = source[start:end]

        self.assertIn("desired.canonical_build_id", wait)
        self.assertIn("desired.run_id", wait)
        self.assertIn("desired.compatibility_key", wait)
        self.assertIn("[string]$RunId", wait)
        self.assertIn("[string]$CompatibilityKey", wait)

    def test_start_can_recover_a_persisted_release_from_its_pending_run_plan(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Ensure-RunLifecycleMatchesRelease")
        end = source.index("function Stage-Release", start)
        recovery = source[start:end]

        self.assertIn("Test-Path -LiteralPath $RunPlanPath", recovery)
        self.assertIn("([string]$plan.run_id).Trim() -eq $releaseRun", recovery)
        self.assertIn(
            "([string]$plan.compatibility_key).Trim().ToLowerInvariant() -eq $releaseKey",
            recovery,
        )
        self.assertIn("Commit-TrainingRunPlan $Python", recovery)


if __name__ == "__main__":
    unittest.main()
