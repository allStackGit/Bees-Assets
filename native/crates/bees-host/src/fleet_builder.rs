//! Unity-free fleet skirmish builder for the native game library.
//!
//! This is game *authoring*, not a second simulation. We reuse the original
//! source-authored two-player hulls and NativeLevelLayout's validated clone
//! operation, then run the ordinary native Level and replay pipeline.

use std::collections::HashMap;
use std::error::Error;
use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};

use bees_host::presentation::{select_library, BattleMenuRow};
use bees_host::unity_scene_snapshot::{
    NativeLevelLayout, NativePlacedStaticBox, NativeSquadClone, NativeSquadMerge,
    NativeSquadShortcut,
};
use bees_sim::ship_assets::ShipAssetCatalog;
use serde_json::{json, Value};

use super::{HiveMindConfig, RunConfig};

const SOURCE_SCENE: &str = include_str!("../../../examples/native-two-player.scene.json");
const SOURCE_RUN: &str = include_str!("../../../examples/native-two-player.run.json");
const SOURCE_ARCHETYPES: &str = include_str!("../../../examples/native-skirmish.scene.json");
const SOURCE_QUEEN: &str = include_str!("../../../examples/native-queen.scene.json");
const FLEET_SIZES: [usize; 8] = [1, 2, 4, 8, 16, 32, 64, 128];
pub(super) const GAME_ID: &str = "fleet-skirmish";

#[derive(Clone, Copy)]
enum Terrain { Open, Original, Cover, DenseCover }

#[derive(Clone, Copy)]
enum Objective { Elimination, Survival, Extraction, Escort, Demolition }

/// Native Level waves are source-authored physical squads. This option
/// changes only when squads enter the same live combat/roster pipeline,
/// never their hulls, weapon behaviors, or trainer's simulation engine.
#[derive(Clone, Copy)]
enum ReinforcementPattern {
    AllAtStart,
    BeesTimed, HumansTimed, BothTimed,
    BeesOnDeath, HumansOnDeath, BothOnDeath,
}

impl ReinforcementPattern {
    fn includes(self, side: usize) -> bool {
        match self {
            Self::AllAtStart => false,
            Self::BeesTimed | Self::BeesOnDeath => side == 0,
            Self::HumansTimed | Self::HumansOnDeath => side == 1,
            Self::BothTimed | Self::BothOnDeath => true,
        }
    }

    fn on_command_ship_death(self) -> bool {
        matches!(self, Self::BeesOnDeath | Self::HumansOnDeath | Self::BothOnDeath)
    }

    fn label(self) -> &'static str {
        match self {
            Self::AllAtStart => "no-waves",
            Self::BeesTimed => "bee-waves",
            Self::HumansTimed => "human-waves",
            Self::BothTimed => "both-waves",
            Self::BeesOnDeath => "bee-death-reserves",
            Self::HumansOnDeath => "human-death-reserves",
            Self::BothOnDeath => "both-death-reserves",
        }
    }
}

#[derive(Clone, Copy)]
enum FleetMix { Fighters, Balanced, SupportHeavy, Specialists, Miners, Chargers, FireBarges, Gate, Detonators, Queen }

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
        patterns.push((ReinforcementPattern::BeesTimed,
            "Bee timed reinforcement waves", "  [Additional Bees at 25s and 55s]"));
        patterns.push((ReinforcementPattern::BeesOnDeath,
            "Bee command-ship-loss reserves", "  [Bees arrive when Bee command ship dies]"));
    }
    if fleet_sizes[1] > 1 {
        patterns.push((ReinforcementPattern::HumansTimed,
            "Human timed reinforcement waves", "  [Additional Humans at 25s and 55s]"));
        patterns.push((ReinforcementPattern::HumansOnDeath,
            "Human command-ship-loss reserves", "  [Humans arrive when Human command ship dies]"));
    }
    if fleet_sizes.iter().all(|&size| size > 1) {
        patterns.push((ReinforcementPattern::BothTimed,
            "Both factions receive timed waves", "  [Each side at 25s and 55s]"));
        patterns.push((ReinforcementPattern::BothOnDeath,
            "Both factions have loss-triggered reserves",
            "  [Each side reinforces when its command ship dies]"));
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
    fn label(self) -> &'static str {
        match self {
            Self::Fighters => "fighters",
            Self::Balanced => "balanced",
            Self::SupportHeavy => "support-heavy",
            Self::Specialists => "specialists",
            Self::Miners => "miners",
            Self::Chargers => "chargers",
            Self::FireBarges => "fire-barges",
            Self::Gate => "warp-gate",
            Self::Detonators => "yellow-jackets",
            Self::Queen => "queen",
        }
    }

    /// Keep a fleet composition's actual source archetypes in one place.
    /// Placement bounds and the authored physical scene must agree.
    fn support_type(self, side: usize) -> u8 {
        if side == 0 {
            if matches!(self, Self::Gate) { 20 } else { 2 }
        } else if matches!(self, Self::Queen) {
            17
        } else {
            5
        }
    }

    fn specialist_type(self, side: usize) -> u8 {
        match self {
            Self::Miners => 4,
            Self::Chargers => 0,
            Self::Detonators => 22,
            _ if side == 0 => 18,
            _ => 10,
        }
    }

    fn support_count(self, total: usize) -> usize {
        (match self {
            Self::Fighters => 0,
            Self::Balanced => total.div_ceil(4).min(8),
            Self::SupportHeavy => (total / 2).min(12),
            Self::Specialists => if total >= 4 { total.div_ceil(4).min(8) } else { 0 },
            Self::Gate | Self::Queen => usize::from(total >= 2),
            Self::Miners | Self::Chargers | Self::FireBarges | Self::Detonators => 0,
        }).min(total.saturating_sub(1 + self.specialist_count(total)))
    }

    fn specialist_count(self, total: usize) -> usize {
        if total < 2 { return 0; }
        match self {
            Self::Miners | Self::Chargers | Self::FireBarges | Self::Detonators =>
                total.div_ceil(4).min(8),
            Self::Specialists => 1,
            _ => 0,
        }
    }
}

impl Objective {
    fn label(self) -> &'static str {
        match self {
            Self::Elimination => "Elimination",
            Self::Survival => "Defend Command Ship",
            Self::Extraction => "Escape to Extraction",
            Self::Escort => "Deliver Command Convoy",
            Self::Demolition => "Destroy Enemy Objective",
        }
    }
}

/// Existing durable network source configuration, supplied by the
/// operator as BEES_NATIVE_HIVEMIND_FLEET_CONFIG. Authentication secrets
/// remain environment-owned and are never included in the battle archive.
#[derive(Clone)]
struct NativeHiveMindFleet {
    network: Value,
    opponent_id: u64,
    initial_delay_seconds: i32,
    controlled_side: i32,
}

