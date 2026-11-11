//! Campaign exit-zone triggers use the same live hulls and EndKill path.

use super::*;

#[derive(Clone, Copy, Debug)]
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

pub(super) struct ExtractionState {
    zone: ExtractionZone,
    active: bool,
    last_mobile_extracted: bool,
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
        self.extraction = Some(ExtractionState { zone, active: false, last_mobile_extracted: false });
        Ok(())
    }

    pub fn active_extraction_bounds(&self) -> Option<Aabb> {
        self.extraction.as_ref().filter(|state| state.active).map(|state| state.zone.bounds())
    }

    pub(crate) fn extraction_outcome(&self) -> Option<(i32, bool)> {
        let state = self.extraction.as_ref()?;
        let player = state.zone.player_side;
        let enemy = 3 - player;
        let player_eliminated = self.living_mobile_ships_on_side(player) == 0;
        if player_eliminated || self.living_mobile_ships_on_side(enemy) == 0 {
            Some((if player_eliminated && !state.last_mobile_extracted { enemy } else { player },
                player_eliminated && state.last_mobile_extracted))
        } else { None }
    }

    pub fn step_extraction(&mut self, collisions: &CollisionMatrix) -> Result<(), CombatWorldError> {
        let Some(state) = self.extraction.as_mut() else { return Ok(()) };
        let zone = state.zone;
        if !state.active {
            let radius_squared = zone.activation_radius * zone.activation_radius;
            state.active = self.ships.iter().zip(self.physics.ships()).any(|(ship, body)|
                body.active && ship.side == zone.player_side &&
                (body.motion.position - zone.center).length_squared() < radius_squared);
            // The source instantiates the trigger from a Level callback.
            // Its first contacts arrive on the next physics step.
            return Ok(());
        }
        let hits = self.physics.ship_ids_overlapping_box(zone.bounds())?
            .filter_map(|id| self.ship_by_id.get(&id).copied())
            .filter(|&index| self.ships[index].side == zone.player_side &&
                collisions.allows(zone.layer, self.physics.ships()[index].layer))
            .collect::<Vec<_>>();
        for index in hits {
            let ship = &mut self.ships[index];
            let ship_id = ship.entity_id;
            let preserved_tsv = ship.vitals.tsv().max(0);
            ship.vitals.kill_without_hit();
            if self.physics.ships()[index].motion.mobile &&
                self.living_mobile_ships_on_side(zone.player_side) == 1
            {
                self.extraction.as_mut().expect("configured extraction").last_mobile_extracted = true;
            }
            self.events.push(CombatEvent::ShipExtracted { ship_id, side: zone.player_side, preserved_tsv });
            self.retire_dead_ship_with_mode(index, 0, 0, true)?;
        }
        Ok(())
    }
}
