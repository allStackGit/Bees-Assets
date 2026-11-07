#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
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
    private sealed class PhysicalScene
    {
        public int schema_version = 1;
        public string level_name;
        public float fixed_dt_seconds;
        public Vector2 min, max;
        public List<SourceShip> ships = new List<SourceShip>();
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
                movement_direction = ship.RlMovementDirection
            });
        }
        if (snapshot.ships.Count == 0)
        {
            throw new InvalidOperationException(
                "Native scene export requires at least one living source ship.");
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
