using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.PlayMode
{
    [TestFixture]
    [Category("BeesPlayModeFoundation")]
    public class TrackedTargetMovementOwnershipTests
    {
        private GameObject _stageObject;
        private GameObject _levelObject;
        private GameObject _shipObject;
        private object _stage;
        private object _level;
        private object _ship;

        [SetUp]
        public void SetUp()
        {
            _stageObject = new GameObject(nameof(TrackedTargetMovementOwnershipTests) + " Stage");
            _stage = _stageObject.AddComponent(RuntimeAssembly.GetType("Stage"));
            ((Behaviour)_stage).enabled = false;

            _levelObject = new GameObject(nameof(TrackedTargetMovementOwnershipTests) + " Level");
            _level = _levelObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"));
            ((Behaviour)_level).enabled = false;
            RuntimeAssembly.SetField(_level, "Stage", _stage);
            RuntimeAssembly.SetField(_level, "HasObstacles", false);

            _shipObject = new GameObject(nameof(TrackedTargetMovementOwnershipTests) + " Ship");
            _ship = _shipObject.AddComponent(RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Honeybee"));
            ((Behaviour)_ship).enabled = false;
            RuntimeAssembly.SetField(_ship, "Transform", _shipObject.transform);
            RuntimeAssembly.SetField(_ship, "Level", _level);
            RuntimeAssembly.SetField(_ship, "Stage", _stage);
            RuntimeAssembly.SetField(_ship, "CanOverrideBounds", true);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_shipObject);
            Object.DestroyImmediate(_levelObject);
            Object.DestroyImmediate(_stageObject);
        }

        [Test]
        public void RecurringPursuitPreservesWorkerRetryAndUsefulDestinationOwnership()
        {
            Vector2 initialDestination = new Vector2(20f, 0f);

            RuntimeAssembly.SetField(_ship, "IsPathfinding", true);
            RuntimeAssembly.Invoke(_ship, "MoveToTrackedPoint", initialDestination);
            Assert.That((bool)RuntimeAssembly.GetField(_ship, "HasTargetCoordinates"), Is.False,
                "A live worker must not be replaced by the recurring tracked-target update.");

            RuntimeAssembly.SetField(_ship, "IsPathfinding", false);
            RuntimeAssembly.SetField(_ship, "_tryingToFindPathAgain", true);
            RuntimeAssembly.Invoke(_ship, "MoveToTrackedPoint", initialDestination);
            Assert.That((bool)RuntimeAssembly.GetField(_ship, "HasTargetCoordinates"), Is.False,
                "The two-second failed-search retry owner must not be bypassed by Aggressive.");

            RuntimeAssembly.SetField(_ship, "_tryingToFindPathAgain", false);
            RuntimeAssembly.SetField(_ship, "IsFollowingPath", false);
            RuntimeAssembly.Invoke(_ship, "MoveToTrackedPoint", initialDestination);
            Assert.That((bool)RuntimeAssembly.GetField(_ship, "HasTargetCoordinates"), Is.True);
            Assert.That((Vector2)RuntimeAssembly.GetField(_ship, "FinalDestination"), Is.EqualTo(initialDestination));

            RuntimeAssembly.Invoke(_ship, "MoveToTrackedPoint", new Vector2(24f, 0f));
            Assert.That((Vector2)RuntimeAssembly.GetField(_ship, "FinalDestination"), Is.EqualTo(initialDestination),
                "Small target jitter must not rewrite a still-useful movement endpoint every 0.25 seconds.");

            Vector2 materialMove = new Vector2(32f, 0f);
            RuntimeAssembly.Invoke(_ship, "MoveToTrackedPoint", materialMove);
            Assert.That((Vector2)RuntimeAssembly.GetField(_ship, "FinalDestination"), Is.EqualTo(materialMove),
                "A materially moved target must still be followed on an obstacle-free map.");
        }
    }
}
