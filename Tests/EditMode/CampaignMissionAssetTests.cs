using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesCampaignScenario")]
    public class CampaignMissionAssetTests
    {
        [TestCase("Bluer Pastures")]
        [TestCase("Pressing Forward")]
        [TestCase("Minesweeper")]
        [TestCase("Bee-noculars")]
        [TestCase("On the Offensive")]
        [TestCase("On the Defensive")]
        public void AuthoredCampaignObstacleLayoutLoadsFromResources(string resourceName)
        {
            GameObject prefab = Resources.Load<GameObject>($"Obstacles/{resourceName}");
            try
            {
                Assert.That(prefab, Is.Not.Null,
                    $"Campaign obstacle layout Resources/Obstacles/{resourceName} is missing.");
            }
            finally
            {
                if (prefab != null)
                {
                    Resources.UnloadAsset(prefab);
                }
            }
        }

        [Test]
        public void MinesweeperContainsThirtyLinkedFireTankDemolitionCharges()
        {
            GameObject prefab = Resources.Load<GameObject>("Obstacles/Minesweeper");
            Assert.That(prefab, Is.Not.Null);

            try
            {
                Type canisterBombType = RuntimeAssembly.GetType("Assets.Scripts.Entities.CanisterBomb");
                Type obstacleType = RuntimeAssembly.GetType("Assets.Scripts.Entities.Obstacle");
                Assert.That(canisterBombType, Is.Not.Null);
                Assert.That(obstacleType, Is.Not.Null);

                Component[] fireTanks = prefab.GetComponentsInChildren(canisterBombType, true);
                Component[] obstacles = prefab.GetComponentsInChildren(obstacleType, true);

                Assert.That(fireTanks.Length, Is.EqualTo(30),
                    "Minesweeper's route-demolition design depends on the authored set of 30 Fire Tanks.");
                Assert.That(obstacles.Length, Is.GreaterThanOrEqualTo(30),
                    "Minesweeper must contain enough obstacle geometry for the demolition network.");

                foreach (Component fireTank in fireTanks)
                {
                    object targetObstacle = RuntimeAssembly.GetField(fireTank, "TargetObstacle");
                    Assert.That(targetObstacle, Is.Not.Null,
                        $"Fire Tank {fireTank.gameObject.name} has no obstacle to demolish.");
                    Assert.That(obstacleType.IsInstanceOfType(targetObstacle), Is.True,
                        $"Fire Tank {fireTank.gameObject.name} targets something other than an Obstacle.");
                }

                int distinctTargets = fireTanks
                    .Select(tank => RuntimeAssembly.GetField(tank, "TargetObstacle"))
                    .Distinct()
                    .Count();
                Assert.That(distinctTargets, Is.EqualTo(30),
                    "Each authored Minesweeper Fire Tank should control its own barrier section.");
            }
            finally
            {
                Resources.UnloadAsset(prefab);
            }
        }

        [Test]
        public void MinesweeperFireTanksRemainDangerousControlledDemolitions()
        {
            GameObject prefab = Resources.Load<GameObject>("Obstacles/Minesweeper");
            Assert.That(prefab, Is.Not.Null);

            try
            {
                Type canisterBombType = RuntimeAssembly.GetType("Assets.Scripts.Entities.CanisterBomb");
                Component fireTank = prefab.GetComponentsInChildren(canisterBombType, true).First();

                Assert.That((int)RuntimeAssembly.GetField(fireTank, "MaxHealth"), Is.EqualTo(250));
                Assert.That((int)RuntimeAssembly.GetField(fireTank, "Power"), Is.EqualTo(350));
                Assert.That(RuntimeAssembly.GetField(fireTank, "TargetObstacle"), Is.Not.Null);
            }
            finally
            {
                Resources.UnloadAsset(prefab);
            }
        }

        [Test]
        public void TitaniaHasBothAuthoredCampaignBattlefields()
        {
            GameObject minesweeper = Resources.Load<GameObject>("Obstacles/Minesweeper");
            GameObject beenoculars = Resources.Load<GameObject>("Obstacles/Bee-noculars");
            try
            {
                Assert.That(minesweeper, Is.Not.Null);
                Assert.That(beenoculars, Is.Not.Null);
                Assert.That(minesweeper, Is.Not.SameAs(beenoculars));
            }
            finally
            {
                if (minesweeper != null) Resources.UnloadAsset(minesweeper);
                if (beenoculars != null) Resources.UnloadAsset(beenoculars);
            }
        }
    }
}
