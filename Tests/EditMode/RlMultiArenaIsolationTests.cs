using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Levels;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class RlMultiArenaIsolationTests
    {
        private Type _policyFrameType;
        private MethodInfo _getQuarterTurns;
        private MethodInfo _endEpisode;
        private MethodInfo _getAssignmentGeneration;
        private MethodInfo _getActiveFrameCount;
        private MethodInfo _resetForTests;
        private Type _multiArenaBootstrapType;
        private MethodInfo _readRequestedArenaCount;
        private MethodInfo _buildLayout;
        private GameObject _arenaAObject;
        private GameObject _arenaBObject;
        private Level _arenaA;
        private Level _arenaB;

        [SetUp]
        public void SetUp()
        {
            Assembly gameAssembly = typeof(Stage).Assembly;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

            _policyFrameType = gameAssembly.GetType("RlPolicyCoordinateFrame", true);
            _getQuarterTurns = _policyFrameType.GetMethod("GetQuarterTurns", flags);
            _endEpisode = _policyFrameType.GetMethod("EndEpisode", flags);
            _getAssignmentGeneration = _policyFrameType.GetMethod("GetAssignmentGenerationForTests", flags);
            _getActiveFrameCount = _policyFrameType.GetMethod("GetActiveFrameCountForTests", flags);
            _resetForTests = _policyFrameType.GetMethod("ResetForTests", flags);

            _multiArenaBootstrapType = gameAssembly.GetType("RlOneVsOneMultiArenaBootstrap", true);
            _readRequestedArenaCount = _multiArenaBootstrapType.GetMethod("ReadRequestedArenaCount", flags);
            _buildLayout = _multiArenaBootstrapType.GetMethod("BuildLayout", flags);

            Assert.That(_getQuarterTurns, Is.Not.Null);
            Assert.That(_endEpisode, Is.Not.Null);
            Assert.That(_getAssignmentGeneration, Is.Not.Null);
            Assert.That(_getActiveFrameCount, Is.Not.Null);
            Assert.That(_resetForTests, Is.Not.Null);
            Assert.That(_readRequestedArenaCount, Is.Not.Null);
            Assert.That(_buildLayout, Is.Not.Null);

            _resetForTests.Invoke(null, null);
            _arenaAObject = new GameObject("RL Test Arena A");
            _arenaBObject = new GameObject("RL Test Arena B");
            _arenaA = _arenaAObject.AddComponent<Level>();
            _arenaB = _arenaBObject.AddComponent<Level>();
        }

        [TearDown]
        public void TearDown()
        {
            _resetForTests?.Invoke(null, null);
            if (_arenaAObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_arenaAObject);
            }
            if (_arenaBObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_arenaBObject);
            }
        }

        [Test]
        public void PolicyFramesAreStoredAndEndedPerArena()
        {
            int arenaATeam0 = GetQuarterTurns(_arenaA, 0);
            int arenaATeam1 = GetQuarterTurns(_arenaA, 1);
            int arenaBTeam0 = GetQuarterTurns(_arenaB, 0);
            int arenaBTeam1 = GetQuarterTurns(_arenaB, 1);

            Assert.That(arenaATeam0, Is.Not.EqualTo(arenaATeam1));
            Assert.That(arenaBTeam0, Is.Not.EqualTo(arenaBTeam1));
            Assert.That(GetActiveFrameCount(), Is.EqualTo(2));
            Assert.That(GetAssignmentGeneration(), Is.EqualTo(2));

            _endEpisode.Invoke(null, new object[] { _arenaA });

            Assert.That(GetActiveFrameCount(), Is.EqualTo(1));
            Assert.That(GetQuarterTurns(_arenaB, 0), Is.EqualTo(arenaBTeam0));
            Assert.That(GetQuarterTurns(_arenaB, 1), Is.EqualTo(arenaBTeam1));
            Assert.That(GetAssignmentGeneration(), Is.EqualTo(2),
                "Ending one arena must not recreate the policy frame of another active arena.");

            _endEpisode.Invoke(null, new object[] { _arenaB });
            Assert.That(GetActiveFrameCount(), Is.EqualTo(0));
        }

        [Test]
        public void EndingOneArenaDoesNotConsumeAnotherArenasFrame()
        {
            GetQuarterTurns(_arenaA, 0);
            GetQuarterTurns(_arenaB, 0);
            Assert.That(GetAssignmentGeneration(), Is.EqualTo(2));

            _endEpisode.Invoke(null, new object[] { _arenaA });
            GetQuarterTurns(_arenaA, 0);

            Assert.That(GetAssignmentGeneration(), Is.EqualTo(3));
            Assert.That(GetActiveFrameCount(), Is.EqualTo(2));
        }

        [TestCase(null, 1)]
        [TestCase(new string[0], 1)]
        [TestCase(new[] { "--bees-rl-arenas-per-env", "4" }, 4)]
        [TestCase(new[] { "--bees-rl-arenas-per-env=8" }, 8)]
        public void ArenaCountParserPreservesSingleArenaDefaultAndAcceptsExplicitCounts(string[] args, int expected)
        {
            Assert.That(ReadRequestedArenaCount(args), Is.EqualTo(expected));
        }

        [TestCase(new[] { "--bees-rl-arenas-per-env", "0" })]
        [TestCase(new[] { "--bees-rl-arenas-per-env", "17" })]
        [TestCase(new[] { "--bees-rl-arenas-per-env", "nope" })]
        [TestCase(new[] { "--bees-rl-arenas-per-env" })]
        public void ArenaCountParserRejectsInvalidCounts(string[] args)
        {
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
                () => _readRequestedArenaCount.Invoke(null, new object[] { args }));
            Assert.That(exception.InnerException, Is.TypeOf<ArgumentException>());
        }

        [Test]
        public void MultiArenaLayoutKeepsArenaCentersUniqueAndSeparated()
        {
            const int arenaCount = 8;
            const float maximumMapSize = 128f;
            Vector2[] positions = BuildLayout(arenaCount, maximumMapSize);

            Assert.That(positions.Length, Is.EqualTo(arenaCount));
            HashSet<Vector2> uniquePositions = new HashSet<Vector2>(positions);
            Assert.That(uniquePositions.Count, Is.EqualTo(arenaCount));

            // BuildLayout uses max(256, mapSize + 128) as center-to-center spacing. At a 128-unit
            // maximum map size this is 256, leaving a full 128-unit guard band between arena edges.
            for (int left = 0; left < positions.Length; left++)
            {
                for (int right = left + 1; right < positions.Length; right++)
                {
                    Vector2 delta = positions[left] - positions[right];
                    bool separatedOnAxis = Mathf.Abs(delta.x) >= 256f || Mathf.Abs(delta.y) >= 256f;
                    Assert.That(separatedOnAxis, Is.True,
                        $"Arena centers {left} and {right} are not separated by the expected guard spacing.");
                }
            }
        }

        private int GetQuarterTurns(Level level, int teamId)
        {
            return (int)_getQuarterTurns.Invoke(null, new object[] { level, teamId });
        }

        private int GetAssignmentGeneration()
        {
            return (int)_getAssignmentGeneration.Invoke(null, null);
        }

        private int GetActiveFrameCount()
        {
            return (int)_getActiveFrameCount.Invoke(null, null);
        }

        private int ReadRequestedArenaCount(string[] args)
        {
            return (int)_readRequestedArenaCount.Invoke(null, new object[] { args });
        }

        private Vector2[] BuildLayout(int arenaCount, float maximumMapSize)
        {
            return (Vector2[])_buildLayout.Invoke(null, new object[] { arenaCount, maximumMapSize });
        }
    }
}
