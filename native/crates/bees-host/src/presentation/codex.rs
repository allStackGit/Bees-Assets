//! Immutable source-authored Fleet Codex for both native menu and
//! authenticated remote client. Only presentation reads this asset;
 //! game physics and native training use the physical CombatWorld.

use std::sync::OnceLock;
use super::text;

/// Codex is read-only authored player information. The numeric fields are
/// imported from Unity's version-5 ship-stats record, independently of
/// physical ship attributes, fleet admission or learning decisions. They are
/// displayed only here; CombatWorld remains the gameplay authority.
#[derive(serde::Deserialize)]
pub(super) struct NativeCodexShip {
    pub(super) ship_type: u8,
    pub(super) name: String,
    pub(super) health: u64,
    pub(super) tsv: u64,
    pub(super) sight: u64,
    pub(super) speed: f32,
    pub(super) range: String,
    pub(super) power: String,
    pub(super) rate_of_fire: String,
    pub(super) description: String,
}

#[derive(serde::Deserialize)]
pub(super) struct NativeCodex {
    pub(super) schema_version: u32,
    pub(super) source_ship_stats_version: u32,
    pub(super) ships: Vec<NativeCodexShip>,
}

pub(super) fn native_codex() -> &'static NativeCodex {
    static SOURCE: OnceLock<NativeCodex> = OnceLock::new();
    SOURCE.get_or_init(|| {
        let codex: NativeCodex = serde_json::from_str(include_str!(
            "../../../../assets/ship-codex.json"
        )).expect("packaged source ship Codex");
        assert_eq!(codex.schema_version, 1);
        assert_eq!(codex.source_ship_stats_version, 5);
        assert_eq!(codex.ships.len(), 23);
        assert!(codex.ships.iter().enumerate().all(|(index, ship)|
            usize::from(ship.ship_type) == index));
        codex
    })
}

/// Wrap only when the selection changes. Rendering at 30fps must not
/// re-layout the source's multi-paragraph descriptions every frame.
pub(super) fn codex_lines(description: &str, max_width: usize) -> Vec<String> {
    let mut lines = Vec::new();
    for paragraph in description.lines() {
        if paragraph.trim().is_empty() {
            lines.push(String::new());
            continue;
        }
        let mut line = String::new();
        for word in paragraph.split_whitespace() {
            if !line.is_empty() &&
                text::width(&format!("{line} {word}"), 1) > max_width {
                lines.push(std::mem::take(&mut line));
            }
            if !line.is_empty() { line.push(' '); }
            line.push_str(word);
        }
        if !line.is_empty() { lines.push(line); }
    }
    lines
}


