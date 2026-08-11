using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class DynamicCommandBanLifetimeTests
    {
        [Test]
        public void EnvironmentDependentBansDoNotMutatePersistentSquadBanSet()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Squad.Commands.cs"));

            Assert.That(source, Does.Not.Contain("BannedStrats.Add(ConfigData.CommandTypes.Mining)"));
            Assert.That(source, Does.Not.Contain("BannedStrats.Add(ConfigData.CommandTypes.FullRetreat)"));
            Assert.That(source, Does.Not.Contain("BannedStrats.Add(ConfigData.CommandTypes.Heal)"));
            Assert.That(source, Does.Contain("_bannedStrats.Add(ConfigData.CommandTypes.Mining)"));
            Assert.That(source, Does.Contain("_bannedStrats.Add(ConfigData.CommandTypes.FullRetreat)"));
            Assert.That(source, Does.Contain("_bannedStrats.Add(ConfigData.CommandTypes.Heal)"));
        }
    }
}
