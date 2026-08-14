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
            Assert.That(method, Does.Contain("_targetingShips.Clear();"));
            Assert.That(method, Does.Contain("_targetingShips.AddRange(EnemySquad.GetShips());"));
            Assert.That(method, Does.Contain("_tempShips = _targetingShips;"));
            Assert.That(method, Does.Not.Contain("_tempShips = EnemySquad.GetShips();"));
        }

        [Test]
        public void CommandFloatTargetingStrategiesDoNotTruncateComparatorDifferences()
        {
            string method = ExtractMethodBody(_commandSource, "MakeTargetingQueue");
            Assert.That(method, Does.Contain("b.Firepower.CompareTo(a.Firepower)"));
            Assert.That(method, Does.Contain("a.Speed.CompareTo(b.Speed)"));
            Assert.That(method, Does.Contain("CacheTargetingDistances();"));
            Assert.That(method, Does.Contain("_tempShips.Sort(_compareClosestTargetingShips);"));
            Assert.That(_commandSource, Does.Contain("_targetingDistanceKeys[a.Id].CompareTo(_targetingDistanceKeys[b.Id])"));
            Assert.That(method, Does.Not.Contain("(int)(b.Firepower - a.Firepower)"));
            Assert.That(method, Does.Not.Contain("(int)(b.Speed - a.Speed)"));
        }

        [Test]
        public void WeaponDisregardRangeTargetingCopiesEnemySquadList()
        {
            string method = ExtractMethodBody(_weaponSource, "GetPotentialEnemyTargetShips");
            Assert.That(method, Does.Contain("_disregardRangeBuffer.Clear();"));
            Assert.That(method, Does.Contain("_disregardRangeBuffer.AddRange(Ship.Squad.GetCommand().EnemySquad.GetShips());"));
            Assert.That(method, Does.Contain("_queue = _disregardRangeBuffer;"));
            Assert.That(method, Does.Not.Contain("_shipQueue = Ship.Squad.GetCommand().EnemySquad.GetShips();"));
        }

        [Test]
        public void WeaponFloatTargetingStrategiesDoNotTruncateComparatorDifferences()
        {
            string method = ExtractMethodBody(_weaponSource, "MakeSortedTargetingList");
            Assert.That(method, Does.Contain("b.Firepower.CompareTo(a.Firepower)"));
            Assert.That(method, Does.Contain("a.Speed.CompareTo(b.Speed)"));
            Assert.That(method, Does.Contain("CacheTargetDistances();"));
            Assert.That(method, Does.Contain("_sortedQueue.Sort(_compareClosestTargets);"));
            Assert.That(_weaponSource, Does.Contain("_targetDistanceKeys[a.Id].CompareTo(_targetDistanceKeys[b.Id])"));
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
            string selector = ExtractMethodBody(_matchupSource, "SelectByDistance");
            Assert.That(selector, Does.Contain("candidateDistance > selectedDistance"));
            Assert.That(selector, Does.Contain("candidateDistance < selectedDistance"));
            Assert.That(selector, Does.Not.Contain("(int)(a.DistanceToPoint(_location) - b.DistanceToPoint(_location))"));
        }

        [Test]
        public void MatchupTypeStrategiesPreferSquadsContainingMoreOfRequestedType()
        {
            string selector = ExtractMethodBody(_matchupSource, "SelectByTypeCount");
            Assert.That(selector, Does.Contain("candidateCount > selectedCount"));
            Assert.That(selector, Does.Contain("CountShipsOfType(candidate, type)"));
            Assert.That(_matchupSource, Does.Not.Contain("return a.GetShips().Where(s => s.ShipType == _type).ToList().Count - b.GetShips().Where(s => s.ShipType == _type).ToList().Count"));
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = FindMethodDeclaration(source, methodName);
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

        private static int FindMethodDeclaration(string source, string methodName)
        {
            string token = methodName + "(";
            int searchFrom = 0;
            while (searchFrom < source.Length)
            {
                int occurrence = source.IndexOf(token, searchFrom, StringComparison.Ordinal);
                if (occurrence < 0) return -1;

                int lineStart = source.LastIndexOf('\n', occurrence);
                lineStart = lineStart < 0 ? 0 : lineStart + 1;
                string prefix = source.Substring(lineStart, occurrence - lineStart);
                if (prefix.Contains("public ") || prefix.Contains("private ") ||
                    prefix.Contains("protected ") || prefix.Contains("internal "))
                {
                    return occurrence;
                }

                searchFrom = occurrence + token.Length;
            }
            return -1;
        }
    }
}
