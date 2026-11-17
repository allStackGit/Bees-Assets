//! Unity-free fleet skirmish builder for the native game library.
//!
//! This is game *authoring*, not a second simulation. We reuse the original
//! source-authored two-player hulls and NativeLevelLayout's validated clone
//! operation, then run the ordinary native Level and replay pipeline.

use std::error::Error;
use std::fs::OpenOptions;
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
const FLEET_SIZES: [usize; 5] = [1, 2, 4, 8, 12];
pub(super) const GAME_ID: &str = "fleet-skirmish";

#[derive(Clone, Copy)]
enum Terrain { Open, Original, Cover, DenseCover }

#[derive(Clone, Copy)]
pub(super) struct FleetChoice {
    /// None preserves the source's two human-controlled factions.
    player_side: Option<i32>,
    fleet_sizes: [usize; 2],
    terrain: Terrain,
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
        fleet_sizes, terrain,
    }))
}

/// The existing private host authenticates a separate remote commander
/// per side; no new network controller, simulation or permission model.
pub(super) fn choose_multiplayer() -> Result<Option<FleetChoice>, Box<dyn Error>> {
    let Some(fleet_sizes) = choose_sizes()? else { return Ok(None); };
    Ok(choose_terrain()?.map(|terrain| FleetChoice {
        player_side: None, fleet_sizes, terrain,
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
fn cover_box(id: i64, position: [f32; 2]) -> NativePlacedStaticBox {
    NativePlacedStaticBox {
        entity_id: id, prefab_name: "Box".to_owned(),
        position, rotation_degrees: 0.0, local_corners: None,
    }
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
        let controlled = selected.player_side.is_none_or(|player| player == side);
        squad["user_controlled"] = json!(controlled);
        squad["can_accept_input"] = json!(controlled);
    }
    scene["local_player_side"] = json!(selected.player_side.unwrap_or(2));
    scene["level_name"] = json!(format!(
        "Fleet Skirmish: {} Bees vs {} Humans",
        selected.fleet_sizes[0], selected.fleet_sizes[1],
    ));

    let mut cloned = NativeLevelLayout::default();
    let mut squad_ids = [vec![101_i64], vec![102_i64]];
    for side in 0..2_usize {
        for index in 1..selected.fleet_sizes[side] {
            // New identities occupy disjoint faction ranges well beyond all
            // source ship and obstacle IDs. Preserve them across different
            // opponent fleet sizes for deterministic multiplayer/replays.
            let side_base = (side as i64 + 1) * 100;
            let new_squad = 2_000 + side_base + index as i64;
            let new_ship = 10_000 + side_base + index as i64;
            let row = ((index + 1) / 2) as f32;
            let sign = if index % 2 == 1 { 1.0 } else { -1.0 };
            cloned.clone_squads.push(NativeSquadClone {
                source_squad_item_id: 101 + side as i64,
                new_squad_item_id: new_squad,
                ship_ids: vec![new_ship],
                by: [0.0, sign * row * 15.0],
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
        Terrain::Cover => &[[-24.0, 45.0], [24.0, -45.0]][..],
        Terrain::DenseCover => &[
            [-24.0, -42.0], [-24.0, 42.0],
            [24.0, -42.0], [24.0, 42.0],
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
        selected.player_side.map_or("local", |side|
            if side == 1 { "bees" } else { "humans" }),
    ));
    // AI squads use the ordinary source command director and target real
    // opposing SquadLifetimes, not an approximate trainer-only opponent.
    // Cycling through the entire opposite roster lets battle AI switch
    // targets as individual ships die; local-human squads own no script.
    let mut queues = Vec::new();
    for side in 0..2_usize {
        if selected.player_side.is_none_or(|player| player == side as i32 + 1) {
            continue;
        }
        for (&owner, position) in squad_ids[side].iter().zip(0..) {
            let mut orders = vec![json!({
                "kind": "move_to_point",
                "destination": [
                    if side == 0 { -10.0 } else { 10.0 },
                    if position == 0 { 0.0 } else {
                        let row = ((position + 1) / 2) as f32;
                        let sign = if position % 2 == 1 { 1.0 } else { -1.0 };
                        sign * row * 15.0
                    },
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
