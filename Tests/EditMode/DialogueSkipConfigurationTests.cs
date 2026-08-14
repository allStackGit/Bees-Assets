using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class DialogueSkipConfigurationTests
    {
        [Test]
        public void DialoguePresentationIsEnabledAndSkipPathPreservesCompletionCallbacks()
        {
            string config = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "ConfigData.Dialogue.cs"));
            string manager = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "DialogueManager.cs"));

            Assert.That(config, Does.Contain("public const bool SkipDialogue = false;"));
            Assert.That(manager, Does.Contain("if (ConfigData.SkipDialogue)"));
            Assert.That(manager, Does.Contain("CutsceneManager.BreakDialogue();"),
                "If development dialogue skipping is re-enabled, intermediate sections must still execute dialogue-break progression.");
            Assert.That(manager, Does.Contain("EndDialogue();"),
                "If development dialogue skipping is re-enabled, final sections must still execute normal dialogue completion.");
            Assert.That(manager, Does.Contain("DialogueBox.SetActive(false);"));
        }
    }
}
