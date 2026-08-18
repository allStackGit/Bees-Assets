using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TrainingResetLifecycleTests
    {
        [Test]
        public void ResetStateCleansRuntimeObjectsBeforeClearingRegistries()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.cs");
            string source = File.ReadAllText(path);

            int reset = source.IndexOf("public void ResetState()");
            int cleanup = source.IndexOf("CleanupRuntimeObjectsForReset();", reset);
            int clearShips = source.IndexOf("Ships.Clear();", reset);
            int clearProjectiles = source.IndexOf("Projectiles.Clear();", reset);

            Assert.That(reset, Is.GreaterThanOrEqualTo(0));
            Assert.That(cleanup, Is.GreaterThan(reset));
            Assert.That(clearShips, Is.GreaterThan(cleanup));
            Assert.That(clearProjectiles, Is.GreaterThan(cleanup));
        }

        [Test]
        public void RuntimeResetCleanupKillsTransientObjectsAndDrainsReleaseQueues()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "GameState.Registry.cs");
            string source = File.ReadAllText(path);

            int start = source.IndexOf("public void CleanupRuntimeObjectsForReset()");
            int releaseMethod = source.IndexOf("public void Release()", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(releaseMethod, Is.GreaterThan(start));

            string method = source.Substring(start, releaseMethod - start);
            StringAssert.Contains("_resetProjectiles.Clear();", method);
            StringAssert.Contains("_resetProjectiles.AddRange(Projectiles);", method);
            StringAssert.Contains("projectile.Kill();", method);
            StringAssert.Contains("_resetObstacles.Clear();", method);
            StringAssert.Contains("_resetObstacles.AddRange(Obstacles);", method);
            StringAssert.Contains("((CollisionAsteroid)obstacle).Kill(true);", method);
            StringAssert.Contains("((MiningAsteroid)obstacle).Kill(true);", method);
            StringAssert.Contains("((AsteroidPiece)obstacle).Kill();", method);
            StringAssert.Contains("Release();", method);
            StringAssert.DoesNotContain("Projectiles.ToList()", method);
            StringAssert.DoesNotContain("Obstacles.ToList()", method);
        }

        [Test]
        public void LevelResetPreservesBoundedSquadRequestHistoryForLateResponses()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Level.Reset.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("request is CommandRequest || request is MatchupStrategyRequest", source);
            StringAssert.Contains("OrderByDescending(request => request.StartTime)", source);
            StringAssert.Contains("Take(StaleSquadRequestHistoryLimit)", source);
            StringAssert.Contains("ConfigData.__PastServerRequests.IntersectWith(staleResponseHistory);", source);
            StringAssert.DoesNotContain(".ToHashSet()", source);
            StringAssert.DoesNotContain("ConfigData.__PastServerRequests.Clear();", source);
        }
    }
}
