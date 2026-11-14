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

/// Returns the selected catalog entry, or None when leaving the launcher.
pub fn select_battle(
    rows: &[BattleMenuRow<'_>], selected: &mut usize, launch_failed: bool,
    fleet_rows: &[String],
) -> Result<Option<usize>, minifb::Error> {
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
    let mut show_fleet = false;
    let mut fleet_selected = 0_usize;
    let mut fleet_first = 0_usize;
    while window.is_open() && !window.is_key_down(Key::Escape) {
        if window.is_key_pressed(Key::F, KeyRepeat::No) {
            show_fleet = !show_fleet;
        }
        if show_fleet {
            if window.is_key_pressed(Key::Up, KeyRepeat::Yes) {
                fleet_selected = fleet_selected.saturating_sub(1);
            }
            if window.is_key_pressed(Key::Down, KeyRepeat::Yes) {
                fleet_selected = (fleet_selected + 1)
                    .min(fleet_rows.len().saturating_sub(1));
            }
            if fleet_selected < fleet_first { fleet_first = fleet_selected; }
            if fleet_selected >= fleet_first + PAGE_SIZE {
                fleet_first = fleet_selected + 1 - PAGE_SIZE;
            }
            pixels.fill(0x080e19);
            text::draw_centered(&mut pixels, WIDTH, 24, "Fleet career", 2, TEXT);
            text::draw_centered(&mut pixels, WIDTH, 78,
                "Completed campaign battles (saved FleetShips only)", 1, MUTED);
            if fleet_rows.is_empty() {
                text::draw_centered(&mut pixels, WIDTH, 218,
                    "No recorded FleetShip careers yet", 1, MUTED);
            }
            for (row, item) in fleet_rows.iter().skip(fleet_first).take(PAGE_SIZE).enumerate() {
                let top = LIST_TOP + row * ROW_HEIGHT;
                let color = if fleet_first + row == fleet_selected { 0x294a66 }
                    else { 0x142235 };
                for y in top..top + ROW_HEIGHT - 4 {
                    pixels[y * WIDTH + 100..y * WIDTH + 700].fill(color);
                }
                let label = text::fit(item, 560);
                text::draw_centered(&mut pixels, WIDTH, top + 8, &label, 1, TEXT);
            }
            if fleet_rows.len() > PAGE_SIZE {
                let page = format!("{}-{} of {} ships", fleet_first + 1,
                    (fleet_first + PAGE_SIZE).min(fleet_rows.len()), fleet_rows.len());
                text::draw_centered(&mut pixels, WIDTH, 417, &page, 1, MUTED);
            }
            text::draw_centered(&mut pixels, WIDTH, 441,
                "F return to missions   Up / Down scroll   Esc exit", 1, MUTED);
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
            return Ok(Some(*selected));
        }
        let pressed = window.get_mouse_down(MouseButton::Left);
        if pressed && !mouse_down {
            if let Some((x, y)) = window.get_mouse_pos(MouseMode::Discard) {
                let y = y as usize;
                if (100.0..700.0).contains(&x) && (LIST_TOP..LIST_TOP + PAGE_SIZE * ROW_HEIGHT).contains(&y) {
                    let row = (y - LIST_TOP) / ROW_HEIGHT;
                    if first + row < titles.len() && (y - LIST_TOP) % ROW_HEIGHT < ROW_HEIGHT - 4 {
                        *selected = first + row;
                        if rows[*selected].unlocked { return Ok(Some(*selected)); }
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
        text::draw_centered(&mut pixels, WIDTH, 441, "Up / Down select   Enter play   F fleet   Esc exit", 1, MUTED);
        if launch_failed {
            text::draw_centered(&mut pixels, WIDTH, 468, "Battle failed. See the console for details.", 1, [247, 172, 131]);
        }
        window.update_with_buffer(&pixels, WIDTH, HEIGHT)?;
    }
    Ok(None)
}
