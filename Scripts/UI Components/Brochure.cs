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
        public List<List<Vector2>> ArrowPositions = new List<List<Vector2>>();

        private int _currentPage = 0;
        private List<GameObject> _activeArrows = new List<GameObject>();

        private void Awake()
        {
            Pages = new List<string>
            {
                "The squad maker is where you create and view your squads and the ships in your fleet. You'll often come here before each level to choose the squads you want to bring into the level and manage your existing squads.",
                "On the left you can see a list of all the ships in your fleet that <i>aren't</i> in your squads. On the right you can see all your current squads. When you lose ships in your squads they will automatically be replenished from your fleet if there are more available.",
                "You can click once on a squad to choose it for the level or click twice to edit the squad. To remove a chosen squad from the level just click on it once. You can also hover over ships or squads to see their stats.",
                "Each ship has a capacity value, and each level has a maximum capacity that you can bring into the level.",
                "When editing a squad you can click or drag a ship in the fleet to add it to the squad. The BLARP buttons can be used to set the formation of the squad. You can select a squad color as well.",
                "Finally, when you're editing a squad, the Squad Action Box you see in the game will appear where you can modify the default actions and shooting strategies of the squad. Good luck!"
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
                OverlayCanvas.gameObject.SetActive(false);
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
                    rect.anchoredPosition = pos;
                    _activeArrows.Add(arrow);
                }
            }
        }
    }
}