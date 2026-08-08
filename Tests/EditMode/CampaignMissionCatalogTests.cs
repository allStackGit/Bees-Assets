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

        private static readonly string[] ExpectedNames =
        {
            "Anomaly", "Reinforcements", "Pushback", "Bluer Pastures",
            "Seize the Means", "Of Production", "Pressing Forward",
            "Minesweeper", "Beenoculars", "On the Offensive",
            "On the Defensive", "A New Threat"
        };

        private static readonly string[] ExpectedSetupMethods =
        {
            "Pluto1Anomaly", "Pluto2Reinforcements", "Pluto3Pushback", "Pluto4BluerPastures",
            "Neptune1SeizeTheMeans", "Neptune2OfProduction", "Neptune3PressingForward",
            "Titania1MinesweeperCampaign", "Titania2BeenocularsCampaign", "Uranus1OnTheOffensive",
            "Uranus2OnTheDefensive", "Uranus3ANewThreat"
        };

        private static readonly string[] ExpectedCompletionMethods =
        {
            "Pluto1Ending", "Pluto2Ending", "Pluto3Ending", "Pluto4Ending",
            "Neptune1Ending", "Neptune2Ending", "Neptune3Ending",
            "Titania1MinesweeperEnding", "Titania2Ending", "Uranus1Ending",
            "Uranus2Ending", "Uranus3Ending"
        };

        private static readonly string[] ExpectedTerminalMethods =
        {
            "Pluto1Ending", "Pluto2Ending", "Pluto3Ending", "Pluto4EndingDialogue",
            "Neptune1Ending", "Neptune2Ending", "Neptune3Ending",
            "Titania1MinesweeperEnding", "Titania2Ending", "Uranus1Ending",
            "Uranus2Ending", "Uranus3Ending"
        };

        private static readonly int[] ExpectedMapIndices =
        {
            0, 0, 0, 0,
            1, 1, 1,
            2, 2,
            3, 3, 3
        };

        [SetUp]
        public void SetUp()
        {
            _catalogType = RuntimeAssembly.GetType("Assets.Scripts.Levels.CampaignMissionCatalog");
            _definitions = (IList)RuntimeAssembly.GetStaticField(_catalogType, "Definitions");
        }

        [Test]
        public void CatalogDefinesTheCurrentTwelveMissionSequenceExactly()
        {
            Assert.That(_definitions.Count, Is.EqualTo(12));
            Type levelType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Level");

            for (int id = 0; id < _definitions.Count; id++)
            {
                object definition = _definitions[id];
                Assert.That((int)RuntimeAssembly.GetField(definition, "Id"), Is.EqualTo(id));
                Assert.That((string)RuntimeAssembly.GetField(definition, "Name"), Is.EqualTo(ExpectedNames[id]));
                Assert.That((string)RuntimeAssembly.GetField(definition, "SetupMethod"), Is.EqualTo(ExpectedSetupMethods[id]));
                Assert.That((string)RuntimeAssembly.GetField(definition, "CompletionMethod"), Is.EqualTo(ExpectedCompletionMethods[id]));
                Assert.That((string)RuntimeAssembly.GetField(definition, "TerminalMethod"), Is.EqualTo(ExpectedTerminalMethods[id]));

                Assert.That(levelType.GetMethod(ExpectedSetupMethods[id], BindingFlags.Instance | BindingFlags.Public),
                    Is.Not.Null, $"Campaign mission {id} setup method is missing.");
                Assert.That(levelType.GetMethod(ExpectedCompletionMethods[id], BindingFlags.Instance | BindingFlags.Public),
                    Is.Not.Null, $"Campaign mission {id} completion method is missing.");
                Assert.That(levelType.GetMethod(ExpectedTerminalMethods[id], BindingFlags.Instance | BindingFlags.Public),
                    Is.Not.Null, $"Campaign mission {id} terminal method is missing.");
            }
        }

        [Test]
        public void MissionSequenceUsesPlutoThenNeptuneThenTitaniaThenUranus()
        {
            Type configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            IList maps = (IList)RuntimeAssembly.GetStaticField(configDataType, "Maps");
            string[] expectedLocations = { "Pluto", "Neptune", "Titania", "Uranus" };

            Assert.That(maps.Count, Is.GreaterThanOrEqualTo(4));
            for (int mapIndex = 0; mapIndex < expectedLocations.Length; mapIndex++)
            {
                object map = maps[mapIndex];
                Assert.That((int)RuntimeAssembly.GetField(map, "Id"), Is.EqualTo(mapIndex));
                Assert.That(RuntimeAssembly.GetField(map, "Location").ToString(), Is.EqualTo(expectedLocations[mapIndex]));
            }

            for (int missionId = 0; missionId < ExpectedMapIndices.Length; missionId++)
            {
                int mapIndex = ExpectedMapIndices[missionId];
                Assert.That(RuntimeAssembly.GetField(maps[mapIndex], "Location").ToString(),
                    Is.EqualTo(expectedLocations[mapIndex]),
                    $"Campaign mission {missionId} is assigned to the wrong campaign location contract.");
            }
        }

        [Test]
        public void EveryScriptedMissionTerminalPathSetsGameOver()
        {
            string source = ReadCampaignLevelSources();

            for (int id = 0; id < _definitions.Count; id++)
            {
                string terminalMethod = ExpectedTerminalMethods[id];
                string body = ExtractMethodBody(source, terminalMethod);
                Assert.That(body, Does.Contain("State.GameOver = true"),
                    $"Campaign mission {id} terminal path {terminalMethod} does not set GameOver.");
            }
        }

        [Test]
        public void EveryPostAnomalyMissionHasAnActiveLevelIntroDispatchCase()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Scenes", "LevelIntro.cs"));
            string withoutComments = Regex.Replace(source, @"//.*?$|/\*[\s\S]*?\*/", string.Empty,
                RegexOptions.Multiline);

            for (int id = 1; id <= 11; id++)
            {
                Assert.That(Regex.IsMatch(withoutComments, $@"\bcase\s+{id}\s*:"), Is.True,
                    $"Campaign mission {id} has no active LevelIntro dispatch case.");
            }
        }

        [Test]
        public void FullSetupAutomationRemainsRestrictedToTheKnownSafeSubset()
        {
            int[] ids = ((IEnumerable)RuntimeAssembly.InvokeStatic(
                    _catalogType, "GetAutomatedScenarioDefinitions"))
                .Cast<object>()
                .Select(definition => (int)RuntimeAssembly.GetField(definition, "Id"))
                .ToArray();

            Assert.That(ids, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6 }),
                "Do not widen full mission Configure() automation until persistent fleet/UI dependencies are isolated.");
        }

        [Test]
        public void UnknownMissionIdFailsExplicitly()
        {
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                RuntimeAssembly.InvokeStatic(_catalogType, "Get", 999));
            Assert.That(exception.InnerException, Is.TypeOf<ArgumentOutOfRangeException>());
        }

        private static string ReadCampaignLevelSources()
        {
            string levels = Path.Combine(Application.dataPath, "Scripts", "Levels");
            return string.Join("\n", Directory.GetFiles(levels, "*.cs", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
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
