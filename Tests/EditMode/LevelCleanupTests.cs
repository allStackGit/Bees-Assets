using System.Collections.Generic;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class LevelCleanupTests
    {
        [Test]
        public void RemovingLevelHandledRequestsPrunesOnlyThatLevelsHashes()
        {
            var allHandledRequests = new HashSet<long> { 10L, 20L, 30L };
            var levelHandledRequests = new HashSet<long> { 10L, 30L, 99L };

            RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType("Assets.Scripts.Levels.Level"),
                "RemoveHandledRequests",
                allHandledRequests,
                levelHandledRequests);

            Assert.That(allHandledRequests, Is.EquivalentTo(new[] { 20L }));
            Assert.That(levelHandledRequests, Is.Empty);
        }
    }
}
