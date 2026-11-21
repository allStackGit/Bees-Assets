//! Authored Titania dialogue from CutsceneManager.cs and CampaignDialogueOverrides.cs.

use bees_gameplay::campaign_dialogue::{CampaignCue, UploadMilestone};

pub(crate) fn lines(cue: CampaignCue) -> &'static [(&'static str, &'static str)] {
    match cue {
        // Original Neptune_OfProduction sections (source dialogue IDs 2-17).
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
        // Exact original PlutoLines_Pushback sections: intro 1..13,
        // victory 13..22, defeat 22..31. Source one-second pauses
        // remain empty, non-speaking dialogue lines.
        CampaignCue::PlutoPushbackBriefing { .. } => &[
            ("Samuel", "Alright, commander, we have all of our Lieutenants online. They’ll be giving orders for each of your vessels as you direct the entire fleet."),
            ("Oviya", " I'm Oviya, your scout lieutenant. Use the scout to… well, scout the battlefield. They get around fast, so as long as you keep giving orders they probably won't get hit by enemy fire."),
            ("Oviya", "They don’t have any guns, though, so please don’t leave them out to dry when they can’t fight back."),
            ("Joey", "Alrighty, Commander, I'm commanding yer Frigates. They're yer explosives experts. They can't shoot far, but they sure pack a wallop."),
            ("Joey", "Those rockets will do some serious damage, and they can even hit multiple targets inside the blast radius."),
            ("Joey", "Use ‘em against those ships that like to group up like moths to a flame and you’ll find them quite effective."),
            ("Marco", "I'll be commanding your Gunships. They're fast-flying dogfighting specialists. Use their speed to your advantage if you can."),
            ("Marco", "Even if they can't fly as well as me, they'll still be good at dodging fire. As long as you’re competent."),
            ("Yoshiko", "And I’m Yoshiko, your Dreadnought lieutenant! These babies are made to brawl. They can take a lotta hits and dish it right back! Keep ‘em out front and watch ‘em tear it up!"),
            ("Samuel", "And that’s everyone. Use all of our fleet’s strengths to win this battle, commander."),
            ("Samuel", " If you ever need a reminder on what any of our ships do or what we’ve discovered about the enemy’s fleet, you can always pause and view the United Fleet Codex."),
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
        CampaignCue::PlutoEvacuationBriefing => &[
            ("Samuel", "Scouts are reporting overwhelming reinforcements from the enemy. We can't outlast them, but we have to buy enough time for those on the planet to evacuate."),
            ("Samuel", "This is a full emergency order, so personnel have to be on space-ready vessels within 5 minutes."),
            ("Marco", "Destroy enemy ships where we can safely. That's the only way we'll get through this."),
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
    lines(cue).iter().copied().filter(move |(speaker, _)| match cue {
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
        _ => true,
    })
}

pub(crate) fn line_count(cue: CampaignCue) -> usize {
    displayed_lines(cue).count()
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
