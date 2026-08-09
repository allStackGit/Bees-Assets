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
    }
}
