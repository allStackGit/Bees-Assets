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
    public List<DialogueLine> PlutoLines_Anomaly, PlutoLines_Reinforcements, PlutoLines_BluerPastures, PlutoToNeptune, Neptune_SeizeTheMeans;
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
        Portraits["Wesley"] = Resources.LoadAll<Sprite>("Sprites/Portraits/wesley_chat");

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
            new DialogueLine("Oviya", Portraits["Oviya"], "I’ve got the scouts mapping an escape route."),

            new DialogueLine("Samuel", Portraits["Samuel"], "Commander, Bees are firing down onto the surface! You need to stop them!"),

            new DialogueLine("Joey", Portraits["Joey"], "That ship’s blasts are splitting up on impact. Don’t group yer ships up near it."),

            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "So those are suicide ships! Don’t let them near any of our ships."),
            new DialogueLine("Samuel", Portraits["Samuel"], "I hope they aren’t manned…"),
            new DialogueLine("Marco", Portraits["Marco"], "This is a war, boy, worry about it later."),

            new DialogueLine("Samuel", Portraits["Samuel"], "Comms with the surface went down, commander."),
            new DialogueLine("Marco", Portraits["Marco"], "They’re a lost cause. Get out of here, now."),
            new DialogueLine("Samuel", Portraits["Samuel"], "But-"),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "He’s right, Sam. We can’t lose everyone."),

            new DialogueLine("Samuel", Portraits["Samuel"], "All personnel are ready to leave."),
            new DialogueLine("Oviya", Portraits["Oviya"], "Go quickly! They’re closing in on our escape route!"),
            new DialogueLine("Samuel", Portraits["Samuel"], "Right!"),
            new DialogueLine("Marco", Portraits["Marco"], "Get moving, people!"),

            new DialogueLine("Samuel", Portraits["Samuel"], "Incredible work, commander! We didn’t lose anyone in the evacuation."),
            new DialogueLine("Marco", Portraits["Marco"], "Impressive."),



        };

        PlutoToNeptune = new List<DialogueLine>
        {
            new DialogueLine("Samuel", Portraits["Samuel"], "What do we do now? Those… bees outnumber us."),
            new DialogueLine("Marco", Portraits["Marco"], "Badly."),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "That doesn’t mean we lose!"),
            new DialogueLine("Joey", Portraits["Joey"], "But it’s a heck of a lot harder."),
            new DialogueLine("Marco", Portraits["Marco"], "And I’m not going into a fight like that unless I absolutely have to."),
            new DialogueLine("Oviya", Portraits["Oviya"], "What we need is allies."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Just because interplanetary comms are cut off doesn’t mean others aren’t still out there… right?"),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "They couldn’t have destroyed all the United Fleet. It’s the finest military presence in the solar system!"),
            new DialogueLine("Marco", Portraits["Marco"], "The <i>only</i> military presence in the solar system. Until now. And we don’t know how many more bees there are."),
            new DialogueLine("Samuel", Portraits["Samuel"], "There’s no way they could take Mars… or even Earth."),
            new DialogueLine("Marco", Portraits["Marco"], "No way to know."),
            new DialogueLine("Oviya", Portraits["Oviya"], "The next closest human settlement is Neptune. Commander, I recommend we go there."),
            new DialogueLine("Samuel", Portraits["Samuel"], "There might be survivors!"),
            new DialogueLine("Joey", Portraits["Joey"], "It’s just a Jensen mining facility, so we won’t find any more United Fleet help. But we can help anyone that’s sticking it out."),
            new DialogueLine("Samuel", Portraits["Samuel"], "And our production carriers can use those to make us ships!"),
            new DialogueLine("Joey", Portraits["Joey"], "If there’s still anything left there."),
            new DialogueLine("Oviya", Portraits["Oviya"], "It’s our best option right now. If Neptune is wiped out, then we move on."),
            new DialogueLine("Joey", Portraits["Joey"], "And if it’s not, we have to deal with Wesley…"),
        };

        Neptune_SeizeTheMeans = new List<DialogueLine>
        {
            new DialogueLine("Samuel", Portraits["Samuel"], "We’re approaching Neptune to see if we can gather any allies there."),
            new DialogueLine("Samuel", Portraits["Samuel"], "The scout team has reported movement, but were unable to determine if it’s friendly or not. We’ll just have to find out."),

            new DialogueLine("Samuel", Portraits["Samuel"], "Sir, we’re getting an emergency signal from somewhere on Neptune."),
            new DialogueLine("Wesley", Portraits["Wesley"], "-day, Mayd- this is an emerg- enem- facility dest- need evac-"),
            new DialogueLine("Samuel", Portraits["Samuel"], "It’s very faint, but it’s clear we need to go help."),
            new DialogueLine("Oviya", Portraits["Oviya"], "It looks like the bees are gathered around ore-rich asteroids around the planet. Scouts are reporting the mining facility on the surface is flattened."),
            new DialogueLine("Joey", Portraits["Joey"], "There’s an underground bunker beneath the facility. I’ll be damned if that’s not where the signal is coming from."),
            new DialogueLine("Marco", Portraits["Marco"], "Let’s clear out those bees so we can land safely."),

            new DialogueLine("Samuel", Portraits["Samuel"], "Strange, that ship doesn’t seem to be firing back."),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "Why won’t they fight?! Cowards."),
            new DialogueLine("Marco", Portraits["Marco"], "Can it, Yoshiko. Look, they’re attached to the asteroid’s surface."),
            new DialogueLine("Joey", Portraits["Joey"], "Mining ships. I know Jensen was working on a model like that. Just less… insectile."),

            new DialogueLine("Samuel", Portraits["Samuel"], "Great work, commander. We can check the facility now."),

            new DialogueLine("Samuel", Portraits["Samuel"], "We’ve lost all the squads we sent out, commander. There’s no safe way onto Neptune."),
            new DialogueLine("Marco", Portraits["Marco"], "There’s not much time to hang around here. The closest United Fleet base is on Uranus. We can find help there. Those people in the mining facility will just have to hang on a bit longer until others can come back."),
            new DialogueLine("Oviya", Portraits["Oviya"], "Scouts are reporting a bee blockade between here and Uranus. We’ll have to break it."),

            
            new DialogueLine("Samuel", Portraits["Samuel"], "Commander, we’ve rescued the mining personnel from their bunker. Their leader has come to speak with you."),
            new DialogueLine("Wesley", Portraits["Wesley"], "I’m no leader. That would be my manager, Derek. I’m simply the regional head of the accounting department for Jensen Industries."),
            new DialogueLine("Joey", Portraits["Joey"], "Same as always."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Ah, greetings, Joey."),
            new DialogueLine("Joey", Portraits["Joey"], "Howdy. Where’s Derek, then?"),
            new DialogueLine("Wesley", Portraits["Wesley"], "Derek is no longer with us. He didn’t make it to the bunker in time. I’ve been the de facto leader for the remaining Jensen personnel."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Then why did you say-"),
            new DialogueLine("Joey", Portraits["Joey"], "Don’t bother. Wesley, tell us what we need to know for this war against the bees."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Bees? Is that what’s attacking us? I was not aware they had the biology to survive in space. They’re hardly surviving on Earth any longer, due to-"),
            new DialogueLine("Marco", Portraits["Marco"], "They’re not bees."),
            new DialogueLine("Joey", Portraits["Joey"], "They just look like ‘em. Easier to call ‘em that."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Ah. Right. Of course. Well, these… bees quickly disposed of our contracted defense team and started bombing the surface. The emergency code was sent, and we all attempted to secure ourselves in the underground bunker. Not everyone made it."),
            new DialogueLine("Marco", Portraits["Marco"], "Great. So we have an accountant-"),
            new DialogueLine("Wesley", Portraits["Wesley"], "Regional head of the accounting department."),
            new DialogueLine("Marco", Portraits["Marco"], "………"),
            new DialogueLine("Marco", Portraits["Marco"], "We have that and a bunch of miners, then? This isn’t really helping our chances."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Well, um, we do have a line of prototype factory ships, capable of mining ore-rich asteroids, as well as personnel trained to man them."),
            new DialogueLine("Joey", Portraits["Joey"], "And plenty of those asteroids nearby. Well, I’ll be, Wesley, this really is helpful"),
            new DialogueLine("Wesley", Portraits["Wesley"], "They are property of Jensen Corporation, and all personnel including myself are on their payroll. I’m sure we can reach some sort of agreement for their use, perhaps a loan."),
            new DialogueLine("Wesley", Portraits["Wesley"], "But of course, these ships are the intellectual property of Jensen, so I can’t have any of you stealing these plans."),
            new DialogueLine("Samuel", Portraits["Samuel"], "We don’t have any way to contact Jensen Corporation, Wesley."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Well, that is unfortunate. I do need approval from a home office manager before I-"),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "How many punches does it take to get him to shut up?"),
            new DialogueLine("Joey", Portraits["Joey"], "I dunno, you’ll have to just try until he quiets down."),
            new DialogueLine("Wesley", Portraits["Wesley"], "N-nw, now, I’m sure w-we don’t have to resort to such… b-barbaric means! We’re all adults here!"),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "I'm thinking at least a dozen. Six to the nose and six to the stomach."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Ahh! Please don’t engage in a physical altercation, it’s far from necessary! Please, use the factory ships however you please! We can negotiate fair compensation once we’ve made contact with my superiors."),
            new DialogueLine("Joey", Portraits["Joey"], "There ya go, commander. Factory ships at your disposal."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Thank you, Wesley."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Yes, of course, just leave my nose intact, please. It’s my best feature."),
            
            
            /*
             * 
             *  new DialogueLine("Samuel", Portraits["Samuel"], ""),
             *  new DialogueLine("Marco", Portraits["Marco"], ""),
             *  new DialogueLine("Oviya", Portraits["Oviya"], ""),
             *  new DialogueLine("Joey", Portraits["Joey"], ""),
             *  new DialogueLine("Wesley", Portraits["Wesley"], ""),
             *  new DialogueLine("Yoshiko", Portraits["Yoshiko"], ""),
             * */

            /*
             Samuel: Commander, we’ve rescued the mining personnel from their bunker. Their leader has come to speak with you.
Wesley: I’m no leader. That would be my manager, Derek. I’m simply the regional head of the accounting department for Jensen Industries.
Joey: Same as always.
Wesley: Ah, greetings, Joey.
Joey: Howdy. Where’s Derek, then?
Wesley: Derek is no longer with us. He didn’t make it to the bunker in time. I’ve been the de facto leader for the remaining Jensen personnel.
Samuel: Then why did you say-
Joey: Don’t bother. Wesley, tell us what we need to know for this war against the bees.
Wesley: Bees? Is that what’s attacking us? I was not aware they had the biology to survive in space. They’re hardly surviving on Earth any longer, due to-
Marco: They’re not bees.
Joey: They just look like ‘em. Easier to call ‘em that.
Wesley: Ah. Right. Of course. Well, these… bees quickly disposed of our contracted defense team and started bombing the surface. The emergency code was sent, and we all attempted to secure ourselves in the underground bunker. Not everyone made it.
Marco: Great. So we have an accountant-
Wesley: Regional head of the accounting department.
Marco: ………
Marco: We have that and a bunch of miners, then? This isn’t really helping our chances.
Wesley: Well, um, we do have a line of prototype factory ships, capable of mining ore-rich asteroids, as well asand personnel trained to man them.
Joey: And plenty of those asteroids nearby. Well, I’ll be, Wesley, this really isis really helpful.
Wesley: They are property of Jensen Corporation, and all personnel including myself are on their payroll. I’m sure we can reach some sort of agreement for their use, perhaps a loan. But of course, these ships are the intellectual property of Jensen, so I can’t have any of you stealing these plans.
Samuel: We don’t have any way to contact Jensen Corporation, Wesley.
Wesley: Well, that is unfortunate. I do need approval from a home office manager before I-
Yoshiko: How many punches does it take to get him to shut up?
Joey: I dunno, you’ll have to just try until he quiets down.
Wesley: N-nw, now, I’m sure w-we don’t have to resort to such… b-barbaric means! We’re all adults here!
Yoshiko: I’m thinking at least a dozen. Six to the nose and six to the stomach.
Wesley: Ahh! Please don’t engage in a physical altercation, it’s far from necessary! Please, use the factory ships however you please! We can negotiate fair compensation once we’ve made contact with my superiors.
Joey: There ya go, commander. Factory ships at your disposal.
Samuel: Thank you, Wesley.
Wesley: Yes, of course, just leave my nose intact, please. It’s my best feature.


             */
        };

        AllDialogues = new List<List<DialogueLine>> { PlutoLines_Anomaly, PlutoLines_Reinforcements, PlutoLines_BluerPastures, PlutoToNeptune, Neptune_SeizeTheMeans };

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
        //Debug.Log("Breaking dialogue in cutscene manager.");
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
