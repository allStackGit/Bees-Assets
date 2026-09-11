using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlScenarioSeedTests
    {
        [Test]
        public void ScenarioSeedIsRepeatableFromUnityRandomState()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type seedType = RuntimeAssembly.GetType("RlOneVsOneScenarioSeed");
            MethodInfo ensureInitialized = seedType.GetMethod("EnsureMlAgentsSeedIsInitialized", flags);
            MethodInfo create = seedType.GetMethod("Create", flags);

            Assert.That(ensureInitialized, Is.Not.Null);
            Assert.That(create, Is.Not.Null);

            // Initialize Academy before taking control of UnityEngine.Random so this test exercises
            // only the scenario-seed derivation rather than one-time ML-Agents startup side effects.
            ensureInitialized.Invoke(null, null);
            UnityEngine.Random.State previousState = UnityEngine.Random.state;
            try
            {
                UnityEngine.Random.InitState(731947);
                int firstA = (int)create.Invoke(null, null);
                int secondA = (int)create.Invoke(null, null);

                UnityEngine.Random.InitState(731947);
                int firstB = (int)create.Invoke(null, null);
                int secondB = (int)create.Invoke(null, null);

                Assert.That(firstB, Is.EqualTo(firstA));
                Assert.That(secondB, Is.EqualTo(secondA));
                Assert.That(secondA, Is.Not.EqualTo(firstA),
                    "Separate scenario samplers should receive distinct streams within one seeded run.");
            }
            finally
            {
                UnityEngine.Random.state = previousState;
            }
        }

        [Test]
        public void MapSizeRandomStreamsRemainIndependentPerArena()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type mapStateType = RuntimeAssembly.GetType("RlOneVsOneArenaMapSizeState");
            Type levelType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Level");
            MethodInfo reset = mapStateType.GetMethod("ResetForTests", flags);
            MethodInfo setSeed = mapStateType.GetMethod("SetRandomSeedForTests", flags);
            MethodInfo sample = mapStateType.GetMethod(
                "SampleMapSize",
                flags,
                null,
                new[] { levelType, typeof(float), typeof(float) },
                null);

            Assert.That(reset, Is.Not.Null);
            Assert.That(setSeed, Is.Not.Null);
            Assert.That(sample, Is.Not.Null);

            GameObject arenaAObject = new GameObject("RL scenario seed arena A");
            GameObject arenaBObject = new GameObject("RL scenario seed arena B");
            Component arenaA = arenaAObject.AddComponent(levelType);
            Component arenaB = arenaBObject.AddComponent(levelType);

            try
            {
                reset.Invoke(null, null);
                setSeed.Invoke(null, new object[] { arenaA, 424242 });
                setSeed.Invoke(null, new object[] { arenaB, 424242 });

                float arenaAFirst = (float)sample.Invoke(null, new object[] { arenaA, 64f, 128f });
                float arenaASecond = (float)sample.Invoke(null, new object[] { arenaA, 64f, 128f });

                // Arena B has not consumed any values while arena A advanced twice. Matching values
                // prove that one arena's asynchronous episode cadence cannot advance another's RNG.
                float arenaBFirst = (float)sample.Invoke(null, new object[] { arenaB, 64f, 128f });
                float arenaBSecond = (float)sample.Invoke(null, new object[] { arenaB, 64f, 128f });

                Assert.That(arenaBFirst, Is.EqualTo(arenaAFirst));
                Assert.That(arenaBSecond, Is.EqualTo(arenaASecond));
            }
            finally
            {
                reset.Invoke(null, null);
                UnityEngine.Object.DestroyImmediate(arenaAObject);
                UnityEngine.Object.DestroyImmediate(arenaBObject);
            }
        }

        [Test]
        public void RuntimeScenarioSamplersNoLongerUseGuidSeeds()
        {
            string matchups = ReadSource("Scripts", "Scenes", "RlOneVsOnePerArenaMatchups.cs");
            string mapSizes = ReadSource("Scripts", "Scenes", "RlOneVsOneArenaMapSizeState.cs");

            Assert.That(matchups, Does.Contain(
                "new RlOneVsOneEpisodeMatchupSelector(options, RlOneVsOneScenarioSeed.Create())"));
            Assert.That(mapSizes, Does.Contain("new System.Random(RlOneVsOneScenarioSeed.Create())"));
            Assert.That(matchups, Does.Not.Contain("Guid.NewGuid"));
            Assert.That(mapSizes, Does.Not.Contain("Guid.NewGuid"));
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
