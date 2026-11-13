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
    UploadTen,
    UploadTwentyFour,
    UploadFifty,
    UploadSeventyFive,
    UploadNinety,
}

impl NativeMissionCue {
    pub(crate) fn matches(self, cue: CampaignCue) -> bool {
        matches!((self, cue),
            (Self::FireTankSpotted, CampaignCue::FireTankSpotted { .. }) |
            (Self::FireTankDestroyed, CampaignCue::FireTankDestroyed { .. }) |
            (Self::UploadTen, CampaignCue::BeenocularsUpload(UploadMilestone::Ten)) |
            (Self::UploadTwentyFour, CampaignCue::BeenocularsUpload(UploadMilestone::TwentyFour)) |
            (Self::UploadFifty, CampaignCue::BeenocularsUpload(UploadMilestone::Fifty)) |
            (Self::UploadSeventyFive, CampaignCue::BeenocularsUpload(UploadMilestone::SeventyFive)) |
            (Self::UploadNinety, CampaignCue::BeenocularsUpload(UploadMilestone::Ninety))
        )
    }

    pub(crate) fn valid_for(self, spec: Option<CampaignDialogueSpec>) -> bool {
        match spec {
            Some(CampaignDialogueSpec::TitaniaMinesweeper { .. }) =>
                matches!(self, Self::FireTankSpotted | Self::FireTankDestroyed),
            Some(CampaignDialogueSpec::TitaniaBeenoculars { .. }) =>
                matches!(self, Self::UploadTen | Self::UploadTwentyFour |
                    Self::UploadFifty | Self::UploadSeventyFive | Self::UploadNinety),
            None => false,
        }
    }
}

/// Every mission event is evaluated against the canonical fixed-tick
/// simulation and its original Level notification stream.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum NativeMissionEvent {
    CampaignCue(NativeMissionCue),
    ShipDestroyed(i64),
}

impl NativeMissionEvent {
    pub(crate) fn valid_for(
        self, campaign: Option<CampaignDialogueSpec>,
        known_ship: impl FnOnce(i64) -> bool,
    ) -> bool {
        match self {
            Self::CampaignCue(cue) => cue.valid_for(campaign),
            Self::ShipDestroyed(id) => id > 0 && known_ship(id),
        }
    }

    pub(crate) fn fired(
        self, tick: u64, world: &bees_sim::combat_world::CombatWorld,
        events: &bees_gameplay::level_events::NativeLevelEventProcessor,
    ) -> bool {
        match self {
            Self::CampaignCue(cue) =>
                events.campaign_cue_fired(tick, |current| cue.matches(current)),
            Self::ShipDestroyed(id) => world.events().iter().any(|event|
                matches!(event,
                    bees_sim::combat_world::CombatEvent::ShipDied {
                        victim_id, ..
                    } if *victim_id == id
                )
            ),
        }
    }

}
