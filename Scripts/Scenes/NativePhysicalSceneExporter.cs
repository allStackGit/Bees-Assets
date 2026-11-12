#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Levels;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Export SOURCE-owned physical ship/obstacle inputs from a live Unity Level.
/// This is NOT a full episode checkpoint: learned visibility, projectile
/// history, existing commands, squad lifecycle tokens, trained-policy state,
/// and reward progress are deliberately not synthesized here.
/// </summary>
internal static class NativePhysicalSceneExporter
{
    [Serializable]
    private sealed class SourceShip
    {
        public SourceCombatActivity combat_activity;
        public SourceWeaponRank weapon_rank_source;
        public SourceSpecialWeapon special_weapon;
        public SourceShipTargetGeometry target_geometry;
        public long entity_id;
        public int ship_type;
        public int side;
        public int health, max_health, max_tsv, current_tsv;
        public int mined_cargo, original_health;
        public float size_class;
        public Vector2Int size_pixels;
        public bool has_healing_zone, has_warp_zone;
        public SourceCollider healing_zone = new SourceCollider();
        public SourceCollider warp_zone = new SourceCollider();
        public SourceShipSpecial special = new SourceShipSpecial();
        public long fleet_ship_id;
        public bool persistent_fleet;
        public Vector2 position, body_velocity, target_coordinates;
        public float rotation_degrees, rotation_speed;
        public float base_speed, current_speed, height;
        public float turning_radius, half_width, half_height;
        public Vector2 offset_from_center;
        public int path_clearance;
        public bool mobile, cannot_change_orders, user_controlled;
        public bool has_target_coordinates, is_rl_policy_controlled, is_moving;
        public int movement_direction;
        public float longest_side, max_weapon_range, firepower;
        public bool is_bomber, is_carrier_ship, has_communication;
        public bool has_bound_policy, policy_movement_disabled, policy_cease_fire;
        public int policy_team_id, policy_quarter_turns;
        // Frozen source v26 controller/capability observations.
        // Model slot remains a separately authenticated host decision.
        public int source_sight, effective_vision_range;
        public bool has_weapons, has_turrets, has_special_action, special_ready;
        public float special_resource_fraction, special_cooldown_fraction, special_phase;
        public bool mining_possible, healing_possible, warp_possible;
        public bool is_carrier_child, has_living_parent_carrier;
        public long parent_carrier_id;
        public int team_retainable_mined_tsv, side_starting_tsv;
        public bool hivemind_registered, hivemind_vision_enabled;
        public int hivemind_sight_range;
        public List<SourceSelfWeapon> self_weapons = new List<SourceSelfWeapon>();
        public Vector4 communication;
        public List<SourceObservedWeapon> observed_weapons =
            new List<SourceObservedWeapon>();
        public List<SourceTurret> turrets = new List<SourceTurret>();
    }

    [Serializable]
    private sealed class SourceCollider
    {
        public string kind = "";
        public List<Vector2> vertices = new List<Vector2>();
        public Vector2 offset;
        public float radius;
    }

    [Serializable]
    private sealed class SourceShipSpecial
    {
        public string kind = "none";
        public int power;
        // Ship.CanOverrideBounds belongs to the authored Barge, not a
        // guessed Level-wide mobility default.
        public bool can_override_bounds;
        public long carrier_id;
        public Vector2 offset_from_center;
        public float refill_distance;
        public bool scout_enabled;
        public SourceBeaconTemplate beacon = new SourceBeaconTemplate();
    }

    [Serializable]
    private sealed class SourceBeaconTemplate
    {
        public SourceCombatActivity combat_activity;
        public SourceBeaconTarget targeting;
        public int original_health, health, max_tsv, effective_vision_range;
        public float size_class, base_speed, rotation_speed, height, longest_side;
        public bool hivemind_registered, hivemind_vision_enabled;
        // Zero is the ordinary game with no training population cap.
        public int max_living_ships_per_side;
    }

    [Serializable]
    private sealed class SourceBeaconTarget
    {
        public float front_offset, firepower;
        public int ship_type_letter;
    }

    [Serializable]
    private sealed class SourceSelfWeapon
    {
        public int slot, weapon_type;
        public Vector2 position;
        public float range, power, rate_of_fire, rotation_rate, projectile_value;
        public double projectile_speed;
        public bool has_projectile_speed, has_target_ship;
    }

    [Serializable]
    private sealed class SourceObservedWeapon
    {
        public int slot, weapon_type;
        public float range, power, rate_of_fire;
    }

    [Serializable]
    private sealed class SourceWeaponRank
    {
        public long fleet_owner_ship_id;
        public int fleet_damage_done;
        public int ship_type_letter, last_killed, max_range;
        public float firepower, speed;
    }

    [Serializable]
    private sealed class SourceTrigger
    {
        public SourceCollider collider;
        public int layer;
        public bool enabled;
    }

    [Serializable]
    private sealed class SourceSpecialWeapon
    {
        public int weapon_id, power, slot;
        public Vector2 mount_offset;
        public SourceTrigger charge_range, proximity;
    }

    [Serializable]
    private sealed class SourceShipTargetGeometry
    {
        public float front_offset;
        public bool bounds_latched;
    }

    [Serializable]
    private sealed class SourceTurret
    {
        public SourceCollider range_collider;
        public bool aim_at_front_of_ship;
        public int range_layer;
        public bool range_enabled;
        public int slot;
        public int weapon_type, projectile_type, power;
        public Vector2 mount_offset, left_offset, right_offset;
        public float initial_rotation_degrees, rotation_rate, range;
        public float rate_of_fire, projectile_value;
        public double projectile_speed;
        public string fire_pattern;
    }

    [Serializable]
    private sealed class SourceObstacle
    {
        public string kind;
        public long entity_id;
        public string prefab_name;
        public int prefab_number;
        public int speed, health, effective_max_health;
        public int layer;
        public bool is_shard;
        public Vector2 position, velocity;
        public float rotation_degrees, angular_velocity_degrees;
        public List<Vector2> collider_vertices = new List<Vector2>();
    }

    [Serializable]
    private sealed class SourceMapObject
    {
        public long entity_id, linked_obstacle_id;
        public string kind, collider_kind;
        public int layer, health, max_health, explosion_power;
        public Vector2 position, linear_velocity, circle_offset;
        public float rotation_degrees, angular_velocity_degrees, circle_radius;
        public List<Vector2> collider_vertices = new List<Vector2>();
    }

    [Serializable]
    private sealed class SourceSquad
    {
        // ItemId is the runtime ID allocated by Stage.Pool.ItemCount++ at
        // every Squad.Setup, including reuse of a pooled Squad wrapper.
        public long item_id;
        public int side;
        public int squad_number;
        public bool is_minion_squad;
        public List<long> member_ids = new List<long>();
        public bool user_controlled, can_accept_input, locked_on, is_carrier_squad;
        // Source Squad.Setup sets this separately from any active command.
        public bool should_chase;
        public bool is_hivemind_controlled, is_immobile;
        // Preserve ulong exactly; JsonUtility's numeric roundtrips can
        // lose 64-bit client identity through JavaScript intermediaries.
        public string opponent_id_decimal;
        public List<string> banned_strategies = new List<string>();
        public string shooting_strategy;
    }

