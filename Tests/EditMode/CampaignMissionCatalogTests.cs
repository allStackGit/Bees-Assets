using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignMissionCatalogTests
    {
        private Type _catalogType;
        private IList _definitions;

        [SetUp]
        public void SetUp()
        {
            _catalogType = RuntimeAssembly.GetType("Assets.Scripts.Levels.CampaignMissionCatalog");
            _definitions = (IList)RuntimeAssembly.GetStaticField(_catalogType, "Definitions");
        }

        [Test]
        public void CatalogIdsAreUniqueContiguousAndBackedBySetupMethods()
        {
            Assert.That(_definitions.Count, Is.EqualTo(12));
            var ids = new List<int>();

            foreach (object definition in _definitions)
            {
                int id = (int)RuntimeAssembly.GetField(definition, "Id");
                string name = (string)RuntimeAssembly.GetField(definition, "Name");
                string setupMethod = (string)RuntimeAssembly.GetField(definition, "SetupMethod");
                string terminalMethod = (string)RuntimeAssembly.GetField(definition, "TerminalMethod");
                ids.Add(id);

                Assert.That(name, Is.Not.Empty, $"Campaign mission {id} has no authoring name.");
                Assert.That(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level").GetMethod(
                    setupMethod, BindingFlags.Instance | BindingFlags.Public), Is.Not.Null,
                    $"Campaign mission {id} points to missing setup method {setupMethod}.");
                Assert.That(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level").GetMethod(
                    terminalMethod, BindingFlags.Instance | BindingFlags.Public), Is.Not.Null,
                    $"Campaign mission {id} points to missing terminal method {terminalMethod}.");
            }

            Assert.That(ids, Is.EqualTo(Enumerable.Range(0, _definitions.Count).ToArray()));
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count));
        }

        [Test]
        public void EveryPersistedMissionTerminalPathSetsGameOver()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "LeveLTriggers.cs");
            string source = File.ReadAllText(path);

            foreach (object definition in _definitions.Cast<object>().Where(definition =>
                RuntimeAssembly.GetField(definition, "ScenarioStatus").ToString() == "Ready"))
            {
                int id = (int)RuntimeAssembly.GetField(definition, "Id");
                string terminalMethod = (string)RuntimeAssembly.GetField(definition, "TerminalMethod");
                string body = ExtractMethodBody(source, terminalMethod);
                Assert.That(body, Does.Contain("State.GameOver = true"),
                    $"Playable campaign mission {id} terminal method {terminalMethod} does not end the game.");
            }
        }

        [Test]
        public void EveryPersistedMissionRequiringAnIntroHasALevelIntroDispatchCase()
        {
            string campaignPath = Path.Combine(Application.dataPath, "Scripts", "Data", "campaign_levels.json");
            string introPath = Path.Combine(Application.dataPath, "Scripts", "Scenes", "LevelIntro.cs");
            string campaignJson = File.ReadAllText(campaignPath);
            string introSource = File.ReadAllText(introPath);
            MatchCollection levels = Regex.Matches(
                campaignJson,
                "\\\"Id\\\"\\s*:\\s*(\\d+)[\\s\\S]*?\\\"HasPreLevelIntro\\\"\\s*:\\s*(true|false)",
                RegexOptions.IgnoreCase);

            Assert.That(levels.Count, Is.EqualTo(9));
            var scenarioIds = ((IEnumerable)RuntimeAssembly.InvokeStatic(
                    _catalogType, "GetAutomatedScenarioDefinitions"))
                .Cast<object>()
                .Select(definition => (int)RuntimeAssembly.GetField(definition, "Id"))
                .ToHashSet();
            foreach (Match level in levels)
            {
                int id = int.Parse(level.Groups[1].Value);
                bool hasIntro = bool.Parse(level.Groups[2].Value);
                if (hasIntro && scenarioIds.Contains(id))
                {
                    Assert.That(introSource, Does.Contain($"case {id}:"),
                        $"Campaign mission {id} requests a pre-level intro but LevelIntro has no dispatch case.");
                }
            }
        }

        [Test]
        public void PersistedCampaignDataHasExactlyOneRecordForEveryCatalogId()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Data", "campaign_levels.json");
            string json = File.ReadAllText(path);
            int[] persistedIds = Regex.Matches(json, "\\\"Id\\\"\\s*:\\s*(\\d+)")
                .Cast<Match>()
                .Select(match => int.Parse(match.Groups[1].Value))
                .ToArray();
            int[] catalogIds = _definitions.Cast<object>()
                .Where(definition => (bool)RuntimeAssembly.GetField(definition, "HasPersistedLevelData"))
                .Select(definition => (int)RuntimeAssembly.GetField(definition, "Id"))
                .ToArray();

            Assert.That(persistedIds, Is.EqualTo(catalogIds),
                "Campaign data and runtime mission dispatch have drifted.");
        }

        [Test]
        public void UnknownMissionIdFailsExplicitly()
        {
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                RuntimeAssembly.InvokeStatic(_catalogType, "Get", 999));
            Assert.That(exception.InnerException, Is.TypeOf<ArgumentOutOfRangeException>());
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = source.IndexOf("public void " + methodName + "(", StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), $"Could not find method {methodName} in source.");
            int openingBrace = source.IndexOf('{', signature);
            int depth = 0;
            for (int index = openingBrace; index < source.Length; index++)
            {
                if (source[index] == '{')
                {
                    depth++;
                }
                else if (source[index] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(openingBrace, index - openingBrace + 1);
                    }
                }
            }
            Assert.Fail($"Method {methodName} has no balanced body.");
            return string.Empty;
        }
    }
}
