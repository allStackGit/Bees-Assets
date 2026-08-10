using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ChargeCommandCompletionTests
    {
        [Test]
        public void MultiBargeChargeCompletesOnlyAfterEveryBargeFinishes()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Charge.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("private bool HaveAllShipsFinished(List<Barge> ships)", source);
            StringAssert.Contains("ships.All((ship) => ship.HasCompletedRun)", source);
            StringAssert.Contains("HaveAllShipsFinished(_timer_barges)", source);
            StringAssert.DoesNotContain("HaveAnyShipsFinished(_timer_barges)", source);
        }
    }
}
