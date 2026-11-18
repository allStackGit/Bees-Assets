//! Unity-free fleet skirmish builder for the native game library.
//!
//! This is game *authoring*, not a second simulation. We reuse the original
//! source-authored two-player hulls and NativeLevelLayout's validated clone
//! operation, then run the ordinary native Level and replay pipeline.

use std::error::Error;
use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};

use bees_host::presentation::{select_library, BattleMenuRow};
use bees_host::unity_scene_snapshot::{
    NativeLevelLayout, NativePlacedStaticBox, NativeSquadClone,
};
use bees_sim::ship_assets::ShipAssetCatalog;
use serde_json::{json, Value};

use super::RunConfig;

const SOURCE_SCENE: &str = include_str!("../../../examples/native-two-player.scene.json");
const SOURCE_RUN: &str = include_str!("../../../examples/native-two-player.run.json");
const SOURCE_ARCHETYPES: &str = include_str!("../../../examples/native-skirmish.scene.json");
const FLEET_SIZES: [usize; 8] = [1, 2, 4, 8, 16, 32, 64, 128];
pub(super) const GAME_ID: &str = "fleet-skirmish";

#[derive(Clone, Copy)]
enum Terrain { Open, Original, Cover, DenseCover }

#[derive(Clone, Copy)]
enum Objective { Elimination, Survival, Extraction }

/// Native Level waves are source-authored physical squads. This option
/// changes only when squads enter the same live combat/roster pipeline,
/// never their hulls, weapon behaviors, or trainer's simulation engine.
#[derive(Clone, Copy)]
enum ReinforcementPattern { AllAtStart, Bees, Humans, Both }

impl ReinforcementPattern {
    fn includes(self, side: usize) -> bool {
        match self {
            Self::AllAtStart => false,
            Self::Bees => side == 0,
            Self::Humans => side == 1,
            Self::Both => true,
        }
    }

    fn label(self) -> &'static str {
        match self {
            Self::AllAtStart => "no-waves",
            Self::Bees => "bee-waves",
            Self::Humans => "human-waves",
            Self::Both => "both-waves",
        }
    }
}

#[derive(Clone, Copy)]
enum FleetMix { Fighters, Balanced, SupportHeavy, Specialists }

#[derive(Clone, Copy)]
enum AiTactics { Aggressive, Circle, RightSwipe, LeftSwipe, InAndOut, Mixed }

impl AiTactics {
    fn source_order(self, squad_index: usize) -> &'static str {
        let tactic = match self {
            Self::Mixed => match squad_index % 5 {
                0 => Self::Aggressive,
                1 => Self::Circle,
                2 => Self::RightSwipe,
                3 => Self::LeftSwipe,
                _ => Self::InAndOut,
            },
            fixed => fixed,
        };
        match tactic {
            Self::Aggressive => "aggressive_nearest",
            Self::Circle => "circle_nearest",
            Self::RightSwipe => "right_swipe_nearest",
            Self::LeftSwipe => "left_swipe_nearest",
            Self::InAndOut => "in_and_out_nearest",
            Self::Mixed => unreachable!("mixed doctrine resolved above"),
        }
    }
}

/// A single hull cannot be deferred: both factions need a real starting
/// squad. Hide impossible choices rather than accepting a misleading menu
/// setting with zero waves.
fn choose_reinforcements(
    fleet_sizes: [usize; 2],
) -> Result<Option<ReinforcementPattern>, Box<dyn Error>> {
    let mut patterns = vec![
        (ReinforcementPattern::AllAtStart,
         "All squads deploy at the start", "  [Ordinary skirmish]"),
    ];
    if fleet_sizes[0] > 1 {
        patterns.push((ReinforcementPattern::Bees,
            "Bee reinforcement waves", "  [Additional Bees at 25s and 55s]"));
    }
    if fleet_sizes[1] > 1 {
        patterns.push((ReinforcementPattern::Humans,
            "Human reinforcement waves", "  [Additional Humans at 25s and 55s]"));
    }
    if fleet_sizes.iter().all(|&size| size > 1) {
        patterns.push((ReinforcementPattern::Both,
            "Both factions receive waves", "  [Each side at 25s and 55s]"));
    }
    if patterns.len() == 1 {
        return Ok(Some(ReinforcementPattern::AllAtStart));
    }
    let rows = patterns.iter().map(|(_, title, status)| BattleMenuRow {
        title, status, unlocked: true,
    }).collect::<Vec<_>>();
    let mut selected = 0;
    Ok(select_library(&rows, &mut selected, false)?
        .map(|index| patterns[index].0))
}

fn choose_ai_tactics() -> Result<Option<AiTactics>, Box<dyn Error>> {
    let options = [
        ("Aggressive pursuit", "  [Source Aggressive command]"),
        ("Circle the target", "  [Source CircleSquad command]"),
        ("Right-side swipe", "  [Source RightSwipe command]"),
        ("Left-side swipe", "  [Source LeftSwipe command]"),
        ("Strike and withdraw", "  [Source InAndOut command]"),
        ("Mixed tactics", "  [Different tactics per squad]"),
    ];
    let rows = options.iter().map(|(title, status)| BattleMenuRow {
        title, status, unlocked: true,
    }).collect::<Vec<_>>();
    let mut selected = 0;
    let Some(chosen) = select_library(&rows, &mut selected, false)? else {
        return Ok(None);
    };
    Ok(Some(match chosen {
        0 => AiTactics::Aggressive,
        1 => AiTactics::Circle,
        2 => AiTactics::RightSwipe,
        3 => AiTactics::LeftSwipe,
        4 => AiTactics::InAndOut,
        _ => AiTactics::Mixed,
    }))
}