fn configured_hivemind_fleet(save_root: &Path)
    -> Result<Option<NativeHiveMindFleet>, Box<dyn Error>>
{
    // Match the existing saved LAN host configuration: a portable native
    // game discovers its own settings without demanding process-global
    // environment edits. An explicit environment path overrides the
    // profile for automation, Windows services and external launchers.
    let override_path = std::env::var_os("BEES_NATIVE_HIVEMIND_FLEET_CONFIG");
    let path = override_path.as_ref().map_or_else(
        || save_root.join("hivemind-fleet.json"),
        |path| PathBuf::from(path.as_os_str()));
    if !path.exists() && override_path.is_none() { return Ok(None); }
    let metadata = fs::symlink_metadata(&path)?;
    if !metadata.is_file() || metadata.file_type().is_symlink() ||
        metadata.len() == 0 || metadata.len() > 32_768
    {
        return Err(invalid("HiveMind fleet settings must be a regular JSON file under 32 KiB").into());
    }
    let config: Value = serde_json::from_slice(&fs::read(&path)?)?;
    let opponent_id = config["opponent_id"].as_u64()
        .filter(|id| *id > 0)
        .ok_or_else(|| invalid("HiveMind fleet needs a source opponent_id"))?;
    let initial_delay_seconds = config.get("initial_command_delay_seconds")
        .map(|value| value.as_i64().and_then(|n| i32::try_from(n).ok()))
        .unwrap_or(Some(0))
        .filter(|&seconds| seconds >= 0)
        .ok_or_else(|| invalid("HiveMind initial command delay must be a nonnegative int32"))?;
    let mut network = config.get("hivemind").cloned()
        .ok_or_else(|| invalid("HiveMind fleet config needs an existing HiveMind run configuration"))?;
    if !config.as_object().is_some_and(|obj|
            obj.keys().all(|key| matches!(key.as_str(),
                "hivemind" | "opponent_id" | "initial_command_delay_seconds")))
    {
        return Err(invalid("HiveMind fleet has unknown configuration fields").into());
    }
    network.as_object_mut()
        .ok_or_else(|| invalid("HiveMind network settings must be an object"))?
        .insert("issue_source_commands".to_owned(), json!(true));
    let validated: HiveMindConfig = serde_json::from_value(network.clone())?;
    if validated.endpoint.trim().is_empty() || validated.user_id == 0 ||
        !validated.version.is_finite() || validated.version <= 0.0 ||
        validated.reconnect_game_id.is_some()
    {
        return Err(invalid("new HiveMind fleet needs a valid endpoint, user and fresh server Game").into());
    }
    Ok(Some(NativeHiveMindFleet {
        network, opponent_id, initial_delay_seconds, controlled_side: 0,
    }))
}

#[derive(Clone)]
pub(super) struct FleetChoice {
    /// None preserves the source's two human-controlled factions.
    player_side: Option<i32>,
    fleet_sizes: [usize; 2],
    squad_sizes: [usize; 2],
    mix: [FleetMix; 2],
    terrain: Terrain,
    fog_of_war: bool,
    mining_field: bool,
    /// None preserves the original authored 320x200 map.
    arena_size: Option<u32>,
    lethal_borders: bool,
    ai_both: bool,
    ai_tactics: AiTactics,
    hivemind: Option<NativeHiveMindFleet>,
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

pub(super) fn choose(save_root: &Path) -> Result<Option<FleetChoice>, Box<dyn Error>> {
    let network = configured_hivemind_fleet(save_root)?;
    let mut choices = vec![
        ("Bees commander vs Human AI", "  [Player side 1]"),
        ("Humans commander vs Bee AI", "  [Player side 2]"),
        ("Two local commanders", "  [Both factions]"),
    ];
    if network.is_some() {
        choices.extend([
            ("Bees commander vs Human HiveMind", "  [Live network command AI]"),
            ("Humans commander vs Bee HiveMind", "  [Live network command AI]"),
        ]);
    }
    let rows = choices.iter().map(|(title, status)| BattleMenuRow {
        title, status, unlocked: true,
    }).collect::<Vec<_>>();
    let mut selection = 0_usize;
    let Some(mode) = select_library(&rows, &mut selection, false)? else {
        return Ok(None);
    };
    let Some(fleet_sizes) = choose_sizes()? else { return Ok(None); };
    let Some(terrain) = choose_terrain()? else { return Ok(None); };
    let Some(mining_field) = choose_mining_field(terrain)? else { return Ok(None); };
    let Some((arena_size, lethal_borders)) = choose_arena_size()? else { return Ok(None); };
    let Some(mix) = choose_mix(fleet_sizes, mining_field, arena_size)? else { return Ok(None); };
    let Some(squad_sizes) = choose_squad_sizes(fleet_sizes, mix)? else { return Ok(None); };
    // A single local window cannot present two opposing, independently
    // fog-filtered viewpoints. LAN commanders have separate viewers and
    // may enable authentic fog independently.
    let fog_of_war = if mode == 2 { false } else {
        let Some(enabled) = choose_visibility()? else { return Ok(None); };
        enabled
    };
    let ai_tactics = if mode >= 2 { AiTactics::Aggressive } else {
        let Some(value) = choose_ai_tactics()? else { return Ok(None); };
        value
    };
    let Some(objective) = choose_objective()? else { return Ok(None); };
    let player_side = match mode {
        0 | 3 => Some(1),
        1 | 4 => Some(2),
        _ => None,
    };
    let hivemind = match mode {
        3 | 4 => Some(NativeHiveMindFleet {
            controlled_side: if mode == 3 { 2 } else { 1 },
            ..network.expect("HiveMind menu option requires pinned network source")
        }),
        _ => None,
    };
    let objective_side = match objective {
        Objective::Elimination => None,
        _ => {
            // A local player can own the mission OR intercept an AI-owned
            // objective. The same playable objective-side choice already
            // exists for LAN and fixed native evaluations.
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
        player_side, fleet_sizes, squad_sizes, mix, terrain, fog_of_war, mining_field, arena_size, lethal_borders,
        ai_both: false, ai_tactics, hivemind, objective,
        reinforcements, objective_side, benchmark_worlds: 1, benchmark_workers: 1,
    }))
}

/// The existing private host authenticates a separate remote commander
/// per side; no new network controller, simulation or permission model.
pub(super) fn choose_multiplayer(
    authenticated_side: Option<i32>, save_root: &Path,
) -> Result<Option<FleetChoice>, Box<dyn Error>> {
    // The existing LAN host's token map is authoritative. A HiveMind
    // opponent is selectable only when exactly one real remote commander
    // can authenticate, so two users can never be assigned the same side.
    let configured = configured_hivemind_fleet(save_root)?;
    let use_hivemind = if configured.is_some() && authenticated_side.is_some() {
        let rows = [
            BattleMenuRow { title: "Two remote commanders",
                status: "  [Side 1 and side 2]", unlocked: true },
            BattleMenuRow { title: "Remote commander vs HiveMind",
                status: "  [Existing authenticated socket AI]", unlocked: true },
        ];
        let mut selected = 0_usize;
        match select_library(&rows, &mut selected, false)? {
            Some(0) => false,
            Some(1) => true,
            _ => return Ok(None),
        }
    } else { false };
    let hivemind = if use_hivemind {
        let side = authenticated_side.expect("one authorized remote commander");
        Some(NativeHiveMindFleet {
            controlled_side: 3 - side,
            ..configured.expect("selected configured network controller")
        })
    } else { None };
    let Some(fleet_sizes) = choose_sizes()? else { return Ok(None); };
    let Some(terrain) = choose_terrain()? else { return Ok(None); };
    let Some(mining_field) = choose_mining_field(terrain)? else { return Ok(None); };
    let Some((arena_size, lethal_borders)) = choose_arena_size()? else { return Ok(None); };
    let Some(mix) = choose_mix(fleet_sizes, mining_field, arena_size)? else { return Ok(None); };
    let Some(squad_sizes) = choose_squad_sizes(fleet_sizes, mix)? else { return Ok(None); };
    let Some(fog_of_war) = choose_visibility()? else { return Ok(None); };
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
        player_side: if use_hivemind { authenticated_side } else { None },
        fleet_sizes, squad_sizes, mix, terrain, fog_of_war, mining_field, arena_size, lethal_borders,
        ai_both: false,
        ai_tactics: AiTactics::Aggressive,
        hivemind, objective, reinforcements, objective_side,
        benchmark_worlds: 1, benchmark_workers: 1,
    }))
}

