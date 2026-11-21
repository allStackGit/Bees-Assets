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
    /// Pluto III's introduction includes only officers whose real
    /// player-side ship classes are present after career fleet restoration.
    /// Bits: Scout, Frigate, Gunship, Dreadnought.
    PlutoPushback { player_side: i32, speaker_mask: u8 },
    /// Source Neptune 3 ending branches on persistent Factory ownership,
    /// not the presence of a surviving Factory in the deployed roster.
    NeptunePressingForward { player_side: i32, has_factory: bool },
    /// Source Neptune I awards five unassigned Factory fleet hulls on a
    /// real military victory; the native career ledger owns those grants.
    NeptuneSeizeMeans { player_side: i32 },
    /// Neptune II's real mining withdrawal and escalating source waves.
    NeptuneProduction { player_side: i32 },
    PlutoEvacuation { player_side: i32, protected_ship_id: i64 },
    /// Source Uranus III protects exactly three real Barge hulls.
    UranusNewThreat { player_side: i32, barges: [i64; 3], has_cruiser: bool },
    /// Source Uranus II: survive six timed Bee waves, mine and withdraw
    /// the fleet at any point using the genuine physical retreat zone.
    UranusDefensive {
        player_side: i32, has_cruiser: bool, met_scientists: bool,
    },
    /// Uranus I source encounter: only the named Bumblebee can trigger
    /// discovery, damage warning and follow-up casualty dialogue.
    UranusOffensive { player_side: i32, bumblebee_id: i64, has_factory: bool },
}

