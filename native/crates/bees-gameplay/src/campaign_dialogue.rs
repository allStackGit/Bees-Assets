//! Authored campaign dialogue driven by the shared Level state.

use bees_sim::{
    combat_world::CombatWorld,
    episode_terminal::SurvivalStatus,
    map_object::MapObjectKind,
    geometry::Vec2,
};
use serde::{Deserialize, Serialize};

/// Titania II's authored defended base, player deployment focus, and camera.
pub const TITANIA_II_CENTER: Vec2 = Vec2::new(-32.0, 55.0);

#[derive(Clone, Copy, Debug, Deserialize, Serialize)]
#[serde(rename_all = "snake_case", deny_unknown_fields)]
pub enum CampaignDialogueSpec {
    TitaniaMinesweeper { player_side: i32 },
    TitaniaBeenoculars {
        player_side: i32,
        #[serde(default)]
        under_heavy_fire: bool,
    },
}

impl CampaignDialogueSpec {
    pub fn player_side(self) -> i32 {
        match self {
            Self::TitaniaMinesweeper { player_side } |
            Self::TitaniaBeenoculars { player_side, .. } => player_side,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum UploadMilestone {
    Ten,
    TwentyFour,
    Fifty,
    SeventyFive,
    Ninety,
}

impl UploadMilestone {
    const ORDERED: [Self; 5] = [Self::Ten, Self::TwentyFour, Self::Fifty, Self::SeventyFive, Self::Ninety];

    pub fn percent(self) -> u8 {
        match self {
            Self::Ten => 10,
            Self::TwentyFour => 24,
            Self::Fifty => 50,
            Self::SeventyFive => 75,
            Self::Ninety => 90,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CampaignCue {
    MinesweeperBriefing,
    FireTankSpotted { object_id: i64 },
    FireTankDestroyed { object_id: i64 },
    MinesweeperVictory,
    MinesweeperDefeat,
    BeenocularsBriefing { under_heavy_fire: bool },
    BeenocularsUpload(UploadMilestone),
    BeenocularsVictory,
    BeenocularsDefeat,
}

impl CampaignCue {
    pub fn is_briefing(self) -> bool {
        matches!(self, Self::MinesweeperBriefing | Self::BeenocularsBriefing { .. })
    }
}

enum DialogueMode {
    Minesweeper { tracked_tank: Option<i64> },
    Beenoculars { next_milestone: usize },
}

pub struct CampaignDialogue {
    player_side: i32,
    mode: DialogueMode,
    cue: CampaignCue,
    sequence: u8,
}

impl CampaignDialogue {
    pub fn new(spec: CampaignDialogueSpec) -> Self {
        let (mode, cue) = match spec {
            CampaignDialogueSpec::TitaniaMinesweeper { .. } => (
                DialogueMode::Minesweeper { tracked_tank: None },
                CampaignCue::MinesweeperBriefing,
            ),
            CampaignDialogueSpec::TitaniaBeenoculars { under_heavy_fire, .. } => (
                DialogueMode::Beenoculars { next_milestone: 0 },
                CampaignCue::BeenocularsBriefing { under_heavy_fire },
            ),
        };
        Self { player_side: spec.player_side(), mode, cue, sequence: 0 }
    }

    pub fn current(&self) -> (u8, CampaignCue) { (self.sequence, self.cue) }

    pub(crate) fn set_cue(&mut self, cue: CampaignCue) {
        self.sequence += 1;
        self.cue = cue;
    }

    pub(crate) fn outcome(&self, winner_side: i32) -> CampaignCue {
        match (&self.mode, winner_side == self.player_side) {
            (DialogueMode::Minesweeper { .. }, true) => CampaignCue::MinesweeperVictory,
            (DialogueMode::Minesweeper { .. }, false) => CampaignCue::MinesweeperDefeat,
            (DialogueMode::Beenoculars { .. }, true) => CampaignCue::BeenocularsVictory,
            (DialogueMode::Beenoculars { .. }, false) => CampaignCue::BeenocularsDefeat,
        }
    }

    pub(crate) fn take_upload_cue(&mut self, survival: SurvivalStatus) -> Option<CampaignCue> {
        let DialogueMode::Beenoculars { next_milestone } = &mut self.mode else { return None };
        let milestone = *UploadMilestone::ORDERED.get(*next_milestone)?;
        // Unity samples upload progress on each whole second of the defense.
        let due_seconds = (f64::from(survival.duration_seconds) * f64::from(milestone.percent()) / 100.0).ceil();
        if f64::from(survival.elapsed_seconds) < due_seconds {
            return None;
        }
        *next_milestone += 1;
        Some(CampaignCue::BeenocularsUpload(milestone))
    }

    pub(crate) fn observe_combat(&mut self, world: &CombatWorld) -> Option<CampaignCue> {
        let DialogueMode::Minesweeper { tracked_tank } = &mut self.mode else { return None };
        if let Some(object_id) = *tracked_tank {
            // Titania I tests the saved MapObject's persistent IsDead state,
            // not whether its destruction event happened on this exact tick.
            // The shared world retires the real object on death and never
            // reuses its entity ID; this also covers owner-driven teardown.
            return (matches!(self.cue, CampaignCue::FireTankSpotted { .. }) &&
                world.map_object(object_id).is_none())
                .then_some(CampaignCue::FireTankDestroyed { object_id });
        }
        // Resolve simultaneous discovery by source object ID for deterministic playback.
        let object_id = world.range_visible_map_object_ids().iter().copied().filter(|&id|
            world.map_object(id).is_some_and(|object| object.kind == MapObjectKind::FireTank)).min()?;
        *tracked_tank = Some(object_id);
        Some(CampaignCue::FireTankSpotted { object_id })
    }
}
