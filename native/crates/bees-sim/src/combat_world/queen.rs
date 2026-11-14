//! Queen timed YellowJacket waves in the one authoritative CombatWorld.
//! The source Queen's scaled timer and per-minion coroutine are translated
//! into fixed-step schedules. Gameplay and MA-POCA use the same events,
//! physical hull, weapon contacts, fleet identity, and side-cap checks.
use super::*;

pub const QUEEN_TYPE: u8 = 17;
pub const QUEEN_MINION_TYPE: u8 = 22;
const MAX_WAVE_SIZE: u8 = 16;

/// All mutable weapon/physics identity comes from the source-authored
/// YellowJacket template. The prefab's original weapon ID is intentionally
/// replaced for every spawned minion.
#[derive(Clone, Debug)]
pub struct QueenMinionSpec {
    pub asset: Arc<ShipAsset>,
    pub vitals: ShipVitals,
    pub original_health: i32,
    pub size_class: f32,
    pub base_speed: f32,
    pub rotation_speed: f32,
    pub height: f32,
    pub spawn_rotation_degrees: f32,
    pub path_clearance: i32,
    pub movement: GameMovementProfile,
    pub combat_activity: Option<NativeCombatActivity>,
    pub front_offset: f32,
    pub ship_type_letter: u8,
    pub max_weapon_range: f32,
    pub firepower: f32,
    pub special_weapon: NativeSpecialWeaponSpec,
    pub max_living_ships_per_side: Option<usize>,
}

impl QueenMinionSpec {
    pub fn valid(&self) -> bool {
        self.asset.ship_type == QUEEN_MINION_TYPE &&
            !self.vitals.is_dead() && self.vitals.mined_cargo() == 0 &&
            self.original_health > 0 && self.original_health >= self.vitals.health() &&
            self.size_class.is_finite() && self.size_class >= 0.0 &&
            [self.base_speed, self.rotation_speed, self.height,
                self.max_weapon_range, self.firepower].iter()
                .all(|value| value.is_finite() && *value >= 0.0) &&
            self.height > 0.0 && self.movement.valid() &&
            self.max_weapon_range < i32::MAX as f32 &&
            self.max_weapon_range.fract() == 0.0 &&
            self.spawn_rotation_degrees.is_finite() && self.path_clearance >= 0 &&
            self.front_offset.is_finite() && self.ship_type_letter < 24 &&
            self.combat_activity.is_none_or(|value| value.valid()) &&
            self.special_weapon.weapon_id == 0 &&
            self.special_weapon.slot < 5 && self.special_weapon.power >= 0 &&
            self.special_weapon.mount_offset.is_finite() &&
            self.special_weapon.charge_range.is_none() &&
            self.special_weapon.proximity.is_some_and(|range| range.valid()) &&
            self.max_living_ships_per_side.is_none_or(|max| max > 0)
    }
}

#[derive(Clone, Debug)]
pub struct QueenSpawnSpec {
    pub minion: Arc<QueenMinionSpec>,
    pub minions_per_wave: u8,
    pub spawn_frequency_seconds: f32,
    pub seconds_between_minions: f32,
    pub spawn_point: Vec2,
}

impl QueenSpawnSpec {
    pub fn valid(&self) -> bool {
        self.minions_per_wave <= MAX_WAVE_SIZE &&
            // Unity's ScaledTimer accepts a zero interval and fires once
            // per subsequent frame, never an unbounded catch-up loop.
            self.spawn_frequency_seconds.is_finite() && self.spawn_frequency_seconds >= 0.0 &&
            self.seconds_between_minions.is_finite() && self.seconds_between_minions >= 0.0 &&
            self.spawn_point.is_finite() && self.minion.valid()
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize)]
pub struct QueenSpawnMeasurements {
    pub waves_started: u64,
    pub minions_spawned: u64,
    pub minions_blocked_by_cap: u64,
    pub max_concurrent_waves: usize,
}

impl QueenSpawnMeasurements {
    /// Totals are episode-local; the peak is the largest single-Queen
    /// world occupancy, not a fabricated simultaneous cross-world peak.
    pub fn merge(&mut self, other: Self) {
        self.waves_started = self.waves_started.saturating_add(other.waves_started);
        self.minions_spawned = self.minions_spawned.saturating_add(other.minions_spawned);
        self.minions_blocked_by_cap = self.minions_blocked_by_cap
            .saturating_add(other.minions_blocked_by_cap);
        self.max_concurrent_waves = self.max_concurrent_waves
            .max(other.max_concurrent_waves);
    }
}