/// The actual headless native runner measures steps/s and wall time with
/// exactly the same collision, command and ship code used in windowed play.
pub(super) fn choose_benchmark() -> Result<Option<FleetChoice>, Box<dyn Error>> {
    let Some(fleet_sizes) = choose_sizes()? else { return Ok(None); };
    let Some(terrain) = choose_terrain()? else { return Ok(None); };
    let Some(mining_field) = choose_mining_field(terrain)? else { return Ok(None); };
    let Some((arena_size, lethal_borders)) = choose_arena_size()? else { return Ok(None); };
    let Some(mix) = choose_mix(fleet_sizes, mining_field, arena_size)? else { return Ok(None); };
    let Some(squad_sizes) = choose_squad_sizes(fleet_sizes, mix)? else { return Ok(None); };
    let Some(fog_of_war) = choose_visibility()? else { return Ok(None); };
    let Some(ai_tactics) = choose_ai_tactics()? else { return Ok(None); };
    let Some(objective) = choose_objective()? else { return Ok(None); };
    let objective_side = match objective {
        Objective::Elimination => None,
        _ => {
            let Some(side) = choose_objective_side()? else { return Ok(None); };
            Some(side)
        },
    };
    // The same published real-wave scenario can be profiled across many
    // arenas, so dynamic admission costs are measurable rather than guessed.
    let reinforcements = if matches!(objective, Objective::Survival) {
        ReinforcementPattern::AllAtStart
    } else {
        let Some(pattern) = choose_reinforcements(fleet_sizes)? else {
            return Ok(None);
        };
        pattern
    };
    let Some(worlds) = choose_resource("Concurrent physical arenas",
        &[1, 2, 4, 8, 16, 32])? else { return Ok(None); };
    let workers = [1, 2, 4, 8, 16, 32].into_iter()
        .filter(|&count| count <= worlds).collect::<Vec<_>>();
    let Some(threads) = choose_resource("CPU physics workers", &workers)?
        else { return Ok(None); };
    Ok(Some(FleetChoice {
        player_side: None, fleet_sizes, squad_sizes, mix, terrain, fog_of_war, mining_field, arena_size, lethal_borders,
        ai_both: true,
        ai_tactics, hivemind: None, objective, objective_side,
        reinforcements, benchmark_worlds: worlds, benchmark_workers: threads,
    }))
}

/// The authored Stage owns FogOfWarVision for gameplay, headless AI and
/// training. Selecting visibility changes only the shared native scene
/// input, never introduces a second presentation-only sight model.
fn choose_visibility() -> Result<Option<bool>, Box<dyn Error>> {
    let options = [
        ("All visible", "  [No fog of war]"),
        ("Fog of war", "  [Source ship vision and discovery]"),
    ];
    let rows = options.iter().map(|(title, status)| BattleMenuRow {
        title, status, unlocked: true,
    }).collect::<Vec<_>>();
    let mut selected = 0_usize;
    Ok(select_library(&rows, &mut selected, false)?.map(|choice| choice == 1))
}

