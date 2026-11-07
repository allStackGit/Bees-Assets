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
        public List<SourceTurret> turrets = new List<SourceTurret>();
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
        public int schema_version = 3;
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
            level.Stage.FixedDeltaTime <= 0)
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
                "Not all living ships belong to exactly one source Squad.");
        }

        foreach (Obstacle obstacle in level.State.Obstacles)
        {
            if (obstacle == null || obstacle.IsDead) continue;
            var prefab = PrefabUtility.GetCorrespondingObjectFromSource(
                obstacle.gameObject);
            if (prefab == null || obstacle.Id <= 0)
            {
                throw new InvalidOperationException(
                    "Native source obstacle needs a real prefab and runtime ID.");
            }
            var entity = new SourceObstacle {
                entity_id = obstacle.Id,
                prefab_name = prefab.name,
                position = obstacle.GetPosition(),
                rotation_degrees = obstacle.transform.localEulerAngles.z,
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
                entity.velocity = full.Body.linearVelocity;
                entity.angular_velocity_degrees = full.Body.angularVelocity;
            }
            else if (prefab.name == "Polygon Obstacle")
            {
                entity.kind = "polygon";
            }
            else if (obstacle is StaticObstacle || obstacle is MapBorder)
            {
                entity.kind = "box";
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
