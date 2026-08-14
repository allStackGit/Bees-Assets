using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class LevelIntroKeyboardTests
    {
        [Test]
        public void SpaceInvokesVisibleContinueButtonAfterDialogueEndsWithoutHidingSceneUpdate()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Scenes", "LevelIntro.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("protected override void Update()", source);
            StringAssert.Contains("base.Update();", source);
            StringAssert.Contains("if (!IsFinalized)", source);
            StringAssert.Contains("ContinueButton.activeInHierarchy", source);
            StringAssert.Contains("ContinueButtonAction.interactable", source);
            StringAssert.Contains("Input.GetKeyDown(KeyCode.Space)", source);
            StringAssert.Contains("ContinueButtonAction.onClick.Invoke();", source);
            StringAssert.DoesNotContain("private void Update()", source);
        }
    }
}
