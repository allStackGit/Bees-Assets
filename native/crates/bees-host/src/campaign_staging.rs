//! Source Titania II player-fleet staging before the native Level is built.
//! This is one-time authored mission setup, never a second movement engine.

use std::collections::HashMap;

use bees_gameplay::player_inputs::NativeLevelPlayerContext;
use bees_sim::geometry::Vec2;
use bees_sim::ship_assets::HUMAN_TARGET_TYPE;
use bees_sim::squad_movement::authored_placement_offset;

use crate::policy_only_metadata::NativePolicyOnlyMetadata;
use crate::scene_builder::{NativeAuthoredEntity, NativeAuthoredLevel, NativeAuthoredShip};

const TITANIA_CENTER: Vec2 = Vec2::new(-32.0, 55.0);

struct Placement {
    index: usize,
    position: Vec2,
    size: Vec2,
    ship_type: u8,
    source_offset: Vec2,
}

fn geometry(slots: &[Placement]) -> (Vec2, f32, f32) {
    let mut left = f32::INFINITY;
    let mut right = f32::NEG_INFINITY;
    let mut bottom = f32::INFINITY;
    let mut top = f32::NEG_INFINITY;
    for slot in slots {
        left = left.min(slot.position.x - slot.size.x * 0.5);
        right = right.max(slot.position.x + slot.size.x * 0.5);
        bottom = bottom.min(slot.position.y - slot.size.y * 0.5);
        top = top.max(slot.position.y + slot.size.y * 0.5);
    }
    (Vec2::new((left + right) * 0.5, (bottom + top) * 0.5),
     right - left, top - bottom)
}

fn place_at_center(radius: f32, occupied: &[(Vec2, f32)],
                   min: Vec2, max: Vec2) -> Vec2 {
    // Titania2Beenoculars.FindTitania2HumanSquadPlacement: ring-first,
    // x-major/y-minor, preserving Unity's first clear candidate tie-break.
    for ring in 1i32..=12 {
        for x in -ring..=ring {
            for y in -ring..=ring {
                if x.abs().max(y.abs()) != ring { continue; }
                let candidate = TITANIA_CENTER + Vec2::new(x as f32 * 24.0, y as f32 * 24.0);
                if candidate.x - radius < min.x || candidate.x + radius > max.x ||
                    candidate.y - radius < min.y || candidate.y + radius > max.y {
                    continue;
                }
                let distance = (candidate - TITANIA_CENTER).length_squared().sqrt();
                if distance < 22.0 + radius { continue; }
                if occupied.iter().any(|&(center, prior_radius)|
                    (candidate - center).length_squared().sqrt() < radius + prior_radius
                ) {
                    continue;
                }
                return candidate;
            }
        }
    }
    // Unity's authored fallback when the map cannot fit another full ring.
    TITANIA_CENTER + Vec2::new(0.0, -(22.0 + radius))
}

impl<A> NativeAuthoredLevel<NativePolicyOnlyMetadata, A> {
    /// Unity StageTitania2HumanFleetAtCenter runs BEFORE the briefing and
    /// before its four initial enemy arrivals. Reposition real player squads
    /// only when the source snapshot has not already staged them (the source
    /// StartingPositions entry is the authoritative signal).
    ///
    /// This reproduces the obstacle-free current mission. An obstacle map
    /// needs the source's collider-aware placement; reject an unstaged
    /// snapshot instead of silently choosing an unsafe position.
    pub(crate) fn stage_titania2_player_fleet(
        &mut self, player_side: i32,
    ) -> Result<(), &'static str> {
        if !(1..=2).contains(&player_side) {
            return Err("Titania II needs source player side 1 or 2");
        }
        let index = (player_side - 1) as usize;
        let start_positions = self.metadata.hivemind_starting_positions
            .ok_or("Titania II needs source Stage starting positions")?;
        if start_positions[index].unity_equal(TITANIA_CENTER) {
            // Already positioned in a captured live Unity campaign scene.
            return Ok(());
        }
        if self.metadata.player_formation_options().has_obstacles {
            return Err("Obstacle-bearing Titania II needs a source-staged player fleet");
        }

        let mut index_by_id = HashMap::new();
        for (index, entity) in self.entities.iter().enumerate() {
            if let NativeAuthoredEntity::Ship(ship) = entity {
                index_by_id.insert(ship.entity_id, index);
            }
        }
        let mut occupied: Vec<(Vec2, f32)> = Vec::new();
        let mut slots = Vec::new();
        let bounds = self.broadphase_bounds;
        // Level.State.GetSquadsBySide preserves this authored squad order.
        for squad in &self.squads {
            if squad.side != player_side || squad.members.is_empty() { continue; }
            slots.clear();
            for &id in &squad.members {
                let &index = index_by_id.get(&id)
                    .ok_or("Titania II player squad references an absent ship")?;
                let NativeAuthoredEntity::Ship(NativeAuthoredShip {
                    ship_type, motion, ..
                }) = &self.entities[index] else {
                    return Err("Titania II player squad references a non-ship entity");
                };
                let size = self.metadata.ship_catalog.sprite_size(*ship_type)
                    .ok_or("Titania II player ship has no authored sprite dimensions")?;
                let offset = self.metadata.stored_formation_offset(id)
                    .ok_or("Titania II player ship has no source formation offset")?;
                if !offset.is_finite() {
                    return Err("Titania II player formation offset is nonfinite");
                }
                slots.push(Placement {
                    index, position: motion.position, size,
                    ship_type: *ship_type, source_offset: offset,
                });
            }
            // Unity creates the protected HumanTarget only AFTER staging
            // the player's pre-existing fleet. The imported native target
            // must remain anchored at the defended center, not be relocated.
            if slots.iter().any(|slot| slot.ship_type == HUMAN_TARGET_TYPE) {
                continue;
            }
            let (_, width, height) = geometry(&slots);
            let radius = width.max(height) * 0.5 + 8.0;
            if !radius.is_finite() { return Err("Titania II fleet size is nonfinite"); }
            let chosen = place_at_center(radius, &occupied, bounds.min, bounds.max);
            for slot in &mut slots {
                slot.position = chosen + authored_placement_offset(
                    slot.ship_type, slots.len(), slot.source_offset,
                );
                if !slot.position.is_finite() {
                    return Err("Titania II fleet placement is nonfinite");
                }
            }
            let (actual_center, actual_width, actual_height) = geometry(&slots);
            occupied.push((actual_center, actual_width.max(actual_height) * 0.5 + 8.0));
            for slot in &slots {
                let NativeAuthoredEntity::Ship(ship) = &mut self.entities[slot.index] else {
                    unreachable!("validated ship entity");
                };
                ship.motion.position = slot.position;
            }
        }
        self.metadata.hivemind_starting_positions.as_mut()
            .expect("source starting positions validated")[index] = TITANIA_CENTER;
        Ok(())
    }
}
