//! Portable user-editable native gameplay hotkeys, shared by the local
//! playable client and the authenticated multiplayer window. Only UI keys
//! are rebound: the single authoritative player-input action vocabulary,
 //! physics, replay and RL all remain unchanged.
 
use std::collections::{BTreeMap, HashSet};
use std::fs;
use std::io;
use std::path::PathBuf;
use minifb::{Key, KeyRepeat, Window};
use serde::{Deserialize, Serialize};
use super::NativeSoftwareRenderer;

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

pub(crate) struct NativeControls {
    path: PathBuf,
    keys: Vec<Key>,
    selected: usize,
    editing: bool,
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
        let mut keys=DEFAULTS.iter().chain(SHIFTED.iter())
            .chain(TACTICAL_SHIFTED.iter())
            .map(|&(_,key)|key).collect::<Vec<_>>();
        match fs::symlink_metadata(&path) {
            Ok(metadata) if !metadata.file_type().is_file() =>
                return Err(io::Error::new(io::ErrorKind::InvalidData,
                    "native control settings must be a regular file")),
            Ok(_) => {
                let stored: StoredBindings=serde_json::from_slice(&fs::read(&path)?)
                    .map_err(|err| io::Error::new(io::ErrorKind::InvalidData,err))?;
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
                        if keys[..index].contains(&keys[index]) {
                            let replacement=LETTERS.iter().copied()
                                .chain(std::iter::once(Key::Comma))
                                .find(|key|!keys[..index].contains(key) &&
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
        Ok(Self {
            path, keys, selected:0, editing:false,
            notice:"Select an action, press Enter, then press a letter".into(),
        })
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
        !Self::shift_held(window) &&
            window.is_key_pressed(self.key(action),repeat)
    }

    pub(crate) fn down(&self, window: &Window, action: &str) -> bool {
        !Self::shift_held(window) && window.is_key_down(self.key(action))
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

    fn display_key(index:usize,key:Key)->String {
        if Self::is_shifted(index) {format!("Shift+{key:?}")}
        else {format!("{key:?}")}
    }

    fn save(&self) -> io::Result<()> {
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
        if self.editing {
            if window.is_key_pressed(Key::Escape,KeyRepeat::No) {
                self.editing=false;
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
                self.editing=false;
                return;
            }
        } else if window.is_key_pressed(Key::Up,KeyRepeat::Yes) {
            self.selected=self.selected.saturating_sub(1);
        } else if window.is_key_pressed(Key::Down,KeyRepeat::Yes) {
            self.selected=(self.selected+1).min(self.keys.len()-1);
        } else if window.is_key_pressed(Key::Enter,KeyRepeat::No) {
            self.editing=true;
            self.notice=format!("Choose a letter for {}{}",
                Self::name(self.selected),
                if Self::is_shifted(self.selected) {" (Shift is automatic)"}
                else {""});
        }
    }

    pub(crate) fn draw(&self,pixels:&mut [u32],width:usize,height:usize) {
        const ROWS:usize=13;
        let start=(self.selected/ROWS)*ROWS;
        let mut lines=vec![
            "NATIVE GAMEPLAY HOTKEY SETTINGS".to_owned(),
            "Up/Down: choose   Enter: rebind   F5: close".to_owned(),
            "Esc cancels; assigned letters swap; Ctrl+A selects all".to_owned(),
            " ".to_owned(),
        ];
        for i in start..(start+ROWS).min(self.keys.len()) {
            lines.push(format!("{} {:<23} : {}",
                if i==self.selected {">"} else {" "},
                Self::name(i),Self::display_key(i,self.keys[i])));
        }
        lines.push(" ".to_owned());
        lines.push(format!("{}",
            if self.editing {"Waiting for a letter (or comma for shooting)..."}
            else {&self.notice}));
        let borrowed=lines.iter().map(String::as_str).collect::<Vec<_>>();
        NativeSoftwareRenderer::draw_help_panel(
            pixels,width,height,&borrowed,&[0,1,2],
        );
    }
}
