using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    public class SquadTab : MonoBehaviour
    {
        public GameObject Tab, Border, Background;

        public void SetColor(Color color)
        {
            Background.GetComponent<Image>().color = color;
        }
        public void ShowSelected()
        {
            Border.SetActive(true);
        }
        public void HideSelected()
        {
            Border.SetActive(false);
        }
        public void ShowTab()
        {
            Tab.SetActive(true);
        }
        public void HideTab()
        {
            Tab.SetActive(false);
        }
        public void DisableTab()
        {
            Color currentColor = Background.GetComponent<Image>().color;
            Background.GetComponent<Image>().color = new Color(currentColor.r, currentColor.g, currentColor.b, .25f);
        }
    }
}