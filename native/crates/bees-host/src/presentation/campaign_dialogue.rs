//! Authored Titania dialogue from CutsceneManager.cs and CampaignDialogueOverrides.cs.

use bees_gameplay::campaign_dialogue::{CampaignCue, UploadMilestone};

pub(crate) fn lines(cue: CampaignCue) -> &'static [(&'static str, &'static str)] {
    match cue {
        // Original PlutoLines_Anomaly is split at physical Scout sighting,
        // Tom's real Gunship arrival, and the first Honeybee kill. Only
        // source non-speaking pause and audio staging lines are omitted;
        // this one existing campaign cue machine owns all stage boundaries.
        CampaignCue::PlutoAnomalyBriefing => &[
            ("Samuel", "Good morning, Commander! I brought your coffee."),
            ("Samuel", "I agree, it doesn't taste as good as Earth coffee. Or even Mars coffee… But hey, coffee is coffee! And we’ll both get off of Pluto soon enough."),
        ],
        CampaignCue::PlutoAnomalyHoneybeeSpotted => &[
            ("Samuel", "Oh, that's odd. A Scout is reporting an unidentified vessel approaching military airspace. What should we do?"),
            ("Samuel", "Right away, sir. Contacting the vessel."),
            ("Samuel", "It isn’t responding, sir."),
            ("Samuel", "Understood, sir. We’ll send Lieutenant Tom out immediately."),
        ],
        // The report's completion releases actual deferred Gunship squads.
        CampaignCue::PlutoAnomalyScoutReportComplete => &[],
        CampaignCue::PlutoAnomalyGunshipArrived => &[
            ("Tom", "This is Gunship P-4 reporting to command. I’m approaching the unidentified vessel now."),
            ("Tom", "Unidentified vessel, you are in United Fleet airspace. Identify yourself immediately."),
            ("Samuel", "Still nothing, even on local communications?"),
            ("Tom", "Negative."),
            ("Samuel", "Strange. It doesn’t seem hostile. What are your orders- oh, we’re getting a call from High Command."),
            ("High Command", "Commander, we have received reports of an alien vessel in Pluto airspace. We cannot allow it to infiltrate our territory. Shoot it down."),
            ("Samuel", "Shoot it down? We don’t even know what it is! Who even reported this?"),
            ("High Command", "Those are your orders, Commander."),
            ("Samuel", "But- oh, they disconnected. Looks like we have to attack, sir."),
            ("Tom", "What are your orders, Commander?"),
            ("Samuel", "In order to attack, he’ll need to be in range of his ship’s guns. Once in range, he can attack the ship."),
        ],
        CampaignCue::PlutoAnomalyGunshipBriefingComplete => &[],
        CampaignCue::PlutoAnomalyHoneybeeDestroyed => &[
            ("Tom", "Well, that was hardly a fight."),
            ("Samuel", "I hope it wasn’t an innocent civilian. Why would High Command even order that?"),
            ("Tom", "Uh, Commander? Are you seeing this?"),
            ("Samuel", "You need to get out of there, now!"),
        ],
        CampaignCue::PlutoAnomalyVictory => &[
            ("Samuel", "Their fleet is huge! We need to contact High Command immediately!"),
            ("Samuel", "Communications are down, sir. What should we do?"),
            ("Samuel", "Understood. Preparing our fleet to deploy, sir."),
        ],
        CampaignCue::PlutoAnomalyDefeat => &[
            ("Samuel", "We've lost contact with our scouting force, commander."),
        ],
        // Pluto II's first two original lines are already given by
        // LevelIntro preparation, outside the playable mission. The live
        // cue begins with the original third line and its Scout objective.
        CampaignCue::PlutoReinforcementsBriefing => &[
            ("Samuel", "Okay, commander, it's up to you to lead us to victory."),
            ("Samuel", "Start with the first Scout. Select it to inspect its squad controls."),
        ],
        CampaignCue::PlutoReinforcementsScoutSelected => &[
            ("Samuel", "Here are different settings for your ship. You can determine your squad’s flight pattern and shooting strategies here. Take some time to familiarize yourself with these options."),
            ("Samuel", "You can also select squads with the number hotkeys on your keyboard. These are displayed at the top of the screen."),
            ("Samuel", "If you need to select multiple squads, click and drag the mouse over the squads."),
            ("Samuel", "Your ships with weapons will automatically shoot at any enemies in range. You can view your selected ships’ ranges at any time by holding R."),
            ("Samuel", "You can manually fire towards your cursor with any selected ships by pressing F."),
            ("Samuel", "I recommend you try to find out where the enemy is with your scouts, then form a plan of attack. I’ll be working on restoring our local communications with the rest of the base."),
            ("Samuel", " . . .Also, Marco wants me to remind you that we are… now at war, so all of our combat vessels are ordered to fire on sight by default."),
        ],
        CampaignCue::PlutoReinforcementsCombatReady => &[],
        CampaignCue::PlutoReinforcementsVictory => &[
            ("Samuel", "Great work, commander. We’ve kept them at bay for now. Local communications are restored, and we have Pluto’s full fleet online. Let’s prepare for the next wave."),
        ],
        CampaignCue::PlutoReinforcementsDefeat => &[
            ("Samuel", "Commander, we may not have won that fight but we've bought enough time for local communications to be restored and more of the fleet to be brought online. Let’s prepare for the next wave."),
        ],
        // Original Neptune_OfProduction sections (source dialogue IDs 2-17).
        CampaignCue::NeptuneProductionRetreatOpened => &[
            ("Samuel", "The commander is calling a retreat. Regroup for our next battle!"),
        ],
        CampaignCue::NeptuneProductionBriefing => &[
            ("Oviya", "The mining asteroids can be found at these locations."),
            ("Wesley", "Our Factory ships should be quite capable of increasing your prof- erm, resources in order to further bolster your fleet. Simply direct them to a mining location."),
            ("Samuel", "Collect these ores as long as you can, commander, but don’t risk too many lives."),
            ("Samuel", "If a Factory goes down, it’ll lose all the resources it collected. It won’t be worth it in the end."),
            ("Wesley", "Be careful with those Factories! They’re not built for combat or maneuverability."),
            ("Oviya", "In that case, you’ll have to plan your retreat carefully."),
        ],
        CampaignCue::NeptuneProductionWave { line, .. } => match line {
            8 => &[("Oviya", "It looks like there are bee scouting parties approaching. They’ll soon find out their fleet here was destroyed.")],
            9 => &[("Oviya", "More bees are en route. The longer we stay here, the more dangerous it becomes, commander.")],
            10 => &[("Yoshiko", "Incoming ships! Get ready for a fight!")],
            11 => &[("Marco", "More bees. Hope you know what you’re doing, commander.")],
            12 => &[("Joey", "Gracious, how many more bees are there?")],
            13 => &[("Oviya", "Scouts are reporting even more bees than before. Brace yourselves.")],
            14 => &[("Samuel", "Commander, get ready for another fight. We have bees incoming.")],
            _ => &[],
        },
        CampaignCue::NeptuneProductionEnding { last_ship_retreated: true, .. } |
        CampaignCue::NeptuneProductionEnding { player_victory: true, .. } => &[
            ("Samuel", "That's everyone commander, we made it out."),
        ],
        CampaignCue::NeptuneProductionEnding { .. } => &[
            ("Samuel", "We've lost the last ships we sent out, commander. It’s time to regroup."),
        ],
        // Original Pluto III line 0 is shown by LevelIntro, while
        // this battle's gated dialogue starts at source index 1.
        // victory 13..22, defeat 22..31. Source one-second pauses
        // remain empty, non-speaking dialogue lines.
        CampaignCue::PlutoPushbackBriefing { .. } => &[
            ("Samuel", "Alright, commander, we have all of our Lieutenants online. They’ll be giving orders for each of your vessels as you direct the entire fleet."),
            ("Oviya", "I'm Oviya, your scout lieutenant. Use the scout to… well, scout the battlefield. They get around fast, so as long as you keep giving orders they probably won't get hit by enemy fire. They don’t have any guns, though, so please don’t leave them out to dry when they can’t fight back."),
            ("Oviya", "Oh! Right, I almost forgot: Scouts also come loaded up with five beacons! You can drop them anywhere and they'll detect enemies that enter their field of vision."),
            ("Joey", "Alrighty, Commander, I'm commanding yer frigates. They're yer explosives experts. They can't shoot far, but they sure pack a wallop."),
            ("Joey", "Those rockets will do some serious damage, and they can even hit multiple targets inside their blast radius."),
            ("Joey", "Use ‘em against those ships that like to group up like moths to a flame and you’ll find them quite effective."),
            ("Marco", "I'll be commanding your gunships. They're fast-flying, dogfighting specialists. Use their speed to your advantage when you can."),
            ("Marco", "Even if these newer recruits can't fly as well as me, they'll still be good at dodging fire. As long as you’re competent."),
            ("Yoshiko", "Alright! It's been a while since we've had a good fight. I'm your dreadnought lieutenant. These babies are made to brawl. They can take a lotta hits and dish it right back. Keep ‘em out front and watch ‘em tear it up!"),
            ("Samuel", "And that’s everyone. Use all of our fleet’s strengths to win this battle, commander."),
            ("Samuel", "If you ever need a reminder on what any of our ships do or what we’ve discovered about the enemy’s fleet, you can always pause and view the United Fleet Codex."),
            ("Samuel", "I’ll be keeping it updated for you. I believe in you!"),
        ],
        CampaignCue::PlutoPushbackVictory => &[
            ("Samuel", "Great work, commander! This might not be so b-"),
            ("Oviya", "Commander, I’ve had my team scouting further ahead during the battle, and… it’s not looking good. There’s… more than we can count."),
            ("Yoshiko", "Good, we didn’t get to have much of a fight yet!"),
            ("Oviya", "No, Yoshiko, it’s far too many for us to defeat here and now."),
            ("Marco", "Commander, I recommend an emergency retreat. We need to regroup and assess this threat before going fully to war with it."),
            ("Oviya", "I agree."),
            ("Samuel", "Commander?"),
            ("Samuel", ""),
            ("Samuel", "Then it’s settled. I’ll issue the evacuation alert, and you’ll prepare the fleet to defend Pluto until we can get our people off the surface."),
        ],
        CampaignCue::PlutoPushbackDefeat => &[
            ("Samuel", "These aliens are much tougher than I thought…"),
            ("Oviya", "Commander, I’ve had my team scouting further ahead during the battle, and… it’s not looking good. There’s… more than we can count."),
            ("Yoshiko", "Good, let’s win the next fight then!"),
            ("Oviya", "No, Yoshiko, it’s far too many for us to defeat here and now. Especially if that first wave was too much for us."),
            ("Marco", "Commander, I recommend an emergency retreat. We need to regroup and assess this threat before going fully to war with it."),
            ("Oviya", "I agree."),
            ("Samuel", "Commander?"),
            ("Samuel", ""),
            ("Samuel", "Then it’s settled. I’ll issue the evacuation alert, and you’ll prepare the fleet to defend Pluto until we can get our people off the surface."),
        ],
        CampaignCue::UranusNewThreatBriefing => &[
            ("Marge", "Is this dang thing working? Hello! Any humans left? Hang in there, team, we’re gonna get through this, okay?"),
            ("Barge Pilot", "It’s not looking good, Marge…"),
            ("Marge", "I'll get you through, okay?"),
            ("Samuel", "Hello? Identify yourself."),
            ("Marge", "Oh, thank the stars! Are you UF? Come help us, quick! We’re surrounded over here!"),
        ],
        CampaignCue::UranusNewThreatBargeLost => &[
            ("Marge", "No!!! They’re gonna pay for that!"),
        ],
        CampaignCue::UranusNewThreatBargeCharged => &[
            ("Marge", "Just cuz I don’t have guns don't mean I can’t fight! Take this!"),
        ],
        CampaignCue::UranusNewThreatReinforcements => &[
            ("Marge", "There’s more coming! "),
            ("Yoshiko", "Those are Yellow Jackets! Don’t let them hit those Barges!"),
        ],
        CampaignCue::UranusNewThreatEnding { barges_survived: false, .. } => &[
            ("Samuel", "We couldn’t save them…"),
            ("Marco", "Sometimes that’s life, kid. We’ll avenge them."),
            ("Oviya", "It looks like they were coming from Saturn."),
            ("Yoshiko", "Bees included. This is bad."),
            ("Emilia", "Why’s that? Well obviously it’s bad because the bees attacked there."),
            ("Joey", "Saturn’s the biggest UF station this side of the asteroid belt."),
            ("Alejandra", "And its largest off-Earth manufacturing site."),
            ("Joey", "They’re mighty fine facilities."),
            ("Marco", "If they’re smart, they’re using those facilities."),
            ("Emilia", "You think they can make more bees? They can probably make more bees."),
            ("Oviya", "Let’s get within scouting distance. If it’s as bad as we think, we need to stop this as soon as possible."),
            ("Yoshiko", "Oh, this is gonna be a big fight. I can tell!"),
        ],
        CampaignCue::UranusNewThreatEnding { barges_survived: true, has_cruiser: false } => &[
            ("Marge", "I thought we were goners. I can’t thank you enough for risking yourselves."),
            ("Marco", "We're getting pretty used to these rescue missions, don’t sweat it."),
            ("Marge", "…right."),
            ("Samuel", "How’d you end up here?"),
            ("Marge", "Well, we were on a transport line from Saturn. Those… things-"),
            ("Emilia", "Bees!"),
            ("Marge", "They do look like that, huh? Wait- you all don’t think they’re really bees, right?"),
            ("Alejandra", "Of course not, that’s preposterous."),
            ("Marge", "Right. Glad most of us have our heads screwed on straight! Either way, they chased behind us during our trip over."),
            ("Oviya", "And they chased you from Saturn. This is bad."),
            ("Emilia", "Why’s that? Well obviously it’s bad because the bees attacked there."),
            ("Joey", "Saturn’s the biggest UF station this side of the asteroid belt."),
            ("Alejandra", "And its largest off-Earth manufacturing site."),
            ("Joey", "They’re mighty fine facilities."),
            ("Marco", "If they’re smart, they’re using those facilities."),
            ("Emilia", "You think they can make more bees? They can probably make more bees."),
            ("Oviya", "Let’s get within scouting distance as soon as possible. If it’s as bad as we think, we need to stop this as soon as possible."),
            ("Yoshiko", "Oh, this is gonna be a big fight. I can tell!"),
        ],
        CampaignCue::UranusNewThreatEnding { barges_survived: true, has_cruiser: true } => &[
            ("Marge", "I thought we were goners. I can’t thank you enough for risking yourselves."),
            ("Marco", "We're getting pretty used to these rescue missions, don’t sweat it."),
            ("Fritz", "And we get to explode stuff! Win win!"),
            ("Marge", "…right."),
            ("Samuel", "How’d you end up here?"),
            ("Marge", "Well, we were on a transport line from Saturn. Those… things-"),
            ("Emilia", "Bees!"),
            ("Marge", "They do look like that, huh? Wait- you all don’t think they’re really bees, right?"),
            ("Alejandra", "Of course not, that’s preposterous."),
            ("Fritz", "…really?! Jeremy lied to me…"),
            ("Marge", "Right. Glad most of us have our heads screwed on straight! Either way, they chased behind us during our trip over."),
            ("Oviya", "And they chased you from Saturn. This is bad."),
            ("Emilia", "Why’s that? Well obviously it’s bad because the bees attacked there."),
            ("Joey", "Saturn’s the biggest UF station this side of the asteroid belt."),
            ("Alejandra", "And its largest off-Earth manufacturing site."),
            ("Joey", "They’re mighty fine facilities."),
            ("Marco", "If they’re smart, they’re using those facilities."),
            ("Emilia", "You think they can make more bees? They can probably make more bees."),
            ("Oviya", "Let’s get within scouting distance as soon as possible. If it’s as bad as we think, we need to stop this as soon as possible."),
            ("Yoshiko", "Oh, this is gonna be a big fight. I can tell!"),
        ],
        CampaignCue::UranusOffensiveBriefing => &[
            ("Oviya", "Bee presence around Uranus is heavy, but not insurmountable. We’re far enough from the blockade that we aren’t expecting reinforcements now."),
            ("Oviya", "Victory should ensure us time to collect more resources for the fleet."),
            ("Samuel", "We aren’t receiving any human signals, but it does look like there’s fighting ahead."),
            ("Yoshiko", "Let’s get in there! And fast!"),
        ],
        CampaignCue::UranusOffensiveBumblebeeSpotted => &[
            ("Oviya", "Commander! This is a bee ship we haven’t seen before. Be careful; we don’t know what it can do."),
        ],
        CampaignCue::UranusOffensiveBumblebeeHit => &[
            ("Samuel", "We just took massive damage from the unknown ship, commander!"),
            ("Yoshiko", "Find a way to take it out!"),
            ("Joey", "That’s not gonna be so easy. Its weapon has a huge range from what I can tell."),
        ],
        CampaignCue::UranusOffensiveBumblebeeDestroyed => &[
            ("Samuel", "Good work, commander. That was scary."),
            ("Marco", "I’m sure there will be more later…"),
        ],
        CampaignCue::UranusOffensiveEnding { player_victory: true, has_factory: true } => &[
            ("Wesley", "Your mining vessels are ready for the asteroids, commander. I recommend initiating operations quickly, before more of these bees arrive."),
            ("Alejandra", "I like the way you think! I’m glad somebody else here cares about efficiency."),
            ("Wesley", "Efficiency creates profit, ma’am."),
        ],
        CampaignCue::UranusOffensiveEnding { player_victory: true, has_factory: false } => &[],
        CampaignCue::UranusOffensiveEnding { player_victory: false, .. } => &[
            ("Samuel", "All of our fleet we sent out is down, commander."),
            ("Yoshiko", "We can still take ‘em!"),
            ("Marco", "We can’t risk further casualties, Yoshiko. Keep your head on straight."),
            ("Oviya", "Hold on, we have more bees on our radar! They’re… not coming towards us?"),
            ("Marco", "No rest for the weary…"),
            ("Samuel", "Alright, commander, gather your squads and let's investigate."),
        ],
        CampaignCue::UranusDefensiveBriefing => &[
            ("Samuel", "Our success has bought us some time to collect resources around Uranus. There will likely be reinforcements called on from the bees we just fought, though. Take care."),
        ],
        CampaignCue::UranusDefensiveRetreatOpened => &[
            ("Samuel", "Fleet, it's time to regroup and head out! Return to the commander's position immediately."),
        ],
        CampaignCue::UranusDefensiveWave { line, .. } => match line {
            1 => &[("Oviya", "Reinforcements from the remnants of the blockade are incoming.")],
            2 => &[("Yoshiko", "More bees! Let’s give ‘em hell!")],
            3 => &[("Marco", "Even more ships. Commander, I trust you’ll know when to exit.")],
            4 => &[("Joey", "More bees? What in tarnation? Where are they coming from?")],
            5 => &[("Oviya", "We've found more bee ships, inbound to our location.")],
            6 => &[("Samuel", "Another wave of ships!")],
            7 => &[("Wesley", "More of those… bees? Don’t risk our profit margins. You’ll receive a bill from Jensen if you lose any Factories.")],
            8 => &[("Oviya", "We're seeing more reinforcements from a different direction, now. It could be their main fleet. Things are going to get tough from here.")],
            9 => &[("Fritz", "Ahaha! We have more bees come to meet the light! That’s right, Dee, this one’s for you!")],
            10 => &[("Alejandra", "More wrinkles in our plans approaching. I trust you can sort them out, commander.")],
            11 => &[("Emilia", "Oh! Uh- is this thing on? Commander! There’s more bees! Get 'em!")],
            _ => &[],
        },
        CampaignCue::UranusDefensiveEnding { last_ship_retreated, player_victory } => {
            const RETREATED: &[(&str, &str)] = &[
                ("Samuel", "That was close! Let's get out of here."),
                ("Oviya", "Hold on, we have more bees on our radar! They’re… not coming towards us?"),
                ("Marco", "No rest for the weary…"),
                ("Samuel", "Alright, commander, gather your squads and let's investigate."),
            ];
            const DEFEATED: &[(&str, &str)] = &[
                ("Samuel", "We've lost the last ship we sent out, commander. It’s time to regroup."),
                ("Oviya", "Hold on, we have more bees on our radar! They’re… not coming towards us?"),
                ("Marco", "No rest for the weary…"),
                ("Samuel", "Alright, commander, gather your squads and let's investigate."),
            ];
            if player_victory { RETREATED } else if last_ship_retreated { RETREATED }
            else { DEFEATED }
        },
        CampaignCue::MinesweeperBriefing => &[
            ("Samuel", "We need to clear out the debris surrounding Titania to give our new friends an exit path. Bee patrols have also been spotted around the area, trying to make sure nothing gets in or out."),
        ],
        CampaignCue::FireTankSpotted { .. } => &[
            ("Alejandra", "Commander, that's one of the explosives."),
            ("Emilia", "You can tell it's a bomb cuz we put a big blinky light on it!"),
            ("Marco", "Can we please remove her from this frequency?"),
            ("Alejandra", "No. As I was saying, Commander, that's one of the bombs."),
            ("Joey", "If we detonate it, it should be able to clear out the debris and cut a path straight to the surface."),
            ("Oviya", "It will also alert the bee patrols."),
            ("Alejandra", "Your fleet's automatic targeting systems won't see it, so you'll have to fire manually and precisely. Once the debris has been jettisoned, we can begin evacuating the base."),
            ("Joey", "Should we be worried about stray debris hitting your base?"),
            ("Alejandra", "The explosives should be powerful enough to disintegrate any debris. Ah. Yes, don't fly too closely."),
            ("Marco", "They're powerful enough to what?"),
            ("Emilia", "She said they were experimental!"),
        ],
        CampaignCue::FireTankDestroyed { .. } => &[
            ("Yoshiko", "Woohoo! Now this is fun!"),
            ("Emilia", "Aww man, I wish I could see."),
            ("Marco", "Stay focused. We're still fighting out here."),
            ("Alejandra", "If the bees weren't alerted to your presence already, they will be now. Stay vigilant."),
        ],
        CampaignCue::MinesweeperVictory => &[
            ("Alejandra", "Excellent work, Commander. We'll begin the evacuation process immediately."),
            ("Oviya", "This fight alerted the bees on Uranus's surface."),
            ("Marco", "Fantastic..."),
            ("Samuel", "Looks like we need to cover their exit, commander."),
            ("Yoshiko", "Stay frosty, everyone!"),
        ],
        CampaignCue::MinesweeperDefeat => &[
            ("Samuel", "We can't clear the bees out, commander."),
            ("Oviya", "More from Uranus's surface have been alerted to our presence as well."),
            ("Alejandra", "What do you propose we do, then?"),
            ("Emilia", "We're gonna die..."),
            ("Alejandra", "None of that, Emilia."),
            ("Yoshiko", "We can still attempt an evacuation, but it'll be risky."),
            ("Marco", "Too risky."),
            ("Samuel", "Don't worry, ma'am, we will get you out."),
            ("Marco", "...Don't make promises you can't keep, son."),
            ("Samuel", "Lives are at stake! Commander, what do we do?"),
        ],
        CampaignCue::BeenocularsBriefing { under_heavy_fire } => {
            const BRIEFING: &[(&str, &str)] = &[
            ("Samuel", "Commander, we have to defend Titania's base long enough for all of their personnel to escape the surface."),
            ("Alejandra", "Our personnel are preparing for evacuation. Rebooting base systems now."),
            ("Emilia", "I'm gonna get A.M.I.!"),
            ("Alejandra", "Em- ugh, she's already run off."),
            ("Yoshiko", "Amy?"),
            ("Alejandra", "A.M.I. She's a strategy AI our team was developing for military applications."),
            ("Joey", "Sounds mighty helpful. Is there a problem with that?"),
            ("Alejandra", "It will consume a considerable amount of time to transfer her to a portable device. Time that I don't surmise we have."),
            ("Samuel", "We'll do our best, ma'am."),
            ("Marco", "If this goes sideways, make damn sure that child is ready to leave on our orders."),
            ("Alejandra", "Affirmative. Good luck, commander."),
            ("Alejandra", "We're already under heavy fire. You have your work cut out for you, but we'll focus our efforts on an expedient evacuation."),
            ];
            let count = BRIEFING.len() - usize::from(!under_heavy_fire);
            &BRIEFING[..count]
        },
        CampaignCue::BeenocularsUpload(UploadMilestone::Ten) => &[
            ("Alejandra", "Upload is at ten percent, commander."),
            ("Oviya", "The bees are definitely starting to take notice!"),
            ("Marco", "Keep your wits about you, team."),
        ],
        CampaignCue::BeenocularsUpload(UploadMilestone::TwentyFour) => &[
            ("Emilia", "Upload's at twenty four percent!"),
            ("Alejandra", "Why would you not wait until twenty five? It's a good fraction of one hundred."),
            ("Yoshiko", "It's turning into a real fight out here!"),
        ],
        CampaignCue::BeenocularsUpload(UploadMilestone::Fifty) => &[
            ("Alejandra", "Fifty percent, now, commander."),
            ("Emilia", "You're doing awesome! Keep it up!"),
            ("Marco", "Don't get too comfortable now."),
        ],
        CampaignCue::BeenocularsUpload(UploadMilestone::SeventyFive) => &[
            ("Emilia", "Seventy fo-"),
            ("Alejandra", "Seventy five percent."),
            ("Marco", "Things are getting dicey out here. That AI better be worth the trouble."),
        ],
        CampaignCue::BeenocularsUpload(UploadMilestone::Ninety) => &[
            ("Alejandra", "Upload is at ninety percent, commander. You are doing an excellent job out there."),
            ("Joey", "Just another day, ma'am."),
        ],
        CampaignCue::BeenocularsVictory => &[
            ("Alejandra", "A.M.I. is fully uploaded, commander. We are engaging the ship's liftoff now."),
            ("Emilia", "Let's go! You guys are the real deal!"),
        ],
        CampaignCue::BeenocularsDefeat => &[
            ("Samuel", "Commander is calling a retreat!"),
            ("Emilia", "But-"),
            ("Alejandra", "Emilia, no. We are leaving now."),
            ("Emilia", "Sorry..."),
            ("Alejandra", "We'll join your formation shortly, commander."),
        ],
        // Source Pluto4BluerPastures[1..13] introduces the first actual
        // civilian-defense mission. Keep its casualty/fleet survival guidance
        // with the real five-minute HumanTarget and reinforcement schedule;
        // no separate tutorial simulation, timer or staged enemy is created.
        CampaignCue::PlutoEvacuationBriefing { .. } => &[
            // Original line 0 was spoken in the prior mission's travel
            // preparation, not repeated inside Pluto4BluerPastures.
            ("Samuel", "Commander, these… What do we call the mysterious alien fleet, anyway?"),
            ("Oviya", "They do look like Bees. We can call them that."),
            ("Joey", "Let’s just call ‘em that. It’s easier than U.F.O.s. or ‘mysterious alien fleet.’"),
            ("Samuel", "Um, right, whatever they are, they’re still coming. In order to evacuate the Pluto base, we have to keep the Bees from reaching the surface."),
            ("Samuel", "If they get to that point, we’re going to start losing people and ships before they can lift off."),
            ("Samuel", "This is a full emergency order, so personnel have to be on space-ready vessels within 5 minutes. As soon as we have the fleet ready to leave, we’ll evacuate out of here at full speed."),
            ("Yoshiko", "We’ve got this, commander! Dreadnoughts standing by."),
            ("Marco", "Gunships at the ready."),
            ("Joey", "Frigates ready to go!"),
            ("Oviya", "I’ve got the Scouts mapping an escape route."),
            ("Yoshiko", "Let's take out as many as we can on our way out! That’ll show 'em!"),
            ("Marco", "Don’t be risky with our fleet’s lives, Yoshiko. Destroy enemy ships where we can safely. That’s the only way we’ll get through this."),
            // The source's optional pre-battle tooltip sequence is
            // delivered alongside its regular briefing until user-local
            // tooltip preferences exist in the native game.
            ("Samuel", "Ships lost in a campaign battle are permanently lost. Your surviving fleet will carry on, but if your entire fleet is destroyed, the campaign will end."),
            ("Samuel", "The Bees have finite resources, too. Every enemy ship you destroy reduces the invasion forces available later, while ships left alive can return in a later mission."),
            ("Samuel", "For this evacuation, each group of personnel saved strengthens your future fleet. Preserve your ships, defend the surface, and thin the invasion as much as you can."),
        ],
        // Original PlutoLines_BluerPastures[14..18]. Each is released
        // only by its corresponding live physical weapon/contact event.
        CampaignCue::PlutoEvacuationSplitterShot => &[
            ("Joey", "That ship's blasts are splitting up on impact. Don't group yer ships up near it."),
        ],
        CampaignCue::PlutoEvacuationYellowJacketHit => &[
            ("Yoshiko", "So those are suicide ships! Don't let them near any of our ships."),
            ("Samuel", "I hope they aren't manned…"),
            ("Marco", "This is a war, boy, worry about it later."),
        ],
        CampaignCue::PlutoEvacuationComplete { shield_health: 0, .. } => &[
            ("Samuel", "Comms with the surface went down, commander."),
            ("Marco", "They're a lost cause. Get out of here, now."),
            ("Samuel", "But-"),
            ("Yoshiko", "He's right, Sam. We can't lose everyone."),
        ],
        CampaignCue::PlutoEvacuationComplete { shield_health, shield_max, .. }
            if shield_health == shield_max => &[
            ("Samuel", "Incredible work, commander! We didn't lose anyone in the evacuation."),
            ("Marco", "Impressive."),
        ],
        CampaignCue::PlutoEvacuationComplete { personnel_saved, .. }
            if personnel_saved > 0 => &[
            ("Samuel", "All personnel are ready to leave."),
            ("Oviya", "Go quickly! They're closing in on our escape route!"),
            ("Samuel", "Right!"),
            ("Marco", "Get moving, people!"),
        ],
        CampaignCue::PlutoEvacuationComplete { .. } => &[
            ("Samuel", "Comms with the surface went down, commander."),
            ("Marco", "They're a lost cause. Get out of here, now."),
        ],
        // Source Neptune1SeizeTheMeans: the first six lines are the
        // post-arrival briefing, followed by actual battle outcome lines.
        // Successful rescue then opens the authentic 31-line Factory story;
        // defeat bypasses the mining chapter entirely.
        CampaignCue::NeptuneSeizeBriefing => &[
            ("Samuel", "Commander, we’re getting an emergency signal from somewhere on Neptune."),
            ("???", "-day, Mayd- this is an emerg- enem- facility dest- need evac-"),
            ("Samuel", "It’s very faint, but it’s clear we need to go help."),
            ("Oviya", "It looks like the bees are gathered around ore-rich asteroids around the planet. Scouts are reporting the mining facility on the surface is flattened."),
            ("Joey", "There’s an underground bunker beneath the facility. I’m darn sure that’s where your signal’s coming from."),
            ("Marco", "Let’s clear out those bees so we can land safely."),
        ],
        CampaignCue::NeptuneSeizeVictory => &[
            ("Samuel", "Great work, commander. We can check the facility now."),
            ("Samuel", "Commander, we’ve rescued the mining personnel from their bunker. Their leader has come to speak with you."),
            ("Wesley", "I’m no leader. That would be my manager, Derek. I’m simply the regional head of the accounting department for Jensen Industries."),
            ("Joey", "Same as always."),
            ("Wesley", "Ah, greetings, Joey."),
            ("Joey", "Howdy. Where’s Derek, then?"),
            ("Wesley", "Derek is no longer with us. He didn’t make it to the bunker in time. I’ve been the de facto leader for the remaining Jensen personnel."),
            ("Samuel", "Then why did you say-"),
            ("Joey", "Don’t bother. Wesley, tell us what we need to know for this war against the Bees."),
            ("Wesley", "Bees? Is that what’s attacking us? I was not aware they had the biology to survive in space. They’re hardly surviving on Earth any longer, due to-"),
            ("Marco", "They’re not bees."),
            ("Joey", "They just look like ‘em. Easier to call ‘em that."),
            ("Wesley", "Ah. Right. Of course. Well, these… bees quickly disposed of our contracted defense team and started bombing the surface."),
            ("Wesley", "The emergency code was sent, and we all attempted to secure ourselves in the underground bunker. Not everyone made it."),
            ("Marco", "Great. So we have an accountant-"),
            ("Wesley", "Regional head of the accounting department."),
            ("Marco", "..."),
            ("Marco", "We have that and a bunch of miners, then? This isn’t really helping our chances."),
            ("Wesley", "Well, um, we do have a line of prototype Factory ships, capable of mining ore-rich asteroids, as well as personnel trained to man them."),
            ("Joey", "And plenty of those asteroids nearby. Well, I’ll be, Wesley, this really is helpful."),
            ("Wesley", "They are property of Jensen Corporation, and all personnel including myself are on their payroll. I’m sure we can reach some sort of agreement for their use, perhaps a loan."),
            ("Wesley", "But of course, these ships are the intellectual property of Jensen, so I can’t have any of you stealing these plans."),
            ("Samuel", "We don’t have any way to contact Jensen Corporation, Wesley."),
            ("Wesley", "Well, that is unfortunate. I do need approval from a home office manager before I-"),
            ("Yoshiko", "How many punches does it take to get him to shut up?"),
            ("Joey", "I dunno, you’ll have to just try until he quiets down."),
            ("Wesley", "N-nw, now, I’m sure w-we don’t have to resort to such… b-barbaric means! We’re all adults here!"),
            ("Yoshiko", "I'm thinking at least a dozen. Six to the nose and six to the stomach."),
            ("Wesley", "Ahh! Please don’t engage in a physical altercation, it’s far from necessary! Please, use the Factories however you please! We can negotiate fair compensation once we’ve made contact with my superiors."),
            ("Joey", "There ya go, commander. Factory ships at your disposal."),
            ("Samuel", "Thank you, Wesley."),
            ("Wesley", "Yes, of course, just leave my nose intact, please. It’s my best feature."),
        ],
        CampaignCue::NeptuneSeizeDefeat => &[
            ("Samuel", "We’ve lost all the squads we sent out, commander. There’s no safe way onto Neptune."),
            ("Marco", "There’s not much time to hang around here. The closest United Fleet base is on Uranus."),
            ("Marco", "We can find help there. Those people in the mining facility will just have to hang on a bit longer until others can come back."),
            ("Oviya", "Scouts are reporting a bee blockade between here and Uranus. We’ll have to break it."),
        ],
        CampaignCue::NeptunePressingForwardBriefing => &[
            ("Samuel", "It's time to break through this blockade, commander. Use everything at your disposal."),
        ],
        CampaignCue::NeptunePressingForwardVictory => &[
            ("Samuel", "Great work, commander! We're free to travel to Uranus."),
            ("Marco", "That was a tough fight, commander, you did well."),
            ("Yoshiko", "We showed those bee losers who the real fighters are!"),
            ("Oviya", "Don't get too cocky now, Yoshiko. Let's focus on what's next."),
        ],
        CampaignCue::NeptunePressingForwardDefeat { has_factory: false } => &[
            ("Samuel", "Commander, we've lost contact with all our forces. What do we do now? The bees from Pluto are catching up."),
            ("Oviya", "The fight diverted forces from elsewhere in the blockade. We can escape if we move quickly!"),
            ("Samuel", "Full speed ahead!"),
        ],
        CampaignCue::NeptunePressingForwardDefeat { has_factory: true } => &[
            ("Samuel", "Commander, we've lost contact with all our forces. What do we do now? The bees from Pluto are catching up."),
            ("Oviya", "The fight diverted forces from elsewhere in the blockade. We can escape if we move quickly!"),
            ("Marco", "Those Factories are slow and defenseless. Leave them behind."),
            ("Wesley", "Now hold on a second! Those ships have already proven lots of shareholder val-"),
            ("Yoshiko", "He's right, Wes, if they can't fight and they can't move they won't make it!"),
            ("Oviya", "Agreed. Now move!"),
            ("Samuel", "Full speed ahead!"),
        ],
    }
}

