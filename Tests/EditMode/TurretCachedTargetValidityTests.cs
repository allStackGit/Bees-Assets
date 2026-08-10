using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TurretCachedTargetValidityTests
    {
        [Test]
        public void AimedAlternateTargetMustStillBeValidBeforeImmediateFire()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Weapons", "Turret.Targeting.cs"));
            int method = source.IndexOf("protected Ship GetAimedAtTarget()");
            int nextMethod = source.IndexOf("protected void Fire()", method);
            string body = source.Substring(method, nextMethod - method);

            Assert.That(body, Does.Contain("IsShipValidTarget(ship)"));
            Assert.That(body, Does.Not.Contain("if (!ship.IsDead &&"));
        }
    }
}
