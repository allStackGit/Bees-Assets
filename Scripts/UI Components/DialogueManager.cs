using Assets.Scripts;
using Assets.Scripts.Levels;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;


public class DialogueManager : MonoBehaviour
{
    public CutsceneManager CutsceneManager;
    public GameObject DialogueBox;
    public TMP_Text DialogueText;
    public TMP_Text ContinuePrompt;
    public TMP_Text SpeakerName;
    public Image PortraitImage;
    public Dialogues CurrentDialogue;

    private Queue<DialogueLine> dialogueLines = new Queue<DialogueLine>();
    private bool _hasContinuePrompt, _isLastDialogue;

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
        _hasContinuePrompt = hasContinueButton;
        ToggleContinuePrompt(false);
        DisplayNextLine();
    }

    public void DisplayNextLine()
    {
        //Debug.Log("Displaying next line in dialogue manager.");
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

        DialogueLine line = dialogueLines.Dequeue();
        StopAllCoroutines();
        SpeakerName.text = line.SpeakerName;
        SetPortrait(line.PortraitA);
        StartCoroutine(TypeLine(line));
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
                        DialogueText.text = $"<i>{line.Text}</i>";
                    }
                    else
                    {
                        DialogueText.text = line.Text;
                    }
                    line.IsSkipped = true;
                    SetPortrait(line.PortraitA);
                    yield return new WaitForSeconds(0.02f);
                    ToggleContinuePrompt(true);
                    break;
                }

                if (line.Type == DialogueLine.DialogueType.Action)
                {
                    if (characterIndex == 0)
                    {
                        DialogueText.text += "<i>";
                    }
                }

                DialogueText.text += c;

                if (line.Type == DialogueLine.DialogueType.Action)
                {
                    if (characterIndex == line.Text.Length - 1)
                    {
                        DialogueText.text += "</i>";
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
                }
            }


            if (!line.IsSkipped)
            {
                ToggleContinuePrompt(true);
            }
        }
        
    }

    public void ToggleContinuePrompt(bool showOrHide)
    {
        if (_hasContinuePrompt)
        {
            ContinuePrompt.gameObject.SetActive(showOrHide);
        }
        else if (showOrHide)
        {
            DisplayNextLineWithDelay(2f); // 2f
        }
    }

    void EndDialogue()
    {
        Debug.Log("Dialogue ended.");
        CutsceneManager.EndDialogue(CurrentDialogue);
    }

}
