using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class LevelDataOrderingTests
    {
        [Test]
        public void PersistedLevelsAreExposedInMissionIdOrder()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Data", "LevelData.cs"));

            int methodStart = source.IndexOf("public List<LevelOptions> GetLevels()", StringComparison.Ordinal);
            int methodEnd = source.IndexOf("public LevelOptions GetLevel(int levelId)", methodStart, StringComparison.Ordinal);

            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(methodEnd, Is.GreaterThan(methodStart));

            string method = source.Substring(methodStart, methodEnd - methodStart);
            Assert.That(method, Does.Contain("_levels.OrderBy(level => level.Id).ToList()"),
                "Campaign/challenge callers use mission IDs as level indexes, so persisted row order must not determine the selected mission.");
        }
    }
}
