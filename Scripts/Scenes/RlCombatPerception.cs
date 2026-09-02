using Assets.Scripts;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using Unity.MLAgents.Sensors;
using UnityEngine;

/// <summary>
/// Fixed-shape tactical perception for the shared combat policy. GameState/Hive Mind memory may
/// track arbitrarily many discovered objects; each controlled ship receives only a bounded nearest
/// subset plus a local occupancy grid for persistent static navigation geometry.
/// </summary>
internal sealed class RlCombatPerception
{
    internal const int ShipTypeBitCount = 5;
    internal const int WeaponTypeBitCount = 4;
    internal const int MapObjectTypeBitCount = 4;

    internal const int MaxObservedAllies = 48;
    internal const int MaxObservedEnemies = 48;
    internal const int MaxObservedMiningAsteroids = 8;
    internal const int MaxObservedMapObjects = 64;
    internal const int MaxObservedCollisionAsteroids = 48;
    internal const int MaxWeaponSlots = 8;

    internal const int NavigationGridSize = 13;
    internal const int NavigationGridCellCount = NavigationGridSize * NavigationGridSize;
    internal const float NavigationGridCellSize = 10f;

    internal const int SelfObservationSize = 28;
    internal const int EntityObservationSize = 18;
    internal const int WeaponObservationSize = 17;
    internal const int MiningAsteroidObservationSize = 7;
    internal const int MapObjectObservationSize = 12;
    internal const int CollisionAsteroidObservationSize = 11;
    internal const int ObservationSize = SelfObservationSize +
        (MaxObservedAllies + MaxObservedEnemies) * EntityObservationSize +
        MaxWeaponSlots * WeaponObservationSize +
        MaxObservedMiningAsteroids * MiningAsteroidObservationSize +
        MaxObservedMapObjects * MapObjectObservationSize +
        MaxObservedCollisionAsteroids * CollisionAsteroidObservationSize +
        NavigationGridCellCount;

    private const int GenericMapObjectObservationType = 2;
    private const int FireTankObservationType = 3;
    private const float LocalDistanceScale = 40f;

    private readonly struct ObservedMiningAsteroid
    {
        internal readonly int Id;
        internal readonly Vector2 Position;
        internal readonly Vector2 HalfExtents;
        internal readonly float ResourceFraction;
        internal readonly float Activity;

        internal ObservedMiningAsteroid(int id, Vector2 position, Vector2 halfExtents, float resourceFraction, float activity)
        {
            Id = id;
            Position = position;
            HalfExtents = halfExtents;
            ResourceFraction = resourceFraction;
            Activity = activity;
        }
    }

    private readonly struct ObservedMapObject
    {
        internal readonly int Id;
        internal readonly int Type;
        internal readonly Vector2 Position;
        internal readonly Vector2 HalfExtents;
        internal readonly float HealthFraction;
        internal readonly float Activity;
        internal readonly bool Targetable;

        internal ObservedMapObject(
            int id,
            int type,
            Vector2 position,
            Vector2 halfExtents,
            float healthFraction,
            float activity,
            bool targetable)
        {
            Id = id;
            Type = type;
            Position = position;
            HalfExtents = halfExtents;
            HealthFraction = healthFraction;
            Activity = activity;
            Targetable = targetable;
        }
    }

    private readonly struct ObservedCollisionAsteroid
    {
        internal readonly int Id;
        internal readonly Vector2 Position;
        internal readonly Vector2 HalfExtents;
        internal readonly float Rotation;
        internal readonly Vector2 Velocity;
        internal readonly float HealthFraction;

        internal ObservedCollisionAsteroid(
            int id,
            Vector2 position,
            Vector2 halfExtents,
            float rotation,
            Vector2 velocity,
            float healthFraction)
        {
            Id = id;
            Position = position;
            HalfExtents = halfExtents;
            Rotation = rotation;
            Velocity = velocity;
            HealthFraction = healthFraction;
        }
    }

