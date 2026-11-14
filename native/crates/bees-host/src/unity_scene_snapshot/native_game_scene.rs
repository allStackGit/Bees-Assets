//! Standalone native gameplay scene authoring. No Unity snapshot parser,
//! generated GameObject state, or alternate gameplay/physics implementation.
//! Every physical ship and weapon field unavailable from the immutable
//! collider catalogs is explicitly authored here.
use super::*;

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameScene {
    native_game_schema: u32,
    level_name: String,
    stage_name: String,
    bounds: GameBounds,
    fixed_dt_seconds: f32,
    bee_side: i32,
    human_side: i32,
    command_settings: GameCommandSettings,
    ships: Vec<GameShip>,
    squads: Vec<GameSquad>,
    /// Native-authored physical resources and destructible level objects.
    #[serde(default)]
    mining_asteroids: Vec<GameMiningAsteroid>,
    #[serde(default)]
    map_objects: Vec<GameMapObject>,
    /// Supply the authentic MA-POCA group/episode baselines explicitly when
    /// the native scene owns actual policy-controlled ships.
    #[serde(default)]
    policy_episode: Option<GamePolicyEpisode>,
    #[serde(default)]
    layout: NativeLevelLayout,
    #[serde(default)]
    shots_harmless: bool,
    #[serde(default)]
    activate_collision_asteroids: bool,
    #[serde(default)]
    activate_mining: bool,
    #[serde(default)]
    activate_fog_of_war: bool,
    #[serde(default)]
    activate_hivemind: bool,
    #[serde(default)]
    hivemind_initial_command_delay_seconds: i32,
    #[serde(default)]
    starting_positions: Option<[[f32; 2]; 2]>,
    #[serde(default)]
    camera_zoom: Option<[f32; 2]>,
    #[serde(default)]
    camera_edge_scrolling: bool,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameBounds { min: [f32; 2], max: [f32; 2] }

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameCommandSettings {
    max_command_seconds: f32,
    ai_random_max_distance: f32,
    ai_patrol_max_size: f32,
    ai_squad_patrol_seconds: f32,
    ai_squad_following_seconds: f32,
    ai_squad_guard_seconds: f32,
}

#[derive(Clone, Copy, Deserialize)]
#[serde(deny_unknown_fields)]
struct GamePolicyEpisode {
    episode_number: u32,
    bee_starting_tsv: i32,
    human_starting_tsv: i32,
    timeout_seconds: f32,
    discovery_baselines: [[i32; 4]; 2],
    movement_disabled_by_team: [bool; 2],
}

#[derive(Clone, Copy, Deserialize)]
#[serde(deny_unknown_fields)]
struct GamePolicyAgent {
    agent_id: u64,
    team_id: u8,
    quarter_turns: u8,
    movement_disabled: bool,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameShip {
    entity_id: i64,
    ship_type: u8,
    side: i32,
    position: [f32; 2],
    rotation_degrees: f32,
    rotation_speed_degrees_per_second: f32,
    base_speed: f32,
    height: f32,
    turning_radius: f32,
    size_class: f32,
    health: i32,
    max_health: i32,
    max_tsv: i32,
    original_health: i32,
    #[serde(default)]
    mined_cargo: i32,
    formation_offset: [f32; 2],
    path_clearance: i32,
    sight: GameSight,
    max_weapon_range: f32,
    firepower: f32,
    ship_type_letter: u8,
    target_max_range: i32,
    front_offset: f32,
    combat_window_seconds: f32,
    #[serde(default)]
    is_bomber: bool,
    #[serde(default)]
    is_carrier_ship: bool,
    /// Whether this ship contributes the original fleet's damage/discovery
    /// value. Free Carrier children and minions never contribute.
    #[serde(default)]
    persistent_fleet: bool,
    /// Explicit identity of its durable FleetShip record; mandatory for
    /// persistent hulls, optional for temporary ones.
    #[serde(default)]
    fleet_ship_id: Option<i64>,
    #[serde(default)]
    user_controlled: bool,
    #[serde(default)]
    cease_fire: bool,
    #[serde(default)]
    turrets: Vec<GameTurret>,
    #[serde(default)]
    special: Option<GameShipSpecial>,
    #[serde(default)]
    special_weapon: Option<GameSpecialWeapon>,
    /// A Beehive HealCollider or WarpGate trigger, separate from its
    /// immutable collision hull and from all targeting weapon ranges.
    #[serde(default)]
    support_zone: Option<GameCollider>,
    #[serde(default)]
    policy: Option<GamePolicyAgent>,
    /// Original CarrierShip.Carrier link for a Drone (Strikers use
    /// special.carrier_id). The parent must be an alive same-side Carrier.
    #[serde(default)]
    drone_carrier_id: Option<i64>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameSight {
    source_sight: i32,
    effective_range: i32,
    trigger_range: i32,
    #[serde(default)]
    fog_radius: Option<f32>,
    #[serde(default = "enabled")]
    enabled: bool,
}
fn enabled() -> bool { true }

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameSquad {
    item_id: i64,
    side: i32,
    members: Vec<i64>,
    shooting_strategy_id: u8,
    #[serde(default)]
    user_controlled: bool,
    #[serde(default)]
    can_accept_input: bool,
    #[serde(default)]
    locked_on: bool,
    #[serde(default)]
    should_chase: bool,
    #[serde(default)]
    cease_fire: bool,
    #[serde(default)]
    hotkey_number: Option<u32>,
    #[serde(default)]
    original_squad_count: Option<u32>,
    #[serde(default)]
    matching_speed: Option<f32>,
    #[serde(default)]
    hivemind: Option<GameHiveMindSquad>,
    #[serde(default)]
    command_bans: Vec<u8>,
    #[serde(default)]
    is_carrier_squad: bool,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameHiveMindSquad {
    opponent_id: u64,
    hivemind_controlled: bool,
    immobile: bool,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameTurret {
    slot: usize,
    weapon_type: u8,
    projectile_type: u8,
    mount_offset: [f32; 2],
    initial_rotation_degrees: f32,
    rotation_rate: f32,
    range: f32,
    power: i32,
    rate_of_fire: f32,
    projectile_value: f32,
    projectile_speed: f64,
    fire_pattern: GameFirePattern,
    #[serde(default)]
    aim_at_front_of_ship: Option<bool>,
    #[serde(default)]
    range_collider: Option<GameWeaponRange>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields, tag = "kind", rename_all = "snake_case")]
enum GameFirePattern {
    Single,
    DualCannon { left_offset: [f32; 2], right_offset: [f32; 2] },
    LaserBuilder,
    FullShipTurret,
    BeamCannon,
}
impl GameFirePattern {
    fn into_native(self) -> NativeFirePattern {
        match self {
            Self::Single => NativeFirePattern::Single,
            Self::DualCannon { left_offset, right_offset } => NativeFirePattern::DualCannon {
                left_offset: Vec2::new(left_offset[0], left_offset[1]),
                right_offset: Vec2::new(right_offset[0], right_offset[1]),
            },
            Self::LaserBuilder => NativeFirePattern::LaserBuilder,
            Self::FullShipTurret => NativeFirePattern::FullShipTurret,
            Self::BeamCannon => NativeFirePattern::BeamCannon,
        }
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameWeaponRange {
    offset: [f32; 2],
    radius: f32,
    layer: u8,
    enabled: bool,
}

/// Authored game-ship abilities are connected to the existing world
/// implementations, never approximated from hull class or geometry.
#[derive(Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
enum GameShipSpecial {
    Barge { power: i32, can_override_bounds: bool },
    FireBarge { power: i32 },
    YellowJacket { power: i32 },
    Striker {
        power: i32,
        #[serde(default)]
        carrier_id: Option<i64>,
        offset_from_center: [f32; 2],
        refill_distance: f32,
    },
    Scout { enabled: bool, beacon: GameBeacon },
    Queen { spawn: GameQueenSpawn },
}

/// Exact native YellowJacket prefab inputs for a Queen's *future* waves.
/// No initial ship, weapon or FleetShip ID is claimed for this template:
/// CombatWorld assigns distinct live identities at each actual spawn.
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameQueenSpawn {
    minions_per_wave: u8,
    spawn_frequency_seconds: f32,
    seconds_between_minions: f32,
    spawn_point: [f32; 2],
    minion: GameQueenMinion,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameQueenMinion {
    health: i32,
    original_health: i32,
    max_tsv: i32,
    size_class: f32,
    base_speed: f32,
    rotation_speed: f32,
    height: f32,
    turning_radius: f32,
    sight: GameSight,
    front_offset: f32,
    ship_type_letter: u8,
    max_weapon_range: f32,
    firepower: f32,
    combat_window_seconds: f32,
    observed_weapon_type: u8,
    special_weapon: GameSpecialWeapon,
    #[serde(default)]
    max_living_ships_per_side: Option<usize>,
}

impl GameQueenSpawn {
    fn into_native(self, catalog: &ShipAssetCatalog)
        -> Result<NativeAuthoredShipSpecial, NativeSnapshotError>
    {
        let invalid = || NativeSnapshotError::InvalidNativeLayout(
            "Queen requires exact source YellowJacket spawn and Bomb inputs",
        );
        let minion = self.minion;
        let sprite = catalog.sprite_size(22).ok_or_else(invalid)?;
        let asset = catalog.by_type(22).ok_or_else(invalid)?.clone();
        let weapon = minion.special_weapon;
        let sight = minion.sight;
        let movement = GameMovementProfile {
            turning_radius: minion.turning_radius,
            half_width: sprite.x * 0.5,
            half_height: sprite.y * 0.5,
        };
        let offset = Vec2::new(weapon.mount_offset[0], weapon.mount_offset[1]);
        let observed = weapon.observation.ok_or_else(invalid)?;
        let mut observed_weapons = [None; 5];
        if weapon.slot >= 5 || weapon.weapon_id != 0 ||
            weapon.charge_range.is_some() ||
            minion.observed_weapon_type >= 10 ||
            ![observed.range, observed.rate_of_fire, observed.rotation_rate,
                observed.projectile_value].iter()
                .all(|v| v.is_finite() && *v >= 0.0) ||
            !offset.is_finite() || !sight.enabled && sight.effective_range == 0 ||
            sight.source_sight < 0 || sight.effective_range <= 0 ||
            sight.trigger_range <= 0 || !sight.enabled ||
            !minion.combat_window_seconds.is_finite() ||
            minion.combat_window_seconds <= 0.0
        { return Err(invalid()); }
        observed_weapons[weapon.slot] = Some(ObservedWeapon {
            weapon_type: minion.observed_weapon_type,
            range: observed.range, power: weapon.power,
            rate_of_fire: observed.rate_of_fire,
        });
        let physical = bees_sim::combat_world::QueenSpawnSpec {
            minions_per_wave: self.minions_per_wave,
            spawn_frequency_seconds: self.spawn_frequency_seconds,
            seconds_between_minions: self.seconds_between_minions,
            spawn_point: Vec2::new(self.spawn_point[0], self.spawn_point[1]),
            minion: Arc::new(bees_sim::combat_world::QueenMinionSpec {
                asset: Arc::new(asset),
                vitals: ShipVitals::new(
                    minion.health, minion.health, minion.max_tsv, 0,
                ).map_err(NativeSnapshotError::Combat)?,
                original_health: minion.original_health,
                size_class: minion.size_class,
                base_speed: minion.base_speed, rotation_speed: minion.rotation_speed,
                height: minion.height, movement,
                combat_activity: Some(bees_sim::combat_world::NativeCombatActivity {
                    enabled: true, window_seconds: minion.combat_window_seconds,
                    elapsed_seconds: None,
                }),
                front_offset: minion.front_offset,
                ship_type_letter: minion.ship_type_letter,
                max_weapon_range: minion.max_weapon_range,
                firepower: minion.firepower,
                special_weapon: bees_sim::combat_world::NativeSpecialWeaponSpec {
                    weapon_id: 0, slot: weapon.slot, power: weapon.power,
                    mount_offset: offset,
                    charge_range: None,
                    proximity: weapon.proximity.map(GameWeaponRange::into_native),
                },
                max_living_ships_per_side: minion.max_living_ships_per_side,
            }),
        };
        let perception = AuthoredShipPerception {
            longest_side: sprite.x.max(sprite.y),
            max_weapon_range: minion.max_weapon_range,
            firepower: minion.firepower,
            is_bomber: true, is_carrier_ship: false,
            minerals_mined_this_level: 0, observed_weapons,
            allied_communication: None,
        };
        if !physical.valid() || !perception.valid() ||
            (sight.enabled && sight.trigger_range == 0) ||
            sight.fog_radius.is_some_and(|v| !v.is_finite() || v < 0.0)
        { return Err(invalid()); }
        Ok(NativeAuthoredShipSpecial::Queen {
            minions: Arc::new(NativeQueenTemplate {
                physical, perception,
                vision: NativeSourceVision {
                    source_sight: Some(sight.source_sight),
                    effective_range: sight.effective_range,
                    trigger_range: sight.trigger_range,
                    fog_radius: sight.fog_radius,
                    registered: true, enabled: sight.enabled,
                },
            }),
        })
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameBeacon {
    health: i32,
    original_health: i32,
    max_tsv: i32,
    size_class: f32,
    base_speed: f32,
    rotation_speed: f32,
    height: f32,
    longest_side: f32,
    effective_vision_range: i32,
    hivemind_registered: bool,
    hivemind_vision_enabled: bool,
    #[serde(default)]
    max_living_ships_per_side: Option<usize>,
    front_offset: f32,
    ship_type_letter: u8,
    firepower: f32,
    combat_window_seconds: f32,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameMiningAsteroid {
    entity_id: i64,
    position: [f32; 2],
    rotation_degrees: f32,
    layer: u8,
    health: i32,
    /// Exact collider path relative to the asteroid root. No guessed
    /// mining radius or duplicate training-only resource entity.
    local_vertices: Vec<[f32; 2]>,
    #[serde(default)]
    velocity: [f32; 2],
    #[serde(default)]
    angular_velocity_degrees: f32,
}

#[derive(Deserialize)]
#[serde(rename_all = "snake_case")]
enum GameMapObjectKind { Generic, FireTank }

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameMapObject {
    entity_id: i64,
    kind: GameMapObjectKind,
    position: [f32; 2],
    rotation_degrees: f32,
    layer: u8,
    max_health: i32,
    collider: GameCollider,
    #[serde(default)]
    explosion_power: Option<i32>,
    #[serde(default)]
    linked_obstacle_id: Option<i64>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct GameSpecialWeapon {
    weapon_id: i64,
    slot: usize,
    power: i32,
    mount_offset: [f32; 2],
    #[serde(default)]
    charge_range: Option<GameWeaponRange>,
    #[serde(default)]
    proximity: Option<GameWeaponRange>,
    /// Source Weapon stat fields not derivable from a physical Bomb trigger.
    /// Mandatory for a policy Bomb controller; ordinary authored game
    /// specialists can still omit policy-only observation metadata.
    #[serde(default)]
    observation: Option<GameBombObservation>,
}

#[derive(Clone, Copy, Deserialize)]
#[serde(deny_unknown_fields)]
struct GameBombObservation {
    range: f32,
    rate_of_fire: f32,
    rotation_rate: f32,
    projectile_value: f32,
    #[serde(default)]
    projectile_speed: Option<f64>,
    #[serde(default)]
    has_target_ship: bool,
}

#[derive(Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
enum GameCollider {
    Circle { offset: [f32; 2], radius: f32 },
    Box { offset: [f32; 2], half_extents: [f32; 2] },
    Polygon { vertices: Vec<[f32; 2]> },
}

impl GameCollider {
    fn into_native(self) -> Option<ShipShape> {
        match self {
            Self::Circle { offset, radius } => {
                let offset = Vec2::new(offset[0], offset[1]);
                (offset.is_finite() && radius.is_finite() && radius > 0.0)
                    .then_some(ShipShape::Circle { offset, radius })
            }
            Self::Box { offset, half_extents } => {
                let offset = Vec2::new(offset[0], offset[1]);
                let half_extents = Vec2::new(half_extents[0], half_extents[1]);
                (offset.is_finite() && half_extents.is_finite() &&
                    half_extents.x > 0.0 && half_extents.y > 0.0)
                    .then_some(ShipShape::Box { offset, half_extents })
            }
            Self::Polygon { vertices } => {
                if !(3..=8192).contains(&vertices.len()) {
                    return None;
                }
                let points = vertices.into_iter()
                    .map(|point| Vec2::new(point[0], point[1]))
                    .collect::<Vec<_>>();
                points.iter().all(|point| point.is_finite()).then(||
                    ShipShape::Polygon(Arc::from(points.into_boxed_slice()))
                )
            }
        }
    }
}

impl GameWeaponRange {
    fn into_native(self) -> NativeTurretRangeSpec {
        NativeTurretRangeSpec {
            offset: Vec2::new(self.offset[0], self.offset[1]),
            radius: self.radius, layer: self.layer, enabled: self.enabled,
        }
    }
}

impl NativePhysicalScene {
    /// A complete native game scene with no live Unity export. Its original
    /// data is a native JSON file and the verified immutable native collider
    /// catalogs. It follows precisely the same Level and CombatWorld builder
    /// as imported levels, including commands, rendering and pathfinding.
    pub fn parse_native_game_scene(
        json: &str,
        catalog: &ShipAssetCatalog,
        boxes: &BoxObstacleCatalog,
        polygons: &PolygonObstacleCatalog,
    ) -> Result<Self, NativeSnapshotError> {
        let raw: GameScene = serde_json::from_str(json)
            .map_err(NativeSnapshotError::Malformed)?;
        let invalid = NativeSnapshotError::InvalidNativeLayout;
        let min = Vec2::new(raw.bounds.min[0], raw.bounds.min[1]);
        let max = Vec2::new(raw.bounds.max[0], raw.bounds.max[1]);
        let span = max - min;
        if raw.native_game_schema != 1 ||
            raw.level_name.is_empty() || raw.stage_name.is_empty() ||
            !min.is_finite() || !max.is_finite() ||
            !span.is_finite() || span.x <= 0.0 || span.y <= 0.0 ||
            raw.bee_side <= 0 || raw.human_side <= 0 ||
            raw.bee_side == raw.human_side ||
            !raw.fixed_dt_seconds.is_finite() || raw.fixed_dt_seconds <= 0.0 ||
            raw.ships.is_empty() || raw.squads.is_empty()
        {
            return Err(invalid("invalid native game Level identity, bounds or fixed timestep"));
        }
        let policy_episode = raw.policy_episode;
        let has_agents = raw.ships.iter().any(|ship| ship.policy.is_some());
        if has_agents != policy_episode.is_some() {
            return Err(invalid("native policy ships require a complete MA-POCA episode and group source"));
        }
        if let Some(episode) = policy_episode {
            if episode.episode_number == 0 ||
                episode.bee_starting_tsv <= 0 || episode.human_starting_tsv <= 0 ||
                !episode.timeout_seconds.is_finite() || episode.timeout_seconds <= 0.0 ||
                episode.discovery_baselines.iter().flatten().any(|&value| value < 0) ||
                !raw.layout.clone_squads.is_empty() ||
                !raw.layout.remove_squads.is_empty() ||
                !raw.layout.translate_squads.is_empty()
            {
                return Err(invalid("native training requires exact positive reward baselines and fixed source policy squad membership"));
            }
        }
        let bounds = Aabb { min, max };
        let grid = NavigationGrid::new(span.x, span.y)
            .map_err(NativeSnapshotError::Navigation)?;
        // User-authored maps cannot allocate an unbounded pathfinding
        // workspace before a game or training worker becomes operational.
        if grid.cell_count() > 1_000_000 {
            return Err(invalid("native map exceeds one million navigation cells"));
        }
        let frame = NavigationMapFrame {
            origin_world: (min + max) * 0.5,
            ..NavigationMapFrame::IDENTITY
        };
        let settings = bees_gameplay::commands::HiveMindCommandSettings {
            max_command_seconds: raw.command_settings.max_command_seconds,
            ai_random_max_distance: raw.command_settings.ai_random_max_distance,
            ai_patrol_max_size: raw.command_settings.ai_patrol_max_size,
            ai_squad_patrol_seconds: raw.command_settings.ai_squad_patrol_seconds,
            ai_squad_following_seconds: raw.command_settings.ai_squad_following_seconds,
            ai_squad_guard_seconds: raw.command_settings.ai_squad_guard_seconds,
        };
        if !settings.valid() {
            return Err(invalid("invalid native game command timing and distance parameters"));
        }
        let camera_zoom = match raw.camera_zoom {
            Some([lo, hi]) if lo.is_finite() && hi.is_finite() &&
                lo > 0.0 && hi >= lo => Some((lo, hi)),
            Some(_) => return Err(invalid("invalid native game camera zoom limits")),
            None => None,
        };
        let starting_positions = raw.starting_positions.map(|values|
            [Vec2::new(values[0][0], values[0][1]),
             Vec2::new(values[1][0], values[1][1])]);
        if starting_positions.is_some_and(|positions|
            positions.iter().any(|pos| !pos.is_finite())
        ) || (raw.activate_hivemind && starting_positions.is_none()) ||
            raw.hivemind_initial_command_delay_seconds < 0
        {
            return Err(invalid("native HiveMind home coordinates or initial delay are invalid"));
        }

        let mut entities = Vec::with_capacity(raw.ships.len());
        let mut fleet = Vec::with_capacity(raw.ships.len());
        let mut authored_ship_perceptions = HashMap::new();
        let mut source_vision = HashMap::new();
        let mut source_formation = HashMap::new();
        let mut ship_sides = HashMap::new();
        let mut ship_types = HashMap::new();
        let mut persistent_fleet_ids = HashSet::new();
        let mut special_weapon_ids = HashSet::new();
        let mut carrier_children = Vec::new();
        let beehive_sprite = catalog.sprite_size(2)
            .ok_or(invalid("native Beehive sprite dimensions are missing"))?;
        // ConfigData.PixelsPerUnit = 8. The catalog exposes world units;
        // source healing eligibility uses integer sprite pixels.
        let beehive_pixels = [
            (beehive_sprite.x * 8.0).round() as u32,
            (beehive_sprite.y * 8.0).round() as u32,
        ];
        let mut ship_player_control = HashMap::new();
        let mut source_policy_bindings = HashMap::new();
        let mut source_policy_inputs = HashMap::new();
        let mut active_policy_ship_ids = Vec::new();
        let mut group_participants = Vec::new();
        let mut team_frames: [Option<u8>; 2] = [None, None];
        for ship in raw.ships {
            let id = ship.entity_id;
            let pos = Vec2::new(ship.position[0], ship.position[1]);
            let offset = Vec2::new(ship.formation_offset[0], ship.formation_offset[1]);
            let sprite = catalog.sprite_size(ship.ship_type)
                .ok_or(invalid("native ship type is not in the authoritative hull catalog"))?;
            if id <= 0 || id == i64::MAX ||
                ship_sides.insert(id, ship.side).is_some() ||
                !matches!(ship.side, side if side == raw.bee_side || side == raw.human_side) ||
                !pos.is_finite() || !offset.is_finite() ||
                pos.x < min.x || pos.x > max.x || pos.y < min.y || pos.y > max.y ||
                !ship.rotation_degrees.is_finite() ||
                !ship.rotation_speed_degrees_per_second.is_finite() ||
                ship.rotation_speed_degrees_per_second < 0.0 ||
                !ship.base_speed.is_finite() || ship.base_speed < 0.0 ||
                !ship.height.is_finite() || ship.height <= 0.0 ||
                !ship.size_class.is_finite() || ship.size_class < 0.0 ||
                !ship.front_offset.is_finite() ||
                !ship.max_weapon_range.is_finite() || ship.max_weapon_range < 0.0 ||
                !ship.firepower.is_finite() || ship.firepower < 0.0 ||
                ship.ship_type_letter >= 24 || ship.target_max_range < 0 ||
                ship.path_clearance < 0 || ship.original_health <= 0 ||
                ship.sight.source_sight < 0 || ship.sight.effective_range < 0 ||
                ship.sight.trigger_range < 0 ||
                ship.sight.enabled && ship.sight.trigger_range == 0 ||
                ship.sight.fog_radius.is_some_and(|v| !v.is_finite() || v < 0.0) ||
                !ship.combat_window_seconds.is_finite() ||
                ship.combat_window_seconds <= 0.0 ||
                (ship.is_carrier_ship && !matches!(ship.ship_type, 8 | 19)) ||
                ship.fleet_ship_id.is_some_and(|value| value <= 0) ||
                (ship.persistent_fleet && (ship.is_carrier_ship ||
                    ship.fleet_ship_id.is_none() ||
                    !persistent_fleet_ids.insert(ship.fleet_ship_id.expect("checked above")))) ||
                ship.policy.is_some() && (ship.user_controlled || ship.mined_cargo != 0) ||
                ship.drone_carrier_id.is_some_and(|carrier|
                    ship.ship_type != 8 || carrier <= 0 || carrier == id
                ) ||
                // Beacon spawns through a configured Scout and needs the
                // owning MotherSquad/vision/fleet identity, not an ordinary
                // free-standing ship registration. Factory has no extra
                // training physics beyond the shared mining implementation.
                ship.ship_type == 1
            {
                return Err(invalid("invalid native ordinary game ship or unsupported special hull"));
            }
            // All ordinary game specialists need the real Bomb weapon
            // identity and authored trigger radius. A bare ship type is
            // insufficient for command reservation or physical hits.
            let special = match &ship.special {
                Some(GameShipSpecial::Barge { power, can_override_bounds })
                    if ship.ship_type == 0 && *power >= 0 =>
                    Some(NativeAuthoredShipSpecial::Barge {
                        power: *power, can_override_bounds: Some(*can_override_bounds),
                    }),
                Some(GameShipSpecial::FireBarge { power })
                    if ship.ship_type == 10 && *power >= 0 =>
                    Some(NativeAuthoredShipSpecial::FireBarge { power: *power }),
                Some(GameShipSpecial::YellowJacket { power })
                    if ship.ship_type == 22 && *power >= 0 =>
                    Some(NativeAuthoredShipSpecial::YellowJacket { power: *power }),
                Some(GameShipSpecial::Striker {
                    power, carrier_id, offset_from_center, refill_distance,
                }) if ship.ship_type == 19 && *power >= 0 &&
                    carrier_id.is_none_or(|id| id > 0 && id != ship.entity_id) &&
                    Vec2::new(offset_from_center[0], offset_from_center[1]).is_finite() &&
                    refill_distance.is_finite() && *refill_distance > 0.0 =>
                    Some(NativeAuthoredShipSpecial::Striker {
                        power: *power,
                        carrier: GameStrikerCarrier {
                            carrier_id: *carrier_id,
                            offset_from_center: Vec2::new(
                                offset_from_center[0], offset_from_center[1],
                            ),
                            refill_distance: *refill_distance,
                        },
                    }),
                Some(GameShipSpecial::Queen { spawn }) if ship.ship_type == 17 =>
                    Some(spawn.into_native(catalog)?),
                Some(GameShipSpecial::Scout { enabled, beacon }) if ship.ship_type == 18 => {
                    if beacon.health <= 0 || beacon.original_health < beacon.health ||
                        beacon.max_tsv < 0 || !beacon.size_class.is_finite() ||
                        beacon.size_class < 0.0 || !beacon.base_speed.is_finite() ||
                        beacon.base_speed < 0.0 || !beacon.rotation_speed.is_finite() ||
                        beacon.rotation_speed < 0.0 || !beacon.height.is_finite() ||
                        beacon.height <= 0.0 || !beacon.longest_side.is_finite() ||
                        beacon.longest_side <= 0.0 || beacon.effective_vision_range < 0 ||
                        (beacon.hivemind_vision_enabled &&
                            (!beacon.hivemind_registered ||
                             beacon.effective_vision_range == 0)) ||
                        beacon.max_living_ships_per_side == Some(0) ||
                        !beacon.front_offset.is_finite() ||
                        beacon.ship_type_letter >= 24 ||
                        !beacon.firepower.is_finite() || beacon.firepower < 0.0 ||
                        !beacon.combat_window_seconds.is_finite() ||
                        beacon.combat_window_seconds <= 0.0
                    {
                        return Err(invalid("native Scout requires a valid authored Beacon prefab and vision source"));
                    }
                    let beacon_asset = catalog.by_type(BEACON_TYPE)
                        .ok_or(invalid("native Beacon collider asset is missing"))?.clone();
                    let physical = ScoutBeaconSpec {
                        combat_activity: Some(bees_sim::combat_world::NativeCombatActivity {
                            enabled: true, window_seconds: beacon.combat_window_seconds,
                            elapsed_seconds: None,
                        }),
                        beacon_asset,
                        beacon_vitals: ShipVitals::new(
                            beacon.health, beacon.health, beacon.max_tsv, 0,
                        ).map_err(NativeSnapshotError::Combat)?,
                        beacon_base_speed: beacon.base_speed,
                        beacon_rotation_speed: beacon.rotation_speed,
                        beacon_height: beacon.height,
                        beacon_original_health: beacon.original_health,
                        beacon_size_class: beacon.size_class,
                        targeting: Some(bees_sim::combat_world::NativeBeaconTargetSource {
                            front_offset: beacon.front_offset,
                            ship_type_letter: beacon.ship_type_letter,
                            firepower: beacon.firepower,
                        }),
                        max_living_ships_per_side: beacon.max_living_ships_per_side,
                    };
                    let template = NativeBeaconTemplate {
                        physical,
                        perception: AuthoredShipPerception {
                            longest_side: beacon.longest_side,
                            max_weapon_range: 0.0,
                            firepower: beacon.firepower,
                            is_bomber: false, is_carrier_ship: false,
                            minerals_mined_this_level: 0,
                            observed_weapons: [None; 5],
                            allied_communication: None,
                        },
                        vision: NativeSourceVision {
                            source_sight: Some(beacon.effective_vision_range),
                            effective_range: beacon.effective_vision_range,
                            trigger_range: beacon.effective_vision_range,
                            fog_radius: None,
                            registered: beacon.hivemind_registered,
                            enabled: beacon.hivemind_vision_enabled,
                        },
                    };
                    Some(NativeAuthoredShipSpecial::Scout {
                        enabled: *enabled,
                        beacon: Arc::new(template),
                    })
                },
                None if !matches!(ship.ship_type, 0 | 10 | 17 | 19 | 22 | 18) => None,
                _ => return Err(invalid("native special ship requires an exact matching ability and power")),
            };
            if ship.special_weapon.is_some() !=
                matches!(ship.ship_type, 0 | 10 | 19 | 22)
            {
                return Err(invalid("native Bomb specialist requires its authored physical weapon"));
            }
            let authored_power = match &special {
                Some(NativeAuthoredShipSpecial::Barge { power, .. }) |
                Some(NativeAuthoredShipSpecial::FireBarge { power }) |
                Some(NativeAuthoredShipSpecial::YellowJacket { power }) |
                Some(NativeAuthoredShipSpecial::Striker { power, .. }) => Some(*power),
                _ => None,
            };
            let mut bomb_observation = None;
            let special_weapon = if let Some(weapon) = ship.special_weapon {
                if ship.policy.is_some() && weapon.observation.is_none() {
                    return Err(invalid("native policy Bomb needs its authored weapon observation attributes"));
                }
                if let Some(observed) = weapon.observation {
                    if !observed.range.is_finite() || observed.range < 0.0 ||
                        !observed.rate_of_fire.is_finite() || observed.rate_of_fire < 0.0 ||
                        !observed.rotation_rate.is_finite() || observed.rotation_rate < 0.0 ||
                        !observed.projectile_value.is_finite() || observed.projectile_value < 0.0 ||
                        observed.projectile_speed.is_some_and(|v| !v.is_finite() || v < 0.0)
                    {
                        return Err(invalid("native Bomb observation attributes must be nonnegative and finite"));
                    }
                    bomb_observation = Some((weapon.slot, observed,
                        Vec2::new(weapon.mount_offset[0], weapon.mount_offset[1]),
                        weapon.power));
                }
                let charge = weapon.charge_range.map(GameWeaponRange::into_native);
                let proximity = weapon.proximity.map(GameWeaponRange::into_native);
                let mount_offset = Vec2::new(weapon.mount_offset[0], weapon.mount_offset[1]);
                if weapon.weapon_id <= 0 || weapon.weapon_id == i64::MAX ||
                    !special_weapon_ids.insert(weapon.weapon_id) ||
                    weapon.slot >= 5 || !mount_offset.is_finite() ||
                    Some(weapon.power) != authored_power ||
                    charge.is_some_and(|range| !range.valid()) ||
                    proximity.is_some_and(|range| !range.valid()) ||
                    (ship.ship_type == 0 && charge.is_none()) ||
                    (ship.ship_type != 0 && proximity.is_none())
                {
                    return Err(invalid("invalid authored native Bomb weapon identity, power or trigger geometry"));
                }
                Some(bees_sim::combat_world::NativeSpecialWeaponSpec {
                    weapon_id: weapon.weapon_id, slot: weapon.slot,
                    power: weapon.power, mount_offset,
                    charge_range: charge, proximity,
                })
            } else { None };
            let striker_carrier = match &special {
                Some(NativeAuthoredShipSpecial::Striker { carrier, .. }) => carrier.carrier_id,
                _ => None,
            };
            let parent_carrier_id = ship.drone_carrier_id.or(striker_carrier);
            if let Some(carrier_id) = parent_carrier_id {
                carrier_children.push((id, ship.side, carrier_id));
            }
            ship_types.insert(id, ship.ship_type);

            let support_zone = match ship.support_zone {
                Some(zone) => Some(zone.into_native()
                    .ok_or(invalid("native support collider has invalid authored geometry"))?),
                None => None,
            };
            if support_zone.is_some() != matches!(ship.ship_type, 2 | 20) {
                return Err(invalid("only Beehives and Warp Gates take exact support trigger colliders"));
            }

            // Policy capability eligibility matches the Unity source, but
            // executable masks, specials and cooldowns remain owned by the
            // one live CombatWorld, not copied into training metadata.
            let policy_capabilities = if ship.policy.is_some() {
                let healing = (ship.side == raw.bee_side &&
                    sprite.x < beehive_sprite.x && sprite.y < beehive_sprite.y)
                    .then_some(NativeHealingEligibility {
                        bee_side: raw.bee_side,
                        ship_size_pixels: [
                            (sprite.x * 8.0).round() as u32,
                            (sprite.y * 8.0).round() as u32,
                        ],
                        beehive_size_pixels: beehive_pixels,
                    });
                NativeAuthoredPolicyCapabilities {
                    mining: matches!(ship.ship_type, 4 | 9),
                    healing,
                    warp_human_side: (ship.side == raw.human_side &&
                        ship.ship_type != 20).then_some(raw.human_side),
                }
            } else { NativeAuthoredPolicyCapabilities::default() };
            let game_movement = GameMovementProfile {
                turning_radius: ship.turning_radius,
                half_width: sprite.x * 0.5,
                half_height: sprite.y * 0.5,
            };
            if !game_movement.valid() {
                return Err(invalid("invalid native ship movement profile"));
            }
            let vitals = ShipVitals::new(
                ship.health, ship.max_health, ship.max_tsv, ship.mined_cargo,
            ).map_err(NativeSnapshotError::Combat)?;
            if vitals.is_dead() {
                return Err(invalid("native initial ship must be alive"));
            }
            ship_player_control.insert(id, ship.user_controlled);
            let mut motion = ShipMotion::new(
                pos, ship.rotation_degrees,
                ship.rotation_speed_degrees_per_second, ship.base_speed,
                ship.height,
            );
            motion.user_controlled = ship.user_controlled;
            let mut observed_weapons = [None; 5];
            let mut self_weapons: [Option<SelfWeapon>; 5] = [None; 5];
            let mut turrets = Vec::with_capacity(ship.turrets.len());
            for turret in ship.turrets {
                let is_beam = matches!(&turret.fire_pattern, GameFirePattern::BeamCannon);
                if turret.slot >= 5 || observed_weapons[turret.slot].is_some() ||
                    !turret.projectile_speed.is_finite() ||
                    (is_beam && turret.projectile_speed != 0.0) ||
                    (!is_beam && turret.projectile_speed <= 0.0) ||
                    turret.range_collider.is_none() ||
                    turret.aim_at_front_of_ship.is_none() || turret.power < 0 ||
                    !turret.range.is_finite() || turret.range <= 0.0 ||
                    !turret.rate_of_fire.is_finite() || turret.rate_of_fire <= 0.0 ||
                    turret.weapon_type >= 10 ||
                    !turret.projectile_value.is_finite() || turret.projectile_value < 0.0 ||
                    !turret.rotation_rate.is_finite() || turret.rotation_rate < 0.0 ||
                    !turret.initial_rotation_degrees.is_finite()
                {
                    return Err(invalid("invalid native turret or duplicate mount slot"));
                }
                let range_collider = turret.range_collider.map(GameWeaponRange::into_native);
                if range_collider.is_some_and(|range| !range.valid()) {
                    return Err(invalid("invalid native turret child range collider"));
                }
                let slot = turret.slot;
                let spec = NativeTurretSpec {
                    weapon_type: turret.weapon_type,
                    projectile_type: turret.projectile_type,
                    mount_offset: Vec2::new(turret.mount_offset[0], turret.mount_offset[1]),
                    initial_rotation_degrees: turret.initial_rotation_degrees,
                    rotation_rate: turret.rotation_rate, range: turret.range,
                    power: turret.power, rate_of_fire: turret.rate_of_fire,
                    projectile_value: turret.projectile_value,
                    projectile_speed: turret.projectile_speed,
                    fire_pattern: turret.fire_pattern.into_native(),
                };
                if !spec.mount_offset.is_finite() {
                    return Err(invalid("nonfinite native turret mount"));
                }
                observed_weapons[slot] = Some(ObservedWeapon {
                    weapon_type: spec.weapon_type,
                    range: spec.range,
                    power: spec.power as f32,
                    rate_of_fire: spec.rate_of_fire,
                });
                // The source self-weapon fallback uses actual authored
                // muzzle geometry. After binding, the canonical physical
                // turret's live angle, cooldown and target status take over.
                let (sin, cos) = ship.rotation_degrees.to_radians().sin_cos();
                let offset = spec.mount_offset;
                let muzzle = pos + Vec2::new(
                    offset.x * cos - offset.y * sin,
                    offset.x * sin + offset.y * cos,
                );
                self_weapons[slot] = Some(SelfWeapon {
                    weapon_type: spec.weapon_type, position: muzzle,
                    range: spec.range, power: spec.power as f32,
                    rate_of_fire: spec.rate_of_fire,
                    rotation_rate: spec.rotation_rate,
                    projectile_value: spec.projectile_value,
                    projectile_speed: Some(spec.projectile_speed),
                    turret: Some(bees_sim::perception_weapons::TurretObservation {
                        rotation_degrees: spec.initial_rotation_degrees,
                        ready_to_fire: false, aligned_with_target: false,
                    }),
                    has_target_ship: false,
                });
                turrets.push(NativeAuthoredTurret {
                    slot, spec, range_collider,
                    aim_at_front_of_ship: turret.aim_at_front_of_ship,
                });
            }
            if let Some((slot, observed, offset, power)) = bomb_observation {
                if observed_weapons[slot].is_some() {
                    return Err(invalid("native Bomb slot collides with an ordinary turret slot"));
                }
                let (sin, cos) = ship.rotation_degrees.to_radians().sin_cos();
                let muzzle = pos + Vec2::new(
                    offset.x * cos - offset.y * sin,
                    offset.x * sin + offset.y * cos,
                );
                observed_weapons[slot] = Some(ObservedWeapon {
                    weapon_type: 0, range: observed.range,
                    power: power as f32, rate_of_fire: observed.rate_of_fire,
                });
                self_weapons[slot] = Some(SelfWeapon {
                    weapon_type: 0, position: muzzle,
                    range: observed.range, power: power as f32,
                    rate_of_fire: observed.rate_of_fire,
                    rotation_rate: observed.rotation_rate,
                    projectile_value: observed.projectile_value,
                    projectile_speed: observed.projectile_speed,
                    turret: None, has_target_ship: observed.has_target_ship,
                });
            }
            let perception = AuthoredShipPerception {
                longest_side: sprite.x.max(sprite.y),
                max_weapon_range: ship.max_weapon_range,
                firepower: ship.firepower,
                is_bomber: ship.is_bomber,
                is_carrier_ship: ship.is_carrier_ship,
                minerals_mined_this_level: ship.mined_cargo,
                observed_weapons,
                allied_communication: None,
            };
            if !perception.valid() {
                return Err(invalid("invalid native weapon/perception values"));
            }
            if raw.activate_fog_of_war && ship.sight.fog_radius.is_none() {
                return Err(invalid("fog-enabled native ships require an explicitly authored fog radius"));
            }
            authored_ship_perceptions.insert(id, perception);
            source_vision.insert(id, NativeSourceVision {
                source_sight: Some(ship.sight.source_sight),
                effective_range: ship.sight.effective_range,
                trigger_range: ship.sight.trigger_range,
                fog_radius: ship.sight.fog_radius,
                registered: true, enabled: ship.sight.enabled,
            });
            source_formation.insert(id, NativeSourceFormation {
                offset, clearance: ship.path_clearance,
            });
            fleet.push(NativeFleetShipValue {
                ship_id: id,
                has_persistent_fleet_value: ship.persistent_fleet,
                minerals_mined_this_level: ship.mined_cargo,
            });
            if let Some(policy) = ship.policy {
                let episode = policy_episode.expect("validated policy group episode");
                let expected_team = team_id_for_side(
                    raw.bee_side, raw.human_side, episode.episode_number, ship.side,
                ).map_err(|_| invalid("invalid native policy agent team side"))?;
                if policy.agent_id == 0 || policy.team_id != expected_team ||
                    policy.quarter_turns >= 4 ||
                    policy.movement_disabled !=
                        episode.movement_disabled_by_team[usize::from(expected_team)]
                {
                    return Err(invalid("native policy agent team, frame or movement flag does not match the authored group"));
                }
                let shared_frame = &mut team_frames[usize::from(expected_team)];
                if shared_frame.is_some_and(|frame| frame != policy.quarter_turns) {
                    return Err(invalid("same-team native agents require an identical policy coordinate frame"));
                }
                *shared_frame = Some(policy.quarter_turns);
                active_policy_ship_ids.push(id);
                group_participants.push(NativeSourceGroupParticipant {
                    ship_id: id, side: ship.side,
                    team_id: expected_team, agent_id: policy.agent_id,
                });
                source_policy_bindings.insert(id, NativeSourcePolicyBinding {
                    team_id: policy.team_id,
                    frame: PolicyFrame::new(policy.quarter_turns),
                    movement_disabled: policy.movement_disabled,
                    cease_fire: ship.cease_fire,
                });
                source_policy_inputs.insert(id, NativeSourcePolicyInputs {
                    controller: ControlledShipPerception {
                        effective_vision_range: ship.sight.effective_range as f32,
                        movement_disabled_for_team: policy.movement_disabled,
                        has_weapons: !turrets.is_empty() || special_weapon.is_some(),
                        has_turrets: !turrets.is_empty(),
                        has_special_action: special.as_ref().is_some_and(|ability|
                            !matches!(ability, NativeAuthoredShipSpecial::Queen { .. })),
                        special_ready: false,
                    },
                    capability: CapabilitySource {
                        special_resource_fraction: 0.0,
                        special_cooldown_fraction: 0.0,
                        special_phase: 0.0,
                        mining_possible: policy_capabilities.mining,
                        healing_possible: policy_capabilities.healing.is_some(),
                        warp_possible: policy_capabilities.warp_human_side.is_some(),
                        // Unity "ship is CarrierShip" is a class identity,
                        // not the mutable CarrierShip.Carrier reference.
                        // A standalone or orphaned Drone/Striker stays
                        // a CarrierShip when its parent is absent or dies.
                        is_carrier_child: matches!(ship.ship_type, 8 | 19),
                        has_living_parent_carrier: parent_carrier_id.is_some(),
                        ship_mined_tsv: 0, team_retainable_mined_tsv: 0,
                        side_starting_tsv: if ship.side == raw.bee_side {
                            episode.bee_starting_tsv
                        } else { episode.human_starting_tsv },
                    },
                    parent_carrier_id,
                    own_weapons: self_weapons,
                });
            }
            entities.push(NativeAuthoredEntity::Ship(NativeAuthoredShip {
                entity_id: id, ship_type: ship.ship_type, side: ship.side,
                vitals, motion, game_movement,
                original_health: ship.original_health, size_class: ship.size_class,
                healing_zone: if ship.ship_type == 2 { support_zone.clone() } else { None },
                warp_zone: if ship.ship_type == 20 { support_zone } else { None },
                policy_capabilities,
                fleet_ship_id: ship.fleet_ship_id.or_else(||
                    special.as_ref().map(|_| id)),
                policy: None, drone_carrier_id: ship.drone_carrier_id,
                cease_fire: ship.cease_fire, turrets, special,
                special_weapon,
                combat_activity: Some(bees_sim::combat_world::NativeCombatActivity {
                    enabled: true,
                    window_seconds: ship.combat_window_seconds,
                    elapsed_seconds: None,
                }),
                weapon_rank_source: Some(NativeWeaponRankSource {
                    stats: bees_sim::targeting::NativeEnemyTargetStats {
                        ship_id: id,
                        ship_type_letter: ship.ship_type_letter,
                        last_killed: 0, fleet_damage_done: 0,
                        firepower: ship.firepower, max_range: ship.target_max_range,
                        speed: ship.base_speed,
                    },
                    fleet_owner_ship_id: ship.fleet_ship_id.unwrap_or(id),
                }),
                target_geometry: Some(NativeShipTargetGeometry {
                    front_offset: ship.front_offset, bounds_latched: false,
                }),
            }));
        }

        if special_weapon_ids.iter().any(|id| ship_sides.contains_key(id)) ||
            carrier_children.iter().any(|&(_, side, carrier_id)|
                ship_types.get(&carrier_id) != Some(&5) ||
                ship_sides.get(&carrier_id) != Some(&side)
            )
        {
            return Err(invalid("native Bomb weapon identity collision or an invalid same-side Carrier child link"));
        }

        let mut world_ids = ship_sides.keys().copied().collect::<HashSet<_>>();
        world_ids.extend(special_weapon_ids.iter().copied());
        for resource in raw.mining_asteroids {
            let id = resource.entity_id;
            let position = Vec2::new(resource.position[0], resource.position[1]);
            let velocity = Vec2::new(resource.velocity[0], resource.velocity[1]);
            if id <= 0 || id == i64::MAX || !world_ids.insert(id) ||
                !position.is_finite() || !velocity.is_finite() ||
                !resource.rotation_degrees.is_finite() ||
                !resource.angular_velocity_degrees.is_finite() ||
                resource.layer >= 32 || resource.health <= 0 ||
                !(3..=8192).contains(&resource.local_vertices.len())
            {
                return Err(invalid("invalid native mining asteroid identity, health or pose"));
            }
            let vertices = resource.local_vertices.into_iter()
                .map(|p| Vec2::new(p[0], p[1])).collect::<Vec<_>>();
            if vertices.iter().any(|p| !p.is_finite()) {
                return Err(invalid("native mining asteroid has nonfinite collider vertices"));
            }
            let body = ObstacleBody::new(id,
                Pose { position, rotation_degrees: resource.rotation_degrees },
                resource.layer, ShipShape::Polygon(Arc::from(vertices.into_boxed_slice())),
                velocity, resource.angular_velocity_degrees,
            ).map_err(|_| invalid("invalid native mining asteroid physical collider"))?;
            let vitals = ObstacleVitals::new(resource.health, resource.health)
                .ok_or(invalid("invalid native resource health"))?;
            entities.push(NativeAuthoredEntity::MiningAsteroid {
                obstacle: CombatObstacle {
                    entity_id: id, kind: ObstacleKind::MiningAsteroid,
                    vitals: Some(vitals), size_class: 0,
                },
                body,
            });
        }
        for source in raw.map_objects {
            let id = source.entity_id;
            let position = Vec2::new(source.position[0], source.position[1]);
            if id <= 0 || id == i64::MAX || !world_ids.insert(id) ||
                !position.is_finite() || !source.rotation_degrees.is_finite() ||
                source.layer >= 32 || source.max_health <= 0
            {
                return Err(invalid("invalid native MapObject identity, health or pose"));
            }
            let shape = source.collider.into_native()
                .ok_or(invalid("native MapObject requires an exact valid collider"))?;
            let object = match source.kind {
                GameMapObjectKind::Generic if source.explosion_power.is_none() &&
                    source.linked_obstacle_id.is_none() =>
                    CombatMapObject::new(
                        id, bees_sim::map_object::MapObjectKind::Generic,
                        source.max_health,
                    ),
                GameMapObjectKind::FireTank => CombatMapObject::new_fire_tank(
                    id, source.max_health,
                    source.explosion_power.ok_or(invalid(
                        "Fire Tank requires its authored explosion power",
                    ))?,
                    source.linked_obstacle_id,
                ),
                _ => return Err(invalid("generic MapObject cannot have a Fire Tank blast or link")),
            }.map_err(|_| invalid("invalid native MapObject damage or explosion contract"))?;
            let body = ObstacleBody::new(
                id, Pose { position, rotation_degrees: source.rotation_degrees },
                source.layer, shape, Vec2::ZERO, 0.0,
            ).map_err(|_| invalid("invalid native MapObject collider"))?;
            entities.push(NativeAuthoredEntity::MapObject { object, body });
        }

        let mut source_squads = Vec::with_capacity(raw.squads.len());
        let mut assigned = HashSet::new();
        let mut squad_ids = HashSet::new();
        let mut has_player = false;
        for squad in raw.squads {
            if squad.item_id <= 0 || squad.item_id == i64::MAX ||
                !squad_ids.insert(squad.item_id) ||
                !matches!(squad.side, side if side == raw.bee_side || side == raw.human_side) ||
                squad.members.is_empty() || squad.members.len() > 1024 ||
                !(1..=40).contains(&squad.shooting_strategy_id) ||
                squad.hotkey_number.is_some() != squad.original_squad_count.is_some() ||
                squad.matching_speed.is_some_and(|value| !value.is_finite() || value < 0.0) ||
                squad.members.iter().any(|id|
                    !assigned.insert(*id) || ship_sides.get(id) != Some(&squad.side) ||
                    ship_player_control.get(id) != Some(&squad.user_controlled)
                ) || raw.activate_hivemind && squad.hivemind.is_none()
            {
                return Err(invalid("invalid native squad identity, membership or authority"));
            }
            let bans = squad.command_bans.into_iter()
                .map(bees_hivemind::dispatch::LocalCommandKind::from_server_strategy_id)
                .collect::<Option<Vec<_>>>()
                .ok_or(invalid("native command ban must be a registered strategy ID 1..15"))?;
            let hivemind = squad.hivemind.map(|source|
                bees_gameplay::level_host::NativeHiveMindSquadSource {
                    opponent_id: source.opponent_id,
                    hivemind_controlled: source.hivemind_controlled,
                    immobile: source.immobile,
                });
            if hivemind.is_some_and(|source| source.hivemind_controlled &&
                source.opponent_id == 0
            ) {
                return Err(invalid("native HiveMind-controlled squad needs an opponent identity"));
            }
            has_player |= squad.user_controlled;
            source_squads.push(NativeSourceSquad {
                item_id: squad.item_id, side: squad.side,
                members: squad.members,
                command_bans: Some(bans),
                is_carrier_squad: Some(squad.is_carrier_squad),
                hotkey_number: squad.hotkey_number,
                original_squad_count: squad.original_squad_count,
                hivemind,
                player_permissions: bees_gameplay::player_inputs::NativePlayerPermissions {
                    user_controlled: squad.user_controlled,
                    can_accept_input: squad.can_accept_input,
                    locked_on: squad.locked_on,
                },
                should_chase: squad.should_chase,
                matching_speed: squad.matching_speed,
                cease_fire: Some(squad.cease_fire),
                shooting: bees_hivemind::protocol::StrategyIdentity {
                    id: squad.shooting_strategy_id,
                    name: bees_hivemind::protocol::SHOOTING_STRATEGIES[
                        usize::from(squad.shooting_strategy_id - 1)
                    ],
                },
            });
        }
        if assigned.len() != ship_sides.len() {
            return Err(invalid("every native game ship must belong to exactly one squad"));
        }
        let agent_ids = group_participants.iter()
            .map(|member| member.agent_id).collect::<HashSet<_>>();
        if agent_ids.len() != group_participants.len() ||
            matches!(team_frames, [Some(left), Some(right)] if left == right)
        {
            return Err(invalid("native MA-POCA agents require unique IDs and distinct opposing policy frames"));
        }
        let episode_source = policy_episode.map(|episode| NativeSourceEpisode {
            bee_side: raw.bee_side, human_side: raw.human_side,
            bee_starting_tsv: episode.bee_starting_tsv,
            human_starting_tsv: episode.human_starting_tsv,
            timeout_seconds: episode.timeout_seconds,
            discovery_baselines: episode.discovery_baselines,
        });
        let group_source = policy_episode.map(|episode| NativeSourcePolicyGroup {
            episode_number: episode.episode_number,
            movement_disabled_by_team: episode.movement_disabled_by_team,
            participants: group_participants,
        });

        let mut scene = Self {
            targeting_context: Some(bees_gameplay::squad_targeting::NativeTargetingContext {
                has_player, user_side: raw.human_side,
            }),
            bee_side: raw.bee_side,
            human_side: raw.human_side,
            source_frame_count: Some(0),
            schema_version: SNAPSHOT_VERSION,
            level_name: raw.level_name,
            stage_name: raw.stage_name,
            bounds,
            starting_positions,
            fixed_dt_seconds: raw.fixed_dt_seconds,
            shots_harmless: raw.shots_harmless,
            activate_collision_asteroids: raw.activate_collision_asteroids,
            activate_mining: Some(raw.activate_mining),
            activate_fog_of_war: Some(raw.activate_fog_of_war),
            camera_zoom,
            camera_edge_scrolling: Some(raw.camera_edge_scrolling),
            activate_hivemind: Some(raw.activate_hivemind),
            hivemind_initial_command_delay_seconds:
                Some(raw.hivemind_initial_command_delay_seconds),
            has_obstacles: false,
            command_settings: settings,
            entities,
            // Apply explicitly authored initial static layout before
            // installing immutable reward baselines and PPO membership.
            source_episode: None,
            source_policy_group: None,
            source_navigation: NativeSourceNavigation {
                map_width: span.x, map_height: span.y, frame,
            },
            fleet: NativeFleetInventory::from_source(fleet)
                .map_err(NativeSnapshotError::Fleet)?,
            authored_ship_perceptions,
            active_policy_ship_ids,
            source_squads,
            source_policy_bindings,
            source_policy_inputs,
            source_vision,
            source_static_discovery_values: HashMap::new(),
            source_formation,
        };
        scene.apply_native_layout(&raw.layout, boxes, polygons)?;
        // Discovery denominators are fixed at episode start by the Unity
        // coordinator, never by how many objects happen to be visible after
        // physics advances. This scene owns all its initial physical entities
        // and source-authored static values, so independently validate the
        // supplied frozen reward contract instead of training on arbitrary
        // normalization numbers.
        if let Some(episode) = policy_episode {
            let mut fleet_tsv = [0_i32; 2];
            let mut mining = 0_i32;
            let mut map_objects = 0_i32;
            for entity in &scene.entities {
                match entity {
                    NativeAuthoredEntity::Ship(ship) if scene.fleet.get(ship.entity_id)
                        .is_some_and(|value| value.has_persistent_fleet_value) =>
                    {
                        let side = usize::from(ship.side != scene.bee_side);
                        fleet_tsv[side] = fleet_tsv[side].checked_add(ship.vitals.tsv().max(1))
                            .ok_or(invalid("native persistent fleet discovery TSV overflow"))?;
                    }
                    NativeAuthoredEntity::MiningAsteroid { obstacle, .. } => {
                        let vitals = obstacle.vitals.ok_or(invalid(
                            "native mining asteroid is missing authored health",
                        ))?;
                        mining = mining.checked_add(vitals.original_health.max(1))
                            .ok_or(invalid("native mining discovery TSV overflow"))?;
                    }
                    NativeAuthoredEntity::MapObject { object, .. } => {
                        map_objects = map_objects.checked_add(object.max_health.max(1))
                            .ok_or(invalid("native map object discovery TSV overflow"))?;
                    }
                    _ => {}
                }
            }
            let statics = scene.source_static_discovery_values.values()
                .try_fold(0_i32, |total, &value|
                    total.checked_add(value.max(1)).ok_or(invalid(
                        "native static obstacle discovery TSV overflow",
                    ))
                )?;
            // Unity LevelConstructor captures InitialTsv from ordinary
            // squads BEFORE it spawns CarrierShip children. Temporary
            // Drones/Strikers must not enlarge either side's starting
            // health/economic reward denominator. This is the same
            // persistent fleet used for enemy-discovery normalization.
            if episode.bee_starting_tsv != fleet_tsv[0] ||
                episode.human_starting_tsv != fleet_tsv[1] {
                return Err(invalid(
                    "native policy starting TSV must equal each side's initial persistent fleet",
                ));
            }
            let expected = [
                [fleet_tsv[1], mining, statics, map_objects],
                [fleet_tsv[0], mining, statics, map_objects],
            ];
            if episode.discovery_baselines != expected {
                return Err(invalid(
                    "native policy discovery denominators disagree with authored initial physical fleet and obstacles",
                ));
            }
        }
        // A Fire Tank may destroy only an actual static obstacle in the
        // *final* authored layout. Validate after optional removals and
        // additions, before any world or policy agent has started.
        let static_ids = scene.entities.iter().filter_map(|entity| match entity {
            NativeAuthoredEntity::BoxObstacle { entity_id, .. } |
            NativeAuthoredEntity::PolygonObstacle { entity_id, .. } =>
                Some(*entity_id),
            _ => None,
        }).collect::<HashSet<_>>();
        if scene.entities.iter().any(|entity| match entity {
            NativeAuthoredEntity::MapObject { object, .. } =>
                object.fire_tank.and_then(|tank| tank.target_obstacle_id)
                    .is_some_and(|id| !static_ids.contains(&id)),
            _ => false,
        }) {
            return Err(invalid("native Fire Tank linked static obstacle is absent from final Level"));
        }
        scene.source_episode = episode_source;
        scene.source_policy_group = group_source;
        Ok(scene)
    }
}
