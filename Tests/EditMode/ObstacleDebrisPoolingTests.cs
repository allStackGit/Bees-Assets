using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ObstacleDebrisPoolingTests
    {
        [Test]
        public void BreakApartUsesStageDebrisPool()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Obstacle.cs");
            string source = File.ReadAllText(path);
            int start = source.IndexOf("public virtual void BreakApart(");
            int end = source.IndexOf("private static float NextFloat", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));

            string method = source.Substring(start, end - start);
            StringAssert.Contains("ObstacleDebrisPool.GetOrCreate(Stage)", method);
            StringAssert.Contains("ObstacleDebrisPiece debrisPiece = debrisPool.Get();", method);
            StringAssert.DoesNotContain("new GameObject(\"Obstacle Debris\")", method);
            StringAssert.DoesNotContain("AddComponent<ObstacleDebrisPiece>()", method);
        }

        [Test]
        public void DebrisPieceReusesRendererAndReturnsToPool()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "ObstacleDebrisPiece.cs");
            string source = File.ReadAllText(path);
            int setupStart = source.IndexOf("public void Setup(");
            int updateStart = source.IndexOf("private void Update()", setupStart);
            Assert.That(setupStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(updateStart, Is.GreaterThan(setupStart));

            string setup = source.Substring(setupStart, updateStart - setupStart);
            StringAssert.Contains("private void Awake()", source);
            StringAssert.Contains("_age = 0f;", setup);
            StringAssert.DoesNotContain("new GameObject", setup);
            StringAssert.DoesNotContain("AddComponent<SpriteRenderer>()", setup);
            StringAssert.Contains("_pool.Release(this);", source);
        }
    }
}
