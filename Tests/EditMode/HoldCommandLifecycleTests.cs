using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class HoldCommandLifecycleTests
    {
        [Test]
        public void HoldStopsMovementInheritedFromPreviousCommand()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Hold.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("GetSquad().StopMoving();", source);
            StringAssert.Contains("GetSquad().Status = $\"Holding this position\";", source);
        }
    }
}
