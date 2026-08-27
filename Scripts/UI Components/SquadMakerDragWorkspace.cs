using Assets.Scripts.Scenes;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Provides one resolution-independent coordinate space for Squad Maker placement.
    ///
    /// The responsive DropZone is only an available host region. The actual visible/interactable
    /// placement canvas is always 600x340 logical UI units and is centered inside that host. It is
    /// never stretched; on an undersized host it may scale down uniformly so the UI remains usable.
    /// Persistent SquadShip offsets remain world-space gameplay data.
    /// </summary>
    internal sealed class SquadMakerDragWorkspace
    {
        internal static readonly Vector2 LogicalSize = new Vector2(600f, 340f);
        private static readonly Vector2 ReferenceResolution = new Vector2(1366f, 768f);
        private const string WorkspaceName = "Invariant Drag Workspace";
        private const float MinimumScale = 0.05f;
        private const float MinimumPixelsPerUnit = 0.0001f;

        private readonly SquadMaker _scene;
        private readonly RectTransform _host;
        private readonly RectTransform _workspace;
        private readonly Canvas _canvas;
        private readonly CanvasScaler _canvasScaler;
        private readonly CanvasScaler _dragCanvasScaler;
        private readonly float _referenceWorkspaceCanvasScale;
        private readonly float _referenceDragCanvasScale;
        private bool _warnedPerspectiveCamera;

        internal RectTransform Rect => _workspace;

        /// <summary>
        /// Multiplier that keeps objects rendered on the separate Drag Canvas at the same visual
        /// scale as the fixed logical workspace. It is normalized so the authored reference view is
        /// exactly 1 even though the two canvases use different CanvasScaler reference resolutions.
        /// </summary>
        internal float VisualScale
        {
            get
            {
                RefreshVisualFit();
                float workspaceRuntimeScale = ScreenPixelsPerWorkspaceUnit();
                float dragRuntimeScale = CurrentDragCanvasPixelsPerUnit();
                float workspaceRelative = workspaceRuntimeScale / Mathf.Max(MinimumPixelsPerUnit, _referenceWorkspaceCanvasScale);
                float dragRelative = dragRuntimeScale / Mathf.Max(MinimumPixelsPerUnit, _referenceDragCanvasScale);
                return Mathf.Max(MinimumPixelsPerUnit, workspaceRelative / Mathf.Max(MinimumPixelsPerUnit, dragRelative));
            }
        }

        internal SquadMakerDragWorkspace(SquadMaker scene)
        {
            _scene = scene;
            _host = scene != null && scene.DropZone != null
                ? scene.DropZone.transform as RectTransform
                : null;
            _canvas = _host != null ? _host.GetComponentInParent<Canvas>()?.rootCanvas : null;
            _canvasScaler = _canvas != null ? _canvas.GetComponent<CanvasScaler>() : null;
            _dragCanvasScaler = scene != null && scene.DragCanvas != null
                ? scene.DragCanvas.GetComponent<CanvasScaler>()
                : null;
            _referenceWorkspaceCanvasScale = CalculateCanvasScaleAtResolution(_canvasScaler, ReferenceResolution);
            _referenceDragCanvasScale = CalculateCanvasScaleAtResolution(_dragCanvasScaler, ReferenceResolution);
            _workspace = EnsureWorkspaceRect(_host);
            ConfigureWorkspaceBackdrop(_host, _workspace);
            RefreshVisualFit();
        }

        internal void RefreshVisualFit()
        {
            if (_host == null || _workspace == null)
            {
                return;
            }

            float availableWidth = Mathf.Abs(_host.rect.width);
            float availableHeight = Mathf.Abs(_host.rect.height);
            float fit = Mathf.Min(
                1f,
                Mathf.Min(
                    availableWidth / Mathf.Max(1f, LogicalSize.x),
                    availableHeight / Mathf.Max(1f, LogicalSize.y)));
            fit = Mathf.Max(MinimumScale, fit);

            // Logical coordinates never change. Only presentation may uniformly shrink when the
            // responsive host genuinely cannot contain the authored workspace.
            _workspace.anchorMin = new Vector2(0.5f, 0.5f);
            _workspace.anchorMax = new Vector2(0.5f, 0.5f);
            _workspace.pivot = new Vector2(0.5f, 0.5f);
            _workspace.sizeDelta = LogicalSize;
            _workspace.anchoredPosition = Vector2.zero;
            _workspace.localScale = new Vector3(fit, fit, 1f);
        }

        internal bool TryScreenToWorldOffset(Vector2 screenPosition, out Vector2 worldOffset)
        {
            worldOffset = Vector2.zero;
            if (!TryScreenToLogical(screenPosition, out Vector2 logical))
            {
                return false;
            }

            worldOffset = LogicalToWorldOffset(logical);
            return true;
        }

        internal bool TryScreenToLogical(Vector2 screenPosition, out Vector2 logical)
        {
            logical = Vector2.zero;
            RefreshVisualFit();
            if (_workspace == null)
            {
                return false;
            }

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _workspace,
                screenPosition,
                EventCamera,
                out logical))
            {
                return false;
            }

            return _workspace.rect.Contains(logical);
        }

        internal bool ContainsWorldOffset(Vector2 worldOffset)
        {
            if (_workspace == null)
            {
                return false;
            }

            Vector2 logical = WorldOffsetToLogical(worldOffset);
            return _workspace.rect.Contains(logical);
        }

        internal Vector2 WorldOffsetToScreen(Vector2 worldOffset)
        {
            RefreshVisualFit();
            if (_workspace == null)
            {
                return Vector2.zero;
            }

            Vector2 logical = WorldOffsetToLogical(worldOffset);
            return RectTransformUtility.WorldToScreenPoint(EventCamera, _workspace.TransformPoint(logical));
        }

        internal Vector2 WorldOffsetToLogical(Vector2 worldOffset)
        {
            Camera camera = _scene != null ? _scene.Camera : null;
            float pixelsPerWorldUnit = ReferencePixelsPerWorldUnit(camera);
            Vector2 cameraCenter = camera != null
                ? new Vector2(camera.transform.position.x, camera.transform.position.y)
                : Vector2.zero;
            Vector2 displayWorldPoint = worldOffset + ConfigData.StartingPositionOffset;
            Vector2 delta = displayWorldPoint - cameraCenter;
            return (_workspace != null ? _workspace.rect.center : Vector2.zero) + delta * pixelsPerWorldUnit;
        }

        internal Vector2 LogicalToWorldOffset(Vector2 logical)
        {
            Camera camera = _scene != null ? _scene.Camera : null;
            float pixelsPerWorldUnit = ReferencePixelsPerWorldUnit(camera);
            Vector2 cameraCenter = camera != null
                ? new Vector2(camera.transform.position.x, camera.transform.position.y)
                : Vector2.zero;
            Vector2 center = _workspace != null ? _workspace.rect.center : Vector2.zero;
            Vector2 displayWorldPoint = cameraCenter + (logical - center) / pixelsPerWorldUnit;
            return displayWorldPoint - ConfigData.StartingPositionOffset;
        }

        internal Vector2 FormationOriginWorldOffset => LogicalToWorldOffset(
            _workspace != null ? _workspace.rect.center : Vector2.zero);

        internal static float CalculateReferencePixelsPerWorldUnit(Camera camera)
        {
            if (camera == null)
            {
                return Mathf.Max(0.001f, ConfigData.PixelsPerUnit);
            }

            if (camera.orthographic && camera.orthographicSize > 0.001f)
            {
                float viewportHeight = Mathf.Max(0.001f, camera.rect.height);
                return ReferenceResolution.y * viewportHeight / (2f * camera.orthographicSize);
            }

            // Squad Maker is a 2D orthographic editor. Keep a deterministic fallback rather than
            // deriving interaction scale from the live screen if a future scene is misconfigured.
            return Mathf.Max(0.001f, ConfigData.PixelsPerUnit);
        }

        internal static float CalculateCanvasScaleAtResolution(CanvasScaler scaler, Vector2 screenSize)
        {
            if (scaler == null)
            {
                return 1f;
            }

            if (scaler.uiScaleMode == CanvasScaler.ScaleMode.ConstantPixelSize)
            {
                return Mathf.Max(MinimumPixelsPerUnit, scaler.scaleFactor);
            }

            if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize)
            {
                // ConstantPhysicalSize depends on device DPI. It is not used by Squad Maker; keep a
                // deterministic fallback for tests/misconfiguration rather than reading live DPI.
                return 1f;
            }

            Vector2 reference = scaler.referenceResolution;
            if (reference.x <= 0f || reference.y <= 0f)
            {
                return 1f;
            }

            float widthScale = Mathf.Max(MinimumPixelsPerUnit, screenSize.x / reference.x);
            float heightScale = Mathf.Max(MinimumPixelsPerUnit, screenSize.y / reference.y);
            switch (scaler.screenMatchMode)
            {
                case CanvasScaler.ScreenMatchMode.Expand:
                    return Mathf.Min(widthScale, heightScale);
                case CanvasScaler.ScreenMatchMode.Shrink:
                    return Mathf.Max(widthScale, heightScale);
                default:
                    float logWidth = Mathf.Log(widthScale, 2f);
                    float logHeight = Mathf.Log(heightScale, 2f);
                    return Mathf.Pow(2f, Mathf.Lerp(logWidth, logHeight, scaler.matchWidthOrHeight));
            }
        }

        private float ReferencePixelsPerWorldUnit(Camera camera)
        {
            if (camera != null && !camera.orthographic && !_warnedPerspectiveCamera)
            {
                _warnedPerspectiveCamera = true;
                Debug.LogWarning("Squad Maker drag workspace expected an orthographic camera; using the fixed logical fallback scale.");
            }

            return CalculateReferencePixelsPerWorldUnit(camera);
        }

        private float ScreenPixelsPerWorkspaceUnit()
        {
            if (_workspace == null)
            {
                return 1f;
            }

            Vector3 origin = _workspace.TransformPoint(Vector3.zero);
            Vector3 oneUnit = _workspace.TransformPoint(Vector3.right);
            Vector2 originScreen = RectTransformUtility.WorldToScreenPoint(EventCamera, origin);
            Vector2 unitScreen = RectTransformUtility.WorldToScreenPoint(EventCamera, oneUnit);
            float distance = Vector2.Distance(originScreen, unitScreen);
            return Mathf.Max(MinimumPixelsPerUnit, distance);
        }

        private float CurrentDragCanvasPixelsPerUnit()
        {
            Canvas dragCanvas = _scene != null ? _scene.DragCanvas : null;
            if (dragCanvas == null)
            {
                return 1f;
            }

            if (dragCanvas.renderMode != RenderMode.WorldSpace)
            {
                return Mathf.Max(MinimumPixelsPerUnit, dragCanvas.scaleFactor);
            }

            RectTransform rect = dragCanvas.transform as RectTransform;
            if (rect == null)
            {
                return 1f;
            }

            Camera eventCamera = dragCanvas.worldCamera;
            Vector3 origin = rect.TransformPoint(Vector3.zero);
            Vector3 oneUnit = rect.TransformPoint(Vector3.right);
            Vector2 originScreen = RectTransformUtility.WorldToScreenPoint(eventCamera, origin);
            Vector2 unitScreen = RectTransformUtility.WorldToScreenPoint(eventCamera, oneUnit);
            return Mathf.Max(MinimumPixelsPerUnit, Vector2.Distance(originScreen, unitScreen));
        }

        private Camera EventCamera
        {
            get
            {
                if (_canvas == null || _canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    return null;
                }

                return _canvas.worldCamera != null
                    ? _canvas.worldCamera
                    : (_scene != null ? _scene.Camera : null);
            }
        }

        private static RectTransform EnsureWorkspaceRect(RectTransform host)
        {
            if (host == null)
            {
                return null;
            }

            Transform existing = host.Find(WorkspaceName);
            RectTransform workspace = existing as RectTransform;
            if (workspace == null)
            {
                GameObject workspaceObject = new GameObject(WorkspaceName, typeof(RectTransform));
                workspace = workspaceObject.GetComponent<RectTransform>();
                workspace.SetParent(host, false);
            }

            workspace.anchorMin = new Vector2(0.5f, 0.5f);
            workspace.anchorMax = new Vector2(0.5f, 0.5f);
            workspace.pivot = new Vector2(0.5f, 0.5f);
            workspace.sizeDelta = LogicalSize;
            workspace.anchoredPosition = Vector2.zero;
            workspace.SetAsFirstSibling();
            return workspace;
        }

        private static void ConfigureWorkspaceBackdrop(RectTransform host, RectTransform workspace)
        {
            if (host == null || workspace == null)
            {
                return;
            }

            Image hostImage = host.GetComponent<Image>();
            if (hostImage == null)
            {
                return;
            }

            Image workspaceImage = workspace.GetComponent<Image>();
            if (workspaceImage == null)
            {
                workspaceImage = workspace.gameObject.AddComponent<Image>();
            }

            workspaceImage.sprite = hostImage.sprite;
            workspaceImage.color = hostImage.color;
            workspaceImage.material = hostImage.material;
            workspaceImage.type = hostImage.type;
            workspaceImage.preserveAspect = hostImage.preserveAspect;
            workspaceImage.fillCenter = hostImage.fillCenter;
            workspaceImage.raycastTarget = false;

            // The responsive host remains the layout-owned available region, but it must not paint a
            // stretched copy of the canvas behind the invariant child.
            hostImage.enabled = false;
        }
    }
}
