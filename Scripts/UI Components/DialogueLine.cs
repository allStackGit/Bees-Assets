using Assets.Scripts;
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

}