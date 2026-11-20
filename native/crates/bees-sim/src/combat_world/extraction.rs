//! Campaign exit-zone triggers use the same live hulls and EndKill path.

use super::*;

#[derive(Clone, Copy, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct ExtractionZone {
    pub player_side: i32,
    pub center: Vec2,
    pub half_extents: Vec2,
    pub activation_radius: f32,
    pub layer: u8,
}

impl ExtractionZone {
    fn bounds(self) -> Aabb {
        Aabb { min: self.center - self.half_extents, max: self.center + self.half_extents }
    }
}

#[derive(Clone, Copy, Debug, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct ExtractionState {
    zone: ExtractionZone,
    active: bool,
    last_mobile_extracted: bool,
    escort_ship_id: Option<i64>,
    escort_delivered: bool,
}

impl ExtractionState {
    /// Authored trigger and escort objective are immutable for this Level.
    /// Activation and delivery are actual source gameplay state.
    pub(super) fn valid_recovery(&self, authored: &Self) -> bool {
        self.zone == authored.zone &&
        self.escort_ship_id == authored.escort_ship_id &&
        (!self.escort_delivered || self.escort_ship_id.is_some()) &&
        (!self.escort_delivered || self.active)
    }
}

impl CombatWorld {
    pub fn configure_extraction(&mut self, zone: ExtractionZone) -> Result<(), CombatWorldError> {
        let bounds = zone.bounds();
        if self.extraction.is_some() || !matches!(zone.player_side, 1 | 2) || zone.layer >= 32 ||
            !zone.activation_radius.is_finite() || zone.activation_radius <= 0.0 ||
            !(zone.activation_radius * zone.activation_radius).is_finite() ||
            !bounds.min.is_finite() || !bounds.max.is_finite() ||
            bounds.min.x >= bounds.max.x || bounds.min.y >= bounds.max.y
        {
            return Err(CombatWorldError::InvalidExtractionZone);
        }
        self.extraction = Some(ExtractionState {
            zone, active: false, last_mobile_extracted: false,
            escort_ship_id: None, escort_delivered: false,
        });
        Ok(())
    }

    /// Bind the existing physical trigger to one authored mobile ship.
    /// The terminal retains only the objective identity and clock.
    pub fn configure_escort_target(&mut self, ship_id: i64) -> Result<(), CombatWorldError> {
        let state = self.extraction.as_ref().ok_or(CombatWorldError::InvalidExtractionZone)?;
        if state.escort_ship_id.is_some() ||
            self.ship(ship_id).is_none_or(|ship|
                ship.side != state.zone.player_side || ship.vitals.is_dead()) ||
            self.ship_body(ship_id).is_none_or(|body| !body.active || !body.motion.mobile)
        {
            return Err(CombatWorldError::InvalidExtractionZone);
        }
        self.extraction.as_mut().expect("validated exit").escort_ship_id = Some(ship_id);
        Ok(())
    }

    pub fn escort_delivery_confirmed(&self, ship_id: i64) -> bool {
        self.extraction.as_ref().is_some_and(|state|
            state.escort_ship_id == Some(ship_id) && state.escort_delivered)
    }

    /// The destination is mission-authored and visible to the escorting
    /// commander even before the first nearby ship activates the collider.
    /// This accessor never changes physics activation or exit processing.
    /// Source-authored extraction objective and its destination, whether
    /// the physical exit trigger has activated yet or not. This read-only
    /// contract is shared by gameplay HUD and policy perception; actual
    /// extraction still occurs exclusively through step_extraction.
    pub fn extraction_objective(&self) -> Option<(i32, Aabb, bool)> {
        self.extraction.as_ref().map(|state| (
            state.zone.player_side, state.zone.bounds(),
            state.escort_ship_id.is_some(),
        ))
    }

    pub fn escorted_destination_bounds(&self) -> Option<Aabb> {
        self.extraction.as_ref()
            .filter(|state| state.escort_ship_id.is_some())
            .map(|state| state.zone.bounds())
    }

