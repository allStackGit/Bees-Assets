using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class UserIdentityWireFormatTests
    {
        private static string ReadSource(params string[] parts)
        {
            string path = Application.dataPath;
            foreach (string part in parts)
            {
                path = Path.Combine(path, part);
            }
            return File.ReadAllText(path);
        }

        [TestCase("SetupLevel.cs")]
        [TestCase("GetUserData.cs")]
        [TestCase("StoreUserData.cs")]
        public void OutboundUserIdsAreSerializedAsDecimalStrings(string filename)
        {
            string source = ReadSource("Scripts", "Server", filename);
            Assert.That(source, Does.Contain("string UserId"));
            Assert.That(source, Does.Contain("userId.ToString(CultureInfo.InvariantCulture)"));
            Assert.That(source, Does.Not.Contain("public readonly ulong UserId"));
            Assert.That(source, Does.Not.Contain("public ulong UserId"));
        }

        [Test]
        public void UserDataResponsesDoNotParseSteamIdsThroughInt32()
        {
            string source = ReadSource("Scripts", "Server", "UserDataResponse.cs");
            Assert.That(source, Does.Contain("public string UserId"));
            Assert.That(source, Does.Not.Contain("public int UserId"));
        }

        [Test]
        public void CurrentIdentitySelectionStillDocumentsDevelopmentOnlySteamBypass()
        {
            string source = ReadSource("Scripts", "ConfigData.cs");
            Assert.That(source, Does.Contain("ONLY SKIP STEAM FOR DEVELOPMENT"));

            int methodIndex = source.IndexOf("public static ulong GetUserId()");
            int developmentCommentIndex = source.IndexOf("ONLY SKIP STEAM FOR DEVELOPMENT", methodIndex);
            int prematureReturnIndex = source.IndexOf("return _userId;", methodIndex);

            // This assertion intentionally documents the remaining blocker. It should be
            // inverted/removed when ConfigData.GetUserId is safely patched so non-development
            // builds reach Steam identity instead of the local PlayerPrefs fallback.
            Assert.That(prematureReturnIndex, Is.LessThan(developmentCommentIndex));
        }
    }
}