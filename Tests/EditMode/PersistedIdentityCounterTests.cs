using System;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class PersistedIdentityCounterTests
    {
        [Test]
        public void ReconcileCounterAdvancesPastExistingObjects()
        {
            Type sceneType = RuntimeAssembly.GetType("Assets.Scripts.Scenes.Scene");
            object result = RuntimeAssembly.InvokeStatic(
                sceneType,
                "ReconcileCounterWithIds",
                1521,
                new long[] { 0, 1521, 1550 });

            Assert.That(result, Is.EqualTo(1550),
                "A stale persisted counter must advance to the highest loaded object ID before a new ID is allocated.");
        }

        [Test]
        public void ReconcileCounterNeverMovesBackward()
        {
            Type sceneType = RuntimeAssembly.GetType("Assets.Scripts.Scenes.Scene");
            object result = RuntimeAssembly.InvokeStatic(
                sceneType,
                "ReconcileCounterWithIds",
                2000,
                new long[] { 12, 500, 1550 });

            Assert.That(result, Is.EqualTo(2000));
        }

        [Test]
        public void ReconcileCounterKeepsCurrentValueWhenCollectionIsEmpty()
        {
            Type sceneType = RuntimeAssembly.GetType("Assets.Scripts.Scenes.Scene");
            object result = RuntimeAssembly.InvokeStatic(
                sceneType,
                "ReconcileCounterWithIds",
                42,
                Array.Empty<long>());

            Assert.That(result, Is.EqualTo(42));
        }
    }
}
