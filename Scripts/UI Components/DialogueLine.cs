using Assets.Scripts;
using System.Collections.Generic;
using UnityEngine;

public class DialogueLine
{
    public string SpeakerName;
    public Sprite PortraitA, PortraitB;
    public string Text;
    public float PauseDuration = 0f;
    public bool IsSkipped, IsOver, IsUnknown;
    public DialogueType Type;

    public enum DialogueType
    {
        Speaking,
        Pause,
        Action,
        Break
    }
    public DialogueLine(string name, Sprite[] portraits, string dialogueText, bool isUnknown = false)
    {
        SpeakerName = name;
        PortraitA = portraits[0];
        PortraitB = portraits[1];
        Text = dialogueText;
        Type = DialogueType.Speaking;
        IsUnknown = isUnknown;
    }
    public DialogueLine(string name, Sprite[] portraits, float pauseDuration)
    {
        SpeakerName = name;
        PortraitA = portraits[0];
        PortraitB = portraits[1];
        PauseDuration = pauseDuration;
        Text = "";
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
    public DialogueLine()
    {
        Type = DialogueType.Break;
    }

}