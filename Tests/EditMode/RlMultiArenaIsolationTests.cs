using System;
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
        private GameObject _arenaAObject;
        private GameObject _arenaBObject;
        private Level _arenaA;
        private Level _arenaB;

        [SetUp]
        public void SetUp()
        {
            Assembly gameAssembly = typeof(Stage).Assembly;
            _policyFrameType = gameAssembly.GetType("RlPolicyCoordinateFrame", true);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            _getQuarterTurns = _policyFrameType.GetMethod("GetQuarterTurns", flags);
            _endEpisode = _policyFrameType.GetMethod("EndEpisode", flags);
            _getAssignmentGeneration = _policyFrameType.GetMethod("GetAssignmentGenerationForTests", flags);
            _getActiveFrameCount = _policyFrameType.GetMethod("GetActiveFrameCountForTests", flags);
            _resetForTests = _policyFrameType.GetMethod("ResetForTests", flags);

            Assert.That(_getQuarterTurns, Is.Not.Null);
            Assert.That(_endEpisode, Is.Not.Null);
            Assert.That(_getAssignmentGeneration, Is.Not.Null);
            Assert.That(_getActiveFrameCount, Is.Not.Null);
            Assert.That(_resetForTests, Is.Not.Null);

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
    }
}
