using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerResponsiveLayoutGuardTests
    {
        private const string GuardTypeName = "Assets.Scripts.UI_Components.SquadMakerResponsiveLayoutGuard";
        private static readonly Vector2 ReferenceResolution = new Vector2(1366f, 768f);

        [Test]
        public void SimultaneouslyActiveHoverDescriptionsDoNotConsumeLayoutRows()
        {
            GameObject column = new GameObject(
                "Chosen Squads Column",
                typeof(RectTransform),
                typeof(VerticalLayoutGroup));
            GameObject startDescription = CreateDescription("Start Description", column.transform);
            GameObject testDescription = CreateDescription("Test Description", column.transform);

            startDescription.SetActive(false);
            testDescription.SetActive(false);

            try
            {
                System.Type guardType = RuntimeAssembly.GetType(GuardTypeName);

                RuntimeAssembly.InvokeStatic(
                    guardType,
                    "SetDescriptionVisibility",
                    startDescription,
                    false);
                RuntimeAssembly.InvokeStatic(
                    guardType,
                    "SetDescriptionVisibility",
                    testDescription,
                    false);

                Assert.That(startDescription.activeSelf, Is.True);
                Assert.That(testDescription.activeSelf, Is.True,
                    "START and TEST descriptions may coexist when both buttons are available.");

                AssertIgnoredByLayout(startDescription);
                AssertIgnoredByLayout(testDescription);
                AssertCanvasGroup(startDescription, 0f);
                AssertCanvasGroup(testDescription, 0f);

                RuntimeAssembly.InvokeStatic(
                    guardType,
                    "SetDescriptionVisibility",
                    startDescription,
                    true);

                AssertIgnoredByLayout(startDescription);
                AssertIgnoredByLayout(testDescription);
                AssertCanvasGroup(startDescription, 1f);
                AssertCanvasGroup(testDescription, 0f);
            }
            finally
            {
                Object.DestroyImmediate(column);
            }
        }

        [TestCase(2047f, 266f)]
        [TestCase(574f, 1594f)]
        [TestCase(2000f, 900f)]
        public void CenteredReferenceCompositionMapsToEntireViewportAtAnyAspect(float width, float height)
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(width, height));
            RectTransform root = CreateRect("Squad Maker Reference Composition", canvas, ReferenceResolution);

            try
            {
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "ApplyReferenceProportionalGeometry",
                    root,
                    new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f),
                    Vector2.zero,
                    ReferenceResolution,
                    Vector3.one);

                Assert.That(changed, Is.True);
                AssertVector(root.anchorMin, Vector2.zero);
                AssertVector(root.anchorMax, Vector2.one);
                AssertVector(root.anchoredPosition, Vector2.zero);
                AssertVector(root.sizeDelta, Vector2.zero);
                Assert.That(root.rect.width, Is.EqualTo(width).Within(0.01f),
                    "The full reference composition must span the live width instead of remaining a fitted 16:9 island.");
                Assert.That(root.rect.height, Is.EqualTo(height).Within(0.01f),
                    "The full reference composition must span the live height instead of remaining a fitted 16:9 island.");
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void CenterAnchoredReferenceBranchCarriesPositionAndExtentProportionally()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(2000f, 900f));
            RectTransform branch = CreateRect("Reference Branch", canvas, new Vector2(600f, 100f));
            Vector2 authoredAnchor = new Vector2(0.5f, 0.5f);
            Vector2 authoredPosition = new Vector2(-110f, 230f);
            branch.anchoredPosition = authoredPosition;

            try
            {
                RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "ApplyReferenceProportionalGeometry",
                    branch,
                    authoredAnchor,
                    authoredAnchor,
                    new Vector2(0.5f, 0.5f),
                    authoredPosition,
                    new Vector2(600f, 100f),
                    Vector3.one);

                Vector2 expectedMin = new Vector2(273f / 1366f, 564f / 768f);
                Vector2 expectedMax = new Vector2(873f / 1366f, 664f / 768f);
                AssertVector(branch.anchorMin, expectedMin);
                AssertVector(branch.anchorMax, expectedMax);
                AssertVector(branch.anchoredPosition, Vector2.zero);
                AssertVector(branch.sizeDelta, Vector2.zero);
                Assert.That(branch.rect.width,
                    Is.EqualTo(2000f * 600f / 1366f).Within(0.01f));
                Assert.That(branch.rect.height,
                    Is.EqualTo(900f * 100f / 768f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void FixedArtboardAnchorMappingIsReversedBeforeReferenceGeometryIsCaptured()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(2000f, 900f));
            RectTransform branch = CreateRect("Squad Maker Branch", canvas, new Vector2(400f, 300f));
            Vector2 authoredMin = new Vector2(0.2f, 0.25f);
            Vector2 authoredMax = new Vector2(0.8f, 0.75f);
            Vector2 referenceOrigin = (canvas.rect.size - ReferenceResolution) * 0.5f;
            branch.anchorMin = MapReferenceAnchor(authoredMin, canvas.rect.size, referenceOrigin);
            branch.anchorMax = MapReferenceAnchor(authoredMax, canvas.rect.size, referenceOrigin);

            try
            {
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "RestoreLegacyReferenceMappedDirectAnchors",
                    canvas);

                Assert.That(changed, Is.True);
                AssertVector(branch.anchorMin, authoredMin);
                AssertVector(branch.anchorMax, authoredMax);
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void NestedStructuralLayoutFillsItsAllocatedCrossAxis()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(2000f, 900f));
            RectTransform region = CreateRect("Squad Presets Region", canvas, new Vector2(1000f, 800f));
            VerticalLayoutGroup layout = region.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 0f;
            layout.childControlWidth = false;
            layout.childForceExpandWidth = false;

            RectTransform body = CreateRect("Body", region, new Vector2(800f, 700f));
            RectTransform toolbar = CreateRect("Toolbar", region, new Vector2(800f, 100f));

            try
            {
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "RepairNestedStructuralLayouts",
                    canvas,
                    canvas,
                    0);

                Assert.That(changed, Is.True);
                Assert.That(body.rect.width, Is.EqualTo(1000f).Within(0.01f));
                Assert.That(toolbar.rect.width, Is.EqualTo(1000f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void UltrawideMultiColumnLayoutGivesSurplusToUniquelyDominantWorkRegion()
        {
            RectTransform row = CreateRect("Squad Maker Columns", null, new Vector2(2000f, 800f));
            HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 0f;
            layout.childControlWidth = false;
            layout.childForceExpandWidth = false;

            RectTransform inventory = CreateRect("Inventory", row, new Vector2(250f, 800f));
            RectTransform workArea = CreateRect("Squad Presets", row, new Vector2(900f, 800f));
            RectTransform squads = CreateRect("Squads", row, new Vector2(250f, 800f));

            try
            {
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "FitDominantStructuralHorizontalChild",
                    row);

                Assert.That(changed, Is.True,
                    "The main work region should absorb ultrawide surplus instead of leaving blank side bands.");
                Assert.That(inventory.rect.width, Is.EqualTo(250f).Within(0.01f));
                Assert.That(workArea.rect.width, Is.EqualTo(1500f).Within(0.01f));
                Assert.That(squads.rect.width, Is.EqualTo(250f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(row.gameObject);
            }
        }

        [Test]
        public void RepeatedResolutionChangesRestoreStructuralChildBaselineInsteadOfCompoundingShrink()
        {
            RectTransform canvas = CreateRect("Canvas", null, ReferenceResolution);
            RectTransform region = CreateRect("Squad Maker Main Region", canvas, ReferenceResolution);
            VerticalLayoutGroup layout = region.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = 0f;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = false;
            layout.childForceExpandWidth = false;

            RectTransform body = CreateRect("Dominant Body", region, new Vector2(1366f, 668f));
            RectTransform footer = CreateRect("Footer", region, new Vector2(1366f, 100f));
            Component guard = region.gameObject.AddComponent(RuntimeAssembly.GetType(GuardTypeName));

            try
            {
                RuntimeAssembly.SetField(guard, "_canvasRect", canvas);
                RuntimeAssembly.Invoke(guard, "CaptureDirectReferenceBranches");
                RuntimeAssembly.SetField(guard, "_referenceGeometryCaptured", true);

                RuntimeAssembly.Invoke(guard, "ApplyViewportFill");
                Assert.That(body.rect.height, Is.EqualTo(668f).Within(0.01f));
                Assert.That(footer.rect.height, Is.EqualTo(100f).Within(0.01f));

                canvas.sizeDelta = new Vector2(1366f, 400f);
                RuntimeAssembly.Invoke(guard, "ApplyViewportFill");
                Assert.That(body.rect.height, Is.EqualTo(300f).Within(0.01f));

                canvas.sizeDelta = new Vector2(1366f, 250f);
                RuntimeAssembly.Invoke(guard, "ApplyViewportFill");
                Assert.That(body.rect.height, Is.EqualTo(150f).Within(0.01f));

                canvas.sizeDelta = ReferenceResolution;
                RuntimeAssembly.Invoke(guard, "ApplyViewportFill");
                Assert.That(body.rect.height, Is.EqualTo(668f).Within(0.01f),
                    "Returning to the authored viewport must rebuild from canonical geometry rather than the previously shrunken runtime size.");
                Assert.That(footer.rect.height, Is.EqualTo(100f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void SmallLocalButtonRowIsNotTreatedAsScreenStructure()
        {
            RectTransform canvas = CreateRect("Canvas", null, new Vector2(2000f, 900f));
            RectTransform row = CreateRect("Save Buttons", canvas, new Vector2(600f, 60f));
            HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.childControlWidth = false;
            layout.childForceExpandWidth = false;

            RectTransform buttonA = CreateRect("Save", row, new Vector2(250f, 50f));
            RectTransform buttonB = CreateRect("Delete", row, new Vector2(250f, 50f));
            Vector2 originalA = buttonA.sizeDelta;
            Vector2 originalB = buttonB.sizeDelta;

            try
            {
                bool structural = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "IsStructuralLayout",
                    canvas,
                    row);
                bool changed = (bool)RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "RepairNestedStructuralLayouts",
                    canvas,
                    canvas,
                    0);

                Assert.That(structural, Is.False);
                Assert.That(changed, Is.False);
                AssertVector(buttonA.sizeDelta, originalA);
                AssertVector(buttonB.sizeDelta, originalB);
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        private static Vector2 MapReferenceAnchor(
            Vector2 authoredAnchor,
            Vector2 canvasSize,
            Vector2 referenceOrigin)
        {
            return new Vector2(
                (referenceOrigin.x + authoredAnchor.x * ReferenceResolution.x) / canvasSize.x,
                (referenceOrigin.y + authoredAnchor.y * ReferenceResolution.y) / canvasSize.y);
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

        private static GameObject CreateDescription(string name, Transform parent)
        {
            GameObject description = new GameObject(name, typeof(RectTransform));
            description.transform.SetParent(parent, false);
            return description;
        }

        private static void AssertIgnoredByLayout(GameObject description)
        {
            LayoutElement layout = description.GetComponent<LayoutElement>();
            Assert.That(layout, Is.Not.Null);
            Assert.That(layout.ignoreLayout, Is.True,
                "Hover-only description text must not reserve space in the level-details column.");
        }

        private static void AssertCanvasGroup(GameObject description, float expectedAlpha)
        {
            CanvasGroup group = description.GetComponent<CanvasGroup>();
            Assert.That(group, Is.Not.Null);
            Assert.That(group.alpha, Is.EqualTo(expectedAlpha));
            Assert.That(group.interactable, Is.False);
            Assert.That(group.blocksRaycasts, Is.False);
        }

        private static void AssertVector(Vector2 actual, Vector2 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.01f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.01f));
        }
    }
}
