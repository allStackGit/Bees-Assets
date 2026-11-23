//! Portable user-editable native gameplay hotkeys, shared by the local
//! playable client and the authenticated multiplayer window. Only UI keys
//! are rebound: the single authoritative player-input action vocabulary,
 //! physics, replay and RL all remain unchanged.
 
use std::collections::{BTreeMap, HashSet};
use std::fs;
use std::io::{self, Read};
use std::path::PathBuf;
use std::time::{Duration, Instant};
use bees_sim::geometry::Vec2;
use minifb::{Key, KeyRepeat, ScaleMode, Window, WindowOptions};
use serde::{Deserialize, Serialize};
use super::NativeSoftwareRenderer;

/// Battlefield right-drag orders use the source world-distance and cadence.
pub(crate) struct WorldMoveDrag {
    next_move_at: Instant,
    last_destination: Vec2,
}

impl WorldMoveDrag {
    const INTERVAL: Duration = Duration::from_millis(250);

    pub(crate) fn new(now: Instant, origin: Vec2) -> Self {
        Self { next_move_at: now + Self::INTERVAL, last_destination: origin }
    }

    pub(crate) fn should_move(&mut self, now: Instant, destination: Vec2) -> bool {
        if now < self.next_move_at || (destination - self.last_destination).length_squared() <= 25.0 {
            return false;
        }
        self.last_destination = destination;
        self.next_move_at = now + Self::INTERVAL;
        true
    }
}

const DEFAULTS: &[(&str, Key)] = &[
    ("Attack on Sight", Key::A),
    ("Show Ranges", Key::B),
    ("Hold", Key::C),
    ("Detonate", Key::D),
    ("Chase", Key::E),
    ("Fit Camera", Key::F),
    ("Guard", Key::G),
    ("Heal", Key::H),
    ("Mini Map", Key::I),
    ("Manual Fire", Key::J),
    ("Beacon", Key::K),
    ("Mine", Key::M),
    ("Lock On", Key::O),
    ("Patrol", Key::P),
    ("Charge", Key::Q),
    ("Retreat", Key::R),
    ("Shooting Priority", Key::S),
    ("Point Fire", Key::T),
    ("Free Speed", Key::U),
    ("Cease Fire", Key::V),
    ("Stop Fire", Key::X),
    ("Match Speed", Key::Y),
    // Source combined tool: holding one key enables the existing range
    // overlay and the same manual-fire gesture, never a new weapon command.
    ("Show Ranges + Manual Fire", Key::W),
];
// Unity UserSettingsData.DefaultHotKeys supplies the 16 source shooting
// priorities directly. Their IDs are the FIRST 16 entries of the existing
// BeesServer SHOOTING_STRATEGIES registry; Type A-X stay in the full picker.
const SHIFTED: &[(&str, Key)] = &[
    ("Shooting: First Seen", Key::A),
    ("Shooting: Random", Key::S),
    ("Shooting: Revenge", Key::D),
    ("Shooting: Most Dangerous", Key::F),
    ("Shooting: Most Health", Key::G),
    ("Shooting: Least Health", Key::H),
    ("Shooting: Most Powerful", Key::J),
    ("Shooting: Least Powerful", Key::K),
    ("Shooting: Closest", Key::Z),
    ("Shooting: Furthest", Key::X),
    ("Shooting: Most Range", Key::C),
    ("Shooting: Least Range", Key::V),
    ("Shooting: Fastest", Key::B),
    ("Shooting: Slowest", Key::N),
    ("Shooting: Most Valuable", Key::M),
    ("Shooting: Least Valuable", Key::Comma),
];

// Unity UserSettingsData's source tactical actions are independent from
// both unmodified native controls and the sixteen direct shooting keys.
// Keep one shared config and one unique Shift-key assignment namespace.
const TACTICAL_SHIFTED: &[(&str,Key)] = &[
    ("Tactic: Match Speed", Key::Q),
    ("Tactic: Attack on Sight", Key::W),
    ("Tactic: Cease Fire", Key::E),
    ("Tactic: Patrol", Key::R),
    ("Tactic: Guard", Key::T),
    ("Tactic: Chase", Key::Y),
    ("Tactic: Hold", Key::U),
    ("Tactic: Detonate", Key::I),
    ("Tactic: Charge", Key::O),
    ("Tactic: Beacon", Key::P),
];