impl FleetMix {
    fn support_count(self, total: usize) -> usize {
        (match self {
            Self::Fighters => 0,
            Self::Balanced => total.div_ceil(4).min(8),
            Self::SupportHeavy => (total / 2).min(12),
            Self::Specialists => if total >= 4 { total.div_ceil(4).min(8) } else { 0 },
        }).min(total.saturating_sub(1 + self.specialist_count(total)))
    }

    fn specialist_count(self, total: usize) -> usize {
        usize::from(matches!(self, Self::Specialists) && total >= 2)
    }
}

impl Objective {
    fn label(self) -> &'static str {
        match self {
            Self::Elimination => "Elimination",
            Self::Survival => "Defend Command Ship",
            Self::Extraction => "Escape to Extraction",
        }
    }
}

#[derive(Clone, Copy)]
pub(super) struct FleetChoice {
    /// None preserves the source's two human-controlled factions.
    player_side: Option<i32>,
    fleet_sizes: [usize; 2],
    mix: [FleetMix; 2],
    terrain: Terrain,
    ai_both: bool,
    ai_tactics: AiTactics,
    objective: Objective,
    reinforcements: ReinforcementPattern,
    /// Mission ownership is independent of whose input commands this host
    /// accepts. Both local/remote players retain full command permissions.
    objective_side: Option<i32>,
    benchmark_worlds: usize,
    benchmark_workers: usize,
}

fn invalid(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidInput, message)
}

pub(super) fn choose() -> Result<Option<FleetChoice>, Box<dyn Error>> {
    let choices = [
        ("Bees commander vs Human AI", "  [Player side 1]"),
        ("Humans commander vs Bee AI", "  [Player side 2]"),
        ("Two local commanders", "  [Both factions]"),
    ];
    let rows = choices.iter().map(|(title, status)| BattleMenuRow {
        title, status, unlocked: true,
    }).collect::<Vec<_>>();
    let mut selection = 0_usize;
    let Some(mode) = select_library(&rows, &mut selection, false)? else {
        return Ok(None);
    };
    let Some(fleet_sizes) = choose_sizes()? else { return Ok(None); };
    let Some(mix) = choose_mix(fleet_sizes)? else { return Ok(None); };
    let Some(terrain) = choose_terrain()? else { return Ok(None); };
    let ai_tactics = if mode == 2 { AiTactics::Aggressive } else {
        let Some(value) = choose_ai_tactics()? else { return Ok(None); };
        value
    };
    let Some(objective) = choose_objective()? else { return Ok(None); };
    let player_side = match mode { 0 => Some(1), 1 => Some(2), _ => None };
    let objective_side = match objective {
        Objective::Elimination => None,
        _ if player_side.is_some() => player_side,
        _ => {
            let Some(side) = choose_objective_side()? else { return Ok(None); };
            Some(side)
        },
    };
    let reinforcements = if matches!(objective, Objective::Survival) {
        // Preserve the original dedicated incoming-enemy wave objective.
        ReinforcementPattern::AllAtStart
    } else {
        let Some(pattern) = choose_reinforcements(fleet_sizes)? else {
            return Ok(None);
        };
        pattern
    };
    Ok(Some(FleetChoice {
        player_side, fleet_sizes, mix, terrain, ai_both: false, ai_tactics, objective,
        reinforcements, objective_side, benchmark_worlds: 1, benchmark_workers: 1,
    }))
}

/// The existing private host authenticates a separate remote commander
/// per side; no new network controller, simulation or permission model.
pub(super) fn choose_multiplayer() -> Result<Option<FleetChoice>, Box<dyn Error>> {
    let Some(fleet_sizes) = choose_sizes()? else { return Ok(None); };
    let Some(mix) = choose_mix(fleet_sizes)? else { return Ok(None); };
    let Some(terrain) = choose_terrain()? else { return Ok(None); };
    let Some(objective) = choose_objective()? else { return Ok(None); };
    let objective_side = match objective {
        Objective::Elimination => None,
        _ => {
            let Some(side) = choose_objective_side()? else { return Ok(None); };
            Some(side)
        },
    };
    let reinforcements = if matches!(objective, Objective::Survival) {
        ReinforcementPattern::AllAtStart
    } else {
        let Some(pattern) = choose_reinforcements(fleet_sizes)? else {
            return Ok(None);
        };
        pattern
    };
    Ok(Some(FleetChoice {
        player_side: None, fleet_sizes, mix, terrain, ai_both: false,
        ai_tactics: AiTactics::Aggressive,
        objective, reinforcements, objective_side,
        benchmark_worlds: 1, benchmark_workers: 1,
    }))
}

/// The actual headless native runner measures steps/s and wall time with
/// exactly the same collision, command and ship code used in windowed play.
pub(super) fn choose_benchmark() -> Result<Option<FleetChoice>, Box<dyn Error>> {
    let Some(fleet_sizes) = choose_sizes()? else { return Ok(None); };
    let Some(mix) = choose_mix(fleet_sizes)? else { return Ok(None); };
    let Some(terrain) = choose_terrain()? else { return Ok(None); };
    let Some(ai_tactics) = choose_ai_tactics()? else { return Ok(None); };
    // The same published real-wave scenario can be profiled across many
    // arenas, so dynamic admission costs are measurable rather than guessed.
    let Some(reinforcements) = choose_reinforcements(fleet_sizes)? else {
        return Ok(None);
    };
    let Some(worlds) = choose_resource("Concurrent physical arenas",
        &[1, 2, 4, 8, 16, 32])? else { return Ok(None); };
    let workers = [1, 2, 4, 8, 16, 32].into_iter()
        .filter(|&count| count <= worlds).collect::<Vec<_>>();
    let Some(threads) = choose_resource("CPU physics workers", &workers)?
        else { return Ok(None); };
    Ok(Some(FleetChoice {
        player_side: None, fleet_sizes, mix, terrain, ai_both: true,
        ai_tactics, objective: Objective::Elimination, objective_side: None,
        reinforcements, benchmark_worlds: worlds, benchmark_workers: threads,
    }))
}

