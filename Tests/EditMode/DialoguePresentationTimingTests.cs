using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class DialoguePresentationTimingTests
    {
        [Test]
        public void IntercomSoundWaitsUntilDialogueHasRendered()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Scripts",
                "UI Components",
                "DialogueManager.cs"));

            string startDialogue = ExtractMethodBody(source, "public void StartDialogue(");
            StringAssert.DoesNotContain("PlayIntercomSound", startDialogue,
                "StartDialogue runs during level construction and must not play presentation audio synchronously.");
            StringAssert.Contains("_playIntercomWhenPresented = dialogueLines.Count > 0;", startDialogue);

            string typeLine = ExtractMethodBody(source, "IEnumerator TypeLine(");
            int renderedFrame = typeLine.IndexOf("yield return new WaitForEndOfFrame();", StringComparison.Ordinal);
            int intercom = typeLine.IndexOf("UIAudioController.Instance?.PlayIntercomSound();", StringComparison.Ordinal);

            Assert.That(renderedFrame, Is.GreaterThanOrEqualTo(0));
            Assert.That(intercom, Is.GreaterThan(renderedFrame),
                "The dialogue intercom cue must not play until after the presentation frame has rendered.");
        }

        private static string ExtractMethodBody(string source, string signature)
        {
            int method = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.That(method, Is.GreaterThanOrEqualTo(0), $"Could not find {signature}");
            int openingBrace = source.IndexOf('{', method);
            Assert.That(openingBrace, Is.GreaterThanOrEqualTo(0));

            int depth = 0;
            for (int i = openingBrace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(openingBrace, i - openingBrace + 1);
            }

            Assert.Fail($"Method {signature} has no balanced body.");
            return string.Empty;
        }
    }
}
