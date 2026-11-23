//! Connection settings for the native desktop multiplayer client.

use minifb::{InputCallback, Key, KeyRepeat, MouseButton, ScaleMode, Window, WindowOptions};
use std::{cell::RefCell, path::PathBuf, rc::Rc};

use super::{text, virtual_window_mouse};

const WIDTH: usize = 800;
const HEIGHT: usize = 400;
const LABELS: [&str; 3] = ["Server address", "Private token file", "Join as"];
const ROW_TOPS: [usize; 3] = [112, 176, 240];
const ROLES: [&str; 4] = ["Side 1", "Side 2", "Spectator for side 1", "Spectator for side 2"];
const TEXT: [u8; 3] = [232, 240, 250];
const MUTED: [u8; 3] = [155, 178, 206];

struct TypedText(Rc<RefCell<String>>);

impl InputCallback for TypedText {
    fn add_char(&mut self, codepoint: u32) {
        if let Some(ch) = char::from_u32(codepoint).filter(|ch| !ch.is_control()) {
            let mut typed = self.0.borrow_mut();
            if typed.len() < 4096 {
                typed.push(ch);
            }
        }
    }
}

#[derive(Clone)]
pub struct MultiplayerJoin {
    pub address: String,
    pub token_file: PathBuf,
    pub side: i32,
    pub spectator: bool,
}

pub fn choose_multiplayer_join(
    initial: Option<MultiplayerJoin>,
) -> Result<Option<MultiplayerJoin>, minifb::Error> {
    let mut window = Window::new("Bees | Join Multiplayer", WIDTH, HEIGHT, WindowOptions {
        resize: true,
        scale_mode: ScaleMode::AspectRatioStretch,
        ..WindowOptions::default()
    })?;
    window.set_target_fps(30);
    let mut pixels = vec![0; WIDTH * HEIGHT];
    let typed = Rc::new(RefCell::new(String::new()));
    window.set_input_callback(Box::new(TypedText(Rc::clone(&typed))));
    let (mut fields, mut role) = match initial {
        Some(initial) => (
            [initial.address, initial.token_file.to_string_lossy().into_owned()],
            usize::from(initial.side == 2) + 2 * usize::from(initial.spectator),
        ),
        None => ([String::new(), String::new()], 0),
    };
    let mut focus = 0;
    let mut mouse_down = false;
    let mut show_error = false;

    while window.is_open() {
        if window.is_key_pressed(Key::Escape, KeyRepeat::No) {
            return Ok(None);
        }
        if let Some(field) = fields.get_mut(focus) {
            let limit = if focus == 0 { 255 } else { 4096 };
            for ch in typed.borrow().chars() {
                if field.len() + ch.len_utf8() <= limit {
                    field.push(ch);
                }
            }
            if window.is_key_pressed(Key::Backspace, KeyRepeat::Yes) {
                field.pop();
            }
            if window.is_key_pressed(Key::Delete, KeyRepeat::No) {
                field.clear();
            }
        }
        typed.borrow_mut().clear();

        if window.is_key_pressed(Key::Tab, KeyRepeat::No) {
            let reverse = window.is_key_down(Key::LeftShift) || window.is_key_down(Key::RightShift);
            focus = (focus + if reverse { 3 } else { 1 }) % 4;
        }
        if focus == 2 {
            if window.is_key_pressed(Key::Left, KeyRepeat::No) {
                role = (role + ROLES.len() - 1) % ROLES.len();
            } else if window.is_key_pressed(Key::Right, KeyRepeat::No) ||
                window.is_key_pressed(Key::Space, KeyRepeat::No)
            {
                role = (role + 1) % ROLES.len();
            }
        }
        let mut join = window.is_key_pressed(Key::Enter, KeyRepeat::No);
        let pressed = window.get_mouse_down(MouseButton::Left);
        if pressed && !mouse_down {
            if let Some((x, y)) = virtual_window_mouse(&window, WIDTH, HEIGHT) {
                if (80.0..720.0).contains(&x) {
                    if let Some(row) = ROW_TOPS.iter().position(|&top|
                        (top as f32..(top + 54) as f32).contains(&y))
                    {
                        focus = row;
                        if row == 2 {
                            role = (role + 1) % ROLES.len();
                        }
                    } else if (310.0..490.0).contains(&x) && (310.0..344.0).contains(&y) {
                        join = true;
                    }
                }
            }
        }
        mouse_down = pressed;

        if join {
            let address = fields[0].trim();
            let file = &fields[1];
            let valid_endpoint = address.rsplit_once(':').is_some_and(|(host, port)|
                !host.is_empty() && port.parse::<u16>().is_ok_and(|port| port != 0));
            if valid_endpoint && address.len() <= 255 && !address.contains(char::is_whitespace) &&
                !file.trim().is_empty()
            {
                return Ok(Some(MultiplayerJoin {
                    address: address.to_owned(),
                    token_file: PathBuf::from(file.as_str()),
                    side: if role % 2 == 0 { 1 } else { 2 },
                    spectator: role >= 2,
                }));
            }
            show_error = true;
        }

        pixels.fill(0x080e19);
        text::draw_centered(&mut pixels, WIDTH, 32, "Join Multiplayer", 2, TEXT);
        text::draw_centered(&mut pixels, WIDTH, 76,
            "Enter host:port and the path to the token file from your host.", 1, MUTED);
        for (row, &top) in ROW_TOPS.iter().enumerate() {
            let active = focus == row;
            let color = if active { TEXT } else { MUTED };
            text::draw_at(&mut pixels, WIDTH, 84, top, LABELS[row], 1, color);
            for line in top + 17..top + 50 {
                pixels[line * WIDTH + 80..line * WIDTH + 720]
                    .fill(if active { 0x263c59 } else { 0x152234 });
            }
            let value = fields.get(row).map_or(ROLES[role], String::as_str);
            text::draw_at(&mut pixels, WIDTH, 91, top + 25,
                text::fit_tail(value, 610), 1, TEXT);
        }
        for line in 310..344 {
            pixels[line * WIDTH + 310..line * WIDTH + 490]
                .fill(if focus == 3 { 0x35557d } else { 0x263c59 });
        }
        text::draw_centered(&mut pixels, WIDTH, 319, "Connect", 1, TEXT);
        let footer = if show_error {
            "Enter a server address with a port and the path to a token file."
        } else {
            "Tab change field   Left / Right change side or role   Enter connect   Esc return"
        };
        text::draw_centered(&mut pixels, WIDTH, 365, footer, 1, MUTED);
        window.update_with_buffer(&pixels, WIDTH, HEIGHT)?;
    }
    Ok(None)
}
