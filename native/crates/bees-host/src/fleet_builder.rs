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
use serde_json::{json, Value};

use super::RunConfig;

const SOURCE_SCENE: &str = include_str!("../../../examples/native-two-player.scene.json");
const SOURCE_RUN: &str = include_str!("../../../examples/native-two-player.run.json");
const SOURCE_ARCHETYPES: &str = include_str!("../../../examples/native-skirmish.scene.json");
const FLEET_SIZES: [usize; 7] = [1, 2, 4, 8, 16, 32, 64];
pub(super) const GAME_ID: &str = "fleet-skirmish";

#[derive(Clone, Copy)]
enum Terrain { Open, Original, Cover, DenseCover }

#[derive(Clone, Copy)]
enum Objective { Elimination, Survival, Extraction }

#[derive(Clone, Copy)]
enum FleetMix { Fighters, Balanced, SupportHeavy }

impl FleetMix {
    fn support_count(self, total: usize) -> usize {
        (match self {
            Self::Fighters => 0,
            Self::Balanced => total.div_ceil(4),
            Self::SupportHeavy => total / 2,
        }).min(total.saturating_sub(1))
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
    objective: Objective,
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
    let objective = if mode == 2 {
        Objective::Elimination
    } else {
        let Some(chosen) = choose_objective()? else { return Ok(None); };
        chosen
    };
    Ok(Some(FleetChoice {
        player_side: match mode { 0 => Some(1), 1 => Some(2), _ => None },
        fleet_sizes, mix, terrain, ai_both: false, objective,
        benchmark_worlds: 1, benchmark_workers: 1,
    }))
}

/// The existing private host authenticates a separate remote commander
/// per side; no new network controller, simulation or permission model.
pub(super) fn choose_multiplayer() -> Result<Option<FleetChoice>, Box<dyn Error>> {
    let Some(fleet_sizes) = choose_sizes()? else { return Ok(None); };
    let Some(mix) = choose_mix(fleet_sizes)? else { return Ok(None); };
    Ok(choose_terrain()?.map(|terrain| FleetChoice {
        player_side: None, fleet_sizes, mix, terrain, ai_both: false,
        objective: Objective::Elimination,
        benchmark_worlds: 1, benchmark_workers: 1,
    }))
}

/// The actual headless native runner measures steps/s and wall time with
/// exactly the same collision, command and ship code used in windowed play.
pub(super) fn choose_benchmark() -> Result<Option<FleetChoice>, Box<dyn Error>> {
    let Some(fleet_sizes) = choose_sizes()? else { return Ok(None); };
    let Some(mix) = choose_mix(fleet_sizes)? else { return Ok(None); };
    let Some(terrain) = choose_terrain()? else { return Ok(None); };
    let Some(worlds) = choose_resource("Concurrent physical arenas",
        &[1, 2, 4, 8, 16, 32])? else { return Ok(None); };
    let workers = [1, 2, 4, 8, 16, 32].into_iter()
        .filter(|&count| count <= worlds).collect::<Vec<_>>();
    let Some(threads) = choose_resource("CPU physics workers", &workers)?
        else { return Ok(None); };
    Ok(Some(FleetChoice {
        player_side: None, fleet_sizes, mix, terrain, ai_both: true,
        objective: Objective::Elimination,
        benchmark_worlds: worlds, benchmark_workers: threads,
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
            format!("{size} ships  |  {title}")
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
        if total == 1 { continue; }
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
/// Keep the original authored hull at (0,0), then admit newly cloned
/// ships in deterministic near-first grid order. Eight columns and nine
/// rows fit up to 64 independent physical squads per faction without
/// crossing map center or the authored +/-100 vertical map bounds.
fn fleet_offsets(side: usize, count: usize) -> Vec<[f32; 2]> {
    let columns = if side == 0 { -4_i32..=3 } else { -3_i32..=4 };
    let mut positions = (-4_i32..=4).flat_map(|row|
        columns.clone().filter_map(move |column|
            (row != 0 || column != 0).then_some((column, row))
        )
    ).collect::<Vec<_>>();
    positions.sort_unstable_by_key(|&(column, row)|
        (column * column + row * row, row.abs(), column.abs(), row, column)
    );
    let mut offsets = vec![[0.0, 0.0]];
    offsets.extend(positions.into_iter().take(count.saturating_sub(1))
        .map(|(x, y)| [x as f32 * 12.0, y as f32 * 18.0]));
    offsets
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
    println!("{}", json!({
        "kind": "native_fleet_benchmark_complete",
        "world_steps": steps,
        "world_steps_per_execution_second": sps,
        "execution_wall_seconds": wall,
        "arenas": arenas,
        "cpu_physics_workers": workers,
        "summary": file,
    }));
    let labels = [
        format!("Measured simulation: {sps:.1} world steps/s"),
        format!("Arenas: {arenas}  |  CPU physics workers: {workers}"),
        format!("Completed: {steps} world steps in {wall:.2}s"),
        "Full timing and physics statistics: rollout-summary.json".to_owned(),
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
    let offsets = [
        fleet_offsets(0, selected.fleet_sizes[0]),
        fleet_offsets(1, selected.fleet_sizes[1]),
    ];
    let support_count = [
        selected.mix[0].support_count(selected.fleet_sizes[0]),
        selected.mix[1].support_count(selected.fleet_sizes[1]),
    ];
    let fighter_count = [
        selected.fleet_sizes[0] - support_count[0],
        selected.fleet_sizes[1] - support_count[1],
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
        let offset = offsets[side][fighter_count[side]];
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
    for side in 0..2_usize {
        for index in 1..selected.fleet_sizes[side] {
            let supporting = index >= fighter_count[side];
            let support_origin = fighter_count[side];
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
        "{level_name}  |  Support: {} Beehives, {} Carriers",
        support_count[0], support_count[1],
    ));
    if !matches!(selected.terrain, Terrain::Original) {
        // The two-player template's id=30 tiny box is a real authored
        // static collider. Remove it when choosing a different map.
        cloned.remove_static_ids.push(30);
    }
    let cover = match selected.terrain {
        Terrain::Open | Terrain::Original => &[][..],
        // Keep all cover in the map's central no-spawn lane. Native
        // 64-ship fleets occupy x<=-39 and x>=39 respectively; prefab
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
        let defender = selected.player_side.ok_or_else(||
            invalid("survival requires one real locally controlled faction"))?;
        let enemy_index = if defender == 1 { 1 } else { 0 };
        let defenders_first_ship = if defender == 1 { 11 } else { 12 };
        run["levels"][0]["survival"] = json!({
            "defending_side": defender,
            "protected_ship_id": defenders_first_ship,
            "seconds": 90.0,
        });
        // Keep one authentic enemy squad active at the start and deploy
        // only later clones. The combat engine reserves each future hull
        // by its real faction; the common terminal will not declare the
        // enemy defeated before its authored waves can arrive.
        let late = &squad_ids[enemy_index][1..];
        let mut waves = Vec::with_capacity(late.len());
        for (index, &squad) in late.iter().enumerate() {
            let at = if index < (late.len() + 1) / 2 { 25.0 } else { 55.0 };
            waves.push(json!({
                "after_seconds": at,
                "squad_item_ids": [squad],
            }));
            deferred_squads.push(squad);
        }
        run["levels"][0]["reinforcements"] = json!(waves);
    } else if matches!(selected.objective, Objective::Extraction) {
        let player = selected.player_side.ok_or_else(||
            invalid("extraction requires one real locally controlled faction"))?;
        run["levels"][0]["extraction"] = json!({
            "player_side": player,
            "center": [if player == 1 { 125.0 } else { -125.0 }, 0.0],
            "half_extents": [6.0, 6.0],
            "activation_radius": 10.0,
            "layer": 19,
        });
    }
    run["next_squad_item_id"] = json!(3_000);
    run["run_id"] = json!(format!(
        "native-fleet-{}v{}-support-{}v{}-{}-{}",
        selected.fleet_sizes[0], selected.fleet_sizes[1],
        support_count[0], support_count[1],
        if selected.ai_both { "benchmark" }
        else { selected.player_side.map_or("local", |side|
            if side == 1 { "bees" } else { "humans" }) },
        match selected.objective {
            Objective::Elimination => "elimination",
            Objective::Survival => "survival",
            Objective::Extraction => "extraction",
        },
    ));
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
        // The source physical scene and scripted actions are identical
        // in every independent arena; only world identity and RNG seed
        // differ. Native Simulation already owns persistent work-stealing
        // CPU workers and correct Stage squad-ID remapping.
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
            let start = offsets[side][position];
            // Passive Beehives and Carriers have no source gun mounts.
            // Never issue them Aggressive: that command requires real
            // armed ships. Fighters still use the normal native target
            // and command lifetime engine.
            let mut orders = if supporting {
                vec![json!({"kind": "hold"})]
            } else {
                vec![json!({
                    "kind": "move_to_point",
                    "destination": [
                        (if side == 0 { -30.0 } else { 30.0 }) + start[0] * 0.25,
                        start[1],
                    ],
                })]
            };
            if !supporting {
                orders.extend(squad_ids[1 - side].iter().filter(|&&enemy|
                    !deferred_squads.contains(&enemy)
                ).map(|&enemy| json!({
                    "kind": "aggressive", "enemy_squad_item_id": enemy,
                })));
            }
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
    let scene_path = parent.join("replay-source-scene.json");
    let run_path = parent.join("replay-source-run.json");
    write_json_new(&scene_path, &scene)?;
    let frozen_scene = scene_path.canonicalize()?;
    for level in run["levels"].as_array_mut().ok_or_else(||
        invalid("native fleet build has no physical Levels"))?
    {
        level["scene"] = json!(frozen_scene);
    }
    // Validate the exact RunConfig accepted by the ordinary gameplay
    // runner before publishing a discoverable replay-source-run file.
    let _: RunConfig = serde_json::from_value(run.clone())?;
    write_json_new(&run_path, &run)?;
    Ok(run_path)
}
