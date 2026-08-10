using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class AggressiveCommandLifecycleTests
    {
        [Test]
        public void FinalizedAggressiveCommandStopsBeforeSchedulingWork()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Aggressive.cs");
            string source = File.ReadAllText(path);

            int executeStart = source.IndexOf("public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy");
            int timerStart = source.IndexOf("private void Timer()", executeStart);
            Assert.That(executeStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(timerStart, Is.GreaterThan(executeStart));

            string execute = source.Substring(executeStart, timerStart - executeStart);
            int baseExecute = execute.IndexOf("base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, false);");
            int deadGuard = execute.IndexOf("if (IsDead)", baseExecute);
            int timerReuse = execute.IndexOf("CommandTimer.Reuse", baseExecute);

            Assert.That(baseExecute, Is.GreaterThanOrEqualTo(0));
            Assert.That(deadGuard, Is.GreaterThan(baseExecute));
            Assert.That(timerReuse, Is.GreaterThan(deadGuard));
        }

        [Test]
        public void AggressiveTimerStopsIfTargetSelectionFinalizesCommand()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Aggressive.cs");
            string source = File.ReadAllText(path);

            int timerStart = source.IndexOf("private void Timer()");
            int moveTowardsEnemies = source.IndexOf("MoveTowardsEnemies();", timerStart);
            int deadGuard = source.IndexOf("if (IsDead)", moveTowardsEnemies);
            int cadenceChange = source.IndexOf("CommandFrequency = .25f;", moveTowardsEnemies);

            Assert.That(timerStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(moveTowardsEnemies, Is.GreaterThan(timerStart));
            Assert.That(deadGuard, Is.GreaterThan(moveTowardsEnemies));
            Assert.That(cadenceChange, Is.GreaterThan(deadGuard));
        }

        [Test]
        public void PooledAggressiveCommandRestoresDefaultCadence()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Aggressive.cs");
            string source = File.ReadAllText(path);

            int clearStart = source.IndexOf("public override void ClearData()");
            int timerStart = source.IndexOf("private void Timer()", clearStart);
            Assert.That(clearStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(timerStart, Is.GreaterThan(clearStart));

            string clear = source.Substring(clearStart, timerStart - clearStart);
            StringAssert.Contains("CommandFrequency = 3f;", clear);
            StringAssert.Contains("CommandFrequency = .25f;", source);
        }
    }
}
