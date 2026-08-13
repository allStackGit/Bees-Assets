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
        public void PointerOutsideWindowCannotOwnEdgeScroll()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "UI Components", "GameHudLayoutGuard.cs"));
            StringAssert.Contains("mouse.x >= 0f && mouse.x < Screen.width", source);
            StringAssert.Contains("mouse.y >= 0f && mouse.y < Screen.height", source);
            StringAssert.Contains("RestoreMouseScrolling();", source);
        }

        [Test]
        public void StandardButtonsHaveOneGenericSoundOwner()
        {
            string source = File.ReadAllText(Path.Combine(Application.dataPath, "Scripts", "UI Components", "ButtonSoundOwnershipGuard.cs"));
            StringAssert.Contains("UnityEventCallState.Off", source);
            StringAssert.Contains("PlayButtonSoundIfActionDidNot", source);
        }
    }
}
