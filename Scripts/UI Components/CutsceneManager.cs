using Assets.Scripts;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;

public class CutsceneManager : MonoBehaviour
{
    public Sprite SamuelPortaitA, SamuelPortaitB;

    public PlayableDirector Director;
    public DialogueManager DialogueManager;
    public GameObject CutsceneCanvas, DialogueCanvas;
    public Stage Stage;
    public List<DialogueLine> PlutoLines_TechnicianIntro;

    public void Setup()
    {
            PlutoLines_TechnicianIntro = new List<DialogueLine>
            {
                new DialogueLine("Fleet Technician", Resources.LoadAll<Sprite>("Sprites/Portraits/samuel_chat"), $"Good morning, Commander {ConfigData.UserProgressData.PlayerName}! I brought your coffee."),
                new DialogueLine("Fleet Technician", Resources.LoadAll<Sprite>("Sprites/Portraits/samuel_chat"), 2),
                new DialogueLine("Fleet Technician", Resources.LoadAll<Sprite>("Sprites/Portraits/samuel_chat"), "I agree, it doesn't taste as good as Earth coffee. Or even Mars coffee. It's alright, we'll both get out of Pluto soon enough."),
                new DialogueLine("Fleet Technician", Resources.LoadAll<Sprite>("Sprites/Portraits/samuel_chat"), "The tech gets a notification of some kind."),
                new DialogueLine("Fleet Technician", Resources.LoadAll<Sprite>("Sprites/Portraits/samuel_chat"), "Oh, that's odd. A scout is reporting an unidentified vessel approaching military airspace. What should we do?"),
                new DialogueLine("Fleet Technician", Resources.LoadAll<Sprite>("Sprites/Portraits/samuel_chat"), 2),
                new DialogueLine("Fleet Technician", Resources.LoadAll<Sprite>("Sprites/Portraits/samuel_chat"), "Right away, sir. Contacting the vessel."),
                new DialogueLine("Fleet Technician", Resources.LoadAll<Sprite>("Sprites/Portraits/samuel_chat"), 2),
                new DialogueLine("Fleet Technician", Resources.LoadAll<Sprite>("Sprites/Portraits/samuel_chat"), "It isn’t responding, sir."),
                new DialogueLine("Fleet Technician", Resources.LoadAll<Sprite>("Sprites/Portraits/samuel_chat"), 2),
                new DialogueLine("Fleet Technician", Resources.LoadAll<Sprite>("Sprites/Portraits/samuel_chat"), "Understood, sir. We’ll send Lieutenant Tom out immediately.")
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
        DialogueCanvas.SetActive(false);
        Stage.EnablePlayerControl();
    }

    public void StartCutScene()
    {
        CutsceneCanvas.SetActive(true);
        DialogueManager.Setup(Stage.PrimaryLevel);
        Director.Play();
    }

    public void StartDialogue()
    {
        DialogueCanvas.SetActive(true);
        StartPlutoTechnicianIntro();
    }

    private void StartPlutoTechnicianIntro()
    {
        DialogueManager.StartDialogue(PlutoLines_TechnicianIntro, false);
    }

}