fn choose_resource(label: &str, options: &[usize]) -> Result<Option<usize>, Box<dyn Error>> {
    let titles = options.iter().map(|count| format!("{count}  |  {label}"))
        .collect::<Vec<_>>();
    let rows = titles.iter().map(|title| BattleMenuRow {
        title, status: "  [Measured allocation]", unlocked: true,
    }).collect::<Vec<_>>();
    let mut selected = 0_usize;
    Ok(select_library(&rows, &mut selected, false)?.map(|index| options[index]))
}

fn choose_sizes() -> Result<Option<[usize; 2]>, Box<dyn Error>> {
    let mut fleet_sizes = [1_usize, 1_usize];
    for (index, title) in ["Bees fleet", "Humans fleet"].into_iter().enumerate() {
        let descriptions = FLEET_SIZES.iter().map(|&size|
            format!("{size} ships  |  {title}{}",
                if size > 64 { "  |  Fighters only" } else { "" })
        ).collect::<Vec<_>>();
        let rows = descriptions.iter().map(|description| BattleMenuRow {
            title: description, status: "  [Physical ships]", unlocked: true,
        }).collect::<Vec<_>>();
        let mut selected = 0_usize;
        let Some(choice) = select_library(&rows, &mut selected, false)? else {
            return Ok(None);
        };
        fleet_sizes[index] = FLEET_SIZES[choice];
    }
    Ok(Some(fleet_sizes))
}

fn choose_mix(fleet_sizes: [usize; 2])
    -> Result<Option<[FleetMix; 2]>, Box<dyn Error>>
{
    let mut choices = [FleetMix::Fighters; 2];
    for side in 0..2 {
        let total = fleet_sizes[side];
        // The dense 10x15 large-fleet layout is sourced from 6x6 Wasp
        // and Gunship sprites. Its 12-unit grid cannot fit the source
        // Beehive's 34-unit sprite or a 19-unit Fire Barge without
        // physical overlap. Keep that benchmark honest and deterministic.
        if total == 1 || total > 64 { continue; }
        let support_name = if side == 0 { "Beehives" } else { "Carriers" };
        let options = [
            (FleetMix::Fighters, format!("{total} fighters  |  No support")),
            (FleetMix::Balanced, format!(
                "{} fighters, {} {support_name}  |  Balanced",
                total - FleetMix::Balanced.support_count(total),
                FleetMix::Balanced.support_count(total),
            )),
            (FleetMix::SupportHeavy, format!(
                "{} fighters, {} {support_name}  |  Support-heavy",
                total - FleetMix::SupportHeavy.support_count(total),
                FleetMix::SupportHeavy.support_count(total),
            )),
            (FleetMix::Specialists, format!(
                "{} fighters, 1 {}, {} {support_name}  |  Specialist",
                total - FleetMix::Specialists.support_count(total) - 1,
                if side == 0 { "Scout" } else { "Fire Barge" },
                FleetMix::Specialists.support_count(total),
            )),
        ];
        let rows = options.iter().map(|(_, title)| BattleMenuRow {
            title, status: "  [Real source hulls]", unlocked: true,
        }).collect::<Vec<_>>();
        let mut selected = 0_usize;
        let Some(index) = select_library(&rows, &mut selected, false)?
            else { return Ok(None); };
        choices[side] = options[index].0;
    }
    Ok(Some(choices))
}

fn choose_objective() -> Result<Option<Objective>, Box<dyn Error>> {
    let choices = [
        ("Eliminate all opposing ships", "  [Standard battle]"),
        ("Defend your command ship for 90 seconds", "  [Incoming waves]"),
        ("Reach the enemy-side extraction zone", "  [Tactical escape]"),
    ];
    let rows = choices.iter().map(|(title, status)| BattleMenuRow {
        title, status, unlocked: true,
    }).collect::<Vec<_>>();
    let mut selected = 0_usize;
    Ok(select_library(&rows, &mut selected, false)?.map(|index| match index {
        1 => Objective::Survival,
        2 => Objective::Extraction,
        _ => Objective::Elimination,
    }))
}

fn choose_objective_side() -> Result<Option<i32>, Box<dyn Error>> {
    let choices = [
        BattleMenuRow {
            title: "Bees mission objective", status: "  [Faction 1]", unlocked: true,
        },
        BattleMenuRow {
            title: "Humans mission objective", status: "  [Faction 2]", unlocked: true,
        },
    ];
    let mut selected = 0;
    Ok(select_library(&choices, &mut selected, false)?
        .map(|index| index as i32 + 1))
}

fn choose_terrain() -> Result<Option<Terrain>, Box<dyn Error>> {
    let choices = [
        ("Open field", "  [No static obstacles]"),
        ("Classic duel", "  [Original minor cover]"),
        ("Flanking cover", "  [Two physical obstacles]"),
        ("Dense cover", "  [Four physical obstacles]"),
    ];
    let rows = choices.iter().map(|(title, status)| BattleMenuRow {
        title, status, unlocked: true,
    }).collect::<Vec<_>>();
    let mut choice = 0;
    Ok(select_library(&rows, &mut choice, false)?.map(|index|
        match index {
            0 => Terrain::Open,
            1 => Terrain::Original,
            2 => Terrain::Cover,
            _ => Terrain::DenseCover,
        }
    ))
}

