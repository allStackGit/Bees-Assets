using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class LevelScopedSaveOwnershipTests
    {
        [Test]
        public void SaveLevelEnumeratesOnlyCurrentLevelObstacles()
        {
            string menus = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "UI Components", "GameMenus.cs"));
            string scope = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "PathfinderObstacleScope.cs"));

            Assert.That(menus, Does.Contain("PathfinderObstacleScope.GetActiveObstacleObjects(CurrentLevel)"));
            Assert.That(menus, Does.Not.Contain("GameObject.FindGameObjectsWithTag(\"Obstacle\")"));
            Assert.That(scope, Does.Contain("level.Map.Transform"));
            Assert.That(scope, Does.Contain("GetComponentsInChildren<Obstacle>(false)"));
        }
    }
}
