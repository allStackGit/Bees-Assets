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

    private Queue<DialogueLine> dialogueLines = new Queue<DialogueLine>();
    private bool _hasContinuePrompt;

    public void Setup(Level level, CutsceneManager cutsceneManager)
    {
        Level = level;
        CutsceneManager = cutsceneManager;
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
        DialogueText.text = "";
        int characterIndex = 0;
        bool aOrB = false;
        ToggleContinuePrompt(false);

        if (line.PauseDuration > 0)
        {
            yield return new WaitForSeconds(line.PauseDuration);
        }
        else
        {
            foreach (char c in line.Text)
            {
                DialogueText.text += c;
                characterIndex++;
                if (characterIndex == line.Text.Length)
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

                yield return new WaitForSeconds(0.01f);
            }
        }



        ToggleContinuePrompt(true);
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
        CutsceneManager.EndDialogue();
    }

}
