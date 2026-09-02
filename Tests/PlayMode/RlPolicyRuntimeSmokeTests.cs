using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Bees.Tests.PlayMode
{
    [TestFixture]
    [Category("BeesRlRuntime")]
    public class RlPolicyRuntimeSmokeTests
    {
        private const string TrainingSceneName = "RL 1v1 Training";
        private const int SmokeShipsPerSide = 11;
        private const int SmokeSeed = 17321;
        private const int MaximumReadyFrames = 900;

        private static readonly string[] ExpectedBeeRoster =
        {
            "Beehive",
            "Bumblebee",
            "CarpenterBee",
            "Honeybee",
            "Hornet",
            "Leafcutter",
            "Queen",
            "Wasp",
            "YellowJacket",
        };

        private static readonly string[] ExpectedHumanRoster =
        {
            "Barge",
            "Carrier",
            "Cruiser",
            "Dreadnought",
            "Factory",
            "FireBarge",
            "Flagship",
            "Frigate",
            "Gunship",
            "Scout",
            "WarpGate",
        };

        private Type _agentType;
        private Type _bootstrapType;
        private Type _matchupsType;
        private Type _optionsType;
        private Type _selectorType;
        private Type _schemaType;
        private Scene _trainingScene;

        [SetUp]
        public void SetUp()
        {
            _agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            _bootstrapType = RuntimeAssembly.GetType("RlOneVsOneTrainingBootstrap");
            _matchupsType = RuntimeAssembly.GetType("RlOneVsOneEpisodeMatchups");
            _optionsType = RuntimeAssembly.GetType("RlOneVsOneTrainingOptions");
            _selectorType = RuntimeAssembly.GetType("RlOneVsOneEpisodeMatchupSelector");
            _schemaType = RuntimeAssembly.GetType("RlPolicySchema");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            RuntimeAssembly.SetStaticField(_bootstrapType, "_runtimeOptions", null);
            RuntimeAssembly.SetStaticField(_matchupsType, "_selector", null);

            if (_trainingScene.IsValid() && _trainingScene.isLoaded)
            {
                Scene cleanup = SceneManager.CreateScene($"RL Smoke Cleanup {Guid.NewGuid():N}");
                SceneManager.SetActiveScene(cleanup);
                AsyncOperation unload = SceneManager.UnloadSceneAsync(_trainingScene);
                while (unload != null && !unload.isDone)
                {
                    yield return null;
                }
            }
        }

        [UnityTest]
        public IEnumerator FullDirectRosterBindsToFrozenPolicyAndExecutesObservationAndActionPaths()
        {
            object options = ParseOptions(
                "--rl-matchup-mode", "sampled",
                "--rl-ships-per-side", SmokeShipsPerSide.ToString(),
                "--rl-health-ratio", "1",
                "--rl-map-size", "120",
                "--rl-episode-timeout", "600");
            RuntimeAssembly.SetStaticField(_bootstrapType, "_runtimeOptions", options);
            RuntimeAssembly.SetStaticField(_matchupsType, "_selector", CreateSelector(options, SmokeSeed));

            AsyncOperation load = SceneManager.LoadSceneAsync(TrainingSceneName, LoadSceneMode.Single);
            Assert.That(load, Is.Not.Null, $"Could not request {TrainingSceneName} scene load.");
            while (!load.isDone)
            {
                yield return null;
            }
            _trainingScene = SceneManager.GetActiveScene();
            Assert.That(_trainingScene.name, Is.EqualTo(TrainingSceneName));

            List<Component> boundAgents = null;
            for (int frame = 0; frame < MaximumReadyFrames; frame++)
            {
                boundAgents = FindBoundAgentsInTrainingScene();
                if (boundAgents.Count >= SmokeShipsPerSide * 2)
                {
                    break;
                }
                yield return null;
            }

            Assert.That(boundAgents, Is.Not.Null);
            Assert.That(boundAgents.Count, Is.EqualTo(SmokeShipsPerSide * 2),
                "Exactly one active self-play controller should bind each live direct-roster ship.");

            object configuration = RuntimeAssembly.GetStaticField(
                RuntimeAssembly.GetType("Assets.Scripts.ConfigData"), "Configuration");
            Assert.That(configuration, Is.Not.Null);
            int beeSide = Convert.ToInt32(GetMember(configuration, "BeeSide"));
            int humanSide = Convert.ToInt32(GetMember(configuration, "HumanSide"));

            HashSet<string> beeTypes = new HashSet<string>();
            HashSet<string> humanTypes = new HashSet<string>();
            MethodInfo validateShip = _schemaType.GetMethod(
                "TryValidateShip",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(validateShip, Is.Not.Null);

            Component waspAgent = null;
            foreach (Component agent in boundAgents)
            {
                object ship = RuntimeAssembly.GetField(agent, "_ship");
                Assert.That(ship, Is.Not.Null);

                object[] validationArguments = { ship, null };
                bool valid = (bool)validateShip.Invoke(null, validationArguments);
                Assert.That(valid, Is.True, validationArguments[1] as string);

                int side = (int)RuntimeAssembly.GetField(agent, "_side");
                string shipType = GetMember(ship, "ShipType").ToString();
                if (side == beeSide)
                {
                    beeTypes.Add(shipType);
                }
                else if (side == humanSide)
                {
                    humanTypes.Add(shipType);
                }
                else
                {
                    Assert.Fail($"RL agent bound unexpected side {side} for {shipType}.");
                }

                if (shipType == "Wasp")
                {
                    waspAgent = agent;
                }
            }

            CollectionAssert.IsSubsetOf(ExpectedBeeRoster, beeTypes,
                "One sampled multi-ship episode must instantiate and bind every directly trainable Bee type before repeats.");
            CollectionAssert.IsSubsetOf(ExpectedHumanRoster, humanTypes,
                "One sampled multi-ship episode must instantiate and bind every directly trainable Human type before repeats.");
            Assert.That(waspAgent, Is.Not.Null, "The full Bee roster smoke episode must contain a Wasp.");

            Assert.That(RuntimeAssembly.GetStaticField(_schemaType, "Version"), Is.EqualTo(4));
            Assert.That(RuntimeAssembly.GetStaticField(_agentType, "ObservationSize"), Is.EqualTo(4685));
            Assert.That(RuntimeAssembly.GetStaticField(_agentType, "ContinuousActionCount"), Is.EqualTo(34));

            object sensor = CreateVectorSensor(4685);
            MethodInfo collectObservations = _agentType.GetMethod("CollectObservations", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(collectObservations, Is.Not.Null);
            collectObservations.Invoke(waspAgent, new[] { sensor });

            MethodInfo getObservations = sensor.GetType().GetMethod("GetObservations", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(getObservations, Is.Not.Null, "ML-Agents VectorSensor must expose its collected vector for smoke validation.");
            object observations = getObservations.Invoke(sensor, null);
            Assert.That(RuntimeAssembly.GetCount(observations), Is.EqualTo(4685),
                "The live policy path must emit exactly the frozen observation count.");

            object actions = CreateZeroActionBuffers(34, 20);
            MethodInfo onActionReceived = _agentType.GetMethod("OnActionReceived", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(onActionReceived, Is.Not.Null);
            onActionReceived.Invoke(waspAgent, new[] { actions });

            object wasp = RuntimeAssembly.GetField(waspAgent, "_ship");
            Assert.That((bool)GetMember(wasp, "HasBrain"), Is.True,
                "A zero-action smoke decision must still traverse the live movement control primitive.");
        }

        private object ParseOptions(params string[] args)
        {
            MethodInfo parse = _optionsType.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(parse, Is.Not.Null);
            return parse.Invoke(null, new object[] { args });
        }

        private object CreateSelector(object options, int seed)
        {
            ConstructorInfo constructor = _selectorType.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { _optionsType, typeof(int) },
                null);
            Assert.That(constructor, Is.Not.Null);
            return constructor.Invoke(new[] { options, (object)seed });
        }

        private List<Component> FindBoundAgentsInTrainingScene()
        {
            UnityEngine.Object[] objects = Resources.FindObjectsOfTypeAll(_agentType);
            List<Component> bound = new List<Component>();
            for (int i = 0; i < objects.Length; i++)
            {
                Component agent = objects[i] as Component;
                if (agent == null || agent.gameObject.scene != _trainingScene)
                {
                    continue;
                }
                if ((bool)RuntimeAssembly.GetField(agent, "_hasBoundShip"))
                {
                    bound.Add(agent);
                }
            }
            return bound;
        }

        private static object CreateVectorSensor(int observationSize)
        {
            Type sensorType = FindLoadedType("Unity.MLAgents.Sensors.VectorSensor");
            ConstructorInfo constructor = sensorType.GetConstructors()
                .Where(candidate =>
                {
                    ParameterInfo[] parameters = candidate.GetParameters();
                    return parameters.Length > 0 && parameters[0].ParameterType == typeof(int);
                })
                .OrderBy(candidate => candidate.GetParameters().Length)
                .First();

            ParameterInfo[] parameterInfo = constructor.GetParameters();
            object[] arguments = new object[parameterInfo.Length];
            arguments[0] = observationSize;
            for (int i = 1; i < arguments.Length; i++)
            {
                arguments[i] = parameterInfo[i].HasDefaultValue
                    ? parameterInfo[i].DefaultValue
                    : parameterInfo[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(parameterInfo[i].ParameterType)
                        : null;
            }
            return constructor.Invoke(arguments);
        }

        private static object CreateZeroActionBuffers(int continuousCount, int discreteCount)
        {
            Type segmentDefinition = FindLoadedType("Unity.MLAgents.Actuators.ActionSegment`1");
            Type floatSegmentType = segmentDefinition.MakeGenericType(typeof(float));
            Type intSegmentType = segmentDefinition.MakeGenericType(typeof(int));
            object continuous = Activator.CreateInstance(floatSegmentType, new object[] { new float[continuousCount] });
            object discrete = Activator.CreateInstance(intSegmentType, new object[] { new int[discreteCount] });

            Type actionBuffersType = FindLoadedType("Unity.MLAgents.Actuators.ActionBuffers");
            ConstructorInfo constructor = actionBuffersType.GetConstructor(new[] { floatSegmentType, intSegmentType });
            Assert.That(constructor, Is.Not.Null);
            return constructor.Invoke(new[] { continuous, discrete });
        }

        private static Type FindLoadedType(string fullName)
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(candidate => candidate != null);
            Assert.That(type, Is.Not.Null, $"Could not find loaded runtime type {fullName}.");
            return type;
        }

        private static object GetMember(object instance, string name)
        {
            Type type = instance.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return field.GetValue(instance);
                }
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null)
                {
                    return property.GetValue(instance);
                }
                type = type.BaseType;
            }
            throw new MissingMemberException(instance.GetType().FullName, name);
        }
    }
}
