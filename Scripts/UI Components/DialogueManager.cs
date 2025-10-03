using Assets.Scripts;
using Assets.Scripts.Levels;
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
    public TMP_Text SpeakerName;
    public Image PortraitImage;
    public Dialogues CurrentDialogue;
    public EventSystem EventSystem;

    private Queue<DialogueLine> dialogueLines = new Queue<DialogueLine>();
    private DialogueLine _currentLine;
    private bool _hasContinueButton, _isLastDialogue;

    public enum Dialogues
    {
        Pluto_Anomaly,
    }

    public void Setup(CutsceneManager cutsceneManager, Dialogues dialogueType)
    {
        CutsceneManager = cutsceneManager;
        CurrentDialogue = dialogueType;
    }
    public void Setup(CutsceneManager cutsceneManager)
    {
        CutsceneManager = cutsceneManager;
    }

    public void SwitchDialogue(Dialogues dialogueType)
    {
        CurrentDialogue = dialogueType;
    }
    public void Update()
    {
        if (_currentLine.IsOver && Input.GetKey(KeyCode.Space))
        {
            DisplayNextLineWithDelay(.5f);
        }
    }

    public void StartDialogue(List<DialogueLine> lines, bool hasContinueButton, bool isLastDialogue)
    {
        _isLastDialogue = isLastDialogue;
        //Debug.Log("Starting dialogue in dialogue manager.");
        dialogueLines.Clear();

        foreach (DialogueLine line in lines)
        {
            dialogueLines.Enqueue(line);
        }


        DialogueBox.SetActive(true);
        _hasContinueButton = hasContinueButton;
        ContinueButton.SetActive(hasContinueButton);
        ToggleContinuePrompt(false);
        DisplayNextLine();
    }

    public void DisplayNextLine()
    {
        //Debug.Log("Displaying next line in dialogue manager.");A
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
        if (line.Type == DialogueLine.DialogueType.Break)
        {
            DialogueText.text = "";
            DialogueLine nextLine = dialogueLines.Peek();
            SpeakerName.text = nextLine.SpeakerName;
            SetPortrait(nextLine.PortraitA);
            CutsceneManager.BreakDialogue();
        }
        else
        {
            DialogueText.text = "";
            int characterIndex = 0;
            bool aOrB = false;
            ToggleContinuePrompt(false);

            //Debug.Log($"Typing line: {line.Text}, Type: {line.Type}");

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
            if (line.PauseDuration > 0)
            {
                if (Input.GetKey(KeyCode.Space))
                {
                    line.IsSkipped = true;
                    yield return new WaitForSeconds(0.02f); // .02f
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
                ToggleContinuePrompt(line.PauseDuration <= 0);
            }
        }
        
    }

    public void ToggleContinuePrompt(bool showOrHide)
    {
        if (showOrHide && Input.GetKey(KeyCode.Space))
        {
            //Debug.Log($"Space held, going to next line"); 
            DisplayNextLine();
        }
        else
        {
            if (_hasContinueButton)
            {
                ContinueButton.SetActive(showOrHide);
            }
            else if (showOrHide)
            {
                DisplayNextLineWithDelay(2f); // 2f
            }
        }

    }

    void EndDialogue()
    {
        Debug.Log("Dialogue ended.");
        CutsceneManager.EndDialogue(CurrentDialogue);
    }
    public void GoToNextLine()
    {
        Debug.Log($"Go to next line");
        EventSystem.GetComponent<UnityEngine.EventSystems.EventSystem>().SetSelectedGameObject(null);
        DisplayNextLine();

    }

}
