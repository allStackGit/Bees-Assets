//! Unity Heal command lifecycle: globally reserved Beehive slots, tracked
//! movement, authored HealCollider arrival and 1-second healing intervals.
//! Physical health/TSV and reservations live only in CombatWorld.

use super::*;
use std::collections::{HashMap, HashSet, VecDeque};
use bees_sim::combat_world::GamePointOrder;

pub(super) struct HealRun {
    /// Fixed Socket.HandleStrategicCommandResponse target order: nearest
    /// Beehive to Squad.GetPosition(), tied by live registry order.
    hives: Vec<i64>,
    pending: VecDeque<i64>,
    /// Exactly the ships holding CombatWorld reservations.
    assigned: HashMap<i64, i64>,
    /// Previously owned by the same squad command during replacement.
    /// Failed activation may not steal that command's Beehive slots.
    borrowed_at_start: HashSet<i64>,
    waiting: Vec<i64>,
    /// Source ShipReachedBeehive order, used for HealShips callbacks.
    healing: Vec<i64>,
    members: HashMap<i64, SquadFormationMember>,
    formation: SquadFormationOptions,
    healing_timer: Option<ScaledTimer>,
    cleanup_scratch: Vec<(i64, bool)>,
    /// Reused removal membership avoids quadratic Vec::retain calls
    /// when a large group completes healing or loses its Beehive.
    released_scratch: HashSet<i64>,
    pub(super) earned_tsv: i64,
}

impl HealRun {
    pub(super) fn start(
        world: &mut CombatWorld, squad: NativeSquadOrder<'_>,
        chosen_hives: Option<&[i64]>,
    ) -> Result<Option<Self>, GameCommandError> {
        let mut side = None;
        let mut seen = HashSet::with_capacity(squad.members.len());
        let mut candidates = Vec::new();
        let mut members = HashMap::with_capacity(squad.members.len());
        for &member in squad.members {
            if !seen.insert(member.ship_id) {
                return Err(GameCommandError::InvalidSquad);
            }
            let ship = world.ship(member.ship_id)
                .ok_or(GameCommandError::InvalidSquad)?;
            if ship.vitals.is_dead() { continue; }
            if side.is_some_and(|other| other != ship.side) {
                return Err(GameCommandError::InvalidSquad);
            }
            side = Some(ship.side);
            if ship.vitals.health() >= ship.vitals.max_health() {
                continue;
            }
            candidates.push((member.ship_id, ship.vitals.health()));
            members.insert(member.ship_id, member);
        }
        if candidates.is_empty() { return Ok(None); }
        let Some(side) = side else { return Ok(None); };
        // Unity Heal keeps every live target Beehive even when its four
        // slots are currently full. Those slots may free during this run;
        // filtering here would permanently exclude that Beehive from the
        // later AssignAvailableHealingSlots callbacks.
        let mut hives: Vec<(i64, f32)> = world.live_beehive_ids_for_side(side)
            .filter_map(|id| {
                let position = world.ship_body(id)?.motion.position;
                Some((id, (position - squad.squad_center).length_squared()))
            }).collect();
        if let Some(chosen) = chosen_hives {
            // Source Heal.Execute preserves the supplied live Beehive list,
            // including its order; only the server dispatcher sorts by range.
            let live: HashMap<_, _> = hives.into_iter().collect();
            hives = chosen.iter().filter_map(|id|
                live.get(id).map(|&distance| (*id, distance))).collect();
        } else {
            hives.sort_by(|a, b| a.1.total_cmp(&b.1));
        }
        if hives.is_empty() { return Ok(None); }
        // A command that cannot find a live heal slot is simply
        // unavailable; it does not require gameplay-only metadata.
        for (id, deficit) in &mut candidates {
            let original = world.game_ship_original_health(*id)
                .ok_or(GameCommandError::MissingOriginalShipHealth(*id))?;
            *deficit -= original;
            if members[&*id].mobile {
                world.validate_ship_game_order(*id)?;
            }
        }
        // Stable sort retains source squad order for equal injury gaps.
        candidates.sort_by_key(|&(_, deficit)| deficit);
        let mut run = Self {
            hives: hives.into_iter().map(|(id, _)| id).collect(),
            pending: candidates.into_iter().map(|(id, _)| id).collect(),
            assigned: HashMap::new(),
            borrowed_at_start: HashSet::new(),
            waiting: Vec::new(),
            healing: Vec::new(),
            members,
            formation: squad.formation,
            healing_timer: None,
            cleanup_scratch: Vec::new(),
            released_scratch: HashSet::new(),
            earned_tsv: 0,
        };
        if let Err(err) = run.assign_slots(world) {
            run.release_startup_claims(world);
            return Err(err);
        }
        if run.assigned.is_empty() { return Ok(None); }
        run.register_arrivals(world);
        if let Err(err) = run.move_waiting(world) {
            run.release_startup_claims(world);
            return Err(err);
        }
        Ok(Some(run))
    }

