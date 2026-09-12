using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlLivePolicyModelBootstrapTests
    {
        private Type _bootstrapType;
        private MethodInfo _validateManifest;
        private string _policySignature;
        private int _policyVersion;

        [SetUp]
        public void SetUp()
        {
            _bootstrapType = RuntimeAssembly.GetType("RlLivePolicyModelBootstrap");
            Assert.That(_bootstrapType, Is.Not.Null);
            _validateManifest = _bootstrapType.GetMethod(
                "TryValidateManifestJsonForTests",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(_validateManifest, Is.Not.Null);

            Type policySchema = RuntimeAssembly.GetType("RlPolicySchema");
            Assert.That(policySchema, Is.Not.Null);
            _policySignature = (string)policySchema.GetField(
                "Signature",
                BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
            _policyVersion = (int)policySchema.GetField(
                "Version",
                BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
        }

        [Test]
        public void CurrentAbiManifestIsAccepted()
        {
            Assert.That(Validate(ManifestJson(_policySignature, _policyVersion), out string error), Is.True, error);
        }

        [Test]
        public void SignatureMismatchIsRejected()
        {
            Assert.That(
                Validate(ManifestJson("different-policy-signature", _policyVersion), out string error),
                Is.False);
            Assert.That(error, Does.Contain("signature"));
        }

        [Test]
        public void DeploymentIdMustMatchDeclaredIdentityPrefix()
        {
            string json = ManifestJson(_policySignature, _policyVersion).Replace(
                "deploy-aaaaaaaaaaaaaaaaaaaaaaaa",
                "deploy-bbbbbbbbbbbbbbbbbbbbbbbb");
            Assert.That(Validate(json, out string error), Is.False);
            Assert.That(error, Does.Contain("deployment id"));
        }

        [Test]
        public void PolicyAbiMismatchIsRejected()
        {
            string json = ManifestJson(_policySignature, _policyVersion).Replace(
                $"\"policy_abi_version\":{_policyVersion}",
                $"\"policy_abi_version\":{_policyVersion - 1}");
            Assert.That(Validate(json, out string error), Is.False);
            Assert.That(error, Does.Contain("policy ABI"));
        }

        [Test]
        public void RuntimeSourceBindsModelBeforeAgentPhysicsAndFallsBackToHiveMind()
        {
            string source = ReadSource("Scripts", "Scenes", "RlLivePolicyModelBootstrap.cs");
            Assert.That(source, Does.Contain("[DefaultExecutionOrder(-10000)]"));
            Assert.That(source, Does.Contain("agent.SetModel(RlOneVsOneAgent.BehaviorName, _model)"));
            Assert.That(source, Does.Contain("behavior.BehaviorType = BehaviorType.InferenceOnly;"));
            Assert.That(source, Does.Contain("_stage.ActivateBrains = false;"));
            Assert.That(source, Does.Contain("level.SetupHivemind();"));
            Assert.That(source, Does.Contain("agents[i].enabled = false;"));
            Assert.That(source, Does.Contain("RlPolicy/BeesRL1v1"));
            Assert.That(source, Does.Contain("RlPolicy/BeesRL1v1Deployment"));
        }

        [Test]
        public void BuildTimeInstallerAndRuntimeLoaderUseSameResourceContract()
        {
            string installer = ReadSource("Training", "bees_continual_unity_bundle.py");
            string runtime = ReadSource("Scripts", "Scenes", "RlLivePolicyModelBootstrap.cs");
            Assert.That(installer, Does.Contain("RESOURCE_MODEL_FILE = \"BeesRL1v1.onnx\""));
            Assert.That(installer, Does.Contain("RESOURCE_MANIFEST_FILE = \"BeesRL1v1Deployment.json\""));
            Assert.That(runtime, Does.Contain("ModelResourcePath = \"RlPolicy/BeesRL1v1\""));
            Assert.That(runtime, Does.Contain("ManifestResourcePath = \"RlPolicy/BeesRL1v1Deployment\""));
        }

        private bool Validate(string json, out string error)
        {
            object[] args = { json, null };
            bool result = (bool)_validateManifest.Invoke(null, args);
            error = args[1] as string;
            return result;
        }

        private static string ManifestJson(string signature, int policyVersion)
        {
            string escapedSignature = signature.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "{" +
                   "\"schema_version\":1," +
                   "\"deployment_id\":\"deploy-aaaaaaaaaaaaaaaaaaaaaaaa\"," +
                   "\"identity_sha256\":\"" + new string('a', 64) + "\"," +
                   "\"model_file\":\"model.onnx\"," +
                   "\"identity\":{" +
                   "\"schema_version\":1," +
                   "\"model_id\":\"bees-rl-v" + policyVersion + "-bbbbbbbbbbbbbbbbbbbbbbbb\"," +
                   "\"model_sha256\":\"" + new string('c', 64) + "\"," +
                   "\"model_size_bytes\":1234," +
                   "\"behavior_name\":\"BeesRL1v1\"," +
                   "\"policy_signature\":\"" + escapedSignature + "\"," +
                   "\"compatibility\":{" +
                   "\"behavior_name\":\"BeesRL1v1\"," +
                   "\"policy_abi_version\":" + policyVersion + "," +
                   "\"observation_schema_version\":" + policyVersion + "," +
                   "\"action_schema_version\":6," +
                   "\"reward_schema_version\":2," +
                   "\"scenario_schema_version\":1}," +
                   "\"game_build_version\":\"test-build\"," +
                   "\"training_run_id\":\"test-run\"," +
                   "\"training_step\":100}}";
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
