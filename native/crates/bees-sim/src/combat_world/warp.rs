//! Unity WarpGate.ShipsWarpingHere and FullRetreat participant lifecycle.
//! This is world-local gameplay state, not a second warp simulation.
//! The rendered WarpGate animation and headless clock use the same
//! authored ChangeSpriteLoop readiness point at 2.375 scaled seconds.

use super::*;
use super::policy::WARP_GATE_TYPE;

/// Sprites/Ships/Animations/Warp Gate Opening.anim: ChangeSpriteLoop event.
pub const WARP_GATE_OPENING_SECONDS: f32 = 2.375;

pub(super) struct GameWarpGateSession {
    elapsed: f32,
    participants: HashSet<i64>,
    /// The actual authored WarpCollider has been touched at least once.
    /// Unity FullRetreat retains a ship in ShipsWaitingToWarp after contact.
    waiting: HashSet<i64>,
    /// Authenticated warp completions are queued for the next fixed step,
    /// so the ordinary death/economic/terminal consumers see them once.
    committed: HashSet<i64>,
}

impl GameWarpGateSession {
    fn new() -> Self {
        Self {
            elapsed: 0.0,
            participants: HashSet::new(),
            waiting: HashSet::new(),
            committed: HashSet::new(),
        }
    }

    fn ready(&self) -> bool {
        self.elapsed >= WARP_GATE_OPENING_SECONDS
    }
}

