using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class UIAudioAuthoringTests
    {
        private string _assetsRoot;

        [SetUp]
        public void SetUp()
        {
            _assetsRoot = Application.dataPath;
        }

        [Test]
        public void UiAudioPrefabReferencesAllAuthoredFeedbackClips()
        {
            string prefab = Read("Prefabs", "UI", "UI Audio.prefab");

            Assert.That(prefab, Does.Contain("4a696d357b522cb4a8a4e7dfb81abd82"), "Delete-squad cue is not wired.");
            Assert.That(prefab, Does.Contain("1baa83f633ccaf54b841f4bf307a34af"), "Error cue is not wired.");
            Assert.That(prefab, Does.Contain("8022db085626e7344ad8edc5312032c4"), "Engine-hum cue is not wired.");
            Assert.That(prefab, Does.Contain("d0107bbafc4b7cf4c95ce20cc4c4e70f"), "Intercom cue is not wired.");
            Assert.That(prefab, Does.Contain("317f4b54581184b4e9d4065aa619a764"), "Save cue is not wired.");
        }

        [Test]
        public void UiFeedbackUsesOneShotsAndDedicatedIntroAmbience()
        {
            string source = Read("Scripts", "UI Components", "UIAudioController.cs");

            Assert.That(source, Does.Contain("PlayOneShot"), "UI feedback should not restart its source on rapid clicks.");
            Assert.That(source, Does.Contain("PlayDeleteSquadSound"));
            Assert.That(source, Does.Contain("PlayErrorSound"));
            Assert.That(source, Does.Contain("PlayIntercomSound"));
            Assert.That(source, Does.Contain("PlaySaveSound"));
            Assert.That(source, Does.Contain("PlayLevelIntroAmbience"));
            Assert.That(source, Does.Contain("_levelIntroAmbience.loop = true"));
            Assert.That(source, Does.Contain("_levelIntroAmbience.outputAudioMixerGroup = MenuMusic.outputAudioMixerGroup"));
        }

        [Test]
        public void IntercomPlaysOnlyWhenANewDialogueSectionStarts()
        {
            string source = Read("Scripts", "UI Components", "DialogueManager.cs");
            string startDialogue = ExtractMethodBody(source, "StartDialogue");
            string displayNextLine = ExtractMethodBody(source, "DisplayNextLine");

            Assert.That(startDialogue, Does.Contain("PlayIntercomSound"));
            Assert.That(displayNextLine, Does.Not.Contain("PlayIntercomSound"),
                "Advancing within the same dialogue must not replay the intercom cue.");
        }

        [Test]
        public void LevelIntroOwnsAndStopsEngineAmbience()
        {
            string source = Read("Scripts", "Scenes", "LevelIntro.cs");

            Assert.That(source, Does.Contain("PauseMusic"));
            Assert.That(source, Does.Contain("PlayLevelIntroAmbience"));
            Assert.That(source, Does.Contain("StopLevelIntroAmbience"));
        }

        [Test]
        public void BlockingAlertsAndSquadActionsUsePurposeSpecificFeedback()
        {
            string alert = Read("Scripts", "UI Components", "Alert.cs");
            string dialogue = Read("Scripts", "UI Components", "Dialogue.cs");

            Assert.That(alert, Does.Contain("new List<UnityAction>(), true"),
                "Blocking alerts should request error feedback.");
            Assert.That(dialogue, Does.Contain("action.Method.Name == \"DeleteCurrentSquad\""));
            Assert.That(dialogue, Does.Contain("PlayDeleteSquadSound"));
            Assert.That(dialogue, Does.Contain("buttonLabels.Count == 0 && buttonActions.Count == 0"));
            Assert.That(dialogue, Does.Contain("PlaySaveSound"));
        }

        [TestCase("Delete Squad Sound Effect.mp3.meta")]
        [TestCase("Error Sound Effect.mp3.meta")]
        [TestCase("Intercom Notification Sound Effect.mp3.meta")]
        [TestCase("Save Sound Effect.mp3.meta")]
        [TestCase("Engine Hum Sound Effect.mp3.meta")]
        public void NewUiSoundsArePreloaded(string fileName)
        {
            string meta = Read("Music", "Sound Effects", fileName);
            Assert.That(meta, Does.Contain("preloadAudioData: 1"),
                $"{fileName} should be ready on first use instead of incurring a UI-time load.");
        }

        private string Read(params string[] parts)
        {
            string path = _assetsRoot;
            foreach (string part in parts)
            {
                path = Path.Combine(path, part);
            }
            return File.ReadAllText(path);
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = source.IndexOf(" " + methodName + "(", StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), $"Could not find method {methodName}.");
            int openingBrace = source.IndexOf('{', signature);
            int depth = 0;
            for (int index = openingBrace; index < source.Length; index++)
            {
                if (source[index] == '{')
                {
                    depth++;
                }
                else if (source[index] == '}' && --depth == 0)
                {
                    return source.Substring(openingBrace, index - openingBrace + 1);
                }
            }

            Assert.Fail($"Method {methodName} has no balanced body.");
            return string.Empty;
        }
    }
}
