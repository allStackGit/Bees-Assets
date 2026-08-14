using Assets.Scripts;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

public static class CampaignDialogueOverrides
{
    public static void Apply(CutsceneManager manager)
    {
        if (manager == null)
        {
            return;
        }

        PatchPlutoAnomaly(manager.PlutoLines_Anomaly);
        PatchPlutoReinforcements(manager.PlutoLines_Reinforcements);
        PatchPlutoPushback(manager.PlutoLines_Pushback);
        PatchPlutoBluerPastures(manager.PlutoLines_BluerPastures);
        PatchPlutoToNeptune(manager.PlutoToNeptune);
        PatchNeptuneSeizeTheMeans(manager.Neptune_SeizeTheMeans);
        PatchNeptuneOfProduction(manager.Neptune_OfProduction);

        // This transition has drifted furthest from the current Mission Scripting document, so
        // rebuild it from the authored version instead of trying to maintain the older sequence.
        manager.NeptuneToTitania = BuildNeptuneToTitania();

        PatchTitaniaBeenoculars(manager.Titania_Beenoculars);
        PatchUranusOnTheOffensive(manager.Uranus_OnTheOffensive);
        PatchUranusOnTheDefensive(manager.Uranus_OnTheDefensive);
        PatchUranusANewThreat(manager.Uranus_ANewThreat);
        PatchSelectedCarrier(manager.SelectedCarrierSquad);
    }

    public static List<DialogueLine> BuildTitaniaToUranus(bool includeAmi)
    {
        // Beenoculars success is now persisted so the authored conditional A.M.I. exchange also
        // survives a scene/device transition. Preserve the parameter for older callers/tests.
        includeAmi = includeAmi || TitaniaRouteState.DidWinTitaniaTwo;

        List<DialogueLine> lines = new List<DialogueLine>
        {
            Line("Alejandra", "Commander, my thanks again for your assistance in our predicament. I hope we can prove our value to your fleet shortly."),
            Line("Emilia", "We’ve got a carrier!"),
        };

        if (includeAmi)
        {
            lines.AddRange(new []
            {
                Line("AMI", "And you will have whatever assistance I can offer in your upcoming battles."),
                Line("Yoshiko", "Oh, hey red lady. What, uh.. Exactly is your assistance?"),
                Line("AMI", "I can make improvements to your codex with strategy suggestions based on our observations and battles with the various bee vessels. Additionally, my predictive routines should assist in knowing what enemy vessels to expect in your battles as you are preparing."),
                Line("Yoshiko", "That’s… insanely cool!"),
                Line("AMI", "Why thank you. If I could blush, I assure you I would be."),
                Line("Emilia", "My dad and Alejandra both worked a lot on her. I think she’s awesome!"),
                Line("Joey", "She’s impressive. Looks like we’ve got ourselves a mighty fine pair of Bee-noculars."),
                Line("AMI", "Ha! That is quite clever."),
                Line("Emilia", "Wow, Marco, I didn’t know eyes could roll that far."),
                Line("Marco", "I’ve got lots of practice…"),
            });
        }

        lines.AddRange(new []
        {
            Line("Alejandra", "Furthermore, if you have the means of production, we also have the blueprints for the carrier. Producing more should help alleviate the pressures on your manpower."),
            Line("Emilia", "Yeah, it’s all robots driving the little ships!"),
            Line("Alejandra", "And perhaps I can help some with organizing your efforts, as well. I’ve noticed a distinct lack of organization amongst you. For example, your command ship isn’t even in proper formation with the rest-"),
            Line("Marco", "That’s not what we need help with. Maybe use your smarts for some battle tactics and weapons."),
            Line("Joey", "You said you were the research lead?"),
            Line("Alejandra", "Yes, Titania is a research and engineering base, after all."),
            Line("Joey", "What about the engineering lead?"),

            // The document's later claim that Philip died taking a Carrier out conflicts with the
            // earlier Titania scene, where Philip dies destroying the weapons bay to create the
            // debris field. The latter is already established in-game, so keep that continuity.
            Line("Alejandra", "He… is no longer with us after the initial assault. He destroyed Titania’s weapons bay to create the debris field that kept the bees away long enough for us to survive. Emilia is his daughter, and she has been helping me in his stead. She is quite talented, I might add."),
            Line("Emilia", "…yeah."),
            Line("Samuel", "I’m so sorry, Emilia."),
            Line("Emilia", "Yeah! Let’s take those stupid bees down! Uranus is under new management! Er- well, I guess old management. We’re taking the new management down. Yeah!"),
            Line("Samuel", "The first way sounds cooler, though."),
            Line("Emilia", "Yeah!"),
            Line("Marco", "Are we sure this is an improvement?"),
            Line("Oviya", "Commander, we need to keep moving. My scouts are reporting there may be some United Fleet stragglers around Uranus that need our help."),
            Line("Samuel", "Let’s go, then!"),
        });

        return lines;
    }

