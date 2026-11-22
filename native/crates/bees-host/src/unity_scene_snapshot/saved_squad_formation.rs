//! Source SquadMaker formation layouts applied to the real campaign fleet.
//! This is a setup-time edit of the SAME physical scene used by play,
//! headless execution and training. No separate presentation formation exists.

use super::*;
use serde::{Deserialize, Serialize};

const SLOT_SPACING: f32 = 15.0 * 1.05;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum NativeSquadFormationKind {
    Authored,
    Line,
    Box,
    Rectangle,
    Pyramid,
    HollowPyramid,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct NativeSavedSquadFormation {
    pub kind: NativeSquadFormationKind,
    /// Unity's ConfigData.Configuration.MaxSquadWidth. For game-created
    /// squads the player can change this without requiring Unity settings.
    pub max_width: u8,
    /// Source SquadMaker permits rotating the deployed formation. Quarter
    /// turns avoid platform-dependent trigonometry in the physical scene.
    #[serde(default)]
    pub clockwise_quarters: u8,
    /// Reverse the saved ship order across the source formation's X axis.
    #[serde(default)]
    pub mirrored: bool,
    /// Persistent SquadMaker deployment displacement in authored world units.
    /// Applied to the complete physical Carrier family at scene setup only.
    #[serde(default)]
    pub translation: [i16; 2],
}

impl NativeSavedSquadFormation {
    pub fn authored() -> Self {
        Self {
            kind: NativeSquadFormationKind::Authored, max_width: 5,
            clockwise_quarters: 0, mirrored: false, translation: [0, 0],
        }
    }

    pub fn is_valid(self) -> bool {
        (1..=32).contains(&self.max_width) && self.clockwise_quarters < 4 &&
            self.translation.iter().all(|&n| (-4096..=4096).contains(&i32::from(n))) &&
            (self.kind != NativeSquadFormationKind::Authored ||
                (self.clockwise_quarters == 0 && !self.mirrored))
    }

    pub fn cycle_kind(self) -> Self {
        use NativeSquadFormationKind::*;
        let kind = match self.kind {
            Authored => Line,
            Line => Box,
            Box => Rectangle,
            Rectangle => Pyramid,
            Pyramid => HollowPyramid,
            HollowPyramid => Authored,
        };
        if kind == Authored {
            Self { kind, clockwise_quarters: 0, mirrored: false, ..self }
        } else { Self { kind, ..self } }
    }

    pub fn rotate_clockwise(self) -> Self {
        if self.kind == NativeSquadFormationKind::Authored { return self; }
        Self { clockwise_quarters: (self.clockwise_quarters + 1) % 4, ..self }
    }

    pub fn mirror(self) -> Self {
        if self.kind == NativeSquadFormationKind::Authored { return self; }
        Self { mirrored: !self.mirrored, ..self }
    }

    pub fn move_deployment(self, dx: i16, dy: i16) -> Self {
        let next = |old: i16, delta: i16| old.saturating_add(delta).clamp(-4096, 4096);
        Self {
            translation: [next(self.translation[0], dx), next(self.translation[1], dy)],
            ..self
        }
    }

    /// The campaign editor's illustrative layout uses precisely the same
    /// slot algorithm as physical deployment. Source-authored formations
    /// intentionally have no synthetic preview positions.
    pub fn preview_slots(self, members: usize) -> Option<Vec<Vec2>> {
        if self.kind == NativeSquadFormationKind::Authored { return None; }
        source_formation_slots(self, members).ok()
    }

    pub fn label(self) -> &'static str {
        use NativeSquadFormationKind::*;
        match self.kind {
            Authored => "source",
            Line => "line",
            Box => "box",
            Rectangle => "rectangle",
            Pyramid => "pyramid",
            HollowPyramid => "hollow-pyramid",
        }
    }
}

/// Match the original Dropper.LineMaker, MultiLine, Box, Rectangle and
/// Pyramid placement rules: center-out horizontal ordering, 1.05*ShipOffset,
/// and the hollow-row rule that leaves interior source icons unplaced until
/// a subsequent row. An overfull formation is rejected rather than silently
/// deleting persistent FleetShips as the Unity drag workspace can.
/// Emit one source Dropper row, leaving hollow interior ships pending for
/// subsequent rows without moving, dropping or duplicating a FleetShip.
fn emit_source_row(
    pending: &mut Vec<usize>, positions: &mut [Option<Vec2>],
    max_width: usize, requested: usize, hollow: bool, row: usize,
) {
    let columns = requested.min(max_width);
    let available = columns.min(pending.len());
    for slot in 0..available {
        if hollow && columns >= 3 && slot < columns - 2 { continue; }
        let steps = if slot % 2 == 0 {
            (slot / 2) as f32
        } else {
            -((slot + 1) / 2) as f32
        };
        positions[pending[slot]] = Some(Vec2::new(
            steps * SLOT_SPACING, -(row as f32) * SLOT_SPACING,
        ));
    }
    pending.retain(|&id| positions[id].is_none());
}

