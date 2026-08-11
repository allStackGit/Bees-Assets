using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class PathfinderWorkerBudgetTests
    {
        [Test]
        public void PathfinderConcurrencyIsCappedToLeaveUnityCpuHeadroom()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "ConfigData.cs"));

            Assert.That(source, Does.Contain("private static int _maxThreads = 1;"));
            Assert.That(source, Does.Contain("set => _maxThreads = Mathf.Clamp(value, 1, 4);"),
                "CPU-heavy A* searches must not scale to nearly every logical processor and starve Unity after squad attack orders.");
        }
    }
}
