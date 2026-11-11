//! Dialogue ported from the Titania_Minesweeper sections in CutsceneManager.cs.

use bees_gameplay::campaign_dialogue::CampaignCue;

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
    }
}
