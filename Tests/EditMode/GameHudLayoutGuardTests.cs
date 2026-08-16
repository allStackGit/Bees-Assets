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
        private const string GuardTypeName = "Assets.Scripts.UI_Components.GameHudLayoutGuard";

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
        public void SquadTabsUseLiveScoreboardGeometryInsteadOfScreenCornerGuessing()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("_menus.Stage.SquadTabs"));
            Assert.That(source, Does.Contain("ResolveScoreboardRect(_menus.Scoreboard, scoreboardSearchRoot)"),
                "The gameplay guard must not trust a stale/wrong serialized scoreboard reference when resolving the visible HUD.");
            Assert.That(source, Does.Contain("FindObjectsByType<RectTransform>"),
                "The scoreboard can live under a different root UI branch from GameMenus, so fallback discovery must cover the active scene.");
            Assert.That(source, Does.Contain("_scoreboardRect"));
            Assert.That(source, Does.Contain("StretchToRootCanvas(tabsRoot, canvasRect)"),
                "The legacy 1366x768 Squad Tabs container must become the live root-canvas rectangle before its layout is calculated.");
            Assert.That(source, Does.Contain("GetSquadTabLeftPadding("));
            Assert.That(source, Does.Contain("scoreboard.TransformPoint("));
            Assert.That(source, Does.Contain("tabsRoot.InverseTransformPoint(scoreboardRightWorld).x"),
                "Sibling scoreboard and Squad Tabs geometry must be converted through world space before calculating the row start.");
            Assert.That(source, Does.Contain("layout.padding = new RectOffset(leftPadding, 0, topPadding, 0);"));
            Assert.That(source, Does.Contain("layout.childAlignment = TextAnchor.UpperLeft;"));
            Assert.That(source, Does.Not.Contain("layout.padding = new RectOffset(0, 0, 0, 0);"),
                "Blindly zeroing the scoreboard reservation would put the squad row over the scoreboard.");
        }

        [Test]
        public void WrongAssignedScoreboardFallsBackAcrossGameplayUiRoots()
        {
            RectTransform menusRoot = CreateRect("UI Manager", null, new Vector2(1600f, 900f));
            RectTransform wrongReference = CreateRect("Summary", menusRoot, new Vector2(500f, 400f));
            RectTransform overlayRoot = CreateRect("UI Overlay", null, new Vector2(1600f, 900f));
            RectTransform liveScoreboard = CreateRect("Scoreboard", overlayRoot, new Vector2(200f, 60f));

            try
            {
                RectTransform resolved = (RectTransform)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "ResolveScoreboardRect",
                    wrongReference.gameObject,
                    menusRoot);

                Assert.That(resolved, Is.SameAs(liveScoreboard));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(menusRoot.gameObject);
                UnityEngine.Object.DestroyImmediate(overlayRoot.gameObject);
            }
        }

        [Test]
        public void VisibleSiblingScoreboardDeterminesSquadTabLeftPadding()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1600f, 900f));
            RectTransform tabsRoot = CreateRect("Squad Tabs", canvas, new Vector2(1600f, 900f));
            RectTransform scoreboard = CreateRect("Scoreboard", canvas, new Vector2(200f, 60f));
            scoreboard.anchorMin = new Vector2(0f, 1f);
            scoreboard.anchorMax = new Vector2(0f, 1f);
            scoreboard.anchoredPosition = new Vector2(100f, -30f);

            try
            {
                int padding = (int)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "GetSquadTabLeftPadding",
                    tabsRoot,
                    scoreboard,
                    8f,
                    0f);

                Assert.That(padding, Is.EqualTo(208),
                    "A 200 px scoreboard at the left edge plus an 8 px gap should put the first squad tab at x=208 from the live canvas left.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void HiddenScoreboardUsesFlushTopLeftSquadTabStart()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1600f, 900f));
            RectTransform tabsRoot = CreateRect("Squad Tabs", canvas, new Vector2(1600f, 900f));
            RectTransform scoreboard = CreateRect("Scoreboard", canvas, new Vector2(200f, 60f));
            scoreboard.gameObject.SetActive(false);

            try
            {
                int padding = (int)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "GetSquadTabLeftPadding",
                    tabsRoot,
                    scoreboard,
                    8f,
                    0f);

                Assert.That(padding, Is.EqualTo(0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void GameplayCornerControlCanBePinnedFlushToCanvasBoundary()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1600f, 900f));
            RectTransform scoreboard = CreateRect("Scoreboard", canvas, new Vector2(200f, 60f));
            scoreboard.anchorMin = new Vector2(0f, 1f);
            scoreboard.anchorMax = new Vector2(0f, 1f);
            scoreboard.anchoredPosition = new Vector2(115f, -45f);

            try
            {
                RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "PinLayoutRootToCorner",
                    scoreboard,
                    canvas,
                    false,
                    true,
                    0f);

                Bounds after = RectTransformUtility.CalculateRelativeRectTransformBounds(canvas, scoreboard);
                Assert.That(after.min.x, Is.EqualTo(canvas.rect.xMin).Within(0.01f));
                Assert.That(after.max.y, Is.EqualTo(canvas.rect.yMax).Within(0.01f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void GameplayHudUsesCanvasEdgesWithoutInset()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("private const float HudEdgeMargin = 0f;"));
            Assert.That(source, Does.Contain("private const float BottomHudMargin = HudEdgeMargin;"));
            Assert.That(source, Does.Contain("KeepScoreboardWithinCanvas();"));
            Assert.That(source, Does.Contain("KeepNormalSpeedButtonWithinCanvas();"));
            Assert.That(source, Does.Contain("KeepActionBoxWithinCanvas();"));
            Assert.That(source, Does.Contain("KeepMiniMapWithinCanvas();"));
            Assert.That(source, Does.Contain("PinLayoutRootToCorner("));
            Assert.That(source, Does.Contain("_menus.SquadActionBoxUI"));
            Assert.That(source, Does.Contain("_menus.MiniMapOutput"));
            Assert.That(source, Does.Contain("_menus.MiniMapCover"));
            Assert.That(source, Does.Contain("available.xMin + margin - bounds.min.x"),
                "The Squad Action Box should be pinned to the bottom-left canvas edge.");
            Assert.That(source, Does.Contain("available.xMax - margin - bounds.max.x"),
                "The Mini Map and right-edge controls should be pinned to the right canvas edge.");
            Assert.That(source, Does.Contain("available.yMin + margin - bounds.min.y"),
                "Bottom HUD islands should be pinned flush to the bottom canvas edge.");
            Assert.That(source, Does.Not.Contain("private const float HudEdgeMargin = 10f;"));
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
            Assert.That(source, Does.Contain("else if (_speedRect.anchoredPosition != _normalSpeedPosition)"),
                "When a mission clock disappears, Game Speed must return to its ordinary authored location even if mission setup moved it first.");
            Assert.That(source, Does.Contain("_speedRect.anchoredPosition = _normalSpeedPosition;"));
        }

        [Test]
        public void PlutoShieldStateUsesLiveShieldAndCounterGeometry()
        {
            string source = ReadGuardSource();

            Assert.That(source, Does.Contain("_menus.PlutoShield.activeInHierarchy"));
            Assert.That(source, Does.Contain("GetRightAlignedX("));
            Assert.That(source, Does.Contain("_plutoShieldRect.anchoredPosition.x"),
                "Pluto IV Game Speed should follow the shared shield frame instead of a fixed screen inset.");
            Assert.That(source, Does.Contain("_menus.Counter.activeInHierarchy"));
            Assert.That(source, Does.Contain("GetTopAlignedY("));
            Assert.That(source, Does.Contain("_counterRect.anchoredPosition.y"),
                "Pluto IV Game Speed should occupy the row beside the Evacuated counter.");
            Assert.That(source, Does.Contain("_plutoShieldRect.anchoredPosition.y"),
                "The no-counter fallback should remain tied to the shield geometry.");
            Assert.That(source, Does.Not.Contain("PlutoSpeedRightInset"),
                "A hard-coded Pluto screen inset can strand Game Speed far from the timed shield HUD after responsive scaling.");
        }

        [Test]
        public void TimedShieldAlignmentMathPreservesReferenceEdgesAndGap()
        {
            Type guardType = RuntimeAssembly.GetType(GuardTypeName);

            float rightAlignedX = (float)RuntimeAssembly.InvokeStatic(
                guardType, "GetRightAlignedX", 100f, 80f, 20f);
            Assert.That(rightAlignedX, Is.EqualTo(130f).Within(0.001f));
            Assert.That(rightAlignedX + 10f, Is.EqualTo(100f + 40f).Within(0.001f),
                "The target and reference right edges should coincide.");

            float topAlignedY = (float)RuntimeAssembly.InvokeStatic(
                guardType, "GetTopAlignedY", 50f, 40f, 10f);
            Assert.That(topAlignedY, Is.EqualTo(65f).Within(0.001f));
            Assert.That(topAlignedY + 5f, Is.EqualTo(50f + 20f).Within(0.001f),
                "The target and reference top edges should coincide.");

            float belowY = (float)RuntimeAssembly.InvokeStatic(
                guardType, "GetBelowY", 50f, 40f, 10f, 5f);
            Assert.That(belowY, Is.EqualTo(20f).Within(0.001f));
            Assert.That((50f - 20f) - (belowY + 5f), Is.EqualTo(5f).Within(0.001f),
                "Titania II should preserve the requested vertical gap below its clock.");
        }

        [Test]
        public void PlutoShieldHealthFillUsesClampedSharedScaleConvention()
        {
            string source = ReadPluto4Source();

            Assert.That(source, Does.Contain("Mathf.Clamp01((float)(15 - personnelLost) / 15f)"),
                "Pluto shield health must be clamped before it is applied to the shared health-bar UI.");
            Assert.That(source, Does.Contain("new Vector2(shieldHealth * 150f, 1f)"),
                "Pluto IV and Titania II share the established 0..150 shield-bar scale convention.");
            Assert.That(source, Does.Not.Contain("new Vector2(((float)(15 - personnelLost) / 15) * 150, 1)"),
                "Do not restore the unbounded inline scale expression.");
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

        private static RectTransform CreateRect(string name, RectTransform parent, Vector2 size)
        {
            GameObject gameObject = new GameObject(name, typeof(RectTransform));
            RectTransform rect = gameObject.GetComponent<RectTransform>();
            if (parent != null)
            {
                rect.SetParent(parent, false);
            }
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
            rect.localScale = Vector3.one;
            return rect;
        }
    }
}
