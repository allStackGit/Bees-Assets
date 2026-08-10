using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SwipeCommandGeometryTests
    {
        [Test]
        public void SwipeDestinationIsCenteredOnEnemySquad()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "SwipeSquad.cs"));

            Assert.That(source, Does.Contain("_swipeDestination = EnemySquad.CirclePoint(_angle, _distance)"));
            Assert.That(source, Does.Not.Contain("_swipeDestination = GetSquad().CirclePoint(_angle, _distance)"));
        }
    }
}
