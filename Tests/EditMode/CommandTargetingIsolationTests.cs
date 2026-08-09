using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CommandTargetingIsolationTests
    {
        private string _commandSource;
        private string _weaponSource;
        private string _matchupSource;

        [SetUp]
        public void SetUp()
        {
            _commandSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Command.cs"));
            _weaponSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Weapons", "Weapon.cs"));
            _matchupSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "MatchupStrategy.cs"));
        }

        [Test]
        public void CommandTargetingQueueSortsSnapshotInsteadOfEnemySquadList()
        {
            string method = ExtractMethodBody(_commandSource, "MakeTargetingQueue");
            Assert.That(method, Does.Contain("EnemySquad.GetShips().ToList()"));
            Assert.That(method, Does.Not.Contain("_tempShips = EnemySquad.GetShips();"));
        }

        [Test]
        public void CommandFloatTargetingStrategiesDoNotTruncateComparatorDifferences()
        {
            string method = ExtractMethodBody(_commandSource, "MakeTargetingQueue");
            Assert.That(method, Does.Contain("b.Firepower.CompareTo(a.Firepower)"));
            Assert.That(method, Does.Contain("a.Speed.CompareTo(b.Speed)"));
            Assert.That(method, Does.Contain("DistanceToPoint(a.GetPosition()).CompareTo"));
            Assert.That(method, Does.Not.Contain("(int)(b.Firepower - a.Firepower)"));
            Assert.That(method, Does.Not.Contain("(int)(b.Speed - a.Speed)"));
        }

        [Test]
        public void WeaponDisregardRangeTargetingCopiesEnemySquadList()
        {
            string method = ExtractMethodBody(_weaponSource, "GetPotentialEnemyTargetShips");
            Assert.That(method, Does.Contain("EnemySquad.GetShips().ToList()"));
            Assert.That(method, Does.Not.Contain("_shipQueue = Ship.Squad.GetCommand().EnemySquad.GetShips();"));
        }

        [Test]
        public void WeaponFloatTargetingStrategiesDoNotTruncateComparatorDifferences()
        {
            string method = ExtractMethodBody(_weaponSource, "MakeSortedTargetingList");
            Assert.That(method, Does.Contain("b.Firepower.CompareTo(a.Firepower)"));
            Assert.That(method, Does.Contain("a.Speed.CompareTo(b.Speed)"));
            Assert.That(method, Does.Contain("DistanceTo(a).CompareTo(DistanceTo(b))"));
            Assert.That(method, Does.Not.Contain("(int) (b.Firepower - a.Firepower)"));
            Assert.That(method, Does.Not.Contain("(int)(a.Speed - b.Speed)"));
        }

        [Test]
        public void MatchupRandomSelectionUsesUniformIndexInsteadOfStableRandomSortKeys()
        {
            string method = ExtractMethodBody(_matchupSource, "SortSquads");
            Assert.That(method, Does.Contain("_queue[Utilities.RandomInt(_queue.Count)]"));
            Assert.That(method, Does.Not.Contain("OrderBy(s => Utilities.RandomInt(2))"));
        }

        [Test]
        public void MatchupDistanceStrategiesDoNotTruncateComparatorDifferences()
        {
            string method = ExtractMethodBody(_matchupSource, "SortSquads");
            Assert.That(method, Does.Contain("a.DistanceToPoint(_location).CompareTo(b.DistanceToPoint(_location))"));
            Assert.That(method, Does.Contain("b.DistanceToPoint(_location).CompareTo(a.DistanceToPoint(_location))"));
            Assert.That(method, Does.Not.Contain("(int)(a.DistanceToPoint(_location) - b.DistanceToPoint(_location))"));
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = source.IndexOf(" " + methodName + "(", StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), $"Could not find method {methodName}.");
            int openingBrace = source.IndexOf('{', signature);
            int depth = 0;
            for (int index = openingBrace; index < source.Length; index++)
            {
                if (source[index] == '{') depth++;
                else if (source[index] == '}' && --depth == 0)
                    return source.Substring(openingBrace, index - openingBrace + 1);
            }

            Assert.Fail($"Method {methodName} has no balanced body.");
            return string.Empty;
        }
    }
}
