using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class ResponsiveScreenLayoutGuardTests
    {
        private const string GuardTypeName = "Assets.Scripts.UI_Components.ResponsiveScreenLayoutGuard";
        private static readonly Vector2 ReferenceResolution = new Vector2(1366f, 768f);

        private static string ReadSource()
        {
            return File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "UI Components", "ResponsiveScreenLayoutGuard.cs"));
        }

        [Test]
        public void GuardRunsOnEveryRootScreenSpaceCanvasIncludingLateCreatedCanvases()
        {
            string source = ReadSource();

            Assert.That(source, Does.Contain("root.GetComponentsInChildren<Canvas>(true)"));
            Assert.That(source, Does.Contain("canvas.renderMode == RenderMode.WorldSpace || !canvas.isRootCanvas"));
            Assert.That(source, Does.Contain("Object.FindObjectsByType<Canvas>"),
                "Late-created root canvases must be discovered with a Unity 6 supported object query.");
            Assert.That(source, Does.Contain("FindObjectsInactive.Include"),
                "Inactive late-created canvases must be included so they are normalized before activation.");
            Assert.That(source, Does.Contain("FindObjectsSortMode.None"),
                "Canvas discovery does not require deterministic ordering and should avoid needless sorting.");
            Assert.That(source, Does.Not.Contain("Canvas.allCanvases"),
                "Canvas.allCanvases is not an available API in the project's Unity version.");
            Assert.That(source, Does.Contain("ResponsiveScreenCanvasDiscovery"));
            Assert.That(source, Does.Contain("RootCanvasCompatibilityGuard.EnsureLiveCanvasGuards();"),
                "Late-created root canvases need both wrapper repair and the final ownership-boundary compatibility pass.");
            Assert.That(source, Does.Contain("CanvasScaler.ScaleMode.ScaleWithScreenSize"));
            Assert.That(source, Does.Contain("CanvasScaler.ScreenMatchMode.Expand"));
        }

        [Test]
        public void FullWidthLegacyFrameStretchesBothAxesAndPreservesAuthoredVerticalMargins()
        {
            RectTransform parent = CreateParent();
            RectTransform child = CreateChild(parent, new Vector2(1366f, 668f));

            try
            {
                bool repaired = RepairLegacyScreenRect(child, parent);

                Assert.That(repaired, Is.True);
                AssertVector(child.anchorMin, Vector2.zero);
                AssertVector(child.anchorMax, Vector2.one);
                Assert.That(child.offsetMin.x, Is.EqualTo(0f).Within(0.01f));
                Assert.That(child.offsetMax.x, Is.EqualTo(0f).Within(0.01f));
                Assert.That(child.offsetMin.y, Is.EqualTo(50f).Within(0.01f));
                Assert.That(child.offsetMax.y, Is.EqualTo(-50f).Within(0.01f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent.gameObject);
            }
        }

        [Test]
        public void FullScreenLeafBackerIsResponsiveEvenWithoutChildren()
        {
            RectTransform parent = CreateParent();
            RectTransform child = CreateChild(parent, ReferenceResolution);

            try
            {
                Assert.That(child.childCount, Is.Zero,
                    "This regression specifically covers leaf backers that the old childCount gate skipped.");

                bool repaired = RepairLegacyScreenRect(child, parent);

                Assert.That(repaired, Is.True);
                AssertVector(child.anchorMin, Vector2.zero);
                AssertVector(child.anchorMax, Vector2.one);
                AssertVector(child.offsetMin, Vector2.zero);
                AssertVector(child.offsetMax, Vector2.zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent.gameObject);
            }
        }

        [Test]
        public void FullWidthBarStretchesHorizontallyWithoutChangingItsVerticalAnchorContract()
        {
            RectTransform parent = CreateParent();
            RectTransform child = CreateChild(parent, new Vector2(1366f, 100f));
            child.anchorMin = new Vector2(0.5f, 1f);
            child.anchorMax = new Vector2(0.5f, 1f);
            child.anchoredPosition = new Vector2(0f, -50f);

            try
            {
                bool repaired = RepairLegacyScreenRect(child, parent);

                Assert.That(repaired, Is.True);
                Assert.That(child.anchorMin.x, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(child.anchorMax.x, Is.EqualTo(1f).Within(0.0001f));
                Assert.That(child.anchorMin.y, Is.EqualTo(1f).Within(0.0001f));
                Assert.That(child.anchorMax.y, Is.EqualTo(1f).Within(0.0001f));
                Assert.That(child.rect.height, Is.EqualTo(100f).Within(0.01f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent.gameObject);
            }
        }

        [Test]
        public void OrdinaryCenteredPanelIsNotMistakenForAScreenWrapper()
        {
            RectTransform parent = CreateParent();
            RectTransform child = CreateChild(parent, new Vector2(800f, 600f));

            try
            {
                bool repaired = RepairLegacyScreenRect(child, parent);

                Assert.That(repaired, Is.False);
                AssertVector(child.anchorMin, new Vector2(0.5f, 0.5f));
                AssertVector(child.anchorMax, new Vector2(0.5f, 0.5f));
                AssertVector(child.sizeDelta, new Vector2(800f, 600f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent.gameObject);
            }
        }

        [Test]
        public void LayoutGroupOwnedScreenChildKeepsAuthoredLayoutContract()
        {
            RectTransform parent = CreateParent();
            RectTransform child = CreateChild(parent, new Vector2(1366f, 718f));
            parent.gameObject.AddComponent<VerticalLayoutGroup>();

            try
            {
                bool repaired = RepairLegacyScreenRect(child, parent);

                Assert.That(repaired, Is.False,
                    "LayoutGroup children must remain under Unity layout ownership; reanchoring them can push sibling footers off-screen.");
                AssertVector(child.anchorMin, new Vector2(0.5f, 0.5f));
                AssertVector(child.anchorMax, new Vector2(0.5f, 0.5f));
                AssertVector(child.sizeDelta, new Vector2(1366f, 718f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent.gameObject);
            }
        }

        [Test]
        public void NestedCanvasesAreSkippedOnlyWhenTheyBelongToAnotherRootCanvas()
        {
            string source = ReadSource();

            Assert.That(source, Does.Contain("childCanvas.rootCanvas != _canvas"),
                "A nested Canvas that belongs to the same root must not prevent responsive traversal of its children.");
        }

        [Test]
        public void WrapperGuardDoesNotOwnSemanticHudPlacement()
        {
            string source = ReadSource();

            Assert.That(source, Does.Not.Contain("RepairSquadTabs("),
                "Squad-tab placement belongs to the gameplay HUD guard, not generic wrapper repair.");
            Assert.That(source, Does.Not.Contain("PinActionBoxToSafeBottomLeft("),
                "The generic guard must not fight GameHudLayoutGuard over the selected-squad panel.");
            Assert.That(source, Does.Not.Contain("GameMenus"),
                "Generic resolution repair should be scene-agnostic and unaware of gameplay UI semantics.");
        }

        [Test]
        public void WrapperGuardDoesNotTranslateOrdinaryAuthoredIslands()
        {
            string source = ReadSource();

            Assert.That(source, Does.Not.Contain("ClampVisibleHierarchyToRect("),
                "Arbitrary panel translation broke authored relationships such as Squad Maker level text above START/TEST.");
            Assert.That(source, Does.Not.Contain("Screen.safeArea"),
                "Screen-wrapper sizing must not introduce an unrelated safe-area translation into desktop layouts.");
            Assert.That(source, Does.Contain("if (IsFullScreenContainer(child))"));
            Assert.That(source, Does.Contain("RepairHierarchy(child, referenceResolution, depth + 1);"),
                "Traversal should continue only through containers that represent the viewport.");
        }

        [Test]
        public void RuntimeResolutionChangesTriggerAnotherWrapperRepair()
        {
            string source = ReadSource();

            Assert.That(source, Does.Contain("Screen.width != _lastScreenWidth"));
            Assert.That(source, Does.Contain("Screen.height != _lastScreenHeight"));
            Assert.That(source, Does.Contain("displayChanged || Time.unscaledTime >= _nextRepairTime"));
            Assert.That(source, Does.Contain("RepairLayout();"));
        }

        private static RectTransform CreateParent()
        {
            GameObject parentObject = new GameObject("Responsive Test Parent", typeof(RectTransform));
            RectTransform parent = parentObject.GetComponent<RectTransform>();
            parent.pivot = new Vector2(0.5f, 0.5f);
            parent.anchorMin = new Vector2(0.5f, 0.5f);
            parent.anchorMax = new Vector2(0.5f, 0.5f);
            parent.sizeDelta = new Vector2(1600f, 900f);
            return parent;
        }

        private static RectTransform CreateChild(RectTransform parent, Vector2 size)
        {
            GameObject childObject = new GameObject("Responsive Test Child", typeof(RectTransform));
            RectTransform child = childObject.GetComponent<RectTransform>();
            child.SetParent(parent, false);
            child.anchorMin = new Vector2(0.5f, 0.5f);
            child.anchorMax = new Vector2(0.5f, 0.5f);
            child.pivot = new Vector2(0.5f, 0.5f);
            child.anchoredPosition = Vector2.zero;
            child.sizeDelta = size;
            child.localScale = Vector3.one;
            return child;
        }

        private static bool RepairLegacyScreenRect(RectTransform rect, RectTransform parent)
        {
            Type guardType = RuntimeAssembly.GetType(GuardTypeName);
            return (bool)RuntimeAssembly.InvokeStatic(
                guardType,
                "RepairLegacyScreenRect",
                rect,
                parent,
                ReferenceResolution);
        }

        private static void AssertVector(Vector2 actual, Vector2 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.01f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.01f));
        }
    }
}
