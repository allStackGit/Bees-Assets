using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlLiveTelemetryTests
    {
        private const BindingFlags StaticFlags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        public void TelemetryCompatibilityAliasesRemainBoundToFrozenPolicyContract()
        {
            Type schema = RuntimeAssembly.GetType("RlPolicySchema");
            Type contract = RuntimeAssembly.GetType("RlLiveTelemetryContract");
            Assert.That(schema, Is.Not.Null);
            Assert.That(contract, Is.Not.Null);

            MethodInfo validate = contract.GetMethod("ValidateOrThrow", StaticFlags);
            Assert.That(validate, Is.Not.Null);
            Assert.DoesNotThrow(() => validate.Invoke(null, null));

            Assert.That(GetStatic<int>(schema, "ObservationSize"),
                Is.EqualTo(GetStatic<int>(schema, "ExpectedObservationSize")));
            Assert.That(GetStatic<int>(schema, "ObservationSchemaVersion"),
                Is.EqualTo(GetStatic<int>(contract, "ObservationSchemaVersion")));
            Assert.That(GetStatic<int>(schema, "ActionSchemaVersion"),
                Is.EqualTo(GetStatic<int>(contract, "ActionSchemaVersion")));
            Assert.That(GetStatic<int>(schema, "RewardSchemaVersion"),
                Is.EqualTo(GetStatic<int>(contract, "RewardSchemaVersion")));
            Assert.That(GetStatic<int>(schema, "ScenarioSchemaVersion"),
                Is.EqualTo(GetStatic<int>(contract, "ScenarioSchemaVersion")));
        }

        [Test]
        public void ReusedLevelStartTimeCreatesANewTelemetryGeneration()
        {
            Type recorder = RuntimeAssembly.GetType("RlLiveTelemetryRecorder");
            MethodInfo changed = recorder.GetMethod("HasLevelGenerationChanged", StaticFlags);
            Assert.That(changed, Is.Not.Null);

            Assert.That((bool)changed.Invoke(null, new object[] { 12.5f, 12.5f }), Is.False);
            Assert.That((bool)changed.Invoke(null, new object[] { 12.5f, 13.0f }), Is.True);
        }

        [Test]
        public void GameplayTelemetryPreservesHumanHiveMindAndNeuralProvenance()
        {
            Type router = RuntimeAssembly.GetType("RlProductionControllerRouter");
            Type controllerKind = router.GetNestedType("ControllerKind", BindingFlags.NonPublic);
            MethodInfo eligible = router.GetMethod("IsExternalExpert", StaticFlags);
            MethodInfo sourceName = router.GetMethod("ExternalSourceName", StaticFlags);
            Type recorder = RuntimeAssembly.GetType("RlLiveTelemetryRecorder");
            Type payloadType = recorder.GetNestedType("TelemetryPayload", BindingFlags.NonPublic);

            Assert.That(router, Is.Not.Null);
            Assert.That(controllerKind, Is.Not.Null);
            Assert.That(eligible, Is.Not.Null);
            Assert.That(sourceName, Is.Not.Null);
            Assert.That(payloadType, Is.Not.Null);

            object player = Enum.Parse(controllerKind, "Player");
            object hiveMind = Enum.Parse(controllerKind, "HiveMind");
            object neural = Enum.Parse(controllerKind, "NeuralNetwork");
            Assert.That((bool)eligible.Invoke(null, new[] { player }), Is.True);
            Assert.That((bool)eligible.Invoke(null, new[] { hiveMind }), Is.True);
            Assert.That((bool)eligible.Invoke(null, new[] { neural }), Is.True);
            Assert.That((string)sourceName.Invoke(null, new[] { player }), Is.EqualTo("human"));
            Assert.That((string)sourceName.Invoke(null, new[] { hiveMind }), Is.EqualTo("hivemind"));
            Assert.That((string)sourceName.Invoke(null, new[] { neural }), Is.EqualTo("neural"));

            object payload = Activator.CreateInstance(payloadType, true);
            Assert.That(payloadType.GetField("mode", InstanceFlags).GetValue(payload),
                Is.EqualTo("gameplay-controller-live"));
        }

        [Test]
        public void UploaderAcceptsCurrentMetadataAndRejectsSchemaDrift()
        {
            Type schema = RuntimeAssembly.GetType("RlPolicySchema");
            Type recorder = RuntimeAssembly.GetType("RlLiveTelemetryRecorder");
            Type uploader = RuntimeAssembly.GetType("RlLiveTelemetryUploader");
            Type payloadType = recorder.GetNestedType("TelemetryPayload", BindingFlags.NonPublic);
            Type stepType = recorder.GetNestedType("TelemetryStep", BindingFlags.NonPublic);
            MethodInfo matches = uploader.GetMethod("PayloadMatchesCurrentContract", StaticFlags);
            Assert.That(payloadType, Is.Not.Null);
            Assert.That(stepType, Is.Not.Null);
            Assert.That(matches, Is.Not.Null);

            object payload = Activator.CreateInstance(payloadType, true);
            SetField(payloadType, payload, "schema_version", 1);
            SetField(payloadType, payload, "match_id", "live-test-s000000");
            SetField(payloadType, payload, "game_build_version", "test-build");
            SetField(payloadType, payload, "mode", "gameplay-controller-live");
            SetField(payloadType, payload, "result", "draw");
            SetField(payloadType, payload, "model_id",
                $"bees-rl-v{GetStatic<int>(schema, "Version")}-aaaaaaaaaaaaaaaaaaaaaaaa");
            SetField(payloadType, payload, "model_sha256", new string('b', 64));
            SetField(payloadType, payload, "deployment_id", "deploy-cccccccccccccccccccccccc");
            SetField(payloadType, payload, "policy_signature", GetStatic<string>(schema, "Signature"));
            SetField(payloadType, payload, "behavior_name", GetStatic<string>(schema, "ExpectedBehaviorName"));
            SetField(payloadType, payload, "policy_abi_version", GetStatic<int>(schema, "Version"));
            SetField(payloadType, payload, "observation_schema_version", GetStatic<int>(schema, "ObservationSchemaVersion"));
            SetField(payloadType, payload, "action_schema_version", GetStatic<int>(schema, "ActionSchemaVersion"));
            SetField(payloadType, payload, "reward_schema_version", GetStatic<int>(schema, "RewardSchemaVersion"));
            SetField(payloadType, payload, "scenario_schema_version", GetStatic<int>(schema, "ScenarioSchemaVersion"));

            object step = Activator.CreateInstance(stepType, true);
            SetField(stepType, step, "controller_kind", "neural");
            IList steps = (IList)payloadType.GetField("steps", InstanceFlags).GetValue(payload);
            steps.Add(step);

            Assert.That((bool)matches.Invoke(null, new[] { payload }), Is.True);

            SetField(
                payloadType,
                payload,
                "reward_schema_version",
                GetStatic<int>(schema, "RewardSchemaVersion") + 1);
            Assert.That((bool)matches.Invoke(null, new[] { payload }), Is.False);
        }

        private static T GetStatic<T>(Type type, string name)
        {
            FieldInfo field = type.GetField(name, StaticFlags);
            Assert.That(field, Is.Not.Null, $"Missing static field {name} on {type.FullName}");
            return (T)field.GetValue(null);
        }

        private static void SetField(Type type, object target, string name, object value)
        {
            FieldInfo field = type.GetField(name, InstanceFlags);
            Assert.That(field, Is.Not.Null, $"Missing field {name} on {type.FullName}");
            field.SetValue(target, value);
        }
    }
}
