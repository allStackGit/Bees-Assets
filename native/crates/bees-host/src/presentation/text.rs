//! Small bitmap text path for the tactical framebuffer.

const FONT: &[u8] = include_bytes!("../../../../assets/ui-font.alpha");
const WIDTH: usize = FONT[0] as usize;
pub(super) const HEIGHT: usize = FONT[1] as usize;
const LEFT_PADDING: usize = FONT[2] as usize;
const STRIDE: usize = WIDTH * HEIGHT + 1;

fn glyph(character: char) -> &'static [u8] {
    let character = if (' '..='~').contains(&character) { character as u8 } else { b'?' };
    let offset = 3 + usize::from(character - 32) * STRIDE;
    &FONT[offset..offset + STRIDE]
}

pub(super) fn width(text: &str, scale: usize) -> usize {
    text.chars().map(|character| usize::from(glyph(character)[0]) * scale).sum()
}

#[cfg(feature = "client")]
pub(super) fn fit(text: &str, max_width: usize) -> &str {
    let mut advance = 0;
    for (index, character) in text.char_indices() {
        advance += usize::from(glyph(character)[0]);
        if advance > max_width { return &text[..index]; }
    }
    text
}

pub(super) fn blend(destination: u32, color: [u8; 3], opacity: u32) -> u32 {
    let mut rgb = 0;
    for (source, shift) in color.into_iter().zip([16, 8, 0]) {
        let previous = (destination >> shift) & 255;
        let channel = (u32::from(source) * opacity + previous * (255 - opacity) + 127) / 255;
        rgb |= channel << shift;
    }
    rgb
}

pub(super) fn draw_centered(
    pixels: &mut [u32], stride: usize, top: usize, text: &str, scale: usize, color: [u8; 3],
) {
    draw_at(pixels, stride, stride.saturating_sub(width(text, scale)) / 2,
        top, text, scale, color);
}

/// Draw at a particular screen coordinate. The clipping and font path are
/// identical to the existing centered text renderer; no second glyph cache.
pub(super) fn draw_at(
    pixels: &mut [u32], stride: usize, x: usize, top: usize,
    text: &str, scale: usize, color: [u8; 3],
) {
    let height = pixels.len() / stride;
    let mut cursor = x;
    for character in text.chars() {
        let glyph = glyph(character);
        let left = cursor as isize - (LEFT_PADDING * scale) as isize;
        for (row, scanline) in glyph[1..].chunks_exact(WIDTH).enumerate() {
            for (column, &opacity) in scanline.iter().enumerate() {
                if opacity == 0 { continue; }
                for sy in 0..scale {
                    let y = top + row * scale + sy;
                    if y >= height { break; }
                    for sx in 0..scale {
                        let x = left + (column * scale + sx) as isize;
                        if x < 0 || x as usize >= stride { continue; }
                        let pixel = &mut pixels[y * stride + x as usize];
                        *pixel = blend(*pixel, color, u32::from(opacity));
                    }
                }
            }
        }
        cursor += usize::from(glyph[0]) * scale;
    }
}