    pub fn active_extraction_bounds(&self) -> Option<Aabb> {
        self.extraction.as_ref().filter(|state| state.active).map(|state| state.zone.bounds())
    }

    pub(crate) fn extraction_outcome(&self) -> Option<(i32, bool)> {
        let state = self.extraction.as_ref()?;
        if state.escort_ship_id.is_some() { return None; }
        let player = state.zone.player_side;
        let enemy = 3 - player;
        // A physical faction with no *deployed* mobile ships may still
        // have an authored reinforcement wave. Do not let extraction
        // bypass the same reserved-hull elimination rule used by normal
        // battles and demolition/escort: the pending ship belongs to the
        // canonical world, not an imagined training-only spectator.
        let player_eliminated = self.living_mobile_ships_on_side(player) == 0 &&
            !self.has_reserved_future_ships_on_side(player);
        let enemy_eliminated = self.living_mobile_ships_on_side(enemy) == 0 &&
            !self.has_reserved_future_ships_on_side(enemy);
        if player_eliminated || enemy_eliminated {
            Some((if player_eliminated && !state.last_mobile_extracted { enemy } else { player },
                player_eliminated && state.last_mobile_extracted))
        } else { None }
    }

    pub fn step_extraction(&mut self, collisions: &CollisionMatrix) -> Result<(), CombatWorldError> {
        let Some(state) = self.extraction.as_ref() else { return Ok(()) };
        let zone = state.zone;
        if !state.active {
            let radius_squared = zone.activation_radius * zone.activation_radius;
            let activated = self.ships.iter().zip(self.physics.ships()).any(|(ship, body)|
                body.active && ship.side == zone.player_side &&
                (body.motion.position - zone.center).length_squared() < radius_squared);
            if activated {
                self.extraction.as_mut().expect("configured zone").active = true;
            }
            // The source instantiates the trigger from a Level callback.
            // Its first contacts arrive on the next physics step.
            return Ok(());
        }
        // Snapshot the trigger's immutable identity before asking other
        // CombatWorld subsystems about reinforcements or collider overlaps.
        // The extraction state is only mutably borrowed for the immediate
        // state transition, never throughout the collision/retirement walk.
        let escort_ship_id = state.escort_ship_id;
        // The final evacuation flag is valid only after the last mobile
        // *including authored future waves* has left. Otherwise a late
        // reserve could be destroyed while an earlier exit remains
        // incorrectly marked as a successful full-fleet extraction.
        let pending_friendly_reinforcements =
            self.has_reserved_future_ships_on_side(zone.player_side);
        let hits = self.physics.ship_ids_overlapping_box(zone.bounds())?
            .filter_map(|id| self.ship_by_id.get(&id).copied())
            .filter(|&index| self.ships[index].side == zone.player_side &&
                escort_ship_id.is_none_or(|id| self.ships[index].entity_id == id) &&
                collisions.allows(zone.layer, self.physics.ships()[index].layer))
            .collect::<Vec<_>>();
        for index in hits {
            let ship = &mut self.ships[index];
            let ship_id = ship.entity_id;
            let preserved_tsv = ship.vitals.tsv().max(0);
            if self.extraction.as_ref().is_some_and(|state|
                state.escort_ship_id == Some(ship_id)
            ) {
                self.extraction.as_mut().expect("configured exit").escort_delivered = true;
            }
            ship.vitals.kill_without_hit();
            if self.physics.ships()[index].motion.mobile &&
                self.living_mobile_ships_on_side(zone.player_side) == 1 &&
                !pending_friendly_reinforcements
            {
                self.extraction.as_mut().expect("configured extraction").last_mobile_extracted = true;
            }
            self.events.push(CombatEvent::ShipExtracted { ship_id, side: zone.player_side, preserved_tsv });
            self.retire_dead_ship_with_mode(index, 0, 0, true)?;
        }
        Ok(())
    }
}
