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
    private const float DialoguePresentationScale = 1.25f;
    private const float MinimumDialogueFontSize = 14f;
    private const float MinimumSpeakerFontSize = 16f;

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
        if (_currentLine != null)
        {
            StopAllCoroutines();
            SpeakerName.text = _currentLine.SpeakerName;
            SetPortrait(_currentLine.PortraitA);
            StartCoroutine(TypeLine(_currentLine));
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

        // Action/stage-direction lines are italicized in Mission Scripting. Do not add literal
        // asterisks around them: TMP rich text should own the visual formatting.
        return line.Type == DialogueLine.DialogueType.Action
            ? $"<i>{line.Text}</i>"
            : line.Text;
    }

    internal static string FormatInstructionText(string instructionText)
    {
        if (string.IsNullOrEmpty(instructionText))
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
            if (characterIndex == visibleCharacterCount - 1 ||
                line.Type != DialogueLine.DialogueType.Speaking)
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
            DialogueText.text = $"{formattedLine}<br><br>{FormatInstructionText(line.InstructionText)}";
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
            DisplayNextLine();
        }
        else if (showOrHide && _currentLine.Type == DialogueLine.DialogueType.Disappearing)
        {
            DisplayNextLineWithDelay(2f);
        }
        else
        {
            ContinueButton.SetActive(showOrHide);
        }
    }

    void EndDialogue()
    {
        Debug.Log("Dialogue ended.");
        CutsceneManager.EndDialogue();
    }
    public void GoToNextLine()
    {
        SpacebarImage.sprite = PressedSpacebar;
        Debug.Log($"Go to next line");
        EventSystem.GetComponent<UnityEngine.EventSystems.EventSystem>().SetSelectedGameObject(null);
        DisplayNextLineWithDelay(.5f);
    }
}
