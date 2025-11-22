using TMPro;
using UnityEngine;

public class Tooltip : MonoBehaviour
{

    public GameObject TooltipObject, CloseButton;
    public TMP_Text TooltipText;
    public RectTransform TooltipPosition;
    public RectTransform TooltipSize;

    public void Place(Vector2 position, Vector2 size)
    {
        TooltipPosition.localPosition = position;
        TooltipSize.sizeDelta = size;
    }
    public void Show(string text, bool hasX)
    {
        TooltipText.text = text;
        // Implement UI logic to display the tooltip with the given text
        Debug.Log($"Showing tooltip: {text}");

        CloseButton.SetActive(hasX);
        TooltipObject.SetActive(true);
    }
    public void Hide()
    {
        // Implement UI logic to hide the tooltip
        TooltipObject.SetActive(false);
    }

}