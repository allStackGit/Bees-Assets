//! Optional game-client soundtrack and sound effects.
//!
//! Uses original Bees MP3 assets but never mutates the deterministic
//! CombatWorld. Audio is absent from headless native gameplay and PPO.
//! The mixer has bounded voices and sound clips are preloaded at client
//! startup, avoiding file I/O during combat frames.

use std::collections::HashMap;
use std::fs::{self, File, OpenOptions};
use std::io::{self, BufReader, Cursor, Read, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use bees_sim::collision::Aabb;
use bees_sim::combat_world::{CombatEvent, CombatWorld, WARP_GATE_OPENING_SECONDS};
use bees_sim::geometry::Vec2;
use bees_sim::simulation::WorldId;
use serde_json::Value;
use rodio::{Decoder, OutputStream, OutputStreamHandle, Sink, Source};

use crate::presentation::{NativeRenderWorldView, NativeShipExplosionSound};
use crate::unity_scene_snapshot::NativeMusicLocation;

const MAX_EFFECT_VOICES: usize = 16;
const MAX_ACTIVE_WARP_AUDIO: usize = 16;
const MAX_NEW_EFFECTS_PER_FRAME: usize = 8;
const MUSIC_VOLUME: f32 = 0.14;
const EFFECT_VOLUME: f32 = 0.30;
const TITANIA_INTRO_END: f32 = 26.565_215;
const TITANIA_LOOP_END: f32 = 185.461_18;

#[derive(serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
struct AudioPreferences {
    schema_version: u32,
    music_level: f32,
    effects_level: f32,
    muted: bool,
}


#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
enum Effect {
    LightLaser, LightLaserAlt, SmallLaser, SmallLaserAlt,
    BigLaser, BigLaserAlt, BeamCannon, FlagshipLaser, QueenLaser,
    RocketLaunch, StrikerBomb, Explosion, SmallExplosion, MediumExplosion,
    LargeExplosion, HugeExplosion, Warp, WarpGateStarting, WarpGateLoop,
    Button, FireBargeClick, Intercom, LevelIntro, Save, DeleteSquad, Error,
}

impl Effect {
    const ALL: [Self; 26] = [
        Self::LightLaser, Self::LightLaserAlt,
        Self::SmallLaser, Self::SmallLaserAlt,
        Self::BigLaser, Self::BigLaserAlt,
        Self::BeamCannon, Self::FlagshipLaser, Self::QueenLaser, Self::RocketLaunch,
        Self::StrikerBomb, Self::Explosion, Self::SmallExplosion,
        Self::MediumExplosion, Self::LargeExplosion, Self::HugeExplosion,
        Self::Warp, Self::WarpGateStarting, Self::WarpGateLoop,
        Self::Button, Self::FireBargeClick, Self::Intercom, Self::LevelIntro,
        Self::Save, Self::DeleteSquad, Self::Error,
    ];

    fn file(self) -> &'static str {
        match self {
            Self::LightLaser => "Light Cannon Laser.mp3",
            Self::LightLaserAlt => "Light Cannon Laser 2.mp3",
            Self::SmallLaser => "Small Cannon Laser.mp3",
            Self::SmallLaserAlt => "Small Cannon Laser 2.mp3",
            Self::BigLaser => "Big Cannon Laser.mp3",
            Self::BigLaserAlt => "Big Cannon Laser 2.mp3",
            Self::BeamCannon => "Beam Cannon.mp3",
            Self::FlagshipLaser => "Flagship Laser Shot.mp3",
            Self::QueenLaser => "Queen Laser.mp3",
            Self::RocketLaunch => "Rocket Launch Sound.mp3",
            Self::StrikerBomb => "Striker Bomb Release.mp3",
            Self::Explosion => "Ship Explosion.mp3",
            Self::SmallExplosion => "Smaller Explosion.mp3",
            Self::MediumExplosion => "Medium Explosion.mp3",
            Self::LargeExplosion => "Larger Explosion.mp3",
            Self::HugeExplosion => "Largest Explosion.mp3",
            Self::Warp => "Enter The Warp.mp3",
            Self::WarpGateStarting => "Warp Gate Opening.mp3",
            Self::WarpGateLoop => "Warp Gate Loop.mp3",
            Self::Button => "Beez Button Click.mp3",
            Self::FireBargeClick => "Fire Barge Detonation Click.mp3",
            Self::Intercom => "Intercom Notification Sound Effect.mp3",
            Self::LevelIntro => "Engine Hum Sound Effect.mp3",
            Self::Save => "Save Sound Effect.mp3",
            Self::DeleteSquad => "Delete Squad Sound Effect.mp3",
            Self::Error => "Error Sound Effect.mp3",
        }
    }

    /// Choose which original audio cues survive a saturated large-battle
    /// presentation frame. The simulation event stream is never reordered.
    fn priority(self) -> u8 {
        match self {
            Self::Intercom | Self::Error | Self::Save | Self::DeleteSquad => 3,
            Self::Explosion | Self::SmallExplosion | Self::MediumExplosion |
            Self::LargeExplosion | Self::HugeExplosion |
            Self::Warp | Self::StrikerBomb | Self::WarpGateStarting => 2,
            Self::Button | Self::FireBargeClick | Self::RocketLaunch |
            Self::BeamCannon | Self::FlagshipLaser | Self::QueenLaser => 1,
            _ => 0,
        }
    }

    fn clip_variant(self, alternate: bool) -> Self {
        if !alternate { return self; }
        match self {
            Self::LightLaser => Self::LightLaserAlt,
            Self::SmallLaser => Self::SmallLaserAlt,
            Self::BigLaser => Self::BigLaserAlt,
            _ => self,
        }
    }

    fn ship_explosion(kind: u8) -> Option<Self> {
        match NativeShipExplosionSound::for_ship_type(kind) {
            NativeShipExplosionSound::Tiny => Some(Self::Explosion),
            NativeShipExplosionSound::Small => Some(Self::SmallExplosion),
            NativeShipExplosionSound::Medium => Some(Self::MediumExplosion),
            NativeShipExplosionSound::Large => Some(Self::LargeExplosion),
            NativeShipExplosionSound::Nuclear => Some(Self::HugeExplosion),
            NativeShipExplosionSound::Silent => None,
        }
    }

    /// Exact ConfigData.WeaponSoundTypes -> AudioController.WeaponSounds.
    /// Other source values have no registered clip (including None and
    /// FlagshipChargingLaser), so do not invent sound at shot release.
    fn source_weapon_sound(kind: u8) -> Option<Self> {
        match kind {
            0 => Some(Self::SmallLaser),
            1 => Some(Self::BigLaser),
            3 => Some(Self::FlagshipLaser),
            4 => Some(Self::QueenLaser),
            5 => Some(Self::BeamCannon),
            7 => Some(Self::LightLaser),
            8 => Some(Self::RocketLaunch),
            _ => None,
        }
    }

    fn weapon(sound: Option<u8>, projectile: u8) -> Option<Self> {
        // A captured explicit silence is different from missing source
        // metadata in an older native scene. Only legacy scenes fall back
        // to the original approximate projectile-family lookup.
        sound.map_or_else(|| Self::projectile(projectile), Self::source_weapon_sound)
    }

    /// The source AudioController assigns weapon sound families, while
    /// historical native scenes only stored actual live projectile types. These
    /// classes identify a sound without creating another shot event in
    /// the shared headless simulation. Explosions instead use gameplay
    /// events so one lethal explosion is not sounded twice.
    fn projectile(kind: u8) -> Option<Self> {
        match kind {
            1 | 2 | 6 => Some(Self::LightLaser),
            3 | 7 | 9 => Some(Self::SmallLaser),
            4 => Some(Self::FlagshipLaser),
            5 => Some(Self::RocketLaunch),
            8 => Some(Self::BeamCannon),
            10 | 11 => Some(Self::QueenLaser),
            // Source StrikerBombDropped is the single authoritative
            // release cue. Do not replay it at the same projectile birth.
            _ => None,
        }
    }
}

