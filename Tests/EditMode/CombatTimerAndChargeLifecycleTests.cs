using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CombatTimerAndChargeLifecycleTests
    {
        [Test]
        public void CombatTimerIsScheduledAfterReuse()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Ship.Combat.cs");
            string source = File.ReadAllText(path);

            int start = source.IndexOf("public void SetCombatTimer()");
            int end = source.IndexOf("public void LogDamage", start);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            Assert.That(end, Is.GreaterThan(start));

            string method = source.Substring(start, end - start);
            int reuse = method.IndexOf("_combatTimerScaledTimer.Reuse");
            int add = method.IndexOf("Level.AddTimer(_combatTimerScaledTimer)");
            Assert.That(reuse, Is.GreaterThanOrEqualTo(0));
            Assert.That(add, Is.GreaterThan(reuse));
        }

        [Test]
        public void ChargeStopsAfterBaseExecutionFinalizes()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Charge.cs");
            string source = File.ReadAllText(path);

            int execute = source.IndexOf("public void Execute(");
            int clearData = source.IndexOf("public override void ClearData", execute);
            Assert.That(execute, Is.GreaterThanOrEqualTo(0));
            Assert.That(clearData, Is.GreaterThan(execute));

            string method = source.Substring(execute, clearData - execute);
            int baseExecute = method.IndexOf("base.Execute(");
            int deadGuard = method.IndexOf("if (IsDead)");
            int enemyUse = method.IndexOf("EnemySquad.Name");
            Assert.That(baseExecute, Is.GreaterThanOrEqualTo(0));
            Assert.That(deadGuard, Is.GreaterThan(baseExecute));
            Assert.That(enemyUse, Is.GreaterThan(deadGuard));
        }
    }
}