/// Use genuine mineral asteroids only where the physical prefab cannot
/// overlap the authored central obstacle geometry at scene creation.
fn choose_mining_field(terrain: Terrain) -> Result<Option<bool>, Box<dyn Error>> {
    if !matches!(terrain, Terrain::Open | Terrain::Original) {
        return Ok(Some(false));
    }
    let rows = [
        BattleMenuRow { title: "Combat only", status: "  [No mineral deposits]",
            unlocked: true },
        BattleMenuRow { title: "Mineral field", status: "  [Two physical mineable asteroids]",
            unlocked: true },
    ];
    let mut selected = 0_usize;
    Ok(select_library(&rows, &mut selected, false)?.map(|index| index == 1))
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

fn choose_mix(fleet_sizes: [usize; 2], mining_field: bool, arena_size: Option<u32>)
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
        let mut options = vec![
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
        if side == 0 {
            if mining_field {
                let miners = FleetMix::Miners.specialist_count(total);
                options.push((FleetMix::Miners, format!(
                    "{} fighters, {} Carpenter Bees  |  Source mineral mining",
                    total - miners, miners,
                )));
            }
            options.push((FleetMix::Gate, format!(
                "{} fighters, 1 WarpGate  |  Source FullRetreat evacuation",
                total - 1,
            )));
        } else {
            let charges = FleetMix::Chargers.specialist_count(total);
            options.push((FleetMix::Chargers, format!(
                "{} fighters, {} Barges  |  Independent physical Charge weapons",
                total - charges, charges,
            )));
            let barges = FleetMix::FireBarges.specialist_count(total);
            options.push((FleetMix::FireBarges, format!(
                "{} fighters, {} Fire Barges  |  Source area-damage weapon crews",
                total - barges, barges,
            )));
            let detonators = FleetMix::Detonators.specialist_count(total);
            options.push((FleetMix::Detonators, format!(
                "{} fighters, {} Yellow Jackets  |  Source physical detonations",
                total - detonators, detonators,
            )));
            if arena_size.is_some_and(|size| size >= 1024) {
                options.push((FleetMix::Queen, format!(
                    "{} fighters, 1 Queen  |  Source Yellow Jacket waves",
                    total - 1,
                )));
            }
        }
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

/// One physical squad may contain several original ships. This reduces
/// command scheduling overhead in large games while preserving each ship's
/// weapon, position, fleet identity, and policy/gameplay simulation.
fn choose_squad_sizes(
    fleet_sizes: [usize; 2], mix: [FleetMix; 2],
) -> Result<Option<[usize; 2]>, Box<dyn Error>> {
    let mut selected_sizes = [1_usize; 2];
    for side in 0..2_usize {
        let fighters = fleet_sizes[side] -
            mix[side].support_count(fleet_sizes[side]) -
            mix[side].specialist_count(fleet_sizes[side]);
        if fighters < 2 { continue; }
        let options = [1_usize, 2, 4, 8].into_iter()
            .filter(|&size| size <= fighters).collect::<Vec<_>>();
        let labels = options.iter().map(|size| {
            format!("{} fighter{} per squad  |  {} independent command groups",
                size, if *size == 1 { "" } else { "s" }, fighters.div_ceil(*size))
        }).collect::<Vec<_>>();
        let rows = labels.iter().map(|title| BattleMenuRow {
            title, status: "  [Shared game and training squads]", unlocked: true,
        }).collect::<Vec<_>>();
        let mut selected = 0_usize;
        let Some(index) = select_library(&rows, &mut selected, false)? else {
            return Ok(None);
        };
        selected_sizes[side] = options[index];
    }
    Ok(Some(selected_sizes))
}

fn choose_objective() -> Result<Option<Objective>, Box<dyn Error>> {
    let choices = [
        ("Eliminate all opposing ships", "  [Standard battle]"),
        ("Defend your command ship for 90 seconds", "  [Incoming waves]"),
        ("Reach the enemy-side extraction zone", "  [Tactical escape]"),
        ("Escort your command ship to extraction", "  [Defend the convoy]"),
        ("Destroy a fortified MapObject", "  [90-second assault]"),
    ];
    let rows = choices.iter().map(|(title, status)| BattleMenuRow {
        title, status, unlocked: true,
    }).collect::<Vec<_>>();
    let mut selected = 0_usize;
    Ok(select_library(&rows, &mut selected, false)?.map(|index| match index {
        1 => Objective::Survival,
        2 => Objective::Extraction,
        3 => Objective::Escort,
        4 => Objective::Demolition,
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

/// The source 320x200 map remains the default for existing games and
/// benchmarks. Larger arenas use the exact source MapBorder dimensions,
/// not cosmetic sprite scaling or different training-only collision rules.
fn choose_arena_size() -> Result<Option<(Option<u32>, bool)>, Box<dyn Error>> {
    let choices = [
        ("Classic map", "  [Original 320 x 200]"),
        ("Medium arena", "  [512 x 512]"),
        ("Large arena", "  [1024 x 1024]"),
        ("Very large arena", "  [2048 x 2048]"),
        ("Hazard arena", "  [512 x 512, lethal border contacts]"),
        ("Large hazard arena", "  [1024 x 1024, lethal border contacts]"),
    ];
    let rows = choices.iter().map(|(title, status)| BattleMenuRow {
        title, status, unlocked: true,
    }).collect::<Vec<_>>();
    let mut selected = 0;
    Ok(select_library(&rows, &mut selected, false)?.map(|index| {
        match index {
            0 => (None, false),
            1 => (Some(512), false),
            2 => (Some(1024), false),
            3 => (Some(2048), false),
            4 => (Some(512), true),
            _ => (Some(1024), true),
        }
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
    arena_size: Option<u32>, catalog: &ShipAssetCatalog,
) -> Result<Vec<[f32; 2]>, Box<dyn Error>> {
    let support = mix.support_count(total);
    let specialist = mix.specialist_count(total);
    let fighters = total - support - specialist;
    let fighter_kind = if side == 0 { 21 } else { 13 };
    let support_kind = mix.support_type(side);
    let specialist_kind = mix.specialist_type(side);
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
        if matches!(mix, FleetMix::Queen) && kind == 17 {
            // A Queen is a genuine 128-unit-class factory, not a fighter
            // squeezed into the classic 320x200 lane. Its source-local
            // Yellow Jacket wave offset is rotated by its own body pose.
            // A large real Stage gives the Queen AND spawned minions enough
            // room without altering CombatWorld's source spawn rules.
            let half = arena_size.map_or(0.0, |size| size as f32 * 0.5);
            let queen_x = 200.0_f32;
            if side != 1 || queen_x + half_x + 16.0 >= half ||
                half_y + 16.0 >= half ||
                occupied.iter().any(|&(x, y, half_other_x, half_other_y)|
                    (queen_x - x).abs() < half_x + half_other_x + 2.0 &&
                    y.abs() < half_y + half_other_y + 2.0)
            {
                return Err(invalid("source Queen needs an unobstructed large native arena").into());
            }
            offsets[index] = [queen_x - home, 0.0];
            occupied.push((queen_x, 0.0, half_x, half_y));
            continue;
        }
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

/// Placement is resolved once at authoring time against all selected real
/// ship extents. A free Striker must begin inside the same Level, separate
/// from its Carrier and every other authored ship; the shared game will
/// handle its flight, bombing, rearming, and eventual retirement.
fn place_free_striker(
    parent: [f32; 2], size: [f32; 2],
    occupied: &mut Vec<(f32, f32, f32, f32)>,
) -> Result<[f32; 2], Box<dyn Error>> {
    let (half_x, half_y) = (size[0] * 0.5, size[1] * 0.5);
    let mut best: Option<(f32, [f32; 2])> = None;
    for x in (35..=151).step_by(4) {
        let x = x as f32;
        if x - half_x < 30.0 || x + half_x > 158.0 { continue; }
        for y in (-92..=92).step_by(4) {
            let y = y as f32;
            if y.abs() + half_y > 96.0 { continue; }
            if occupied.iter().any(|&(other_x, other_y, other_half_x, other_half_y)|
                (x - other_x).abs() < half_x + other_half_x + 2.0 &&
                (y - other_y).abs() < half_y + other_half_y + 2.0
            ) {
                continue;
            }
            let score = (x - (parent[0] - 31.0)).powi(2) +
                (y - parent[1]).powi(2);
            if best.is_none_or(|(prior, _)| score < prior) {
                best = Some((score, [x, y]));
            }
        }
    }
    let (_, position) = best.ok_or_else(||
        invalid("no unobstructed source-sized space for the Carrier's Striker deck"))?;
    occupied.push((position[0], position[1], half_x, half_y));
    Ok(position)
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
    // These outcomes are emitted by the same durable terminal journal as
    // the playable game. Report physical mission completion alongside SPS,
    // not just whichever faction happened to eliminate its opponent.
    let mut objective_results = serde_json::Map::new();
    if let Some(scenes) = summary["evaluation"]["by_scene_sha256"].as_object() {
        for source in scenes.values() {
            if let Some(kinds) = source["mission_outcomes"].as_object() {
                for (kind, counts) in kinds {
                    let attempts = counts["attempts"].as_u64().ok_or_else(||
                        invalid("native mission benchmark has no physical attempt count"))?;
                    let completions = counts["completions"].as_u64().ok_or_else(||
                        invalid("native mission benchmark has no physical completion count"))?;
                    if completions > attempts {
                        return Err(invalid("native mission benchmark has impossible objective successes").into());
                    }
                    let totals = objective_results.entry(kind.clone()).or_insert_with(|| json!({
                        "attempts": 0_u64, "completions": 0_u64,
                    }));
                    let prior_attempts = totals["attempts"].as_u64().unwrap_or(0);
                    let prior_completions = totals["completions"].as_u64().unwrap_or(0);
                    totals["attempts"] = json!(prior_attempts + attempts);
                    totals["completions"] = json!(prior_completions + completions);
                }
            }
        }
    }
    for counts in objective_results.values_mut() {
        let attempts = counts["attempts"].as_u64().unwrap_or(0);
        let completions = counts["completions"].as_u64().unwrap_or(0);
        counts["completion_rate"] = json!(if attempts > 0 {
            completions as f64 / attempts as f64
        } else { 0.0 });
    }
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
        "physical_mission_outcomes": objective_results,
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

/// Resize the source Map around its true center and install the exact four
/// Unity MapBorder trigger boxes. This edits only the authored scene/layout
/// and the rollout's fragment bounds: windowed, LAN, policy and benchmarks
/// all subsequently use the same ordinary CombatWorld collider records.
fn apply_native_arena(
    scene: &mut Value, run: &mut Value,
    layout: &mut NativeLevelLayout, size: u32,
) -> Result<(), Box<dyn Error>> {
    let lower = scene["bounds"]["min"].as_array()
        .ok_or_else(|| invalid("native arena is missing source map bounds"))?;
    let upper = scene["bounds"]["max"].as_array()
        .ok_or_else(|| invalid("native arena is missing source map bounds"))?;
    if lower.len() != 2 || upper.len() != 2 {
        return Err(invalid("native arena source bounds have the wrong dimension").into());
    }
    let mut middle = [0.0_f32; 2];
    for axis in 0..2 {
        let min = lower[axis].as_f64()
            .ok_or_else(|| invalid("native arena source minimum is missing"))?;
        let max = upper[axis].as_f64()
            .ok_or_else(|| invalid("native arena source maximum is missing"))?;
        if !min.is_finite() || !max.is_finite() || min >= max {
            return Err(invalid("native arena source bounds are not finite").into());
        }
        middle[axis] = ((min + max) * 0.5) as f32;
    }
    // 4-unit source map steps; keep square bounds within NavigationGrid's
    // existing one-million-cell hard cap. The UI offers bounded sizes.
    if size < 64 || size > 4000 || size % 4 != 0 {
        return Err(invalid("invalid source-sized native arena").into());
    }
    let half = size as f32 * 0.5;
    let [x, y] = middle;
    scene["bounds"] = json!({
        "min": [x - half, y - half], "max": [x + half, y + half],
    });
    scene["starting_positions"] = json!([
        [x - half * 0.5, y], [x + half * 0.5, y],
    ]);
    run["fragment_spawn"]["map_bounds"] = scene["bounds"].clone();
    run["fragment_spawn"]["half_map_extents"] = json!([half, half]);

    let center = half + 12.0;
    let overhung = half + 24.0;
    let border_specs = [
        ("Top Border", [x, y + center], [overhung, 12.0]),
        ("Bottom Border Variant", [x, y - center], [overhung, 12.0]),
        ("Left Border Variant", [x - center, y], [12.0, overhung]),
        ("Right Border Variant", [x + center, y], [12.0, overhung]),
    ];
    layout.add_map_borders.extend(
        border_specs.into_iter().enumerate().map(|(index, (name, position, half))|
            NativePlacedStaticBox {
                // Distinct from source ships, cloned fighter IDs and the
                // cover obstacles at 20_001..20_004.
                entity_id: 20_101 + index as i64,
                prefab_name: name.into(),
                position,
                rotation_degrees: 0.0,
                local_corners: Some([
                    [-half[0], -half[1]], [half[0], -half[1]],
                    [half[0], half[1]], [-half[0], half[1]],
                ]),
            }
        ),
    );
    Ok(())
}

/// Freeze a complete custom battle beside its eventual input trace. Replays
/// load these exact authored scene/config bytes, not today's library template.
pub(super) fn archive(
    parent: &Path, selected: FleetChoice,
) -> Result<PathBuf, Box<dyn Error>> {
    let mut scene: Value = serde_json::from_str(SOURCE_SCENE)?;
    let mut run: Value = serde_json::from_str(SOURCE_RUN)?;
    // This is the same native FogOfWarVision switch consumed by the
    // actual Level; camera and policy perception read that shared state.
    scene["activate_fog_of_war"] = json!(selected.fog_of_war);
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
    let mut scout_squads = HashMap::<i64, f64>::new();
    let mut miner_squads = Vec::new();
    let mut bombing_squads = Vec::new();
    let mut charger_squads = Vec::new();
    // Carrier roots are source FleetShips; their individual free Strikers
    // are materialized after every ordinary hull position is known.
    let mut carrier_parents: Vec<(i64, i64, usize, [f32; 2], bool)> = Vec::new();
    let catalog = ShipAssetCatalog::embedded()
        .map_err(|_| invalid("native source ship collider catalogue is incomplete"))?;
    let offsets = [
        if matches!(selected.mix[0], FleetMix::Fighters) {
            fleet_offsets(0, selected.fleet_sizes[0])
        } else {
            mixed_fleet_offsets(0, selected.fleet_sizes[0], selected.mix[0], selected.arena_size, &catalog)?
        },
        if matches!(selected.mix[1], FleetMix::Fighters) {
            fleet_offsets(1, selected.fleet_sizes[1])
        } else {
            mixed_fleet_offsets(1, selected.fleet_sizes[1], selected.mix[1], selected.arena_size, &catalog)?
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
    let mut source_archetypes: Value = serde_json::from_str(SOURCE_ARCHETYPES)?;
    if matches!(selected.mix[1], FleetMix::Queen) {
        let queen_source: Value = serde_json::from_str(SOURCE_QUEEN)?;
        let queen = queen_source["ships"].as_array()
            .and_then(|ships| ships.iter().find(|ship|
                ship["side"] == 2 && ship["ship_type"] == 17))
            .ok_or_else(|| invalid("native original Queen scene has no physical source hull"))?
            .clone();
        // Preserve the complete source Queen.Spawn minion template. The
        // shared Level will materialize real Yellow Jackets on its clock.
        source_archetypes["ships"].as_array_mut()
            .ok_or_else(|| invalid("native source archetypes are not a ship roster"))?
            .push(queen);
    }
    if selected.mining_field {
        // Duplicate the original authenticated physical mining collider
        // into a new, nonconflicting native scene identity. Its ore/health,
        // collision layer, and eventual depletion are handled by the shared
        // CombatWorld and Level, not by this fleet authoring screen.
        let mut mine = source_archetypes["mining_asteroids"]
            .as_array().and_then(|ores| ores.first())
            .ok_or_else(|| invalid("native source has no mineable asteroid archetype"))?
            .clone();
        // Keep source entities below the native layout's cloned ship ID
        // range (10_000+). Layout validation requires every clone identity
        // to be higher than all authored scene entities.
        mine["entity_id"] = json!(31_i64);
        let mut other = mine.clone();
        other["entity_id"] = json!(32_i64);
        let original = mine["position"].as_array().ok_or_else(||
            invalid("source mineable asteroid has no physical position"))?;
        let x = original.first().and_then(Value::as_f64)
            .ok_or_else(|| invalid("source mineable asteroid has no X coordinate"))?;
        let y = original.get(1).and_then(Value::as_f64)
            .ok_or_else(|| invalid("source mineable asteroid has no Y coordinate"))?;
        other["position"] = json!([-x, -y]);
        scene["activate_mining"] = json!(true);
        scene["mining_asteroids"] = json!([mine, other]);
    }
    let available = source_archetypes["ships"].as_array().ok_or_else(||
        invalid("native skirmish has no source ship archetypes"))?;
    for side in 0..2_usize {
        if support_count[side] == 0 { continue; }
        let side_id = side as i64 + 1;
        let archetype = selected.mix[side].support_type(side);
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
        if side == 1 && archetype == 5 {
            carrier_parents.push((ship_id, squad_id,
                fighter_count[side] + specialist_count[side],
                [75.0 + offset[0], offset[1]], controlled));
        }
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
        let specialist_type = selected.mix[side].specialist_type(side);
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
        if matches!(selected.mix[side], FleetMix::Miners) {
            miner_squads.push(squad_id);
        } else if matches!(selected.mix[side], FleetMix::Chargers) {
            charger_squads.push(squad_id);
        } else if side == 0 {
            let sight = template["sight"]["source_sight"].as_f64()
                .filter(|v| v.is_finite() && *v > 0.0)
                .ok_or_else(|| invalid("authored Scout has no source sight range"))?;
            scout_squads.insert(squad_id, sight);
        } else {
            bombing_squads.push(squad_id);
        }
    }
    for side in 0..2_usize {
        for index in 1..selected.fleet_sizes[side] {
            let specialist = specialist_count[side] != 0 &&
                (fighter_count[side]..fighter_count[side] + specialist_count[side])
                    .contains(&index);
            let supporting = index >= fighter_count[side] + specialist_count[side];
            let support_origin = fighter_count[side] + specialist_count[side];
            if specialist {
                if index == fighter_count[side] {
                    // The first specialty hull is the source template.
                    squad_ids[side].push(105 + side as i64);
                } else if matches!(selected.mix[side], FleetMix::Miners) {
                    // A Carpenter Bee has no source-owned child, active
                    // special weapon or lifecycle contract. It is safe to
                    // use the shared clone path for this ordinary miner.
                    let side_base = (side as i64 + 1) * 100;
                    let new_squad = 2_000 + side_base + index as i64;
                    let new_ship = 10_000 + side_base + index as i64;
                    let offset = offsets[side][index];
                    let origin = offsets[side][fighter_count[side]];
                    cloned.clone_squads.push(NativeSquadClone {
                        source_squad_item_id: 105 + side as i64,
                        new_squad_item_id: new_squad,
                        ship_ids: vec![new_ship],
                        by: [offset[0] - origin[0], offset[1] - origin[1]],
                        count_as_fleet_value: true,
                    });
                    squad_ids[side].push(new_squad);
                    miner_squads.push(new_squad);
                } else {
                    // Every Barge and Yellow Jacket has a physically
                    // registered Bomb/Charge weapon identity. Generic
                    // NativeSquadClone correctly refuses those actors:
                    // materialize independent source-authored FleetShips
                    // with distinct weapons and ordinary command squads.
                    if side != 1 || !matches!(selected.mix[side],
                        FleetMix::Chargers | FleetMix::FireBarges | FleetMix::Detonators)
                    {
                        return Err(invalid("source specialist replication is unsupported").into());
                    }
                    let ship_type = selected.mix[side].specialist_type(side);
                    let template = available.iter().find(|ship|
                        ship["side"] == 2 && ship["ship_type"] == ship_type
                    ).ok_or_else(|| invalid("multi-specialist fleet lacks physical source class"))?;
                    let expected_special = match selected.mix[side] {
                        FleetMix::Chargers => "barge",
                        FleetMix::FireBarges => "fire_barge",
                        FleetMix::Detonators => "yellow_jacket",
                        _ => unreachable!("validated native special fleet mix"),
                    };
                    if template["special"]["kind"] != expected_special ||
                        !template["special_weapon"].is_object()
                    {
                        return Err(invalid("native specialist lacks its source Bomb weapon contract").into());
                    }
                    // Under 10,000 so native clone IDs remain above all
                    // original entities. Weapon IDs are globally distinct
                    // from the source special mounts and Carrier decks.
                    let ship_id = 600_i64 + index as i64;
                    let squad_id = 700_i64 + index as i64;
                    let weapon_id = 7_000_i64 + index as i64;
                    let offset = offsets[side][index];
                    let controlled = !selected.ai_both &&
                        selected.player_side.is_none_or(|player| player == 2);
                    let mut ship = template.clone();
                    ship["entity_id"] = json!(ship_id);
                    ship["fleet_ship_id"] = json!(ship_id);
                    ship["special_weapon"]["weapon_id"] = json!(weapon_id);
                    ship["position"] = json!([75.0 + offset[0], offset[1]]);
                    ship["formation_offset"] = json!([0.0, 0.0]);
                    ship["user_controlled"] = json!(controlled);
                    scene["ships"].as_array_mut()
                        .ok_or_else(|| invalid("native specialist scene lost ship roster"))?
                        .push(ship);
                    scene["squads"].as_array_mut()
                        .ok_or_else(|| invalid("native specialist scene lost squad roster"))?
                        .push(json!({
                            "item_id": squad_id, "side": 2, "members": [ship_id],
                            "shooting_strategy_id": 1,
                            "user_controlled": controlled,
                            "can_accept_input": controlled,
                            "command_bans": [],
                        }));
                    squad_ids[side].push(squad_id);
                    if matches!(selected.mix[side], FleetMix::Chargers) {
                        charger_squads.push(squad_id);
                    } else {
                        bombing_squads.push(squad_id);
                    }
                }
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
            if supporting && side == 1 &&
                !matches!(selected.mix[side], FleetMix::Queen)
            {
                // A Carrier must not be cloned through NativeLevelLayout:
                // its free Striker deck has real source-owned relationships
                // and cannot be remapped by a generic single-hull clone.
                // Materialize this one additional Carrier as a new source
                // FleetShip, then attach its authentic deck below.
                let authored_ship_id = 100 + index as i64;
                let authored_squad_id = 200 + index as i64;
                let mut carrier = scene["ships"].as_array()
                    .and_then(|ships| ships.iter().find(|ship|
                        ship["entity_id"] == 14))
                    .ok_or_else(|| invalid("native human support Carrier template is missing"))?
                    .clone();
                carrier["entity_id"] = json!(authored_ship_id);
                carrier["fleet_ship_id"] = json!(authored_ship_id);
                carrier["position"] = json!([75.0 + offset[0], offset[1]]);
                let controlled = !selected.ai_both &&
                    selected.player_side.is_none_or(|player| player == 2);
                carrier["user_controlled"] = json!(controlled);
                scene["ships"].as_array_mut().ok_or_else(||
                    invalid("native scene has no physical carrier roster"))?.push(carrier);
                scene["squads"].as_array_mut().ok_or_else(||
                    invalid("native scene has no carrier squad registry"))?.push(json!({
                        "item_id": authored_squad_id,
                        "side": 2, "members": [authored_ship_id],
                        "shooting_strategy_id": 1,
                        "user_controlled": controlled,
                        "can_accept_input": controlled,
                        "command_bans": [],
                    }));
                carrier_parents.push((
                    authored_ship_id, authored_squad_id, index,
                    [75.0 + offset[0], offset[1]], controlled,
                ));
                squad_ids[side].push(authored_squad_id);
                support_squads.push(authored_squad_id);
                continue;
            }
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
    let mut carrier_children = HashMap::<i64, i64>::new();
    let mut free_striker_squads = Vec::<(i64, bool)>::new();
    if !carrier_parents.is_empty() {
        let striker_template = available.iter().find(|ship|
            ship["side"] == 2 && ship["ship_type"] == 19
        ).ok_or_else(|| invalid("authored Carrier has no original Striker child template"))?;
        if striker_template["special"]["kind"] != "striker" ||
            !striker_template["special_weapon"].is_object()
        {
            return Err(invalid("authored Carrier Striker has no physical Bomb weapon").into());
        }
        let striker_size = catalog.sprite_size(19)
            .ok_or_else(|| invalid("original Striker sprite extents are missing"))?;
        // All allocated source hulls and stage clones are placed according
        // to these exact offsets. Reserve their extents before choosing
        // the free child's new physical position.
        let mut occupied = Vec::new();
        for side in 0..2_usize {
            for index in 0..selected.fleet_sizes[side] {
                let kind = if index >= fighter_count[side] + specialist_count[side] {
                    selected.mix[side].support_type(side)
                } else if index >= fighter_count[side] {
                    selected.mix[side].specialist_type(side)
                } else if side == 0 { 21 } else { 13 };
                let extents = catalog.sprite_size(kind)
                    .ok_or_else(|| invalid("native Carrier fleet has no source sprite extents"))?;
                let offset = offsets[side][index];
                let x = (if side == 0 { -75.0 } else { 75.0 }) + offset[0];
                occupied.push((x, offset[1], extents.x * 0.5, extents.y * 0.5));
            }
        }
        for (parent_id, parent_squad, index, parent_position, controlled)
            in carrier_parents
        {
            // These authored IDs stay below native layout clone IDs and
            // above the source's original weapon IDs; no relationship is
            // cloned without its owning parent or unique Bomb mount.
            let child_id = 5_000 + index as i64;
            let child_weapon_id = 6_000 + index as i64;
            let child_squad_id = 500 + index as i64;
            let position = place_free_striker(parent_position,
                [striker_size.x, striker_size.y], &mut occupied)?;
            let mut striker = striker_template.clone();
            striker["entity_id"] = json!(child_id);
            striker["position"] = json!(position);
            striker["user_controlled"] = json!(controlled);
            striker["persistent_fleet"] = json!(false);
            striker["fleet_ship_id"] = json!(parent_id);
            striker["is_carrier_ship"] = json!(true);
            striker["formation_offset"] = json!([0.0, 0.0]);
            striker["special"]["carrier_id"] = json!(parent_id);
            striker["special_weapon"]["weapon_id"] = json!(child_weapon_id);
            scene["ships"].as_array_mut().ok_or_else(||
                invalid("native Carrier source lost its ship registry"))?.push(striker);
            scene["squads"].as_array_mut().ok_or_else(||
                invalid("native Carrier source lost its squad registry"))?.push(json!({
                    "item_id": child_squad_id, "side": 2,
                    "members": [child_id], "shooting_strategy_id": 1,
                    "user_controlled": controlled, "can_accept_input": controlled,
                    "is_carrier_squad": true, "command_bans": [],
                }));
            carrier_children.insert(parent_squad, child_squad_id);
            free_striker_squads.push((child_squad_id, controlled));
        }
    }
    if let Some(hivemind) = &selected.hivemind {
        // This is the original game's per-squad HiveMind ownership, not
        // scripted AI disguised as a network request. Even human squads
        // carry explicit non-HiveMind authority while Stage activation is
        // enabled. The ordinary DurableHiveMindConnection will manage
        // authentication, command hashes, outcomes and continuation.
        let mobility = scene["ships"].as_array()
            .ok_or_else(|| invalid("HiveMind battle has no physical ships"))?
            .iter().map(|ship| {
                let id = ship["entity_id"].as_i64()
                    .ok_or_else(|| invalid("HiveMind hull needs a physical identity"))?;
                let speed = ship["base_speed"].as_f64()
                    .filter(|speed| speed.is_finite() && *speed >= 0.0)
                    .ok_or_else(|| invalid("HiveMind hull needs authentic movement speed"))?;
                Ok::<_, io::Error>((id, speed > 0.0))
            }).collect::<Result<HashMap<_, _>, _>>()?;
        for squad in scene["squads"].as_array_mut()
            .ok_or_else(|| invalid("HiveMind battle has no source squads"))?
        {
            let side = squad["side"].as_i64()
                .ok_or_else(|| invalid("HiveMind squad lacks faction ownership"))?;
            let members = squad["members"].as_array()
                .ok_or_else(|| invalid("HiveMind squad lacks source physical members"))?;
            if members.is_empty() {
                return Err(invalid("HiveMind cannot own an empty native squad").into());
            }
            let mobile = members.iter().map(|member|
                member.as_i64().and_then(|id| mobility.get(&id).copied())
            ).collect::<Option<Vec<_>>>()
                .ok_or_else(|| invalid("HiveMind squad references an unknown physical hull"))?
                .into_iter().any(|mobile| mobile);
            squad["hivemind"] = json!({
                "opponent_id": hivemind.opponent_id,
                "hivemind_controlled": side == i64::from(hivemind.controlled_side),
                "immobile": !mobile,
            });
        }
        scene["activate_hivemind"] = json!(true);
        scene["hivemind_initial_command_delay_seconds"] =
            json!(hivemind.initial_delay_seconds);
        run["hivemind"] = hivemind.network.clone();
    }
    let level_name = scene["level_name"].as_str()
        .unwrap_or("Fleet Skirmish").to_owned();
    scene["level_name"] = json!(format!(
        "{level_name}  |  Support: {} {}, {} {}  |  Specialist hulls: {} Bees, {} Humans",
        support_count[0],
        if matches!(selected.mix[0], FleetMix::Gate) { "WarpGates" } else { "Beehives" },
        support_count[1],
        if matches!(selected.mix[1], FleetMix::Queen) { "Queens" } else { "Carriers" },
        specialist_count[0], specialist_count[1],
    ));
    if selected.mining_field {
        let title = scene["level_name"].as_str().unwrap_or("Fleet Skirmish");
        scene["level_name"] = json!(format!("{title}  |  Physical mineral field"));
    }
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
    if let Some(size) = selected.arena_size {
        apply_native_arena(&mut scene, &mut run, &mut cloned, size)?;
        // Both modes are physical Level rules; saved fleet training
        // materializes exactly these game scene bytes before PPO ownership.
        scene["lethal_map_borders"] = json!(selected.lethal_borders);
        // Classic maps have no generated MapBorder geometry: changing
        // their historical free-flight behavior would silently alter
        // existing saved battles, benchmarks and game replay results.
        scene["constrain_map_borders"] =
            json!(selected.arena_size.is_some() && !selected.lethal_borders);
        let name = scene["level_name"].as_str().unwrap_or("Fleet Skirmish");
        scene["level_name"] = json!(format!("{name}  |  Arena: {size} x {size}{}",
            if selected.lethal_borders { " (lethal borders)" } else { "" }));
    }
    // The sources and their true physical identities have all been
    // authored by now. Group only ordinary fighters, leaving original
    // specialist, support, Carrier and transient minion ownership intact.
    // The shared native Level merges these command owners after cloning
    // all physical bodies, before gameplay or RL policy registration.
    for side in 0..2_usize {
        let originals = std::mem::take(&mut squad_ids[side]);
        let mut groups = Vec::with_capacity(originals.len());
        for members in originals[..fighter_count[side]]
            .chunks(selected.squad_sizes[side])
        {
            let &primary = members.first()
                .ok_or_else(|| invalid("fighter squad group is empty"))?;
            groups.push(primary);
            if members.len() > 1 {
                cloned.merge_squads.push(NativeSquadMerge {
                    primary_squad_item_id: primary,
                    merged_squad_item_ids: members[1..].to_vec(),
                });
            }
        }
        groups.extend_from_slice(&originals[fighter_count[side]..]);
        squad_ids[side] = groups;
    }
    // The same source hotkey metadata that GameState sets up for Unity's
    // initial player squads is now available to both local commanders and
    // independently authenticated LAN clients. Each side receives its own
    // stable 1..9,0 mapping after grouped squads have been constructed.
    for side in 0..2_usize {
        let controlled = !selected.ai_both &&
            selected.player_side.is_none_or(|player| player == side as i32 + 1);
        if !controlled { continue; }
        let count = u32::try_from(squad_ids[side].len())
            .map_err(|_| invalid("native player squad count overflow"))?;
        for (index, &squad_item_id) in squad_ids[side].iter().take(10).enumerate() {
            cloned.number_squads.push(NativeSquadShortcut {
                squad_item_id, side: side as i32 + 1,
                number: index as u32 + 1,
                original_squad_count: count,
            });
        }
    }
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
    } else if matches!(selected.objective, Objective::Extraction | Objective::Escort) {
        let player = selected.objective_side.ok_or_else(||
            invalid("extraction and escort require a real objective faction"))?;
        run["levels"][0]["extraction"] = json!({
            "player_side": player,
            "center": [if player == 1 { 125.0 } else { -125.0 }, 0.0],
            "half_extents": [6.0, 6.0],
            "activation_radius": 10.0,
            "layer": 19,
        });
        if matches!(selected.objective, Objective::Escort) {
            // The first commander is a real mobile, immediately deployed
            // hull in either faction. Delivery and death use the same
            // extraction collider and shared terminal as headless training.
            run["levels"][0]["escort"] = json!({
                "escorting_side": player,
                "escorted_ship_id": if player == 1 { 11 } else { 12 },
                "seconds": 90.0,
            });
        }
    } else if matches!(selected.objective, Objective::Demolition) {
        let attacker = selected.objective_side.ok_or_else(||
            invalid("demolition requires a real attacking faction"))?;
        // This is a *physical* native MapObject, not a cosmetic marker:
        // authored collision, durable health, point-fire and terminal
        // logic all belong to the normal CombatWorld. Choose a fresh
        // identity distinct from the duel's original ships/obstacle.
        let target_id = 91_i64;
        let map_objects = scene.as_object_mut().ok_or_else(||
            invalid("fleet source scene must be a JSON object"))?
            .entry("map_objects").or_insert_with(|| json!([]));
        map_objects.as_array_mut().ok_or_else(||
            invalid("fleet scene has invalid MapObjects"))?.push(json!({
                "entity_id": target_id,
                "kind": "generic",
                "position": [0.0, 0.0],
                "rotation_degrees": 0.0,
                "layer": 18,
                "max_health": 240,
                "collider": { "kind": "circle", "offset": [0.0, 0.0], "radius": 7.0 },
            }));
        run["levels"][0]["demolition"] = json!({
            "attacking_side": attacker,
            "target_map_object_id": target_id,
            "seconds": 90.0,
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
            let seconds = if selected.reinforcements.on_command_ship_death() &&
                !matches!(selected.objective, Objective::Survival)
            {
                // Events are ordered with other authored wave triggers
                // and have no time barrier; they fire on the real source
                // commander death inside the shared combat transaction.
                0
            } else if index < late.len().div_ceil(2) { 25_u32 } else { 55 };
            wave_times_and_squads.push((seconds, side, squad));
            deferred_squads.push(squad);
            if let Some(&child) = carrier_children.get(&squad) {
                // The native Level registers a Carrier's free Striker
                // complement atomically with its parent's real arrival.
                deferred_squads.push(child);
            }
        }
    }
    if !wave_times_and_squads.is_empty() {
        // The native wave constructor requires nondecreasing source times
        // for timed plans. Death-triggered plans are separate event gates.
        // Stable faction/squad ties also preserve physical spawn order.
        wave_times_and_squads.sort_unstable_by_key(|&(seconds, side, squad)|
            (seconds, side, squad));
        run["levels"][0]["reinforcements"] = json!(
            wave_times_and_squads.iter().map(|&(seconds, side, squad)| {
                let mut group = vec![squad];
                if let Some(&child) = carrier_children.get(&squad) {
                    group.push(child);
                }
                if seconds == 0 {
                    // The first source ship (11 Bees / 12 Humans) remains
                    // deployed initially even when its faction has reserves.
                    // The Level's death event, not a timer or UI condition,
                    // is the sole activation source.
                    json!({
                        "on_ship_destroyed": if side == 0 { 11 } else { 12 },
                        "squad_item_ids": group,
                    })
                } else {
                    json!({
                        "after_seconds": seconds as f32,
                        "squad_item_ids": group,
                    })
                }
            }).collect::<Vec<_>>()
        );
    }
    run["next_squad_item_id"] = json!(3_000);
    run["run_id"] = json!(format!(
        "native-fleet-{}v{}-squad-{}v{}-mix-{}v{}-support-{}v{}-specialist-{}v{}-{}-{}-owner{}-ore{}-fog{}",
        selected.fleet_sizes[0], selected.fleet_sizes[1],
        selected.squad_sizes[0], selected.squad_sizes[1],
        selected.mix[0].label(), selected.mix[1].label(),
        support_count[0], support_count[1],
        specialist_count[0], specialist_count[1],
        if selected.ai_both { "benchmark" }
        else { selected.player_side.map_or("local", |side|
            if side == 1 { "bees" } else { "humans" }) },
        match selected.objective {
            Objective::Elimination => "elimination",
            Objective::Survival => "survival",
            Objective::Extraction => "extraction",
            Objective::Escort => "escort",
            Objective::Demolition => "demolition",
        },
        selected.objective_side.unwrap_or(0),
        u8::from(selected.mining_field), u8::from(selected.fog_of_war),
    ));
    if let Some(hivemind) = &selected.hivemind {
        let id = run["run_id"].as_str()
            .ok_or_else(|| invalid("HiveMind battle is missing its run identity"))?;
        run["run_id"] = json!(format!("{id}-hivemind-side{}", hivemind.controlled_side));
    }
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
        // Mission deadlines are 90s. A 6,000-step benchmark with twelve
        // episodes would time out before physical objective completion and
        // report arbitrary fast throughput. Leave every authored episode
        // enough fixed-step budget to execute and measure the same terminal
        // used in the actual playable mission.
        let mission = !matches!(selected.objective, Objective::Elimination);
        run["max_rounds"] = json!(if mission { 12_000 } else { 6_000 });
        run["episodes_per_world"] = json!(if mission { 2 } else { 12 });
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
            (selected.player_side.is_none_or(|player| player == side as i32 + 1) ||
             selected.hivemind.as_ref().is_some_and(|hivemind|
                 hivemind.controlled_side == side as i32 + 1))
        {
            continue;
        }
        for (position, &owner) in squad_ids[side].iter().enumerate() {
            let supporting = support_squads.contains(&owner);
            let scout = scout_squads.get(&owner).copied();
            let miner = miner_squads.contains(&owner);
            let bomber = bombing_squads.contains(&owner);
            let charger = charger_squads.contains(&owner);
            // A source-authored Aggressive lifetime still owns all
            // physical movement, weapon targeting and enemy deaths.
            // Do not store O(allies * enemies) scripted target lists
            // for a large battle: after one target dies the nearest live
            // opponent is chosen deterministically from the actual roster.
            // Passive support has no guns. Only scripted squads reach
            // this loop; a player's commander never loses input authority.
            let mission_owner = selected.objective_side == Some(side as i32 + 1);
            let orders = if supporting {
                vec![json!({"kind": "hold"})]
            } else if mission_owner &&
                matches!(selected.objective, Objective::Survival) &&
                position == 0
            {
                // The protected hull stays alive behind the defense. Its
                // normal physical turrets can still attack nearby enemies;
                // sending it on Aggressive pursuit defeats the objective.
                vec![json!({"kind": "hold"})]
            } else if mission_owner &&
                matches!(selected.objective, Objective::Extraction)
            {
                // Every mobile unit must physically evacuate. Reuse the
                // exact Level move command and extraction trigger, rather
                // than awarding the benchmark an artificial win.
                vec![json!({"kind": "move_to_point", "destination": [
                    if side == 0 { 125.0 } else { -125.0 }, 0.0,
                ]})]
            } else if mission_owner &&
                matches!(selected.objective, Objective::Escort) &&
                position == 0
            {
                // The first physical squad owns the escorted commander.
                // Its remaining allies protect it using the original
                // squad combat strategies.
                vec![json!({"kind": "move_to_point", "destination": [
                    if side == 0 { 125.0 } else { -125.0 }, 0.0,
                ]})]
            } else if mission_owner &&
                matches!(selected.objective, Objective::Escort)
            {
                // The rest of the convoy's mobile squads follow its
                // real command ship through native Guard lifetime
                // semantics. Nearest-enemy pursuit would abandon the
                // moving escort target.
                vec![json!({
                    "kind": "guard",
                    "friendly_squad_item_id": squad_ids[side][0],
                })]
            } else if !mission_owner &&
                matches!(selected.objective, Objective::Demolition) &&
                position == 0
            {
                // One physical defender holds the objective instead of
                // chasing the first decoy across the arena. Its weapons
                // remain normal automatically acquired combat turrets.
                vec![
                    json!({"kind": "move_to_point", "destination": [
                        if side == 0 { -16.0 } else { 16.0 }, 0.0,
                    ]}),
                    json!({"kind": "hold"}),
                ]
            } else if mission_owner &&
                matches!(selected.objective, Objective::Demolition) &&
                    scout.is_none() && !miner && !bomber && !charger
            {
                // Approach within the actual turret firing range, then
                // use the source point-fire controller against the live
                // authored MapObject collider. No custom weapon simulator.
                vec![
                    json!({"kind": "move_to_point", "destination": [
                        if side == 0 { -28.0 } else { 28.0 }, 0.0,
                    ]}),
                    json!({"kind": "bombard_map_object", "target_map_object_id": 91}),
                ]
            } else if miner {
                // A genuine source Carpenter Bee uses the existing
                // authored mining order; on depletion it selects the next
                // visible physical ore body rather than minting minerals.
                vec![json!({"kind": "mining_nearest"})]
            } else if let Some(max_sight) = scout {
                // The real Scouting command owns vision, discovery and
                // Beacon deployment. Moving to one arbitrary waypoint
                // would skip its distinctive physical mechanics.
                vec![json!({
                    "kind": "scouting",
                    "max_sight": max_sight,
                    "human_side": scene["human_side"],
                })]
            } else if bomber {
                vec![json!({"kind": "bombing_nearest"})]
            } else if charger {
                vec![json!({"kind": "charge_nearest"})]
            } else {
                // Between real combat orders, allied fighter squads can
                // repair at an existing Beehive. An unavailable heal never
                // grants health; the scripted director advances to the
                // ordinary source maneuver on its next fixed tick.
                let mut orders = Vec::new();
                if side == 0 && support_count[side] > 0 &&
                    !matches!(selected.mix[side], FleetMix::Gate)
                {
                    orders.push(json!({"kind": "heal_available"}));
                }
                orders.push(json!({"kind": selected.ai_tactics.source_order(position)}));
                orders
            };
            // Only commands that physically complete a one-shot goal
            // terminate their script. Escort defenders and ordinary
            // bombardment interceptors still need repeated live target
            // selection as opposing squads are killed.
            let one_shot_goal = mission_owner && match selected.objective {
                Objective::Extraction => true,
                Objective::Escort | Objective::Survival => position == 0,
                Objective::Demolition =>
                    !supporting && scout.is_none() && !miner && !bomber && !charger,
                _ => false,
            };
            let mut queue = json!({
                "squad_item_id": owner, "repeat": !supporting && !one_shot_goal,
                "orders": orders,
            });
            if deferred_squads.contains(&owner) {
                queue["activation"] = json!({"kind": "on_deployment"});
            }
            queues.push(queue);
        }
    }
    // Free Strikers remain physically real members of the same Carrier
    // family. The existing scripted BombingRun owns autonomous AI children;
    // player-controlled decks instead retain their ordinary player input
    // permissions, so no competing command authority is introduced.
    for (child_squad, player_controlled) in free_striker_squads {
        if player_controlled || selected.hivemind.as_ref()
            .is_some_and(|hivemind| hivemind.controlled_side == 2)
        { continue; }
        let mut queue = json!({
            "squad_item_id": child_squad,
            "repeat": true,
            "orders": [{"kind": "bombing_nearest"}],
        });
        if deferred_squads.contains(&child_squad) {
            queue["activation"] = json!({"kind": "on_deployment"});
        }
        queues.push(queue);
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
    // Generated playable fleets continue the same native World after an
    // interruption. Headless benchmark/trainable cohorts retain their
    // independent performance and checkpoint configuration.
    if !selected.ai_both {
        run["host_recovery_checkpoint"] = json!("native-host-checkpoint.json");
    }
    // Validate the exact RunConfig accepted by the ordinary gameplay
    // runner before publishing a discoverable replay-source-run file.
    let _: RunConfig = serde_json::from_value(run.clone())?;
    write_json_new(&run_path, &run)?;
    Ok(run_path)
}
