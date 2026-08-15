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
        public void EveryRootScreenSpaceCanvasGetsResolutionIndependentScaling()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("InstallResponsiveCanvasGuards(scene);"));
            Assert.That(source, Does.Contain("root.GetComponentsInChildren<Canvas>(true)"));
            Assert.That(source, Does.Contain("canvas.renderMode == RenderMode.WorldSpace || !canvas.isRootCanvas"),
                "Only root screen-space canvases should own the responsive guard; world-space UI must remain untouched.");
            Assert.That(source, Does.Contain("CanvasScaler.ScaleMode.ScaleWithScreenSize"));
            Assert.That(source, Does.Contain("CanvasScaler.ScreenMatchMode.Expand"),
                "Wide/tall devices must preserve the complete authored reference rectangle instead of cropping one axis.");
            Assert.That(source, Does.Contain("scaler = canvas.gameObject.AddComponent<CanvasScaler>();"),
                "A legacy screen-space canvas without a scaler must still become resolution independent.");
            Assert.That(source, Does.Contain("guard.InitializeCanvas(canvas, scaler);"),
                "Responsive behavior must be installed even in scenes that do not contain GameMenus.");
        }

        [Test]
        public void ResponsiveCanvasesTrackRuntimeResolutionAndMacSafeAreaChanges()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("Screen.width != _lastScreenWidth"));
            Assert.That(source, Does.Contain("Screen.height != _lastScreenHeight"));
            Assert.That(source, Does.Contain("Screen.safeArea"),
                "Notched Mac displays and other inset displays must use the live safe area.");
            Assert.That(source, Does.Contain("_responsiveLayoutDirty = true;"));
            Assert.That(source, Does.Contain("ResponsiveLayoutScanInterval"),
                "Late layout rebuilds must be rechecked instead of relying only on sceneLoaded.");
            Assert.That(source, Does.Contain("GetSafeCanvasRect(_responsiveCanvasRect, ResponsiveSafeMargin)"));
            Assert.That(source, Does.Contain("ClampLayoutChildren(_responsiveCanvasRect, safeRect, 0);"));
            Assert.That(source, Does.Contain("RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, layoutRoot)"),
                "Zero-sized legacy UI roots must be evaluated using the bounds of their visible descendants.");
        }

        [Test]
        public void FixedUiIslandsAreClampedWithoutMovingFullScreenContainers()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("IsFullScreenContainer(child)"));
            Assert.That(source, Does.Contain("span.x >= 0.95f && span.y >= 0.95f"),
                "Full-screen/stretch containers should remain full-screen while their fixed children are checked independently.");
            Assert.That(source, Does.Contain("bounds.size.x <= available.width"));
            Assert.That(source, Does.Contain("bounds.size.y <= available.height"));
            Assert.That(source, Does.Contain("layoutRoot.position += worldCorrection;"));
        }

        [Test]
        public void SquadTabsArePinnedToActualRootCanvasTopLeft()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("_menus.Stage.SquadTabs"));
            Assert.That(source, Does.Contain("rootCanvas = canvas != null ? canvas.rootCanvas : null;"),
                "The Squad Tabs parent is a legacy intermediate container, so tab placement must use root-canvas coordinates.");
            Assert.That(source, Does.Contain("Rect safeRect = GetSafeCanvasRect(canvasRect, 0f);"));
            Assert.That(source, Does.Contain("rect.anchorMin = new Vector2(0f, 1f);"));
            Assert.That(source, Does.Contain("rect.anchorMax = new Vector2(0f, 1f);"));
            Assert.That(source, Does.Contain("rect.pivot = new Vector2(0f, 1f);"));
            Assert.That(source, Does.Contain("rect.position = canvasRect.TransformPoint(new Vector3(x, y, 0f));"),
                "Using anchoredPosition alone leaves the tabs relative to the wrong parent on some aspect ratios.");
            Assert.That(source, Does.Contain("_normalizedSquadTabCount = -1;"),
                "Resolution/safe-area changes must force the tab row to be positioned again.");
        }

        [Test]
        public void BottomActionBoxIsPinnedToRootCanvasBottomLeft()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("KeepActionBoxWithinCanvas();"));
            Assert.That(source, Does.Contain("nearestCanvas.rootCanvas"),
                "The ActionBox must use the display root canvas rather than a nested legacy canvas.");
            Assert.That(source, Does.Contain("RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, actionRect)"),
                "The ActionBox has a zero-sized root, so its visible descendant bounds must drive placement.");
            Assert.That(source, Does.Contain("available.xMin - bounds.min.x"));
            Assert.That(source, Does.Contain("available.yMin - bounds.min.y"),
                "The visible ActionBox should remain attached to the bottom-left safe corner at every resolution.");
            Assert.That(source, Does.Contain("actionRect.position += worldCorrection;"));
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
