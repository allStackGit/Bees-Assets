using System.Collections.Generic;
using UnityEngine;

public class DialogueLine
{
    public string SpeakerName;
    public Sprite PortraitA, PortraitB;
    public string Text;
    public float PauseDuration = 0f;

    public DialogueLine(string name, Sprite[] portraits, string dialogueText)
    {
        SpeakerName = name;
        PortraitA = portraits[0];
        PortraitB = portraits[1];
        Text = dialogueText;
    }
    public DialogueLine(string name, Sprite[] portraits, float pauseDuration)
    {
        SpeakerName = name;
        PortraitA = portraits[0];
        PortraitB = portraits[1];
        PauseDuration = pauseDuration;
    }


    private static string PauseFor1Second = " ";
    private static string PauseFor2Seconds = "                                 "; // 33 characters, 1 second of pause + base pause
    public static List<DialogueLine> PlutoLines_TechnicianIntro = new List<DialogueLine>
    {
        new DialogueLine("Fleet Technician", Resources.LoadAll<Sprite>("Sprites/Portraits/samuel_chat"), "Good morning, Commander [Player name]! I brought your coffee."),
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