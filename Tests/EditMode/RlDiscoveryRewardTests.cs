using System;
using NUnit.Framework;

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
        public void RewardScaleMatchesOnePointTerminalBasis()
        {
            Assert.That(RuntimeAssembly.GetStaticField(_rewardType, "WinReward"), Is.EqualTo(1f));
            Assert.That(RuntimeAssembly.GetStaticField(_rewardType, "LossReward"), Is.EqualTo(-1f));
            Assert.That(RuntimeAssembly.GetStaticField(_rewardType, "TimeoutReward"), Is.EqualTo(-1.1f));
            Assert.That(RuntimeAssembly.GetStaticField(_rewardType, "TsvRewardScale"), Is.EqualTo(0.1f));
            Assert.That(RuntimeAssembly.GetStaticField(_rewardType, "MaximumEpisodeTimePenalty"), Is.EqualTo(0.01f));
            Assert.That(RuntimeAssembly.GetStaticField(_rewardType, "MaximumPositiveShapingReward"), Is.EqualTo(0.2f));
            Assert.That(RuntimeAssembly.GetStaticField(_rewardType, "EnemyShipDiscoveryBudget"), Is.EqualTo(0.006f));
            Assert.That(RuntimeAssembly.GetStaticField(_rewardType, "MiningAsteroidDiscoveryBudget"), Is.EqualTo(0.0015f));
            Assert.That(RuntimeAssembly.GetStaticField(_rewardType, "StaticObstacleDiscoveryBudget"), Is.EqualTo(0.0015f));
            Assert.That(RuntimeAssembly.GetStaticField(_rewardType, "MapObjectDiscoveryBudget"), Is.EqualTo(0.001f));
            Assert.That(RuntimeAssembly.GetStaticField(_rewardType, "CollisionAsteroidDiscoveryBudget"), Is.EqualTo(0.0025f));
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
        public void EconomicMiningValueIsLinearUncappedAndRelativeToReceivingSide()
        {
            float equalToStarting = (float)RuntimeAssembly.InvokeStatic(
                _rewardType,
                "CalculateEconomicValueReward",
                500,
                500);
            float tripleStarting = (float)RuntimeAssembly.InvokeStatic(
                _rewardType,
                "CalculateEconomicValueReward",
                1500,
                500);
            float sameCargoForLargerSide = (float)RuntimeAssembly.InvokeStatic(
                _rewardType,
                "CalculateEconomicValueReward",
                1500,
                1500);

            Assert.That(equalToStarting, Is.EqualTo(1f));
            Assert.That(tripleStarting, Is.EqualTo(3f));
            Assert.That(sameCargoForLargerSide, Is.EqualTo(1f));
            Assert.That(tripleStarting, Is.GreaterThan(
                (float)RuntimeAssembly.GetStaticField(_rewardType, "WinReward")),
                "Campaign economic value must be allowed to outweigh one battle victory.");
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
            double maximum = (float)RuntimeAssembly.GetStaticField(_rewardType, "MaximumPositiveShapingReward");
            double atOne = (double)RuntimeAssembly.InvokeStatic(
                _rewardType,
                "CalculateBoundedPositiveShapingReward",
                1d);
            double atTwo = (double)RuntimeAssembly.InvokeStatic(
                _rewardType,
                "CalculateBoundedPositiveShapingReward",
                2d);
            double atTen = (double)RuntimeAssembly.InvokeStatic(
                _rewardType,
                "CalculateBoundedPositiveShapingReward",
                10d);
            double veryLateIncrement = (double)RuntimeAssembly.InvokeStatic(
                _rewardType,
                "CalculateBoundedPositiveShapingIncrement",
                1000000d,
                1d);

            Assert.That(atOne, Is.GreaterThan(0d));
            Assert.That(atTwo, Is.GreaterThan(atOne));
            Assert.That(atTen, Is.GreaterThan(atTwo));
            Assert.That(atTen, Is.LessThan(maximum));
            Assert.That(atTwo - atOne, Is.GreaterThan(0d));
            Assert.That(atTen - atTwo, Is.GreaterThan(0d));
            Assert.That(veryLateIncrement, Is.GreaterThan(0d));
        }

    }
}
