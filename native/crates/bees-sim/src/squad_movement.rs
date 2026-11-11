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
use crate::combat_world::{
    CombatWorld, CombatWorldError, GamePointOrder, GamePointOrderResult,
};
use crate::geometry::Vec2;
use crate::navigation_runtime::NavigationRuntime;

pub const FORMATION_COMPRESSION_STEPS: u32 = 20;

/// Unity Squad.GetFormationAdjustment, used by
/// SetOffscreenStartingPosition and SetStartingPosition when physically
/// placing a saved squad. It deliberately differs from Squad.MoveInternal,
/// which uses the unadjusted Ship.OffsetFromCenter for movement orders.
/// Ship types are the frozen ConfigData.ShipTypes IDs in ship_assets.rs.
pub fn authored_placement_offset(ship_type: u8, member_count: usize, offset: Vec2) -> Vec2 {
    if member_count <= 1 { return Vec2::ZERO; }
    let (x_scale, y_scale) = match ship_type {
        17 => (2.75, 2.0),          // Queen
        3 => (1.2, 1.2),           // Bumblebee
        0 | 10 | 11 | 4 => (1.4, 1.0), // Barge, FireBarge, Flagship, CarpenterBee
        2 | 20 => (2.5, 1.0),     // Beehive, WarpGate
        _ => (1.0, 1.0),
    };
    Vec2::new(offset.x * x_scale, offset.y * y_scale)
}

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
    // One-ship training arenas do not need a duplicate-ID allocation.
    // For larger squads keep O(n) membership validation, but read mobile
    // members from the original slice rather than copying them into a
    // second temporary Vec on every move.
    let mut unique_ids = (members.len() > 1)
        .then(|| HashSet::with_capacity(members.len()));
    let mut mobile_count = 0usize;
    let mut largest_clearance = 0;
    for &member in members {
        if member.ship_id <= 0 ||
            !member.offset_from_center.is_finite() ||
            member.clearance < 0 {
            return Err(SquadFormationError::InvalidRequest);
        }
        if unique_ids.as_mut().is_some_and(|ids| !ids.insert(member.ship_id)) {
            return Err(SquadFormationError::DuplicateShip);
        }
        if member.mobile {
            mobile_count += 1;
            largest_clearance = largest_clearance.max(member.clearance);
        }
    }
    let mut center = options.force_bounds(requested_destination);
    let mut compression = 1.0;
    if options.has_obstacles && mobile_count != 0 {
        if let Some(first) = formation_compression(
            members, center, options, navigation,
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
                members, center, options, navigation,
            ) else { return Ok(None) };
            compression = retry;
        }
    }

    let mut orders = Vec::with_capacity(mobile_count);
    for member in members.iter().filter(|member| member.mobile) {
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
        for member in members.iter().filter(|member| member.mobile) {
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

#[derive(Debug)]
pub enum SquadOrderError {
    Formation(SquadFormationError),
    Ship(CombatWorldError),
    MissingShip,
    MembershipMismatch,
}

impl From<SquadFormationError> for SquadOrderError {
    fn from(error: SquadFormationError) -> Self { Self::Formation(error) }
}

impl From<CombatWorldError> for SquadOrderError {
    fn from(error: CombatWorldError) -> Self { Self::Ship(error) }
}

#[derive(Clone, Debug, PartialEq)]
pub struct SquadFormationExecution {
    pub center: Vec2,
    pub compression: f32,
    pub results: Vec<(i64, GamePointOrderResult)>,
}

/// Run Unity Squad.Move via a single shared CombatWorld. Order planning
/// finishes before mutating the first ship, and all referenced ship
/// controllers are preflighted so a missing/dead/policy-owned member cannot
/// silently leave half the formation executing.
pub fn issue_squad_move(
    world: &mut CombatWorld,
    navigation: &NavigationRuntime,
    members: &[SquadFormationMember],
    destination: Vec2,
    options: SquadFormationOptions,
) -> Result<Option<SquadFormationExecution>, SquadOrderError> {
    execute_squad_move(world, navigation, members, destination, options, false)
}

/// The same formation planning and preflight as ordinary Move, but each ship
/// uses its shared tracked destination controller. This is called by commands
/// following moving squads, never by a separate AI navigation implementation.
pub fn issue_squad_tracked_move(
    world: &mut CombatWorld,
    navigation: &NavigationRuntime,
    members: &[SquadFormationMember],
    destination: Vec2,
    options: SquadFormationOptions,
) -> Result<Option<SquadFormationExecution>, SquadOrderError> {
    execute_squad_move(world, navigation, members, destination, options, true)
}

fn execute_squad_move(
    world: &mut CombatWorld,
    navigation: &NavigationRuntime,
    members: &[SquadFormationMember],
    destination: Vec2,
    options: SquadFormationOptions,
    tracked: bool,
) -> Result<Option<SquadFormationExecution>, SquadOrderError> {
    let Some(plan) = plan_squad_formation(
        members, destination, options, navigation,
    )? else { return Ok(None) };
    for &(ship_id, _) in &plan.orders {
        let game_ship = world.ship(ship_id)
            .ok_or(SquadOrderError::MissingShip)?;
        let physics_ship = world.ship_body(ship_id)
            .ok_or(SquadOrderError::MissingShip)?;
        if !physics_ship.motion.mobile || !physics_ship.active ||
            game_ship.vitals.is_dead() {
            return Err(SquadOrderError::MembershipMismatch);
        }
        world.validate_ship_game_order(ship_id)?;
    }

    let mut results = Vec::with_capacity(plan.orders.len());
    for &(ship_id, order) in &plan.orders {
        let outcome = if tracked {
            world.move_ship_to_tracked_point(ship_id, order)?
        } else {
            world.move_ship_to_point(ship_id, order)?
        };
        results.push((ship_id, outcome));
    }
    Ok(Some(SquadFormationExecution {
        center: plan.center, compression: plan.compression, results,
    }))
}

/// Unity Squad.SetSquadSpeed sets the current speed of every ship,
/// including stationary members, clamped individually to authored Speed.
/// Preflight the complete membership before applying any mutation.
pub fn set_squad_game_speed(
    world: &mut CombatWorld,
    members: &[SquadFormationMember],
    speed: f32,
) -> Result<(), SquadOrderError> {
    if !speed.is_finite() || speed < 0.0 {
        return Err(SquadOrderError::Formation(
            SquadFormationError::InvalidRequest,
        ));
    }
    let mut seen = HashSet::with_capacity(members.len());
    for member in members {
        if member.ship_id <= 0 || !seen.insert(member.ship_id) {
            return Err(SquadOrderError::Formation(
                SquadFormationError::InvalidRequest,
            ));
        }
        world.validate_ship_game_order(member.ship_id)?;
    }
    for member in members {
        world.set_ship_game_speed(member.ship_id, speed)?;
    }
    Ok(())
}

/// Unity Squad.StopMoving only stops members that are mobile. A Hold
/// command invokes this path, rather than issuing a zero-distance order
/// that could accidentally leave an A* job or pending waypoint active.
pub fn stop_squad_game_movement(
    world: &mut CombatWorld,
    members: &[SquadFormationMember],
) -> Result<Vec<i64>, SquadOrderError> {
    let mut seen = HashSet::new();
    let mut to_stop = Vec::new();
    for member in members {
        if member.ship_id <= 0 ||
            !seen.insert(member.ship_id) {
            return Err(SquadOrderError::Formation(
                SquadFormationError::InvalidRequest,
            ));
        }
        if !member.mobile { continue; }
        let body = world.ship_body(member.ship_id)
            .ok_or(SquadOrderError::MissingShip)?;
        if !body.motion.mobile {
            return Err(SquadOrderError::MembershipMismatch);
        }
        world.validate_ship_game_order(member.ship_id)?;
        to_stop.push(member.ship_id);
    }
    for &ship_id in &to_stop {
        world.stop_ship_game_movement(ship_id)?;
    }
    Ok(to_stop)
}
