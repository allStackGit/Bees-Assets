//! Source Squad.RefreshCompositionCommandBans, recomputed after AddShip
//! and RemoveShip. This is a pure game decision, shared by graphical and
//! native headless Levels. It never invents weapon firepower from hulls.
//!
//! These six command bans form one composition-specific subset of the
//! squad's larger authored banned-strategy collection; do not overwrite
//! independent gameplay/campaign bans when using this snapshot.

use bees_hivemind::dispatch::{LocalCommandKind, SquadCommandComposition};
use bees_hivemind::protocol::COMMAND_STRATEGIES;
use bees_sim::combat_world::CombatWorld;

use crate::squads::NativeSquadEntry;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SquadCompositionError {
    EmptySquad,
    MissingOrDeadShip(i64),
    MissingShipFirepower(i64),
    InvalidShipFirepower(i64),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CompositionCommandBans {
    pub defenseless: bool,
    pub only_bombers: bool,
    pub only_barges: bool,
}

impl CompositionCommandBans {
    /// The six commands that source RefreshCompositionCommandBans
    /// adds/removes. Other independently banned strategies are unaffected.
    pub fn disallows(self, command: LocalCommandKind) -> bool {
        match command {
            LocalCommandKind::Aggressive => self.defenseless,
            LocalCommandKind::CircleSquad |
            LocalCommandKind::RightSwipe |
            LocalCommandKind::LeftSwipe |
            LocalCommandKind::InAndOut =>
                self.defenseless || self.only_bombers || self.only_barges,
            LocalCommandKind::Hold => self.defenseless || self.only_bombers,
            _ => false,
        }
    }
}

/// Read the actual authored Ship.Firepower (not an inference or collision
/// approximation) and live roster. Called only when squad composition
/// changes, not on every ship's fixed physics update.
pub fn composition_command_bans(
    world: &CombatWorld,
    squad: &NativeSquadEntry,
    mut authored_firepower: impl FnMut(i64) -> Option<f32>,
) -> Result<CompositionCommandBans, SquadCompositionError> {
    if squad.members.is_empty() {
        return Err(SquadCompositionError::EmptySquad);
    }
    let mut types = Vec::with_capacity(squad.members.len());
    let mut defenseless = true;
    for &ship_id in &squad.members {
        let ship = world.ship(ship_id)
            .filter(|ship| ship.side == squad.side &&
                !ship.vitals.is_dead())
            .ok_or(SquadCompositionError::MissingOrDeadShip(ship_id))?;
        if !world.ship_body(ship_id).is_some_and(|body| body.active) {
            return Err(SquadCompositionError::MissingOrDeadShip(ship_id));
        }
        let firepower = authored_firepower(ship_id)
            .ok_or(SquadCompositionError::MissingShipFirepower(ship_id))?;
        if !firepower.is_finite() || firepower < 0.0 {
            return Err(SquadCompositionError::InvalidShipFirepower(ship_id));
        }
        defenseless &= firepower == 0.0;
        types.push(i32::from(ship.ship_type));
    }
    let composition = SquadCommandComposition::from_unity_ship_types(types);
    Ok(CompositionCommandBans {
        defenseless,
        only_bombers: composition.only_bombers,
        only_barges: composition.only_barges,
    })
}

/// Build the deployed BeesServer `BannedStrats` names for one strategy
/// request. The Level keeps its other bans (campaign conditions, temporary
/// command failure, etc.). This function adds or removes nothing from those:
/// it only appends the six source composition restrictions.
///
/// Use COMMAND_STRATEGIES, not Rust enum names, so CircleSquad is sent as
/// "Circle", MoveToRandom as "Random", and other wire names stay compatible
/// with the deployed Node backend. No additional allocation is needed in a
/// physics tick; this is called only when issuing a strategy request.
pub fn banned_strategy_names(
    bans: CompositionCommandBans,
    existing: impl IntoIterator<Item = String>,
) -> Vec<String> {
    let mut result: Vec<String> = existing.into_iter().collect();
    for (index, &name) in COMMAND_STRATEGIES.iter().enumerate() {
        let kind = LocalCommandKind::from_server_strategy_id(
            (index + 1) as u8,
        ).expect("fixed server strategy table has 15 supported IDs");
        if bans.disallows(kind) &&
            !result.iter().any(|existing| existing == name) {
            result.push(name.to_owned());
        }
    }
    result
}
