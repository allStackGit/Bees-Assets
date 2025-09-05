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
            Pages = new List<string>
            {
                "The Squad Maker is where you create and view your squads and the ships in your fleet. You'll often come here before each level to choose the squads you want to bring into the level and manage your existing squads.",
                "On the left you can see a list of all the ships in your fleet that <i>aren't</i> in your squads. On the right you can see all your current squads. When you lose ships in your squads they will automatically be replenished from your fleet if there are more available.",
                "You can click once on a squad to choose it for the level or click twice to edit the squad. To remove a chosen squad from the level just click on it once. You can also hover over ships or squads to see their stats.",
                "Each ship has a capacity value, and each level has a maximum capacity that you can bring into the level. You can view a ship's capacity by hovering over it in the fleet list.",
                "When editing a squad you can click or drag a ship in the fleet to add it to the squad. The BLARP buttons can be used to set the formation of the squad. You can select a squad color as well.",
                "Finally, when you're editing a squad, the Squad Action Box you see in the game will appear where you can modify the default actions and shooting strategies of the squad. Good luck!"
            };

            ArrowPositions = new List<Dictionary<Vector2, Vector3>>
            {
                new Dictionary<Vector2, Vector3>(),
                new Dictionary<Vector2, Vector3>(),
                new Dictionary<Vector2, Vector3>() { { new Vector2(190, 330), new Vector3(0, 0, 270) } },
                new Dictionary<Vector2, Vector3>() { { new Vector2(480, -300), new Vector3(0, 0, 230) }, { new Vector2(-320, 50), new Vector3(0, 0, 0) }  },
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