impl CampaignDialogueSpec {
    pub fn player_side(self) -> i32 {
        match self {
            Self::TitaniaMinesweeper { player_side } |
            Self::TitaniaBeenoculars { player_side, .. } |
            Self::PlutoPushback { player_side, .. } |
            Self::NeptunePressingForward { player_side, .. } |
            Self::NeptuneSeizeMeans { player_side } |
            Self::NeptuneProduction { player_side } |
            Self::PlutoEvacuation { player_side, .. } |
            Self::UranusNewThreat { player_side, .. } |
            Self::UranusDefensive { player_side, .. } |
            Self::UranusOffensive { player_side, .. } => player_side,
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
    PlutoPushbackBriefing { speaker_mask: u8 },
    PlutoPushbackVictory,
    PlutoPushbackDefeat,
    NeptunePressingForwardBriefing,
    NeptunePressingForwardVictory,
    NeptunePressingForwardDefeat { has_factory: bool },
    NeptuneSeizeBriefing,
    NeptuneSeizeVictory,
    NeptuneSeizeDefeat,
    NeptuneProductionBriefing,
    NeptuneProductionWave { number: u32, line: u8 },
    NeptuneProductionEnding { last_ship_retreated: bool, player_victory: bool },
    PlutoEvacuationBriefing,
    PlutoEvacuationComplete { personnel_saved: u32, shield_health: i32, shield_max: i32 },
    UranusNewThreatBriefing,
    UranusNewThreatBargeLost,
    UranusNewThreatBargeCharged,
    UranusNewThreatReinforcements,
    UranusNewThreatEnding { barges_survived: bool, has_cruiser: bool },
    UranusDefensiveBriefing,
    /// Source six physical reinforcement announcements, in arrival order.
    UranusDefensiveWave { number: u8, line: u8 },
    UranusDefensiveEnding { last_ship_retreated: bool, player_victory: bool },
    UranusOffensiveBriefing,
    UranusOffensiveBumblebeeSpotted,
    UranusOffensiveBumblebeeHit,
    UranusOffensiveBumblebeeDestroyed,
    UranusOffensiveEnding { has_factory: bool, player_victory: bool },
}

impl CampaignCue {
    pub fn is_briefing(self) -> bool {
        matches!(self, Self::MinesweeperBriefing | Self::BeenocularsBriefing { .. } |
            Self::PlutoPushbackBriefing { .. } |
            Self::NeptunePressingForwardBriefing | Self::NeptuneSeizeBriefing |
            Self::PlutoEvacuationBriefing | Self::NeptuneProductionBriefing |
            Self::UranusNewThreatBriefing | Self::UranusDefensiveBriefing |
            Self::UranusOffensiveBriefing)
    }
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
enum DialogueMode {
    Minesweeper { tracked_tank: Option<i64> },
    Beenoculars { under_heavy_fire: bool, next_milestone: usize },
    PlutoPushback { speaker_mask: u8 },
    NeptunePressingForward { has_factory: bool },
    NeptuneSeizeMeans,
    NeptuneProduction { waves_reported: u32 },
    PlutoEvacuation { protected_ship_id: i64 },
    UranusNewThreat {
        barges: [i64; 3], has_cruiser: bool,
        loss_reported: bool, charge_observed: bool, charge_reported: bool,
        reinforcement_reported: bool,
    },
    UranusDefensive {
        has_cruiser: bool, met_scientists: bool,
        waves_reported: u8,
    },
    UranusOffensive {
        bumblebee_id: i64, has_factory: bool,
        discovered: bool, hit_seen: bool, hit_reported: bool, destroyed_reported: bool,
    },
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
    sequence: u32,
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
    sequence: u32,
}

/// Read-only public mission identity, derived from the one campaign
/// state machine. This is not another objective/lifecycle owner.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CampaignObjectiveKind {
    PlutoPushback,
    UranusOffensive,
    UranusDefensive,
}

impl CampaignDialogue {
    pub fn objective_kind(&self) -> Option<CampaignObjectiveKind> {
        match self.mode {
            DialogueMode::PlutoPushback { .. } =>
                Some(CampaignObjectiveKind::PlutoPushback),
            DialogueMode::UranusOffensive { .. } =>
                Some(CampaignObjectiveKind::UranusOffensive),
            DialogueMode::UranusDefensive { .. } =>
                Some(CampaignObjectiveKind::UranusDefensive),
            _ => None,
        }
    }

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
            CampaignDialogueSpec::PlutoPushback { speaker_mask, .. } => (
                DialogueMode::PlutoPushback { speaker_mask },
                CampaignCue::PlutoPushbackBriefing { speaker_mask },
            ),
            CampaignDialogueSpec::NeptunePressingForward { has_factory, .. } => (
                DialogueMode::NeptunePressingForward { has_factory },
                CampaignCue::NeptunePressingForwardBriefing,
            ),
            CampaignDialogueSpec::NeptuneSeizeMeans { .. } => (
                DialogueMode::NeptuneSeizeMeans,
                CampaignCue::NeptuneSeizeBriefing,
            ),
            CampaignDialogueSpec::NeptuneProduction { .. } => (
                DialogueMode::NeptuneProduction { waves_reported: 0 },
                CampaignCue::NeptuneProductionBriefing,
            ),
            CampaignDialogueSpec::PlutoEvacuation { protected_ship_id, .. } => (
                DialogueMode::PlutoEvacuation { protected_ship_id },
                CampaignCue::PlutoEvacuationBriefing,
            ),
            CampaignDialogueSpec::UranusNewThreat { barges, has_cruiser, .. } => (
                DialogueMode::UranusNewThreat {
                    barges, has_cruiser, loss_reported: false,
                    charge_observed: false, charge_reported: false,
                    reinforcement_reported: false,
                },
                CampaignCue::UranusNewThreatBriefing,
            ),
            CampaignDialogueSpec::UranusDefensive {
                has_cruiser, met_scientists, ..
            } => (
                DialogueMode::UranusDefensive {
                    has_cruiser, met_scientists, waves_reported: 0,
                },
                CampaignCue::UranusDefensiveBriefing,
            ),
            CampaignDialogueSpec::UranusOffensive {
                bumblebee_id, has_factory, ..
            } => (
                DialogueMode::UranusOffensive {
                    bumblebee_id, has_factory, discovered: false,
                    hit_seen: false, hit_reported: false, destroyed_reported: false,
                },
                CampaignCue::UranusOffensiveBriefing,
            ),
        };
        Self { player_side: spec.player_side(), mode, cue, sequence: 0 }
    }

