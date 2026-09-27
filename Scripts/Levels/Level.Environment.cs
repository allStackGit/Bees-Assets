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
        private MatchSetupRandom _multiplayerSetupRandom;
        private long _nextMultiplayerSetupSquadId;
        private long _nextMultiplayerSetupFleetId;

        public bool UsesDeterministicMultiplayerSetupRandom =>
            ConfigData.CurrentGameMode == ConfigData.GameModes.FreePlay &&
            Stage != null &&
            Stage.MatchSession != null &&
            Stage.MatchSession.HasRemotePeer;

        public void ResetMultiplayerSetupRandom()
        {
            if (!UsesDeterministicMultiplayerSetupRandom)
            {
                _multiplayerSetupRandom = null;
                return;
            }

            int seed = Stage.MatchSession.GetLevelSetupSeed(MatchLevelId);
            _multiplayerSetupRandom = new MatchSetupRandom(seed);
            _nextMultiplayerSetupSquadId =
                -2000000000000L - ((long)MatchLevelId * 1000000L);
            _nextMultiplayerSetupFleetId =
                -3000000000000L - ((long)MatchLevelId * 1000000L);
        }

        public int SetupUtilityRandomInt(int maxExclusive)
        {
            return _multiplayerSetupRandom == null
                ? Utilities.RandomInt(maxExclusive)
                : _multiplayerSetupRandom.NextInt(maxExclusive);
        }

        public int SetupUnityRandomRange(int minInclusive, int maxExclusive)
        {
            return _multiplayerSetupRandom == null
                ? UnityEngine.Random.Range(minInclusive, maxExclusive)
                : _multiplayerSetupRandom.Range(minInclusive, maxExclusive);
        }

        public float SetupRandomFloat(float maxExclusive)
        {
            return _multiplayerSetupRandom == null
                ? Utilities.RandomFloat(maxExclusive)
                : _multiplayerSetupRandom.NextFloat(maxExclusive);
        }

        public bool SetupCoinToss()
        {
            return _multiplayerSetupRandom == null
                ? SetupCoinToss()
                : _multiplayerSetupRandom.CoinToss();
        }

        public int SetupRandomSign()
        {
            return _multiplayerSetupRandom == null
                ? Utilities.RandomSign()
                : _multiplayerSetupRandom.NextSign();
        }

        public Vector2 SetupRandomCoordinate(
            Vector2 position,
            Vector2 maxDistance,
            Vector2 minDistance)
        {
            if (_multiplayerSetupRandom == null)
            {
                return Utilities.RandomCoordinate(this, position, maxDistance, minDistance);
            }

            Vector2 newLocation = Vector2.zero;
            int loops = 0;
            while ((newLocation == Vector2.zero ||
                    !Utilities.VectorInBounds(this, newLocation)) &&
                   loops < 35)
            {
                newLocation = new Vector2(
                    position.x +
                    (SetupRandomFloat(maxDistance.x) + minDistance.x) *
                    SetupRandomSign(),
                    position.y +
                    (SetupRandomFloat(maxDistance.y) + minDistance.y) *
                    SetupRandomSign());
                loops++;
            }

            return newLocation;
        }

        public long AllocateSetupSavedSquadId()
        {
            return _multiplayerSetupRandom == null
                ? Utilities.GetNegativeSavedSquadId()
                : _nextMultiplayerSetupSquadId--;
        }

        public long AllocateSetupFleetShipId()
        {
            return _multiplayerSetupRandom == null
                ? Utilities.GetNegativeFleetshipId()
                : _nextMultiplayerSetupFleetId--;
        }

        private void RandomizeOptions()
        {
            bool logEnvironment = !Stage.IsTraining;
            if (CurrentLevelOptions.MapIndex == -1)
            {
                CurrentLevelOptions.MapIndex = SetupUtilityRandomInt(Stage.Prefabs.Maps.Count);
            }
            MapData = ConfigData.Maps[CurrentLevelOptions.MapIndex];
            Map = Stage.Pool.GetPooledMap(CurrentLevelOptions.MapIndex);

            bool hiveMindTraining = Stage.IsTrainingHiveMind;
            bool useStaticObstacles = hiveMindTraining
                ? SetupCoinToss()
                : (((CurrentLevelOptions.Obstacles == "" && SetupCoinToss()) || CurrentLevelOptions.Obstacles != "No") && !Stage.IsTraining);

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
                    ? SetupCoinToss()
                    : (CurrentLevelOptions.AsteroidOption == -1 && SetupUtilityRandomInt(4) == 0) || CurrentLevelOptions.AsteroidOption > 0;
                SetAsteroidOptionForTraining(hiveMindTraining, useAsteroids);
                ActivateCollisionAsteroids = useAsteroids;
                if (logEnvironment)
                {
                }
            }
            else
            {
                bool useAsteroids = hiveMindTraining
                    ? SetupCoinToss()
                    : (((CurrentLevelOptions.AsteroidOption == -1 && SetupCoinToss()) || CurrentLevelOptions.AsteroidOption > 0) && !Stage.IsTraining);
                SetAsteroidOptionForTraining(hiveMindTraining, useAsteroids);

                CurrentLevelOptions.Obstacles = "No";
                ActivateCollisionAsteroids = useAsteroids;
                HasObstacles = useAsteroids;
                if (logEnvironment)
                {
                }
            }

            if (Stage.DoesUserHaveController && ((CurrentLevelOptions.FogOfWar == -1 && SetupCoinToss()) || CurrentLevelOptions.FogOfWar == 1))
            {
                ActivateFogOfWar = true;
            }
            else
            {
                ActivateFogOfWar = false;
            }

            if ((CurrentLevelOptions.Mining == -1 && !HasObstacles && SetupCoinToss()) || CurrentLevelOptions.Mining == 1)
            {
                ActivateMining = true;
            }
            else
            {
                ActivateMining = false;
            }

            // This currently has an override (the " && false" at the end) to prevent reinforcements.
            if (((CurrentLevelOptions.EnemyReinforcementsOption == -1 && SetupCoinToss()) || CurrentLevelOptions.EnemyReinforcementsOption == 1) && false)
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
                ? (SetupCoinToss() ? 1 : 2)
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

        private StaticObstaclePool _staticObstaclePool;
        private bool _usesPooledStaticObstaclePrefabs;

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
            for (int i = 0; i < SetupUtilityRandomInt(10) + 1; i++)
            {
                StaticObstacle obstacle = obstaclePool.GetObstacle(Map.transform);
                if (SetupCoinToss())
                {
                    obstacle.transform.localScale = new Vector2(SetupUtilityRandomInt(150) + 20, SetupUtilityRandomInt(50) + 20);
                }
                else
                {
                    obstacle.transform.localScale = new Vector2(SetupUtilityRandomInt(50) + 20, SetupUtilityRandomInt(150) + 20);
                }
                obstacle.transform.localPosition = SetupRandomCoordinate(Vector2.zero, maxSpawnDistance - new Vector2(0, obstacle.transform.localScale.y / 2), Vector2.zero);
                obstacle.Collider.enabled = false;
                obstacle.Collider.enabled = true;
                ObstacleMap.Obstacles.Add(obstacle);
            }
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
                    GenerateRandomObstacles();
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
                _asteroidSpawnTimer.Reuse(minimumSpawnRate + SetupUtilityRandomInt(spawnRateRange), SpawnAsteroid, true);
                AddTimer(_asteroidSpawnTimer);
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
        private void SpawnMiningAsteroids(
            int minimum = 1,
            int maximum = 5,
            bool useDeterministicSetupRandom = false)
        {
            MiningAsteroidSpawnDistance = new Vector2(HalfMapWidth - 64, HalfMapHeight - 64);
            int asteroidCount = useDeterministicSetupRandom
                ? SetupUtilityRandomInt((maximum + 1) - minimum) + minimum
                : Utilities.RandomInt((maximum + 1) - minimum) + minimum;
            for (_spawn_i = 0; _spawn_i < asteroidCount; _spawn_i++)
            {
                _spawn_miningAsteroid = Stage.Pool.GetMiningAsteroidFromPool();
                _spawn_miningAsteroid.Setup(this, useDeterministicSetupRandom);
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
