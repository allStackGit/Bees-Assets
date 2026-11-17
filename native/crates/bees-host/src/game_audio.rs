//! Optional game-client soundtrack and sound effects.
//!
//! Uses original Bees MP3 assets but never mutates the deterministic
//! CombatWorld. Audio is absent from headless native gameplay and PPO.
//! The mixer has bounded voices and sound clips are preloaded at client
//! startup, avoiding file I/O during combat frames.

use std::collections::HashMap;
use std::fs::{self, File};
use std::io::{BufReader, Cursor};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use bees_sim::collision::Aabb;
use bees_sim::combat_world::CombatEvent;
use bees_sim::geometry::Vec2;
use bees_sim::simulation::WorldId;
use rodio::{Decoder, OutputStream, OutputStreamHandle, Sink, Source};

use crate::presentation::NativeRenderWorldView;
use crate::unity_scene_snapshot::NativeMusicLocation;

const MAX_EFFECT_VOICES: usize = 16;
const MAX_NEW_EFFECTS_PER_FRAME: usize = 8;
const MUSIC_VOLUME: f32 = 0.14;
const EFFECT_VOLUME: f32 = 0.30;
const TITANIA_INTRO_END: f32 = 26.565_215;
const TITANIA_LOOP_END: f32 = 185.461_18;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
enum Effect {
    LightLaser, SmallLaser, BigLaser, BeamCannon, FlagshipLaser, QueenLaser,
    RocketLaunch, StrikerBomb, Explosion, HugeExplosion,
    Warp, Button,
}

impl Effect {
    const ALL: [Self; 12] = [
        Self::LightLaser, Self::SmallLaser, Self::BigLaser,
        Self::BeamCannon, Self::FlagshipLaser, Self::QueenLaser, Self::RocketLaunch,
        Self::StrikerBomb, Self::Explosion, Self::HugeExplosion,
        Self::Warp, Self::Button,
    ];

    fn file(self) -> &'static str {
        match self {
            Self::LightLaser => "Light Cannon Laser.mp3",
            Self::SmallLaser => "Small Cannon Laser.mp3",
            Self::BigLaser => "Big Cannon Laser.mp3",
            Self::BeamCannon => "Beam Cannon.mp3",
            Self::FlagshipLaser => "Flagship Laser Shot.mp3",
            Self::QueenLaser => "Queen Laser.mp3",
            Self::RocketLaunch => "Rocket Launch Sound.mp3",
            Self::StrikerBomb => "Striker Bomb Release.mp3",
            Self::Explosion => "Ship Explosion.mp3",
            Self::HugeExplosion => "Largest Explosion.mp3",
            Self::Warp => "Enter The Warp.mp3",
            Self::Button => "Beez Button Click.mp3",
        }
    }

    /// The source AudioController assigns weapon sound families, while
    /// the native renderer only sees actual live projectile types. These
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
    pub music_tracks_started: u64,
    pub music_tracks_missing: u64,
    pub missed_visualized_ticks: u64,
    pub muted: bool,
}