    pub fn current(&self) -> (u32, CampaignCue) { (self.sequence, self.cue) }

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
            (DialogueMode::PlutoPushback { speaker_mask: saved },
                DialogueMode::PlutoPushback { speaker_mask: authored }) =>
                saved == authored,
            (DialogueMode::NeptunePressingForward { has_factory: saved },
                DialogueMode::NeptunePressingForward { has_factory: authored }) =>
                saved == authored,
            (DialogueMode::NeptuneSeizeMeans, DialogueMode::NeptuneSeizeMeans) =>
                true,
            (DialogueMode::NeptuneProduction { .. },
                DialogueMode::NeptuneProduction { .. }) => true,
            (DialogueMode::PlutoEvacuation { protected_ship_id: saved },
                DialogueMode::PlutoEvacuation { protected_ship_id: authored }) =>
                saved == authored,
            (DialogueMode::UranusNewThreat {
                barges: saved, has_cruiser: saved_cruiser, ..
            }, DialogueMode::UranusNewThreat {
                barges: authored, has_cruiser: authored_cruiser, ..
            }) => saved == authored && saved_cruiser == authored_cruiser,
            (DialogueMode::UranusDefensive {
                has_cruiser: old_cruiser, met_scientists: old_scientists, ..
            }, DialogueMode::UranusDefensive {
                has_cruiser: new_cruiser, met_scientists: new_scientists, ..
            }) => old_cruiser == new_cruiser && old_scientists == new_scientists,
            (DialogueMode::UranusOffensive {
                bumblebee_id: saved, has_factory: old, ..
            }, DialogueMode::UranusOffensive {
                bumblebee_id: authored, has_factory: new, ..
            }) => saved == authored && old == new,
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
            (DialogueMode::PlutoPushback { speaker_mask: authored },
                CampaignCue::PlutoPushbackBriefing { speaker_mask }) =>
                sequence == 0 && *authored == speaker_mask && speaker_mask <= 0b1111,
            (DialogueMode::PlutoPushback { speaker_mask },
                CampaignCue::PlutoPushbackVictory | CampaignCue::PlutoPushbackDefeat) =>
                sequence == 1 && *speaker_mask <= 0b1111,
            (DialogueMode::NeptuneProduction { waves_reported: 0 },
                CampaignCue::NeptuneProductionBriefing) => sequence == 0,
            (DialogueMode::NeptuneProduction { waves_reported },
                CampaignCue::NeptuneProductionWave { number, line }) =>
                number > 0 && number == *waves_reported &&
                sequence == number && match number {
                    1 => line == 8,
                    3 => line == 9,
                    _ => (10..=14).contains(&line),
                },
            (DialogueMode::NeptuneProduction { waves_reported },
                CampaignCue::NeptuneProductionEnding { .. }) =>
                sequence == *waves_reported + 1,
            (DialogueMode::NeptuneSeizeMeans, CampaignCue::NeptuneSeizeBriefing) =>
                sequence == 0,
            (DialogueMode::NeptuneSeizeMeans,
                CampaignCue::NeptuneSeizeVictory | CampaignCue::NeptuneSeizeDefeat) =>
                sequence == 1,
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
            (DialogueMode::UranusOffensive {
                discovered: false, hit_seen: false,
                hit_reported: false, destroyed_reported: false, ..
            }, CampaignCue::UranusOffensiveBriefing) => sequence == 0,
            (DialogueMode::UranusOffensive { discovered: true, hit_reported: false,
                destroyed_reported: false, .. },
                CampaignCue::UranusOffensiveBumblebeeSpotted) => sequence == 1,
            (DialogueMode::UranusOffensive { discovered: true, hit_seen: true,
                hit_reported: true, destroyed_reported, .. },
                CampaignCue::UranusOffensiveBumblebeeHit) =>
                sequence == 2 + u32::from(*destroyed_reported),
            (DialogueMode::UranusOffensive { discovered: true, hit_reported,
                destroyed_reported: true, .. },
                CampaignCue::UranusOffensiveBumblebeeDestroyed) =>
                sequence == 2 + u32::from(*hit_reported),
            (DialogueMode::UranusOffensive { discovered, hit_reported,
                destroyed_reported, has_factory: original, .. },
                CampaignCue::UranusOffensiveEnding { has_factory, .. }) =>
                *original == has_factory && sequence ==
                    1 + u32::from(*discovered) + u32::from(*hit_reported) +
                        u32::from(*destroyed_reported),
            (DialogueMode::UranusDefensive {
                waves_reported: 0, ..
            }, CampaignCue::UranusDefensiveBriefing) => sequence == 0,
            (DialogueMode::UranusDefensive {
                has_cruiser, met_scientists, waves_reported,
            }, CampaignCue::UranusDefensiveWave { number, line }) =>
                number > 0 && number <= 6 && number == *waves_reported &&
                sequence == u32::from(number) &&
                Self::uranus_defensive_line_valid(number, line, *has_cruiser, *met_scientists),
            (DialogueMode::UranusDefensive { waves_reported, .. },
                CampaignCue::UranusDefensiveEnding { .. }) =>
                *waves_reported <= 6 && sequence == u32::from(*waves_reported) + 1,
            (DialogueMode::UranusNewThreat {
                loss_reported: false, charge_observed: false,
                charge_reported: false, reinforcement_reported: false, ..
            }, CampaignCue::UranusNewThreatBriefing) => sequence == 0,
            (DialogueMode::UranusNewThreat {
                loss_reported: true, charge_reported, reinforcement_reported, ..
            }, CampaignCue::UranusNewThreatBargeLost) =>
                sequence == 1 + u32::from(*charge_reported) +
                    u32::from(*reinforcement_reported),
            (DialogueMode::UranusNewThreat {
                loss_reported, charge_observed: true,
                charge_reported: true, reinforcement_reported, ..
            }, CampaignCue::UranusNewThreatBargeCharged) =>
                sequence == 1 + u32::from(*loss_reported) +
                    u32::from(*reinforcement_reported),
            (DialogueMode::UranusNewThreat {
                loss_reported, charge_reported, reinforcement_reported: true, ..
            }, CampaignCue::UranusNewThreatReinforcements) =>
                sequence == 1 + u32::from(*loss_reported) +
                    u32::from(*charge_reported),
            (DialogueMode::UranusNewThreat {
                loss_reported, charge_observed, charge_reported,
                reinforcement_reported, has_cruiser: authored, ..
            }, CampaignCue::UranusNewThreatEnding { has_cruiser, .. }) =>
                *authored == has_cruiser &&
                (!*charge_reported || *charge_observed) &&
                sequence == 1 + u32::from(*loss_reported) +
                    u32::from(*charge_reported) + u32::from(*reinforcement_reported),
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

