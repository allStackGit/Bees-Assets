using System.IO;
using Assets.Scripts.Entities;
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
            GameObject gameObject = new GameObject("headless-asteroid-piece");
            try
            {
                AsteroidPiece piece = gameObject.AddComponent<AsteroidPiece>();

                Assert.DoesNotThrow(piece.DeathTimer);
                Assert.That(piece.HalfSeconds, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}
