using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        private readonly List<SavedSquad> _randomSquadBuffer = new List<SavedSquad>();
        private int _randomQueenCount;

        private static float GetRlShipClearanceRadius(ConfigData.ShipTypes shipType)
        {
            return global::RlOneVsOneArenaMapSizeState.GetRotationSafeShipRadius(shipType);
        }

        private static float GetRlShipClearanceRadius(Ship ship)
        {
            float halfWidth = Mathf.Max(0f, ship.GetHalfWidth());
            float halfHeight = Mathf.Max(0f, ship.GetHalfHeight());
            return Mathf.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight) +
                   global::RlOneVsOneArenaMapSizeState.SpawnSafetyMargin;
        }

        private void SetupShipsForSide(int side)
        {
            bool rlOneVsOneTraining = global::RlOneVsOneTrainingBootstrap.IsActiveFor(Stage);
            if (rlOneVsOneTraining && side == ConfigData.Configuration.AISide)
            {
                // SetupShips always processes the AI side first. Advance this arena's balanced matchup
                // cycle exactly once here so both sides use the same prepared pair for the whole episode.
                global::RlOneVsOnePerArenaMatchups.PrepareEpisode(this);
                ConfigureRlOneVsOneSpawnPositions();
            }

            bool generateRandomSquads = Stage.IsTrainingNueralNetwork ||
                                        Stage.UseFullyRandomSquads ||
                                        ((Stage.UseFullyRandomEnemySquads || CurrentLevelOptions.EnemySquadGenerationCount > 0) &&
                                         side == ConfigData.Configuration.AISide);

            if (rlOneVsOneTraining)
            {
                AddRlOneVsOneSquadForSetup(side);
            }
            else if (generateRandomSquads)
            {
                AddRandomSquadsForSetup(side);
            }
            else if ((Stage.UseOverrideSquads && side == ConfigData.Configuration.UserSide) ||
                     (Stage.UseOverrideEnemySquads && side == ConfigData.Configuration.AISide))
            {
                LevelConstructor.AddOverrideSquads(side);
            }

            if (side == ConfigData.Configuration.AISide)
            {
                List<int> existingSquadIds = CurrentLevelOptions.EnemyExistingSquads;
                for (int i = 0; i < existingSquadIds.Count; i++)
                {
                    SavedSquad existingSquad = ConfigData.CurrentShips.GetSavedSquad(existingSquadIds[i]);
                    if (existingSquad != null)
                    {
                        CurrentLevelOptions.EnemySquads.Add(existingSquad);
                    }
                }
                LevelConstructor.SpawnShipsAndSquads(
                    CurrentLevelOptions.EnemySquads,
                    StartingPositions[side - 1],
                    Vector2.zero,
                    false);
            }
            else
            {
                LevelConstructor.SpawnShipsAndSquads(
                    CurrentLevelOptions.ChosenSquads,
                    StartingPositions[side - 1],
                    Vector2.zero,
                    false);
            }

            if (rlOneVsOneTraining)
            {
                EnsureRlSpawnedShipsAreHazardClear(side);
                RandomizeRlOneVsOneFacing(side);
            }
        }

        private void ConfigureRlOneVsOneSpawnPositions()
        {
            float spawnRadius = global::RlOneVsOneArenaMapSizeState.GetSpawnRadius(this);
            float angle = Random.Range(0f, Mathf.PI * 2f);
            Vector2 offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * spawnRadius;
            StartingPositions[ConfigData.Configuration.BeeSide - 1] = -offset;
            StartingPositions[ConfigData.Configuration.HumanSide - 1] = offset;
        }

        private void EnsureRlSpawnedShipsAreHazardClear(int side)
        {
            List<Ship> ships = State.GetShips(side);
            if (ships.Count == 0)
            {
                return;
            }

            HashSet<Squad> movedSquads = new HashSet<Squad>();
            for (int shipIndex = 0; shipIndex < ships.Count; shipIndex++)
            {
                Ship ship = ships[shipIndex];
                if (ship == null || ship.IsDead)
                {
                    continue;
                }

                float clearance = GetRlShipClearanceRadius(ship);
                Vector2 current = ship.transform.localPosition;
                if (IsRlShipPositionHazardClear(current, clearance))
                {
                    continue;
                }

                if (!TryFindNearestRlHazardClearPosition(current, clearance, out Vector2 safePosition))
                {
                    throw new System.InvalidOperationException(
                        $"RL training could not place {ship.ShipType} on side {side} clear of the map border and lethal obstacles.");
                }

                ship.transform.localPosition = safePosition;
                if (ship.Squad != null)
                {
                    movedSquads.Add(ship.Squad);
                }
            }

            foreach (Squad squad in movedSquads)
            {
                squad.SetOffsets();
            }
        }

        private bool TryFindNearestRlHazardClearPosition(
            Vector2 requested,
            float clearance,
            out Vector2 safePosition)
        {
            if (IsRlShipPositionHazardClear(requested, clearance))
            {
                safePosition = requested;
                return true;
            }

            int step = Mathf.Max(1, Pathfinder.Scale);
            int maxSearchDistance = Mathf.Max(MapWidth, MapHeight);
            int maxRadius = Mathf.CeilToInt((float)maxSearchDistance / step);
            for (int radius = 1; radius <= maxRadius; radius++)
            {
                Vector2 best = Vector2.zero;
                float bestDistance = float.MaxValue;
                bool found = false;
                for (int x = -radius; x <= radius; x++)
                {
                    for (int y = -radius; y <= radius; y++)
                    {
                        if (Mathf.Abs(x) != radius && Mathf.Abs(y) != radius)
                        {
                            continue;
                        }

                        Vector2 candidate = requested + new Vector2(x * step, y * step);
                        if (!IsRlShipPositionHazardClear(candidate, clearance))
                        {
                            continue;
                        }

                        float distance = (candidate - requested).sqrMagnitude;
                        if (!found || distance < bestDistance)
                        {
                            found = true;
                            bestDistance = distance;
                            best = candidate;
                        }
                    }
                }

                if (found)
                {
                    safePosition = best;
                    return true;
                }
            }

            // The RL obstacle generator always leaves the center cross clear. Checking the center
            // explicitly also covers coarse Pathfinder-scale searches on maps where a very large
            // ship has only a narrow valid center region.
            if (IsRlShipPositionHazardClear(Vector2.zero, clearance))
            {
                safePosition = Vector2.zero;
                return true;
            }

            safePosition = requested;
            return false;
        }

        private bool IsRlShipPositionHazardClear(Vector2 shipPosition, float shipExtent)
        {
            if (shipPosition.x - shipExtent <= MinX ||
                shipPosition.x + shipExtent >= MaxX ||
                shipPosition.y - shipExtent <= MinY ||
                shipPosition.y + shipExtent >= MaxY)
            {
                return false;
            }

            if (!global::RlOneVsOneTrainingBootstrap.CurrentStaticObstaclesEnabled ||
                ObstacleMap == null ||
                ObstacleMap.Obstacles == null)
            {
                return true;
            }

            for (int obstacleIndex = 0; obstacleIndex < ObstacleMap.Obstacles.Count; obstacleIndex++)
            {
                StaticObstacle obstacle = ObstacleMap.Obstacles[obstacleIndex];
                if (obstacle == null || obstacle.IsDead || !obstacle.KillsShipsOnContact ||
                    obstacle.Collider == null)
                {
                    continue;
                }

                Bounds worldBounds = obstacle.Collider.bounds;
                Vector3 localMin = Map.Transform.InverseTransformPoint(worldBounds.min);
                Vector3 localMax = Map.Transform.InverseTransformPoint(worldBounds.max);
                float obstacleMinX = Mathf.Min(localMin.x, localMax.x);
                float obstacleMaxX = Mathf.Max(localMin.x, localMax.x);
                float obstacleMinY = Mathf.Min(localMin.y, localMax.y);
                float obstacleMaxY = Mathf.Max(localMin.y, localMax.y);

                if (shipPosition.x + shipExtent > obstacleMinX &&
                    shipPosition.x - shipExtent < obstacleMaxX &&
                    shipPosition.y + shipExtent > obstacleMinY &&
                    shipPosition.y - shipExtent < obstacleMaxY)
                {
                    return false;
                }
            }

            return true;
        }

        private void RandomizeRlOneVsOneFacing(int side)
        {
            List<Ship> ships = State.GetShips(side);
            for (int i = 0; i < ships.Count; i++)
            {
                Ship ship = ships[i];
                Vector3 euler = ship.transform.localEulerAngles;
                euler.z = Random.Range(0f, 360f);
                ship.transform.localEulerAngles = euler;

                // Ship movement and turret aiming use their cached world rotations rather than
                // reading the Transform every frame. Keep those caches synchronized with the
                // randomized visual facing or the ship can physically travel tail-first.
                ship.Rotation = ship.transform.eulerAngles.z;
                for (int turretIndex = 0; turretIndex < ship.Turrets.Count; turretIndex++)
                {
                    ship.Turrets[turretIndex].Rotation = ship.Turrets[turretIndex].PieceTransform.eulerAngles.z;
                }
            }
        }

        private void AddRlOneVsOneSquadForSetup(int side)
        {
            int shipCount = global::RlOneVsOneTrainingBootstrap.CurrentShipsPerSide;

            // Dedicated RL finalizes after server settings and deliberately never loads the player's
            // fleet/profile facade. Hash() is already process-unique, so transient negative IDs do not
            // need the CurrentShips collection-count salt used by ordinary player-facing generation.
            long squadId = -Utilities.Hash();
            SavedSquad savedSquad = new SavedSquad(
                squadId,
                side,
                $"RL training squad #{squadId}",
                Vector2.zero,
                false,
                false,
                ConfigData.DefaultShootingStrategy,
                ConfigData.UnsetColor,
                null);

            for (int shipIndex = 0; shipIndex < shipCount; shipIndex++)
            {
                ConfigData.ShipTypes type = global::RlOneVsOnePerArenaMatchups.GetShipType(this, side, shipIndex);
                if (Utilities.ConvertShipTypeToSide[type] != side)
                {
                    throw new System.InvalidOperationException(
                        $"RL training requires a ship belonging to side {side}; configured type at slot {shipIndex} was {type}.");
                }

                long fleetShipId = -Utilities.Hash();
                FleetShip fleetShip = new FleetShip(
                    fleetShipId,
                    type,
                    false,
                    false,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0);

                // Preserve the exact original spawn for the default 1v1. Larger teams use a compact,
                // deterministic formation around the same side-specific squad starting position.
                if (shipCount == 1)
                {
                    savedSquad.AddShipToSquad(new SquadShip(fleetShip, Vector2.zero));
                }
                else
                {
                    Vector2 shipOffset = global::RlOneVsOneArenaMapSizeState.GetShipFormationOffset(this, shipIndex);
                    savedSquad.AddShipToSquad(new SquadShip(fleetShip, shipOffset));
                }
            }

            if (side == ConfigData.Configuration.AISide)
            {
                CurrentLevelOptions.EnemySquads.Add(savedSquad);
            }
            else
            {
                CurrentLevelOptions.ChosenSquads.Add(savedSquad);
            }
        }

        private void AddRandomSquadsForSetup(int side)
        {
            _randomQueenCount = 0;
            bool noVisibleArmedTypes = HasNoVisibleArmedTypes(side);

            for (int option = 0; option < (ActivateLoadingShipsMidLevel ? 2 : 1); option++)
            {
                bool hasArmedSquads = false;
                _randomSquadBuffer.Clear();

                for (int i = 0; i < CurrentLevelOptions.EnemySquadGenerationCount; i++)
                {
                    // Preserve the legacy random draw order exactly. Human-side generation first
                    // consumes a Bee-type draw and then replaces it with the Human-type draw.
                    ConfigData.ShipTypes type = Stage.BeeShipTypes[Random.Range(0, Stage.BeeShipTypes.Count)];
                    if (side == ConfigData.Configuration.HumanSide)
                    {
                        type = Stage.HumanShipTypes[Random.Range(0, Stage.HumanShipTypes.Count)];
                    }
                    while (side == ConfigData.Configuration.BeeSide &&
                           type == ConfigData.ShipTypes.Queen &&
                           Stage.BeeShipTypes.Count > 1 &&
                           (HasObstacles || _randomQueenCount == 2 || Utilities.RandomInt(4) != 3))
                    {
                        type = Stage.BeeShipTypes[Random.Range(0, Stage.BeeShipTypes.Count)];
                    }

                    long squadId = Utilities.GetNegativeSavedSquadId();
                    SavedSquad savedSquad = new SavedSquad(
                        squadId,
                        side,
                        $"{type}s #{squadId}",
                        Vector2.zero,
                        false,
                        false,
                        ConfigData.DefaultShootingStrategy,
                        ConfigData.UnsetColor,
                        null);
                    savedSquad.SetupRandomShips(type);
                    _randomSquadBuffer.Add(savedSquad);

                    if (type == ConfigData.ShipTypes.Queen)
                    {
                        _randomQueenCount++;
                    }

                    if (ConfigData.ArmedShipTypes.Contains(type) ||
                        (side == ConfigData.Configuration.BeeSide && Stage.OverrideBeeShipTypes.Count > 0) ||
                        (side == ConfigData.Configuration.HumanSide && Stage.OverrideHumanShipTypes.Count > 0) ||
                        CurrentLevelOptions.EnemyShipTypeOption != 0 ||
                        noVisibleArmedTypes)
                    {
                        hasArmedSquads = true;
                    }

                    if (i == CurrentLevelOptions.EnemySquadGenerationCount - 1 && !hasArmedSquads)
                    {
                        i--;
                        _randomSquadBuffer.RemoveAt(_randomSquadBuffer.Count - 1);
                    }
                }

                if (option == 0)
                {
                    if (side == ConfigData.Configuration.AISide)
                    {
                        CurrentLevelOptions.EnemySquads.AddRange(_randomSquadBuffer);
                    }
                    else
                    {
                        CurrentLevelOptions.ChosenSquads.AddRange(_randomSquadBuffer);
                    }
                }
                else if (side == ConfigData.Configuration.AISide)
                {
                    CurrentLevelOptions.EnemyReinforcements.AddRange(_randomSquadBuffer);
                }
            }
        }

        private static bool HasNoVisibleArmedTypes(int side)
        {
            if (side == ConfigData.Configuration.BeeSide)
            {
                foreach (ConfigData.ShipTypes type in ConfigData.UserProgressData.VisibleBeeShipTypes)
                {
                    if (ConfigData.ArmedShipTypes.Contains(type))
                    {
                        return false;
                    }
                }
                return true;
            }

            foreach (ConfigData.ShipTypes type in ConfigData.UserProgressData.VisibleHumanShipTypes)
            {
                if (ConfigData.ArmedShipTypes.Contains(type))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
