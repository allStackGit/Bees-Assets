using System.Collections;
using System.Collections.Generic;
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
        private List<GameObject> _activeArrows = new List<GameObject>();

        private void Awake()
        {
            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign || ConfigData.UserProgressData.GetCurrentLevel(ConfigData.Configuration.HumanSide) != 2 || !ConfigData.UserProgressData.ShowToolTips)
            {
                Destroy(gameObject);
                return;
            }
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
                
                new Dictionary<Vector2, Vector3>() { { new Vector2(-380, -5), new Vector3(0, 0, 90) }, { new Vector2(-430, 337), new Vector3(0, 0, 90) }, { new Vector2(80, 100), new Vector3(0, 0, 180) } },

            };
        }
        void Start()
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
                // Optionally hide or close the brochure
                gameObject.SetActive(false);
            }
        }

        private void ShowPage(int pageIndex)
        {
            _currentPage = pageIndex;
            BrochureText.text = Pages[pageIndex];

            // Remove previous arrows
            foreach (GameObject arrow in _activeArrows)
            {
                Destroy(arrow);
            }
            _activeArrows.Clear();

            // Show arrows for this page
            if (ArrowPositions.Count > pageIndex)
            {
                foreach (var pos in ArrowPositions[pageIndex])
                {
                    var arrow = Instantiate(ArrowPrefab, OverlayCanvas.transform);
                    var rect = arrow.GetComponent<RectTransform>();
                    rect.anchoredPosition = pos.Key;
                    rect.eulerAngles = pos.Value;
                    _activeArrows.Add(arrow);
                }
            }
        }
    }
}