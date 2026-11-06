//! Source FullRetreat command: one target WarpGate, participant reservations,
//! exact WarpCollider contact, 3-second tracked movement and 2-second
//! animation-ready polling. Physical warps remain in CombatWorld's normal
//! fixed-step event/reward/retirement path, never in command callbacks.

use super::*;
use bees_sim::combat_world::{CombatEvent, GamePointOrder};
use std::collections::HashSet;

const WAIT_FOR_WARP_SECONDS: f32 = 2.0;
// Unity ConfigData.ShipTypes: Drone=8, Striker=19. These two retreating
// ship types subtract their existing TSV from the command's earned TSV.
const DRONE_TYPE: u8 = 8;
const STRIKER_TYPE: u8 = 19;

pub(super) struct FullRetreatRun {
    gate_id: i64,
    members: Vec<SquadFormationMember>,
    formation: SquadFormationOptions,
    /// Preserves source squad registration order; a ship remains listed
    /// until the next authoritative fixed tick actually retires it.
    participants: Vec<i64>,
    warp_poll: ScaledTimer,
    /// Warp event settlement is idempotent across delivery boundaries.
    accounted_warps: HashSet<i64>,
    pub(super) earned_tsv: i64,
}

impl FullRetreatRun {
    pub(super) fn gate_id(&self) -> i64 { self.gate_id }

    pub(super) fn start(
        world: &mut CombatWorld, random: &mut RandomStream,
        squad: NativeSquadOrder<'_>,
        replacing_gate: Option<i64>,
    ) -> Result<Option<Self>, GameCommandError> {
        let mut side = None;
        let mut members = Vec::new();
        let mut participants = Vec::new();
        let mut unique = HashSet::with_capacity(squad.members.len());
        for &member in squad.members {
            if !unique.insert(member.ship_id) {
                return Err(GameCommandError::InvalidSquad);
            }
            let ship = world.ship(member.ship_id)
                .ok_or(GameCommandError::InvalidSquad)?;
            if ship.vitals.is_dead() { continue; }
            if side.is_some_and(|other| other != ship.side) {
                return Err(GameCommandError::InvalidSquad);
            }
            side = Some(ship.side);
            if ship.ship_type == 20 { continue; }
            if member.mobile {
                world.validate_ship_game_order(member.ship_id)?;
            }
            participants.push(member.ship_id);
            members.push(member);
        }
        if participants.is_empty() { return Ok(None); }
        let side = side.ok_or(GameCommandError::InvalidSquad)?;
        let mut closest = None;
        let mut best_distance = f32::INFINITY;
        for gate_id in world.live_warp_gate_ids_for_side(side) {
            let Some(position) = world.game_warp_gate_position(gate_id) else {
                continue;
            };
            let distance = (position - squad.squad_center).length_squared();
            if closest.is_none() || distance < best_distance {
                best_distance = distance;
                closest = Some(gate_id);
            }
        }
        let Some(gate_id) = closest else { return Ok(None); };
        // A ship already held by another command/gate is not silently
        // stolen. Same-owner replacement at this gate transfers its
        // existing registration without resetting the opening animation.
        for &id in &participants {
            if let Some(held) = world.game_warp_participant_gate(id) {
                if held != gate_id || replacing_gate != Some(gate_id) {
                    return Err(GameCommandError::InvalidSquad);
                }
            }
        }
        let mut acquired = Vec::new();
        for &id in &participants {
            match world.register_game_warp_participant(id, gate_id) {
                Ok(new) => {
                    if new { acquired.push(id); }
                    world.queue_game_warp_contact(id, gate_id);
                }
                Err(error) => {
                    for ship_id in acquired {
                        world.release_game_warp_participant(ship_id);
                    }
                    return Err(error.into());
                }
            }
        }
        let run = Self {
            gate_id, members, formation: squad.formation, participants,
            warp_poll: ScaledTimer::new(WAIT_FOR_WARP_SECONDS, true, false),
            accounted_warps: HashSet::new(), earned_tsv: 0,
        };
        if let Err(error) = run.move_to_gate(world, random) {
            for ship_id in acquired {
                world.release_game_warp_participant(ship_id);
            }
            return Err(error);
        }
        Ok(Some(run))
    }

