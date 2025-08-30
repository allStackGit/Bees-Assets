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
    public Level Level;
    public Dialogues CurrentDialogue;

    private Queue<DialogueLine> dialogueLines = new Queue<DialogueLine>();
    private bool _hasContinuePrompt;

    public enum Dialogues
    {
        Pluto_TechnicianIntro,
        Pluto_TomIntro,
    }

    public void Setup(Level level, CutsceneManager cutsceneManager, Dialogues dialogueType)
    {
        Level = level;
        CutsceneManager = cutsceneManager;
        CurrentDialogue = dialogueType;
    }

    public void SwitchDialogue(Dialogues dialogueType)
    {
        CurrentDialogue = dialogueType;
    }

    public void StartDialogue(List<DialogueLine> lines, bool hasContinueButton)
    {
        Debug.Log("Starting dialogue in dialogue manager.");
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
            EndDialogue();
            return;
        }

        DialogueLine line = dialogueLines.Dequeue();
        StopAllCoroutines();
        SpeakerName.text = line.SpeakerName;

        StartCoroutine(TypeLine(line));
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
                yield return new WaitForSeconds(line.PauseDuration); 
            }



            ToggleContinuePrompt(true);
        }
        
    }

    private ScaledTimer _continuePromptTimer = new ScaledTimer();
    public void ToggleContinuePrompt(bool showOrHide)
    {
        if (_hasContinuePrompt)
        {
            ContinuePrompt.gameObject.SetActive(showOrHide);
        }
        else if (showOrHide)
        {
            _continuePromptTimer.Reuse(1, DisplayNextLine, false);
            Level.AddTimer(_continuePromptTimer);
        }
    }

    void EndDialogue()
    {
        Debug.Log("Dialogue ended.");
        CutsceneManager.EndDialogue(CurrentDialogue);
    }

}
