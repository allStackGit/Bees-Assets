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
    /// Source Neptune 3 ending branches on persistent Factory ownership,
    /// not the presence of a surviving Factory in the deployed roster.
    NeptunePressingForward { player_side: i32, has_factory: bool },
    PlutoEvacuation { player_side: i32, protected_ship_id: i64 },
}

impl CampaignDialogueSpec {
    pub fn player_side(self) -> i32 {
        match self {
            Self::TitaniaMinesweeper { player_side } |
            Self::TitaniaBeenoculars { player_side, .. } |
            Self::NeptunePressingForward { player_side, .. } |
            Self::PlutoEvacuation { player_side, .. } => player_side,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
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

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
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
    NeptunePressingForwardBriefing,
    NeptunePressingForwardVictory,
    NeptunePressingForwardDefeat { has_factory: bool },
    PlutoEvacuationBriefing,
    PlutoEvacuationComplete { personnel_saved: u32, shield_health: i32, shield_max: i32 },
}

impl CampaignCue {
    pub fn is_briefing(self) -> bool {
        matches!(self, Self::MinesweeperBriefing | Self::BeenocularsBriefing { .. } |
            Self::NeptunePressingForwardBriefing | Self::PlutoEvacuationBriefing)
    }
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
enum DialogueMode {
    Minesweeper { tracked_tank: Option<i64> },
    Beenoculars { under_heavy_fire: bool, next_milestone: usize },
    NeptunePressingForward { has_factory: bool },
    PlutoEvacuation { protected_ship_id: i64 },
}

/// Exact authored campaign progress at the shared fixed-step recovery
/// boundary. The outbox delivering cues is owned by the Level event
/// processor; recovering this state alone must not re-emit old cues.
#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CampaignDialogueCheckpoint {
    schema_version: u32,
    player_side: i32,
    mode: DialogueMode,
    cue: CampaignCue,
    sequence: u8,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CampaignDialogueRestoreError {
    InvalidVersion,
    WrongStory,
    InvalidProgress,
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
                DialogueMode::Beenoculars { under_heavy_fire, next_milestone: 0 },
                CampaignCue::BeenocularsBriefing { under_heavy_fire },
            ),
            CampaignDialogueSpec::NeptunePressingForward { has_factory, .. } => (
                DialogueMode::NeptunePressingForward { has_factory },
                CampaignCue::NeptunePressingForwardBriefing,
            ),
            CampaignDialogueSpec::PlutoEvacuation { protected_ship_id, .. } => (
                DialogueMode::PlutoEvacuation { protected_ship_id },
                CampaignCue::PlutoEvacuationBriefing,
            ),
        };
        Self { player_side: spec.player_side(), mode, cue, sequence: 0 }
    }

    pub fn current(&self) -> (u8, CampaignCue) { (self.sequence, self.cue) }

    pub fn checkpoint(&self) -> CampaignDialogueCheckpoint {
        CampaignDialogueCheckpoint {
            schema_version: 1,
            player_side: self.player_side,
            mode: self.mode,
            cue: self.cue,
            sequence: self.sequence,
        }
    }

