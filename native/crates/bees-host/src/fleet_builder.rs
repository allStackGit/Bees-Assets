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
const FLEET_SIZES: [usize; 7] = [1, 2, 4, 8, 16, 32, 64];
pub(super) const GAME_ID: &str = "fleet-skirmish";

#[derive(Clone, Copy)]
enum Terrain { Open, Original, Cover, DenseCover }

#[derive(Clone, Copy)]
pub(super) struct FleetChoice {
    /// None preserves the source's two human-controlled factions.
    player_side: Option<i32>,
    fleet_sizes: [usize; 2],
    terrain: Terrain,
    ai_both: bool,
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
    Ok(choose_terrain()?.map(|terrain| FleetChoice {
        player_side: match mode { 0 => Some(1), 1 => Some(2), _ => None },
        fleet_sizes, terrain, ai_both: false,
    }))
}

/// The existing private host authenticates a separate remote commander
/// per side; no new network controller, simulation or permission model.
pub(super) fn choose_multiplayer() -> Result<Option<FleetChoice>, Box<dyn Error>> {
    let Some(fleet_sizes) = choose_sizes()? else { return Ok(None); };
    Ok(choose_terrain()?.map(|terrain| FleetChoice {
        player_side: None, fleet_sizes, terrain, ai_both: false,
    }))
}

/// The actual headless native runner measures steps/s and wall time with
/// exactly the same collision, command and ship code used in windowed play.
pub(super) fn choose_benchmark() -> Result<Option<FleetChoice>, Box<dyn Error>> {
    let Some(fleet_sizes) = choose_sizes()? else { return Ok(None); };
    Ok(choose_terrain()?.map(|terrain| FleetChoice {
        player_side: None, fleet_sizes, terrain, ai_both: true,
    }))
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
    if !sps.is_finite() || !wall.is_finite() || wall <= 0.0 ||
        !summary["failed_window"].is_null() {
        return Err(invalid("native benchmark has no complete valid measurement").into());
    }
    println!("{}", json!({
        "kind": "native_fleet_benchmark_complete",
        "world_steps": steps,
        "world_steps_per_execution_second": sps,
        "execution_wall_seconds": wall,
        "summary": file,
    }));
    let labels = [
        format!("Measured simulation: {sps:.1} steps/s"),
        format!("Completed: {steps} world steps in {wall:.2}s"),
        "Detailed timing and physics counters saved as rollout-summary.json".to_owned(),
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
    if selected.ai_both {
        scene.as_object_mut().ok_or_else(||
            invalid("fleet source scene must be a JSON object"))?
            .remove("local_player_side");
    } else {
        scene["local_player_side"] = json!(selected.player_side.unwrap_or(2));
    }
    scene["level_name"] = json!(format!(
        "Fleet Skirmish: {} Bees vs {} Humans",
        selected.fleet_sizes[0], selected.fleet_sizes[1],
    ));

    let mut cloned = NativeLevelLayout::default();
    let mut squad_ids = [vec![101_i64], vec![102_i64]];
    let offsets = [
        fleet_offsets(0, selected.fleet_sizes[0]),
        fleet_offsets(1, selected.fleet_sizes[1]),
    ];
    for side in 0..2_usize {
        for index in 1..selected.fleet_sizes[side] {
            // New identities occupy disjoint faction ranges well beyond all
            // source ship and obstacle IDs. Preserve them across different
            // opponent fleet sizes for deterministic multiplayer/replays.
            let side_base = (side as i64 + 1) * 100;
            let new_squad = 2_000 + side_base + index as i64;
            let new_ship = 10_000 + side_base + index as i64;
            cloned.clone_squads.push(NativeSquadClone {
                source_squad_item_id: 101 + side as i64,
                new_squad_item_id: new_squad,
                ship_ids: vec![new_ship],
                by: offsets[side][index],
            });
            squad_ids[side].push(new_squad);
        }
    }
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
    run["next_squad_item_id"] = json!(3_000);
    run["run_id"] = json!(format!(
        "native-fleet-{}v{}-{}",
        selected.fleet_sizes[0], selected.fleet_sizes[1],
        if selected.ai_both { "benchmark" }
        else { selected.player_side.map_or("local", |side|
            if side == 1 { "bees" } else { "humans" }) },
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
            let start = offsets[side][position];
            let mut orders = vec![json!({
                "kind": "move_to_point",
                "destination": [
                    (if side == 0 { -30.0 } else { 30.0 }) + start[0] * 0.25,
                    start[1],
                ],
            })];
            orders.extend(squad_ids[1 - side].iter().map(|&enemy| json!({
                "kind": "aggressive", "enemy_squad_item_id": enemy,
            })));
            queues.push(json!({
                "squad_item_id": owner, "repeat": true, "orders": orders,
            }));
        }
    }
    run["levels"][0]["scripted_queues"] = json!(queues);
    let scene_path = parent.join("replay-source-scene.json");
    let run_path = parent.join("replay-source-run.json");
    write_json_new(&scene_path, &scene)?;
    run["levels"][0]["scene"] = json!(scene_path.canonicalize()?);
    // Validate the exact RunConfig accepted by the ordinary gameplay
    // runner before publishing a discoverable replay-source-run file.
    let _: RunConfig = serde_json::from_value(run.clone())?;
    write_json_new(&run_path, &run)?;
    Ok(run_path)
}
