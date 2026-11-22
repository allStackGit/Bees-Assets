//! Scenario selection using the same window and bitmap font as play.

use minifb::{Key, KeyRepeat, MouseButton, MouseMode, Window, WindowOptions};
use std::sync::OnceLock;

use super::text;
use crate::unity_scene_snapshot::{NativeSquadColorSetting, NativeSavedSquadFormation};

/// The on-screen palette is a convenience; the CLI accepts any RGB.
/// Presets affect artwork only, never ship ownership or physics.
const SQUAD_COLORS: [[u8; 3]; 6] = [
    [0x75, 0xdb, 0xab], [0x62, 0xb9, 0xff], [0xe1, 0xa3, 0x4b],
    [0xc2, 0x9b, 0xe8], [0xeb, 0x78, 0x85], [0xed, 0xe1, 0x94],
];

fn squad_name_key(key: Key, shift: bool) -> Option<char> {
    let letters = [
        Key::A, Key::B, Key::C, Key::D, Key::E, Key::F, Key::G,
        Key::H, Key::I, Key::J, Key::K, Key::L, Key::M, Key::N,
        Key::O, Key::P, Key::Q, Key::R, Key::S, Key::T, Key::U,
        Key::V, Key::W, Key::X, Key::Y, Key::Z,
    ];
    if let Some(index) = letters.iter().position(|&entry| entry == key) {
        let ascii = if shift { b'A' } else { b'a' };
        return Some((ascii + index as u8) as char);
    }
    match key {
        Key::Key0 => Some('0'), Key::Key1 => Some('1'), Key::Key2 => Some('2'),
        Key::Key3 => Some('3'), Key::Key4 => Some('4'), Key::Key5 => Some('5'),
        Key::Key6 => Some('6'), Key::Key7 => Some('7'), Key::Key8 => Some('8'),
        Key::Key9 => Some('9'),
        Key::Space => Some(' '), Key::Minus => Some(if shift { '_' } else { '-' }),
        _ => None,
    }
}

/// Accept all authored squad RGB colors, including the thirteen original
/// Carrier deck variants, without constraining them to UI swatches.
fn hex_rgb_key(key: Key) -> Option<char> {
    match key {
        Key::Key0 => Some('0'), Key::Key1 => Some('1'),
        Key::Key2 => Some('2'), Key::Key3 => Some('3'),
        Key::Key4 => Some('4'), Key::Key5 => Some('5'),
        Key::Key6 => Some('6'), Key::Key7 => Some('7'),
        Key::Key8 => Some('8'), Key::Key9 => Some('9'),
        Key::A => Some('A'), Key::B => Some('B'), Key::C => Some('C'),
        Key::D => Some('D'), Key::E => Some('E'), Key::F => Some('F'),
        _ => None,
    }
}

fn parse_squad_rgb(hex: &str) -> Option<[u8; 3]> {
    if hex.len() != 6 || !hex.bytes().all(|b| b.is_ascii_hexdigit()) {
        return None;
    }
    let value = u32::from_str_radix(hex, 16).ok()?;
    Some([(value >> 16) as u8, (value >> 8) as u8, value as u8])
}

fn cycle_squad_color(value: NativeSquadColorSetting) -> NativeSquadColorSetting {
    match value {
        NativeSquadColorSetting::Inherit => NativeSquadColorSetting::Clear,
        NativeSquadColorSetting::Clear =>
            NativeSquadColorSetting::Custom(SQUAD_COLORS[0]),
        NativeSquadColorSetting::Custom(rgb) =>
            match SQUAD_COLORS.iter().position(|&item| item == rgb) {
                Some(index) if index + 1 == SQUAD_COLORS.len() =>
                    NativeSquadColorSetting::Inherit,
                Some(index) => NativeSquadColorSetting::Custom(SQUAD_COLORS[index + 1]),
                None => NativeSquadColorSetting::Inherit,
            },
    }
}


const WIDTH: usize = 800;
const HEIGHT: usize = 496;
const LIST_TOP: usize = 118;
const ROW_HEIGHT: usize = 42;
const PAGE_SIZE: usize = 7;
const TEXT: [u8; 3] = [232, 240, 250];
const MUTED: [u8; 3] = [155, 178, 206];

/// One list-navigation contract for mission selection, shop, squad management,
/// and career history. Keep the selected row visible even for very large
/// authored campaigns, without allocating a second UI model.
fn navigate_rows(window: &Window, selected: &mut usize, first: &mut usize, count: usize) {
    if count == 0 {
        *selected = 0;
        *first = 0;
        return;
    }
    *selected = (*selected).min(count - 1);
    if window.is_key_pressed(Key::Home, KeyRepeat::No) {
        *selected = 0;
    } else if window.is_key_pressed(Key::End, KeyRepeat::No) {
        *selected = count - 1;
    } else if window.is_key_pressed(Key::PageUp, KeyRepeat::Yes) {
        *selected = selected.saturating_sub(PAGE_SIZE);
    } else if window.is_key_pressed(Key::PageDown, KeyRepeat::Yes) {
        *selected = selected.saturating_add(PAGE_SIZE).min(count - 1);
    } else if window.is_key_pressed(Key::Up, KeyRepeat::Yes) {
        *selected = selected.saturating_sub(1);
    } else if window.is_key_pressed(Key::Down, KeyRepeat::Yes) {
        *selected = selected.saturating_add(1).min(count - 1);
    }
    if let Some((_, vertical)) = window.get_scroll_wheel() {
        // Wheel direction follows native window conventions: positive is up.
        // Fractional high-resolution wheel movement still advances one row.
        let rows = vertical.abs().ceil() as usize;
        if vertical > 0.0 {
            *selected = selected.saturating_sub(rows);
        } else if vertical < 0.0 {
            *selected = selected.saturating_add(rows).min(count - 1);
        }
    }
    if *selected < *first {
        *first = *selected;
    } else if *selected >= first.saturating_add(PAGE_SIZE) {
        *first = *selected + 1 - PAGE_SIZE;
    }
}

