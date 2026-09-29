using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        private void RandomizeOptions()
        {
            bool logEnvironment = !Stage.IsTraining;
            if (CurrentLevelOptions.MapIndex == -1)
            {
                CurrentLevelOptions.MapIndex = Utilities.RandomInt(Stage.Prefabs.Maps.Count);
            }
            MapData = ConfigData.Maps[CurrentLevelOptions.MapIndex];
            Map = Stage.Pool.GetPooledMap(CurrentLevelOptions.MapIndex);

            bool hiveMindTraining = Stage.IsTrainingHiveMind;
            bool useStaticObstacles = hiveMindTraining
                ? Utilities.CoinToss()
                : (((CurrentLevelOptions.Obstacles == "" && Utilities.CoinToss()) || CurrentLevelOptions.Obstacles != "No") && !Stage.IsTraining);

            // Dedicated Hive Mind training should learn the same environmental dimensions it can
            // encounter in play. The authored training LevelOptions default to "No" obstacles, so
            // choose the static-obstacle dimension explicitly instead of inheriting that default.
            if (hiveMindTraining)
            {
                CurrentLevelOptions.Obstacles = useStaticObstacles ? "" : "No";
            }

            if (useStaticObstacles)
            {
                HasObstacles = true;

                bool useAsteroids = hiveMindTraining
                    ? Utilities.CoinToss()
                    : (CurrentLevelOptions.AsteroidOption == -1 && Utilities.RandomInt(4) == 0) || CurrentLevelOptions.AsteroidOption > 0;
                SetAsteroidOptionForTraining(hiveMindTraining, useAsteroids);
                ActivateCollisionAsteroids = useAsteroids;
                if (logEnvironment)
                {
                }
            }
            else
            {
                bool useAsteroids = hiveMindTraining
                    ? Utilities.CoinToss()
                    : (((CurrentLevelOptions.AsteroidOption == -1 && Utilities.CoinToss()) || CurrentLevelOptions.AsteroidOption > 0) && !Stage.IsTraining);
                SetAsteroidOptionForTraining(hiveMindTraining, useAsteroids);

                CurrentLevelOptions.Obstacles = "No";
                ActivateCollisionAsteroids = useAsteroids;
                HasObstacles = useAsteroids;
                if (logEnvironment)
                {
                }
            }

            if (Stage.DoesUserHaveController && ((CurrentLevelOptions.FogOfWar == -1 && Utilities.CoinToss()) || CurrentLevelOptions.FogOfWar == 1))
            {
                ActivateFogOfWar = true;
            }
            else
            {
                ActivateFogOfWar = false;
            }

            if ((CurrentLevelOptions.Mining == -1 && !HasObstacles && Utilities.CoinToss()) || CurrentLevelOptions.Mining == 1)
            {
                ActivateMining = true;
            }
            else
            {
                ActivateMining = false;
            }

            // This currently has an override (the " && false" at the end) to prevent reinforcements.
            if (((CurrentLevelOptions.EnemyReinforcementsOption == -1 && Utilities.CoinToss()) || CurrentLevelOptions.EnemyReinforcementsOption == 1) && false)
            {
                ActivateLoadingShipsMidLevel = true;
                if (CurrentLevelOptions.EnemyReinforcements.Count == 0)
                {
                    CurrentLevelOptions.EnemyReinforcements = CurrentLevelOptions.EnemySquads.ToList();
                }
            }
            else
            {
                ActivateLoadingShipsMidLevel = false;
            }
        }

        private void SetAsteroidOptionForTraining(bool hiveMindTraining, bool useAsteroids)
        {
            if (!hiveMindTraining)
            {
                return;
            }

            // Exercise both normal and doubled-frequency asteroid encounters while keeping "none"
            // explicit when the asteroid dimension is disabled for this episode.
            CurrentLevelOptions.AsteroidOption = useAsteroids
                ? (Utilities.CoinToss() ? 1 : 2)
                : 0;
        }

        private List<Ship> _clearance_Ships = new List<Ship>();
        private float _clearance_width, _clearance_height;
        private int _f_clearance;
        public void CalculateShipClearances()
        {
            _clearance_Ships = State.GetShips();
            while (_clearance_Ships.Count > 0)
            {
                if (!Stage.ShipClearances.ContainsKey(_clearance_Ships[0].ShipType))
                {
                    _clearance_width = _clearance_Ships[0].GetHalfWidth();
                    _clearance_height = _clearance_Ships[0].GetHalfHeight();
                    _f_clearance = (_clearance_width > _clearance_height ? Mathf.CeilToInt(_clearance_width) : Mathf.CeilToInt(_clearance_height));

                    while (_f_clearance % Pathfinder.Scale > 0)
                    {
                        _f_clearance++;
                    }
                    _f_clearance /= Pathfinder.Scale;
                    _f_clearance = Math.Max(_f_clearance, ConfigData.MinimumClearance);

                    Stage.ShipClearances.Add(_clearance_Ships[0].ShipType, _f_clearance);
                    _clearance_Ships.ForEach((s) =>
                    {
                        if (s.ShipType == _clearance_Ships[0].ShipType)
                        {
                            s.Clearance = _f_clearance;
                        }
                    });

                    if (_f_clearance > MaximumClearance)
                    {
                        MaximumClearance = _f_clearance;
                    }
                }

                _clearance_Ships = _clearance_Ships.Where((s) => s.ShipType != _clearance_Ships[0].ShipType).ToList();
            }
        }

        private const float RlStaticObstacleMaximumAreaFraction = 0.25f;
        private const int RlStaticObstacleMaximumCount = 4;
        private const float RlStaticObstacleMinimumSize = Pathfinder.Scale;
        private const float RlStaticObstacleBoundaryMargin = 1f;

        private StaticObstaclePool _staticObstaclePool;
        private bool _usesPooledStaticObstaclePrefabs;
        private System.Random _rlEnvironmentRandom;

        private StaticObstaclePool GetStaticObstaclePool()
        {
            if (_staticObstaclePool == null)
            {
                _staticObstaclePool = StaticObstaclePool.GetOrCreate(Stage);
            }
            return _staticObstaclePool;
        }

        private void GenerateRandomObstacles()
        {
            StaticObstaclePool obstaclePool = GetStaticObstaclePool();
            Vector2 maxSpawnDistance = new Vector2(MaxX - 150, MaxY - 150);
            ObstacleMap.ObstacleBackground = obstaclePool.GetBackground(Map.transform);
            for (int i = 0; i < Utilities.RandomInt(10) + 1; i++)
            {
                StaticObstacle obstacle = obstaclePool.GetObstacle(Map.transform);
                if (Utilities.CoinToss())
                {
                    obstacle.transform.localScale = new Vector2(Utilities.RandomInt(150) + 20, Utilities.RandomInt(50) + 20);
                }
                else
                {
                    obstacle.transform.localScale = new Vector2(Utilities.RandomInt(50) + 20, Utilities.RandomInt(150) + 20);
                }
                obstacle.transform.localPosition = Utilities.RandomCoordinate(this, Vector2.zero, maxSpawnDistance - new Vector2(0, obstacle.transform.localScale.y / 2), Vector2.zero);
                obstacle.Collider.enabled = false;
                obstacle.Collider.enabled = true;
                ObstacleMap.Obstacles.Add(obstacle);
            }
        }

        private System.Random GetRlEnvironmentRandom()
        {
            if (_rlEnvironmentRandom == null)
            {
                _rlEnvironmentRandom = new System.Random(global::RlOneVsOneScenarioSeed.Create(
                    this,
                    global::RlOneVsOneScenarioSeed.EnvironmentStreamSalt));
            }
            return _rlEnvironmentRandom;
        }

        private void GenerateRlTrainingObstacles()
        {
            StaticObstaclePool obstaclePool = GetStaticObstaclePool();
            System.Random random = GetRlEnvironmentRandom();
            List<Rect> layout = BuildRlTrainingObstacleLayout(
                MinX,
                MaxX,
                MinY,
                MaxY,
                random.Next());

            for (int i = 0; i < layout.Count; i++)
            {
                Rect rect = layout[i];
                StaticObstacle obstacle = obstaclePool.GetObstacle(Map.transform);
                obstacle.KillsShipsOnContact = true;
                obstacle.transform.localRotation = Quaternion.identity;
                obstacle.transform.localPosition = rect.center;
                obstacle.transform.localScale = rect.size;
                obstacle.Collider.enabled = false;
                obstacle.Collider.enabled = true;
                ObstacleMap.Obstacles.Add(obstacle);
            }
        }

        internal static List<Rect> BuildRlTrainingObstacleLayout(
            float minX,
            float maxX,
            float minY,
            float maxY,
            int seed)
        {
            List<Rect> layout = new List<Rect>();
            float playableWidth = Mathf.Max(0f, maxX - minX);
            float playableHeight = Mathf.Max(0f, maxY - minY);
            if (playableWidth <= 0f || playableHeight <= 0f)
            {
                return layout;
            }

            float maximumArea = playableWidth * playableHeight * RlStaticObstacleMaximumAreaFraction;
            float centerX = (minX + maxX) * 0.5f;
            float centerY = (minY + maxY) * 0.5f;

            // Keep the middle half of the smaller playable dimension clear on both axes. The
            // resulting full-width/full-height cross guarantees that corner obstacles cannot join
            // into a wall that partitions the arena.
            float corridorHalfWidth = Mathf.Min(playableWidth, playableHeight) * 0.25f;
            Rect[] cells =
            {
                Rect.MinMaxRect(
                    minX + RlStaticObstacleBoundaryMargin,
                    minY + RlStaticObstacleBoundaryMargin,
                    centerX - corridorHalfWidth,
                    centerY - corridorHalfWidth),
                Rect.MinMaxRect(
                    centerX + corridorHalfWidth,
                    minY + RlStaticObstacleBoundaryMargin,
                    maxX - RlStaticObstacleBoundaryMargin,
                    centerY - corridorHalfWidth),
                Rect.MinMaxRect(
                    minX + RlStaticObstacleBoundaryMargin,
                    centerY + corridorHalfWidth,
                    centerX - corridorHalfWidth,
                    maxY - RlStaticObstacleBoundaryMargin),
                Rect.MinMaxRect(
                    centerX + corridorHalfWidth,
                    centerY + corridorHalfWidth,
                    maxX - RlStaticObstacleBoundaryMargin,
                    maxY - RlStaticObstacleBoundaryMargin),
            };

            System.Random random = new System.Random(seed);
            int[] cellOrder = { 0, 1, 2, 3 };
            for (int i = cellOrder.Length - 1; i > 0; i--)
            {
                int swapIndex = random.Next(i + 1);
                (cellOrder[i], cellOrder[swapIndex]) = (cellOrder[swapIndex], cellOrder[i]);
            }

            int desiredCount = random.Next(1, RlStaticObstacleMaximumCount + 1);
            float usedArea = 0f;
            for (int orderIndex = 0; orderIndex < cellOrder.Length && layout.Count < desiredCount; orderIndex++)
            {
                Rect cell = cells[cellOrder[orderIndex]];
                if (cell.width < RlStaticObstacleMinimumSize || cell.height < RlStaticObstacleMinimumSize)
                {
                    continue;
                }

                float maximumWidth = Mathf.Max(
                    RlStaticObstacleMinimumSize,
                    Mathf.Min(cell.width, cell.width * 0.8f));
                float maximumHeight = Mathf.Max(
                    RlStaticObstacleMinimumSize,
                    Mathf.Min(cell.height, cell.height * 0.8f));
                float width = Mathf.Lerp(
                    RlStaticObstacleMinimumSize,
                    maximumWidth,
                    (float)random.NextDouble());
                float height = Mathf.Lerp(
                    RlStaticObstacleMinimumSize,
                    maximumHeight,
                    (float)random.NextDouble());

                float remainingArea = maximumArea - usedArea;
                if (remainingArea < RlStaticObstacleMinimumSize * RlStaticObstacleMinimumSize)
                {
                    break;
                }

                float area = width * height;
                if (area > remainingArea)
                {
                    float scale = Mathf.Sqrt(remainingArea / area);
                    width *= scale;
                    height *= scale;
                    if (width < RlStaticObstacleMinimumSize || height < RlStaticObstacleMinimumSize)
                    {
                        continue;
                    }
                }

                float x = Mathf.Lerp(cell.xMin, cell.xMax - width, (float)random.NextDouble());
                float y = Mathf.Lerp(cell.yMin, cell.yMax - height, (float)random.NextDouble());
                Rect obstacleRect = new Rect(x, y, width, height);
                layout.Add(obstacleRect);
                usedArea += obstacleRect.width * obstacleRect.height;
            }

            return layout;
        }

        private void SpawnObstacles()
        {
            if (ObstacleMap == null)
            {
                ObstacleMap = new ObstacleMap(1);
            }
            else
            {
                ObstacleMap.Obstacles.Clear();
                ObstacleMap.ObstacleBackground = null;
            }
            _usesPooledStaticObstaclePrefabs = false;
            if (CurrentLevelOptions.Obstacles != "No")
            {
                if (CurrentLevelOptions.Obstacles == "" && CurrentLevelOptions.ObstacleList.Count == 0)
                {
                    _usesPooledStaticObstaclePrefabs = true;
                    if (global::RlOneVsOneTrainingBootstrap.IsActiveFor(Stage))
                    {
                        GenerateRlTrainingObstacles();
                    }
                    else
                    {
                        GenerateRandomObstacles();
                    }
                }
                else if (CurrentLevelOptions.ObstacleList.Count > 0)
                {
                    _usesPooledStaticObstaclePrefabs = true;
                    StaticObstaclePool obstaclePool = GetStaticObstaclePool();
                    ObstacleMap.ObstacleBackground = obstaclePool.GetBackground(Map.transform);
                    for (int i = 0; i < CurrentLevelOptions.ObstacleList.Count; i++)
                    {
                        (Vector2, Vector2) vectorPair = CurrentLevelOptions.ObstacleList[i];
                        StaticObstacle obstacle = obstaclePool.GetObstacle(Map.transform);
                        obstacle.transform.localPosition = vectorPair.Item1;
                        obstacle.transform.localScale = vectorPair.Item2;
                        obstacle.Collider.enabled = false;
                        obstacle.Collider.enabled = true;
                        ObstacleMap.Obstacles.Add(obstacle);
                    }
                }
                else
                {
                    GameObject obstacleContainer = Instantiate(Resources.Load<GameObject>($"Obstacles/{CurrentLevelOptions.Obstacles}"), Map.transform);
                    List<StaticObstacle> obstacles = obstacleContainer.GetComponentsInChildren<StaticObstacle>().ToList();
                    HideTitaniaObstacleDebugBackgrounds(CurrentLevelOptions.Obstacles, obstacles);
                    List<MapObject> objects = obstacleContainer.GetComponentsInChildren<MapObject>().ToList();
                    objects.ForEach((o) => o.Setup(this));
                    ObstacleMap.Obstacles = obstacles;
                }
            }

            ObstacleMap.Obstacles.ForEach((obstacle) => obstacle.gameObject.SetActive(true));

            if (ActivateCollisionAsteroids)
            {
                Stage.HasAsteroids = true;

                if (global::RlOneVsOneTrainingBootstrap.IsActiveFor(Stage))
                {
                    float spawnSeconds = global::RlOneVsOneTrainingBootstrap.CurrentCollisionAsteroidSpawnSeconds;
                    if (spawnSeconds > 0f)
                    {
                        _asteroidSpawnTimer.Reuse(spawnSeconds, SpawnAsteroid, true);
                        AddTimer(_asteroidSpawnTimer);
                    }
                }
                else
                {
                    // Spawn timing belongs to this Level. Stage hosts many simultaneous training Levels,
                    // so do not mutate the serialized Stage baseline or share mutable Current* rates.
                    int minimumSpawnRate = Math.Max(1, Stage.AsteroidMinimumSpawnRate);
                    int maximumSpawnRate = Math.Max(minimumSpawnRate, Stage.AsteroidMaxSpawnRate);
                    if (CurrentLevelOptions.AsteroidOption == 2)
                    {
                        minimumSpawnRate = Math.Max(1, minimumSpawnRate / 2);
                        maximumSpawnRate = Math.Max(minimumSpawnRate, maximumSpawnRate / 2);
                    }
                    else if (CurrentLevelOptions.AsteroidOption == 3)
                    {
                        minimumSpawnRate = 1;
                        maximumSpawnRate = Math.Max(minimumSpawnRate, maximumSpawnRate / 2);
                    }

                    int spawnRateRange = Math.Max(1, maximumSpawnRate - minimumSpawnRate);
                    _asteroidSpawnTimer.Reuse(minimumSpawnRate + Utilities.RandomInt(spawnRateRange), SpawnAsteroid, true);
                    AddTimer(_asteroidSpawnTimer);
                }
            }
        }

        private static void HideTitaniaObstacleDebugBackgrounds(
            string obstacleName,
            IEnumerable<StaticObstacle> obstacles)
        {
            if (obstacleName != "Minesweeper" && obstacleName != "Bee-noculars")
            {
                return;
            }

            foreach (StaticObstacle obstacle in obstacles)
            {
                // _Obstacle Prefab itself has no SpriteRenderer. Titania's authored fields add a
                // direct opaque pink/white renderer to these roots solely as a layout aid; child
                // masks and map-object art are separate components and remain untouched.
                SpriteRenderer debugBackground = obstacle.GetComponent<SpriteRenderer>();
                if (debugBackground == null)
                {
                    continue;
                }

                Color color = debugBackground.color;
                color.a = 0f;
                debugBackground.color = color;
            }
        }

        private MiningAsteroid _spawn_miningAsteroid;
        public Vector2 MiningAsteroidSpawnDistance;
        private int _spawn_i;
        private void SpawnMiningAsteroids(int minimum = 1, int maximum = 5)
        {
            int asteroidCount;
            if (global::RlOneVsOneTrainingBootstrap.IsActiveFor(Stage))
            {
                minimum = 0;
                maximum = 6;
                MiningAsteroidSpawnDistance = new Vector2(
                    Mathf.Max(0f, HalfMapWidth - ConfigData.MapEdgePadding.x - 1f),
                    Mathf.Max(0f, HalfMapHeight - ConfigData.MapEdgePadding.y - 1f));
                asteroidCount = GetRlEnvironmentRandom().Next(minimum, maximum + 1);
            }
            else
            {
                MiningAsteroidSpawnDistance = new Vector2(HalfMapWidth - 64, HalfMapHeight - 64);
                asteroidCount = Utilities.RandomInt((maximum + 1) - minimum) + minimum;
            }

            for (_spawn_i = 0; _spawn_i < asteroidCount; _spawn_i++)
            {
                _spawn_miningAsteroid = Stage.Pool.GetMiningAsteroidFromPool();
                _spawn_miningAsteroid.Setup(this);
                MaxMinerals += _spawn_miningAsteroid.OriginalHealth;
            }
            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign && State.GetShips(ConfigData.Configuration.UserSide).Find((s) => s.ShipType == ConfigData.ShipTypes.Factory) != null)
            {
                Stage.Menus.MineralsMinedStatus.SetActive(true);
                Stage.Menus.UpdateMineralsMined(State.PlayerMineralsMined, MaxMinerals);
            }
        }

        private ScaledTimer _asteroidSpawnTimer = new ScaledTimer();
        private void SpawnAsteroid()
        {
            Stage.Pool.GetCollisionAsteroidFromPool().Setup(this);
        }
    }
}
