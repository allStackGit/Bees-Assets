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
            //Debug.Log($"Randomizing options...");
            //Debug.Log($"Level selection option: {ConfigData.SelectedLevelMapIndex}");

            if (CurrentLevelOptions.MapIndex == -1)
            {
                CurrentLevelOptions.MapIndex = Utilities.RandomInt(Stage.Prefabs.Maps.Count);              
            }
            MapData = ConfigData.Maps[CurrentLevelOptions.MapIndex];
            //Map = Instantiate(Stage.Prefabs.Maps[CurrentLevelOptions.MapIndex]).GetComponent<UI_Components.Map>();
            Map = Stage.Pool.GetPooledMap(CurrentLevelOptions.MapIndex);
            //Debug.Log($"Playing on the {MapData.Name} ({Map.Name}) at index #{CurrentLevelOptions.MapIndex} map");

            //Debug.Log($"Obstacle Map Index: {CurrentLevelOptions.ObstacleMapIndex}");
            if (((CurrentLevelOptions.Obstacles == "" && Utilities.CoinToss()) || CurrentLevelOptions.Obstacles != "No") && !Stage.IsTraining) // User chose random and random chose obstacles OR user chose obstacles
            {
                HasObstacles = true;
                Debug.Log($"The map has obstacles: {CurrentLevelOptions.Obstacles}");
                if (CurrentLevelOptions.Obstacles == "")
                {
                    //CurrentLevelOptions.Obstacles = ConfigData.ObstacleMaps[Utilities.RandomInt(ConfigData.ObstacleMaps.Count - 1) + 1].Name;
                }
                //ObstacleMap = Stage.Pool.GetObstacleMapFromPool(CurrentLevelOptions.Obstacles);

                if ((CurrentLevelOptions.AsteroidOption == -1 && Utilities.RandomInt(4) == 0) || CurrentLevelOptions.AsteroidOption > 0) // User chose random and random chose asteroids OR User chose asteroids
                {
                    ActivateCollisionAsteroids = true;
                    Debug.Log($"The map has obstacles ({CurrentLevelOptions.Obstacles}) and asteroids as well");
                }
                else // user chose no asteroids or random chose no asteroids
                {
                    ActivateCollisionAsteroids = false;
                    Debug.Log($"The map has obstacles ({CurrentLevelOptions.Obstacles}) and not asteroids");
                }
            }
            else // either the user chose no obstacles or random chose no obstacles
            {

                if (((CurrentLevelOptions.AsteroidOption == -1 && Utilities.CoinToss()) || CurrentLevelOptions.AsteroidOption > 0) && !Stage.IsTraining) // User chose random and random chose asteroids OR User chose asteroids
                {
                    HasObstacles = true;
                    //ObstacleMap = Stage.Pool.GetObstacleMapFromPool(0);
                    CurrentLevelOptions.Obstacles = "No";
                    ActivateCollisionAsteroids = true;
                    Debug.Log($"The map has asteroids but not obstacles");
                }
                else
                {
                    CurrentLevelOptions.Obstacles = "No";
                    ActivateCollisionAsteroids = false;
                    HasObstacles = false;
                    Debug.Log($"The map does not have asteroids or obstacles");
                }
            }


            if (Stage.DoesUserHaveController && ((CurrentLevelOptions.FogOfWar == -1 && Utilities.CoinToss()) || CurrentLevelOptions.FogOfWar == 1))
            {
                ActivateFogOfWar = true;
                Debug.Log($"The map has fog of war");
            }
            else
            {
                ActivateFogOfWar = false;
                Debug.Log($"The map does not have fog of war");
            }

            if ((CurrentLevelOptions.Mining == -1  && !HasObstacles && Utilities.CoinToss()) || CurrentLevelOptions.Mining == 1)
            {
                ActivateMining = true;
                Debug.Log($"The map has mining");
            }
            else
            {
                ActivateMining = false;
                Debug.Log($"The map does not have mining");
            }

            // This currently has an override (the " && false" at the end) to prevent reinforcements
            if (((CurrentLevelOptions.EnemyReinforcementsOption == -1 && Utilities.CoinToss()) || CurrentLevelOptions.EnemyReinforcementsOption == 1) && false)
            {
                ActivateLoadingShipsMidLevel = true;

                //Debug.Log($"The map has ships loading midlevel");
                if (CurrentLevelOptions.EnemyReinforcements.Count == 0)
                {
                    CurrentLevelOptions.EnemyReinforcements = CurrentLevelOptions.EnemySquads.ToList();
                }
            }
            else
            {
                ActivateLoadingShipsMidLevel = false;
                //Debug.Log($"The map does not have ships loading midlevel");
            }

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

                    while (_f_clearance % Pathfinder.Scale > 0) // round the clearance up to the nearest multiple of Scale (e.g. round 13 to 16 if the Scale is 4)
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
        private List<StaticObstacle> GenerateRandomObstacles()
        {
            List<StaticObstacle> obstacles = new List<StaticObstacle>();
            Vector2 maxSpawnDistance = new Vector2(MaxX - 150, MaxY - 150);
            GameObject obstacleBackground = Instantiate(Stage.Prefabs.ObstacleBackgroundPrefab, Map.transform);
            ObstacleMap.ObstacleBackground = obstacleBackground;
            //SpriteRenderer sr = obstacleBackground.GetComponent<SpriteRenderer>();
            //sr.sprite = Map.SpriteRenderer.sprite;
            //sr.size = Map.SpriteRenderer.size;
            for (int i = 0; i < Utilities.RandomInt(10) + 1; i++)
            {
                GameObject obstacleObject = Instantiate(Stage.Prefabs.ObstaclePrefab, Map.transform);
                StaticObstacle obstacle = obstacleObject.GetComponent<StaticObstacle>();
                if (Utilities.CoinToss())
                {
                    obstacle.transform.localScale = new Vector2(Utilities.RandomInt(150) + 20, Utilities.RandomInt(50) + 20); // wider rather than taller
                }
                else
                {
                    obstacle.transform.localScale = new Vector2(Utilities.RandomInt(50) + 20, Utilities.RandomInt(150) + 20); // taller rather than wider
                }
                obstacle.transform.localPosition = Utilities.RandomCoordinate(this, Vector2.zero, maxSpawnDistance - new Vector2(0, obstacle.transform.localScale.y / 2), Vector2.zero);

                obstacle.Collider.enabled = false;
                obstacle.Collider.enabled = true;

                //Debug.Log($"Spawning obstacle of size {obstacle.transform.localScale} at {obstacle.transform.localPosition}");
                obstacles.Add(obstacle);
            }
            return obstacles;
        }
        //private Vector2 _spawn_position;
        private void SpawnObstacles()
        {

            // Load the obstacles
            //Debug.Log($"Loading obstacles:  Obstacles/{CurrentLevelOptions.Obstacles}");
            ObstacleMap = new ObstacleMap(1);
            if (CurrentLevelOptions.Obstacles != "No")
            {
                if (CurrentLevelOptions.Obstacles == "" && CurrentLevelOptions.ObstacleList.Count == 0)
                {
                    ObstacleMap.Obstacles = GenerateRandomObstacles();
                    //CurrentLevelOptions.ObstacleList = ObstacleMap.Obstacles;

                }
                else if (CurrentLevelOptions.ObstacleList.Count > 0)
                {
                    GameObject obstacleBackground = Instantiate(Stage.Prefabs.ObstacleBackgroundPrefab, Map.transform);
                    ObstacleMap.ObstacleBackground = obstacleBackground;
                    //SpriteRenderer sr = obstacleBackground.GetComponent<SpriteRenderer>();
                    //sr.sprite = Map.SpriteRenderer.sprite;
                    //sr.size = Map.SpriteRenderer.size;

                    ObstacleMap.Obstacles = CurrentLevelOptions.ObstacleList.Select((vectorPair) =>
                    {
                        GameObject obstacleObject = Instantiate(Stage.Prefabs.ObstaclePrefab, Map.transform);
                        StaticObstacle obstacle = obstacleObject.GetComponent<StaticObstacle>();

                        obstacle.transform.localPosition = vectorPair.Item1;
                        obstacle.transform.localScale = vectorPair.Item2;

                        obstacle.Collider.enabled = false;
                        obstacle.Collider.enabled = true;

                        Debug.Log($"Spawning saved obstacle of size {obstacle.transform.localScale} at {obstacle.transform.localPosition}");
                        return obstacle;
                    }).ToList();
                }
                else
                {
                    GameObject obstacleContainer = Instantiate(Resources.Load<GameObject>($"Obstacles/{CurrentLevelOptions.Obstacles}"), Map.transform);
                    List<StaticObstacle> obstacles = obstacleContainer.GetComponentsInChildren<StaticObstacle>().ToList();

                    Debug.Log($"Spawning obstacles from prefab with count {obstacles.Count}");
                    List<MapObject> objects = obstacleContainer.GetComponentsInChildren<MapObject>().ToList();
                    Debug.Log($"Found {objects.Count} map objects in the obstacle prefab");
                    objects.ForEach((o) =>
                    {
                        o.Setup(this);
                    });

                    ObstacleMap.Obstacles = obstacles;
                }
            }
          
            ObstacleMap.Obstacles.ForEach((obstacle) =>
            {
                obstacle.gameObject.SetActive(true);
            });

            //Debug.Log($"LevelData: {Utilities.ListToString(ObstacleMap.Obstacles)}");

            if (ActivateCollisionAsteroids) 
            {
                Stage.HasAsteroids = true;
                if (CurrentLevelOptions.AsteroidOption == 2) 
                {
                    Stage.CurrentAsteroidMaxSpawnRate /= 2;
                    Stage.CurrentAsteroidMinimumSpawnRate /= 2;
                }
                else if (CurrentLevelOptions.AsteroidOption == 3)
                {
                    Stage.CurrentAsteroidMaxSpawnRate /= 2;
                    Stage.CurrentAsteroidMinimumSpawnRate /= 2;
                    Stage.AsteroidMinimumSpawnRate = 1;
                }


                // [debug]
                //Stage.CurrentAsteroidMaxSpawnRate /= 2;
                //Stage.CurrentAsteroidMinimumSpawnRate /= 2;
                //_asteroidSpawnTimer.Reuse(Utilities.RandomInt(Stage.CurrentAsteroidMaxSpawnRate - Stage.CurrentAsteroidMinimumSpawnRate), SpawnAsteroid, true);

                _asteroidSpawnTimer.Reuse(Stage.AsteroidMinimumSpawnRate + Utilities.RandomInt(Stage.CurrentAsteroidMaxSpawnRate - Stage.CurrentAsteroidMinimumSpawnRate), SpawnAsteroid, true);
                AddTimer(_asteroidSpawnTimer);
                //Invoke(nameof(SpawnAsteroid), Stage.AsteroidMinimumSpawnRate + Utilities.RandomInt(Stage.CurrentAsteroidMaxSpawnRate - Stage.CurrentAsteroidMinimumSpawnRate));
            }
        }
        private MiningAsteroid _spawn_miningAsteroid;
        public Vector2 MiningAsteroidSpawnDistance;
        private int _spawn_i;
        private void SpawnMiningAsteroids(int minimum = 1, int maximum = 5)
        {
            MiningAsteroidSpawnDistance = new Vector2(HalfMapWidth - 64, HalfMapHeight - 64);
            for (_spawn_i = 0; _spawn_i < Utilities.RandomInt((maximum + 1)-minimum) + minimum; _spawn_i++)
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
            //Debug.Log($"Spawning asteroid");
            //_asteroidSpawnTimer.Reuse(Stage.AsteroidMinimumSpawnRate + Utilities.RandomInt(Stage.CurrentAsteroidMaxSpawnRate - Stage.CurrentAsteroidMinimumSpawnRate), SpawnAsteroid);
            //GameObject instance = Instantiate(Stage.Prefabs.CollisionAsteroidPrefabs[Utilities.RandomInt(Stage.Prefabs.CollisionAsteroidPrefabs.Count)]);
            //AddTimer(_asteroidSpawnTimer);
            
            
            Stage.Pool.GetCollisionAsteroidFromPool().Setup(this);


            //Invoke(nameof(SpawnAsteroid), Stage.AsteroidMinimumSpawnRate + Utilities.RandomInt(Stage.CurrentAsteroidMaxSpawnRate - Stage.CurrentAsteroidMinimumSpawnRate));

        }
    }
}
