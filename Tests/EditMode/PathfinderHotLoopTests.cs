using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class PathfinderHotLoopTests
    {
        [Test]
        public void UpdateDoesNotLogLongRunningWorkersEveryFrame()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "Pathfinder.cs"));

            Assert.That(source, Does.Not.Contain("has been running for"),
                "A queued path backlog must not emit a Debug.Log every frame for long-running worker slots.");
        }
    }
}
