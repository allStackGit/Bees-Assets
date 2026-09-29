using System.Collections;
using System.Collections.Generic;
using Assets.Scripts.Scenes;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Assets.Scripts.UI_Components
{
    public class Brochure : MonoBehaviour
    {
        [Header("UI References")]
        public Canvas OverlayCanvas;
        public TMP_Text BrochureText;
        public Button NextPageButton;
        public GameObject ArrowPrefab;

        [Header("Brochure Content")]
        [TextArea(3, 10)]
        public List<string> Pages = new List<string>();
        public List<Dictionary<Vector2, Vector3>> ArrowPositions = new List<Dictionary<Vector2, Vector3>>();

        private int _currentPage = 0;
        private readonly List<GameObject> _activeArrows = new List<GameObject>();
        private SquadMaker _squadMaker;
        private RectTransform _brochurePanel;

        private void Awake()
        {
            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign || ConfigData.UserProgressData.GetCurrentLevel(ConfigData.Configuration.HumanSide) != 2 || !ConfigData.UserProgressData.ShowToolTips)
            {
                Destroy(gameObject);
                return;
            }

            _squadMaker = FindObjectOfType<SquadMaker>();
            ApplyTooltipPresentation();

            Pages = new List<string>
            {
                "This is where you create and view your squads. You'll come here before most levels to manage your squads and choose which ones to bring into the level.",
                "On the left you'll find a list of all the ships in your fleet that haven't been assigned to a squad. On the right is a list of all of your current squads. Ships in your squads that are lost in battle are automatically replenished from your fleet if there are more available.",
                "Each ship has a capacity value, and each level has a maximum capacity that limits how many ships you can bring into it. Hover over a ship type in the fleet list to check its capacity value.",
                "Click on a squad to add it to the level, and click on it again to remove it from the level.",
                "Double-click on a squad to edit it. Click or drag a ship from the fleet to add it to the squad, and use the BLARP buttons to set the formation. You can also customize the color of your squad and give it a name.",
                "Use the Squad Action Box to modify the squad's default actions and shooting strategy. You will also to have the ability to change these during the level. Good luck!"
            };

            ArrowPositions = new List<Dictionary<Vector2, Vector3>>
            {
                new Dictionary<Vector2, Vector3>(),
                new Dictionary<Vector2, Vector3>(),
                new Dictionary<Vector2, Vector3>() { { new Vector2(480, -300), new Vector3(0, 0, 230) }, { new Vector2(-320, 50), new Vector3(0, 0, 0) }  },
                new Dictionary<Vector2, Vector3>() { { new Vector2(190, 330), new Vector3(0, 0, 270) } },
                new Dictionary<Vector2, Vector3>() { { new Vector2(-380, -5), new Vector3(0, 0, 90) }, { new Vector2(-430, 337), new Vector3(0, 0, 90) } },
            };
        }

        private void Start()
        {
            ShowPage(0);
            NextPageButton.onClick.AddListener(ShowNextPage);
        }

        public void ShowNextPage()
        {
            int nextPage = _currentPage + 1;
            if (nextPage < Pages.Count)
            {
                ShowPage(nextPage);
            }
            else
            {
                gameObject.SetActive(false);
            }
        }

        private void ShowPage(int pageIndex)
        {
            _currentPage = pageIndex;
            BrochureText.text = Pages[pageIndex];

            foreach (GameObject arrow in _activeArrows)
            {
                Destroy(arrow);
            }
            _activeArrows.Clear();

            if (ArrowPositions.Count > pageIndex)
            {
                foreach (var pos in ArrowPositions[pageIndex])
                {
                    CreateArrow(pos.Key, pos.Value);
                }
            }

            // The final editing page's color callout must follow the actual responsive Color
            // button rather than a hard-coded canvas coordinate. The button moves substantially
            // between aspect ratios, which was why the old arrow missed it.
            if (pageIndex == 4)
            {
                AddColorButtonArrow();
            }
        }

        private void CreateArrow(Vector2 anchoredPosition, Vector3 rotation)
        {
            if (ArrowPrefab == null || OverlayCanvas == null)
            {
                return;
            }

            GameObject arrow = Instantiate(ArrowPrefab, OverlayCanvas.transform);
            RectTransform rect = arrow.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchoredPosition = anchoredPosition;
                rect.eulerAngles = rotation;
            }
            _activeArrows.Add(arrow);
        }

        private void AddColorButtonArrow()
        {
            if (_squadMaker == null || _squadMaker.SquadColorPickerButton == null || OverlayCanvas == null)
            {
                return;
            }

            RectTransform target = _squadMaker.SquadColorPickerButton.transform as RectTransform;
            RectTransform canvasRect = OverlayCanvas.transform as RectTransform;
            if (target == null || canvasRect == null)
            {
                return;
            }

            Vector3 targetWorldCenter = target.TransformPoint(target.rect.center);
            Camera eventCamera = OverlayCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : OverlayCanvas.worldCamera;
            Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(eventCamera, targetWorldCenter);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect,
                    screenPoint,
                    eventCamera,
                    out Vector2 targetCanvasPoint))
            {
                return;
            }

            float verticalOffset = Mathf.Max(42f, Mathf.Abs(target.rect.height) * 1.1f);
            CreateArrow(
                targetCanvasPoint + new Vector2(0f, verticalOffset),
                new Vector3(0f, 0f, 180f));
        }

        private void ApplyTooltipPresentation()
        {
            if (BrochureText == null)
            {
                return;
            }

            BrochureText.color = Color.white;
            BrochureText.enableWordWrapping = true;

            Image panelImage = FindLargestPanelImage();
            if (panelImage != null)
            {
                _brochurePanel = panelImage.rectTransform;
                panelImage.color = new Color(0.20f, 0.25f, 0.30f, 0.98f);
                Outline outline = panelImage.GetComponent<Outline>();
                if (outline == null)
                {
                    outline = panelImage.gameObject.AddComponent<Outline>();
                }
                outline.effectColor = new Color(0.52f, 0.60f, 0.66f, 1f);
                outline.effectDistance = new Vector2(2f, -2f);
                CreateInfoTab();
            }

            TMP_Text[] labels = GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                if (labels[i] != null)
                {
                    labels[i].color = Color.white;
                }
            }

            if (NextPageButton != null)
            {
                Image buttonImage = NextPageButton.GetComponent<Image>();
                if (buttonImage != null)
                {
                    buttonImage.color = new Color(0.28f, 0.33f, 0.38f, 0.98f);
                }
                TMP_Text buttonLabel = NextPageButton.GetComponentInChildren<TMP_Text>(true);
                if (buttonLabel != null)
                {
                    buttonLabel.color = Color.white;
                }
            }
        }

        private Image FindLargestPanelImage()
        {
            Image[] images = GetComponentsInChildren<Image>(true);
            Image largest = null;
            float largestArea = 0f;
            for (int i = 0; i < images.Length; i++)
            {
                Image image = images[i];
                if (image == null || (NextPageButton != null && image.gameObject == NextPageButton.gameObject))
                {
                    continue;
                }

                RectTransform rect = image.rectTransform;
                float area = Mathf.Abs(rect.rect.width * rect.rect.height);
                if (area > largestArea)
                {
                    largestArea = area;
                    largest = image;
                }
            }
            return largest;
        }

        private void CreateInfoTab()
        {
            if (_brochurePanel == null || _brochurePanel.Find("Info Tab") != null)
            {
                return;
            }

            GameObject tab = new GameObject(
                "Info Tab",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            tab.transform.SetParent(_brochurePanel, false);
            RectTransform tabRect = tab.GetComponent<RectTransform>();
            tabRect.anchorMin = new Vector2(0f, 1f);
            tabRect.anchorMax = new Vector2(0f, 1f);
            tabRect.pivot = new Vector2(0f, 0f);
            tabRect.anchoredPosition = new Vector2(0f, 2f);
            tabRect.sizeDelta = new Vector2(92f, 28f);
            tab.GetComponent<Image>().color = new Color(0.34f, 0.39f, 0.44f, 1f);

            GameObject labelObject = new GameObject(
                "Label",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            labelObject.transform.SetParent(tab.transform, false);
            RectTransform labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
            label.text = "INFO";
            label.font = BrochureText.font;
            label.fontSize = Mathf.Max(12f, BrochureText.fontSize * 0.8f);
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
        }
    }
}