    private readonly List<Ship> _allyCandidates = new List<Ship>();
    private readonly List<Ship> _enemyCandidates = new List<Ship>();
    private readonly List<ObservedMiningAsteroid> _miningAsteroidCandidates = new List<ObservedMiningAsteroid>();
    private readonly List<ObservedMapObject> _mapObjectCandidates = new List<ObservedMapObject>();
    private readonly List<ObservedCollisionAsteroid> _collisionAsteroidCandidates = new List<ObservedCollisionAsteroid>();
    private readonly float[] _navigationOccupancy = new float[NavigationGridCellCount];

    internal void Collect(Ship ship, int side, VectorSensor sensor)
    {
        Vector2 origin = ship.GetPosition();
        AddSelfObservations(ship, side, sensor, origin);
        CollectAllies(ship, side, origin);
        AddEntitySlots(sensor, _allyCandidates, MaxObservedAllies, origin);
        CollectVisibleEnemies(ship, side, origin);
        AddEntitySlots(sensor, _enemyCandidates, MaxObservedEnemies, origin);
        AddWeaponSlots(ship, sensor, origin);
        CollectVisibleMiningAsteroids(ship, side, origin);
        AddMiningAsteroidSlots(sensor, origin);
        CollectVisibleMapObjects(ship, side, origin);
        AddMapObjectSlots(sensor, origin);
        CollectVisibleEnvironment(ship, side, origin);
        AddCollisionAsteroidSlots(sensor, origin);
        AddNavigationGridObservations(sensor);
    }

    private static void AddSelfObservations(Ship ship, int side, VectorSensor sensor, Vector2 position)
    {
        AddEnumBits(sensor, (int)ship.ShipType, ShipTypeBitCount);
        Level level = ship.Level;
        sensor.AddObservation(NormalizeSignedCoordinate(position.x, level.MinX, level.MaxX));
        sensor.AddObservation(NormalizeSignedCoordinate(position.y, level.MinY, level.MaxY));
        sensor.AddObservation(NormalizePositive(Mathf.Max(0f, level.MaxX - level.MinX), 100f));
        sensor.AddObservation(NormalizePositive(Mathf.Max(0f, level.MaxY - level.MinY), 100f));
        AddHeading(sensor, ship.Rotation);
        sensor.AddObservation(GetHealthFraction(ship));
        sensor.AddObservation(NormalizePositive(ship.Speed, 20f));
        sensor.AddObservation(NormalizePositive(ship.CurrentSpeed, 20f));
        sensor.AddObservation(NormalizePositive(ship.RotationSpeed, 240f));
        sensor.AddObservation(NormalizePositive(ship.LongestSide, 10f));
        sensor.AddObservation(NormalizePositive(ship.Sight, 80f));
        sensor.AddObservation(NormalizePositive(ship.MaxRange, 80f));
        sensor.AddObservation(NormalizePositive(ship.Firepower, 200f));
        sensor.AddObservation(ship.IsMobile ? 1f : 0f);
        sensor.AddObservation(ship.IsBomber ? 1f : 0f);
        sensor.AddObservation(ship.IsCarrierShip ? 1f : 0f);
        sensor.AddObservation(ship.HasWeapons ? 1f : 0f);
        sensor.AddObservation(ship.HasTurrets ? 1f : 0f);
        sensor.AddObservation(HasSpecialAction(ship) ? 1f : 0f);
        sensor.AddObservation(GetSpecialReadiness(ship));

        GameState state = level.State;
        sensor.AddObservation(NormalizePositive(state.GetShips(side).Count, 32f));
        sensor.AddObservation(NormalizePositive(state.GetShipsVisibleToHiveMind(side).Count, 32f));
    }

    private void CollectAllies(Ship ship, int side, Vector2 origin)
    {
        _allyCandidates.Clear();
        List<Ship> ships = ship.Level.State.GetShips(side);
        for (int i = 0; i < ships.Count; i++)
        {
            Ship candidate = ships[i];
            if (candidate != null && candidate != ship && !candidate.IsDead)
            {
                _allyCandidates.Add(candidate);
            }
        }
        SortShipsForObservation(_allyCandidates, origin);
    }