/// Filter the original Pluto III lieutenant introduction by the real
/// surviving player FleetShip classes after career restoration. Presentation
/// never copies or mutates the campaign mission owner.
pub(crate) fn displayed_lines(cue: CampaignCue) ->
    impl Iterator<Item = (&'static str, &'static str)>
{
    lines(cue).iter().copied().filter(move |(speaker, line)| match cue {
        CampaignCue::PlutoPushbackBriefing { speaker_mask } => {
            let bit = match *speaker {
                "Oviya" => 0,
                "Joey" => 1,
                "Marco" => 2,
                "Yoshiko" => 3,
                _ => return true,
            };
            (speaker_mask & (1_u8 << bit)) != 0
        }
        CampaignCue::PlutoEvacuationBriefing { speaker_mask } => {
            // Only original Pluto4BluerPastures lines 7..10 depend on
            // available classes. The other lines from these same officers
            // are unconditional; filtering an entire speaker would drop
            // the original encounter and safety briefing.
            let bit = match *speaker {
                "Yoshiko" if *line == "We’ve got this, commander! Dreadnoughts standing by." => 3,
                "Marco" if *line == "Gunships at the ready." => 2,
                "Joey" if *line == "Frigates ready to go!" => 1,
                "Oviya" if *line == "I’ve got the Scouts mapping an escape route." => 0,
                _ => return true,
            };
            (speaker_mask & (1_u8 << bit)) != 0
        }
        _ => true,
    })
}

