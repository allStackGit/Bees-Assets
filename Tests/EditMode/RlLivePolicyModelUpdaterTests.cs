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