    private void CollectVisibleEnemies(Ship ship, int side, Vector2 origin)
    {
        _enemyCandidates.Clear();
        foreach (Ship candidate in ship.Level.State.GetShipsVisibleToHiveMind(side))
        {
            if (candidate != null && !candidate.IsDead && candidate.Side != side)
            {
                _enemyCandidates.Add(candidate);
            }
        }
        SortShipsForObservation(_enemyCandidates, origin);
    }

    private static void SortShipsForObservation(List<Ship> ships, Vector2 origin)
    {
        ships.Sort((left, right) =>
        {
            int compare = (left.GetPosition() - origin).sqrMagnitude.CompareTo(
                (right.GetPosition() - origin).sqrMagnitude);
            if (compare != 0)
            {
                return compare;
            }
            compare = ((int)left.ShipType).CompareTo((int)right.ShipType);
            if (compare != 0)
            {
                return compare;
            }
            long leftFleetId = left.FleetShip != null ? left.FleetShip.Id : long.MaxValue;
            long rightFleetId = right.FleetShip != null ? right.FleetShip.Id : long.MaxValue;
            compare = leftFleetId.CompareTo(rightFleetId);
            return compare != 0 ? compare : left.Id.CompareTo(right.Id);
        });
    }

    private static void AddEntitySlots(VectorSensor sensor, List<Ship> ships, int slots, Vector2 origin)
    {
        for (int slot = 0; slot < slots; slot++)
        {
            if (slot >= ships.Count)
            {
                AddZeroObservations(sensor, EntityObservationSize);
                continue;
            }

            Ship observed = ships[slot];
            Vector2 relative = observed.GetPosition() - origin;
            sensor.AddObservation(1f);
            sensor.AddObservation(SquashSignedDistance(relative.x));
            sensor.AddObservation(SquashSignedDistance(relative.y));
            AddHeading(sensor, observed.Rotation);
            sensor.AddObservation(GetHealthFraction(observed));
            sensor.AddObservation(NormalizePositive(observed.Speed, 20f));
            sensor.AddObservation(NormalizePositive(observed.CurrentSpeed, 20f));
            sensor.AddObservation(NormalizePositive(observed.LongestSide, 10f));
            sensor.AddObservation(NormalizePositive(observed.MaxRange, 80f));
            sensor.AddObservation(NormalizePositive(observed.Firepower, 200f));
            sensor.AddObservation(observed.IsMobile ? 1f : 0f);
            sensor.AddObservation(observed.IsBomber ? 1f : 0f);
            AddEnumBits(sensor, (int)observed.ShipType, ShipTypeBitCount);
        }
    }

