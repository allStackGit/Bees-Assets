using System;
using System.Collections.Generic;
using Assets.Scripts;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class Tooltip : MonoBehaviour
{
    private const float HorizontalPadding = 22f;
    private const float VerticalPadding = 18f;
    private const float SequenceFooterHeight = 34f;
    private const float MinReadableWidth = 260f;
    private const float MaxReadableWidth = 500f;
    private const float WidthMultiplier = 1.1f;
    private const float SequenceRequestedHeightMultiplier = 0.78f;
    private const float StandardRequestedHeightMultiplier = 1f;
    private const float InfoTabWidth = 90f;
    private const float InfoTabHeight = 30f;
    private const float InfoTabSlant = 12f;
    private const float InfoTabBorder = 1.5f;

    public GameObject TooltipObject, CloseButton;
    public TMP_Text TooltipText;
    public RectTransform TooltipPosition;
    public RectTransform TooltipSize;

    private bool _visualsConfigured;
    private float _authoredFontSize;
    private Vector2 _requestedPosition;
    private Vector2 _requestedSize = new Vector2(150f, 150f);
    private readonly List<string> _sequencePages = new List<string>();
    private int _sequenceIndex;
    private Action _sequenceComplete;
    private bool _sequenceActive;
    private GameObject _sequenceFooter;
    private Button _previousButton;
    private Button _nextButton;
    private TMP_Text _previousLabel;
    private TMP_Text _nextLabel;
    private DialogueManager _dialogueManager;

    private void Awake()
    {
        ConfigureVisuals();
    }

    private void Update()
    {
        KeepBelowActiveDialogue();

        if (!_sequenceActive || !TooltipObject.activeInHierarchy || !Input.GetKeyDown(KeyCode.Space))
        {
            return;
        }

        GameObject selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
        if (selected != null &&
            (selected.GetComponentInParent<TMP_InputField>() != null || selected.GetComponentInParent<InputField>() != null))
        {
            return;
        }

        NextPage();
    }

    private void KeepBelowActiveDialogue()
    {
        if (TooltipObject == null || !TooltipObject.activeInHierarchy || transform.parent == null)
        {
            return;
        }

        Transform parent = transform.parent;
        if (_dialogueManager == null)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform sibling = parent.GetChild(i);
                if (sibling == transform)
                {
                    continue;
                }

                _dialogueManager = sibling.GetComponent<DialogueManager>();
                if (_dialogueManager != null)
                {
                    break;
                }
            }
        }

        if (_dialogueManager == null || !_dialogueManager.gameObject.activeInHierarchy)
        {
            return;
        }

        Transform dialogueTransform = _dialogueManager.transform;
        if (dialogueTransform.parent != parent)
        {
            _dialogueManager = null;
            return;
        }

        int dialogueIndex = dialogueTransform.GetSiblingIndex();
        if (transform.GetSiblingIndex() > dialogueIndex)
        {
            // Level dialogue and tutorial tooltips are siblings under the shared UI overlay.
            // Only move this tutorial box below the active dialogue; preserve all other HUD
            // and popup ordering rather than forcing dialogue to the top of the entire canvas.
            transform.SetSiblingIndex(dialogueIndex);
        }
    }

    public void Place(Vector2 position, Vector2 size)
    {
        _requestedPosition = position;
        _requestedSize = size;
        ApplyLayout();
    }

    public void Show(string text, bool hasX)
    {
        ResetSequence(false);
        ShowInternal(text, hasX);
    }

    public void ShowSequence(IList<string> pages, bool hasX, Action onComplete = null)
    {
        ResetSequence(false);

        if (pages == null || pages.Count == 0 || !ConfigData.UserProgressData.ShowToolTips)
        {
            HideInternal();
            if (onComplete != null)
            {
                onComplete();
            }
            return;
        }

        _sequencePages.AddRange(pages);
        _sequenceIndex = 0;
        _sequenceComplete = onComplete;
        _sequenceActive = true;
        ConfigureVisuals();
        _sequenceFooter.SetActive(true);
        CloseButton.SetActive(hasX);
        ShowSequencePage();
    }

    public void Hide()
    {
        bool completeSequence = _sequenceActive;
        Action complete = completeSequence ? _sequenceComplete : null;
        ResetSequence(false);
        HideInternal();
        if (complete != null)
        {
            complete();
        }
    }

    private void ShowInternal(string text, bool hasX)
    {
        ConfigureVisuals();
        if (ConfigData.UserProgressData.ShowToolTips)
        {
            TooltipText.text = text;
            Debug.Log($"Showing tooltip: {text}");
            CloseButton.SetActive(hasX);
            _sequenceFooter.SetActive(false);
            TooltipObject.SetActive(true);
            ApplyLayout();
        }
        else
        {
            HideInternal();
        }
    }

    private void ShowSequencePage()
    {
        if (!_sequenceActive || _sequencePages.Count == 0)
        {
            return;
        }

        TooltipText.text = _sequencePages[_sequenceIndex];
        TooltipObject.SetActive(true);
        _sequenceFooter.SetActive(true);
        _previousButton.interactable = _sequenceIndex > 0;
        _previousLabel.text = "PREV";
        _nextLabel.text = (_sequenceIndex == _sequencePages.Count - 1 ? "CLOSE" : "NEXT") +
                          $" ({_sequenceIndex + 1}/{_sequencePages.Count})";
        ApplyLayout();
    }

    private void PreviousPage()
    {
        if (!_sequenceActive || _sequenceIndex <= 0)
        {
            return;
        }

        _sequenceIndex--;
        ShowSequencePage();
    }

    private void NextPage()
    {
        if (!_sequenceActive)
        {
            return;
        }

        if (_sequenceIndex < _sequencePages.Count - 1)
        {
            _sequenceIndex++;
            ShowSequencePage();
            return;
        }

        Hide();
    }

    private void ResetSequence(bool hideTooltip)
    {
        _sequencePages.Clear();
        _sequenceIndex = 0;
        _sequenceComplete = null;
        _sequenceActive = false;
        if (_sequenceFooter != null)
        {
            _sequenceFooter.SetActive(false);
        }
        if (hideTooltip)
        {
            HideInternal();
        }
    }

    private void HideInternal()
    {
        if (TooltipObject != null)
        {
            TooltipObject.SetActive(false);
        }
    }

    private void ConfigureVisuals()
    {
        if (_visualsConfigured || TooltipText == null || TooltipSize == null)
        {
            return;
        }

        _authoredFontSize = TooltipText.fontSize;
        TooltipText.fontSize = Mathf.Max(_authoredFontSize + 2f, _authoredFontSize * 1.1f);
        TooltipText.enableWordWrapping = true;

        ConfigureTextPadding();
        ConfigureCloseButton();
        ConfigureSteelBorder();
        CreateInfoTab();
        CreateSequenceFooter();
        _visualsConfigured = true;
    }

    private void ConfigureTextPadding()
    {
        RectTransform textRect = TooltipText.rectTransform;
        if (textRect == null || textRect.parent != TooltipSize)
        {
            return;
        }

        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.pivot = new Vector2(0.5f, 0.5f);
        textRect.offsetMin = new Vector2(HorizontalPadding, VerticalPadding);
        textRect.offsetMax = new Vector2(-HorizontalPadding, -VerticalPadding);
    }

    private void ConfigureCloseButton()
    {
        if (CloseButton == null)
        {
            return;
        }

        // Disable the old EventTrigger path so hover and click use the same Selectable state.
        EventTrigger trigger = CloseButton.GetComponent<EventTrigger>();
        if (trigger != null)
        {
            if (trigger.triggers != null)
            {
                trigger.triggers.Clear();
            }
            trigger.enabled = false;
        }

        Button button = CloseButton.GetComponent<Button>();
        if (button == null)
        {
            button = CloseButton.AddComponent<Button>();
        }
        button.onClick.RemoveListener(Hide);
        button.onClick.AddListener(Hide);

        Graphic xGraphic = CloseButton.GetComponent<Graphic>();
        if (xGraphic == null)
        {
            xGraphic = CloseButton.GetComponentInChildren<Graphic>(true);
        }
        if (xGraphic != null)
        {
            // Keep the prefab's authored ColorTint states. Rebuilding the ColorBlock from the
            // Image's white tint makes normal and highlighted identical, so hover has no effect.
            button.targetGraphic = xGraphic;
        }

        // A transparent child provides one deterministic 32x32 raycast area without resizing
        // the authored X graphic itself.
        Transform existingHitArea = CloseButton.transform.Find("Tutorial Close Hit Area");
        GameObject hitArea;
        if (existingHitArea == null)
        {
            hitArea = new GameObject("Tutorial Close Hit Area", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            hitArea.transform.SetParent(CloseButton.transform, false);
        }
        else
        {
            hitArea = existingHitArea.gameObject;
        }

        RectTransform hitRect = hitArea.GetComponent<RectTransform>();
        hitRect.anchorMin = new Vector2(0.5f, 0.5f);
        hitRect.anchorMax = new Vector2(0.5f, 0.5f);
        hitRect.pivot = new Vector2(0.5f, 0.5f);
        hitRect.anchoredPosition = Vector2.zero;
        hitRect.sizeDelta = new Vector2(32f, 32f);
        Image hitImage = hitArea.GetComponent<Image>();
        hitImage.color = new Color(1f, 1f, 1f, 0f);
        hitImage.raycastTarget = true;

        // Preserve the established name used to opt this control out of legacy HUD resizing.
        CloseButton.name = "Close Button Stable";
    }

    private void ConfigureSteelBorder()
    {
        Graphic body = TooltipSize.GetComponent<Graphic>();
        if (body == null && TooltipObject != null)
        {
            body = TooltipObject.GetComponent<Graphic>();
        }
        if (body == null)
        {
            return;
        }

        UnityEngine.UI.Outline outline = body.GetComponent<UnityEngine.UI.Outline>();
        if (outline == null)
        {
            outline = body.gameObject.AddComponent<UnityEngine.UI.Outline>();
        }
        outline.effectColor = new Color(0.48f, 0.55f, 0.61f, 0.9f);
        outline.effectDistance = new Vector2(1.5f, -1.5f);
        outline.useGraphicAlpha = true;
    }

    private void CreateInfoTab()
    {
        if (TooltipSize.Find("Tutorial Info Tab") != null)
        {
            return;
        }

        // The tab is part of the panel silhouette rather than a floating badge. Extend its left
        // edge by the steel outline thickness while keeping the right-hand join at the same point.
        GameObject tab = new GameObject(
            "Tutorial Info Tab",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TutorialInfoTabGraphic));
        tab.transform.SetParent(TooltipSize, false);
        RectTransform rect = tab.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 0f);
        rect.anchoredPosition = new Vector2(-InfoTabBorder, 0f);
        rect.sizeDelta = new Vector2(InfoTabWidth + InfoTabBorder, InfoTabHeight);

        TutorialInfoTabGraphic border = tab.GetComponent<TutorialInfoTabGraphic>();
        border.color = new Color(0.62f, 0.69f, 0.74f, 0.95f);
        border.Slant = InfoTabSlant;
        border.raycastTarget = false;

        GameObject fillObject = new GameObject(
            "Fill",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TutorialInfoTabGraphic));
        fillObject.transform.SetParent(tab.transform, false);
        RectTransform fillRect = fillObject.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.pivot = new Vector2(0.5f, 0.5f);
        fillRect.offsetMin = new Vector2(InfoTabBorder, InfoTabBorder);
        fillRect.offsetMax = new Vector2(-InfoTabBorder, -InfoTabBorder);

        TutorialInfoTabGraphic fill = fillObject.GetComponent<TutorialInfoTabGraphic>();
        fill.color = new Color(0.34f, 0.39f, 0.44f, 0.96f);
        fill.Slant = Mathf.Max(0f, InfoTabSlant - InfoTabBorder);
        fill.raycastTarget = false;

        GameObject labelObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        labelObject.transform.SetParent(tab.transform, false);
        RectTransform labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(6f, 2f);
        labelRect.offsetMax = new Vector2(-(InfoTabSlant + 4f), -2f);
        TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
        label.text = "INFO";
        label.font = TooltipText.font;
        label.fontSize = Mathf.Max(12f, TooltipText.fontSize * 0.8f);
        label.alignment = TextAlignmentOptions.Center;
        label.color = TooltipText.color;
        label.raycastTarget = false;
    }

    private void CreateSequenceFooter()
    {
        _sequenceFooter = new GameObject("Tutorial Sequence Footer", typeof(RectTransform));
        _sequenceFooter.transform.SetParent(TooltipSize, false);
        RectTransform footerRect = _sequenceFooter.GetComponent<RectTransform>();
        footerRect.anchorMin = new Vector2(0f, 0f);
        footerRect.anchorMax = new Vector2(1f, 0f);
        footerRect.pivot = new Vector2(0.5f, 0f);
        footerRect.anchoredPosition = Vector2.zero;
        footerRect.sizeDelta = new Vector2(0f, SequenceFooterHeight);

        _previousButton = CreateFooterButton("Previous", new Vector2(1f, 0.5f), new Vector2(-176f, 0f), new Vector2(74f, 27f), out _previousLabel);
        _previousButton.onClick.AddListener(PreviousPage);
        _nextButton = CreateFooterButton("Next", new Vector2(1f, 0.5f), new Vector2(-72f, 0f), new Vector2(126f, 27f), out _nextLabel);
        _nextButton.onClick.AddListener(NextPage);
        _sequenceFooter.SetActive(false);
    }

    private Button CreateFooterButton(string objectName, Vector2 anchor, Vector2 position, Vector2 size, out TMP_Text label)
    {
        GameObject buttonObject = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(_sequenceFooter.transform, false);
        RectTransform rect = buttonObject.GetComponent<RectTransform>();
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;

        Image image = buttonObject.GetComponent<Image>();
        image.color = new Color(0.24f, 0.28f, 0.32f, 0.9f);
        Button button = buttonObject.GetComponent<Button>();
        button.targetGraphic = image;

        GameObject labelObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        labelObject.transform.SetParent(buttonObject.transform, false);
        RectTransform labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(4f, 2f);
        labelRect.offsetMax = new Vector2(-4f, -2f);
        TextMeshProUGUI text = labelObject.GetComponent<TextMeshProUGUI>();
        text.font = TooltipText.font;
        text.fontSize = Mathf.Max(11f, TooltipText.fontSize * 0.7f);
        text.alignment = TextAlignmentOptions.Center;
        text.color = TooltipText.color;
        text.raycastTarget = false;
        label = text;
        return button;
    }

    private void ApplyLayout()
    {
        if (TooltipPosition == null || TooltipSize == null || TooltipText == null)
        {
            return;
        }

        TooltipPosition.localPosition = _requestedPosition;

        string message = TooltipText.text ?? string.Empty;
        float requestedWidth = Mathf.Max(MinReadableWidth, _requestedSize.x * WidthMultiplier);
        float readableWidth = Mathf.Max(requestedWidth, EstimateReadableWidth(message));
        readableWidth = Mathf.Min(readableWidth, MaxReadableWidth);

        float contentWidth = Mathf.Max(1f, readableWidth - HorizontalPadding * 2f);
        Vector2 preferred = TooltipText.GetPreferredValues(message, contentWidth, 0f);
        float footer = _sequenceActive ? SequenceFooterHeight : 0f;
        float readableHeight = preferred.y + VerticalPadding * 2f + footer;
        float requestedHeightMultiplier = _sequenceActive
            ? SequenceRequestedHeightMultiplier
            : StandardRequestedHeightMultiplier;
        float requestedHeight = _requestedSize.y * requestedHeightMultiplier;
        float height = Mathf.Max(requestedHeight, readableHeight);
        TooltipSize.sizeDelta = new Vector2(readableWidth, height);

        RectTransform textRect = TooltipText.rectTransform;
        if (textRect != null && textRect.parent == TooltipSize)
        {
            textRect.offsetMin = new Vector2(HorizontalPadding, VerticalPadding + footer);
            textRect.offsetMax = new Vector2(-HorizontalPadding, -VerticalPadding);
        }
    }

    private float EstimateReadableWidth(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return MinReadableWidth;
        }

        string[] clauses = message.Split(new[] { '.', ';', '?', '!', ':', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int longestClause = 0;
        for (int i = 0; i < clauses.Length; i++)
        {
            if (clauses[i].Length > longestClause)
            {
                longestClause = clauses[i].Length;
            }
        }

        float estimate = longestClause * TooltipText.fontSize * 0.44f + HorizontalPadding * 2f;
        return Mathf.Clamp(estimate, MinReadableWidth, MaxReadableWidth);
    }
}

