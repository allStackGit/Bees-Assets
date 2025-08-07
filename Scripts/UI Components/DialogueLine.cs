using Assets.Scripts;
using System.Collections.Generic;
using UnityEditor.Rendering;
using UnityEngine;

public class DialogueLine
{
    public string SpeakerName;
    public Sprite PortraitA, PortraitB;
    public string Text;
    public float PauseDuration = 0f;
    public DialogueType Type;

    public enum DialogueType
    {
        Speaking,
        Pause,
        Action
    }
    public DialogueLine(string name, Sprite[] portraits, string dialogueText)
    {
        SpeakerName = name;
        PortraitA = portraits[0];
        PortraitB = portraits[1];
        Text = dialogueText;
        Type = DialogueType.Speaking;
    }
    public DialogueLine(string name, Sprite[] portraits, float pauseDuration)
    {
        SpeakerName = name;
        PortraitA = portraits[0];
        PortraitB = portraits[1];
        PauseDuration = pauseDuration;
        Type = DialogueType.Pause;
    }

    public DialogueLine(string name, Sprite[] portraits, string actionText, float pauseDuration)
    {
        SpeakerName = name;
        PortraitA = portraits[0];
        PortraitB = portraits[1];
        PauseDuration = pauseDuration;
        Text = actionText;
        Type = DialogueType.Action;
    }

}