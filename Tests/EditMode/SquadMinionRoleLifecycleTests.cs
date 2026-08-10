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
        public void GeneralSquadClearsMinionRoleBeforePoolRelease()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Squad.Combat.cs");
            string source = File.ReadAllText(path);

            int clearIndex = source.IndexOf("IsMinionSquad = false;");
            int releaseIndex = source.IndexOf("Level.State.RemoveSquad(this);");
            Assert.That(clearIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(releaseIndex, Is.GreaterThan(clearIndex));
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
