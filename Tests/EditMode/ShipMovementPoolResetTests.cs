using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ShipMovementPoolResetTests
    {
        [Test]
        public void ClearDataResetsMovementRetryAndAsteroidCheckState()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts", "Entities", "Ships", "Ship.Lifecycle.cs"));

            int methodStart = source.IndexOf("public virtual void ClearData()", System.StringComparison.Ordinal);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            int nextMethod = source.IndexOf("protected void FixedUpdate()", methodStart, System.StringComparison.Ordinal);
            Assert.That(nextMethod, Is.GreaterThan(methodStart));
            string clearData = source.Substring(methodStart, nextMethod - methodStart);

            Assert.That(clearData, Does.Contain("_isDoubleCheckingForAsteroids = false;"));
            Assert.That(clearData, Does.Contain("_tryingToFindPathAgain = false;"));
            Assert.That(clearData, Does.Contain("_retries = 0;"));
            Assert.That(clearData, Does.Contain("_remainingEgressWaypoints = 0;"));
        }
    }
}
