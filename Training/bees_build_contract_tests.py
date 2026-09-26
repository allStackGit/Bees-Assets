"""Focused source-contract tests for command-line Unity training builds."""

from __future__ import annotations

import re
import shutil
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
BUILD_SCRIPT = ROOT / "Editor" / "BeesCommandLineBuild.cs"
OPERATOR_SCRIPT = ROOT / "bees.ps1"
REMOTE_BOOTSTRAP_SCRIPT = ROOT / "Training" / "bees_remote_bootstrap.ps1"
TRAINING_WORKER_AGENT = ROOT / "Training" / "bees_training_worker_agent.py"


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

    def test_build_preflight_distinguishes_live_unity_from_stale_lock(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Get-UnityProcessesForProject")
        end = source.index("function Get-UnityBuildProgressStatus", start)
        block = source[start:end]

        self.assertIn("Get-CimInstance Win32_Process", block)
        self.assertIn("Name = 'Unity.exe'", block)
        self.assertIn("if(-not(Test-Path -LiteralPath $lock)){ return }", block)
        self.assertIn("Removed stale Unity lock file because no Unity Editor process is running", block)
        self.assertIn("Refusing to remove the lock automatically", block)
        self.assertNotIn("appears to already be open in the Unity Editor", block)

    def test_build_preflights_unity_before_archive_or_destructive_build_reset(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Invoke-Build")
        end = source.index("function Invoke-Server", start)
        block = source[start:end]

        preflight = block.index("Assert-UnityProjectAvailableForBatchBuild")
        archive = block.index("Archive-TrainingRun")
        reset = block.index("Reset-BuildDirectory $win")
        self.assertLess(preflight, archive)
        self.assertLess(preflight, reset)

    def test_remote_worker_reports_runtime_preparation_blocker(self):
        source = TRAINING_WORKER_AGENT.read_text(encoding="utf-8")
        self.assertIn(
            "Unity artifact is prepared for ",
            source,
        )
        self.assertIn(
            "but the remote Python runtime ",
            source,
        )
        self.assertIn(
            "runtime_ready_build or '(none)'",
            source,
        )

    def test_operator_hashes_actual_server_bytes_and_pins_training_runtime_release(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("Get-GitTreeSha", source)
        self.assertIn(
            "$serverSourceHash=Get-BeesServerRuntimeSourceHash",
            source,
        )
        self.assertIn("function Get-BeesServerRuntimeSourceHash", source)
        server_hash_start = source.index("function Get-BeesServerRuntimeSourceHash")
        server_hash_end = source.index("function Get-BeesServerDependencyHash", server_hash_start)
        server_hash = source[server_hash_start:server_hash_end]
        self.assertIn("Get-ChildItem -LiteralPath $ServerRoot -Filter '*.js' -File", server_hash)
        self.assertIn("@('package.json','package-lock.json')", server_hash)
        self.assertNotIn("docs", server_hash)
        self.assertNotIn("test\\", server_hash)
        self.assertNotIn("Get-WorkingTreeContentSha256", source)

        self.assertIn(
            "$ReleaseRuntimeScript=Join-Path $AssetsRoot "
            "'Training\\bees_release_runtime.py'",
            source,
        )
        self.assertIn(
            "$trainingRuntime=New-ReleaseTrainingRuntime "
            "$python $buildId $sha $trainingRuntimeArchive",
            source,
        )
        self.assertIn("schema_version=3", source)
        self.assertIn("training_runtime=$trainingRuntime", source)
        self.assertIn("function Resolve-ReleaseTrainingRuntime", source)
        self.assertIn("'--expected-sha256',$archiveSha", source)
        self.assertIn("'--expected-version',$runtimeVersion", source)
        self.assertIn(
            "Copy-Item -LiteralPath $releaseRuntimeArchive "
            "-Destination $runtimeZipTemp",
            source,
        )
        self.assertNotIn("remote-runtime-staging", source)
        self.assertNotIn("Get-TrainingRuntimeSourceHash", source)
        self.assertNotIn("Get-DirectoryContentSha256", source)

        central_start = source.index("function Start-CentralAgentIfNeeded")
        central_end = source.index("function Get-EnvironmentArgs", central_start)
        central = source[central_start:central_end]
        self.assertIn("Install-ReleaseTrainingRuntime", central)
        self.assertIn("$agent=Join-Path $runtimeRoot", central)
        self.assertIn("$service=Join-Path $runtimeRoot", central)
        self.assertIn('"--runtime-training-root=$runtimeRoot"', central)
        self.assertIn("$runtimeVersion", central)
        self.assertNotIn("Get-TrainingRuntimeSourceHash", central)

        start = source.index("function Invoke-Start")
        invoke_start = source[start:]
        self.assertIn("training_runtime=$release.training_runtime", invoke_start)
        self.assertIn(
            "Prepare-RemoteBootstrap $config $python $release",
            invoke_start,
        )
        self.assertIn(
            "Start-CentralAgentIfNeeded $config $python $unity $release",
            invoke_start,
        )

    def test_learner_python_is_isolated_by_release_requirements_identity(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Ensure-LearnerPython")
        end = source.index("function Resolve-Node", start)
        block = source[start:end]

        self.assertIn("$RequirementsRoot", block)
        self.assertIn("bees_learner_requirements.txt", block)
        self.assertIn("bees_remote_requirements.txt", block)
        self.assertIn("$venvRoot=Join-Path $venvBase $requirementsHash", block)
        self.assertIn(
            "Installing central learner dependencies for runtime $requirementsHash",
            block,
        )
        self.assertNotIn("$venvRoot=Join-Path $RuntimeRoot 'LearnerPython'\n", block)

    def test_operator_script_parses_when_powershell_is_available(self):
        executable = shutil.which("powershell.exe") or shutil.which("pwsh")
        if executable is None:
            self.skipTest("PowerShell is not available in this test environment")

        escaped = str(OPERATOR_SCRIPT.resolve()).replace("'", "''")
        command = (
            "$errors=$null;$tokens=$null;"
            "[System.Management.Automation.Language.Parser]::ParseFile("
            f"'{escaped}',[ref]$tokens,[ref]$errors)|Out-Null;"
            "if($errors.Count -gt 0){"
            "$errors|ForEach-Object{Write-Error $_.Message};exit 1"
            "}"
        )
        completed = subprocess.run(
            [executable, "-NoLogo", "-NoProfile", "-Command", command],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(
            completed.returncode,
            0,
            completed.stderr + completed.stdout,
        )

    def test_non_runtime_server_edits_do_not_participate_in_restart_identity(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Get-BeesServerRuntimeSourceHash")
        end = source.index("function Get-BeesServerDependencyHash", start)
        block = source[start:end]

        self.assertIn("-Filter '*.js' -File", block)
        self.assertIn("'package.json'", block)
        self.assertIn("'package-lock.json'", block)
        self.assertNotIn("AGENTS.md", block)
        self.assertNotIn("docs", block)
        self.assertNotIn("Training_CONTROL", block)

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
        self.assertIn("Rollout blockers:", block)
        self.assertIn("'prepared_build_id'", block)
        self.assertIn("missing/no heartbeat", block)
        self.assertIn("not prepared", block)
        self.assertIn(
            "$beeAimSamples=Get-ObjectPropertyValue $m 'bee_aim_samples'",
            block,
        )
        self.assertIn(
            "$humanAimSamples=Get-ObjectPropertyValue $m 'human_aim_samples'",
            block,
        )
        self.assertNotIn("$m.bee_aim_samples", block)
        self.assertNotIn("$m.human_aim_samples", block)
        self.assertIn(
            "$cap=Get-ObjectPropertyValue $record 'worker_capacity'",
            block,
        )
        self.assertIn(
            "$opt=Get-ObjectPropertyValue $record 'env_optimizer'",
            block,
        )
        self.assertIn(
            "$currentEnvs=Get-ObjectPropertyValue $cap 'current_envs'",
            block,
        )
        self.assertIn(
            "$measuredSps=Get-ObjectPropertyValue $opt 'measured_sps'",
            block,
        )
        self.assertNotIn("$cap.current_envs", block)
        self.assertNotIn("$opt.measured_sps", block)

    def test_idempotent_start_keeps_healthy_tailnet_gateway_running(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Start-TailnetGatewayIfNeeded")
        end = source.index("function Invoke-Checked", start)
        block = source[start:end]

        self.assertIn("$gatewayConfigHash=Get-StringSha256", block)
        self.assertIn(
            "$recordedConfigHash -eq $gatewayConfigHash",
            block,
        )
        self.assertIn("Embedded tailnet gateway already healthy", block)
        self.assertIn("return", block)
        self.assertIn("config_hash=$gatewayConfigHash", block)
        self.assertIn(
            "The gateway reads runtime/release/worker/WAN payload files "
            "for every bootstrap request",
            block,
        )
        keep = block.index("$recordedConfigHash -eq $gatewayConfigHash")
        stop = block.index(
            "Stop-ManagedProcessTree $gatewayState $bridge "
            "'embedded tailnet gateway'"
        )
        self.assertLess(keep, stop)

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


    def test_central_launch_quotes_spaced_equals_option_values(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Quote-Arg")
        end = source.index("function Get-StringSha256", start)
        block = source[start:end]

        self.assertIn("$equals=$Value.IndexOf('=')", block)
        self.assertIn("$Value.StartsWith('--')", block)
        self.assertIn("$name=$Value.Substring(0,$equals + 1)", block)
        self.assertIn("$argumentValue=$Value.Substring($equals + 1)", block)
        self.assertIn("return $name + '\"' + $argumentValue + '\"'", block)

        central_start = source.index("function Start-CentralAgentIfNeeded")
        central_end = source.index("function Get-EnvironmentArgs", central_start)
        central = source[central_start:central_end]
        self.assertIn('"--unity-editor=$Unity"', central)
        self.assertIn("$args|ForEach-Object{Quote-Arg ([string]$_)}", central)

    def test_release_wait_reports_live_progress_and_rejects_identity_drift(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Wait-ReleaseRollout")
        end = source.index("function Invoke-Build", start)
        block = source[start:end]

        self.assertIn("Waiting for release rollout: phase=", block)
        self.assertIn("Release rollout complete: build=", block)
        self.assertIn("A different release became pending while waiting", block)
        self.assertIn("Release rollout ended without activating the expected identity", block)
        self.assertIn("$trainerRecords=@($status.trainers)", block)
        self.assertIn("$prepared=[string](Get-ObjectPropertyValue $r 'prepared_build_id')", block)
        self.assertIn("$stale=[bool](Get-ObjectPropertyValue $r 'stale')", block)
        self.assertIn("($now - $lastProgressAt).TotalSeconds -ge 10", block)
        self.assertIn("Central learner failed while rolling release", block)
        self.assertIn("central-agent.err.log", block)
        self.assertIn("central-agent.out.log", block)
        self.assertIn("${BuildId}: $centralError", block)
        self.assertNotIn("$BuildId: $centralError", block)

    def test_forced_new_run_waits_for_matching_compatible_pending_release(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Invoke-Start")
        invoke_start = source[start:]

        self.assertIn(
            "Latest compatible release is still rolling out",
            invoke_start,
        )
        self.assertIn(
            "$status=Wait-ReleaseRollout $config $admin $pendingBuild $pendingRun $pendingKey",
            invoke_start,
        )
        self.assertIn(
            "if(-not $pendingIncompatible -and",
            invoke_start,
        )
        self.assertIn(
            "$pendingBuild -eq $latestBuild",
            invoke_start,
        )
        self.assertIn(
            "$pendingRun -eq $latestRun",
            invoke_start,
        )
        self.assertIn(
            "$pendingKey -eq $latestKey",
            invoke_start,
        )
        self.assertIn(
            "Cannot force a new training run while a different or incompatible release rollout is pending",
            invoke_start,
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
