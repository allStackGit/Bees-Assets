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

use crate::commands::{GameCommandError, NativeFriendlySquad};
use crate::squad_geometry::live_squad_bounds_from_catalog;
use crate::squads::NativeSquadEntry;

/// Construct the source-friendly entry for ClosestFriendly and Guard.
///
/// Call in actual Level.State.Squads order; this function does not sort
/// or select targets. Command kind and protected-Guard identity must come
/// from the Level's real command registry (which includes user orders),
/// not solely the HiveMind director. All sizes, locations and immutable
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
