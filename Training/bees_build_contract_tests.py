"""Focused source/launch-contract tests for Bees build and operator tooling."""

from __future__ import annotations

import json
import re
import shutil
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
BUILD_SCRIPT = ROOT / "Editor" / "BeesCommandLineBuild.cs"
OPERATOR_SCRIPT = ROOT / "bees.ps1"
NODE_OPERATOR = ROOT / "Training" / "bees_operator.js"
OPERATOR_ROOT = ROOT / "Training" / "operator"
REMOTE_BOOTSTRAP_SCRIPT = ROOT / "Training" / "bees_remote_bootstrap.ps1"
TRAINING_WORKER_AGENT = ROOT / "Training" / "bees_training_worker_agent.py"
SERVER_ROOT = ROOT / "BeesServer~"


def read_operator(name: str) -> str:
    return (OPERATOR_ROOT / name).read_text(encoding="utf-8")


def node_executable() -> str | None:
    return shutil.which("node") or shutil.which("node.exe")


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

    def test_remote_worker_reports_runtime_preparation_blocker(self):
        source = TRAINING_WORKER_AGENT.read_text(encoding="utf-8")
        self.assertIn("Unity artifact is prepared for ", source)
        self.assertIn("but the remote Python runtime ", source)
        self.assertIn("runtime_ready_build or '(none)'", source)

    def test_powershell_operator_is_only_a_thin_node_shim(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        lines = source.splitlines()
        self.assertLessEqual(len(lines), 100)
        self.assertIn("Training\\bees_operator.js", source)
        self.assertIn("& $node @arguments", source)
        self.assertIn("foreach($value in @($EnvArg))", source)
        self.assertNotRegex(source, r"(?m)^function\s+")
        self.assertNotIn("Start-Process", source)
        self.assertNotIn("Quote-Arg", source)
        self.assertNotIn("Invoke-ControlPost", source)
        self.assertNotIn("Invoke-Build", source)
        self.assertNotIn("Invoke-Start", source)

    def test_powershell_shim_preserves_existing_public_parameter_surface(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        for command in (
            "build",
            "server",
            "start",
            "stop",
            "status",
            "bundle",
            "qualify",
        ):
            self.assertIn(f"'{command}'", source)
        for parameter in (
            "[switch]$FullGame",
            "[switch]$Force",
            "[switch]$NewRun",
            "[string[]]$EnvArg",
            "[switch]$Once",
            "[int]$RefreshSeconds=2",
            "[switch]$Server",
            "[double]$LogPercent=10.0",
            "[string]$RunId",
        ):
            self.assertIn(parameter, source)

    def test_node_operator_exposes_all_public_commands(self):
        source = NODE_OPERATOR.read_text(encoding="utf-8")
        for command in (
            "'build'",
            "'server'",
            "'start'",
            "'stop'",
            "'status'",
            "'bundle'",
            "'qualify'",
        ):
            self.assertIn(command, source)
        self.assertIn("invokeBuild(options)", source)
        self.assertIn("invokeStart(options)", source)
        self.assertIn("invokeStop(options)", source)
        self.assertIn("invokeBundle(options)", source)

    def test_node_operator_files_parse_when_node_is_available(self):
        node = node_executable()
        if not node:
            self.skipTest("node is not available")
        files = [NODE_OPERATOR, *sorted(OPERATOR_ROOT.glob("*.js"))]
        self.assertGreaterEqual(len(files), 10)
        for file in files:
            completed = subprocess.run(
                [node, "--check", str(file)],
                cwd=ROOT,
                capture_output=True,
                text=True,
                timeout=30,
                check=False,
            )
            self.assertEqual(
                completed.returncode,
                0,
                msg=f"{file} failed node --check:\n{completed.stdout}\n{completed.stderr}",
            )

    def test_node_cli_parser_preserves_repeated_environment_arguments(self):
        node = node_executable()
        if not node:
            self.skipTest("node is not available")
        script = (
            "const op=require(process.argv[1]);"
            "process.stdout.write(JSON.stringify(op.parseArgs("
            "['start','--new-run','--env-arg','--rl-map-size-min=32',"
            "'--env-arg','--rl-human-ship-types=Scout,Gunship'])));"
        )
        completed = subprocess.run(
            [node, "-e", script, str(NODE_OPERATOR)],
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )
        self.assertEqual(completed.returncode, 0, msg=completed.stderr)
        parsed = json.loads(completed.stdout)
        self.assertEqual(parsed["command"], "start")
        self.assertTrue(parsed["options"]["newRun"])
        self.assertEqual(
            parsed["options"]["envArgs"],
            [
                "--rl-map-size-min=32",
                "--rl-human-ship-types=Scout,Gunship",
            ],
        )

    def test_central_learner_argv_keeps_spaced_unity_path_as_one_value(self):
        source = read_operator("central.js")
        self.assertIn("'--unity-editor', unity", source)
        self.assertNotIn("'--unity-editor=' + unity", source)
        self.assertNotIn('"--unity-editor="', source)
        self.assertIn("spawn(bootstrapPython, launchArgs", source)
        self.assertIn("argv_transport: 'node-spawn-array-v1'", source)
        self.assertNotIn("launchArgString", source)
        self.assertNotIn("Quote-Arg", source)

        node = node_executable()
        if not node:
            self.skipTest("node is not available")
        central = OPERATOR_ROOT / "central.js"
        spaced = r"C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe"
        script = (
            "const c=require(process.argv[1]);"
            "const a=c.buildCentralLearnerArgv({generationSteps:1,numLocalEnvs:0,"
            "maxRemoteActors:2,minRemoteActors:1,brokerPort:55051},"
            "'C:\\\\Python\\\\python.exe',process.argv[2],'C:\\\\Runtime');"
            "process.stdout.write(JSON.stringify(a));"
        )
        completed = subprocess.run(
            [node, "-e", script, str(central), spaced],
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )
        self.assertEqual(completed.returncode, 0, msg=completed.stderr)
        argv = json.loads(completed.stdout)
        index = argv.index("--unity-editor")
        self.assertEqual(argv[index + 1], spaced)
        self.assertFalse(any(value.startswith("--unity-editor=") for value in argv))

    def test_environment_validation_uses_argv_array_without_powershell_reparse(self):
        source = read_operator("validation.js")
        self.assertIn("spawn(executable, args.map(String)", source)
        self.assertIn("'--rl-validate-options-only'", source)
        self.assertIn("...args", source)
        self.assertNotIn("Start-Process", source)
        self.assertNotIn("ArgumentList", source)
        self.assertNotIn("Quote-Arg", source)

    def test_unity_build_uses_argv_array_without_powershell_reparse(self):
        source = read_operator("build.js")
        self.assertIn("const child = spawn(unity, args", source)
        self.assertIn("'-projectPath', paths.beesRoot", source)
        self.assertNotIn("unityArgumentString", source)
        self.assertNotIn("Start-Process", source)

    def test_build_pins_release_runtime_before_unity_compilation(self):
        source = read_operator("build.js")
        start = source.index("async function invokeBuild")
        block = source[start:]
        pin_runtime = block.index("newReleaseTrainingRuntime(")
        windows_build = block.index("'BeesCommandLineBuild.BuildWindowsRl'")
        linux_build = block.index("'BeesCommandLineBuild.BuildLinuxRl'")
        self.assertLess(pin_runtime, windows_build)
        self.assertLess(pin_runtime, linux_build)

    def test_build_rechecks_rl_compatibility_after_unity_before_publish(self):
        source = read_operator("build.js")
        start = source.index("async function invokeBuild")
        block = source[start:]
        linux_build = block.index("'BeesCommandLineBuild.BuildLinuxRl'")
        fingerprint = block.index("getTrainingCompatibilityFingerprint(python)")
        save_release = block.index("saveLatestRelease(release)")
        self.assertLess(linux_build, fingerprint)
        self.assertLess(fingerprint, save_release)
        self.assertIn("Refusing to publish a mixed release", block)

    def test_build_preflight_runs_before_archiving_or_destructive_reset(self):
        source = read_operator("build.js")
        start = source.index("async function invokeBuild")
        block = source[start:]
        preflight = block.index("assertUnityProjectAvailableForBatchBuild()")
        archive = block.index("archiveTrainingRun(")
        reset = block.index("resetBuildDirectory(windowsBuild")
        self.assertLess(preflight, archive)
        self.assertLess(preflight, reset)

    def test_start_validates_environment_before_creating_forced_run_intent(self):
        source = read_operator("commands.js")
        start = source.index("async function invokeStart")
        end = source.index("async function invokeStop", start)
        block = source[start:end]
        validation = block.index("assertRlEnvironmentArgsValid(")
        forced_plan = block.index("newTrainingRunPlan(python")
        self.assertLess(validation, forced_plan)
        self.assertIn("environmentArgs: envArgs", block)

    def test_forced_new_run_intent_is_durable_before_release_staging(self):
        source = read_operator("commands.js")
        start = source.index("async function invokeStart")
        end = source.index("async function invokeStop", start)
        block = source[start:end]
        forced_plan = block.index("forcedPlan = newTrainingRunPlan")
        save = block.index("saveLatestRelease(release)", forced_plan)
        commit = block.index("commitTrainingRunPlan(python)", save)
        stage = block.index("stageRelease(", commit)
        self.assertLess(forced_plan, save)
        self.assertLess(save, commit)
        self.assertLess(commit, stage)
        self.assertIn("completeForcedNewRunPlan(forcedPlan, release)", block)

    def test_release_wait_requires_build_run_and_compatibility_identity(self):
        source = read_operator("control.js")
        self.assertIn("canonical_build_id", source)
        self.assertIn("desired.run_id", source)
        self.assertIn("desired.compatibility_key", source)
        self.assertIn("pendingBuild !== buildId", source)
        self.assertIn("pendingRun !== runId", source)
        self.assertIn("pendingKey !== compatibilityKey", source)
        self.assertIn("Central learner failed while rolling release", source)

    def test_server_replacement_is_prepared_before_live_cutover(self):
        source = read_operator("server.js")
        start = source.index("async function startBeesServerIfNeeded")
        block = source[start:]
        prepare = block.index("prepareBeesServerRuntime(node)")
        stop = block.index("stopManagedProcessTree(state, node, 'BeesServer')")
        self.assertLess(prepare, stop)
        self.assertIn("restoring previously verified runtime", block)

    def test_server_runtime_hash_matches_transitive_local_require_graph(self):
        source = read_operator("server.js")
        match = re.search(
            r"const SERVER_RUNTIME_FILES = Object\.freeze\(\[(.*?)\]\);",
            source,
            re.DOTALL,
        )
        self.assertIsNotNone(match)
        runtime_files = set(re.findall(r"'([^']+)'", match.group(1)))
        self.assertIn("start-server.js", runtime_files)
        self.assertIn("server.js", runtime_files)
        self.assertIn("package.json", runtime_files)
        self.assertIn("package-lock.json", runtime_files)

        required = set()
        queue = ["start-server.js", "server.js"]
        seen = set()
        require_pattern = re.compile(r"require\(['\"]\./([^'\"]+)['\"]\)")
        while queue:
            name = queue.pop()
            if name in seen:
                continue
            seen.add(name)
            file = SERVER_ROOT / name
            self.assertTrue(file.is_file(), name)
            for target in require_pattern.findall(file.read_text(encoding="utf-8")):
                candidate = target if target.endswith(".js") else target + ".js"
                if (SERVER_ROOT / candidate).is_file():
                    required.add(candidate)
                    queue.append(candidate)

        missing = required - runtime_files
        self.assertFalse(missing, f"server runtime list omits local dependencies: {sorted(missing)}")

    def test_server_runtime_identity_hashes_exact_staged_file_set(self):
        source = read_operator("server.js")
        self.assertIn("getNamedFileSetSha256(runtimeEntries(paths.serverRoot))", source)
        self.assertIn("getNamedFileSetSha256(runtimeEntries(runtimeRoot))", source)
        self.assertIn("actualHash !== sourceHash", source)

    def test_managed_process_stop_requires_persisted_identity_not_pid_alone(self):
        source = read_operator("common.js")
        start = source.index("function stopManagedProcessTree")
        block = source[start:source.index("function readTail", start)]
        self.assertIn("testManagedProcessIdentity(state)", block)
        self.assertIn("getStateReferencedLivePid(state)", block)
        self.assertIn("PID may have been reused", block)
        taskkill = block.index("'taskkill.exe'")
        identity_check = block.index("testManagedProcessIdentity(state)")
        self.assertLess(identity_check, taskkill)

    def test_central_launch_intent_is_durable_before_process_creation(self):
        source = read_operator("central.js")
        start = source.index("async function startCentralAgentIfNeeded")
        block = source[start:]
        write_intent = block.index("writeJsonAtomic(paths.centralAgentStatePath, launchIntent)")
        spawn = block.index("spawn(bootstrapPython, launchArgs")
        active = block.index("status: 'active'", spawn)
        self.assertLess(write_intent, spawn)
        self.assertLess(spawn, active)
        self.assertIn("graceful_checkpoint_shutdown: true", block)

    def test_gateway_launch_intent_is_durable_before_process_creation(self):
        source = read_operator("tailnet.js")
        start = source.index("async function startTailnetGatewayIfNeeded")
        end = source.index("function escapePowerShellSingleQuoted", start)
        block = source[start:end]
        write_intent = block.index("writeJsonAtomic(paths.tailnetGatewayStatePath, launchIntent)")
        spawn = block.index("spawn(bridge, launchArgs")
        active = block.index("status: 'active'", spawn)
        self.assertLess(write_intent, spawn)
        self.assertLess(spawn, active)
        self.assertIn("argv_transport: 'node-spawn-array-v1'", block)

    def test_status_preserves_remote_network_traffic_columns(self):
        source = read_operator("status.js")
        self.assertIn("network_sent_bytes_total", source)
        self.assertIn("network_received_bytes_total", source)
        self.assertIn("network_mib_per_s", source)
        self.assertIn("'SentGiB'", source)
        self.assertIn("'RecvGiB'", source)
        self.assertIn("'MiB/s'", source)

    def test_status_learner_log_scan_is_bounded(self):
        source = read_operator("status.js")
        self.assertIn(".slice(0, 24)", source)
        self.assertIn("readTail(file.full, 1000", source)
        self.assertIn("path.join(paths.trainingRoot, 'trainer-results')", source)

    def test_remote_bootstrap_is_published_as_one_atomic_generation(self):
        source = read_operator("tailnet.js")
        start = source.index("function prepareRemoteBootstrap")
        block = source[start:]
        windows = block.index(
            "atomicReplace(windowsCandidate, path.join(paths.remoteRoot, 'bees-remote-worker.cmd'))"
        )
        linux = block.index(
            "atomicReplace(linuxCandidate, path.join(paths.remoteRoot, 'bees-remote-worker.sh'))"
        )
        bundle = block.index("atomicReplace(bundleCandidate, paths.bootstrapBundlePath)")
        self.assertLess(windows, bundle)
        self.assertLess(linux, bundle)
        self.assertIn("validatePowerShellFile(generated)", block)

    def test_operator_module_split_keeps_large_responsibilities_out_of_powershell(self):
        modules = {
            "server.js": "startBeesServerIfNeeded",
            "central.js": "startCentralAgentIfNeeded",
            "tailnet.js": "startTailnetGatewayIfNeeded",
            "validation.js": "assertRlEnvironmentArgsValid",
            "build.js": "invokeBuild",
            "status.js": "getStatusFrameLines",
            "diagnostics.js": "invokeBundle",
            "commands.js": "invokeStart",
        }
        for filename, symbol in modules.items():
            source = read_operator(filename)
            self.assertIn(symbol, source, filename)

        shim = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        for symbol in modules.values():
            self.assertNotIn(symbol, shim)


    def test_build_preflight_distinguishes_live_unity_from_stale_lock(self):
        source = read_operator("build.js")
        start = source.index("function queryUnityProcesses")
        end = source.index("function getUnityBuildProgressStatus", start)
        block = source[start:end]
        self.assertIn("Get-CimInstance Win32_Process", block)
        self.assertIn("Get-Process -Name \\'Unity\\'", block)
        self.assertIn("if (!exists(lock)) return", block)
        self.assertIn("Removed stale Unity lock file because no Unity Editor process is running", block)
        self.assertIn("Refusing to remove the lock automatically", block)

    def test_build_preflights_unity_before_archive_or_destructive_build_reset(self):
        source = read_operator("build.js")
        start = source.index("async function invokeBuild")
        block = source[start:]
        preflight = block.index("assertUnityProjectAvailableForBatchBuild()")
        archive = block.index("archiveTrainingRun(", preflight)
        reset = block.index("resetBuildDirectory(windowsBuild", preflight)
        self.assertLess(preflight, archive)
        self.assertLess(preflight, reset)

    def test_operator_hashes_actual_server_bytes_and_pins_training_runtime_release(self):
        server = read_operator("server.js")
        build = read_operator("build.js")
        runtime = read_operator("runtime.js")
        central = read_operator("central.js")
        tailnet = read_operator("tailnet.js")

        self.assertIn("getNamedFileSetSha256(runtimeEntries(paths.serverRoot))", server)
        for runtime_file in (
            "start-server.js", "server.js", "siServerDev.js", "serverContracts.js",
            "database.js", "gamePersistence.js", "outcomeReservations.js",
            "campaignCheckpoint.js", "security.js", "cachePersistence.js",
            "rlDemonstrationUploads.js", "rlTelemetryUploadSecurity.js",
            "rlTelemetryUploads.js", "rlModelDistributionSecurity.js",
            "rlModelDistribution.js", "trainingControl.js", "trainingEnvOptimizer.js",
            "package.json", "package-lock.json",
        ):
            self.assertIn(f"'{runtime_file}'", server)
        self.assertNotIn("readdirSync(paths.serverRoot)", server)

        self.assertIn("newReleaseTrainingRuntime(", build)
        self.assertIn("schema_version: 3", build)
        self.assertIn("training_runtime: trainingRuntime", build)
        self.assertIn("'--expected-sha256', archiveSha", runtime)
        self.assertIn("'--expected-version', runtimeVersion", runtime)
        self.assertIn("fs.copyFileSync(releaseRuntimeArchive, runtimeZipTemp)", tailnet)
        self.assertNotIn("remote-runtime-staging", tailnet)

        self.assertIn("installReleaseTrainingRuntime(bootstrapPython, release, true)", central)
        self.assertIn("ensureLearnerPython(config, runtimeRoot)", central)
        self.assertIn("launch_command: launchCommand", central)
        self.assertIn("writeJsonAtomic(paths.centralRuntimePointerPath", central)
        self.assertIn("writeTextAtomic(paths.centralRuntimeReadyBuildPath", central)

    def test_build_and_start_prepare_central_runtime_before_release_barrier(self):
        build = read_operator("build.js")
        start = build.index("async function invokeBuild")
        block = build[start:]
        prepare = block.index("prepareCentralReleaseRuntime(", block.index("const preStageStatus"))
        central = block.index("startCentralAgentIfNeeded(", prepare)
        stage = block.index("stageRelease(", central)
        self.assertLess(prepare, central)
        self.assertLess(central, stage)

        commands = read_operator("commands.js")
        start = commands.index("async function invokeStart")
        end = commands.index("async function invokeStop", start)
        block = commands[start:end]
        prepare = block.index("prepareCentralReleaseRuntime(")
        central = block.index("startCentralAgentIfNeeded(", prepare)
        stage = block.index("stageRelease(", central)
        self.assertLess(prepare, central)
        self.assertLess(central, stage)

    def test_build_reconciles_previous_release_before_new_release_identity_exists(self):
        source = read_operator("build.js")
        start = source.index("async function invokeBuild")
        block = source[start:]
        current = block.index("const currentRelease = getLatestRelease()")
        reconcile = block.index("reconcileLatestReleaseBeforeBuild(", current)
        archive = block.index("archiveTrainingRun(", reconcile)
        plan = block.index("newTrainingRunPlan(python)", archive)
        release = block.index("const release = {", plan)
        self.assertLess(current, reconcile)
        self.assertLess(reconcile, archive)
        self.assertLess(reconcile, plan)
        self.assertLess(reconcile, release)

        helper_start = source.index("async function reconcileLatestReleaseBeforeBuild")
        helper_end = source.index("async function invokeBuild", helper_start)
        helper = source[helper_start:helper_end]
        self.assertIn("ensureRunLifecycleMatchesRelease(python, release)", helper)
        self.assertIn("prepareCentralReleaseRuntime(config, python, unity, release)", helper)
        self.assertIn("startCentralAgentIfNeeded(config, python, unity, release, centralRuntime)", helper)
        self.assertIn("Previous release is still rolling out", helper)
        self.assertIn("waitReleaseRollout(config, adminToken, releaseBuild, releaseRun, releaseKey)", helper)
        self.assertIn("was persisted but is not canonical", helper)
        self.assertIn("prepareRemoteBootstrap(config, python, release)", helper)
        self.assertIn("publishRelease(config, adminToken, release)", helper)

    def test_prebuild_reconciliation_rejects_control_release_identity_drift(self):
        source = read_operator("build.js")
        start = source.index("async function reconcileLatestReleaseBeforeBuild")
        end = source.index("async function invokeBuild", start)
        block = source[start:end]
        self.assertIn("pendingBuild !== releaseBuild", block)
        self.assertIn("pendingRun !== releaseRun", block)
        self.assertIn("pendingKey !== releaseKey", block)
        self.assertIn("Training control has a pending release that differs from latest release metadata", block)
        self.assertIn("Previous release reconciliation returned without making", block)

    def test_learner_python_is_isolated_by_release_requirements_identity(self):
        source = read_operator("runtime.js")
        start = source.index("function ensureLearnerPython")
        end = source.index("function newReleaseTrainingRuntime", start)
        block = source[start:end]
        self.assertIn("bees_learner_requirements.txt", block)
        self.assertIn("bees_remote_requirements.txt", block)
        self.assertIn("const venvRoot = path.join(venvBase, requirementsHash)", block)
        self.assertIn("path.join(paths.runtimeRoot, 'LearnerPython')", block)
        self.assertIn("Installing central learner dependencies for runtime", block)
        self.assertIn("pruneLearnerPythonRuntimes([venvPython])", block)

    def test_operator_exposes_side_effect_free_robustness_qualification(self):
        shim = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        cli = NODE_OPERATOR.read_text(encoding="utf-8")
        commands = read_operator("commands.js")
        start = commands.index("async function invokeQualify")
        block = commands[start:commands.index("module.exports", start)]
        self.assertIn("'qualify'", shim)
        self.assertIn("'qualify'", cli)
        self.assertIn("ensureLearnerPython(config)", block)
        self.assertIn("resolveUnityEditor(config)", block)
        self.assertIn("paths.robustnessQualificationScript", block)
        self.assertIn("'--bees-root', paths.beesRoot", block)
        self.assertIn("'--assets-root', paths.assetsRoot", block)
        self.assertIn("'--unity-editor', unity", block)
        for forbidden in (
            "startBeesServerIfNeeded", "stageRelease(", "setDesiredState(",
            "prepareRemoteBootstrap", "startCentralAgentIfNeeded", "archiveTrainingRun",
        ):
            self.assertNotIn(forbidden, block)

    def test_non_runtime_server_edits_do_not_participate_in_restart_identity(self):
        source = read_operator("server.js")
        start = source.index("const SERVER_RUNTIME_FILES")
        end = source.index("function controlProbeHost", start)
        block = source[start:end]
        self.assertIn("'package.json'", block)
        self.assertIn("'package-lock.json'", block)
        for non_runtime_file in (
            "run-tests.js", "testServerConfig.js", "trainingControlCli.js",
            "migrate.js", "recover-tables.js", "mediaServer.js",
            "schemaMigrations.js", "eslint.config.js", "app.js", "tst.js",
        ):
            self.assertNotIn(f"'{non_runtime_file}'", block)
        self.assertNotIn("AGENTS.md", block)
        self.assertNotIn("docs", block)

    def test_server_start_reconciles_owned_process_before_endpoint_health(self):
        source = read_operator("server.js")
        start = source.index("async function startBeesServerIfNeeded")
        block = source[start:source.index("module.exports", start)]
        load_state = block.index("if (exists(paths.serverStatePath))")
        prove_owned = block.index("if (testManagedProcessIdentity(state))")
        probe_control = block.index("const online = await testControl")
        restart_owned = block.index("} else if (owned)", probe_control)
        stop_owned = block.index("stopManagedProcessTree(state, node, 'BeesServer')")
        self.assertLess(load_state, prove_owned)
        self.assertLess(prove_owned, probe_control)
        self.assertLess(probe_control, restart_owned)
        self.assertLess(restart_owned, stop_owned)
        self.assertIn("Managed BeesServer is not accepting the desired control endpoint/token", block)
        self.assertIn("The process is not the verified managed BeesServer, so it will not be killed automatically.", block)
        self.assertIn("await waitForTcpPortClosed", block)
        self.assertIn("if (await testTcpPortOpen", block)

    def test_server_launch_config_identity_covers_control_tokens_and_runtime_inputs(self):
        source = read_operator("server.js")
        start = source.index("function getBeesServerLaunchConfigHash")
        end = source.index("function writeBeesServerManagedState", start)
        block = source[start:end]
        for field in (
            "control_url", "control_host", "control_port", "gameplay_port",
            "worker_token_sha256", "admin_token_sha256",
            "environment_validation_secret_sha256", "control_state",
            "artifact_root", "log_root", "db_host", "db_user",
            "db_password_sha256", "db_name", "require_test_db",
            "disable_background_jobs",
        ):
            self.assertIn(field, block)
        self.assertIn("sha256Text(workerToken)", block)
        self.assertIn("sha256Text(adminToken)", block)
        self.assertIn("ensureTokenFile(paths.environmentValidationTokenPath)", block)
        self.assertNotIn("worker_token: workerToken", block)
        self.assertNotIn("admin_token: adminToken", block)

        operator = source[source.index("async function startBeesServerIfNeeded"):]
        self.assertIn("String(state.config_hash || '') === configHash", operator)

    def test_training_runtime_retention_preserves_active_staged_and_recent_roots(self):
        source = read_operator("runtime.js")
        self.assertIn("function pruneReleaseTrainingRuntimes(extraRoots = [], keepNewest = 4)", source)
        for state_path in (
            "paths.centralRuntimePointerPath",
            "paths.centralRuntimeStatePath",
            "paths.centralAgentStatePath",
        ):
            self.assertIn(state_path, source)
        self.assertIn("['runtime_root', 'release_runtime_root']", source)
        self.assertIn("keepNewest", source)
        self.assertIn("60 * 60 * 1000", source)

        central = read_operator("central.js")
        self.assertIn("pruneReleaseTrainingRuntimes([runtimeRoot])", central)

    def test_learner_python_retention_preserves_active_staged_and_recent_envs(self):
        source = read_operator("runtime.js")
        self.assertIn("function pruneLearnerPythonRuntimes(extraExecutables = [], keepNewest = 3)", source)
        for state_path in (
            "paths.centralRuntimePointerPath",
            "paths.centralRuntimeStatePath",
            "paths.centralAgentStatePath",
        ):
            self.assertIn(state_path, source)
        self.assertIn("['python_executable', 'learner_python']", source)
        self.assertIn("keepNewest", source)
        self.assertIn("pruneLearnerPythonRuntimes([venvPython])", source)

    def test_server_runtime_retention_never_prunes_active_or_candidate_runtime(self):
        source = read_operator("server.js")
        start = source.index("function pruneBeesServerRuntimes")
        end = source.index("function prepareBeesServerRuntime", start)
        block = source[start:end]
        self.assertIn("keepNewest = 3", block)
        self.assertIn("entry.name.includes('.candidate-')", block)
        self.assertIn("60 * 60 * 1000", block)
        self.assertIn("keep.has(path.resolve(entry.full).toLowerCase())", block)
        operator = source[source.index("async function startBeesServerIfNeeded"):]
        self.assertIn("pruneBeesServerRuntimes([runtimeRoot])", operator)
        self.assertIn("pruneBeesServerRuntimes([previousRuntimeRoot])", operator)

    def test_cached_server_runtime_revalidates_dependency_load(self):
        source = read_operator("server.js")
        start = source.index("function testBeesServerRuntimeLoad")
        end = source.index("function pruneBeesServerRuntimes", start)
        block = source[start:end]
        self.assertIn("runtime.loadLegacyRuntime()", block)
        self.assertIn("result.status === 0", block)
        self.assertIn("testBeesServerRuntimeLoad(node, runtimeRoot)", block)

    def test_server_replacement_is_prepared_before_live_process_cutover(self):
        source = read_operator("server.js")
        start = source.index("async function startBeesServerIfNeeded")
        block = source[start:]
        prepare = block.index("prepareBeesServerRuntime(node)")
        stop = block.index("stopManagedProcessTree(state, node, 'BeesServer')")
        self.assertLess(prepare, stop)

    def test_server_cutover_records_replacement_ownership_before_health_wait(self):
        source = read_operator("server.js")
        start = source.index("async function startBeesServerRuntimeProcess")
        end = source.index("async function startBeesServerIfNeeded", start)
        block = source[start:end]
        identity = block.index("const identity = getProcessIdentity(launchedPid)")
        starting = block.index("writeBeesServerManagedState(", identity)
        deadline = block.index("const deadline = Date.now()", starting)
        healthy = block.index("if (await testControl", deadline)
        active = block.index("'active'", healthy)
        self.assertLess(identity, starting)
        self.assertLess(starting, deadline)
        self.assertLess(deadline, healthy)
        self.assertLess(healthy, active)

        write_start = source.index("function writeBeesServerManagedState")
        write_end = source.index("async function startBeesServerRuntimeProcess", write_start)
        write_block = source[write_start:write_end]
        self.assertIn("schema_version: 5", write_block)
        self.assertIn("writeJsonAtomic(paths.serverStatePath", write_block)
        self.assertIn("writeTextAtomic(paths.serverPidPath", write_block)

    def test_server_cutover_preserves_old_state_until_new_identity_is_durable(self):
        source = read_operator("server.js")
        start = source.index("async function startBeesServerIfNeeded")
        block = source[start:]
        stop = block.index("stopManagedProcessTree(state, node, 'BeesServer')")
        launch = block.index("startBeesServerRuntimeProcess(", stop)
        between = block[stop:launch]
        self.assertNotIn("removeIfExists(paths.serverStatePath)", between)
        self.assertNotIn("removeIfExists(paths.serverPidPath)", between)
        self.assertIn("String(state.status || '') !== 'active'", block)
        self.assertIn("writeBeesServerManagedState(", block)

    def test_failed_server_replacement_restores_previous_verified_runtime(self):
        source = read_operator("server.js")
        start = source.index("async function startBeesServerIfNeeded")
        block = source[start:]
        self.assertIn("Replacement BeesServer failed after cutover; restoring previously verified runtime", block)
        self.assertIn("testBeesServerStagedRuntime(previousRuntimeRoot, previousSourceHash, node)", block)
        self.assertIn("previousConfigHash === configHash", block)
        self.assertIn("previousRuntimeRoot", block)
        self.assertIn("replacementError", block)
        self.assertIn("previous verified runtime was restored successfully", block)

    def test_operator_never_kills_a_managed_process_by_pid_alone(self):
        source = read_operator("common.js")
        start = source.index("function stopManagedProcessTree")
        end = source.index("function readTail", start)
        block = source[start:end]
        self.assertIn("testManagedProcessIdentity(state)", block)
        self.assertIn("getStateReferencedLivePid(state)", block)
        self.assertIn("PID may have been reused", block)
        self.assertNotIn("process.kill(Number(state.pid)", block.split("if (!testManagedProcessIdentity(state))", 1)[1].split("}", 1)[0])

    def test_status_distinguishes_control_transport_failure_from_render_failure(self):
        source = read_operator("status.js")
        start = source.index("async function getStatusFrameLines")
        end = source.index("async function showStatus", start)
        block = source[start:end]
        request = block.index("status = await requestJson")
        offline = block.index("Server: OFFLINE/UNREACHABLE", request)
        render_try = block.index("try {", offline + 1)
        render_error = block.index("Dashboard: RENDER ERROR", render_try)
        responded = block.index("Control endpoint: RESPONDED", render_error)
        self.assertLess(request, offline)
        self.assertLess(offline, render_try)
        self.assertLess(render_try, render_error)
        self.assertLess(render_error, responded)
        self.assertEqual(block.count("Server: OFFLINE/UNREACHABLE"), 1)

    def test_status_learner_log_scan_is_bounded_to_active_run(self):
        source = read_operator("status.js")
        start = source.index("function getLocalLearnerStats")
        end = source.index("function number", start)
        block = source[start:end]
        self.assertIn("if (runId) trainerResultsRoot = path.join(trainerResultsRoot, runId)", block)
        self.assertIn(".slice(0, 24)", block)
        status = source[source.index("async function getStatusFrameLines"):]
        self.assertIn("getLocalLearnerStats(String(desired.run_id || ''))", status)



    def test_start_reports_fresh_rollout_state_after_wait(self):
        source = read_operator("commands.js")
        start = source.index("async function invokeStart")
        end = source.index("async function invokeStop", start)
        block = source[start:end]
        requested = block.index("'Training requested: build='")
        refresh = block.index("const finalStatus = await getStatus(config, admin)", requested)
        self.assertLess(requested, refresh)
        self.assertIn("Release rollout: complete", block)
        self.assertNotIn("staged.pending_release.phase", block[requested:])

    def test_operator_status_shows_remote_wan_traffic(self):
        source = read_operator("status.js")
        for metric in (
            "network_sent_bytes_total",
            "network_received_bytes_total",
            "network_mib_per_s",
        ):
            self.assertIn(metric, source)
        for column in ("SentGiB", "RecvGiB", "MiB/s"):
            self.assertIn(column, source)

    def test_live_gateway_reuses_tailnet_identity_without_duplicate_auth(self):
        source = read_operator("tailnet.js")
        start = source.index("function ensureTailnetIdentity")
        end = source.index("function testTailnetGatewayHealth", start)
        block = source[start:end]
        self.assertIn("testManagedProcessIdentity(gatewayState)", block)
        self.assertIn("reusing the live gateway state", block)
        self.assertIn("Refusing to start a second tsnet server", block)
        reuse = block.index("reusing the live gateway state")
        authenticate = block.index("runChecked(bridge", reuse)
        self.assertLess(reuse, authenticate)

    def test_idempotent_start_keeps_healthy_tailnet_gateway_running(self):
        source = read_operator("tailnet.js")
        start = source.index("async function startTailnetGatewayIfNeeded")
        end = source.index("function escapePowerShellSingleQuoted", start)
        block = source[start:end]
        self.assertIn("const configHash = sha256Text(", block)
        self.assertIn("String(state.config_hash || '') === configHash", block)
        self.assertIn("Embedded tailnet gateway already healthy", block)
        keep = block.index("String(state.config_hash || '') === configHash")
        stop = block.index("stopManagedProcessTree(state, bridge, 'embedded tailnet gateway')")
        self.assertLess(keep, stop)
        self.assertIn("'--bootstrap-bundle', paths.bootstrapBundlePath", block)
        self.assertNotIn("'--runtime'", block)
        self.assertNotIn("'--worker-token'", block)
        self.assertNotIn("'--wan-token'", block)
        self.assertNotIn("'--release'", block)

    def test_build_recovers_crashed_managed_server_without_reviving_intentional_stop(self):
        source = read_operator("build.js")
        start = source.index("async function invokeBuild")
        block = source[start:]
        self.assertIn("const managedServerExists = exists(paths.serverStatePath)", block)
        self.assertGreaterEqual(
            block.count("controlOnline || managedServerExists"),
            2,
        )
        self.assertIn(
            "Managed BeesServer reconciliation completed without a reachable training-control endpoint",
            block,
        )
        post = block.rindex("const managedServerExists = exists(paths.serverStatePath)")
        reconcile = block.index("startBeesServerIfNeeded(config, worker, admin)", post)
        publish = block.index("publishRelease(config, admin, release)", reconcile)
        self.assertLess(reconcile, publish)

    def test_live_build_always_reconciles_private_gateway(self):
        source = read_operator("build.js")
        start = source.index("async function invokeBuild")
        block = source[start:]
        prepare = block.rindex("prepareRemoteBootstrap(config, python, release)")
        reconcile = block.index("startTailnetGatewayIfNeeded(config)", prepare)
        stage = block.index("stageRelease(", reconcile)
        self.assertLess(prepare, reconcile)
        self.assertLess(reconcile, stage)
        between = block[prepare:reconcile]
        self.assertNotIn("if (tailnetBridgeChanged)", between)

    def test_managed_process_ownership_is_distinct_from_desired_executable(self):
        common = read_operator("common.js")
        start = common.index("function stopManagedProcessTree")
        end = common.index("function readTail", start)
        stop_block = common[start:end]
        self.assertIn("if (!testManagedProcessIdentity(state))", stop_block)
        self.assertIn(
            "desired executable changed; safely replacing verified owned process",
            stop_block,
        )
        first_guard = stop_block.index("if (!testManagedProcessIdentity(state))")
        executable_guard = stop_block.index(
            "if (expectedExecutable && !testManagedProcessIdentity(state, expectedExecutable))"
        )
        self.assertLess(first_guard, executable_guard)

        central = read_operator("central.js")
        self.assertIn("if (testManagedProcessIdentity(existing))", central)
        self.assertIn("testManagedProcessIdentity(existing, bootstrapPython)", central)

    def test_operator_persists_identity_for_every_managed_process_owner(self):
        common = read_operator("common.js")
        server = read_operator("server.js")
        central = read_operator("central.js")
        tailnet = read_operator("tailnet.js")
        self.assertIn("process_start_utc: String(value.process_start_utc || '')", common)
        self.assertIn("executable_path: path.resolve(String(value.executable_path))", common)
        self.assertIn("writeJsonAtomic(paths.serverStatePath", server)
        self.assertIn("writeJsonAtomic(paths.centralAgentStatePath", central)
        self.assertIn("writeJsonAtomic(paths.tailnetGatewayStatePath", tailnet)
        self.assertIn("stopManagedProcessTree(state, node, 'BeesServer')", server)
        self.assertIn("stopManagedProcessTree(state, bridge, 'embedded tailnet gateway')", tailnet)

    def test_legacy_pid_only_state_fails_closed_instead_of_being_killed(self):
        common = read_operator("common.js")
        central = read_operator("central.js")
        server = read_operator("server.js")
        tailnet = read_operator("tailnet.js")
        identity = common[
            common.index("function testManagedProcessIdentity"):
            common.index("function getStateReferencedLivePid")
        ]
        self.assertIn("!state.process_start_utc", identity)
        self.assertIn("!state.executable_path", identity)
        self.assertIn("PID may have been reused", common)
        self.assertIn("legacy PID-only state", server)
        self.assertIn("legacy PID-only state", tailnet)
        self.assertIn("legacy PID-only state", central)

    def test_central_launch_quotes_spaced_equals_option_values(self):
        # The old implementation needed bespoke PowerShell quoting. The durable contract is
        # that paths with spaces remain one argument; Node now guarantees this structurally.
        source = read_operator("central.js")
        self.assertIn("'--unity-editor', unity", source)
        self.assertIn("'--unity-project-root', paths.beesRoot", source)
        self.assertIn("spawn(bootstrapPython, launchArgs", source)
        self.assertNotIn("launchArgString", source)
        self.assertNotIn("Quote-Arg", source)

    def test_release_wait_reports_live_progress_and_rejects_identity_drift(self):
        source = read_operator("control.js")
        start = source.index("async function waitReleaseRollout")
        block = source[start:source.index("module.exports", start)]
        self.assertIn("Waiting for release rollout: phase=", block)
        self.assertIn("Release rollout complete: build=", block)
        self.assertIn("A different release became pending while waiting", block)
        self.assertIn("Release rollout ended without activating the expected identity", block)
        self.assertIn("prepared_build_id", source)
        self.assertIn("record.stale", source)
        self.assertIn("now - lastProgressAt >= 10000", block)
        self.assertIn("Central learner failed while rolling release", block)
        self.assertIn("central-agent.err.log", block)
        self.assertIn("central-agent.out.log", block)

    def test_forced_new_run_waits_for_matching_compatible_pending_release(self):
        source = read_operator("commands.js")
        start = source.index("async function invokeStart")
        end = source.index("async function invokeStop", start)
        block = source[start:end]
        self.assertIn("Latest compatible release is still rolling out", block)
        self.assertIn("status = await waitReleaseRollout(", block)
        self.assertIn("!pendingIncompatible", block)
        self.assertIn("pendingBuild === latestBuild", block)
        self.assertIn("pendingRun === latestRun", block)
        self.assertIn("pendingKey === latestKey", block)
        self.assertIn(
            "Cannot force a new training run while a different or incompatible release rollout is pending",
            block,
        )

    def test_forced_new_run_intent_is_persisted_before_server_cutover(self):
        source = read_operator("commands.js")
        start = source.index("async function invokeStart")
        end = source.index("async function invokeStop", start)
        block = source[start:end]
        forced_plan = block.index("forcedPlan = newTrainingRunPlan")
        save_release = block.index("saveLatestRelease(release)", forced_plan)
        commit_plan = block.index("commitTrainingRunPlan(python)", save_release)
        stage_release = block.index("stageRelease(", commit_plan)
        self.assertLess(forced_plan, save_release)
        self.assertLess(save_release, commit_plan)
        self.assertLess(commit_plan, stage_release)
        self.assertIn("ensureRunLifecycleMatchesRelease(python, release)", block)

    def test_forced_new_run_stages_environment_args_atomically(self):
        control = read_operator("control.js")
        stage = control[
            control.index("async function stageRelease"):
            control.index("function rolloutTrainerRecord")
        ]
        self.assertIn("environmentArgs", stage)
        self.assertIn("body.environment_args = [...environmentArgs].map(String)", stage)

        commands = read_operator("commands.js")
        start = commands.index("async function invokeStart")
        end = commands.index("async function invokeStop", start)
        block = commands[start:end]
        forced = block.index("if (performForcedNewRun)")
        forced_stage = block.index("stageRelease(", forced)
        forced_state = block.index("setDesiredState(", forced_stage)
        self.assertNotIn("environment_args", block[forced_state:block.index("} else {", forced_state)])
        ordinary_stage = block.index("stageRelease(", block.index("} else {", forced_state))
        ordinary_state = block.index("setDesiredState(", ordinary_stage)
        self.assertNotIn("environment_args", block[ordinary_state:ordinary_state + 180])
        self.assertIn("waitReleaseRollout(", block[ordinary_stage:])

    def test_environment_args_are_validated_before_any_run_or_control_mutation(self):
        validation = read_operator("validation.js")
        self.assertIn("'--rl-validate-options-only'", validation)
        self.assertIn("waitForExitOrMarker(", validation)
        self.assertIn("60000", validation)
        self.assertIn("Unknown RL training option '--rl-validate-options-only'", validation)
        self.assertIn("RL training command-line validation succeeded:", validation)
        self.assertIn("RL training configuration ", validation)
        self.assertIn("Invalid RL environment arguments", validation)
        self.assertIn("writeTextAtomic(", validation)
        self.assertIn("extractZip(activePython, archivePath, candidate)", validation)
        self.assertIn("removeIfExists(candidate, { recursive: true })", validation)
        self.assertIn("bees-environment-validation-v2", validation)

        server = read_operator("server.js")
        self.assertIn("BEES_TRAINING_ENVIRONMENT_VALIDATION_SECRET", server)

        commands = read_operator("commands.js")
        start = commands.index("async function invokeStart")
        end = commands.index("async function invokeStop", start)
        block = commands[start:end]
        first_validation = block.index("assertRlEnvironmentArgsValid(")
        new_plan = block.index("newTrainingRunPlan(python", first_validation)
        stage = block.index("stageRelease(", first_validation)
        self.assertLess(first_validation, new_plan)
        self.assertLess(first_validation, stage)

    def test_live_build_and_recovery_validate_environment_before_staging(self):
        source = read_operator("build.js")
        reconcile_start = source.index("async function reconcileLatestReleaseBeforeBuild")
        reconcile_end = source.index("async function invokeBuild", reconcile_start)
        reconcile = source[reconcile_start:reconcile_end]
        validate = reconcile.index("assertRlEnvironmentArgsValid(")
        central_runtime = reconcile.index("prepareCentralReleaseRuntime(", validate)
        stage = reconcile.index("stageRelease(", central_runtime)
        self.assertLess(validate, central_runtime)
        self.assertLess(central_runtime, stage)
        self.assertIn("const status = await getStatus", reconcile[central_runtime:])

        build = source[source.index("async function invokeBuild"):]
        validate = build.index("assertRlEnvironmentArgsValid(")
        central_runtime = build.index("prepareCentralReleaseRuntime(", validate)
        stage = build.index("stageRelease(", central_runtime)
        self.assertLess(validate, central_runtime)
        self.assertLess(central_runtime, stage)

    def test_gateway_and_central_launch_intent_is_durable_before_process_creation(self):
        central = read_operator("central.js")
        central_start = central.index("async function startCentralAgentIfNeeded")
        central_block = central[central_start:]
        self.assertLess(
            central_block.index("writeJsonAtomic(paths.centralAgentStatePath, launchIntent)"),
            central_block.index("spawn(bootstrapPython, launchArgs"),
        )

        tailnet = read_operator("tailnet.js")
        gateway_start = tailnet.index("async function startTailnetGatewayIfNeeded")
        gateway_end = tailnet.index("function escapePowerShellSingleQuoted", gateway_start)
        gateway = tailnet[gateway_start:gateway_end]
        self.assertLess(
            gateway.index("writeJsonAtomic(paths.tailnetGatewayStatePath, launchIntent)"),
            gateway.index("spawn(bridge, launchArgs"),
        )

    def test_learner_infrastructure_supervisors_have_recoverable_child_ownership(self):
        tailnet = read_operator("tailnet.js")
        gateway = tailnet[
            tailnet.index("async function startTailnetGatewayIfNeeded"):
            tailnet.index("function escapePowerShellSingleQuoted")
        ]
        self.assertIn("'gateway-supervisor'", gateway)
        self.assertIn("sha256Text('bees-managed-child:' + state.owner_token)", gateway)
        self.assertIn("'orphaned embedded tailnet gateway child'", gateway)

        server = read_operator("server.js")
        launch = server[
            server.index("async function startBeesServerRuntimeProcess"):
            server.index("async function startBeesServerIfNeeded")
        ]
        self.assertIn("'--managed-owner-token', ownerToken", launch)
        self.assertIn("const ownerToken =", launch)

        reconcile = server[server.index("async function startBeesServerIfNeeded"):]
        self.assertIn("findManagedProcessByOwnerToken(node, ownerToken, 'BeesServer supervisor')", reconcile)
        self.assertIn("sha256Text('bees-managed-child:' + ownerToken)", reconcile)
        self.assertIn("'orphaned BeesServer child'", reconcile)

    def test_forced_new_run_operation_is_resumable_until_terminal_archive(self):
        runtime = read_operator("runtime.js")
        plan = runtime[
            runtime.index("function newTrainingRunPlan"):
            runtime.index("function commitTrainingRunPlan")
        ]
        self.assertIn("options.buildId", plan)
        self.assertIn("options.environmentArgs", plan)
        self.assertIn("'--build-id'", plan)
        self.assertIn("'--environment-args-base64'", plan)
        self.assertNotIn("'--environment-args-json'", plan)
        self.assertIn("function getPendingForcedNewRunPlan", runtime)
        self.assertIn("function completeForcedNewRunPlan", runtime)

        build = read_operator("build.js")
        invoke_build = build[build.index("async function invokeBuild"):]
        guard = invoke_build.index("getPendingForcedNewRunPlan()")
        archive = invoke_build.index("archiveTrainingRun(", guard)
        self.assertLess(guard, archive)
        self.assertIn("resume/finalize it before creating another build", invoke_build)

        commands = read_operator("commands.js")
        start = commands.index("async function invokeStart")
        end = commands.index("async function invokeStop", start)
        invoke_start = commands[start:end]
        discover = invoke_start.index("getPendingForcedNewRunPlan()")
        create = invoke_start.index("} else if (options.newRun)", discover)
        self.assertLess(discover, create)
        self.assertIn("envArgs = (forcedPlan.environment_args || []).map(String)", invoke_start)
        self.assertIn("buildId: String(release.build_id)", invoke_start)
        self.assertIn("environmentArgs: envArgs", invoke_start)
        self.assertIn("Boolean(options.newRun || resumeForcedNewRun)", invoke_start)
        final_archive = invoke_start.index("'forced-new-final'")
        complete = invoke_start.index("completeForcedNewRunPlan(forcedPlan, release)", final_archive)
        self.assertLess(final_archive, complete)

    def test_legacy_forced_new_plan_is_recovered_once_instead_of_becoming_a_blocker(self):
        commands = read_operator("commands.js")
        start = commands.index("async function invokeStart")
        end = commands.index("async function invokeStop", start)
        block = commands[start:end]
        self.assertIn("Resuming a legacy forced-new run plan without a persisted build binding", block)
        self.assertIn("Legacy forced-new plan has no persisted environment arguments", block)

        runtime = read_operator("runtime.js")
        complete = runtime[
            runtime.index("function completeForcedNewRunPlan"):
            runtime.index("function archiveTrainingRun")
        ]
        self.assertIn("const buildMatches = !currentBuild ||", complete)

    def test_start_can_recover_a_persisted_release_from_its_pending_run_plan(self):
        source = read_operator("runtime.js")
        start = source.index("function ensureRunLifecycleMatchesRelease")
        end = source.index("function convertReleaseToForcedRunPlan", start)
        block = source[start:end]
        self.assertIn("if (exists(paths.runPlanPath))", block)
        self.assertIn("String(plan.run_id || '').trim() === releaseRun", block)
        self.assertIn(
            "String(plan.compatibility_key || '').trim().toLowerCase() === releaseKey",
            block,
        )
        self.assertIn("commitTrainingRunPlan(python)", block)

    def test_exact_process_start_identity_is_not_truncated_by_javascript_date(self):
        source = read_operator("common.js")
        identity = source[
            source.index("function getProcessIdentity"):
            source.index("function isProcessAlive")
        ]
        compare = source[
            source.index("function testManagedProcessIdentity"):
            source.index("function getStateReferencedLivePid")
        ]
        self.assertIn("process_start_utc: String(value.process_start_utc || '')", identity)
        self.assertIn(
            "String(current.process_start_utc) !== String(state.process_start_utc)",
            compare,
        )
        self.assertNotIn("new Date(", identity)
        self.assertNotIn("normalizeIso", compare)

    def test_server_cutover_waits_for_old_port_and_fails_closed_on_unknown_owner(self):
        source = read_operator("server.js")
        start = source.index("async function startBeesServerIfNeeded")
        block = source[start:]
        stop = block.index("stopManagedProcessTree(state, node, 'BeesServer')")
        wait = block.index("waitForTcpPortClosed(", stop)
        port_guard = block.index("if (await testTcpPortOpen(", wait)
        launch = block.index("startBeesServerRuntimeProcess(", port_guard)
        self.assertLess(stop, wait)
        self.assertLess(wait, port_guard)
        self.assertLess(port_guard, launch)
        self.assertIn("will not be killed automatically", block[port_guard:launch])

    def test_remote_bootstrap_repairs_legacy_bom_before_python_reads_release_json(self):
        source = read_operator("tailnet.js")
        start = source.index("function prepareRemoteBootstrap")
        block = source[start:]
        repair = block.index("removeUtf8BomIfPresent(paths.latestReleasePath)")
        publish = block.index("const bundle = invokePythonJson(", repair)
        self.assertLess(repair, publish)

    def test_portable_go_keeps_curl_and_powershell_download_fallbacks(self):
        source = read_operator("tailnet.js")
        start = source.index("function resolvePortableGo")
        end = source.index("function getTailnetBridgeSourceHash", start)
        block = source[start:end]
        self.assertIn("resolveCommand('curl.exe')", block)
        self.assertIn("Invoke-WebRequest -UseBasicParsing", block)
        self.assertIn("BEES_DOWNLOAD_URL", block)
        self.assertIn("BEES_DOWNLOAD_OUTPUT", block)

    def test_diagnostic_timeout_waits_for_process_exit_before_cleanup(self):
        source = read_operator("diagnostics.js")
        start = source.index("async function waitForExit")
        end = source.index("async function invokeCentralDiagnosticBenchmark", start)
        block = source[start:end]
        kill = block.index("killProcessTree(child)")
        wait = block.index("const killDeadline", kill)
        exit_check = block.index("child.exitCode !== null || child.signalCode !== null", wait)
        self.assertLess(kill, wait)
        self.assertLess(wait, exit_check)

    def test_child_spawn_failures_are_contained_at_node_process_boundaries(self):
        common = read_operator("common.js")
        self.assertIn("function waitForSpawn", common)
        self.assertIn("child.once('error'", common)

        central = read_operator("central.js")
        self.assertIn("await waitForSpawn(child, 'Central training supervisor')", central)

        tailnet = read_operator("tailnet.js")
        self.assertIn("await waitForSpawn(child, 'Embedded tailnet gateway')", tailnet)

        validation = read_operator("validation.js")
        self.assertIn("child.on('error'", validation)
        self.assertIn("child.beesSpawnError", validation)

    def test_validation_only_probe_waits_for_authoritative_exit_before_legacy_marker_fallback(self):
        source = read_operator("validation.js")
        start = source.index("async function assertRlEnvironmentArgsValid")
        block = source[start:]
        wait_call = block.index("let outcome = await waitForExitOrMarker(")
        wait_slice = block[wait_call:wait_call + 180]
        self.assertIn("60000", wait_slice)
        self.assertIn("null", wait_slice)
        success = block.index("RL training command-line validation succeeded:", wait_call)
        legacy = block.index("RL training configuration ", success)
        self.assertLess(success, legacy)


    def test_operator_script_parses_when_powershell_is_available(self):
        powershell = shutil.which("powershell") or shutil.which("pwsh")
        if not powershell:
            self.skipTest("PowerShell is not available")
        command = (
            "$e=$null;"
            f"$null=[System.Management.Automation.Language.Parser]::ParseFile('{str(OPERATOR_SCRIPT).replace(chr(39), chr(39)*2)}',[ref]$null,[ref]$e);"
            "if($e.Count){$e|ForEach-Object{Write-Error $_.Message};exit 2}"
        )
        completed = subprocess.run(
            [powershell, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )
        self.assertEqual(
            completed.returncode,
            0,
            msg=completed.stdout + completed.stderr,
        )


if __name__ == "__main__":
    unittest.main()
