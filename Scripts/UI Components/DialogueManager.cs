using Assets.Scripts;
using Assets.Scripts.Levels;
using Assets.Scripts.UI_Components;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;


public class DialogueManager : MonoBehaviour
{
    private const int FirstCampaignLevelId = 0;
    private const float DialoguePresentationScale = 1.25f;
    private const float MinimumDialogueFontSize = 14f;
    private const float MinimumSpeakerFontSize = 16f;
    private const float ContinuePromptFontSize = 11f;
    private const string ProtectedSamuelOrdersLine = "What are your orders- oh";

    private static readonly string[] ShipTypeNames =
    {
        "Barge", "Beacon", "Carrier", "Cruiser", "Dreadnought", "Drone", "Factory", "Fire Barge",
        "Flagship", "Frigate", "Gunship", "Scout", "Striker", "Warp Gate", "Beehive", "Bumblebee",
        "Carpenter Bee", "Honeybee", "Hornet", "Leafcutter", "Queen", "Wasp", "Yellow Jacket"
    };

    public CutsceneManager CutsceneManager;
    public GameObject DialogueBox;
    public TMP_Text DialogueText;
    public GameObject ContinueButton;
    public Image SpacebarImage;
    public Sprite UnpressedSpacebar;
    public Sprite PressedSpacebar;
    public TMP_Text SpeakerName;
    public Image PortraitImage;
    public EventSystem EventSystem;

    private Queue<DialogueLine> dialogueLines = new Queue<DialogueLine>();
    private DialogueLine _currentLine;
    private bool _isLastDialogue;
    private bool _isAdvancingDialogue;
    private bool _playIntercomWhenPresented;
    private bool _presentationConfigured;
    private TMP_Text _continuePromptLabel;
    private bool _continueInstructionClaimed;
    private bool _showContinueInstructionForCurrentLine;
    private static bool _disabledLegacyCampaignDialogueGuard;


    public void Setup(CutsceneManager cutsceneManager)
    {
        CutsceneManager = cutsceneManager;
        ConfigurePresentation();
    }

    private void ConfigurePresentation()
    {
        if (_presentationConfigured)
        {
            return;
        }
        _presentationConfigured = true;

        if (DialogueText != null)
        {
            DialogueText.richText = true;
            DialogueText.fontSize = Mathf.Max(DialogueText.fontSize, MinimumDialogueFontSize);
        }

        if (SpeakerName != null)
        {
            SpeakerName.richText = true;
            SpeakerName.fontSize = Mathf.Max(SpeakerName.fontSize, MinimumSpeakerFontSize);
        }

        ConfigureContinuePrompt();

        RectTransform dialogueRect = DialogueBox != null
            ? DialogueBox.GetComponent<RectTransform>()
            : null;
        if (dialogueRect == null)
        {
            return;
        }

        Vector3 oldScale = dialogueRect.localScale;
        Vector3 newScale = new Vector3(
            oldScale.x * DialoguePresentationScale,
            oldScale.y * DialoguePresentationScale,
            oldScale.z);

        // The authored dialogue is right-anchored. Preserve its visible right edge while making
        // the whole panel, portrait, prompt and text proportionally larger so the expanded box
        // does not simply grow off-screen.
        if (Mathf.Abs(dialogueRect.anchorMin.x - 1f) < 0.001f &&
            Mathf.Abs(dialogueRect.anchorMax.x - 1f) < 0.001f)
        {
            float visualWidthIncrease = dialogueRect.rect.width * (newScale.x - oldScale.x);
            Vector2 position = dialogueRect.anchoredPosition;
            position.x -= visualWidthIncrease * (1f - dialogueRect.pivot.x);
            dialogueRect.anchoredPosition = position;
        }

        dialogueRect.localScale = newScale;
    }

