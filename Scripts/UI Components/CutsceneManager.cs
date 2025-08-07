using UnityEngine;
using UnityEngine.Playables;

public class CutsceneManager : MonoBehaviour
{
    public Sprite SamuelPortaitA, SamuelPortaitB;

    public PlayableDirector Director;
    public DialogueManager DialogueManager;
    public GameObject CutsceneCanvas, DialogueCanvas;
    public Stage Stage; 


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
        DialogueManager.StartDialogue(DialogueLine.PlutoLines_TechnicianIntro, false);
    }

}
