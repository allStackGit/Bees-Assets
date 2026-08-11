using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class AggressiveInputResponsivenessTests
    {
        [Test]
        public void AggressiveMovementSpreadsPathInitializationAcrossFrames()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Commands", "Aggressive.cs"));

            Assert.That(source, Does.Contain("MoveTowardsEnemiesAcrossFrames"));
            Assert.That(source, Does.Contain("ship.MoveToPoint(target.GetPosition());"));
            Assert.That(source, Does.Contain("yield return null;"),
                "Attack startup must not initialize every ship path request in one Unity update.");
            Assert.That(source, Does.Contain("if (_moveTowardsEnemiesCoroutine == null && !IsDead)"),
                "Recurring aggressive timers must not start overlapping movement-startup coroutines.");
            Assert.That(source, Does.Not.Contain("                        MoveTowardsEnemies();"));
        }
    }
}
