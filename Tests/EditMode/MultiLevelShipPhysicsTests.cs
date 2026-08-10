using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class MultiLevelShipPhysicsTests
    {
        [Test]
        public void MovementObstacleCastTranslatesLevelLocalShipPositionToWorldSpace()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Geometry.cs"));
            int method = source.IndexOf("public Collider2D GetObstacleInPath(Vector2 destination)");
            int nextMethod = source.IndexOf("private Collider2D GetLiveObstacleFromBoxCast", method);
            string body = source.Substring(method, nextMethod - method);

            Assert.That(body, Does.Contain("GetPosition() + Level.GetPosition()"));
            Assert.That(body, Does.Not.Contain("GetLiveObstacleFromBoxCast(\n                GetPosition(),"));
        }

        [Test]
        public void MovementMarkerUsesItsLevelLocalDestinationUnderMapParent()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Movement.cs"));
            int method = source.IndexOf("private void MoveMovementMarker()");
            int nextMethod = source.IndexOf("public void SetTargetCoordinates", method);
            string body = source.Substring(method, nextMethod - method);

            Assert.That(body, Does.Contain("MovementMarker.transform.localPosition = FinalDestination"));
            Assert.That(body, Does.Not.Contain("MovementMarker.transform.position = FinalDestination"));
        }
    }
}
