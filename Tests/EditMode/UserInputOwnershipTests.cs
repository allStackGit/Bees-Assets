using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class UserInputOwnershipTests
    {
        [Test]
        public void RawMovementRespectsCampaignInputLock()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Levels", "LevelInputManager.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("Where(s => !s.IsLockedOn && s.CanAcceptUserInput)", source);
        }
    }
}