/// Retain the eight highest-value original sound cues without heap
/// allocations or a new event clock. On an overfull frame, replace only a
/// strictly lower-priority sound. Ties preserve the source's event order.
fn queue_frame_effect(
    effects: &mut [Option<Effect>; MAX_NEW_EFFECTS_PER_FRAME],
    count: &mut usize, effect: Effect,
    measurements: &mut NativeAudioMeasurements,
) {
    if *count < effects.len() {
        effects[*count] = Some(effect);
        *count += 1;
        return;
    }
    if let Some(index) = effects.iter().position(|queued|
        queued.is_some_and(|queued| queued.priority() < effect.priority())
    ) {
        // Prefer discarding the lowest priority, not just the first shot.
        let lowest = effects.iter().enumerate()
            .min_by_key(|(_, queued)| queued.expect("full effect queue").priority())
            .expect("bounded queue has eight slots").0;
        let index = if effects[lowest].expect("full effect queue").priority() <
            effect.priority() { lowest } else { index };
        effects[index] = Some(effect);
        measurements.high_priority_frame_cues_preserved += 1;
    }
    measurements.sound_effects_dropped += 1;
}

#[inline]
fn on_screen(viewport: Aabb, point: Vec2) -> bool {
    point.x >= viewport.min.x && point.x <= viewport.max.x &&
        point.y >= viewport.min.y && point.y <= viewport.max.y
}

fn source_audio_root() -> Option<PathBuf> {
    // A supplied path is authoritative; do not silently play unrelated
    // source files if a packaged game's configured root is incomplete.
    if let Some(root) = std::env::var_os("BEES_NATIVE_AUDIO") {
        return Some(PathBuf::from(root));
    }
    if let Some(root) = crate::presentation::packaged_asset_root() {
        return Some(root.join("audio"));
    }
    let mut candidates = vec![
        PathBuf::from("assets/audio"), PathBuf::from("native/assets/audio"),
        PathBuf::from("Music"),
    ];
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            candidates.push(dir.join("assets/audio"));
        }
    }
    candidates.push(Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../../Music"));
    candidates.into_iter().find(|root| root.join("Sound Effects").is_dir())
}

#[derive(Clone, Copy, Debug, Default, serde::Serialize)]
pub struct NativeAudioMeasurements {
    pub device_available: bool,
    pub sound_clips_preloaded: usize,
    pub sound_clips_missing: usize,
    pub sound_effects_started: u64,
    pub sound_effects_dropped: u64,
    /// Actual important source events admitted after displacing an
    /// ordinary shot in the same bounded rendered-frame sound budget.
    pub high_priority_frame_cues_preserved: u64,
    /// Real source WarpGate activation and ChangeSpriteLoop playback.
    pub warp_gate_openings_started: u64,
    pub warp_gate_loops_started: u64,
    pub warp_gate_voices_stopped: u64,
    pub warp_gate_voices_dropped: u64,
    pub level_intro_loops_started: u64,
    pub music_tracks_started: u64,
    pub music_tracks_missing: u64,
    pub missed_visualized_ticks: u64,
    pub muted: bool,
    pub music_level: f32,
    pub effects_level: f32,
}

#[derive(Clone, Copy)]
pub enum NativeUiFeedback {
    Saved,
    SquadDeleted,
    Error,
}

