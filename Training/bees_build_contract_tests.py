"""Executable operator behavior checks used by distributed-training qualification.

Keep this suite focused on behavior that can be exercised directly. Do not add source-text,
function-order, formatting, or exact-implementation assertions here.
"""

from __future__ import annotations

import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
NODE_OPERATOR = ROOT / "Training" / "bees_operator.js"
OPERATOR_ROOT = ROOT / "Training" / "operator"
OPERATOR_SCRIPT = ROOT / "bees.ps1"
REMOTE_BOOTSTRAP_SCRIPT = ROOT / "Training" / "bees_remote_bootstrap.ps1"


def node_executable() -> str | None:
    return shutil.which("node") or shutil.which("node.exe")


class OperatorBehaviorTests(unittest.TestCase):
    def test_node_cli_parser_handles_environment_args_and_preserve_run(self):
        node = node_executable()
        if not node:
            self.skipTest("node is not available")
        script = (
            "const op=require(process.argv[1]);"
            "process.stdout.write(JSON.stringify({"
            "start:op.parseArgs(['start','--new-run','--env-arg','--rl-map-size-min=32',"
            "'--env-arg','--rl-human-ship-types=Scout,Gunship']),"
            "preserve:op.parseArgs(['build','--preserve-run']),"
            "observe:op.parseArgs(['observe'])"
            "}));"
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

        start = parsed["start"]
        self.assertEqual(start["command"], "start")
        self.assertTrue(start["options"]["newRun"])
        self.assertEqual(
            start["options"]["envArgs"],
            [
                "--rl-map-size-min=32",
                "--rl-human-ship-types=Scout,Gunship",
            ],
        )

        preserve = parsed["preserve"]
        self.assertEqual(preserve["command"], "build")
        self.assertTrue(preserve["options"]["preserveRun"])

        observe = parsed["observe"]
        self.assertEqual(observe["command"], "observe")

    def test_observe_visual_args_preserve_scenario_and_force_one_arena(self):
        node = node_executable()
        if not node:
            self.skipTest("node is not available")
        observe = OPERATOR_ROOT / "observe.js"
        script = (
            "const o=require(process.argv[1]);"
            "process.stdout.write(JSON.stringify(o.visualEnvironmentArgs(["
            "'--rl-ships-per-side=2','--bees-rl-arenas-per-env','8',"
            "'--rl-static-obstacles=true'])));"
        )
        completed = subprocess.run(
            [node, "-e", script, str(observe)],
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )
        self.assertEqual(completed.returncode, 0, msg=completed.stderr)
        self.assertEqual(
            json.loads(completed.stdout),
            [
                "--rl-ships-per-side=2",
                "--rl-static-obstacles=true",
                "--bees-rl-arenas-per-env=1",
            ],
        )

    def test_control_client_retries_transient_reset_for_idempotent_admin_post(self):
        node = node_executable()
        if not node:
            self.skipTest("node is not available")
        common = OPERATOR_ROOT / "common.js"
        script = r"""
const http=require('node:http');
const c=require(process.argv[1]);
let count=0;
const server=http.createServer((req,res)=>{
    count++;
    if(count===1){
        req.socket.destroy();
        return;
    }
    let body='';
    req.on('data',chunk=>body+=chunk);
    req.on('end',()=>{
        res.statusCode=200;
        res.setHeader('Content-Type','application/json');
        res.end(JSON.stringify({ok:true,body:JSON.parse(body)}));
    });
});
server.listen(0,'127.0.0.1',async()=>{
    const address=server.address();
    try{
        const value=await c.requestJson(
            'http://127.0.0.1:'+address.port,
            'token',
            'POST',
            '/v1/admin/artifact',
            {build_id:'build-1'},
            1000
        );
        process.stdout.write(JSON.stringify({count,value}));
    }catch(error){
        process.stderr.write(error.stack||String(error));
        process.exitCode=1;
    }finally{
        server.close();
    }
});
"""
        completed = subprocess.run(
            [node, "-e", script, str(common)],
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )
        self.assertEqual(completed.returncode, 0, msg=completed.stderr)
        result = json.loads(completed.stdout)
        self.assertEqual(result["count"], 2)
        self.assertTrue(result["value"]["ok"])
        self.assertEqual(result["value"]["body"]["build_id"], "build-1")

    def test_control_client_does_not_retry_http_semantic_failure(self):
        node = node_executable()
        if not node:
            self.skipTest("node is not available")
        common = OPERATOR_ROOT / "common.js"
        script = r"""
const http=require('node:http');
const c=require(process.argv[1]);
let count=0;
const server=http.createServer((_req,res)=>{
    count++;
    res.statusCode=409;
    res.setHeader('Content-Type','application/json');
    res.end(JSON.stringify({error:'conflict'}));
});
server.listen(0,'127.0.0.1',async()=>{
    const address=server.address();
    try{
        await c.requestJson(
            'http://127.0.0.1:'+address.port,
            'token',
            'POST',
            '/v1/admin/release',
            {build_id:'build-1'},
            1000
        );
        process.exitCode=2;
    }catch(error){
        process.stdout.write(JSON.stringify({count,message:String(error.message)}));
    }finally{
        server.close();
    }
});
"""
        completed = subprocess.run(
            [node, "-e", script, str(common)],
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )
        self.assertEqual(completed.returncode, 0, msg=completed.stderr)
        result = json.loads(completed.stdout)
        self.assertEqual(result["count"], 1)
        self.assertIn("HTTP 409", result["message"])

    def test_central_supervisor_uses_prepared_immutable_runtime_copy(self):
        node = node_executable()
        if not node:
            self.skipTest("node is not available")
        central = OPERATOR_ROOT / "central.js"
        with tempfile.TemporaryDirectory() as temp:
            runtime_root = Path(temp).resolve()
            agent = runtime_root / "bees_training_worker_agent.py"
            agent.write_text("# pinned central supervisor\n", encoding="utf-8")
            script = (
                "const c=require(process.argv[1]);"
                "process.stdout.write(c.centralSupervisorAgentPath({runtime_root:process.argv[2]}));"
            )
            completed = subprocess.run(
                [node, "-e", script, str(central), str(runtime_root)],
                cwd=ROOT,
                capture_output=True,
                text=True,
                timeout=30,
                check=False,
            )
            self.assertEqual(completed.returncode, 0, msg=completed.stderr)
            self.assertEqual(Path(completed.stdout), agent)

    def test_central_supervisor_launch_contract_rejects_legacy_source_path_state(self):
        node = node_executable()
        if not node:
            self.skipTest("node is not available")
        central = OPERATOR_ROOT / "central.js"
        with tempfile.TemporaryDirectory() as temp:
            runtime_root = Path(temp).resolve()
            agent = runtime_root / "bees_training_worker_agent.py"
            agent.write_text("# pinned central supervisor\n", encoding="utf-8")
            script = (
                "const c=require(process.argv[1]);"
                "const agent=process.argv[2];"
                "const legacy={command_hash:'hash',runtime_cutover_capable:true,argv_transport:'node-spawn-array-v1'};"
                "const pinned={...legacy,supervisor_agent:agent};"
                "process.stdout.write(JSON.stringify({"
                "legacy:c.centralSupervisorLaunchContractMatches(legacy,'hash',agent),"
                "pinned:c.centralSupervisorLaunchContractMatches(pinned,'hash',agent)"
                "}));"
            )
            completed = subprocess.run(
                [node, "-e", script, str(central), str(agent)],
                cwd=ROOT,
                capture_output=True,
                text=True,
                timeout=30,
                check=False,
            )
            self.assertEqual(completed.returncode, 0, msg=completed.stderr)
            result = json.loads(completed.stdout)
            self.assertFalse(result["legacy"])
            self.assertTrue(result["pinned"])

    def test_central_supervisor_persists_exact_canonical_fallback_command(self):
        node = node_executable()
        if not node:
            self.skipTest("node is not available")
        central = OPERATOR_ROOT / "central.js"
        script = (
            "const c=require(process.argv[1]);"
            "const state={runtime_cutover_capable:true,fallback_build_id:'stable-build',"
            "fallback_launch_command:['python','stable-service.py','--env={env}']};"
            "process.stdout.write(JSON.stringify({"
            "match:c.persistedCentralFallback(state,'stable-build'),"
            "mismatch:c.persistedCentralFallback(state,'other-build')"
            "}));"
        )
        completed = subprocess.run(
            [node, "-e", script, str(central)],
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )
        self.assertEqual(completed.returncode, 0, msg=completed.stderr)
        result = json.loads(completed.stdout)
        self.assertEqual(result["match"]["build_id"], "stable-build")
        self.assertEqual(
            result["match"]["launch_command"],
            ["python", "stable-service.py", "--env={env}"],
        )
        self.assertIsNone(result["mismatch"])

    def test_local_actor_supervisor_uses_prepared_immutable_runtime_copy(self):
        node = node_executable()
        if not node:
            self.skipTest("node is not available")
        local_actor = OPERATOR_ROOT / "localActor.js"
        with tempfile.TemporaryDirectory() as temp:
            runtime_root = Path(temp).resolve()
            agent = runtime_root / "bees_training_worker_agent.py"
            agent.write_text("# pinned local actor supervisor\n", encoding="utf-8")
            script = (
                "const a=require(process.argv[1]);"
                "process.stdout.write(a.localActorSupervisorAgentPath({runtime_root:process.argv[2]}));"
            )
            completed = subprocess.run(
                [node, "-e", script, str(local_actor), str(runtime_root)],
                cwd=ROOT,
                capture_output=True,
                text=True,
                timeout=30,
                check=False,
            )
            self.assertEqual(completed.returncode, 0, msg=completed.stderr)
            self.assertEqual(Path(completed.stdout), agent)

    def test_public_powershell_entrypoints_parse(self):
        powershell = shutil.which("powershell") or shutil.which("pwsh")
        if not powershell:
            self.skipTest("PowerShell is not available")
        for script in (OPERATOR_SCRIPT, REMOTE_BOOTSTRAP_SCRIPT):
            escaped = str(script).replace(chr(39), chr(39) * 2)
            command = (
                "$e=$null;"
                f"$null=[System.Management.Automation.Language.Parser]::ParseFile('{escaped}',[ref]$null,[ref]$e);"
                "if($e.Count){$e|ForEach-Object{Write-Error "
                "('line {0}, column {1}: {2} :: {3}' -f "
                "$_.Extent.StartLineNumber,$_.Extent.StartColumnNumber,$_.Message,$_.Extent.Text)};exit 2}"
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
                msg=f"{script} failed PowerShell parse:\n{completed.stdout}{completed.stderr}",
            )


if __name__ == "__main__":
    unittest.main()
