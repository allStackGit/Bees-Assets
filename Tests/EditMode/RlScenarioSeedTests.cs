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
        public void ScenarioSeedDerivationIsStableAndSeparatesArenaAndStream()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type seedType = RuntimeAssembly.GetType("RlOneVsOneScenarioSeed");
            MethodInfo derive = seedType.GetMethod("Derive", flags);
            FieldInfo matchupSaltField = seedType.GetField("MatchupStreamSalt", flags);
            FieldInfo mapSaltField = seedType.GetField("MapSizeStreamSalt", flags);

            Assert.That(derive, Is.Not.Null);
            Assert.That(matchupSaltField, Is.Not.Null);
            Assert.That(mapSaltField, Is.Not.Null);

            int matchupSalt = (int)matchupSaltField.GetRawConstantValue();
            int mapSalt = (int)mapSaltField.GetRawConstantValue();
            object[] arenaZeroMatchup = { 731947, 0, matchupSalt };

            int first = (int)derive.Invoke(null, arenaZeroMatchup);
            int repeated = (int)derive.Invoke(null, arenaZeroMatchup);
            int otherArena = (int)derive.Invoke(null, new object[] { 731947, 1, matchupSalt });
            int otherStream = (int)derive.Invoke(null, new object[] { 731947, 0, mapSalt });

            Assert.That(repeated, Is.EqualTo(first));
            Assert.That(otherArena, Is.Not.EqualTo(first));
            Assert.That(otherStream, Is.Not.EqualTo(first));
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
                "SampleMapSizeForLevel",
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
        public void RuntimeScenarioSamplersUseMlAgentsRootAndStableArenaStreams()
        {
            string matchups = ReadSource("Scripts", "Scenes", "RlOneVsOnePerArenaMatchups.cs");
            string mapSizes = ReadSource("Scripts", "Scenes", "RlOneVsOneArenaMapSizeState.cs");

            Assert.That(matchups, Does.Contain("_ = Academy.Instance;"));
            Assert.That(matchups, Does.Contain("UnityEngine.Random.Range(0, int.MaxValue)"));
            Assert.That(matchups, Does.Contain("GetArenaIndex(level)"));
            Assert.That(matchups, Does.Contain(
                "RlOneVsOneScenarioSeed.Create(level, RlOneVsOneScenarioSeed.MatchupStreamSalt)"));
            Assert.That(mapSizes, Does.Contain(
                "RlOneVsOneScenarioSeed.Create(level, RlOneVsOneScenarioSeed.MapSizeStreamSalt)"));
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
