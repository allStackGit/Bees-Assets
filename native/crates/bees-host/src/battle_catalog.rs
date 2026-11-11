//! Authored battle lists and progress derived from completed runs.

use std::collections::{HashMap, HashSet};
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use serde::Deserialize;

use super::{invalid, CampaignOutcome};

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct BattleEntry {
    pub title: String,
    pub config: PathBuf,
    pub mission: Option<Mission>,
}

impl BattleEntry {
    pub fn config_for(&self, progress: &CampaignProgress<'_>) -> &Path {
        self.mission.as_ref().and_then(|mission| mission.on_victory.as_ref())
            .filter(|route| progress.latest_victory(&route.mission))
            .map_or(self.config.as_path(), |route| route.config.as_path())
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Mission {
    pub id: String,
    pub player_side: i32,
    #[serde(default)]
    pub requires: Vec<String>,
    #[serde(default)]
    advance_on: AdvanceOn,
    on_victory: Option<VictoryRoute>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct VictoryRoute {
    mission: String,
    config: PathBuf,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "snake_case")]
enum AdvanceOn {
    #[default]
    Victory,
    Finished,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct BattleCatalog {
    pub campaign_id: Option<String>,
    pub battles: Vec<BattleEntry>,
}

#[derive(Default)]
pub struct CampaignProgress<'a> {
    missions: HashMap<&'a str, MissionProgress>,
}

#[derive(Default)]
struct MissionProgress {
    completed: bool,
    latest_victory: bool,
}

impl CampaignProgress<'_> {
    pub fn completed(&self, id: &str) -> bool {
        self.missions.get(id).is_some_and(|mission| mission.completed)
    }

    fn latest_victory(&self, id: &str) -> bool {
        self.missions.get(id).is_some_and(|mission| mission.latest_victory)
    }
}

impl BattleCatalog {
    pub fn validate(&self) -> io::Result<()> {
        if self.battles.is_empty() || self.battles.iter().any(|entry|
            entry.title.trim().is_empty() || entry.config.as_os_str().is_empty())
        {
            return Err(invalid("The battle catalog needs at least one battle, each with a title and run configuration"));
        }
        if self.campaign_id.as_ref().is_some_and(|id| id.trim().is_empty()) {
            return Err(invalid("campaign_id must not be blank"));
        }
        let mut earlier = HashSet::new();
        for mission in self.battles.iter().filter_map(|entry| entry.mission.as_ref()) {
            if self.campaign_id.is_none() || mission.id.trim().is_empty() ||
                !matches!(mission.player_side, 1 | 2) || earlier.contains(mission.id.as_str()) ||
                mission.requires.iter().any(|id| !earlier.contains(id.as_str()))
            {
                return Err(invalid(format!(
                    "Mission {:?} needs a campaign ID, unique mission ID, player side 1 or 2, and prerequisites listed earlier in the catalog",
                    mission.id,
                )));
            }
            if mission.on_victory.as_ref().is_some_and(|route|
                !mission.requires.contains(&route.mission) || route.config.as_os_str().is_empty()
            ) {
                return Err(invalid(format!(
                    "Mission {:?} needs an existing prerequisite and a run configuration for on_victory",
                    mission.id,
                )));
            }
            earlier.insert(mission.id.as_str());
        }
        Ok(())
    }

    pub fn load_progress<'a>(&'a self, output: &Path) -> io::Result<CampaignProgress<'a>> {
        let mut progress = CampaignProgress::default();
        let Some(campaign) = &self.campaign_id else { return Ok(progress) };
        let record = |directory: &Path, progress: &mut CampaignProgress<'a>| -> io::Result<()> {
            #[derive(Deserialize)]
            struct Summary { campaign_outcome: Option<CampaignOutcome> }

            let bytes = match fs::read(directory.join("rollout-summary.json")) {
                Ok(bytes) => bytes,
                Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(()),
                Err(error) => return Err(error),
            };
            // Older interrupted runs may have a partial summary. Only a
            // complete, readable campaign outcome can grant progress.
            let Ok(Summary { campaign_outcome: Some(outcome) }) = serde_json::from_slice(&bytes) else { return Ok(()) };
            if outcome.campaign_id != *campaign { return Ok(()); }
            if let Some(mission) = self.battles.iter().filter_map(|entry| entry.mission.as_ref())
                .find(|mission| mission.id == outcome.mission_id && mission.player_side == outcome.player_side)
            {
                let state = progress.missions.entry(&mission.id).or_default();
                state.latest_victory = outcome.winner_side == mission.player_side;
                state.completed |= matches!(mission.advance_on, AdvanceOn::Finished) || state.latest_victory;
            }
            Ok(())
        };
        for battle in numbered_directories(output, "battle-")? {
            let run = battle.join("run");
            record(&run, &mut progress)?;
            // A fresh rematch can complete a mission or change its route.
            // Watching a recording has no campaign outcome in its summary.
            for rematch in numbered_directories(&run, "replay-")? {
                record(&rematch, &mut progress)?;
            }
        }
        Ok(progress)
    }
}

fn numbered_directories(root: &Path, prefix: &str) -> io::Result<Vec<PathBuf>> {
    let entries = match fs::read_dir(root) {
        Ok(entries) => entries,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(error) => return Err(error),
    };
    let mut numbered = Vec::new();
    for entry in entries {
        let entry = entry?;
        let name = entry.file_name();
        let Some(number) = name.to_str().and_then(|name| name.strip_prefix(prefix))
            .filter(|suffix| suffix.bytes().all(|byte| byte.is_ascii_digit()))
            .and_then(|suffix| suffix.parse::<u64>().ok()) else { continue };
        if entry.file_type()?.is_dir() {
            numbered.push((number, entry.path()));
        }
    }
    numbered.sort_unstable();
    Ok(numbered.into_iter().map(|(_, path)| path).collect())
}
