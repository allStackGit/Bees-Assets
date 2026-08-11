using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SocketCommandResponseValidationTests
    {
        [Test]
        public void BannedHiveMindResponseIsRejectedBeforeCommandConstruction()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Server", "Socket.cs"));

            int bannedCheck = source.IndexOf("_strategicStandingRequest.Request.BannedStrats.Contains");
            int commandSwitch = source.IndexOf("switch (_tempCommandType)", bannedCheck);
            Assert.That(bannedCheck, Is.GreaterThanOrEqualTo(0));
            Assert.That(commandSwitch, Is.GreaterThan(bannedCheck));

            string rejectionBlock = source.Substring(bannedCheck, commandSwitch - bannedCheck);
            Assert.That(rejectionBlock, Does.Contain("_tempSquad.AddToCommandList();"));
            Assert.That(rejectionBlock, Does.Contain("return;"));
        }
    }
}
