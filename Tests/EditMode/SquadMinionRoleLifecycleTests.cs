using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMinionRoleLifecycleTests
    {
        [Test]
        public void GeneralSquadClearsMinionRoleAfterDeregistrationBeforePoolReuse()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.Combat.cs");
            string source = File.ReadAllText(path);

            int releaseIndex = source.IndexOf("Level.State.RemoveSquad(this);");
            int clearIndex = source.IndexOf("IsMinionSquad = false;", releaseIndex);
            Assert.That(releaseIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(clearIndex, Is.GreaterThan(releaseIndex));
        }

        [Test]
        public void MinionSquadsDoNotOwnPersistedLoadedState()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("if (!squad.IsMinionSquad)", source);
            StringAssert.Contains("squad.SavedSquad.IsLoadedIntoLevel = true;", source);
            StringAssert.Contains("squad.SavedSquad.IsLoadedIntoLevel = false;", source);
        }

        [Test]
        public void CarrierSquadReassertsMinionRoleOnEachSetup()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "CarrierSquad.cs");
            string source = File.ReadAllText(path);
            int setupIndex = source.IndexOf("public void SetupCarrierSquad");
            int roleIndex = source.IndexOf("IsMinionSquad = true;", setupIndex);

            Assert.That(setupIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(roleIndex, Is.GreaterThan(setupIndex));
        }
    }
}
