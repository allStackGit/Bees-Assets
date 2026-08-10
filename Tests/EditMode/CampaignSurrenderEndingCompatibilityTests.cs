using System;
using System.Reflection;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CampaignSurrenderEndingCompatibilityTests
    {
        [Test]
        public void EveryCatalogMissionExposesLegacyLevelNumberEndingMethod()
        {
            Type levelType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Level");
            Type catalogType = RuntimeAssembly.GetType("Assets.Scripts.Levels.CampaignMissionCatalog");
            object definitions = catalogType.GetField("Definitions", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            int count = RuntimeAssembly.GetCount(definitions);

            for (int id = 0; id < count; id++)
            {
                MethodInfo method = levelType.GetMethod(
                    $"Level{id}Ending",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                Assert.That(method, Is.Not.Null, $"Campaign level {id} has no legacy surrender ending entry point.");
            }
        }
    }
}