#[derive(Clone, Debug)]
struct QueenWave {
    elapsed_seconds: f32,
    next_index: u8,
}

#[derive(Clone, Debug)]
pub(super) struct QueenRuntime {
    pub spec: Arc<QueenSpawnSpec>,
    until_next_wave: f32,
    next_sequence: u64,
    waves: Vec<QueenWave>,
}

impl CombatWorld {
    pub fn configure_queen_spawning(
        &mut self, queen_id: i64, spec: Arc<QueenSpawnSpec>,
    ) -> Result<(), CombatWorldError> {
        if self.ship(queen_id).is_none_or(|ship| ship.ship_type != QUEEN_TYPE) ||
            !spec.valid() || self.game_queens.contains_key(&queen_id) {
            return Err(CombatWorldError::InvalidQueenConfiguration);
        }
        self.game_queens.insert(queen_id, QueenRuntime {
            until_next_wave: spec.spawn_frequency_seconds,
            next_sequence: 0,
            waves: Vec::new(), spec,
        });
        Ok(())
    }

    pub fn queen_spawn_measurements(&self) -> QueenSpawnMeasurements {
        self.queen_spawn_measurements
    }

    /// Stage-owned transient ship/weapon IDs cannot be allocated in two
    /// arenas at once. Only worlds with a wave/minion due during the
    /// upcoming step require the global deterministic allocator lane.
    pub fn may_spawn_queen_minion_during(&self, dt: f32) -> bool {
        self.game_queens.iter().any(|(&id, runtime)| {
            let live = self.ship(id).is_some_and(|ship| !ship.vitals.is_dead()) &&
                self.ship_body(id).is_some_and(|body| body.active);
            live && runtime.spec.minions_per_wave > 0 &&
                (runtime.until_next_wave < dt ||
                runtime.waves.iter().any(|wave| wave.next_index < runtime.spec.minions_per_wave &&
                    wave.elapsed_seconds + dt >=
                        f32::from(wave.next_index) * runtime.spec.seconds_between_minions))
        })
    }

    /// Source rows (left-to-right, front-to-back) from
    /// ConfigData.QueenYellowJacketSpawnFormation, ShipOffset = (15, 15).
    pub fn queen_minion_formation_offset(index: u8) -> Option<Vec2> {
        (index < MAX_WAVE_SIZE).then(|| {
            let column = index % 4;
            Vec2::new([-15.0, -7.5, 7.5, 15.0][column as usize],
                -((index / 4) as f32) * 15.0)
        })
    }

    fn spawn_queen_minion(
        &mut self, queen_id: i64, runtime: &QueenRuntime,
        wave_sequence: u64, index: u8,
    ) -> Result<bool, CombatWorldError> {
        let queen = self.ship(queen_id).ok_or(CombatWorldError::UnknownShip)?;
        let side = queen.side;
        let minion = &runtime.spec.minion;
        if minion.max_living_ships_per_side.is_some_and(|cap|
            self.living_ships_on_side(side) >= cap
        ) {
            self.queen_spawn_measurements.minions_blocked_by_cap += 1;
            return Ok(false);
        }
        let parent_fleet_id = self.ship_fleet_identity(queen_id)
            .ok_or(CombatWorldError::InvalidFleetIdentity)?;
        let motion = &self.ship_body(queen_id)
            .ok_or(CombatWorldError::UnknownShip)?.motion;
        let spawn_position = Pose {
            position: motion.position, rotation_degrees: motion.rotation_degrees,
        }.transform_point(runtime.spec.spawn_point);
        let id = self.next_entity_id;
        let weapon_id = id.checked_add(1).ok_or(CombatWorldError::EntityIdExhausted)?;
        if weapon_id == i64::MAX {
            return Err(CombatWorldError::EntityIdExhausted);
        }
        // New YellowJacket Setup starts at the prefab's original rotation,
        // not the Queen's current rotation; only SpawnPoint is rotated.
        let minion_motion = ShipMotion::new(
            spawn_position, minion.spawn_rotation_degrees, minion.rotation_speed,
            minion.base_speed, minion.height,
        );
        self.spawn_ship(&minion.asset, id, side, minion.vitals, minion_motion)?;
        self.configure_ship_game_movement(id, minion.movement)?;
        self.configure_game_ship_original_health(id, minion.original_health)?;
        self.set_ship_collision_profile(id, ShipCollisionProfile {
            original_health: minion.original_health, size_class: minion.size_class,
        })?;
        self.set_ship_fleet_identity(id, parent_fleet_id)?;
        if let Some(activity) = minion.combat_activity {
            self.configure_combat_activity(id, activity)?;
        }
        self.configure_ship_target_geometry(id, NativeShipTargetGeometry {
            front_offset: minion.front_offset, bounds_latched: false,
        })?;
        if self.weapon_rank_history.frame.is_some() {
            let fleet_owner_ship_id = self.weapon_rank_history.ships.get(&queen_id)
                .ok_or(CombatWorldError::MissingAutomaticTargetingInputs)?
                .fleet_owner_ship_id;
            let mut stats = self.weapon_target_rank_stats(queen_id)?;
            stats.ship_id = id;
            stats.ship_type_letter = minion.ship_type_letter;
            // The source minion points its FleetShip at the Queen's
            // persistent parent. Keep the parent's kill-history values.
            stats.firepower = minion.firepower;
            stats.max_range = minion.max_weapon_range as i32;
            stats.speed = minion.base_speed;
            self.configure_weapon_rank_source(NativeWeaponRankSource {
                stats, fleet_owner_ship_id,
            })?;
        }
        self.configure_special_weapon(id, NativeSpecialWeaponSpec {
            weapon_id, ..minion.special_weapon
        })?;
        self.events.push(CombatEvent::QueenMinionSpawned {
            queen_id, minion_id: id, wave_sequence,
            minion_index: index, parent_fleet_id,
        });
        self.queen_spawn_measurements.minions_spawned += 1;
        Ok(true)
    }

