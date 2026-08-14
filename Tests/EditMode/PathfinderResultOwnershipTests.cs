using System;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class PathfinderResultOwnershipTests
    {
        private object _pathfinder;
        private GameObject _shipObject;
        private Component _ship;

        [SetUp]
        public void SetUp()
        {
            _pathfinder = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Levels.Pathfinder");
            RuntimeAssembly.SetField(_pathfinder, "IsThreadActive", new[] { false });
            RuntimeAssembly.SetField(_pathfinder, "Ships", Array.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"), 1));
            RuntimeAssembly.SetField(_pathfinder, "RequestIds", new int[1]);
            RuntimeAssembly.SetField(_pathfinder, "LifecycleIds", new int[1]);

            _shipObject = new GameObject(nameof(PathfinderResultOwnershipTests));
            _ship = _shipObject.AddComponent(
                RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship"));
            ((Behaviour)_ship).enabled = false;
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_shipObject);
        }

        [Test]
        public void OlderRequestCannotOverwriteNewerRequestInSameLifecycle()
        {
            const int lifecycleId = 4;
            ConfigureSlot(requestId: 10, lifecycleId);
            RuntimeAssembly.SetField(_ship, "PathfindingRequestId", 11);
            RuntimeAssembly.SetField(_ship, "PathfindingLifecycleId", lifecycleId);
            RuntimeAssembly.SetField(_ship, "PathfindingCompletedRequestId", -1);
            RuntimeAssembly.SetField(_ship, "PathfindingThreadComplete", false);

            ApplyResult(requestId: 10, lifecycleId, CreatePath(10));

            AssertRejectedResultReleasedSlot();
        }

        [Test]
        public void OldLifecycleCannotWinWhenPooledShipReusesSameNumericRequestId()
        {
            const int sharedRequestId = 7;
            ConfigureSlot(sharedRequestId, lifecycleId: 1);
            object currentPath = CreatePath(70);
            RuntimeAssembly.SetField(_ship, "PathfindingRequestId", sharedRequestId);
            RuntimeAssembly.SetField(_ship, "PathfindingLifecycleId", 2);
            RuntimeAssembly.SetField(_ship, "PathfindingCompletedRequestId", -1);
            RuntimeAssembly.SetField(_ship, "PathfindingThreadComplete", false);
            RuntimeAssembly.SetField(_ship, "PathfindingValue", currentPath);

            ApplyResult(sharedRequestId, lifecycleId: 1, CreatePath(71));

            AssertRejectedResultReleasedSlot();
            Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingValue"), Is.SameAs(currentPath));
        }

        [Test]
        public void CurrentOwnerResultIsPublishedAndSlotOwnershipIsCleared()
        {
            const int requestId = 12;
            const int lifecycleId = 5;
            object path = CreatePath(120);
            ConfigureSlot(requestId, lifecycleId);
            RuntimeAssembly.SetField(_ship, "PathfindingRequestId", requestId);
            RuntimeAssembly.SetField(_ship, "PathfindingLifecycleId", lifecycleId);
            RuntimeAssembly.SetField(_ship, "PathfindingThreadComplete", false);

            ApplyResult(requestId, lifecycleId, path);

            Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingValue"), Is.SameAs(path));
            Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingCompletedRequestId"), Is.EqualTo(requestId));
            Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingThreadComplete"), Is.True);
            AssertSlotReleased();
        }

        [Test]
        public void OldQueuedLifecycleCannotSuppressReusedShipsNewRequest()
        {
            Type waitingType = RuntimeAssembly.GetType("Assets.Scripts.Levels.Pathfinder+PathWaiting");
            object waitingQueue = Activator.CreateInstance(
                typeof(System.Collections.Generic.Queue<>).MakeGenericType(waitingType));
            object queuedShips = Activator.CreateInstance(
                typeof(System.Collections.Generic.HashSet<>).MakeGenericType(
                    RuntimeAssembly.GetType("Assets.Scripts.Entities.Ships.Ship")));
            RuntimeAssembly.SetField(_pathfinder, "PathsWaiting", waitingQueue);
            RuntimeAssembly.SetField(_pathfinder, "ShipsQueued", queuedShips);

            object oldRequest = CreateWaitingRequest(waitingType, requestId: 1, lifecycleId: 1);
            object newRequest = CreateWaitingRequest(waitingType, requestId: 1, lifecycleId: 2);
            RuntimeAssembly.Invoke(_pathfinder, "QueuePathRequest", oldRequest);
            RuntimeAssembly.Invoke(_pathfinder, "QueuePathRequest", newRequest);

            Assert.That(RuntimeAssembly.GetCount(waitingQueue), Is.EqualTo(2));
            object dequeuedOldRequest = waitingQueue.GetType().GetMethod("Dequeue").Invoke(waitingQueue, null);
            Assert.That(dequeuedOldRequest, Is.EqualTo(oldRequest));
            RuntimeAssembly.Invoke(_pathfinder, "ReleaseQueuedShipIfNoRemainingRequests", _ship);
            Assert.That(RuntimeAssembly.GetCount(queuedShips), Is.EqualTo(1),
                "The queue marker was removed while the reused Ship still had a current request.");

            waitingQueue.GetType().GetMethod("Dequeue").Invoke(waitingQueue, null);
            RuntimeAssembly.Invoke(_pathfinder, "ReleaseQueuedShipIfNoRemainingRequests", _ship);
            Assert.That(RuntimeAssembly.GetCount(queuedShips), Is.Zero);
        }

        private void ConfigureSlot(int requestId, int lifecycleId)
        {
            ((Array)RuntimeAssembly.GetField(_pathfinder, "Ships")).SetValue(_ship, 0);
            ((int[])RuntimeAssembly.GetField(_pathfinder, "RequestIds"))[0] = requestId;
            ((int[])RuntimeAssembly.GetField(_pathfinder, "LifecycleIds"))[0] = lifecycleId;
            ((bool[])RuntimeAssembly.GetField(_pathfinder, "IsThreadActive"))[0] = true;
        }

        private void ApplyResult(int requestId, int lifecycleId, object path)
        {
            RuntimeAssembly.Invoke(
                _pathfinder,
                "ApplyCompletedPathResult",
                _ship,
                requestId,
                lifecycleId,
                0,
                path);
        }

        private object CreatePath(int coordinate)
        {
            return Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Pathfinder+Path"),
                new object[] { coordinate, coordinate, coordinate + 1, coordinate + 1 });
        }

        private object CreateWaitingRequest(Type waitingType, int requestId, int lifecycleId)
        {
            return Activator.CreateInstance(
                waitingType,
                new object[] { _ship, 0, 0, 1, 1, 1, requestId, lifecycleId });
        }

        private void AssertRejectedResultReleasedSlot()
        {
            Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingCompletedRequestId"), Is.EqualTo(-1));
            Assert.That(RuntimeAssembly.GetField(_ship, "PathfindingThreadComplete"), Is.False);
            AssertSlotReleased();
        }

        private void AssertSlotReleased()
        {
            Assert.That(((bool[])RuntimeAssembly.GetField(_pathfinder, "IsThreadActive"))[0], Is.False);
            Assert.That(((Array)RuntimeAssembly.GetField(_pathfinder, "Ships")).GetValue(0), Is.Null);
            Assert.That(((int[])RuntimeAssembly.GetField(_pathfinder, "RequestIds"))[0], Is.Zero);
            Assert.That(((int[])RuntimeAssembly.GetField(_pathfinder, "LifecycleIds"))[0], Is.Zero);
        }
    }
}
