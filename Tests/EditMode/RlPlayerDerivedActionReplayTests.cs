using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlPlayerDerivedActionReplayTests
    {
        private Type _replayType;
        private MethodInfo _loadCatalog;
        private string _tempDirectory;

        [SetUp]
        public void SetUp()
        {
            _replayType = RuntimeAssembly.GetType("RlPlayerDerivedActionReplay");
            _loadCatalog = _replayType.GetMethod(
                "LoadCatalogForTests",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(_loadCatalog, Is.Not.Null);
            _tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "bees-replay-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }

        [Test]
        public void CatalogLoadsBoundedReplayAndPreservesFrameData()
        {
            string replayPath = WriteReplay(frameCount: 2);
            string catalogPath = WriteCatalog(replayPath, frameCount: 2);

            IDictionary catalog = _loadCatalog.Invoke(null, new object[] { catalogPath }) as IDictionary;
            Assert.That(catalog, Is.Not.Null);
            Assert.That(catalog.Count, Is.EqualTo(1));
            object replay = catalog["adv-aaaaaaaaaaaaaaaaaaaaaaaa"];
            Assert.That((int)RuntimeAssembly.GetField(replay, "FrameCount"), Is.EqualTo(2));
            Assert.That((int)RuntimeAssembly.GetField(replay, "FixedStepInterval"), Is.EqualTo(5));
            Vector2 source = (Vector2)RuntimeAssembly.GetField(replay, "SourceStartDirection");
            Assert.That(source, Is.EqualTo(Vector2.up));
            float[] continuous = (float[])RuntimeAssembly.GetField(replay, "ContinuousActions");
            ushort[] fireMasks = (ushort[])RuntimeAssembly.GetField(replay, "FireMasks");
            Assert.That(continuous.Length, Is.EqualTo(68));
            Assert.That(continuous[0], Is.EqualTo(1f));
            Assert.That(continuous[3], Is.EqualTo(1f));
            Assert.That(fireMasks, Is.EqualTo(new ushort[] { 1, 2 }));
        }

        [Test]
        public void CatalogRejectsReplayHashOrSizeMismatchBeforeUse()
        {
            string replayPath = WriteReplay(frameCount: 1);
            string catalogPath = WriteCatalog(replayPath, frameCount: 1, replayHash: new string('0', 64));
            AssertLoadFails(catalogPath);

            catalogPath = WriteCatalog(replayPath, frameCount: 2);
            AssertLoadFails(catalogPath);
        }

        [Test]
        public void CatalogRejectsIdentityHashMismatchBeforeUse()
        {
            string replayPath = WriteReplay(frameCount: 1);
            string relative = Path.GetFileName(replayPath).Replace('\\', '/');
            string hash = Sha256(File.ReadAllBytes(replayPath));
            string path = Path.Combine(_tempDirectory, "bad-catalog-hash.json");
            File.WriteAllText(
                path,
                CatalogJson(
                    "adv-aaaaaaaaaaaaaaaaaaaaaaaa",
                    relative,
                    hash,
                    1,
                    catalogHash: new string('0', 64)));
            AssertLoadFails(path);
        }

        [Test]
        public void CatalogRejectsMalformedContentIdsAndRootedReplayPath()
        {
            string replayPath = WriteReplay(frameCount: 1);
            string relative = Path.GetFileName(replayPath).Replace('\\', '/');
            string hash = Sha256(File.ReadAllBytes(replayPath));
            string malformed = Path.Combine(_tempDirectory, "bad-id.json");
            File.WriteAllText(
                malformed,
                CatalogJson("adv-GGGGGGGGGGGGGGGGGGGGGGGG", relative, hash, 1));
            AssertLoadFails(malformed);

            string rooted = Path.Combine(_tempDirectory, "rooted.json");
            File.WriteAllText(
                rooted,
                CatalogJson(
                    "adv-aaaaaaaaaaaaaaaaaaaaaaaa",
                    replayPath.Replace('\\', '/'),
                    hash,
                    1));
            AssertLoadFails(rooted);
        }

        [Test]
        public void PolicyAgentAndArenaLifecycleKeepScriptedSideOutOfPpoOwnership()
        {
            string agent = ReadSource("Scripts", "Scenes", "RlOneVsOneAgent.cs");
            Assert.That(agent, Does.Contain("!RlPlayerDerivedActionReplay.IsScriptedSide(_level, _side)"));
            Assert.That(agent, Does.Contain("internal static void ApplyMovementCommand(Ship ship"));
            Assert.That(agent, Does.Contain("internal static void ApplyWeaponCommand(Ship ship"));

            string matchups = ReadSource("Scripts", "Scenes", "RlOneVsOnePerArenaMatchups.cs");
            Assert.That(matchups, Does.Contain("RlPlayerDerivedActionReplay.PrepareEpisode(level);"));
            Assert.That(matchups, Does.Contain("RlPlayerDerivedActionReplay.EndEpisode(level);"));

            string replay = ReadSource("Scripts", "Scenes", "RlPlayerDerivedActionReplay.cs");
            Assert.That(replay, Does.Contain("private bool _hasBoundOnce;"));
            Assert.That(replay, Does.Contain("if (_hasBoundOnce)"));
            Assert.That(replay, Does.Contain("_neutralized = true;"));
        }

        private string WriteReplay(int frameCount)
        {
            string path = Path.Combine(_tempDirectory, "replay.brpl");
            using (FileStream stream = File.Create(path))
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.ASCII, false))
            {
                writer.Write(Encoding.ASCII.GetBytes("BEESRPL1"));
                writer.Write(frameCount);
                writer.Write(5);
                writer.Write(0f);
                writer.Write(1f);
                for (int frame = 0; frame < frameCount; frame++)
                {
                    for (int action = 0; action < 34; action++)
                    {
                        float value = action == 0 ? 1f : action == 3 ? 1f : 0f;
                        writer.Write(value);
                    }
                    writer.Write((ushort)(1 << frame));
                }
            }
            return path;
        }

        private string WriteCatalog(
            string replayPath,
            int frameCount,
            string replayHash = null)
        {
            string path = Path.Combine(_tempDirectory, "catalog.json");
            string relative = Path.GetFileName(replayPath).Replace('\\', '/');
            string hash = replayHash ?? Sha256(File.ReadAllBytes(replayPath));
            File.WriteAllText(
                path,
                CatalogJson("adv-aaaaaaaaaaaaaaaaaaaaaaaa", relative, hash, frameCount));
            return path;
        }

        private static string CatalogJson(
            string scenarioId,
            string replayPath,
            string replayHash,
            int frameCount,
            string catalogHash = null)
        {
            string identity = CatalogIdentityJson(scenarioId, replayPath, replayHash, frameCount);
            string hash = catalogHash ?? Sha256(Encoding.UTF8.GetBytes(identity));
            return "{" +
                   "\"schemaVersion\":1," +
                   "\"catalogSha256\":\"" + hash + "\"," +
                   "\"entries\":[{" +
                   "\"scenarioId\":\"" + EscapeJson(scenarioId) + "\"," +
                   "\"side\":\"Human\"," +
                   "\"replayId\":\"advreplay-bbbbbbbbbbbbbbbbbbbbbbbb\"," +
                   "\"replayPath\":\"" + EscapeJson(replayPath) + "\"," +
                   "\"replaySha256\":\"" + replayHash + "\"," +
                   "\"frameCount\":" + frameCount.ToString(CultureInfo.InvariantCulture) + "," +
                   "\"fixedStepInterval\":5" +
                   "}]}";
        }

        private static string CatalogIdentityJson(
            string scenarioId,
            string replayPath,
            string replayHash,
            int frameCount)
        {
            return "{\"entries\":[{" +
                   "\"fixedStepInterval\":5," +
                   "\"frameCount\":" + frameCount.ToString(CultureInfo.InvariantCulture) + "," +
                   "\"replayId\":\"advreplay-bbbbbbbbbbbbbbbbbbbbbbbb\"," +
                   "\"replayPath\":\"" + EscapeJson(replayPath) + "\"," +
                   "\"replaySha256\":\"" + replayHash + "\"," +
                   "\"scenarioId\":\"" + EscapeJson(scenarioId) + "\"," +
                   "\"side\":\"Human\"}]," +
                   "\"schemaVersion\":1}";
        }

        private void AssertLoadFails(string catalogPath)
        {
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                _loadCatalog.Invoke(null, new object[] { catalogPath }));
            Assert.That(exception.InnerException, Is.TypeOf<ArgumentException>());
        }

        private static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                StringBuilder builder = new StringBuilder(digest.Length * 2);
                for (int i = 0; i < digest.Length; i++)
                {
                    builder.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string ReadSource(params string[] parts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < parts.Length; i++)
            {
                path = Path.Combine(path, parts[i]);
            }
            return File.ReadAllText(path);
        }
    }
}
