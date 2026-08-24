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
        public void ResolverUsesSerializedSquadUiCanvasWhenControllerLivesOutsideCanvas()
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

                object resolved = RuntimeAssembly.InvokeStatic(
                    RuntimeAssembly.GetType(GuardTypeName),
                    "ResolveOwnedCanvas",
                    squadMaker);

                Assert.That(resolved, Is.SameAs(expectedCanvas),
                    "Responsive ownership must come from the serialized Squad Maker UI hierarchy rather than the UI Manager transform ancestry.");
            }
            finally
            {
                Object.DestroyImmediate(manager);
                Object.DestroyImmediate(canvasObject);
            }
        }
    }
}
