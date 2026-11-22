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
        let mut keys = DEFAULTS.iter().map(|&(_, key)| key).collect::<Vec<_>>();
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
                for (action, letter) in stored.bindings {
                    let Some(index) = DEFAULTS.iter().position(|&(name,_)| name==action)
                    else { return Err(io::Error::new(io::ErrorKind::InvalidData,
                        format!("unknown native hotkey action: {action}"))) };
                    let Some(key) = LETTERS.iter().copied()
                        .find(|key|format!("{key:?}")==letter)
                    else { return Err(io::Error::new(io::ErrorKind::InvalidData,
                        format!("invalid native hotkey for {action}: {letter}"))) };
                    keys[index]=key;
                }
            },
            Err(err) if err.kind()==io::ErrorKind::NotFound => {},
            Err(err) => return Err(err),
        }
        if keys.iter().copied().collect::<HashSet<_>>().len()!=keys.len() {
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

    pub(crate) fn pressed(
        &self, window: &Window, action: &str, repeat: KeyRepeat,
    ) -> bool {
        window.is_key_pressed(self.key(action),repeat)
    }

    pub(crate) fn down(&self, window: &Window, action: &str) -> bool {
        window.is_key_down(self.key(action))
    }

    fn save(&self) -> io::Result<()> {
        let mut bindings=BTreeMap::new();
        for ((name,_), key) in DEFAULTS.iter().zip(&self.keys) {
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
                if !LETTERS.contains(&key) {continue;}
                // Preserve one-to-one bindings. A new key already held
                // by another action swaps the old assignment atomically,
                // so users need no temporary spare key to customize.
                let old=self.keys[self.selected];
                let swap=self.keys.iter().enumerate().find_map(|(i,&used)|
                    (i!=self.selected && used==key).then_some(i));
                if let Some(other)=swap { self.keys[other]=old; }
                self.keys[self.selected]=key;
                match self.save() {
                    Ok(())=> self.notice=format!(
                        "{} mapped to {key:?}{}",
                        DEFAULTS[self.selected].0,
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
            self.notice=format!("Press the new letter for {}",DEFAULTS[self.selected].0);
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
            lines.push(format!("{} {:<23} : {:?}",
                if i==self.selected {">"} else {" "},
                DEFAULTS[i].0,self.keys[i]));
        }
        lines.push(" ".to_owned());
        lines.push(format!("{}",
            if self.editing {"Waiting for a letter..."}
            else {&self.notice}));
        let borrowed=lines.iter().map(String::as_str).collect::<Vec<_>>();
        NativeSoftwareRenderer::draw_help_panel(
            pixels,width,height,&borrowed,&[0,1,2],
        );
    }
}
