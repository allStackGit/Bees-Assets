using Assets.Scripts.Scenes;
using UnityEngine;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Provides one resolution-independent coordinate space for Squad Maker placement.
    ///
    /// The authored drop surface is 600x340 logical UI units. That logical size never changes;
    /// only its presentation scale changes when the responsive host is too small to display it at
    /// full size. Persistent SquadShip offsets remain world-space gameplay data.
    /// </summary>
    internal sealed class SquadMakerDragWorkspace
    {
        internal static readonly Vector2 LogicalSize = new Vector2(600f, 340f);
        private static readonly Vector2 ReferenceResolution = new Vector2(1366f, 768f);
        private const string WorkspaceName = "Invariant Drag Workspace";
        private const float MinimumScale = 0.05f;

        private readonly SquadMaker _scene;
        private readonly RectTransform _host;
        private readonly RectTransform _workspace;
        private readonly Canvas _canvas;
        private bool _warnedPerspectiveCamera;

        internal RectTransform Rect => _workspace;
        internal float VisualScale => _workspace != null ? Mathf.Abs(_workspace.localScale.x) : 1f;

        internal SquadMakerDragWorkspace(SquadMaker scene)
        {
            _scene = scene;
            _host = scene != null && scene.DropZone != null
                ? scene.DropZone.transform as RectTransform
                : null;
            _canvas = _host != null ? _host.GetComponentInParent<Canvas>()?.rootCanvas : null;
            _workspace = EnsureWorkspaceRect(_host);
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

        private float ReferencePixelsPerWorldUnit(Camera camera)
        {
            if (camera != null && !camera.orthographic && !_warnedPerspectiveCamera)
            {
                _warnedPerspectiveCamera = true;
                Debug.LogWarning("Squad Maker drag workspace expected an orthographic camera; using the fixed logical fallback scale.");
            }

            return CalculateReferencePixelsPerWorldUnit(camera);
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
                workspace.SetAsLastSibling();
            }

            workspace.anchorMin = new Vector2(0.5f, 0.5f);
            workspace.anchorMax = new Vector2(0.5f, 0.5f);
            workspace.pivot = new Vector2(0.5f, 0.5f);
            workspace.sizeDelta = LogicalSize;
            workspace.anchoredPosition = Vector2.zero;
            return workspace;
        }
    }
}