    /// Read-only objective identity for local and authenticated LAN HUDs.
    /// The three source FleetShip hulls are still owned by CombatWorld.
    pub fn uranus_offensive_target(&self) -> Option<i64> {
        if let DialogueMode::UranusOffensive { bumblebee_id, discovered: false, .. } =
            self.mode {
            Some(bumblebee_id)
        } else {
            None
        }
    }

    pub fn rescue_barges(&self) -> Option<[i64; 3]> {
        if let DialogueMode::UranusNewThreat { barges, .. } = self.mode {
            Some(barges)
        } else {
            None
        }
    }

    pub(crate) fn set_cue(&mut self, cue: CampaignCue) {
        // Long source survival battles can legitimately announce hundreds
        // of physical reinforcement arrivals. u32 keeps the same on-wire
        // integer and old JSON checkpoints readable without wrapping
        // the cue revision and causing replay/multiplayer duplicates.
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
            (DialogueMode::PlutoPushback { .. }, true) =>
                CampaignCue::PlutoPushbackVictory,
            (DialogueMode::PlutoPushback { .. }, false) =>
                CampaignCue::PlutoPushbackDefeat,
            (DialogueMode::NeptuneProduction { .. }, player_victory) =>
                CampaignCue::NeptuneProductionEnding {
                    last_ship_retreated: world.extraction_retreat_completed(),
                    player_victory,
                },
            (DialogueMode::NeptuneSeizeMeans, true) =>
                CampaignCue::NeptuneSeizeVictory,
            (DialogueMode::NeptuneSeizeMeans, false) =>
                CampaignCue::NeptuneSeizeDefeat,
            (DialogueMode::NeptunePressingForward { .. }, true) =>
                CampaignCue::NeptunePressingForwardVictory,
            (DialogueMode::NeptunePressingForward { has_factory }, false) =>
                CampaignCue::NeptunePressingForwardDefeat { has_factory: *has_factory },
            (DialogueMode::UranusOffensive { has_factory, .. }, player_victory) =>
                CampaignCue::UranusOffensiveEnding {
                    has_factory: *has_factory, player_victory,
                },
            (DialogueMode::UranusDefensive { .. }, player_victory) =>
                CampaignCue::UranusDefensiveEnding {
                    last_ship_retreated: world.extraction_retreat_completed(),
                    player_victory,
                },
            (DialogueMode::UranusNewThreat { barges, has_cruiser, .. }, _) =>
                CampaignCue::UranusNewThreatEnding {
                    barges_survived: barges.iter().any(|id|
                        world.ship(*id).is_some_and(|ship| !ship.vitals.is_dead())),
                    has_cruiser: *has_cruiser,
                },
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

    fn uranus_defensive_line_valid(
        wave: u8, line: u8, has_cruiser: bool, met_scientists: bool,
    ) -> bool {
        match wave {
            1 => line == 1,
            4 => line == 8,
            2 | 3 | 5 | 6 => (2..=7).contains(&line) ||
                (has_cruiser && line == 9) ||
                (met_scientists && matches!(line, 10 | 11)),
            _ => false,
        }
    }

    /// Caller consumes the shared World RNG only for the source waves
    /// which actually choose dialogue. Fixed waves need no random draw.
    pub(crate) fn reinforcement_choice_count(&self, elapsed_seconds: f64) -> Option<u64> {
        if let DialogueMode::NeptuneProduction { waves_reported } = self.mode {
            let due = if waves_reported < 4 {
                f64::from(waves_reported + 1) * 120.0
            } else {
                480.0 + f64::from(waves_reported - 3) * 60.0
            };
            if elapsed_seconds + 0.0001 < due ||
                matches!(waves_reported, 0 | 2) {
                return None;
            }
            // The first and third Neptune II wave lines are fixed.
            // Every other arrival uses Utilities.RandomInt(5) + 10.
            return Some(5);
        }
        let DialogueMode::UranusDefensive {
            has_cruiser, met_scientists, waves_reported,
        } = self.mode else { return None };
        let next = usize::from(waves_reported);
        let due = [120.0, 240.0, 360.0, 480.0, 540.0, 600.0];
        if next >= due.len() || elapsed_seconds + 0.0001 < due[next] ||
            matches!(next, 0 | 3) {
            return None;
        }
        Some(6 + u64::from(has_cruiser) + 2 * u64::from(met_scientists))
    }

    /// Source Uranus III announces the timed follow-up wave when its
    /// physical squads actually arrive, not merely when the timer elapses.
    /// The shared Level event owner provides the arrival receipt exactly
    /// once, even for multiple squads admitted in the same fixed tick.
    pub(crate) fn reinforcement_arrived(
        &mut self, elapsed_seconds: f64, random_choice: Option<u64>,
    ) -> Option<CampaignCue> {
        if let DialogueMode::NeptuneProduction { waves_reported } = &mut self.mode {
            let due = if *waves_reported < 4 {
                f64::from(*waves_reported + 1) * 120.0
            } else {
                480.0 + f64::from(*waves_reported - 3) * 60.0
            };
            if elapsed_seconds + 0.0001 < due {
                return None;
            }
            let number = waves_reported.checked_add(1)?;
            let line = match number {
                1 => 8,
                3 => 9,
                _ => {
                    let choice = random_choice?;
                    if choice >= 5 { return None; }
                    10 + choice as u8
                }
            };
            *waves_reported = number;
            return Some(CampaignCue::NeptuneProductionWave { number, line });
        }
        if let DialogueMode::UranusDefensive {
            has_cruiser, met_scientists, waves_reported,
        } = &mut self.mode {
            const DUE: [f64; 6] = [120.0, 240.0, 360.0, 480.0, 540.0, 600.0];
            let next = usize::from(*waves_reported);
            if next >= DUE.len() || elapsed_seconds + 0.0001 < DUE[next] {
                return None;
            }
            let number = *waves_reported + 1;
            // Unity Utilities.RandomInt(dialogueLines.Count) samples only
            // waves 2, 3, 5 and 6. Optional Cruiser/scientist lines expand
            // the source pool without changing the default six messages.
            let line = match number {
                1 => 1,
                4 => 8,
                _ => {
                    let choice = random_choice?;
                    let available = 6 + u64::from(*has_cruiser) +
                        2 * u64::from(*met_scientists);
                    if choice >= available { return None; }
                    if choice < 6 { 2 + choice as u8 }
                    else if *has_cruiser && choice == 6 { 9 }
                    else { 10 + (choice - 6 - u64::from(*has_cruiser)) as u8 }
                }
            };
            *waves_reported = number;
            return Some(CampaignCue::UranusDefensiveWave { number, line });
        }
        let DialogueMode::UranusNewThreat { reinforcement_reported, .. } =
            &mut self.mode else { return None };
        // The original mission plays this cue for the 60-second follow-up
        // wave, not initial deployments or an unrelated scripted arrival.
        if *reinforcement_reported || elapsed_seconds + 0.0001 < 60.0 {
            return None;
        }
        *reinforcement_reported = true;
        Some(CampaignCue::UranusNewThreatReinforcements)
    }

    pub(crate) fn observe_combat(
        &mut self, world: &CombatWorld, source_bumblebee_spotted: bool,
    ) -> Option<CampaignCue> {
        if let DialogueMode::UranusOffensive {
            bumblebee_id, discovered, hit_seen, hit_reported,
            destroyed_reported, ..
        } = &mut self.mode {
            // The source creates its attack/death triggers only after the
            // Bumblebee has actually been discovered. A pre-discovery shot
            // must not count as the later "ShipsHit" callback.
            if !*discovered && source_bumblebee_spotted {
                *discovered = true;
                return Some(CampaignCue::UranusOffensiveBumblebeeSpotted);
            }
            let both_sides_fighting = [self.player_side, 3 - self.player_side]
                .into_iter().all(|side|
                    world.living_mobile_ships_on_side(side) > 0 ||
                    world.has_reserved_future_ships_on_side(side));
            if !*discovered || !both_sides_fighting {
                return None;
            }
            // Unity Ship.ApplyDamage adds the living target to
            // attacker.ShipsHit *before* subtracting health, even when
            // MakeShotsHarmless reduces the applied power to zero. The
            // Bumblebee's encounter trigger reads ShipsHit.Count > 0,
            // not accumulated damage or the victim's faction.
            *hit_seen |= world.events().iter().any(|event| matches!(event,
                bees_sim::combat_world::CombatEvent::Hit(hit)
                    if hit.attacker_id == *bumblebee_id
            ));
            if *hit_seen && !*hit_reported {
                *hit_reported = true;
                return Some(CampaignCue::UranusOffensiveBumblebeeHit);
            }
            if !*destroyed_reported && world.ship(*bumblebee_id)
                .is_some_and(|ship| ship.vitals.is_dead()) {
                *destroyed_reported = true;
                return Some(CampaignCue::UranusOffensiveBumblebeeDestroyed);
            }
            return None;
        }
        if let DialogueMode::UranusNewThreat {
            barges, loss_reported, charge_observed, charge_reported, ..
        } = &mut self.mode {
            // Observe simultaneous true physical loss and first charge.
            // Deliver one dialogue cue per fixed step without discarding
            // the other when both happen in the same physics tick.
            *charge_observed |= world.events().iter().any(|event|
                matches!(event, bees_sim::combat_world::CombatEvent::BargeChargeActivated {
                    ship_id
                } if barges.contains(ship_id)));
            let side_alive = |side| world.living_mobile_ships_on_side(side) > 0 ||
                world.has_reserved_future_ships_on_side(side);
            if !*loss_reported && barges.iter().any(|id|
                world.ship(*id).is_some_and(|ship| ship.vitals.is_dead())) &&
                side_alive(self.player_side) && side_alive(3 - self.player_side)
            {
                *loss_reported = true;
                return Some(CampaignCue::UranusNewThreatBargeLost);
            }
            if *charge_observed && !*charge_reported &&
                side_alive(self.player_side) && side_alive(3 - self.player_side) {
                *charge_reported = true;
                return Some(CampaignCue::UranusNewThreatBargeCharged);
            }
            return None;
        }
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
