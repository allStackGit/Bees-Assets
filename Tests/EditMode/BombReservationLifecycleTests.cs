using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class BombReservationLifecycleTests
    {
        private static string Read(params string[] path) => File.ReadAllText(Path.Combine(Application.dataPath, Path.Combine(path)));

        [Test]
        public void BombOwnsReservationUntilDeliveryOrCancellation()
        {
            string bomb = Read("Scripts", "Entities", "Ships", "Weapons", "Bomb.cs");
            Assert.That(bomb, Does.Contain("private ShipDamageStatus _reservedDamageStatus"));

            int setTarget = bomb.IndexOf("protected override void SetTargetShip", StringComparison.Ordinal);
            int release = bomb.IndexOf("ReleaseTargetReservation();", setTarget, StringComparison.Ordinal);
            int reserve = bomb.IndexOf("_reservedDamageStatus = Level.State.GetShipDamageStatus", setTarget, StringComparison.Ordinal);
            Assert.That(release, Is.GreaterThan(setTarget));
            Assert.That(reserve, Is.GreaterThan(release));

            Assert.That(bomb, Does.Contain("public void TransferTargetReservation()"));
            Assert.That(bomb, Does.Contain("public void ReleaseTargetReservation()"));
        }

        [Test]
        public void StrikerTransfersRenderedReservationAndReleasesTrainingReservation()
        {
            string striker = Read("Scripts", "Entities", "Ships", "Striker.cs");
            Assert.That(striker, Does.Contain("_bomb.Setup(Level, Bomb"));
            Assert.That(striker, Does.Contain("Bomb.TransferTargetReservation();"));

            int logBombDamage = striker.IndexOf("public void LogBombDamage()", StringComparison.Ordinal);
            int release = striker.IndexOf("Bomb.ReleaseTargetReservation();", logBombDamage, StringComparison.Ordinal);
            int applyDamage = striker.IndexOf("LogAttackingDamage", logBombDamage, StringComparison.Ordinal);
            Assert.That(release, Is.GreaterThan(logBombDamage));
            Assert.That(applyDamage, Is.GreaterThan(release));

            Assert.That(striker, Does.Contain("public override void Kill"));
        }

        [Test]
        public void ContactBombersAndCommandFinalizationReleaseUndeliveredReservations()
        {
            string yellowJacket = Read("Scripts", "Entities", "Ships", "YellowJacket.cs");
            string fireBarge = Read("Scripts", "Entities", "Ships", "FireBarge.cs");
            string bombingRun = Read("Scripts", "Levels", "Commands", "BombingRun.cs");

            Assert.That(yellowJacket, Does.Contain("Bomb.ReleaseTargetReservation();"));
            Assert.That(fireBarge, Does.Contain("Bomb.ReleaseTargetReservation();"));
            Assert.That(bombingRun, Does.Contain("public override void SetFinalize(string cause)"));
            Assert.That(bombingRun, Does.Contain("bomb?.ReleaseTargetReservation();"));
        }
    }
}
