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
    /// RectTransforms can therefore inherit structural row geometry that is appropriate for the column
    /// but wildly wrong for a tooltip. This guard becomes the single owner of hover-description size
    /// and content bounds: it removes obsolete root layout writers, sizes from rendered TMP content,
    /// normalizes any intermediate wrappers, and leaves InteractionGuard to clamp the finished outer rect.
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

            // Until SquadMakerInteractionGuard reparents the description, keep it out of the native
            // Chosen Squads layout so this compact tooltip size cannot alter structural row budgeting.
            LayoutElement layoutElement = description.GetComponent<LayoutElement>();
            if (layoutElement == null)
            {
                layoutElement = description.gameObject.AddComponent<LayoutElement>();
            }
            layoutElement.ignoreLayout = true;

            // Start Text/Test Text carry authored layout writers from their former structural role.
            // Once these objects are hover overlays those writers have no semantic ownership and can
            // otherwise rewrite the canonical tooltip/content geometry after this guard normalizes it.
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

            // InteractionGuard clamps the outer description rect to the root-canvas edge. The real
            // scene may place TMP under one or more authored wrapper RectTransforms; normalizing only
            // the TMP rect leaves any narrow wrapper authoritative and can collapse the text to one
            // character per line. Normalize the entire parent chain to the tooltip bounds, then apply
            // the intended padding only to the final TMP rect.
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

            RectTransform wrapper = content.parent as RectTransform;
            while (wrapper != null && wrapper != description)
            {
                StretchToParent(wrapper, Vector2.zero, Vector2.zero);
                wrapper = wrapper.parent as RectTransform;
            }

            float horizontalInset = Mathf.Max(0f, horizontalPadding) * 0.5f;
            float verticalInset = Mathf.Max(0f, verticalPadding) * 0.5f;
            StretchToParent(
                content,
                new Vector2(horizontalInset, verticalInset),
                new Vector2(-horizontalInset, -verticalInset));
        }

        private static void StretchToParent(
            RectTransform rect,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            if (rect == null)
            {
                return;
            }

            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            rect.localScale = Vector3.one;
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
