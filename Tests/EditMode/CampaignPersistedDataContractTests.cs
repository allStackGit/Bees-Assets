using System.Collections;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignPersistedDataContractTests
    {
        private static readonly int[] ExpectedMapIndices =
        {
            0, 0, 0, 0,
            1, 1, 1,
            2, 2,
            3, 3, 3
        };

        [Test]
        public void CatalogMarksAllTwelveServerBackedCampaignMissionsAsPersisted()
        {
            var catalogType = RuntimeAssembly.GetType("Assets.Scripts.Levels.CampaignMissionCatalog");
            var definitions = (IList)RuntimeAssembly.GetStaticField(catalogType, "Definitions");

            Assert.That(definitions.Count, Is.EqualTo(12));
            for (int id = 0; id < definitions.Count; id++)
            {
                object definition = definitions[id];
                Assert.That(
                    RuntimeAssembly.GetField(definition, "HasPersistedLevelData"),
                    Is.True,
                    $"Campaign mission {id} exists in server-backed campaign_levels_data and must not be marked missing.");
            }
        }

        [Test]
        public void CatalogOwnsCanonicalMapForEveryCampaignMission()
        {
            var catalogType = RuntimeAssembly.GetType("Assets.Scripts.Levels.CampaignMissionCatalog");
            var definitions = (IList)RuntimeAssembly.GetStaticField(catalogType, "Definitions");

            Assert.That(definitions.Count, Is.EqualTo(ExpectedMapIndices.Length));
            for (int id = 0; id < definitions.Count; id++)
            {
                Assert.That(
                    RuntimeAssembly.GetField(definitions[id], "MapIndex"),
                    Is.EqualTo(ExpectedMapIndices[id]),
                    $"Campaign mission {id} is assigned to the wrong campaign map.");
            }
        }

        [TestCase(11, false)]
        [TestCase(12, true)]
        [TestCase(32, true)]
        public void CampaignCompletionUsesScriptedMissionBoundary(int currentMissionId, bool expectedComplete)
        {
            var catalogType = RuntimeAssembly.GetType("Assets.Scripts.Levels.CampaignMissionCatalog");

            Assert.That(
                RuntimeAssembly.InvokeStatic(catalogType, "IsCampaignComplete", currentMissionId),
                Is.EqualTo(expectedComplete));
        }

        [Test]
        public void UranusMissionsRemainExcludedFromFullConfigureAutomationUntilIsolationIsReady()
        {
            var catalogType = RuntimeAssembly.GetType("Assets.Scripts.Levels.CampaignMissionCatalog");
            var definitions = (IList)RuntimeAssembly.GetStaticField(catalogType, "Definitions");

            for (int id = 9; id <= 11; id++)
            {
                object definition = definitions[id];
                Assert.That(
                    RuntimeAssembly.GetField(definition, "ScenarioStatus").ToString(),
                    Is.EqualTo("InDevelopment"));
            }
        }
    }
}
