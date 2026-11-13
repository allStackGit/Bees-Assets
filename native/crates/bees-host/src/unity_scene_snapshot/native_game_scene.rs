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
        let mut ship_player_control = HashMap::new();
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
                ship.is_bomber || ship.is_carrier_ship ||
                // Special lifecycle/controllers need fully authored native
                // relationship specifications, not missing Unity components.
                matches!(ship.ship_type, 0 | 1 | 2 | 5 | 9 | 10 | 18 | 19 | 20 | 22)
            {
                return Err(invalid("invalid native ordinary game ship or unsupported special hull"));
            }
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
                let range_collider = turret.range_collider.map(|range|
                    NativeTurretRangeSpec {
                        offset: Vec2::new(range.offset[0], range.offset[1]),
                        radius: range.radius, layer: range.layer, enabled: range.enabled,
                    });
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
            entities.push(NativeAuthoredEntity::Ship(NativeAuthoredShip {
                entity_id: id, ship_type: ship.ship_type, side: ship.side,
                vitals, motion, game_movement,
                original_health: ship.original_health, size_class: ship.size_class,
                healing_zone: None, warp_zone: None,
                policy_capabilities: NativeAuthoredPolicyCapabilities::default(),
                fleet_ship_id: None, policy: None, drone_carrier_id: None,
                cease_fire: ship.cease_fire, turrets, special: None,
                special_weapon: None,
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
                is_carrier_squad: Some(false),
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
            source_episode: None,
            source_policy_group: None,
            source_navigation: NativeSourceNavigation {
                map_width: span.x, map_height: span.y, frame,
            },
            fleet: NativeFleetInventory::from_source(fleet)
                .map_err(NativeSnapshotError::Fleet)?,
            authored_ship_perceptions,
            active_policy_ship_ids: Vec::new(),
            source_squads,
            source_policy_bindings: HashMap::new(),
            source_policy_inputs: HashMap::new(),
            source_vision,
            source_static_discovery_values: HashMap::new(),
            source_formation,
        };
        scene.apply_native_layout(&raw.layout, boxes, polygons)?;
        Ok(scene)
    }
}
