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
    PlutoHoneybeeSpotted,
    PlutoScoutReportComplete,
    PlutoGunshipBriefingComplete,
    PlutoHoneybeeDestroyed,
    PlutoGunshipArrived,
    PlutoReinforcementsScoutSelected,
    PlutoReinforcementsCombatReady,
    /// The actual wave arrival, not a guessed elapsed-time trigger.
    /// Its selected narrative line remains unrelated to order scheduling.
    NeptuneProductionWave { number: u32 },
    UranusDefensiveWave { number: u8 },
    UranusBumblebeeSpotted,
    UranusBumblebeeHit,
    UranusBumblebeeDestroyed,
    FireTankSpotted,
    /// A particular real discovered Fire Tank, not any member of a field.
    /// Scripts may begin point-fire only after this exact contact.
    FireTankSpottedObject { object_id: i64 },
    FireTankDestroyed,
    FireTankDestroyedObject { object_id: i64 },
    UranusBargeLost,
    UranusBargeCharged,
    UranusReinforcements,
    UploadTen,
    UploadTwentyFour,
    UploadFifty,
    UploadSeventyFive,
    UploadNinety,
}

impl NativeMissionCue {
    pub(crate) fn matches(self, cue: CampaignCue) -> bool {
        match (self, cue) {
            (Self::NeptuneProductionWave { number: expected },
                CampaignCue::NeptuneProductionWave { number, .. }) => expected == number,
            (Self::UranusDefensiveWave { number: expected },
                CampaignCue::UranusDefensiveWave { number, .. }) => expected == number,
            (Self::FireTankSpottedObject { object_id: expected },
                CampaignCue::FireTankSpotted { object_id }) => expected == object_id,
            (Self::FireTankDestroyedObject { object_id: expected },
                CampaignCue::FireTankDestroyed { object_id }) => expected == object_id,
            pair => matches!(pair,
            (Self::PlutoHoneybeeSpotted, CampaignCue::PlutoAnomalyHoneybeeSpotted) |
            (Self::PlutoScoutReportComplete, CampaignCue::PlutoAnomalyScoutReportComplete) |
            (Self::PlutoGunshipBriefingComplete, CampaignCue::PlutoAnomalyGunshipBriefingComplete) |
            (Self::PlutoHoneybeeDestroyed, CampaignCue::PlutoAnomalyHoneybeeDestroyed) |
            (Self::PlutoGunshipArrived, CampaignCue::PlutoAnomalyGunshipArrived) |
            (Self::PlutoReinforcementsScoutSelected, CampaignCue::PlutoReinforcementsScoutSelected) |
            (Self::PlutoReinforcementsCombatReady, CampaignCue::PlutoReinforcementsCombatReady) |
            (Self::UranusBumblebeeSpotted, CampaignCue::UranusOffensiveBumblebeeSpotted) |
            (Self::UranusBumblebeeHit, CampaignCue::UranusOffensiveBumblebeeHit) |
            (Self::UranusBumblebeeDestroyed, CampaignCue::UranusOffensiveBumblebeeDestroyed) |
            (Self::FireTankSpotted, CampaignCue::FireTankSpotted { .. }) |
            (Self::FireTankDestroyed, CampaignCue::FireTankDestroyed { .. }) |
            (Self::UranusBargeLost, CampaignCue::UranusNewThreatBargeLost) |
            (Self::UranusBargeCharged, CampaignCue::UranusNewThreatBargeCharged) |
            (Self::UranusReinforcements, CampaignCue::UranusNewThreatReinforcements) |
            (Self::UploadTen, CampaignCue::BeenocularsUpload(UploadMilestone::Ten)) |
            (Self::UploadTwentyFour, CampaignCue::BeenocularsUpload(UploadMilestone::TwentyFour)) |
            (Self::UploadFifty, CampaignCue::BeenocularsUpload(UploadMilestone::Fifty)) |
            (Self::UploadSeventyFive, CampaignCue::BeenocularsUpload(UploadMilestone::SeventyFive)) |
            (Self::UploadNinety, CampaignCue::BeenocularsUpload(UploadMilestone::Ninety))
            ),
        }
    }

