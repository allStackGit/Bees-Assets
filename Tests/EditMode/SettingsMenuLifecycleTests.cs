using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SettingsMenuLifecycleTests
    {
        [Test]
        public void OpeningSettingsInitializesControlsExactlyOnce()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "UI Components", "SettingsMenu.cs"));

            Assert.That(source, Does.Contain("if (!_isSetup)"));
            Assert.That(source, Does.Contain("Stage ownerStage = GetComponentInParent<Stage>();"));
            Assert.That(source, Does.Contain("SetupSettings(ownerStage);"));
            Assert.That(source, Does.Contain("if (_isSetup)"));
        }
    }
}
