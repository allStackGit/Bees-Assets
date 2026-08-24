using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Keeps the legacy Main Menu and Squad Maker composed against the 1366x768 artboard they were
    /// authored for. These screens are not fluid dashboards: stretching their internal layout groups
    /// changes relative proportions, opens filler regions and separates controls. CanvasScaler.Expand
    /// provides uniform physical scaling, while this guard maps only the root presentation anchors
    /// into a centered 1366x768 virtual frame and leaves the authored child geometry intact.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class LegacyScreenResponsiveLayoutGuard : MonoBehaviour
    {
        private const string MainMenuSceneName = "Main Menu";
        private const string SquadMakerSceneName = "Squad Maker";
        private const float RepairInterval = 0.25f;
        private const float AnchorTolerance = 0.001f;
        private static readonly Vector2 ReferenceResolution = new Vector2(1366f, 768f);

        private sealed class RectGeometry
        {
            public RectTransform Rect;
            public Vector2 AnchorMin;
            public Vector2 AnchorMax;
            public Vector2 Pivot;
            public Vector2 AnchoredPosition;
            public Vector2 SizeDelta;
            public Vector3 LocalScale;
        }

        private sealed class PresentationBranch
        {
            public RectGeometry Root;
            public readonly List<RectGeometry> Descendants = new List<RectGeometry>();
            public bool IsBackdrop;
        }

        private Canvas _canvas;
        private CanvasScaler _scaler;
        private RectTransform _canvasRect;
        private string _sceneName;
        private readonly List<PresentationBranch> _branches = new List<PresentationBranch>();
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;
        private float _nextRepairTime;
        private bool _restoredAfterCompetingInitialization;

        // Subscribe before the generic BeforeSceneLoad installers. This lets us snapshot the actual
        // authored RectTransform data before any compatibility guard has a chance to rewrite it.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!IsFixedReferencePresentationScene(scene.name))
            {
                return;
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Canvas[] canvases = root.GetComponentsInChildren<Canvas>(true);
                for (int i = 0; i < canvases.Length; i++)
                {
                    Canvas canvas = canvases[i];
                    if (canvas == null || !canvas.isRootCanvas || canvas.renderMode == RenderMode.WorldSpace)
                    {
                        continue;
                    }

                    LegacyScreenResponsiveLayoutGuard guard =
                        canvas.GetComponent<LegacyScreenResponsiveLayoutGuard>();
                    if (guard == null)
                    {
                        guard = canvas.gameObject.AddComponent<LegacyScreenResponsiveLayoutGuard>();
                    }

                    guard.Initialize(canvas, scene.name);
                }
            }
        }

        internal static bool IsFixedReferencePresentationScene(string sceneName)
        {
            return string.Equals(sceneName, MainMenuSceneName, StringComparison.Ordinal) ||
                   string.Equals(sceneName, SquadMakerSceneName, StringComparison.Ordinal);
        }

        private void Initialize(Canvas canvas, string sceneName)
        {
            _canvas = canvas;
            _canvasRect = canvas != null ? canvas.transform as RectTransform : null;
            _scaler = canvas != null ? canvas.GetComponent<CanvasScaler>() : null;
            if (_canvas != null && _scaler == null)
            {
                _scaler = _canvas.gameObject.AddComponent<CanvasScaler>();
            }

            _sceneName = sceneName;
            ConfigureScaler();
            CaptureAuthoredBranches();
            _lastScreenWidth = Screen.width;
            _lastScreenHeight = Screen.height;
            _restoredAfterCompetingInitialization = false;
            ApplyReferencePresentation(restoreDescendants: false);
        }

        private void Update()
        {
            if (_canvas == null || _canvasRect == null)
            {
                return;
            }

            DisableCompetingGeometryGuards();
            ConfigureScaler();

            bool displayChanged = Screen.width != _lastScreenWidth || Screen.height != _lastScreenHeight;
            if (displayChanged)
            {
                _lastScreenWidth = Screen.width;
                _lastScreenHeight = Screen.height;
                ConfigData.ScreenWidth = Screen.width;
                ConfigData.ScreenHeight = Screen.height;
            }

            bool firstStableFrame = !_restoredAfterCompetingInitialization;
            if (!firstStableFrame && !displayChanged && Time.unscaledTime < _nextRepairTime)
            {
                return;
            }

            _nextRepairTime = Time.unscaledTime + RepairInterval;
            CaptureNewDirectBranches();
            ApplyReferencePresentation(firstStableFrame);
            _restoredAfterCompetingInitialization = true;
        }

        private void ConfigureScaler()
        {
            if (_scaler == null)
            {
                return;
            }

            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _scaler.referenceResolution = ReferenceResolution;
            _scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        }

        private void DisableCompetingGeometryGuards()
        {
            ResponsiveScreenLayoutGuard responsive = _canvas.GetComponent<ResponsiveScreenLayoutGuard>();
            if (responsive != null && responsive.enabled)
            {
                responsive.enabled = false;
            }

            RootCanvasCompatibilityGuard compatibility = _canvas.GetComponent<RootCanvasCompatibilityGuard>();
            if (compatibility != null && compatibility.enabled)
            {
                compatibility.enabled = false;
            }
        }

        private void CaptureAuthoredBranches()
        {
            _branches.Clear();
            CaptureNewDirectBranches();
        }

        private void CaptureNewDirectBranches()
        {
            if (_canvasRect == null)
            {
                return;
            }

            for (int i = 0; i < _canvasRect.childCount; i++)
            {
                RectTransform child = _canvasRect.GetChild(i) as RectTransform;
                if (child == null || IsAlreadyCaptured(child))
                {
                    continue;
                }

                PresentationBranch branch = new PresentationBranch
                {
                    Root = CaptureGeometry(child),
                    IsBackdrop = IsViewportBackdrop(child)
                };
                CaptureDescendants(child, branch.Descendants);
                _branches.Add(branch);
            }
        }

        private bool IsAlreadyCaptured(RectTransform rect)
        {
            for (int i = 0; i < _branches.Count; i++)
            {
                if (_branches[i].Root.Rect == rect)
                {
                    return true;
                }
            }

            return false;
        }

        private static RectGeometry CaptureGeometry(RectTransform rect)
        {
            return new RectGeometry
            {
                Rect = rect,
                AnchorMin = rect.anchorMin,
                AnchorMax = rect.anchorMax,
                Pivot = rect.pivot,
                AnchoredPosition = rect.anchoredPosition,
                SizeDelta = rect.sizeDelta,
                LocalScale = rect.localScale
            };
        }

        private static void CaptureDescendants(RectTransform parent, List<RectGeometry> destination)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                RectTransform child = parent.GetChild(i) as RectTransform;
                if (child == null)
                {
                    continue;
                }

                destination.Add(CaptureGeometry(child));
                CaptureDescendants(child, destination);
            }
        }

        private static bool IsViewportBackdrop(RectTransform branch)
        {
            if (branch == null || branch.GetComponentInChildren<Selectable>(true) != null ||
                branch.GetComponentInChildren<LayoutGroup>(true) != null)
            {
                return false;
            }

            string objectName = branch.gameObject.name;
            bool namedBackdrop = objectName.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 objectName.IndexOf("backer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 objectName.IndexOf("starfield", StringComparison.OrdinalIgnoreCase) >= 0;
            bool fullStretch = Approximately(branch.anchorMin, Vector2.zero) &&
                               Approximately(branch.anchorMax, Vector2.one);
            return namedBackdrop || fullStretch;
        }

        private void ApplyReferencePresentation(bool restoreDescendants)
        {
            if (_canvasRect == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            Vector2 canvasSize = _canvasRect.rect.size;
            if (canvasSize.x <= 0f || canvasSize.y <= 0f)
            {
                return;
            }

            for (int i = 0; i < _branches.Count; i++)
            {
                PresentationBranch branch = _branches[i];
                if (branch.Root.Rect == null)
                {
                    continue;
                }

                if (restoreDescendants)
                {
                    for (int j = 0; j < branch.Descendants.Count; j++)
                    {
                        RestoreGeometry(branch.Descendants[j]);
                    }
                }

                if (branch.IsBackdrop)
                {
                    StretchBackdrop(branch.Root.Rect);
                }
                else
                {
                    ApplyReferenceRootGeometry(branch.Root, canvasSize);
                }

                if (branch.Root.Rect.GetComponent<LayoutGroup>() != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(branch.Root.Rect);
                }
            }

            Canvas.ForceUpdateCanvases();
        }

        private static void RestoreGeometry(RectGeometry geometry)
        {
            if (geometry == null || geometry.Rect == null)
            {
                return;
            }

            geometry.Rect.anchorMin = geometry.AnchorMin;
            geometry.Rect.anchorMax = geometry.AnchorMax;
            geometry.Rect.pivot = geometry.Pivot;
            geometry.Rect.anchoredPosition = geometry.AnchoredPosition;
            geometry.Rect.sizeDelta = geometry.SizeDelta;
            geometry.Rect.localScale = geometry.LocalScale;
        }

        private static void ApplyReferenceRootGeometry(RectGeometry geometry, Vector2 canvasSize)
        {
            if (geometry == null || geometry.Rect == null)
            {
                return;
            }

            geometry.Rect.anchorMin = MapReferenceAnchor(geometry.AnchorMin, canvasSize);
            geometry.Rect.anchorMax = MapReferenceAnchor(geometry.AnchorMax, canvasSize);
            geometry.Rect.pivot = geometry.Pivot;
            geometry.Rect.anchoredPosition = geometry.AnchoredPosition;
            geometry.Rect.sizeDelta = geometry.SizeDelta;
            geometry.Rect.localScale = geometry.LocalScale;
        }

        internal static Vector2 MapReferenceAnchor(Vector2 authoredAnchor, Vector2 canvasSize)
        {
            if (canvasSize.x <= 0f || canvasSize.y <= 0f)
            {
                return authoredAnchor;
            }

            Vector2 referenceOrigin = (canvasSize - ReferenceResolution) * 0.5f;
            return new Vector2(
                (referenceOrigin.x + authoredAnchor.x * ReferenceResolution.x) / canvasSize.x,
                (referenceOrigin.y + authoredAnchor.y * ReferenceResolution.y) / canvasSize.y);
        }

        internal static bool ApplyReferenceGeometryForTest(
            RectTransform rect,
            Vector2 canvasSize,
            Vector2 authoredAnchorMin,
            Vector2 authoredAnchorMax,
            Vector2 authoredPosition,
            Vector2 authoredSize)
        {
            if (rect == null)
            {
                return false;
            }

            Vector2 mappedMin = MapReferenceAnchor(authoredAnchorMin, canvasSize);
            Vector2 mappedMax = MapReferenceAnchor(authoredAnchorMax, canvasSize);
            bool changed = !Approximately(rect.anchorMin, mappedMin) ||
                           !Approximately(rect.anchorMax, mappedMax) ||
                           !Approximately(rect.anchoredPosition, authoredPosition) ||
                           !Approximately(rect.sizeDelta, authoredSize) ||
                           !Approximately(rect.localScale, Vector3.one);

            rect.anchorMin = mappedMin;
            rect.anchorMax = mappedMax;
            rect.anchoredPosition = authoredPosition;
            rect.sizeDelta = authoredSize;
            rect.localScale = Vector3.one;
            return changed;
        }

        internal static bool StretchBackdrop(RectTransform rect)
        {
            if (rect == null)
            {
                return false;
            }

            bool changed = !Approximately(rect.anchorMin, Vector2.zero) ||
                           !Approximately(rect.anchorMax, Vector2.one) ||
                           !Approximately(rect.offsetMin, Vector2.zero) ||
                           !Approximately(rect.offsetMax, Vector2.zero);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return changed;
        }

        private static bool Approximately(Vector2 left, Vector2 right)
        {
            return Mathf.Abs(left.x - right.x) <= AnchorTolerance &&
                   Mathf.Abs(left.y - right.y) <= AnchorTolerance;
        }

        private static bool Approximately(Vector3 left, Vector3 right)
        {
            return Mathf.Abs(left.x - right.x) <= AnchorTolerance &&
                   Mathf.Abs(left.y - right.y) <= AnchorTolerance &&
                   Mathf.Abs(left.z - right.z) <= AnchorTolerance;
        }
    }
}
