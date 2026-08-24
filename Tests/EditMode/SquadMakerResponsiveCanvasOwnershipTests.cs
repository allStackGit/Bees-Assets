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
    }
}
