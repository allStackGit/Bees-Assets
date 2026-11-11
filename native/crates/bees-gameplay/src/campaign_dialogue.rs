//! Titania I dialogue cues driven by the authoritative Level clock and roster.

use bees_sim::{combat_world::{CombatEvent, CombatWorld}, map_object::MapObjectKind};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CampaignCue {
    MinesweeperBriefing,
    FireTankSpotted { object_id: i64 },
    FireTankDestroyed { object_id: i64 },
    MinesweeperVictory,
    MinesweeperDefeat,
}

pub struct MinesweeperDialogue {
    pub(crate) player_side: i32,
    tracked_tank: Option<i64>,
    cue: CampaignCue,
    sequence: u8,
}

impl MinesweeperDialogue {
    pub fn new(player_side: i32) -> Self {
        Self { player_side, tracked_tank: None, cue: CampaignCue::MinesweeperBriefing, sequence: 0 }
    }

    pub fn current(&self) -> (u8, CampaignCue) { (self.sequence, self.cue) }

    pub(crate) fn set_cue(&mut self, cue: CampaignCue) {
        self.sequence += 1;
        self.cue = cue;
    }

    pub(crate) fn observe_combat(&mut self, world: &CombatWorld) -> Option<CampaignCue> {
        if let Some(object_id) = self.tracked_tank {
            return (matches!(self.cue, CampaignCue::FireTankSpotted { .. }) &&
                world.events().iter().any(|event| matches!(event,
                    CombatEvent::MapObjectDestroyed { object_id: id, .. } if *id == object_id)))
                .then_some(CampaignCue::FireTankDestroyed { object_id });
        }
        // Unity's visible set has no ordering contract. Resolve simultaneous
        // discovery by source object ID so playback is deterministic.
        let object_id = world.range_visible_map_object_ids().iter().copied().filter(|&id|
            world.map_object(id).is_some_and(|object| object.kind == MapObjectKind::FireTank)).min()?;
        self.tracked_tank = Some(object_id);
        Some(CampaignCue::FireTankSpotted { object_id })
    }
}