fn source_formation_slots(
    formation: NativeSavedSquadFormation, members: usize,
) -> Result<Vec<Vec2>, NativeSnapshotError> {
    let invalid = NativeSnapshotError::InvalidNativeLayout;
    if !formation.is_valid() || members > 1024 {
        return Err(invalid("invalid native saved squad formation width or size"));
    }
    if members == 0 { return Ok(Vec::new()); }
    let width = usize::from(formation.max_width);
    let mut pending: Vec<usize> = (0..members).collect();
    let mut positions = vec![None; members];
    let mut row = 0_usize;
    use NativeSquadFormationKind::*;
    match formation.kind {
        Authored => return Err(invalid("authored formation requires no squad repositioning")),
        Line => {
            while !pending.is_empty() && row < members {
                emit_source_row(&mut pending, &mut positions, width, width, false, row);
                row += 1;
            }
        }
        Box => {
            let columns = if members < 4 { members }
                else if members == 4 { 2 }
                else if members < 10 { 3 }
                else if members < 17 { 4 }
                else { 5 };
            while !pending.is_empty() && row < members {
                emit_source_row(&mut pending, &mut positions, width, columns, false, row);
                row += 1;
            }
        }
        Rectangle => {
            if members < 5 {
                emit_source_row(&mut pending, &mut positions, width, members, false, row);
            } else {
                let columns = if members < 11 {
                    (members / 2).clamp(3, width.max(3))
                } else {
                    ((members - 4) / 2).clamp(3, width.max(3))
                };
                emit_source_row(&mut pending, &mut positions, width, columns, false, row);
                row += 1;
                emit_source_row(&mut pending, &mut positions, width, columns, true, row);
                row += 1;
                if members >= 11 {
                    emit_source_row(&mut pending, &mut positions, width, columns, true, row);
                    row += 1;
                    let final_width = pending.len().max(columns);
                    emit_source_row(&mut pending, &mut positions, width, final_width, false, row);
                } else {
                    emit_source_row(&mut pending, &mut positions, width, columns, false, row);
                }
            }
        }
        Pyramid | HollowPyramid => {
            while !pending.is_empty() && row < members {
                emit_source_row(&mut pending, &mut positions, width, row.saturating_mul(2).saturating_add(1),
                    formation.kind == HollowPyramid, row);
                row += 1;
            }
        }
    }
    let mut slots = positions.into_iter().collect::<Option<Vec<_>>>().ok_or(
        invalid("formation cannot place every persistent FleetShip within the requested width"),
    )?;
    // Modify only setup-time slot geometry. Same per-ship IDs, physical
    // motion, Carrier descendants and authoritative weapon ownership survive.
    for slot in &mut slots {
        if formation.mirrored { slot.x = -slot.x; }
        *slot = match formation.clockwise_quarters {
            0 => *slot,
            1 => Vec2::new(slot.y, -slot.x),
            2 => Vec2::new(-slot.x, -slot.y),
            3 => Vec2::new(-slot.y, slot.x),
            _ => unreachable!("validated formation quarter turn"),
        };
    }
    Ok(slots)
}

