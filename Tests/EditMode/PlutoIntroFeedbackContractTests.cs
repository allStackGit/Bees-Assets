using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class PlutoIntroFeedbackContractTests
    {
        [Test]
        public void FirstBeeRevealUsesEasedCameraFocusAndTemporaryZoom()
        {
            string source = Read("Scripts", "UI Components", "PlutoIntroFeedbackGuard.cs");

            StringAssert.Contains("RevealDuration = 0.7f", source);
            StringAssert.Contains("_stage.IsFollowingShip = false;", source);
            StringAssert.Contains("_revealHoneybee.GetPosition()", source);
            StringAssert.Contains("Mathf.SmoothStep", source);
            StringAssert.Contains("Mathf.Sin(Mathf.PI", source);
            StringAssert.Contains("_stage.IsPlayerControlling = false;", source);
            StringAssert.Contains("_stage.IsPlayerControlling = _restorePlayerControl;", source);
            StringAssert.DoesNotContain("ToggleFogOfWar", source);
        }

        [Test]
        public void IntroCommandTutorialStartsSmallHighlightsAttackAndRestoresAdvancedCommands()
        {
            string source = Read("Scripts", "UI Components", "PlutoIntroFeedbackGuard.cs");

            StringAssert.Contains("RememberAndHide(actionBox.PatrolButton);", source);
            StringAssert.Contains("RememberAndHide(actionBox.GuardButton);", source);
            StringAssert.Contains("RememberAndHide(actionBox.HoldButton);", source);
            StringAssert.Contains("RememberAndHide(actionBox.LockOnButton);", source);
            StringAssert.Contains("actionBox.AttackOnSightButton", source);
            StringAssert.Contains("CommandPulseAmount", source);
            StringAssert.Contains("gunship.Squad.AttackOnSight", source);
            StringAssert.Contains("RestoreCommandPresentation();", source);
        }

        [Test]
        public void SquadMakerTutorialKeepsExplicitCloseControl()
        {
            string source = Read("Scripts", "UI Components", "SquadMakerFeedbackAdjustmentGuard.cs");

            StringAssert.Contains("\"Squad Maker Tutorial\"", source);
            StringAssert.Contains("\"Close Button\"", source);
            StringAssert.Contains("_tutorial.ShowSequence", source);
            StringAssert.Contains("}, true, () =>", source);
        }

        [Test]
        public void MissionSummaryOffersContinueWithoutRemovingCloseButtonBehavior()
        {
            string source = Read("Scripts", "UI Components", "SummaryClosePressGuard.cs");

            StringAssert.Contains("ContinueButtonName = \"Continue Button\"", source);
            StringAssert.Contains("label.text = \"CONTINUE\"", source);
            StringAssert.Contains("button.onClick.AddListener(menus.HideMissionSummary);", source);
            StringAssert.Contains("EventTriggerType.PointerDown", source);
        }

        private static string Read(params string[] parts)
        {
            string path = Application.dataPath;
            for (int i = 0; i < parts.Length; i++)
            {
                path = Path.Combine(path, parts[i]);
            }
            return File.ReadAllText(path);
        }
    }
}