pub struct NativeGameAudio {
    handle: Option<OutputStreamHandle>,
    root: Option<PathBuf>,
    effects: HashMap<Effect, Arc<[u8]>>,
    voices: Vec<(Sink, f32)>,
    music: Vec<Sink>,
    level_intro_active: bool,
    level_intro_voice: Option<Sink>,
    location: Option<NativeMusicLocation>,
    current_episode: Option<(WorldId, u64)>,
    last_tick: Option<u64>,
    // Last seen physical tick, not a second projectile identity allocator.
    seen_projectiles: HashMap<i64, u64>,
    // UI-only WarpGate opening/looping clips, bound to the actual
    // CombatWorld gate lifetime. Never stored in a policy/checkpoint.
    seen_warp_gates: HashMap<i64, u64>,
    warp_gate_openings: HashMap<i64, Sink>,
    warp_gate_loops: HashMap<i64, Sink>,
    paused: bool,
    muted: bool,
    music_level: f32,
    effects_level: f32,
    preferences_path: Option<PathBuf>,
    measurements: NativeAudioMeasurements,
    // Drop the output stream only AFTER music and effect sinks.
    _stream: Option<OutputStream>,
}

impl NativeGameAudio {
    pub fn new() -> Self {
        let (stream, handle) = match OutputStream::try_default() {
            Ok((stream, handle)) => (Some(stream), Some(handle)),
            Err(_) => (None, None),
        };
        let root = source_audio_root();
        let mut measurements = NativeAudioMeasurements {
            device_available: handle.is_some(),
            ..NativeAudioMeasurements::default()
        };
        let mut effects = HashMap::new();
        if handle.is_some() {
            for effect in Effect::ALL {
                let data = root.as_ref().and_then(|root|
                    fs::read(root.join("Sound Effects").join(effect.file())).ok()
                );
                if let Some(data) = data {
                    effects.insert(effect, Arc::from(data.into_boxed_slice()));
                    measurements.sound_clips_preloaded += 1;
                } else {
                    measurements.sound_clips_missing += 1;
                }
            }
        }
        Self {
            handle, root, effects,
            voices: Vec::new(), music: Vec::new(), location: None,
            level_intro_active: false, level_intro_voice: None,
            current_episode: None, last_tick: None,
            seen_projectiles: HashMap::new(),
            seen_warp_gates: HashMap::new(),
            warp_gate_openings: HashMap::new(),
            warp_gate_loops: HashMap::new(),
            paused: false, muted: false,
            music_level: 1.0, effects_level: 1.0,
            preferences_path: None,
            measurements, _stream: stream,
        }
    }

    pub fn measurements(&self) -> NativeAudioMeasurements {
        NativeAudioMeasurements {
            muted: self.muted,
            music_level: self.music_level,
            effects_level: self.effects_level,
            ..self.measurements
        }
    }

    /// User-local settings are independent of game saves, level setup,
    /// replay, network authorization and trainer checkpoints. Malformed or
    /// inaccessible preferences cannot prevent the physical game starting.
    pub fn load_preferences(&mut self, path: PathBuf) {
        // Preferences are user-controlled data, not an unbounded game
        // asset. Read at most 4097 bytes even when a file changes size
        // between metadata inspection and opening on disk.
        let existing = fs::symlink_metadata(&path).ok();
        let valid_file = existing.as_ref().is_some_and(|metadata|
            metadata.is_file() && !metadata.file_type().is_symlink() &&
            metadata.len() <= 4096
        );
        let bytes = valid_file.then(|| {
            let mut bytes = Vec::new();
            let file = File::open(&path).ok()?;
            file.take(4097).read_to_end(&mut bytes).ok()?;
            Some(bytes)
        }).flatten();
        if let Some(bytes) = bytes {
            let parsed = (bytes.len() <= 4096).then(|| {
                serde_json::from_slice::<AudioPreferences>(&bytes).ok()
            }).flatten();
            if let Some(config) = parsed.filter(|settings|
                settings.schema_version == 1 &&
                settings.music_level.is_finite() &&
                settings.effects_level.is_finite() &&
                (0.0..=1.0).contains(&settings.music_level) &&
                (0.0..=1.0).contains(&settings.effects_level)
            ) {
                self.music_level = config.music_level;
                self.effects_level = config.effects_level;
                self.muted = config.muted;
                self.apply_levels();
            } else {
                eprintln!("Ignoring invalid native audio preferences at {}", path.display());
            }
        }
        self.preferences_path = Some(path);
    }

    fn save_preferences(&self) -> io::Result<()> {
        let Some(path) = self.preferences_path.as_ref() else { return Ok(()); };
        let parent = path.parent().ok_or_else(|| io::Error::new(
            io::ErrorKind::InvalidInput, "native audio preferences have no parent directory"
        ))?;
        fs::create_dir_all(parent)?;
        let payload = serde_json::to_vec_pretty(&AudioPreferences {
            schema_version: 1, music_level: self.music_level,
            effects_level: self.effects_level, muted: self.muted,
        }).map_err(io::Error::other)?;
        // A tiny write only on user interaction, never from the combat tick.
        // An exclusive temporary file avoids following an existing symlink;
        // rename publishes a complete JSON record on both Windows and Linux.
        for attempt in 0..8 {
            let temporary = path.with_extension(format!(
                "tmp-{}-{attempt}", std::process::id()
            ));
            let file = OpenOptions::new().write(true).create_new(true)
                .open(&temporary);
            let mut file = match file {
                Ok(file) => file,
                Err(error) if error.kind() == io::ErrorKind::AlreadyExists => continue,
                Err(error) => return Err(error),
            };
            let saved = file.write_all(&payload)
                .and_then(|_| file.sync_all())
                .and_then(|_| fs::rename(&temporary, path));
            if saved.is_err() {
                let _ = fs::remove_file(&temporary);
            }
            return saved;
        }
        Err(io::Error::new(io::ErrorKind::AlreadyExists,
            "native audio preference temporary files are occupied"))
    }

