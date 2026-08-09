using System;
using System.Linq;
using System.Reflection;
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
            Assert.That(prefab, Is.Not.Null,
                $"Campaign obstacle layout Resources/Obstacles/{resourceName} is missing.");
        }

        [Test]
        public void MinesweeperContainsThirtyLinkedFireTankDemolitionCharges()
        {
            GameObject prefab = Resources.Load<GameObject>("Obstacles/Minesweeper");
            Assert.That(prefab, Is.Not.Null);

            GameObject instance = UnityEngine.Object.Instantiate(prefab);
            try
            {
                Type levelType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Level");
                MethodInfo repair = levelType.GetMethod("RepairMinesweeperDemolitionTargets",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(repair, Is.Not.Null,
                    "Minesweeper must repair stale/duplicated demolition links before gameplay.");
                repair.Invoke(null, new object[] { instance.transform });

                Type canisterBombType = RuntimeAssembly.GetType("Assets.Scripts.Entities.CanisterBomb");
                Type obstacleType = RuntimeAssembly.GetType("Assets.Scripts.Entities.Obstacle");
                Assert.That(canisterBombType, Is.Not.Null);
                Assert.That(obstacleType, Is.Not.Null);

                Component[] fireTanks = instance.GetComponentsInChildren(canisterBombType, true);
                Component[] obstacles = instance.GetComponentsInChildren(obstacleType, true);

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

                // The prefab instance has not run MapObject.Setup, so every target still has
                // runtime Id 0. Compare each target's Transform rather than MapObject's
                // game-ID equality when verifying authored/runtime prefab linkage.
                int distinctTargets = fireTanks
                    .Select(tank => (Component)RuntimeAssembly.GetField(tank, "TargetObstacle"))
                    .Select(target => target.transform)
                    .Distinct()
                    .Count();
                Assert.That(distinctTargets, Is.EqualTo(30),
                    "Each runtime Minesweeper Fire Tank must control its own barrier section.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void MinesweeperFireTanksRemainDangerousControlledDemolitions()
        {
            GameObject prefab = Resources.Load<GameObject>("Obstacles/Minesweeper");
            Assert.That(prefab, Is.Not.Null);

            Type canisterBombType = RuntimeAssembly.GetType("Assets.Scripts.Entities.CanisterBomb");
            Component fireTank = prefab.GetComponentsInChildren(canisterBombType, true).First();

            Assert.That((int)RuntimeAssembly.GetField(fireTank, "MaxHealth"), Is.EqualTo(250));
            Assert.That((int)RuntimeAssembly.GetField(fireTank, "Power"), Is.EqualTo(350));
            Assert.That(RuntimeAssembly.GetField(fireTank, "TargetObstacle"), Is.Not.Null);
        }

        [Test]
        public void TitaniaHasBothAuthoredCampaignBattlefields()
        {
            GameObject minesweeper = Resources.Load<GameObject>("Obstacles/Minesweeper");
            GameObject beenoculars = Resources.Load<GameObject>("Obstacles/Bee-noculars");

            Assert.That(minesweeper, Is.Not.Null);
            Assert.That(beenoculars, Is.Not.Null);
            Assert.That(minesweeper, Is.Not.SameAs(beenoculars));
        }
    }
}
