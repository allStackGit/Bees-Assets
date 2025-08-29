using Assets.Scripts;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

public class CutsceneManager : MonoBehaviour
{
    public Sprite SamuelPortaitA, SamuelPortaitB;

    public PlayableDirector Director;
    public DialogueManager DialogueManager;
    public GameObject CutsceneCanvas, DialogueCanvas;
    public Stage Stage;
    public List<DialogueLine> PlutoLines_TechnicianIntro, PlutoLines_TomIntro;
    public bool PlutoLines_TechnicianIntro_Completed, PlutoLines_TomIntro_Completed = false;

    public TimelineAsset PlutoIntroCutscene;

    public static Dictionary<string, Sprite[]> Portraits = new Dictionary<string, Sprite[]>();


    public List<DialogueLine> CurrentDialogueLines;

    public void Setup()
    {
        Portraits["Samuel"] = Resources.LoadAll<Sprite>("Sprites/Portraits/samuel_chat");
        Portraits["Tom"] = Resources.LoadAll<Sprite>("Sprites/Portraits/starman");

        PlutoLines_TechnicianIntro = new List<DialogueLine>
        {
            new DialogueLine("Samuel", Portraits["Samuel"], $"Good morning, Commander {ConfigData.UserProgressData.PlayerName}! I brought your coffee."),
            new DialogueLine("Samuel", Portraits["Samuel"], 3),
            new DialogueLine("Samuel", Portraits["Samuel"], "I agree, it doesn't taste as good as Earth coffee. Or even Mars coffee. It's alright, we'll both get out of Pluto soon enough."),
            new DialogueLine("Samuel", Portraits["Samuel"], "The tech gets a notification of some kind.", 2),
            new DialogueLine("Samuel", Portraits["Samuel"], "Oh, that's odd. A scout is reporting an unidentified vessel approaching military airspace. What should we do?"),
            new DialogueLine("Samuel", Portraits["Samuel"], 2),
            new DialogueLine("Samuel", Portraits["Samuel"], "Right away, sir. Contacting the vessel."),
            new DialogueLine("Samuel", Portraits["Samuel"], 2),
            new DialogueLine("Samuel", Portraits["Samuel"], "It isn’t responding, sir."),
            new DialogueLine("Samuel", Portraits["Samuel"], 2),
            new DialogueLine("Samuel", Portraits["Samuel"], "Understood, sir. We’ll send Lieutenant Tom out immediately.")
        };

        PlutoLines_TomIntro = new List<DialogueLine> {
            new DialogueLine("Tom", Portraits["Tom"], $"This is Gunship P-4 reporting to command. I’m approaching the unidentified vessel now."),
            new DialogueLine(),
        };
    }
    public void EnablePlayerControl()
    {
        Stage.EnablePlayerControl();
    }
    public void HideIntroMessage()
    {
        CutsceneCanvas.SetActive(false);
    }
    public void HideDialogue()
    {
        DialogueCanvas.SetActive(false);
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
        DialogueCanvas.SetActive(true);
    }
    public void StartDialogue(DialogueManager.Dialogues dialogueType)
    {
        switch (dialogueType)
        {
            case DialogueManager.Dialogues.Pluto_TechnicianIntro:
                DialogueManager.Setup(Stage.PrimaryLevel, this, DialogueManager.Dialogues.Pluto_TechnicianIntro);
                DialogueManager.SetPortrait(PlutoLines_TechnicianIntro[0].PortraitA);
                DialogueManager.StartDialogue(PlutoLines_TechnicianIntro, false);
                break;

            case DialogueManager.Dialogues.Pluto_TomIntro:
                DialogueManager.SwitchDialogue(DialogueManager.Dialogues.Pluto_TomIntro);
                DialogueManager.SetPortrait(PlutoLines_TomIntro[0].PortraitA);
                DialogueManager.StartDialogue(PlutoLines_TomIntro, false);
                break;
        }

    }
    public void BreakDialogue()
    {
        DialogueCanvas.SetActive(false);
    }
    public void EndDialogue(DialogueManager.Dialogues dialogueType)
    {
        switch (dialogueType)
        {
            case DialogueManager.Dialogues.Pluto_TechnicianIntro:
                PlutoLines_TechnicianIntro_Completed = true;
                DialogueCanvas.SetActive(false);
                break;
            case DialogueManager.Dialogues.Pluto_TomIntro:
                PlutoLines_TomIntro_Completed = true;
                DialogueCanvas.SetActive(false);
                break;
        }
    }


}