    /// Preflight a complete dialogue restore without changing the running
    /// story. The caller commits it only after every other Level owner has
    /// accepted the same checkpoint. Already-delivered cues stay delivered.
    pub fn prepare_restore(
        &self, checkpoint: CampaignDialogueCheckpoint,
    ) -> Result<Self, CampaignDialogueRestoreError> {
        let CampaignDialogueCheckpoint {
            schema_version, player_side, mode, cue, sequence,
        } = checkpoint;
        if schema_version != 1 {
            return Err(CampaignDialogueRestoreError::InvalidVersion);
        }
        let same_authored_story = match (&mode, &self.mode) {
            (DialogueMode::Minesweeper { .. }, DialogueMode::Minesweeper { .. }) =>
                true,
            (DialogueMode::Beenoculars { under_heavy_fire: saved, .. },
                DialogueMode::Beenoculars { under_heavy_fire: authored, .. }) =>
                saved == authored,
            (DialogueMode::NeptunePressingForward { has_factory: saved },
                DialogueMode::NeptunePressingForward { has_factory: authored }) =>
                saved == authored,
            (DialogueMode::PlutoEvacuation { protected_ship_id: saved },
                DialogueMode::PlutoEvacuation { protected_ship_id: authored }) =>
                saved == authored,
            _ => false,
        };
        if player_side != self.player_side || !same_authored_story {
            return Err(CampaignDialogueRestoreError::WrongStory);
        }
        let valid = match (&mode, cue) {
            (DialogueMode::Minesweeper { tracked_tank: None },
                CampaignCue::MinesweeperBriefing) => sequence == 0,
            (DialogueMode::Minesweeper { tracked_tank: None },
                CampaignCue::MinesweeperVictory | CampaignCue::MinesweeperDefeat) =>
                sequence == 1,
            (DialogueMode::Minesweeper { tracked_tank: Some(id) },
                CampaignCue::FireTankSpotted { object_id }) =>
                *id > 0 && *id == object_id && sequence == 1,
            (DialogueMode::Minesweeper { tracked_tank: Some(id) },
                CampaignCue::FireTankDestroyed { object_id }) =>
                *id > 0 && *id == object_id && sequence == 2,
            (DialogueMode::Minesweeper { tracked_tank: Some(id) },
                CampaignCue::MinesweeperVictory | CampaignCue::MinesweeperDefeat) =>
                *id > 0 && (2..=3).contains(&sequence),
            (DialogueMode::Beenoculars {
                under_heavy_fire: authored, next_milestone: 0,
            }, CampaignCue::BeenocularsBriefing { under_heavy_fire }) =>
                sequence == 0 && *authored == under_heavy_fire,
            (DialogueMode::Beenoculars { next_milestone, .. },
                CampaignCue::BeenocularsUpload(milestone)) =>
                *next_milestone > 0 &&
                UploadMilestone::ORDERED.get(*next_milestone - 1) == Some(&milestone) &&
                usize::from(sequence) == *next_milestone,
            (DialogueMode::Beenoculars { next_milestone, .. },
                CampaignCue::BeenocularsVictory | CampaignCue::BeenocularsDefeat) =>
                *next_milestone <= UploadMilestone::ORDERED.len() &&
                usize::from(sequence) == *next_milestone + 1,
            (DialogueMode::NeptunePressingForward { .. },
                CampaignCue::NeptunePressingForwardBriefing) => sequence == 0,
            (DialogueMode::NeptunePressingForward { .. },
                CampaignCue::NeptunePressingForwardVictory) => sequence == 1,
            (DialogueMode::NeptunePressingForward { has_factory: authored },
                CampaignCue::NeptunePressingForwardDefeat { has_factory }) =>
                sequence == 1 && *authored == has_factory,
            (DialogueMode::PlutoEvacuation { .. },
                CampaignCue::PlutoEvacuationBriefing) => sequence == 0,
            (DialogueMode::PlutoEvacuation { .. },
                CampaignCue::PlutoEvacuationComplete {
                    personnel_saved, shield_health, shield_max,
                }) => sequence == 1 && personnel_saved <= 60 &&
                    shield_max > 0 && (0..=shield_max).contains(&shield_health),
            _ => false,
        };
        if !valid {
            return Err(CampaignDialogueRestoreError::InvalidProgress);
        }
        Ok(Self { player_side, mode, cue, sequence })
    }

    /// Authored story owner, independent of the current local viewer or
    /// any authenticated remote commander's temporary input scope.
    pub fn player_side(&self) -> i32 { self.player_side }

    pub(crate) fn set_cue(&mut self, cue: CampaignCue) {
        self.sequence += 1;
        self.cue = cue;
    }

    pub(crate) fn outcome(
        &self, winner_side: i32, world: &CombatWorld,
        survival: Option<SurvivalStatus>,
    ) -> CampaignCue {
        match (&self.mode, winner_side == self.player_side) {
            (DialogueMode::Minesweeper { .. }, true) => CampaignCue::MinesweeperVictory,
            (DialogueMode::Minesweeper { .. }, false) => CampaignCue::MinesweeperDefeat,
            (DialogueMode::Beenoculars { .. }, true) => CampaignCue::BeenocularsVictory,
            (DialogueMode::Beenoculars { .. }, false) => CampaignCue::BeenocularsDefeat,
            (DialogueMode::NeptunePressingForward { .. }, true) =>
                CampaignCue::NeptunePressingForwardVictory,
            (DialogueMode::NeptunePressingForward { has_factory }, false) =>
                CampaignCue::NeptunePressingForwardDefeat { has_factory: *has_factory },
            (DialogueMode::PlutoEvacuation { protected_ship_id }, _) => {
                let status = survival.expect("Pluto evacuation requires survival");
                // The protected hull may already have been retired from
                // the active World when its lethal terminal event closed
                // the mission. That is a destroyed shield, not a story
                // checkpoint failure or a reason to fabricate a victory.
                let (shield_health, shield_max) = world.ship(*protected_ship_id)
                    .map(|ship| (ship.vitals.health().max(0),
                        ship.vitals.max_health()))
                    .unwrap_or((0, 3000));
                CampaignCue::PlutoEvacuationComplete {
                    personnel_saved: (status.elapsed_seconds / 5.0)
                        .floor().clamp(0.0, 60.0) as u32,
                    shield_health,
                    shield_max,
                }
            },
        }
    }

    pub(crate) fn take_upload_cue(&mut self, survival: SurvivalStatus) -> Option<CampaignCue> {
        let DialogueMode::Beenoculars { next_milestone, .. } = &mut self.mode else { return None };
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
