//! Authored battle lists and progress derived from completed runs.

use std::collections::{HashMap, HashSet};
use std::fs::{self, TryLockError};
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use bees_host::unity_scene_snapshot::{NativePurchasedShip, NativeCreatedSquad, NativeSavedSquadTactics};
use serde_json::{json, Value};

use super::{invalid, CampaignOutcome, CampaignFleetShipOutcome};
use bees_host::fleet_inventory::NativeFleetCombatStats;

/// Exclusive campaign owner for the native menu, headless missions and
/// all fleet edits. A stable OS lock survives as a pathname, but ownership
/// automatically releases when the process dies. Never unlink the lock inode:
/// a second writer could otherwise lock a replacement inode concurrently.
///
/// A legacy PID-file owner is not assumed dead. Its old filename remains a
/// permanent marker, so pre-migration binaries cannot create a parallel
/// campaign writer while the OS-locked native host owns the career ledger.
pub struct CampaignWriterLease {
    _file: fs::File,
}

impl CampaignWriterLease {
    pub fn acquire(root: &Path) -> io::Result<Self> {
        fs::create_dir_all(root)?;
        let lock_path = root.join("campaign-writer.lease");
        match fs::symlink_metadata(&lock_path) {
            Ok(metadata) if !metadata.file_type().is_file() =>
                return Err(invalid("campaign writer lease must be a regular file")),
            Ok(_) => {},
            Err(error) if error.kind() == io::ErrorKind::NotFound => {},
            Err(error) => return Err(error),
        }
        let file = fs::OpenOptions::new().read(true).write(true).create(true)
            .open(&lock_path)?;
        match file.try_lock() {
            Ok(()) => {},
            Err(TryLockError::WouldBlock) =>
                return Err(invalid(format!(
                    "native campaign writer is already active: {}", root.display(),
                ))),
            Err(TryLockError::Error(error)) => return Err(error),
        }
        let marker_path = root.join("campaign-writer.lock");
        const OWNER: &str = "native-os-campaign-writer-v1\n";
        match fs::symlink_metadata(&marker_path) {
            Ok(metadata) if !metadata.file_type().is_file() =>
                return Err(invalid("campaign writer marker must be a regular file")),
            Ok(_) => {
                let mut marker = String::new();
                fs::File::open(&marker_path)?.take(64).read_to_string(&mut marker)?;
                if marker != OWNER {
                    return Err(invalid(format!(
                        "legacy campaign writer PID marker at {} requires confirmation that its original process has stopped before one-time removal",
                        marker_path.display(),
                    )));
                }
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                let mut marker = fs::OpenOptions::new().write(true).create_new(true)
                    .open(&marker_path)?;
                marker.write_all(OWNER.as_bytes())?;
                marker.sync_all()?;
                #[cfg(unix)]
                fs::File::open(root)?.sync_all()?;
            }
            Err(error) => return Err(error),
        }
        Ok(Self { _file: file })
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct BattleEntry {
    pub title: String,
    pub config: PathBuf,
    pub mission: Option<Mission>,
}

impl BattleEntry {
    pub fn configurations(&self) -> impl Iterator<Item = &Path> {
        std::iter::once(self.config.as_path()).chain(
            self.mission.iter().flat_map(|mission|
                mission.on_victory.iter().chain(mission.on_defeat.iter())
                    .map(|route| route.config.as_path())),
        )
    }

    pub fn config_for(&self, progress: &CampaignProgress<'_>) -> &Path {
        let Some(mission) = &self.mission else { return self.config.as_path() };
        if let Some(route) = mission.on_victory.as_ref()
            .filter(|route| progress.latest_victory(&route.mission)) {
            return route.config.as_path();
        }
        mission.on_defeat.as_ref().filter(|route|
            progress.latest_defeat(&route.mission)
        ).map_or(self.config.as_path(), |route| route.config.as_path())
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
    /// Source Neptune Of Production credits all surviving FleetShip mined
    /// TSV even if the last active ship retreats and the encounter ends.
    /// Default banking remains victory-only for existing authored games.
    #[serde(default)]
    pub bank_on_finished: bool,
    /// Opt-in historical FleetShip deaths on the next authored Level.
    /// Only ships belonging to the player side are removed at setup;
    /// opposing authored forces remain owned by their scenario.
    #[serde(default)]
    pub persist_fleet_losses: bool,
    /// Opt-in original Bee career continuity. Only real source FleetShip
    /// IDs already retired in committed campaign encounters are omitted;
    /// fresh generated reinforcement hulls remain unrelated.
    #[serde(default)]
    pub persist_enemy_fleet_losses: bool,
    /// Source mission-ending action that permanently sacrifices every
    /// currently owned FleetShip of the listed classes on defeat,
    /// including ships in reserve rather than deployed in the last Level.
    #[serde(default)]
    pub retire_on_defeat_types: Vec<u8>,
    #[serde(default)]
    pub requires: Vec<String>,
    /// Optional alternative source campaign routes. Each prerequisite is
    /// a prior committed mission outcome; at least one must match. This
    /// supports original defeat-skip branches without fabricating a win
    /// or marking a skipped Level as played.
    #[serde(default)]
    unlock_if_any: Vec<OutcomePrerequisite>,
    /// One-time physical FleetShip rewards for the first verified victory.
    /// Type/TSV and SavedSquad must be supported by the same authored
    /// native scenes as ordinary purchases. No currency is spent.
    #[serde(default)]
    pub victory_grants: Vec<VictoryShipGrant>,
    /// New persistent SavedSquads earned once on a victorious mission.
    /// The existing native squad installer clones the authored player
    /// command template; these are never live during the awarding battle.
    #[serde(default)]
    pub victory_squads: Vec<VictorySquadGrant>,
    #[serde(default)]
    advance_on: AdvanceOn,
    /// Source campaign endings such as Pluto 3 can award ships after a
    /// completed defeat. The default remains the original victory gate.
    #[serde(default)]
    grant_on: AdvanceOn,
    /// Source Pluto IV evacuation pays a varying subset of its authored
    /// maximum fleet rewards, derived from the *real* survival clock.
    #[serde(default)]
    pub evacuation_rewards: bool,
    on_victory: Option<MissionRoute>,
    on_defeat: Option<MissionRoute>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct VictoryShipGrant {
    pub ship_type: u8,
    pub count: u32,
    pub saved_squad_id: i64,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct VictorySquadGrant {
    pub saved_squad_id: i64,
    pub template_saved_squad_id: i64,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct MissionRoute {
    mission: String,
    config: PathBuf,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct OutcomePrerequisite {
    mission: String,
    result: RequiredOutcome,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "snake_case")]
enum RequiredOutcome {
    Finished,
    Victory,
    Defeat,
}

impl OutcomePrerequisite {
    fn satisfied(&self, progress: &CampaignProgress<'_>) -> bool {
        match self.result {
            RequiredOutcome::Finished => progress.completed(&self.mission),
            RequiredOutcome::Victory => progress.latest_victory(&self.mission),
            RequiredOutcome::Defeat => progress.latest_defeat(&self.mission),
        }
    }
}

/// Pluto IV / Bluer Pastures: actual source reward tiers, based on the
/// number evacuated before the physical HumanTarget is destroyed or
/// the 300.49-second survival clock expires. Types come from Unity's
/// ConfigData.ShipTypes: Scout, Gunship, Frigate, Dreadnought.
pub(crate) fn pluto_evacuation_reward_count(ship_type: u8, evacuated: u32) -> u32 {
    match ship_type {
        18 => 5,
        13 => if evacuated == 60 { 8 } else if evacuated > 50 { 6 }
              else if evacuated > 15 { 4 } else { 1 },
        12 => if evacuated == 60 { 4 } else if evacuated > 35 { 2 } else { 0 },
        7 => if evacuated > 50 { 2 } else { 0 },
        _ => 0,
    }
}

impl Mission {
    pub fn has_outcome_alternatives(&self) -> bool {
        !self.unlock_if_any.is_empty()
    }

    pub fn grants_on_finished(&self) -> bool {
        matches!(self.grant_on, AdvanceOn::Finished)
    }

    pub fn advances_on_finished(&self) -> bool {
        matches!(self.advance_on, AdvanceOn::Finished)
    }

    pub fn unlocked(&self, progress: &CampaignProgress<'_>) -> bool {
        self.requires.iter().all(|id| progress.completed(id)) &&
            (self.unlock_if_any.is_empty() ||
                self.unlock_if_any.iter().any(|route| route.satisfied(progress)))
    }
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
    /// An explicitly authored ship shop. Prices are the source
    /// ConfigData.GetShipInfo(type).Tsv and must match the canonical
    /// full-health ship prototype on every native campaign scene.
    #[serde(default)]
    pub buildable_ships: Vec<BuildableShip>,
    /// Unity Configuration.MaxSquadSize for this authored campaign.
    /// Required for a shop: buying ships must not bypass the saved-squad
    /// capacity limit or change the canonical game roster contract.
    #[serde(default)]
    pub max_squad_ships: Option<u32>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct BuildableShip {
    pub player_side: i32,
    pub ship_type: u8,
    /// Canonical authored MaxTSV, required for every purchased or
    /// awarded FleetShip. A victory grant never debits this price.
    pub price_tsv: u64,
    /// Some source classes can only be earned in a mission. They still
    /// use this same class/scene contract, but never appear in the shop.
    #[serde(default = "default_purchasable")]
    pub purchasable: bool,
    /// Campaign prerequisites for purchasing this class.
    #[serde(default)]
    pub unlocked_after: Vec<String>,
}

fn default_purchasable() -> bool { true }

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct CampaignPurchase {
    campaign_id: String,
    player_side: i32,
    ship: NativePurchasedShip,
}

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct CampaignSquadCreation {
    campaign_id: String,
    squad: NativeCreatedSquad,
}

/// Tombstone for a user-created SavedSquad with no live fleet assignments.
/// Historical battle receipts and permanent IDs remain intact.
#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct CampaignSquadRetirement {
    campaign_id: String,
    side: i32,
    saved_squad_id: i64,
}

/// One immutable tactical edit. Later numbered edits supersede earlier
/// preferences without rewriting the receipt for a prior campaign battle.
#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct CampaignTacticsEdit {
    campaign_id: String,
    tactics: NativeSavedSquadTactics,
}

/// Rename the durable squad identity, not the transient Stage SquadLifetime.
/// Career names survive rematches and do not enter the physical game digest.
#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct CampaignSquadNameEdit {
    campaign_id: String,
    side: i32,
    saved_squad_id: i64,
    name: String,
}

fn valid_saved_squad_name(name: &str) -> bool {
    (1..=32).contains(&name.len()) &&
    name.trim() == name &&
    name.bytes().all(|byte|
        byte.is_ascii_alphanumeric() || matches!(byte, b' ' | b'-' | b'_'))
}

/// Reassignment changes only who commands a purchased persistent hull.
#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct CampaignTransfer {
    campaign_id: String,
    player_side: i32,
    fleet_ship_id: i64,
    from_saved_squad_id: i64,
    to_saved_squad_id: i64,
}

/// An original authored FleetShip is owned independently of which
/// saved squad currently commands it. Original ownership is recorded
/// once; subsequent transfers replay through the same durable ledger.
#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct CampaignAuthoredAssignment {
    campaign_id: String,
    player_side: i32,
    fleet_ship_id: i64,
    from_saved_squad_id: i64,
    to_saved_squad_id: i64,
}

#[derive(Clone, Copy)]
struct AuthoredRosterOwner {
    side: i32,
    initial_saved_squad_id: i64,
    current_saved_squad_id: i64,
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
    spent_minerals: u64,
    purchases: Vec<(i32, NativePurchasedShip)>,
    /// Awarded FleetShips share the ordinary deployment/transfer path
    /// with purchases, but never debit the source mineral account.
    awarded_fleet_ids: HashSet<i64>,
    awarded_saved_squad_ids: HashSet<i64>,
    created_squads: Vec<NativeCreatedSquad>,
    retired_squad_ids: HashSet<i64>,
    squad_names: HashMap<i64, String>,
    tactics: Vec<NativeSavedSquadTactics>,
    tactics_edits: u64,
    transfer_count: u64,
    authored_roster: HashMap<i64, AuthoredRosterOwner>,
    authored_transfer_count: u64,
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
    special_history_complete: bool,
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
    attempted: bool,
    latest_victory: bool,
    grants_claimed: bool,
    evacuated_personnel: Option<u32>,
}

impl CampaignProgress<'_> {
    pub fn completed(&self, id: &str) -> bool {
        self.missions.get(id).is_some_and(|mission| mission.completed)
    }

    fn latest_victory(&self, id: &str) -> bool {
        self.missions.get(id).is_some_and(|mission| mission.attempted && mission.latest_victory)
    }

    fn latest_defeat(&self, id: &str) -> bool {
        self.missions.get(id).is_some_and(|mission| mission.attempted && !mission.latest_victory)
    }

    pub fn grants_claimed(&self, id: &str) -> bool {
        self.missions.get(id).is_some_and(|mission| mission.grants_claimed)
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

    pub fn unspent_minerals(&self) -> io::Result<u64> {
        self.banked_minerals.checked_sub(self.spent_minerals)
            .ok_or_else(|| invalid("campaign purchases exceed banked minerals"))
    }

    pub fn bank_history_complete(&self) -> bool {
        self.bank_history_complete
    }

    pub fn is_ship_unlocked(
        &self, shop: &BuildableShip,
    ) -> bool {
        shop.purchasable &&
            shop.unlocked_after.iter().all(|mission| self.completed(mission))
    }

    pub fn highest_fleet_id(&self) -> i64 {
        self.fleet.keys().copied().max().unwrap_or(0)
    }

    pub fn original_fleet_is_editable(&self, side: i32, fleet_id: i64) -> bool {
        // A fresh account has no battle receipts yet; the source scene
        // still owns its original player FleetShips. Never require a
        // fictitious played battle just to enable its fleet editor.
        self.battles_without_fleet_data == 0 &&
        self.fleet.get(&fleet_id)
            .is_none_or(|record| record.side == side && !record.is_dead &&
                record.loss_history_complete) &&
        self.purchased_ship(fleet_id).is_none()
    }

    pub fn purchased_ship(&self, fleet_ship_id: i64) -> Option<(i32, NativePurchasedShip)> {
        self.purchases.iter().copied()
            .find(|(_, ship)| ship.fleet_ship_id == fleet_ship_id)
    }

    pub fn purchases_for_side(&self, side: i32) -> Vec<NativePurchasedShip> {
        self.purchases.iter().filter_map(|&(owner, ship)|
            (owner == side).then_some(ship)).collect()
    }

    fn authored_roster_for_all(&self) -> Vec<Value> {
        let mut ids = self.authored_roster.keys().copied().collect::<Vec<_>>();
        ids.sort_unstable();
        ids.into_iter().map(|id| {
            let owner = &self.authored_roster[&id];
            json!({
                "fleet_ship_id": id,
                "player_side": owner.side,
                "initial_saved_squad_id": owner.initial_saved_squad_id,
                "saved_squad_id": owner.current_saved_squad_id,
                "in_reserve": owner.current_saved_squad_id == 0,
            })
        }).collect()
    }

    pub fn authored_roster_for_side(&self, side: i32) -> Vec<(i64, i64, i64)> {
        let mut rows = self.authored_roster.iter().filter_map(|(&fleet_id, owner)|
            (owner.side == side).then_some((
                fleet_id, owner.initial_saved_squad_id, owner.current_saved_squad_id,
            ))).collect::<Vec<_>>();
        rows.sort_unstable_by_key(|row| row.0);
        rows
    }

    pub fn authored_current_squad(&self, fleet_id: i64) -> Option<i64> {
        self.authored_roster.get(&fleet_id).map(|owner| owner.current_saved_squad_id)
    }

    /// Original authored squads cannot be deleted. A generated squad may
    /// be retired after its real owned FleetShips have returned to reserve,
    /// preserving their identities and all historical career statistics.
    pub fn can_retire_saved_squad(&self, side: i32, id: i64) -> bool {
        matches!(side, 1 | 2) && id > 0 &&
        self.created_squads.iter().any(|squad|
            squad.side == side && squad.saved_squad_id == id) &&
        !self.retired_squad_ids.contains(&id) &&
        !self.created_squads.iter().any(|squad|
            squad.template_saved_squad_id == id)
    }

    /// Historical career rows may retain retired SavedSquad identities.
    /// Never accept a newly published physical assignment or preference
    /// for an identity with an immutable retirement tombstone.
    pub fn is_retired_saved_squad(&self, saved_squad_id: i64) -> bool {
        self.retired_squad_ids.contains(&saved_squad_id)
    }

    pub fn squad_name(&self, saved_squad_id: i64) -> Option<&str> {
        self.squad_names.get(&saved_squad_id).map(String::as_str)
    }

    pub fn squad_label(&self, saved_squad_id: i64) -> String {
        let label = format!("Squad #{saved_squad_id}");
        match self.squad_name(saved_squad_id) {
            Some(name) => format!("{label} ({name})"),
            None => label,
        }
    }

    pub fn created_squads_for_side(&self, side: i32) -> Vec<NativeCreatedSquad> {
        self.created_squads.iter().copied().filter(|squad|
            squad.side == side).collect()
    }

    /// One effective edit per SavedSquad, in stable source identity order.
    pub fn tactics_for_side(&self, side: i32) -> Vec<NativeSavedSquadTactics> {
        let mut entries = self.tactics.iter().copied()
            .filter(|entry| entry.side == side).collect::<Vec<_>>();
        entries.sort_unstable_by_key(|entry| entry.saved_squad_id);
        entries
    }

    pub fn tactics_for_squad(&self, side: i32, id: i64) -> Option<NativeSavedSquadTactics> {
        self.tactics.iter().copied().find(|entry|
            entry.side == side && entry.saved_squad_id == id)
    }

    pub fn highest_saved_squad_id(&self) -> i64 {
        self.squads.keys().copied().chain(
            self.created_squads.iter().map(|squad| squad.saved_squad_id)
        ).chain(self.retired_squad_ids.iter().copied())
            .max().unwrap_or(0)
    }

    pub fn saved_squad_ids_for_side(&self, side: i32) -> Vec<i64> {
        let mut ids = self.squads.iter().filter_map(|(&id, squad)|
            (squad.side == side).then_some(id))
            .chain(self.created_squads.iter().filter_map(|squad|
                (squad.side == side).then_some(squad.saved_squad_id)))
            .collect::<Vec<_>>();
        ids.retain(|id| !self.retired_squad_ids.contains(id));
        ids.sort_unstable();
        ids.dedup();
        ids
    }

    pub fn squad_rows(&self) -> Vec<String> {
        let mut ids = self.squads.keys().copied().chain(
            self.created_squads.iter().map(|squad| squad.saved_squad_id)
        ).collect::<Vec<_>>();
        ids.sort_unstable();
        ids.dedup();
        ids.into_iter().map(|id| {
            let label = match self.squad_name(id) {
                Some(name) => format!("Squad #{id} {name}"),
                None => format!("Squad #{id}"),
            };
            if let Some(squad) = self.squads.get(&id) {
                format!(
                    "{label} S{} W{}/{} Lost{} Hit{} Taken{} Kill{}",
                    squad.side, squad.battles_won, squad.battles_fought,
                    squad.ships_lost, squad.damage_done, squad.damage_received,
                    squad.kills,
                ) + if self.retired_squad_ids.contains(&id) {
                    " [Deleted]"
                } else { "" }
            } else {
                let side = self.created_squads.iter().find(|squad|
                    squad.saved_squad_id == id).map_or(0, |squad| squad.side);
                format!("{label} S{side}  Created - no completed battles yet")
            }
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
    pub fn reserved_saved_squad_id_ceiling(&self) -> i64 {
        self.battles.iter().filter_map(|entry| entry.mission.as_ref())
            .flat_map(|mission| &mission.victory_squads)
            .map(|grant| grant.saved_squad_id)
            .max().unwrap_or(0)
    }

    pub fn validate(&self) -> io::Result<()> {
        if self.battles.is_empty() || self.battles.iter().any(|entry|
            entry.title.trim().is_empty() || entry.config.as_os_str().is_empty())
        {
            return Err(invalid("The battle catalog needs at least one battle, each with a title and run configuration"));
        }
        if self.campaign_id.as_ref().is_some_and(|id| id.trim().is_empty()) {
            return Err(invalid("campaign_id must not be blank"));
        }
        if let Some(first) = self.buildable_ships.first() {
            if self.buildable_ships.iter().any(|ship|
                ship.player_side != first.player_side) ||
                self.battles.iter().filter_map(|entry| entry.mission.as_ref())
                    .any(|mission| mission.player_side != first.player_side)
            {
                return Err(invalid(
                    "the campaign ship bank and shop must belong to one player faction",
                ));
            }
        }
        if !self.buildable_ships.is_empty() &&
            !self.max_squad_ships.is_some_and(|n| (1..=1024).contains(&n))
        {
            return Err(invalid("a native campaign ship shop requires the authored maximum SavedSquad size"));
        }
        let mut available = HashSet::new();
        for ship in &self.buildable_ships {
            if self.campaign_id.is_none() ||
                !matches!(ship.player_side, 1 | 2) ||
                ship.price_tsv == 0 || ship.ship_type >= 32 ||
                !available.insert((ship.player_side, ship.ship_type)) ||
                !self.battles.iter().any(|entry| entry.mission.as_ref()
                    .is_some_and(|mission| mission.player_side == ship.player_side)) ||
                (!ship.purchasable && !ship.unlocked_after.is_empty()) ||
                ship.unlocked_after.iter().any(|required|
                    !self.battles.iter().any(|entry| entry.mission.as_ref()
                        .is_some_and(|mission| mission.player_side == ship.player_side &&
                            mission.id == *required))) ||
                self.battles.iter().filter_map(|entry| entry.mission.as_ref())
                    .any(|mission| mission.player_side == ship.player_side &&
                        (!mission.record_fleet_stats || !mission.persist_fleet_losses))
            {
                return Err(invalid(
                    "native campaign ship shops require unique source prices and persistent casualty receipts on every mission",
                ));
            }
        }
        let mut earlier = HashSet::new();
        let mut advances_on_finish = HashSet::new();
        let mut awarded_squad_ids = HashSet::new();
        for mission in self.battles.iter().filter_map(|entry| entry.mission.as_ref()) {
            if self.campaign_id.is_none() || mission.id.trim().is_empty() ||
                !matches!(mission.player_side, 1 | 2) ||
                ((mission.persist_fleet_losses || mission.bank_player_minerals) &&
                    !mission.record_fleet_stats) ||
                (mission.bank_on_finished &&
                    (!mission.bank_player_minerals ||
                        !matches!(mission.advance_on, AdvanceOn::Finished))) ||
                (mission.persist_enemy_fleet_losses &&
                    (!mission.persist_fleet_losses || !mission.record_fleet_stats)) ||
                (mission.grants_on_finished() &&
                    !matches!(mission.advance_on, AdvanceOn::Finished)) ||
                (mission.evacuation_rewards && !mission.grants_on_finished()) ||
                (mission.evacuation_rewards && {
                    let classes = [(18, 5), (13, 8), (12, 4), (7, 2)];
                    classes.iter().any(|&(kind, count)|
                        mission.victory_grants.iter()
                            .filter(|grant| grant.ship_type == kind)
                            .map(|grant| grant.count).sum::<u32>() != count) ||
                    mission.victory_grants.iter().any(|grant|
                        !classes.iter().any(|&(kind, _)| kind == grant.ship_type))
                }) ||
                earlier.contains(mission.id.as_str()) ||
                mission.requires.iter().any(|id| !earlier.contains(id.as_str())) ||
                mission.unlock_if_any.len() > 40 ||
                (!mission.retire_on_defeat_types.is_empty() &&
                    (!mission.record_fleet_stats || !mission.persist_fleet_losses)) ||
                mission.retire_on_defeat_types.iter().any(|&kind| kind >= 32) ||
                mission.retire_on_defeat_types.iter().enumerate().any(|(index, kind)|
                    mission.retire_on_defeat_types[..index].contains(kind)) ||
                mission.unlock_if_any.iter().any(|route|
                    !earlier.contains(route.mission.as_str())) ||
                mission.unlock_if_any.iter().enumerate().any(|(index, route)|
                    mission.unlock_if_any[..index].iter().any(|earlier|
                        earlier.mission == route.mission && earlier.result == route.result))
            {
                return Err(invalid(format!(
                    "Mission {:?} needs a campaign ID, unique mission ID, player side 1 or 2, and prerequisites listed earlier in the catalog",
                    mission.id,
                )));
            }
            if !mission.victory_squads.is_empty() && (
                !mission.record_fleet_stats || !mission.persist_fleet_losses ||
                !self.max_squad_ships.is_some_and(|n| (1..=1024).contains(&n)) ||
                mission.victory_squads.len() > 64 ||
                mission.victory_squads.iter().any(|grant|
                    grant.saved_squad_id <= 0 ||
                    grant.saved_squad_id == i64::MAX ||
                    grant.template_saved_squad_id <= 0 ||
                    grant.template_saved_squad_id == i64::MAX ||
                    grant.saved_squad_id == grant.template_saved_squad_id ||
                    !awarded_squad_ids.insert(grant.saved_squad_id))
            ) {
                return Err(invalid(format!(
                    "Mission {:?} requires unique valid new persistent SavedSquads and fleet history",
                    mission.id,
                )));
            }
            if !mission.victory_grants.is_empty() && (
                !self.max_squad_ships.is_some_and(|n| (1..=1024).contains(&n)) ||
                !mission.record_fleet_stats || !mission.persist_fleet_losses ||
                mission.victory_grants.iter().any(|grant|
                    grant.count == 0 || grant.count > 1024 ||
                    // Zero is an unassigned permanent FleetShip in
                    // the source account, not an instantiated game squad.
                    // Players may later assign it using the existing
                    // authoritative purchased-hull transfer transaction.
                    grant.saved_squad_id < 0 ||
                    !self.buildable_ships.iter().any(|shop|
                        shop.player_side == mission.player_side &&
                        shop.ship_type == grant.ship_type)) ||
                mission.victory_grants.iter().map(|grant| grant.count as usize)
                    .sum::<usize>() > 1024
            ) {
                return Err(invalid(format!(
                    "Mission {:?} needs source-priced ship rewards, persistent fleet losses and a SavedSquad cap",
                    mission.id,
                )));
            }
            // Conditional scenarios must have a realizable path for their
            // named outcome. A defeat-only gate cannot activate a victory
            // scenario, and a victory-only advancement cannot unlock a
            // first-attempt defeat. No separate campaign state is needed.
            let route_reachable = |route: &MissionRoute, victory: bool| {
                let prior_finishes = advances_on_finish.contains(route.mission.as_str());
                let required = mission.requires.contains(&route.mission) &&
                    (victory || prior_finishes);
                let alternative = mission.unlock_if_any.iter().any(|condition|
                    condition.mission == route.mission &&
                    match condition.result {
                        RequiredOutcome::Victory => victory,
                        RequiredOutcome::Defeat => !victory,
                        RequiredOutcome::Finished => victory || prior_finishes,
                    });
                !route.config.as_os_str().is_empty() && (required || alternative)
            };
            if mission.on_victory.as_ref().is_some_and(|route|
                !route_reachable(route, true)) ||
                mission.on_defeat.as_ref().is_some_and(|route|
                    !route_reachable(route, false)) ||
                mission.on_victory.as_ref().zip(mission.on_defeat.as_ref())
                    .is_some_and(|(win, defeat)| win.mission != defeat.mission) {
                return Err(invalid(format!(
                    "Mission {:?} requires a reachable previous outcome and a native run for its conditional route",
                    mission.id,
                )));
            }
            earlier.insert(mission.id.as_str());
            if matches!(mission.advance_on, AdvanceOn::Finished) {
                advances_on_finish.insert(mission.id.as_str());
            }
        }
        Ok(())
    }

    /// Commit one immutable purchase after resource and native asset
    /// validation. The newly allocated directory is a transaction slot;
    /// only an atomic published receipt changes the account balance.
    /// Interrupted writes leave no spendable or playable purchased hull.
    pub fn buy_ship(
        &self, progress: &CampaignProgress<'_>, output: &Path,
        player_side: i32, ship_type: u8, saved_squad_id: i64,
        fleet_ship_id: i64,
    ) -> io::Result<NativePurchasedShip> {
        let campaign_id = self.campaign_id.as_deref()
            .ok_or_else(|| invalid("ship purchase requires a native campaign"))?;
        let shop = self.buildable_ships.iter().find(|entry|
            entry.player_side == player_side && entry.ship_type == ship_type &&
            entry.purchasable
        ).ok_or_else(|| invalid("requested ship type is not purchasable in this campaign shop"))?;
        if !progress.bank_history_complete ||
            !progress.is_ship_unlocked(shop) ||
            progress.unspent_minerals()? < shop.price_tsv ||
            fleet_ship_id <= 0 || fleet_ship_id <= progress.highest_fleet_id() ||
            saved_squad_id <= 0 ||
            progress.is_retired_saved_squad(saved_squad_id)
        {
            return Err(invalid("purchase requires verified bank funds, new FleetShip ID and a valid SavedSquad"));
        }
        let ship = NativePurchasedShip {
            fleet_ship_id, saved_squad_id, ship_type, price_tsv: shop.price_tsv,
        };
        let purchase = CampaignPurchase {
            campaign_id: campaign_id.to_owned(), player_side, ship,
        };
        commit_campaign_edit(output, "build-", "purchase.json", &purchase)?;
        Ok(ship)
    }

    /// A purchased ship can join a different SavedSquad between missions.
    /// Preserve its FleetShip identity, type, TSV cost and career totals;
    /// never modify a historical battle or the original purchase receipt.
    /// The caller has already validated this destination against the
    /// authoritative native Stage and all relevant mission variants.
    /// An unplayed but source-authored SavedSquad has no career row yet.
    pub fn transfer_purchased_ship(
        &self, progress: &CampaignProgress<'_>, output: &Path,
        fleet_ship_id: i64, to_saved_squad_id: i64,
    ) -> io::Result<NativePurchasedShip> {
        let campaign_id = self.campaign_id.as_deref()
            .ok_or_else(|| invalid("squad transfer requires a campaign"))?;
        let (side, ship) = progress.purchased_ship(fleet_ship_id)
            .ok_or_else(|| invalid("only previously purchased hulls can be transferred"))?;
        if to_saved_squad_id < 0 || to_saved_squad_id == ship.saved_squad_id ||
            (to_saved_squad_id > 0 &&
                progress.is_retired_saved_squad(to_saved_squad_id)) ||
            progress.fleet.get(&fleet_ship_id)
                .is_none_or(|fleet| fleet.side != side ||
                    (fleet.is_dead && to_saved_squad_id != 0))
        {
            return Err(invalid("transfer needs a live purchased hull and a different owned SavedSquad"));
        }
        commit_campaign_edit(output, "transfer-", "transfer.json", &CampaignTransfer {
            campaign_id: campaign_id.to_owned(),
            player_side: side,
            fleet_ship_id,
            from_saved_squad_id: ship.saved_squad_id,
            to_saved_squad_id,
        })?;
        Ok(NativePurchasedShip { saved_squad_id: to_saved_squad_id, ..ship })
    }

    /// One immutable career rename. Caller verifies the source squad
    /// against the selected playable missions before committing.
    pub fn name_saved_squad(
        &self, progress: &CampaignProgress<'_>, output: &Path,
        side: i32, saved_squad_id: i64, name: &str,
    ) -> io::Result<()> {
        let campaign_id = self.campaign_id.as_deref()
            .ok_or_else(|| invalid("squad naming requires a native campaign"))?;
        if !matches!(side, 1 | 2) || saved_squad_id <= 0 ||
            progress.is_retired_saved_squad(saved_squad_id) ||
            !valid_saved_squad_name(name) ||
            progress.squad_name(saved_squad_id) == Some(name) ||
            progress.squads.get(&saved_squad_id)
                .is_some_and(|record| record.side != side) ||
            progress.created_squads.iter().any(|record|
                record.saved_squad_id == saved_squad_id && record.side != side)
        {
            return Err(invalid("squad rename needs a live, owned SavedSquad and printable name"));
        }
        commit_campaign_edit(output, "squad-name-", "name.json",
            &CampaignSquadNameEdit {
                campaign_id: campaign_id.to_owned(),
                side, saved_squad_id, name: name.to_owned(),
            })
    }

    /// Append a player-owned SavedSquad without borrowing a transient
    /// Level ItemId. An empty squad remains in the campaign editor and
    /// only becomes a live SquadLifetime after a real ship joins it.
    pub fn create_saved_squad(
        &self, progress: &CampaignProgress<'_>, output: &Path,
        side: i32, template_id: i64, new_id: i64,
    ) -> io::Result<NativeCreatedSquad> {
        let campaign_id = self.campaign_id.as_deref()
            .ok_or_else(|| invalid("new SavedSquads require a native campaign"))?;
        // The caller has already verified the original player template
        // against each native scene. On a fresh campaign, the physical
        // SavedSquad does not yet have a career receipt in progress.squads.
        if !matches!(side, 1 | 2) || template_id <= 0 ||
            progress.is_retired_saved_squad(template_id) ||
            new_id <= progress.highest_saved_squad_id() ||
            new_id <= 0 || new_id == i64::MAX
        {
            return Err(invalid("new SavedSquad requires an owned template and unused persistent ID"));
        }
        let squad = NativeCreatedSquad {
            saved_squad_id: new_id, template_saved_squad_id: template_id,
            side,
        };
        commit_campaign_edit(output, "squad-", "squad.json",
            &CampaignSquadCreation {
                campaign_id: campaign_id.to_owned(), squad,
            })?;
        Ok(squad)
    }

    /// Retire a vacant user-created or earned squad without rewriting
    /// previous battles. Its stable ID remains reserved for all time.
    /// Physical ships must first be moved into another squad or reserve
    /// through the existing validated transfer/assignment operations.
    pub fn retire_saved_squad(
        &self, progress: &CampaignProgress<'_>, output: &Path,
        side: i32, saved_squad_id: i64,
    ) -> io::Result<()> {
        let campaign_id = self.campaign_id.as_deref()
            .ok_or_else(|| invalid("SavedSquad deletion requires a native campaign"))?;
        if !progress.can_retire_saved_squad(side, saved_squad_id) ||
            progress.purchases.iter().any(|(_, ship)|
                ship.saved_squad_id == saved_squad_id) ||
            progress.authored_roster.values().any(|owner|
                owner.current_saved_squad_id == saved_squad_id)
        {
            return Err(invalid("retired SavedSquad must be created, unreferenced and have all ships in reserve"));
        }
        commit_campaign_edit(output, "squad-retire-", "retirement.json",
            &CampaignSquadRetirement {
                campaign_id: campaign_id.to_owned(), side, saved_squad_id,
            })
    }

    /// Publish an authenticated, zero-cost SavedSquad tactics change.
    /// The caller has already checked every mission configuration using
    /// the common native scene installer. No completed battle is mutated.
    pub fn set_saved_squad_tactics(
        &self, progress: &CampaignProgress<'_>, output: &Path,
        entry: NativeSavedSquadTactics,
    ) -> io::Result<()> {
        let campaign_id = self.campaign_id.as_deref()
            .ok_or_else(|| invalid("saved tactics require a campaign"))?;
        if !matches!(entry.side, 1 | 2) || entry.saved_squad_id <= 0 ||
            progress.is_retired_saved_squad(entry.saved_squad_id) ||
            !(1..=40).contains(&entry.shooting_strategy_id) ||
            entry.formation.is_some_and(|formation| !formation.is_valid()) ||
            progress.tactics_for_squad(entry.side, entry.saved_squad_id) == Some(entry) ||
            progress.squads.get(&entry.saved_squad_id)
                .is_some_and(|squad| squad.side != entry.side) ||
            progress.created_squads.iter().any(|squad|
                squad.saved_squad_id == entry.saved_squad_id &&
                squad.side != entry.side)
        {
            return Err(invalid("invalid or unchanged SavedSquad tactics"));
        }
        commit_campaign_edit(output, "tactics-", "tactics.json", &CampaignTacticsEdit {
            campaign_id: campaign_id.to_owned(), tactics: entry,
        })
    }

    /// Commit an authored (pre-purchase) FleetShip reassignment after
    /// validating the actual upcoming scene with the common builder.
    /// This never changes ship ownership, combat career or TSV balance.
    pub fn reassign_authored_ship(
        &self, progress: &CampaignProgress<'_>, output: &Path,
        side: i32, fleet_ship_id: i64, from_squad: i64, to_squad: i64,
    ) -> io::Result<()> {
        let campaign_id = self.campaign_id.as_deref()
            .ok_or_else(|| invalid("authored roster changes require a campaign"))?;
        if !matches!(side, 1 | 2) || fleet_ship_id <= 0 ||
            from_squad < 0 || to_squad < 0 || from_squad == to_squad ||
            (to_squad > 0 && progress.is_retired_saved_squad(to_squad)) ||
            progress.purchased_ship(fleet_ship_id).is_some() ||
            progress.authored_current_squad(fleet_ship_id)
                .is_some_and(|saved| saved != from_squad) ||
            progress.fleet.get(&fleet_ship_id)
                .is_some_and(|fleet| fleet.side != side ||
                    (fleet.is_dead && to_squad != 0)) ||
            (to_squad != 0 &&
                progress.squads.get(&to_squad).is_some_and(|squad| squad.side != side))
        {
            return Err(invalid("invalid, unavailable or stale authored FleetShip assignment"));
        }
        commit_campaign_edit(output, "squad-edit-", "assignment.json",
            &CampaignAuthoredAssignment {
                campaign_id: campaign_id.to_owned(),
                player_side: side, fleet_ship_id,
                from_saved_squad_id: from_squad,
                to_saved_squad_id: to_squad,
            },
        )
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
                #[serde(default)]
                kind: Option<String>,
                #[serde(default)]
                failed_window: Option<Value>,
                #[serde(default)]
                cleanup_errors: Option<Vec<Value>>,
                #[serde(default)]
                user_closed: Option<bool>,
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
                kind, failed_window, cleanup_errors, user_closed,
                campaign_outcome: Some(outcome), campaign_fleet,
                campaign_squads, campaign_banked_minerals,
            }) = serde_json::from_slice(&bytes) else { return Ok(()) };
            if outcome.campaign_id != *campaign { return Ok(()); }
            // Source progress can only come from the immutable successful
            // run_final transaction, not a failed/aborted multiplayer battle
            // or a partial summary that happens to contain a winner. Keep
            // older incomplete summaries inert, but never let a conflicting
            // terminal record mint fleet rewards or banked career income.
            if kind.as_deref() != Some("run_final") ||
                failed_window.is_some() ||
                cleanup_errors.as_ref().is_some_and(|errors| !errors.is_empty()) ||
                user_closed == Some(true)
            {
                return Err(invalid("campaign result is not a successful committed native battle"));
            }
            if let Some(mission) = self.battles.iter().filter_map(|entry| entry.mission.as_ref())
                .find(|mission| mission.id == outcome.mission_id && mission.player_side == outcome.player_side)
            {
                if mission.evacuation_rewards != outcome.evacuated_personnel.is_some() ||
                    outcome.evacuated_personnel.is_some_and(|count| count > 60) {
                    return Err(invalid("campaign evacuation needs an authentic bounded personnel count"));
                }
                let state = progress.missions.entry(&mission.id).or_default();
                state.attempted = true;
                state.latest_victory = outcome.mission_victory();
                state.evacuated_personnel = outcome.evacuated_personnel;
                state.completed |= matches!(mission.advance_on, AdvanceOn::Finished) || state.latest_victory;
                // Awarded hulls and SavedSquads are one atomic campaign
                // outcome. They are reconstructed together from this
                // immutable winning battle, not from player-edit receipts.
                if !outcome.awarded_ships.is_empty() || !outcome.awarded_squads.is_empty() {
                    if (!state.latest_victory && !mission.grants_on_finished()) ||
                        state.grants_claimed {
                        return Err(invalid("campaign victory ships were granted on an ineligible or repeated result"));
                    }
                    if outcome.awarded_squads.len() != mission.victory_squads.len() {
                        return Err(invalid("campaign winning SavedSquad reward count differs from authored mission"));
                    }
                    for (received, expected) in outcome.awarded_squads.iter()
                        .zip(&mission.victory_squads)
                    {
                        if received.saved_squad_id != expected.saved_squad_id ||
                            received.template_saved_squad_id != expected.template_saved_squad_id ||
                            received.side != mission.player_side ||
                            progress.created_squads.iter().any(|squad|
                                squad.saved_squad_id == received.saved_squad_id) ||
                            progress.squads.contains_key(&received.saved_squad_id)
                        {
                            return Err(invalid("invalid, repeated or stale campaign SavedSquad reward"));
                        }
                    }
                    let mut selected = HashMap::<u8, u32>::new();
                    let expected: Vec<_> = mission.victory_grants.iter()
                        .flat_map(|grant| std::iter::repeat((
                            grant.ship_type, grant.saved_squad_id,
                        )).take(grant.count as usize))
                        .filter(|(kind, _)| {
                            if let Some(evacuated) = outcome.evacuated_personnel {
                                let count = selected.entry(*kind).or_default();
                                if *count >= pluto_evacuation_reward_count(*kind, evacuated) {
                                    return false;
                                }
                                *count += 1;
                            }
                            true
                        }).collect();
                    if expected.len() != outcome.awarded_ships.len() {
                        return Err(invalid("campaign victory ship count differs from authored reward"));
                    }
                    let mut unique = HashSet::new();
                    for (ship, (ship_type, saved_squad_id)) in
                        outcome.awarded_ships.iter().zip(expected)
                    {
                        let shop = self.buildable_ships.iter().find(|item|
                            item.player_side == mission.player_side &&
                            item.ship_type == ship_type
                        ).ok_or_else(|| invalid("campaign grant has no authored ship class"))?;
                        if ship.fleet_ship_id <= 0 || !unique.insert(ship.fleet_ship_id) ||
                            progress.fleet.contains_key(&ship.fleet_ship_id) ||
                            shop.price_tsv != ship.price_tsv ||
                            ship.ship_type != ship_type ||
                            ship.saved_squad_id != saved_squad_id
                        {
                            return Err(invalid("invalid, duplicated or stale campaign FleetShip reward"));
                        }
                    }
                    for squad in &outcome.awarded_squads {
                        progress.awarded_saved_squad_ids.insert(squad.saved_squad_id);
                        progress.created_squads.push(*squad);
                    }
                    for ship in &outcome.awarded_ships {
                        progress.fleet.insert(ship.fleet_ship_id, FleetProgress {
                            side: mission.player_side, loss_history_complete: true,
                            combat_history_complete: true, special_history_complete: true,
                            ..FleetProgress::default()
                        });
                        progress.purchases.push((mission.player_side, *ship));
                        progress.awarded_fleet_ids.insert(ship.fleet_ship_id);
                    }
                    state.grants_claimed = true;
                }
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
                                special_history_complete: true,
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
                            fleet.combat.striker_bombs_dropped =
                                add(fleet.combat.striker_bombs_dropped, stats.striker_bombs_dropped)?;
                            fleet.combat.fire_barge_explosions =
                                add(fleet.combat.fire_barge_explosions, stats.fire_barge_explosions)?;
                            fleet.combat.yellow_jacket_detonations =
                                add(fleet.combat.yellow_jacket_detonations, stats.yellow_jacket_detonations)?;
                            fleet.combat.barge_charges_started =
                                add(fleet.combat.barge_charges_started, stats.barge_charges_started)?;
                            fleet.combat.scout_beacons_deployed =
                                add(fleet.combat.scout_beacons_deployed, stats.scout_beacons_deployed)?;
                            fleet.combat.queen_minions_spawned =
                                add(fleet.combat.queen_minions_spawned, stats.queen_minions_spawned)?;
                            if !stats.special_actions_recorded {
                                fleet.special_history_complete = false;
                            }
                        } else {
                            fleet.combat_history_complete = false;
                            fleet.special_history_complete = false;
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
                // Neptune's source ending can permanently sacrifice all
                // Factories even when some were in reserve and never
                // appeared in the physical World. Read the exact retired
                // FleetShip IDs from this battle's committed terminal
                // outcome, not its current combat casualties or mutable
                // player menu. Retired ships cannot be redeployed.
                if !outcome.retired_fleet_ids.is_empty() {
                    if state.latest_victory ||
                        mission.retire_on_defeat_types.is_empty() ||
                        !mission.persist_fleet_losses ||
                        outcome.retired_fleet_ids.len() > 32768
                    {
                        return Err(invalid(
                            "unauthorized native campaign defeat FleetShip retirement",
                        ));
                    }
                    let mut seen = HashSet::new();
                    for &id in &outcome.retired_fleet_ids {
                        if id <= 0 || !seen.insert(id) {
                            return Err(invalid(
                                "invalid duplicate permanent campaign FleetShip loss",
                            ));
                        }
                        let fleet = progress.fleet.entry(id).or_insert_with(||
                            FleetProgress {
                                side: mission.player_side,
                                loss_history_complete: true,
                                combat_history_complete: true,
                                special_history_complete: true,
                                ..FleetProgress::default()
                            }
                        );
                        if fleet.side != mission.player_side {
                            return Err(invalid(
                                "campaign defeat attempted to retire another faction's FleetShip",
                            ));
                        }
                        fleet.is_dead = true;
                        fleet.loss_history_complete = true;
                    }
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
        let mut created_ids = progress.created_squads.iter()
            .map(|squad| squad.saved_squad_id).collect::<HashSet<_>>();
        for created_dir in numbered_directories(output, "squad-")? {
            let bytes = match fs::read(created_dir.join("squad.json")) {
                Ok(bytes) => bytes,
                Err(error) if error.kind() == io::ErrorKind::NotFound => continue,
                Err(error) => return Err(error),
            };
            let creation: CampaignSquadCreation = serde_json::from_slice(&bytes)
                .map_err(|error| invalid(format!("invalid SavedSquad creation: {error}")))?;
            let squad = creation.squad;
            if creation.campaign_id != *campaign ||
                !matches!(squad.side, 1 | 2) || squad.saved_squad_id <= 0 ||
                squad.saved_squad_id == i64::MAX ||
                squad.template_saved_squad_id <= 0 ||
                squad.template_saved_squad_id == squad.saved_squad_id ||
                !created_ids.insert(squad.saved_squad_id) ||
                progress.squads.get(&squad.saved_squad_id)
                    .is_some_and(|record| record.side != squad.side)
            {
                return Err(invalid("campaign created an invalid or duplicated SavedSquad"));
            }
            progress.created_squads.push(squad);
        }
        for directory in numbered_directories(output, "tactics-")? {
            let bytes = match fs::read(directory.join("tactics.json")) {
                Ok(bytes) => bytes,
                Err(error) if error.kind() == io::ErrorKind::NotFound => continue,
                Err(error) => return Err(error),
            };
            let edit: CampaignTacticsEdit = serde_json::from_slice(&bytes)
                .map_err(|error| invalid(format!("invalid SavedSquad tactics: {error}")))?;
            let entry = edit.tactics;
            if edit.campaign_id != *campaign ||
                !matches!(entry.side, 1 | 2) || entry.saved_squad_id <= 0 ||
                !(1..=40).contains(&entry.shooting_strategy_id) ||
                entry.formation.is_some_and(|formation| !formation.is_valid()) ||
                progress.tactics_for_squad(entry.side, entry.saved_squad_id) == Some(entry) ||
                progress.squads.get(&entry.saved_squad_id)
                    .is_some_and(|squad| squad.side != entry.side) ||
                progress.created_squads.iter().any(|squad|
                    squad.saved_squad_id == entry.saved_squad_id &&
                    squad.side != entry.side) ||
                progress.tactics.iter().any(|other|
                    other.saved_squad_id == entry.saved_squad_id &&
                    other.side != entry.side)
            {
                return Err(invalid("invalid, duplicate or unchanged saved squad tactics receipt"));
            }
            progress.tactics.retain(|other|
                other.saved_squad_id != entry.saved_squad_id);
            progress.tactics.push(entry);
            progress.tactics_edits = progress.tactics_edits.checked_add(1)
                .ok_or_else(|| invalid("SavedSquad tactics history overflow"))?;
        }
        let mut purchased_ids = HashSet::new();
        for build in numbered_directories(output, "build-")? {
            let bytes = match fs::read(build.join("purchase.json")) {
                Ok(bytes) => bytes,
                Err(error) if error.kind() == io::ErrorKind::NotFound => continue,
                Err(error) => return Err(error),
            };
            let purchase: CampaignPurchase = serde_json::from_slice(&bytes)
                .map_err(|error| invalid(format!("invalid campaign purchase: {error}")))?;
            if purchase.campaign_id != *campaign ||
                progress.purchases.iter().any(|(_, ship)|
                    ship.fleet_ship_id == purchase.ship.fleet_ship_id) ||
                purchase.ship.fleet_ship_id <= 0 ||
                purchase.ship.saved_squad_id <= 0 ||
                !purchased_ids.insert(purchase.ship.fleet_ship_id) ||
                !self.buildable_ships.iter().any(|shop|
                    shop.player_side == purchase.player_side &&
                    shop.ship_type == purchase.ship.ship_type &&
                    shop.price_tsv == purchase.ship.price_tsv &&
                    shop.purchasable)
            {
                return Err(invalid("campaign purchase has an unknown type, cost, or identity"));
            }
            progress.spent_minerals = progress.spent_minerals
                .checked_add(purchase.ship.price_tsv)
                .ok_or_else(|| invalid("campaign purchase total overflow"))?;
            let fleet = progress.fleet.entry(purchase.ship.fleet_ship_id)
                .or_insert_with(|| FleetProgress {
                    side: purchase.player_side,
                    loss_history_complete: true,
                    combat_history_complete: true,
                    special_history_complete: true,
                    ..FleetProgress::default()
                });
            if fleet.side != purchase.player_side {
                return Err(invalid("purchased FleetShip changed its authoritative side"));
            }
            progress.purchases.push((purchase.player_side, purchase.ship));
        }
        for transfer_dir in numbered_directories(output, "transfer-")? {
            let bytes = match fs::read(transfer_dir.join("transfer.json")) {
                Ok(bytes) => bytes,
                Err(error) if error.kind() == io::ErrorKind::NotFound => continue,
                Err(error) => return Err(error),
            };
            let transfer: CampaignTransfer = serde_json::from_slice(&bytes)
                .map_err(|error| invalid(format!("invalid saved squad transfer: {error}")))?;
            let member = progress.purchases.iter_mut().find(|(_, ship)|
                ship.fleet_ship_id == transfer.fleet_ship_id
            ).ok_or_else(|| invalid("transfer references an unpurchased FleetShip"))?;
            if transfer.campaign_id != *campaign ||
                member.0 != transfer.player_side ||
                transfer.to_saved_squad_id < 0 ||
                transfer.from_saved_squad_id == transfer.to_saved_squad_id ||
                member.1.saved_squad_id != transfer.from_saved_squad_id ||
                progress.fleet.get(&transfer.fleet_ship_id)
                    .is_some_and(|ship| ship.side != transfer.player_side) ||
                // Empty source-authored SavedSquads have no career history
                // until their first battle. The command's physical scene
                // preflight validates the destination before publishing this
                // receipt; replay must not reject it merely for lacking a
                // prior squad outcome. Still reject known foreign ownership.
                (transfer.to_saved_squad_id != 0 &&
                    (progress.squads.get(&transfer.to_saved_squad_id)
                        .is_some_and(|squad| squad.side != transfer.player_side) ||
                     progress.created_squads.iter().any(|squad|
                        squad.saved_squad_id == transfer.to_saved_squad_id &&
                        squad.side != transfer.player_side) ||
                     progress.tactics.iter().any(|tactics|
                        tactics.saved_squad_id == transfer.to_saved_squad_id &&
                        tactics.side != transfer.player_side)))
            {
                return Err(invalid("invalid or stale saved-squad FleetShip transfer receipt"));
            }
            member.1.saved_squad_id = transfer.to_saved_squad_id;
            progress.transfer_count = progress.transfer_count.checked_add(1)
                .ok_or_else(|| invalid("saved squad transfer count overflow"))?;
        }
        for assignment_dir in numbered_directories(output, "squad-edit-")? {
            let bytes = match fs::read(assignment_dir.join("assignment.json")) {
                Ok(bytes) => bytes,
                Err(error) if error.kind() == io::ErrorKind::NotFound => continue,
                Err(error) => return Err(error),
            };
            let edit: CampaignAuthoredAssignment = serde_json::from_slice(&bytes)
                .map_err(|error| invalid(format!("invalid authored fleet assignment: {error}")))?;
            if edit.campaign_id != *campaign || !matches!(edit.player_side, 1 | 2) ||
                edit.fleet_ship_id <= 0 ||
                edit.from_saved_squad_id < 0 || edit.to_saved_squad_id < 0 ||
                edit.from_saved_squad_id == edit.to_saved_squad_id ||
                progress.purchases.iter().any(|(_, ship)|
                    ship.fleet_ship_id == edit.fleet_ship_id) ||
                progress.fleet.get(&edit.fleet_ship_id)
                    .is_some_and(|ship| ship.side != edit.player_side)
            {
                return Err(invalid("invalid original FleetShip reassignment receipt"));
            }
            let owner = progress.authored_roster.entry(edit.fleet_ship_id)
                .or_insert(AuthoredRosterOwner {
                    side: edit.player_side,
                    initial_saved_squad_id: edit.from_saved_squad_id,
                    current_saved_squad_id: edit.from_saved_squad_id,
                });
            if owner.side != edit.player_side ||
                owner.current_saved_squad_id != edit.from_saved_squad_id ||
                (edit.to_saved_squad_id != 0 &&
                    progress.squads.get(&edit.to_saved_squad_id)
                        .is_some_and(|squad| squad.side != edit.player_side))
            {
                return Err(invalid("stale original FleetShip squad assignment"));
            }
            owner.current_saved_squad_id = edit.to_saved_squad_id;
            progress.authored_transfer_count = progress.authored_transfer_count.checked_add(1)
                .ok_or_else(|| invalid("authored squad edit counter overflow"))?;
        }
        // Renames are presentation-only career history, kept separately
        // from tactics, physical source offsets, and past mission receipts.
        for directory in numbered_directories(output, "squad-name-")? {
            let bytes = match fs::read(directory.join("name.json")) {
                Ok(bytes) => bytes,
                Err(error) if error.kind() == io::ErrorKind::NotFound => continue,
                Err(error) => return Err(error),
            };
            let edit: CampaignSquadNameEdit = serde_json::from_slice(&bytes)
                .map_err(|error| invalid(format!("invalid saved squad name: {error}")))?;
            if edit.campaign_id != *campaign ||
                !matches!(edit.side, 1 | 2) || edit.saved_squad_id <= 0 ||
                !valid_saved_squad_name(&edit.name) ||
                progress.squad_name(edit.saved_squad_id) == Some(edit.name.as_str()) ||
                progress.squads.get(&edit.saved_squad_id)
                    .is_some_and(|record| record.side != edit.side) ||
                progress.created_squads.iter().any(|record|
                    record.saved_squad_id == edit.saved_squad_id &&
                    record.side != edit.side)
            {
                return Err(invalid("invalid or stale saved squad name receipt"));
            }
            progress.squad_names.insert(edit.saved_squad_id, edit.name);
        }
        // Retirement is replayed after every fleet assignment. Because
        // retirement is allowed only for an empty created squad, later
        // valid transfers cannot depend on a removed identity. Keep an
        // immutable tombstone rather than making that ID reusable.
        for directory in numbered_directories(output, "squad-retire-")? {
            let bytes = match fs::read(directory.join("retirement.json")) {
                Ok(bytes) => bytes,
                Err(error) if error.kind() == io::ErrorKind::NotFound => continue,
                Err(error) => return Err(error),
            };
            let edit: CampaignSquadRetirement = serde_json::from_slice(&bytes)
                .map_err(|error| invalid(format!("invalid SavedSquad retirement: {error}")))?;
            if edit.campaign_id != *campaign ||
                !matches!(edit.side, 1 | 2) || edit.saved_squad_id <= 0 ||
                !progress.created_squads.iter().any(|squad|
                    squad.side == edit.side && squad.saved_squad_id == edit.saved_squad_id) ||
                progress.retired_squad_ids.contains(&edit.saved_squad_id) ||
                progress.created_squads.iter().any(|squad|
                    squad.template_saved_squad_id == edit.saved_squad_id) ||
                progress.purchases.iter().any(|(_, ship)|
                    ship.saved_squad_id == edit.saved_squad_id) ||
                progress.authored_roster.values().any(|owner|
                    owner.current_saved_squad_id == edit.saved_squad_id)
            {
                return Err(invalid("stale or nonempty retired SavedSquad receipt"));
            }
            progress.created_squads.retain(|squad|
                squad.saved_squad_id != edit.saved_squad_id);
            progress.tactics.retain(|row|
                row.saved_squad_id != edit.saved_squad_id);
            progress.retired_squad_ids.insert(edit.saved_squad_id);
        }
        progress.unspent_minerals()?;
        Ok(progress)
    }

    /// Deterministic career identity for resuming an existing physical
    /// World. Unlike the public status view, this excludes authored mission
    /// menus, prices and reward definitions, which can change independently
    /// of the durable player record. The original source is checked by its
    /// separate frozen configuration digest.
    pub fn career_identity(&self, progress: &CampaignProgress<'_>) -> Value {
        let mut identity = self.status(progress);
        let fields = identity.as_object_mut()
            .expect("native campaign status must be a JSON object");
        fields.remove("shop");
        fields.remove("fleet_classes");
        fields.remove("max_squad_ships");
        let mut mission_ids = progress.missions.keys().copied().collect::<Vec<_>>();
        mission_ids.sort_unstable();
        let missions = self.battles.iter().filter_map(|entry|
            entry.mission.as_ref()).collect::<Vec<_>>();
        let defeat_routes = missions.iter().any(|mission|
            mission.on_defeat.is_some() ||
            mission.unlock_if_any.iter().any(|condition|
                condition.result == RequiredOutcome::Defeat));
        let evacuation_history = missions.iter().any(|mission| mission.evacuation_rewards);
        let mission_results = mission_ids.into_iter().map(|id| {
            let result = &progress.missions[id];
            let mut row = json!({
                "id": id,
                "completed": result.completed,
                "latest_victory": result.latest_victory,
                "grants_claimed": result.grants_claimed,
            });
            // Existing campaigns retain their checkpoint identity. An
            // outcome-branching campaign must additionally pin whether a
            // non-victorious mission was ever played: false is not the same
            // as never attempted for a defeat-conditioned mission variant.
            if defeat_routes { row["attempted"] = json!(result.attempted); }
            if evacuation_history {
                row["evacuated_personnel"] = json!(result.evacuated_personnel);
            }
            row
        }).collect::<Vec<_>>();
        fields.insert("missions".to_owned(), json!(mission_results));
        identity
    }

    /// Read-only, complete campaign view for the game menu and external
    /// tooling. No sidecar save file is ever more authoritative than the
    /// original successful battle summaries.
    pub fn status(&self, progress: &CampaignProgress<'_>) -> Value {
        let has_defeat_conditions = self.battles.iter().filter_map(|entry|
            entry.mission.as_ref()).any(|mission|
                mission.on_defeat.is_some() ||
                mission.unlock_if_any.iter().any(|condition|
                    condition.result == RequiredOutcome::Defeat));
        let missions = self.battles.iter().filter_map(|entry| {
            let mission = entry.mission.as_ref()?;
            let mut row = json!({
                "id": mission.id,
                "title": entry.title,
                "selected_config": entry.config_for(progress),
                "victory_route": mission.on_victory.as_ref().map(|route| json!({
                    "previous_mission": route.mission,
                    "config": route.config,
                    "active": progress.latest_victory(&route.mission),
                })),
                
                "requires": mission.requires,
                "waiting_for": mission.requires.iter().filter(|id|
                    !progress.completed(id)).collect::<Vec<_>>(),
                "advance_on": if matches!(mission.advance_on, AdvanceOn::Finished) {
                    "finished"
                } else { "victory" },
                "unlocked": mission.unlocked(progress),
                "completed": progress.completed(&mission.id),
                "latest_victory": progress.latest_victory(&mission.id),
                "victory_grants_claimed": progress.grants_claimed(&mission.id),
                "victory_squads": mission.victory_squads.iter().map(|grant| json!({
                    "saved_squad_id": grant.saved_squad_id,
                    "template_saved_squad_id": grant.template_saved_squad_id,
                })).collect::<Vec<_>>(),
                "victory_grants": mission.victory_grants.iter().map(|grant| json!({
                    "ship_type": grant.ship_type, "count": grant.count,
                    "saved_squad_id": grant.saved_squad_id,
                })).collect::<Vec<_>>(),
                "record_fleet_stats": mission.record_fleet_stats,
                "bank_player_minerals": mission.bank_player_minerals,
                "persist_fleet_losses": mission.persist_fleet_losses,
            });
            if mission.persist_enemy_fleet_losses {
                row["persist_enemy_fleet_losses"] = json!(true);
            }
            if !mission.retire_on_defeat_types.is_empty() {
                row["retire_on_defeat_types"] = json!(mission.retire_on_defeat_types);
            }
            if mission.grants_on_finished() {
                row["grant_on"] = json!("finished");
            }
            if mission.bank_on_finished {
                row["bank_on_finished"] = json!(true);
            }
            if mission.evacuation_rewards {
                row["evacuation_rewards"] = json!(true);
                row["evacuated_personnel"] = json!(progress.missions
                    .get(mission.id.as_str())
                    .and_then(|result| result.evacuated_personnel));
            }
            if has_defeat_conditions {
                row["attempted"] = json!(progress.missions.get(mission.id.as_str())
                    .is_some_and(|result| result.attempted));
                row["latest_defeat"] = json!(progress.latest_defeat(&mission.id));
            }
            if !mission.unlock_if_any.is_empty() {
                row["unlock_if_any"] = json!(mission.unlock_if_any.iter()
                    .map(|condition| json!({
                        "mission": condition.mission,
                        "result": match condition.result {
                            RequiredOutcome::Finished => "finished",
                            RequiredOutcome::Victory => "victory",
                            RequiredOutcome::Defeat => "defeat",
                        },
                        "satisfied": condition.satisfied(progress),
                    })).collect::<Vec<_>>());
            }
            if let Some(route) = &mission.on_defeat {
                row["defeat_route"] = json!({
                    "previous_mission": route.mission,
                    "config": route.config,
                    "active": progress.latest_defeat(&route.mission) &&
                        mission.on_victory.as_ref().is_none_or(|win|
                            !progress.latest_victory(&win.mission)),
                });
            }
            Some(row)
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
                "special_history_complete": record.special_history_complete,
                "striker_bombs_dropped": record.special_history_complete
                    .then_some(record.combat.striker_bombs_dropped),
                "fire_barge_explosions": record.special_history_complete
                    .then_some(record.combat.fire_barge_explosions),
                "yellow_jacket_detonations": record.special_history_complete
                    .then_some(record.combat.yellow_jacket_detonations),
                "barge_charges_started": record.special_history_complete
                    .then_some(record.combat.barge_charges_started),
                "scout_beacons_deployed": record.special_history_complete
                    .then_some(record.combat.scout_beacons_deployed),
                "queen_minions_spawned": record.special_history_complete
                    .then_some(record.combat.queen_minions_spawned),
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
            "spent_minerals": progress.spent_minerals,
            "available_minerals": progress.unspent_minerals().expect("validated account balance"),
            "bank_history_complete": progress.bank_history_complete,
            "max_squad_ships": self.max_squad_ships,
            // Only paid stock belongs in the shop. The class registry
            // below exposes grant-only source templates without making
            // them accidentally purchasable in any client.
            "shop": self.buildable_ships.iter().filter(|shop|
                shop.purchasable).map(|shop| json!({
                "player_side": shop.player_side,
                "ship_type": shop.ship_type,
                "price_tsv": shop.price_tsv,
                "unlocked": progress.is_ship_unlocked(shop),
                "unlocked_after": shop.unlocked_after,
            })).collect::<Vec<_>>(),
            "fleet_classes": self.buildable_ships.iter().map(|class| json!({
                "player_side": class.player_side,
                "ship_type": class.ship_type,
                "source_max_tsv": class.price_tsv,
                "purchasable": class.purchasable,
            })).collect::<Vec<_>>(),
            "squad_transfers": progress.transfer_count,
            "created_squads": progress.created_squads.iter().map(|squad| json!({
                "saved_squad_id": squad.saved_squad_id,
                "template_saved_squad_id": squad.template_saved_squad_id,
                "side": squad.side,
                "has_recorded_battle": progress.squads.get(&squad.saved_squad_id)
                    .is_some_and(|row| row.battles_fought > 0),
                "acquisition": if progress.awarded_saved_squad_ids.contains(&squad.saved_squad_id) {
                    "victory_grant"
                } else { "player_created" },
            })).collect::<Vec<_>>(),
            "retired_saved_squad_ids": {
                let mut ids = progress.retired_squad_ids.iter().copied().collect::<Vec<_>>();
                ids.sort_unstable();
                ids
            },
            "named_saved_squads": {
                let mut ids = progress.squad_names.keys().copied().collect::<Vec<_>>();
                ids.sort_unstable();
                ids.into_iter().map(|id| json!({
                    "saved_squad_id": id,
                    "name": progress.squad_names[&id],
                    "retired": progress.retired_squad_ids.contains(&id),
                })).collect::<Vec<_>>()
            },
            "saved_squad_tactics_edits": progress.tactics_edits,
            "saved_squad_tactics": progress.tactics.iter().map(|entry| json!({
                "side": entry.side,
                "saved_squad_id": entry.saved_squad_id,
                "shooting_strategy_id": entry.shooting_strategy_id,
                "shooting_strategy": bees_hivemind::protocol::SHOOTING_STRATEGIES[
                    usize::from(entry.shooting_strategy_id - 1)
                ],
                "should_chase": entry.should_chase,
                "cease_fire": entry.cease_fire,
                "match_speed": entry.match_speed,
                "color_setting": entry.color_setting,
                "formation": entry.formation,
            })).collect::<Vec<_>>(),
            "original_fleet_transfers": progress.authored_transfer_count,
            "original_roster_overrides": progress.authored_roster_for_all(),
            "purchased_ships": progress.purchases.iter().map(|(side, ship)| json!({
                "player_side": side,
                "in_reserve": ship.saved_squad_id == 0,
                "fleet_ship_id": ship.fleet_ship_id,
                "saved_squad_id": ship.saved_squad_id,
                "ship_type": ship.ship_type,
                "acquisition": if progress.awarded_fleet_ids.contains(&ship.fleet_ship_id) {
                    "victory_grant"
                } else { "purchase" },
                "cost_tsv": if progress.awarded_fleet_ids.contains(&ship.fleet_ship_id) {
                    0
                } else { ship.price_tsv },
                "source_price_tsv": ship.price_tsv,
            })).collect::<Vec<_>>(),
        })
    }
}

/// One publication path for player edits. An incomplete write never
/// appears in a replay, and an existing numbered receipt is never changed.
fn commit_campaign_edit<T: Serialize>(
    root: &Path, prefix: &str, filename: &str, value: &T,
) -> io::Result<()> {
    fs::create_dir_all(root)?;
    let mut number = 0_u64;
    let directory = loop {
        number = number.checked_add(1)
            .ok_or_else(|| invalid("campaign edit receipt sequence exhausted"))?;
        let directory = root.join(format!("{prefix}{number:04}"));
        match fs::create_dir(&directory) {
            Ok(()) => break directory,
            Err(error) if error.kind() == io::ErrorKind::AlreadyExists => continue,
            Err(error) => return Err(error),
        }
    };
    let pending = directory.join(format!("{filename}.tmp"));
    let final_path = directory.join(filename);
    let file = fs::OpenOptions::new().create_new(true).write(true)
        .open(&pending)?;
    serde_json::to_writer(&file, value)
        .map_err(|error| invalid(format!("cannot serialize campaign edit: {error}")))?;
    file.sync_all()?;
    drop(file);
    fs::rename(pending, final_path)?;
    #[cfg(unix)]
    fs::File::open(&directory)?.sync_all()?;
    Ok(())
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
