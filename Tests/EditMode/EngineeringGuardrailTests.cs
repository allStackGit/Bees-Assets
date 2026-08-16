using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesEngineeringGuardrails")]
    public class EngineeringGuardrailTests
    {
        private static readonly string[] RequiredFiles =
        {
            "AGENTS.md",
            "PROJECT_CONSTITUTION.md",
            "docs/DEVELOPMENT_MEMORY.md",
            "docs/TESTING.md",
            "docs/engineering/INVARIANTS.md",
            "docs/engineering/SYSTEM_MAP.md",
            "docs/engineering/VALIDATION_POLICY.md",
            "docs/engineering/REGRESSIONS.md",
            "docs/engineering/CONTEXT_INDEX.md",
            "docs/engineering/LEARNING_STATE.md",
            "QUALITY_LEDGER.md",
            ".agents/skills/repo-learning/SKILL.md",
            ".agents/skills/continuous-learning/SKILL.md",
            ".agents/skills/search-index/SKILL.md",
            ".agents/skills/code-quality/SKILL.md",
            ".agents/skills/test-health/SKILL.md",
            ".agents/skills/bug-finding/SKILL.md",
            ".agents/skills/performance-optimization/SKILL.md"
        };

        [Test]
        public void RequiredEngineeringGuardrailsRemainPresent()
        {
            string missing = string.Join(", ", RequiredFiles.Where(path => !File.Exists(RepoPath(path))));
            Assert.That(missing, Is.Empty, "Missing mandatory repository guardrail file(s): " + missing);
        }

        [Test]
        public void PermanentRegressionEntriesRequireRootCauseProtectionAndVerification()
        {
            string text = File.ReadAllText(RepoPath("docs/engineering/REGRESSIONS.md"));
            MatchCollection headings = Regex.Matches(text, @"(?m)^### REG-\d+\s+—.*$");

            foreach (Match heading in headings)
            {
                int nextHeading = text.IndexOf("\n### REG-", heading.Index + heading.Length, StringComparison.Ordinal);
                string entry = nextHeading >= 0
                    ? text.Substring(heading.Index, nextHeading - heading.Index)
                    : text.Substring(heading.Index);

                StringAssert.Contains("**Root cause:**", entry, heading.Value + " is missing a root-cause field.");
                StringAssert.Contains("**Permanent protection:**", entry, heading.Value + " is missing permanent protection.");
                StringAssert.Contains("**Verification:**", entry, heading.Value + " is missing verification evidence.");
            }
        }

        [Test]
        public void SelfLearningWorkflowRemainsWired()
        {
            string agents = File.ReadAllText(RepoPath("AGENTS.md"));
            string repoLearning = File.ReadAllText(RepoPath(".agents/skills/repo-learning/SKILL.md"));
            string[] requiredLinks =
            {
                "docs/engineering/CONTEXT_INDEX.md",
                ".agents/skills/continuous-learning/SKILL.md",
                ".agents/skills/search-index/SKILL.md",
                ".agents/skills/code-quality/SKILL.md"
            };

            foreach (string link in requiredLinks)
            {
                StringAssert.Contains(link, agents, "AGENTS.md no longer requires " + link);
                StringAssert.Contains(link, repoLearning, "repo-learning no longer wires " + link);
            }

            string learning = File.ReadAllText(RepoPath(".agents/skills/continuous-learning/SKILL.md"));
            foreach (string disposition in new[] { "promote", "refresh", "defer", "reject" })
            {
                StringAssert.Contains(disposition, learning, "continuous-learning lost " + disposition + " disposition");
            }
        }

        [Test]
        public void ContextIndexRemainsCompactNavigation()
        {
            string text = File.ReadAllText(RepoPath("docs/engineering/CONTEXT_INDEX.md"));
            int nonEmptyLineCount = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Count(line => !string.IsNullOrWhiteSpace(line));

            Assert.That(nonEmptyLineCount, Is.LessThanOrEqualTo(400),
                "Context index has become too large; move detail to owner documents.");
            StringAssert.Contains("navigation, not authority", text.ToLowerInvariant());
        }

        private static string RepoPath(string relativePath)
        {
            return Path.Combine(Application.dataPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
