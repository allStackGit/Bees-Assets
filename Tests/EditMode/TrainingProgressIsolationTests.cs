using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TrainingProgressIsolationTests
    {
        private GameObject _stageObject;
        private GameObject _levelObject;
        private Component _stage;
        private Component _level;

        [SetUp]
        public void SetUp()
        {
            _stageObject = new GameObject(nameof(TrainingProgressIsolationTests) + " Stage");
            _stage = _stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
            ((Behaviour)_stage).enabled = false;

            _levelObject = new GameObject(nameof(TrainingProgressIsolationTests) + " Level");
            _level = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            ((Behaviour)_level).enabled = false;

            RuntimeAssembly.SetField(_level, "Stage", _stage);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_levelObject);
            Object.DestroyImmediate(_stageObject);
        }

        [Test]
        public void TrainingResultReturnsBeforePlayerProgressDependencies()
        {
            RuntimeAssembly.SetField(_stage, "IsTraining", true);
            RuntimeAssembly.SetField(_level, "DidUserWin", true);

            MethodInfo recordPlayerLevelResult = _level.GetType().GetMethod(
                "RecordPlayerLevelResult",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(recordPlayerLevelResult, Is.Not.Null);
            Assert.DoesNotThrow(() => recordPlayerLevelResult.Invoke(_level, null),
                "Training result handling should return before touching player profile/UI dependencies.");
            Assert.That(RuntimeAssembly.GetField(_level, "DidUserWin"), Is.False,
                "Automated training has no user-specific win result.");
        }
    }
}