/// Reuse the existing source collider prefab, not an approximate visual
/// barrier. Both live game and training share its authentic collision and
/// navigation behavior through NativeLevelLayout.
/// Keep 1..64 fleets on their original fixed near-first layout, so
/// pre-existing saved battles and performance measurements retain the same
/// physical spawn geometry. Fighter-only large fleets use a denser, disjoint
/// source-centered 10x15 grid. On the native 320x200 arena that occupies
/// x -147..-39 (Bees) and 39..147 (Humans), y -84..84, keeping
/// hulls away from borders and the central authored cover lane.
fn fleet_offsets(side: usize, count: usize) -> Vec<[f32; 2]> {
    let large = count > 64;
    let columns = if large {
        if side == 0 { -6_i32..=3 } else { -3_i32..=6 }
    } else if side == 0 { -4_i32..=3 } else { -3_i32..=4 };
    let rows = if large { -7_i32..=7 } else { -4_i32..=4 };
    let mut positions = rows.flat_map(|row|
        columns.clone().filter_map(move |column|
            (row != 0 || column != 0).then_some((column, row))
        )
    ).collect::<Vec<_>>();
    positions.sort_unstable_by_key(|&(column, row)|
        (column * column + row * row, row.abs(), column.abs(), row, column)
    );
    let spacing_y = if large { 12.0 } else { 18.0 };
    let mut offsets = vec![[0.0, 0.0]];
    offsets.extend(positions.into_iter().take(count.saturating_sub(1))
        .map(|(x, y)| [x as f32 * 12.0, y as f32 * spacing_y]));
    offsets
}

/// Fit real sprite extents, not just squad centers, in each faction's
/// physical half of the native arena. The source Wasp/Gunship remains at
/// its original center; larger support hulls occupy the backline first,
/// then the authentic specialist and fighter squads fill available cells.
/// Combat, collision, AI and inference still use the same source geometry.
/// This one-time authoring search does not run inside any simulation tick.
fn mixed_fleet_offsets(
    side: usize, total: usize, mix: FleetMix,
    catalog: &ShipAssetCatalog,
) -> Result<Vec<[f32; 2]>, Box<dyn Error>> {
    let support = mix.support_count(total);
    let specialist = mix.specialist_count(total);
    let fighters = total - support - specialist;
    let fighter_kind = if side == 0 { 21 } else { 13 };
    let support_kind = if side == 0 { 2 } else { 5 };
    let specialist_kind = if side == 0 { 18 } else { 10 };
    let fighter_size = catalog.sprite_size(fighter_kind)
        .ok_or_else(|| invalid("source fighter has no sprite extents"))?;
    let home = if side == 0 { -75.0 } else { 75.0 };
    let mut offsets = vec![[0.0, 0.0]; total];
    let mut occupied = vec![(home, 0.0, fighter_size.x * 0.5, fighter_size.y * 0.5)];
    let mut y_rows = (-22_i32..=22).collect::<Vec<_>>();
    y_rows.sort_unstable_by_key(|&row| (row.abs(), row));
    let outer_x = (-151_i32..=-35).step_by(4).collect::<Vec<_>>();
    let mut inner_x = outer_x.clone();
    inner_x.reverse();
    for index in (fighters + specialist..total)
        .chain(fighters..fighters + specialist)
        .chain(1..fighters)
    {
        let kind = if index >= fighters + specialist { support_kind }
            else if index >= fighters { specialist_kind }
            else { fighter_kind };
        let size = catalog.sprite_size(kind)
            .ok_or_else(|| invalid("source mixed fleet has no sprite extents"))?;
        let half_x = size.x * 0.5;
        let half_y = size.y * 0.5;
        let columns = if index >= fighters + specialist {
            &outer_x
        } else {
            &inner_x
        };
        let mut placed = false;
        for &column in columns {
            let x = if side == 0 { column as f32 } else { -(column as f32) };
            if x.abs() - half_x < 30.0 || x.abs() + half_x > 158.0 {
                continue;
            }
            for &row in &y_rows {
                let y = row as f32 * 4.0;
                if y.abs() + half_y > 95.0 ||
                    occupied.iter().any(|&(other_x, other_y, other_half_x, other_half_y)| {
                        (x - other_x).abs() < half_x + other_half_x + 2.0 &&
                        (y - other_y).abs() < half_y + other_half_y + 2.0
                    })
                {
                    continue;
                }
                offsets[index] = [x - home, y];
                occupied.push((x, y, half_x, half_y));
                placed = true;
                break;
            }
            if placed { break; }
        }
        if !placed {
            return Err(invalid(
                "mixed fleet cannot fit its authentic ship hulls inside the selected map"
            ).into());
        }
    }
    Ok(offsets)
}

fn cover_box(id: i64, position: [f32; 2]) -> NativePlacedStaticBox {
    NativePlacedStaticBox {
        entity_id: id, prefab_name: "Box".to_owned(),
        position, rotation_degrees: 0.0, local_corners: None,
    }
}

