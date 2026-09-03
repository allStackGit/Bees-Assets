using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlSpecialActionSmokeTests
    {
        private static string ReadSource(params string[] parts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < parts.Length; i++)
            {
                path = Path.Combine(path, parts[i]);
            }
            return File.ReadAllText(path);
        }

        [Test]
        public void DirectionalBrainMovementHonorsGameplaySpeedAndStopState()
        {
            string source = ReadSource("Scripts", "Entities", "Ships", "Ship.Movement.cs");

            StringAssert.Contains(
                "new Vector2(CurrentSpeed * Mathf.Sin(_tempAngle), -CurrentSpeed * Mathf.Cos(_tempAngle))",
                source);
            StringAssert.Contains("if (HasBrain && !Squad.IsUserControlled)", source);
            StringAssert.Contains("Direction = 360;", source);
        }

        [Test]
        public void DedicatedMlAgentsDoesNotUseLegacyShouldDetonateOwnership()
        {
            string movement = ReadSource("Scripts", "Entities", "Ships", "Ship.Movement.cs");
            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");

            StringAssert.Contains("if (ShouldDetonate && Stage.ActivateBrains)", movement);
            StringAssert.Contains("yellowJacket.TryToDetonate();", agent);
            StringAssert.Contains("fireBarge.Detonate();", agent);
        }

        [Test]
        public void BargeRlChargeLocksHeadingAndUsesAuthoredChargeSpeed()
        {
            string source = ReadSource("Scripts", "Entities", "Ships", "Barge.cs");

            StringAssert.Contains("SetCurrentSpeed(80, 80);", source);
            StringAssert.Contains("if (Stage.IsTrainingNueralNetwork && HasBrain && !Squad.IsUserControlled)", source);
            StringAssert.Contains("Direction = NormalizeDirection(Rotation);", source);
            StringAssert.Contains("StopMoving(\"Pausing to build up steam before charging\");", source);
        }

        [Test]
        public void YellowJacketSpecialRejectsInvalidOrDeadContacts()
        {
            string source = ReadSource("Scripts", "Entities", "Ships", "YellowJacket.cs");

            StringAssert.Contains(
                "if (TouchingShip != null && !TouchingShip.IsDead && TouchingShip.Side != Side)",
                source);
            StringAssert.Contains(
                "if (ContactedShip == null || ContactedShip.IsDead || ContactedShip.Side == Side)",
                source);
        }

        [Test]
        public void HealthBarFillCannotRenderPastItsAuthoredBounds()
        {
            string source = ReadSource("Scripts", "Entities", "Ships", "Ship.Visuals.cs");

            StringAssert.Contains(
                "_healthPercent = MaxHealth > 0 ? Mathf.Clamp01((float)Health / MaxHealth) : 0f;",
                source);
        }
    }
}
