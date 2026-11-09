//! Source Squad.MakeMatchupAndGetCommand over the one live CombatWorld.
//!
//! Called at a HiveMind request boundary, not on the fixed-step hot path.
//! Deliberately keep the strategic and request-local shooting keys distinct:
//! nearby allied ships affect the former, never the latter. All weapon-range
//! eligibility is supplied by the same Level/weapon owner, not by a
//! surrogate distance threshold or an independently cached target list.

use bees_hivemind::session::SquadLifetime;
use bees_sim::combat_world::CombatWorld;
use bees_sim::ship_assets::ShipAssetCatalog;
use crate::commands::GameCommandError;
use crate::squad_geometry::live_squad_bounds_from_catalog;
use crate::squads::NativeSquadRoster;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct NativeSourceStrategyMatchup {
    pub matchup: String,
    pub shooting_matchup: String,
}

#[derive(Clone, Copy)]
struct CombatMatchupCandidate {
    ship_id: i64,
    ship_type: u8,
    distance: f32,
}

fn source_letters(
    world: &CombatWorld, ships: &[i64],
) -> Result<String, GameCommandError> {
    let mut letters = Vec::with_capacity(ships.len());
    for &id in ships {
        let stats = world.weapon_target_rank_stats(id)?;
        if stats.ship_type_letter >= 24 {
            return Err(GameCommandError::MissingCombatSnapshot);
        }
        letters.push(b'A' + stats.ship_type_letter);
    }
    letters.sort_unstable();
    Ok(String::from_utf8(letters).expect("validated source ship letters"))
}

fn average_health(
    world: &CombatWorld, ships: &[i64],
) -> Result<f64, GameCommandError> {
    if ships.is_empty() { return Ok(0.0); }
    let mut total = 0.0_f64;
    for &id in ships {
        let ship = world.ship(id).ok_or(GameCommandError::MissingCombatSnapshot)?;
        if ship.vitals.is_dead() {
            return Err(GameCommandError::MissingCombatSnapshot);
        }
        let original = ship.vitals.max_health();
        if original > 0 {
            total += f64::from(ship.vitals.health()) / f64::from(original) * 100.0;
        }
    }
    Ok(total / ships.len() as f64)
}

fn candidate(
    world: &CombatWorld, ship_id: i64, origin: bees_sim::geometry::Vec2,
) -> Result<CombatMatchupCandidate, GameCommandError> {
    let ship = world.ship(ship_id).ok_or(GameCommandError::MissingCombatSnapshot)?;
    let body = world.ship_body(ship_id).ok_or(GameCommandError::MissingCombatSnapshot)?;
    if ship.vitals.is_dead() || !body.active || !body.motion.position.is_finite() {
        return Err(GameCommandError::MissingCombatSnapshot);
    }
    let delta = body.motion.position - origin;
    Ok(CombatMatchupCandidate {
        ship_id, ship_type: ship.ship_type, distance: delta.length_squared().sqrt(),
    })
}

fn sort_candidates(candidates: &mut [CombatMatchupCandidate]) {
    // Unity Squad.CompareCombatDistance breaks equal distances by source
    // ShipTypes, then runtime Id; sort before truncating to 64.
    candidates.sort_unstable_by(|a, b|
        a.distance.total_cmp(&b.distance)
            .then_with(|| a.ship_type.cmp(&b.ship_type))
            .then_with(|| a.ship_id.cmp(&b.ship_id))
    );
}

