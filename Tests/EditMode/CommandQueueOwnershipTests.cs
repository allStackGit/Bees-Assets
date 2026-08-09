using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CommandQueueOwnershipTests
    {
        [Test]
        public void CommandSetupDoesNotClaimActiveSquadOwnership()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Command.cs");
            string source = File.ReadAllText(path);

            int setupStart = source.IndexOf("public virtual void Setup(Squad squad");
            int getSquadStart = source.IndexOf("public Squad GetSquad()", setupStart);
            int executeStart = source.IndexOf("public virtual void Execute", getSquadStart);
            Assert.That(setupStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(getSquadStart, Is.GreaterThan(setupStart));
            Assert.That(executeStart, Is.GreaterThan(getSquadStart));

            string setup = source.Substring(setupStart, getSquadStart - setupStart);
            string ownershipSection = source.Substring(getSquadStart, executeStart - getSquadStart);
            StringAssert.DoesNotContain("HasCommand = true", setup);
            StringAssert.DoesNotContain("HasCommand = true", ownershipSection);

            string execute = source.Substring(executeStart, source.IndexOf("public void SetDestination", executeStart) - executeStart);
            StringAssert.Contains("GetSquad().HasCommand = true;", execute);
        }

        [Test]
        public void OverrideMoveClaimsOwnershipAtExecutionAndReturnsAfterFinalize()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "MoveToPoint.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("GetSquad().HasCommand = true;", source);
            StringAssert.Contains("SetFinalize(\"Reached the specified destination on the map\");\n                    return;", source.Replace("\r\n", "\n"));
        }

        [Test]
        public void RandomMoveReturnsImmediatelyAfterFinalize()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "MoveToRandom.cs");
            string source = File.ReadAllText(path).Replace("\r\n", "\n");

            StringAssert.Contains("SetFinalize(\"Reached the random destination on the map\");\n                    return;", source);
        }
    }
}