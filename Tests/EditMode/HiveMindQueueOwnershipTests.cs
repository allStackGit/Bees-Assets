using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class HiveMindQueueOwnershipTests
    {
        [Test]
        public void AwaitingCommandQueueRejectsDeadAndDuplicateSquads()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Commands.cs");
            string source = File.ReadAllText(path);

            int methodStart = source.IndexOf("public void AddToSquadsAwaitingHiveMindCommands(Squad squad)");
            int methodEnd = source.IndexOf("public bool TryDequeueSquadAwaitingHiveMindCommand", methodStart);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(methodEnd, Is.GreaterThan(methodStart));

            string method = source.Substring(methodStart, methodEnd - methodStart);
            StringAssert.Contains("squad == null", method);
            StringAssert.Contains("squad.IsDead", method);
            StringAssert.Contains("!_squadsAwaitingCommandSet.Add(squad)", method);
            StringAssert.Contains("SquadsAwaitingCommands.Enqueue(squad)", method);
            StringAssert.Contains("_squadsAwaitingCommandSet.Remove(squad)", source);
        }
    }
}