/// Renaming consumes its own key stream so ordinary typing never activates
/// mission shortcuts; it uses the same row paging bounds as other menus.
fn renamed_row_index(index: usize, count: usize, key: Key) -> usize {
    if count == 0 { return 0; }
    match key {
        Key::Up => index.saturating_sub(1),
        Key::Down => index.saturating_add(1).min(count - 1),
        Key::PageUp => index.saturating_sub(PAGE_SIZE),
        Key::PageDown => index.saturating_add(PAGE_SIZE).min(count - 1),
        Key::Home => 0,
        Key::End => count - 1,
        _ => index,
    }
}

pub struct BattleMenuRow<'a> {
    pub title: &'a str,
    pub status: &'a str,
    pub unlocked: bool,
}

/// The shop has no independent save file or game state. The caller
/// derives rows from the same catalog and verified campaign ledger.
pub struct NativeShopMenuRow {
    pub title: String,
    pub unlocked: bool,
    pub side: i32,
    pub ship_type: u8,
    pub saved_squad_id: i64,
}

/// Read-only projection from the committed source campaign ledger. No
/// presentation-side match archive or independent score ownership.
pub struct NativeCampaignMenuStats {
    pub score: i64,
    pub score_complete: bool,
    pub ships_lost: u64,
    pub ships_returned: u64,
    pub credited_kills: u64,
    pub total_mined_tsv: Option<u64>,
    pub match_history_complete: bool,
}

pub struct NativeTransferMenuRow {
    pub title: String,
    pub fleet_ship_id: i64,
    pub to_saved_squad_id: i64,
    /// Original hulls need source-faction and current SavedSquad
    /// provenance; purchased hulls use the existing transfer ledger.
    pub authored_from: Option<(i32, i64)>,
}

pub struct NativeCreateSquadMenuRow {
    pub title: String,
    pub side: i32,
    pub template_saved_squad_id: i64,
}

/// Only generated SavedSquads can be retired. Their FleetShips return
/// to reserve through the ordinary persistent career transfer ledger.
pub struct NativeDeleteSquadMenuRow {
    pub title: String,
    pub side: i32,
    pub saved_squad_id: i64,
}

/// Same editor and validation key surface for original SavedSquad and
/// FleetShip identities; no separate menu or simulated squad state.
#[derive(Clone, Copy)]
pub enum NativeNameTarget {
    SavedSquad(i64),
    FleetShip(i64),
}

pub struct NativeNameMenuRow {
    pub title: String,
    pub side: i32,
    pub target: NativeNameTarget,
    pub name: String,
}

pub struct NativeTacticsMenuRow {
    pub title: String,
    pub side: i32,
    pub saved_squad_id: i64,
    pub shooting_strategy_id: u8,
    pub should_chase: bool,
    pub cease_fire: bool,
    pub match_speed: bool,
    pub color_setting: NativeSquadColorSetting,
    pub formation: NativeSavedSquadFormation,
}

pub enum NativeBattleMenuAction {
    Play(usize),
    CreateSquad { side: i32, template_saved_squad_id: i64 },
    DuplicateSquad { side: i32, template_saved_squad_id: i64 },
    DeleteSquad { side: i32, saved_squad_id: i64 },
    NameSquad { side: i32, saved_squad_id: i64, name: String },
    NameFleetShip { side: i32, fleet_ship_id: i64, name: String },
    Tactics { side: i32, saved_squad_id: i64,
        shooting_strategy_id: u8, should_chase: bool, cease_fire: bool,
        match_speed: bool, color_setting: NativeSquadColorSetting,
        formation: NativeSavedSquadFormation },
    Buy { side: i32, ship_type: u8, saved_squad_id: i64 },
    Transfer { fleet_ship_id: i64, to_saved_squad_id: i64 },
    Reassign { side: i32, fleet_ship_id: i64,
        from_saved_squad_id: i64, to_saved_squad_id: i64 },
}

/// Returns a playable battle or purchase request, never applies
/// gameplay/economic changes inside the presentation layer.
pub fn show_campaign_intermission(
    intermissions: &[super::campaign_dialogue::NativeCampaignIntermission],
) -> Result<bool, minifb::Error> {
    // A travel intermission owns presentation only: no physical World,
    // mission receipt, or fleet changes are created until battle launch.
    let lines = intermissions.iter().copied().flat_map(|intermission|
        intermission.lines().map(move |(speaker, message)|
            (intermission.title(), speaker, message))
    ).collect::<Vec<_>>();
    if lines.is_empty() { return Ok(true); }
    let mut window = Window::new("Bees | Campaign intermission",
        WIDTH, HEIGHT, WindowOptions::default())?;
    window.set_target_fps(30);
    let mut pixels = vec![0; WIDTH * HEIGHT];
    let mut page = 0_usize;
    let mut mouse_down = false;
    while window.is_open() {
        if window.is_key_pressed(Key::Escape, KeyRepeat::No) {
            return Ok(false);
        }
        if window.is_key_pressed(Key::S, KeyRepeat::No) {
            // Skip the current travel dialogue, but still show the next
            // original mission briefing when the sequence has one.
            let title = lines[page].0;
            while page < lines.len() && lines[page].0 == title {
                page += 1;
            }
            if page == lines.len() { return Ok(true); }
        }
        if window.is_key_pressed(Key::Left, KeyRepeat::No) {
            page = page.saturating_sub(1);
        }
        let click = window.get_mouse_down(MouseButton::Left);
        let next = window.is_key_pressed(Key::Space, KeyRepeat::No) ||
            window.is_key_pressed(Key::Enter, KeyRepeat::No) ||
            window.is_key_pressed(Key::Right, KeyRepeat::No) ||
            (click && !mouse_down && window.get_mouse_pos(MouseMode::Discard)
                .is_some_and(|(_, y)| y >= 400.0));
        mouse_down = click;
        if next {
            page += 1;
            if page == lines.len() { return Ok(true); }
        }
        let (title, speaker, message) = lines[page];
        pixels.fill(0x080e19);
        text::draw_centered(&mut pixels, WIDTH, 35,
            title, 2, TEXT);
        text::draw_centered(&mut pixels, WIDTH, 90,
            &format!("Transmission {} / {}", page + 1, lines.len()), 1, MUTED);
        for y in 130..380 {
            pixels[y * WIDTH + 40..y * WIDTH + WIDTH - 40].fill(0x111e2e);
        }
        text::draw_at(&mut pixels, WIDTH, 64, 150, speaker, 2, [117, 219, 171]);
        let mut rest = message;
        let mut y = 205;
        while !rest.is_empty() {
            let prefix = text::fit(rest, WIDTH - 128);
            if prefix.is_empty() { break; }
            let length = if prefix.len() == rest.len() { prefix.len() }
                else { prefix.rfind(' ').filter(|&i| i != 0).unwrap_or(prefix.len()) };
            text::draw_at(&mut pixels, WIDTH, 64, y, &rest[..length], 1, TEXT);
            y += text::HEIGHT + 8;
            rest = rest[length..].trim_start();
        }
        text::draw_centered(&mut pixels, WIDTH, 415,
            if page + 1 == lines.len() { "Continue to fleet preparation" }
                else { "Continue dialogue" }, 2, TEXT);
        text::draw_centered(&mut pixels, WIDTH, 458,
            "Space / Enter / Click next   Left previous   S skip   Esc back", 1, MUTED);
        window.update_with_buffer(&pixels, WIDTH, HEIGHT)?;
    }
    Ok(false)
}

