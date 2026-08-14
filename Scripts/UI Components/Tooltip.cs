using Assets.Scripts;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

public class Tooltip : MonoBehaviour
{
    public GameObject TooltipObject, CloseButton;
    public TMP_Text TooltipText;
    public RectTransform TooltipPosition;
    public RectTransform TooltipSize;
    private bool _closePressConfigured;

    public void Place(Vector2 position, Vector2 size)
    {
        TooltipPosition.localPosition = position;
        TooltipSize.sizeDelta = size;
    }

    public void Show(string text, bool hasX)
    {
        if (ConfigData.UserProgressData.ShowToolTips)
        {
            TooltipText.text = text;
            Debug.Log($"Showing tooltip: {text}");

            CloseButton.SetActive(hasX);
            ConfigureClosePress();
            TooltipObject.SetActive(true);
        }
        else
        {
            Hide();
        }
    }

    private void ConfigureClosePress()
    {
        if (_closePressConfigured || CloseButton == null)
        {
            return;
        }

        RectTransform closeRect = CloseButton.GetComponent<RectTransform>();
        if (closeRect != null)
        {
            closeRect.sizeDelta = new Vector2(16f, 16f);
        }
        // The superseded HUD workaround enlarged objects named exactly "Close Button". Runtime
        // references are serialized, so mark this instance after restoring its authored size.
        CloseButton.name = "Close Button Stable";

        EventTrigger trigger = CloseButton.GetComponent<EventTrigger>();
        if (trigger == null)
        {
            trigger = CloseButton.AddComponent<EventTrigger>();
        }
        if (trigger.triggers == null)
        {
            trigger.triggers = new System.Collections.Generic.List<EventTrigger.Entry>();
        }

        EventTrigger.Entry press = new EventTrigger.Entry
        {
            eventID = EventTriggerType.PointerDown
        };
        press.callback.AddListener(_ => Hide());
        trigger.triggers.Add(press);
        _closePressConfigured = true;
    }

    public void Hide()
    {
        TooltipObject.SetActive(false);
    }
}
