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
        _isLastDialogue = isLastDialogue;
        _isAdvancingDialogue = false;
        dialogueLines.Clear();

        foreach (DialogueLine line in lines)
        {
            dialogueLines.Enqueue(line);
        }

        if (dialogueLines.Count > 0)
        {
            UIAudioController.Instance?.PlayIntercomSound();
        }

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
                yield return new WaitForSeconds(0.02f);
                DisplayNextLine();
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
