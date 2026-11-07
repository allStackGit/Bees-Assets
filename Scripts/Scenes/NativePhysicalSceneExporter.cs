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
        public long entity_id;
        public int ship_type;
        public int side;
        public int health, max_health, max_tsv, current_tsv;
        public int mined_cargo, original_health;
        public long fleet_ship_id;
        public bool persistent_fleet;
        public Vector2 position, body_velocity, target_coordinates;
        public float rotation_degrees, rotation_speed;
        public float base_speed, current_speed, height;
        public bool mobile, cannot_change_orders, user_controlled;
        public bool has_target_coordinates, is_rl_policy_controlled, is_moving;
        public int movement_direction;
        public float longest_side, max_weapon_range, firepower;
        public bool is_bomber, is_carrier_ship, has_communication;
        public Vector4 communication;
        public List<SourceObservedWeapon> observed_weapons =
            new List<SourceObservedWeapon>();
        public List<SourceTurret> turrets = new List<SourceTurret>();
    }

    [Serializable]
    private sealed class SourceObservedWeapon
    {
        public int slot, weapon_type;
        public float range, power, rate_of_fire;
    }

    [Serializable]
    private sealed class SourceTurret
    {
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
        public bool is_shard;
        public Vector2 position, velocity;
        public float rotation_degrees, angular_velocity_degrees;
        public List<Vector2> collider_vertices = new List<Vector2>();
    }

    [Serializable]
    private sealed class SourceSquad
    {
        // ItemId is the runtime ID allocated by Stage.Pool.ItemCount++ at
        // every Squad.Setup, including reuse of a pooled Squad wrapper.
        public long item_id;
        public int side;
        public List<long> member_ids = new List<long>();
    }

    [Serializable]
    private sealed class PhysicalScene
    {
        public int schema_version = 6;
        public string level_name;
        public float fixed_dt_seconds;
        public Vector2 min, max;
        public List<SourceShip> ships = new List<SourceShip>();
        public List<SourceSquad> squads = new List<SourceSquad>();
        public List<SourceObstacle> obstacles = new List<SourceObstacle>();
    }

    [MenuItem("Bees/Native/Export Selected Level Physical Scene")]
    private static void Export()
    {
        var selected = Selection.activeGameObject;
        var level = selected == null ? null : selected.GetComponent<Level>();
        if (level == null || level.Stage == null || level.State == null ||
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

        var snapshot = new PhysicalScene {
            level_name = !string.IsNullOrEmpty(level.Name)
                ? level.Name : level.gameObject.name,
            fixed_dt_seconds = level.Stage.FixedDeltaTime,
            min = new Vector2(level.MinX, level.MinY),
            max = new Vector2(level.MaxX, level.MaxY)
        };

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
            bool hasCommunication = RlOneVsOneAgent.TryReadCommunication(
                ship, out Vector4 communication);
            snapshot.ships.Add(new SourceShip {
                entity_id = ship.Id,
                ship_type = (int)ship.ShipType,
                side = ship.Side,
                health = ship.Health,
                max_health = ship.MaxHealth,
                max_tsv = ship.OriginalTsv,
                current_tsv = ship.Tsv,
                mined_cargo = fleet.MineralsMinedThisLevel,
                original_health = ship.OriginalHealth,
                fleet_ship_id = fleet.Id,
                persistent_fleet = fleet.Id > 0 &&
                    !ship.IsMinionShip && !ship.IsCarrierShip,
                position = ship.GetPosition(),
                rotation_degrees = ship.Rotation,
                rotation_speed = ship.RotationSpeed,
                base_speed = ship.Speed,
                current_speed = ship.CurrentSpeed,
                body_velocity = ship.Body.linearVelocity,
                height = ship.GetHeight(),
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
                communication = communication,
                observed_weapons = ExportObservedWeapons(ship),
                turrets = ExportTurrets(ship)
            });
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
        foreach (Squad squad in level.State.Squads)
        {
            if (squad == null || squad.IsDead) continue;
            if (squad.ItemId <= 0 || squad.Side <= 0)
                throw new InvalidOperationException(
                    "Cannot export a Squad without an allocated source identity.");
            var members = squad.GetShips();
            if (members == null || members.Count == 0)
                throw new InvalidOperationException(
                    "Cannot export an empty living Squad " + squad.ItemId);
            var sourceSquad = new SourceSquad {
                item_id = squad.ItemId, side = squad.Side
            };
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
            snapshot.squads.Add(sourceSquad);
        }
        if (registered.Count != liveIds.Count)
        {
            throw new InvalidOperationException(
                "Not all        // GameState.Obstacles is not complete for every level. Map-authored
        // physical borders and static prefabs can instead be set up by
        // Pathfinder.InitializeMap without ever joining that State list.
        // Reuse Unity's exact Level-scoped obstacle discovery, merge with
        // the State-owned physical obstacles, and preserve real entity IDs.
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

       }
            snapshot.obstacles.Add(entity);
        }

        if (level.State.Projectiles.Count != 0)
        {
            throw new InvalidOperationException(
                "Physical scene snapshots cannot omit active projectiles. " +
                "Capture before combat begins.");
        }

        // Capture the stable world-local physical scene; SquadLifetime,
        // agent-group IDs, model routes, and persistent command data are
        // supplied to the native host separately and never guessed here.
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
            effective_max_health = obstacle.OriginalHealth
        };
        if (obstacle is CollisionAsteroid full)
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
            ExportColliderVertices(map, polygon, entity, points);
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
            ExportColliderVertices(map, box, entity, corners);
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
        Transform map, Collider2D collider, SourceObstacle destination,
        Vector2[] points)
    {
        Quaternion unrotate = Quaternion.Euler(
            0f, 0f, -destination.rotation_degrees);
        foreach (Vector2 point in points)
        {
            Vector2 world = collider.transform.TransformPoint(point);
            Vector2 local = map.InverseTransformPoint(world);
            destination.collider_vertices.Add(
                unrotate * (local - destination.position));
        }
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
            entries.Add(new SourceTurret {
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
