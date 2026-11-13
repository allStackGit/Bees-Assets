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
    #[serde(default)]
    policy: Option<GamePolicyAgent>,
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
struct GameSpecialWeapon {
    weapon_id: i64,
    slot: usize,
    power: i32,
    mount_offset: [f32; 2],
    #[serde(default)]
    charge_range: Option<GameWeaponRange>,
    #[serde(default)]
    proximity: Option<GameWeaponRange>,
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
        let mut special_weapon_ids = HashSet::new();
        let mut striker_carriers = Vec::new();
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
                (ship.is_carrier_ship && ship.ship_type != 5) ||
                ship.policy.is_some() && (ship.user_controlled || ship.mined_cargo != 0) ||
                // Beacon/Heal/Warp/Scout and other special controllers must
                // first have their complete source-owned trigger data.
                matches!(ship.ship_type, 1 | 2 | 9 | 20)
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
                None if !matches!(ship.ship_type, 0 | 10 | 19 | 22 | 18) => None,
                _ => return Err(invalid("native special ship requires an exact matching ability and power")),
            };
            if (ship.policy.is_some() && special.is_some()) ||
                ship.special_weapon.is_some() !=
                    matches!(ship.ship_type, 0 | 10 | 19 | 22)
            {
                return Err(invalid("native game special requires its weapon; special policy abilities are not yet independently authored"));
            }
            let authored_power = match &special {
                Some(NativeAuthoredShipSpecial::Barge { power, .. }) |
                Some(NativeAuthoredShipSpecial::FireBarge { power }) |
                Some(NativeAuthoredShipSpecial::YellowJacket { power }) |
                Some(NativeAuthoredShipSpecial::Striker { power, .. }) => Some(*power),
                _ => None,
            };
            let special_weapon = if let Some(weapon) = ship.special_weapon {
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
            if let Some(NativeAuthoredShipSpecial::Striker { carrier, .. }) = &special {
                if let Some(carrier_id) = carrier.carrier_id {
                    striker_carriers.push((ship.entity_id, ship.side, carrier_id));
                }
            }
            ship_types.insert(id, ship.ship_type);

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
                has_persistent_fleet_value: false,
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
                        has_weapons: !turrets.is_empty(), has_turrets: !turrets.is_empty(),
                        has_special_action: false, special_ready: false,
                    },
                    capability: CapabilitySource {
                        special_resource_fraction: 0.0,
                        special_cooldown_fraction: 0.0,
                        special_phase: 0.0,
                        mining_possible: false, healing_possible: false,
                        warp_possible: false, is_carrier_child: false,
                        has_living_parent_carrier: false,
                        ship_mined_tsv: 0, team_retainable_mined_tsv: 0,
                        side_starting_tsv: if ship.side == raw.bee_side {
                            episode.bee_starting_tsv
                        } else { episode.human_starting_tsv },
                    },
                    parent_carrier_id: None,
                    own_weapons: self_weapons,
                });
            }
            entities.push(NativeAuthoredEntity::Ship(NativeAuthoredShip {
                entity_id: id, ship_type: ship.ship_type, side: ship.side,
                vitals, motion, game_movement,
                original_health: ship.original_health, size_class: ship.size_class,
                healing_zone: None, warp_zone: None,
                policy_capabilities: NativeAuthoredPolicyCapabilities::default(),
                fleet_ship_id: special.as_ref().map(|_| id),
                policy: None, drone_carrier_id: None,
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
                    fleet_owner_ship_id: id,
                }),
                target_geometry: Some(NativeShipTargetGeometry {
                    front_offset: ship.front_offset, bounds_latched: false,
                }),
            }));
        }

        if special_weapon_ids.iter().any(|id| ship_sides.contains_key(id)) ||
            striker_carriers.iter().any(|&(_, side, carrier_id)|
                ship_types.get(&carrier_id) != Some(&5) ||
                ship_sides.get(&carrier_id) != Some(&side)
            )
        {
            return Err(invalid("native Bomb weapon identities collide with ships or a Striker links to a non-Carrier on another side"));
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
        scene.source_episode = episode_source;
        scene.source_policy_group = group_source;
        Ok(scene)
    }
}
