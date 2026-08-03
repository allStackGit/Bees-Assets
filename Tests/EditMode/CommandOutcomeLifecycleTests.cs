using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class CommandOutcomeLifecycleTests
    {
        private GameObject _stageObject;
        private GameObject _levelObject;
        private GameObject _squadObject;
        private readonly System.Collections.Generic.List<GameObject> _commandObjects =
            new System.Collections.Generic.List<GameObject>();
        private Component _stage;
        private Component _level;
        private Component _state;
        private Component _squad;

        [SetUp]
        public void SetUp()
        {
            _stageObject = new GameObject(nameof(CommandOutcomeLifecycleTests) + " Stage");
            _stage = _stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
            Component debugLogger = _stageObject.AddComponent(RuntimeAssembly.GetType("DebugLogger"));
            RuntimeAssembly.SetField(_stage, "DebugLogger", debugLogger);
            RuntimeAssembly.SetField(_stage, "ActivateHiveMind", false);

            _levelObject = new GameObject(nameof(CommandOutcomeLifecycleTests) + " Level");
            _level = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            _state = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.GameState"));
            RuntimeAssembly.SetField(_level, "Stage", _stage);
            RuntimeAssembly.SetField(_level, "State", _state);
            RuntimeAssembly.SetField(_state, "Level", _level);
            RuntimeAssembly.SetField(_state, "Stage", _stage);
            ((Behaviour)_level).enabled = false;

            _squadObject = new GameObject(nameof(CommandOutcomeLifecycleTests) + " Squad");
            _squad = _squadObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Squad"));
            RuntimeAssembly.SetField(_squad, "Level", _level);
            RuntimeAssembly.SetField(_squad, "Stage", _stage);
            RuntimeAssembly.SetField(_squad, "Name", "Outcome test squad");
            RuntimeAssembly.SetField(_squad, "ItemId", 41);
            RuntimeAssembly.SetField(_squad, "IsDead", false);
            RuntimeAssembly.SetField(_squad, "IsSelected", false);
            ((Behaviour)_squad).enabled = false;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject commandObject in _commandObjects)
            {
                UnityEngine.Object.DestroyImmediate(commandObject);
            }
            UnityEngine.Object.DestroyImmediate(_squadObject);
            UnityEngine.Object.DestroyImmediate(_levelObject);
            UnityEngine.Object.DestroyImmediate(_stageObject);
        }

        [Test]
        public void DuplicateOutcomeRegistrationIsRejectedWithoutPartialHistoryMutation()
        {
            Component first = CreateCommand(outcomeId: 9001L, tsv: 10L);
            Component duplicate = CreateCommand(outcomeId: 9001L, tsv: 99L);

            Assert.That(RuntimeAssembly.Invoke(_state, "AddCommand", first), Is.True);
            LogAssert.Expect(LogType.Error, "Could not register duplicate command outcome #9001.");
            Assert.That(RuntimeAssembly.Invoke(_state, "AddCommand", duplicate), Is.False);

            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "PastCommands")), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetCount(
                RuntimeAssembly.GetField(_state, "OutcomeIdToPastCommandIndex")), Is.EqualTo(1));
            object stored = First(RuntimeAssembly.GetField(_state, "PastCommands"));
            Assert.That(RuntimeAssembly.GetField(stored, "Tsv"), Is.EqualTo(10L),
                "The duplicate command overwrote the original outcome record.");
        }

        [Test]
        public void FinalizeUpdatesOwnedRecordAndQueuesReleaseExactlyOnce()
        {
            Component command = CreateCommand(outcomeId: 9002L, tsv: 37L);
            Assert.That(RuntimeAssembly.Invoke(_state, "AddCommand", command), Is.True);
            RuntimeAssembly.SetField(command, "HasStoredOutcomeRecord", true);
            RuntimeAssembly.Invoke(_squad, "SetCommand", command);

            RuntimeAssembly.Invoke(command, "SetFinalize", "test completion");

            object stored = First(RuntimeAssembly.GetField(_state, "PastCommands"));
            Assert.That(RuntimeAssembly.GetField(stored, "IsFinalized"), Is.True);
            Assert.That(RuntimeAssembly.GetField(stored, "Tsv"), Is.EqualTo(37L));
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "CommandsToRelease")), Is.EqualTo(1));
            Assert.That(RuntimeAssembly.GetField(_squad, "HasCommand"), Is.False);
            Assert.That(RuntimeAssembly.Invoke(_squad, "GetCommand"), Is.Null);

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                "Trying to finalize a command .*already been finalized"));
            RuntimeAssembly.Invoke(command, "SetFinalize", "duplicate completion");
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "CommandsToRelease")), Is.EqualTo(1));
        }

        [Test]
        public void MissingOutcomeMappingReportsFailureWithoutCrashingOrUpdatingWrongRecord()
        {
            Component command = CreateCommand(outcomeId: 9003L, tsv: 55L);
            Assert.That(RuntimeAssembly.Invoke(_state, "AddCommand", command), Is.True);
            RuntimeAssembly.SetField(command, "HasStoredOutcomeRecord", true);
            RuntimeAssembly.Invoke(_squad, "SetCommand", command);
            object index = RuntimeAssembly.GetField(_state, "OutcomeIdToPastCommandIndex");
            index.GetType().GetMethod("Remove", new[] { typeof(long) }).Invoke(index, new object[] { 9003L });

            LogAssert.Expect(LogType.Error,
                "Could not finalize command outcome #9003: its stored-command mapping is missing or stale.");
            Assert.DoesNotThrow(() => RuntimeAssembly.Invoke(command, "SetFinalize", "missing mapping"));

            object stored = First(RuntimeAssembly.GetField(_state, "PastCommands"));
            Assert.That(RuntimeAssembly.GetField(stored, "IsFinalized"), Is.False);
            Assert.That(RuntimeAssembly.GetCount(RuntimeAssembly.GetField(_state, "CommandsToRelease")), Is.EqualTo(1));
        }

        private Component CreateCommand(long outcomeId, long tsv)
        {
            GameObject commandObject = new GameObject(nameof(CommandOutcomeLifecycleTests) + " Command");
            _commandObjects.Add(commandObject);
            Component command = commandObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Commands.Hold"));
            RuntimeAssembly.SetField(command, "Stage", _stage);
            RuntimeAssembly.SetField(command, "Level", _level);
            RuntimeAssembly.SetField(command, "OutcomeId", outcomeId);
            RuntimeAssembly.SetField(command, "Tsv", tsv);
            RuntimeAssembly.SetField(command, "IsHiveMindCommand", true);
            RuntimeAssembly.SetField(command, "IsDead", false);
            RuntimeAssembly.SetField(command, "MatchupStrategy", Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Commands.MatchupStrategy")));
            RuntimeAssembly.SetField(command, "ShootingStrategy", Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Commands.ShootingStrategy")));
            RuntimeAssembly.Invoke(command, "SetSquad", _squad);
            ((Behaviour)command).enabled = false;
            return command;
        }

        private static object First(object collection)
        {
            foreach (object item in (IEnumerable)collection)
            {
                return item;
            }
            throw new AssertionException("Expected a non-empty collection.");
        }
    }
}