pub fn select_battle(
    rows: &[BattleMenuRow<'_>], selected: &mut usize, launch_failed: bool,
    fleet_rows: &[String], squad_rows: &[String],
    shop_rows: &[NativeShopMenuRow], banked_tsv: u64,
    transfer_rows: &[NativeTransferMenuRow],
    create_rows: &[NativeCreateSquadMenuRow],
    tactics_rows: &[NativeTacticsMenuRow],
    delete_rows: &[NativeDeleteSquadMenuRow],
    name_rows: &[NativeNameMenuRow],
    codex_types: Option<&[u8]>,
    career_stats: NativeCampaignMenuStats,
) -> Result<Option<NativeBattleMenuAction>, minifb::Error> {
    select_battle_inner(rows, selected, launch_failed, fleet_rows, squad_rows,
        shop_rows, banked_tsv, transfer_rows, create_rows, tactics_rows,
        delete_rows, name_rows, codex_types, Some(career_stats), false)
}

/// The packaged-game launcher uses the same list renderer without exposing
/// campaign-only FleetShip, purchase or SavedSquad tools until a career opens.
pub fn select_library(
    rows: &[BattleMenuRow<'_>], selected: &mut usize, launch_failed: bool,
) -> Result<Option<usize>, minifb::Error> {
    match select_battle_inner(rows, selected, launch_failed,
        &[], &[], &[], 0, &[], &[], &[], &[], &[], None, None, true)? {
        Some(NativeBattleMenuAction::Play(index)) => Ok(Some(index)),
        _ => Ok(None),
    }
}

fn select_battle_inner(
    rows: &[BattleMenuRow<'_>], selected: &mut usize, launch_failed: bool,
    fleet_rows: &[String], squad_rows: &[String],
    shop_rows: &[NativeShopMenuRow], banked_tsv: u64,
    transfer_rows: &[NativeTransferMenuRow],
    create_rows: &[NativeCreateSquadMenuRow],
    tactics_rows: &[NativeTacticsMenuRow],
    delete_rows: &[NativeDeleteSquadMenuRow],
    name_rows: &[NativeNameMenuRow],
    codex_types: Option<&[u8]>,
    career_stats: Option<NativeCampaignMenuStats>,
    library_mode: bool,
) -> Result<Option<NativeBattleMenuAction>, minifb::Error> {
    if rows.is_empty() { return Ok(None); }
    *selected = (*selected).min(rows.len() - 1);
    let mut window = Window::new(if library_mode { "Bees | Game Library" } else { "Bees | Battles" },
        WIDTH, HEIGHT, WindowOptions::default())?;
    window.set_target_fps(30);
    let mut pixels = vec![0; WIDTH * HEIGHT];
    let titles = rows.iter().map(|row| {
        let available = 560_usize.saturating_sub(text::width(row.status, 1));
        format!("{}{}", text::fit(row.title, available), row.status)
    }).collect::<Vec<_>>();
    let mut first = (*selected / PAGE_SIZE) * PAGE_SIZE;
    let mut mouse_down = false;
    // None is mission selection, false is ship careers, true is
    // saved-squad careers. Neither screen owns or mutates game data.
    let mut career_view: Option<bool> = None;
    let mut show_codex = false;
    let mut codex_selected = 0_usize;
    let mut codex_first = 0_usize;
    let mut show_shop = false;
    let mut show_transfer = false;
    let mut show_create = false;
    let mut show_delete = false;
    let mut show_tactics = false;
    let mut tactic_edit_index: Option<usize> = None;
    let mut tactic_strategy = 1_u8;
    let mut tactic_chase = false;
    let mut tactic_cease_fire = false;
    let mut tactic_match_speed = false;
    let mut tactic_color = NativeSquadColorSetting::Inherit;
    // Editing happens entirely in the existing tactics view; only a
    // committed Tactics action reaches the saved-squad ledger.
    let mut hex_color_input: Option<String> = None;
    let mut tactic_formation = NativeSavedSquadFormation::authored();
    let mut show_names = false;
    let mut name_selected = 0_usize;
    let mut name_first = 0_usize;
    let mut name_input = String::new();
    let mut shop_selected = 0_usize;
    let mut shop_first = 0_usize;
    let mut fleet_selected = 0_usize;
    let mut fleet_first = 0_usize;
    while window.is_open() {
        if let Some(hex) = hex_color_input.as_mut() {
            // Full RGB input is modal: digits/letters must never activate
            // battle, purchase, mission or tactical shortcuts underneath.
            let mut apply = false;
            let mut cancel = false;
            for key in window.get_keys_pressed(KeyRepeat::Yes) {
                match key {
                    Key::Escape | Key::Tab => { cancel = true; break; }
                    Key::Enter => { apply = hex.len() == 6; break; }
                    Key::Backspace => { hex.pop(); }
                    key => {
                        if hex.len() < 6 {
                            if let Some(ch) = hex_rgb_key(key) { hex.push(ch); }
                        }
                    }
                }
            }
            if apply {
                if let Some(rgb) = parse_squad_rgb(hex) {
                    tactic_color = NativeSquadColorSetting::Custom(rgb);
                    hex_color_input = None;
                }
            } else if cancel {
                hex_color_input = None;
            }
            pixels.fill(0x080e19);
            text::draw_centered(&mut pixels, WIDTH, 60, "Custom squad color", 2, TEXT);
            text::draw_centered(&mut pixels, WIDTH, 125,
                "Enter six hexadecimal RGB digits (RRGGBB)", 1, MUTED);
            let current = hex_color_input.as_deref().unwrap_or("");
            let label = format!("#{}{}", current, "_".repeat(6 - current.len()));
            text::draw_centered(&mut pixels, WIDTH, 196, &label, 2, TEXT);
            if let Some(rgb) = parse_squad_rgb(current) {
                for y in 253..306 {
                    pixels[y * WIDTH + 310..y * WIDTH + 490]
                        .fill((u32::from(rgb[0]) << 16) |
                              (u32::from(rgb[1]) << 8) | u32::from(rgb[2]));
                }
            }
            text::draw_centered(&mut pixels, WIDTH, 365,
                "Enter accept color   Backspace delete   Esc cancel", 1, MUTED);
            mouse_down = window.get_mouse_down(MouseButton::Left);
            window.update_with_buffer(&pixels, WIDTH, HEIGHT)?;
            continue;
        }
        // Escape returns to the parent campaign screen. A player browsing
        // ships, changing tactics or renaming a squad should not have to
        // abandon the entire battle launcher to reach mission selection.
        if window.is_key_pressed(Key::Escape, KeyRepeat::No) {
            if show_codex {
                show_codex = false;
            } else if show_names {
                show_names = false;
            } else if show_shop || show_transfer || show_create ||
                show_delete || show_tactics
            {
                show_shop = false;
                show_transfer = false;
                show_create = false;
                show_delete = false;
                show_tactics = false;
                tactic_edit_index = None;
            } else if career_view.is_some() {
                career_view = None;
            } else {
                break;
            }
            // A pointer release that closes a view must not become a
            // purchase, transfer or battle activation on the parent.
            mouse_down = window.get_mouse_down(MouseButton::Left);
            // Pump a new input frame before checking for another Esc edge.
            // Otherwise one keypress would close the submenu and immediately
            // exit the parent battle launcher on the same stale input state.
            window.update();
            continue;
        }
        if !library_mode && !show_tactics && !show_names &&
            codex_types.is_some() &&
            window.is_key_pressed(Key::F2, KeyRepeat::No)
        {
            show_codex = !show_codex;
            show_shop = false;
            show_transfer = false;
            show_create = false;
            show_delete = false;
            career_view = None;
            mouse_down = window.get_mouse_down(MouseButton::Left);
            window.update();
            continue;
        }
        if show_codex {
            let types = codex_types.unwrap_or(&[]);
            navigate_rows(&window, &mut codex_selected, &mut codex_first, types.len());
            pixels.fill(0x080e19);
            text::draw_centered(&mut pixels, WIDTH, 24,
                "United Fleet Codex", 2, TEXT);
            text::draw_centered(&mut pixels, WIDTH, 78,
                "Ship classes earned through original campaign progression", 1, MUTED);
            static SHIPS: OnceLock<bees_sim::ship_assets::ShipAssetCatalog> = OnceLock::new();
            let catalog = SHIPS.get_or_init(|| bees_sim::ship_assets::ShipAssetCatalog::embedded()
                .expect("native source ship catalog loaded by gameplay"));
            if types.is_empty() {
                text::draw_centered(&mut pixels, WIDTH, 215,
                    "No Codex ship classes unlocked", 1, MUTED);
            }
            for (row, &kind) in types.iter().skip(codex_first).take(PAGE_SIZE).enumerate() {
                let top = LIST_TOP + row * ROW_HEIGHT;
                for y in top..top + ROW_HEIGHT - 4 {
                    pixels[y * WIDTH + 100..y * WIDTH + 700].fill(
                        if codex_selected == codex_first + row {
                            0x294a66
                        } else { 0x142235 }
                    );
                }
                let name = catalog.by_type(kind).map(|ship| ship.name.as_str())
                    .unwrap_or("Unknown ship");
                let label = format!("Type {kind:02}  {name}");
                text::draw_centered(&mut pixels, WIDTH, top + 8, &label, 1, TEXT);
            }
            if types.len() > PAGE_SIZE {
                text::draw_centered(&mut pixels, WIDTH, 417,
                    &format!("{}-{} of {} classes",
                        codex_first + 1,
                        (codex_first + PAGE_SIZE).min(types.len()), types.len()), 1, MUTED);
            }
            text::draw_centered(&mut pixels, WIDTH, 441,
                "Up/Down / Wheel browse   F2 / Esc back", 1, MUTED);
            mouse_down = window.get_mouse_down(MouseButton::Left);
            window.update_with_buffer(&pixels, WIDTH, HEIGHT)?;
            continue;
        }
        if !library_mode && !show_tactics && !show_names &&
            window.is_key_pressed(Key::R, KeyRepeat::No)
        {
            show_names = true;
            show_shop = false;
            show_transfer = false;
            show_create = false;
            show_delete = false;
            show_tactics = false;
            career_view = None;
            name_selected = 0;
            name_first = 0;
            name_input = name_rows.first().map_or(String::new(), |row| row.name.clone());
            // The opening R must not itself insert a literal 'r'.
            pixels.fill(0x080e19);
            text::draw_centered(&mut pixels, WIDTH, 40,
                "Name saved squads and FleetShips", 2, TEXT);
            window.update_with_buffer(&pixels, WIDTH, HEIGHT)?;
            continue;
        }
        if show_names {
            for key in window.get_keys_pressed(KeyRepeat::Yes) {
                match key {
                    Key::Tab => { show_names = false; break; },
                    Key::Up | Key::Down | Key::PageUp | Key::PageDown |
                    Key::Home | Key::End => {
                        let next = renamed_row_index(name_selected, name_rows.len(), key);
                        if next != name_selected {
                            name_selected = next;
                            name_input = name_rows.get(next)
                                .map_or(String::new(), |row| row.name.clone());
                        }
                    }
                    Key::Enter => {
                        if let Some(row) = name_rows.get(name_selected) {
                            let name = name_input.trim().to_owned();
                            if !name.is_empty() && name.len() <= 32 &&
                                name != row.name
                            {
                                return Ok(Some(match row.target {
                                    NativeNameTarget::SavedSquad(saved_squad_id) =>
                                        NativeBattleMenuAction::NameSquad {
                                            side: row.side, saved_squad_id, name,
                                        },
                                    NativeNameTarget::FleetShip(fleet_ship_id) =>
                                        NativeBattleMenuAction::NameFleetShip {
                                            side: row.side, fleet_ship_id, name,
                                        },
                                }));
                            }
                        }
                    }
                    Key::Backspace => { name_input.pop(); },
                    key => {
                        let shift = window.is_key_down(Key::LeftShift) ||
                            window.is_key_down(Key::RightShift);
                        if let Some(ch) = squad_name_key(key, shift) {
                            if name_input.len() < 32 { name_input.push(ch); }
                        }
                    }
                }
            }
            if show_names {
                if let Some((_, vertical)) = window.get_scroll_wheel() {
                    let rows = vertical.abs().ceil() as usize;
                    let next = if vertical > 0.0 {
                        name_selected.saturating_sub(rows)
                    } else {
                        name_selected.saturating_add(rows)
                            .min(name_rows.len().saturating_sub(1))
                    };
                    if next != name_selected {
                        name_selected = next;
                        name_input = name_rows.get(next)
                            .map_or(String::new(), |row| row.name.clone());
                    }
                }
                if name_selected < name_first { name_first = name_selected; }
                if name_selected >= name_first + PAGE_SIZE {
                    name_first = name_selected + 1 - PAGE_SIZE;
                }
                pixels.fill(0x080e19);
                text::draw_centered(&mut pixels, WIDTH, 24,
                    "Name saved squads and FleetShips", 2, TEXT);
                text::draw_centered(&mut pixels, WIDTH, 73,
                    "Type a name (32 chars max). Enter save, Tab return to missions",
                    1, MUTED);
                text::draw_centered(&mut pixels, WIDTH, 96,
                    &format!("Current name: {}", if name_input.is_empty() {
                        "(unnamed)"
                    } else { name_input.as_str() }), 1, TEXT);
                if name_rows.is_empty() {
                    text::draw_centered(&mut pixels, WIDTH, 220,
                        "No player SavedSquads or FleetShips available to name", 1, MUTED);
                }
                for index in name_first..(name_first + PAGE_SIZE).min(name_rows.len()) {
                    let y = LIST_TOP + (index - name_first) * ROW_HEIGHT;
                    let background = if index == name_selected { 0x294a66 }
                        else { 0x142235 };
                    for line in y..y + ROW_HEIGHT - 4 {
                        pixels[line * WIDTH + 100..line * WIDTH + 700].fill(background);
                    }
                    let row = &name_rows[index];
                    let label = format!("{}  [{}]", row.title,
                        if row.name.is_empty() { "Unnamed" } else { &row.name });
                    text::draw_centered(&mut pixels, WIDTH, y + 8,
                        &text::fit(&label, 560), 1, TEXT);
                }
                text::draw_centered(&mut pixels, WIDTH, 441,
                    "Arrows/scroll choose | Letters/numbers/space/-/_ | Backspace erase | Tab back",
                    1, MUTED);
                window.update_with_buffer(&pixels, WIDTH, HEIGHT)?;
                continue;
            }
        }
        if !library_mode && !show_tactics &&
            window.is_key_pressed(Key::F, KeyRepeat::No) {
            career_view = if career_view == Some(false) && !show_shop && !show_transfer && !show_create {
                None
            } else { Some(false) };
            show_shop = false;
            show_transfer = false;
            show_create = false;
            show_tactics = false;
            show_delete = false;
        }
        if !library_mode && window.is_key_pressed(Key::S, KeyRepeat::No) {
            career_view = if career_view == Some(true) && !show_shop && !show_transfer && !show_create {
                None
            } else { Some(true) };
            show_shop = false;
            show_transfer = false;
            show_create = false;
            show_tactics = false;
            show_delete = false;
        }
        if !library_mode && window.is_key_pressed(Key::B, KeyRepeat::No) {
            show_shop = !show_shop;
            show_transfer = false;
            show_create = false;
            show_tactics = false;
            show_delete = false;
            career_view = None;
        }
        if !library_mode && window.is_key_pressed(Key::T, KeyRepeat::No) {
            show_transfer = !show_transfer;
            show_shop = false;
            show_create = false;
            show_tactics = false;
            show_delete = false;
            career_view = None;
        }
        if !library_mode && window.is_key_pressed(Key::N, KeyRepeat::No) {
            show_create = !show_create;
            show_shop = false;
            show_transfer = false;
            show_tactics = false;
            show_delete = false;
            career_view = None;
        }
        if !library_mode && window.is_key_pressed(Key::P, KeyRepeat::No) {
            show_tactics = !show_tactics;
            show_shop = false;
            show_transfer = false;
            show_create = false;
            show_delete = false;
            tactic_edit_index = None;
            career_view = None;
        }
        if !library_mode && window.is_key_pressed(Key::D, KeyRepeat::No) {
            show_delete = !show_delete;
            show_shop = false;
            show_transfer = false;
            show_create = false;
            show_tactics = false;
            career_view = None;
        }
        if show_shop || show_transfer || show_create || show_tactics || show_delete {
            let count = if show_transfer { transfer_rows.len() }
                else if show_create { create_rows.len() }
                else if show_delete { delete_rows.len() }
                else if show_tactics { tactics_rows.len() }
                else { shop_rows.len() };
            let choose = |index: usize, edit_index: Option<usize>,
                          strategy: u8, chase: bool, cease_fire: bool,
                          match_speed: bool, color_setting: NativeSquadColorSetting,
                          formation: NativeSavedSquadFormation| {
                if show_transfer {
                    transfer_rows.get(index).map(|row| match row.authored_from {
                        Some((side, from_saved_squad_id)) => NativeBattleMenuAction::Reassign {
                            side,
                            fleet_ship_id: row.fleet_ship_id,
                            from_saved_squad_id,
                            to_saved_squad_id: row.to_saved_squad_id,
                        },
                        None => NativeBattleMenuAction::Transfer {
                            fleet_ship_id: row.fleet_ship_id,
                            to_saved_squad_id: row.to_saved_squad_id,
                        },
                    })
                } else if show_create {
                    create_rows.get(index).map(|row| NativeBattleMenuAction::CreateSquad {
                        side: row.side,
                        template_saved_squad_id: row.template_saved_squad_id,
                    })
                } else if show_delete {
                    delete_rows.get(index).map(|row| NativeBattleMenuAction::DeleteSquad {
                        side: row.side, saved_squad_id: row.saved_squad_id,
                    })
                } else if show_tactics {
                    tactics_rows.get(index).map(|row| NativeBattleMenuAction::Tactics {
                        side: row.side,
                        saved_squad_id: row.saved_squad_id,
                        shooting_strategy_id: if edit_index == Some(index) {
                            strategy
                        } else { row.shooting_strategy_id },
                        should_chase: if edit_index == Some(index) {
                            chase
                        } else { row.should_chase },
                        cease_fire: if edit_index == Some(index) {
                            cease_fire
                        } else { row.cease_fire },
                        match_speed: if edit_index == Some(index) {
                            match_speed
                        } else { row.match_speed },
                        color_setting: if edit_index == Some(index) {
                            color_setting
                        } else { row.color_setting },
                        formation: if edit_index == Some(index) {
                            formation
                        } else { row.formation },
                    })
                } else {
                    shop_rows.get(index).filter(|row| row.unlocked)
                        .map(|row| NativeBattleMenuAction::Buy {
                            side: row.side,
                            ship_type: row.ship_type,
                            saved_squad_id: row.saved_squad_id,
                        })
                }
            };
            navigate_rows(&window, &mut shop_selected, &mut shop_first, count);
            if show_tactics {
                if let Some(row) = tactics_rows.get(shop_selected) {
                    if tactic_edit_index != Some(shop_selected) {
                        tactic_edit_index = Some(shop_selected);
                        tactic_strategy = row.shooting_strategy_id;
                        tactic_chase = row.should_chase;
                        tactic_cease_fire = row.cease_fire;
                        tactic_match_speed = row.match_speed;
                        tactic_color = row.color_setting;
                        tactic_formation = row.formation;
                    }
                    if window.is_key_pressed(Key::Left, KeyRepeat::Yes) {
                        tactic_strategy = if tactic_strategy == 1 { 40 }
                            else { tactic_strategy - 1 };
                    }
                    if window.is_key_pressed(Key::Right, KeyRepeat::Yes) {
                        tactic_strategy = if tactic_strategy == 40 { 1 }
                            else { tactic_strategy + 1 };
                    }
                    if window.is_key_pressed(Key::C, KeyRepeat::No) {
                        tactic_chase = !tactic_chase;
                    }
                    if window.is_key_pressed(Key::V, KeyRepeat::No) {
                        tactic_cease_fire = !tactic_cease_fire;
                    }
                    if window.is_key_pressed(Key::M, KeyRepeat::No) {
                        tactic_match_speed = !tactic_match_speed;
                    }
                    if window.is_key_pressed(Key::R, KeyRepeat::No) {
                        tactic_color = cycle_squad_color(tactic_color);
                    }
                    if window.is_key_pressed(Key::H, KeyRepeat::No) {
                        hex_color_input = Some(match tactic_color {
                            NativeSquadColorSetting::Custom(rgb) =>
                                format!("{:02X}{:02X}{:02X}", rgb[0], rgb[1], rgb[2]),
                            _ => String::new(),
                        });
                    }
                    if window.is_key_pressed(Key::F, KeyRepeat::No) {
                        tactic_formation = tactic_formation.cycle_kind();
                    }
                    if window.is_key_pressed(Key::W, KeyRepeat::No) {
                        tactic_formation.max_width = if tactic_formation.max_width >= 16 {
                            1
                        } else { tactic_formation.max_width + 1 };
                    }
                }
            }
            if show_create && window.is_key_pressed(Key::D, KeyRepeat::No) {
                if let Some(row) = create_rows.get(shop_selected) {
                    return Ok(Some(NativeBattleMenuAction::DuplicateSquad {
                        side: row.side,
                        template_saved_squad_id: row.template_saved_squad_id,
                    }));
                }
            }
            if window.is_key_pressed(Key::Enter, KeyRepeat::No) {
                if let Some(action) = choose(shop_selected, tactic_edit_index,
                        tactic_strategy, tactic_chase, tactic_cease_fire,
                        tactic_match_speed, tactic_color, tactic_formation) {
                    return Ok(Some(action));
                }
            }
            let pressed = window.get_mouse_down(MouseButton::Left);
            if pressed && !mouse_down {
                if let Some((x, y)) = window.get_mouse_pos(MouseMode::Discard) {
                    let y = y as usize;
                    if (100.0..700.0).contains(&x) &&
                        (LIST_TOP..LIST_TOP + PAGE_SIZE * ROW_HEIGHT).contains(&y)
                    {
                        let index = shop_first + (y - LIST_TOP) / ROW_HEIGHT;
                        if index < count {
                            shop_selected = index;
                            if (y - LIST_TOP) % ROW_HEIGHT < ROW_HEIGHT - 4 {
                                if show_tactics || show_delete || show_shop {
                                    // Spending mined TSV also requires Enter;
                                    // selecting a row by mouse cannot
                                    // accidentally commit a fleet purchase.
                                    if show_tactics { tactic_edit_index = None; }
                                } else if let Some(action) = choose(index, tactic_edit_index,
                                    tactic_strategy, tactic_chase, tactic_cease_fire,
                                    tactic_match_speed, tactic_color, tactic_formation)
                                {
                                    return Ok(Some(action));
                                }
                            }
                        }
                    }
                }
            }
            mouse_down = pressed;
            pixels.fill(0x080e19);
            text::draw_centered(&mut pixels, WIDTH, 24,
                if show_transfer { "Assign fleet" }
                else if show_create { "Create saved squad" }
                else if show_delete { "Delete saved squad" }
                else if show_tactics { "Saved squad tactics" }
                else { "Build fleet" }, 2, TEXT);
            text::draw_centered(&mut pixels, WIDTH, 78,
                if show_transfer { "Move owned ships between saved squads (no TSV cost)" }
                else if show_create { "Enter: empty template   D: duplicate squad using matching reserve FleetShips" }
                else if show_delete { "Created squads only; assigned ships return to fleet reserve" }
                else if show_tactics { "Left/Right strategy, C chase, V fire, M speed, R palette, H custom RGB" }
                else { "Purchase for deployment or buy into reserve; Enter spends mined TSV" }, 1, MUTED);
            if show_shop {
                text::draw_centered(&mut pixels, WIDTH, 94,
                    &format!("Available mined TSV: {banked_tsv}"), 1, MUTED);
            }
            if count == 0 {
                text::draw_centered(&mut pixels, WIDTH, 218,
                    if show_transfer { "No transferable owned ships" }
                    else if show_create { "No native player squad templates" }
                    else if show_delete { "No deletable created SavedSquads" }
                    else if show_tactics { "No editable authored player squads" }
                    else { "No purchasable source FleetShip classes" }, 1, MUTED);
            }
            for index in shop_first..(shop_first + PAGE_SIZE).min(count) {
                let top = LIST_TOP + (index - shop_first) * ROW_HEIGHT;
                let background = if shop_selected == index { 0x294a66 }
                    else { 0x142235 };
                for y in top..top + ROW_HEIGHT - 4 {
                    pixels[y * WIDTH + 100..y * WIDTH + 700].fill(background);
                }
                let (label, enabled) = if show_transfer {
                    (transfer_rows[index].title.as_str(), true)
                } else if show_create {
                    (create_rows[index].title.as_str(), true)
                } else if show_delete {
                    (delete_rows[index].title.as_str(), true)
                } else if show_tactics {
                    (tactics_rows[index].title.as_str(), true)
                } else { (shop_rows[index].title.as_str(), shop_rows[index].unlocked) };
                let tactical_label = if show_tactics {
                    let row = &tactics_rows[index];
                    let (strategy, chase, cease, match_speed, color_setting, formation) =
                        if tactic_edit_index == Some(index) {
                            (tactic_strategy, tactic_chase, tactic_cease_fire,
                                tactic_match_speed, tactic_color, tactic_formation)
                        } else {
                            (row.shooting_strategy_id, row.should_chase,
                                row.cease_fire, row.match_speed, row.color_setting, row.formation)
                        };
                    let name = bees_hivemind::protocol::SHOOTING_STRATEGIES[
                        usize::from(strategy - 1)
                    ];
                    let color = match color_setting {
                        NativeSquadColorSetting::Inherit => "source".to_owned(),
                        NativeSquadColorSetting::Clear => "none".to_owned(),
                        NativeSquadColorSetting::Custom(rgb) =>
                            format!("#{:02X}{:02X}{:02X}", rgb[0], rgb[1], rgb[2]),
                    };
                    format!("Form {}:{} | {} | {} | Chase {} | Fire {} | Speed {} | RGB {}",
                        formation.label(), formation.max_width,
                        row.title, name, if chase { "on" } else { "off" },
                        if cease { "hold" } else { "auto" },
                        if match_speed { "matched" } else { "normal" }, color)
                } else { String::new() };
                let title = text::fit(if show_tactics { &tactical_label } else { label }, 560);
                text::draw_centered(&mut pixels, WIDTH, top + 8, &title, 1,
                    if enabled { TEXT } else { MUTED });
            }
            if count > PAGE_SIZE {
                let page = format!("{}-{} of {} choices", shop_first + 1,
                    (shop_first + PAGE_SIZE).min(count), count);
                text::draw_centered(&mut pixels, WIDTH, 417, &page, 1, MUTED);
            }
            text::draw_centered(&mut pixels, WIDTH, 441,
                if show_tactics {
                    "Arrows strategy | C chase V fire M speed | R swatch H RGB | F form W width | Enter save"
                } else if show_delete {
                    "Up/Down select   Enter delete   D close   Esc back"
                } else if show_shop {
                    "Up/Down select   Enter purchase   B close   Esc back"
                } else {
                    "Enter select   B build   T transfer   N new   D delete   P tactics"
                }, 1, MUTED);
            window.update_with_buffer(&pixels, WIDTH, HEIGHT)?;
            continue;
        }
        if let Some(show_squads) = career_view {
            let career_rows = if show_squads { squad_rows } else { fleet_rows };
            // Each screen has a different set of persisted identities.
            // Clamp when switching between long ship lists and short
            // squad lists, rather than rendering an apparently empty page.
            navigate_rows(&window, &mut fleet_selected, &mut fleet_first, career_rows.len());
            pixels.fill(0x080e19);
            text::draw_centered(&mut pixels, WIDTH, 24,
                if show_squads { "Saved squads" } else { "Fleet career" }, 2, TEXT);
            text::draw_centered(&mut pixels, WIDTH, 78,
                if show_squads { "Completed campaign battles (saved squads only)" }
                    else { "Completed campaign battles (saved FleetShips only)" }, 1, MUTED);
            if career_rows.is_empty() {
                text::draw_centered(&mut pixels, WIDTH, 218,
                    if show_squads { "No recorded saved squad careers yet" }
                        else { "No recorded FleetShip careers yet" }, 1, MUTED);
            }
            for (row, item) in career_rows.iter().skip(fleet_first).take(PAGE_SIZE).enumerate() {
                let top = LIST_TOP + row * ROW_HEIGHT;
                let color = if fleet_first + row == fleet_selected { 0x294a66 }
                    else { 0x142235 };
                for y in top..top + ROW_HEIGHT - 4 {
                    pixels[y * WIDTH + 100..y * WIDTH + 700].fill(color);
                }
                let label = text::fit(item, 560);
                text::draw_centered(&mut pixels, WIDTH, top + 8, &label, 1, TEXT);
            }
            if career_rows.len() > PAGE_SIZE {
                let page = format!("{}-{} of {} {}", fleet_first + 1,
                    (fleet_first + PAGE_SIZE).min(career_rows.len()), career_rows.len(),
                    if show_squads { "squads" } else { "ships" });
                text::draw_centered(&mut pixels, WIDTH, 417, &page, 1, MUTED);
            }
            text::draw_centered(&mut pixels, WIDTH, 441,
                "F fleet   S squads   Wheel / arrows / PageUp / PageDown browse   Esc back", 1, MUTED);
            mouse_down = window.get_mouse_down(MouseButton::Left);
            window.update_with_buffer(&pixels, WIDTH, HEIGHT)?;
            continue;
        }
        navigate_rows(&window, selected, &mut first, titles.len());
        if window.is_key_pressed(Key::Enter, KeyRepeat::No) && rows[*selected].unlocked {
            return Ok(Some(NativeBattleMenuAction::Play(*selected)));
        }
        let pressed = window.get_mouse_down(MouseButton::Left);
        if pressed && !mouse_down {
            if let Some((x, y)) = window.get_mouse_pos(MouseMode::Discard) {
                let y = y as usize;
                if (100.0..700.0).contains(&x) && (LIST_TOP..LIST_TOP + PAGE_SIZE * ROW_HEIGHT).contains(&y) {
                    let row = (y - LIST_TOP) / ROW_HEIGHT;
                    if first + row < titles.len() && (y - LIST_TOP) % ROW_HEIGHT < ROW_HEIGHT - 4 {
                        *selected = first + row;
                        if rows[*selected].unlocked {
                            return Ok(Some(NativeBattleMenuAction::Play(*selected)));
                        }
                    }
                }
            }
        }
        mouse_down = pressed;

        pixels.fill(0x080e19);
        text::draw_centered(&mut pixels, WIDTH, 24,
            if library_mode { "Bees Native" } else { "Battles" }, 2, TEXT);
        text::draw_centered(&mut pixels, WIDTH, 78,
            if library_mode { "Campaigns and quick battles" }
            else { "Choose a battle to play" }, 1, MUTED);
        if let Some(career) = &career_stats {
            let known_mining = career.total_mined_tsv.map_or(
                "unknown".to_owned(), |value| value.to_string(),
            );
            text::draw_centered(&mut pixels, WIDTH, 96, &format!(
                "Score {:+} | Lost {} Returned {} Kills {} | Mined {} TSV{}",
                career.score, career.ships_lost, career.ships_returned,
                career.credited_kills, known_mining,
                if career.score_complete && career.match_history_complete &&
                    career.total_mined_tsv.is_some()
                {
                    ""
                } else { " (partial older history)" },
            ), 1, MUTED);
        }
        for (row, title) in titles.iter().skip(first).take(PAGE_SIZE).enumerate() {
            let top = LIST_TOP + row * ROW_HEIGHT;
            let color = if first + row == *selected { 0x294a66 } else { 0x142235 };
            for y in top..top + ROW_HEIGHT - 4 {
                pixels[y * WIDTH + 100..y * WIDTH + 700].fill(color);
            }
            let text_color = if rows[first + row].unlocked { TEXT } else { MUTED };
            text::draw_centered(&mut pixels, WIDTH, top + 8, title, 1, text_color);
        }
        if titles.len() > PAGE_SIZE {
            let page = format!("{}-{} of {} {}", first + 1,
                (first + PAGE_SIZE).min(titles.len()), titles.len(),
                if library_mode { "games" } else { "battles" });
            text::draw_centered(&mut pixels, WIDTH, 417, &page, 1, MUTED);
        }
        text::draw_centered(&mut pixels, WIDTH, 441,
            if library_mode { "Enter open   Wheel / arrows / PageUp / PageDown browse   Esc exit" }
            else { "Enter play  B build  T transfer  N new  D delete  P tactics  R name  F2 codex  F fleet  S squads" },
            1, MUTED);
        if launch_failed {
            text::draw_centered(&mut pixels, WIDTH, 468,
                if library_mode { "Game failed. See the console for details." }
                else { "Battle failed. See the console for details." }, 1, [247, 172, 131]);
        }
        window.update_with_buffer(&pixels, WIDTH, HEIGHT)?;
    }
    Ok(None)
}
