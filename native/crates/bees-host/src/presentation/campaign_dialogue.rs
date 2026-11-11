//! Authored Titania dialogue from CutsceneManager.cs and CampaignDialogueOverrides.cs.

use bees_gameplay::campaign_dialogue::{CampaignCue, UploadMilestone};

pub(crate) fn lines(cue: CampaignCue) -> &'static [(&'static str, &'static str)] {
    match cue {
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
    }
}
