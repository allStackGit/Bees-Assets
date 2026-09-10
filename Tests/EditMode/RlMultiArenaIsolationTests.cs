using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
        private Type _perArenaMatchupsType;
        private Type _trainingOptionsType;
        private Type _matchupSelectorType;
        private Type _arenaMapSizeStateType;
        private Type _episodeResultType;
        private MethodInfo _prepareMatchupEpisode;
        private MethodInfo _handleMatchupEpisodeEnded;
        private MethodInfo _getSelectorCountForTests;
        private MethodInfo _resetMatchupsForTests;
        private MethodInfo _setMapSizeForTests;
        private MethodInfo _getMapSize;
        private MethodInfo _handleMapEpisodeEnded;
        private MethodInfo _getTrackedMapLevelCountForTests;
        private MethodInfo _resetMapSizesForTests;
        private GameObject _arenaAObject;
        private GameObject _arenaBObject;
        private Component _arenaA;
        private Component _arenaB;

        [SetUp]
        public void SetUp()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

            _policyFrameType = RuntimeAssembly.GetType("RlPolicyCoordinateFrame");
            _getQuarterTurns = _policyFrameType.GetMethod("GetQuarterTurns", flags);
            _endEpisode = _policyFrameType.GetMethod("EndEpisode", flags);
            _getAssignmentGeneration = _policyFrameType.GetMethod("GetAssignmentGenerationForTests", flags);
            _getActiveFrameCount = _policyFrameType.GetMethod("GetActiveFrameCountForTests", flags);
            _resetForTests = _policyFrameType.GetMethod("ResetForTests", flags);

            _multiArenaBootstrapType = RuntimeAssembly.GetType("RlOneVsOneMultiArenaBootstrap");
            _readRequestedArenaCount = _multiArenaBootstrapType.GetMethod("ReadRequestedArenaCount", flags);
            _buildLayout = _multiArenaBootstrapType.GetMethod("BuildLayout", flags);

            _perArenaMatchupsType = RuntimeAssembly.GetType("RlOneVsOnePerArenaMatchups");
            _trainingOptionsType = RuntimeAssembly.GetType("RlOneVsOneTrainingOptions");
            _matchupSelectorType = RuntimeAssembly.GetType("RlOneVsOneEpisodeMatchupSelector");
            _prepareMatchupEpisode = _perArenaMatchupsType.GetMethod("PrepareEpisode", flags);
            _handleMatchupEpisodeEnded = _perArenaMatchupsType.GetMethod("HandleEpisodeEnded", flags);
            _getSelectorCountForTests = _perArenaMatchupsType.GetMethod("GetSelectorCountForTests", flags);
            _resetMatchupsForTests = _perArenaMatchupsType.GetMethod("ResetForTests", flags);

            _arenaMapSizeStateType = RuntimeAssembly.GetType("RlOneVsOneArenaMapSizeState");
            _setMapSizeForTests = _arenaMapSizeStateType.GetMethod("SetMapSizeForTests", flags);
            _getMapSize = _arenaMapSizeStateType.GetMethod("GetMapSize", flags);
            _handleMapEpisodeEnded = _arenaMapSizeStateType.GetMethod("HandleEpisodeEnded", flags);
            _getTrackedMapLevelCountForTests = _arenaMapSizeStateType.GetMethod("GetTrackedLevelCountForTests", flags);
            _resetMapSizesForTests = _arenaMapSizeStateType.GetMethod("ResetForTests", flags);

            Type episodeCoordinatorType = RuntimeAssembly.GetType("RlOneVsOneEpisodeCoordinator");
            _episodeResultType = episodeCoordinatorType.GetNestedType(
                "EpisodeResult",
                BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(_getQuarterTurns, Is.Not.Null);
            Assert.That(_endEpisode, Is.Not.Null);
            Assert.That(_getAssignmentGeneration, Is.Not.Null);
            Assert.That(_getActiveFrameCount, Is.Not.Null);
            Assert.That(_resetForTests, Is.Not.Null);
            Assert.That(_readRequestedArenaCount, Is.Not.Null);
            Assert.That(_buildLayout, Is.Not.Null);
            Assert.That(_prepareMatchupEpisode, Is.Not.Null);
            Assert.That(_handleMatchupEpisodeEnded, Is.Not.Null);
            Assert.That(_getSelectorCountForTests, Is.Not.Null);
            Assert.That(_resetMatchupsForTests, Is.Not.Null);
            Assert.That(_setMapSizeForTests, Is.Not.Null);
            Assert.That(_getMapSize, Is.Not.Null);
            Assert.That(_handleMapEpisodeEnded, Is.Not.Null);
            Assert.That(_getTrackedMapLevelCountForTests, Is.Not.Null);
            Assert.That(_resetMapSizesForTests, Is.Not.Null);
            Assert.That(_episodeResultType, Is.Not.Null);

            _resetForTests.Invoke(null, null);
            _resetMatchupsForTests.Invoke(null, null);
            _resetMapSizesForTests.Invoke(null, null);
            Type levelType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Level");
            _arenaAObject = new GameObject("RL Test Arena A");
            _arenaBObject = new GameObject("RL Test Arena B");
            _arenaA = _arenaAObject.AddComponent(levelType);
            _arenaB = _arenaBObject.AddComponent(levelType);
        }

        [TearDown]
        public void TearDown()
        {
            _resetForTests?.Invoke(null, null);
            _resetMatchupsForTests?.Invoke(null, null);
            _resetMapSizesForTests?.Invoke(null, null);
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

        [Test]
        public void SampledMatchupOutcomeIsRecordedOnlyForTheArenaThatEnded()
        {
            object options = RuntimeAssembly.InvokeStatic(
                _trainingOptionsType,
                "Parse",
                (object)new[]
                {
                    "--rl-matchup-mode=sampled",
                    "--rl-bee-ship-types=Wasp,Hornet",
                    "--rl-human-ship-types=Gunship,Frigate"
                });
            ConstructorInfo selectorConstructor = _matchupSelectorType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { _trainingOptionsType, typeof(int) },
                null);
            Assert.That(selectorConstructor, Is.Not.Null);

            object selectorA = selectorConstructor.Invoke(new[] { options, (object)101 });
            object selectorB = selectorConstructor.Invoke(new[] { options, (object)202 });
            FieldInfo selectorsField = _perArenaMatchupsType.GetField(
                "Selectors",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(selectorsField, Is.Not.Null);
            IDictionary selectors = (IDictionary)selectorsField.GetValue(null);
            selectors[_arenaA] = selectorA;
            selectors[_arenaB] = selectorB;

            _prepareMatchupEpisode.Invoke(null, new object[] { _arenaA });
            _prepareMatchupEpisode.Invoke(null, new object[] { _arenaB });
            Assert.That((int)_getSelectorCountForTests.Invoke(null, null), Is.EqualTo(2));

            FieldInfo outcomeRecorded = _matchupSelectorType.GetField(
                "_currentOutcomeRecorded",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(outcomeRecorded, Is.Not.Null);
            Assert.That((bool)outcomeRecorded.GetValue(selectorA), Is.False);
            Assert.That((bool)outcomeRecorded.GetValue(selectorB), Is.False);

            object drawResult = Activator.CreateInstance(_episodeResultType);
            _handleMatchupEpisodeEnded.Invoke(null, new[] { _arenaA, drawResult });

            Assert.That((bool)outcomeRecorded.GetValue(selectorA), Is.True,
                "The completed arena must record the outcome against its own prepared matchup.");
            Assert.That((bool)outcomeRecorded.GetValue(selectorB), Is.False,
                "An asynchronously running arena must not consume or mutate another arena's outcome.");
        }

        [Test]
        public void EndingOneArenaInvalidatesOnlyItsOwnSampledMapSize()
        {
            _setMapSizeForTests.Invoke(null, new object[] { _arenaA, 64f });
            _setMapSizeForTests.Invoke(null, new object[] { _arenaB, 128f });

            Assert.That((int)_getTrackedMapLevelCountForTests.Invoke(null, null), Is.EqualTo(2));
            Assert.That((float)_getMapSize.Invoke(null, new object[] { _arenaA }), Is.EqualTo(64f));
            Assert.That((float)_getMapSize.Invoke(null, new object[] { _arenaB }), Is.EqualTo(128f));

            object drawResult = Activator.CreateInstance(_episodeResultType);
            _handleMapEpisodeEnded.Invoke(null, new[] { _arenaA, drawResult });

            Assert.That((int)_getTrackedMapLevelCountForTests.Invoke(null, null), Is.EqualTo(1));
            Assert.That((float)_getMapSize.Invoke(null, new object[] { _arenaB }), Is.EqualTo(128f),
                "Ending one arena must not replace the sampled map size of another active arena.");
        }

        [Test]
        public void SecondaryArenasApplyTheSameDurabilityCurriculumAsPrimaryArena()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "Scenes",
                "RlOneVsOneMultiArenaBootstrap.cs"));

            Assert.That(source, Does.Contain("for (int levelIndex = 1; levelIndex < _stage.Levels.Count; levelIndex++)"),
                "The multi-arena bootstrap must explicitly process every non-primary Level.");
            Assert.That(source, Does.Contain("RlOneVsOneTrainingDurabilityGuard.ApplyTrainingDurability("));
            Assert.That(source, Does.Contain("RlOneVsOneTrainingDurabilityGuard.TrainingHealthFraction"),
                "Secondary arenas must receive the same configured health fraction as PrimaryLevel.");
        }

        [Test]
        public void ArenaCountParserPreservesSingleArenaDefaultAndAcceptsExplicitCounts()
        {
            Assert.That(ReadRequestedArenaCount(null), Is.EqualTo(1));
            Assert.That(ReadRequestedArenaCount(Array.Empty<string>()), Is.EqualTo(1));
            Assert.That(ReadRequestedArenaCount(new[] { "--bees-rl-arenas-per-env", "4" }), Is.EqualTo(4));
            Assert.That(ReadRequestedArenaCount(new[] { "--bees-rl-arenas-per-env=8" }), Is.EqualTo(8));
        }

        [Test]
        public void ArenaCountParserRejectsInvalidCounts()
        {
            AssertInvalidArenaCount(new[] { "--bees-rl-arenas-per-env", "0" });
            AssertInvalidArenaCount(new[] { "--bees-rl-arenas-per-env", "17" });
            AssertInvalidArenaCount(new[] { "--bees-rl-arenas-per-env", "nope" });
            AssertInvalidArenaCount(new[] { "--bees-rl-arenas-per-env" });
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

        private void AssertInvalidArenaCount(string[] args)
        {
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
                () => _readRequestedArenaCount.Invoke(null, new object[] { args }));
            Assert.That(exception.InnerException, Is.TypeOf<ArgumentException>());
        }

        private int GetQuarterTurns(Component level, int teamId)
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