    /// The only reservation allocator. World owns capacity shared across
    /// commands; the command maintains only its own pending/active order.
    fn assign_slots(&mut self, world: &mut CombatWorld)
        -> Result<(), GameCommandError>
    {
        for &hive in &self.hives {
            while !self.pending.is_empty() &&
                world.beehive_healing_slots_available(hive) > 0 {
                let id = self.pending.pop_front().expect("nonempty queue");
                let Some(ship) = world.ship(id) else { continue; };
                if ship.vitals.is_dead() ||
                    ship.vitals.health() >= ship.vitals.max_health() {
                    continue;
                }
                let already_reserved =
                    world.reserved_healing_beehive(id) == Some(hive);
                if world.try_reserve_beehive_healing_slot(id, hive)? {
                    self.assigned.insert(id, hive);
                    if already_reserved {
                        self.borrowed_at_start.insert(id);
                    }
                    self.waiting.push(id);
                } else {
                    // The previous command may still hold this ship at
                    // another hive during activation. Retain its FIFO
                    // priority for the next poll after supersession
                    // releases that old reservation; never spin here.
                    self.pending.push_front(id);
                    break;
                }
            }
        }
        Ok(())
    }

    fn register_arrivals(&mut self, world: &CombatWorld) {
        let mut i = 0;
        while i < self.waiting.len() {
            let id = self.waiting[i];
            let Some(&hive) = self.assigned.get(&id) else {
                i += 1;
                continue;
            };
            if world.ship_touches_beehive_healing_zone(id, hive) {
                self.healing.push(self.waiting.remove(i));
                if self.healing_timer.is_none() {
                    self.healing_timer = Some(ScaledTimer::new(1.0, true, false));
                }
            } else {
                i += 1;
            }
        }
    }

    fn release_unavailable(&mut self, world: &mut CombatWorld) {
        self.cleanup_scratch.clear();
        for &id in self.waiting.iter().chain(self.healing.iter()) {
            let hive = self.assigned.get(&id).copied();
            let (live, damaged) = world.ship(id).map_or((false, false), |s|
                (!s.vitals.is_dead(),
                    !s.vitals.is_dead() &&
                        s.vitals.health() < s.vitals.max_health())
            );
            let hive_dead = hive.is_some_and(|h| world.ship(h).is_none_or(|s|
                s.vitals.is_dead()));
            if hive_dead {
                self.cleanup_scratch.push((id, damaged));
            } else if !live || !damaged ||
                hive.is_none_or(|h|
                    world.reserved_healing_beehive(id) != Some(h)) {
                self.cleanup_scratch.push((id, false));
            }
        }
        self.released_scratch.clear();
        for &(id, requeue) in &self.cleanup_scratch {
            world.release_beehive_healing_slot(id);
            self.assigned.remove(&id);
            self.released_scratch.insert(id);
            if requeue { self.pending.push_back(id); }
        }
        if !self.released_scratch.is_empty() {
            self.waiting.retain(|id| !self.released_scratch.contains(id));
            self.healing.retain(|id| !self.released_scratch.contains(id));
        }
    }

    fn move_waiting(&self, world: &mut CombatWorld)
        -> Result<(), GameCommandError>
    {
        for &id in &self.waiting {
            let hive = *self.assigned.get(&id)
                .ok_or(GameCommandError::InvalidSquad)?;
            let member = self.members.get(&id)
                .ok_or(GameCommandError::InvalidSquad)?;
            if !member.mobile { continue; }
            let destination = world.ship_body(hive)
                .ok_or(GameCommandError::InvalidSquad)?
                .motion.position;
            world.move_ship_to_tracked_point(id, GamePointOrder {
                destination,
                ship_clearance: member.clearance,
                map_bounds: self.formation.map_bounds,
                has_obstacles: self.formation.has_obstacles,
                can_override_bounds: self.formation.can_override_bounds,
                newly_found_obstacle: false,
            }).map_err(|error| GameCommandError::Squad(
                SquadOrderError::Ship(error),
            ))?;
        }
        Ok(())
    }

