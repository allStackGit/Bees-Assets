using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class PathfinderStaticRebuildConcurrencyTests
    {
        [Test]
        public void DirtyStaticLayerReservesIdleSlotsUntilActiveWorkerFinishes()
        {
            object pathfinder = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Levels.Pathfinder");
            bool[] active = { true, false, false };
            RuntimeAssembly.SetField(pathfinder, "IsThreadActive", active);
            RuntimeAssembly.SetField(pathfinder, "_staticRebuildBlockedSlots", null);

            RuntimeAssembly.Invoke(pathfinder, "MarkObstacleLayerDirty");

            Assert.That(RuntimeAssembly.GetField(pathfinder, "_staticObstacleRebuildPending"), Is.True);
            Assert.That(RuntimeAssembly.GetField(pathfinder, "_staticObstacleLayerDirty"), Is.False);
            Assert.That(active, Is.EqualTo(new[] { true, true, true }),
                "Idle worker slots must be reserved so a new search cannot start on a stale static map.");
            Assert.That(
                (bool[])RuntimeAssembly.GetField(pathfinder, "_staticRebuildBlockedSlots"),
                Is.EqualTo(new[] { false, true, true }));

            Assert.That(RuntimeAssembly.Invoke(pathfinder, "PreparePendingStaticObstacleRebuild"), Is.False,
                "The static map must not rebuild while a worker can still be reading its old snapshot.");

            active[0] = false; // completion application releases the real worker slot

            Assert.That(RuntimeAssembly.Invoke(pathfinder, "PreparePendingStaticObstacleRebuild"), Is.True);
            Assert.That(RuntimeAssembly.GetField(pathfinder, "_staticObstacleRebuildPending"), Is.False);
            Assert.That(RuntimeAssembly.GetField(pathfinder, "_staticObstacleLayerDirty"), Is.True);
            Assert.That(active, Is.EqualTo(new[] { false, false, false }),
                "Reserved slots must be released before queued requests are allowed to start on the rebuilt map.");
        }

        [Test]
        public void DirtyStaticLayerCanRebuildImmediatelyWhenNoWorkerIsActive()
        {
            object pathfinder = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Levels.Pathfinder");
            bool[] active = { false, false };
            RuntimeAssembly.SetField(pathfinder, "IsThreadActive", active);
            RuntimeAssembly.SetField(pathfinder, "_staticRebuildBlockedSlots", null);

            RuntimeAssembly.Invoke(pathfinder, "MarkObstacleLayerDirty");

            Assert.That(RuntimeAssembly.GetField(pathfinder, "_staticObstacleRebuildPending"), Is.False);
            Assert.That(RuntimeAssembly.GetField(pathfinder, "_staticObstacleLayerDirty"), Is.True);
            Assert.That(active, Is.EqualTo(new[] { false, false }));
        }
    }
}
