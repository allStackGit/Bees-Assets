using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Preserves the Main Menu's authored presentation as one visual unit. The MainPanel owns all
    /// menu controls, while the Bees Logo is an authored root-canvas sibling. Scaling or centering
    /// only the panel detaches the logo on tall displays and leaves the panel's fixed-size children
    /// too large on short ultrawide canvases. This late pass keeps the reference geometry intact and
    /// uniformly scales both pieces when the live canvas is shorter or narrower than the authored
    /// presentation envelope.
    /// </summary>
    [DefaultExecutionOrder(-700)]
    public sealed class MainMenuResponsivePresentationGuard : MonoBehaviour
    {
        private const string MainMenuSceneName = "Main Menu";
        private const string MainPanelName = "MainPanel";
        private const string BeesLogoName = "Bees Logo";

        // Authored Main Menu scene geometry at the 1366x768 reference resolution.
        private static readonly Vector2 MainPanelReferenceSize = new Vector2(1366f, 668f);
        private static readonly Vector2 BeesLogoReferenceSize = new Vector2(197f, 52f);
        private const float LogoCenterAbovePanelCenter = 319f;
        private const float PresentationHalfHeight = 345f;

        private RectTransform _canvasRect;
        private RectTransform _mainPanel;
        private RectTransform _beesLogo;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!string.Equals(scene.name, MainMenuSceneName, StringComparison.Ordinal))
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

                    MainMenuResponsivePresentationGuard guard =
                        canvas.GetComponent<MainMenuResponsivePresentationGuard>();
                    if (guard == null)
                    {
                        guard = canvas.gameObject.AddComponent<MainMenuResponsivePresentationGuard>();
                    }

                    guard.Initialize(canvas);
                }
            }
        }

        private void Initialize(Canvas canvas)
        {
            _canvasRect = canvas != null ? canvas.transform as RectTransform : null;
            _mainPanel = FindDirectChild(_canvasRect, MainPanelName);
            _beesLogo = FindDirectChild(_canvasRect, BeesLogoName);
            ApplyPresentationLayout(_canvasRect, _mainPanel, _beesLogo);
        }

        private void LateUpdate()
        {
            if (_canvasRect == null || _mainPanel == null || _beesLogo == null)
            {
                return;
            }

            // LegacyScreenResponsiveLayoutGuard runs earlier and may restore its older container-size
            // repair on a periodic pass. Re-checking these two cached RectTransforms is intentionally
            // cheap and guarantees this semantic Main Menu owner is the final layout authority.
            if (ApplyPresentationLayout(_canvasRect, _mainPanel, _beesLogo))
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_mainPanel);
            }
        }

        internal static bool ApplyPresentationLayout(
            RectTransform canvasRect,
            RectTransform mainPanel,
            RectTransform beesLogo)
        {
            if (canvasRect == null || mainPanel == null || beesLogo == null)
            {
                return false;
            }

            Vector2 canvasSize = canvasRect.rect.size;
            if (canvasSize.x <= 0f || canvasSize.y <= 0f)
            {
                return false;
            }

            float widthScale = canvasSize.x / MainPanelReferenceSize.x;
            float heightScale = canvasSize.y / (PresentationHalfHeight * 2f);
            float scale = Mathf.Min(1f, Mathf.Min(widthScale, heightScale));
            if (scale <= 0f)
            {
                return false;
            }

            Vector2 center = new Vector2(0.5f, 0.5f);
            Vector3 uniformScale = new Vector3(scale, scale, 1f);
            Vector2 logoPosition = new Vector2(0f, LogoCenterAbovePanelCenter * scale);

            bool changed =
                !Approximately(mainPanel.anchorMin, center) ||
                !Approximately(mainPanel.anchorMax, center) ||
                !Approximately(mainPanel.pivot, center) ||
                !Approximately(mainPanel.anchoredPosition, Vector2.zero) ||
                !Approximately(mainPanel.sizeDelta, MainPanelReferenceSize) ||
                !Approximately(mainPanel.localScale, uniformScale) ||
                !Approximately(beesLogo.anchorMin, center) ||
                !Approximately(beesLogo.anchorMax, center) ||
                !Approximately(beesLogo.pivot, center) ||
                !Approximately(beesLogo.anchoredPosition, logoPosition) ||
                !Approximately(beesLogo.sizeDelta, BeesLogoReferenceSize) ||
                !Approximately(beesLogo.localScale, uniformScale);

            mainPanel.anchorMin = center;
            mainPanel.anchorMax = center;
            mainPanel.pivot = center;
            mainPanel.anchoredPosition = Vector2.zero;
            mainPanel.sizeDelta = MainPanelReferenceSize;
            mainPanel.localScale = uniformScale;

            beesLogo.anchorMin = center;
            beesLogo.anchorMax = center;
            beesLogo.pivot = center;
            beesLogo.anchoredPosition = logoPosition;
            beesLogo.sizeDelta = BeesLogoReferenceSize;
            beesLogo.localScale = uniformScale;

            return changed;
        }

        private static RectTransform FindDirectChild(RectTransform parent, string childName)
        {
            if (parent == null || string.IsNullOrEmpty(childName))
            {
                return null;
            }

            for (int i = 0; i < parent.childCount; i++)
            {
                RectTransform child = parent.GetChild(i) as RectTransform;
                if (child != null && string.Equals(child.gameObject.name, childName, StringComparison.Ordinal))
                {
                    return child;
                }
            }

            return null;
        }

        private static bool Approximately(Vector2 left, Vector2 right)
        {
            return Mathf.Abs(left.x - right.x) <= 0.01f &&
                   Mathf.Abs(left.y - right.y) <= 0.01f;
        }

        private static bool Approximately(Vector3 left, Vector3 right)
        {
            return Mathf.Abs(left.x - right.x) <= 0.001f &&
                   Mathf.Abs(left.y - right.y) <= 0.001f &&
                   Mathf.Abs(left.z - right.z) <= 0.001f;
        }
    }
}