pub(crate) fn line_count(cue: CampaignCue) -> usize {
    displayed_lines(cue).count()
}

/// DialogueManager.TypeLine reveals one visible character per 0.02
/// presentation seconds and alternates the source chat portrait every
/// six visible characters. This is UI-only; campaign cue progress,
/// simulation, and remote player decisions use their existing clocks.
pub(crate) fn revealed_dialogue_text(
    message: &str, elapsed_seconds: f32,
) -> (&str, bool) {
    let count = (elapsed_seconds.max(0.0) * 50.0)
        .floor().max(0.0) as usize;
    let byte_end = message.char_indices().nth(count)
        .map_or(message.len(), |(offset, _)| offset);
    let talking = byte_end < message.len() && (count / 6) % 2 == 1;
    (&message[..byte_end], talking)
}

pub(crate) fn visible_line(cue: CampaignCue, index: usize) ->
    Option<(&'static str, &'static str)>
{
    displayed_lines(cue).nth(index)
}

/// Source PlutoLines_Pushback uses a one-second Pause DialogueLine before
/// the closing decision, not a selectable blank spoken line. This is a
/// presentation clock only; it never advances the physical Level.
pub(crate) fn pause_seconds(cue: CampaignCue, visible_index: usize) -> Option<f32> {
    (matches!(cue,
        CampaignCue::PlutoPushbackVictory | CampaignCue::PlutoPushbackDefeat
    ) && visible_index == 7).then_some(1.0)
}