    fn record_preferences(&self) {
        if let Err(error) = self.save_preferences() {
            eprintln!("Could not save native audio preferences: {error}");
        }
    }

    fn music_volume(&self) -> f32 {
        if self.muted { 0.0 } else { MUSIC_VOLUME * self.music_level }
    }

    fn effect_volume(&self) -> f32 {
        if self.muted { 0.0 } else { EFFECT_VOLUME * self.effects_level }
    }

    fn level_intro_volume(&self) -> f32 {
        if self.muted { 0.0 } else { 0.35 * self.music_level }
    }

    fn apply_levels(&self) {
        if let Some(voice) = &self.level_intro_voice {
            voice.set_volume(self.level_intro_volume());
        }
        for sink in &self.music { sink.set_volume(self.music_volume()); }
        for (sink, gain) in &self.voices { sink.set_volume(self.effect_volume() * *gain); }
        for sink in self.warp_gate_openings.values()
            .chain(self.warp_gate_loops.values()) {
            sink.set_volume(self.effect_volume());
        }
    }

    /// Source soundtrack and sound effects are distinct mixing channels.
    /// Levels affect existing sinks and every subsequently started clip.
    /// Muting never destroys the user's chosen channel levels.
    pub fn adjust_levels(&mut self, music: bool, delta: f32) -> f32 {
        let level = if music { self.music_level } else { self.effects_level };
        self.set_level(music, (level * 10.0).round() / 10.0 + delta)
    }

    pub fn set_level(&mut self, music: bool, value: f32) -> f32 {
        let level = if music { &mut self.music_level } else { &mut self.effects_level };
        if !value.is_finite() || *level == value.clamp(0.0, 1.0) {
            return *level;
        }
        let updated = value.clamp(0.0, 1.0);
        *level = updated;
        self.apply_levels();
        self.record_preferences();
        updated
    }

    pub fn toggle_muted(&mut self) -> bool {
        self.muted = !self.muted;
        self.apply_levels();
        self.record_preferences();
        self.muted
    }

    pub fn set_paused(&mut self, paused: bool) {
        if self.paused == paused { return; }
        self.paused = paused;
        // Unity's AudioController pauses its soundtrack while gameplay is
        // paused. Short sound effects finish independently of the clock.
        for sink in &self.music {
            if paused { sink.pause(); } else { sink.play(); }
        }
        for sink in self.warp_gate_openings.values()
            .chain(self.warp_gate_loops.values()) {
            if paused { sink.pause(); } else { sink.play(); }
        }
    }

    fn add_music_intro(&mut self, path: &Path) -> Duration {
        let Some(handle) = self.handle.as_ref() else { return Duration::ZERO };
        let Ok(file) = File::open(path) else {
            self.measurements.music_tracks_missing += 1;
            return Duration::ZERO;
        };
        let Ok(source) = Decoder::new(BufReader::new(file)) else {
            self.measurements.music_tracks_missing += 1;
            return Duration::ZERO;
        };
        let duration = source.total_duration().unwrap_or(Duration::ZERO);
        if let Ok(sink) = Sink::try_new(handle) {
            sink.set_volume(self.music_volume());
            sink.append(source);
            if self.paused { sink.pause(); }
            self.music.push(sink);
            self.measurements.music_tracks_started += 1;
        }
        duration
    }

    fn add_music_loop(&mut self, path: &Path, delay: Duration) {
        let Some(handle) = self.handle.as_ref() else { return };
        let Ok(file) = File::open(path) else {
            self.measurements.music_tracks_missing += 1;
            return;
        };
        let Ok(source) = Decoder::new_looped(BufReader::new(file)) else {
            self.measurements.music_tracks_missing += 1;
            return;
        };
        if let Ok(sink) = Sink::try_new(handle) {
            sink.set_volume(self.music_volume());
            // Loops are synchronized to the authentic intro's duration.
            sink.append(source.delay(delay));
            if self.paused { sink.pause(); }
            self.music.push(sink);
            self.measurements.music_tracks_started += 1;
        }
    }

    fn select_titania(&mut self, root: &Path) {
        // The Unity source creates two clips from one master recording.
        // Preserve its exact excerpt boundaries without copying a second
        // MP3 into the repository.
        let packaged = root.join("Titania/Titania Source.mp3");
        let source = root.parent()
            .map(|repo| repo.join("Resources/Music/Titania/Titania Source.mp3"));
        let path = if packaged.is_file() { packaged }
            else if let Some(source) = source.filter(|path| path.is_file()) { source }
            else {
                self.measurements.music_tracks_missing += 1;
                return;
            };
        let intro = Duration::from_secs_f32(TITANIA_INTRO_END);
        let loop_len = Duration::from_secs_f32(
            TITANIA_LOOP_END - TITANIA_INTRO_END,
        );
        if let Some(handle) = self.handle.as_ref() {
            if let Ok(file) = File::open(&path) {
                if let Ok(source) = Decoder::new(BufReader::new(file)) {
                    if let Ok(sink) = Sink::try_new(handle) {
                        sink.set_volume(self.music_volume());
                        sink.append(source.take_duration(intro));
                        if self.paused { sink.pause(); }
                        self.music.push(sink);
                        self.measurements.music_tracks_started += 1;
                    }
                }
            }
            if let Ok(file) = File::open(&path) {
                if let Ok(source) = Decoder::new(BufReader::new(file)) {
                    if let Ok(sink) = Sink::try_new(handle) {
                        sink.set_volume(self.music_volume());
                        // Repeat only the source-authored segment, never
                        // Titania's nonrepeating introduction.
                        sink.append(source.skip_duration(intro)
                            .take_duration(loop_len).repeat_infinite().delay(intro));
                        if self.paused { sink.pause(); }
                        self.music.push(sink);
                        self.measurements.music_tracks_started += 1;
                    }
                }
            }
        }
    }