impl CombatWorld {
    /// Authored live WarpGate registry: filtered from the same index used
    /// by policy action masks. The ID order matches GameState live order
    /// for stable command target selection by its owning Level adapter.
    pub fn live_warp_gate_ids_for_side(
        &self, side: i32,
    ) -> impl Iterator<Item = i64> + '_ {
        self.support_zone_ids
            .get(&(WARP_GATE_TYPE, side))
            .into_iter()
            .flat_map(|ids| ids.iter().copied())
            .filter(|id| self.ship(*id).is_some_and(|ship|
                !ship.vitals.is_dead() && ship.ship_type == WARP_GATE_TYPE
            ))
    }

    pub fn game_warp_gate_position(&self, gate_id: i64) -> Option<Vec2> {
        let &index = self.ship_by_id.get(&gate_id)?;
        let ship = &self.ships[index];
        let body = &self.physics.ships()[index];
        if ship.ship_type != WARP_GATE_TYPE ||
            ship.vitals.is_dead() || !body.active ||
            !self.support_zone_ids
                .get(&(WARP_GATE_TYPE, ship.side))
                .is_some_and(|ids| ids.binary_search(&gate_id).is_ok()) {
            return None;
        }
        Some(body.motion.position)
    }

    /// Exact child WarpCollider, not the WarpGate's primary hull.
    pub fn game_ship_touches_warp_gate(
        &self, ship_id: i64, gate_id: i64,
    ) -> bool {
        let Some(&ship_index) = self.ship_by_id.get(&ship_id) else {
            return false;
        };
        let Some(&gate_index) = self.ship_by_id.get(&gate_id) else {
            return false;
        };
        let ship = &self.ships[ship_index];
        let gate = &self.ships[gate_index];
        ship_id != gate_id && ship.side == gate.side &&
            gate.ship_type == WARP_GATE_TYPE &&
            ship.ship_type != WARP_GATE_TYPE &&
            !ship.vitals.is_dead() && !gate.vitals.is_dead() &&
            self.physics.ships()[ship_index].active &&
            self.physics.ships()[gate_index].active &&
            self.physics.ships()[gate_index].warp_zone_touches(
                &self.physics.ships()[ship_index],
            )
    }

    /// Reserve one live non-WarpGate ship for one gate. All FullRetreat
    /// commands share this ownership just as they share Unity's per-gate
    /// ShipsWarpingHere; only the first participant starts the opening.
    /// Duplicate registration at the same gate is idempotent.
    pub fn register_game_warp_participant(
        &mut self, ship_id: i64, gate_id: i64,
    ) -> Result<bool, CombatWorldError> {
        let ship = self.ship(ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        if ship.vitals.is_dead() || ship.ship_type == WARP_GATE_TYPE ||
            self.game_warp_gate_position(gate_id).is_none() ||
            self.ship(gate_id).is_none_or(|gate| gate.side != ship.side) {
            return Err(CombatWorldError::InvalidPolicyWarp);
        }
        if let Some(&already) = self.game_warp_gate_by_ship.get(&ship_id) {
            if already != gate_id {
                return Err(CombatWorldError::InvalidPolicyWarp);
            }
            return Ok(false);
        }
        self.game_warp_gate_by_ship.insert(ship_id, gate_id);
        self.game_warp_gate_sessions
            .entry(gate_id).or_insert_with(GameWarpGateSession::new)
            .participants.insert(ship_id);
        Ok(true)
    }

    pub fn game_warp_participant_gate(&self, ship_id: i64) -> Option<i64> {
        self.game_warp_gate_by_ship.get(&ship_id).copied()
    }

    /// Has this participant already completed the source WaitToWarp
    /// callback? The world still owns its pending next-fixed-step
    /// retirement, but no gameplay command should reissue movement.
    pub fn game_warp_participant_committed(
        &self, ship_id: i64, gate_id: i64,
    ) -> bool {
        self.game_warp_participant_gate(ship_id) == Some(gate_id) &&
            self.game_warp_gate_sessions.get(&gate_id)
                .is_some_and(|session| session.committed.contains(&ship_id))
    }

    /// A collision entry is checked against the *real* WarpCollider once.
    /// The waiting registration survives later drift, as in FullRetreat.
    pub fn queue_game_warp_contact(
        &mut self, ship_id: i64, gate_id: i64,
    ) -> bool {
        if self.game_warp_participant_gate(ship_id) != Some(gate_id) {
            return false;
        }
        // A source QueueShipForWarp contact remains waiting even after
        // drifting out of the WarpCollider. Checking its exact hull again
        // every fixed tick wastes collision work in long FullRetreat runs.
        if self.game_warp_gate_sessions.get(&gate_id)
            .is_some_and(|session| session.waiting.contains(&ship_id)) {
            return false;
        }
        if !self.game_ship_touches_warp_gate(ship_id, gate_id) {
            return false;
        }
        self.game_warp_gate_sessions.get_mut(&gate_id)
            .is_some_and(|session| session.waiting.insert(ship_id))
    }

    /// Read-only animation state for renderer and headless commands.
    pub fn game_warp_gate_ready(&self, gate_id: i64) -> bool {
        self.game_warp_gate_sessions.get(&gate_id)
            .is_some_and(GameWarpGateSession::ready) &&
            self.game_warp_gate_position(gate_id).is_some()
    }

    pub fn game_warp_gate_active(&self, gate_id: i64) -> bool {
        self.game_warp_gate_sessions.contains_key(&gate_id)
    }

    /// Inactive gates cost nothing per fixed step. Unlike policy training's
    /// immediate contact warp, FullRetreat waits for the authored
    /// animation's ChangeSpriteLoop event on the scaled world clock.
    pub(super) fn advance_game_warp_gate_animations(&mut self, dt: f32) {
        for session in self.game_warp_gate_sessions.values_mut() {
            session.elapsed =
                (session.elapsed + dt).min(WARP_GATE_OPENING_SECONDS);
        }
    }

    /// A ready, physically queued participant is committed for the NEXT
    /// fixed combat step. Unity's command timer executes between physics
    /// ticks; retirement in the next fixed step preserves the existing
    /// roster, economic, terminal and policy attribution sequence.
    /// Never retire a gameplay ship after that sequence has already run.
    pub fn complete_game_warp(
        &mut self, ship_id: i64, gate_id: i64,
    ) -> Result<bool, CombatWorldError> {
        if !self.game_warp_gate_ready(gate_id) ||
            self.game_warp_participant_gate(ship_id) != Some(gate_id) ||
            !self.game_warp_gate_sessions.get(&gate_id)
                .is_some_and(|session| session.waiting.contains(&ship_id)) {
            return Ok(false);
        }
        let Some(ship) = self.ship(ship_id) else { return Ok(false) };
        if ship.vitals.is_dead() { return Ok(false); }
        let Some(session) = self.game_warp_gate_sessions.get_mut(&gate_id)
        else { return Ok(false) };
        if !session.committed.insert(ship_id) {
            return Ok(false);
        }
        self.pending_game_warps.push((ship_id, gate_id));
        Ok(true)
    }

    /// Applies only source-authorized returns at the earliest ordinary
    /// fixed-step event boundary. Stable IDs make simultaneous gate
    /// callbacks deterministic across parallel headless worlds.
    pub(super) fn apply_pending_game_warps(
        &mut self,
    ) -> Result<(), CombatWorldError> {
        self.pending_game_warps.sort_unstable();
        for i in 0..self.pending_game_warps.len() {
            let (ship_id, gate_id) = self.pending_game_warps[i];
            if self.game_warp_participant_gate(ship_id) != Some(gate_id) ||
                !self.game_warp_gate_ready(gate_id) ||
                !self.game_warp_gate_sessions.get(&gate_id)
                    .is_some_and(|session| session.committed.contains(&ship_id)) {
                continue;
            }
            let Some(ship) = self.ship(ship_id) else { continue };
            if ship.vitals.is_dead() { continue; }
            self.end_kill_warp_ship(ship_id, gate_id)?;
        }
        self.pending_game_warps.clear();
        Ok(())
    }

    /// Source FullRetreat.SetFinalize removes only its own participants.
    /// Returning the gate to the inactive state when the final participant
    /// leaves resets animation readiness for the next command.
    pub fn release_game_warp_participant(&mut self, ship_id: i64) -> bool {
        let Some(gate_id) = self.game_warp_gate_by_ship.remove(&ship_id)
        else { return false };
        let empty = if let Some(session) =
            self.game_warp_gate_sessions.get_mut(&gate_id) {
            session.participants.remove(&ship_id);
            session.waiting.remove(&ship_id);
            session.committed.remove(&ship_id);
            session.participants.is_empty()
        } else { false };
        if empty {
            self.game_warp_gate_sessions.remove(&gate_id);
        }
        true
    }

    /// Gate death is rare; ordinary reservations use O(1) release.
    pub(super) fn retire_game_warp_gate(&mut self, gate_id: i64) {
        if let Some(session) = self.game_warp_gate_sessions.remove(&gate_id) {
            for ship_id in session.participants {
                self.game_warp_gate_by_ship.remove(&ship_id);
            }
        }
    }

    /// Shared physical warp retirement for training policy, live-game
    /// policy and FullRetreat. Policy decides immediate vs animation-ready
    /// timing outside this method; all paths preserve the same TSV and
    /// emit ShipWarped before ShipDied, without attributed combat damage.
    pub(super) fn end_kill_warp_ship(
        &mut self, ship_id: i64, gate_id: i64,
    ) -> Result<(), CombatWorldError> {
        let &index = self.ship_by_id.get(&ship_id)
            .ok_or(CombatWorldError::UnknownShip)?;
        let side = self.ships[index].side;
        let preserved_tsv = self.ships[index].vitals.tsv().max(0);
        self.ships[index].vitals.kill_without_hit();
        self.events.push(CombatEvent::ShipWarped {
            ship_id, gate_id, side, preserved_tsv,
        });
        self.retire_dead_ship_with_mode(index, 0, 0, true)
    }
}
