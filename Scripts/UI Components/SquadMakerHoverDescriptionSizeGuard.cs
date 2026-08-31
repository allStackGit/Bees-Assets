using Assets.Scripts.Scenes;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Keeps the START/TEST hover descriptions compact before SquadMakerInteractionGuard moves and
    /// positions them in its root-canvas overlay.
    ///
    /// The authored scene stores these descriptions in the Chosen Squads layout hierarchy. Their live
    /// RectTransforms can therefore inherit a structural row height that is appropriate for the column
    /// but wildly too tall for a tooltip. The description roots also carry an authored LayoutGroup that
    /// was useful in the structural column but must not keep driving their TMP child after the roots are
    /// converted to hover overlays. This guard owns the compact description size and content bounds.
    /// </summary>
    [DefaultExecutionOrder(-675)]
    public sealed class SquadMakerHoverDescriptionSizeGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const float MinimumWidth = 160f;
        private const float MaximumWidth = 320f;
        private const float MinimumHeight = 24f;
        private const float MaximumHeight = 120f;
        private const float HorizontalPadding = 16f;
        private const float VerticalPadding = 10f;
        private const float CanvasMargin = 8f;
        private const float SizeTolerance = 0.01f;

        private SquadMaker _squadMaker;
        private RectTransform _rootCanvasRect;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            if (scene.name != SquadMakerSceneName)
            {
                return;
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                SquadMaker squadMaker = root.GetComponentInChildren<SquadMaker>(true);
                if (squadMaker == null)
                {
                    continue;
                }

                SquadMakerHoverDescriptionSizeGuard guard =
                    squadMaker.GetComponent<SquadMakerHoverDescriptionSizeGuard>();
                if (guard == null)
                {
                    guard = squadMaker.gameObject.AddComponent<SquadMakerHoverDescriptionSizeGuard>();
                }

                guard.Initialize(squadMaker);
                return;
            }
        }

        private void Awake()
        {
            if (_squadMaker == null)
            {
                Initialize(GetComponent<SquadMaker>());
            }
        }

        private void Initialize(SquadMaker squadMaker)
        {
            if (squadMaker == null)
            {
                return;
            }

            _squadMaker = squadMaker;
            ResolveRootCanvas();
        }

        private void LateUpdate()
        {
            if (_squadMaker == null)
            {
                return;
            }

            if (_rootCanvasRect == null)
            {
                ResolveRootCanvas();
            }

            NormalizeDescription(_squadMaker.StartText);
            NormalizeDescription(_squadMaker.TestText);
        }

        private void ResolveRootCanvas()
        {
            Canvas canvas = null;
            if (_squadMaker != null && _squadMaker.ChosenSquadList != null)
            {
                canvas = _squadMaker.ChosenSquadList.GetComponentInParent<Canvas>();
            }
            if (canvas == null && _squadMaker != null)
            {
                canvas = _squadMaker.GetComponentInParent<Canvas>();
            }

            Canvas rootCanvas = canvas != null ? canvas.rootCanvas : null;
            _rootCanvasRect = rootCanvas != null ? rootCanvas.transform as RectTransform : null;
        }

        private void NormalizeDescription(GameObject descriptionObject)
        {
            RectTransform description = descriptionObject != null
                ? descriptionObject.transform as RectTransform
                : null;
            if (description == null)
            {
                return;
            }

            // The Chosen Squads column owns the description root as a structural child only until the
            // hover system takes over. Keep that root out of parent layout measurement, then disable the
            // root's own authored LayoutGroup so it cannot rewrite the TMP rect after we normalize it.
            LayoutElement layoutElement = description.GetComponent<LayoutElement>();
            if (layoutElement == null)
            {
                layoutElement = description.gameObject.AddComponent<LayoutElement>();
            }
            layoutElement.ignoreLayout = true;
            DisableDescriptionLayoutWriters(description);

            TMP_Text text = description.GetComponentInChildren<TMP_Text>(true);
            Vector2 targetSize = CalculateDescriptionSize(text, _rootCanvasRect);
            if (targetSize.x <= SizeTolerance || targetSize.y <= SizeTolerance)
            {
                return;
            }

            if (Mathf.Abs(description.rect.width - targetSize.x) > SizeTolerance)
            {
                description.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetSize.x);
            }
            if (Mathf.Abs(description.rect.height - targetSize.y) > SizeTolerance)
            {
                description.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, targetSize.y);
            }

            // In the tracked scene the TMP object is the direct child of Start Text/Test Text. Once the
            // obsolete root layout writer is disabled, this stretched padded rect remains the actual
            // rendered text boundary that InteractionGuard can safely clamp by the outer description.
            NormalizeContentRect(description, text != null ? text.rectTransform : null);
        }

        internal static void DisableDescriptionLayoutWriters(RectTransform description)
        {
            if (description == null)
            {
                return;
            }

            LayoutGroup[] layoutGroups = description.GetComponents<LayoutGroup>();
            for (int index = 0; index < layoutGroups.Length; index++)
            {
                if (layoutGroups[index] != null)
                {
                    layoutGroups[index].enabled = false;
                }
            }

            ContentSizeFitter[] fitters = description.GetComponents<ContentSizeFitter>();
            for (int index = 0; index < fitters.Length; index++)
            {
                if (fitters[index] != null)
                {
                    fitters[index].enabled = false;
                }
            }
        }

        private static Vector2 CalculateDescriptionSize(TMP_Text text, RectTransform rootCanvas)
        {
            if (text == null || string.IsNullOrWhiteSpace(text.text))
            {
                return Vector2.zero;
            }

            float canvasWidth = rootCanvas != null
                ? Mathf.Abs(rootCanvas.rect.width)
                : MaximumWidth + CanvasMargin * 2f;
            float availableWidth = Mathf.Max(1f, canvasWidth - CanvasMargin * 2f);
            float maximumOuterWidth = Mathf.Min(MaximumWidth, availableWidth);

            Vector2 unconstrained = text.GetPreferredValues(text.text, 0f, 0f);
            float outerWidth = CalculateCompactWidth(
                unconstrained.x,
                maximumOuterWidth,
                MinimumWidth,
                HorizontalPadding);
            float textWidth = Mathf.Max(1f, outerWidth - HorizontalPadding);
            float wrappedTextHeight = text.GetPreferredValues(text.text, textWidth, 0f).y;
            float outerHeight = CalculateCompactHeight(
                wrappedTextHeight,
                MinimumHeight,
                MaximumHeight,
                VerticalPadding);

            return new Vector2(outerWidth, outerHeight);
        }

        internal static void NormalizeContentRect(
            RectTransform description,
            RectTransform content,
            float horizontalPadding = HorizontalPadding,
            float verticalPadding = VerticalPadding)
        {
            if (description == null || content == null || content == description ||
                !content.IsChildOf(description))
            {
                return;
            }

            float horizontalInset = Mathf.Max(0f, horizontalPadding) * 0.5f;
            float verticalInset = Mathf.Max(0f, verticalPadding) * 0.5f;
            content.anchorMin = Vector2.zero;
            content.anchorMax = Vector2.one;
            content.pivot = new Vector2(0.5f, 0.5f);
            content.offsetMin = new Vector2(horizontalInset, verticalInset);
            content.offsetMax = new Vector2(-horizontalInset, -verticalInset);
            content.localScale = Vector3.one;
        }

        internal static float CalculateCompactWidth(
            float preferredTextWidth,
            float availableMaximumWidth,
            float minimumWidth = MinimumWidth,
            float horizontalPadding = HorizontalPadding)
        {
            float maximumWidth = Mathf.Max(1f, availableMaximumWidth);
            float requestedWidth = Mathf.Max(0f, preferredTextWidth) + Mathf.Max(0f, horizontalPadding);
            float effectiveMinimum = Mathf.Min(Mathf.Max(1f, minimumWidth), maximumWidth);
            return Mathf.Clamp(requestedWidth, effectiveMinimum, maximumWidth);
        }

        internal static float CalculateCompactHeight(
            float preferredWrappedTextHeight,
            float minimumHeight = MinimumHeight,
            float maximumHeight = MaximumHeight,
            float verticalPadding = VerticalPadding)
        {
            float effectiveMinimum = Mathf.Max(1f, minimumHeight);
            float effectiveMaximum = Mathf.Max(effectiveMinimum, maximumHeight);
            float requestedHeight = Mathf.Max(0f, preferredWrappedTextHeight) + Mathf.Max(0f, verticalPadding);
            return Mathf.Clamp(requestedHeight, effectiveMinimum, effectiveMaximum);
        }
    }
}
