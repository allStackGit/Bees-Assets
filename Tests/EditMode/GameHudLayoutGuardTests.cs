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

        private static string ReadPluto4Source()
        {
            return File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Levels", "Level.Campaign.Pluto4.cs"));
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
        public void GenericCanvasPassDoesNotTranslateAuthoredUiIslands()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("Screen.width != _lastScreenWidth"));
            Assert.That(source, Does.Contain("Screen.height != _lastScreenHeight"));
            Assert.That(source, Does.Contain("ResponsiveLayoutScanInterval"));
            Assert.That(source, Does.Not.Contain("ClampLayoutChildren("),
                "The canvas guard must not move arbitrary panels and break authored sibling relationships.");
            Assert.That(source, Does.Not.Contain("GetSafeCanvasRect("),
                "Desktop HUD edge placement is against the actual root canvas, not a safe-area inset that pushes controls inward.");
            Assert.That(source, Does.Not.Contain("Screen.safeArea"),
                "The screenshot-defined desktop HUD uses the real display edges.");
        }

        [Test]
        public void SquadTabsLayoutGroupOwnsActualScreenTopLeft()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("_menus.Stage.SquadTabs"));
            Assert.That(source, Does.Contain("RectTransform tabsRoot = firstTabRect != null ? firstTabRect.parent as RectTransform : null;"));
            Assert.That(source, Does.Contain("HorizontalLayoutGroup layout = tabsRoot.GetComponent<HorizontalLayoutGroup>();"));
            Assert.That(source, Does.Contain("layout.padding = new RectOffset(0, 0, 0, 0);"),
                "The squad-number row should use the actual screen top-left without inherited 200 px or safe-area padding.");
            Assert.That(source, Does.Contain("layout.childAlignment = TextAnchor.UpperLeft;"));
            Assert.That(source, Does.Contain("layout.spacing = SquadTabGap;"));
            Assert.That(source, Does.Contain("layout.childForceExpandWidth = false;"));
            Assert.That(source, Does.Contain("layout.childForceExpandHeight = false;"));
            Assert.That(source, Does.Contain("LayoutRebuilder.ForceRebuildLayoutImmediate(tabsRoot);"),
                "A later Unity layout pass must not push the tabs down again.");
            Assert.That(source, Does.Contain("_normalizedSquadTabCount = -1;"),
                "Resolution changes must force the tab row layout to be normalized again.");
        }

        [Test]
        public void BottomHudIslandsArePinnedToActualCanvasEdges()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("KeepActionBoxWithinCanvas();"));
            Assert.That(source, Does.Contain("KeepMiniMapWithinCanvas();"));
            Assert.That(source, Does.Contain("_menus.SquadActionBoxUI"));
            Assert.That(source, Does.Contain("_menus.MiniMapOutput"));
            Assert.That(source, Does.Contain("_menus.MiniMapCover"));
            Assert.That(source, Does.Contain("RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, layoutRoot)"),
                "Zero-sized legacy roots must be positioned from the bounds of their visible descendants.");
            Assert.That(source, Does.Contain("available.xMin + margin - bounds.min.x"),
                "The Squad Action Box should remain fully visible at bottom-left.");
            Assert.That(source, Does.Contain("available.xMax - margin - bounds.max.x"),
                "The Mini Map should remain fully visible at bottom-right.");
            Assert.That(source, Does.Contain("available.yMin + margin - bounds.min.y"),
                "Bottom HUD islands must not be clipped below the canvas.");
        }

        [Test]
        public void VisibleMissionClockMovesGameSpeedButtonBesideIt()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("_menus.Clock.activeInHierarchy"));
            Assert.That(source, Does.Contain("_clockRect.rect.width + _speedRect.rect.width"));
            Assert.That(source, Does.Contain("ControlGap"));
            Assert.That(source, Does.Contain("Vector2 desiredPosition = new Vector2(x, y);"));
            Assert.That(source, Does.Contain("_speedRect.anchoredPosition = desiredPosition;"));
            Assert.That(source, Does.Contain("_speedRect.anchoredPosition = _normalSpeedPosition;"));
        }

        [Test]
        public void PlutoShieldStatePreservesMissionSpecificSpeedButtonInset()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("private const float PlutoSpeedRightInset = 290f;"));
            Assert.That(source, Does.Contain("_menus.PlutoShield.activeInHierarchy"));
            Assert.That(source, Does.Contain("x = -PlutoSpeedRightInset;"),
                "Pluto IV deliberately keeps Game Speed away from the planetary-shield rectangle.");
            Assert.That(source, Does.Contain("_menus.Counter.activeInHierarchy"));
            Assert.That(source, Does.Contain("_counterRect.anchoredPosition.y"));
            Assert.That(source, Does.Contain("_plutoShieldRect.anchoredPosition.y"));
        }

        [Test]
        public void PlutoShieldHealthFillUsesNormalizedScale()
        {
            string source = ReadPluto4Source();

            Assert.That(source, Does.Contain("Mathf.Clamp01((float)(15 - personnelLost) / 15f)"));
            Assert.That(source, Does.Contain("new Vector2(shieldHealth, 1f)"));
            Assert.That(source, Does.Not.Contain("((float)(15 - personnelLost) / 15) * 150"),
                "The 150-pixel health-bar root must not be scaled up by another factor of 150.");
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
