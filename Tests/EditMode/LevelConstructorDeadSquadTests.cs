using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class LevelConstructorDeadSquadTests
    {
        [Test]
        public void DeadOnlySquadsAreSkippedBeforeAllocatingRuntimeSquad()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "LevelConstructor.cs");
            string source = File.ReadAllText(path);

            int methodIndex = source.IndexOf("public void SpawnShipsAndSquads");
            int liveShipsIndex = source.IndexOf("savedSquad.GetSquadShips().Where", methodIndex);
            int emptyGuardIndex = source.IndexOf("if (ships.Count == 0)", liveShipsIndex);
            int toSquadIndex = source.IndexOf("savedSquad.ToSquad(Level)", methodIndex);

            Assert.That(methodIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(liveShipsIndex, Is.GreaterThan(methodIndex));
            Assert.That(emptyGuardIndex, Is.GreaterThan(liveShipsIndex));
            Assert.That(toSquadIndex, Is.GreaterThan(emptyGuardIndex),
                "A dead-only persisted squad must be rejected before ToSquad allocates a pooled runtime wrapper.");
        }
    }
}
