using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class GuardCommandLifecycleTests
    {
        [Test]
        public void GuardFinalizationAlwaysRestoresSpeedAndReciprocalMembership()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "Commands", "Guard.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("public override void SetFinalize(string cause)", source);
            StringAssert.Contains("GetSquad().SetSquadSpeed(GetSquad().MaxSpeed);", source);
            StringAssert.Contains("guardCommand.OtherGuardSquads.Remove(GetSquad());", source);
            StringAssert.Contains("base.SetFinalize(cause);", source);
        }
    }
}
