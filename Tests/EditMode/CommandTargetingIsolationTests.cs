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
        private string _source;

        [SetUp]
        public void SetUp()
        {
            _source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Command.cs"));
        }

        [Test]
        public void TargetingQueueSortsSnapshotInsteadOfEnemySquadList()
        {
            string method = ExtractMethodBody(_source, "MakeTargetingQueue");
            Assert.That(method, Does.Contain("EnemySquad.GetShips().ToList()"));
            Assert.That(method, Does.Not.Contain("_tempShips = EnemySquad.GetShips();"));
        }

        [Test]
        public void FloatTargetingStrategiesDoNotTruncateComparatorDifferences()
        {
            string method = ExtractMethodBody(_source, "MakeTargetingQueue");
            Assert.That(method, Does.Contain("b.Firepower.CompareTo(a.Firepower)"));
            Assert.That(method, Does.Contain("a.Speed.CompareTo(b.Speed)"));
            Assert.That(method, Does.Contain("DistanceToPoint(a.GetPosition()).CompareTo"));
            Assert.That(method, Does.Not.Contain("(int)(b.Firepower - a.Firepower)"));
            Assert.That(method, Does.Not.Contain("(int)(b.Speed - a.Speed)"));
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
