using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CollisionAsteroidCoordinateTests
    {
        [Test]
        public void AsteroidMovementDestinationUsesLevelLocalCoordinates()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "CollisionAsteroid.cs"));
            Assert.That(source, Does.Contain("Utilities.RandomCoordinate(Level, Vector2.zero"));
            Assert.That(source, Does.Not.Contain("Utilities.RandomCoordinate(Level, Level.GetPosition()"));
        }

        [Test]
        public void AsteroidDebrisDestinationUsesLevelLocalCoordinates()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "AsteroidPiece.cs"));
            Assert.That(source, Does.Contain("Utilities.RandomCoordinate(Level, Vector2.zero"));
            Assert.That(source, Does.Not.Contain("Utilities.RandomCoordinate(Level, Level.GetPosition()"));
        }

        [Test]
        public void PooledAsteroidDebrisRestoresAuthoredColor()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "AsteroidPiece.cs"));
            Assert.That(source, Does.Contain("_originalColor = SpriteRenderer.color"));
            Assert.That(source, Does.Contain("SpriteRenderer.color = _originalColor"));
        }

        [Test]
        public void AsteroidPieceDeathTimerWorksWithoutRenderer()
        {
            System.Type asteroidPieceType = System.Type.GetType(
                "Assets.Scripts.Entities.AsteroidPiece, Assembly-CSharp");
            Assert.That(asteroidPieceType, Is.Not.Null);

            GameObject gameObject = new GameObject("headless-asteroid-piece");
            try
            {
                Component piece = gameObject.AddComponent(asteroidPieceType);
                System.Reflection.MethodInfo deathTimer =
                    asteroidPieceType.GetMethod("DeathTimer");
                System.Reflection.FieldInfo halfSeconds =
                    asteroidPieceType.GetField("HalfSeconds");

                Assert.That(deathTimer, Is.Not.Null);
                Assert.That(halfSeconds, Is.Not.Null);
                Assert.DoesNotThrow(() => deathTimer.Invoke(piece, null));
                Assert.That((int)halfSeconds.GetValue(piece), Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}
