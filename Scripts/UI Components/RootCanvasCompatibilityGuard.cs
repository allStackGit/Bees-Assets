using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Final compatibility pass for legacy screen-space UI after responsive wrapper repair and
    /// semantic gameplay HUD layout have run. This guard deliberately operates at ownership
    /// boundaries only: viewport-level LayoutGroup owners/backers, direct root-canvas interactive
    /// islands, and the legacy Squad Tabs container. It must not translate arbitrary nested UI.
    /// </summary>
    [DefaultExecutionOrder(-800)]
    public sealed class RootCanvasCompatibilityGuard : MonoBehaviour
    {
        private const float RepairInterval = 0.25f;
        private const float ScreenCoverageThreshold = 0.90f;
        private const float FullAnchorThreshold = 0.95f;
        private const int MaxHierarchyDepth = 16;
        private static readonly Vector2 DefaultReferenceResolution = new Vector2(1366f, 768f);

        private Canvas _canvas;
        private RectTransform _canvasRect;
        private CanvasScaler _scaler;
        private RectTransform _squadTabsRoot;
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;
        private float _nextRepairTime;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
                for (int i = 0; i < canvases.Length; i++)
                {
                    Canvas canvas = canvases[i];
                    if (canvas == null || canvas.renderMode == RenderMode.WorldSpace || !canvas.isRootCanvas)
                    {
                        continue;
                    }

                    RootCanvasCompatibilityGuard guard = canvas.GetComponent<RootCanvasCompatibilityGuard>();
                    if (guard == null)
                    {
                        guard = canvas.gameObject.AddComponent<RootCanvasCompatibilityGuard>();
                    }
                    guard.Initialize(canvas);
                }
            }
        }

        private void Initialize(Canvas canvas)
        {
            _canvas = canvas;
            _canvasRect = canvas != null ? canvas.transform as RectTransform : null;
            _scaler = canvas != null ? canvas.GetComponent<CanvasScaler>() : null;
            _squadTabsRoot = null;
            _lastScreenWidth = -1;
            _lastScreenHeight = -1;
            ApplyCompatibilityLayout();
        }

        private void LateUpdate()
        {
            if (_canvas == null || _canvasRect == null || _canvas.renderMode == RenderMode.WorldSpace)
            {
                return;
            }

            bool displayChanged = Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight;
            if (displayChanged)
            {
                _lastScreenWidth = Screen.width;
                _lastScreenHeight = Screen.height;
                _squadTabsRoot = null;
            }

            if (!displayChanged && Time.unscaledTime < _nextRepairTime)
            {
                return;
            }

            _nextRepairTime = Time.unscaledTime + RepairInterval;
            ApplyCompatibilityLayout();
        }

        private void ApplyCompatibilityLayout()
        {
            if (_canvas == null || _canvasRect == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            RepairViewportOwners(_canvasRect, GetReferenceResolution(), 0);
            ClampDirectInteractiveIslands(_canvasRect, GetReferenceResolution());
            KeepSquadTabsAtActualTopLeft();
            Canvas.ForceUpdateCanvases();
        }

        private Vector2 GetReferenceResolution()
        {
            return _scaler != null &&
                   _scaler.referenceResolution.x > 0f &&
                   _scaler.referenceResolution.y > 0f
                ? _scaler.referenceResolution
                : DefaultReferenceResolution;
        }

        private void RepairViewportOwners(RectTransform parent, Vector2 referenceResolution, int depth)
        {
            if (parent == null || depth >= MaxHierarchyDepth)
            {
                return;
            }

            bool parentRepresentsViewport = parent == _canvasRect ||
                                            RectCoversReferenceScreen(parent, referenceResolution) ||
                                            HasFullStretchAnchors(parent);

            for (int i = 0; i < parent.childCount; i++)
            {
                RectTransform child = parent.GetChild(i) as RectTransform;
                if (child == null)
                {
                    continue;
                }

                Canvas childCanvas = child.GetComponent<Canvas>();
                if (childCanvas != null && childCanvas.rootCanvas != _canvas)
                {
                    continue;
                }

                if (parentRepresentsViewport && parent.GetComponent<LayoutGroup>() == null)
                {
                    bool layoutOwner = child.GetComponent<LayoutGroup>() != null;
                    bool screenBacker = IsScreenBacker(child);
                    bool looksScreenSized = HasFullStretchAnchors(child) ||
                                            RectCoversReferenceScreen(child, referenceResolution);

                    if ((layoutOwner || screenBacker) && looksScreenSized)
                    {
                        StretchToParent(child);
                        if (layoutOwner)
                        {
                            LayoutRebuilder.ForceRebuildLayoutImmediate(child);
                        }
                    }
                }

                if (HasFullStretchAnchors(child) || RectCoversReferenceScreen(child, referenceResolution))
                {
                    RepairViewportOwners(child, referenceResolution, depth + 1);
                }
            }
        }

        private static bool IsScreenBacker(RectTransform rect)
        {
            if (rect == null || rect.GetComponent<Image>() == null)
            {
                return false;
            }

            string objectName = rect.gameObject.name;
            return objectName.IndexOf("backer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   objectName.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool StretchToParent(RectTransform rect)
        {
            if (rect == null || rect.parent is not RectTransform parent ||
                parent.GetComponent<LayoutGroup>() != null)
            {
                return false;
            }

            bool alreadyFilled = rect.anchorMin == Vector2.zero &&
                                 rect.anchorMax == Vector2.one &&
                                 rect.offsetMin == Vector2.zero &&
                                 rect.offsetMax == Vector2.zero;
            if (alreadyFilled)
            {
                return false;
            }

            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return true;
        }

        private static void ClampDirectInteractiveIslands(
            RectTransform canvasRect,
            Vector2 referenceResolution)
        {
            if (canvasRect == null)
            {
                return;
            }

            for (int i = 0; i < canvasRect.childCount; i++)
            {
                RectTransform child = canvasRect.GetChild(i) as RectTransform;
                if (child == null || child.GetComponent<LayoutGroup>() != null ||
                    HasFullStretchAnchors(child) || RectCoversReferenceScreen(child, referenceResolution))
                {
                    continue;
                }

                Canvas nestedCanvas = child.GetComponent<Canvas>();
                if (nestedCanvas != null && nestedCanvas.isRootCanvas)
                {
                    continue;
                }

                if (child.GetComponentInChildren<Selectable>(true) == null)
                {
                    continue;
                }

                ClampIslandToCanvas(child, canvasRect);
            }
        }

        internal static bool ClampIslandToCanvas(RectTransform island, RectTransform canvasRect)
        {
            if (island == null || canvasRect == null)
            {
                return false;
            }

            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, island);
            if (bounds.size.x < 1f || bounds.size.y < 1f)
            {
                return false;
            }

            Rect available = canvasRect.rect;
            Vector2 correction = Vector2.zero;

            if (bounds.size.x <= available.width)
            {
                if (bounds.min.x < available.xMin)
                {
                    correction.x = available.xMin - bounds.min.x;
                }
                else if (bounds.max.x > available.xMax)
                {
                    correction.x = available.xMax - bounds.max.x;
                }
            }

            if (bounds.size.y <= available.height)
            {
                if (bounds.min.y < available.yMin)
                {
                    correction.y = available.yMin - bounds.min.y;
                }
                else if (bounds.max.y > available.yMax)
                {
                    correction.y = available.yMax - bounds.max.y;
                }
            }

            if (correction == Vector2.zero)
            {
                return false;
            }

            Vector3 worldCorrection = canvasRect.TransformVector(new Vector3(correction.x, correction.y, 0f));
            island.position += worldCorrection;
            return true;
        }

        private void KeepSquadTabsAtActualTopLeft()
        {
            if (_squadTabsRoot == null)
            {
                _squadTabsRoot = FindNamedRectTransform(_canvasRect, "Squad Tabs", 0);
            }

            if (_squadTabsRoot == null || !_squadTabsRoot.gameObject.activeInHierarchy)
            {
                return;
            }

            LayoutGroup layout = _squadTabsRoot.GetComponent<LayoutGroup>();
            if (layout != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_squadTabsRoot);
            }

            PinLayoutRootToCanvasCorner(_squadTabsRoot, _canvasRect, false, true);
        }

        private static RectTransform FindNamedRectTransform(RectTransform parent, string objectName, int depth)
        {
            if (parent == null || depth >= MaxHierarchyDepth)
            {
                return null;
            }

            for (int i = 0; i < parent.childCount; i++)
            {
                RectTransform child = parent.GetChild(i) as RectTransform;
                if (child == null)
                {
                    continue;
                }

                if (child.gameObject.name == objectName)
                {
                    return child;
                }

                RectTransform found = FindNamedRectTransform(child, objectName, depth + 1);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        internal static bool PinLayoutRootToCanvasCorner(
            RectTransform layoutRoot,
            RectTransform canvasRect,
            bool pinRight,
            bool pinTop)
        {
            if (layoutRoot == null || canvasRect == null)
            {
                return false;
            }

            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(canvasRect, layoutRoot);
            if (bounds.size.x < 1f || bounds.size.y < 1f)
            {
                return false;
            }

            Rect available = canvasRect.rect;
            float correctionX = pinRight
                ? available.xMax - bounds.max.x
                : available.xMin - bounds.min.x;
            float correctionY = pinTop
                ? available.yMax - bounds.max.y
                : available.yMin - bounds.min.y;

            if (Mathf.Approximately(correctionX, 0f) && Mathf.Approximately(correctionY, 0f))
            {
                return false;
            }

            Vector3 worldCorrection = canvasRect.TransformVector(new Vector3(correctionX, correctionY, 0f));
            layoutRoot.position += worldCorrection;
            return true;
        }

        private static bool HasFullStretchAnchors(RectTransform rect)
        {
            if (rect == null)
            {
                return false;
            }

            Vector2 span = rect.anchorMax - rect.anchorMin;
            return span.x >= FullAnchorThreshold && span.y >= FullAnchorThreshold;
        }

        private static bool RectCoversReferenceScreen(RectTransform rect, Vector2 referenceResolution)
        {
            if (rect == null || referenceResolution.x <= 0f || referenceResolution.y <= 0f)
            {
                return false;
            }

            Vector2 size = rect.rect.size;
            float coverageX = Mathf.Abs(size.x * rect.localScale.x) / referenceResolution.x;
            float coverageY = Mathf.Abs(size.y * rect.localScale.y) / referenceResolution.y;
            return coverageX >= ScreenCoverageThreshold && coverageY >= ScreenCoverageThreshold;
        }
    }
}
