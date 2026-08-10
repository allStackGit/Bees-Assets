using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class DialogueAdvanceOwnershipTests
    {
        [Test]
        public void PauseLineHeldSpaceUsesSingleDebouncedAdvancePath()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "UI Components",
                "DialogueManager.cs"));

            int pauseBranch = source.IndexOf("if (line.Type == DialogueLine.DialogueType.Pause)");
            Assert.That(pauseBranch, Is.GreaterThanOrEqualTo(0));
            string pauseSource = source.Substring(pauseBranch, source.IndexOf("if (!line.IsSkipped)", pauseBranch) - pauseBranch);

            Assert.That(pauseSource, Does.Contain("DisplayNextLineWithDelay(0.02f);"));
            Assert.That(pauseSource, Does.Not.Contain("DisplayNextLine();"));
        }
    }
}
