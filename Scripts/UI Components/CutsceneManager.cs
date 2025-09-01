using Assets.Scripts;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

public class CutsceneManager : MonoBehaviour
{
    public PlayableDirector Director;
    public DialogueManager DialogueManager;
    public GameObject CutsceneCanvas;
    public Stage Stage;
    public List<DialogueLine> PlutoLines_Anomaly, PlutoLines_Reinforcements;
    public List<List<DialogueLine>> AllDialogues;
    public bool PlutoLines_Anomaly_Completed = false;
    public bool HitDialogueBreak = false;
    public Action EndDialogueAction;
    public bool HasEndDialogueAction = false;

    public TimelineAsset PlutoIntroCutscene;

    public static Dictionary<string, Sprite[]> Portraits = new Dictionary<string, Sprite[]>();


    public List<DialogueLine> CurrentDialogueLines;

    public void Setup(Action endDialogueAction)
    {
        if (endDialogueAction != null)
        {
            EndDialogueAction = endDialogueAction;
            HasEndDialogueAction = true;
        }
        Portraits["Samuel"] = Resources.LoadAll<Sprite>("Sprites/Portraits/samuel_chat");
        Portraits["Tom"] = Resources.LoadAll<Sprite>("Sprites/Portraits/starman");
        Portraits["High Command"] = Resources.LoadAll<Sprite>("Sprites/Portraits/highcommand");

        PlutoLines_Anomaly = new List<DialogueLine>
        {
            new DialogueLine("Samuel", Portraits["Samuel"], $"Good morning, Commander {ConfigData.UserProgressData.PlayerName}! I brought your coffee."),
            new DialogueLine("Samuel", Portraits["Samuel"], 1),
            new DialogueLine("Samuel", Portraits["Samuel"], "I agree, it doesn't taste as good as Earth coffee. Or even Mars coffee. It's alright, we'll both get out of Pluto soon enough."),
            new DialogueLine("Samuel", Portraits["Samuel"], "The tech gets a notification of some kind.", 2),
            new DialogueLine("Samuel", Portraits["Samuel"], "Oh, that's odd. A scout is reporting an unidentified vessel approaching military airspace. What should we do?"),
            new DialogueLine("Samuel", Portraits["Samuel"], 1),
            new DialogueLine("Samuel", Portraits["Samuel"], "Right away, sir. Contacting the vessel."),
            new DialogueLine("Samuel", Portraits["Samuel"], 1),
            new DialogueLine("Samuel", Portraits["Samuel"], "It isn’t responding, sir."),
            new DialogueLine("Samuel", Portraits["Samuel"], 1),
            new DialogueLine("Samuel", Portraits["Samuel"], "Understood, sir. We’ll send Lieutenant Tom out immediately."),
            new DialogueLine(),

            new DialogueLine("Tom", Portraits["Tom"], $"This is Gunship D-4 reporting to command. I’m approaching the unidentified vessel now."),
            new DialogueLine(),
            new DialogueLine("Tom", Portraits["Tom"], "Unidentified vessel, you are in United Earth military airspace. Identify yourself now."),
            new DialogueLine("Samuel", Portraits["Samuel"], 1),
            new DialogueLine("Samuel", Portraits["Samuel"], "Still nothing, even on local communications?"),
            new DialogueLine("Tom", Portraits["Tom"], "Negative."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Strange. It doesn’t seem hostile. What are your orders- oh, we’re getting a call from High Command."),
            new DialogueLine("High Command", Portraits["High Command"], $"Commander {ConfigData.UserProgressData.PlayerName}, we have received reports of an alien vessel in Pluto airspace. We cannot allow it to infiltrate our territory. Shoot it down."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Shoot it down? We don’t even know what it is! Who even reported this?"),
            new DialogueLine("High Command", Portraits["High Command"], "Those are your orders, Commander."),
            new DialogueLine("Samuel", Portraits["Samuel"], "But- oh, they disconnected. Looks like we have to attack, sir."),
            new DialogueLine(),
            new DialogueLine("Tom", Portraits["Tom"], "What are your orders, Commander?"),
            new DialogueLine("Samuel", Portraits["Samuel"], "In order to attack, he’ll need to get in range. Once he's in range, he can attack the ship."),
            new DialogueLine(),
            new DialogueLine("Tom", Portraits["Tom"], "Well, that was hardly a fight."),
            new DialogueLine("Samuel", Portraits["Samuel"], "I hope it wasn’t an innocent civilian. Why would High Command even order that?"),
            new DialogueLine(),
            new DialogueLine("Tom", Portraits["Tom"], "Uh, Commander? Are you picking this up?"),
            new DialogueLine("Samuel", Portraits["Samuel"], "You need to get out of there, now!"),
            new DialogueLine(),
            new DialogueLine("Samuel", Portraits["Samuel"], "Their fleet is huge! We need to contact High Command immediately!"),
            new DialogueLine("Samuel", Portraits["Samuel"], "Dial-up noises", 2),
            new DialogueLine("Samuel", Portraits["Samuel"], "Communications are down, sir. What should we do?"),
            new DialogueLine("Samuel", Portraits["Samuel"], 1),
            new DialogueLine("Samuel", Portraits["Samuel"], "Understood. Preparing our fleet to deploy, sir."),

        };

        PlutoLines_Reinforcements = new List<DialogueLine>
        {
            new DialogueLine("Samuel", Portraits["Samuel"], "The strange alien fleet has called in reinforcements. We have to rally our ships and form a defense, quickly!"),
        };

        AllDialogues = new List<List<DialogueLine>> { PlutoLines_Anomaly, PlutoLines_Reinforcements };

    }
    public void HideIntroMessage()
    {
        CutsceneCanvas.SetActive(false);
    }
    public void HideDialogue()
    {
        DialogueManager.gameObject.SetActive(false);
    }
    public void EndCutscene()
    {
        CutsceneCanvas.SetActive(false);
        //DialogueCanvas.SetActive(false);
        Stage.EnablePlayerControl();
    }

    public void StartCutScene()
    {
        CutsceneCanvas.SetActive(true);

        Director.playableAsset = PlutoIntroCutscene;
        Director.Play();
    }
    public void ShowDialogue()
    {
        DialogueManager.gameObject.SetActive(true);
    }
    public void PlaySingleDialogueLine(DialogueLine line)
    {
        ShowDialogue();
        DialogueManager.Setup(this);
        DialogueManager.SetPortrait(line.PortraitA);
        DialogueManager.StartDialogue(new List<DialogueLine> { line }, false);
    }
    public void StartDialogue(DialogueManager.Dialogues dialogueType)
    {
        ShowDialogue();
        switch (dialogueType)
        {
            case DialogueManager.Dialogues.Pluto_Anomaly:
                DialogueManager.Setup(this, DialogueManager.Dialogues.Pluto_Anomaly);
                DialogueManager.SetPortrait(PlutoLines_Anomaly[0].PortraitA);
                DialogueManager.StartDialogue(PlutoLines_Anomaly, false);
                break;
        }

    }
    private ScaledTimer _retryDialogue = new ScaledTimer();
    public void ContinueDialogue()
    {
        if (HitDialogueBreak)
        {
            Debug.Log("Continuing dialogue in cutscene manager.");
            ShowDialogue();
            HitDialogueBreak = false;
            DialogueManager.DisplayNextLine();
        }
        else
        {
            _retryDialogue.Reuse(1, ContinueDialogue, false);
            Stage.PrimaryLevel.AddTimer(_retryDialogue);
        }

    }
    public void BreakDialogue()
    {
        Debug.Log("Breaking dialogue in cutscene manager.");
        HitDialogueBreak = true;
        DialogueManager.gameObject.SetActive(false);
    }
    public void EndDialogue(DialogueManager.Dialogues dialogueType)
    {
        DialogueManager.gameObject.SetActive(false);
        if (HasEndDialogueAction)
        {
            EndDialogueAction();
        }
    }


}
