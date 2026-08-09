using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class PooledLifetimeAuditTests
    {
        private string _fogSource;
        private string _fireBargeSource;
        private string _chargingBarSource;

        [SetUp]
        public void SetUp()
        {
            _fogSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "FogOfWarVision.cs"));
            _fireBargeSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "FireBarge.cs"));
            _chargingBarSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "ChargingBar.cs"));
        }

        [Test]
        public void ReusedFogVisionCancelsPreviousDeathFadeAndResetsScale()
        {
            string activate = ExtractMethodBody(_fogSource, "Activate");
            string kill = ExtractMethodBody(_fogSource, "Kill");

            Assert.That(activate, Does.Contain("CancelTimer(_shrinkVisionStartTimer)"));
            Assert.That(activate, Does.Contain("CancelTimer(_shrinkVisionTimer)"));
            Assert.That(activate, Does.Contain("Transform.localScale = new Vector3(Range, Range, 0)"));
            Assert.That(kill, Does.Contain("Transform.position = Ship.GetPosition()"));
            Assert.That(kill, Does.Contain("enabled = false"));
        }

        [Test]
        public void FireBargeCannotReturnToPoolBeforeExplosionLifetimeEnds()
        {
            string kill = ExtractMethodBody(_fireBargeSource, "Kill");
            string delayedKill = ExtractMethodBody(_fireBargeSource, "DelayedKill");

            Assert.That(kill, Does.Contain("Level.State.ShipsToRelease.Remove(this)"));
            Assert.That(kill, Does.Contain("_waitingForDelayedRelease = true"));
            Assert.That(kill, Does.Contain("_delayedKillTimer.Reuse(5f, DelayedKill)"));
            Assert.That(delayedKill, Does.Contain("Level.State.ShipsToRelease.Add(this)"));
            Assert.That(delayedKill, Does.Contain("_waitingForDelayedRelease = false"));
        }

        [Test]
        public void ChargingBarReuseCancelsOldTimerAndCannotOverfill()
        {
            string setup = ExtractMethodBody(_chargingBarSource, "Setup");
            string charge = ExtractMethodBody(_chargingBarSource, "ChargeBar");

            Assert.That(setup, Does.Contain("CancelTimer(_chargeBarTimer)"));
            Assert.That(setup, Does.Contain("IsCharging = false"));
            Assert.That(charge, Does.Contain("math.min(100, PercentCharged + ChargingIncrement)"));
            Assert.That(charge, Does.Contain("PercentCharged >= 100"));
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = source.IndexOf(" " + methodName + "(", StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), $"Could not find method {methodName}.");
            int openingBrace = source.IndexOf('{', signature);
            int depth = 0;
            for (int index = openingBrace; index < source.Length; index++)
            {
                if (source[index] == '{') depth++;
                else if (source[index] == '}' && --depth == 0)
                    return source.Substring(openingBrace, index - openingBrace + 1);
            }
            Assert.Fail($"Method {methodName} has no balanced body.");
            return string.Empty;
        }
    }
}