    private static void PatchPlutoAnomaly(List<DialogueLine> lines)
    {
        Set(lines, 3, "Samuel", "The tech gets a notification of some kind.");
        Set(lines, 6, "Samuel", "Right away, sir. Contacting the vessel.");
        Set(lines, 8, "Samuel", "It isn’t responding, sir.");
        Set(lines, 10, "Samuel", "Understood, sir. We’ll send Lieutenant Tom out immediately.");
        Set(lines, 11, "Tom", "This is Gunship P-4 reporting to command. I’m approaching the unidentified vessel now.");
        Set(lines, 12, "Tom", "Unidentified vessel, you are in United Fleet airspace. Identify yourself immediately.");
        Set(lines, 20, "Samuel", "But- oh, they disconnected. Looks like we have to attack, sir.");
        Set(lines, 22, "Samuel", "In order to attack, he’ll need to be in range of his ship’s guns. Once in range, he can attack the ship.");
        Set(lines, 25, "Tom", "Uh, Commander? Are you seeing this?");
        Set(lines, 29, "Samuel", "Communications are down, sir. What should we do?");
        Set(lines, 31, "Samuel", "Understood. Preparing our fleet to deploy, sir.");
    }

    private static void PatchPlutoReinforcements(List<DialogueLine> lines)
    {
        Set(lines, 5, "Samuel", "Great work, commander. We’ve kept them at bay for now. Local communications are restored, and we have Pluto’s full fleet online. Let’s prepare for the next wave.");
        // The design document has no Reinforcements failure line, while the playable mission can
        // fail-forward. Keep the existing failure-specific fallback rather than praising a loss.
    }

    private static void PatchPlutoPushback(List<DialogueLine> lines)
    {
        Set(lines, 0, "Samuel", "With the full force of Pluto’s fleet and the cover of some floating space junk coming our way, we’re going to try and push back the assault from these aliens.");
        Set(lines, 2, "Oviya", "I'm Oviya, your scout lieutenant. Use the scout to… well, scout the battlefield. They get around fast, so as long as you keep giving orders they probably won't get hit by enemy fire. They don’t have any guns, though, so please don’t leave them out to dry when they can’t fight back.");
        Set(lines, 3, "Oviya", "Oh! Right, I almost forgot: Scouts also come loaded up with five beacons! You can drop them anywhere and they'll detect enemies that enter their field of vision.");
        Set(lines, 4, "Joey", "Alrighty, Commander, I'm commanding yer frigates. They're yer explosives experts. They can't shoot far, but they sure pack a wallop.");
        Set(lines, 5, "Joey", "Those rockets will do some serious damage, and they can even hit multiple targets inside their blast radius.");
        Set(lines, 6, "Joey", "Use ‘em against those ships that like to group up like moths to a flame and you’ll find them quite effective.");
        Set(lines, 7, "Marco", "I'll be commanding your gunships. They're fast-flying, dogfighting specialists. Use their speed to your advantage when you can.");
        Set(lines, 8, "Marco", "Even if these newer recruits can't fly as well as me, they'll still be good at dodging fire. As long as you’re competent.");
        Set(lines, 9, "Yoshiko", "Alright! It's been a while since we've had a good fight. I'm your dreadnought lieutenant. These babies are made to brawl. They can take a lotta hits and dish it right back. Keep ‘em out front and watch ‘em tear it up!");
        Set(lines, 10, "Samuel", "And that’s everyone. Use all of our fleet’s strengths to win this battle, commander.");
        Set(lines, 11, "Samuel", "If you ever need a reminder on what any of our ships do or what we’ve discovered about the enemy’s fleet, you can always pause and view the United Fleet Codex.");
        Set(lines, 12, "Samuel", "I’ll be keeping it updated for you. I believe in you!");
    }

