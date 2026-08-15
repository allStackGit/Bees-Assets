using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class GameHudLayoutGuardTests
    {
        private static string ReadGuardSource()
        {
            return File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "GameHudLayoutGuard.cs"));
        }

        private static string ReadDialogueSource()
        {
            return File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "Dialogue.cs"));
        }

        private static string ReadGameMenusSource()
        {
            return File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "GameMenus.cs"));
        }

        [Test]
        public void ScreenSpaceCanvasesKeepWholeReferenceAreaAcrossAspectRatios()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("ApplyAspectRatioSafeCanvasScaling(scene);"));
            Assert.That(source, Does.Contain("CanvasScaler.ScaleMode.ScaleWithScreenSize"));
            Assert.That(source, Does.Contain("CanvasScaler.ScreenMatchMode.Expand"),
                "Wide/tall devices must preserve the entire authored UI reference rectangle instead of cropping one axis.");
            Assert.That(source, Does.Contain("canvas.renderMode == RenderMode.WorldSpace"),
                "World-space gameplay canvases must not be altered by the screen UI compatibility pass.");
        }

        [Test]
        public void SquadTabsArePinnedToTopLeftInListOrderAndRenormalizedWhenAdded()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("_menus.Stage.SquadTabs"));
            Assert.That(source, Does.Contain("tabCount == _normalizedSquadTabCount"),
                "Late-created squad tabs must cause the complete tab row to be normalized again.");
            Assert.That(source, Does.Contain("rect.anchorMin = new Vector2(0f, 1f);"));
            Assert.That(source, Does.Contain("rect.anchorMax = new Vector2(0f, 1f);"));
            Assert.That(source, Does.Contain("rect.pivot = new Vector2(0f, 1f);"));
            Assert.That(source, Does.Contain("rect.anchoredPosition = new Vector2(x, -SquadTabTopMargin);"));
        }

        [Test]
        public void BottomActionBoxUsesVisibleDescendantBoundsAndStaysInsideCanvas()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("KeepActionBoxWithinCanvas();"));
            Assert.That(source, Does.Contain("RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, actionRect)"),
                "The ActionBox has a legacy zero-sized root, so the guard must clamp its visible descendants rather than its root rect.");
            Assert.That(source, Does.Contain("available.yMin + BottomHudMargin"));
            Assert.That(source, Does.Contain("available.yMax - BottomHudMargin"));
            Assert.That(source, Does.Contain("actionRect.position += worldCorrection;"));
            Assert.That(source, Does.Contain("_actionBoxNeedsClamp = true;"),
                "The ActionBox must be checked again when it becomes visible or the resolution changes.");
        }

        [Test]
        public void VisibleMissionClockMovesGameSpeedButtonBesideIt()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("_menus.Clock.activeInHierarchy"));
            Assert.That(source, Does.Contain("_clockRect.rect.width + _speedRect.rect.width"));
            Assert.That(source, Does.Contain("ControlGap"));
            Assert.That(source, Does.Contain("Vector2 desiredPosition = new Vector2(x, y);"));
            Assert.That(source, Does.Contain("_speedRect.anchoredPosition != desiredPosition"));
            Assert.That(source, Does.Contain("_speedRect.anchoredPosition = desiredPosition;"));
            Assert.That(source, Does.Contain("_speedRect.anchoredPosition = _normalSpeedPosition;"));
        }

        [Test]
        public void VisiblePlutoShieldAlignsGameSpeedButtonWithEvacuationCounterTop()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("_menus.PlutoShield.activeInHierarchy"));
            Assert.That(source, Does.Contain("_menus.Counter.activeInHierarchy"));
            Assert.That(source, Does.Contain("_counterRect.anchoredPosition.y"));
            Assert.That(source, Does.Contain("_counterRect.rect.height - _speedRect.rect.height"));
            Assert.That(source, Does.Contain("((_counterRect.rect.height - _speedRect.rect.height) * 0.5f)"));
        }

        [Test]
        public void ShieldOnlyLayoutStillPlacesGameSpeedButtonBelowShield()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("_plutoShieldRect.anchoredPosition.y"));
            Assert.That(source, Does.Contain("_plutoShieldRect.rect.height + _speedRect.rect.height"));
            Assert.That(source, Does.Contain("((_plutoShieldRect.rect.height + _speedRect.rect.height) * 0.5f) - ControlGap"));
        }

        [Test]
        public void TextInputsMatchButtonGreenAndStayBrightWhenSelected()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("GetComponentsInChildren<TMP_InputField>(true)"));
            Assert.That(source, Does.Contain("new Color32(30, 207, 136, 255)"));
            Assert.That(source, Does.Contain("colors.selectedColor = Color.white;"));
            Assert.That(source, Does.Contain("input.textComponent.color = foreground;"));
            Assert.That(source, Does.Contain("input.caretColor = foreground;"));
        }

        [Test]
        public void FreePlaySideSwitchFollowsBeeProgression()
        {
            string source = ReadGameMenusSource();

            Assert.That(source, Does.Contain("VisibleBeeShipTypes.Contains(ConfigData.ShipTypes.Beehive)"));
            Assert.That(source, Does.Contain("SwitchSidesButton.SetActive(false);"));

            int switchMethod = source.IndexOf("public void SwitchSides()", StringComparison.Ordinal);
            int progressionCheck = source.IndexOf("!IsBeeFreePlaySideAvailable", switchMethod, StringComparison.Ordinal);
            int earlyReturn = source.IndexOf("return;", progressionCheck, StringComparison.Ordinal);
            int closeLevel = source.IndexOf("CurrentLevel.CloseLevel();", switchMethod, StringComparison.Ordinal);

            Assert.That(progressionCheck, Is.GreaterThan(switchMethod));
            Assert.That(earlyReturn, Is.GreaterThan(progressionCheck));
            Assert.That(closeLevel, Is.GreaterThan(earlyReturn),
                "The side switch must stop before closing the current free-play level when the Bee progression marker is absent.");
        }

        [Test]
        public void ReplacingDialogueButtonPreservesItsLayoutPosition()
        {
            string source = ReadDialogueSource();

            Assert.That(source, Does.Contain("int siblingIndex = previousButton.transform.GetSiblingIndex();"));
            Assert.That(source, Does.Contain("previousButton.SetActive(false);"));
            Assert.That(source, Does.Contain("replacementButton.transform.SetSiblingIndex(siblingIndex);"));
            Assert.That(source, Does.Contain("GameObject.Destroy(previousButton);"));
        }
    }
}
