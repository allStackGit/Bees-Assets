using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ResponsiveScreenLayoutGuardTests
    {
        private static string ReadSource()
        {
            return File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "ResponsiveScreenLayoutGuard.cs"));
        }

        [Test]
        public void GuardRunsOnEveryRootScreenSpaceCanvas()
        {
            string source = ReadSource();

            Assert.That(source, Does.Contain("root.GetComponentsInChildren<Canvas>(true)"));
            Assert.That(source, Does.Contain("canvas.renderMode == RenderMode.WorldSpace || !canvas.isRootCanvas"));
            Assert.That(source, Does.Contain("canvas.gameObject.AddComponent<ResponsiveScreenLayoutGuard>()"));
            Assert.That(source, Does.Contain("CanvasScaler.ScaleMode.ScaleWithScreenSize"));
            Assert.That(source, Does.Contain("CanvasScaler.ScreenMatchMode.Expand"));
        }

        [Test]
        public void ReferenceSizedLegacyScreenWrappersAreConvertedToStretchContainers()
        {
            string source = ReadSource();

            Assert.That(source, Does.Contain("IsLegacyReferenceContainer(child, parent)"));
            Assert.That(source, Does.Contain("referenceResolution.x * ReferenceSizeToleranceFraction"));
            Assert.That(source, Does.Contain("referenceResolution.y * ReferenceSizeToleranceFraction"));
            Assert.That(source, Does.Contain("parent == _canvasRect || IsFullScreenContainer(parent)"));
            Assert.That(source, Does.Contain("StretchToParent(child);"));
            Assert.That(source, Does.Contain("rect.anchorMin = Vector2.zero;"));
            Assert.That(source, Does.Contain("rect.anchorMax = Vector2.one;"));
            Assert.That(source, Does.Contain("rect.offsetMin = Vector2.zero;"));
            Assert.That(source, Does.Contain("rect.offsetMax = Vector2.zero;"));
            Assert.That(source, Does.Contain("LayoutRebuilder.ForceRebuildLayoutImmediate(child);"),
                "A wrapper converted from fixed reference size must rebuild layout in its new display-sized coordinate system.");
        }

        [Test]
        public void NestedCanvasesAreSkippedOnlyWhenTheyBelongToAnotherRootCanvas()
        {
            string source = ReadSource();

            Assert.That(source, Does.Contain("childCanvas.rootCanvas != _canvas"),
                "A nested Canvas that belongs to the same root must not prevent responsive traversal of its children.");
        }

        [Test]
        public void SquadTabsRepairTheParentLayoutGroupInsteadOfFightingItPerChild()
        {
            string source = ReadSource();

            Assert.That(source, Does.Contain("RectTransform tabsRoot = firstTabRect != null ? firstTabRect.parent as RectTransform : null;"));
            Assert.That(source, Does.Contain("StretchToParent(tabsRoot);"),
                "The legacy 1366x768 Squad Tabs wrapper must become the actual display-sized container.");
            Assert.That(source, Does.Contain("HorizontalLayoutGroup layout = tabsRoot.GetComponent<HorizontalLayoutGroup>();"));
            Assert.That(source, Does.Contain("layout.padding = new RectOffset(leftPadding, 0, topPadding, 0);"),
                "The authored 200 px left padding must be replaced with safe-area-aware top-left padding.");
            Assert.That(source, Does.Contain("layout.childAlignment = TextAnchor.UpperLeft;"));
            Assert.That(source, Does.Contain("layout.childForceExpandWidth = false;"));
            Assert.That(source, Does.Contain("layout.childForceExpandHeight = false;"));
            Assert.That(source, Does.Contain("LayoutRebuilder.ForceRebuildLayoutImmediate(tabsRoot);"),
                "The layout group, rather than manual child positions, must own final tab placement.");
        }

        [Test]
        public void RuntimeResolutionAndSafeAreaChangesTriggerAnotherRepair()
        {
            string source = ReadSource();

            Assert.That(source, Does.Contain("Screen.width != _lastScreenWidth"));
            Assert.That(source, Does.Contain("Screen.height != _lastScreenHeight"));
            Assert.That(source, Does.Contain("Screen.safeArea"));
            Assert.That(source, Does.Contain("displayChanged || Time.unscaledTime >= _nextRepairTime"));
            Assert.That(source, Does.Contain("RepairLayout();"));
        }

        [Test]
        public void SelectedSquadActionBoxIsPinnedAfterLayoutToSafeBottomLeft()
        {
            string source = ReadSource();

            Assert.That(source, Does.Contain("private void LateUpdate()"));
            Assert.That(source, Does.Contain("PinActionBoxToSafeBottomLeft();"));
            Assert.That(source, Does.Contain("RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, actionRect)"));
            Assert.That(source, Does.Contain("available.xMin - bounds.min.x"));
            Assert.That(source, Does.Contain("available.yMin - bounds.min.y"));
            Assert.That(source, Does.Contain("actionRect.position += worldCorrection;"));
        }
    }
}
