using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlDemonstrationUploaderTests
    {
        [Serializable]
        private sealed class ManifestFixture
        {
            public int schemaVersion;
            public string behaviorName;
            public int policyAbiVersion;
            public string policySignature;
            public int observationSize;
            public int continuousActionCount;
            public int[] discreteBranchSizes;
        }

        [Test]
        public void UploadRequiresExplicitCommandLineOptIn()
        {
            MethodInfo method = GetStaticMethod("IsUploadRequested");

            Assert.That(method.Invoke(null, new object[] { new[] { "game.exe" } }), Is.False);
            Assert.That(method.Invoke(null, new object[] { new[] { "game.exe", "--rl-upload-demonstrations" } }), Is.True);
            Assert.That(method.Invoke(null, new object[] { new[] { "game.exe", "--RL-UPLOAD-DEMONSTRATIONS" } }), Is.True);
        }

        [Test]
        public void CaptureManifestMustMatchExactFrozenPolicy()
        {
            ManifestFixture manifest = CurrentManifest();
            MethodInfo method = GetStaticMethod("CaptureManifestMatchesCurrentPolicy");

            Assert.That(method.Invoke(null, new object[] { JsonUtility.ToJson(manifest) }), Is.True);

            manifest.policySignature += "-stale";
            Assert.That(method.Invoke(null, new object[] { JsonUtility.ToJson(manifest) }), Is.False);
        }

        [Test]
        public void DemonstrationIdIsContentAddressedAndAbiScoped()
        {
            Type schemaType = RuntimeAssembly.GetType("RlPolicySchema");
            int version = (int)RuntimeAssembly.GetStaticField(schemaType, "Version");
            MethodInfo method = GetStaticMethod("BuildDemonstrationId");
            string hash = new string('a', 64);

            string value = (string)method.Invoke(null, new object[] { hash });

            Assert.That(value, Is.EqualTo($"v{version}-{new string('a', 24)}"));
        }

        [Test]
        public void UploadBundleLimitIncludesCaptureManifestBytes()
        {
            Type uploaderType = RuntimeAssembly.GetType("RlDemonstrationUploader");
            int maximum = (int)RuntimeAssembly.GetStaticField(uploaderType, "MaxUploadBundleBytes");
            MethodInfo method = GetStaticMethod("IsUploadBundleWithinLimit");
            string manifest = "{\"policy\":\"v7\"}";
            int manifestBytes = Encoding.UTF8.GetByteCount(manifest);

            Assert.That(method.Invoke(null, new object[] { (long)(maximum - manifestBytes), manifest }), Is.True);
            Assert.That(method.Invoke(null, new object[] { (long)(maximum - manifestBytes + 1), manifest }), Is.False);
            Assert.That(method.Invoke(null, new object[] { 0L, manifest }), Is.False);
            Assert.That(method.Invoke(null, new object[] { 1L, null }), Is.False);
        }

        [Test]
        public void SnapshotIncludesOnlyClosedHumanDemoFilesFromCurrentAbiDirectory()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "bees-rl-upload-snapshot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Type passiveType = RuntimeAssembly.GetType("RlGameplayDemonstrationAgent");
                string manifestName = (string)RuntimeAssembly.GetStaticField(
                    passiveType,
                    "CaptureManifestFileName");
                File.WriteAllText(
                    Path.Combine(root, manifestName),
                    JsonUtility.ToJson(CurrentManifest(), true));

                string human = Path.Combine(root, "Human");
                string hive = Path.Combine(root, "HiveMind");
                Directory.CreateDirectory(human);
                Directory.CreateDirectory(hive);
                string first = Path.Combine(human, "human-cap-s0.demo");
                string second = Path.Combine(human, "human-s0.demo");
                File.WriteAllBytes(first, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(second, new byte[] { 4, 5, 6 });
                File.WriteAllBytes(Path.Combine(hive, "hivemind-s0.demo"), new byte[] { 7 });
                File.WriteAllText(Path.Combine(human, "not-a-demo.txt"), "ignored");
                string nested = Path.Combine(human, "Nested");
                Directory.CreateDirectory(nested);
                File.WriteAllBytes(Path.Combine(nested, "nested.demo"), new byte[] { 8 });

                List<string> destination = new List<string>();
                object[] arguments = { root, destination, null, null };
                GetStaticMethod("SnapshotClosedHumanDemonstrations").Invoke(null, arguments);

                Assert.That(destination, Is.EqualTo(new[] { first, second }.OrderBy(value => value, StringComparer.Ordinal)));
                Assert.That((string)arguments[2], Is.Not.Null.And.Not.Empty);
                Assert.That((string)arguments[3], Does.Match("^[0-9a-f]{64}$"));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static ManifestFixture CurrentManifest()
        {
            Type agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            Type schemaType = RuntimeAssembly.GetType("RlPolicySchema");
            MethodInfo branchMethod = agentType.GetMethod(
                "CreateDiscreteBranchSizes",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(branchMethod, Is.Not.Null);
            return new ManifestFixture
            {
                schemaVersion = 1,
                behaviorName = (string)RuntimeAssembly.GetStaticField(schemaType, "ExpectedBehaviorName"),
                policyAbiVersion = (int)RuntimeAssembly.GetStaticField(schemaType, "Version"),
                policySignature = (string)RuntimeAssembly.GetStaticField(schemaType, "Signature"),
                observationSize = (int)RuntimeAssembly.GetStaticField(schemaType, "ExpectedObservationSize"),
                continuousActionCount = (int)RuntimeAssembly.GetStaticField(schemaType, "ExpectedContinuousActions"),
                discreteBranchSizes = (int[])branchMethod.Invoke(null, null)
            };
        }

        private static MethodInfo GetStaticMethod(string name)
        {
            Type type = RuntimeAssembly.GetType("RlDemonstrationUploader");
            MethodInfo method = type.GetMethod(
                name,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(method, Is.Not.Null);
            return method;
        }
    }
}
