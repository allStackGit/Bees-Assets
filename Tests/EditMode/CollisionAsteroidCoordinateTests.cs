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
    }
}