    private static void AddWeaponSlots(Ship ship, VectorSensor sensor, Vector2 origin)
    {
        // Weapon is an authored List rather than an unordered set. Its setup order is the stable slot identity.
        for (int slot = 0; slot < MaxWeaponSlots; slot++)
        {
            if (ship.Weapons == null || slot >= ship.Weapons.Count || ship.Weapons[slot] == null)
            {
                AddZeroObservations(sensor, WeaponObservationSize);
                continue;
            }

            Weapon weapon = ship.Weapons[slot];
            sensor.AddObservation(1f);
            AddEnumBits(sensor, (int)weapon.Type, WeaponTypeBitCount);
            Vector2 relative = weapon.GetPosition() - origin;
            float size = Mathf.Max(1f, ship.LongestSide);
            sensor.AddObservation(Mathf.Clamp(relative.x / size, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp(relative.y / size, -1f, 1f));
            sensor.AddObservation(NormalizePositive(weapon.Range, 80f));
            sensor.AddObservation(NormalizePositive(weapon.Power, 100f));
            sensor.AddObservation(NormalizePositive(weapon.RateOfFire, 5f));
            sensor.AddObservation(NormalizePositive(weapon.RotationRate, 240f));
            sensor.AddObservation(NormalizePositive(weapon.ProjectileValue, 2f));

            if (weapon is Turret turret)
            {
                sensor.AddObservation(1f);
                AddHeading(sensor, turret.Rotation);
                sensor.AddObservation(turret.ReadyToFire ? 1f : 0f);
                sensor.AddObservation(turret.IsAimedAtTarget ? 1f : 0f);
            }
            else
            {
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(weapon.HasTargetShip ? 1f : 0f);
                sensor.AddObservation(0f);
            }
        }
    }

    private void CollectVisibleMiningAsteroids(Ship ship, int side, Vector2 origin)
    {
        _miningAsteroidCandidates.Clear();
        foreach (MiningAsteroid asteroid in ship.Level.State.GetMiningAsteroidsVisibleToHiveMind(side))
        {
            if (asteroid == null || asteroid.IsDead)
            {
                continue;
            }

            Collider2D collider = asteroid.ClearanceMappingCollider != null
                ? asteroid.ClearanceMappingCollider
                : asteroid.Collider;
            GetColliderGeometry(ship.Level, collider, asteroid.GetPosition(), out Vector2 position, out Vector2 halfExtents);
            _miningAsteroidCandidates.Add(new ObservedMiningAsteroid(
                asteroid.Id,
                position,
                halfExtents,
                GetMiningAsteroidResourceFraction(asteroid),
                asteroid.SquadsMining.Count));
        }

        _miningAsteroidCandidates.Sort((left, right) =>
        {
            int compare = (left.Position - origin).sqrMagnitude.CompareTo((right.Position - origin).sqrMagnitude);
            return compare != 0 ? compare : left.Id.CompareTo(right.Id);
        });
    }

    private void AddMiningAsteroidSlots(VectorSensor sensor, Vector2 origin)
    {
        for (int slot = 0; slot < MaxObservedMiningAsteroids; slot++)
        {
            if (slot >= _miningAsteroidCandidates.Count)
            {
                AddZeroObservations(sensor, MiningAsteroidObservationSize);
                continue;
            }

            ObservedMiningAsteroid asteroid = _miningAsteroidCandidates[slot];
            Vector2 relative = asteroid.Position - origin;
            sensor.AddObservation(1f);
            sensor.AddObservation(SquashSignedDistance(relative.x));
            sensor.AddObservation(SquashSignedDistance(relative.y));
            sensor.AddObservation(asteroid.ResourceFraction);
            sensor.AddObservation(NormalizePositive(asteroid.HalfExtents.x, 20f));
            sensor.AddObservation(NormalizePositive(asteroid.HalfExtents.y, 20f));
            sensor.AddObservation(NormalizePositive(asteroid.Activity, 4f));
        }
    }

    private void CollectVisibleMapObjects(Ship ship, int side, Vector2 origin)
    {
        _mapObjectCandidates.Clear();
        foreach (MapObject mapObject in ship.Level.State.GetMapObjectsVisibleToHiveMind(side))
        {
            if (mapObject == null || mapObject.IsDead)
            {
                continue;
            }

            GetColliderGeometry(
                ship.Level,
                mapObject.Collider,
                mapObject.transform.localPosition,
                out Vector2 position,
                out Vector2 halfExtents);
            int type = mapObject is CanisterBomb
                ? FireTankObservationType
                : GenericMapObjectObservationType;
            float healthFraction = mapObject.MaxHealth > 0
                ? Mathf.Clamp01((float)mapObject.Health / mapObject.MaxHealth)
                : 0f;
            _mapObjectCandidates.Add(new ObservedMapObject(
                mapObject.Id,
                type,
                position,
                halfExtents,
                healthFraction,
                0f,
                true));
        }

        _mapObjectCandidates.Sort((left, right) =>
        {
            int compare = (left.Position - origin).sqrMagnitude.CompareTo((right.Position - origin).sqrMagnitude);
            if (compare != 0)
            {
                return compare;
            }
            compare = left.Type.CompareTo(right.Type);
            return compare != 0 ? compare : left.Id.CompareTo(right.Id);
        });
    }

    private void AddMapObjectSlots(VectorSensor sensor, Vector2 origin)
    {
        for (int slot = 0; slot < MaxObservedMapObjects; slot++)
        {
            if (slot >= _mapObjectCandidates.Count)
            {
                AddZeroObservations(sensor, MapObjectObservationSize);
                continue;
            }

            ObservedMapObject mapObject = _mapObjectCandidates[slot];
            Vector2 relative = mapObject.Position - origin;
            sensor.AddObservation(1f);
            AddEnumBits(sensor, mapObject.Type, MapObjectTypeBitCount);
            sensor.AddObservation(SquashSignedDistance(relative.x));
            sensor.AddObservation(SquashSignedDistance(relative.y));
            sensor.AddObservation(mapObject.HealthFraction);
            sensor.AddObservation(NormalizePositive(mapObject.HalfExtents.x, 20f));
            sensor.AddObservation(NormalizePositive(mapObject.HalfExtents.y, 20f));
            sensor.AddObservation(mapObject.Targetable ? 1f : 0f);
            sensor.AddObservation(NormalizePositive(mapObject.Activity, 4f));
        }
    }

    private void CollectVisibleEnvironment(Ship ship, int side, Vector2 origin)
    {
        _collisionAsteroidCandidates.Clear();
        for (int i = 0; i < _navigationOccupancy.Length; i++)
        {
            _navigationOccupancy[i] = 0f;
        }
        MarkNavigationBounds(_navigationOccupancy, ship.Level, origin);

        foreach (Obstacle obstacle in ship.Level.State.GetObstaclesVisibleToHiveMind(side))
        {
            if (obstacle == null || obstacle.IsDead || obstacle is MiningAsteroid || obstacle is AsteroidPiece)
            {
                continue;
            }

            Collider2D collider = obstacle.ClearanceMappingCollider != null
                ? obstacle.ClearanceMappingCollider
                : obstacle.Collider;
            GetColliderGeometry(ship.Level, collider, obstacle.GetPosition(), out Vector2 position, out Vector2 halfExtents);

            if (obstacle is CollisionAsteroid collisionAsteroid)
            {
                Vector2 velocity = collisionAsteroid.Body != null
                    ? GetLevelLocalVelocity(ship.Level, collisionAsteroid.Body.linearVelocity)
                    : Vector2.zero;
                float healthFraction = collisionAsteroid.OriginalHealth > 0
                    ? Mathf.Clamp01((float)collisionAsteroid.Health / collisionAsteroid.OriginalHealth)
                    : 0f;
                _collisionAsteroidCandidates.Add(new ObservedCollisionAsteroid(
                    collisionAsteroid.Id,
                    position,
                    halfExtents,
                    collisionAsteroid.transform.localEulerAngles.z,
                    velocity,
                    healthFraction));
                continue;
            }

            if (obstacle.ObstacleType == ConfigData.ObstacleTypes.StaticObstacle)
            {
                MarkNavigationAabb(_navigationOccupancy, origin, position, halfExtents);
            }
        }

        _collisionAsteroidCandidates.Sort((left, right) =>
        {
            int compare = DistanceSquaredToBounds(left, origin).CompareTo(DistanceSquaredToBounds(right, origin));
            return compare != 0 ? compare : left.Id.CompareTo(right.Id);
        });
    }

    private void AddCollisionAsteroidSlots(VectorSensor sensor, Vector2 origin)
    {
        for (int slot = 0; slot < MaxObservedCollisionAsteroids; slot++)
        {
            if (slot >= _collisionAsteroidCandidates.Count)
            {
                AddZeroObservations(sensor, CollisionAsteroidObservationSize);
                continue;
            }

            ObservedCollisionAsteroid asteroid = _collisionAsteroidCandidates[slot];
            Vector2 relative = asteroid.Position - origin;
            sensor.AddObservation(1f);
            sensor.AddObservation(SquashSignedDistance(relative.x));
            sensor.AddObservation(SquashSignedDistance(relative.y));
            sensor.AddObservation(NormalizePositive(asteroid.HalfExtents.x, 20f));
            sensor.AddObservation(NormalizePositive(asteroid.HalfExtents.y, 20f));
            AddHeading(sensor, asteroid.Rotation);
            sensor.AddObservation(SquashSignedDistance(asteroid.Velocity.x));
            sensor.AddObservation(SquashSignedDistance(asteroid.Velocity.y));
            sensor.AddObservation(asteroid.HealthFraction);
            sensor.AddObservation(1f); // Collision asteroids are destructible point-fire targets.
        }
    }

    private void AddNavigationGridObservations(VectorSensor sensor)
    {
        for (int i = 0; i < _navigationOccupancy.Length; i++)
        {
            sensor.AddObservation(_navigationOccupancy[i]);
        }
    }

    private static void MarkNavigationBounds(float[] occupancy, Level level, Vector2 origin)
    {
        if (level == null || level.MaxX <= level.MinX || level.MaxY <= level.MinY)
        {
            return;
        }

        float halfSpan = NavigationGridSize * NavigationGridCellSize * 0.5f;
        float gridMinX = origin.x - halfSpan;
        float gridMinY = origin.y - halfSpan;
        for (int y = 0; y < NavigationGridSize; y++)
        {
            float cellMinY = gridMinY + y * NavigationGridCellSize;
            float cellMaxY = cellMinY + NavigationGridCellSize;
            for (int x = 0; x < NavigationGridSize; x++)
            {
                float cellMinX = gridMinX + x * NavigationGridCellSize;
                float cellMaxX = cellMinX + NavigationGridCellSize;
                if (cellMinX < level.MinX || cellMaxX > level.MaxX ||
                    cellMinY < level.MinY || cellMaxY > level.MaxY)
                {
                    occupancy[y * NavigationGridSize + x] = 1f;
                }
            }
        }
    }

    /// <summary>
    /// Marks an axis-aligned local occupancy footprint. Static geometry is persistent knowledge, so
    /// iteration order is intentionally irrelevant: every overlapping cell is simply set to blocked.
    /// </summary>
    internal static void MarkNavigationAabb(float[] occupancy, Vector2 origin, Vector2 position, Vector2 halfExtents)
    {
        if (occupancy == null || occupancy.Length != NavigationGridCellCount)
        {
            return;
        }

        float halfSpan = NavigationGridSize * NavigationGridCellSize * 0.5f;
        float gridMinX = origin.x - halfSpan;
        float gridMinY = origin.y - halfSpan;
        float gridMaxX = gridMinX + NavigationGridSize * NavigationGridCellSize;
        float gridMaxY = gridMinY + NavigationGridSize * NavigationGridCellSize;
        float obstacleMinX = position.x - Mathf.Abs(halfExtents.x);
        float obstacleMaxX = position.x + Mathf.Abs(halfExtents.x);
        float obstacleMinY = position.y - Mathf.Abs(halfExtents.y);
        float obstacleMaxY = position.y + Mathf.Abs(halfExtents.y);

        if (obstacleMaxX <= gridMinX || obstacleMinX >= gridMaxX ||
            obstacleMaxY <= gridMinY || obstacleMinY >= gridMaxY)
        {
            return;
        }

        int minX = Mathf.Clamp(Mathf.FloorToInt((obstacleMinX - gridMinX) / NavigationGridCellSize), 0, NavigationGridSize - 1);
        int maxX = Mathf.Clamp(Mathf.CeilToInt((obstacleMaxX - gridMinX) / NavigationGridCellSize) - 1, 0, NavigationGridSize - 1);
        int minY = Mathf.Clamp(Mathf.FloorToInt((obstacleMinY - gridMinY) / NavigationGridCellSize), 0, NavigationGridSize - 1);
        int maxY = Mathf.Clamp(Mathf.CeilToInt((obstacleMaxY - gridMinY) / NavigationGridCellSize) - 1, 0, NavigationGridSize - 1);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                occupancy[y * NavigationGridSize + x] = 1f;
            }
        }
    }

    private static void GetColliderGeometry(
        Level level,
        Collider2D collider,
        Vector2 fallbackPosition,
        out Vector2 position,
        out Vector2 halfExtents)
    {
        position = fallbackPosition;
        halfExtents = Vector2.zero;
        if (collider == null || !collider.enabled)
        {
            return;
        }

        Bounds bounds = collider.bounds;
        Vector2 min = PathfinderObstacleScope.WorldToLevel(level, bounds.min);
        Vector2 max = PathfinderObstacleScope.WorldToLevel(level, bounds.max);
        position = (min + max) * 0.5f;
        halfExtents = new Vector2(
            Mathf.Abs(max.x - min.x) * 0.5f,
            Mathf.Abs(max.y - min.y) * 0.5f);
    }

    private static Vector2 GetLevelLocalVelocity(Level level, Vector2 worldVelocity)
    {
        Transform mapTransform = level?.Map?.Transform;
        return mapTransform != null
            ? (Vector2)mapTransform.InverseTransformVector(worldVelocity)
            : worldVelocity;
    }

    private static float DistanceSquaredToBounds(ObservedCollisionAsteroid asteroid, Vector2 origin)
    {
        Vector2 relative = asteroid.Position - origin;
        float dx = Mathf.Max(0f, Mathf.Abs(relative.x) - asteroid.HalfExtents.x);
        float dy = Mathf.Max(0f, Mathf.Abs(relative.y) - asteroid.HalfExtents.y);
        return dx * dx + dy * dy;
    }

    private static bool HasSpecialAction(Ship ship)
    {
        return ship is YellowJacket || ship is Striker || ship is FireBarge || ship is Barge || ship is Scout;
    }

    private static float GetSpecialReadiness(Ship ship)
    {
        if (ship is YellowJacket)
        {
            return 1f;
        }
        if (ship is Striker striker)
        {
            return striker.IsBombReady ? 1f : 0f;
        }
        if (ship is Barge barge)
        {
            return !barge.HasStartedCharging && !barge.IsCharging ? 1f : 0f;
        }
        if (ship is FireBarge)
        {
            return 1f;
        }
        if (ship is Scout scout)
        {
            return scout.IsBeaconReady ? 1f : 0f;
        }
        return 0f;
    }

    private static float GetHealthFraction(Ship ship)
    {
        return ship != null && ship.MaxHealth > 0
            ? Mathf.Clamp01((float)ship.Health / ship.MaxHealth)
            : 0f;
    }

    private static float GetMiningAsteroidResourceFraction(MiningAsteroid asteroid)
    {
        return asteroid != null && asteroid.OriginalHealth > 0
            ? Mathf.Clamp01((float)asteroid.Health / asteroid.OriginalHealth)
            : 0f;
    }

    internal static float SquashSignedDistance(float value)
    {
        float absolute = Mathf.Abs(value);
        return absolute <= 0f ? 0f : Mathf.Sign(value) * absolute / (absolute + LocalDistanceScale);
    }

    private static float NormalizeSignedCoordinate(float value, float minimum, float maximum)
    {
        float range = maximum - minimum;
        if (range <= 0.0001f)
        {
            return 0f;
        }
        return Mathf.Clamp(((value - minimum) / range) * 2f - 1f, -1f, 1f);
    }

    private static float NormalizePositive(float value, float scale)
    {
        float positive = Mathf.Max(0f, value);
        return positive <= 0f ? 0f : positive / (positive + Mathf.Max(0.0001f, scale));
    }

    private static void AddEnumBits(VectorSensor sensor, int value, int bits)
    {
        for (int bit = 0; bit < bits; bit++)
        {
            sensor.AddObservation((value & (1 << bit)) != 0 ? 1f : 0f);
        }
    }

    private static void AddHeading(VectorSensor sensor, float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        sensor.AddObservation(Mathf.Sin(radians));
        sensor.AddObservation(Mathf.Cos(radians));
    }

    private static void AddZeroObservations(VectorSensor sensor, int count)
    {
        for (int i = 0; i < count; i++)
        {
            sensor.AddObservation(0f);
        }
    }
}
