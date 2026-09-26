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