    /// Roll back a failed activation without stealing slots from an
    /// existing command. Successful replacement transfers ownership.
    fn release_startup_claims(&mut self, world: &mut CombatWorld) {
        for &id in self.assigned.keys() {
            if !self.borrowed_at_start.contains(&id) {
                world.release_beehive_healing_slot(id);
            }
        }
        self.assigned.clear();
        self.waiting.clear();
        self.healing.clear();
        self.pending.clear();
    }

    /// Superseding Heal transfers any identical reservations to the
    /// already-validated replacement command. Old-only slots are released
    /// immediately; neither command can duplicate a shared Beehive slot.
    pub(super) fn release_superseded(
        &mut self, world: &mut CombatWorld, successor: Option<&HealRun>,
    ) {
        for (&id, &hive) in &self.assigned {
            let transferred = successor
                .and_then(|next| next.assigned.get(&id))
                == Some(&hive);
            if !transferred {
                world.release_beehive_healing_slot(id);
            }
        }
        self.assigned.clear();
        self.waiting.clear();
        self.healing.clear();
        self.pending.clear();
        self.healing_timer = None;
    }

    pub(super) fn release_all(&mut self, world: &mut CombatWorld) {
        for &id in self.assigned.keys() {
            world.release_beehive_healing_slot(id);
        }
        self.assigned.clear();
        self.waiting.clear();
        self.healing.clear();
        self.pending.clear();
        self.healing_timer = None;
    }

    /// GameState.RemoveShip calls Heal.ShipBecameUnavailable before the
    /// surviving Squad's next command timer. CombatWorld has already
    /// released the dead ship's physical Beehive reservation; reconcile
    /// this command's waiting/active queues and offer any freed slot to
    /// another pending member now, not up to three seconds later.
    pub(super) fn ship_became_unavailable(
        &mut self, world: &mut CombatWorld,
    ) -> Result<bool, GameCommandError> {
        self.pending.retain(|&id| world.ship(id).is_some_and(|ship|
            !ship.vitals.is_dead() &&
                ship.vitals.health() < ship.vitals.max_health()
        ));
        self.release_unavailable(world);
        self.assign_slots(world)?;
        Ok(self.pending.is_empty() && self.waiting.is_empty() &&
            self.healing.is_empty())
    }

    pub(super) fn advance(
        &mut self, world: &mut CombatWorld,
        command_poll: &mut Option<ScaledTimer>, dt: f32,
    ) -> Result<(Option<CommandFinishCause>, u64), GameCommandError> {
        self.register_arrivals(world);
        let mut callbacks = 0;
        if let Some(due) = command_poll.as_mut()
            .and_then(|timer| timer.begin_update(dt)) {
            callbacks += 1;
            self.release_unavailable(world);
            self.hives.retain(|&id| world.ship(id).is_some_and(|s|
                !s.vitals.is_dead()));
            if self.hives.is_empty() {
                return Ok((Some(CommandFinishCause::HealingBeehivesDestroyed), callbacks));
            }
            self.assign_slots(world)?;
            self.move_waiting(world)?;
            command_poll.as_mut().expect("poll timer exists").finish_update(due);
        }
        if let Some(due) = self.healing_timer.as_mut()
            .and_then(|timer| timer.begin_update(dt)) {
            callbacks += 1;
            self.release_unavailable(world);
            self.cleanup_scratch.clear();
            for &id in &self.healing {
                let hive = self.assigned[&id];
                match world.heal_reserved_command_ship(id, hive)? {
                    Some(gain) => {
                        self.earned_tsv = self.earned_tsv
                            .checked_add(i64::from(gain))
                            .ok_or(GameCommandError::InvalidCapabilityAccounting)?;
                    }
                    None => {}
                }
                if world.ship(id).is_some_and(|ship|
                    ship.vitals.health() >= ship.vitals.max_health()) {
                    self.cleanup_scratch.push((id, false));
                }
            }
            self.released_scratch.clear();
            for &(id, _) in &self.cleanup_scratch {
                world.release_beehive_healing_slot(id);
                self.assigned.remove(&id);
                self.released_scratch.insert(id);
            }
            if !self.released_scratch.is_empty() {
                self.healing.retain(|id| !self.released_scratch.contains(id));
            }
            self.assign_slots(world)?;
            self.healing_timer.as_mut()
                .expect("healing timer remains installed").finish_update(due);
        }
        if self.pending.is_empty() && self.waiting.is_empty() &&
            self.healing.is_empty() {
            return Ok((Some(CommandFinishCause::HealingCompleted), callbacks));
        }
        Ok((None, callbacks))
    }
}