    /// Called on scene/world selection. A same-location selection keeps
    /// soundtrack phase continuous; changing location stops the old stems.
    /// Menu instances own this loop; pausing them preserves the playhead
    /// while the battle or another screen owns the foreground soundtrack.
    pub fn play_menu_music(&mut self) {
        self.set_paused(false);
        if self.measurements.music_tracks_started != 0 || self.measurements.music_tracks_missing != 0 {
            return;
        }
        if let Some(root) = &self.root {
            let path = root.join("Moon Moon.mp3");
            self.add_music_loop(&path, Duration::ZERO);
        }
    }

    pub fn select_location(&mut self, location: Option<NativeMusicLocation>) {
        if self.location == location { return; }
        // A location transition removes the old Level's attached gate
        // sounds immediately; no menu frame can inherit an offscreen loop.
        self.reset_warp_gate_audio();
        self.location = location;
        for sink in self.music.drain(..) { sink.stop(); }
        let (Some(root), Some(location)) = (self.root.clone(), location) else { return };
        match location {
            NativeMusicLocation::Titania => self.select_titania(&root),
            NativeMusicLocation::Pluto |
            NativeMusicLocation::Neptune |
            NativeMusicLocation::Uranus => {
                let (intro, loops): (&str, &[&str]) = match location {
                    NativeMusicLocation::Pluto => (
                        "Pluto/Intros/Pluto Intro.mp3",
                        &["Pluto/Loops/Pluto Location Loop.mp3",
                          "Pluto/Loops/Pluto Human Loop.mp3",
                          "Pluto/Loops/Pluto Bees Loop.mp3"],
                    ),
                    NativeMusicLocation::Neptune => (
                        "Neptune/Intros/Neptune Intro.mp3",
                        &["Neptune/Loops/Neptune Location Loop.mp3",
                          "Neptune/Loops/Neptune Human Loop.mp3",
                          "Neptune/Loops/Neptune Bees Loop.mp3"],
                    ),
                    NativeMusicLocation::Uranus => (
                        "Uranus/Intros/Uranus Intro.mp3",
                        &["Uranus/Loops/Uranus Location Loop.mp3",
                          "Uranus/Loops/Uranus Human Loop.mp3",
                          "Uranus/Loops/Uranus Bees Loop.mp3"],
                    ),
                    NativeMusicLocation::Titania => unreachable!(),
                };
                let duration = self.add_music_intro(&root.join(intro));
                for &name in loops {
                    self.add_music_loop(&root.join(name), duration);
                }
            }
        }
    }

    /// The original LevelIntro runs a separate loop while its story is
    /// visible, even while physical gameplay and its soundtrack are paused.
    pub fn set_level_intro_ambience(&mut self, active: bool) {
        if self.level_intro_active == active { return; }
        self.level_intro_active = active;
        if !active {
            if let Some(voice) = self.level_intro_voice.take() { voice.stop(); }
            return;
        }
        let Some(handle) = self.handle.as_ref() else { return };
        let Some(bytes) = self.effects.get(&Effect::LevelIntro) else { return };
        let Ok(source) = Decoder::new(Cursor::new(Arc::clone(bytes))) else { return };
        let Ok(voice) = Sink::try_new(handle) else { return };
        voice.set_volume(self.level_intro_volume());
        voice.append(source.repeat_infinite());
        self.level_intro_voice = Some(voice);
        self.measurements.level_intro_loops_started += 1;
    }

    /// A remote TCP seat change retires all sounds belonging to the old
    /// authoritative presentation, even when the new session uses the same
    /// world ID or the map has no soundtrack location.
    pub fn reset_remote_session(&mut self) {
        self.set_level_intro_ambience(false);
        self.reset_warp_gate_audio();
        for (sink, _) in self.voices.drain(..) { sink.stop(); }
        self.current_episode = None;
        self.last_tick = None;
        self.seen_projectiles.clear();
        self.select_location(None);
    }

    /// Authentic game UI click; completely separate from gameplay
    /// commands and the durable player-input trace.
    pub fn click_ui(&mut self) {
        self.voices.retain(|(sink, _)| !sink.empty());
        self.play(Effect::Button);
    }

    pub fn play_ui_feedback(&mut self, feedback: NativeUiFeedback) {
        self.voices.retain(|(sink, _)| !sink.empty());
        self.play(match feedback {
            NativeUiFeedback::Saved => Effect::Save,
            NativeUiFeedback::SquadDeleted => Effect::DeleteSquad,
            NativeUiFeedback::Error => Effect::Error,
        });
    }

    /// Source SquadActionBox.Detonate plays this cue once for every
    /// selected physical Fire Barge before calling FireBarge.Detonate.
    /// It is a local user-command sound, never a fabricated CombatEvent
    /// or a headless training cost. Bound the burst for large selections.
    pub fn campaign_dialogue_presented(&mut self) {
        self.voices.retain(|(sink, _)| !sink.empty());
        self.play(Effect::Intercom);
    }

    pub fn click_fire_barge_detonation(&mut self, count: usize) {
        self.voices.retain(|(sink, _)| !sink.empty());
        self.measurements.sound_effects_dropped +=
            count.saturating_sub(MAX_NEW_EFFECTS_PER_FRAME) as u64;
        for _ in 0..count.min(MAX_NEW_EFFECTS_PER_FRAME) {
            self.play(Effect::FireBargeClick);
        }
    }

