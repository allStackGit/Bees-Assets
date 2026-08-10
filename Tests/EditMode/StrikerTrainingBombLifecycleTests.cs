using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class StrikerTrainingBombLifecycleTests
    {
        [Test]
        public void DelayedTrainingBombCannotDamageReusedTargetWrapper()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Entities", "Ships", "Striker.cs"));

            Assert.That(source, Does.Contain("private long _trainingBombTargetRuntimeId"));
            Assert.That(source, Does.Contain("_trainingBombTargetRuntimeId = ContactedShip != null ? ContactedShip.Id : 0"));
            Assert.That(source, Does.Contain("ContactedShip.Id != _trainingBombTargetRuntimeId"));
            Assert.That(source, Does.Contain("_trainingBombTargetRuntimeId = 0"));
        }
    }
}