impl NativePhysicalScene {
    /// Reposition all selected SavedSquad hulls after purchases, original
    /// reassignment and death filtering. Parent Carriers move their authentic
    /// free children by the same displacement, without cloning or rebuilding
    /// any physical gameplay state. Each adjusted member gets a true new
    /// formation offset, used by both player and scripted movement.
    pub fn apply_campaign_formations(
        &mut self, side: i32, settings: &[NativeSavedSquadTactics],
    ) -> Result<usize, NativeSnapshotError> {
        let invalid = NativeSnapshotError::InvalidNativeLayout;
        if !matches!(side, 1 | 2) || !self.allows_campaign_fleet_edit(side) {
            return Err(invalid("native formation editing requires a player-owned campaign Level"));
        }
        let mut targets: HashMap<i64, Vec2> = HashMap::new();
        let mut offsets: HashMap<i64, Vec2> = HashMap::new();
        let mut changed_squads = 0;
        for entry in settings {
            let formation = entry.formation.unwrap_or_else(NativeSavedSquadFormation::authored);
            if !formation.is_valid() || !entry.placements_valid() || entry.side != side {
                return Err(invalid("invalid native formation settings or player ownership"));
            }
            // Original authored squad geometry can also be translated
            // without choosing an automatic slot rearrangement.
            let authored = formation.kind == NativeSquadFormationKind::Authored;
            let manually_placed = entry.placements.as_deref().unwrap_or(&[]);
            if authored && formation.translation == [0, 0] && manually_placed.is_empty() {
                continue;
            }
            let squad = self.source_squads.iter().find(|squad|
                squad.saved_squad_id == Some(entry.saved_squad_id) &&
                squad.side == side && squad.player_permissions.user_controlled &&
                !squad.hivemind.as_ref()
                    .is_some_and(|source| source.hivemind_controlled)
            ).ok_or(invalid("formation requires one authorized saved player squad"))?;
            if squad.members.is_empty() || (squad.members.len() == 1 &&
                formation.translation == [0, 0] && manually_placed.is_empty()) {
                continue;
            }
            let mut originals = Vec::with_capacity(squad.members.len());
            for &id in &squad.members {
                let position = self.entities.iter().find_map(|entity| match entity {
                    NativeAuthoredEntity::Ship(ship) if ship.entity_id == id =>
                        Some(ship.motion.position),
                    _ => None,
                }).ok_or(invalid("formation source squad contains a missing physical ship"))?;
                if !self.source_formation.contains_key(&id) {
                    return Err(invalid("formation source ship has no original offset"));
                }
                originals.push(position);
            }
            // For authored geometry, preserve the exact original formation
            // offsets rather than reconstructing them from ship centers.
            let slots = if authored {
                originals.clone()
            } else {
                source_formation_slots(formation, squad.members.len())?
            };
            let translation = Vec2::new(
                f32::from(formation.translation[0]), f32::from(formation.translation[1]),
            );
            // Current FleetShip IDs are the only durable position keys.
            // Missing/dead/reserved/transferred identities stay in the
            // career ledger without moving an unrelated physical ship.
            let overrides = manually_placed.iter().map(|placement|
                (placement.fleet_ship_id, Vec2::new(
                    f32::from(placement.offset[0]),
                    f32::from(placement.offset[1]),
                ))).collect::<HashMap<_, _>>();
            let divisor = squad.members.len() as f32;
            let original_center = originals.iter().copied()
                .fold(Vec2::ZERO, |sum, item| sum + item) * divisor.recip();
            let slot_center = slots.iter().copied()
                .fold(Vec2::ZERO, |sum, item| sum + item) * divisor.recip();
            for ((&id, original), slot) in squad.members.iter()
                .zip(originals.into_iter()).zip(slots)
            {
                let fleet_id = self.entities.iter().find_map(|entity| match entity {
                    NativeAuthoredEntity::Ship(ship) if ship.entity_id == id => ship.fleet_ship_id,
                    _ => None,
                });
                let custom = fleet_id.and_then(|key| overrides.get(&key)).copied();
                // Manual placement is an additive nudge from the original
                // authored/preset slot. Zero never teleports a ship to the
                // squad center. Unedited ships retain their exact layout.
                let offset = slot - slot_center + custom.unwrap_or(Vec2::ZERO);
                let destination = original_center + offset + translation;
                // An individual saved placement must not disturb the
                // remaining source-authored ships. Skip untouched owners
                // instead of revalidating/copying their entire families.
                if authored && custom.is_none() &&
                    formation.translation == [0, 0] {
                    continue;
                }
                let delta = destination - original;
                let family = self.movable_authored_family(side, id)
                    .ok_or(invalid("formation cannot move an active or ambiguous Carrier family"))?;
                for family_id in family {
                    let current = self.entities.iter().find_map(|entity| match entity {
                        NativeAuthoredEntity::Ship(ship) if ship.entity_id == family_id =>
                            Some(ship.motion.position),
                        _ => None,
                    }).ok_or(invalid("formation Carrier family has no physical ship"))?;
                    if targets.insert(family_id, current + delta).is_some() {
                        return Err(invalid("formation moves a physical ship through two owners"));
                    }
                }
                if !authored || custom.is_some() { offsets.insert(id, offset); }
            }
            changed_squads += 1;
        }
        if targets.is_empty() { return Ok(0); }
        // All owner changes are checked before any source scene mutation.
        // Compare final AABBs, not just squad centers: mixed original hulls
        // have different collider dimensions and may not fit the Unity grid.
        for entity in &self.entities {
            let NativeAuthoredEntity::Ship(ship) = entity else { continue };
            let Some(&destination) = targets.get(&ship.entity_id) else { continue };
            let half_width = ship.game_movement.half_width.max(1.0);
            let half_height = ship.game_movement.half_height.max(1.0);
            if !self.campaign_position_allowed(
                ship.motion.position, destination, half_width.max(half_height),
            ) {
                return Err(invalid("formation places a fleet hull outside the authored Level"));
            }
            if self.entities.iter().any(|other| match other {
                NativeAuthoredEntity::Ship(peer) if peer.entity_id != ship.entity_id => {
                    let peer_pos = targets.get(&peer.entity_id)
                        .copied().unwrap_or(peer.motion.position);
                    (destination.x - peer_pos.x).abs() <
                        half_width + peer.game_movement.half_width.max(1.0) &&
                    (destination.y - peer_pos.y).abs() <
                        half_height + peer.game_movement.half_height.max(1.0)
                }
                _ => false,
            }) {
                return Err(invalid("formation overlaps another physical fleet hull"));
            }
        }
        for entity in &mut self.entities {
            let NativeAuthoredEntity::Ship(ship) = entity else { continue };
            let Some(&target) = targets.get(&ship.entity_id) else { continue };
            ship.motion.position = target;
            if let Some(geometry) = &mut ship.target_geometry {
                geometry.bounds_latched = false;
            }
            if let Some(activity) = &mut ship.combat_activity {
                activity.elapsed_seconds = None;
            }
        }
        for (id, offset) in offsets {
            self.source_formation.get_mut(&id)
                .expect("validated physical fleet formation offset").offset = offset;
        }
        Ok(changed_squads)
    }
}
