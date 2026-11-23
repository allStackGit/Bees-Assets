//! Native desktop multiplayer connection and listener settings.

use minifb::{InputCallback, Key, KeyRepeat, MouseButton, ScaleMode, Window, WindowOptions};
use std::{cell::RefCell, path::PathBuf, rc::Rc};

use super::{text, virtual_window_mouse};

const WIDTH: usize = 800;
const HEIGHT: usize = 480;
const ROW_TOPS: [usize; 4] = [112, 176, 240, 304];
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
    /// DER-encoded CA for a TLS host, not a player private key.
    pub trusted_ca_file: Option<PathBuf>,
    pub side: i32,
    pub spectator: bool,
}

pub fn choose_multiplayer_join(
    initial: Option<MultiplayerJoin>,
) -> Result<Option<(MultiplayerJoin, bool)>, minifb::Error> {
    let (fields, role) = match initial {
        Some(initial) => (
            [initial.address, initial.token_file.to_string_lossy().into_owned(),
                initial.trusted_ca_file.as_ref().map(|path|
                    path.to_string_lossy().into_owned()).unwrap_or_default(),
                String::new()],
            usize::from(initial.side == 2) + 2 * usize::from(initial.spectator),
        ),
        None => ([String::new(), String::new(), String::new(), String::new()], 0),
    };
    Ok(connection_form(fields, Some(role))?.map(|([address, file, ca, _], role, remember)| {
        let role = role.expect("join form has a role");
        (MultiplayerJoin {
            address,
            token_file: PathBuf::from(file),
            trusted_ca_file: (!ca.trim().is_empty()).then(|| PathBuf::from(ca.trim())),
            side: if role % 2 == 0 { 1 } else { 2 },
            spectator: role >= 2,
        }, remember)
    }))
}

pub fn choose_multiplayer_host(
    listen: String, tokens_file: &std::path::Path,
    certificate: Option<&std::path::Path>, private_key: Option<&std::path::Path>,
) -> Result<Option<(String, PathBuf, Option<PathBuf>, Option<PathBuf>, bool)>, minifb::Error> {
    let fields = [
        listen, tokens_file.to_string_lossy().into_owned(),
        certificate.map(|path| path.to_string_lossy().into_owned()).unwrap_or_default(),
        private_key.map(|path| path.to_string_lossy().into_owned()).unwrap_or_default(),
    ];
    Ok(connection_form(fields, None)?.map(|([listen, file, cert, key], _, remember)| (
        listen, PathBuf::from(file),
        (!cert.trim().is_empty()).then(|| PathBuf::from(cert.trim())),
        (!key.trim().is_empty()).then(|| PathBuf::from(key.trim())),
        remember,
    )))
}