/// Prepare the actual source strategic and shooting identities. Missing
/// authored weapon/ship metadata is fatal, not a silent omission.
///
/// `can_target` must implement Ship.IsShipWithinRange for two live ships
/// using authored Weapon.IsShipValidTarget. It may mutate lazy source
/// bounds latches on CombatWorld. Unsupported weapon subclasses MUST return
/// an error rather than approximate range using hull centers.
///
/// `None` means the source had a selected enemy squad but no visible
/// potential enemy ships; Unity requeues that squad without sending
/// GetStrategy. A missing selected squad builds the ordinary no-target key.
pub fn build_source_strategy_matchup(
    world: &mut CombatWorld,
    roster: &NativeSquadRoster,
    catalog: &ShipAssetCatalog,
    owner: SquadLifetime,
    selected_enemy: Option<SquadLifetime>,
    mut can_target: impl FnMut(&mut CombatWorld, i64, i64) -> Result<bool, GameCommandError>,
) -> Result<Option<NativeSourceStrategyMatchup>, GameCommandError> {
    let own = roster.get(owner).ok_or(GameCommandError::InvalidSquad)?;
    if own.members.is_empty() || !roster.covers_live_roster(world) {
        return Err(GameCommandError::InvalidSquad);
    }
    let own_ids: Vec<i64> = own.members.iter().copied().take(64).collect();
    let own_letters = source_letters(world, &own_ids)?;
    let Some(enemy_owner) = selected_enemy else {
        return Ok(Some(NativeSourceStrategyMatchup {
            shooting_matchup: format!("{own_letters}||"),
            matchup: format!("{own_letters}||0|0"),
        }));
    };
    let enemy = roster.get(enemy_owner).ok_or(GameCommandError::InvalidEnemy)?;
    if enemy_owner.level_id != owner.level_id || enemy.side == own.side ||
        enemy.members.is_empty() {
        return Err(GameCommandError::InvalidEnemy);
    }

    let own_center = live_squad_bounds_from_catalog(world, own, catalog)
        .map_err(GameCommandError::SquadGeometry)?.center;
    let enemy_center = live_squad_bounds_from_catalog(world, enemy, catalog)
        .map_err(GameCommandError::SquadGeometry)?.center;
    let mut visible = Vec::new();
    // Unity GameState.VisionCache retains discoveries until the target
    // dies; no transient line-of-sight rescan or inferred player sight.
    for &id in world.discovery().ship_ids_in_sighting_order(own.side) {
        let Some(ship) = world.ship(id) else { continue; };
        if ship.side == own.side || ship.vitals.is_dead() { continue; }
        visible.push(candidate(world, id, own_center)?);
    }
    sort_candidates(&mut visible);
    let mut enemies = Vec::with_capacity(64);
    // Target-squad members get first priority even when farther away;
    // only remaining slots are filled by other visible enemies in range.
    for candidate in &visible {
        if roster.owning_squad(candidate.ship_id) == Some(enemy_owner) {
            enemies.push(candidate.ship_id);
            if enemies.len() == 64 { break; }
        }
    }
    if enemies.len() < 64 {
        for candidate in &visible {
            if roster.owning_squad(candidate.ship_id) == Some(enemy_owner) {
                continue;
            }
            if own.members.iter().any(|&id| {
                // Evaluated below without swallowing source errors.
                id == candidate.ship_id
            }) { continue; }
            let mut eligible = false;
            for &id in &own.members {
                if can_target(world, candidate.ship_id, id)? {
                    eligible = true;
                    break;
                }
            }
            if eligible {
                enemies.push(candidate.ship_id);
                if enemies.len() == 64 { break; }
            }
        }
    }
    if enemies.is_empty() { return Ok(None); }

    let ally_limit = 64 - own_ids.len();
    let mut allies = Vec::new();
    if ally_limit > 0 {
        for &other_owner in roster.source_order() {
            let Some(other) = roster.get(other_owner) else {
                return Err(GameCommandError::InvalidSquad);
            };
            if other.side != own.side || other_owner == owner { continue; }
            for &id in &other.members {
                let mut eligible = false;
                for &target_id in &enemy.members {
                    if can_target(world, id, target_id)? {
                        eligible = true;
                        break;
                    }
                }
                if eligible { allies.push(candidate(world, id, enemy_center)?); }
            }
        }
    }
    sort_candidates(&mut allies);
    let allies: Vec<i64> = allies.into_iter().take(ally_limit).map(|item| item.ship_id).collect();

    let mut enemy_in_range = false;
    for &enemy_id in &enemy.members {
        for &own_id in &own.members {
            if can_target(world, enemy_id, own_id)? {
                enemy_in_range = true;
                break;
            }
        }
        if enemy_in_range { break; }
    }

    let mut friendly_health = own_ids.clone();
    friendly_health.extend_from_slice(&allies);
    let own_percent = average_health(world, &friendly_health)?;
    let enemy_percent = average_health(world, &enemies)?;
    // C# Math.Round(double) defaults to ties-to-even. 165 forces the
    // upper bucket when enemy percentage is zero.
    let ratio = if enemy_percent <= 0.0 { 165.0 } else {
        (own_percent / enemy_percent * 100.0).round_ties_even()
    };
    let bucket = if ratio < 50.0 { 0 } else if ratio < 85.0 { 1 }
        else if ratio < 115.0 { 2 } else if ratio < 165.0 { 3 } else { 4 };

    let enemy_letters = source_letters(world, &enemies)?;
    let ally_letters = source_letters(world, &allies)?;
    Ok(Some(NativeSourceStrategyMatchup {
        shooting_matchup: format!("{own_letters}|{enemy_letters}|"),
        matchup: format!("{own_letters}{ally_letters}|{enemy_letters}|{}|{bucket}",
            u8::from(enemy_in_range)),
    }))
}
