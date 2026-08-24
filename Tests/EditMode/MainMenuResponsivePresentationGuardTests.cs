using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class MainMenuResponsivePresentationGuardTests
    {
        private const string GuardTypeName =
            "Assets.Scripts.UI_Components.MainMenuResponsivePresentationGuard";

        [Test]
        public void ReferenceCanvasPreservesAuthoredPanelAndLogoRelationship()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1366f, 768f));
            RectTransform panel = CreateRect("MainPanel", canvas, new Vector2(1366f, 668f));
            RectTransform logo = CreateRect("Bees Logo", canvas, new Vector2(197f, 52f));
            logo.anchorMin = new Vector2(0.5f, 1f);
            logo.anchorMax = new Vector2(0.5f, 1f);
            logo.anchoredPosition = new Vector2(0f, -65f);

            try
            {
                bool changed = Apply(canvas, panel, logo);

                Assert.That(changed, Is.True,
                    "The authored top-anchored logo must be converted to the panel-owned presentation relationship.");
                AssertVector(panel.sizeDelta, new Vector2(1366f, 668f));
                AssertVector(panel.anchoredPosition, Vector2.zero);
                AssertVector(panel.localScale, Vector3.one);
                AssertVector(logo.anchorMin, new Vector2(0.5f, 0.5f));
                AssertVector(logo.anchorMax, new Vector2(0.5f, 0.5f));
                AssertVector(logo.anchoredPosition, new Vector2(0f, 319f));
                AssertVector(logo.localScale, Vector3.one);
                Assert.That(panel.rect.height * 0.5f - logo.anchoredPosition.y,
                    Is.EqualTo(15f).Within(0.01f),
                    "At reference size the logo center is authored 15 pixels inside the panel's top edge.");
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void ShortUltrawideCanvasScalesTheWholePresentationInsteadOfShrinkingOnlyThePanelRect()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1366f, 550f));
            RectTransform panel = CreateRect("MainPanel", canvas, new Vector2(1124f, 550f));
            RectTransform logo = CreateRect("Bees Logo", canvas, new Vector2(197f, 52f));
            logo.anchorMin = new Vector2(0.5f, 1f);
            logo.anchorMax = new Vector2(0.5f, 1f);
            logo.anchoredPosition = new Vector2(0f, -65f);

            try
            {
                bool changed = Apply(canvas, panel, logo);
                float expectedScale = 550f / 690f;

                Assert.That(changed, Is.True);
                AssertVector(panel.sizeDelta, new Vector2(1366f, 668f),
                    "The panel keeps its authored layout rectangle so its fixed-size children are not squeezed independently.");
                AssertVector(panel.localScale, new Vector3(expectedScale, expectedScale, 1f));
                AssertVector(logo.localScale, new Vector3(expectedScale, expectedScale, 1f));
                Assert.That(logo.anchoredPosition.y,
                    Is.EqualTo(319f * expectedScale).Within(0.01f));

                float canvasHalfHeight = canvas.rect.height * 0.5f;
                float logoTop = logo.anchoredPosition.y + 26f * expectedScale;
                float panelBottom = -334f * expectedScale;
                Assert.That(logoTop, Is.LessThanOrEqualTo(canvasHalfHeight + 0.01f));
                Assert.That(panelBottom, Is.GreaterThanOrEqualTo(-canvasHalfHeight - 0.01f));
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void TallCanvasKeepsLogoAttachedAndResolutionCyclingDoesNotAccumulateScale()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(1366f, 550f));
            RectTransform panel = CreateRect("MainPanel", canvas, new Vector2(1366f, 668f));
            RectTransform logo = CreateRect("Bees Logo", canvas, new Vector2(197f, 52f));

            try
            {
                Apply(canvas, panel, logo);
                float shortScale = 550f / 690f;
                Assert.That(panel.localScale.x, Is.EqualTo(shortScale).Within(0.001f));

                canvas.sizeDelta = new Vector2(1366f, 1794f);
                Apply(canvas, panel, logo);
                AssertVector(panel.localScale, Vector3.one,
                    "Tall-display surplus belongs to the starfield; the menu should return to authored scale.");
                AssertVector(panel.anchoredPosition, Vector2.zero);
                AssertVector(logo.anchoredPosition, new Vector2(0f, 319f),
                    "The logo follows the centered panel instead of remaining pinned to the top of the tall canvas.");

                canvas.sizeDelta = new Vector2(1366f, 550f);
                Apply(canvas, panel, logo);
                Assert.That(panel.localScale.x, Is.EqualTo(shortScale).Within(0.001f));
                Assert.That(logo.localScale.x, Is.EqualTo(shortScale).Within(0.001f));
                Assert.That(logo.anchoredPosition.y,
                    Is.EqualTo(319f * shortScale).Within(0.01f),
                    "Repeated aspect-ratio changes must derive from authored geometry rather than the previous runtime scale.");
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        private static bool Apply(RectTransform canvas, RectTransform panel, RectTransform logo)
        {
            return (bool)RuntimeAssembly.InvokeStatic(
                RuntimeAssembly.GetType(GuardTypeName),
                "ApplyPresentationLayout",
                canvas,
                panel,
                logo);
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

        private static void AssertVector(Vector2 actual, Vector2 expected, string message = null)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.01f), message);
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.01f), message);
        }

        private static void AssertVector(Vector3 actual, Vector3 expected, string message = null)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.001f), message);
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.001f), message);
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.001f), message);
        }
    }
}
