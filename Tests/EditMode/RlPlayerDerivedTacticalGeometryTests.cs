using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlPlayerDerivedTacticalGeometryTests
    {
        private Type _geometryType;
        private MethodInfo _parseCatalog;
        private MethodInfo _parseFixed;

        [SetUp]
        public void SetUp()
        {
            _geometryType = RuntimeAssembly.GetType("RlPlayerDerivedTacticalGeometry");
            _parseCatalog = _geometryType.GetMethod(
                "ParseCatalogForTests",
                BindingFlags.Static | BindingFlags.NonPublic);
            _parseFixed = _geometryType.GetMethod(
                "ParseFixedForTests",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(_parseCatalog, Is.Not.Null);
            Assert.That(_parseFixed, Is.Not.Null);
        }

        [Test]
        public void CatalogParsesIndependentScenarioMapAndSeparationGeometry()
        {
            object parsed = _parseCatalog.Invoke(null, new object[]
            {
                new[]
                {
                    "game.exe",
                    "--bees-adversarial-geometry-catalog=" +
                    "adv-aaaaaaaaaaaaaaaaaaaaaaaa:96,0.25;" +
                    "adv-bbbbbbbbbbbbbbbbbbbbbbbb:48,0.5"
                }
            });
            IDictionary catalog = parsed as IDictionary;
            Assert.That(catalog, Is.Not.Null);
            Assert.That(catalog.Count, Is.EqualTo(2));

            object first = catalog["adv-aaaaaaaaaaaaaaaaaaaaaaaa"];
            Assert.That((float)RuntimeAssembly.GetField(first, "MapSize"), Is.EqualTo(96f));
            Assert.That(
                (float)RuntimeAssembly.GetField(first, "SpawnSeparationRatio"),
                Is.EqualTo(0.25f));
        }

        [Test]
        public void GeometryRejectsMalformedUnsafeOrDuplicateEntries()
        {
            AssertCatalogFails("adv-aaaaaaaaaaaaaaaaaaaaaaaa:9,0.5");
            AssertCatalogFails("adv-aaaaaaaaaaaaaaaaaaaaaaaa:96,0");
            AssertCatalogFails("adv-aaaaaaaaaaaaaaaaaaaaaaaa:96,0.751");
            AssertCatalogFails("adv-nothex:96,0.5");
            AssertCatalogFails(
                "adv-aaaaaaaaaaaaaaaaaaaaaaaa:96,0.5;" +
                "adv-aaaaaaaaaaaaaaaaaaaaaaaa:48,0.25");
        }

        [Test]
        public void FixedGeometryUsesSameBoundsForPairedDiagnosticEvaluation()
        {
            object geometry = _parseFixed.Invoke(null, new object[]
            {
                new[] { "game.exe", "--bees-rl-fixed-geometry=64,0.375" }
            });
            Assert.That(geometry, Is.Not.Null);
            Assert.That((float)RuntimeAssembly.GetField(geometry, "MapSize"), Is.EqualTo(64f));
            Assert.That(
                (float)RuntimeAssembly.GetField(geometry, "SpawnSeparationRatio"),
                Is.EqualTo(0.375f));

            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                _parseFixed.Invoke(null, new object[]
                {
                    new[] { "game.exe", "--bees-rl-fixed-geometry=64,0.9" }
                }));
            Assert.That(exception.InnerException, Is.TypeOf<ArgumentException>());
        }

        [Test]
        public void MapSetupPreparesExactlyOneEpisodeBeforeReadingScenarioGeometry()
        {
            string mapState = ReadSource("Scripts", "Scenes", "RlOneVsOneArenaMapSizeState.cs");
            int prepare = mapState.IndexOf(
                "RlOneVsOnePerArenaMatchups.PrepareEpisode(level);",
                StringComparison.Ordinal);
            int mapSelection = mapState.IndexOf(
                "EpisodeMapSizes.TryGetValue(level",
                StringComparison.Ordinal);
            int geometry = mapState.IndexOf(
                "RlPlayerDerivedTacticalGeometry.TryGetCurrent",
                StringComparison.Ordinal);
            Assert.That(prepare, Is.GreaterThanOrEqualTo(0));
            Assert.That(mapSelection, Is.GreaterThan(prepare));
            Assert.That(geometry, Is.GreaterThan(prepare));
            Assert.That(mapState, Does.Contain("geometry.SpawnSeparationRatio * 0.5f"));

            string perArena = ReadSource("Scripts", "Scenes", "RlOneVsOnePerArenaMatchups.cs");
            Assert.That(perArena, Does.Contain("PreparedEpisodes.Contains(level)"));
            Assert.That(perArena, Does.Contain("PreparedEpisodes.Add(level)"));
            Assert.That(perArena, Does.Contain("PreparedEpisodes.Remove(level)"));

            string squadSetup = ReadSource("Scripts", "Levels", "Level.RandomSquadSetup.cs");
            Assert.That(squadSetup, Does.Contain("RlOneVsOnePerArenaMatchups.PrepareEpisode(this)"),
                "Existing ship setup should keep its preparation call; the per-arena owner makes it idempotent.");
        }

        private void AssertCatalogFails(string encoded)
        {
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                _parseCatalog.Invoke(null, new object[]
                {
                    new[] { "game.exe", "--bees-adversarial-geometry-catalog=" + encoded }
                }));
            Assert.That(exception.InnerException, Is.TypeOf<ArgumentException>());
        }

        private static string ReadSource(params string[] parts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < parts.Length; i++)
            {
                path = Path.Combine(path, parts[i]);
            }
            return File.ReadAllText(path);
        }
    }
}
