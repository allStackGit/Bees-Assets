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
    public List<DialogueLine> PlutoLines_Anomaly, PlutoLines_Reinforcements, PlutoLines_BluerPastures;
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
        Portraits["Oviya"] = Resources.LoadAll<Sprite>("Sprites/Portraits/oviya_chat");
        Portraits["Marco"] = Resources.LoadAll<Sprite>("Sprites/Portraits/marco_chat");
        Portraits["Yoshiko"] = Resources.LoadAll<Sprite>("Sprites/Portraits/yoshiko_chat");
        Portraits["Joey"] = Resources.LoadAll<Sprite>("Sprites/Portraits/joey_chat");

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

            new DialogueLine("Samuel", Portraits["Samuel"], "Okay, commander, it's up to you to lead us to victory."),

            new DialogueLine("Oviya", Portraits["Oviya"], "This is a scout! They're the fastest ship around, and- oh, right! I'm Oviya, your scout commander. Sorry, Commander! Anyway, use the scout to… well, scout the battlefield."),

            new DialogueLine("Oviya", Portraits["Oviya"], "They get around fast, so as long as you keep giving orders, they probably won't get hit by enemy fire. Oh, but they don't have any guns, so don't try fighting with them."),

            new DialogueLine("Oviya", Portraits["Oviya"], 1),

            new DialogueLine("Oviya", Portraits["Oviya"], "Scouts also come loaded up with five beacons! You can drop them anywhere and they'll detect enemies."),

            new DialogueLine("Samuel", Portraits["Samuel"], "You should try to find out where the enemy is with your scouts, then form a plan of attack."),

            new DialogueLine("Marco", Portraits["Marco"], "I'll be commanding your gunships. They're fast-flying dogfighting specialists. Use their speed to your advantage if you can."),
            new DialogueLine("Marco", Portraits["Marco"], "Even if they can't fly as well as me, they'll still be good at dodging fire."),

            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "Alright! It's been a while since we've had a good fight. I'm your dreadnought commander."),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "These babies are made to brawl. They can take a lotta hits and dish it right back! Keep ‘em out front and watch ‘em tear it up. Woohoo!"),

            new DialogueLine("Joey", Portraits["Joey"], "Alrighty, Commander, I'm commanding yer frigates. They're yer explosives experts. They can't shoot far, but they sure pack a wallop."),
            new DialogueLine("Joey", Portraits["Joey"], "Those rockets will do some serious damage, and they can even hit multiple targets inside the blast radius."),

            new DialogueLine("Samuel", Portraits["Samuel"], "Great work, commander. We’ve kept them at bay for now."),
            new DialogueLine("Oviya", Portraits["Oviya"], "Some of our Scouts are already finding more fleets. We- um, how do I put this… We can’t win. Not here."),
            new DialogueLine("Samuel", Portraits["Samuel"], "We’ll have to send an emergency evacuation alert, then."),

        };

        PlutoLines_BluerPastures = new List<DialogueLine>
        {
            new DialogueLine("Samuel", Portraits["Samuel"], "Scouts are reporting overwhelming reinforcements from the enemy. We can’t outlast them, but we have to buy enough time for those on the planet to evacuate."),

            new DialogueLine("Samuel", Portraits["Samuel"], "Commander, these… Bees?"),
            new DialogueLine("Oviya", Portraits["Oviya"], "They do look like Bees."),
            new DialogueLine("Joey", Portraits["Joey"], "Let’s just call ‘em that. It’s easier than U.F.O.s."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Um, right, whatever they are, they’re still coming. In order to evacuate Pluto base, we have to keep the Bees from reaching the surface."),

            new DialogueLine("Samuel", Portraits["Samuel"], "If they get to that point, we’re going to start losing people and ships before they can lift off."),
            new DialogueLine("Samuel", Portraits["Samuel"], "This is a full emergency order, so personnel have to be on space-ready vessels within 5 minutes. As soon as we have the fleet ready to leave, we’ll evacuate out of here at full speed."),

            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "We’ve got this, commander! Dreadnoughts standing by."),
            new DialogueLine("Marco", Portraits["Marco"], "Gunships at the ready."),
            new DialogueLine("Joey", Portraits["Joey"], "Frigates ready to go!"),
            new DialogueLine("Oviya", Portraits["Oviya"], "I’ve got the couts mapping an escape route."),



        };

        AllDialogues = new List<List<DialogueLine>> { PlutoLines_Anomaly, PlutoLines_Reinforcements, PlutoLines_BluerPastures };

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
    public void PlaySingleDialogueLine(DialogueLine line, bool isLastDialogue = false)
    {
        HitDialogueBreak = false;
        PlayDialogueSection(new List<DialogueLine> { line }, isLastDialogue);
    }
    public void PlayDialogueSection(List<DialogueLine> lines, bool isLastDialogue = false)
    {
        HitDialogueBreak = false;
        ShowDialogue();
        DialogueManager.Setup(this);
        DialogueManager.SetPortrait(lines[0].PortraitA);
        DialogueManager.StartDialogue(lines, false, isLastDialogue);
    }
    public void StartDialogue(DialogueManager.Dialogues dialogueType)
    {
        ShowDialogue();
        switch (dialogueType)
        {
            case DialogueManager.Dialogues.Pluto_Anomaly:
                DialogueManager.Setup(this, DialogueManager.Dialogues.Pluto_Anomaly);
                DialogueManager.SetPortrait(PlutoLines_Anomaly[0].PortraitA);
                DialogueManager.StartDialogue(PlutoLines_Anomaly, false, false);
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