fn connection_form(
    mut fields: [String; 4], mut role: Option<usize>,
) -> Result<Option<([String; 4], Option<usize>, bool)>, minifb::Error> {
    let joining = role.is_some();
    let (title, hint, labels, action) = if joining {
        ("Join Multiplayer", "Use host:port or tls://host:port with a private player token.",
            ["Server address", "Private token file", "Trusted CA (DER file, TLS only)", "Join as"], "Connect")
    } else {
        ("Host Multiplayer", "Set an optional TLS certificate and private key, or use the host environment.",
            ["Listen address", "Token map file (blank creates invitations)",
             "TLS server certificate (DER, optional)", "TLS private key (PKCS8 DER, optional)"], "Continue")
    };
    let remember_row = 4;
    let button = 5;
    let field_count = if joining { 3 } else { 4 };
    let row_tops = &ROW_TOPS;
    let action_top = 382;
    let footer_top = 444;
    let mut window = Window::new(&format!("Bees | {title}"), WIDTH, HEIGHT, WindowOptions {
        resize: true,
        scale_mode: ScaleMode::AspectRatioStretch,
        ..WindowOptions::default()
    })?;
    window.set_target_fps(30);
    let mut pixels = vec![0; WIDTH * HEIGHT];
    let typed = Rc::new(RefCell::new(String::new()));
    window.set_input_callback(Box::new(TypedText(Rc::clone(&typed))));
    let mut focus = 0;
    let mut cursors = std::array::from_fn::<_, 4, _>(|index| fields[index].len());
    let mut mouse_down = false;
    let mut show_error = false;
    let mut remember = false;

    while window.is_open() {
        if window.is_key_pressed(Key::Escape, KeyRepeat::No) {
            return Ok(None);
        }
        if let Some(field) = fields.get_mut(focus).filter(|_| focus < field_count) {
            let cursor = &mut cursors[focus];
            let limit = if focus == 0 { 255 } else { 4096 };
            for ch in typed.borrow().chars() {
                if field.len() + ch.len_utf8() <= limit {
                    field.insert(*cursor, ch);
                    *cursor += ch.len_utf8();
                }
            }
            if window.is_key_pressed(Key::Home, KeyRepeat::No) {
                *cursor = 0;
            } else if window.is_key_pressed(Key::End, KeyRepeat::No) {
                *cursor = field.len();
            } else if window.is_key_pressed(Key::Left, KeyRepeat::Yes) {
                *cursor = field[..*cursor].char_indices()
                    .next_back().map_or(0, |(index, _)| index);
            } else if window.is_key_pressed(Key::Right, KeyRepeat::Yes) {
                if *cursor < field.len() {
                    *cursor += field[*cursor..].chars().next()
                        .expect("cursor is on a character boundary").len_utf8();
                }
            }
            if window.is_key_pressed(Key::Backspace, KeyRepeat::Yes) && *cursor > 0 {
                let previous = field[..*cursor].char_indices()
                    .next_back().map_or(0, |(index, _)| index);
                field.drain(previous..*cursor);
                *cursor = previous;
            }
            if window.is_key_pressed(Key::Delete, KeyRepeat::No) && *cursor < field.len() {
                let next = *cursor + field[*cursor..].chars().next()
                    .expect("cursor is on a character boundary").len_utf8();
                field.drain(*cursor..next);
            }
        }
        typed.borrow_mut().clear();

        if window.is_key_pressed(Key::Tab, KeyRepeat::No) {
            let reverse = window.is_key_down(Key::LeftShift) || window.is_key_down(Key::RightShift);
            focus = (focus + if reverse { button } else { 1 }) % (button + 1);
        }
        if let Some(role) = role.as_mut().filter(|_| focus == 3) {
            if window.is_key_pressed(Key::Left, KeyRepeat::No) {
                *role = (*role + ROLES.len() - 1) % ROLES.len();
            } else if window.is_key_pressed(Key::Right, KeyRepeat::No) ||
                window.is_key_pressed(Key::Space, KeyRepeat::No)
            {
                *role = (*role + 1) % ROLES.len();
            }
        }
        if focus == remember_row && window.is_key_pressed(Key::Space, KeyRepeat::No) {
            remember = !remember;
        }
        let mut submit = window.is_key_pressed(Key::Enter, KeyRepeat::No);
        let pressed = window.get_mouse_down(MouseButton::Left);
        if pressed && !mouse_down {
            if let Some((x, y)) = virtual_window_mouse(&window, WIDTH, HEIGHT) {
                if (80.0..720.0).contains(&x) {
                    if let Some(row) = row_tops.iter().position(|&top|
                        (top as f32..(top + 54) as f32).contains(&y))
                    {
                        focus = row;
                        if let Some(role) = role.as_mut().filter(|_| row == 3) {
                            *role = (*role + 1) % ROLES.len();
                        }
                    } else if (80.0..305.0).contains(&x) &&
                        (action_top as f32..(action_top + 34) as f32).contains(&y) {
                        focus = remember_row;
                        remember = !remember;
                    } else if (310.0..490.0).contains(&x) &&
                        (action_top as f32..(action_top + 34) as f32).contains(&y) {
                        submit = true;
                    }
                }
            }
        }
        mouse_down = pressed;

        if submit {
            let address = fields[0].trim();
            let file = &fields[1];
            let valid_endpoint = address.rsplit_once(':').is_some_and(|(host, port)|
                !host.is_empty() && port.parse::<u16>().is_ok_and(|port| port != 0));
            let ca = fields[2].trim();
            let tls = address.starts_with("tls://");
            if valid_endpoint && address.len() <= 255 && !address.contains(char::is_whitespace) &&
                (!joining || !file.trim().is_empty()) &&
                (!joining || tls || ca.is_empty()) &&
                (joining || fields[2].trim().is_empty() == fields[3].trim().is_empty())
            {
                fields[0] = address.to_owned();
                return Ok(Some((fields, role, remember)));
            }
            show_error = true;
        }

        pixels.fill(0x080e19);
        text::draw_centered(&mut pixels, WIDTH, 32, title, 2, TEXT);
        text::draw_centered(&mut pixels, WIDTH, 76, hint, 1, MUTED);
        for (row, &top) in row_tops.iter().enumerate() {
            let active = focus == row;
            let color = if active { TEXT } else { MUTED };
            text::draw_at(&mut pixels, WIDTH, 84, top, labels[row], 1, color);
            for line in top + 17..top + 50 {
                pixels[line * WIDTH + 80..line * WIDTH + 720]
                    .fill(if active { 0x263c59 } else { 0x152234 });
            }
            let value = if joining && row == 3 {
                ROLES[role.expect("role row")]
            } else {
                &fields[row]
            };
            if active && row < field_count {
                // Keep the edit point visible even when a server address
                // or certificate path is wider than the field. The glyph
                // renderer and Unicode byte boundaries are shared with all
                // other native menus; no second text widget is introduced.
                let caret = cursors[row];
                let before = text::fit_tail(&value[..caret], 310);
                let available = 610 - text::width(before, 1).min(610);
                let after = text::fit(&value[caret..], available);
                text::draw_at(&mut pixels, WIDTH, 91, top + 25, before, 1, TEXT);
                let caret_x = 91 + text::width(before, 1);
                text::draw_at(&mut pixels, WIDTH, caret_x, top + 25, after, 1, TEXT);
                for y in top + 22..top + 42 {
                    pixels[y * WIDTH + caret_x] = 0xe8f0fa;
                }
            } else {
                text::draw_at(&mut pixels, WIDTH, 91, top + 25,
                    text::fit_tail(value, 610), 1, TEXT);
            }
        }
        for line in action_top..action_top + 34 {
            pixels[line * WIDTH + 80..line * WIDTH + 305]
                .fill(if focus == remember_row { 0x35557d } else { 0x263c59 });
            pixels[line * WIDTH + 310..line * WIDTH + 490]
                .fill(if focus == button { 0x35557d } else { 0x263c59 });
        }
        let remember_label = if remember {
            "[x] Remember settings"
        } else {
            "[ ] Remember settings"
        };
        text::draw_at(&mut pixels, WIDTH, 91, action_top + 9, remember_label, 1, TEXT);
        text::draw_centered(&mut pixels, WIDTH, action_top + 9, action, 1, TEXT);
        let footer = if show_error && joining {
            "Enter host:port, token file, and CA only for TLS."
        } else if show_error {
            "Enter host:port and either both TLS server files or neither."
        } else if joining {
            "Tab fields   Arrows edit / change role   Enter connect   Esc return"
        } else {
            "Tab fields   Left / Right edit   Enter continue   Esc return"
        };
        text::draw_centered(&mut pixels, WIDTH, footer_top, footer, 1, MUTED);
        window.update_with_buffer(&pixels, WIDTH, HEIGHT)?;
    }
    Ok(None)
}
