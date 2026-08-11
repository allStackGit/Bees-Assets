using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SceneAuditInvariantTests
    {
        [Test]
        public void DeadVersionAlertIsShownOnlyOncePerSceneLifecycle()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Scenes", "Scene.cs"));

            Assert.That(source, Does.Contain("private bool _hasShownDeadVersionAlert;"));
            Assert.That(source, Does.Contain("if (!_hasShownDeadVersionAlert)"));
            Assert.That(source, Does.Contain("_hasShownDeadVersionAlert = true;"));
        }
    }
}
