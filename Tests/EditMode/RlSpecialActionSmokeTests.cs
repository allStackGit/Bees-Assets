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
        public void RlDirectionalMovementHonorsGameplaySpeedAndStopState()
        {
            string source = ReadSource("Scripts", "Entities", "Ships", "Ship.Movement.cs");

            StringAssert.Contains(
                "new Vector2(CurrentSpeed * Mathf.Sin(_tempAngle), -CurrentSpeed * Mathf.Cos(_tempAngle))",
                source);
            StringAssert.Contains("if (IsRlPolicyControlled && !Squad.IsUserControlled)", source);
            StringAssert.Contains("RlMovementDirection = 360;", source);
        }

        [Test]
        public void LegacyBrainStateIsRemovedAndSpecialActionsRemainPolicyOwned()
        {
            string ship = ReadSource("Scripts", "Entities", "Ships", "Ship.cs");
            string movement = ReadSource("Scripts", "Entities", "Ships", "Ship.Movement.cs");
            string stage = ReadSource("Scripts", "Scenes", "Stage.cs");
            string bootstrap = ReadSource("Scripts", "Scenes", "RlOneVsOneTrainingBootstrap.cs");
            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");

            StringAssert.DoesNotContain("ActivateBrains", stage);
            StringAssert.DoesNotContain("ActivateBrains", bootstrap);
            StringAssert.DoesNotContain("ShouldDetonate", ship);
            StringAssert.DoesNotContain("ShouldDetonate", movement);
            StringAssert.DoesNotContain("RLShootingStrategy", ship);
            StringAssert.DoesNotContain("RLSide", ship);
            StringAssert.DoesNotContain("RLHealth", ship);
            StringAssert.DoesNotContain("RLShipType", ship);
            StringAssert.Contains("yellowJacket.TryToDetonate();", agent);
            StringAssert.Contains("fireBarge.Detonate();", agent);
        }

        [Test]
        public void BargeRlSpecialActionLeavesChargeReservationToBarge()
        {
            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");

            int start = agent.IndexOf("private void ApplySpecialAction()");
            int end = agent.IndexOf("private void TryApplyMiningAction()", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));

            string method = agent.Substring(start, end - start);
            StringAssert.Contains("barge.StartCoroutine(barge.ChargeForward(FindNearestVisibleEnemy()));", method);
            StringAssert.DoesNotContain("TryReserveCharge", method);
        }

        [Test]
        public void BargeRlChargeLocksHeadingAndUsesAuthoredChargeSpeed()
        {
            string source = ReadSource("Scripts", "Entities", "Ships", "Barge.cs");

            StringAssert.Contains("SetCurrentSpeed(80, 80);", source);
            StringAssert.Contains("if (Stage.IsTrainingNueralNetwork && IsRlPolicyControlled && !Squad.IsUserControlled)", source);
            StringAssert.Contains("RlMovementDirection = NormalizeDirection(Rotation);", source);
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
