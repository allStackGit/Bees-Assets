using Assets.Scripts.Scenes;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Keeps the Chosen Squads Supply Capacity text centered inside its background row.
    ///
    /// The warning background lives on the parent container while the serialized label/text keep
    /// their own RectTransform offsets. Those offsets are hard to notice against the normal dark
    /// background but become obvious when the row turns red for an over-capacity selection. The
    /// background owns the row bounds; the label and TMP text therefore fill those bounds and TMP
    /// owns only glyph alignment inside them.
    /// </summary>
    [DefaultExecutionOrder(-575)]
    public sealed class SquadMakerSupplyCapacityPresentationGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";
        private const float RepairInterval = 0.25f;

        private SquadMaker _squadMaker;
        private RectTransform _background;
        private RectTransform _label;
        private TMP_Text _text;
        private float _nextRepairTime;

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

                SquadMakerSupplyCapacityPresentationGuard guard =
                    squadMaker.GetComponent<SquadMakerSupplyCapacityPresentationGuard>();
                if (guard == null)
                {
                    guard = squadMaker.gameObject.AddComponent<SquadMakerSupplyCapacityPresentationGuard>();
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
            ResolvePresentation();
            ApplyPresentation();
            _nextRepairTime = 0f;
        }

        private void LateUpdate()
        {
            if (_squadMaker == null || Time.unscaledTime < _nextRepairTime)
            {
                return;
            }

            _nextRepairTime = Time.unscaledTime + RepairInterval;
            if (_background == null || _label == null || _text == null)
            {
                ResolvePresentation();
            }

            ApplyPresentation();
        }

        private void ResolvePresentation()
        {
            GameObject labelObject = _squadMaker != null
                ? _squadMaker.ChosenSquadsSupplyCapacityLabel
                : null;
            _label = labelObject != null ? labelObject.transform as RectTransform : null;
            _background = _label != null ? _label.parent as RectTransform : null;
            _text = labelObject != null ? labelObject.GetComponentInChildren<TMP_Text>(true) : null;
        }

        private void ApplyPresentation()
        {
            CenterPresentation(_background, _label, _text);
        }

        internal static void CenterPresentation(
            RectTransform background,
            RectTransform label,
            TMP_Text text)
        {
            if (background == null || label == null || text == null)
            {
                return;
            }

            StretchToParent(label);

            RectTransform textRect = text.rectTransform;
            if (textRect != null)
            {
                StretchToParent(textRect);
            }

            text.alignment = TextAlignmentOptions.Center;
            text.margin = Vector4.zero;
        }

        private static void StretchToParent(RectTransform rect)
        {
            if (rect == null || !(rect.parent is RectTransform))
            {
                return;
            }

            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            rect.localScale = Vector3.one;
        }
    }
}
