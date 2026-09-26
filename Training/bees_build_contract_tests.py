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

    def test_build_pins_release_runtime_before_unity_compilation(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Invoke-Build")
        end = source.index("function Invoke-Server", start)
        block = source[start:end]

        package_root = block.index(
            "$packageRoot=Join-Path (Join-Path $BuildsRoot 'Packages') $buildId"
        )
        pin_runtime = block.index(
            "$trainingRuntime=New-ReleaseTrainingRuntime "
            "$python $buildId $sha $trainingRuntimeArchive"
        )
        windows_build = block.index(
            "Invoke-UnityBuild $unity 'BeesCommandLineBuild.BuildWindowsRl'"
        )
        linux_build = block.index(
            "Invoke-UnityBuild $unity 'BeesCommandLineBuild.BuildLinuxRl'"
        )

        self.assertLess(package_root, pin_runtime)
        self.assertLess(pin_runtime, windows_build)
        self.assertLess(pin_runtime, linux_build)
        self.assertEqual(
            block.count(
                "$trainingRuntime=New-ReleaseTrainingRuntime "
                "$python $buildId $sha $trainingRuntimeArchive"
            ),
            1,
        )

    def test_build_rechecks_rl_compatibility_after_unity_before_publish(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Invoke-Build")
        end = source.index("function Invoke-Server", start)
        block = source[start:end]

        linux_build = block.index(
            "Invoke-UnityBuild $unity 'BeesCommandLineBuild.BuildLinuxRl'"
        )
        recheck = block.index(
            "$postBuildFingerprint=Get-TrainingCompatibilityFingerprint $python"
        )
        package = block.index("Package-Build $python $win", recheck)
        save_release = block.index("Save-LatestRelease $release", recheck)

        self.assertLess(linux_build, recheck)
        self.assertLess(recheck, package)
        self.assertLess(recheck, save_release)
        self.assertIn(
            "$postBuildKey -ne $plannedKey",
            block,
        )
        self.assertIn(
            "Refusing to publish a mixed release",
            block,
        )
        self.assertIn(
            "function Get-TrainingCompatibilityFingerprint",
            source,
        )
        self.assertIn(
            "$RunLifecycleScript,'fingerprint'",
            source,
        )

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
        for runtime_file in (
            "start-server.js",
            "server.js",
            "siServerDev.js",
            "serverContracts.js",
            "database.js",
            "gamePersistence.js",
            "outcomeReservations.js",
            "campaignCheckpoint.js",
            "security.js",
            "cachePersistence.js",
            "rlDemonstrationUploads.js",
            "rlTelemetryUploadSecurity.js",
            "rlTelemetryUploads.js",
            "rlModelDistributionSecurity.js",
            "rlModelDistribution.js",
            "trainingControl.js",
            "trainingEnvOptimizer.js",
            "package.json",
            "package-lock.json",
        ):
            self.assertIn(f"'{runtime_file}'", server_hash)
        self.assertNotIn("Get-ChildItem -LiteralPath $ServerRoot -Filter '*.js'", server_hash)
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

        prepare_start = source.index("function Prepare-CentralReleaseRuntime")
        prepare_end = source.index("function Get-CentralFallbackLaunchCommand", prepare_start)
        prepare = source[prepare_start:prepare_end]
        self.assertIn("Install-ReleaseTrainingRuntime", prepare)
        self.assertIn("Ensure-LearnerPython $Config $runtimeRootPath", prepare)
        self.assertIn("launch_command=@($launchCommand)", prepare)
        self.assertIn("Install-AtomicFile $pointerTemp $CentralRuntimePointerPath", prepare)
        self.assertIn(
            "Install-AtomicFile $readyTemp $CentralRuntimeReadyBuildPath",
            prepare,
        )

        central_start = source.index("function Start-CentralAgentIfNeeded")
        central_end = source.index("function Get-EnvironmentArgs", central_start)
        central = source[central_start:central_end]
        self.assertIn(
            "$agent=Join-Path $AssetsRoot 'Training\\bees_training_worker_agent.py'",
            central,
        )
        self.assertIn("'--runtime-ready-file',$CentralRuntimeReadyBuildPath", central)
        self.assertIn("'--runtime-cutover-pointer',$CentralRuntimePointerPath", central)
        self.assertIn("'--runtime-state-file',$CentralRuntimeStatePath", central)
        self.assertIn("runtime_cutover_capable=$true", central)
        self.assertIn("$fallbackCommand", central)
        self.assertNotIn("$agent=Join-Path $runtimeRoot", central)
        self.assertNotIn("Get-TrainingRuntimeSourceHash", central)

        start = source.index("function Invoke-Start")
        invoke_start = source[start:]
        self.assertIn("training_runtime=$release.training_runtime", invoke_start)
        self.assertIn(
            "Prepare-RemoteBootstrap $config $python $release",
            invoke_start,
        )
        self.assertIn(
            "$centralRuntime=Prepare-CentralReleaseRuntime "
            "$config $bootstrapPython $unity $release",
            invoke_start,
        )
        self.assertIn(
            "Start-CentralAgentIfNeeded $config $bootstrapPython "
            "$unity $release $centralRuntime",
            invoke_start,
        )

    def test_build_and_start_prepare_central_runtime_before_release_barrier(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")

        build_start = source.index("function Invoke-Build")
        build_end = source.index("function Invoke-Server", build_start)
        build = source[build_start:build_end]
        prepare = build.index(
            "$centralRuntime=Prepare-CentralReleaseRuntime $config $python $unity $release"
        )
        central = build.index(
            "Start-CentralAgentIfNeeded $config $python $unity $release $centralRuntime"
        )
        stage = build.index("$staged=Stage-Release", central)
        self.assertLess(prepare, central)
        self.assertLess(central, stage)

        start = source.index("function Invoke-Start")
        invoke_start = source[start:]
        prepare = invoke_start.index(
            "$centralRuntime=Prepare-CentralReleaseRuntime "
            "$config $bootstrapPython $unity $release"
        )
        central = invoke_start.index(
            "Start-CentralAgentIfNeeded $config $bootstrapPython "
            "$unity $release $centralRuntime"
        )
        stage = invoke_start.index("$staged=Stage-Release", central)
        self.assertLess(prepare, central)
        self.assertLess(central, stage)

    def test_build_migrates_central_supervisor_before_new_release_identity_exists(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Invoke-Build")
        end = source.index("function Invoke-Server", start)
        block = source[start:end]

        migrate = block.index("$currentRelease=Get-LatestRelease")
        plan = block.index("$plan=New-TrainingRunPlan")
        new_release = block.index("$release=[pscustomobject]", plan)
        self.assertLess(migrate, plan)
        self.assertLess(migrate, new_release)
        self.assertIn(
            "Start-CentralAgentIfNeeded $config $python $unity "
            "$currentRelease $currentCentralRuntime",
            block,
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

        self.assertIn("'package.json'", block)
        self.assertIn("'package-lock.json'", block)
        for non_runtime_file in (
            "run-tests.js",
            "testServerConfig.js",
            "trainingControlCli.js",
            "migrate.js",
            "recover-tables.js",
            "mediaServer.js",
            "schemaMigrations.js",
            "eslint.config.js",
            "app.js",
            "tst.js",
        ):
            self.assertNotIn(f"'{non_runtime_file}'", block)
        self.assertNotIn("AGENTS.md", block)
        self.assertNotIn("docs", block)

    def test_server_runtime_hash_matches_transitive_local_require_graph(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Get-BeesServerRuntimeSourceHash")
        end = source.index("function Get-BeesServerDependencyHash", start)
        block = source[start:end]
        declared = set(re.findall(r"'([^']+\\.js)'", block))

        server_root = ROOT / "BeesServer~"
        discovered = set()
        pending = ["start-server.js", "server.js", "siServerDev.js"]
        while pending:
            name = pending.pop()
            if name in discovered:
                continue
            discovered.add(name)
            text = (server_root / name).read_text(encoding="utf-8")
            for dependency in re.findall(
                r"""require\(\s*['"]\./([^'"]+)['"]\s*\)""",
                text,
            ):
                dependency_name = (
                    dependency if dependency.endswith(".js") else dependency + ".js"
                )
                self.assertTrue(
                    (server_root / dependency_name).is_file(),
                    f"Missing local runtime dependency {dependency_name} required by {name}",
                )
                pending.append(dependency_name)

        self.assertEqual(declared, discovered)

    def test_server_start_reconciles_owned_process_before_endpoint_health(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Start-BeesServerIfNeeded")
        end = source.index("function Get-LatestRelease", start)
        block = source[start:end]

        load_state = block.index("if(Test-Path -LiteralPath $ServerStatePath)")
        prove_owned = block.index("if(Test-ManagedProcessIdentity $managedState)")
        probe_control = block.index("$online=Test-Control $base $AdminToken")
        restart_owned = block.index("elseif($managedOwned)")
        stop_owned = block.index(
            "Stop-ManagedProcessTree $managedState $node 'BeesServer'"
        )

        self.assertLess(load_state, prove_owned)
        self.assertLess(prove_owned, probe_control)
        self.assertLess(probe_control, restart_owned)
        self.assertLess(restart_owned, stop_owned)
        self.assertIn(
            "Managed BeesServer is not accepting the desired control endpoint/token",
            block,
        )
        self.assertIn(
            "The process is not the verified managed BeesServer, so it will not be killed automatically.",
            block,
        )
        self.assertIn("config_hash=$serverConfigHash", block)
        self.assertIn("schema_version=3", block)

    def test_server_launch_config_identity_covers_control_tokens_and_runtime_inputs(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Get-BeesServerLaunchConfigHash")
        end = source.index("function Start-BeesServerIfNeeded", start)
        block = source[start:end]

        for field in (
            "control_url",
            "control_host",
            "control_port",
            "gameplay_port",
            "worker_token_sha256",
            "admin_token_sha256",
            "control_state",
            "artifact_root",
            "log_root",
            "db_host",
            "db_user",
            "db_password_sha256",
            "db_name",
            "require_test_db",
            "disable_background_jobs",
        ):
            self.assertIn(field, block)
        self.assertIn("Get-StringSha256 $WorkerToken", block)
        self.assertIn("Get-StringSha256 $AdminToken", block)
        self.assertNotIn("worker_token=$WorkerToken", block)
        self.assertNotIn("admin_token=$AdminToken", block)

        server_start = source.index("function Start-BeesServerIfNeeded")
        server_end = source.index("function Get-LatestRelease", server_start)
        server = source[server_start:server_end]
        self.assertIn(
            "$managedConfigHash -eq $serverConfigHash",
            server,
        )

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

    def test_status_distinguishes_control_transport_failure_from_render_failure(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Get-StatusFrameLines")
        end = source.index("function Initialize-LiveStatusRegion", start)
        block = source[start:end]

        request = block.index(
            '$s=Invoke-ControlGet "$($Config.controlUrl)/v1/status" $AdminToken'
        )
        offline = block.index("Server: OFFLINE/UNREACHABLE", request)
        render_try = block.index("try {", offline + 1)
        render_error = block.index("Dashboard: RENDER ERROR", render_try)
        responded = block.index("Control endpoint: RESPONDED", render_error)

        self.assertLess(request, offline)
        self.assertLess(offline, render_try)
        self.assertLess(render_try, render_error)
        self.assertLess(render_error, responded)
        self.assertEqual(block.count("Server: OFFLINE/UNREACHABLE"), 1)
        self.assertIn(
            "$d=Get-ObjectPropertyValue $s 'desired'",
            block,
        )
        self.assertIn(
            "$trainerValue=Get-ObjectPropertyValue $s 'trainers'",
            block,
        )
        self.assertNotIn("$s.desired", block)
        self.assertNotIn("$s.trainers", block)
        self.assertNotIn("$d.pending_release", block)
        self.assertNotIn("$d.environment_args", block)

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

    def test_live_gateway_reuses_tailnet_identity_without_duplicate_auth(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Ensure-TailnetIdentity")
        end = source.index("function Start-TailnetGatewayIfNeeded", start)
        block = source[start:end]

        self.assertIn("Test-ManagedProcessIdentity $gatewayState", block)
        self.assertIn("reusing the live gateway state", block)
        self.assertIn("Refusing to start a second tsnet server", block)
        reuse = block.index("reusing the live gateway state")
        authenticate = block.index(
            "Invoke-Checked $bridge @('auth','--state',$state"
        )
        self.assertLess(reuse, authenticate)

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

    def test_managed_process_ownership_is_distinct_from_desired_executable(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        start = source.index("function Stop-ManagedProcessTree")
        end = source.index("function Get-RunningCentralAgentPid", start)
        stop_block = source[start:end]

        self.assertIn(
            "if(-not(Test-ManagedProcessIdentity $State)){",
            stop_block,
        )
        self.assertIn(
            "desired executable changed; safely replacing verified owned process",
            stop_block,
        )
        self.assertNotIn(
            "if(-not(Test-ManagedProcessIdentity $State $ExpectedExecutable)){",
            stop_block,
        )

        central_start = source.index("function Start-CentralAgentIfNeeded")
        central_end = source.index("function Get-EnvironmentArgs", central_start)
        central = source[central_start:central_end]
        self.assertIn("if(Test-ManagedProcessIdentity $existing){", central)
        self.assertIn(
            "(Test-ManagedProcessIdentity $existing $BootstrapPython) -and",
            central,
        )

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
            "Test-ManagedProcessIdentity $existing $BootstrapPython",
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

        launch_start = source.index("function New-CentralLearnerLaunchCommand")
        launch_end = source.index("function Prepare-CentralReleaseRuntime", launch_start)
        launch = source[launch_start:launch_end]
        self.assertIn('"--unity-editor=$Unity"', launch)

        central_start = source.index("function Start-CentralAgentIfNeeded")
        central_end = source.index("function Get-EnvironmentArgs", central_start)
        central = source[central_start:central_end]
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
