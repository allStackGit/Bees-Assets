using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class EnemyRequiredCommandLifecycleTests
    {
        [TestCase("Aggressive.cs")]
        [TestCase("BombingRun.cs")]
        [TestCase("Charge.cs")]
        [TestCase("CircleSquad.cs")]
        [TestCase("InAndOut.cs")]
        [TestCase("Retreat.cs")]
        [TestCase("SwipeSquad.cs")]
        public void EnemyRequiredCommandStopsIfBaseExecutionFinalizes(string fileName)
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", fileName);
            string source = File.ReadAllText(path);
            int execute = source.IndexOf("public void Execute(");
            int baseExecute = source.IndexOf("base.Execute(", execute);
            int deadGuard = source.IndexOf("if (IsDead)", baseExecute);

            Assert.That(execute, Is.GreaterThanOrEqualTo(0), fileName);
            Assert.That(baseExecute, Is.GreaterThan(execute), fileName);
            Assert.That(deadGuard, Is.GreaterThan(baseExecute), fileName);

            string between = source.Substring(baseExecute, deadGuard - baseExecute);
            StringAssert.DoesNotContain("EnemySquad.", between, fileName);
        }
    }
}
