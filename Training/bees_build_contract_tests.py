"""Focused source-contract tests for command-line Unity training builds."""

from __future__ import annotations

import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
BUILD_SCRIPT = ROOT / "Editor" / "BeesCommandLineBuild.cs"
OPERATOR_SCRIPT = ROOT / "bees.ps1"


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


    def test_operator_hashes_actual_server_and_training_runtime_bytes(self):
        source = OPERATOR_SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("Get-GitTreeSha", source)
        self.assertIn(
            "$serverSourceHash=Get-DirectoryContentSha256 $ServerRoot @('node_modules')",
            source,
        )
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


if __name__ == "__main__":
    unittest.main()