    pub(crate) fn valid_for(self, spec: Option<CampaignDialogueSpec>) -> bool {
        match spec {
            Some(CampaignDialogueSpec::PlutoAnomaly { .. }) =>
                matches!(self, Self::PlutoHoneybeeSpotted |
                    Self::PlutoScoutReportComplete | Self::PlutoGunshipArrived |
                    Self::PlutoGunshipBriefingComplete | Self::PlutoHoneybeeDestroyed),
            Some(CampaignDialogueSpec::PlutoReinforcements { .. }) =>
                matches!(self, Self::PlutoReinforcementsScoutSelected |
                    Self::PlutoReinforcementsCombatReady),
            Some(CampaignDialogueSpec::NeptuneProduction { .. }) =>
                matches!(self, Self::NeptuneProductionWave { number } if number > 0),
            Some(CampaignDialogueSpec::UranusDefensive { .. }) =>
                matches!(self, Self::UranusDefensiveWave { number } if (1..=6).contains(&number)),
            Some(CampaignDialogueSpec::UranusOffensive { .. }) =>
                matches!(self, Self::UranusBumblebeeSpotted |
                    Self::UranusBumblebeeHit | Self::UranusBumblebeeDestroyed),
            Some(CampaignDialogueSpec::TitaniaMinesweeper { .. }) =>
                matches!(self, Self::FireTankSpotted | Self::FireTankDestroyed) ||
                matches!(self,
                    Self::FireTankSpottedObject { object_id } |
                    Self::FireTankDestroyedObject { object_id } if object_id > 0),
            Some(CampaignDialogueSpec::TitaniaBeenoculars { .. }) =>
                matches!(self, Self::UploadTen | Self::UploadTwentyFour |
                    Self::UploadFifty | Self::UploadSeventyFive | Self::UploadNinety),
            Some(CampaignDialogueSpec::UranusNewThreat { .. }) =>
                matches!(self, Self::UranusBargeLost | Self::UranusBargeCharged |
                    Self::UranusReinforcements),
            Some(CampaignDialogueSpec::PlutoPushback { .. }) |
            Some(CampaignDialogueSpec::NeptuneSeizeMeans { .. }) |
            Some(CampaignDialogueSpec::NeptunePressingForward { .. }) |
            Some(CampaignDialogueSpec::PlutoEvacuation { .. }) | None => false,
        }
    }
}

/// Every mission event is evaluated against the canonical fixed-tick
/// simulation and its original Level notification stream.
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub(crate) enum NativeMissionEvent {
    CampaignCue(NativeMissionCue),
    ShipDestroyed(i64),
    /// Only the canonical physical MapObject death event can release this.
    /// Stable Level entity IDs never alias a ship or a replacement collider.
    MapObjectDestroyed(i64),
    /// Real MiningAsteroid.Kill(false), never an inferred ore counter.
    MiningAsteroidDepleted(i64),
}

impl NativeMissionEvent {
    pub(crate) fn valid_for(
        self, campaign: Option<CampaignDialogueSpec>,
        known_ship: impl FnOnce(i64) -> bool,
        known_map_object: impl FnOnce(i64) -> bool,
        known_mining_asteroid: impl FnOnce(i64) -> bool,
    ) -> bool {
        match self {
            Self::CampaignCue(cue) => cue.valid_for(campaign),
            Self::ShipDestroyed(id) => id > 0 && known_ship(id),
            Self::MapObjectDestroyed(id) => id > 0 && known_map_object(id),
            Self::MiningAsteroidDepleted(id) =>
                id > 0 && known_mining_asteroid(id),
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
                        victim_id, end_kill: false, ..
                    } if *victim_id == id
                )
            ),
            Self::MapObjectDestroyed(id) => world.events().iter().any(|event|
                matches!(event,
                    bees_sim::combat_world::CombatEvent::MapObjectDestroyed { object_id, .. }
                        if *object_id == id
                )
            ),
            Self::MiningAsteroidDepleted(id) => world.events().iter().any(|event|
                matches!(event,
                    bees_sim::combat_world::CombatEvent::MiningAsteroidDepleted { obstacle_id }
                        if *obstacle_id == id
                )
            ),
        }
    }

}