    [Serializable]
    private sealed class SourcePolicyEpisode
    {
        public int bee_side, human_side, bee_starting_tsv, human_starting_tsv;
        public int episode_number;
        public bool team0_movement_disabled, team1_movement_disabled;
        public float timeout_seconds;
        public int[] discovery_baselines;
        public List<SourceGroupParticipant> group_participants =
            new List<SourceGroupParticipant>();
    }

    [Serializable]
    private sealed class SourceGroupParticipant
    {
        // Active Unity Agent instance, NOT the runtime ship it controls.
        public long agent_id, ship_id;
        public int side, team_id;
    }

    [Serializable]
    private sealed class SourceCommandSettings
    {
        public float max_command_seconds, ai_random_max_distance, ai_patrol_max_size;
        public float ai_squad_patrol_seconds, ai_squad_following_seconds, ai_squad_guard_seconds;
    }

    [Serializable]
    private sealed class SourceCombatActivity
    {
        public bool enabled, active;
        public float window_seconds, elapsed_seconds;
    }
    [Serializable]
    private sealed class SourceTargetingContext
    {
        public bool has_player;
        public int user_side;
    }

    [Serializable]
    private sealed class PhysicalScene
    {
        public int schema_version = 31;
        public SourceTargetingContext targeting_context;
        public int source_frame_count = Time.frameCount;
        public string level_name;
        public string stage_name;
        public float fixed_dt_seconds;
        public bool shots_harmless;
        public bool activate_collision_asteroids;
        public bool activate_mining;
        public bool activate_hivemind;
        public int hivemind_initial_command_delay_seconds;
        public bool has_obstacles;
        public SourceCommandSettings command_settings;
        public int bee_side, human_side;
        public Vector2Int beehive_size_pixels;
        public bool has_policy_episode;
        public SourcePolicyEpisode policy_episode = new SourcePolicyEpisode();
        public Vector2 min, max;
        // Source Level.StartingPositions[Side-1] for a squad returning
        // in bounds before requesting its next HiveMind target.
        public Vector2[] starting_positions;
        // Counts survive original squad deaths, unlike the live roster.
        // Unity's 1-0 hotkeys wrap against this original side-specific count.
        public int[] original_squad_counts;
        // Exact Pathfinder grid dimensions and map-to-Unity affine frame,
        // not approximations from gameplay ForceBounds or broadphase.
        public int navigation_map_width, navigation_map_height;
        public Vector2 navigation_origin_world;
        public Vector2 navigation_x_axis_world, navigation_y_axis_world;
        public List<SourceShip> ships = new List<SourceShip>();
        public List<SourceSquad> squads = new List<SourceSquad>();
        public List<SourceObstacle> obstacles = new List<SourceObstacle>();
        public List<SourceMapObject> map_objects = new List<SourceMapObject>();
    }