const LETTERS: &[Key] = &[
    Key::A, Key::B, Key::C, Key::D, Key::E, Key::F, Key::G,
    Key::H, Key::I, Key::J, Key::K, Key::L, Key::M, Key::N,
    Key::O, Key::P, Key::Q, Key::R, Key::S, Key::T, Key::U,
    Key::V, Key::W, Key::X, Key::Y, Key::Z,
];

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct StoredBindings {
    schema_version: u32,
    bindings: BTreeMap<String,String>,
}

#[derive(Default, Deserialize, Serialize)]
#[serde(default, deny_unknown_fields)]
struct BattleViewPreferences {
    edge_scrolling: Option<bool>,
    wasd_camera: Option<bool>,
    campaign_tooltips: Option<bool>,
    /// Render pacing only; deterministic physics and PPO step cadence are
    /// independent of the user's presentation frame rate.
    render_fps: Option<u32>,
}

impl BattleViewPreferences {
    fn load(path: &std::path::Path) -> Self {
        let loaded = (|| -> io::Result<Self> {
            let metadata = fs::symlink_metadata(path)?;
            if !metadata.file_type().is_file() || metadata.len() > 1024 {
                return Err(io::Error::new(io::ErrorKind::InvalidData, "invalid native camera settings"));
            }
            let mut bytes = Vec::new();
            fs::File::open(path)?.take(1025).read_to_end(&mut bytes)?;
            if bytes.len() > 1024 {
                return Err(io::Error::new(io::ErrorKind::InvalidData, "invalid native camera settings"));
            }
            serde_json::from_slice(&bytes).map_err(io::Error::other)
        })();
        match loaded {
            Ok(value) => value,
            Err(error) if error.kind() == io::ErrorKind::NotFound => Self::default(),
            Err(error) => {
                eprintln!("Using the level's native camera settings: {error}");
                Self::default()
            }
        }
    }
}

const BINDINGS_PER_PAGE: usize = 13;

