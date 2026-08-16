using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UIComponents
{
    /// <summary>
    /// Keeps the derived carrier-deck artwork synchronized on Squad Maker UI surfaces that are
    /// separate from the draggable formation icons: saved squads, chosen squads, and squad info.
    /// The deck remains derived from SavedSquad.Color and is never persisted separately.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class SquadMakerCarrierDeckPresenter : MonoBehaviour
    {
        private SquadMaker _squadMaker;
        private Transform _queuedInfoLabel;

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
                SquadMaker[] squadMakers = root.GetComponentsInChildren<SquadMaker>(true);
                for (int i = 0; i < squadMakers.Length; i++)
                {
                    EnsurePresenter(squadMakers[i]);
                }
            }
        }

        private static void EnsurePresenter(SquadMaker squadMaker)
        {
            if (squadMaker == null || squadMaker.GetComponent<SquadMakerCarrierDeckPresenter>() != null)
            {
                return;
            }

            squadMaker.gameObject.AddComponent<SquadMakerCarrierDeckPresenter>();
        }

        private void Awake()
        {
            _squadMaker = GetComponent<SquadMaker>();
            EnsureListObservers();
        }

        private void Start()
        {
            EnsureListObservers();
        }

        private void EnsureListObservers()
        {
            if (_squadMaker == null)
            {
                return;
            }

            AttachListObserver(_squadMaker.SavedSquadList);
            AttachListObserver(_squadMaker.ChosenSquadList);
        }

        private void AttachListObserver(GameObject listRoot)
        {
            if (listRoot == null)
            {
                return;
            }

            SquadMakerCarrierDeckListObserver observer = listRoot.GetComponent<SquadMakerCarrierDeckListObserver>();
            if (observer == null)
            {
                observer = listRoot.AddComponent<SquadMakerCarrierDeckListObserver>();
            }
            observer.Initialize(this);
        }

        internal bool IsSquadLabel(Transform label)
        {
            if (label == null)
            {
                return false;
            }

            string labelName = label.gameObject.name;
            return (labelName.StartsWith("Saved Squad - ") || labelName.StartsWith("Chosen Squad - ")) &&
                   labelName.LastIndexOf('#') >= 0;
        }

        internal void RefreshLabel(Transform label)
        {
            if (!TryGetSquad(label, out SavedSquad squad))
            {
                return;
            }

            Transform iconTransform = label.Find("Icon Container/Ship Icon");
            Image icon = iconTransform != null ? iconTransform.GetComponent<Image>() : null;
            ApplySquadDeckVariant(icon, squad);
        }

        internal void QueueSquadInfo(Transform label)
        {
            if (IsSquadLabel(label))
            {
                _queuedInfoLabel = label;
            }
        }

        private void LateUpdate()
        {
            if (_queuedInfoLabel == null || _squadMaker == null || _squadMaker.SquadInfoBox == null ||
                !_squadMaker.SquadInfoBox.activeInHierarchy)
            {
                return;
            }

            if (!TryGetSquad(_queuedInfoLabel, out SavedSquad squad))
            {
                _queuedInfoLabel = null;
                return;
            }

            Image infoIcon = _squadMaker.SquadInfoBoxIcon != null
                ? _squadMaker.SquadInfoBoxIcon.GetComponent<Image>()
                : null;
            ApplySquadDeckVariant(infoIcon, squad);
            _queuedInfoLabel = null;
        }

        private static void ApplySquadDeckVariant(Image image, SavedSquad squad)
        {
            if (squad == null || !squad.HasShips)
            {
                CarrierDeckVariants.SetUiDeckVariant(image, null);
                return;
            }

            SquadShip representativeShip = squad.GetMostValuableShip();
            ApplyDeckVariant(
                image,
                representativeShip != null && representativeShip.ShipType == ConfigData.ShipTypes.Carrier,
                squad.HasCustomColor,
                squad.Color);
        }

        internal static void ApplyDeckVariant(Image image, bool isCarrier, bool hasCustomColor, Color color)
        {
            Sprite deckSprite = isCarrier && hasCustomColor
                ? CarrierDeckVariants.GetDeckSprite(color)
                : null;
            CarrierDeckVariants.SetUiDeckVariant(image, deckSprite);
        }

        private static bool TryGetSquad(Transform label, out SavedSquad squad)
        {
            squad = null;
            if (label == null || ConfigData.CurrentShips == null)
            {
                return false;
            }

            string labelName = label.gameObject.name;
            int hashIndex = labelName.LastIndexOf('#');
            if (hashIndex < 0 || !long.TryParse(labelName.Substring(hashIndex + 1), out long squadId))
            {
                return false;
            }

            squad = ConfigData.CurrentShips.GetSavedSquad(squadId);
            return squad != null;
        }
    }

    internal sealed class SquadMakerCarrierDeckListObserver : MonoBehaviour
    {
        private SquadMakerCarrierDeckPresenter _presenter;

        internal void Initialize(SquadMakerCarrierDeckPresenter presenter)
        {
            _presenter = presenter;
            RefreshChildren();
        }

        private void OnEnable()
        {
            RefreshChildren();
        }

        private void OnTransformChildrenChanged()
        {
            RefreshChildren();
        }

        private void RefreshChildren()
        {
            if (_presenter == null)
            {
                return;
            }

            for (int i = 0; i < transform.childCount; i++)
            {
                Transform label = transform.GetChild(i);
                if (!_presenter.IsSquadLabel(label))
                {
                    continue;
                }

                SquadMakerCarrierDeckLabelObserver observer = label.GetComponent<SquadMakerCarrierDeckLabelObserver>();
                if (observer == null)
                {
                    observer = label.gameObject.AddComponent<SquadMakerCarrierDeckLabelObserver>();
                }
                observer.Initialize(_presenter);
            }
        }
    }

    internal sealed class SquadMakerCarrierDeckLabelObserver : MonoBehaviour, IPointerEnterHandler
    {
        private SquadMakerCarrierDeckPresenter _presenter;

        internal void Initialize(SquadMakerCarrierDeckPresenter presenter)
        {
            _presenter = presenter;
            _presenter.RefreshLabel(transform);
        }

        private void OnEnable()
        {
            _presenter?.RefreshLabel(transform);
        }

        private void OnTransformChildrenChanged()
        {
            _presenter?.RefreshLabel(transform);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_presenter == null)
            {
                return;
            }

            _presenter.RefreshLabel(transform);
            _presenter.QueueSquadInfo(transform);
        }
    }
}
