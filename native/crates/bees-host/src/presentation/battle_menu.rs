//! Scenario selection using the same window and bitmap font as play.

use minifb::{Key, KeyRepeat, MouseButton, MouseMode, Window, WindowOptions};

use super::text;

const WIDTH: usize = 800;
const HEIGHT: usize = 496;
const LIST_TOP: usize = 118;
const ROW_HEIGHT: usize = 42;
const PAGE_SIZE: usize = 7;
const TEXT: [u8; 3] = [232, 240, 250];
const MUTED: [u8; 3] = [155, 178, 206];

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

pub enum NativeBattleMenuAction {
    Play(usize),
    Buy { side: i32, ship_type: u8, saved_squad_id: i64 },
}

/// Returns a playable battle or purchase request, never applies
/// gameplay/economic changes inside the presentation layer.
pub fn select_battle(
    rows: &[BattleMenuRow<'_>], selected: &mut usize, launch_failed: bool,
    fleet_rows: &[String], squad_rows: &[String],
    shop_rows: &[NativeShopMenuRow], banked_tsv: u64,
) -> Result<Option<NativeBattleMenuAction>, minifb::Error> {
    if rows.is_empty() { return Ok(None); }
    *selected = (*selected).min(rows.len() - 1);
    let mut window = Window::new("Bees | Battles", WIDTH, HEIGHT, WindowOptions::default())?;
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
    let mut show_shop = false;
    let mut shop_selected = 0_usize;
    let mut shop_first = 0_usize;
    let mut fleet_selected = 0_usize;
    let mut fleet_first = 0_usize;
    while window.is_open() && !window.is_key_down(Key::Escape) {
        if window.is_key_pressed(Key::F, KeyRepeat::No) {
            career_view = if career_view == Some(false) && !show_shop {
                None
            } else { Some(false) };
            show_shop = false;
        }
        if window.is_key_pressed(Key::S, KeyRepeat::No) {
            career_view = if career_view == Some(true) && !show_shop {
                None
            } else { Some(true) };
            show_shop = false;
        }
        if window.is_key_pressed(Key::B, KeyRepeat::No) {
            show_shop = !show_shop;
            career_view = None;
        }
        if show_shop {
            shop_selected = shop_selected.min(shop_rows.len().saturating_sub(1));
            shop_first = shop_first.min(shop_selected);
            if window.is_key_pressed(Key::Up, KeyRepeat::Yes) {
                shop_selected = shop_selected.saturating_sub(1);
            }
            if window.is_key_pressed(Key::Down, KeyRepeat::Yes) {
                shop_selected = (shop_selected + 1)
                    .min(shop_rows.len().saturating_sub(1));
            }
            if shop_selected < shop_first { shop_first = shop_selected; }
            if shop_selected >= shop_first + PAGE_SIZE {
                shop_first = shop_selected + 1 - PAGE_SIZE;
            }
            if window.is_key_pressed(Key::Enter, KeyRepeat::No) &&
                shop_rows.get(shop_selected).is_some_and(|row| row.unlocked)
            {
                let row = &shop_rows[shop_selected];
                return Ok(Some(NativeBattleMenuAction::Buy {
                    side: row.side, ship_type: row.ship_type,
                    saved_squad_id: row.saved_squad_id,
                }));
            }
            let pressed = window.get_mouse_down(MouseButton::Left);
            if pressed && !mouse_down {
                if let Some((x, y)) = window.get_mouse_pos(MouseMode::Discard) {
                    let y = y as usize;
                    if (100.0..700.0).contains(&x) &&
                        (LIST_TOP..LIST_TOP + PAGE_SIZE * ROW_HEIGHT).contains(&y)
                    {
                        let index = shop_first + (y - LIST_TOP) / ROW_HEIGHT;
                        if let Some(row) = shop_rows.get(index) {
                            shop_selected = index;
                            if row.unlocked && (y - LIST_TOP) % ROW_HEIGHT < ROW_HEIGHT - 4 {
                                return Ok(Some(NativeBattleMenuAction::Buy {
                                    side: row.side, ship_type: row.ship_type,
                                    saved_squad_id: row.saved_squad_id,
                                }));
                            }
                        }
                    }
                }
            }
            mouse_down = pressed;
            pixels.fill(0x080e19);
            text::draw_centered(&mut pixels, WIDTH, 24, "Build fleet", 2, TEXT);
            text::draw_centered(&mut pixels, WIDTH, 78,
                &format!("Available mined TSV: {banked_tsv}"), 1, MUTED);
            if shop_rows.is_empty() {
                text::draw_centered(&mut pixels, WIDTH, 218,
                    "Finish a campaign battle to register saved squads", 1, MUTED);
            }
            for (i, row) in shop_rows.iter().skip(shop_first).take(PAGE_SIZE).enumerate() {
                let top = LIST_TOP + i * ROW_HEIGHT;
                let background = if shop_selected == shop_first + i {
                    0x294a66
                } else { 0x142235 };
                for y in top..top + ROW_HEIGHT - 4 {
                    pixels[y * WIDTH + 100..y * WIDTH + 700].fill(background);
                }
                let label = text::fit(&row.title, 560);
                text::draw_centered(&mut pixels, WIDTH, top + 8, &label, 1,
                    if row.unlocked { TEXT } else { MUTED });
            }
            if shop_rows.len() > PAGE_SIZE {
                let page = format!("{}-{} of {} purchases", shop_first + 1,
                    (shop_first + PAGE_SIZE).min(shop_rows.len()), shop_rows.len());
                text::draw_centered(&mut pixels, WIDTH, 417, &page, 1, MUTED);
            }
            text::draw_centered(&mut pixels, WIDTH, 441,
                "Enter build   B return   F fleet   S squads   Esc exit", 1, MUTED);
            window.update_with_buffer(&pixels, WIDTH, HEIGHT)?;
            continue;
        }
        if let Some(show_squads) = career_view {
            let career_rows = if show_squads { squad_rows } else { fleet_rows };
            // Each screen has a different set of persisted identities.
            // Clamp when switching between long ship lists and short
            // squad lists, rather than rendering an apparently empty page.
            fleet_selected = fleet_selected.min(career_rows.len().saturating_sub(1));
            fleet_first = fleet_first.min(fleet_selected);
            if window.is_key_pressed(Key::Up, KeyRepeat::Yes) {
                fleet_selected = fleet_selected.saturating_sub(1);
            }
            if window.is_key_pressed(Key::Down, KeyRepeat::Yes) {
                fleet_selected = (fleet_selected + 1)
                    .min(career_rows.len().saturating_sub(1));
            }
            if fleet_selected < fleet_first { fleet_first = fleet_selected; }
            if fleet_selected >= fleet_first + PAGE_SIZE {
                fleet_first = fleet_selected + 1 - PAGE_SIZE;
            }
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
                "F fleet   S squads   Up / Down scroll   Esc exit", 1, MUTED);
            mouse_down = window.get_mouse_down(MouseButton::Left);
            window.update_with_buffer(&pixels, WIDTH, HEIGHT)?;
            continue;
        }
        if window.is_key_pressed(Key::Up, KeyRepeat::Yes) {
            *selected = selected.saturating_sub(1);
        }
        if window.is_key_pressed(Key::Down, KeyRepeat::Yes) {
            *selected = (*selected + 1).min(titles.len() - 1);
        }
        if *selected < first { first = *selected; }
        if *selected >= first + PAGE_SIZE { first = *selected + 1 - PAGE_SIZE; }
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
        text::draw_centered(&mut pixels, WIDTH, 24, "Battles", 2, TEXT);
        text::draw_centered(&mut pixels, WIDTH, 78, "Choose a battle to play", 1, MUTED);
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
            let page = format!("{}-{} of {} battles", first + 1, (first + PAGE_SIZE).min(titles.len()), titles.len());
            text::draw_centered(&mut pixels, WIDTH, 417, &page, 1, MUTED);
        }
        text::draw_centered(&mut pixels, WIDTH, 441, "Enter play   B build   F fleet   S squads   Esc exit", 1, MUTED);
        if launch_failed {
            text::draw_centered(&mut pixels, WIDTH, 468, "Battle failed. See the console for details.", 1, [247, 172, 131]);
        }
        window.update_with_buffer(&pixels, WIDTH, HEIGHT)?;
    }
    Ok(None)
}
