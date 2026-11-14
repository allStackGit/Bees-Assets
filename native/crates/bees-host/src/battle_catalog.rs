//! Authored battle lists and progress derived from completed runs.

use std::collections::{HashMap, HashSet};
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use serde::Deserialize;
use serde_json::{json, Value};

use super::{invalid, CampaignOutcome, CampaignFleetShipOutcome};
use bees_host::fleet_inventory::NativeFleetCombatStats;

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
    /// Exactly Unity Stage.RecordStats for this authored mission; a
    /// successful battle must not invent FleetShip career progression
    /// if its source stage deliberately disables stat recording.
    #[serde(default)]
    pub record_fleet_stats: bool,
    /// Unity CollectMinedMineralsForPlayer is a stage ending action,
    /// not a default reward on every completed battle. Only explicitly
    /// designated authored missions credit the player account.
    #[serde(default)]
    pub bank_player_minerals: bool,
    /// Opt-in historical FleetShip deaths on the next authored Level.
    /// Only ships belonging to the player side are removed at setup;
    /// opposing authored forces remain owned by their scenario.
    #[serde(default)]
    pub persist_fleet_losses: bool,
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
    fleet: HashMap<i64, FleetProgress>,
    squads: HashMap<i64, SquadProgress>,
    battles_without_fleet_data: u64,
    battles_without_squad_data: u64,
    banked_minerals: u64,
    bank_history_complete: bool,
}

#[derive(Default)]
struct FleetProgress {
    side: i32,
    battles_fought: u64,
    battles_won: u64,
    minerals_mined: u64,
    is_dead: bool,
    loss_history_complete: bool,
    combat: NativeFleetCombatStats,
    combat_history_complete: bool,
}

#[derive(Default)]
struct SquadProgress {
    side: i32,
    battles_fought: u64,
    battles_won: u64,
    ships_lost: u64,
    damage_done: u64,
    damage_received: u64,
    kills: u64,
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

    /// Current native menu presentation of the same durable career record
    /// exposed by the read-only campaign status API.
    pub fn lost_player_fleet_ids(&self, side: i32) -> io::Result<HashSet<i64>> {
        if self.battles_without_fleet_data != 0 ||
            self.fleet.values().any(|ship|
                ship.side == side && !ship.loss_history_complete)
        {
            return Err(invalid(
                "saved campaign contains older fleet receipts without death evidence; cannot safely restore permanent losses",
            ));
        }
        Ok(self.fleet.iter().filter_map(|(&id, ship)|
            (ship.side == side && ship.is_dead).then_some(id)).collect())
    }

    pub fn squad_rows(&self) -> Vec<String> {
        let mut ids = self.squads.keys().copied().collect::<Vec<_>>();
        ids.sort_unstable();
        ids.into_iter().map(|id| {
            let squad = &self.squads[&id];
            format!(
                "Squad #{id} S{} W{}/{} Lost{} Hit{} Taken{} Kill{}",
                squad.side, squad.battles_won, squad.battles_fought,
                squad.ships_lost, squad.damage_done, squad.damage_received,
                squad.kills,
            )
        }).collect()
    }

    pub fn fleet_rows(&self) -> Vec<String> {
        let mut ids = self.fleet.keys().copied().collect::<Vec<_>>();
        ids.sort_unstable();
        ids.into_iter().map(|id| {
            let ship = &self.fleet[&id];
            format!(
                "#{id} S{} W{}/{} M{} Hit{} Taken{} Kill{}{}",
                ship.side, ship.battles_won, ship.battles_fought,
                ship.minerals_mined, ship.combat.enemy_tsv_damage_done,
                ship.combat.tsv_damage_received, ship.combat.attributed_enemy_kills,
                if ship.is_dead { " LOST" } else { "" },
            )
        }).collect()
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
                !matches!(mission.player_side, 1 | 2) ||
                ((mission.persist_fleet_losses || mission.bank_player_minerals) &&
                    !mission.record_fleet_stats) ||
                earlier.contains(mission.id.as_str()) ||
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

    /// Reconstruct save data from *completed* campaign summaries, not from
    /// mutable UI state. A crashed game cannot half-credit minerals or a
    /// rematch, and re-opening the menu cannot credit the same run twice.
    /// Every FleetShip in a finished battle contributes one BattlesFought,
    /// one BattlesWon on its side's victory, and its exact settled mineral
    /// tally from that battle's Level-owned FleetShip inventory.
    pub fn load_progress<'a>(&'a self, output: &Path) -> io::Result<CampaignProgress<'a>> {
        let mut progress = CampaignProgress {
            bank_history_complete: true, ..CampaignProgress::default()
        };
        let Some(campaign) = &self.campaign_id else { return Ok(progress) };
        let record = |directory: &Path, progress: &mut CampaignProgress<'a>| -> io::Result<()> {
            #[derive(Deserialize)]
            struct Summary {
                campaign_outcome: Option<CampaignOutcome>,
                #[serde(default)]
                campaign_fleet: Option<Vec<CampaignFleetShipOutcome>>,
                #[serde(default)]
                campaign_squads: Option<Vec<super::CampaignSquadOutcome>>,
                #[serde(default)]
                campaign_banked_minerals: Option<u64>,
            }

