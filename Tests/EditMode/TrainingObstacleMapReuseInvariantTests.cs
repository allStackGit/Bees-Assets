using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TrainingObstacleMapReuseInvariantTests
    {
        [Test]
        public void ResetReleasesPooledObstacleLayoutBeforeReturningMap()
        {
            string reset = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Reset.cs"));
            int resetStart = reset.IndexOf("public void ResetGameData()", System.StringComparison.Ordinal);
            int cleanup = reset.IndexOf("ReleasePooledObstacleLayoutForReset();", resetStart, System.StringComparison.Ordinal);
            int mapReturn = reset.IndexOf("Stage.Pool.ReturnMapToPool(Map);", resetStart, System.StringComparison.Ordinal);

            Assert.That(resetStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(cleanup, Is.GreaterThan(resetStart),
                "A reset must release the current generated obstacle layout.");
            Assert.That(mapReturn, Is.GreaterThan(cleanup),
                "Pooled map children must be detached before the map is reused.");
            Assert.That(reset, Does.Contain("obstaclePool.ReleaseObstacle(ObstacleMap.Obstacles[i])"));
            Assert.That(reset, Does.Contain("obstaclePool.ReleaseBackground(ObstacleMap.ObstacleBackground)"));
        }
    }
}