pub struct NativeGameAudio {
    handle: Option<OutputStreamHandle>,
    root: Option<PathBuf>,
    effects: HashMap<Effect, Arc<[u8]>>,
    voices: Vec<Sink>,
    music: Vec<Sink>,
    location: Option<NativeMusicLocation>,
    current_episode: Option<(WorldId, u64)>,
    last_tick: Option<u64>,
    // Last seen physical tick, not a second projectile identity allocator.
    seen_projectiles: HashMap<i64, u64>,
    paused: bool,
    muted: bool,
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
            current_episode: None, last_tick: None,
            seen_projectiles: HashMap::new(), paused: false, muted: false,
            measurements, _stream: stream,
        }
    }

    pub fn measurements(&self) -> NativeAudioMeasurements {
        NativeAudioMeasurements {
            muted: self.muted,
            ..self.measurements
        }
    }

    pub fn toggle_muted(&mut self) -> bool {
        self.muted = !self.muted;
        let music_volume = if self.muted { 0.0 } else { MUSIC_VOLUME };
        let effect_volume = if self.muted { 0.0 } else { EFFECT_VOLUME };
        for sink in &self.music { sink.set_volume(music_volume); }
        for sink in &self.voices { sink.set_volume(effect_volume); }
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
            sink.set_volume(if self.muted { 0.0 } else { MUSIC_VOLUME });
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
            sink.set_volume(if self.muted { 0.0 } else { MUSIC_VOLUME });
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
                        sink.set_volume(if self.muted { 0.0 } else { MUSIC_VOLUME });
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
                        sink.set_volume(if self.muted { 0.0 } else { MUSIC_VOLUME });
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
    pub fn select_location(&mut self, location: Option<NativeMusicLocation>) {
        if self.location == location { return; }
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

    /// Authentic game UI click; completely separate from gameplay
    /// commands and the durable player-input trace.
    pub fn click_ui(&mut self) {
        self.voices.retain(|sink| !sink.empty());
        self.play(Effect::Button);
    }

    fn play(&mut self, effect: Effect) {
        let Some(handle) = self.handle.as_ref() else { return };
        if self.muted || self.paused { return; }
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
        sink.set_volume(EFFECT_VOLUME);
        sink.append(source);
        self.voices.push(sink);
        self.measurements.sound_effects_started += 1;
    }

    /// Presentation-only projection of current canonical physical events.
    /// Once-per-entity sound identity is scoped to world AND episode and
    /// bounded by the number of currently active shots.
    pub fn observe(
        &mut self, view: &NativeRenderWorldView<'_>, visible: Aabb,
    ) {
        let scope = (view.world_id, view.episode_revision);
        if self.current_episode != Some(scope) {
            self.current_episode = Some(scope);
            self.last_tick = None;
            self.seen_projectiles.clear();
        }
        if self.last_tick == Some(view.tick) { return; }
        if let Some(previous) = self.last_tick {
            self.measurements.missed_visualized_ticks +=
                view.tick.saturating_sub(previous).saturating_sub(1);
        }
        self.last_tick = Some(view.tick);
        self.voices.retain(|sink| !sink.empty());
        let mut budget = MAX_NEW_EFFECTS_PER_FRAME;
        let mut new_effects = Vec::new();
        for (projectile, body) in view.combat.render_projectiles() {
            if self.seen_projectiles.insert(projectile.entity_id, view.tick).is_some() {
                continue;
            }
            if !on_screen(visible, body.motion.position) { continue; }
            if let Some(effect) = Effect::projectile(projectile.projectile_type) {
                if budget > 0 {
                    budget -= 1;
                    new_effects.push(effect);
                } else {
                    self.measurements.sound_effects_dropped += 1;
                }
            }
        }
        self.seen_projectiles.retain(|_, seen| *seen == view.tick);
        for event in view.combat.events() {
            let (effect, point): (Effect, Option<Vec2>) = match event {
                CombatEvent::ShipDied { victim_id, .. } => (
                    if view.combat.ship(*victim_id)
                        .is_some_and(|ship| matches!(ship.ship_type, 7 | 10 | 11 | 17)) {
                        Effect::HugeExplosion
                    } else { Effect::Explosion },
                    view.combat.ship_body(*victim_id)
                        .map(|body| body.motion.position),
                ),
                CombatEvent::StrikerBombDropped { ship_id, .. } => (
                    Effect::StrikerBomb,
                    view.combat.ship_body(*ship_id)
                        .map(|body| body.motion.position),
                ),
                // Fire Barge death is already represented by ShipDied;
                // sounding both would double the same physical explosion.
                CombatEvent::FireTankExploded { .. } |
                CombatEvent::BombDetonated { .. } => (
                    Effect::HugeExplosion, None,
                ),
                CombatEvent::ShipWarped { ship_id, .. } => (
                    Effect::Warp,
                    view.combat.ship_body(*ship_id)
                        .map(|body| body.motion.position),
                ),
                // Source Barge charging has no authenticated sound
                // identity. Do not impersonate the Fire Barge UI click.
                _ => continue,
            };
            if point.is_some_and(|point| !on_screen(visible, point)) {
                continue;
            }
            if budget > 0 {
                budget -= 1;
                new_effects.push(effect);
            } else {
                self.measurements.sound_effects_dropped += 1;
            }
        }
        for effect in new_effects { self.play(effect); }
    }
}
