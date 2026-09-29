using System;
using System.Collections;
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
        public void FixedGeometryUsesSameBoundsAndRequiresEvaluatorMode()
        {
            object geometry = _parseFixed.Invoke(null, new object[]
            {
                new[]
                {
                    "game.exe",
                    "--bees-rl-evaluator",
                    "--bees-rl-fixed-geometry=64,0.375"
                }
            });
            Assert.That(geometry, Is.Not.Null);
            Assert.That((float)RuntimeAssembly.GetField(geometry, "MapSize"), Is.EqualTo(64f));
            Assert.That(
                (float)RuntimeAssembly.GetField(geometry, "SpawnSeparationRatio"),
                Is.EqualTo(0.375f));

            TargetInvocationException unsafeGeometry = Assert.Throws<TargetInvocationException>(() =>
                _parseFixed.Invoke(null, new object[]
                {
                    new[]
                    {
                        "game.exe",
                        "--bees-rl-evaluator",
                        "--bees-rl-fixed-geometry=64,0.9"
                    }
                }));
            Assert.That(unsafeGeometry.InnerException, Is.TypeOf<ArgumentException>());

            TargetInvocationException trainingOverride = Assert.Throws<TargetInvocationException>(() =>
                _parseFixed.Invoke(null, new object[]
                {
                    new[] { "game.exe", "--bees-rl-fixed-geometry=64,0.375" }
                }));
            Assert.That(trainingOverride.InnerException, Is.TypeOf<ArgumentException>());
            Assert.That(
                trainingOverride.InnerException.Message,
                Does.Contain("reserved for authoritative evaluator runs"));
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

    }
}
