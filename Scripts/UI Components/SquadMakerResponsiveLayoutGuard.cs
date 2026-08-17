using Assets.Scripts.Scenes;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Keeps the Squad Maker's hover-only START/TEST help text from changing the right column's
    /// measured layout. START and TEST can both be active, so their description objects remain
    /// available for hover visibility without reserving LayoutGroup rows.
    /// </summary>
    [DefaultExecutionOrder(-700)]
    public sealed class SquadMakerResponsiveLayoutGuard : MonoBehaviour
    {
        private const string SquadMakerSceneName = "Squad Maker";

        private SquadMaker _squadMaker;
        private SquadMakerHoverDescriptionRelay _startRelay;
        private SquadMakerHoverDescriptionRelay _testRelay;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
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

                SquadMakerResponsiveLayoutGuard guard =
                    squadMaker.GetComponent<SquadMakerResponsiveLayoutGuard>();
                if (guard == null)
                {
                    guard = squadMaker.gameObject.AddComponent<SquadMakerResponsiveLayoutGuard>();
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
            _squadMaker = squadMaker;
            if (_squadMaker == null)
            {
                return;
            }

            StabilizeHoverDescriptions();
        }

        private void LateUpdate()
        {
            // The authored pointer-exit callbacks still call SetActive(false). Restore the
            // description object before Unity's canvas/layout rebuild for this frame, while its
            // LayoutElement keeps it out of the measured column.
            StabilizeHoverDescriptions();
        }

        private void StabilizeHoverDescriptions()
        {
            if (_squadMaker == null)
            {
                return;
            }

            StabilizeDescription(
                _squadMaker.StartButton,
                _squadMaker.StartText,
                ref _startRelay);
            StabilizeDescription(
                _squadMaker.TestButton,
                _squadMaker.TestText,
                ref _testRelay);
        }

        private static void StabilizeDescription(
            GameObject button,
            GameObject description,
            ref SquadMakerHoverDescriptionRelay relay)
        {
            if (button == null || description == null)
            {
                return;
            }

            if (!button.activeSelf)
            {
                if (description.activeSelf)
                {
                    description.SetActive(false);
                }
                relay?.ResetHover();
                return;
            }

            if (relay == null || relay.gameObject != button)
            {
                relay = button.GetComponent<SquadMakerHoverDescriptionRelay>();
                if (relay == null)
                {
                    relay = button.AddComponent<SquadMakerHoverDescriptionRelay>();
                }
                relay.Configure(description);
            }

            SetDescriptionVisibility(description, relay.IsHovered);
        }

        internal static void SetDescriptionVisibility(GameObject description, bool visible)
        {
            if (description == null)
            {
                return;
            }

            // These are overlays, not structural rows. Keeping them active avoids hover-time
            // layout rebuilds; ignoring them in LayoutGroups prevents one or two invisible help
            // objects from consuming the space needed by the level title/details.
            LayoutElement layoutElement = description.GetComponent<LayoutElement>();
            if (layoutElement == null)
            {
                layoutElement = description.AddComponent<LayoutElement>();
            }
            layoutElement.ignoreLayout = true;

            if (!description.activeSelf)
            {
                description.SetActive(true);
            }

            CanvasGroup group = description.GetComponent<CanvasGroup>();
            if (group == null)
            {
                group = description.AddComponent<CanvasGroup>();
            }

            group.alpha = visible ? 1f : 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
        }
    }

    internal sealed class SquadMakerHoverDescriptionRelay : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler
    {
        private GameObject _description;

        internal bool IsHovered { get; private set; }

        internal void Configure(GameObject description)
        {
            _description = description;
            IsHovered = false;
            SquadMakerResponsiveLayoutGuard.SetDescriptionVisibility(_description, false);
        }

        internal void ResetHover()
        {
            IsHovered = false;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            IsHovered = true;
            SquadMakerResponsiveLayoutGuard.SetDescriptionVisibility(_description, true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            IsHovered = false;
            SquadMakerResponsiveLayoutGuard.SetDescriptionVisibility(_description, false);
        }
    }
}