// Original LevelIntro travel dialogue, separate from in-battle cues.
// The authored Titania-to-Uranus middle exchange exists only if A.M.I.
// survived the committed Beenoculars outcome.
/// The source LevelIntro mission briefing, shown before fleet preparation.
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Deserialize)]
pub enum NativeCampaignPreparation {
    #[serde(rename = "pluto_reinforcements")]
    PlutoReinforcements,
    #[serde(rename = "pluto_pushback")]
    PlutoPushback,
    #[serde(rename = "pluto_bluer_pastures")]
    PlutoBluerPastures,
    #[serde(rename = "neptune_seize_means")]
    NeptuneSeizeMeans,
    #[serde(rename = "neptune_of_production")]
    NeptuneProduction,
    #[serde(rename = "neptune_pressing_forward")]
    NeptunePressingForward,
    #[serde(rename = "titania_minesweeper")]
    TitaniaMinesweeper,
    #[serde(rename = "titania_beenoculars")]
    TitaniaBeenoculars,
    #[serde(rename = "uranus_on_the_offensive")]
    UranusOffensive,
    #[serde(rename = "uranus_on_the_defensive")]
    UranusDefensive,
    #[serde(rename = "uranus_new_threat")]
    UranusNewThreat,
}

impl NativeCampaignPreparation {
    fn title(self) -> &'static str {
        match self {
            Self::PlutoReinforcements => "Reinforcements",
            Self::PlutoPushback => "Pushback",
            Self::PlutoBluerPastures => "Bluer Pastures",
            Self::NeptuneSeizeMeans => "Seize the Means",
            Self::NeptuneProduction => "Of Production",
            Self::NeptunePressingForward => "Pressing Forward",
            Self::TitaniaMinesweeper => "Minesweeper",
            Self::TitaniaBeenoculars => "Beenoculars",
            Self::UranusOffensive => "On the Offensive",
            Self::UranusDefensive => "On the Defensive",
            Self::UranusNewThreat => "A New Threat",
        }
    }

    fn lines(self) -> &'static [(&'static str, &'static str)] {
        match self {
            Self::PlutoReinforcements => &[
                ("Samuel", "We've been caught off guard by the strange alien fleet cutting our communications. Only our patrol ships are ready for combat for the moment, but we have sent orders to the rest."),
                ("Samuel", "Commander, you need to engage in this fight with the patrol ships and buy time for the rest of the fleet to mobilize."),
            ],
            Self::PlutoPushback => &[
                ("Samuel", "With the full force of Pluto's fleet and the cover of some floating space junk coming our way, we're going to try and push back the assault from these aliens."),
            ],
            Self::PlutoBluerPastures => &[
                ("Samuel", "Scouts are reporting overwhelming reinforcements from the enemy. We can't defeat them, but we have to try and buy enough time for those on the planet to evacuate."),
            ],
            Self::NeptuneSeizeMeans => &[
                ("Samuel", "We're approaching Neptune to see if we can gather any allies there."),
                ("Samuel", "The scout team has reported movement, but were unable to determine if it's friendly or not. We'll just have to find out."),
            ],
            Self::NeptuneProduction => &[
                ("Samuel", "Scouts are reporting the bee forces on all sides. Their fleet from Pluto is catching up, and they've already established a blockade between here and Uranus."),
                ("Samuel", "Now that we have Factories, we can gather resources from the same asteroids the bees were defending. That'll give us a fighting chance against the blockade, but we need to be quick."),
            ],
            Self::NeptunePressingForward => &[
                ("Samuel", "It's time to break through this blockade, commander. Use everything at your disposal."),
            ],
            Self::TitaniaMinesweeper => &[
                ("Samuel", "We need to clear out the debris surrounding Titania to give our new friends an exit path. Bee patrols have also been spotted around the area, trying to make sure nothing gets in or out."),
            ],
            Self::TitaniaBeenoculars => &[
                ("Samuel", "Commander, we have to defend Titania's base long enough for all of their personnel to escape the surface."),
            ],
            Self::UranusOffensive => &[
                ("Samuel", "There is still fighting around Uranus, another resource-rich area. If we take the bees down, we can bolster our own fleet with resources and personnel."),
                ("Samuel", "Radars are jammed in the area, so we'll need to rely on our Scouts for vision."),
            ],
            Self::UranusDefensive => &[
                ("Samuel", "Our success has bought us some time to collect resources around Uranus. There will likely be reinforcements called on from the bees we just fought, though. Take care."),
            ],
            Self::UranusNewThreat => &[
                ("Samuel", "We need to check out a grouping of bees further out from Uranus. They're moving... very slowly."),
            ],
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeCampaignIntermission {
    PlutoToNeptune,
    NeptuneToTitania,
    TitaniaToUranus { ami_survived: bool },
    Preparation(NativeCampaignPreparation),
}

const PLUTO_TO_NEPTUNE: &[(&str, &str)] = &[
    ("Samuel", "What do we do now? Those... bees outnumber us."),
    ("Marco", "Heavily."),
    ("Yoshiko", "That doesn't mean we lose!"),
    ("Joey", "But it's a heck of a lot harder."),
    ("Marco", "And I'm not going into a fight like that unless I absolutely have to."),
    ("Oviya", "What we need is allies."),
    ("Samuel", "Just because interplanetary comms are cut off doesn't mean others aren't still out there... right?"),
    ("Yoshiko", "They couldn't have destroyed all the United Fleet. It's the finest military presence in the solar system!"),
    ("Marco", "The only military presence in the solar system. Until now. And we don't know how many more bees there are."),
    ("Samuel", "There's no way they could take Mars... or even Earth."),
    ("Marco", "No way to know."),
    ("Oviya", "The next closest human settlement is Neptune. Commander, I recommend we go there."),
    ("Samuel", "There might be survivors!"),
    ("Joey", "It's just a Jensen mining facility, so we won't find any more United Fleet help. But we can help anyone that's sticking it out."),
    ("Samuel", "And they could have a way to make more ships for our fleet!"),
    ("Joey", "If there's still anything left there."),
    ("Oviya", "It's our best option right now. If Neptune is wiped out, then we move on."),
    ("Joey", "And if it's not, we have to deal with Wesley..."),
];

const NEPTUNE_TO_TITANIA: &[(&str, &str)] = &[
    ("Samuel", "Commander, I've been reviewing the colonies and bases throughout the solar system. While there is a small UF outpost on Uranus proper, there's also a research and engineering base on one of its moons, Titania. We should be able to find allies here."),
    ("Marco", "If there's anyone left."),
    ("Samuel", "If- yeah, if... I'm just trying to think through our options."),
    ("Oviya", "Titania is en route to Uranus. I'll have some scouts look ahead and see if there's anything worth stopping for."),
    ("Yoshiko", "You think the nerds on Titania might have cool new weapons we can use?"),
    ("Samuel", "Nerds?"),
    ("Yoshiko", "Yeah, you said it's a research and... whatever base. I love those nerds."),
    ("Joey", "Ha! We could probably use some more nerds on our side. Even one other person who understands how explosives work."),
    ("Yoshiko", "Just cuz I blew up one frigate-"),
    ("Marco", "Can we focus, please?"),
    ("Oviya", "Scouts are reporting Titania base looks evacuated. No damage, but no signals either. And lots of bee patrols."),
    ("Samuel", "Wait a minute, we're getting a request right now! Let me put them on..."),
    ("Alejandra", "Hello? Do you read me?"),
    ("Samuel", "Loud and clear, ma'am."),
    ("Alejandra", "Ah, good. This is Alejandra Vasquez, research and engineering wing, regional commander and research lead for United Fleet Titania."),
    ("Emilia", "Ohhhh, who are they?"),
    ("Alejandra", "Emilia, please. We saw other United Fleet vessels around our base. I presume those belong to your contingent?"),
    ("Oviya", "Yes, that was my scout team. We thought your base was abandoned, though."),
    ("Emilia", "We need help!"),
    ("Alejandra", "I was getting there, Emilia."),
    ("Emilia", "We can't get off the base right now!"),
    ("Alejandra", "That is correct. The initial assault of these unidentified aliens targeted the surface of Uranus, primarily. They were swiftly dismantled. We sent a signal on the emergency frequency, as is protocol, but they detected us. Their attack was more than we could handle, and we had no response from the fleet. We're only alive because of Emilia's father."),
    ("Samuel", "What did he do?"),
    ("Alejandra", "I... don't wish to discuss it in current company."),
    ("Emilia", "He blew up the weapons bay to surround the base in junk and experimental explosives."),
    ("Samuel", "What's so bad about saying that?"),
    ("Alejandra", "Philip is... No longer with us."),
    ("Samuel", "Oh- oh, I'm so sorry...."),
    ("Emilia", "It's fine... he's... he saved us."),
    ("Alejandra", "Precisely. His selfless act is the only reason we're alive. If it weren't for that, the bee patrols around us would have decimated us days ago. But ironically, it is now the reason we are stuck."),
    ("Emilia", "You can help us! You have fighting ships! And we have a carrier to help too!"),
    ("Yoshiko", "Oh, what's a carrier?"),
    ("Emilia", "It's this awesome ship my dad designed. It has a bunch of little robot controlled ships. We call 'em drones and strikers, and they can-"),
    ("Alejandra", "Please, Emilia, I'd love to discuss the details of Philip's designs, but we don't have the time right now. To abbreviate this conversation, would your fleet be able to assist in our attempt to extricate our staff and their families from this moon? The prototypal carrier is our only military vessel at the moment."),
    ("Emilia", "Right! We're running out of food and batteries!"),
    ("Samuel", "Of course we can help. We're already pretty experienced bee fighters."),
    ("Alejandra", "Bees?"),
    ("Samuel", "Erm... yes, that's what we're calling the aliens."),
    ("Alejandra", "Yes, I do suppose they bear some resemblance to the endangered species. Fascinating observation."),
    ("Emilia", "I like that name! Stupid bees! Yeah, it feels good to say."),
    ("Marco", "Quit wasting time."),
    ("Samuel", "Right. Commander, what are your orders?"),
];

const TITANIA_TO_URANUS_START: &[(&str, &str)] = &[
    ("Alejandra", "Commander, my thanks again for your assistance in our predicament. I hope we can prove our value to your fleet shortly."),
    ("Emilia", "We've got a carrier!"),
];

const TITANIA_TO_URANUS_AMI: &[(&str, &str)] = &[
    ("AMI", "And you will have whatever assistance I can offer in your upcoming battles."),
    ("Yoshiko", "Oh, hey red lady. What, uh.. Exactly is your assistance?"),
    ("AMI", "I can make improvements to your codex with strategy suggestions based on our observations and battles with the various bee vessels. Additionally, my predictive routines should assist in knowing what enemy vessels to expect in your battles as you are preparing."),
    ("Yoshiko", "That's... insanely cool!"),
    ("AMI", "Why thank you. If I could blush, I assure you I would be."),
    ("Emilia", "My dad and Alejandra both worked a lot on her. I think she's awesome!"),
    ("Joey", "She's impressive. Looks like we've got ourselves a mighty fine pair of Bee-noculars."),
    ("AMI", "Ha! That is quite clever."),
    ("Emilia", "Wow, Marco, I didn't know eyes could roll that far."),
    ("Marco", "I've got lots of practice..."),
];

const TITANIA_TO_URANUS_END: &[(&str, &str)] = &[
    ("Alejandra", "Furthermore, if you have the means of production, we also have the blueprints for the carrier. Producing more should help alleviate the pressures on your manpower."),
    ("Emilia", "Yeah, it's all robots driving the little ships!"),
    ("Alejandra", "And perhaps I can help some with organizing your efforts, as well. I've noticed a distinct lack of organization amongst you. For example, your command ship isn't even in proper formation with the rest-"),
    ("Marco", "That's not what we need help with. Maybe use your smarts for some battle tactics and weapons."),
    ("Emilia", "Yeah! Let's take those stupid bees down! Uranus is under new management! Er- well, I guess old management. We're taking the new management down. Yeah!"),
    ("Samuel", "The first way sounds cooler, though."),
    ("Emilia", "Yeah!"),
    ("Marco", "Are we sure this is an improvement?"),
    ("Oviya", "Commander, we need to keep moving. My scouts are reporting there may be some United Fleet stragglers around Uranus that need our help."),
    ("Samuel", "Let's go, then!"),
];

impl NativeCampaignIntermission {
    pub fn title(self) -> &'static str {
        match self {
            Self::PlutoToNeptune => "Pluto to Neptune",
            Self::NeptuneToTitania => "Neptune to Titania",
            Self::TitaniaToUranus { .. } => "Titania to Uranus",
            Self::Preparation(preparation) => preparation.title(),
        }
    }

    pub(crate) fn lines(self) -> impl Iterator<Item = (&'static str, &'static str)> {
        let (first, middle, last) = match self {
            Self::PlutoToNeptune => (PLUTO_TO_NEPTUNE, &[][..], &[][..]),
            Self::NeptuneToTitania => (NEPTUNE_TO_TITANIA, &[][..], &[][..]),
            Self::Preparation(preparation) => (preparation.lines(), &[][..], &[][..]),
            Self::TitaniaToUranus { ami_survived } => (
                TITANIA_TO_URANUS_START,
                if ami_survived { TITANIA_TO_URANUS_AMI } else { &[][..] },
                TITANIA_TO_URANUS_END,
            ),
        };
        first.iter().chain(middle).chain(last).copied()
    }
}
