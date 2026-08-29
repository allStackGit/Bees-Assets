using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerResponsiveCanvasOwnershipTests
    {
        private const string GuardTypeName = "Assets.Scripts.UI_Components.SquadMakerResponsiveLayoutGuard";
        private const string SquadMakerTypeName = "Assets.Scripts.Scenes.SquadMaker";

        [Test]
        public void GuardInitializesAgainstSerializedSquadUiCanvasWhenControllerLivesOutsideCanvas()
        {
            GameObject manager = new GameObject("UI Manager");
            GameObject canvasObject = new GameObject(
                "IntroPopup",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler));
            GameObject chosenSquadList = new GameObject("Content", typeof(RectTransform));
            chosenSquadList.transform.SetParent(canvasObject.transform, false);

            try
            {
                Canvas expectedCanvas = canvasObject.GetComponent<Canvas>();
                expectedCanvas.renderMode = RenderMode.ScreenSpaceOverlay;

                Component squadMaker = manager.AddComponent(RuntimeAssembly.GetType(SquadMakerTypeName));
                RuntimeAssembly.SetField(squadMaker, "ChosenSquadList", chosenSquadList);

                Assert.That(manager.GetComponentInParent<Canvas>(), Is.Null,
                    "The regression requires the controller to remain outside the visible UI Canvas, matching Squad Maker.unity.");

                Component guard = manager.AddComponent(RuntimeAssembly.GetType(GuardTypeName));
                object resolvedCanvas = RuntimeAssembly.GetField(guard, "_canvas");

                Assert.That(resolvedCanvas, Is.SameAs(expectedCanvas),
                    "The specialized Squad Maker guard must initialize against the Canvas that owns its serialized UI, not the UI Manager transform ancestry.");
            }
            finally
            {
                Object.DestroyImmediate(manager);
                Object.DestroyImmediate(canvasObject);
            }
        }

        [Test]
        public void ReinitializingSameGuardDoesNotRecaptureResponsiveGeometryAsAuthoredBaseline()
        {
            GameObject manager = new GameObject("UI Manager");
            GameObject canvasObject = new GameObject(
                "IntroPopup",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler));
            GameObject chosenSquadList = new GameObject("Content", typeof(RectTransform));
            RectTransform chosenRect = chosenSquadList.GetComponent<RectTransform>();
            chosenRect.SetParent(canvasObject.transform, false);
            chosenRect.anchorMin = new Vector2(0.5f, 0.5f);
            chosenRect.anchorMax = new Vector2(0.5f, 0.5f);
            chosenRect.pivot = new Vector2(0.5f, 0.5f);
            chosenRect.anchoredPosition = new Vector2(17f, -11f);
            chosenRect.sizeDelta = new Vector2(321f, 123f);

            try
            {
                Canvas expectedCanvas = canvasObject.GetComponent<Canvas>();
                expectedCanvas.renderMode = RenderMode.ScreenSpaceOverlay;

                Component squadMaker = manager.AddComponent(RuntimeAssembly.GetType(SquadMakerTypeName));
                RuntimeAssembly.SetField(squadMaker, "ChosenSquadList", chosenSquadList);

                // AddComponent invokes Awake synchronously, which performs the first initialization.
                Component guard = manager.AddComponent(RuntimeAssembly.GetType(GuardTypeName));
                Vector2 firstBaselineSize = GetFirstReferenceBranchSizeDelta(guard);

                Assert.That(firstBaselineSize.x, Is.EqualTo(321f).Within(0.001f));
                Assert.That(firstBaselineSize.y, Is.EqualTo(123f).Within(0.001f));
                Assert.That(chosenRect.sizeDelta, Is.EqualTo(Vector2.zero),
                    "The first responsive pass should have converted the authored fixed rect into proportional anchors.");

                // Scene-load bootstrap calls Initialize after AddComponent. That second call must be
                // idempotent; otherwise it captures the already-responsive zero-sizeDelta geometry
                // as the new reference and future display changes accumulate drift.
                RuntimeAssembly.Invoke(guard, "Initialize", squadMaker);

                Vector2 secondBaselineSize = GetFirstReferenceBranchSizeDelta(guard);
                Assert.That(secondBaselineSize.x, Is.EqualTo(firstBaselineSize.x).Within(0.001f));
                Assert.That(secondBaselineSize.y, Is.EqualTo(firstBaselineSize.y).Within(0.001f));
                Assert.That(RuntimeAssembly.GetField(guard, "_canvas"), Is.SameAs(expectedCanvas));
            }
            finally
            {
                Object.DestroyImmediate(manager);
                Object.DestroyImmediate(canvasObject);
            }
        }

        private static Vector2 GetFirstReferenceBranchSizeDelta(Component guard)
        {
            IList branches = (IList)RuntimeAssembly.GetField(guard, "_referenceBranches");
            Assert.That(branches.Count, Is.GreaterThan(0));

            object branch = branches[0];
            return (Vector2)branch.GetType().GetField("SizeDelta").GetValue(branch);
        }
    }
}