    fn play(&mut self, effect: Effect) {
        let Some(handle) = self.handle.as_ref() else { return };
        // The original UIAudioController is independent of the Level's
        // paused combat soundtrack. Dialogue and menu clicks remain
        // audible while a briefing blocks physical gameplay ticks.
        if self.muted || (self.paused &&
            !matches!(effect, Effect::Button | Effect::Intercom |
                Effect::Save | Effect::DeleteSquad | Effect::Error))
        {
            return;
        }
        if self.voices.len() >= MAX_EFFECT_VOICES {
            self.measurements.sound_effects_dropped += 1;
            return;
        }
        let Some(data) = self.effects.get(&effect) else { return };
        let Ok(source) = Decoder::new(Cursor::new(Arc::clone(data))) else {
            self.measurements.sound_effects_dropped += 1;
            return;
        };
        let Ok(sink) = Sink::try_new(handle) else {
            self.measurements.sound_effects_dropped += 1;
            return;
        };
        let gain = if matches!(effect, Effect::Save | Effect::DeleteSquad) {
            0.5625 // Source UIAudioController.ReducedNotificationVolume.
        } else { 1.0 };
        sink.set_volume(self.effect_volume() * gain);
        sink.append(source);
        self.voices.push((sink, gain));
        self.measurements.sound_effects_started += 1;
    }

    /// Start a client-only WarpGate audio stem on its original animation
    /// clock. The actual gameplay gate and opening instant belong to
    /// CombatWorld; never use a second timer to open or close the gate.
    fn start_warp_voice(&mut self, effect: Effect, elapsed: f32) -> Option<Sink> {
        let handle = self.handle.as_ref()?;
        let data = self.effects.get(&effect)?;
        let source = Decoder::new(Cursor::new(Arc::clone(data))).ok()?;
        let sink = Sink::try_new(handle).ok()?;
        sink.set_volume(self.effect_volume());
        if effect == Effect::WarpGateLoop {
            // A reconnecting spectator joins the gate's existing loop
            // phase, not a fresh loop at the beginning of the recording.
            // Source ChangeSpriteLoop fires at the same physical threshold.
            let phase = (elapsed - WARP_GATE_OPENING_SECONDS).max(0.0);
            // Seeking through the entire lifetime of an infinite source
            // would decode minutes of audio when a peer reconnects late.
            // Reduce by the clip duration before asking rodio to skip.
            let offset = source.total_duration()
                .filter(|duration| !duration.is_zero())
                .map(|duration| phase % duration.as_secs_f32())
                .unwrap_or(0.0);
            sink.append(source.repeat_infinite().skip_duration(
                Duration::from_secs_f32(offset),
            ));
        } else {
            // A spectator attaching while the gate is already opening
            // hears the remaining original clip, not a restarted gate.
            sink.append(source.skip_duration(Duration::from_secs_f32(
                elapsed.max(0.0),
            )));
        }
        if self.paused { sink.pause(); }
        Some(sink)
    }

    fn sync_warp_gate(&mut self, gate_id: i64, elapsed: f32, ready: bool, tick: u64) {
        if !elapsed.is_finite() || elapsed < 0.0 { return; }
        let first_seen = self.seen_warp_gates.insert(gate_id, tick).is_none();
        if ready {
            if let Some(starting) = self.warp_gate_openings.remove(&gate_id) {
                starting.stop();
                self.measurements.warp_gate_voices_stopped += 1;
            }
            if self.warp_gate_loops.contains_key(&gate_id) { return; }
        } else {
            if let Some(looping) = self.warp_gate_loops.remove(&gate_id) {
                looping.stop();
                self.measurements.warp_gate_voices_stopped += 1;
            }
            if self.warp_gate_openings.contains_key(&gate_id) { return; }
        }
        if self.warp_gate_openings.len() + self.warp_gate_loops.len() >= MAX_ACTIVE_WARP_AUDIO {
            // No unbounded per-ship audio sources in a large battle.
            // Count distinct rejected gate appearances, not a
            // frame-rate-dependent estimate of missing sound events.
            self.measurements.warp_gate_voices_dropped += u64::from(first_seen);
            return;
        }
        let effect = if ready { Effect::WarpGateLoop } else { Effect::WarpGateStarting };
        if let Some(sink) = self.start_warp_voice(effect, elapsed) {
            if ready {
                self.warp_gate_loops.insert(gate_id, sink);
                self.measurements.warp_gate_loops_started += 1;
            } else {
                self.warp_gate_openings.insert(gate_id, sink);
                self.measurements.warp_gate_openings_started += 1;
            }
        }
    }

    /// Any gate that died, deactivated, became hidden, or left the camera
    /// stops playback. Reuse the per-episode ID map instead of allocating
    /// a new set of visible gates every presentation frame.
    fn finish_warp_gate_audio(&mut self, tick: u64) {
        let seen = &self.seen_warp_gates;
        let before = self.warp_gate_openings.len() + self.warp_gate_loops.len();
        self.warp_gate_openings.retain(|id, sink| {
            if seen.get(id) == Some(&tick) { true } else {
                sink.stop();
                false
            }
        });
        self.warp_gate_loops.retain(|id, sink| {
            if seen.get(id) == Some(&tick) { true } else {
                sink.stop();
                false
            }
        });
        self.measurements.warp_gate_voices_stopped += (before -
            self.warp_gate_openings.len() - self.warp_gate_loops.len()) as u64;
        self.seen_warp_gates.retain(|_, last| *last == tick);
    }

    fn reset_warp_gate_audio(&mut self) {
        for (_, voice) in self.warp_gate_openings.drain()
            .chain(self.warp_gate_loops.drain()) {
            voice.stop();
            self.measurements.warp_gate_voices_stopped += 1;
        }
        self.seen_warp_gates.clear();
    }

