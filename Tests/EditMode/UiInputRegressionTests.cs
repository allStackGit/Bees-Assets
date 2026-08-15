using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class UiInputRegressionTests
    {
        [Test]
        public void TextEntryOwnsGameplayKeys()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "Levels", "HotKey.cs"));
            StringAssert.Contains("TMP_InputField", source);
            StringAssert.Contains("input.isFocused", source);
            StringAssert.Contains("_blockedByTextInput", source);
        }

        [Test]
        public void TextInputsUseButtonGreen()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "UI Components", "GameHudLayoutGuard.cs"));
            StringAssert.Contains("new Color32(30, 207, 136, 255)", source);
        }

        [Test]
        public void EdgeScrollUsesLiveClientDimensionsWithoutMutatingPreference()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "UI Components", "GameHudLayoutGuard.cs"));
            StringAssert.Contains("ConfigData.ScreenWidth = Screen.width;", source);
            StringAssert.Contains("ConfigData.ScreenHeight = Screen.height;", source);
            StringAssert.Contains("Screen.width != _lastScreenWidth", source);
            StringAssert.DoesNotContain("ConfigData.UserProgressData.UseMouseScrolling = false", source,
                "Display/focus compatibility code must not temporarily rewrite the user's edge-scroll setting.");
        }

        [Test]
        public void StandardAndDynamicButtonsHaveOneGenericSoundOwner()
        {
            string guardSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "UI Components", "ButtonSoundOwnershipGuard.cs"));
            string dialogueSource = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "UI Components", "Dialogue.cs"));

            StringAssert.Contains("UnityEventCallState.Off", guardSource);
            StringAssert.Contains("PlayButtonSoundIfActionDidNot", guardSource);
            StringAssert.Contains("ButtonSoundOwnershipGuard.Configure(button);", dialogueSource);
        }
    }
}
