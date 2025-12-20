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
    public List<DialogueLine> PlutoLines_Anomaly, PlutoLines_Reinforcements, PlutoLines_Pushback, PlutoLines_BluerPastures, PlutoToNeptune, Neptune_SeizeTheMeans, Neptune_OfProduction, Neptune_PressingForward, NeptuneToUranus, Uranus_OnTheOffensive, Uranus_OnTheDefensive, Uranus_ANewThreat, LostCampaign, StartedChallengeMode, SelectedCarrierSquad, EasterEggLines;
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
        Portraits["Tom"] = Resources.LoadAll<Sprite>("Sprites/Portraits/tom_chat");
        Portraits["High Command"] = Resources.LoadAll<Sprite>("Sprites/Portraits/commander_chat");
        Portraits["Oviya"] = Resources.LoadAll<Sprite>("Sprites/Portraits/oviya_chat");
        Portraits["Marco"] = Resources.LoadAll<Sprite>("Sprites/Portraits/marco_chat");
        Portraits["Yoshiko"] = Resources.LoadAll<Sprite>("Sprites/Portraits/yoshiko_chat");
        Portraits["Joey"] = Resources.LoadAll<Sprite>("Sprites/Portraits/joey_chat");
        Portraits["Wesley"] = Resources.LoadAll<Sprite>("Sprites/Portraits/wesley_chat");
        Portraits["Alejandra"] = Resources.LoadAll<Sprite>("Sprites/Portraits/alejandra_chat");
        Portraits["Emilia"] = Resources.LoadAll<Sprite>("Sprites/Portraits/emilia_chat");
        Portraits["Fritz"] = Resources.LoadAll<Sprite>("Sprites/Portraits/fritz_chat");
        Portraits["Marge"] = Resources.LoadAll<Sprite>("Sprites/Portraits/marge_chat");
        Portraits["Barge Pilot"] = Resources.LoadAll<Sprite>("Sprites/Portraits/barge_pilot_chat");
        Portraits["AMI"] = Resources.LoadAll<Sprite>("Sprites/Portraits/ami_chat");

        PlutoLines_Anomaly = new List<DialogueLine>
        {
            new DialogueLine("Samuel", Portraits["Samuel"], $"Good morning, Commander {ConfigData.UserProgressData.PlayerName}! I brought your coffee.", "[Press Space to Continue]"),
            new DialogueLine("Samuel", Portraits["Samuel"], 1),
            new DialogueLine("Samuel", Portraits["Samuel"], "I agree, it doesn't taste as good as Earth coffee. Or even Mars coffee… But hey, coffee is coffee! And we’ll both get off of Pluto soon enough."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Samuel gets a notification of some kind.", 2),
            new DialogueLine("Samuel", Portraits["Samuel"], "Oh, that's odd. A Scout is reporting an unidentified vessel approaching military airspace. What should we do?"),
            new DialogueLine("Samuel", Portraits["Samuel"], 1),
            new DialogueLine("Samuel", Portraits["Samuel"], "Right away. Contacting the vessel."),
            new DialogueLine("Samuel", Portraits["Samuel"], 1),
            new DialogueLine("Samuel", Portraits["Samuel"], "It isn’t responding, commander."),
            new DialogueLine("Samuel", Portraits["Samuel"], 1),
            new DialogueLine("Samuel", Portraits["Samuel"], "Understood, commander. We’ll send Lieutenant Tom out immediately."),

            new DialogueLine("Tom", Portraits["Tom"], $"This is Gunship D-4 reporting to command. I’m approaching the unidentified vessel now."),

            new DialogueLine("Tom", Portraits["Tom"], "Unidentified vessel, you are in United Earth military airspace. Identify yourself now."),
            new DialogueLine("Samuel", Portraits["Samuel"], 1),
            new DialogueLine("Samuel", Portraits["Samuel"], "Still nothing, even on local communications?"),
            new DialogueLine("Tom", Portraits["Tom"], "Negative."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Strange. It doesn’t seem hostile. What are your orders- oh, we’re getting a call from High Command."),
            new DialogueLine("High Command", Portraits["High Command"], $"Commander {ConfigData.UserProgressData.PlayerName}, we have received reports of an alien vessel in Pluto airspace. We cannot allow it to infiltrate our territory. Shoot it down."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Shoot it down? We don’t even know what it is! Who even reported this?"),
            new DialogueLine("High Command", Portraits["High Command"], "Those are your orders, Commander."),
            new DialogueLine("Samuel", Portraits["Samuel"], "But- oh, they disconnected. Looks like we have to attack, commander."),

            new DialogueLine("Tom", Portraits["Tom"], "What are your orders, Commander?"),
            new DialogueLine("Samuel", Portraits["Samuel"], "In order to attack, he’ll need to get in range. Once he's in range, he can attack the ship."),

            new DialogueLine("Tom", Portraits["Tom"], "Well, that was hardly a fight."),
            new DialogueLine("Samuel", Portraits["Samuel"], "I hope it wasn’t an innocent civilian. Why would High Command even order that?"),

            new DialogueLine("Tom", Portraits["Tom"], "Uh, Commander? Are you picking this up?"),
            new DialogueLine("Samuel", Portraits["Samuel"], "You need to get out of there, now!"),

            new DialogueLine("Samuel", Portraits["Samuel"], "Their fleet is huge! We need to contact High Command immediately!"),
            new DialogueLine("Samuel", Portraits["Samuel"], "Dial-up noises", 2),
            new DialogueLine("Samuel", Portraits["Samuel"], "Communications are down. What should we do?"),
            new DialogueLine("Samuel", Portraits["Samuel"], 1),
            new DialogueLine("Samuel", Portraits["Samuel"], "Understood. Preparing our fleet to deploy, commander."),

        };

        PlutoLines_Reinforcements = new List<DialogueLine>
        {

            new DialogueLine("Samuel", Portraits["Samuel"], "We’ve been caught off guard by the strange alien fleet cutting our communications. Only our patrol ships are ready for combat for the moment, but we have sent orders to the rest."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Commander, you need to engage in this fight with the patrol ships and buy time for the rest of the fleet to mobilize."),


            new DialogueLine("Samuel", Portraits["Samuel"], "Okay, commander, it's up to you to lead us to victory."),

            new DialogueLine("Samuel", Portraits["Samuel"], "I recommend you try to find out where the enemy is with your scouts, then form a plan of attack. I’ll be working on restoring our local communications with the rest of the base."),

            new DialogueLine("Samuel", Portraits["Samuel"], " . . .Also, Marco wants me to remind you that we are… now at war, so all of our combat vessels are ordered to fire on sight by default."),


            new DialogueLine("Samuel", Portraits["Samuel"], "Great work, commander. We’ve kept them at bay for now. Local communications are restored, and we have more of Pluto’s fleet online. Let’s prepare for the next wave."),

            new DialogueLine("Samuel", Portraits["Samuel"], "Commander, we may not have won that fight but we've bought enough time for local communications to be restored and more of the fleet to be brought online. Let’s prepare for the next wave."),

        };

        PlutoLines_Pushback = new List<DialogueLine>
        {
            new DialogueLine("Samuel", Portraits["Samuel"], "With the more of Pluto’s fleet online and the cover of some floating space junk coming our way, we’re going to try and push back the assault from these aliens."),


            new DialogueLine("Samuel", Portraits["Samuel"], "Alright, commander, we have all of our Lieutenants online. They’ll be giving orders for each of your vessels as you direct the entire fleet."),

            new DialogueLine("Oviya", Portraits["Oviya"], " I'm Oviya, your scout lieutenant. Use the scout to… well, scout the battlefield. They get around fast, so as long as you keep giving orders they probably won't get hit by enemy fire."),
            new DialogueLine("Oviya", Portraits["Oviya"], "They don’t have any guns, though, so please don’t leave them out to dry when they can’t fight back."),

            new DialogueLine("Joey", Portraits["Joey"], "Alrighty, Commander, I'm commanding yer Frigates. They're yer explosives experts. They can't shoot far, but they sure pack a wallop."),
            new DialogueLine("Joey", Portraits["Joey"], "Those rockets will do some serious damage, and they can even hit multiple targets inside the blast radius."),
            new DialogueLine("Joey", Portraits["Joey"], "Use ‘em against those ships that like to group up like moths to a flame and you’ll find them <i>quite</i> effective."),

            new DialogueLine("Marco", Portraits["Marco"], "I'll be commanding your Gunships. They're fast-flying dogfighting specialists. Use their speed to your advantage if you can."),
            new DialogueLine("Marco", Portraits["Marco"], "Even if they can't fly as well as me, they'll still be good at dodging fire. As long as you’re competent."),

            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "And I’m Yoshiko, your Dreadnought lieutenant! These babies are made to brawl. They can take a lotta hits and dish it right back! Keep ‘em out front and watch ‘em tear it up!"),

            new DialogueLine("Samuel", Portraits["Samuel"], "And that’s everyone. Use all of our fleet’s strengths to win this battle, commander."),
            new DialogueLine("Samuel", Portraits["Samuel"], " If you ever need a reminder on what any of our ships do or what we’ve discovered about the enemy’s fleet, you can always pause and view the United Fleet Codex."),
            new DialogueLine("Samuel", Portraits["Samuel"], "I’ll be keeping it updated for you. I believe in you!"),


            new DialogueLine("Samuel", Portraits["Samuel"], "Great work, commander! This might not be so b-"),
            new DialogueLine("Oviya", Portraits["Oviya"], "Commander, I’ve had my team scouting further ahead during the battle, and… it’s not looking good. There’s… more than we can count."),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "Good, we didn’t get to have much of a fight yet!"),
            new DialogueLine("Oviya", Portraits["Oviya"], "No, Yoshiko, it’s far too many for us to defeat here and now."),
            new DialogueLine("Marco", Portraits["Marco"], "Commander, I recommend an emergency retreat. We need to regroup and assess this threat before going fully to war with it."),
            new DialogueLine("Oviya", Portraits["Oviya"], "I agree."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Commander?"),
            new DialogueLine("Samuel", Portraits["Samuel"], 1),
            new DialogueLine("Samuel", Portraits["Samuel"], "Then it’s settled. I’ll issue the evacuation alert, and you’ll prepare the fleet to defend Pluto until we can get our people off the surface."),


            new DialogueLine("Samuel", Portraits["Samuel"], "These aliens are much tougher than I thought…"),
            new DialogueLine("Oviya", Portraits["Oviya"], "Commander, I’ve had my team scouting further ahead during the battle, and… it’s not looking good. There’s… more than we can count."),
             new DialogueLine("Yoshiko", Portraits["Yoshiko"], "Good, let’s win the next fight then!"),
            new DialogueLine("Oviya", Portraits["Oviya"], "No, Yoshiko, it’s far too many for us to defeat here and now. Especially if that first wave was too much for us."),
            new DialogueLine("Marco", Portraits["Marco"], "Commander, I recommend an emergency retreat. We need to regroup and assess this threat before going fully to war with it."),
            new DialogueLine("Oviya", Portraits["Oviya"], "I agree."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Commander?"),
            new DialogueLine("Samuel", Portraits["Samuel"], 1),
            new DialogueLine("Samuel", Portraits["Samuel"], "Then it’s settled. I’ll issue the evacuation alert, and you’ll prepare the fleet to defend Pluto until we can get our people off the surface."),
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
            new DialogueLine("Oviya", Portraits["Oviya"], "I’ve got the Scouts mapping an escape route."),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "Let's take out as many as we can on our way out! That’ll show 'em!"),
            new DialogueLine("Marco", Portraits["Marco"], "Don’t be risky with our fleet’s lives, Yoshiko. Destroy enemy ships where we can <i>safely</i>. That’s the only way we’ll get through this."),

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
            new DialogueLine("Samuel", Portraits["Samuel"], "And they could have a way to make more ships for our fleet!"),
            new DialogueLine("Joey", Portraits["Joey"], "If there’s still anything left there."),
            new DialogueLine("Oviya", Portraits["Oviya"], "It’s our best option right now. If Neptune is wiped out, then we move on."),
            new DialogueLine("Joey", Portraits["Joey"], "And if it’s not, we have to deal with Wesley…"),
        };

        Neptune_SeizeTheMeans = new List<DialogueLine>
        {
            new DialogueLine("Samuel", Portraits["Samuel"], "We’re approaching Neptune to see if we can gather any allies there."),
            new DialogueLine("Samuel", Portraits["Samuel"], "The Scout team has reported movement, but we're unable to determine if it’s friendly or not. We’ll just have to find out."),

            new DialogueLine("Samuel", Portraits["Samuel"], "Commander, we’re getting an emergency signal from somewhere on Neptune."),
            new DialogueLine("???", Portraits["Wesley"], "-day, Mayd- this is an emerg- enem- facility dest- need evac-", true),
            new DialogueLine("Samuel", Portraits["Samuel"], "It’s very faint, but it’s clear we need to go help."),
            new DialogueLine("Oviya", Portraits["Oviya"], "It looks like the bees are gathered around ore-rich asteroids around the planet. Scouts are reporting the mining facility on the surface is flattened."),
            new DialogueLine("Joey", Portraits["Joey"], "There’s an underground bunker beneath the facility. I’m darn sure that’s where your signal’s coming from."),
            new DialogueLine("Marco", Portraits["Marco"], "Let’s clear out those bees so we can land safely."),

            new DialogueLine("Samuel", Portraits["Samuel"], "Strange, that ship doesn’t seem to be firing back."),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "Why won’t they fight?! Cowards."),
            new DialogueLine("Marco", Portraits["Marco"], "Can it, Yoshiko. Look, they’re attached to the asteroid’s surface."),
            new DialogueLine("Joey", Portraits["Joey"], "Mining ships. I know Jensen was working on a model like that. Just less… insectile."),

            new DialogueLine("Samuel", Portraits["Samuel"], "Great work, commander. We can check the facility now."),

            new DialogueLine("Samuel", Portraits["Samuel"], "We’ve lost all the squads we sent out, commander. There’s no safe way onto Neptune."),
            new DialogueLine("Marco", Portraits["Marco"], "There’s not much time to hang around here. The closest United Fleet base is on Uranus."),
             new DialogueLine("Marco", Portraits["Marco"], "We can find help there. Those people in the mining facility will just have to hang on a bit longer until others can come back."),
            new DialogueLine("Oviya", Portraits["Oviya"], "Scouts are reporting a bee blockade between here and Uranus. We’ll have to break it."),

            
            new DialogueLine("Samuel", Portraits["Samuel"], "Commander, we’ve rescued the mining personnel from their bunker. Their leader has come to speak with you."),
            new DialogueLine("Wesley", Portraits["Wesley"], "I’m no leader. That would be my manager, Derek. I’m simply the regional head of the accounting department for Jensen Industries."),
            new DialogueLine("Joey", Portraits["Joey"], "Same as always."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Ah, greetings, Joey."),
            new DialogueLine("Joey", Portraits["Joey"], "Howdy. Where’s Derek, then?"),
            new DialogueLine("Wesley", Portraits["Wesley"], "Derek is no longer with us. He didn’t make it to the bunker in time. I’ve been the de facto leader for the remaining Jensen personnel."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Then why did you say-"),
            new DialogueLine("Joey", Portraits["Joey"], "Don’t bother. Wesley, tell us what we need to know for this war against the Bees."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Bees? Is that what’s attacking us? I was not aware they had the biology to survive in space. They’re hardly surviving on Earth any longer, due to-"),
            new DialogueLine("Marco", Portraits["Marco"], "They’re not bees."),
            new DialogueLine("Joey", Portraits["Joey"], "They just look like ‘em. Easier to call ‘em that."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Ah. Right. Of course. Well, these… bees quickly disposed of our contracted defense team and started bombing the surface."),
             new DialogueLine("Wesley", Portraits["Wesley"], "The emergency code was sent, and we all attempted to secure ourselves in the underground bunker. Not everyone made it."),
            new DialogueLine("Marco", Portraits["Marco"], "Great. So we have an accountant-"),
            new DialogueLine("Wesley", Portraits["Wesley"], "Regional head of the accounting department."),
            new DialogueLine("Marco", Portraits["Marco"], "..."),
            new DialogueLine("Marco", Portraits["Marco"], "We have that and a bunch of miners, then? This isn’t really helping our chances."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Well, um, we do have a line of prototype Factory ships, capable of mining ore-rich asteroids, as well as personnel trained to man them."),
            new DialogueLine("Joey", Portraits["Joey"], "And plenty of those asteroids nearby. Well, I’ll be, Wesley, this really is helpful."),
            new DialogueLine("Wesley", Portraits["Wesley"], "They are property of Jensen Corporation, and all personnel including myself are on their payroll. I’m sure we can reach some sort of agreement for their use, perhaps a loan."),
            new DialogueLine("Wesley", Portraits["Wesley"], "But of course, these ships are the intellectual property of Jensen, so I can’t have any of you stealing these plans."),
            new DialogueLine("Samuel", Portraits["Samuel"], "We don’t have any way to contact Jensen Corporation, Wesley."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Well, that is unfortunate. I do need approval from a home office manager before I-"),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "How many punches does it take to get him to shut up?"),
            new DialogueLine("Joey", Portraits["Joey"], "I dunno, you’ll have to just try until he quiets down."),
            new DialogueLine("Wesley", Portraits["Wesley"], "N-nw, now, I’m sure w-we don’t have to resort to such… b-barbaric means! We’re all adults here!"),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "I'm thinking at least a dozen. Six to the nose and six to the stomach."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Ahh! Please don’t engage in a physical altercation, it’s far from necessary! Please, use the Factories however you please! We can negotiate fair compensation once we’ve made contact with my superiors."),
            new DialogueLine("Joey", Portraits["Joey"], "There ya go, commander. Factory ships at your disposal."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Thank you, Wesley."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Yes, of course, just leave my nose intact, please. It’s my best feature."),
            

        };

        Neptune_OfProduction = new List<DialogueLine>
        {
            new DialogueLine("Samuel", Portraits["Samuel"], "Scouts are reporting the bee forces on all sides. Their fleet from Pluto is catching up, and they’ve already established a blockade between here and Uranus."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Now that we have Factories, we can gather resources from the same asteroids the bees were defending. That’ll give us a fighting chance against the blockade, but we need to be quick."),

            new DialogueLine("Oviya", Portraits["Oviya"], "The mining asteroids can be found at these locations."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Our Factory ships should be quite capable of increasing your prof- erm, resources in order to further bolster your fleet. Simply direct them to a mining location."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Collect these ores as long as you can, commander, but don’t risk too many lives."),
            new DialogueLine("Samuel", Portraits["Samuel"], "If a Factory goes down, it’ll lose all the resources it collected. It won’t be worth it in the end."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Be careful with those Factories! They’re not built for combat or maneuverability."),
            new DialogueLine("Oviya", Portraits["Oviya"], "In that case, you’ll have to plan your retreat carefully."),


            new DialogueLine("Oviya", Portraits["Oviya"], "It looks like there are bee scouting parties approaching. They’ll soon find out their fleet here was destroyed.", DialogueLine.DialogueType.Disappearing),

            new DialogueLine("Oviya", Portraits["Oviya"], "More bees are en route. The longer we stay here, the more dangerous it becomes, commander.", DialogueLine.DialogueType.Disappearing),

            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "Incoming ships! Get ready for a fight!", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Marco", Portraits["Marco"], "More bees. Hope you know what you’re doing, commander.", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Joey", Portraits["Joey"], "Gracious, how many more bees are there?", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Oviya", Portraits["Oviya"], "Scouts are reporting even more bees than before. Brace yourselves.", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Samuel", Portraits["Samuel"], "Commander, get ready for another fight. We have bees incoming.", DialogueLine.DialogueType.Disappearing),

            new DialogueLine("Samuel", Portraits["Samuel"], "The commander is calling a retreat. Regroup for our next battle!"),
            new DialogueLine("Samuel", Portraits["Samuel"], "We've lost the last ships we sent out, commander. It’s time to regroup."),
            new DialogueLine("Samuel", Portraits["Samuel"], "That's everyone commander, we made it out."),
        };

        Neptune_PressingForward = new List<DialogueLine>
        {
            new DialogueLine("Samuel", Portraits["Samuel"], "It’s time to break through this blockade, commander. Use everything at your disposal."),

            new DialogueLine("Samuel", Portraits["Samuel"], "Great work, commander! We’re free to travel to Uranus."),
            new DialogueLine("Marco", Portraits["Marco"], "That was a tough fight, commander, you did well."),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "We showed those bee losers who the real fighters are!"),
            new DialogueLine("Oviya", Portraits["Oviya"], "Don’t get too cocky now, Yoshiko. Let’s focus on what’s next."),

            new DialogueLine("Samuel", Portraits["Samuel"], "Commander, we’ve lost contact with all our forces. What do we do now? The bees from Pluto are catching up."),
            new DialogueLine("Oviya", Portraits["Oviya"], "The fight diverted forces from elsewhere in the blockade. We can escape if we move quickly!"),
            new DialogueLine("Marco", Portraits["Marco"], "Those Factories are slow and defenseless. Leave them behind."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Now hold on a second! Those ships have already proven lots of shareholder val-"),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "He’s right, Wes, if they can’t fight and they can’t move they won’t make it!"),
            new DialogueLine("Oviya", Portraits["Oviya"], "Agreed. Now move!"),
            new DialogueLine("Samuel", Portraits["Samuel"], "Full speed ahead!"),

        };

        NeptuneToUranus = new List<DialogueLine>
        {
            new DialogueLine("Oviya", Portraits["Oviya"], "Our next destination is Uranus. There are bases on the planet proper and one of its moons-"),
            new DialogueLine("Samuel", Portraits["Samuel"], "Commander! We’re picking up other human vessels on our radars!"),
            new DialogueLine("Oviya", Portraits["Oviya"], " We’re near Titania’s research and engineering base. It could be survivors."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Let’s establish contact."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "Hello? Ah, good. This is Alejandra Vasquez, research and engineering wing, regional commander and research lead for United Fleet Titania."),
            new DialogueLine("Emilia", Portraits["Emilia"], "Ohhhh, who are they?"),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "Emilia, please. I see you all have also survived the assault. There is safety in numbers, as the saying goes."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "I imagine our chances of success directly correlate with the strength of our numbers. May we join your fleet?"),
            new DialogueLine("Samuel", Portraits["Samuel"], "Of course! I mean- well, it’s up to the commander."),
            new DialogueLine("Samuel", Portraits["Samuel"], 2),
            new DialogueLine("Samuel", Portraits["Samuel"], "Yes, you can join."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "Excellent. We have just finished the first prototypes of our Carrier units for the United Fleet. I imagine it will be a valuable addition to your fleet."),
            new DialogueLine("Emilia", Portraits["Emilia"], "Oh yay! We’re going to survive!"),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "Yes, let’s meet in person and discuss a plan."),
            new DialogueLine("Samuel", Portraits["Samuel"], "We’ll prepare for boarding here."),
            new DialogueLine("Samuel", Portraits["Samuel"], 2),
            new DialogueLine("Samuel", Portraits["Samuel"], "The Titania research and engineering team should make a great addition to the fleet."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "We will. You appear horribly disorganized after all. Your fleet isn’t even in regulation formations, or any formation for that matter. You could use my help."),
            new DialogueLine("Marco", Portraits["Marco"], "You’ve been on our ship for about ten seconds."),
            new DialogueLine("Emilia", Portraits["Emilia"], "Well, I think you’re doing great! The way you fought was amazing! And we need their help, too, Alejandra."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "Of course. I have great admiration for this group. Was that not clear?"),
            new DialogueLine("Joey", Portraits["Joey"], "Clear as mud, ma’am."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "My apologies."),

            new DialogueLine("Joey", Portraits["Joey"], "Tell me about those Carriers you mentioned, they sound mighty interesting."),
            new DialogueLine("Emilia", Portraits["Emilia"], "Oh! Oh! They’re really cool! So the Carrier itself doesn’t fight anything, but it has Drones and Strikers on board, and those can go out and attack things."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "And we possess all of the blueprints for that design with us, so if you have adequate production facilities, you may construct more than we have here."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "Now, tell me about your fleet’s survival. Any information on these unidentified, possibly alien vessels will be critical."),

            new DialogueLine("Samuel", Portraits["Samuel"], "We originally came from Pluto. We fought with the bees-"),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "Bees?"),
            new DialogueLine("Samuel", Portraits["Samuel"], "Erm… yes, that’s what we’re calling this enemy."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "Yes, I do suppose they bear some resemblance to the endangered species. Fascinating observation."),
            new DialogueLine("Emilia", Portraits["Emilia"], "I like that name! Stupid bees! Yeah, it feels good to say."),
            new DialogueLine("Samuel", Portraits["Samuel"], "What are you writing? Um- nevermind."),
            new DialogueLine("Samuel", Portraits["Samuel"], " Anyway, we fought them, but there were too many for us to beat. We’ve been making our way closer to Earth and picking up survivors along the way."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "And I assume you’ve encountered their communications jamming as well?"),
            new DialogueLine("Joey", Portraits["Joey"], "Yes, ma’am. "),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "It’s been frustrating. We lacked significant combat capabilities, so we had to go dark after the first assault."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "It was enough to throw them off, but it seems the fighting on Uranus was intense enough to be their main focus. We took the opportunity to flee."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "I can’t say if there are survivors there."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "It was primarily a mining facility, but because it supplied the manufacturing operations on Saturn, we had some of our advanced Cruisers patrolling the area."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "We may need to act fast."),
            new DialogueLine("Marco", Portraits["Marco"], "We’re used to that by now."),
            new DialogueLine("Joey", Portraits["Joey"], "You said you were the research lead? Where’s the engineering lead?"),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "He… is no longer with us after the initial assault. He took a Carrier out to draw their fire while we went dark. His daughter, Emilia, has been helping me in his stead. She is quite talented."),
            new DialogueLine("Emilia", Portraits["Emilia"], " …yeah."),
            new DialogueLine("Samuel", Portraits["Samuel"], "I’m so sorry, Emilia."),
            new DialogueLine("Emilia", Portraits["Emilia"], "Now let’s take those stupid bees down! Uranus is under new management! Er- well, I guess old management. We’re taking the new management down. Yeah!"),
            new DialogueLine("Samuel", Portraits["Samuel"], "The first way sounds cooler, though."),
            new DialogueLine("Emilia", Portraits["Emilia"], "Yeah!"),
            new DialogueLine("Marco", Portraits["Marco"], "Are we sure this is an improvement?"),

        };

        Uranus_OnTheOffensive = new List<DialogueLine>
        {
            new DialogueLine("Samuel", Portraits["Samuel"], "There is still fighting around Uranus, another resource-rich area. If we take the bees down, we can bolster our own fleet with resources and personnel."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Radars are jammed in the area, so we’ll need to rely on our Scouts for vision."),


            new DialogueLine("Oviya", Portraits["Oviya"], "Bee presence around Uranus is heavy, but not insurmountable. We’re far enough from the blockade that we aren’t expecting reinforcements now."),
            new DialogueLine("Oviya", Portraits["Oviya"], "Victory should ensure us time to collect more resources for the fleet."),
            new DialogueLine("Samuel", Portraits["Samuel"], "We aren’t receiving any human signals, but it does look like there’s fighting ahead."),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "Let’s get in there! And fast!"),

            new DialogueLine("Oviya", Portraits["Oviya"], "Commander, a survivor! ", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Marco", Portraits["Marco"], "They’re outnumbered, move quick.", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Samuel", Portraits["Samuel"], "We’ve established comms with the ship.", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Fritz", Portraits["Fritz"], "Ahahahaha! Die! See the light and DIE!", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Samuel", Portraits["Samuel"], "They’re… colorful.", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "I like them!", DialogueLine.DialogueType.Disappearing),

            new DialogueLine("Oviya", Portraits["Oviya"], "Commander! This is a bee ship we haven’t seen before. Be careful; we don’t know what it can do.", DialogueLine.DialogueType.Disappearing),

            new DialogueLine("Samuel", Portraits["Samuel"], "We just took massive damage from the unknown ship, commander!", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "Find a way to take it out!", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Joey", Portraits["Joey"], "That’s not gonna be so easy. Its weapon range is huge from what I can tell.", DialogueLine.DialogueType.Disappearing),

            new DialogueLine("Samuel", Portraits["Samuel"], "Good work, commander. That was scary.", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Marco", Portraits["Marco"], "I’m sure there will be more later…", DialogueLine.DialogueType.Disappearing),

            new DialogueLine("Samuel", Portraits["Samuel"], "We did it, commander!"),
            new DialogueLine("Oviya", Portraits["Oviya"], "Scouts are on the way to see what can be salvaged from the surface."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Let’s get our new… friend? Let’s get them on board."),
            new DialogueLine("Fritz", Portraits["Fritz"], "H-hi! Other humans! Wait-"),
            new DialogueLine("Samuel", Portraits["Samuel"], "Please stop touching my face."),
            new DialogueLine("Fritz", Portraits["Fritz"], " Real! You’re real! You hear that, NATALIE?! Why can’t you be like him?!"),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "Fritz? What were you doing here?"),
            new DialogueLine("Fritz", Portraits["Fritz"], "Another memory… or a ghost- ow!"),
            new DialogueLine("Emilia", Portraits["Emilia"], "Memories can’t slap you! Neither can ghosts!"),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "Erm, thank you Emilia."),
            new DialogueLine("Fritz", Portraits["Fritz"], "If… if I remember… I was fixing the gun. Then… boom! Boom. Bees. Pilot’s down. Bees! I’ll get them… I’ll blow them up! Where are they?!"),
            new DialogueLine("Marco", Portraits["Marco"], "Snap out of it!"),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "Fight’s over! We won."),
            new DialogueLine("Samuel", Portraits["Samuel"], "You’re safe now. Promise."),
            new DialogueLine("Fritz", Portraits["Fritz"], "Ah- yes. Right. There will be more. Let me blow them up! The ghosts want to. Cruisers are great at that."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "You do have schematics for such a ship, correct? Since you are a Cruiser engineer."),
            new DialogueLine("Fritz", Portraits["Fritz"], "Yes! Yes yes yes! Make more! More light! Bees die in light!"),

            new DialogueLine("Wesley", Portraits["Wesley"], "Your mining vessels are ready for the asteroids, commander. I recommend initiating operations quickly, before more of these bees arrive."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "I like the way you think! I’m glad somebody else here cares about efficiency."),
            new DialogueLine("Wesley", Portraits["Wesley"], "Efficiency creates profit, ma’am."),

            new DialogueLine("Samuel", Portraits["Samuel"], "All of our fleet we sent out is down, commander."),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "We can still take ‘em!"),
            new DialogueLine("Marco", Portraits["Marco"], "We can’t risk further casualties, Yoshiko. Keep your head on straight."),
        };

        Uranus_OnTheDefensive = new List<DialogueLine>
        {
            new DialogueLine("Samuel", Portraits["Samuel"], "Our success has bought us some time to collect resources around Uranus. There will likely be reinforcements called on from the bees we just fought, though. Take care."),

            new DialogueLine("Oviya", Portraits["Oviya"], "Reinforcements from the remnants of the blockade are incoming.", DialogueLine.DialogueType.Disappearing),

            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "More bees! Let’s give ‘em hell!", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Marco", Portraits["Marco"], "Even more ships. Commander, I trust you’ll know when to exit.", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Joey", Portraits["Joey"], "More bees? What in tarnation? Where are they coming from? ", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Oviya", Portraits["Oviya"], "We've found more bee ships, inbound to our location.", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Samuel", Portraits["Samuel"], "Another wave of ships!", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Wesley", Portraits["Wesley"], "More of those… bees? Don’t risk our profit margins. You’ll receive a bill from Jensen if you lose any Factories.", DialogueLine.DialogueType.Disappearing),

            new DialogueLine("Oviya", Portraits["Oviya"], "We're seeing more reinforcements from a different direction, now. It could be their main fleet. Things are going to get tough from here."),

            new DialogueLine("Fritz", Portraits["Fritz"], "Ahaha! We have more bees come to meet the light! That’s right, Dee, this one’s for you!"),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "More wrinkles in our plans approaching. I trust you can sort them out, commander."),
            new DialogueLine("Emilia", Portraits["Emilia"], "Oh! Uh- is this thing on? Commander! There’s more bees! Get 'em!"),

            new DialogueLine("Samuel", Portraits["Samuel"], "Fleet, it’s time to regroup and head out! Return to the commander’s position immediately."),

            new DialogueLine("Samuel", Portraits["Samuel"], "We've lost the last ship we sent out, commander. It’s time to regroup."),

            new DialogueLine("Oviya", Portraits["Oviya"], "Hold on, we have more bees on our radar! They’re… not coming towards us?"),
            new DialogueLine("Marco", Portraits["Marco"], "No rest for the weary…"),
            new DialogueLine("Samuel", Portraits["Samuel"], "Alright, commander, gather your squads and let's investigate."),

            new DialogueLine("Samuel", Portraits["Samuel"], "That was close! Let's get out of here."),
        };

        Uranus_ANewThreat = new List<DialogueLine> {
            new DialogueLine("Samuel", Portraits["Samuel"], "We need to check out a grouping of bees further out from Uranus. They’re moving… very slowly."),

            new DialogueLine("Marge", Portraits["Marge"], "Is this dang thing working? Hello! Any humans left? Hang in there, team, we’re gonna get through this, okay?"),
            new DialogueLine("Barge Pilot", Portraits["Barge Pilot"], "It’s not looking good, Marge…"),
            new DialogueLine("Marge", Portraits["Marge"], "I'll get you through, okay?"),
            new DialogueLine("Samuel", Portraits["Samuel"], "Hello? Identify yourself."),
            new DialogueLine("Marge", Portraits["Marge"], "Oh, thank the stars! Are you UF? Come help us, quick! We’re surrounded over here!"),

            new DialogueLine("Marge", Portraits["Marge"], "No!!! They’re gonna pay for that!", DialogueLine.DialogueType.Disappearing),

            new DialogueLine("Marge", Portraits["Marge"], "There’s more coming! ", DialogueLine.DialogueType.Disappearing),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "Those are Yellow Jackets! Don’t let them hit those Barges!", DialogueLine.DialogueType.Disappearing),

            new DialogueLine("Marge", Portraits["Marge"], "Just cuz I don’t have guns don't mean I can’t fight! Take this!", DialogueLine.DialogueType.Disappearing),

            new DialogueLine("Marge", Portraits["Marge"], "I thought we were goners. I can’t thank you enough for risking yourselves."),
            new DialogueLine("Marco", Portraits["Marco"], "We're getting pretty used to these rescue missions, don’t sweat it."),
            new DialogueLine("Fritz", Portraits["Fritz"], "And we get to explode stuff! Win win!"),
            new DialogueLine("Marge", Portraits["Marge"], "…right."),
            new DialogueLine("Samuel", Portraits["Samuel"], "How’d you end up here?"),
            new DialogueLine("Marge", Portraits["Marge"], "Well, we were on a transport line from Saturn. Those… things-"),
            new DialogueLine("Emilia", Portraits["Emilia"], "Bees!"),
            new DialogueLine("Marge", Portraits["Marge"], "They do look like that, huh? Wait- you all don’t think they’re really bees, right?"),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "Of course not, that’s preposterous."),
            new DialogueLine("Fritz", Portraits["Fritz"], "…really?! Jeremy lied to me…"),
            new DialogueLine("Marge", Portraits["Marge"], "Right. Glad most of us have our heads screwed on straight! Either way, they chased behind us during our trip over."),
            new DialogueLine("Oviya", Portraits["Oviya"], "And they chased you from Saturn. This is bad."),
            new DialogueLine("Emilia", Portraits["Emilia"], "Why’s that? Well obviously it’s bad because the bees attacked there."),
            new DialogueLine("Joey", Portraits["Joey"], "Saturn’s the biggest UF station this side of the asteroid belt."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "And its largest off-Earth manufacturing site."),
            new DialogueLine("Joey", Portraits["Joey"], "They’re mighty fine facilities."),
            new DialogueLine("Marco", Portraits["Marco"], "If they’re smart, they’re using those facilities."),
            new DialogueLine("Emilia", Portraits["Emilia"], "You think they can make more bees? They can probably make more bees."),
            new DialogueLine("Oviya", Portraits["Oviya"], "Let’s get within scouting distance as soon as possible. If it’s as bad as we think, we need to stop this immediately."),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "Oh, this is gonna be a big fight. I can tell!"),

            new DialogueLine("Samuel", Portraits["Samuel"], "We couldn’t save them…"),
            new DialogueLine("Marco", Portraits["Marco"], "Sometimes that’s life, kid. We’ll avenge them."),
            new DialogueLine("Oviya", Portraits["Oviya"], "It looks like they were coming from Saturn."),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "Bees included. This is bad."),
            new DialogueLine("Emilia", Portraits["Emilia"], "Why’s that? Well obviously it’s bad because the bees attacked there."),
            new DialogueLine("Joey", Portraits["Joey"], "Saturn’s the biggest UF station this side of the asteroid belt."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "And its largest off-Earth manufacturing site."),
            new DialogueLine("Joey", Portraits["Joey"], "They’re mighty fine facilities."),
            new DialogueLine("Marco", Portraits["Marco"], "If they’re smart, they’re using those facilities."),
            new DialogueLine("Emilia", Portraits["Emilia"], "You think they can make more bees? They can probably make more bees."),
            new DialogueLine("Oviya", Portraits["Oviya"], "Let’s get within scouting distance as soon as possible. If it’s as bad as we think, we need to stop this immediately."),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "Oh, this is gonna be a big fight. I can tell!"),
        };

        LostCampaign = new List<DialogueLine>
        {
            new DialogueLine("Samuel", Portraits["Samuel"], "Commander! We have no ships left that can fight!"),
            new DialogueLine("Yoshiko", Portraits["Yoshiko"], "Who needs ships?! I'll fight them with my bare hands!"),
            new DialogueLine("Marco", Portraits["Marco"], "It’s a death wish. Others will continue the fight, and we’re no use to them if we’re gone."),
        };

        StartedChallengeMode = new List<DialogueLine>
        {
            new DialogueLine("A.M.I.", Portraits["AMI"], "Welcome to Challenge Mode, Commander!"),
            new DialogueLine("A.M.I.", Portraits["AMI"], "This simulation works a little differently from Campaign Mode. First of all, you start out with a set amount of ships that decreases as you lose them. You won’t be able to construct additional ships for your fleet."),
            new DialogueLine("A.M.I.", Portraits["AMI"], "Secondly, you only advance onto the next level when you beat the current level. If you fail, you lose your ships, but the Bees are as strong as before."),
            new DialogueLine("A.M.I.", Portraits["AMI"], $"The goal is to make it as far as possible before you run out of ships. As the name implies, Challenge Mode will challenge your strategic and leadership skills, so use this opportunity to sharpen your abilities. Good luck Commander {ConfigData.UserProgressData.PlayerName}!"),
        };

        SelectedCarrierSquad = new List<DialogueLine>
        {
            new DialogueLine("Emilia", Portraits["Emilia"], "Alright, commander, let’s show you how it’s done!"),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "Yes, it is a complex vessel. The Carrier itself cannot engage in combat."),
            new DialogueLine("Emilia", Portraits["Emilia"], "But the Drone shoots stuff! And the Striker explodes stuff!"),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "More or less. Drones will operate much like the other fleet types you’re used to. Strikers function a bit differently. If you- Emilia, wake up."),
            new DialogueLine("Emilia", Portraits["Emilia"], "Ah! Right, so the strikers have to drop their bombs! Don’t worry about it being space, they’re magnetic. But the magnets only engage once they release the bombs. The mechanism behind that is really neat, because it-"),
             new DialogueLine("Alejandra", Portraits["Alejandra"], "Emilia, stay on task."),
             new DialogueLine("Emilia", Portraits["Emilia"], "Right, right. Anyway, your strikers will have to get to their target to drop off their payload, so keep in mind that they need a target. It’s a very simple program, so they won’t automatically attack anything."),
             new DialogueLine("Alejandra", Portraits["Alejandra"], "Their effective range is nonexistent."),
             new DialogueLine("Emilia", Portraits["Emilia"], "And then after that, they can’t attack anything else until they get a new payload at the Carrier."),
             new DialogueLine("Alejandra", Portraits["Alejandra"], "The Carrier will always be ready to restock your Strikers, and they will seek a reload after dropping their payload automatically."),
             new DialogueLine("Emilia", Portraits["Emilia"], "Now go get ‘em!"),
        };

        EasterEggLines = new List<DialogueLine>
        {
            new DialogueLine("Wesley", Portraits["Wesley"], "You really should be playing Pikmin 2 instead."),
            new DialogueLine("Marco", Portraits["Marco"], "I'm just hoping that this gets Richard Hammond to notice me."),
            new DialogueLine("Fritz", Portraits["Fritz"], "Jeremy has nothing on Dee when it comes to the social game which is really the crux of the show, but in season four thousand six hundred and twenty, the meta <i>really</i> changed..."),
            new DialogueLine("Samuel", Portraits["Samuel"], "Have you heard of my new game? It's called The Folk and there's an eclectic assortment of mysteriously transformed animals from all over Montana."),
            new DialogueLine("A.M.I.", Portraits["AMI"], "I've never actually played a video game in my life, I just really like Bees."),
            new DialogueLine("Alejandra", Portraits["Alejandra"], "Si este juego no está completamente traducido al español, ahogaré a mi esposo en su propio batido."),
        };
        /*
         * 
         *  new DialogueLine("Samuel", Portraits["Samuel"], ""),
         *  new DialogueLine("Marco", Portraits["Marco"], ""),
         *  new DialogueLine("Oviya", Portraits["Oviya"], ""),
         *  new DialogueLine("Joey", Portraits["Joey"], ""),
         *  new DialogueLine("Wesley", Portraits["Wesley"], ""),
         *  new DialogueLine("Yoshiko", Portraits["Yoshiko"], ""),
         *  new DialogueLine("Alejandra", Portraits["Alejandra"], ""),
         *  new DialogueLine("Emilia", Portraits["Emilia"], ""),
         *  new DialogueLine("Fritz", Portraits["Fritz"], ""),
         *  new DialogueLine("Marge", Portraits["Marge"], ""),
         *  new DialogueLine("A.M.I.", Portraits["AMI"], ""),
         *  
         * */




        /*


         */
    }

    public void HideDialogue()
    {
        DialogueManager.gameObject.SetActive(false);
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
        //DialogueManager.SetPortrait(lines[0].PortraitA);
        DialogueManager.StartDialogue(lines, isLastDialogue);
    }

    public void BreakDialogue()
    {
        //Debug.Log("Breaking dialogue in cutscene manager.");
        HitDialogueBreak = true;
        DialogueManager.gameObject.SetActive(false);
    }
    public void EndDialogue()
    {
        DialogueManager.gameObject.SetActive(false);
        if (HasEndDialogueAction)
        {
            EndDialogueAction();
        }
    }


}