    fn move_to_gate(
        &self, world: &mut CombatWorld, random: &mut RandomStream,
    ) -> Result<(), GameCommandError> {
        let position = world.game_warp_gate_position(self.gate_id)
            .ok_or(GameCommandError::InvalidSquad)?;
        // Utilities.RandomInt(6) * Vector2.one, not a uniform disk.
        let offset = (next_unit(random) * 6.0).floor().min(5.0);
        let destination = position + Vec2::new(offset, offset);
        for member in &self.members {
            if !self.participants.contains(&member.ship_id) ||
                !member.mobile || !world.ship(member.ship_id)
                    .is_some_and(|ship| !ship.vitals.is_dead()) {
                continue;
            }
            world.move_ship_to_tracked_point(member.ship_id, GamePointOrder {
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

    /// Called before the roster retires any squad on ShipDied. Only a
    /// real source-authorized ShipWarped event can subtract the preserved
    /// TSV of a returned Striker/Drone; queuing an attempt is not enough.
    pub(super) fn record_warp_event(
        &mut self, event: &CombatEvent, world: &CombatWorld,
    ) {
        let CombatEvent::ShipWarped {
            ship_id, gate_id, preserved_tsv, ..
        } = event else { return };
        if *gate_id != self.gate_id ||
            !self.participants.contains(ship_id) ||
            !self.accounted_warps.insert(*ship_id) {
            return;
        }
        if world.ship(*ship_id).is_some_and(|ship|
            matches!(ship.ship_type, DRONE_TYPE | STRIKER_TYPE)) {
            self.earned_tsv = self.earned_tsv
                .saturating_sub(i64::from(*preserved_tsv));
        }
    }

    /// A replacement inherits only same-gate/same-ship registrations.
    /// Never cancel its pending fixed-step warps or the shared animation.
    pub(super) fn release_all(
        &self, world: &mut CombatWorld, successor: Option<&Self>,
    ) {
        for &id in &self.participants {
            if successor.is_some_and(|next|
                next.gate_id == self.gate_id &&
                next.participants.contains(&id)) {
                continue;
            }
            world.release_game_warp_participant(id);
        }
    }

    pub(super) fn advance(
        &mut self, world: &mut CombatWorld,
        random: &mut RandomStream,
        command_poll: &mut Option<ScaledTimer>, dt: f32,
    ) -> Result<(Option<CommandFinishCause>, u64), GameCommandError> {
        if world.game_warp_gate_position(self.gate_id).is_none() {
            return Ok((Some(CommandFinishCause::WarpGateDestroyed), 0));
        }
        let mut callbacks = 0;
        self.participants.retain(|&id| {
            let alive = world.ship(id)
                .is_some_and(|ship| !ship.vitals.is_dead());
            if !alive { world.release_game_warp_participant(id); }
            alive && world.game_warp_participant_gate(id) == Some(self.gate_id)
        });
        if self.participants.is_empty() {
            return Ok((Some(CommandFinishCause::AllWarpParticipantsGone), 0));
        }
        // The collision enters before the timer. Once queued, a ship
        // remains waiting even if it drifts away before animation readiness.
        for &id in &self.participants {
            world.queue_game_warp_contact(id, self.gate_id);
        }
        if let Some(due) = self.warp_poll.begin_update(dt) {
            callbacks += 1;
            if world.game_warp_gate_ready(self.gate_id) {
                for &id in &self.participants {
                    world.complete_game_warp(id, self.gate_id)?;
                }
            }
            self.warp_poll.finish_update(due);
        }
        if let Some(due) = command_poll.as_mut()
            .and_then(|timer| timer.begin_update(dt)) {
            callbacks += 1;
            self.move_to_gate(world, random)?;
            command_poll.as_mut().expect("active FullRetreat command timer")
                .finish_update(due);
        }
        Ok((None, callbacks))
    }
}
