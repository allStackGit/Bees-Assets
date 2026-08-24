using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

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
        public void MainMenuPresentationWidthUsesControlsAndIgnoresDecorativeFullWidthGraphics()
        {
            RectTransform panel = CreateRect("MainPanel", null, new Vector2(1366f, 668f));
            RectTransform primary = CreateRect("Campaign", panel, new Vector2(300f, 50f));
            primary.anchoredPosition = new Vector2(-60f, 0f);
            primary.gameObject.AddComponent<Button>();

            RectTransform reset = CreateRect("Reset Progress", panel, new Vector2(100f, 50f));
            reset.anchoredPosition = new Vector2(160f, 0f);
            reset.gameObject.AddComponent<Button>();

            RectTransform decoration = CreateRect("Decorative Full Width Image", panel, new Vector2(1366f, 668f));
            decoration.gameObject.AddComponent<Image>();

            try
            {
                Vector2 presentationSize = (Vector2)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "CalculateMainMenuPresentationSizeForTest",
                    panel);

                // The controls extend to +/-210 around the panel center. The responsive contract
                // then adds 240 authored units of breathing room, producing a 660-wide fitting box.
                Assert.That(presentationSize.x, Is.EqualTo(660f).Within(0.01f));
                Assert.That(presentationSize.y, Is.EqualTo(668f).Within(0.01f));
                Assert.That(presentationSize.x, Is.LessThan(1366f),
                    "A decorative/full-width Graphic must not make portrait screens fit the empty 1366-wide authoring frame again.");
            }
            finally
            {
                Object.DestroyImmediate(panel.gameObject);
            }
        }

        [Test]
        public void PortraitCanvasExpandsMainMenuUntilFunctionalPresentationFitsWidth()
        {
            Vector2 portraitLogicalCanvas = new Vector2(1366f, 3686f);
            Vector2 presentationSize = new Vector2(660f, 668f);

            float scale = CalculateMainMenuScale(portraitLogicalCanvas, presentationSize);
            float expectedWidthLimitedScale = (1366f - 48f) / 660f;

            Assert.That(scale, Is.EqualTo(expectedWidthLimitedScale).Within(0.001f));
            Assert.That(scale, Is.GreaterThan(1f),
                "A tall/narrow Expand canvas has abundant vertical room. The menu must grow inside the virtual canvas instead of inheriting the tiny width-driven CanvasScaler scale.");
        }

        [Test]
        public void FourByThreeCanvasUsesExtraHeightWithoutChangingMenuComposition()
        {
            Vector2 fourByThreeLogicalCanvas = new Vector2(1366f, 1024f);
            Vector2 presentationSize = new Vector2(660f, 668f);

            float scale = CalculateMainMenuScale(fourByThreeLogicalCanvas, presentationSize);

            Assert.That(scale, Is.EqualTo(1024f / 768f).Within(0.001f),
                "At moderate tall aspect ratios the presentation should continue tracking viewport height until its functional content actually reaches an edge.");
        }

        [Test]
        public void UltrawideCanvasDoesNotEnlargeMainMenuBeyondReferenceHeight()
        {
            Vector2 ultrawideLogicalCanvas = new Vector2(1908f, 768f);
            Vector2 presentationSize = new Vector2(660f, 668f);

            float scale = CalculateMainMenuScale(ultrawideLogicalCanvas, presentationSize);

            Assert.That(scale, Is.EqualTo(1f).Within(0.001f),
                "Horizontal surplus belongs to the starfield. Matching the reference height must keep the authored Main Menu presentation scale.");
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

        [TestCase(1908f, 768f, 1908f / 768f)]
        [TestCase(1366f, 3686f, 3686f / 1366f)]
        public void PreserveAspectBackdropCoverScaleEnvelopesExtremeViewport(
            float viewportWidth,
            float viewportHeight,
            float expectedScale)
        {
            float scale = (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateBackdropCoverScale",
                new Vector2(viewportWidth, viewportHeight),
                new Vector2(1024f, 1024f));

            Assert.That(scale, Is.EqualTo(expectedScale).Within(0.001f));
        }

        [Test]
        public void ViewportBackdropRenderedGraphicCoversUltrawideCanvas()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1908f, 768f));
            RectTransform backdrop = CreateRect("PanelBacker", canvas, new Vector2(1366f, 768f));
            backdrop.localScale = new Vector3(2f, 2f, 2f);
            Texture2D texture = null;
            Sprite sprite = null;

            try
            {
                texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                sprite = Sprite.Create(texture, new Rect(0f, 0f, 64f, 64f), new Vector2(0.5f, 0.5f));
                Image image = backdrop.gameObject.AddComponent<Image>();
                image.sprite = sprite;
                image.preserveAspect = true;

                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "StretchBackdrop",
                    backdrop);

                float expectedCoverScale = 1908f / 768f;
                Assert.That(changed, Is.True);
                AssertVector(backdrop.anchorMin, Vector2.zero);
                AssertVector(backdrop.anchorMax, Vector2.one);
                AssertVector(backdrop.offsetMin, Vector2.zero);
                AssertVector(backdrop.offsetMax, Vector2.zero);
                Assert.That(backdrop.localScale.x, Is.EqualTo(expectedCoverScale).Within(0.001f));
                Assert.That(backdrop.localScale.y, Is.EqualTo(expectedCoverScale).Within(0.001f));

                // A square preserve-aspect Image inside a 1908x768 rect first renders at 768x768.
                // The backdrop transform must then enlarge that rendered square to at least 1908
                // pixels in both axes, otherwise the old black side bars return.
                float containedGraphicSize = Mathf.Min(canvas.rect.width, canvas.rect.height);
                Assert.That(containedGraphicSize * backdrop.localScale.x,
                    Is.GreaterThanOrEqualTo(canvas.rect.width - 0.01f));
                Assert.That(containedGraphicSize * backdrop.localScale.y,
                    Is.GreaterThanOrEqualTo(canvas.rect.height - 0.01f));
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
                if (sprite != null)
                {
                    Object.DestroyImmediate(sprite);
                }
                if (texture != null)
                {
                    Object.DestroyImmediate(texture);
                }
            }
        }

        [Test]
        public void NonPreserveBackdropUsesExactViewportWithoutOverscan()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1908f, 768f));
            RectTransform backdrop = CreateRect("Background", canvas, new Vector2(1366f, 768f));
            backdrop.localScale = new Vector3(2f, 2f, 2f);
            Image image = backdrop.gameObject.AddComponent<Image>();
            image.preserveAspect = false;

            try
            {
                RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "StretchBackdrop",
                    backdrop);

                AssertVector(backdrop.anchorMin, Vector2.zero);
                AssertVector(backdrop.anchorMax, Vector2.one);
                AssertVector(backdrop.offsetMin, Vector2.zero);
                AssertVector(backdrop.offsetMax, Vector2.zero);
                Assert.That(backdrop.localScale.x, Is.EqualTo(1f).Within(0.001f));
                Assert.That(backdrop.localScale.y, Is.EqualTo(1f).Within(0.001f));
                Assert.That(backdrop.rect.width, Is.EqualTo(1908f).Within(0.01f));
                Assert.That(backdrop.rect.height, Is.EqualTo(768f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        private static float CalculateMainMenuScale(Vector2 canvasSize, Vector2 presentationSize)
        {
            return (float)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "CalculateMainMenuPresentationScale",
                canvasSize,
                presentationSize);
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
