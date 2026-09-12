using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlLivePolicyModelUpdaterTests
    {
        [TestCase(RuntimePlatform.WindowsPlayer, "WindowsPlayer")]
        [TestCase(RuntimePlatform.OSXPlayer, "OSXPlayer")]
        [TestCase(RuntimePlatform.LinuxPlayer, "LinuxPlayer")]
        public void DesktopPlatformNamesMatchServerDistributionPointers(RuntimePlatform platform, string expected)
        {
            Type updater = RuntimeAssembly.GetType("RlLivePolicyModelUpdater");
            Assert.That(updater, Is.Not.Null);
            MethodInfo platformName = updater.GetMethod(
                "PlatformName",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(platformName, Is.Not.Null);
            Assert.That(platformName.Invoke(null, new object[] { platform }), Is.EqualTo(expected));
        }

        [Test]
        public void EditorIsNotEligibleForProductionHotDistribution()
        {
            Type updater = RuntimeAssembly.GetType("RlLivePolicyModelUpdater");
            MethodInfo platformName = updater.GetMethod(
                "PlatformName",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(platformName.Invoke(null, new object[] { RuntimePlatform.WindowsEditor }), Is.Null);
        }

        [Test]
        public void CurrentDescriptorAcceptsExactPublishedIdentityAndRejectsUpToDateLie()
        {
            Type updater = RuntimeAssembly.GetType("RlLivePolicyModelUpdater");
            Type responseType = updater.GetNestedType("ModelResponse", BindingFlags.NonPublic);
            MethodInfo validate = updater.GetMethod(
                "TryValidateCurrentResponse",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(responseType, Is.Not.Null);
            Assert.That(validate, Is.Not.Null);

            int policyVersion = GetPolicyVersion();
            string deploymentId = "deploy-aaaaaaaaaaaaaaaaaaaaaaaa";
            object descriptor = NewResponse(responseType,
                ("Platform", "WindowsPlayer"),
                ("UpToDate", false),
                ("DeploymentId", deploymentId),
                ("ModelId", $"bees-rl-v{policyVersion}-bbbbbbbbbbbbbbbbbbbbbbbb"),
                ("BundleSha256", new string('c', 64)),
                ("BundleSizeBytes", 1234L),
                ("ChunkBytes", 512),
                ("PolicyAbiVersion", policyVersion),
                ("PolicySignature", GetPolicySignature()));

            object[] firstArgs = { descriptor, "WindowsPlayer", null, null };
            Assert.That((bool)validate.Invoke(null, firstArgs), Is.True, firstArgs[3] as string);
            Assert.That(firstArgs[3], Is.Null);

            SetField(responseType, descriptor, "UpToDate", true);
            object[] liedArgs = { descriptor, "WindowsPlayer", null, null };
            Assert.That((bool)validate.Invoke(null, liedArgs), Is.False);
            Assert.That(liedArgs[3] as string, Does.Contain("UpToDate"));

            object[] currentArgs = { descriptor, "WindowsPlayer", deploymentId, null };
            Assert.That((bool)validate.Invoke(null, currentArgs), Is.True, currentArgs[3] as string);
        }

        [Test]
        public void ChunkValidationReassemblesPinnedServerPayloadExactly()
        {
            Type updater = RuntimeAssembly.GetType("RlLivePolicyModelUpdater");
            Type responseType = updater.GetNestedType("ModelResponse", BindingFlags.NonPublic);
            MethodInfo validate = updater.GetMethod(
                "TryValidateChunkResponse",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(responseType, Is.Not.Null);
            Assert.That(validate, Is.Not.Null);

            byte[] payload = { 3, 1, 4, 1, 5, 9, 2, 6, 5, 3, 5, 8, 9 };
            string deploymentId = "deploy-aaaaaaaaaaaaaaaaaaaaaaaa";
            string bundleSha = new string('d', 64);
            object descriptor = NewResponse(responseType,
                ("Platform", "WindowsPlayer"),
                ("DeploymentId", deploymentId),
                ("BundleSha256", bundleSha),
                ("BundleSizeBytes", (long)payload.Length));

            using (MemoryStream reconstructed = new MemoryStream())
            {
                long offset = 0;
                int[] chunkLengths = { 5, 4, 4 };
                foreach (int length in chunkLengths)
                {
                    byte[] chunkBytes = new byte[length];
                    Buffer.BlockCopy(payload, (int)offset, chunkBytes, 0, length);
                    long nextOffset = offset + length;
                    object chunk = NewResponse(responseType,
                        ("Platform", "WindowsPlayer"),
                        ("DeploymentId", deploymentId),
                        ("BundleSha256", bundleSha),
                        ("Offset", offset),
                        ("NextOffset", nextOffset),
                        ("Complete", nextOffset == payload.Length),
                        ("Data", Convert.ToBase64String(chunkBytes)));

                    object[] args = { chunk, descriptor, offset, null, null };
                    Assert.That((bool)validate.Invoke(null, args), Is.True, args[4] as string);
                    byte[] validatedBytes = (byte[])args[3];
                    reconstructed.Write(validatedBytes, 0, validatedBytes.Length);
                    offset = nextOffset;
                }

                CollectionAssert.AreEqual(payload, reconstructed.ToArray());
            }
        }

        [Test]
        public void ChunkValidationRejectsDeploymentChangeMidDownload()
        {
            Type updater = RuntimeAssembly.GetType("RlLivePolicyModelUpdater");
            Type responseType = updater.GetNestedType("ModelResponse", BindingFlags.NonPublic);
            MethodInfo validate = updater.GetMethod(
                "TryValidateChunkResponse",
                BindingFlags.Static | BindingFlags.NonPublic);
            string bundleSha = new string('e', 64);
            object descriptor = NewResponse(responseType,
                ("Platform", "WindowsPlayer"),
                ("DeploymentId", "deploy-aaaaaaaaaaaaaaaaaaaaaaaa"),
                ("BundleSha256", bundleSha),
                ("BundleSizeBytes", 4L));
            object staleChunk = NewResponse(responseType,
                ("Platform", "WindowsPlayer"),
                ("DeploymentId", "deploy-bbbbbbbbbbbbbbbbbbbbbbbb"),
                ("BundleSha256", bundleSha),
                ("Offset", 0L),
                ("NextOffset", 4L),
                ("Complete", true),
                ("Data", Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })));

            object[] args = { staleChunk, descriptor, 0L, null, null };
            Assert.That((bool)validate.Invoke(null, args), Is.False);
            Assert.That(args[3], Is.Null);
            Assert.That(args[4] as string, Does.Contain("deployment"));
        }

        [Test]
        public void UpdaterPinsAuthenticatedDeploymentAndVerifiesBytesBeforeApplying()
        {
            string source = ReadSource("Scripts", "Scenes", "RlLivePolicyModelUpdater.cs");
            Assert.That(source, Does.Contain("CurrentRequestType = \"rl-model-current\""));
            Assert.That(source, Does.Contain("ChunkRequestType = \"rl-model-chunk\""));
            Assert.That(source, Does.Contain("SteamWebApiAuth.TicketHex"));
            Assert.That(source, Does.Contain("PolicyAbiVersion = RlPolicySchema.Version"));
            Assert.That(source, Does.Contain("PolicySignature = RlPolicySchema.Signature"));
            Assert.That(source, Does.Contain("chunk.BundleSha256 = descriptor.BundleSha256"));
            Assert.That(source, Does.Contain("ComputeFileSha256(tempPath)"));
            Assert.That(source, Does.Contain("AssetBundle.LoadFromFileAsync(finalPath)"));
            Assert.That(source, Does.Contain("LoadAssetAsync<ModelAsset>(ModelAddress)"));
            Assert.That(source, Does.Contain("LoadAssetAsync<TextAsset>(ManifestAddress)"));
            Assert.That(source, Does.Contain("_bootstrap.TryApplyHotBundle("));
            Assert.That(source, Does.Contain("_currentDeploymentId = descriptor.DeploymentId;"));
        }

        [Test]
        public void BootstrapHotSwapRollsBackPartialReplacementAndFallsBackIfRollbackFails()
        {
            string source = ReadSource("Scripts", "Scenes", "RlLivePolicyModelBootstrap.cs");
            Assert.That(source, Does.Contain("List<RlLivePolicyAgent> changedAgents"));
            Assert.That(source, Does.Contain("agent.SetModel(RlOneVsOneAgent.BehaviorName, previousModel)"));
            Assert.That(source, Does.Contain("if (rollbackFailed)"));
            Assert.That(source, Does.Contain("FailToHiveMind(error + \"; rollback to the prior champion also failed\")"));
            Assert.That(source, Does.Contain("_model = model;"));
            Assert.That(source, Does.Contain("_deploymentId = manifest.deployment_id;"));
        }

        private static object NewResponse(Type responseType, params (string name, object value)[] fields)
        {
            object response = Activator.CreateInstance(responseType, true);
            foreach ((string name, object value) in fields)
            {
                SetField(responseType, response, name, value);
            }
            return response;
        }

        private static void SetField(Type type, object target, string name, object value)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing field {name} on {type.FullName}");
            field.SetValue(target, value);
        }

        private static int GetPolicyVersion()
        {
            return GetPolicyMember<int>("Version");
        }

        private static string GetPolicySignature()
        {
            return GetPolicyMember<string>("Signature");
        }

        private static T GetPolicyMember<T>(string name)
        {
            Type schema = RuntimeAssembly.GetType("RlPolicySchema");
            Assert.That(schema, Is.Not.Null);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo field = schema.GetField(name, flags);
            if (field != null)
            {
                return (T)field.GetValue(null);
            }

            PropertyInfo property = schema.GetProperty(name, flags);
            Assert.That(property, Is.Not.Null, $"Missing static policy member {name} on {schema.FullName}");
            return (T)property.GetValue(null, null);
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
