//! Source-grounded squad formation destination selection.
//!
//! Port of Squad.Movement.MoveInternal and TryGetFormationCompression.
//! This module produces individual ship point orders; it never implements
//! alternate ship movement or pathfinding rules. CombatWorld still owns each
//! ship's live movement controller and A* request.
//!
//! `Level.ForceBounds` is represented by explicit world-coordinate bounds.
//! Pathfinder's clearance is expressed in Level-local coordinates and is
//! queried through the map's authored affine transform.

use std::collections::HashSet;

use crate::collision::Aabb;
use crate::combat_world::GamePointOrder;
use crate::geometry::Vec2;
use crate::navigation_runtime::NavigationRuntime;

pub const FORMATION_COMPRESSION_STEPS: u32 = 20;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SquadFormationMember {
    pub ship_id: i64,
    /// `Ship.OffsetFromCenter`, in shared physics/world coordinates.
    pub offset_from_center: Vec2,
    /// The authored `Ship.GetClearance()` result.
    pub clearance: i32,
    /// Whether `Ship.IsMobile` at order creation.
    pub mobile: bool,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SquadFormationOptions {
    pub map_bounds: Aabb,
    pub has_obstacles: bool,
    pub can_override_bounds: bool,
}

impl SquadFormationOptions {
    pub fn valid(self) -> bool {
        self.map_bounds.min.is_finite() &&
        self.map_bounds.max.is_finite() &&
        self.map_bounds.min.x <= self.map_bounds.max.x &&
        self.map_bounds.min.y <= self.map_bounds.max.y
    }

    pub fn force_bounds(self, destination: Vec2) -> Vec2 {
        if self.can_override_bounds { return destination; }
        Vec2::new(
            destination.x.clamp(self.map_bounds.min.x, self.map_bounds.max.x),
            destination.y.clamp(self.map_bounds.min.y, self.map_bounds.max.y),
        )
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SquadFormationError {
    InvalidRequest,
    DuplicateShip,
}

/// An all-or-nothing *plan*, not an all-or-nothing mutation of the world.
/// Callers must validate the live ship controllers before applying orders.
/// No destination is issued if Unity's formation compression and nearest
/// walkable-center fallback cannot find a legal formation.
#[derive(Clone, Debug, PartialEq)]
pub struct SquadFormationPlan {
    pub center: Vec2,
    pub compression: f32,
    pub orders: Vec<(i64, GamePointOrder)>,
}

pub fn plan_squad_formation(
    members: &[SquadFormationMember],
    requested_destination: Vec2,
    options: SquadFormationOptions,
    navigation: &NavigationRuntime,
) -> Result<Option<SquadFormationPlan>, SquadFormationError> {
    if !requested_destination.is_finite() || !options.valid() {
        return Err(SquadFormationError::InvalidRequest);
    }
    let mut unique_ids = HashSet::new();
    let mut mobile_members = Vec::with_capacity(members.len());
    let mut largest_clearance = 0;
    for &member in members {
        if member.ship_id <= 0 ||
            !member.offset_from_center.is_finite() ||
            member.clearance < 0 ||
            !unique_ids.insert(member.ship_id) {
            return Err(if unique_ids.contains(&member.ship_id) {
                SquadFormationError::DuplicateShip
            } else {
                SquadFormationError::InvalidRequest
            });
        }
        if member.mobile {
            mobile_members.push(member);
            largest_clearance = largest_clearance.max(member.clearance);
        }
    }
    let mut center = options.force_bounds(requested_destination);
    let mut compression = 1.0;
    if options.has_obstacles && !mobile_members.is_empty() {
        if let Some(first) = formation_compression(
            &mobile_members, center, options, navigation,
        ) {
            compression = first;
        } else {
            let local = navigation.map_frame().world_to_level(center);
            let Some(candidate) = navigation.clearance()
                .nearest_walkable_destination(local, largest_clearance) else {
                return Ok(None);
            };
            center = options.force_bounds(
                navigation.map_frame().level_to_world(candidate),
            );
            let Some(retry) = formation_compression(
                &mobile_members, center, options, navigation,
            ) else { return Ok(None) };
            compression = retry;
        }
    }

    let mut orders = Vec::with_capacity(mobile_members.len());
    for member in mobile_members {
        let destination = options.force_bounds(
            center + member.offset_from_center * compression,
        );
        orders.push((member.ship_id, GamePointOrder {
            destination,
            ship_clearance: member.clearance,
            map_bounds: options.map_bounds,
            has_obstacles: options.has_obstacles,
            can_override_bounds: options.can_override_bounds,
            newly_found_obstacle: false,
        }));
    }
    Ok(Some(SquadFormationPlan { center, compression, orders }))
}

fn formation_compression(
    members: &[SquadFormationMember],
    center: Vec2,
    options: SquadFormationOptions,
    navigation: &NavigationRuntime,
) -> Option<f32> {
    for step in 0..=FORMATION_COMPRESSION_STEPS {
        let compression = 1.0 - step as f32 / FORMATION_COMPRESSION_STEPS as f32;
        let mut all_valid = true;
        for member in members {
            let destination = options.force_bounds(
                center + member.offset_from_center * compression,
            );
            let local = navigation.map_frame().world_to_level(destination);
            if !navigation.clearance()
                .can_occupy_cell(local, member.clearance) {
                all_valid = false;
                break;
            }
        }
        if all_valid { return Some(compression); }
    }
    None
}
