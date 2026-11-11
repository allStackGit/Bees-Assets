//! Authored battle lists and progress derived from completed runs.

use std::collections::HashSet;
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

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Mission {
    pub id: String,
    pub player_side: i32,
    #[serde(default)]
    pub requires: Vec<String>,
    #[serde(default)]
    advance_on: AdvanceOn,
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
            earlier.insert(mission.id.as_str());
        }
        Ok(())
    }

    pub fn load_progress<'a>(&'a self, output: &Path) -> io::Result<HashSet<&'a str>> {
        let mut progress = HashSet::new();
        if self.campaign_id.is_some() {
            for entry in fs::read_dir(output)? {
                let entry = entry?;
                if entry.file_type()?.is_dir() && numbered(&entry.file_name(), "battle-") {
                    self.add_battle_progress(&entry.path().join("run"), &mut progress)?;
                }
            }
        }
        Ok(progress)
    }

    pub fn add_battle_progress<'a>(&'a self, run: &Path, progress: &mut HashSet<&'a str>) -> io::Result<()> {
        let Some(campaign) = &self.campaign_id else { return Ok(()) };
        let record = |directory: &Path, progress: &mut HashSet<&'a str>| -> io::Result<()> {
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
                if matches!(mission.advance_on, AdvanceOn::Finished) || outcome.winner_side == mission.player_side {
                    progress.insert(&mission.id);
                }
            }
            Ok(())
        };
        record(run, progress)?;
        let entries = match fs::read_dir(run) {
            Ok(entries) => entries,
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(()),
            Err(error) => return Err(error),
        };
        // A fresh rematch started at the result screen can complete a
        // mission. Watching a recording has no campaign outcome in its summary.
        for entry in entries {
            let entry = entry?;
            if entry.file_type()?.is_dir() && numbered(&entry.file_name(), "replay-") {
                record(&entry.path(), progress)?;
            }
        }
        Ok(())
    }
}

fn numbered(name: &std::ffi::OsStr, prefix: &str) -> bool {
    name.to_str().and_then(|name| name.strip_prefix(prefix))
        .is_some_and(|suffix| !suffix.is_empty() && suffix.bytes().all(|byte| byte.is_ascii_digit()))
}