            let bytes = match fs::read(directory.join("rollout-summary.json")) {
                Ok(bytes) => bytes,
                Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(()),
                Err(error) => return Err(error),
            };
            // Older interrupted runs may have a partial summary. Only a
            // complete, readable campaign outcome can grant progress.
            let Ok(Summary {
                campaign_outcome: Some(outcome), campaign_fleet,
                campaign_squads, campaign_banked_minerals,
            }) = serde_json::from_slice(&bytes) else { return Ok(()) };
            if outcome.campaign_id != *campaign { return Ok(()); }
            if let Some(mission) = self.battles.iter().filter_map(|entry| entry.mission.as_ref())
                .find(|mission| mission.id == outcome.mission_id && mission.player_side == outcome.player_side)
            {
                let state = progress.missions.entry(&mission.id).or_default();
                state.latest_victory = outcome.winner_side == mission.player_side;
                state.completed |= matches!(mission.advance_on, AdvanceOn::Finished) || state.latest_victory;
                if mission.bank_player_minerals {
                    if let Some(minerals) = campaign_banked_minerals {
                        progress.banked_minerals = progress.banked_minerals
                            .checked_add(minerals)
                            .ok_or_else(|| invalid("campaign banked mineral overflow"))?;
                    } else {
                        progress.bank_history_complete = false;
                    }
                } else if campaign_banked_minerals.is_some_and(|minerals| minerals > 0) {
                    return Err(invalid("campaign minerals were banked without mission authorization"));
                }
                if let Some(squads) = campaign_squads.filter(|squads| {
                    let mut seen = HashSet::new();
                    squads.iter().all(|squad| {
                        squad.saved_squad_id > 0 && squad.side > 0 &&
                        squad.ships_deployed > 0 && squad.ships_lost <= squad.ships_deployed &&
                        seen.insert(squad.saved_squad_id)
                    })
                }) {
                    for squad in squads {
                        let career = progress.squads.entry(squad.saved_squad_id).or_insert_with(||
                            SquadProgress { side: squad.side, ..SquadProgress::default() }
                        );
                        if career.side != squad.side {
                            return Err(invalid(format!(
                                "SavedSquad {} changed sides between campaigns", squad.saved_squad_id,
                            )));
                        }
                        let sum = |left: u64, right: u64| left.checked_add(right)
                            .ok_or_else(|| invalid("campaign squad career count overflow"));
                        career.battles_fought = sum(career.battles_fought, 1)?;
                        career.battles_won = sum(career.battles_won,
                            u64::from(outcome.winner_side == squad.side))?;
                        career.ships_lost = sum(career.ships_lost, squad.ships_lost)?;
                        career.damage_done = sum(career.damage_done, squad.damage_done)?;
                        career.damage_received = sum(career.damage_received, squad.damage_received)?;
                        career.kills = sum(career.kills, squad.kills)?;
                    }
                } else {
                    progress.battles_without_squad_data += 1;
                }
                if let Some(ships) = campaign_fleet.filter(|ships| {
                    let mut seen = HashSet::new();
                    ships.iter().all(|ship| ship.fleet_ship_id > 0 &&
                        ship.side > 0 && ship.minerals_mined_this_level >= 0 &&
                        seen.insert(ship.fleet_ship_id))
                }) {
                    for ship in ships {
                        let fleet = progress.fleet.entry(ship.fleet_ship_id).or_insert_with(|| {
                            FleetProgress { side: ship.side,
                                loss_history_complete: true,
                                combat_history_complete: true,
                                ..FleetProgress::default() }
                        });
                        if let Some(stats) = ship.combat {
                            let add = |left: u64, right: u64| left.checked_add(right)
                                .ok_or_else(|| invalid("campaign combat counter overflow"));
                            fleet.combat.observed_turret_shots =
                                add(fleet.combat.observed_turret_shots, stats.observed_turret_shots)?;
                            fleet.combat.enemy_tsv_damage_done =
                                add(fleet.combat.enemy_tsv_damage_done, stats.enemy_tsv_damage_done)?;
                            fleet.combat.tsv_damage_received =
                                add(fleet.combat.tsv_damage_received, stats.tsv_damage_received)?;
                            fleet.combat.attributed_enemy_kills =
                                add(fleet.combat.attributed_enemy_kills, stats.attributed_enemy_kills)?;
                        } else {
                            fleet.combat_history_complete = false;
                        }
                        if let Some(dead) = ship.is_dead {
                            fleet.is_dead |= dead;
                        } else {
                            fleet.loss_history_complete = false;
                        }
                        if fleet.side != ship.side {
                            return Err(invalid(format!(
                                "FleetShip {} changed sides between completed campaign battles",
                                ship.fleet_ship_id,
                            )));
                        }
                        fleet.battles_fought = fleet.battles_fought.checked_add(1)
                            .ok_or_else(|| invalid("campaign fleet battle count overflow"))?;
                        if outcome.winner_side == ship.side {
                            fleet.battles_won = fleet.battles_won.checked_add(1)
                                .ok_or_else(|| invalid("campaign fleet victory count overflow"))?;
                        }
                        fleet.minerals_mined = fleet.minerals_mined
                            .checked_add(ship.minerals_mined_this_level as u64)
                            .ok_or_else(|| invalid("campaign fleet mineral count overflow"))?;
                    }
                } else {
                    // Pre-fleet-receipt runs still unlock missions, but do
                    // not invent fleet statistics for unavailable history.
                    progress.battles_without_fleet_data += 1;
                }
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

    /// Read-only, complete campaign view for the game menu and external
    /// tooling. No sidecar save file is ever more authoritative than the
    /// original successful battle summaries.
    pub fn status(&self, progress: &CampaignProgress<'_>) -> Value {
        let missions = self.battles.iter().filter_map(|entry| {
            let mission = entry.mission.as_ref()?;
            Some(json!({
                "id": mission.id,
                "title": entry.title,
                "unlocked": mission.requires.iter().all(|id| progress.completed(id)),
                "completed": progress.completed(&mission.id),
                "latest_victory": progress.latest_victory(&mission.id),
                "record_fleet_stats": mission.record_fleet_stats,
                "bank_player_minerals": mission.bank_player_minerals,
                "persist_fleet_losses": mission.persist_fleet_losses,
            }))
        }).collect::<Vec<_>>();
        let mut ids = progress.fleet.keys().copied().collect::<Vec<_>>();
        ids.sort_unstable();
        let fleet = ids.into_iter().map(|id| {
            let record = &progress.fleet[&id];
            json!({
                "fleet_ship_id": id,
                "side": record.side,
                "battles_fought": record.battles_fought,
                "battles_won": record.battles_won,
                "minerals_mined": record.minerals_mined,
                "is_dead": record.is_dead,
                "loss_history_complete": record.loss_history_complete,
                "observed_turret_shots": record.combat.observed_turret_shots,
                "enemy_tsv_damage_done": record.combat.enemy_tsv_damage_done,
                "tsv_damage_received": record.combat.tsv_damage_received,
                "attributed_enemy_kills": record.combat.attributed_enemy_kills,
                "combat_history_complete": record.combat_history_complete,
            })
        }).collect::<Vec<_>>();
        let mut squad_ids = progress.squads.keys().copied().collect::<Vec<_>>();
        squad_ids.sort_unstable();
        let squads = squad_ids.into_iter().map(|id| {
            let row = &progress.squads[&id];
            json!({
                "saved_squad_id": id,
                "side": row.side,
                "battles_fought": row.battles_fought,
                "battles_won": row.battles_won,
                "ships_lost": row.ships_lost,
                "damage_done": row.damage_done,
                "damage_received": row.damage_received,
                "kills": row.kills,
            })
        }).collect::<Vec<_>>();
        json!({
            "schema_version": 1,
            "kind": "native_campaign_status",
            "campaign_id": self.campaign_id,
            "missions": missions,
            "fleet": fleet,
            "squads": squads,
            "battles_without_fleet_data": progress.battles_without_fleet_data,
            "battles_without_squad_data": progress.battles_without_squad_data,
            "banked_minerals": progress.banked_minerals,
            "bank_history_complete": progress.bank_history_complete,
        })
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
