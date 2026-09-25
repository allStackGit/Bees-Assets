"""Focused source-contract tests for command-line Unity training builds."""

from __future__ import annotations

import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
BUILD_SCRIPT = ROOT / "Editor" / "BeesCommandLineBuild.cs"


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


if __name__ == "__main__":
    unittest.main()