    private static void PatchPlutoBluerPastures(List<DialogueLine> lines)
    {
        Set(lines, 0, "Samuel", "Scouts are reporting overwhelming reinforcements from the enemy. We can’t defeat them, but we have to try and buy enough time for those on the planet to evacuate.");
        Set(lines, 1, "Samuel", "Commander, these… What do we call the mysterious alien fleet, anyway?");
        Set(lines, 2, "Oviya", "They do look like Bees. We can call them that.");
        Set(lines, 3, "Joey", "Let’s just call ‘em that. It’s easier than U.F.O.s. or ‘mysterious alien fleet.’");
        Set(lines, 4, "Samuel", "Um, right, whatever they are, they’re still coming. In order to evacuate the Pluto base, we have to keep the Bees from reaching the surface.");
        Set(lines, 15, "Yoshiko", "So those are kamikaze ships! Don’t let them near any of our fleet.");
        Set(lines, 17, "Marco", "This is a war, kid, worry about it later.");
    }

    private static void PatchPlutoToNeptune(List<DialogueLine> lines)
    {
        Set(lines, 1, "Marco", "Heavily.");
    }

    private static void PatchNeptuneSeizeTheMeans(List<DialogueLine> lines)
    {
        Set(lines, 1, "Samuel", "The scout team has reported movement, but were unable to determine if it’s friendly or not. We’ll just have to find out.");
        Set(lines, 3, "Wesley", "-day, Mayd- this is an emerg- enem- facility dest- need evac-", false);
        Set(lines, 10, "Marco", "Slow down and look at them; they’re attached to the asteroid’s surface.");
        Set(lines, 15, "Marco", "We can find help there. Those people in the mining facility will just have to hang on a bit longer until someone else can come back for them.");
        Set(lines, 34, "Wesley", "Well, um, we do have a line of prototype factory ships capable of mining ore-rich asteroids, as well as personnel trained to man them.");
        Set(lines, 42, "Wesley", "N-no, now, I’m sure w-we don’t have to resort to such… b-barbaric means! We’re all adults here!");
        Set(lines, 44, "Wesley", "Ahh! Please don’t engage in a physical altercation, it’s far from necessary! Please, use the factory ships however you please! We can negotiate fair compensation once we’ve made contact with my superiors.");
        Set(lines, 45, "Joey", "There ya go, commander. Factory ships at your disposal.");
    }

    private static void PatchNeptuneOfProduction(List<DialogueLine> lines)
    {
        Set(lines, 4, "Samuel", "Collect these ores as long as you can, commander, but don’t risk too many lives. It won’t be worth it in the end.");
        Set(lines, 5, "Wesley", "Be careful with those factories! They’re not built for combat or maneuverability.");
        Set(lines, 6, "Oviya", "In that case, you’ll have to plan your retreat carefully.");
        Set(lines, 7, "Oviya", "If those ships go down, the resources they collect will go down with them.");
        Set(lines, 16, "Samuel", "We’ve lost all the ships we sent out, commander. It’s time to regroup.");
        // The extra final-retreat acknowledgement at index 17 is required by the current retreat
        // flow to finish the mission after the last ship leaves; the document does not specify it.
    }

    private static void PatchTitaniaBeenoculars(List<DialogueLine> lines)
    {
        if (lines == null || lines.Count <= 11)
        {
            return;
        }

        Set(lines, 10, "Alejandra", "Affirmative. Good luck, commander.");
        if (!TitaniaRouteState.DidWinTitaniaOne)
        {
            // The current mission starts the base dialogue synchronously before the campaign
            // override guard can enqueue another line. Keep the authored loss-only warning in the
            // same Alejandra turn so it still appears before control is returned to the player.
            lines[10].Text += "\n\nWe’re already under heavy fire. You have your work cut out for you, but we’ll focus our efforts on an expedient evacuation.";
        }
    }

