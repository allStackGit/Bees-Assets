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

            StringAssert.Contains("private static bool HaveAllShipsFinished(List<Ship> ships)", source);
            StringAssert.Contains("if (!((Barge)ships[i]).HasCompletedRun)", source);
            StringAssert.Contains("if (!IsDead && HaveAllShipsFinished(ships))", source);
            StringAssert.DoesNotContain("HaveAnyShipsFinished", source);
        }
    }
}