    private void ConfigureContinuePrompt()
    {
        if (ContinueButton == null || SpacebarImage == null)
        {
            return;
        }

        Transform existing = ContinueButton.transform.Find("Continue Prompt Label");
        if (existing != null)
        {
            _continuePromptLabel = existing.GetComponent<TMP_Text>();
            if (_continuePromptLabel != null)
            {
                _continuePromptLabel.gameObject.SetActive(false);
            }
            return;
        }

        GameObject labelObject = new GameObject(
            "Continue Prompt Label",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TextMeshProUGUI));
        labelObject.transform.SetParent(ContinueButton.transform, false);
        RectTransform labelRect = labelObject.GetComponent<RectTransform>();
        RectTransform spaceRect = SpacebarImage.rectTransform;
        labelRect.anchorMin = spaceRect.anchorMin;
        labelRect.anchorMax = spaceRect.anchorMax;
        labelRect.pivot = new Vector2(1f, 0.5f);
        labelRect.anchoredPosition = spaceRect.anchoredPosition + new Vector2(-spaceRect.rect.width * 0.55f - 6f, 0f);
        labelRect.sizeDelta = new Vector2(150f, Mathf.Max(18f, spaceRect.rect.height));

        TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
        label.text = "Press space bar to continue";
        label.font = DialogueText != null ? DialogueText.font : null;
        label.fontSize = ContinuePromptFontSize;
        label.alignment = TextAlignmentOptions.MidlineRight;
        label.color = DialogueText != null ? DialogueText.color : Color.white;
        label.raycastTarget = false;
        label.gameObject.SetActive(false);
        _continuePromptLabel = label;
    }

    public void Update()
    {
        if (_currentLine != null && _currentLine.IsOver && Input.GetKey(KeyCode.Space))
        {
            SpacebarImage.sprite = PressedSpacebar;
            DisplayNextLineWithDelay(.5f);
        }
    }

    public void StartDialogue(List<DialogueLine> lines, bool isLastDialogue)
    {
        ConfigurePresentation();

        // CutsceneManager.Setup rebuilds the campaign dialogue lists whenever a mission registers
        // its ending callback. Apply the current Mission Scripting wording at the presentation
        // boundary so even dialogue started synchronously during level construction (notably
        // Beenoculars) is updated before any line is enqueued or displayed. GetRange() returns the
        // same DialogueLine objects, so in-place patches are reflected in the supplied list too.
        if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign && CutsceneManager != null)
        {
            CampaignDialogueOverrides.Apply(CutsceneManager);

            // The original override guard was introduced before presentation-time application was
            // available. Once a DialogueManager has taken over that responsibility, disable the
            // persistent polling component so campaign gameplay does not perform a scene-wide
            // CutsceneManager search every frame.
            if (!_disabledLegacyCampaignDialogueGuard)
            {
                CampaignDialogueOverrideGuard guard = FindObjectOfType<CampaignDialogueOverrideGuard>();
                if (guard != null)
                {
                    guard.enabled = false;
                    _disabledLegacyCampaignDialogueGuard = true;
                }
            }
        }

        _isLastDialogue = isLastDialogue;
        _isAdvancingDialogue = false;
        _playIntercomWhenPresented = false;
        dialogueLines.Clear();
        _currentLine = null;

        if (ConfigData.SkipDialogue)
        {
            // Skipping presentation must still advance the cutscene state exactly as if the
            // dialogue section had been completed. Intermediate sections use BreakDialogue()
            // so mission-specific break callbacks execute; the final section uses EndDialogue().
            foreach (DialogueLine line in lines)
            {
                if (line == null)
                {
                    continue;
                }
                line.IsSkipped = true;
                line.IsOver = true;
            }

            StopAllCoroutines();
            DialogueBox.SetActive(false);
            ContinueButton.SetActive(false);

            if (_isLastDialogue)
            {
                EndDialogue();
            }
            else
            {
                CutsceneManager.BreakDialogue();
            }
            return;
        }

        foreach (DialogueLine line in lines)
        {
            dialogueLines.Enqueue(line);
        }
        _playIntercomWhenPresented = dialogueLines.Count > 0;

        DialogueBox.SetActive(true);
        ContinueButton.SetActive(true);
        ToggleContinuePrompt(false);
        DisplayNextLine();
    }

    public void DisplayNextLine()
    {
        _isAdvancingDialogue = false;
        if (dialogueLines.Count == 0)
        {
            if (_isLastDialogue)
            {
                EndDialogue();
            }
            else
            {
                CutsceneManager.BreakDialogue();
            }
            return;
        }
        if (_currentLine != null)
        {
            _currentLine.IsOver = false;
        }
        _currentLine = dialogueLines.Dequeue();
        _showContinueInstructionForCurrentLine = ShouldShowContinueInstruction(_currentLine);
        if (_showContinueInstructionForCurrentLine)
        {
            // Claim the one-time hint when the line begins, not when typing finishes. If the
            // player is already holding Space, the hint must not migrate to the next message.
            _continueInstructionClaimed = true;
        }
        SetContinueInstructionVisible(false);

        if (_currentLine != null)
        {
            StopAllCoroutines();
            SpeakerName.text = _currentLine.SpeakerName;
            SetPortrait(_currentLine.PortraitA);
            StartCoroutine(TypeLine(_currentLine));
        }

    }

    private bool ShouldShowContinueInstruction(DialogueLine line)
    {
        return !_continueInstructionClaimed &&
               line != null &&
               line.Type == DialogueLine.DialogueType.Speaking &&
               ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign &&
               ConfigData.UserProgressData != null &&
               ConfigData.Configuration != null &&
               ConfigData.UserProgressData.GetCurrentLevel(
                   ConfigData.Configuration.UserSide,
                   ConfigData.GameModes.Campaign) == FirstCampaignLevelId;
    }

    private void SetContinueInstructionVisible(bool continueControlVisible)
    {
        if (_continuePromptLabel != null)
        {
            _continuePromptLabel.gameObject.SetActive(
                continueControlVisible && _showContinueInstructionForCurrentLine);
        }
    }

    public void DisplayNextLineWithDelay(float delaySeconds = 2f)
    {
        if (_isAdvancingDialogue)
        {
            return;
        }
        _isAdvancingDialogue = true;
        StartCoroutine(DisplayNextLineCoroutine(delaySeconds));
    }

    private IEnumerator DisplayNextLineCoroutine(float delaySeconds)
    {
        yield return new WaitForSeconds(delaySeconds);
        DisplayNextLine();
    }

    public void SetPortrait(Sprite sprite)
    {
        if (_currentLine.IsUnknown)
        {
            PortraitImage.color = Color.black;
        }
        else
        {
            PortraitImage.color = Color.white;
        }
        PortraitImage.sprite = sprite;
    }

    internal static string FormatLineText(DialogueLine line)
    {
        if (line == null || string.IsNullOrEmpty(line.Text))
        {
            return string.Empty;
        }

        string text = line.Text;
        bool preserveMarkOrdersFeedback =
            string.Equals(line.SpeakerName, "Samuel", System.StringComparison.OrdinalIgnoreCase) &&
            text.StartsWith(ProtectedSamuelOrdersLine, System.StringComparison.Ordinal);

        if (!preserveMarkOrdersFeedback)
        {
            text = NormalizeDialoguePunctuation(text);
            text = NormalizeShipTypeCapitalization(text);
        }

        // Keep the continue affordance out of character dialogue. It is rendered beside the
        // space/skip control instead.
        text = text.Replace("Press space bar to continue", string.Empty).Trim();
        text = text.Replace("Pluto airspace", "the space around Pluto");

        // Action/stage-direction lines are italicized in Mission Scripting. Do not add literal
        // asterisks around them: TMP rich text should own the visual formatting.
        return line.Type == DialogueLine.DialogueType.Action
            ? $"<i>{text}</i>"
            : text;
    }

    private static string NormalizeDialoguePunctuation(string text)
    {
        return text
            .Replace(" - ", " — ")
            .Replace("- ", "— ");
    }

    private static string NormalizeShipTypeCapitalization(string text)
    {
        for (int i = 0; i < ShipTypeNames.Length; i++)
        {
            string shipType = ShipTypeNames[i];
            text = text.Replace(shipType + "s", shipType.ToLowerInvariant() + "s");
            text = text.Replace(shipType, shipType.ToLowerInvariant());
        }

        if (text.Length > 0 && char.IsLetter(text[0]))
        {
            text = char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        // Ship types stay lowercase in normal prose, but remain capitalized when they begin a
        // later sentence in the same dialogue line.
        for (int i = 0; i < ShipTypeNames.Length; i++)
        {
            string lower = ShipTypeNames[i].ToLowerInvariant();
            string capitalized = char.ToUpperInvariant(lower[0]) + lower.Substring(1);
            text = text.Replace(". " + lower, ". " + capitalized);
            text = text.Replace("! " + lower, "! " + capitalized);
            text = text.Replace("? " + lower, "? " + capitalized);
            text = text.Replace("\n" + lower, "\n" + capitalized);
        }
        return text;
    }

    internal static string FormatInstructionText(string instructionText)
    {
        if (string.IsNullOrEmpty(instructionText))
        {
            return string.Empty;
        }

        string normalized = instructionText.Trim().Trim('[', ']').Trim();
        if (string.Equals(normalized, "Press Space to Continue", System.StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "Press Space Bar to Continue", System.StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        // Player-facing instruction blocks are the emphasized > blocks in Mission Scripting.
        return $"<b><i>{instructionText}</i></b>";
    }

    IEnumerator TypeLine(DialogueLine line)
    {
        SpacebarImage.sprite = UnpressedSpacebar;
        bool aOrB = false;
        ToggleContinuePrompt(false);

        string formattedLine = FormatLineText(line);
        DialogueText.richText = true;
        DialogueText.text = formattedLine;
        DialogueText.maxVisibleCharacters = 0;
        DialogueText.ForceMeshUpdate();
        int visibleCharacterCount = DialogueText.textInfo.characterCount;

        if (_playIntercomWhenPresented)
        {
            _playIntercomWhenPresented = false;
            // Campaign dialogue can be queued while the Stage is still constructing its map,
            // ships, camera, and UI. Wait until that frame has actually rendered before playing
            // the intercom cue so audio never announces dialogue over the loading transition.
            yield return new WaitForEndOfFrame();
            if (DialogueBox != null && DialogueBox.activeInHierarchy)
            {
                UIAudioController.Instance?.PlayIntercomSound();
            }
        }

        for (int characterIndex = 0; characterIndex < visibleCharacterCount; characterIndex++)
        {
            if (Input.GetKey(KeyCode.Space))
            {
                DialogueText.maxVisibleCharacters = int.MaxValue;
                line.IsSkipped = true;
                SetPortrait(line.PortraitA);
                yield return new WaitForSeconds(0.5f);
                ToggleContinuePrompt(true);
                break;
            }

            DialogueText.maxVisibleCharacters = characterIndex + 1;
            if (characterIndex == visibleCharacterCount - 1 || line.Type != DialogueLine.DialogueType.Speaking)
            {
                SetPortrait(line.PortraitA);
            }
            else if ((characterIndex + 1) % 6 == 0)
            {
                if (aOrB)
                {
                    SetPortrait(line.PortraitA);
                }
                else
                {
                    SetPortrait(line.PortraitB);
                }
                aOrB = !aOrB;
            }

            yield return new WaitForSeconds(0.02f);
        }

        DialogueText.maxVisibleCharacters = int.MaxValue;
        line.IsOver = true;

        if (line.HasInstructionText)
        {
            yield return new WaitForSeconds(0.5f);
            string instruction = FormatInstructionText(line.InstructionText);
            DialogueText.text = string.IsNullOrEmpty(instruction)
                ? formattedLine
                : $"{formattedLine}<br><br>{instruction}";
            DialogueText.maxVisibleCharacters = int.MaxValue;
        }

        if (line.Type == DialogueLine.DialogueType.Pause)
        {
            if (Input.GetKey(KeyCode.Space))
            {
                line.IsSkipped = true;
                DisplayNextLineWithDelay(0.02f);
            }
            else
            {
                yield return new WaitForSeconds(line.PauseDuration);
                DisplayNextLineWithDelay(2f);
            }
        }

        if (!line.IsSkipped)
        {
            ToggleContinuePrompt(line.Type != DialogueLine.DialogueType.Pause);
        }
    }

    public void ToggleContinuePrompt(bool showOrHide)
    {
        if (showOrHide && Input.GetKey(KeyCode.Space))
        {
            SetContinueInstructionVisible(false);
            DisplayNextLine();
        }
        else if (showOrHide && _currentLine.Type == DialogueLine.DialogueType.Disappearing)
        {
            SetContinueInstructionVisible(false);
            DisplayNextLineWithDelay(2f);
        }
        else
        {
            ContinueButton.SetActive(showOrHide);
            SetContinueInstructionVisible(showOrHide);
        }
    }

    void EndDialogue()
    {
        CutsceneManager.EndDialogue();
    }
    public void GoToNextLine()
    {
        SpacebarImage.sprite = PressedSpacebar;
        EventSystem.GetComponent<UnityEngine.EventSystems.EventSystem>().SetSelectedGameObject(null);
        DisplayNextLineWithDelay(.5f);
    }
}