using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class LegacyScreenResponsiveLayoutGuardTests
    {
        private const string GuardTypeName = "Assets.Scripts.UI_Components.LegacyScreenResponsiveLayoutGuard";

        [TestCase("Main Menu", true)]
        [TestCase("Squad Maker", true)]
        [TestCase("Space", false)]
        public void FixedReferencePolicyIsLimitedToLegacyPresentationScenes(string sceneName, bool expected)
        {
            bool actual = (bool)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "IsFixedReferencePresentationScene",
                sceneName);

            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void UltrawideCanvasKeepsFullReferenceRootAt1366By768()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1908f, 768f));
            RectTransform root = CreateRect("Main Container", canvas, Vector2.zero);
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.sizeDelta = Vector2.zero;

            try
            {
                bool changed = ApplyReferenceGeometry(
                    root,
                    canvas.rect.size,
                    Vector2.zero,
                    Vector2.one,
                    Vector2.zero,
                    Vector2.zero);

                Assert.That(changed, Is.True);
                Assert.That(root.rect.width, Is.EqualTo(1366f).Within(0.01f));
                Assert.That(root.rect.height, Is.EqualTo(768f).Within(0.01f));
                Assert.That(root.rect.center.x, Is.EqualTo(0f).Within(0.01f));
                Assert.That(root.rect.center.y, Is.EqualTo(0f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void TallCanvasKeepsFullReferenceRootAt1366By768()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1366f, 1794f));
            RectTransform root = CreateRect("Main Container", canvas, Vector2.zero);
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.sizeDelta = Vector2.zero;

            try
            {
                ApplyReferenceGeometry(
                    root,
                    canvas.rect.size,
                    Vector2.zero,
                    Vector2.one,
                    Vector2.zero,
                    Vector2.zero);

                Assert.That(root.rect.width, Is.EqualTo(1366f).Within(0.01f));
                Assert.That(root.rect.height, Is.EqualTo(768f).Within(0.01f));
                Assert.That(root.rect.center.x, Is.EqualTo(0f).Within(0.01f));
                Assert.That(root.rect.center.y, Is.EqualTo(0f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void MainMenuPanelKeepsAuthoredSizeInsteadOfReceivingSecondScale()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1908f, 768f));
            RectTransform panel = CreateRect("MainPanel", canvas, new Vector2(1366f, 668f));

            try
            {
                bool changed = ApplyReferenceGeometry(
                    panel,
                    canvas.rect.size,
                    new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f),
                    Vector2.zero,
                    new Vector2(1366f, 668f));

                Assert.That(changed, Is.False,
                    "CanvasScaler.Expand already supplies uniform physical scaling; the menu root must not be scaled a second time.");
                AssertVector(panel.anchorMin, new Vector2(0.5f, 0.5f));
                AssertVector(panel.anchorMax, new Vector2(0.5f, 0.5f));
                AssertVector(panel.sizeDelta, new Vector2(1366f, 668f));
                Assert.That(panel.localScale, Is.EqualTo(Vector3.one));
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void EdgeAnchoredFooterTracksReferenceFrameRatherThanLiveCanvasEdge()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1908f, 768f));
            RectTransform backButton = CreateRect("Back", canvas, new Vector2(200f, 50f));
            Vector2 authoredAnchor = Vector2.zero;
            Vector2 authoredPosition = new Vector2(110f, 30f);
            backButton.anchorMin = authoredAnchor;
            backButton.anchorMax = authoredAnchor;
            backButton.anchoredPosition = authoredPosition;

            try
            {
                ApplyReferenceGeometry(
                    backButton,
                    canvas.rect.size,
                    authoredAnchor,
                    authoredAnchor,
                    authoredPosition,
                    new Vector2(200f, 50f));

                float expectedReferenceLeft = -(1366f * 0.5f);
                float actualLeft = backButton.anchoredPosition.x +
                                   backButton.anchorMin.x * canvas.rect.width -
                                   canvas.rect.width * 0.5f;
                Assert.That(actualLeft, Is.EqualTo(expectedReferenceLeft + authoredPosition.x).Within(0.01f),
                    "Footer/navigation controls must remain attached to the legacy artboard, not drift to an ultrawide physical edge.");
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void RepeatedAspectChangesRestoreCanonicalGeometryWithoutAccumulatingDrift()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1908f, 768f));
            RectTransform root = CreateRect("Main Container", canvas, Vector2.zero);
            Vector2 authoredMin = Vector2.zero;
            Vector2 authoredMax = Vector2.one;

            try
            {
                ApplyReferenceGeometry(root, canvas.rect.size, authoredMin, authoredMax, Vector2.zero, Vector2.zero);
                Assert.That(root.rect.width, Is.EqualTo(1366f).Within(0.01f));

                canvas.sizeDelta = new Vector2(1366f, 1794f);
                root.anchorMin = new Vector2(0.2f, 0.2f);
                root.anchorMax = new Vector2(0.8f, 0.8f);
                root.sizeDelta = new Vector2(500f, 500f);
                ApplyReferenceGeometry(root, canvas.rect.size, authoredMin, authoredMax, Vector2.zero, Vector2.zero);
                Assert.That(root.rect.width, Is.EqualTo(1366f).Within(0.01f));
                Assert.That(root.rect.height, Is.EqualTo(768f).Within(0.01f));

                canvas.sizeDelta = new Vector2(1908f, 768f);
                root.sizeDelta = new Vector2(250f, 250f);
                ApplyReferenceGeometry(root, canvas.rect.size, authoredMin, authoredMax, Vector2.zero, Vector2.zero);
                Assert.That(root.rect.width, Is.EqualTo(1366f).Within(0.01f));
                Assert.That(root.rect.height, Is.EqualTo(768f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void ViewportBackdropStillFillsAspectRatioSurplus()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1908f, 768f));
            RectTransform backdrop = CreateRect("Background", canvas, new Vector2(1366f, 768f));

            try
            {
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "StretchBackdrop",
                    backdrop);

                Assert.That(changed, Is.True);
                AssertVector(backdrop.anchorMin, Vector2.zero);
                AssertVector(backdrop.anchorMax, Vector2.one);
                AssertVector(backdrop.offsetMin, Vector2.zero);
                AssertVector(backdrop.offsetMax, Vector2.zero);
                Assert.That(backdrop.rect.width, Is.EqualTo(1908f).Within(0.01f));
                Assert.That(backdrop.rect.height, Is.EqualTo(768f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        private static bool ApplyReferenceGeometry(
            RectTransform rect,
            Vector2 canvasSize,
            Vector2 authoredMin,
            Vector2 authoredMax,
            Vector2 authoredPosition,
            Vector2 authoredSize)
        {
            return (bool)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "ApplyReferenceGeometryForTest",
                rect,
                canvasSize,
                authoredMin,
                authoredMax,
                authoredPosition,
                authoredSize);
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

        private static void AssertVector(Vector2 actual, Vector2 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.01f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.01f));
        }
    }
}