    private static void PatchUranusOnTheOffensive(List<DialogueLine> lines)
    {
        Set(lines, 10, "Samuel", "They’re… different.");
        Set(lines, 15, "Joey", "That’s not gonna be so easy. Its weapon has a huge range from what I can tell.");
        Set(lines, 38, "Samuel", "All of our fleet we sent out is down, commander.");
    }

    private static void PatchUranusOnTheDefensive(List<DialogueLine> lines)
    {
        Set(lines, 4, "Joey", "More bees? What in tarnation? Where are they coming from?");
        Set(lines, 5, "Oviya", "We’ve found more bee ships, inbound to our location.");
        Set(lines, 7, "Wesley", "More of those… bees? Don’t risk our profit margins. You’ll receive a bill from Jensen if you lose any factory ships.");
        Set(lines, 11, "Emilia", "Oh! Uh- is this thing on? Commander! There’s more bees! Get ‘em!");
        Set(lines, 13, "Samuel", "We’ve lost all the ships we sent out, commander. It’s time to regroup.");
    }

    private static void PatchUranusANewThreat(List<DialogueLine> lines)
    {
        Set(lines, 28, "Oviya", "Let’s get within scouting distance as soon as possible. If it’s as bad as we think, we need to stop this as soon as possible.");
        Set(lines, 39, "Emilia", "You think they can make more bees?");
        Set(lines, 40, "Oviya", "Let’s get within scouting distance. If it’s as bad as we think, we need to stop this as soon as possible.");
        Set(lines, 41, "Yoshiko", "Oh, this is gonna be a big fight, I can tell…");
    }

    private static void PatchSelectedCarrier(List<DialogueLine> lines)
    {
        Set(lines, 0, "Emilia", "Alright, commander, you need to know how the carrier works!");
    }

    private static List<DialogueLine> BuildNeptuneToTitania()
    {
        return new List<DialogueLine>
        {
            Line("Samuel", "Commander, I’ve been reviewing the colonies and bases throughout the solar system. While there is a small UF outpost on Uranus proper, there’s also a research and engineering base on one of its moons, Titania. We should be able to find allies here."),
            Line("Marco", "If there’s anyone left."),
            Line("Samuel", "If- yeah, if… I’m just trying to think through our options."),
            Line("Oviya", "Titania is en route to Uranus. I’ll have some scouts look ahead and see if there’s anything worth stopping for."),
            Line("Yoshiko", "You think the nerds on Titania might have cool new weapons we can use?"),
            Line("Samuel", "Nerds?"),
            Line("Yoshiko", "Yeah, you said it’s a research and… whatever base. I love those nerds."),
            Line("Joey", "Ha! We could probably use some more nerds on our side. Even one other person who understands how explosives work."),
            Line("Yoshiko", "Just cuz I blew up one frigate-"),
            Line("Marco", "Can we focus, please?"),
            Line("Oviya", "Scouts are reporting Titania base looks evacuated. No damage, but no signals either. And lots of bee patrols."),
            Line("Samuel", "Wait a minute, we’re getting a request right now! Let me put them on…"),
            new DialogueLine("Samuel", Portrait("Samuel"), 1f),
            Line("Alejandra", "Hello? Do you read me?"),
            Line("Samuel", "Loud and clear, ma’am."),
            Line("Alejandra", "Ah, good. This is Alejandra Vasquez, research and engineering wing, regional commander and research lead for United Fleet Titania."),
            Line("Emilia", "Ohhhh, who are they?"),
            Line("Alejandra", "Emilia, please. We saw other United Fleet vessels around our base. I presume those belong to your contingent?"),
            Line("Oviya", "Yes, that was my scout team. We thought your base was abandoned, though."),
            Line("Emilia", "We need help!"),
            Line("Alejandra", "I was getting there, Emilia."),
            Line("Emilia", "We can’t get off the base right now!"),
            Line("Alejandra", "That is correct. The initial assault of these unidentified aliens targeted the surface of Uranus, primarily. They were swiftly dismantled. We sent a signal on the emergency frequency, as is protocol, but they detected us. Their attack was more than we could handle, and we had no response from the fleet. We’re only alive because of Emilia’s father."),
            Line("Samuel", "What did he do?"),
            Line("Alejandra", "I… don’t wish to discuss it in current company."),
            Line("Emilia", "He blew up the weapons bay to surround the base in junk and experimental explosives."),
            Line("Samuel", "What’s so bad about saying that?"),
            Line("Alejandra", "Philip is... No longer with us."),
            Line("Samuel", "Oh- oh, I’m so sorry…."),
            Line("Emilia", "It’s fine… he’s… he saved us."),
            Line("Alejandra", "Precisely. His selfless act is the only reason we’re alive. If it weren’t for that, the bee patrols around us would have decimated us days ago. But ironically, it is now the reason we are stuck."),
            Line("Emilia", "You can help us! You have fighting ships! And we have a carrier to help too!"),
            Line("Yoshiko", "Oh, what’s a carrier?"),
            Line("Emilia", "It’s this awesome ship my dad designed. It has a bunch of little robot controlled ships. We call ‘em drones and strikers, and they can-"),
            Line("Alejandra", "Please, Emilia, I’d love to discuss the details of Philip’s designs, but we don’t have the time right now. To abbreviate this conversation, would your fleet be able to assist in our attempt to extricate our staff and their families from this moon? The prototypal carrier is our only military vessel at the moment."),
            Line("Emilia", "Right! We’re running out of food and batteries!"),
            Line("Samuel", "Of course we can help. We’re already pretty experienced bee fighters."),
            Line("Alejandra", "Bees?"),
            Line("Samuel", "Erm… yes, that’s what we’re calling the aliens."),
            Line("Alejandra", "Yes, I do suppose they bear some resemblance to the endangered species. Fascinating observation."),
            Line("Emilia", "I like that name! Stupid bees! Yeah, it feels good to say."),
            Line("Marco", "Quit wasting time."),
            Line("Samuel", "Right. Commander, what are your orders?"),
        };
    }

