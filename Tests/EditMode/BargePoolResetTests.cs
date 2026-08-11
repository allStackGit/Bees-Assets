using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class BargePoolResetTests
    {
        [Test]
        public void ClearDataDropsDeferredMovementFromPriorLifecycle()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts", "Entities", "Ships", "Barge.cs"));

            int methodStart = source.IndexOf("public override void ClearData()", System.StringComparison.Ordinal);
            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            int nextMethod = source.IndexOf("public override void Deactivate()", methodStart, System.StringComparison.Ordinal);
            Assert.That(nextMethod, Is.GreaterThan(methodStart));
            string clearData = source.Substring(methodStart, nextMethod - methodStart);

            Assert.That(clearData, Does.Contain("HasWaitingTargetCoordinates = false;"));
            Assert.That(clearData, Does.Contain("WaitingTargetCoordinates = Vector2.zero;"));
        }
    }
}
