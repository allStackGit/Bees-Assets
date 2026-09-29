using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class StrikerTrainingBombLifecycleTests
    {
        [Test]
        public void DelayedTrainingBombSurvivesShooterDeathAndRejectsReusedWrappers()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Striker.cs"));

            Assert.That(source, Does.Contain("private void ScheduleTrainingBombDamage(Ship target)"));
            Assert.That(source, Does.Contain("long shooterFleetShipId = shooterFleetShip != null ? shooterFleetShip.Id : 0;"));
            Assert.That(source, Does.Contain("long targetRuntimeId = target.Id;"));
            Assert.That(source, Does.Contain("target.Id != targetRuntimeId"),
                "A delayed training bomb must not damage a new occupant of a recycled target wrapper.");
            Assert.That(source, Does.Contain("shooter.FleetShip.Id == shooterFleetShipId"),
                "Delayed attribution must not be stolen by a new occupant of a recycled Striker wrapper.");

            Assert.That(source, Does.Contain("ScaledTimer damageTimer = new ScaledTimer("));
            Assert.That(source, Does.Contain("Level.AddTimer(damageTimer);"),
                "The dropped bomb fuse must be owned by the Level rather than by the Striker lifecycle.");
            Assert.That(source, Does.Not.Contain("private ScaledTimer _damageTimer"),
                "A ship-owned damage timer would be canceled/reused with the Striker and make an in-flight bomb disappear.");

            int kill = source.IndexOf("public override void Kill", StringComparison.Ordinal);
            Assert.That(kill, Is.GreaterThanOrEqualTo(0));
            string killBody = source.Substring(kill);
            Assert.That(killBody, Does.Not.Contain("CancelTimer(_damageTimer)"));
            Assert.That(killBody, Does.Contain("Bomb.ReleaseTargetReservation();"),
                "Death before delivery must still release an undropped bomb reservation.");
        }
    }
}
