//! Native Level squad membership and lifecycle bookkeeping.
//!
//! Source: Squad.AddShip / RemoveShip / Kill, Ship.Kill and Scout.DropBeacon.
//! The physics and deaths remain solely in bees-sim. This module tracks
//! which exact live SquadLifetime owns each ship, and reports formation/
//! command invalidation obligations to the gameplay adapter. It never
//! mutates combat state or silently settles HiveMind reservations.

use std::collections::{BTreeMap, HashMap, HashSet};

use bees_hivemind::session::{HiveMindSession, SquadLifetime};
use bees_sim::combat_world::{CombatEvent, CombatWorld};

use crate::commands::{
    FinishedNativeCommand, GameCommandError, NativeSquadCommandDirector,
};

const SCOUT_TYPE: u8 = 18;
const BEACON_TYPE: u8 = 14;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SquadRosterError {
    InvalidOwner,
    InvalidMembers,
    DuplicateSquad,
    DuplicateShip(i64),
    MissingScout(i64),
    MissingBeacon(i64),
    InvalidBeaconEvent,
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
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SquadRosterMeasurements {
    pub squads_registered: u64,
    pub minion_squads_registered: u64,
    pub ships_removed: u64,
    pub squads_retired: u64,
    pub peak_squads: usize,
}

/// Contains only data the Level already owns; physical positions, health,
/// vision and abilities are deliberately not duplicated here.
#[derive(Clone, Debug)]
pub struct NativeSquadEntry {
    pub owner: SquadLifetime,
    pub side: i32,
    pub members: Vec<i64>,
    pub mother_squad: Option<SquadLifetime>,
}

#[derive(Default)]
pub struct NativeSquadRoster {
    active: BTreeMap<SquadLifetime, NativeSquadEntry>,
    owner_by_ship: HashMap<i64, SquadLifetime>,
    measurements: SquadRosterMeasurements,
}

impl NativeSquadRoster {
    pub fn new() -> Self { Self::default() }

    pub fn measurements(&self) -> SquadRosterMeasurements {
        self.measurements
    }

    pub fn active_squad_count(&self) -> usize {
        self.active.len()
    }

    pub fn owning_squad(&self, ship_id: i64) -> Option<SquadLifetime> {
        self.owner_by_ship.get(&ship_id).copied()
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
        if !owner.valid() || side <= 0 || members.is_empty() {
            return Err(SquadRosterError::InvalidOwner);
        }
        if self.active.keys().any(|other|
            other.level_id == owner.level_id &&
            other.squad_id == owner.squad_id
        ) {
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
            owner, side, members: members.to_vec(), mother_squad: None,
        };
        self.active.insert(owner, entry);
        for &ship_id in members {
            self.owner_by_ship.insert(ship_id, owner);
        }
        self.measurements.squads_registered += 1;
        self.measurements.peak_squads =
            self.measurements.peak_squads.max(self.active.len());
        Ok(())
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
            self.active.keys().any(|other|
                other.level_id == minion_owner.level_id &&
                other.squad_id == minion_owner.squad_id
            ) {
            return Err(SquadRosterError::InvalidOwner);
        }
        let parent = world.ship(*scout_id)
            .ok_or(SquadRosterError::MissingScout(*scout_id))?;
        let beacon = world.ship(*beacon_id)
            .ok_or(SquadRosterError::MissingBeacon(*beacon_id))?;
        if parent.ship_type != SCOUT_TYPE ||
            beacon.ship_type != BEACON_TYPE ||
            beacon.side != parent.side || beacon.vitals.is_dead() ||
            !world.ship_body(*beacon_id).is_some_and(|body| body.active) ||
            world.scout_beacon_parent(*beacon_id) != Some(*scout_id) ||
            world.ship_fleet_identity(*beacon_id) != Some(*parent_fleet_id) ||
            world.ship_fleet_identity(*scout_id) != Some(*parent_fleet_id) ||
            self.owner_by_ship.contains_key(beacon_id)
        {
            return Err(SquadRosterError::InvalidBeaconEvent);
        }
        let entry = NativeSquadEntry {
            owner: minion_owner,
            side: beacon.side,
            members: vec![*beacon_id],
            mother_squad: Some(mother_squad),
        };
        self.active.insert(minion_owner, entry);
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

    /// Match the source Ship.Kill sequence: remove this ship from its
    /// owning Squad, refresh surviving members, and retire the Squad only
    /// when it becomes empty. A parent Scout's death does not itself
    /// destroy independently living minion squads.
    pub fn on_ship_death(
        &mut self,
        ship_id: i64,
        changes: &mut Vec<SquadRosterChange>,
    ) {
        let Some(owner) = self.owner_by_ship.remove(&ship_id) else {
            return;
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
            self.active.remove(&owner);
            self.measurements.squads_retired += 1;
            changes.push(SquadRosterChange::SquadRetired { squad: owner });
        } else {
            changes.push(SquadRosterChange::MemberLost {
                squad: owner, ship_id, remaining: squad.members.len(),
            });
        }
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
            self.on_ship_death(*victim_id, changes);
        }
    }
}

/// Invoke on a SquadRetired transition after the combat callback unwinds.
/// A dead Squad must cease executing commands and may no longer receive
/// an in-flight Hive Mind decision. The outstanding OutcomeIds are NOT
/// dropped, credited or forged here: the returned FinishedNativeCommand
/// must be settled by the authoritative game/TSV accounting layer, and
/// HiveMindSession keeps in-flight requests as explicit discards when
/// their responses eventually arrive.
///
/// MemberLost and MinionCreated do not kill their still-living squads.
pub fn reconcile_retired_squad(
    change: SquadRosterChange,
    world: &mut CombatWorld,
    commands: &mut NativeSquadCommandDirector,
    session: &mut HiveMindSession,
) -> Result<Option<FinishedNativeCommand>, GameCommandError> {
    let SquadRosterChange::SquadRetired { squad } = change else {
        return Ok(None);
    };
    // Keep the session usable on a command-director failure; that error
    // must be surfaced so the caller can reconcile the outstanding order.
    let finished = commands.retire_squad(world, squad)?;
    session.retire_squad(squad);
    Ok(finished)
}

/// A native fixed step must project the one CombatWorld event stream into
/// gameplay ownership before the next tick/command/inference pass.
/// Returning an error is fatal to this world's integration step: replaying
/// an already applied partial event batch would duplicate registrations.
#[derive(Debug)]
pub enum SquadEventBridgeError {
    Roster(SquadRosterError),
    Commands(GameCommandError),
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
        session: &mut HiveMindSession,
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
                CombatEvent::ShipDied { victim_id, .. } =>
                    self.on_ship_death(*victim_id, changes),
                _ => {}
            }
        }
        // Only whole dead squads retire in-flight strategies. Surviving
        // squads keep their lifecycle: their Level adapter may refresh or
        // supersede orders after recomputing formation and command bans.
        for &change in changes.iter() {
            if let SquadRosterChange::SquadRetired { .. } = change {
                if let Some(command) = reconcile_retired_squad(
                    change, world, commands, session,
                )? {
                    finished.push(command);
                }
            }
        }
        Ok(())
    }
}