    /// Both the standalone rendered game and the headless policy Level
    /// execute this timer inside their one CombatWorld fixed step. A dead
    /// Queen cannot start or complete a suspended wave. Each attempted
    /// minion enforces the current *live* faction cap independently.
    pub(super) fn advance_queen_waves(&mut self, dt: f32)
        -> Result<(), CombatWorldError>
    {
        if self.game_queens.is_empty() { return Ok(()); }
        self.queen_spawn_ids.clear();
        self.queen_spawn_ids.extend(self.game_queens.keys().copied());
        for index in 0..self.queen_spawn_ids.len() {
            let queen_id = self.queen_spawn_ids[index];
            if self.ship(queen_id).is_none_or(|ship| ship.vitals.is_dead()) ||
                self.ship_body(queen_id).is_none_or(|body| !body.active)
            {
                self.game_queens.remove(&queen_id);
                continue;
            }
            let mut runtime = self.game_queens.remove(&queen_id)
                .expect("recorded Queen exists");
            // Source ScaledTimer.Update invokes a recurring callback at
            // most once per frame and uses strict elapsed > Length.
            // Retain overflow for subsequent ticks, even for Length=0.
            let previously_active = runtime.waves.len();
            runtime.until_next_wave -= dt;
            if runtime.until_next_wave < 0.0 {
                // Source coroutines can overlap without a fixed wave
                // count limit. Only one wave starts per fixed step, so
                // no catch-up loop can allocate an unbounded burst.
                runtime.next_sequence = runtime.next_sequence.checked_add(1)
                    .ok_or(CombatWorldError::EntityIdExhausted)?;
                runtime.waves.push(QueenWave {
                    elapsed_seconds: 0.0, next_index: 0,
                });
                runtime.until_next_wave += runtime.spec.spawn_frequency_seconds;
                self.queen_spawn_measurements.waves_started += 1;
            }
            self.queen_spawn_measurements.max_concurrent_waves =
                self.queen_spawn_measurements.max_concurrent_waves.max(runtime.waves.len());
            for wave_index in 0..runtime.waves.len() {
                // Unity starts the newly created coroutine immediately.
                // Its first minion arrives now, but its next yield has not
                // yet accumulated a frame of Time.deltaTime.
                if wave_index < previously_active {
                    runtime.waves[wave_index].elapsed_seconds += dt;
                }
                loop {
                    let wave = &runtime.waves[wave_index];
                    if wave.next_index >= runtime.spec.minions_per_wave ||
                        wave.elapsed_seconds <
                            f32::from(wave.next_index) * runtime.spec.seconds_between_minions
                    { break; }
                    let minion_index = wave.next_index;
                    // Queen.SpawnMinions clears CurrentMinionSquad at
                    // each new wave. An older overlapping coroutine
                    // subsequently joins the *current* group too.
                    let sequence = runtime.next_sequence;
                    runtime.waves[wave_index].next_index += 1;
                    self.spawn_queen_minion(queen_id, &runtime, sequence, minion_index)?;
                }
            }
            runtime.waves.retain(|wave|
                wave.next_index < runtime.spec.minions_per_wave);
            self.game_queens.insert(queen_id, runtime);
        }
        Ok(())
    }
}