/// The unmodified native headless runner writes the authoritative timing
/// and physics counters. Show those exact measurements in the game menu,
/// while keeping the full JSON analysis artifact in the numbered save.
pub(super) fn show_benchmark_result(run_dir: &Path) -> Result<(), Box<dyn Error>> {
    let file = run_dir.join("rollout-summary.json");
    let summary: Value = serde_json::from_slice(&fs::read(&file)?)?;
    let metrics = &summary["throughput"];
    let sps = metrics["world_steps_per_execution_second"].as_f64()
        .ok_or_else(|| invalid("native benchmark omitted measured SPS"))?;
    let steps = metrics["successful_world_steps"].as_u64()
        .ok_or_else(|| invalid("native benchmark omitted executed steps"))?;
    let wall = metrics["execution_wall_seconds"].as_f64()
        .ok_or_else(|| invalid("native benchmark omitted execution time"))?;
    let workers = metrics["requested_physics_workers"].as_u64()
        .ok_or_else(|| invalid("native benchmark omitted CPU worker allocation"))?;
    let arenas = summary["native_layouts"].as_array()
        .ok_or_else(|| invalid("native benchmark omitted physical Level count"))?
        .len();
    if !sps.is_finite() || !wall.is_finite() || wall <= 0.0 ||
        !summary["failed_window"].is_null() {
        return Err(invalid("native benchmark has no complete valid measurement").into());
    }
    // The most recent simulator bug duplicated worlds before their AI
    // queues existed. Refuse to present an apparently high SPS number
    // for an arena with no intended combat orders.
    let command_plan = &summary["scripted_command_plan"];
    let worlds_with_orders = command_plan["worlds_with_orders"].as_u64()
        .ok_or_else(|| invalid("native benchmark omitted AI World coverage"))?;
    let queue_count = command_plan["configured_queues"].as_u64()
        .ok_or_else(|| invalid("native benchmark omitted AI squad commands"))?;
    let order_count = command_plan["configured_orders"].as_u64()
        .ok_or_else(|| invalid("native benchmark omitted authored command work"))?;
    let minimum_queues = command_plan["min_queues_per_world"].as_u64()
        .ok_or_else(|| invalid("native benchmark omitted per-arena AI coverage"))?;
    if worlds_with_orders != arenas as u64 || minimum_queues == 0 ||
        order_count < queue_count {
        return Err(invalid("native benchmark contains an arena without authentic AI commands").into());
    }
    let scripted = &summary["scripted_command_work_current_episodes"];
    let started_commands = scripted["commands_started"].as_u64().unwrap_or(0);
    let nearest_queries = scripted["nearest_enemy_resolutions"].as_u64().unwrap_or(0);
    let nearest_candidates = scripted["nearest_enemy_candidates_scanned"]
        .as_u64().unwrap_or(0);
    let admissions = &summary["reinforcement_arrivals"];
    let waves_arrived = admissions["waves"].as_u64().unwrap_or(0);
    let squads_arrived = admissions["squads"].as_u64().unwrap_or(0);
    let ships_arrived = admissions["ships"].as_u64().unwrap_or(0);
    // Separate configured tactical coverage from actually executed
    // source commands. The latter are episode-local counters, not an
    // invented measure of all lifetime combat maneuvers.
    let configured_tactics = command_plan["configured_tactical_maneuvers"]
        .as_u64().unwrap_or(0);
    let circles = scripted["circle_nearest_commands_started"].as_u64().unwrap_or(0);
    let swipes = scripted["swipe_nearest_commands_started"].as_u64().unwrap_or(0);
    let strike_retreats = scripted["in_and_out_nearest_commands_started"]
        .as_u64().unwrap_or(0);
    println!("{}", json!({
        "kind": "native_fleet_benchmark_complete",
        "world_steps": steps,
        "world_steps_per_execution_second": sps,
        "execution_wall_seconds": wall,
        "arenas": arenas,
        "cpu_physics_workers": workers,
        "scripted_ai_queues": queue_count,
        "scripted_ai_orders": order_count,
        "configured_tactical_maneuver_orders": configured_tactics,
        "scripted_worlds": worlds_with_orders,
        "current_episodes_circle_commands_started": circles,
        "current_episodes_swipe_commands_started": swipes,
        "current_episodes_in_and_out_commands_started": strike_retreats,
        "scripted_commands_started_current_episodes": started_commands,
        "nearest_enemy_queries_current_episodes": nearest_queries,
        "nearest_enemy_candidates_current_episodes": nearest_candidates,
        "reinforcement_waves_arrived": waves_arrived,
        "reinforcement_squads_arrived": squads_arrived,
        "reinforcement_ships_arrived": ships_arrived,
        "summary": file,
    }));
    let labels = [
        format!("Measured simulation: {sps:.1} world steps/s"),
        format!("Arenas: {arenas}  |  CPU physics workers: {workers}"),
        format!("AI combat: {queue_count} squads / {order_count} source orders in {worlds_with_orders} arenas"),
        format!("Current episodes: {started_commands} real scripted commands activated"),
        format!("Tactics: {configured_tactics} prepared / {circles} Circle / {swipes} Swipe / {strike_retreats} In-Out started"),
        format!("Current AI targeting: {nearest_queries} retarget checks / {nearest_candidates} live candidates"),
        format!("Real reinforcements: {waves_arrived} waves / {squads_arrived} squads / {ships_arrived} ships deployed"),
        format!("Completed: {steps} world steps in {wall:.2}s"),
        "Full timing, physics and scripted-command measurements: rollout-summary.json".to_owned(),
    ];
    let rows = labels.iter().map(|title| BattleMenuRow {
        title, status: "  [Native benchmark]", unlocked: true,
    }).collect::<Vec<_>>();
    let mut selected = 0_usize;
    let _ = select_library(&rows, &mut selected, false)?;
    Ok(())
}

fn write_json_new(path: &Path, value: &Value) -> Result<(), Box<dyn Error>> {
    let mut output = OpenOptions::new().write(true).create_new(true).open(path)?;
    serde_json::to_writer_pretty(&mut output, value)?;
    output.write_all(b"\n")?;
    output.sync_all()?;
    Ok(())
}

