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

            string levels = Path.Combine(Application.dataPath, "Scripts", "Levels");
            _triggerSource = string.Join("\n", Directory.GetFiles(levels, "*.cs", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
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
                Assert.That(setupBody.Contains(completion + "(") || setupBody.Contains(completion),
                    Is.True,
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
        [TestCase(6, "Neptune3PressingForwardCampaign", "break through the blockade", "ResolveEliminationWinner")]
        [TestCase(7, "Titania1MinesweeperCampaign", "ExitZonePrefab", "PlayerVisibleMapObjects")]
        [TestCase(8, "Titania2BeenocularsCampaign", "HumanTarget", "450")]
        [TestCase(9, "Uranus1OnTheOffensive", "Bumblebee", "Cruiser")]
        [TestCase(10, "Uranus2OnTheDefensive", "Survive and mine as many minerals as you can", "CreateCampaignRetreatZone")]
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

        [Test]
        public void BeenocularsEncodesFullEvacuationDefenseContract()
        {
            string setup = ExtractMethodBody(_triggerSource, "Titania2BeenocularsCampaign");
            string ending = ExtractMethodBody(_triggerSource, "Titania2CampaignEnding");

            Assert.That(setup, Does.Contain("const float survivalDuration = 450f"));
            Assert.That(setup, Does.Contain("ResolveTitania2(titania, true)"));
            Assert.That(setup, Does.Contain("ResolveTitania2(titania, false)"));
            Assert.That(setup, Does.Not.Contain("timeLeft <= 0 || State.IsSideKilled(ConfigData.Configuration.AISide)"),
                "Beenoculars is a timed evacuation defense, not an elimination shortcut.");

            Assert.That(setup, Does.Contain("StageTitania2HumanFleetAtCenter"),
                "Beenoculars must stage the player's fleet in Titania's central defensive pocket.");
            Assert.That(_triggerSource, Does.Contain("FindTitania2HumanSquadPlacement"));
            Assert.That(_triggerSource, Does.Contain("formationRadius = Mathf.Max(squad.GetWidth(), squad.GetHeight())"),
                "Central staging must validate the complete saved formation envelope rather than individual ships.");
            Assert.That(_triggerSource, Does.Contain("Physics2D.OverlapCircle(worldCandidate, formationRadius, ConfigData.ObstaclesLayerMask)"),
                "Central staging must reject obstacle-overlapping whole-squad positions.");
            Assert.That(_triggerSource, Does.Contain("squad.SetStartingPosition(placement)"),
                "Central staging must relocate the squad as a formation rather than scattering its ships independently.");
            Assert.That(_triggerSource, Does.Contain("CurrentLevelOptions.UserStartingPosition = Titania2Center"));
            Assert.That(_triggerSource, Does.Contain("Stage.DefaultCameraPosition = Titania2Center"));

            Assert.That(setup, Does.Contain("GetRange(12, 3)"));
            Assert.That(setup, Does.Contain("GetRange(15, 3)"));
            Assert.That(setup, Does.Contain("GetRange(18, 3)"));
            Assert.That(setup, Does.Contain("GetRange(21, 3)"));
            Assert.That(setup, Does.Contain("GetRange(24, 2)"));

            foreach (string delay in new[] { "60f", "120f", "210f", "300f", "375f" })
            {
                Assert.That(setup, Does.Contain("new ScaledTimer(" + delay),
                    $"Beenoculars is missing its authored/escalating wave at {delay} seconds.");
            }

            Assert.That(setup, Does.Contain("AddTitania2BeeWave"));
            Assert.That(_triggerSource, Does.Contain("GetTitania2OffMapEntry"));
            Assert.That(_triggerSource, Does.Contain("const float outsideDistance = 80f"));
            Assert.That(_triggerSource, Does.Contain("MaxX + outsideDistance"));
            Assert.That(_triggerSource, Does.Contain("MinX - outsideDistance"));
            Assert.That(_triggerSource, Does.Contain("MaxY + outsideDistance"));
            Assert.That(_triggerSource, Does.Contain("MinY - outsideDistance"));
            Assert.That(_triggerSource, Does.Contain("Physics2D.OverlapCircle(entryPoint, laneClearance, ConfigData.ObstaclesLayerMask)"),
                "Bee entry lanes must be checked against the authored walls.");
            Assert.That(_triggerSource, Does.Contain("Physics2D.Linecast(spawnPoint, entryPoint, ConfigData.ObstaclesLayerMask)"),
                "The off-map arrival segment must actually pass through a clear opening.");
            Assert.That(_triggerSource, Does.Contain("AddReinforcementSquads(new List<SavedSquad> { squads[i] }, spawnPoint, entryPoint)"));
            Assert.That(_triggerSource, Does.Contain("SetOffscreenStartingPosition(startingPosition)"),
                "Validated Beenoculars reinforcements must be moved back off-screen without obstacle-aware relocation before entering the map.");

            Assert.That(_triggerSource, Does.Contain("GetRange(26, 5)"));
            Assert.That(_triggerSource, Does.Contain("GetRange(31, 2)"));
            Assert.That(_triggerSource, Does.Contain("if (_titania2Resolved)"));
            Assert.That(_triggerSource, Does.Contain("Stage.Menus.Clock.SetActive(false)"));

            Assert.That(ending, Does.Contain("CampaignScore += State.PlayerScore"));
            Assert.That(ending, Does.Contain("AdvanceToNextLevel"));
            Assert.That(ending, Does.Contain("SaveSquadData"));
            Assert.That(ending, Does.Contain("SaveFleetData"));
            Assert.That(ending, Does.Contain("State.GameOver = true"));
            Assert.That(ending, Does.Contain("ShowLevelSummary"));
        }

        [TestCase("Pluto3Pushback")]
        [TestCase("Neptune1SeizeTheMeans")]
        [TestCase("Neptune3PressingForwardCampaign")]
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
