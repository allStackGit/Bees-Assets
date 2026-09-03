using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlDiscoveryRewardTests
    {
        private Type _rewardType;

        [SetUp]
        public void SetUp()
        {
            _rewardType = RuntimeAssembly.GetType("RlOneVsOneReward");
        }

        [Test]
        public void PositiveShapingAndDiscoveryRemainBelowVictory()
        {
            float winReward = (float)RuntimeAssembly.GetStaticField(_rewardType, "WinReward");
            float maximumPositiveShaping = (float)RuntimeAssembly.GetStaticField(
                _rewardType,
                "MaximumPositiveShapingReward");
            float totalDiscoveryBudget =
                (float)RuntimeAssembly.GetStaticField(_rewardType, "EnemyShipDiscoveryBudget") +
                (float)RuntimeAssembly.GetStaticField(_rewardType, "MiningAsteroidDiscoveryBudget") +
                (float)RuntimeAssembly.GetStaticField(_rewardType, "StaticObstacleDiscoveryBudget") +
                (float)RuntimeAssembly.GetStaticField(_rewardType, "MapObjectDiscoveryBudget") +
                (float)RuntimeAssembly.GetStaticField(_rewardType, "CollisionAsteroidDiscoveryBudget");

            Assert.That(maximumPositiveShaping, Is.GreaterThan(0f));
            Assert.That(maximumPositiveShaping, Is.LessThan(winReward));
            Assert.That(totalDiscoveryBudget, Is.GreaterThan(0f));
            Assert.That(totalDiscoveryBudget, Is.LessThan(maximumPositiveShaping));
        }

        [Test]
        public void StaticDiscoveryRewardIsValueScaledAndCategoryBounded()
        {
            const float budget = 0.2f;
            float small = (float)RuntimeAssembly.InvokeStatic(
                _rewardType,
                "CalculateStaticDiscoveryReward",
                25,
                100,
                budget);
            float large = (float)RuntimeAssembly.InvokeStatic(
                _rewardType,
                "CalculateStaticDiscoveryReward",
                75,
                100,
                budget);
            float unexpectedSpawn = (float)RuntimeAssembly.InvokeStatic(
                _rewardType,
                "CalculateStaticDiscoveryReward",
                200,
                100,
                budget);

            Assert.That(small, Is.EqualTo(0.05f).Within(0.00001f));
            Assert.That(large, Is.EqualTo(0.15f).Within(0.00001f));
            Assert.That(small + large, Is.EqualTo(budget).Within(0.00001f));
            Assert.That(unexpectedSpawn, Is.LessThanOrEqualTo(budget));
        }

        [Test]
        public void CollisionAsteroidDiscoveryNeverNeedsAnEpisodeStartCount()
        {
            float budget = (float)RuntimeAssembly.GetStaticField(
                _rewardType,
                "CollisionAsteroidDiscoveryBudget");
            float accumulated = 0f;
            float previous = float.MaxValue;
            for (int discoveryIndex = 0; discoveryIndex < 1000; discoveryIndex++)
            {
                float reward = (float)RuntimeAssembly.InvokeStatic(
                    _rewardType,
                    "CalculateCollisionAsteroidDiscoveryReward",
                    6,
                    discoveryIndex);
                Assert.That(reward, Is.GreaterThan(0f));
                Assert.That(reward, Is.LessThan(previous));
                accumulated += reward;
                previous = reward;
            }

            float smallAsteroid = (float)RuntimeAssembly.InvokeStatic(
                _rewardType,
                "CalculateCollisionAsteroidDiscoveryReward",
                1,
                0);
            float largeAsteroid = (float)RuntimeAssembly.InvokeStatic(
                _rewardType,
                "CalculateCollisionAsteroidDiscoveryReward",
                8,
                0);

            Assert.That(accumulated, Is.LessThan(budget));
            Assert.That(largeAsteroid, Is.GreaterThan(smallAsteroid));
        }

        [Test]
        public void PositiveShapingApproachesItsLimitWithoutAHardCutoff()
        {
            float maximum = (float)RuntimeAssembly.GetStaticField(_rewardType, "MaximumPositiveShapingReward");
            float atOne = (float)RuntimeAssembly.InvokeStatic(
                _rewardType,
                "CalculateBoundedPositiveShapingReward",
                1f);
            float atTwo = (float)RuntimeAssembly.InvokeStatic(
                _rewardType,
                "CalculateBoundedPositiveShapingReward",
                2f);
            float atTen = (float)RuntimeAssembly.InvokeStatic(
                _rewardType,
                "CalculateBoundedPositiveShapingReward",
                10f);

            Assert.That(atOne, Is.GreaterThan(0f));
            Assert.That(atTwo, Is.GreaterThan(atOne));
            Assert.That(atTen, Is.GreaterThan(atTwo));
            Assert.That(atTen, Is.LessThan(maximum));
            Assert.That(atTwo - atOne, Is.GreaterThan(0f));
            Assert.That(atTen - atTwo, Is.GreaterThan(0f));
        }

        [Test]
        public void DiscoveryHooksAreGatedByFirstSideWideCacheInsertion()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Levels",
                "GameState.Queries.cs"));

            StringAssert.Contains("bool isFirstSideWideSighting = VisionCache[sideIndex].Add(spotted);", source);
            StringAssert.Contains("if (isFirstSideWideSighting)", source);
            StringAssert.Contains("RecordShipDiscovery(observer, spotted);", source);
            StringAssert.Contains("bool isNew = HiveMindMapObjectCache[sideIndex].Add(mapObject);", source);
            StringAssert.Contains("RecordMapObjectDiscovery(observer, mapObject);", source);
            StringAssert.Contains("bool isNew = HiveMindMiningAsteroidCache[sideIndex].Add(asteroid);", source);
            StringAssert.Contains("RecordMiningAsteroidDiscovery(observer, asteroid);", source);
            StringAssert.Contains("bool isNew = HiveMindObstacleCache[sideIndex].Add(obstacle);", source);
            StringAssert.Contains("RecordObstacleDiscovery(observer, obstacle);", source);
        }
    }
}