/// <summary>
/// Simple four-vertex UI graphic used by the tutorial INFO tab. The lower-right point extends
/// farther right than the upper-right point, so the exposed right edge slopes downward/right.
/// </summary>
public sealed class TutorialInfoTabGraphic : MaskableGraphic
{
    [SerializeField]
    private float _slant = 12f;

    public float Slant
    {
        get => _slant;
        set
        {
            float clamped = Mathf.Max(0f, value);
            if (Mathf.Approximately(_slant, clamped))
            {
                return;
            }
            _slant = clamped;
            SetVerticesDirty();
        }
    }

    protected override void OnPopulateMesh(VertexHelper vertexHelper)
    {
        vertexHelper.Clear();
        Vector2[] vertices = CalculateInfoTabVertices(GetPixelAdjustedRect(), _slant);
        UIVertex vertex = UIVertex.simpleVert;
        vertex.color = color;

        for (int index = 0; index < vertices.Length; index++)
        {
            vertex.position = vertices[index];
            vertex.uv0 = Vector2.zero;
            vertexHelper.AddVert(vertex);
        }

        vertexHelper.AddTriangle(0, 1, 2);
        vertexHelper.AddTriangle(0, 2, 3);
    }

    internal static Vector2[] CalculateInfoTabVertices(Rect rect, float slant)
    {
        float safeSlant = Mathf.Clamp(slant, 0f, Mathf.Max(0f, rect.width));
        return new[]
        {
            new Vector2(rect.xMin, rect.yMin),
            new Vector2(rect.xMax, rect.yMin),
            new Vector2(rect.xMax - safeSlant, rect.yMax),
            new Vector2(rect.xMin, rect.yMax)
        };
    }
}