pub fn show_control_settings() -> Result<(), Box<dyn std::error::Error>> {
    const WIDTH: usize = 800;
    const HEIGHT: usize = 496;
    let mut controls = NativeControls::load()?;
    let mut window = Window::new("Bees | Controls", WIDTH, HEIGHT, WindowOptions {
        resize: true,
        scale_mode: ScaleMode::AspectRatioStretch,
        ..WindowOptions::default()
    })?;
    window.set_target_fps(30);
    let mut pixels = vec![0; WIDTH * HEIGHT];
    while window.is_open() {
        if window.is_key_pressed(Key::F5, KeyRepeat::No) ||
            (controls.mode == EditMode::Navigate && window.is_key_pressed(Key::Escape, KeyRepeat::No)) {
            break;
        }
        controls.edit(&window);
        pixels.fill(0x080e19);
        controls.draw(&mut pixels, WIDTH, HEIGHT);
        window.update_with_buffer(&pixels, WIDTH, HEIGHT)?;
    }
    Ok(())
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum EditMode {
    Navigate,
    Capture,
    ConfirmReset,
}

pub(crate) struct NativeControls {
    path: PathBuf,
    camera: BattleViewPreferences,
    keys: Vec<Key>,
    selected: usize,
    mode: EditMode,
    settings_unreadable: bool,
    notice: String,
}

impl NativeControls {
    pub(crate) fn load() -> io::Result<Self> {
        let path = if let Some(path) = std::env::var_os("BEES_NATIVE_CONTROLS") {
            PathBuf::from(path)
        } else {
            let root = std::env::var_os("XDG_CONFIG_HOME")
                .map(PathBuf::from)
                .or_else(|| std::env::var_os("APPDATA").map(PathBuf::from))
                .or_else(|| std::env::var_os("HOME")
                    .map(|home| PathBuf::from(home).join(".config")))
                .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound,
                    "no XDG_CONFIG_HOME, APPDATA or HOME for native controls"))?;
            root.join("bees-native").join("controls.json")
        };
        let (keys, notice, settings_unreadable) = match Self::load_keys(&path) {
            Ok(keys) => (keys, "Select an action, press Enter, then press a letter".into(), false),
            Err(error) => {
                eprintln!("Using default native controls: {error}");
                (Self::default_keys(), "Saved controls could not be read. Delete restores defaults.".into(), true)
            }
        };
        let mut camera = BattleViewPreferences::load(&path.with_extension("camera.json"));
        if camera.render_fps.is_some_and(|fps| !matches!(fps, 30 | 60 | 120)) {
            eprintln!("Invalid native render cap; using the original game default");
            camera.render_fps = None;
        }
        Ok(Self {
            path, camera, keys, selected: 0, mode: EditMode::Navigate, notice, settings_unreadable,
        })
    }

    fn default_keys() -> Vec<Key> {
        DEFAULTS.iter().chain(SHIFTED.iter()).chain(TACTICAL_SHIFTED.iter())
            .map(|&(_, key)| key).collect()
    }

    fn load_keys(path: &std::path::Path) -> io::Result<Vec<Key>> {
        const LIMIT: u64 = 16 * 1024;
        let mut keys = Self::default_keys();
        match fs::symlink_metadata(path) {
            Ok(metadata) if !metadata.file_type().is_file() =>
                return Err(io::Error::new(io::ErrorKind::InvalidData,
                    "native control settings must be a regular file")),
            Ok(_) => {
                let mut bytes = Vec::new();
                fs::File::open(path)?.take(LIMIT + 1).read_to_end(&mut bytes)?;
                if bytes.len() as u64 > LIMIT {
                    return Err(io::Error::new(io::ErrorKind::InvalidData,
                        "native control settings exceed the supported size"));
                }
                let stored: StoredBindings = serde_json::from_slice(&bytes)
                    .map_err(|err| io::Error::new(io::ErrorKind::InvalidData, err))?;
                if stored.schema_version != 1 {
                    return Err(io::Error::new(io::ErrorKind::InvalidData,
                        "unsupported native control settings schema"));
                }
                // Existing source hotkey settings predate tactical
                // Shift aliases. Never invalidate a user's chosen direct
                // shooting bindings simply because a newly supplied
                // default happens to occupy that key.
                let has_tactical=stored.bindings.keys().any(|name|
                    name.starts_with("Tactic: "));
                let has_combined=stored.bindings.contains_key(
                    "Show Ranges + Manual Fire");
                for (action, letter) in stored.bindings {
                    let index=DEFAULTS.iter().chain(SHIFTED.iter())
                        .chain(TACTICAL_SHIFTED.iter())
                        .position(|&(name,_)|name==action)
                        .ok_or_else(||io::Error::new(io::ErrorKind::InvalidData,
                            format!("unknown native hotkey action: {action}")))?;
                    let Some(key) = LETTERS.iter().copied()
                        .chain(std::iter::once(Key::Comma))
                        .find(|key|
                            (index>=DEFAULTS.len() || *key!=Key::Comma) &&
                            format!("{key:?}")==letter)
                    else { return Err(io::Error::new(io::ErrorKind::InvalidData,
                        format!("invalid native hotkey for {action}: {letter}"))) };
                    keys[index]=key;
                }
                if !has_combined {
                    // Existing customized layouts may have reassigned W.
                    // Extend the old layout without losing any user key.
                    let combined=DEFAULTS.len()-1;
                    if keys[..combined].contains(&keys[combined]) {
                        keys[combined]=LETTERS.iter().copied()
                            .find(|key| !keys[..combined].contains(key))
                            .ok_or_else(||io::Error::new(io::ErrorKind::InvalidData,
                                "no free combined range/fire shortcut"))?;
                    }
                }
                if !has_tactical {
                    // Legacy files have only the original non-shifted
                    // commands and 16 shooting preferences. Validate that
                    // original layout first; conflicting saved user keys
                    // were invalid even before the new source shortcuts.
                    let previous=DEFAULTS.len()+SHIFTED.len();
                    if keys[..previous].iter().enumerate().map(|(i,&key)|
                        (i>=DEFAULTS.len(),key)).collect::<HashSet<_>>()
                            .len()!=previous
                    {
                        return Err(io::Error::new(io::ErrorKind::InvalidData,
                            "legacy native hotkeys contain duplicate assignments"));
                    }
                    // Extend, rather than replace, those saved choices.
                    // There are 26 shifted commands and 27 supported
                    // shifted keys, so a free slot exists whenever a
                    // newly added tactical default overlaps an old binding.
                    for index in previous..keys.len() {
                        if keys[DEFAULTS.len()..index].contains(&keys[index]) {
                            let replacement=LETTERS.iter().copied()
                                .chain(std::iter::once(Key::Comma))
                                .find(|key|!keys[DEFAULTS.len()..index].contains(key) &&
                                    !keys[index+1..].contains(key))
                                .ok_or_else(||io::Error::new(
                                    io::ErrorKind::InvalidData,
                                    "no free source tactical shortcut"))?;
                            keys[index]=replacement;
                        }
                    }
                }
            },
            Err(err) if err.kind()==io::ErrorKind::NotFound => {},
            Err(err) => return Err(err),
        }
        if keys.iter().enumerate().map(|(index,&key)|
            (index>=DEFAULTS.len(),key)).collect::<HashSet<_>>().len()!=keys.len() {
            return Err(io::Error::new(io::ErrorKind::InvalidData,
                "native gameplay hotkeys must be unique"));
        }
        Ok(keys)
    }

    pub(crate) fn edge_scrolling(&self, level_default: bool) -> bool {
        self.camera.edge_scrolling.unwrap_or(level_default)
    }

    fn save_edge_scrolling(&mut self, enabled: Option<bool>) -> io::Result<()> {
        // Camera settings have their own file, so changing them from a
        // battle never republishes stale key bindings from that window.
        self.camera.edge_scrolling = enabled;
        self.save_view_preferences()
    }

    fn save_view_preferences(&self) -> io::Result<()> {
        // Keep the existing preferences path so older settings load in place.
        let path = self.path.with_extension("camera.json");
        match fs::symlink_metadata(&path) {
            Ok(metadata) if !metadata.file_type().is_file() =>
                return Err(io::Error::new(io::ErrorKind::InvalidData, "invalid native camera settings path")),
            Ok(_) => {},
            Err(error) if error.kind() == io::ErrorKind::NotFound => {},
            Err(error) => return Err(error),
        }
        let parent = path.parent().filter(|parent| !parent.as_os_str().is_empty())
            .unwrap_or_else(|| std::path::Path::new("."));
        fs::create_dir_all(parent)?;
        crate::checkpoint_file::persist_atomic_json(&path, &self.camera)
            .map_err(|error| io::Error::other(format!("{error:?}")))
    }

    pub(crate) fn toggle_edge_scrolling(&mut self, level_default: bool) -> &'static str {
        let enabled = !self.edge_scrolling(level_default);
        if let Err(error) = self.save_edge_scrolling(Some(enabled)) {
            eprintln!("Could not save native edge scrolling: {error}");
            "Mouse-edge scrolling changed for this battle only"
        } else if enabled {
            "Mouse-edge scrolling enabled"
        } else {
            "Mouse-edge scrolling disabled"
        }
    }

    pub(crate) fn wasd_camera(&self) -> bool {
        self.camera.wasd_camera.unwrap_or(true)
    }

    pub(crate) fn show_campaign_tooltips(&self) -> bool {
        self.camera.campaign_tooltips.unwrap_or(true)
    }

    /// The frame cap affects only the client's window event/render loop.
    /// The fixed timestep remains owned by the one native Level.
    pub(crate) fn render_fps(&self, default: usize) -> usize {
        self.camera.render_fps.map_or(default, |fps| fps as usize)
    }

    fn uses_camera_letter(&self, key: Key) -> bool {
        self.wasd_camera() && matches!(key, Key::W | Key::A | Key::S | Key::D)
    }

    fn control_held(window: &Window) -> bool {
        window.is_key_down(Key::LeftCtrl) || window.is_key_down(Key::RightCtrl)
    }

    fn alt_held(window: &Window) -> bool {
        window.is_key_down(Key::LeftAlt) || window.is_key_down(Key::RightAlt)
    }

    /// Bare WASD pans. Conflicting native bindings retain their saved
    /// letters and have an Alt chord in this camera layout.
    pub(crate) fn base_binding_matches(&self, window: &Window, key: Key) -> bool {
        !Self::shift_held(window) && (!self.uses_camera_letter(key) ||
            (Self::alt_held(window) && !Self::control_held(window)))
    }

    pub(crate) fn camera_direction(&self, window: &Window) -> Vec2 {
        if !window.is_active() { return Vec2::ZERO; }
        let use_letters = self.wasd_camera() && !Self::shift_held(window) &&
            !Self::alt_held(window) && !Self::control_held(window);
        let down = |arrow, letter| window.is_key_down(arrow) ||
            (use_letters && window.is_key_down(letter));
        let axis = |positive, negative| f32::from(u8::from(positive)) - f32::from(u8::from(negative));
        Vec2::new(
            axis(down(Key::Right, Key::D), down(Key::Left, Key::A)),
            axis(down(Key::Up, Key::W), down(Key::Down, Key::S)),
        )
    }

    pub(crate) fn key(&self, action: &str) -> Key {
        let index = DEFAULTS.iter().position(|&(name,_)|name==action)
            .expect("code uses a declared native hotkey action");
        self.keys[index]
    }

    fn shift_held(window: &Window) -> bool {
        window.is_key_down(Key::LeftShift) ||
            window.is_key_down(Key::RightShift)
    }

    pub(crate) fn pressed(
        &self, window: &Window, action: &str, repeat: KeyRepeat,
    ) -> bool {
        let key = self.key(action);
        self.base_binding_matches(window, key) && window.is_key_pressed(key, repeat)
    }

    pub(crate) fn down(&self, window: &Window, action: &str) -> bool {
        let key = self.key(action);
        self.base_binding_matches(window, key) && window.is_key_down(key)
    }

    /// Source UserSettingsData binds Show Ranges + Manual Fire as a chord
    /// of its separate range and firing keys. Existing native installations
    /// also have a distinct editable combination key. Both routes enter the
    /// one existing held-fire action; neither changes World weapon rules.
    pub(crate) fn combined_range_fire_down(&self, window: &Window) -> bool {
        self.down(window, "Show Ranges + Manual Fire") ||
            (self.down(window, "Show Ranges") &&
             self.down(window, "Manual Fire"))
    }

    pub(crate) fn combined_range_fire_started(&self, window: &Window) -> bool {
        self.pressed(window, "Show Ranges + Manual Fire", KeyRepeat::No) ||
            (self.down(window, "Show Ranges") &&
             self.pressed(window, "Manual Fire", KeyRepeat::No)) ||
            (self.down(window, "Manual Fire") &&
             self.pressed(window, "Show Ranges", KeyRepeat::No))
    }

    pub(crate) fn manual_fire_down(&self, window: &Window) -> bool {
        self.down(window, "Manual Fire") || self.combined_range_fire_down(window)
    }

    pub(crate) fn manual_fire_started(&self, window: &Window) -> bool {
        self.pressed(window, "Manual Fire", KeyRepeat::No) ||
            self.combined_range_fire_started(window)
    }

    /// Accept the original source direct priority in IDs 1..16. This
    /// output is still sent through NativePlayerSquadShooting and validated
    /// by the one native Level, not applied to any client-owned weapon.
    pub(crate) fn shooting_for_key(&self,key:Key,shift:bool) -> Option<u8> {
        if !shift {return None;}
        self.keys[DEFAULTS.len()..DEFAULTS.len()+SHIFTED.len()]
            .iter().position(|&binding|binding==key)
            .map(|index|(index+1) as u8)
    }

    pub(crate) fn shooting_pressed(&self,window:&Window) -> Option<u8> {
        if !Self::shift_held(window) ||
            window.is_key_down(Key::LeftCtrl) ||
            window.is_key_down(Key::RightCtrl) {
            return None;
        }
        self.keys[DEFAULTS.len()..DEFAULTS.len()+SHIFTED.len()]
            .iter().enumerate().find_map(|(index,&key)|
            window.is_key_pressed(key,KeyRepeat::No)
                .then_some((index+1) as u8))
    }

    /// Source tactical combinations are interchangeable bindings for the
    /// existing typed gameplay actions; only input recognition changes.
    /// In particular, Shift+I is Detonate and Shift+O is Charge, not the
    /// old native-only flanking gestures.
    pub(crate) fn shifted_tactical_for_key(
        &self,key:Key,shift:bool,
    ) -> Option<&'static str> {
        if !shift {return None;}
        let start=DEFAULTS.len()+SHIFTED.len();
        self.keys[start..].iter().position(|&binding|binding==key)
            .and_then(|i| TACTICAL_SHIFTED[i].0.strip_prefix("Tactic: "))
    }

    pub(crate) fn command_pressed(
        &self,window:&Window,action:&str,repeat:KeyRepeat,
    ) -> bool {
        if window.is_key_down(Key::LeftCtrl) ||
            window.is_key_down(Key::RightCtrl) {return false;}
        if !Self::shift_held(window) {
            return self.pressed(window,action,repeat);
        }
        let start=DEFAULTS.len()+SHIFTED.len();
        TACTICAL_SHIFTED.iter().enumerate().any(|(i,&(label,_))|
            label.strip_prefix("Tactic: ")==Some(action) &&
            window.is_key_pressed(self.keys[start+i],repeat))
    }

    fn is_shifted(index:usize)->bool {index>=DEFAULTS.len()}

    fn name(index:usize)->&'static str {
        DEFAULTS.iter().chain(SHIFTED.iter())
            .chain(TACTICAL_SHIFTED.iter()).nth(index)
            .expect("settings selection must be within the authored list").0
    }

    fn display_key(&self, index: usize, key: Key) -> String {
        if Self::is_shifted(index) { format!("Shift+{key:?}") }
        else if self.uses_camera_letter(key) { format!("Alt+{key:?}") }
        else { format!("{key:?}") }
    }

    fn save(&self) -> io::Result<()> {
        match fs::symlink_metadata(&self.path) {
            Ok(metadata) if !metadata.file_type().is_file() =>
                return Err(io::Error::new(io::ErrorKind::InvalidData,
                    "native control settings must be a regular file")),
            Ok(_) => {},
            Err(error) if error.kind() == io::ErrorKind::NotFound => {},
            Err(error) => return Err(error),
        }
        let mut bindings=BTreeMap::new();
        for ((name,_), key) in DEFAULTS.iter().chain(SHIFTED.iter())
            .chain(TACTICAL_SHIFTED.iter()).zip(&self.keys) {
            bindings.insert((*name).to_string(),format!("{key:?}"));
        }
        let record=StoredBindings {schema_version:1,bindings};
        let parent=self.path.parent().ok_or_else(||io::Error::new(
            io::ErrorKind::InvalidInput,"native controls require a filename"))?;
        fs::create_dir_all(parent)?;
        // Reuse the original host's cross-platform atomic JSON writer,
        // not a second app-specific temporary-file/rename implementation.
        crate::checkpoint_file::persist_atomic_json(&self.path,&record)
            .map_err(|err| io::Error::other(format!("{err:?}")))
    }

    /// Up/Down select, Enter capture next letter, Esc cancel capture.
    /// Each assignment is validated and persisted before it takes effect.
    /// A failed disk commit leaves the live layout unchanged.
    pub(crate) fn edit(&mut self,window:&Window) {
        if self.mode == EditMode::ConfirmReset {
            if window.is_key_pressed(Key::Escape, KeyRepeat::No) {
                self.mode = EditMode::Navigate;
                self.notice = "Restore defaults cancelled".into();
            } else if window.is_key_pressed(Key::Enter, KeyRepeat::No) {
                let previous = std::mem::replace(&mut self.keys, Self::default_keys());
                match self.save() {
                    Ok(()) => {
                        self.settings_unreadable = false;
                        self.notice = "Default controls restored".into();
                    }
                    Err(error) => {
                        self.keys = previous;
                        self.notice = format!("Could not restore default controls: {error}");
                    }
                }
                self.mode = EditMode::Navigate;
            }
            return;
        }
        if self.mode == EditMode::Capture {
            if window.is_key_pressed(Key::Escape,KeyRepeat::No) {
                self.mode = EditMode::Navigate;
                self.notice="Key change cancelled".into();
                return;
            }
            for key in window.get_keys_pressed(KeyRepeat::No) {
                if !LETTERS.contains(&key) &&
                    !(key==Key::Comma && Self::is_shifted(self.selected)) {
                    continue;
                }
                // Preserve one-to-one bindings. A new key already held
                // by another action swaps the old assignment atomically,
                // so users need no temporary spare key to customize.
                let old=self.keys[self.selected];
                let swap=self.keys.iter().enumerate().find_map(|(i,&used)|
                    (i!=self.selected && used==key &&
                        Self::is_shifted(i)==Self::is_shifted(self.selected))
                        .then_some(i));
                if let Some(other)=swap { self.keys[other]=old; }
                self.keys[self.selected]=key;
                match self.save() {
                    Ok(())=> self.notice=format!(
                        "{} mapped to {key:?}{}",
                        Self::name(self.selected),
                        if swap.is_some() {" (previous action swapped)"} else {""}),
                    Err(error)=>{
                        self.keys[self.selected]=old;
                        if let Some(other)=swap {self.keys[other]=key;}
                        self.notice=format!("Could not save binding: {error}");
                    }
                }
                self.mode = EditMode::Navigate;
                return;
            }
        } else if window.is_key_pressed(Key::Insert, KeyRepeat::No) {
            let next = match self.camera.edge_scrolling {
                None => Some(true),
                Some(true) => Some(false),
                Some(false) => None,
            };
            self.notice = match self.save_edge_scrolling(next) {
                Ok(()) => "Camera preference saved".into(),
                Err(error) => format!("Could not save camera preference: {error}"),
            };
        } else if window.is_key_pressed(Key::F7, KeyRepeat::No) {
            let prior = self.camera.wasd_camera;
            self.camera.wasd_camera = Some(!self.wasd_camera());
            self.notice = match self.save_view_preferences() {
                Ok(()) if self.wasd_camera() => "Camera uses WASD and arrows".into(),
                Ok(()) => "Camera uses arrow keys".into(),
                Err(error) => {
                    self.camera.wasd_camera = prior;
                    format!("Could not save camera keys: {error}")
                }
            };
        } else if window.is_key_pressed(Key::F8, KeyRepeat::No) {
            let prior = self.camera.campaign_tooltips;
            self.camera.campaign_tooltips = Some(!self.show_campaign_tooltips());
            self.notice = match self.save_view_preferences() {
                Ok(()) if self.show_campaign_tooltips() => "Campaign tips enabled".into(),
                Ok(()) => "Campaign tips disabled".into(),
                Err(error) => {
                    self.camera.campaign_tooltips = prior;
                    format!("Could not save campaign tips: {error}")
                }
            };
        } else if window.is_key_pressed(Key::F9, KeyRepeat::No) {
            let previous = self.camera.render_fps;
            self.camera.render_fps = match previous {
                None => Some(30),
                Some(30) => Some(60),
                Some(60) => Some(120),
                Some(_) => None,
            };
            self.notice = match self.save_view_preferences() {
                Ok(()) => format!("Native render cap: {}",
                    self.camera.render_fps.map_or_else(
                        || "Game default".to_owned(),
                        |fps| format!("{fps} FPS"),
                    )),
                Err(error) => {
                    self.camera.render_fps = previous;
                    format!("Could not save native render cap: {error}")
                }
            };
        } else if window.is_key_pressed(Key::Delete, KeyRepeat::No) {
            self.mode = EditMode::ConfirmReset;
            self.notice = "Replace saved bindings with defaults? Enter confirms, Esc cancels.".into();
        } else if window.is_key_pressed(Key::Home, KeyRepeat::No) {
            self.selected = 0;
        } else if window.is_key_pressed(Key::End, KeyRepeat::No) {
            self.selected = self.keys.len() - 1;
        } else if window.is_key_pressed(Key::PageUp, KeyRepeat::Yes) {
            self.selected = self.selected.saturating_sub(BINDINGS_PER_PAGE);
        } else if window.is_key_pressed(Key::PageDown, KeyRepeat::Yes) {
            self.selected = (self.selected + BINDINGS_PER_PAGE).min(self.keys.len() - 1);
        } else if window.is_key_pressed(Key::Up,KeyRepeat::Yes) {
            self.selected=self.selected.saturating_sub(1);
        } else if window.is_key_pressed(Key::Down,KeyRepeat::Yes) {
            self.selected=(self.selected+1).min(self.keys.len()-1);
        } else if window.is_key_pressed(Key::Enter,KeyRepeat::No) {
            if self.settings_unreadable {
                self.notice = "Restore defaults with Delete before replacing unreadable settings".into();
                return;
            }
            self.mode = EditMode::Capture;
            self.notice=format!("Choose a letter for {}{}",
                Self::name(self.selected),
                if Self::is_shifted(self.selected) {" (Shift is automatic)"}
                else {""});
        }
    }

    pub(crate) fn draw(&self,pixels:&mut [u32],width:usize,height:usize) {
        let start = (self.selected / BINDINGS_PER_PAGE) * BINDINGS_PER_PAGE;
        let mut lines=vec![
            "NATIVE GAMEPLAY HOTKEY SETTINGS".to_owned(),
            "Up/Down: choose   Enter: rebind   Delete: defaults   F5: close".to_owned(),
            "Page Up/Down: jump   Esc: cancel rebind   Assigned letters swap".to_owned(),
            format!("Insert: mouse-edge scrolling [{}]", match self.camera.edge_scrolling {
                None => "Level default",
                Some(true) => "On",
                Some(false) => "Off",
            }),
            format!("F7: camera keys [{}]   F8: campaign tips [{}]",
                if self.wasd_camera() { "WASD + arrows" } else { "Arrows" },
                if self.show_campaign_tooltips() { "On" } else { "Off" }),
            format!("F9: native rendering [{}] (30 / 60 / 120 FPS)",
                self.camera.render_fps.map_or_else(
                    || "Game default".to_owned(), |fps| format!("{fps} FPS")
                )),
        ];
        for i in start..(start + BINDINGS_PER_PAGE).min(self.keys.len()) {
            lines.push(format!("{} {:<23} : {}",
                if i==self.selected {">"} else {" "},
                Self::name(i), self.display_key(i, self.keys[i])));
        }
        lines.push(" ".to_owned());
        lines.push(format!("{}",
            if self.mode == EditMode::Capture {"Waiting for a letter (or comma for shooting)..."}
            else {&self.notice}));
        let borrowed=lines.iter().map(String::as_str).collect::<Vec<_>>();
        NativeSoftwareRenderer::draw_help_panel(
            pixels,width,height,&borrowed,&[0,1,2],
        );
    }
}
