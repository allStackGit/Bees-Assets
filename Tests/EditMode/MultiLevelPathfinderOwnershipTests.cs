using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class MultiLevelPathfinderOwnershipTests
    {
        private static string Read(params string[] path)
        {
            return File.ReadAllText(Path.Combine(Application.dataPath, Path.Combine(path)));
        }

        [Test]
        public void ObstacleDiscoveryIsScopedToOwningLevel()
        {
            string obstacles = Read("Scripts", "Levels", "Pathfinder.Obstacles.cs");
            string scope = Read("Scripts", "Levels", "PathfinderObstacleScope.cs");

            Assert.That(obstacles, Does.Contain("PathfinderObstacleScope.GetActiveObstacleObjects(Level)"));
            Assert.That(obstacles, Does.Not.Contain("GameObject.FindGameObjectsWithTag"));
            Assert.That(scope, Does.Contain("GetComponentsInChildren<Obstacle>(false)"));
        }

        [Test]
        public void PhysicsQueriesExplicitlyCrossWorldLevelBoundary()
        {
            string obstacles = Read("Scripts", "Levels", "Pathfinder.Obstacles.cs");

            Assert.That(obstacles, Does.Contain("PathfinderObstacleScope.WorldToLevel"));
            Assert.That(obstacles, Does.Contain("PathfinderObstacleScope.LevelToWorld"));
            Assert.That(obstacles, Does.Contain("collider.OverlapPoint(worldCenter)"));
        }

        [Test]
        public void TrainingResetRetiresPathfinderInstance()
        {
            string state = Read("Scripts", "Levels", "GameState.cs");
            Assert.That(state, Does.Contain("Level.Pathfinder = null;"));
        }

        [Test]
        public void PathfinderResponsibilitiesRemainSplitIntoFocusedPartials()
        {
            string core = Read("Scripts", "Levels", "Pathfinder.cs");
            string search = Read("Scripts", "Levels", "Pathfinder.Search.cs");

            Assert.That(core, Does.Contain("public partial class Pathfinder"));
            Assert.That(core, Does.Not.Contain("public void InitializeMap()"));
            Assert.That(search, Does.Contain("ReferenceIdentityComparer<Ship>.Instance"));
        }
    }
}
