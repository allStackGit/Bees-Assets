//! Source MatchupStrategy.SortSquads over the canonical Level roster/world.
use bees_hivemind::{protocol::{ValidatedTargetingSelection, TARGETING_STRATEGIES}, session::SquadLifetime};
use bees_sim::{combat_world::CombatWorld, level_discovery::DiscoveryKind,
    ship_assets::ShipAssetCatalog, simulation::RandomStream};
use crate::{commands::NativeSquadCommandDirector, squads::NativeSquadRoster,
    squad_geometry::live_squad_bounds_from_catalog};

#[derive(Clone, Copy, Debug)]
pub struct NativeTargetingContext { pub has_player: bool, pub user_side: i32 }
#[derive(Clone, Copy, Debug)]
pub struct NativeInstalledTargeting {
    pub selection: ValidatedTargetingSelection,
    pub enemy: Option<SquadLifetime>,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeTargetingError { MissingSource, InvalidOwner, InvalidStrategy, MissingShip(i64) }
#[derive(Default)]
pub struct NativeSquadTargetingScratch { candidates: Vec<SquadLifetime> }

pub fn select_enemy_squad(
    world: &CombatWorld, roster: &NativeSquadRoster, commands: &NativeSquadCommandDirector,
    catalog: &ShipAssetCatalog, context: NativeTargetingContext, owner: SquadLifetime,
    selection: ValidatedTargetingSelection, random: &mut RandomStream,
    scratch: &mut NativeSquadTargetingScratch,
) -> Result<Option<SquadLifetime>, NativeTargetingError> {
    let id = selection.strategy.id;
    if id == 0 || TARGETING_STRATEGIES.get(usize::from(id)-1).copied() != Some(selection.strategy.name) {
        return Err(NativeTargetingError::InvalidStrategy);
    }
    let own = roster.get(owner).ok_or(NativeTargetingError::InvalidOwner)?;
    if context.has_player && context.user_side <= 0 { return Err(NativeTargetingError::MissingSource); }
    scratch.candidates.clear();
    for &candidate in roster.source_order() {
        let squad = roster.get(candidate).expect("canonical source order");
        if squad.side == own.side { continue; }
        let alive = |id| world.ship(id).is_some_and(|ship| !ship.vitals.is_dead()) &&
            world.ship_body(id).is_some_and(|body| body.active);
        if !squad.members.iter().copied().any(alive) { continue; }
        if !(context.has_player && own.side == context.user_side) && !squad.members.iter()
            .any(|&id| alive(id) && world.discovery().has(own.side, DiscoveryKind::Ship, id)) { continue; }
        scratch.candidates.push(candidate);
    }
    if scratch.candidates.is_empty() { return Ok(None); }
    if id == 1 { return Ok(Some(scratch.candidates[random.next_below(scratch.candidates.len() as u64) as usize])); }
    if id == 15 {
        // Source GangUp uses the FIRST commanded enemy, not a popularity count.
        for &friendly in roster.source_order() {
            if roster.get(friendly).is_some_and(|squad| squad.side == own.side) {
                if let Some(enemy) = commands.selected_enemy(friendly) {
                    if roster.get(enemy).is_some_and(|squad| squad.members.iter().any(|&id|
                        world.ship(id).is_some_and(|ship| !ship.vitals.is_dead()))) { return Ok(Some(enemy)); }
                }
            }
        }
    }
    let origin = if matches!(id, 8 | 9) {
        Some(live_squad_bounds_from_catalog(world, own, catalog)
            .map_err(|_| NativeTargetingError::MissingSource)?.center)
    } else { None };
    let metric = |candidate| -> Result<(bool, f64), NativeTargetingError> {
        let squad = roster.get(candidate).ok_or(NativeTargetingError::InvalidOwner)?;
        if let Some(origin) = origin {
            let position = live_squad_bounds_from_catalog(world, squad, catalog)
                .map_err(|_| NativeTargetingError::MissingSource)?.center;
            return Ok((false, f64::from((position-origin).length_squared().sqrt())));
        }
        let mut total = 0.0_f64;
        let mut combat = false;
        for &ship_id in &squad.members {
            let ship = world.ship(ship_id).ok_or(NativeTargetingError::MissingShip(ship_id))?;
            if ship.vitals.is_dead() { continue; }
            if matches!(id, 4 | 5 | 16 | 17) {
                total += f64::from(if matches!(id, 4 | 5) { ship.vitals.health() } else { ship.vitals.tsv() });
                continue;
            }
            let rank = world.weapon_target_rank_stats(ship_id).map_err(|_| NativeTargetingError::MissingSource)?;
            if !rank.valid() { return Err(NativeTargetingError::MissingSource); }
            match id {
                2 => total = total.max(f64::from(rank.last_killed)),
                3 => total += rank.fleet_damage_done as f64,
                6 | 7 => total += f64::from(rank.firepower),
                10 | 11 => total = total.max(f64::from(rank.max_range)),
                12 | 13 => total += f64::from(rank.speed),
                14 | 15 => {
                    combat |= world.ship_in_combat(ship_id).ok_or(NativeTargetingError::MissingSource)?;
                    total = total.max(f64::from(rank.max_range));
                }
                18..=40 => if rank.ship_type_letter == id - 18 { total += 1.0; },
                _ => return Err(NativeTargetingError::InvalidStrategy),
            }
        }
        Ok((combat, total))
    };
    let mut selected = scratch.candidates[0];
    let mut best = metric(selected)?;
    for &candidate in &scratch.candidates[1..] {
        let score = metric(candidate)?;
        let better = match id {
            14 | 15 => (score.0 && !best.0) || (score.0 == best.0 && score.1 < best.1),
            4 | 7 | 8 | 11 | 13 | 17 => score.1 < best.1,
            _ => score.1 > best.1,
        };
        if better { selected = candidate; best = score; }
    }
    Ok(Some(selected))
}
