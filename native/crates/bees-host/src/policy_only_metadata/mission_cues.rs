//! Source-authored campaign signals shared by scripted orders and
//! reinforcement admission. Matching consumes only canonical Level events.

use bees_gameplay::campaign_dialogue::{
    CampaignCue, CampaignDialogueSpec, UploadMilestone,
};
use serde::{Deserialize, Serialize};

/// The actual kind of source campaign event. Fire Tank identity and
/// upload progress are checked by the canonical event emitter; mission
/// queues subscribe to a kind, not a copied object-ID or timer.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum NativeMissionCue {
    FireTankSpotted,
    FireTankDestroyed,
    MinesweeperVictory,
    MinesweeperDefeat,
    UploadTen,
    UploadTwentyFour,
    UploadFifty,
    UploadSeventyFive,
    UploadNinety,
    BeenocularsVictory,
    BeenocularsDefeat,
}

impl NativeMissionCue {
    pub(crate) fn matches(self, cue: CampaignCue) -> bool {
        matches!((self, cue),
            (Self::FireTankSpotted, CampaignCue::FireTankSpotted { .. }) |
            (Self::FireTankDestroyed, CampaignCue::FireTankDestroyed { .. }) |
            (Self::MinesweeperVictory, CampaignCue::MinesweeperVictory) |
            (Self::MinesweeperDefeat, CampaignCue::MinesweeperDefeat) |
            (Self::UploadTen, CampaignCue::BeenocularsUpload(UploadMilestone::Ten)) |
            (Self::UploadTwentyFour, CampaignCue::BeenocularsUpload(UploadMilestone::TwentyFour)) |
            (Self::UploadFifty, CampaignCue::BeenocularsUpload(UploadMilestone::Fifty)) |
            (Self::UploadSeventyFive, CampaignCue::BeenocularsUpload(UploadMilestone::SeventyFive)) |
            (Self::UploadNinety, CampaignCue::BeenocularsUpload(UploadMilestone::Ninety)) |
            (Self::BeenocularsVictory, CampaignCue::BeenocularsVictory) |
            (Self::BeenocularsDefeat, CampaignCue::BeenocularsDefeat)
        )
    }

    pub(crate) fn valid_for(self, spec: Option<CampaignDialogueSpec>) -> bool {
        match spec {
            Some(CampaignDialogueSpec::TitaniaMinesweeper { .. }) =>
                matches!(self, Self::FireTankSpotted | Self::FireTankDestroyed |
                    Self::MinesweeperVictory | Self::MinesweeperDefeat),
            Some(CampaignDialogueSpec::TitaniaBeenoculars { .. }) =>
                matches!(self, Self::UploadTen | Self::UploadTwentyFour |
                    Self::UploadFifty | Self::UploadSeventyFive | Self::UploadNinety |
                    Self::BeenocularsVictory | Self::BeenocularsDefeat),
            None => false,
        }
    }
}

