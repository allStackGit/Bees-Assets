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
        private Type _coordinatorType;
        private Type _matchupsType;
        private Type _optionsType;
        private Type _selectorType;
        private Type _schemaType;
        private Type _shipType;
        private Type _stageType;
        private Scene _trainingScene;

        [SetUp]
        public void SetUp()
        {
            _agentType = RuntimeAssembly.GetType("RlOneVsOneAgent");
            _bootstrapType = RuntimeAssembly.GetType("RlOneVsOneTrainingBootstrap");
            _coordinatorType = RuntimeAssembly.GetType("RlOneVsOneEpisodeCoordinator");
            _matchupsType = RuntimeAssembly.GetType("RlOneVsOneEpisodeMatchups");
            _optionsType = RuntimeAssembly.GetType("RlOneVsOneTrainingOptions");
            _selectorType = RuntimeAssembly.GetType("RlOneVsOneEpisodeMatchupSelector");
            _schemaType = RuntimeAssembly.GetType("RlPolicySchema");
            _shipType = RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship");
            _stageType = RuntimeAssembly.GetType("Stage");
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
        public IEnumerator FullDirectRosterBindsToFrozenPolicyAndExecutesObservationActionAndRewardPaths()
        {
            object options = ParseOptions(
                "--rl-matchup-mode", "sampled",
                "--rl-ships-per-side", SmokeShipsPerSide.ToString(),
                "--rl-health-ratio", "1",
                "--rl-map-size", "256",
                "--rl-episode-timeout", "600");
            RuntimeAssembly.SetStaticField(_bootstrapType, "_runtimeOptions", options);
            RuntimeAssembly.SetStaticField(_matchupsType, "_selector", CreateSelector(options, SmokeSeed));

            // RuntimeInitializeOnLoadMethod runs when a player starts, not reliably for every scene
            // that a PlayMode test loads later. Mirror the real AfterSceneLoad hooks explicitly in
            // sceneLoaded, which Unity invokes before Start, so this exercises the same production
            // bootstrap/coordinator/agent installer without depending on test-runner startup order.
            UnityEngine.Events.UnityAction<Scene, LoadSceneMode> sceneLoaded = BootstrapLoadedTrainingScene;
            SceneManager.sceneLoaded += sceneLoaded;
            AsyncOperation load;
            try
            {
                load = SceneManager.LoadSceneAsync(TrainingSceneName, LoadSceneMode.Single);
                Assert.That(load, Is.Not.Null, $"Could not request {TrainingSceneName} scene load.");
                while (!load.isDone)
                {
                    yield return null;
                }
            }
            finally
            {
                SceneManager.sceneLoaded -= sceneLoaded;
            }

            _trainingScene = SceneManager.GetActiveScene();
            Assert.That(_trainingScene.name, Is.EqualTo(TrainingSceneName));

            MethodInfo requiresPolicyControl = _agentType.GetMethod(
                "RequiresPolicyControl",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(requiresPolicyControl, Is.Not.Null);

            List<Component> liveShips = null;
            List<Component> boundAgents = null;
            int requiredPolicyControllers = int.MaxValue;
            for (int frame = 0; frame < MaximumReadyFrames; frame++)
            {
                liveShips = FindLiveShipsInTrainingScene();
                boundAgents = FindBoundAgentsInTrainingScene();
                requiredPolicyControllers = CountPolicyControlledShips(liveShips, requiresPolicyControl);
                if (liveShips.Count >= SmokeShipsPerSide * 2 && boundAgents.Count >= requiredPolicyControllers)
                {
                    break;
                }
                yield return null;
            }

            Assert.That(liveShips, Is.Not.Null);
            Assert.That(liveShips.Count, Is.GreaterThanOrEqualTo(SmokeShipsPerSide * 2),
                "The real training scene must instantiate both sampled primary fleets.");
            Assert.That(boundAgents, Is.Not.Null);
            Assert.That(boundAgents.Count, Is.GreaterThanOrEqualTo(requiredPolicyControllers),
                "Every live ship that requires policy control must acquire its active self-play controller.");

            object configuration = RuntimeAssembly.GetStaticField(
                RuntimeAssembly.GetType("Assets.Scripts.ConfigData"), "Configuration");
            Assert.That(configuration, Is.Not.Null);
            int beeSide = Convert.ToInt32(GetMember(configuration, "BeeSide"));
            int humanSide = Convert.ToInt32(GetMember(configuration, "HumanSide"));

            MethodInfo validateShip = _schemaType.GetMethod(
                "TryValidateShip",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(validateShip, Is.Not.Null);

            HashSet<string> beeTypes = new HashSet<string>();
            HashSet<string> humanTypes = new HashSet<string>();
            foreach (Component ship in liveShips)
            {
                object[] validationArguments = { ship, null };
                bool valid = (bool)validateShip.Invoke(null, validationArguments);
                Assert.That(valid, Is.True, validationArguments[1] as string);

                int side = Convert.ToInt32(GetMember(ship, "Side"));
                string shipTypeName = GetMember(ship, "ShipType").ToString();
                if (side == beeSide)
                {
                    beeTypes.Add(shipTypeName);
                }
                else if (side == humanSide)
                {
                    humanTypes.Add(shipTypeName);
                }
            }

            CollectionAssert.IsSubsetOf(ExpectedBeeRoster, beeTypes,
                "One sampled multi-ship episode must instantiate every directly trainable Bee type before repeats.");
            CollectionAssert.IsSubsetOf(ExpectedHumanRoster, humanTypes,
                "One sampled multi-ship episode must instantiate every directly trainable Human type before repeats.");

            List<Component> beeAgents = new List<Component>();
            List<Component> humanAgents = new List<Component>();
            Component waspAgent = null;
            foreach (Component agent in boundAgents)
            {
                object ship = RuntimeAssembly.GetField(agent, "_ship");
                Assert.That(ship, Is.Not.Null);
                int side = (int)RuntimeAssembly.GetField(agent, "_side");
                if (side == beeSide)
                {
                    beeAgents.Add(agent);
                }
                else if (side == humanSide)
                {
                    humanAgents.Add(agent);
                }
                else
                {
                    Assert.Fail($"RL agent bound unexpected side {side}.");
                }

                if (GetMember(ship, "ShipType").ToString() == "Wasp")
                {
                    waspAgent = agent;
                }
            }
            Assert.That(waspAgent, Is.Not.Null, "The full Bee roster smoke episode must bind a Wasp policy agent.");

            Assert.That(RuntimeAssembly.GetStaticField(_schemaType, "Version"), Is.EqualTo(4));
            Assert.That(RuntimeAssembly.GetStaticField(_agentType, "ObservationSize"), Is.EqualTo(4685));
            Assert.That(RuntimeAssembly.GetStaticField(_agentType, "ContinuousActionCount"), Is.EqualTo(34));

            object sensor = CreateVectorSensor(4685);
            MethodInfo collectObservations = _agentType.GetMethod("CollectObservations", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(collectObservations, Is.Not.Null);
            collectObservations.Invoke(waspAgent, new[] { sensor });

            MethodInfo getObservations = sensor.GetType().GetMethod(
                "GetObservations",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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

            AssertRewardRouting(beeAgents, humanAgents, beeSide, humanSide);
        }

        private void BootstrapLoadedTrainingScene(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != TrainingSceneName)
            {
                return;
            }

            _trainingScene = scene;
            SceneManager.SetActiveScene(scene);
            Component stage = FindComponentInScene(_stageType, scene);
            Assert.That(stage, Is.Not.Null, "RL runtime smoke could not find the training Stage before Start.");

            InvokeStatic(_bootstrapType, "Apply", stage);
            InvokeStatic(_coordinatorType, "AttachToDedicatedTrainingScene");
            InvokeStatic(_agentType, "InstallForDedicatedTrainingScene");
        }

        private int CountPolicyControlledShips(List<Component> ships, MethodInfo requiresPolicyControl)
        {
            int count = 0;
            for (int i = 0; i < ships.Count; i++)
            {
                if ((bool)requiresPolicyControl.Invoke(null, new object[] { ships[i] }))
                {
                    count++;
                }
            }
            return count;
        }

        private void AssertRewardRouting(
            List<Component> beeAgents,
            List<Component> humanAgents,
            int beeSide,
            int humanSide)
        {
            Assert.That(beeAgents.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(humanAgents.Count, Is.GreaterThanOrEqualTo(1));

            object beeShipA = RuntimeAssembly.GetField(beeAgents[0], "_ship");
            object beeShipB = RuntimeAssembly.GetField(beeAgents[1], "_ship");
            object humanShip = RuntimeAssembly.GetField(humanAgents[0], "_ship");

            MethodInfo recordHit = _coordinatorType.GetMethod("RecordHit", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo recordUnattributed = _coordinatorType.GetMethod(
                "RecordUnattributedTsvLoss",
                BindingFlags.Static | BindingFlags.NonPublic);
            EventInfo rewardEvent = _coordinatorType.GetEvent(
                "TsvRewardOccurred",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(recordHit, Is.Not.Null);
            Assert.That(recordUnattributed, Is.Not.Null);
            Assert.That(rewardEvent, Is.Not.Null);

            List<int> rewardSides = new List<int>();
            List<float> rewards = new List<float>();
            Action<int, float> handler = (side, reward) =>
            {
                rewardSides.Add(side);
                rewards.Add(reward);
            };
            MethodInfo addHandler = rewardEvent.GetAddMethod(true);
            MethodInfo removeHandler = rewardEvent.GetRemoveMethod(true);
            Assert.That(addHandler, Is.Not.Null);
            Assert.That(removeHandler, Is.Not.Null);
            addHandler.Invoke(null, new object[] { handler });

            try
            {
                rewardSides.Clear();
                rewards.Clear();
                recordHit.Invoke(null, new[] { beeShipA, humanShip, (object)10, (object)10 });
                Assert.That(rewards.Count, Is.EqualTo(2), "Enemy TSV loss should produce attacker credit and target penalty exactly once each.");
                Assert.That(HasReward(rewardSides, rewards, beeSide, positive: true), Is.True);
                Assert.That(HasReward(rewardSides, rewards, humanSide, positive: false), Is.True);

                rewardSides.Clear();
                rewards.Clear();
                recordHit.Invoke(null, new[] { beeShipA, beeShipB, (object)10, (object)10 });
                Assert.That(rewards.Count, Is.EqualTo(1), "Friendly fire must produce only the damaged side penalty.");
                Assert.That(rewardSides[0], Is.EqualTo(beeSide));
                Assert.That(rewards[0], Is.LessThan(0f));

                rewardSides.Clear();
                rewards.Clear();
                recordUnattributed.Invoke(null, new[] { humanShip, (object)10 });
                Assert.That(rewards.Count, Is.EqualTo(1), "Unattributed/self/environmental loss must produce one casualty penalty and no opponent credit.");
                Assert.That(rewardSides[0], Is.EqualTo(humanSide));
                Assert.That(rewards[0], Is.LessThan(0f));
            }
            finally
            {
                removeHandler.Invoke(null, new object[] { handler });
            }
        }

        private static bool HasReward(List<int> sides, List<float> rewards, int side, bool positive)
        {
            for (int i = 0; i < rewards.Count; i++)
            {
                if (sides[i] == side && (positive ? rewards[i] > 0f : rewards[i] < 0f))
                {
                    return true;
                }
            }
            return false;
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

        private List<Component> FindLiveShipsInTrainingScene()
        {
            UnityEngine.Object[] objects = Resources.FindObjectsOfTypeAll(_shipType);
            List<Component> ships = new List<Component>();
            for (int i = 0; i < objects.Length; i++)
            {
                Component ship = objects[i] as Component;
                if (ship == null || ship.gameObject.scene != _trainingScene)
                {
                    continue;
                }
                if (GetMember(ship, "Level") == null || GetMember(ship, "FleetShip") == null ||
                    (bool)GetMember(ship, "IsDead"))
                {
                    continue;
                }
                ships.Add(ship);
            }
            return ships;
        }

        private static Component FindComponentInScene(Type type, Scene scene)
        {
            UnityEngine.Object[] objects = Resources.FindObjectsOfTypeAll(type);
            for (int i = 0; i < objects.Length; i++)
            {
                Component component = objects[i] as Component;
                if (component != null && component.gameObject.scene == scene)
                {
                    return component;
                }
            }
            return null;
        }

        private static void InvokeStatic(Type type, string methodName, params object[] arguments)
        {
            MethodInfo method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing {type.FullName}.{methodName}.");
            method.Invoke(null, arguments);
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
            Type actionBuffersType = FindLoadedType("Unity.MLAgents.Actuators.ActionBuffers");
            ConstructorInfo constructor = actionBuffersType.GetConstructor(new[] { typeof(float[]), typeof(int[]) });
            Assert.That(constructor, Is.Not.Null);
            return constructor.Invoke(new object[] { new float[continuousCount], new int[discreteCount] });
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
