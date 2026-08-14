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


    public void Setup(CutsceneManager cutsceneManager)
    {
        CutsceneManager = cutsceneManager;
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
        // CutsceneManager.Setup rebuilds the campaign dialogue lists whenever a mission registers
        // its ending callback. Apply the current Mission Scripting wording at the presentation
        // boundary so even dialogue started synchronously during level construction (notably
        // Beenoculars) is updated before any line is enqueued or displayed. GetRange() returns the
        // same DialogueLine objects, so in-place patches are reflected in the supplied list too.
        if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign && CutsceneManager != null)
        {
            CampaignDialogueOverrides.Apply(CutsceneManager);
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

    IEnumerator TypeLine(DialogueLine line)
    {
        SpacebarImage.sprite = UnpressedSpacebar;
        DialogueText.text = "";
        int characterIndex = 0;
        bool aOrB = false;
        ToggleContinuePrompt(false);

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

        foreach (char c in line.Text)
        {
            if (Input.GetKey(KeyCode.Space))
            {
                if (line.Type == DialogueLine.DialogueType.Action)
                {
                    DialogueText.text = $"*<i>{line.Text}</i>*";
                }
                else
                {
                    DialogueText.text = line.Text;
                }
                line.IsSkipped = true;
                SetPortrait(line.PortraitA);
                yield return new WaitForSeconds(0.5f);
                ToggleContinuePrompt(true);
                break;
            }

            if (line.Type == DialogueLine.DialogueType.Action)
            {
                if (characterIndex == 0)
                {
                    DialogueText.text += "*<i>";
                }
            }

            DialogueText.text += c;

            if (line.Type == DialogueLine.DialogueType.Action)
            {
                if (characterIndex == line.Text.Length - 1)
                {
                    DialogueText.text += "</i>*";
                }
            }
            characterIndex++;
            if (characterIndex == line.Text.Length || line.Type != DialogueLine.DialogueType.Speaking)
            {
                SetPortrait(line.PortraitA);
            }
            else if (characterIndex % 6 == 0)
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
        line.IsOver = true;

        if (line.HasInstructionText)
        {
            yield return new WaitForSeconds(0.5f);
            DialogueText.text += $"<br><br>{line.InstructionText}";
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
