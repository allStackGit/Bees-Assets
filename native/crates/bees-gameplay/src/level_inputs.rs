//! Authoritative Level.State.Squads input projection for commands.
//!
//! The source Level owns its squad iteration order and live command/guard
//! state. This helper fills one entry at a time from the *existing* native
//! roster, physical world and immutable Unity-derived ship asset catalog;
//! no second command or ship registry and no per-frame copy are introduced.

use bees_hivemind::dispatch::LocalCommandKind;
use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::CombatWorld;
use bees_sim::ship_assets::ShipAssetCatalog;
use bees_sim::squad_movement::{SquadFormationMember, SquadFormationOptions};

use crate::commands::{
    GameCommandError, NativeEnemySquad, NativeFriendlySquad, NativeScoutingContext,
    NativeSquadOrder,
};
use crate::squad_geometry::live_squad_bounds_from_catalog;
use crate::squads::{NativeSquadEntry, NativeSquadRoster};

/// Construct the source-friendly entry for ClosestFriendly and Guard.
///
/// Call in actual Level.State.Squads order; this function does not sort
/// or select targets. Command kind and protected-Guard identity must come
/// from the canonical director (including local and HiveMind origins).
/// All sizes, locations and immutable
/// ship speeds come from the authoritative native game state.
pub fn live_friendly_squad_from_catalog(
    world: &CombatWorld,
    squad: &NativeSquadEntry,
    catalog: &ShipAssetCatalog,
    active_command: Option<LocalCommandKind>,
    guarded_squad: Option<SquadLifetime>,
) -> Result<NativeFriendlySquad, GameCommandError> {
    let geometry = live_squad_bounds_from_catalog(world, squad, catalog)
        .map_err(GameCommandError::SquadGeometry)?;
    let mut slowest_speed = f32::INFINITY;
    for &id in &squad.members {
        let body = world.ship_body(id)
            .filter(|body| body.active)
            .ok_or(GameCommandError::InvalidFriendlies)?;
        let speed = body.motion.base_speed;
        if !speed.is_finite() || speed < 0.0 {
            return Err(GameCommandError::InvalidFriendlies);
        }
        slowest_speed = slowest_speed.min(speed);
    }
    let friendly = NativeFriendlySquad {
        owner: squad.owner,
        side: squad.side,
        position: geometry.center,
        is_alive: true,
        active_command,
        guarded_squad,
        width: geometry.width,
        height: geometry.height,
        slowest_ship_speed: slowest_speed,
    };
    if !friendly.valid() {
        return Err(GameCommandError::InvalidFriendlies);
    }
    Ok(friendly)
}

/// Build the complete reusable friendly projection on demand for a due
/// ClosestFriendly/Guard timer. The *source Level* supplies its actual
/// Level.State.Squads order and command/guard state; dead or retired
/// lifetimes no longer appear in the native roster and are skipped.
///
/// Every shape and speed comes from canonical world state. This runs only
/// for due following-command polls, never on every fixed combat step.
pub fn collect_live_friendlies_from_catalog(
    world: &CombatWorld,
    roster: &NativeSquadRoster,
    catalog: &ShipAssetCatalog,
    source_squad_order: &[SquadLifetime],
    mut source_command: impl FnMut(SquadLifetime)
        -> Result<(Option<LocalCommandKind>, Option<SquadLifetime>), GameCommandError>,
    output: &mut Vec<NativeFriendlySquad>,
) -> Result<(), GameCommandError> {
    output.clear();
    for &owner in source_squad_order {
        let Some(squad) = roster.get(owner) else { continue };
        let (active_command, guarded_squad) = source_command(owner)?;
        output.push(live_friendly_squad_from_catalog(
            world, squad, catalog, active_command, guarded_squad,
        )?);
    }
    Ok(())
}

/// Build the genuine command-activation squad view from world geometry
/// and the owning Level's non-geometric source inputs.
///
/// An authenticated HiveMind strategy receipt supplies the actual enemy
/// and Scouting context. The Level supplies each ship's stored
/// Ship.OffsetFromCenter (retained between Squad.SetOffsets calls) and
/// current Pathfinder clearance. Reconstructing an offset from the ship's
/// *current* position at command issue changes Unity formation behavior
/// when a member has already moved away from its original formation.
/// Ship.GetClearance can also depend on the active stage; neither quantity
/// can be guessed from a collider or sprite dimension. Unity requests
/// clearance only for mobile ships on obstacle-bearing maps.
///
/// Reuses one caller-owned buffer across activations. Its borrowed members
/// remain valid until that buffer changes, matching one command-issue
/// boundary; the command director takes its own member snapshot if needed.
pub fn native_squad_order_from_catalog<'a>(
    world: &CombatWorld,
    squad: &NativeSquadEntry,
    catalog: &ShipAssetCatalog,
    formation: SquadFormationOptions,
    enemy: Option<NativeEnemySquad>,
    scouting: Option<NativeScoutingContext>,
    mut authored_offset: impl FnMut(i64) -> Option<bees_sim::geometry::Vec2>,
    mut authored_clearance: impl FnMut(i64) -> Option<i32>,
    members: &'a mut Vec<SquadFormationMember>,
) -> Result<NativeSquadOrder<'a>, GameCommandError> {
    members.clear();
    let geometry = live_squad_bounds_from_catalog(world, squad, catalog)
        .map_err(GameCommandError::SquadGeometry)?;
    members.reserve(squad.members.len());
    for &ship_id in &squad.members {
        let body = world.ship_body(ship_id)
            .filter(|body| body.active)
            .ok_or(GameCommandError::InvalidSquad)?;
        let offset = authored_offset(ship_id)
            .filter(|value| value.is_finite())
            .ok_or(GameCommandError::InvalidSquad)?;
        // Source Squad.MoveInternal calls Ship.GetClearance only for
        // mobile ships when Level.HasObstacles. An obstacle-free training
        // level must not need Pathfinder clearance metadata at all.
        let clearance = if formation.has_obstacles && body.motion.mobile {
            authored_clearance(ship_id)
                .filter(|&value| value >= 0)
                .ok_or(GameCommandError::InvalidSquad)?
        } else {
            0
        };
        members.push(SquadFormationMember {
            ship_id,
            offset_from_center: offset,
            clearance,
            mobile: body.motion.mobile,
        });
    }
    let order = NativeSquadOrder {
        targeting: squad.targeting,
        owner: squad.owner,
        members: members.as_slice(),
        squad_center: geometry.center,
        formation,
        enemy,
        scouting,
    };
    if !order.valid() {
        return Err(GameCommandError::InvalidSquad);
    }
    Ok(order)
}
