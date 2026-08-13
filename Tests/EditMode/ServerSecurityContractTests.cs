using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ServerSecurityContractTests
    {
        [Test]
        public void ProductionSocketUsesWssFactory()
        {
            string config = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "ConfigData.cs"));
            string factory = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Server", "SecureSocketFactory.cs"));

            Assert.That(config, Does.Contain("SecureSocketFactory.Create"));
            Assert.That(factory, Does.Contain("wss://{hostname}:{port}"));
            Assert.That(factory, Does.Contain("FormatterServices.GetUninitializedObject"));
            Assert.That(factory, Does.Not.Contain("bootstrapSocket.Close"));
        }

        [Test]
        public void EveryUserBootstrapRequestCarriesSteamWebApiTicket()
        {
            string settings = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Server", "GetUserSettingsData.cs"));
            string setup = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Server", "SetupLevel.cs"));
            string getData = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Server", "GetUserData.cs"));
            string storeData = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Server", "StoreUserData.cs"));
            string auth = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Server", "SteamWebApiAuth.cs"));

            Assert.That(settings, Does.Contain("GetUserSettingsData : GetUserData"),
                "Settings bootstrap requests inherit the authenticated GetUserData request fields.");
            Assert.That(setup, Does.Contain("SteamWebApiAuth.TicketHex"));
            Assert.That(getData, Does.Contain("SteamWebApiAuth.TicketHex"));
            Assert.That(storeData, Does.Contain("SteamWebApiAuth.TicketHex"));
            Assert.That(auth, Does.Contain("SteamUser.GetAuthTicketForWebApi"));
            Assert.That(auth, Does.Contain("ConfigData.LoadSettings();"));
        }
    }
}
