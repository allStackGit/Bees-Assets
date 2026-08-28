using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class SquadMakerDragWorkspaceTests
    {
        private const string SquadMakerTypeName = "Assets.Scripts.Scenes.SquadMaker";
        private const string WorkspaceTypeName = "Assets.Scripts.UIComponents.SquadMakerDragWorkspace";

        [Test]
        public void LogicalWorkspaceAndCanonicalMappingStayInvariantAcrossHostSizes()
        {
            GameObject canvasObject = new GameObject("Root Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            RectTransform host = CreateRect("Drop Zone", canvasObject.transform, new Vector2(600f, 340f));
            host.gameObject.AddComponent<Image>();

            GameObject dragCanvasObject = new GameObject("Drag Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            Canvas dragCanvas = dragCanvasObject.GetComponent<Canvas>();
            dragCanvas.renderMode = RenderMode.ScreenSpaceOverlay;

            GameObject cameraObject = new GameObject("Camera", typeof(Camera));
            Camera camera = cameraObject.GetComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 50f;
            camera.transform.position = new Vector3(0f, 0f, -10f);

            GameObject manager = new GameObject("UI Manager");
            Component squadMaker = manager.AddComponent(RuntimeAssembly.GetType(SquadMakerTypeName));
            RuntimeAssembly.SetField(squadMaker, "DropZone", host.gameObject);
            RuntimeAssembly.SetField(squadMaker, "DragCanvas", dragCanvas);
            RuntimeAssembly.SetField(squadMaker, "Camera", camera);

            Type workspaceType = RuntimeAssembly.GetType(WorkspaceTypeName);
            object workspace = Activator.CreateInstance(
                workspaceType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { squadMaker },
                culture: null);
            RectTransform workspaceRect = (RectTransform)RuntimeAssembly.GetField(workspace, "_workspace");

            try
            {
                Vector2 canonicalOffset = new Vector2(12.5f, -7.25f);
                Vector2 referenceLogical = (Vector2)RuntimeAssembly.Invoke(workspace, "WorldOffsetToLogical", canonicalOffset);
                Vector2 roundTrip = (Vector2)RuntimeAssembly.Invoke(workspace, "LogicalToWorldOffset", referenceLogical);
                Assert.That(roundTrip.x, Is.EqualTo(canonicalOffset.x).Within(0.0001f));
                Assert.That(roundTrip.y, Is.EqualTo(canonicalOffset.y).Within(0.0001f));

                (Vector2 hostSize, float expectedScale)[] cases =
                {
                    (new Vector2(600f, 340f), 1f),
                    (new Vector2(1200f, 680f), 1f),
                    (new Vector2(300f, 170f), 0.5f),
                    (new Vector2(900f, 200f), 200f / 340f),
                    (new Vector2(750f, 510f), 1f),
                    (new Vector2(600f, 340f), 1f)
                };

                foreach ((Vector2 hostSize, float expectedScale) in cases)
                {
                    host.sizeDelta = hostSize;
                    RuntimeAssembly.Invoke(workspace, "RefreshVisualFit");

                    Assert.That(workspaceRect.sizeDelta.x, Is.EqualTo(600f).Within(0.001f));
                    Assert.That(workspaceRect.sizeDelta.y, Is.EqualTo(340f).Within(0.001f));
                    Assert.That(workspaceRect.localScale.x, Is.EqualTo(expectedScale).Within(0.001f));
                    Assert.That(workspaceRect.localScale.y, Is.EqualTo(expectedScale).Within(0.001f));
                    Assert.That(workspaceRect.localScale.x, Is.EqualTo(workspaceRect.localScale.y).Within(0.0001f));

                    Vector2 logical = (Vector2)RuntimeAssembly.Invoke(workspace, "WorldOffsetToLogical", canonicalOffset);
                    Assert.That(logical.x, Is.EqualTo(referenceLogical.x).Within(0.0001f));
                    Assert.That(logical.y, Is.EqualTo(referenceLogical.y).Within(0.0001f));
                    Assert.That((bool)RuntimeAssembly.Invoke(workspace, "ContainsWorldOffset", canonicalOffset), Is.True);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(manager);
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(dragCanvasObject);
                UnityEngine.Object.DestroyImmediate(canvasObject);
            }
        }

        [Test]
        public void ResizeRefreshIsPresentationOnlyAndDoesNotReDropSquadShips()
        {
            string dropper = ReadSource("Scripts", "UI Components", "Dropper.cs");
            string refresh = ExtractMethod(dropper, "public void RefreshWorkspacePresentation()", "public void PullNewDragIcon");
            Assert.That(refresh, Does.Contain("_workspace.RefreshVisualFit()"));
            Assert.That(refresh, Does.Contain("WorldOffsetToScreen"));
            Assert.That(refresh, Does.Not.Contain("FleetDragEnd"));
            Assert.That(refresh, Does.Not.Contain("PlaceShipAtWorldOffset"));
            Assert.That(refresh, Does.Not.Contain("AddShipToSquad"));
            Assert.That(refresh, Does.Not.Contain("RemoveShipFromSquad"));
            Assert.That(refresh, Does.Not.Contain("SetOffset("));

            string resizeGuard = ReadSource("Scripts", "UI Components", "SquadMakerDragWorkspaceResizeGuard.cs");
            Assert.That(resizeGuard, Does.Contain("CancelInvoke(LegacyResizeCallback)"));
            Assert.That(resizeGuard, Does.Contain("RefreshDisplayMetrics()"));
            Assert.That(resizeGuard, Does.Contain("RefreshWorkspacePresentation()"));
            Assert.That(resizeGuard, Does.Not.Contain("GetSquadShips().Clear"));
            Assert.That(resizeGuard, Does.Not.Contain(".Reposition("));
            Assert.That(resizeGuard, Does.Not.Contain("FleetDragEnd"));
        }

        [Test]
        public void AutoDropCommitsTemporaryOriginBeforeApplyingFormation()
        {
            string dropper = ReadSource("Scripts", "UI Components", "Dropper.cs");
            string autoPlace = ExtractMethod(dropper, "public void AutoPlaceShip", "public void StartDragExistingIcon");

            Assert.That(autoPlace, Does.Contain("SetupActiveDragging"));
            Assert.That(autoPlace, Does.Contain("IsValidDropLocation = _workspace.ContainsWorldOffset(origin)"));
            Assert.That(autoPlace, Does.Contain("_scene.FleetDragEnd()"));
            Assert.That(autoPlace, Does.Contain("_scene.SetFormation(_scene.CurrentFormation)"));
            Assert.That(autoPlace, Does.Not.Contain("PlaceShipAtWorldOffset(origin"));
            Assert.That(autoPlace.IndexOf("_scene.FleetDragEnd()", StringComparison.Ordinal),
                Is.LessThan(autoPlace.IndexOf("_scene.SetFormation(_scene.CurrentFormation)", StringComparison.Ordinal)));
        }

        private static RectTransform CreateRect(string name, Transform parent, Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = Vector2.zero;
            rect.localScale = Vector3.one;
            return rect;
        }

        private static string ReadSource(params string[] segments)
        {
            string path = Application.dataPath;
            foreach (string segment in segments)
            {
                path = Path.Combine(path, segment);
            }
            return File.ReadAllText(path);
        }

        private static string ExtractMethod(string source, string startMarker, string nextMarker)
        {
            int start = source.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), $"Missing method marker: {startMarker}");
            int end = source.IndexOf(nextMarker, start + startMarker.Length, StringComparison.Ordinal);
            Assert.That(end, Is.GreaterThan(start), $"Missing next method marker: {nextMarker}");
            return source.Substring(start, end - start);
        }
    }
}