    [MenuItem("Bees/Native/Export Selected Level Physical Scene")]
    private static void Export()
    {
        var selected = Selection.activeGameObject;
        var level = selected == null ? null : selected.GetComponent<Level>();
        if (level == null || level.Stage == null || level.State == null ||
            ConfigData.Configuration == null ||
            !IsFinite(level.MinX) || !IsFinite(level.MaxX) ||
            !IsFinite(level.MinY) || !IsFinite(level.MaxY) ||
            level.MinX >= level.MaxX || level.MinY >= level.MaxY ||
            !IsFinite(level.Stage.FixedDeltaTime) ||
            level.Stage.FixedDeltaTime <= 0 ||
            level.Map == null || level.Map.Transform == null)
        {
            throw new InvalidOperationException(
                "Select a fully initialized live Level with valid source bounds.");
        }

        if (level.StartingPositions == null ||
            level.StartingPositions.Length != 2 ||
            !IsFinite(level.StartingPositions[0].x) ||
            !IsFinite(level.StartingPositions[0].y) ||
            !IsFinite(level.StartingPositions[1].x) ||
            !IsFinite(level.StartingPositions[1].y))
        {
            throw new InvalidOperationException(
                "Native source squad return positions must be finite and complete.");
        }

        var snapshot = new PhysicalScene {
            targeting_context = new SourceTargetingContext { has_player = level.HasPlayer, user_side = ConfigData.Configuration.UserSide },
            level_name = !string.IsNullOrEmpty(level.Name)
                ? level.Name : level.gameObject.name,
            fixed_dt_seconds = level.Stage.FixedDeltaTime,
            stage_name = level.Stage.gameObject.name,
            shots_harmless = level.Stage.MakeShotsHarmless,
            activate_collision_asteroids = level.ActivateCollisionAsteroids,
            activate_mining = level.ActivateMining,
            activate_hivemind = level.Stage.ActivateHiveMind,
            hivemind_initial_command_delay_seconds = level.Stage.InitialCommandDelay,
            has_obstacles = level.HasObstacles,
            command_settings = new SourceCommandSettings {
                max_command_seconds = ConfigData.StandardMaxCommandTime,
                ai_random_max_distance = ConfigData.Configuration.AIRandomMovementMaxDistance,
                ai_patrol_max_size = ConfigData.Configuration.AIPatrolMaxSize,
                ai_squad_patrol_seconds = ConfigData.Configuration.AISquadPatrolTime,
                ai_squad_following_seconds = ConfigData.Configuration.AISquadFollowingTime,
                ai_squad_guard_seconds = ConfigData.Configuration.AISquadGuardTime
            },
            bee_side = ConfigData.Configuration.BeeSide,
            human_side = ConfigData.Configuration.HumanSide,
            beehive_size_pixels = ConfigData.ShipSizes[ConfigData.ShipTypes.Beehive],
            min = new Vector2(level.MinX, level.MinY),
            max = new Vector2(level.MaxX, level.MaxY),
            starting_positions = (Vector2[])level.StartingPositions.Clone(),
            original_squad_counts = (int[])level.State.OriginalSquadCounts.Clone(),
            navigation_map_width = level.MapWidth,
            navigation_map_height = level.MapHeight,
            navigation_origin_world = level.Map.Transform.TransformPoint(
                Vector3.zero),
            navigation_x_axis_world =
                level.Map.Transform.TransformPoint(Vector3.right) -
                level.Map.Transform.TransformPoint(Vector3.zero),
            navigation_y_axis_world =
                level.Map.Transform.TransformPoint(Vector3.up) -
                level.Map.Transform.TransformPoint(Vector3.zero)
        };

        if (snapshot.original_squad_counts == null ||
            snapshot.original_squad_counts.Length != 2 ||
            snapshot.original_squad_counts[0] < 0 ||
            snapshot.original_squad_counts[1] < 0)
        {
            throw new InvalidOperationException(
                "Native squad hotkeys require the source's original side counts.");
        }

        if (snapshot.navigation_map_width <= 0 ||
            snapshot.navigation_map_height <= 0 ||
            !IsFinite(snapshot.navigation_origin_world.x) ||
            !IsFinite(snapshot.navigation_origin_world.y) ||
            !IsFinite(snapshot.navigation_x_axis_world.x) ||
            !IsFinite(snapshot.navigation_x_axis_world.y) ||
            !IsFinite(snapshot.navigation_y_axis_world.x) ||
            !IsFinite(snapshot.navigation_y_axis_world.y))
        {
            throw new InvalidOperationException(
                "Source Pathfinder map size or affine frame is invalid.");
        }

        if (RlOneVsOneEpisodeCoordinator.TryReadNativeInitialEpisode(
                level,
                out int beeSide, out int humanSide,
                out int beeStartingTsv, out int humanStartingTsv,
                out float timeoutSeconds, out int[] discoveryBaselines))
        {
            snapshot.has_policy_episode = true;
            snapshot.policy_episode = new SourcePolicyEpisode {
                bee_side = beeSide, human_side = humanSide,
                bee_starting_tsv = beeStartingTsv,
                human_starting_tsv = humanStartingTsv,
                timeout_seconds = timeoutSeconds,
                discovery_baselines = discoveryBaselines,
            };
        }

        if (string.IsNullOrWhiteSpace(snapshot.stage_name))
            throw new InvalidOperationException(
                "Native scene must retain the original Stage identity.");

        foreach (Ship ship in level.State.Ships)
        {
            if (ship == null || ship.IsDead) continue;
            if (ship.Body == null || ship.FleetShip == null ||
                ship.Health <= 0 || ship.MaxHealth <= 0 ||
                ship.OriginalHealth <= 0 || ship.Id <= 0)
            {
                throw new InvalidOperationException(
                    "Cannot export an incomplete source ship: " +
                    (ship == null ? "<null>" : ship.name));
            }
            var fleet = ship.FleetShip;
            if (!IsFinite(ship.SizeClass) || ship.SizeClass < 0f)
                throw new InvalidOperationException("Invalid source SizeClass on ship " + ship.Id);
            Collider2D healingZone = (ship as Beehive)?.HealCollider;
            Collider2D warpZone = (ship as WarpGate)?.WarpCollider;
            if ((ship is Beehive && healingZone == null) ||
                (ship is WarpGate && warpZone == null))
                throw new InvalidOperationException("Missing authored support collider on ship " + ship.Id);
            bool hasCommunication = RlOneVsOneAgent.TryReadCommunication(
                ship, out Vector4 communication);
            bool hasBoundPolicy = RlOneVsOneAgent.TryReadBoundPolicy(
                ship, out int policyTeamId,
                out int policyQuarterTurns, out bool movementDisabled);
            // Read only the real current policy controller. Do not request
            // a new frame or start the episode merely for export.
            CarrierShip carrierChild = ship as CarrierShip;
            bool hasParent = carrierChild != null &&
                carrierChild.Carrier != null && !carrierChild.Carrier.IsDead;
            if (hasBoundPolicy &&
                (ship.Side < 1 || ship.Side > level.State.InitialTsv.Length ||
                 level.State.InitialTsv[ship.Side - 1] <= 0))
            {
                throw new InvalidOperationException(
                    "Bound policy ship has no configured source episode TSV: " + ship.Id);
            }
            if ((!ship.IsRlPolicyControlled || ship.IsUserControlled) &&
                (ship.HasTargetCoordinates || ship.IsMoving))
            {
                throw new InvalidOperationException(
                    "Export a fresh scene before game movement starts; active routes are not checkpoints: " + ship.Id);
            }
            float turningRadius = ConfigData.ShipTurningRadius;
            float halfWidth = ship.GetHalfWidth();
            float halfHeight = ship.GetHalfHeight();
            if (!IsFinite(turningRadius) || turningRadius <= 0f ||
                !IsFinite(halfWidth) || halfWidth <= 0f ||
                !IsFinite(halfHeight) || halfHeight <= 0f)
                throw new InvalidOperationException(
                    "Cannot export source game steering profile for ship: " + ship.Id);
            snapshot.ships.Add(new SourceShip {
                combat_activity = new SourceCombatActivity {
                    enabled = ship.Stage.ActivateHiveMind, active = ship.InCombat,
                    window_seconds = ship.CombatActivityWindowSeconds,
                    elapsed_seconds = ship.CombatActivityElapsedSeconds,
                },
                entity_id = ship.Id,
                ship_type = (int)ship.ShipType,
                side = ship.Side,
                health = ship.Health,
                max_health = ship.MaxHealth,
                max_tsv = ship.OriginalTsv,
                current_tsv = ship.Tsv,
                mined_cargo = fleet.MineralsMinedThisLevel,
                original_health = ship.OriginalHealth,
                size_class = ship.SizeClass,
                size_pixels = ConfigData.ShipSizes[ship.ShipType],
                has_healing_zone = healingZone != null && healingZone.isActiveAndEnabled,
                has_warp_zone = warpZone != null && warpZone.isActiveAndEnabled,
                healing_zone = ExportShipSupportZone(level, ship, healingZone),
                warp_zone = ExportShipSupportZone(level, ship, warpZone),
                special = ExportShipSpecial(ship),
                fleet_ship_id = fleet.Id,
                persistent_fleet = fleet.Id > 0 &&
                    !ship.IsMinionShip && !ship.IsCarrierShip,
                position = ship.GetPosition(),
                rotation_degrees = ship.Rotation,
                rotation_speed = ship.RotationSpeed,
                base_speed = ship.Speed,
                current_speed = ship.CurrentSpeed,
                body_velocity = ship.Body.linearVelocity,
                weapon_rank_source = new SourceWeaponRank {
                    fleet_owner_ship_id = FindRankFleetOwner(ship),
                    ship_type_letter = (int)ship.ShipTypeLetter,
                    last_killed = ship.LastKilled,
                    fleet_damage_done = ship.FleetShip.DamageDone,
                    firepower = ship.Firepower,
                    max_range = ship.MaxRange,
                    speed = ship.Speed,
                },
                special_weapon = ExportSpecialWeapon(ship),
                target_geometry = new SourceShipTargetGeometry {
                    front_offset = halfHeight - ConfigData.OffsetFromFrontOfShip.GetValueOrDefault(ship.ShipType),
                    bounds_latched = ship.IsInBoundsLatched
                },
                height = ship.GetHeight(),
                turning_radius = turningRadius,
                half_width = halfWidth,
                half_height = halfHeight,
                offset_from_center = ship.OffsetFromCenter,
                path_clearance = ship.GetClearance(),
                mobile = ship.IsMobile,
                cannot_change_orders = ship.CannotChangeMovementOrders,
                user_controlled = ship.IsUserControlled,
                has_target_coordinates = ship.HasTargetCoordinates,
                target_coordinates = ship.TargetCoordinates,
                is_rl_policy_controlled = ship.IsRlPolicyControlled,
                is_moving = ship.IsMoving,
                movement_direction = ship.RlMovementDirection,
                longest_side = ship.LongestSide,
                max_weapon_range = ship.MaxRange,
                firepower = ship.Firepower,
                is_bomber = ship.IsBomber,
                is_carrier_ship = ship.IsCarrierShip,
                has_communication = hasCommunication,
                has_bound_policy = hasBoundPolicy,
                policy_team_id = policyTeamId,
                policy_quarter_turns = policyQuarterTurns,
                policy_movement_disabled = movementDisabled,
                policy_cease_fire = ship.IsCeaseFire,
                source_sight = ship.Sight,
                effective_vision_range = HiveMindVision.GetEffectiveRange(ship),
                hivemind_registered = ship.IsHiveMindControlled,
                hivemind_vision_enabled = ship.HiveMindVision != null &&
                    ship.HiveMindVision.enabled &&
                    ship.HiveMindVision.gameObject.activeInHierarchy &&
                    ship.HiveMindVision.Collider != null &&
                    ship.HiveMindVision.Collider.enabled,
                // The Unity trigger keeps its Create-time Range even if
                // a weapon's observed MaxRange later changes.
                hivemind_sight_range = ship.HiveMindVision != null
                    ? ship.HiveMindVision.Range : 0,
                has_weapons = hasBoundPolicy && ship.HasWeapons,
                has_turrets = hasBoundPolicy && ship.HasTurrets,
                has_special_action = hasBoundPolicy &&
                    RlCombatPerception.HasSpecialAction(ship),
                special_ready = hasBoundPolicy &&
                    RlCombatPerception.GetSpecialReadiness(ship) > 0f,
                special_resource_fraction = hasBoundPolicy
                    ? RlCombatPerception.GetSpecialResourceFraction(ship) : 0f,
                special_cooldown_fraction = hasBoundPolicy
                    ? RlCombatPerception.GetSpecialCooldownFraction(ship) : 0f,
                special_phase = hasBoundPolicy
                    ? RlCombatPerception.GetSpecialPhase(ship) : 0f,
                mining_possible = hasBoundPolicy &&
                    RlOneVsOneAgent.CanUseMiningAction(ship),
                healing_possible = hasBoundPolicy &&
                    RlOneVsOneAgent.CanUseHealingAction(ship),
                warp_possible = hasBoundPolicy &&
                    RlOneVsOneAgent.CanUseWarpAction(ship),
                // Carrier.Kill reassigns ordinary Drone/Striker children
                // too. Preserve their real link independently of whether
                // a neural policy happens to control this ship.
                is_carrier_child = carrierChild != null,
                has_living_parent_carrier = hasParent,
                parent_carrier_id = hasParent ? carrierChild.Carrier.Id : 0,
                side_starting_tsv = hasBoundPolicy
                    ? level.State.InitialTsv[ship.Side - 1] : 0,
                team_retainable_mined_tsv = hasBoundPolicy
                    ? RlOneVsOneEpisodeCoordinator.GetRetainableMinedTsv(
                        level, ship.Side) : 0,
                self_weapons = hasBoundPolicy
                    ? ExportNonTurretSelfWeapons(ship)
                    : new List<SourceSelfWeapon>(),
                communication = communication,
                observed_weapons = ExportObservedWeapons(ship),
                turrets = ExportTurrets(ship)
            });
        }
        if (snapshot.has_policy_episode)
        {
            if (!RlOneVsOneEpisodeCoordinator.TryReadNativeEpisodeNumber(
                    level, out int episodeNumber))
            {
                throw new InvalidOperationException(
                    "Source MA-POCA episode identity is unavailable.");
            }
            snapshot.policy_episode.episode_number = episodeNumber;
            snapshot.policy_episode.team0_movement_disabled =
                RlOneVsOneTrainingBootstrap.CurrentMovementDisabledForTeam(0);
            snapshot.policy_episode.team1_movement_disabled =
                RlOneVsOneTrainingBootstrap.CurrentMovementDisabledForTeam(1);
            var registeredBoundShips = new HashSet<long>();
            foreach (var member in RlOneVsOneAgent.ReadNativeGroupMembers(level))
            {
                if (member.Side != snapshot.policy_episode.bee_side &&
                    member.Side != snapshot.policy_episode.human_side)
                {
                    throw new InvalidOperationException(
                        "MA-POCA group participant belongs to another source side.");
                }
                if (member.BoundShipId != 0)
                {
                    SourceShip bound = snapshot.ships.Find(ship =>
                        ship.entity_id == member.BoundShipId);
                    if (bound == null || !bound.has_bound_policy ||
                        bound.side != member.Side ||
                        bound.policy_team_id != member.TeamId ||
                        !registeredBoundShips.Add(member.BoundShipId))
                    {
                        throw new InvalidOperationException(
                            "MA-POCA participant does not own its exported policy ship.");
                    }
                }
                snapshot.policy_episode.group_participants.Add(
                    new SourceGroupParticipant {
                        agent_id = member.AgentId, side = member.Side,
                        team_id = member.TeamId, ship_id = member.BoundShipId
                    });
            }
            foreach (SourceShip ship in snapshot.ships)
            {
                if (ship.has_bound_policy &&
                    !registeredBoundShips.Contains(ship.entity_id))
                {
                    throw new InvalidOperationException(
                        "Exported policy ship has no participating MA-POCA Agent.");
                }
            }
        }
        if (!snapshot.has_policy_episode &&
            snapshot.ships.Exists(ship => ship.has_bound_policy))
        {
            throw new InvalidOperationException(
                "A bound policy requires the real source episode seed; " +
                "do not invent starting TSV or discovery reward baselines.");
        }
        if (snapshot.ships.Count == 0)
        {
            throw new InvalidOperationException(
                "Native scene export requires at least one living source ship.");
        }

        // Source GameState.Squads defines membership and source order; the
        // physics snapshot alone cannot recover command or group ownership.
        // Reject partial formation, double ownership and unbound living
        // ships rather than constructing a plausible but false native game.
        var liveIds = new HashSet<long>();
        foreach (var ship in snapshot.ships) liveIds.Add(ship.entity_id);
        var registered = new HashSet<long>();
        var liveSquadNumbers = new HashSet<long>();
        foreach (Squad squad in level.State.Squads)
        {
            if (squad == null || squad.IsDead) continue;
            if (squad.ItemId <= 0 || squad.Side <= 0)
                throw new InvalidOperationException(
                    "Cannot export a Squad without an allocated source identity.");
            if (squad.Side > snapshot.original_squad_counts.Length ||
                squad.SquadNumber <= 0 ||
                (!squad.IsMinionSquad &&
                 squad.SquadNumber > snapshot.original_squad_counts[squad.Side - 1]) ||
                !liveSquadNumbers.Add(((long)squad.Side << 32) | (uint)squad.SquadNumber))
            {
                throw new InvalidOperationException(
                    "Invalid or repeated source squad number for side " + squad.Side);
            }
            var members = squad.GetShips();
            if (members == null || members.Count == 0)
                throw new InvalidOperationException(
                    "Cannot export an empty living Squad " + squad.ItemId);
            var sourceSquad = new SourceSquad {
                item_id = squad.ItemId, side = squad.Side,
                squad_number = squad.SquadNumber,
                is_minion_squad = squad.IsMinionSquad,
                user_controlled = squad.IsUserControlled,
                can_accept_input = squad.CanAcceptUserInput,
                locked_on = squad.IsLockedOn,
                should_chase = squad.ShouldChase(),
                is_carrier_squad = squad.IsCarrierSquad,
                is_hivemind_controlled = squad.IsHiveMindControlled,
                is_immobile = squad.IsImmobile,
                opponent_id_decimal = squad.OpponentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                shooting_strategy = Utilities.ConvertShootingStrategyTypeToName[squad.GetShootingStrategy()]
            };
            if (squad.HasCommand || squad.HasCommandQueue || squad.CommandQueue.Count > 0)
                throw new InvalidOperationException(
                    "Export a fresh scene before commands start; command history is not a checkpoint: " + squad.ItemId);
            foreach (Ship ship in members)
            {
                if (ship == null || ship.IsDead || ship.Side != squad.Side ||
                    !liveIds.Contains(ship.Id) || !registered.Add(ship.Id))
                {
                    throw new InvalidOperationException(
                        "Invalid/duplicate live Squad membership in " +
                        squad.ItemId);
                }
                sourceSquad.member_ids.Add(ship.Id);
            }
            foreach (var banned in squad.BannedStrats)
                sourceSquad.banned_strategies.Add(Utilities.ConvertCommandTypeToName[banned]);
            sourceSquad.banned_strategies.Sort(StringComparer.Ordinal);
            snapshot.squads.Add(sourceSquad);
        }
        if (registered.Count != liveIds.Count)
        {
            throw new InvalidOperationException(
                "Not all living ships belong to exactly one source Squad.");
        }

        // GameState.Obstacles does not include all map-authored physical
        // borders and static colliders. PathfinderObstacleScope uses the
        // Level's actual active obstacle hierarchy, while the State list
        // also includes transient physical entities outside that hierarchy.
        // Union by reference, then reject repeated or missing source IDs.
        var exportedObjects =
            new HashSet<Obstacle>(ReferenceIdentityComparer<Obstacle>.Instance);
        var exportedIds = new HashSet<long>(liveIds);
        foreach (Obstacle obstacle in level.State.Obstacles)
        {
            if (!IsLivePhysicalObstacle(obstacle)) continue;
            AddPhysicalObstacle(level, snapshot, obstacle,
                exportedObjects, exportedIds);
        }
        foreach (GameObject mapChild in
                 PathfinderObstacleScope.GetActiveObstacleObjects(level))
        {
            if (mapChild == null) continue;
            Obstacle obstacle = mapChild.GetComponent<Obstacle>();
            if (!IsLivePhysicalObstacle(obstacle)) continue;
            AddPhysicalObstacle(level, snapshot, obstacle,
                exportedObjects, exportedIds);
        }

        // MapObjects do NOT derive from Obstacle and were previously
        // omitted even when their physics and damage were active. The
        // canonical native combat world already owns their collision,
        // health, Fire Tank explosion and discovery gameplay.
        foreach (MapObject obj in
                 level.Map.Transform.GetComponentsInChildren<MapObject>(false))
        {
            if (obj == null || obj.IsDead) continue;
            if (!obj.gameObject.activeInHierarchy) continue;
            if (obj.Level != level)
                throw new InvalidOperationException(
                    "MapObject belongs to a different Level: " + obj.name);
            AddMapObject(level, snapshot, obj, exportedIds);
        }

        if (level.State.Projectiles.Count != 0)
        {
            throw new InvalidOperationException(
                "Physical scene snapshots cannot omit active projectiles. " +
                "Capture before combat begins.");
        }

        // Capture the stable physical scene and actual participating
        // Agent instance identities. The model weight slots, runtime
        // squad lifetimes and pending server commands are still supplied
        // independently; this is not a resumable gameplay checkpoint.
        string safe = Sanitize(snapshot.level_name);
        string dir = Path.Combine(Application.dataPath, "native", "assets");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "unity-physical-" + safe + ".json");
        File.WriteAllText(path, JsonUtility.ToJson(snapshot, true) + "\n",
            new UTF8Encoding(false));
        AssetDatabase.Refresh();
        Debug.Log("Exported native physical Level source: " + path);
    }

    private static bool IsLivePhysicalObstacle(Obstacle obstacle)
    {
        return obstacle != null && !obstacle.IsDead &&
            obstacle.gameObject.activeInHierarchy &&
            obstacle.Collider != null && obstacle.Collider.isActiveAndEnabled;
    }

    private static void AddPhysicalObstacle(
        Level level,
        PhysicalScene snapshot,
        Obstacle obstacle,
        HashSet<Obstacle> exportedObjects,
        HashSet<long> exportedIds)
    {
        if (!exportedObjects.Add(obstacle)) return;
        if (obstacle.Id <= 0)
        {
            // Some map borders are active before Pathfinder has called
            // Obstacle.Setup. Allocating a fake Unity ID here would change
            // transport/event identity. Export is deliberately refused.
            throw new InvalidOperationException(
                "Active map obstacle has no assigned source ID: " +
                obstacle.gameObject.name +
                ". Export after the Level has initialized its obstacles.");
        }
        if (!exportedIds.Add(obstacle.Id))
        {
            throw new InvalidOperationException(
                "Duplicate source physical entity ID " + obstacle.Id);
        }

        var prefab = PrefabUtility.GetCorrespondingObjectFromSource(
            obstacle.gameObject);
        if (prefab == null)
        {
            throw new InvalidOperationException(
                "Native obstacle needs a verified source prefab: " +
                obstacle.gameObject.name);
        }
        Transform map = level.Map.Transform;
        var entity = new SourceObstacle {
            entity_id = obstacle.Id,
            prefab_name = prefab.name,
            // A map-authored obstacle may have a nested parent. The
            // physical world and native simulation both use Level-local
            // coordinates, not the immediate parent-local transform.
            position = map.InverseTransformPoint(obstacle.transform.position),
            rotation_degrees = Mathf.DeltaAngle(
                map.eulerAngles.z, obstacle.transform.eulerAngles.z),
            health = obstacle.Health,
            effective_max_health = obstacle.OriginalHealth,
            layer = obstacle.Collider.gameObject.layer
        };
        if (obstacle is MiningAsteroid)
        {
            // Source mining asteroids are authored PolygonCollider2D
            // resources, not asteroid-size approximations. The snapshot
            // represents an initial fully stocked mine, never a partially
            // extracted mid-episode resource.
            if (!(obstacle.Collider is PolygonCollider2D polygon) ||
                polygon.pathCount != 1 ||
                obstacle.OriginalHealth <= 0 ||
                obstacle.Health != obstacle.OriginalHealth)
            {
                throw new InvalidOperationException(
                    "Only fully stocked authored polygon mining asteroids " +
                    "can be imported: " + obstacle.name);
            }
            entity.kind = "mining";
            Vector2[] points = polygon.GetPath(0);
            for (int i = 0; i < points.Length; i++)
                points[i] += polygon.offset;
            ExportColliderVertices(map, polygon, entity.position,
                entity.rotation_degrees, points, entity.collider_vertices);
        }
        else if (obstacle is CollisionAsteroid full)
        {
            if (full.IsShard || full.IsDelayKilled ||
                full.Health != full.EffectiveMaxHealth ||
                !prefab.name.StartsWith("Asteroid ", StringComparison.Ordinal) ||
                !int.TryParse(
                    prefab.name.Substring("Asteroid ".Length),
                    out int prefabNumber) ||
                prefabNumber < 1 || prefabNumber > 28 ||
                full.Body == null)
            {
                throw new InvalidOperationException(
                    "Only complete ordinary source asteroids can be imported: " +
                    obstacle.name);
            }
            entity.kind = "full-asteroid";
            entity.prefab_number = prefabNumber;
            entity.speed = full.Speed;
            entity.health = full.Health;
            entity.effective_max_health = full.EffectiveMaxHealth;
            entity.velocity = map.InverseTransformVector(
                full.Body.linearVelocity);
            entity.angular_velocity_degrees = full.Body.angularVelocity;
        }
        else if (prefab.name == "Polygon Obstacle")
        {
            if (!(obstacle.Collider is PolygonCollider2D polygon) ||
                polygon.pathCount != 1)
            {
                throw new InvalidOperationException(
                    "Only a single actual primary PolygonCollider2D path " +
                    "can be imported: " + obstacle.name);
            }
            entity.kind = "polygon";
            Vector2[] points = polygon.GetPath(0);
            for (int i = 0; i < points.Length; i++)
                points[i] += polygon.offset;
            ExportColliderVertices(map, polygon, entity.position,
                entity.rotation_degrees, points, entity.collider_vertices);
        }
        else if (obstacle is StaticObstacle || obstacle is MapBorder)
        {
            if (!(obstacle.Collider is BoxCollider2D box) ||
                box.edgeRadius != 0f)
            {
                throw new InvalidOperationException(
                    "Only exact unrounded primary BoxCollider2D " +
                    "can be imported: " + obstacle.name);
            }
            entity.kind = "box";
            Vector2 half = box.size * 0.5f;
            Vector2 center = box.offset;
            Vector2[] corners = {
                center + new Vector2(-half.x, -half.y),
                center + new Vector2(half.x, -half.y),
                center + new Vector2(half.x, half.y),
                center + new Vector2(-half.x, half.y)
            };
            ExportColliderVertices(map, box, entity.position,
                entity.rotation_degrees, corners, entity.collider_vertices);
        }
        else
        {
            // MiningAsteroids and other classes require authentic
            // source-specific bodies/reward state, not invented boxes.
            throw new InvalidOperationException(
                "Native physical snapshot cannot represent obstacle " +
                obstacle.name + " (" + prefab.name + ")");
        }
        snapshot.obstacles.Add(entity);
    }

    /// Re-express the live primary Collider2D geometry in the native
    /// Obstacle body local frame, including nested rotation/scale. The
    /// Level owns these coordinates; presentation never affects them.
    private static void ExportColliderVertices(
        Transform map, Collider2D collider,
        Vector2 rootPosition, float rootRotation,
        Vector2[] points, List<Vector2> destination)
    {
        Quaternion unrotate = Quaternion.Euler(0f, 0f, -rootRotation);
        foreach (Vector2 point in points)
        {
            Vector2 world = collider.transform.TransformPoint(point);
            Vector2 local = map.InverseTransformPoint(world);
            destination.Add(unrotate * (local - rootPosition));
        }
    }

    private static void AddMapObject(
        Level level, PhysicalScene snapshot, MapObject obj,
        HashSet<long> exportedIds)
    {
        if (obj.Id <= 0 || !exportedIds.Add(obj.Id) ||
            obj.Health <= 0 || obj.MaxHealth <= 0 ||
            obj.Health != obj.MaxHealth ||
            obj.Collider == null || !obj.Collider.isActiveAndEnabled)
        {
            throw new InvalidOperationException(
                "Cannot export a partial/inactive physical MapObject: " + obj.name);
        }
        Transform map = level.Map.Transform;
        Collider2D collider = obj.Collider;
        SourceMapObject entry = new SourceMapObject {
            entity_id = obj.Id,
            kind = obj is CanisterBomb ? "fire-tank" : "generic",
            layer = collider.gameObject.layer,
            health = obj.Health,
            max_health = obj.MaxHealth,
            position = map.InverseTransformPoint(obj.transform.position),
            rotation_degrees = Mathf.DeltaAngle(
                map.eulerAngles.z, obj.transform.eulerAngles.z),
            linear_velocity = obj.Body != null
                ? (Vector2)map.InverseTransformVector(obj.Body.linearVelocity)
                : Vector2.zero,
            angular_velocity_degrees = obj.Body != null
                ? obj.Body.angularVelocity : 0f,
        };
        if (obj is CanisterBomb tank)
        {
            entry.explosion_power = tank.Power;
            if (tank.TargetObstacle != null)
            {
                if (tank.TargetObstacle.IsDead || tank.TargetObstacle.Id <= 0)
                    throw new InvalidOperationException(
                        "Fire Tank has no live linked obstacle: " + obj.name);
                entry.linked_obstacle_id = tank.TargetObstacle.Id;
            }
        }
        SourceCollider shape = ExportColliderShape(map, collider, entry.position, entry.rotation_degrees);
        entry.collider_kind = shape.kind;
        entry.collider_vertices = shape.vertices;
        entry.circle_offset = shape.offset;
        entry.circle_radius = shape.radius;
        snapshot.map_objects.Add(entry);
    }

    private static SourceCollider ExportShipSupportZone(Level level, Ship ship, Collider2D collider)
    {
        if (collider == null || !collider.isActiveAndEnabled) return new SourceCollider();
        return ExportColliderShape(level.Map.Transform, collider, ship.GetPosition(), ship.Rotation);
    }

    private static SourceSpecialWeapon ExportSpecialWeapon(Ship ship)
    {
        Weapon weapon = ship is Barge barge ? barge.Charge :
            ship is Striker striker ? striker.Bomb : ship is FireBarge fire ? fire.Bomb :
            ship is YellowJacket jacket ? jacket.Bomb : null;
        if (weapon == null)
        {
            if (ship is Barge || ship is Striker || ship is FireBarge || ship is YellowJacket)
                throw new InvalidOperationException("Missing actual special weapon: " + ship.Id);
            return null;
        }
        int slot = ship.Weapons.IndexOf(weapon);
        if (!(weapon is Bomb) || weapon.Id <= 0 || weapon.Power < 0 || slot < 0 || slot >= 5)
            throw new InvalidOperationException("Invalid actual special weapon: " + ship.Id);
        SourceTrigger Capture(CircleCollider2D collider)
        {
            if (collider == null) throw new InvalidOperationException("Missing special trigger: " + ship.Id);
            return new SourceTrigger {
                collider = ExportColliderShape(ship.Level.Map.Transform, collider, ship.GetPosition(), ship.Rotation),
                layer = collider.gameObject.layer,
                enabled = collider.enabled && collider.gameObject.activeInHierarchy,
            };
        }
        return new SourceSpecialWeapon {
            weapon_id = weapon.Id, power = weapon.Power,
            slot = slot,
            // Weapon.GetPosition is authoritative; Bomb inherits the ship
            // origin even when its visual piece has an offset.
            mount_offset = Quaternion.Euler(0f, 0f, -ship.Rotation) * (weapon.GetPosition() - ship.GetPosition()),
            charge_range = ship is Barge ? Capture(weapon.RangeCollider?.Collider) : null,
            proximity = ship is Barge ? null : Capture(ship.ProximityCollider?.Collider),
        };
    }

    private static SourceShipSpecial ExportShipSpecial(Ship ship)
    {
        var result = new SourceShipSpecial();
        Weapon weapon = null;
        if (ship is Striker striker)
        {
            if (!striker.IsBombReady || striker.HasDroppedBomb || striker.HasCompletedRun ||
                (striker.Carrier != null && striker.Carrier.IsDead))
                throw new InvalidOperationException("Cannot export an active Striker lifecycle: " + ship.Id);
            result.kind = "striker";
            result.carrier_id = striker.Carrier != null ? striker.Carrier.Id : 0;
            result.offset_from_center = ship.OffsetFromCenter;
            // Source Striker.CheckCarrierReload uses the fixed 15-unit radius.
            result.refill_distance = 15f;
            weapon = striker.Bomb;
        }
        else if (ship is FireBarge fireBarge)
        {
            if (fireBarge.RlSelfDetonationRequested)
                throw new InvalidOperationException("Cannot export a detonating Fire Barge: " + ship.Id);
            result.kind = "fire-barge";
            weapon = fireBarge.Bomb;
        }
        else if (ship is YellowJacket jacket)
        {
            result.kind = "yellow-jacket";
            weapon = jacket.Bomb;
        }
        else if (ship is Barge barge)
        {
            if (!barge.IsRlChargeReady || barge.IsCharging || barge.HasStartedCharging)
                throw new InvalidOperationException("Cannot export an active Barge charge: " + ship.Id);
            result.kind = "barge";
            result.can_override_bounds = barge.CanOverrideBounds;
            weapon = barge.Charge;
        }
        else if (ship is Scout scout)
        {
            float clock = ship.Stage.IsTraining ? Time.time : Time.realtimeSinceStartup;
            if (scout.MinionType != ConfigData.ShipTypes.Beacon || scout.BeaconsDropped != 0 ||
                clock - scout.TimeSinceLastBeaconDropped + 0.0001f < ConfigData.MinimumDelayPerBeacon)
                throw new InvalidOperationException("Cannot export a partially consumed Scout lifecycle: " + ship.Id);
            result.kind = "scout";
            result.scout_enabled = scout.CanDropBeacons;
            result.beacon = ExportBeaconTemplate(ship);
            return result;
        }
        else return result;

        if (weapon == null || weapon.Power < 0)
            throw new InvalidOperationException("Missing real ship-special weapon power: " + ship.Id);
        result.power = weapon.Power;
        return result;
    }

    private static SourceBeaconTemplate ExportBeaconTemplate(Ship scout)
    {
        GameObject prefab = scout.Stage.Prefabs.BeaconPrefab;
        Beacon beacon = prefab != null ? prefab.GetComponent<Beacon>() : null;
        var stats = ConfigData.GetShipInfo(ConfigData.ShipTypes.Beacon);
        if (beacon == null || beacon.Side != scout.Side ||
            AssetDatabase.GetAssetPath(prefab) != "Assets/Prefabs/Entities/Ships/Beacon.prefab" ||
            stats.Health <= 0 || stats.ProjectileValues.Count != 0 ||
            beacon.HiveMindVision == null || beacon.HiveMindVision.Collider == null)
            throw new InvalidOperationException("Unsupported/missing authored Beacon prefab on Scout " + scout.Id);
        // Read the SAME source Create/Setup inputs without instantiating a
        // pool item or changing the live Level merely to produce an export.
        Vector2 size = ConfigData.ShipSizes[ConfigData.ShipTypes.Beacon] / ConfigData.PixelsPerUnit;
        bool training = RlOneVsOneTrainingBootstrap.IsActiveFor(scout.Stage);
        bool user = scout.Side == ConfigData.Configuration.UserSide && scout.Stage.DoesUserHaveController;
        bool registered = scout.Stage.IsTrainingNueralNetwork || !user;
        if (beacon.GetComponentsInChildren<Weapon>(true).Length != 0)
            throw new InvalidOperationException("Beacon combat activity requires initialized source weapon rates.");
        return new SourceBeaconTemplate {
            combat_activity = new SourceCombatActivity {
                enabled = scout.Stage.ActivateHiveMind, active = false, elapsed_seconds = 0f,
                window_seconds = Ship.CalculateCombatActivityWindow(Array.Empty<Weapon>()),
            },
            targeting = new SourceBeaconTarget {
                front_offset = size.y * 0.5f - ConfigData.OffsetFromFrontOfShip.GetValueOrDefault(ConfigData.ShipTypes.Beacon),
                ship_type_letter = (int)Utilities.ConvertShipTypeToShipTypeLetter[ConfigData.ShipTypes.Beacon],
                firepower = beacon.SpecialFirePower,
            },
            original_health = stats.Health,
            health = training ? RlOneVsOneTrainingDurabilityGuard.CalculateTrainingHealth(stats.Health) : stats.Health,
            max_tsv = stats.Tsv,
            size_class = beacon.SizeClass,
            base_speed = stats.Speed,
            rotation_speed = stats.Speed * ConfigData.Configuration.RotationMultiplier,
            height = size.y,
            longest_side = Mathf.Max(size.x, size.y),
            effective_vision_range = stats.Sight,
            hivemind_registered = registered,
            hivemind_vision_enabled = registered,
            max_living_ships_per_side = training ? RlOneVsOneTrainingBootstrap.CurrentShipsPerSide : 0,
        };
    }

    private static SourceCollider ExportColliderShape(
        Transform map, Collider2D collider, Vector2 rootPosition, float rootRotation)
    {
        var shape = new SourceCollider();
        if (collider is BoxCollider2D box)
        {
            if (box.edgeRadius != 0f)
                throw new InvalidOperationException(
                    "Rounded authored box is not representable: " + collider.name);
            shape.kind = "box";
            Vector2 h = box.size * 0.5f;
            Vector2 c = box.offset;
            Vector2[] corners = {
                c + new Vector2(-h.x, -h.y),
                c + new Vector2(h.x, -h.y),
                c + new Vector2(h.x, h.y),
                c + new Vector2(-h.x, h.y),
            };
            ExportColliderVertices(map, box, rootPosition,
                rootRotation, corners, shape.vertices);
        }
        else if (collider is PolygonCollider2D polygon)
        {
            if (polygon.pathCount != 1)
                throw new InvalidOperationException(
                    "Collider requires a single authored polygon path: " + collider.name);
            shape.kind = "polygon";
            Vector2[] points = polygon.GetPath(0);
            for (int i = 0; i < points.Length; i++)
                points[i] += polygon.offset;
            ExportColliderVertices(map, polygon, rootPosition,
                rootRotation, points, shape.vertices);
        }
        else if (collider is CircleCollider2D circle)
        {
            // A native circle must remain a circle under the actual
            // complete map/parent transforms. Never substitute an AABB
            // when Unity's physical shape becomes an ellipse.
            Vector2 x = map.InverseTransformVector(
                circle.transform.TransformVector(Vector3.right * circle.radius));
            Vector2 y = map.InverseTransformVector(
                circle.transform.TransformVector(Vector3.up * circle.radius));
            float radiusX = x.magnitude;
            float radiusY = y.magnitude;
            float limit = Mathf.Max(0.0001f, Mathf.Max(radiusX, radiusY) * 0.0001f);
            if (!IsFinite(radiusX) || !IsFinite(radiusY) ||
                radiusX <= 0f || radiusY <= 0f ||
                Mathf.Abs(radiusX - radiusY) > limit ||
                Mathf.Abs(Vector2.Dot(x, y)) > limit * Mathf.Max(radiusX, radiusY))
                throw new InvalidOperationException(
                    "Scaled authored circle is not isotropic: " + collider.name);
            shape.kind = "circle";
            shape.radius = (radiusX + radiusY) * 0.5f;
            Vector2 center = map.InverseTransformPoint(
                circle.transform.TransformPoint(circle.offset));
            shape.offset = Quaternion.Euler(
                0f, 0f, -rootRotation) * (center - rootPosition);
        }
        else
        {
            throw new InvalidOperationException(
                "Unsupported primary authored Collider2D: " +
                collider.GetType().Name);
        }
        return shape;
    }

    private static List<SourceSelfWeapon> ExportNonTurretSelfWeapons(Ship ship)
    {
        var entries = new List<SourceSelfWeapon>();
        if (ship.Weapons == null) return entries;
        int count = Math.Min(5, ship.Weapons.Count);
        for (int slot = 0; slot < count; slot++)
        {
            Weapon weapon = ship.Weapons[slot];
            // Turrets are entirely owned by CombatWorld. Passing them
            // again as external observations would duplicate mutable
            // aiming and cooldown state.
            if (weapon == null || weapon is Turret) continue;
            GameObject source = FindProjectilePrefab(
                ship.Stage.Prefabs, weapon.ProjectileType);
            Projectile projectile = source != null
                ? source.GetComponent<Projectile>() : null;
            entries.Add(new SourceSelfWeapon {
                slot = slot,
                weapon_type = (int)weapon.Type,
                position = weapon.GetPosition(),
                range = weapon.Range,
                power = weapon.Power,
                rate_of_fire = weapon.RateOfFire,
                rotation_rate = weapon.RotationRate,
                projectile_value = weapon.ProjectileValue,
                has_projectile_speed = projectile != null,
                projectile_speed = projectile != null ? projectile.Speed : 0d,
                has_target_ship = weapon.HasTargetShip
            });
        }
        return entries;
    }

    private static List<SourceObservedWeapon> ExportObservedWeapons(Ship ship)
    {
        var entries = new List<SourceObservedWeapon>();
        if (ship.Weapons == null) return entries;
        // RlCombatPerception.AddEntityWeaponSlots consumes exactly the
        // first five actual Weapon list slots without compacting gaps.
        int count = Math.Min(5, ship.Weapons.Count);
        for (int slot = 0; slot < count; slot++)
        {
            Weapon weapon = ship.Weapons[slot];
            if (weapon == null) continue;
            entries.Add(new SourceObservedWeapon {
                slot = slot,
                weapon_type = (int)weapon.Type,
                range = weapon.Range,
                power = weapon.Power,
                rate_of_fire = weapon.RateOfFire
            });
        }
        return entries;
    }

    private static long FindRankFleetOwner(Ship ship)
    {
        // Preserve actual FleetShip reference sharing, including transient
        // objects without a persistent database ID. The alias is a REAL ship.
        long owner = ship.Id;
        foreach (Ship candidate in ship.Level.State.Ships)
            if (candidate != null && !candidate.IsDead &&
                ReferenceEquals(candidate.FleetShip, ship.FleetShip))
                owner = Math.Min(owner, candidate.Id);
        return owner;
    }

    private static List<SourceTurret> ExportTurrets(Ship ship)
    {
        var entries = new List<SourceTurret>();
        if (ship.Weapons == null) return entries;
        for (int slot = 0; slot < ship.Weapons.Count; slot++)
        {
            if (!(ship.Weapons[slot] is Turret turret)) continue;
            if (slot >= 5 || turret.PieceTransform == null)
            {
                throw new InvalidOperationException(
                    "Unsupported authored turret slot on ship " + ship.Id);
            }
            string pattern = "single";
            Vector2 left = Vector2.zero, right = Vector2.zero;
            if (turret is FullShipTurret) pattern = "full-ship";
            else if (turret is LaserBuilder) pattern = "laser-builder";
            else if (turret is BeamCannon) pattern = "beam";
            else if (turret is DualCannon dual)
            {
                pattern = "dual";
                left = dual.LeftCannonOffset;
                right = dual.RightCannonOffset;
            }
            GameObject source = FindProjectilePrefab(
                ship.Stage.Prefabs, turret.ProjectileType);
            Projectile projectile = source == null
                ? null : source.GetComponent<Projectile>();
            if (projectile == null)
            {
                throw new InvalidOperationException(
                    "No source projectile Speed for " + turret.ProjectileType +
                    " on ship " + ship.Id);
            }
            Vector3 localMount = ship.Transform.InverseTransformPoint(
                turret.PieceTransform.position);
            CircleCollider2D range = turret.RangeCollider == null ? null : turret.RangeCollider.Collider;
            if (range == null)
                throw new InvalidOperationException("Missing actual turret range collider on ship " + ship.Id);
            SourceCollider rangeShape = ExportColliderShape(
                ship.Level.Map.Transform, range, turret.GetPosition(), turret.Rotation);
            entries.Add(new SourceTurret {
                range_collider = rangeShape,
                aim_at_front_of_ship = turret.ShouldFireAtFrontOfShip,
                range_layer = range.gameObject.layer,
                range_enabled = range.enabled && range.gameObject.activeInHierarchy,
                slot = slot,
                weapon_type = (int)turret.Type,
                projectile_type = (int)turret.ProjectileType,
                mount_offset = new Vector2(localMount.x, localMount.y),
                left_offset = left,
                right_offset = right,
                initial_rotation_degrees = turret.Rotation,
                rotation_rate = turret.RotationRate,
                range = turret.Range,
                power = turret.Power,
                rate_of_fire = turret.RateOfFire,
                projectile_value = turret.ProjectileValue,
                projectile_speed = projectile.Speed,
                fire_pattern = pattern
            });
        }
        return entries;
    }

    private static GameObject FindProjectilePrefab(
        Prefabs p, ConfigData.ProjectileTypes type)
    {
        if (p == null) return null;
        switch (type)
        {
            case ConfigData.ProjectileTypes.BeeSmall:
                return p.BeeSmallLaserShotPrefab;
            case ConfigData.ProjectileTypes.BeeMedium:
                return p.BeeMediumLaserShotPrefab;
            case ConfigData.ProjectileTypes.BumblebeeShot:
                return p.BumblebeeShotPrefab;
            case ConfigData.ProjectileTypes.FlagshipShot:
                return p.FlagshipShotPrefab;
            case ConfigData.ProjectileTypes.Rocket:
                return p.RocketPrefab;
            case ConfigData.ProjectileTypes.HumanSmall:
                return p.HumanSmallPrefab;
            case ConfigData.ProjectileTypes.HumanMedium:
                return p.HumanMediumPrefab;
            case ConfigData.ProjectileTypes.Beam:
                return p.BeamPrefab;
            case ConfigData.ProjectileTypes.SplitShot:
                return p.SplitShotPrefab;
            case ConfigData.ProjectileTypes.QueenSmall:
                return p.QueenSmallPrefab;
            case ConfigData.ProjectileTypes.QueenLarge:
                return p.QueenLargePrefab;
            case ConfigData.ProjectileTypes.StrikerBomb:
                return p.StrikerBombPrefab;
            default:
                return null;
        }
    }

    // Unity targets both .NET Standard and Mono profiles where
    // Single.IsFinite is not consistently available.
    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static string Sanitize(string input)
    {
        var builder = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_'
                ? c : '_');
        }
        if (builder.Length == 0)
            throw new InvalidOperationException("Level name cannot be empty.");
        return builder.ToString();
    }

    [MenuItem("Bees/Native/Export Selected Level Physical Scene", true)]
    private static bool CanExport()
    {
        return Selection.activeGameObject != null &&
            Selection.activeGameObject.GetComponent<Level>() != null;
    }
}
#endif
