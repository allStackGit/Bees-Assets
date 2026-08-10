using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class DialogueManagerLifecycleTests
    {
        [Test]
        public void DialogueUpdateIsNullSafeAndAdvanceIsDebounced()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "UI Components", "DialogueManager.cs"));

            Assert.That(source, Does.Contain("_currentLine != null && _currentLine.IsOver"));
            Assert.That(source, Does.Contain("private bool _isAdvancingDialogue"));
            Assert.That(source, Does.Contain("if (_isAdvancingDialogue)"));
            Assert.That(source, Does.Contain("_isAdvancingDialogue = true"));
            Assert.That(source, Does.Contain("_isAdvancingDialogue = false"));
        }
    }
}