    /// Read-only remote sound projection. The only input is host-authorized
    /// visible physical entities and fog-filtered cues. There are no gameplay
    /// callbacks, new policy decisions, or physics work in this client.
    pub fn observe_remote(&mut self, state: &Value, visible: Aabb) {
        let (Some(world), Some(revision), Some(tick)) = (
            state["world_id"].as_u64(), state["episode_revision"].as_u64(),
            state["tick"].as_u64(),
        ) else { return };
        let scope = (WorldId(world), revision);
        if self.current_episode != Some(scope) {
            self.reset_warp_gate_audio();
            self.current_episode = Some(scope);
            self.last_tick = None;
            self.seen_projectiles.clear();
        }
        if self.last_tick.is_some_and(|previous| tick <= previous) { return; }
        let first_snapshot = self.last_tick.is_none();
        if let Some(previous) = self.last_tick {
            self.measurements.missed_visualized_ticks +=
                tick.saturating_sub(previous).saturating_sub(1);
        }
        self.last_tick = Some(tick);
        self.voices.retain(|(sink, _)| !sink.empty());
        // At most eight sounds can be admitted per frame: keep the
        // mixing queue on the stack, including during large battles.
        let mut effects = [None; MAX_NEW_EFFECTS_PER_FRAME];
        let mut effects_len = 0;
        // Explicit server launch events take precedence over observing the
        // corresponding still-live shot in the same snapshot. No double
        // weapon sound when both forms of evidence are present.
        for event in state["audio_events"].as_array().into_iter().flatten() {
            if let Some(id) = event["projectile_id"].as_i64() {
                self.seen_projectiles.insert(id, tick);
            }
            if let Some(id) = event["paired_projectile_id"].as_i64() {
                self.seen_projectiles.insert(id, tick);
            }
            let effect = match event["effect"].as_str() {
                Some("explosion") => Some(Effect::Explosion),
                Some("small_explosion") => Some(Effect::SmallExplosion),
                Some("medium_explosion") => Some(Effect::MediumExplosion),
                Some("large_explosion") => Some(Effect::LargeExplosion),
                Some("huge_explosion") => Some(Effect::HugeExplosion),
                Some("striker_bomb") => Some(Effect::StrikerBomb),
                Some("warp") => Some(Effect::Warp),
                Some("projectile") => event["projectile_type"].as_u64()
                    .and_then(|kind| u8::try_from(kind).ok())
                    .and_then(|kind| Effect::weapon(
                        event["weapon_sound_type"].as_u64()
                            .and_then(|sound| u8::try_from(sound).ok()),
                        kind,
                    ))
                    .map(|effect| effect.clip_variant(
                        event["weapon_sound_variant"].as_bool().unwrap_or(false)
                    )),
                _ => None,
            };
            let (Some(effect), Some(x), Some(y)) =
                (effect, event["x"].as_f64(), event["y"].as_f64())
                else { continue };
            if !x.is_finite() || !y.is_finite() ||
                !on_screen(visible, Vec2::new(x as f32, y as f32)) {
                continue;
            }
            queue_frame_effect(
                &mut effects, &mut effects_len, effect, &mut self.measurements,
            );
        }
        // The fallback still supports earlier hosts that only transmit
        // projectile state, without inventing projectile spawn events.
        for shot in state["projectiles"].as_array().into_iter().flatten() {
            let (Some(id), Some(kind), Some(x), Some(y)) = (
                shot["id"].as_i64(), shot["type"].as_u64(),
                shot["x"].as_f64(), shot["y"].as_f64(),
            ) else { continue };
            if self.seen_projectiles.insert(id, tick).is_some() ||
                first_snapshot || !x.is_finite() || !y.is_finite() ||
                !on_screen(visible, Vec2::new(x as f32, y as f32)) {
                continue;
            }
            let Some(effect) = u8::try_from(kind).ok().and_then(Effect::projectile)
                else { continue };
            queue_frame_effect(
                &mut effects, &mut effects_len, effect, &mut self.measurements,
            );
        }
        self.seen_projectiles.retain(|_, seen| *seen == tick);
        for effect in effects[..effects_len].iter().flatten().copied() {
            self.play(effect);
        }
        // Ship records were filtered by the host's fog and the player's
        // current viewport. A hidden or retired gate cannot keep an
        // unauthorized local loop playing after its physical session ends.
        for ship in state["ships"].as_array().into_iter().flatten() {
            if ship["ship_type"].as_u64() != Some(20) { continue; }
            let (Some(id), Some(x), Some(y), Some(variant), Some(elapsed)) = (
                ship["id"].as_i64(), ship["x"].as_f64(),
                ship["y"].as_f64(), ship["animation_variant"].as_u64(),
                ship["warp_audio_elapsed_seconds"].as_f64(),
            ) else { continue };
            if !(0..=1).contains(&variant) ||
                !x.is_finite() || !y.is_finite() ||
                !elapsed.is_finite() || elapsed < 0.0 ||
                !on_screen(visible, Vec2::new(x as f32, y as f32))
            { continue; }
            self.sync_warp_gate(id, elapsed as f32, variant == 1, tick);
        }
        self.finish_warp_gate_audio(tick);
    }

    /// Presentation-only projection of current canonical physical events.
    /// Once-per-entity sound identity is scoped to world AND episode and
    /// bounded by the number of currently active shots.
    /// Rendering may be slower than physical catch-up. Consume each game's
    /// real event buffer at its own tick, not only the final rendered frame.
    pub fn observe(
        &mut self, view: &NativeRenderWorldView<'_>, visible: Aabb,
    ) {
        self.observe_combat(view.world_id, view.episode_revision,
            view.tick, view.combat, visible);
    }

