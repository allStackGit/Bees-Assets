using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class PersistedDataMutationAuditTests
    {
        [Test]
        public void SpriteCachingUsesSquadSnapshotAndDoesNotMutatePersistedColorAlpha()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Utilities.cs"));
            int start = source.IndexOf("public static IEnumerator CacheSquadCustomSprites", StringComparison.Ordinal);
            int end = source.IndexOf("public static Sprite SetImageColor", start, StringComparison.Ordinal);
            string method = source.Substring(start, end - start);

            Assert.That(method, Does.Contain("squad.GetSquadShips().ToList()"));
            Assert.That(method, Does.Not.Contain("squad.Color.a ="));
            Assert.That(method, Does.Contain("Color cacheSquadColor = squad.Color"));
        }

        [Test]
        public void CountedCompositionSelectionUsesAliveShipsForAllBounds()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Ships.cs"));
            int start = source.IndexOf("public SavedSquad GetSquadByComposition(Level level", StringComparison.Ordinal);
            int end = source.IndexOf("public SavedSquad GetSquadByComposition(ConfigData.ShipTypes shipType)", start, StringComparison.Ordinal);
            string method = source.Substring(start, end - start);

            Assert.That(method, Does.Not.Contain("GetSquadShips().Count"));
            Assert.That(method, Does.Contain("GetAliveSquadShips().Count == shipCount"));
            Assert.That(method, Does.Contain("GetAliveSquadShips().Count <= shipCount"));
            Assert.That(method, Does.Contain("GetAliveSquadShips().Count >= shipCount"));
        }
    }
}
