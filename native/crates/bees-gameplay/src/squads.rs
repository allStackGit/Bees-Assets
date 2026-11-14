//! Native Level squad membership and lifecycle bookkeeping.
//!
//! Source: Squad.AddShip / RemoveShip / Kill, Ship.Kill and Scout.DropBeacon.
//! The physics and deaths remain solely in bees-sim. This module tracks
//! which exact live SquadLifetime owns each ship, and reports formation/
//! command invalidation obligations to the gameplay adapter. It never
//! mutates combat state or silently settles HiveMind reservations.

use std::collections::{BTreeMap, HashMap, HashSet};

use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::{CombatEvent, CombatWorld};
use bees_sim::ship_assets::BEACON_TYPE;
use bees_sim::episode_terminal::{
    EpisodeDeathAttribution, EpisodeTerminalError, EpisodeTerminalResult,
    EpisodeTerminalRewards, NativeEpisodeTerminal,
};

use crate::commands::{
    FinishedNativeCommand, GameCommandError, NativeSquadCommandDirector,
};

const SCOUT_TYPE: u8 = 18;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SquadRosterError {
    InvalidOwner,
    InvalidMembers,
    InvalidSourceHotkey,
    DuplicateSquad,
    MissingSquad,
    DuplicateShip(i64),
    MissingScout(i64),
    MissingBeacon(i64),
    InvalidBeaconEvent,
    InvalidQueenEvent,
    MissingQueen(i64),
    MissingShipOwner(i64),
    UnregisteredLiveShips,
    FailedRoster,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SquadRosterChange {
    /// Unity RemoveShip -> RefreshCompositionCommandBans, SetOffsets and
    /// MoveSquadBox on surviving combat squads. The Level adapter owns
    /// the actual geometry, formation and command adjustments.
    MemberLost {
        squad: SquadLifetime,
        ship_id: i64,
        remaining: usize,
    },
    /// Unity Squad.AddShip -> RefreshCompositionCommandBans and
    /// HasAddedShips. Existing membership is never implicitly moved.
    MemberAdded {
        squad: SquadLifetime,
        ship_id: i64,
        count: usize,
    },
    /// Unity Squad.Kill after its final ship dies. The caller MUST retire
    /// this lifecycle in both NativeSquadCommandDirector and HiveMindSession,
    /// retaining pending OutcomeIds for explicit later settlement.
    SquadRetired { squad: SquadLifetime },
    /// Scout spawned a real Beacon ship and a new one-ship minion Squad.
    /// Native Level must create its display/command/formation entities.
    MinionCreated {
        squad: SquadLifetime,
        mother_squad: SquadLifetime,
        scout_id: i64,
        beacon_id: i64,
    },
    /// Timed Queen waves create a new squad for the first living wave
    /// member, then add later YellowJackets in the same source formation.
    QueenMinionCreated {
        squad: SquadLifetime, mother_squad: SquadLifetime,
        queen_id: i64, minion_id: i64, minion_index: u8,
    },
    QueenMinionAdded {
        squad: SquadLifetime, queen_id: i64,
        minion_id: i64, minion_index: u8,
    },
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SquadRosterMeasurements {
    pub squads_registered: u64,
    pub minion_squads_registered: u64,
    pub ships_removed: u64,
    pub ships_added_to_existing: u64,
    pub squads_retired: u64,
    pub peak_squads: usize,
    /// How many source-order compactions were needed at retirement.
    /// Batched deaths compact once, not once per killed squad.
    pub source_order_compactions: u64,
}

/// Contains only data the Level already owns; physical positions, health,
/// vision and abilities are deliberately not duplicated here.
#[derive(Clone, Debug)]
pub struct NativeSquadEntry {
    /// None means the source collection was not captured. Never guess an empty set.
    pub command_bans: Option<Vec<bees_hivemind::dispatch::LocalCommandKind>>,
    pub targeting: Option<crate::squad_targeting::NativeInstalledTargeting>,
    pub owner: SquadLifetime,
    pub side: i32,
    pub members: Vec<i64>,
    pub mother_squad: Option<SquadLifetime>,
    /// Source Queen's one timed wave. Removed when its last member dies.
    pub queen_wave: Option<(i64, u64)>,
    pub source_hotkey_number: Option<u32>,
    pub original_squad_count: Option<u32>,
}

#[derive(Default)]
pub struct NativeSquadRoster {
    active: BTreeMap<SquadLifetime, NativeSquadEntry>,
    /// Actual Level.State.Squads registration order, independent of IDs.
    source_order: Vec<SquadLifetime>,
    owner_by_ship: HashMap<i64, SquadLifetime>,
    queen_wave_owner: HashMap<(i64, u64), SquadLifetime>,
    measurements: SquadRosterMeasurements,
    /// A partial source-order event pass must never be replayed.
    failed: bool,
}

impl NativeSquadRoster {
    pub fn new() -> Self { Self::default() }

    pub fn measurements(&self) -> SquadRosterMeasurements {
        self.measurements
    }

    pub fn is_failed(&self) -> bool {
        self.failed
    }

    pub fn active_squad_count(&self) -> usize {
        self.active.len()
    }

    /// A pooled squad number cannot be reused while its prior lifecycle
    /// remains active. The BTreeMap already orders by
    /// (level_id, squad_id, lifecycle_id), so only that number's range
    /// needs inspection instead of every squad in a large battle.
    fn has_live_squad_number(&self, owner: SquadLifetime) -> bool {
        let first = SquadLifetime { lifecycle_id: 0, ..owner };
        let last = SquadLifetime { lifecycle_id: u64::MAX, ..owner };
        self.active.range(first..=last).next().is_some()
    }

    /// Stable live lifetimes for terminal HiveMind session retirement.
    /// Level completion does not imply that every ship physically died.
    pub fn active_lifetimes(&self) -> impl Iterator<Item = SquadLifetime> + '_ {
        self.active.keys().copied()
    }

    pub fn owning_squad(&self, ship_id: i64) -> Option<SquadLifetime> {
        self.owner_by_ship.get(&ship_id).copied()
    }

    pub fn source_order(&self) -> &[SquadLifetime] { &self.source_order }

    /// Install source-initial shortcut metadata once at construction.
    /// The original count never changes when a ship/squad dies; minions
    /// receive the count but cannot acquire an original number.
    pub fn set_source_hotkey(&mut self, owner: SquadLifetime,
        number: Option<u32>, original_count: u32,
    ) -> Result<(), SquadRosterError> {
        let side = self.active.get(&owner)
            .ok_or(SquadRosterError::MissingSquad)?.side;
        if number.is_some_and(|n| n == 0 || n > original_count) {
            return Err(SquadRosterError::InvalidSourceHotkey);
        }
        if self.active.iter().any(|(&id, squad)| id != owner &&
            squad.side == side && (
                squad.original_squad_count.is_some_and(|count| count != original_count) ||
                (number.is_some() && squad.source_hotkey_number == number)
            )
        ) {
            return Err(SquadRosterError::InvalidSourceHotkey);
        }
        let squad = self.active.get_mut(&owner)
            .ok_or(SquadRosterError::MissingSquad)?;
        if squad.original_squad_count.is_some() {
            return Err(SquadRosterError::InvalidSourceHotkey);
        }
        squad.source_hotkey_number = number;
        squad.original_squad_count = Some(original_count);
        Ok(())
    }

    /// A full native Level cannot emit valid rollout samples when ships
    /// are missing from the squad registry. Both counts are maintained
    /// at the real spawn/death boundaries, so this check is constant time.
    pub fn covers_live_roster(&self, world: &CombatWorld) -> bool {
        self.owner_by_ship.len() == world.total_living_ships()
    }

    pub fn install_targeting(&mut self, owner: SquadLifetime, choice: crate::squad_targeting::NativeInstalledTargeting)
        -> Result<(), SquadRosterError>
    {
        self.active.get_mut(&owner).ok_or(SquadRosterError::MissingSquad)?.targeting = Some(choice);
        Ok(())
    }

    pub fn set_command_bans(&mut self, owner: SquadLifetime,
        bans: Vec<bees_hivemind::dispatch::LocalCommandKind>,
    ) -> Result<(), SquadRosterError> {
        self.active.get_mut(&owner).ok_or(SquadRosterError::MissingSquad)?.command_bans = Some(bans);
        Ok(())
    }

    pub fn command_bans_mut(&mut self, owner: SquadLifetime)
        -> Option<&mut Vec<bees_hivemind::dispatch::LocalCommandKind>> {
        self.active.get_mut(&owner)?.command_bans.as_mut()
    }

    pub fn get(&self, owner: SquadLifetime) -> Option<&NativeSquadEntry> {
        self.active.get(&owner)
    }

    /// Called at the source Level's Squad.Setup + AddShip boundary.
    /// Reject pooled squad IDs whose previous lifecycle is still active;
    /// a new lifecycle must never silently inherit its predecessor's
    /// command reservation or ownership.
    pub fn register_squad(
        &mut self,
        world: &CombatWorld,
        owner: SquadLifetime,
        side: i32,
        members: &[i64],
    ) -> Result<(), SquadRosterError> {
        if self.failed { return Err(SquadRosterError::FailedRoster); }
        if !owner.valid() || side <= 0 || members.is_empty() {
            return Err(SquadRosterError::InvalidOwner);
        }
        if self.has_live_squad_number(owner) {
            return Err(SquadRosterError::DuplicateSquad);
        }
        let mut distinct = HashSet::with_capacity(members.len());
        for &ship_id in members {
            if ship_id <= 0 || !distinct.insert(ship_id) ||
                !world.ship(ship_id).is_some_and(|ship|
                    ship.side == side && !ship.vitals.is_dead()) ||
                !world.ship_body(ship_id).is_some_and(|body| body.active)
            {
                return Err(SquadRosterError::InvalidMembers);
            }
            if self.owner_by_ship.contains_key(&ship_id) {
                return Err(SquadRosterError::DuplicateShip(ship_id));
            }
        }
        let entry = NativeSquadEntry {
            command_bans: None,
            targeting: None,
            owner, side, members: members.to_vec(), mother_squad: None,
            queen_wave: None,
            source_hotkey_number: None, original_squad_count: None,
        };
        self.active.insert(owner, entry);
        self.source_order.push(owner);
        for &ship_id in members {
            self.owner_by_ship.insert(ship_id, owner);
        }
        self.measurements.squads_registered += 1;
        self.measurements.peak_squads =
            self.measurements.peak_squads.max(self.active.len());
        Ok(())
    }

    /// Add an actual living ship to an existing Squad, matching the source
    /// Squad.AddShip. The Level caller must then refresh composition-specific
    /// command bans and mark its formation for recalculation. A ship already
    /// owned by another squad is an error, never an implicit transfer.
    pub fn add_member(
        &mut self,
        world: &CombatWorld,
        owner: SquadLifetime,
        ship_id: i64,
    ) -> Result<SquadRosterChange, SquadRosterError> {
        if self.failed { return Err(SquadRosterError::FailedRoster); }
        let side = self.active.get(&owner)
            .ok_or(SquadRosterError::MissingSquad)?.side;
        if self.owner_by_ship.contains_key(&ship_id) {
            return Err(SquadRosterError::DuplicateShip(ship_id));
        }
        if ship_id <= 0 ||
            !world.ship(ship_id).is_some_and(|ship|
                ship.side == side && !ship.vitals.is_dead()) ||
            !world.ship_body(ship_id).is_some_and(|body| body.active)
        {
            return Err(SquadRosterError::InvalidMembers);
        }
        let squad = self.active.get_mut(&owner)
            .expect("prevalidated live squad");
        squad.members.push(ship_id);
        self.owner_by_ship.insert(ship_id, owner);
        self.measurements.ships_added_to_existing += 1;
        Ok(SquadRosterChange::MemberAdded {
            squad: owner, ship_id, count: squad.members.len(),
        })
    }

    /// Translate the already-executed CombatWorld::ScoutBeaconDropped into
    /// the source Level's minion Squad.Setup + AddShip. The Level provides
    /// a unique transient SquadLifetime; no synthetic global ID allocator
    /// or training-only squad is introduced here.
    ///
    /// Process world events in their recorded order, so a Scout killed
    /// later in the same tick still owns its earlier Beacon creation.
    pub fn register_scout_beacon(
        &mut self,
        world: &CombatWorld,
        event: &CombatEvent,
        minion_owner: SquadLifetime,
    ) -> Result<SquadRosterChange, SquadRosterError> {
        if self.failed { return Err(SquadRosterError::FailedRoster); }
        let CombatEvent::ScoutBeaconDropped {
            scout_id, beacon_id, parent_fleet_id,
        } = event else {
            return Err(SquadRosterError::InvalidBeaconEvent);
        };
        let Some(mother_squad) = self.owning_squad(*scout_id) else {
            return Err(SquadRosterError::MissingScout(*scout_id));
        };
        if !minion_owner.valid() ||
            minion_owner.level_id != mother_squad.level_id ||
            self.has_live_squad_number(minion_owner) {
            return Err(SquadRosterError::InvalidOwner);
        }
        let parent = world.ship(*scout_id)
            .ok_or(SquadRosterError::MissingScout(*scout_id))?;
        let beacon = world.ship(*beacon_id)
            .ok_or(SquadRosterError::MissingBeacon(*beacon_id))?;
        // Beacon may already be dead by the time the Level consumes
        // this tick's immutable spawn event. Scout/Beacon IDs, type,
        // side and inherited fleet identity survive in CombatWorld's
        // historical ship table. Do NOT require a live collider or the
        // retired Beacon's transient parent mapping. Its subsequent
        // ShipDied event will retire this one-member minion Squad.
        if parent.ship_type != SCOUT_TYPE ||
            beacon.ship_type != BEACON_TYPE ||
            beacon.side != parent.side ||
            world.ship_fleet_identity(*beacon_id) != Some(*parent_fleet_id) ||
            world.ship_fleet_identity(*scout_id) != Some(*parent_fleet_id) ||
            self.owner_by_ship.contains_key(beacon_id)
        {
            return Err(SquadRosterError::InvalidBeaconEvent);
        }
        let entry = NativeSquadEntry {
            command_bans: None,
            targeting: None,
            owner: minion_owner,
            side: beacon.side,
            members: vec![*beacon_id],
            mother_squad: Some(mother_squad),
            queen_wave: None,
            source_hotkey_number: None, original_squad_count: None,
        };
        self.active.insert(minion_owner, entry);
        self.source_order.push(minion_owner);
        self.owner_by_ship.insert(*beacon_id, minion_owner);
        self.measurements.squads_registered += 1;
        self.measurements.minion_squads_registered += 1;
        self.measurements.peak_squads =
            self.measurements.peak_squads.max(self.active.len());
        Ok(SquadRosterChange::MinionCreated {
            squad: minion_owner, mother_squad,
            scout_id: *scout_id, beacon_id: *beacon_id,
        })
    }

    /// Queen.SpawnMinionWave groups spawned children under one transient
    /// Squad until the last one dies. The source Queen may outlive several
    /// waves; each wave keeps a distinct identity even when timed spawns
    /// overlap. Registration accepts a child killed later in the same tick.
    pub fn register_queen_minion(
        &mut self, world: &CombatWorld, event: &CombatEvent,
        mut allocate: impl FnMut(SquadLifetime, i64)
            -> Result<SquadLifetime, SquadRosterError>,
    ) -> Result<SquadRosterChange, SquadRosterError> {
        if self.failed { return Err(SquadRosterError::FailedRoster); }
        let CombatEvent::QueenMinionSpawned {
            queen_id, minion_id, wave_sequence, minion_index, parent_fleet_id,
        } = event else { return Err(SquadRosterError::InvalidQueenEvent); };
        let mother = self.owning_squad(*queen_id)
            .ok_or(SquadRosterError::MissingQueen(*queen_id))?;
        let queen = world.ship(*queen_id)
            .ok_or(SquadRosterError::MissingQueen(*queen_id))?;
        let minion = world.ship(*minion_id)
            .ok_or(SquadRosterError::InvalidQueenEvent)?;
        if queen.ship_type != 17 || minion.ship_type != 22 ||
            minion.side != queen.side || *minion_index >= 16 ||
            *wave_sequence == 0 ||
            world.ship_fleet_identity(*queen_id) != Some(*parent_fleet_id) ||
            world.ship_fleet_identity(*minion_id) != Some(*parent_fleet_id) ||
            self.owner_by_ship.contains_key(minion_id)
        { return Err(SquadRosterError::InvalidQueenEvent); }

        let key = (*queen_id, *wave_sequence);
        if let Some(&owner) = self.queen_wave_owner.get(&key) {
            let squad = self.active.get_mut(&owner)
                .ok_or(SquadRosterError::InvalidQueenEvent)?;
            if squad.side != minion.side ||
                squad.mother_squad != Some(mother) ||
                squad.queen_wave != Some(key)
            { return Err(SquadRosterError::InvalidQueenEvent); }
            squad.members.push(*minion_id);
            self.owner_by_ship.insert(*minion_id, owner);
            self.measurements.ships_added_to_existing += 1;
            return Ok(SquadRosterChange::QueenMinionAdded {
                squad: owner, queen_id: *queen_id,
                minion_id: *minion_id, minion_index: *minion_index,
            });
        }
        let owner = allocate(mother, *minion_id)?;
        if !owner.valid() || owner.level_id != mother.level_id ||
            self.has_live_squad_number(owner)
        { return Err(SquadRosterError::InvalidOwner); }
        // Source Queen.CreateMinionSquad inherits the SavedSquad's
        // independent bans. Composition-specific bans are recalculated
        // when its first actual YellowJacket joins the live formation.
        let inherited_bans = self.active.get(&mother)
            .and_then(|parent| parent.command_bans.clone());
        self.active.insert(owner, NativeSquadEntry {
            command_bans: inherited_bans, targeting: None,
            owner, side: minion.side, members: vec![*minion_id],
            mother_squad: Some(mother), queen_wave: Some(key),
            source_hotkey_number: None, original_squad_count: None,
        });
        self.queen_wave_owner.insert(key, owner);
        self.source_order.push(owner);
        self.owner_by_ship.insert(*minion_id, owner);
        self.measurements.squads_registered += 1;
        self.measurements.minion_squads_registered += 1;
        self.measurements.peak_squads =
            self.measurements.peak_squads.max(self.active.len());
        Ok(SquadRosterChange::QueenMinionCreated {
            squad: owner, mother_squad: mother, queen_id: *queen_id,
            minion_id: *minion_id, minion_index: *minion_index,
        })
    }

    /// Match the source Ship.Kill sequence: remove this ship from its
    /// owning Squad, refresh surviving members, and retire the Squad only
    /// when it becomes empty. A parent Scout's death does not itself
    /// destroy independently living minion squads.
    pub fn on_ship_death(
        &mut self,
        ship_id: i64,
        changes: &mut Vec<SquadRosterChange>,
    ) -> bool {
        self.on_ship_death_inner(ship_id, changes, true)
    }

    /// An isolated death must leave the public roster fully ordered.
    /// The canonical fixed-step event bridge can defer compaction until
    /// after all deaths from the same event batch have been applied.
    fn on_ship_death_inner(
        &mut self, ship_id: i64, changes: &mut Vec<SquadRosterChange>,
        compact_source_order: bool,
    ) -> bool {
        let Some(owner) = self.owner_by_ship.remove(&ship_id) else {
            return false;
        };
        let Some(squad) = self.active.get_mut(&owner) else {
            unreachable!("ship mapping and owning squad updated atomically");
        };
        let Some(index) = squad.members.iter().position(|&id| id == ship_id)
        else {
            unreachable!("ship mapping must match member list");
        };
        squad.members.remove(index); // preserve formation list order
        self.measurements.ships_removed += 1;
        if squad.members.is_empty() {
            if let Some(key) = squad.queen_wave {
                self.queen_wave_owner.remove(&key);
            }
            self.active.remove(&owner);
            if compact_source_order {
                self.source_order.retain(|entry| *entry != owner);
                self.measurements.source_order_compactions += 1;
            }
            self.measurements.squads_retired += 1;
            changes.push(SquadRosterChange::SquadRetired { squad: owner });
        } else {
            changes.push(SquadRosterChange::MemberLost {
                squad: owner, ship_id, remaining: squad.members.len(),
            });
        }
        true
    }

    /// Feed this from the one authoritative world event queue, not from
    /// recreated training-only health logic. The caller separately passes
    /// ScoutBeaconDropped events to register_scout_beacon with an actual
    /// Level-allocated transient squad lifecycle.
    pub fn on_combat_death(
        &mut self,
        event: &CombatEvent,
        changes: &mut Vec<SquadRosterChange>,
    ) {
        if let CombatEvent::ShipDied { victim_id, .. } = event {
            let _ = self.on_ship_death(*victim_id, changes);
        }
    }
}

/// Invoke on a SquadRetired transition after the combat callback unwinds.
/// A dead Squad must cease executing commands and may no longer receive
/// an in-flight Hive Mind decision. The outstanding OutcomeIds are NOT
/// dropped, credited or forged here: the returned FinishedNativeCommand
/// must be settled by the authoritative game/TSV accounting layer, and
/// The *connection-owned* HiveMindSession must be notified by the native
/// transport, using the Level's persistent squad-retirement outbox.
/// An in-flight response then becomes an explicit discard obligation.
///
/// MemberLost and MinionCreated do not kill their still-living squads.
pub fn reconcile_retired_squad(
    change: SquadRosterChange,
    world: &mut CombatWorld,
    commands: &mut NativeSquadCommandDirector,
) -> Result<Option<FinishedNativeCommand>, GameCommandError> {
    let SquadRosterChange::SquadRetired { squad } = change else {
        return Ok(None);
    };
    // A dead Level retires its local execution immediately; the network
    // session belongs to the shared connection, not the Level or world.
    commands.retire_squad(world, squad)
}

/// A native fixed step must project the one CombatWorld event stream into
/// gameplay ownership before the next tick/command/inference pass.
/// Returning an error is fatal to this world's integration step: replaying
/// an already applied partial event batch would duplicate registrations.
#[derive(Debug)]
pub enum SquadEventBridgeError {
    Roster(SquadRosterError),
    Commands(GameCommandError),
    FailedRoster,
}

impl From<SquadRosterError> for SquadEventBridgeError {
    fn from(value: SquadRosterError) -> Self { Self::Roster(value) }
}
impl From<GameCommandError> for SquadEventBridgeError {
    fn from(value: GameCommandError) -> Self { Self::Commands(value) }
}

impl NativeSquadRoster {
    /// Consume just the source world's combat/special events in their
    /// original order. The Level supplies a fresh temporary SquadLifetime
    /// for each Scout Beacon; no static allocator or training-only identity
    /// participates in physics, squad ownership or HiveMind reservations.
    ///
    /// `changes` and `finished` are reused output buffers. The caller must
    /// settle every finished command's real TSV/outcome reservations and
    /// recompute survivor formation offsets for MemberLost. A minion creation
    /// requires registering the graphical squad and its presentation in the
    /// ordinary Level adapter.
    pub fn reconcile_combat_tick(
        &mut self,
        world: &mut CombatWorld,
        commands: &mut NativeSquadCommandDirector,
        allocate_minion: impl FnMut(SquadLifetime, i64)
            -> Result<SquadLifetime, SquadRosterError>,
        changes: &mut Vec<SquadRosterChange>,
        finished: &mut Vec<FinishedNativeCommand>,
    ) -> Result<(), SquadEventBridgeError> {
        if self.failed {
            return Err(SquadEventBridgeError::FailedRoster);
        }
        let result = self.reconcile_combat_tick_inner(
            world, commands, allocate_minion, changes, finished,
        );
        if result.is_err() {
            // The event pass may have already registered a minion or
            // retired a command. Replaying would duplicate mutations.
            self.failed = true;
        }
        result
    }

    fn reconcile_combat_tick_inner(
        &mut self,
        world: &mut CombatWorld,
        commands: &mut NativeSquadCommandDirector,
        mut allocate_minion: impl FnMut(SquadLifetime, i64)
            -> Result<SquadLifetime, SquadRosterError>,
        changes: &mut Vec<SquadRosterChange>,
        finished: &mut Vec<FinishedNativeCommand>,
    ) -> Result<(), SquadEventBridgeError> {
        changes.clear();
        finished.clear();
        // Read only the small discriminants needed by the roster. The
        // combat event store remains authoritative and is not drained.
        // Command retirement after this pass can mutate world speed safely.
        let mut retired_any_squad = false;
        for event in world.events() {
            match event {
                CombatEvent::ScoutBeaconDropped {
                    scout_id, beacon_id, ..
                } => {
                    let mother = self.owning_squad(*scout_id)
                        .ok_or(SquadRosterError::MissingScout(*scout_id))?;
                    let owner = allocate_minion(mother, *beacon_id)?;
                    changes.push(self.register_scout_beacon(
                        world, event, owner,
                    )?);
                }
                CombatEvent::QueenMinionSpawned { queen_id, minion_id, .. } => {
                    let mother = self.owning_squad(*queen_id)
                        .ok_or(SquadRosterError::MissingQueen(*queen_id))?;
                    changes.push(self.register_queen_minion(world, event,
                        |_, id| allocate_minion(mother, id))?);
                }
                CombatEvent::ShipDied { victim_id, .. } => {
                    if !self.on_ship_death_inner(*victim_id, changes, false) {
                        return Err(SquadRosterError::MissingShipOwner(
                            *victim_id,
                        ).into());
                    }
                    retired_any_squad |= matches!(
                        changes.last(), Some(SquadRosterChange::SquadRetired { .. })
                    );
                },
                _ => {}
            }
        }
        if retired_any_squad {
            // Preserve canonical registration order, including any Beacon
            // squads created earlier/later in this very same event batch.
            // A mass death used to scan this list once PER retired squad.
            self.source_order.retain(|owner| self.active.contains_key(owner));
            self.measurements.source_order_compactions += 1;
        }
        if !self.covers_live_roster(world) {
            return Err(SquadRosterError::UnregisteredLiveShips.into());
        }
        // Unity GameState.RemoveShip notifies a surviving squad's Heal
        // command before the next 3s movement poll. The physical death
        // already freed its slot; assign waiting members immediately and
        // emit a finished command if none remain. All other command types
        // retain their existing lifecycle until explicit replacement.
        for &change in changes.iter() {
            if let SquadRosterChange::MemberLost { squad, .. } = change {
                if let Some(command) = commands.healing_member_lost(
                    world, squad,
                )? {
                    finished.push(command);
                }
            }
            if let SquadRosterChange::SquadRetired { .. } = change {
                if let Some(command) = reconcile_retired_squad(
                    change, world, commands,
                )? {
                    finished.push(command);
                }
            }
        }
        Ok(())
    }
}

/// Episode termination is a consumer of the same validated squad-retirement
/// changes, not a second gameplay rule. An immobile Beacon or stationary
/// target can remain alive after its side is eliminated; the terminal
/// controller reads the world's authored mobile-roster counts.
///
/// Call only after reconcile_combat_tick completes. Attributed terminal
/// reward delivery and MA-POCA group ending belong to the episode adapter.
pub fn observe_terminal_after_roster(
    episode: &mut NativeEpisodeTerminal,
    world: &mut CombatWorld,
    dt_seconds: f32,
    changes: &[SquadRosterChange],
) -> Result<Option<EpisodeTerminalResult>, EpisodeTerminalError> {
    let squad_was_retired = changes.iter().any(|change|
        matches!(change, SquadRosterChange::SquadRetired { .. })
    );
    episode.observe_after_step(world, dt_seconds, squad_was_retired)
}

/// The native Level's complete, source-order terminal handoff. The combat
/// event store is read once here by the episode death-attribution tracker,
/// then the already-reconciled squad transitions decide whether the last
/// squad was killed. A stationary-only side never ends on its own merely
/// from having zero mobile ships; timeout is a draw for both sides.
///
/// Rendering, reward delivery, GroupEpisode end, reset and persistent TSV
/// accounting remain host responsibilities, not hidden side effects.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FinalizedNativeEpisode {
    pub outcome: EpisodeTerminalResult,
    pub rewards: EpisodeTerminalRewards,
}

pub fn observe_terminal_rewards_after_roster(
    episode: &mut NativeEpisodeTerminal,
    attribution: &mut EpisodeDeathAttribution,
    world: &mut CombatWorld,
    dt_seconds: f32,
    changes: &[SquadRosterChange],
) -> Result<Option<FinalizedNativeEpisode>, EpisodeTerminalError> {
    // Do not consume or mutate attribution for an invalid timestep.
    if !dt_seconds.is_finite() || dt_seconds <= 0.0 {
        return Err(EpisodeTerminalError::InvalidStep);
    }
    if episode.result().is_some() {
        return Ok(None);
    }
    attribution.record_combat_events(world, world.events());
    let Some(outcome) = observe_terminal_after_roster(
        episode, world, dt_seconds, changes,
    )? else { return Ok(None) };
    let rewards = episode.terminal_rewards_from_attribution(attribution)
        .ok_or(EpisodeTerminalError::InvalidSides)?;
    Ok(Some(FinalizedNativeEpisode { outcome, rewards }))
}