/// Freeze a complete custom battle beside its eventual input trace. Replays
/// load these exact authored scene/config bytes, not today's library template.
pub(super) fn archive(
    parent: &Path, selected: FleetChoice,
) -> Result<PathBuf, Box<dyn Error>> {
    let mut scene: Value = serde_json::from_str(SOURCE_SCENE)?;
    let mut run: Value = serde_json::from_str(SOURCE_RUN)?;
    let squads = scene["squads"].as_array_mut()
        .ok_or_else(|| invalid("built-in fleet template has no squads"))?;
    if squads.len() != 2 || squads[0]["item_id"] != 101 ||
        squads[1]["item_id"] != 102 ||
        squads[0]["side"] != 1 || squads[1]["side"] != 2
    {
        return Err(invalid("built-in fleet template has incompatible source squads").into());
    }
    for squad in squads {
        let side = squad["side"].as_i64()
            .ok_or_else(|| invalid("fleet template squad is missing its faction"))?;
        let controlled = !selected.ai_both &&
            selected.player_side.is_none_or(|player| i64::from(player) == side);
        squad["user_controlled"] = json!(controlled);
        squad["can_accept_input"] = json!(controlled);
    }
    // Source Ship.HasUser and Squad.IsUserControlled are separate
    // permissions. Change both when authoring an AI side, rather than
    // leaving the AI's physical hull masquerading as a player ship.
    for ship in scene["ships"].as_array_mut().ok_or_else(||
        invalid("fleet template has no authentic physical ships"))?
    {
        let side = ship["side"].as_i64().ok_or_else(||
            invalid("fleet template ship is missing its source faction"))?;
        let controlled = !selected.ai_both &&
            selected.player_side.is_none_or(|player| i64::from(player) == side);
        ship["user_controlled"] = json!(controlled);
    }
    if selected.ai_both {
        scene.as_object_mut().ok_or_else(||
            invalid("fleet source scene must be a JSON object"))?
            .remove("local_player_side");
    } else {
        scene["local_player_side"] = json!(selected.player_side.unwrap_or(2));
    }
    scene["level_name"] = json!(format!(
        "Fleet Skirmish: {} Bees vs {} Humans ({})",
        selected.fleet_sizes[0], selected.fleet_sizes[1],
        selected.objective.label(),
    ));

    let mut cloned = NativeLevelLayout::default();
    let mut squad_ids = [vec![101_i64], vec![102_i64]];
    let mut support_squads = Vec::new();
    let mut scout_squads = Vec::new();
    let catalog = ShipAssetCatalog::embedded()
        .map_err(|_| invalid("native source ship collider catalogue is incomplete"))?;
    let offsets = [
        if matches!(selected.mix[0], FleetMix::Fighters) {
            fleet_offsets(0, selected.fleet_sizes[0])
        } else {
            mixed_fleet_offsets(0, selected.fleet_sizes[0], selected.mix[0], &catalog)?
        },
        if matches!(selected.mix[1], FleetMix::Fighters) {
            fleet_offsets(1, selected.fleet_sizes[1])
        } else {
            mixed_fleet_offsets(1, selected.fleet_sizes[1], selected.mix[1], &catalog)?
        },
    ];
    let support_count = [
        selected.mix[0].support_count(selected.fleet_sizes[0]),
        selected.mix[1].support_count(selected.fleet_sizes[1]),
    ];
    let specialist_count = [
        selected.mix[0].specialist_count(selected.fleet_sizes[0]),
        selected.mix[1].specialist_count(selected.fleet_sizes[1]),
    ];
    let fighter_count = [
        selected.fleet_sizes[0] - support_count[0] - specialist_count[0],
        selected.fleet_sizes[1] - support_count[1] - specialist_count[1],
    ];
    // Embed the actual side-specific source Beehive and Carrier
    // archetypes ONLY when requested. Their healing/support capabilities,
    // fleet metadata, visuals and colliders come from a known native game
    // scene, never from an approximate handcrafted stats table.
    let source_archetypes: Value = serde_json::from_str(SOURCE_ARCHETYPES)?;
    let available = source_archetypes["ships"].as_array().ok_or_else(||
        invalid("native skirmish has no source ship archetypes"))?;
    for side in 0..2_usize {
        if support_count[side] == 0 { continue; }
        let side_id = side as i64 + 1;
        let archetype = if side == 0 { 2 } else { 5 };
        let template = available.iter().find(|ship|
            ship["side"].as_i64() == Some(side_id) &&
            ship["ship_type"].as_u64() == Some(archetype)
        ).ok_or_else(|| invalid("native mixed fleet is missing its source support archetype"))?;
        let ship_id = 13 + side as i64;
        let squad_id = 103 + side as i64;
        let controlled = !selected.ai_both &&
            selected.player_side.is_none_or(|player| i64::from(player) == side_id);
        let offset = offsets[side][fighter_count[side] + specialist_count[side]];
        let mut ship = template.clone();
        ship["entity_id"] = json!(ship_id);
        ship["position"] = json!([
            (if side == 0 { -75.0 } else { 75.0 }) + offset[0], offset[1],
        ]);
        ship["user_controlled"] = json!(controlled);
        ship["fleet_ship_id"] = json!(ship_id);
        scene["ships"].as_array_mut().ok_or_else(||
            invalid("native mixed fleet scene lost its physical roster"))?
            .push(ship);
        scene["squads"].as_array_mut().ok_or_else(||
            invalid("native mixed fleet scene lost its real squad roster"))?
            .push(json!({
                "item_id": squad_id, "side": side_id,
                "members": [ship_id],
                "shooting_strategy_id": 1,
                "user_controlled": controlled,
                "can_accept_input": controlled,
                "command_bans": [],
            }));
    }
    // Source special abilities are genuine ships, not cloned fighter stats.
    // Only the first specialist of each side is authored; its physical
    // beacon/charge definitions come from the same game scenario as combat.
    for side in 0..2_usize {
        if specialist_count[side] == 0 { continue; }
        let side_id = side as i64 + 1;
        let specialist_type = if side == 0 { 18 } else { 10 };
        let template = available.iter().find(|ship|
            ship["side"].as_i64() == Some(side_id) &&
            ship["ship_type"].as_u64() == Some(specialist_type)
        ).ok_or_else(|| invalid("native specialist hull has no authentic source archetype"))?;
        let ship_id = 15 + side as i64;
        let squad_id = 105 + side as i64;
        let offset = offsets[side][fighter_count[side]];
        let controlled = !selected.ai_both &&
            selected.player_side.is_none_or(|player| i64::from(player) == side_id);
        let mut ship = template.clone();
        ship["entity_id"] = json!(ship_id);
        ship["fleet_ship_id"] = json!(ship_id);
        ship["position"] = json!([
            (if side == 0 { -75.0 } else { 75.0 }) + offset[0], offset[1],
        ]);
        ship["user_controlled"] = json!(controlled);
        scene["ships"].as_array_mut().ok_or_else(||
            invalid("native specialist fleet lost its physical scene ships"))?
            .push(ship);
        scene["squads"].as_array_mut().ok_or_else(||
            invalid("native specialist fleet lost its physical source squads"))?
            .push(json!({
                "item_id": squad_id, "side": side_id, "members": [ship_id],
                "shooting_strategy_id": 1,
                "user_controlled": controlled, "can_accept_input": controlled,
                "command_bans": [],
            }));
        if side == 0 { scout_squads.push(squad_id); }
    }
    for side in 0..2_usize {
        for index in 1..selected.fleet_sizes[side] {
            let specialist = specialist_count[side] != 0 &&
                index == fighter_count[side];
            let supporting = index >= fighter_count[side] + specialist_count[side];
            let support_origin = fighter_count[side] + specialist_count[side];
            if specialist {
                squad_ids[side].push(105 + side as i64);
                continue;
            }
            let source_squad = if supporting {
                103 + side as i64
            } else { 101 + side as i64 };
            if supporting && index == support_origin {
                // The first support ship is already a real source hull,
                // not a duplicated or simulated clone.
                squad_ids[side].push(source_squad);
                support_squads.push(source_squad);
                continue;
            }
            // Independent source-compatible entity/Stage identities per
            // side, stable when the opposite fleet's composition changes.
            let side_base = (side as i64 + 1) * 100;
            let new_squad = 2_000 + side_base + index as i64;
            let new_ship = 10_000 + side_base + index as i64;
            let offset = offsets[side][index];
            let base = if supporting { offsets[side][support_origin] }
                else { [0.0, 0.0] };
            cloned.clone_squads.push(NativeSquadClone {
                source_squad_item_id: source_squad,
                new_squad_item_id: new_squad,
                ship_ids: vec![new_ship],
                by: [offset[0] - base[0], offset[1] - base[1]],
                // An intentionally built fighter/support fleet has real
                // independent combat value even though the cloned squads
                // are not persistent SavedSquads in a player's campaign.
                // Legacy ad-hoc scene clones still default to false.
                count_as_fleet_value: true,
            });
            squad_ids[side].push(new_squad);
            if supporting { support_squads.push(new_squad); }
        }
    }
    let level_name = scene["level_name"].as_str()
        .unwrap_or("Fleet Skirmish").to_owned();
    scene["level_name"] = json!(format!(
        "{level_name}  |  Support: {} Beehives, {} Carriers  |  Specialists: {} Scouts, {} Fire Barges",
        support_count[0], support_count[1], specialist_count[0], specialist_count[1],
    ));
    if !matches!(selected.terrain, Terrain::Original) {
        // The two-player template's id=30 tiny box is a real authored
        // static collider. Remove it when choosing a different map.
        cloned.remove_static_ids.push(30);
    }
    let cover = match selected.terrain {
        Terrain::Open | Terrain::Original => &[][..],
        // Keep all cover in the map's central no-spawn lane. Native
        // 64- and 128-ship fleets occupy x<=-39 and x>=39; prefab
        // half-width is 25 and never overlaps a fresh ship by design.
        Terrain::Cover => &[[0.0, 52.0], [0.0, -52.0]][..],
        Terrain::DenseCover => &[
            [0.0, -75.0], [0.0, -20.0],
            [0.0, 35.0], [0.0, 90.0],
        ][..],
    };
    cloned.add_static_boxes.extend(cover.iter().enumerate().map(|(index, &position)|
        cover_box(20_001 + index as i64, position)
    ));
    run["levels"][0]["native_layout"] = serde_json::to_value(&cloned)?;
    let mut deferred_squads = Vec::new();
    if matches!(selected.objective, Objective::Survival) {
        let defender = selected.objective_side.ok_or_else(||
            invalid("survival requires a real defending faction"))?;
        let defenders_first_ship = if defender == 1 { 11 } else { 12 };
        run["levels"][0]["survival"] = json!({
            "defending_side": defender,
            "protected_ship_id": defenders_first_ship,
            "seconds": 90.0,
        });
    } else if matches!(selected.objective, Objective::Extraction) {
        let player = selected.objective_side.ok_or_else(||
            invalid("extraction requires a real extracting faction"))?;
        run["levels"][0]["extraction"] = json!({
            "player_side": player,
            "center": [if player == 1 { 125.0 } else { -125.0 }, 0.0],
            "half_extents": [6.0, 6.0],
            "activation_radius": 10.0,
            "layer": 19,
        });
    }

    // Every wave is an ordinary source-identified physical squad, not a
    // fabricated wave spawner. Preserve the survival game's original enemy
    // waves; other game modes can defer either or both fleets as selected.
    // Keep the first squad per faction alive at tick zero. Physics reserves
    // every later hull, so no early elimination silently discards a wave.
    let defer_side = |side: usize| {
        if matches!(selected.objective, Objective::Survival) {
            selected.objective_side != Some(side as i32 + 1)
        } else {
            selected.reinforcements.includes(side)
        }
    };
    let mut wave_times_and_squads = Vec::new();
    for side in 0..2_usize {
        if !defer_side(side) { continue; }
        let late = &squad_ids[side][1..];
        for (index, &squad) in late.iter().enumerate() {
            let seconds = if index < late.len().div_ceil(2) { 25_u32 } else { 55 };
            wave_times_and_squads.push((seconds, side, squad));
            deferred_squads.push(squad);
        }
    }
    if !wave_times_and_squads.is_empty() {
        // The native wave constructor requires nondecreasing source times.
        // Stable faction/squad tie ordering also gives every replay and
        // training arena the same physical deployment sequence.
        wave_times_and_squads.sort_unstable_by_key(|&(seconds, side, squad)|
            (seconds, side, squad));
        run["levels"][0]["reinforcements"] = json!(
            wave_times_and_squads.iter().map(|&(seconds, _, squad)| json!({
                "after_seconds": seconds as f32,
                "squad_item_ids": [squad],
            })).collect::<Vec<_>>()
        );
    }
    run["next_squad_item_id"] = json!(3_000);
    run["run_id"] = json!(format!(
        "native-fleet-{}v{}-support-{}v{}-specialist-{}v{}-{}-{}-owner{}",
        selected.fleet_sizes[0], selected.fleet_sizes[1],
        support_count[0], support_count[1],
        specialist_count[0], specialist_count[1],
        if selected.ai_both { "benchmark" }
        else { selected.player_side.map_or("local", |side|
            if side == 1 { "bees" } else { "humans" }) },
        match selected.objective {
            Objective::Elimination => "elimination",
            Objective::Survival => "survival",
            Objective::Extraction => "extraction",
        },
        selected.objective_side.unwrap_or(0),
    ));
    // Different arrival patterns must have different immutable source IDs,
    // especially when saved battles later become separate PPO experiments.
    if !wave_times_and_squads.is_empty() &&
        !matches!(selected.objective, Objective::Survival)
    {
        let run_id = run["run_id"].as_str()
            .ok_or_else(|| invalid("native fleet run identity is missing"))?
            .to_owned();
        run["run_id"] = json!(format!(
            "{run_id}-{}", selected.reinforcements.label(),
        ));
    }
    if selected.ai_both {
        // A repeatable multi-CPU stress case: the runner does no GUI or
        // Python inference here, and writes its normal sampled CPU/SPS
        // measurements in run/rollout-summary.json.
        run["max_rounds"] = json!(6_000);
        // Repeat quick 1v1 eliminations rather than publishing a noisy
        // millisecond-long sample. Large fleet fights naturally consume
        // more of the shared fixed-round budget per episode.
        run["episodes_per_world"] = json!(12);
        run["rounds_per_window"] = json!(128);
        run["timing_sample_period"] = json!(32);
        run["status_every_rounds"] = json!(2_000);
        run["physics_workers"] = json!(selected.benchmark_workers);
    }
    // AI squads use the ordinary source command director and target real
    // opposing SquadLifetimes, not an approximate trainer-only opponent.
    // Cycling through the entire opposite roster lets battle AI switch
    // targets as individual ships die; local-human squads own no script.
    let mut queues = Vec::new();
    for side in 0..2_usize {
        if !selected.ai_both &&
            selected.player_side.is_none_or(|player| player == side as i32 + 1)
        {
            continue;
        }
        for (position, &owner) in squad_ids[side].iter().enumerate() {
            let supporting = support_squads.contains(&owner);
            let scout = scout_squads.contains(&owner);
            let start = offsets[side][position];
            // A source-authored Aggressive lifetime still owns all
            // physical movement, weapon targeting and enemy deaths.
            // Do not store O(allies * enemies) scripted target lists
            // for a large battle: after one target dies the nearest live
            // opponent is chosen deterministically from the actual roster.
            // Passive support has no guns; scouts retain their movement
            // orders rather than inventing combat weapons.
            let orders = if supporting {
                vec![json!({"kind": "hold"})]
            } else if scout {
                vec![json!({
                    "kind": "move_to_point",
                    "destination": [
                        (if side == 0 { -30.0 } else { 30.0 }) + start[0] * 0.25,
                        start[1],
                    ],
                })]
            } else {
                vec![json!({"kind": selected.ai_tactics.source_order(position)})]
            };
            let mut queue = json!({
                "squad_item_id": owner, "repeat": !supporting,
                "orders": orders,
            });
            if deferred_squads.contains(&owner) {
                queue["activation"] = json!({"kind": "on_deployment"});
            }
            queues.push(queue);
        }
    }
    run["levels"][0]["scripted_queues"] = json!(queues);
    if selected.ai_both {
        // Every independent World must have the same fully authored AI
        // commands, physical hulls and objectives. Fan out only AFTER
        // queues are installed; otherwise all but World 1 become idle
        // and an apparent SPS improvement measures no meaningful combat.
        // The native runtime remaps per-World Stage squad lifetimes.
        let original = run["levels"][0].clone();
        run["levels"] = Value::Array((0..selected.benchmark_worlds)
            .map(|index| {
                let mut level = original.clone();
                level["world_id"] = json!(index as u64 + 1);
                level["level_id"] = json!(index as i64 + 1);
                level["seed"] = json!(347_u64 + (index as u64) * 1_009);
                level
            }).collect());
    }
    let scene_path = parent.join("replay-source-scene.json");
    let run_path = parent.join("replay-source-run.json");
    write_json_new(&scene_path, &scene)?;
    // Saved battles, recorded replays, and trainable source archives own
    // their scene in this same directory. Resolve it relative to the run,
    // not the developer's absolute Windows/Linux save path, so moving or
    // copying a complete battle archive preserves its physical identity.
    for level in run["levels"].as_array_mut().ok_or_else(||
        invalid("native fleet build has no physical Levels"))?
    {
        level["scene"] = json!("replay-source-scene.json");
    }
    // Validate the exact RunConfig accepted by the ordinary gameplay
    // runner before publishing a discoverable replay-source-run file.
    let _: RunConfig = serde_json::from_value(run.clone())?;
    write_json_new(&run_path, &run)?;
    Ok(run_path)
}
