using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesCampaignScenario")]
    public class CampaignScenarioAuthoringTests
    {
        private Type _catalogType;
        private List<object> _readyDefinitions;
        private string _triggerSource;

        [SetUp]
        public void SetUp()
        {
            _catalogType = RuntimeAssembly.GetType(
                "Assets.Scripts.Levels.CampaignMissionCatalog");
            _readyDefinitions = ((IEnumerable)RuntimeAssembly.InvokeStatic(
                    _catalogType, "GetAutomatedScenarioDefinitions"))
                .Cast<object>()
                .ToList();
            _triggerSource = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "LeveLTriggers.cs"));
        }

        [Test]
        public void FullConfigureScenarioSetRemainsTheKnownSafePlutoAndNeptuneSubset()
        {
            int[] ids = _readyDefinitions
                .Select(definition => (int)RuntimeAssembly.GetField(definition, "Id"))
                .ToArray();
            Assert.That(ids, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6 }));
        }

        [Test]
        public void EveryReadyScenarioReferencesItsCompletionFromSetupAndReachesTerminalState()
        {
            foreach (object definition in _readyDefinitions)
            {
                int id = (int)RuntimeAssembly.GetField(definition, "Id");
                string setup = (string)RuntimeAssembly.GetField(definition, "SetupMethod");
                string completion = (string)RuntimeAssembly.GetField(definition, "CompletionMethod");
                string terminal = (string)RuntimeAssembly.GetField(definition, "TerminalMethod");
                string setupBody = ExtractMethodBody(_triggerSource, setup);
                string completionBody = ExtractMethodBody(_triggerSource, completion);
                string terminalBody = completion == terminal
                    ? completionBody
                    : ExtractMethodBody(_triggerSource, terminal);

                Assert.That(setupBody, Does.Contain("Stage.CutsceneManager.Setup"),
                    $"Mission {id} does not register a completion callback during setup.");
                Assert.That(setupBody, Does.Contain(completion + "("),
                    $"Mission {id} setup does not reference completion method {completion}.");
                if (completion != terminal)
                {
                    Assert.That(completionBody, Does.Contain(terminal + "("),
                        $"Mission {id} completion does not reach terminal method {terminal}.");
                }
                Assert.That(terminalBody, Does.Contain("State.GameOver = true"),
                    $"Mission {id} terminal method does not set GameOver.");
                Assert.That(terminalBody, Does.Contain("Stage.Menus.ShowLevel"),
                    $"Mission {id} terminal method does not present a level result.");
            }
        }

        [TestCase(0, "Pluto1Anomaly", "Scout and explore around Pluto", "HumanProximityColliderPrefab")]
        [TestCase(1, "Pluto2Reinforcements", "Find and destroy the enemy ships", "CanAcceptUserInput")]
        [TestCase(2, "Pluto3Pushback", "Push back the enemy", "ResolveEliminationWinner")]
        [TestCase(3, "Pluto4BluerPastures", "_questPoints", "personnel")]
        [TestCase(4, "Neptune1SeizeTheMeans", "Find and destroy all the Bees", "MiningAsteroid")]
        [TestCase(5, "Neptune2OfProduction", "Survive and mine as many minerals as you can", "ExitZonePrefab")]
        [TestCase(6, "Neptune3PressingForward", "break through the blockade", "ResolveEliminationWinner")]
        [TestCase(7, "Titania1Minesweeper", "ExitZonePrefab", "90")]
        [TestCase(8, "Titania2Beenoculars", "HumanTarget", "450")]
        [TestCase(9, "Uranus1OnTheOffensive", "Bumblebee", "Cruiser")]
        [TestCase(10, "Uranus2OnTheDefensive", "Survive and mine as many minerals as you can", "ExitZonePrefab")]
        [TestCase(11, "Uranus3ANewThreat", "Rescue the Barges and destroy all the Bees", "Barge")]
        public void EveryScriptedMissionRetainsItsDefiningGameplayContract(
            int missionId, string setupMethod, string firstMarker, string secondMarker)
        {
            string body = ExtractMethodBody(_triggerSource, setupMethod);
            Assert.That(body, Does.Contain(firstMarker),
                $"Mission {missionId} lost defining authoring marker '{firstMarker}'.");
            Assert.That(body, Does.Contain(secondMarker),
                $"Mission {missionId} lost defining authoring marker '{secondMarker}'.");
        }

        [TestCase("Pluto3Pushback")]
        [TestCase("Neptune1SeizeTheMeans")]
        [TestCase("Neptune3PressingForward")]
        public void EliminationScenariosEncodeBothUserAndAiOutcomes(string setupMethod)
        {
            string body = ExtractMethodBody(_triggerSource, setupMethod);
            Assert.That(body, Does.Contain("State.IsSideKilled(ConfigData.Configuration.UserSide)"));
            Assert.That(body, Does.Contain("State.IsSideKilled(ConfigData.Configuration.AISide)"));
            Assert.That(body, Does.Contain("CampaignObjectiveRules.ResolveEliminationWinner"));
            Assert.That(body, Does.Contain("WinningSide == ConfigData.Configuration.UserSide"));
        }

        [TestCase(true, false, 2)]
        [TestCase(false, true, 1)]
        [TestCase(true, true, 2)]
        public void EliminationRuleCoversPlayerLossWinAndSimultaneousWipe(
            bool userKilled, bool aiKilled, int expectedWinner)
        {
            Type rules = RuntimeAssembly.GetType(
                "Assets.Scripts.Levels.CampaignObjectiveRules");
            Assert.That(RuntimeAssembly.InvokeStatic(rules,
                "ResolveEliminationWinner", userKilled, aiKilled, 1, 2),
                Is.EqualTo(expectedWinner));
        }

        [Test]
        public void EliminationRuleRejectsAnUnfinishedBattle()
        {
            Type rules = RuntimeAssembly.GetType(
                "Assets.Scripts.Levels.CampaignObjectiveRules");
            var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                RuntimeAssembly.InvokeStatic(rules,
                    "ResolveEliminationWinner", false, false, 1, 2));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = source.IndexOf("public void " + methodName + "(", StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), $"Could not find method {methodName}.");
            int openingBrace = source.IndexOf('{', signature);
            int depth = 0;
            for (int index = openingBrace; index < source.Length; index++)
            {
                if (source[index] == '{')
                {
                    depth++;
                }
                else if (source[index] == '}' && --depth == 0)
                {
                    return source.Substring(openingBrace, index - openingBrace + 1);
                }
            }
            Assert.Fail($"Method {methodName} has no balanced body.");
            return string.Empty;
        }
    }
}
