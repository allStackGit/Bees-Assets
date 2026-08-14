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
        public void IdentitySelectionUsesCentralSteamManagerAndKeepsLocalFallback()
        {
            string source = ReadSource("Scripts", "ConfigData.Runtime.cs");

            int methodIndex = source.IndexOf("public static ulong GetUserId()");
            int nextMethodIndex = source.IndexOf("public static bool HasPlayedBefore()", methodIndex);
            string method = source.Substring(methodIndex, nextMethodIndex - methodIndex);

            Assert.That(method, Does.Contain("SteamManager.Initialized"));
            Assert.That(method, Does.Not.Contain("SteamAPI.Init()"));
            Assert.That(method, Does.Contain("PlayerPrefs.GetInt(\"user_id\")"));
            Assert.That(method, Does.Contain("using local fallback identity"));
        }

        [Test]
        public void SteamInitializationFailureIsNonFatal()
        {
            string source = ReadSource("Scripts", "Steamworks.NET", "SteamManager.cs");

            Assert.That(source, Does.Contain("MarkInitializationFailed"));
            Assert.That(source, Does.Contain("Continuing without Steam features."));
            Assert.That(source, Does.Contain("m_bInitialized = SteamAPI.Init();"));
            Assert.That(source, Does.Not.Contain("Could not load [lib]steam_api.dll/so/dylib"));
        }

        [Test]
        public void SteamBuildPackagerRequiresCompleteUnityAndSteamRuntime()
        {
            string source = ReadSource("Editor", "SteamBuildPackager.cs");

            Assert.That(source, Does.Contain("UnityPlayer.dll"));
            Assert.That(source, Does.Contain("MonoBleedingEdge"));
            Assert.That(source, Does.Contain("GameAssembly.dll"));
            Assert.That(source, Does.Contain("steam_api64.dll"));
            Assert.That(source, Does.Contain("Directory.EnumerateFiles(buildRoot, \"*\", SearchOption.AllDirectories)"));
        }
    }
}