    private static void Set(List<DialogueLine> lines, int index, string speaker, string text, bool? isUnknown = null)
    {
        if (lines == null || index < 0 || index >= lines.Count || lines[index] == null)
        {
            return;
        }

        DialogueLine line = lines[index];
        string portraitKey = speaker == "A.M.I." ? "AMI" : speaker;
        line.SpeakerName = speaker;
        if (CutsceneManager.Portraits.TryGetValue(portraitKey, out Sprite[] portraits) && portraits.Length >= 2)
        {
            line.PortraitA = portraits[0];
            line.PortraitB = portraits[1];
        }
        line.Text = text;
        if (isUnknown.HasValue)
        {
            line.IsUnknown = isUnknown.Value;
        }
    }

    private static DialogueLine Line(string speaker, string text)
    {
        return new DialogueLine(speaker == "AMI" ? "A.M.I." : speaker, Portrait(speaker), text);
    }

    private static Sprite[] Portrait(string speaker)
    {
        return CutsceneManager.Portraits[speaker];
    }
}

/// <summary>
/// CutsceneManager.Setup rebuilds its dialogue lists each time a mission registers an ending.
/// LevelIntro applies overrides directly; live campaign stages use this late guard so the same
/// authored dialogue is restored immediately after each rebuild without coupling every mission
/// setup method to the dialogue document.
/// </summary>
[DefaultExecutionOrder(10000)]
internal sealed class CampaignDialogueOverrideGuard : MonoBehaviour
{
    private readonly Dictionary<int, DialogueLine> _appliedMarkers = new Dictionary<int, DialogueLine>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Install()
    {
        GameObject host = new GameObject("Campaign Dialogue Override Guard");
        Object.DontDestroyOnLoad(host);
        host.AddComponent<CampaignDialogueOverrideGuard>();
    }

    private void Update()
    {
        if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign)
        {
            return;
        }

        foreach (CutsceneManager manager in Object.FindObjectsOfType<CutsceneManager>())
        {
            if (manager == null || manager.PlutoLines_Anomaly == null || manager.PlutoLines_Anomaly.Count == 0)
            {
                continue;
            }

            DialogueLine marker = manager.PlutoLines_Anomaly[0];
            int id = manager.GetInstanceID();
            if (_appliedMarkers.TryGetValue(id, out DialogueLine appliedMarker) &&
                ReferenceEquals(marker, appliedMarker))
            {
                continue;
            }

            CampaignDialogueOverrides.Apply(manager);
            _appliedMarkers[id] = marker;
        }
    }
}