    pub(crate) fn observe_combat(
        &mut self, world_id: WorldId, episode_revision: u64, tick: u64,
        combat: &CombatWorld, visible: Aabb,
    ) {
        let scope = (world_id, episode_revision);
        if self.current_episode != Some(scope) {
            self.reset_warp_gate_audio();
            self.current_episode = Some(scope);
            self.last_tick = None;
            self.seen_projectiles.clear();
        }
        if self.last_tick == Some(tick) { return; }
        // A client attaching to an already-running Level must not sound
        // every currently airborne projectile as a newly fired weapon.
        // Still record those identities so later frames remain launch-only.
        let first_snapshot = self.last_tick.is_none();
        if let Some(previous) = self.last_tick {
            self.measurements.missed_visualized_ticks +=
                tick.saturating_sub(previous).saturating_sub(1);
        }
        self.last_tick = Some(tick);
        self.voices.retain(|(sink, _)| !sink.empty());
        let mut new_effects = [None; MAX_NEW_EFFECTS_PER_FRAME];
        let mut new_effects_len = 0;
        // Source Weapon.PlaySoundEffect runs on the firing command, not on
        // the first later frame that happens to contain an airborne shot.
        // Projectiles can collide and retire within one fixed tick. The
        // canonical fire event and installed turret survive that short
        // lifetime, while the original simulation remains unchanged.
        let mut previous_launch: Option<(i64, u8, i64)> = None;
        for event in combat.events() {
            let (ship_id, slot, projectile_id) = match event {
                CombatEvent::GameWeaponFired { ship_id, slot, projectile_id } |
                CombatEvent::PolicyWeaponFired { ship_id, slot, projectile_id } =>
                    (*ship_id, *slot, *projectile_id),
                _ => continue,
            };
            // The canonical DualCannon launches two contiguous shot IDs
            // from the SAME source turret on a single SendProjectile.
            // Source PlaySoundEffect runs once, not once per barrel.
            let second_barrel = previous_launch.is_some_and(|(prior_ship, prior_slot, prior_id)|
                prior_ship == ship_id && prior_slot == slot &&
                prior_id.checked_add(1) == Some(projectile_id) &&
                combat.weapon_is_dual_cannon(ship_id, slot));
            previous_launch = Some((ship_id, slot, projectile_id));
            if self.seen_projectiles.insert(projectile_id, tick).is_some() ||
                first_snapshot || second_barrel { continue; }
            let Some(kind) = combat.weapon_projectile_type(ship_id, slot)
                else { continue };
            let Some(effect) = Effect::weapon(
                combat.weapon_sound_type(ship_id, slot), kind,
            ) else { continue };
            let effect = effect.clip_variant(
                crate::presentation::mounted_weapon_clip_variant(
                    ship_id, slot, world_id.0, episode_revision,
                )
            );
            let Some(point) = combat.ship_body(ship_id)
                .map(|body| body.motion.position) else { continue };
            if !on_screen(visible, point) { continue; }
            queue_frame_effect(
                &mut new_effects, &mut new_effects_len, effect,
                &mut self.measurements,
            );
        }
        // Every native turret launch produces a GameWeaponFired or
        // PolicyWeaponFired event in this same fixed step. The old
        // render_projectiles() fallback iterated every live projectile at
        // every catch-up tick, turning audio work into O(active shots) even
        // when none fired. Sounds now follow the actual launch events,
        // including shots that collide and disappear in the same step.
        self.seen_projectiles.retain(|_, seen| *seen == tick);
        for event in combat.events() {
            let (effect, point): (Effect, Option<Vec2>) = match event {
                CombatEvent::ShipDied { victim_id, end_kill: false, .. } => (
                    match combat.ship(*victim_id) {
                        // FireBarge.Kill plays the explosion prefab's
                        // AudioSource once. The same death emits a real
                        // FireTankExploded event below; replaying both
                        // would double its nuclear sound.
                        Some(ship) if ship.ship_type == 10 => continue,
                        Some(ship) => {
                            let Some(effect) = Effect::ship_explosion(ship.ship_type)
                                else { continue };
                            effect
                        }
                        None => Effect::Explosion,
                    },
                    combat.ship_body(*victim_id)
                        .map(|body| body.motion.position),
                ),
                CombatEvent::StrikerBombDropped { ship_id, .. } => (
                    Effect::StrikerBomb,
                    combat.ship_body(*ship_id)
                        .map(|body| body.motion.position),
                ),
                // The Fire Barge's actual explosion prefab owns the one
                // source sound cue; ShipDied only retires its visible hull.
                CombatEvent::FireTankExploded {
                    origin, explosion_projectile_id, ..
                } => (
                    Effect::HugeExplosion,
                    (*origin).or_else(|| combat.render_projectile(
                        *explosion_projectile_id,
                    ).map(|(_, body)| body.motion.position)),
                ),
                CombatEvent::BombDetonated {
                    origin, target_id, ..
                } => (
                    Effect::HugeExplosion,
                    (*origin).or_else(|| combat.ship_body(*target_id)
                        .map(|body| body.motion.position)),
                ),
                CombatEvent::ShipWarped { ship_id, .. } => (
                    Effect::Warp,
                    combat.ship_body(*ship_id)
                        .map(|body| body.motion.position),
                ),
                // Source Barge charging has no authenticated sound
                // identity. Do not impersonate the Fire Barge UI click.
                _ => continue,
            };
            if point.is_some_and(|point| !on_screen(visible, point)) {
                continue;
            }
            queue_frame_effect(
                &mut new_effects, &mut new_effects_len, effect,
                &mut self.measurements,
            );
        }
        for effect in new_effects[..new_effects_len].iter().flatten().copied() {
            self.play(effect);
        }
        // Query only the two physical WarpGate registries, not every
        // deployed ship. This remains client-only presentation cost;
        // headless simulation and PPO never iterate audio state.
        for gate_id in combat.live_warp_gate_ids() {
            let Some(elapsed) = combat.game_warp_gate_animation_elapsed(gate_id)
                else { continue };
            let Some(position) = combat.game_warp_gate_position(gate_id)
                else { continue };
            if on_screen(visible, position) {
                self.sync_warp_gate(
                    gate_id, elapsed, combat.game_warp_gate_ready(gate_id),
                    tick,
                );
            }
        }
        self.finish_warp_gate_audio(tick);
    }
}
