using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.UI_Components;
using Assets.Scripts.UIComponents;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    /// <summary>
    /// A container class for the map and all entities in it. Can coexist or be indepdentent with any number of levels. Belongs to a stage.
    /// </summary>
    public class Level: MonoBehaviour
    {
        //public float __RotationTest;
        //public Vector2 __OriginalPosition;
        public GameState State;
        // If hivemind is activate, get commands from the server
        // If brains are activated, get actions from the nueral network
        // If IsTrainingNueralNetwork, train the neural network. IsTrainingHiveMind, train the hive mind
        // Training Hivemind or Nueral Network then there is no player, levels are reset every time, and the camera position doesn't matter
        public bool HasObstacles, ActivateCollisionAsteroids, ActivateMining, ActivateFogOfWar, ActivateLoadingShipsMidLevel;
        public UI_Components.Map Map;
        public LevelConstructor LevelConstructor;
        public Pathfinder Pathfinder;
        public SimpleMultiAgentGroup AgentGroup;
        public SimpleMultiAgentGroup HumanAgentGroup;
        public float MinX, MinY, MaxX, MaxY;
        public Vector2[] StartingPositions = new Vector2[2];

        /// <summary>
        /// Whether or not the level has been setup initially on the server
        /// </summary>
        public bool IsLevelSetupOnServer;
        /// <summary>
        /// Whether or not this level is currently connected and setup on the server, regardless of whether other levels are connected
        /// </summary>
        public bool IsLevelConnectedToServer;
        public bool IsRestarting;
        public bool HasPlayer;
        /// <summary>
        /// Whether or not a tester pressed the pause key to pause the game
        /// </summary>
        public bool IsPausedByTester;
        public int WinningSide;
        public float MapX, MapY, MaxDistance, HalfX, HalfY;
        public int MapWidth, MapHeight, HalfMapWidth, HalfMapHeight, MaximumClearance;
        public float Seconds, StartTime;
        public int TriggersActivated;
        public float TimePaused;
        public List<Trigger> Triggers = new List<Trigger>();
        public LevelOptions SaveLevelOptions;
        public LevelOptions CurrentLevelOptions;
        public Data.Map MapData;
        public List<SavedSquad> AllSquads = new List<SavedSquad>();
        public string Name;
        /// <summary>
        /// All the ids of requests that have been handled by this level. Must be level specific because it's used to remove handled requests from ConfigData
        /// </summary>
        public HashSet<long> HandledRequests = new HashSet<long>();
        public Stage Stage;
        public bool DidUserWin;
        /// <summary>
        /// The chosen obstacle map
        /// </summary>
        public ObstacleMap ObstacleMap;
        public List<ScaledTimer> Timers = new List<ScaledTimer>();


        public List<string> __BeeHivemindShips, __HumanHivemindShips, __PastCommands, __PathfindingThreads, __CustomLevels, __Timers;


        private void UpdateDebugVariables()
        {
            __BeeHivemindShips = State.GetShipsVisibleToHiveMind(ConfigData.Configuration.BeeSide).Select(s => s.ToString()).ToList();
            __HumanHivemindShips = State.GetShipsVisibleToHiveMind(ConfigData.Configuration.HumanSide).Select(s => s.ToString()).ToList();
            __PastCommands = State.PastCommands.Select((c) => $"Command #{c.OutcomeId} - {c.CommandType} for Squad {c.Squad} against [{c.Enemy}] with {c.Tsv} TSV").ToList();
            __CustomLevels = ConfigData.GetLevelData().GetLevels().Select((level) => level.ToString()).ToList();
            
            if (Pathfinder != null)
            {
                __PathfindingThreads = Pathfinder.IsThreadActive.Select((s, i) => $"#{i} - {(s ? Pathfinder.Ships[i].Name : s)}").ToList();
            }
            __Timers = Timers.Select((t) => t.ToString()).ToList();

            //string path = $"{ConfigData.GetBasePath()}/debug/minimap_{Utilities.Hash()}.png";
            //Texture2D dest = new Texture2D(MiniMapTexture.width, MiniMapTexture.height, TextureFormat.RGB24, false);
            //RenderTexture.active = MiniMapTexture;
            //dest.ReadPixels(new Rect(0, 0, MiniMapTexture.width, MiniMapTexture.height), 0, 0);
            //dest.Apply();
            //File.WriteAllBytes(path, dest.EncodeToPNG());
            State.UpdateDebugVariables();
        }
        public void Setup(Stage stage, string name)
        {
            Stage = stage;
            Name = name;
            gameObject.name = $"Level - {Name}";

            if (ConfigData.IsPlayingCampaign)
            {
                Stage.ReplaceDeadShips = true;
                Stage.RecordStats = true;
            }

            LevelConstructor = new LevelConstructor(this);
            LevelConstructor.RequestServerSetup();

            if (Stage.DoesUserHaveController)
            {
                HasPlayer = true;
                if (ConfigData.IsUserLoadingCustomSquads)
                {
                    Stage.UseFullyRandomSquads = false;
                }
                if (ConfigData.IsUserLoadingCustomEnemySquads)
                {
                    Stage.UseFullyRandomEnemySquads = false;
                }
            }
            else
            {
                HasPlayer = ConfigData.Configuration.DoesUserHaveController;
            }

            //_obstacleLists = new Dictionary<int, List<GameObject>>()
            //{
            //    {0, Stage.Prefabs.EmptyObstacleList }, // it's important to have this here so we choose an empty level for testing
            //    {1, Stage.Prefabs.MazePrefabs },
            //    {2, Stage.Prefabs.ThreePathsPrefabs },
            //    {3, Stage.Prefabs.ForestPrefabs },
            //    {4, Stage.Prefabs.TheWallPrefabs }
            //};

            if (Stage.ActivateBrains)
            {
                AgentGroup = new SimpleMultiAgentGroup();
                HumanAgentGroup = new SimpleMultiAgentGroup();

                if (Stage.IsTrainingNueralNetwork)
                {
                    Academy.Instance.OnEnvironmentReset += () =>
                    {
                        //Debug.Log($"Reset environment, {Academy.Instance.StepCount}");
                    };

                }
            }


            // Setup Game State
            State = gameObject.AddComponent<GameState>();
            State.Setup(this);

            if (Stage.IsTraining)
            {
                AddTimer(_timeoutTimer);
                //Invoke(nameof(TimeOut), Stage.TimeoutTime);
            }
            SetupLevel();
        }
        private void RandomizeOptions()
        {
            Debug.Log($"Randomizing options...");
            //Debug.Log($"Level selection option: {ConfigData.SelectedLevelMapIndex}");

            if (CurrentLevelOptions.MapIndex == -1)
            {
                CurrentLevelOptions.MapIndex = Utilities.RandomInt(Stage.Prefabs.Maps.Count);              
            }
            MapData = ConfigData.Maps[CurrentLevelOptions.MapIndex];
            //Map = Instantiate(Stage.Prefabs.Maps[CurrentLevelOptions.MapIndex]).GetComponent<UI_Components.Map>();
            Map = Stage.Pool.GetPooledMap(CurrentLevelOptions.MapIndex);
            //Debug.Log($"Playing on the {MapData.Name} ({Map.Name}) at index #{CurrentLevelOptions.MapIndex} map");

            if (((CurrentLevelOptions.ObstacleMapIndex == -1 && Utilities.CoinToss()) || CurrentLevelOptions.ObstacleMapIndex > 0) && !Stage.IsTraining) // User chose random and random chose obstacles OR user chose obstacles
            {
                HasObstacles = true;
                Debug.Log($"The map has obstacles");
                if (CurrentLevelOptions.ObstacleMapIndex == -1)
                {
                    CurrentLevelOptions.ObstacleMapIndex = Utilities.RandomInt(Stage.ObstacleListCount - 1) + 1;
                }
                ObstacleMap = Stage.Pool.GetObstacleMapFromPool(CurrentLevelOptions.ObstacleMapIndex);

                if ((CurrentLevelOptions.AsteroidOption == -1 && Utilities.RandomInt(4) == 0) || CurrentLevelOptions.AsteroidOption > 0) // User chose random and random chose asteroids OR User chose asteroids
                {
                    ActivateCollisionAsteroids = true;
                    Debug.Log($"The map has obstacles and asteroids as well");
                }
                else // user chose no asteroids or random chose no asteroids
                {
                    ActivateCollisionAsteroids = false;
                    Debug.Log($"The map has obstacles and not asteroids");
                }
            }
            else // either the user chose no obstacles or random chose no obstacles
            {
                
                if (((CurrentLevelOptions.AsteroidOption == -1 && Utilities.CoinToss()) || CurrentLevelOptions.AsteroidOption > 0) && !Stage.IsTraining) // User chose random and random chose asteroids OR User chose asteroids
                {
                    HasObstacles = true;
                    ObstacleMap = Stage.Pool.GetObstacleMapFromPool(0);
                    CurrentLevelOptions.ObstacleMapIndex = 0;
                    ActivateCollisionAsteroids = true;
                    Debug.Log($"The map has asteroids but not obstacles");
                }
                else
                {
                    CurrentLevelOptions.ObstacleMapIndex = 0;
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

            if ((CurrentLevelOptions.EnemyReinforcementsOption == -1 && Utilities.CoinToss()) || CurrentLevelOptions.EnemyReinforcementsOption == 1)
            {
                ActivateLoadingShipsMidLevel = true;
                Debug.Log($"The map has ships loading midlevel");
                if (CurrentLevelOptions.EnemyReinforcements.Count == 0)
                {
                    CurrentLevelOptions.EnemyReinforcements = CurrentLevelOptions.EnemySquads.ToList();
                }
            }
            else
            {
                ActivateLoadingShipsMidLevel = false;
                Debug.Log($"The map does not have ships loading midlevel");
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
                    _f_clearance += 2; // 2 for padding
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
        private Vector2 _spawn_position;
        private void SpawnObstacles()
        {
            Stage.CurrentAsteroidMaxSpawnRate = Stage.AsteroidMaxSpawnRate;
            Stage.CurrentAsteroidMinimumSpawnRate = Stage.AsteroidMinimumSpawnRate;
            ObstacleMap.Obstacles.ForEach((obstacle) =>
            {
                _spawn_position = obstacle.transform.position;
                obstacle.transform.parent = Map.transform;
                obstacle.transform.localPosition = _spawn_position;
                //State.AddObstacle(obstacle.GetComponent<Obstacle>());
            });

            if (ActivateCollisionAsteroids)
            {
                Stage.HasAsteroids = true;
                if (CurrentLevelOptions.AsteroidOption == 2)
                {
                    Stage.CurrentAsteroidMaxSpawnRate /= 2;
                    Stage.CurrentAsteroidMinimumSpawnRate /= 2;
                }
                _asteroidSpawnTimer.Reuse(Stage.AsteroidMinimumSpawnRate + Utilities.RandomInt(Stage.CurrentAsteroidMaxSpawnRate - Stage.CurrentAsteroidMinimumSpawnRate), SpawnAsteroid, true);
                AddTimer(_asteroidSpawnTimer);
                //Invoke(nameof(SpawnAsteroid), Stage.AsteroidMinimumSpawnRate + Utilities.RandomInt(Stage.CurrentAsteroidMaxSpawnRate - Stage.CurrentAsteroidMinimumSpawnRate));
            }
        }
        private MiningAsteroid _spawn_miningAsteroid;
        public Vector2 MiningAsteroidSpawnDistance;
        private int _spawn_i;
        private void SpawnMiningAsteroids()
        {
            MiningAsteroidSpawnDistance = new Vector2(HalfMapWidth - 64, HalfMapHeight - 64);
            for (_spawn_i = 0; _spawn_i < Utilities.RandomInt(5); _spawn_i++)
            {
                _spawn_miningAsteroid = Stage.Pool.GetMiningAsteroidFromPool();
                _spawn_miningAsteroid.Setup(this);
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
        private Vector2 _trigger_moveToPoint;
        private Vector2 _trigger_double = new Vector2(0, 2);
        private void SetTriggers()
        {
            Triggers.Clear();

            if (Utilities.CoinToss())
            {
                Triggers.Add(new Trigger(() =>
                {
                    return Time.realtimeSinceStartup - StartTime >= CurrentLevelOptions.EnemyReinforcementDelay;
                }, () =>
                {
                    //Debug.Log($"{CurrentLevelOptions.EnemyReinforcementDelay} seconds have passed, spawning new enemy ships for side {ConfigData.Configuration.AISide}: {Utilities.ListToString(CurrentLevelOptions.EnemyReinforcements)}");
                    _trigger_moveToPoint = StartingPositions[ConfigData.Configuration.AISide - 1];
                    LevelConstructor.SpawnShipsAndSquads(CurrentLevelOptions.EnemyReinforcements, StartingPositions[ConfigData.Configuration.AISide - 1] * _trigger_double, _trigger_moveToPoint);

                }));
            }
            else
            {
                //Triggers.Add(new Trigger(() =>
                //{
                //    return Time.realtimeSinceStartup - StartTime >= CurrentLevelOptions.EnemyReinforcementDelay;
                //}, () =>
                //{
                //    //Debug.Log($"{CurrentLevelOptions.EnemyReinforcementDelay} seconds have passed, spawning new enemy ships for side {ConfigData.Configuration.AISide}: {Utilities.ListToString(CurrentLevelOptions.EnemyReinforcements)}");
                //    _trigger_moveToPoint = StartingPositions[ConfigData.Configuration.UserSide - 1];
                //    LevelConstructor.SpawnShipsAndSquads(CurrentLevelOptions.FriendlyReinforcements, StartingPositions[ConfigData.Configuration.UserSide - 1] * _trigger_double, _trigger_moveToPoint);

                //}));
            }




        }
        /// <summary>
        /// Checks if any of the trigger conditions to load new ships for a level have been satisfied or not. For actual levels, this should probably be defined in some external file on a per level basis
        /// </summary>
        private void CheckTriggers()
        {
            //Debug.Log($"Checking triggers");

            Triggers.ForEach((trigger) =>
            {
                if (trigger.Conditional())
                {
                    trigger.Action();
                }
            });
            Triggers = Triggers.Where((trigger) => !trigger.HasBeenTriggered).ToList();   
            if (Triggers.Count == 0) {
                CancelTimer(_checkTriggersTimer);
                //CancelInvoke(nameof(CheckTriggers));
            }
        }
        private int _updateIndex;
        public void CancelTimer(ScaledTimer scaledTimer)
        {
           
            Timers.Remove(scaledTimer);
            scaledTimer.IsCanceled = true;
            //Debug.Log($"Canceled {scaledTimer}");
        }
        public void AddTimer(ScaledTimer scaledTimer)
        {
            //Debug.Log($"Adding {scaledTimer}");
            Timers.Add(scaledTimer);
        }
        private ScaledTimer[] _loopTimers;
        void Update()
        {
            //GameObject.Find("Rotated Point").transform.position = Utilities.RotatePointAroundPoint(GameObject.Find("Pivot").transform.position, __OriginalPosition, __RotationTest);
            //if (UseRLServer)
            //{
            //    RLSocket.Update();
            //}
            if (State.GameOver && !State.LevelEnded /*&& !State.CanShipsKeepMining()*/) // Turn this back on when the hivemind is better trained at mining
            {
                LevelOver();
                return;

            }
            if ((State.IsPaused || ConfigData.SocketManager.NetworkDisconnection.IsOpen || !IsLevelConnectedToServer) && !Stage.IsTraining)
            {
                if (IsPausedByTester && Stage.InputManager.HasPauseInput() && Time.realtimeSinceStartup - TimePaused > 1)
                {
                    IsPausedByTester = false;
                    TimePaused = Time.realtimeSinceStartup;
                    UnPause();
                }
            }
            else
            {
                if (HasObstacles)
                {
                    //Debug.Log($"Calling path finder update again");
                    Pathfinder.Update();
                }
                if (Timers.Count > 0)
                {
                    _loopTimers = Timers.ToArray();

                    for (_updateIndex = 0; _updateIndex < _loopTimers.Length; _updateIndex++)
                    {
                        if (_loopTimers[_updateIndex].Update() && !_loopTimers[_updateIndex].IsRecurring && !_loopTimers[_updateIndex].IsCanceled)
                        {
                            CancelTimer(_loopTimers[_updateIndex]);
                        }
                    }


                }

            }



        }
        private float _levelOver_fps, _levelOver_fups;
        private ScaledTimer _saveAndEndHalfSecond = new ScaledTimer();
        private ScaledTimer _saveAndEndFiveSecond = new ScaledTimer();
        /// <summary>
        /// Ends the level and marks the winner
        /// </summary>
        public void LevelOver() // [stats-method] [note]
        {
            if (!Stage.IsTrainingNueralNetwork)
            {
                Stage.__LevelCompletes++;
                State.LevelEnded = true;
                Pause();
                //Debug.Log("LEVEL OVER!");

                State.GetAllSquads().ForEach((squad) =>
                {
                    if (squad.HasCommand)
                    {
                        squad.GetCommand().SetFinalize("Level ended");
                    }
                });

                _levelOver_fps = Time.frameCount / Time.unscaledTime;
                _levelOver_fups = Stage.FixedUpdates / Time.unscaledTime;
                ConfigData.__TotalLength += Time.realtimeSinceStartup - Stage.StartTime;
                ConfigData.__AverageLatency = ConfigData.__TotalLatency / ConfigData.__TotalRequests;

                Debug.Log($"{$"fps: {_levelOver_fps}".PadRight(10).Substring(0, 10)}  {$"fups: {_levelOver_fups}".PadRight(10).Substring(0, 10)}     " +
                      $"{$"latency: {(int)(ConfigData.__AverageLatency * 1000)}ms".PadRight(18)} {$"CPS: {Stage.__HivemindCommands / Time.unscaledTime}".PadRight(9).Substring(0, 9)}   " +
                      $"LTO: {Stage.__LevelTimeouts} LC: {Stage.__LevelCompletes} AveLT: {(int)ConfigData.__AverageLength}s || Hashes: {ConfigData.UsedHashes.Count}"
                );

                if (State.IsSideKilled(ConfigData.Configuration.BeeSide) && !State.IsSideKilled(ConfigData.Configuration.HumanSide))
                {
                    WinningSide = ConfigData.Configuration.HumanSide;
                    if (!ConfigData.IsPlayingCampaign)
                    {
                        ConfigData.GetUserProgressData().HumanFreePlayWins++;
                        ConfigData.GetUserProgressData().Save();
                    }
                }
                else if (State.IsSideKilled(ConfigData.Configuration.HumanSide) && !State.IsSideKilled(ConfigData.Configuration.BeeSide))
                {
                    WinningSide = ConfigData.Configuration.BeeSide;
                    if (!ConfigData.IsPlayingCampaign)
                    {
                        ConfigData.GetUserProgressData().BeeFreePlayWins++;
                        ConfigData.GetUserProgressData().Save();
                    }
                }
                else if (State.IsSideKilled(ConfigData.Configuration.HumanSide) && State.IsSideKilled(ConfigData.Configuration.BeeSide))
                {
                    Debug.Log("Both sides are dead!");
                }
                else
                {
                    Debug.Log("Neither side is dead!");
                }

                if (!Stage.IsTraining && !ConfigData.IsPlayingCampaign)
                {
                    Stage.Menus.UpdateScore(ConfigData.GetUserProgressData().HumanFreePlayWins, ConfigData.GetUserProgressData().BeeFreePlayWins);
                }

                if (WinningSide == ConfigData.Configuration.UserSide)
                {
                    DidUserWin = true;
                }

                UnPause();
            }
            

            if (Stage.IsTrainingNueralNetwork)
            {
                
                ResetLevel(false);
            }
            else
            {
                if (Stage.IsTrainingHiveMind)
                {
                    SaveAndEnd(); // invoke immediately because training is happening

                }
                else
                {
                    if (State.FireBargeExplosions.Count > 0)
                    {
                        //Invoke(nameof(SaveAndEnd), 5f); // invoke after 5 seconds because the explosion should be fully seen
                        _saveAndEndFiveSecond.Reuse(5f, SaveAndEnd);
                        AddTimer(_saveAndEndFiveSecond);

                    }
                    else
                    {
                        _saveAndEndHalfSecond.Reuse(.5f, SaveAndEnd);
                        AddTimer(_saveAndEndHalfSecond);
                        //Invoke(nameof(SaveAndEnd), .5f); // inoke after half a second 
                    }

                }
            }



        }
        private Ship[] _reset_ships;
        private float _reset_remainingHumanTsv, _reset_remainingHumanTSVPercentage, _reset_remainingBeeTsv, _reset_remainingBeeTSVPercentage;
        private Vector2 _reset_swap;
        readonly List<SpottedShip>[] _reset_spottedShips = new List<SpottedShip>[] { new List<SpottedShip>(), new List<SpottedShip>() };
        private int _reset_i;
        /// <summary>
        /// Used for Nueral Network training. Resets the level.
        /// </summary>
        /// <param name="isStepTimeout"></param>
        public void ResetLevel(bool isStepTimeout)
        {

            Academy.Instance.StatsRecorder.Add("Episode Time", Seconds);

            //Debug.Log($"Reset level ({Seconds}), Unclamped Bee reward: {BeeCumaltiveReward}, Unclamped Human reward: {HumanCumulativeReward}");
            _reset_ships = State.GetShips().ToArray();

            State.GameOver = false;
            State.LevelEnded = false;
            _reset_remainingHumanTsv = _reset_ships.Where((s) => s.Side == ConfigData.Configuration.HumanSide).Sum((s) => s.Tsv);
            _reset_remainingHumanTSVPercentage = _reset_remainingHumanTsv / State.InitialTsv[ConfigData.Configuration.HumanSide - 1];

            _reset_remainingBeeTsv = _reset_ships.Where((s) => s.Side == ConfigData.Configuration.BeeSide).Sum((s) => s.Tsv);
            _reset_remainingBeeTSVPercentage = _reset_remainingBeeTsv / State.InitialTsv[ConfigData.Configuration.BeeSide - 1];

            //if (Utilities.RandomInt(10) > 7)
            //{
            //    UserStartingPosition = new Vector2(Utilities.RandomInt((int)MaxX * 2), Utilities.RandomInt((int)MaxY * 2)) - new Vector2(MaxX, MaxY);
            //    //UserStartingPosition = new Vector2(Utilities.RandomInt((int)MaxX), Utilities.RandomInt((int)MaxY)) - new Vector2(MaxX, 0);
            //}

            //UserStartingPosition = new Vector2(Utilities.RandomInt((int)MaxX * 2), UserStartingPosition.y*2) - new Vector2(MaxX, UserStartingPosition.y);

            //AIStartingPosition = new Vector2(Utilities.RandomInt((int)MaxX * 2), AIStartingPosition.y*2) - new Vector2(MaxX, AIStartingPosition.y);

            Map.AIStartingPosition = new Vector2(UnityEngine.Random.Range(MinX, MaxX), UnityEngine.Random.Range(0, MaxY));

            Map.UserStartingPosition = new Vector2(UnityEngine.Random.Range(MinX, MaxX), UnityEngine.Random.Range(MinY, 0));

            if (Utilities.CoinToss())
            {
                _reset_swap = Map.UserStartingPosition;
                Map.UserStartingPosition = Map.AIStartingPosition;
                Map.AIStartingPosition = _reset_swap;
               
            }

            StartingPositions[ConfigData.Configuration.AISide - 1] = Map.AIStartingPosition;
            StartingPositions[ConfigData.Configuration.UserSide - 1] = Map.UserStartingPosition;



            if (!isStepTimeout)
            {
                if (State.IsSideKilled(ConfigData.Configuration.HumanSide) && !State.IsSideKilled(ConfigData.Configuration.BeeSide))
                {
                    //WinningSide = ConfigData.Configuration.BeeSide;
                    //Debug.Log($"Bees won! They had {remainingBeeTsv} / {state.InitialTsv[ConfigData.Configuration.BeeSide - 1]} remaining TSV or {remainingBeeTSVPercentage} x of the original.");

                    AgentGroup.SetGroupReward(_reset_remainingBeeTSVPercentage);
                    HumanAgentGroup.SetGroupReward(-_reset_remainingBeeTSVPercentage);
                    //BeeCumaltiveReward += 1f;
                    //HumanCumulativeReward = -1f;
                    //Debug.Log($"Bees won! Lost {LostBeeShips} bees, reward: {BeeCumaltiveReward}, {HumanCumulativeReward}");

                }
                else if (State.IsSideKilled(ConfigData.Configuration.BeeSide) && !State.IsSideKilled(ConfigData.Configuration.HumanSide))
                {
                    //Debug.Log($"Humans won! They had {remainingHumanTsv} / {state.InitialTsv[ConfigData.Configuration.HumanSide - 1]} remaining TSV or {remainingHumanTSVPercentage} x of the original.");

                    AgentGroup.SetGroupReward(-_reset_remainingHumanTSVPercentage);
                    HumanAgentGroup.SetGroupReward(_reset_remainingHumanTSVPercentage);
                    //BeeCumaltiveReward = -1f;
                    //HumanCumulativeReward += 1f;
                    //Debug.Log($"Humans won! Lost {LostBeeShips} bees, reward: {BeeCumaltiveReward}, {HumanCumulativeReward}");

                }
                else
                {
                    Debug.Log($"Both sides died! no on won!");
                    AgentGroup.SetGroupReward(0);
                    HumanAgentGroup.SetGroupReward(0);

                }
                AgentGroup.EndGroupEpisode();
                HumanAgentGroup.EndGroupEpisode();
            }
            Array.Clear(_reset_spottedShips, 0, 2);
            State.SpottedShips = _reset_spottedShips;


            for (_reset_i = 0; _reset_i < _reset_ships.Length; _reset_i++)
            {
                _reset_ships[_reset_i].EndKill();
            }
            SetupLevel();
            //Invoke(nameof(StartNew), .1f);
            //WinningSide = 0;
        }
        private List<LevelOptions> _setup_possibleLevels;
        /// <summary>
        /// Called by both ResetLevel(), FinalizeSceneWithUserData(), and SaveAndEnd(). Prepares the LevelStage for a new level
        /// </summary>
        public void SetupLevel()
        {
            //StartTime = Time.realtimeSinceStartup;

            StartTime = Time.realtimeSinceStartup;
            if (ConfigData.ChooseRandomLevel)
            {
                _setup_possibleLevels = ConfigData.GetLevelData().GetLevels().Where((level) => level.Side == ConfigData.Configuration.AISide).ToList();
                CurrentLevelOptions = (LevelOptions)_setup_possibleLevels[Utilities.RandomInt(_setup_possibleLevels.Count)].Clone();
            }
            else if (ConfigData.LevelOptions == null)
            {
                CurrentLevelOptions = new LevelOptions(ConfigData.GetLevelData().GetNewId(), ConfigData.Configuration.AISide, $"Random Level #{ConfigData.GetLevelData().GetNewId()}");
            }
            else
            {
                CurrentLevelOptions = (LevelOptions)ConfigData.LevelOptions.Clone();
            }

            if (Stage.GeneratedSquadCountOverride > 0)
            {
                CurrentLevelOptions.EnemySquadGenerationCount = Stage.GeneratedSquadCountOverride;
            }
            if (CurrentLevelOptions.EnemySquadGenerationCount > 0)
            {
                CurrentLevelOptions.EnemySquadGenerationCount = Utilities.RandomInt(CurrentLevelOptions.EnemySquadGenerationCount - Stage.GeneratedSquadCountMinimum) + 1 + Stage.GeneratedSquadCountMinimum;
            }

            // Reset any data that might have changed from a previous level
            ResetGameData();
            if (ConfigData.LevelOptions != null)
            {
                ConfigData.LevelOptions.ChosenSquads.ForEach((savedSquad) =>
                {
                    CurrentLevelOptions.ChosenSquads.Add(ConfigData.CurrentShips.GetSavedSquad(savedSquad.Id));
                });
            }


            //Debug.Log($"Playing level: {CurrentLevelOptions.Name} with squads: {Utilities.ListToString(CurrentLevelOptions.ChosenSquads)}");
            // Check settings and config variables
            Stage.SetConfigOptionsAndOverrides();

            //Debug.Log($"The human side is {ConfigData.Configuration.HumanSide}, the Bee side is {ConfigData.Configuration.BeeSide}, the AI side is {ConfigData.Configuration.AISide}, the user side is {ConfigData.Configuration.UserSide}");
            //Debug.Log($"The AI Starting position is {AIStartingPosition}, the user starting position is {UserStartingPosition}");

            //Debug.Log($"Chosen squads: {Utilities.ListToString(CurrentLevelOptions.ChosenSquads)}");
            if (Stage.HasRandomizedOptions)
            {
                RandomizeOptions();
            }
            else
            {
                Debug.Log($"The map does not have randomized options");
                CurrentLevelOptions.MapIndex = Stage.OverrideMapIndex;
                MapData = ConfigData.Maps[CurrentLevelOptions.MapIndex];
                Map = Stage.Pool.GetPooledMap(CurrentLevelOptions.MapIndex);


                CurrentLevelOptions.ObstacleMapIndex = Stage.OverrideObstacleMapIndex;
                ObstacleMap = Stage.Pool.GetObstacleMapFromPool(CurrentLevelOptions.ObstacleMapIndex);

                if (CurrentLevelOptions.ObstacleMapIndex > 0)
                {
                    HasObstacles = true;
                }
                else
                {
                    HasObstacles = false;
                }
            }
            SetupMapAndCamera();

            SetupShips();
            if (!Stage.IsTraining)
            {
                MakeSaveLevel();
            }

            AllSquads.AddRange(CurrentLevelOptions.EnemySquads);
            AllSquads.AddRange(CurrentLevelOptions.ChosenSquads);
            AllSquads.AddRange(CurrentLevelOptions.EnemyReinforcements);
            //AllSquads.AddRange(CurrentLevelOptions.FriendlyReinforcements);

            if (ActivateMining)
            {
                SpawnMiningAsteroids();
            }
            if (ActivateFogOfWar && HasPlayer)
            {

                Map.FogOfWar.SetActive(true);
            }

            SetupHivemind();

            //float end = (Time.realtimeSinceStartup - StartTime) * 1000; // seconds to milliseconds
            //Debug.Log($"It took {Math.Round(end, 2)} ms to set up the level and {Math.Round(Time.realtimeSinceStartup, 2)}s total time.");
        }
        /// <summary>
        /// Cleans up the game state and requests and deletes the previous map
        /// </summary>
        public void ResetGameData()
        {
            //int count = 0;
            //GameObject.FindGameObjectsWithTag("Projectile").ToList().ForEach((projectileObject) =>
            //{
            //    Projectile projectile = projectileObject.GetComponent<Projectile>();
            //    try
            //    {
            //        if (!projectile.IsDead)
            //        {
            //            if (projectile.Type == ConfigData.ProjectileTypes.FireBargeExplosion)
            //            {
            //                projectile.Deactivate();
            //            }
            //            else
            //            {
            //                count++;
            //                Debug.Log($"{Name} ended with {projectile.Name} still alive");
            //                Debug.Log(projectile);
            //            }

            //        }
            //    }
            //    catch (Exception e)
            //    {
            //        Debug.Log(projectileObject.name);
            //        throw e;
            //    }

            //});
            //if (count > 0)
            //{
            //    Debug.LogError($"Found alive projectiles at end of level");
            //} 
            Timers.Clear();
            ConfigData.CurrentShips.ReplaceDeadSquadShips();
            State.ResetState();
            State.GameOver = false;
            State.LevelEnded = false;
            Seconds = 0;
            //Socket.StandingRequests.Clear();
            ConfigData.Socket.HandledRequests.Except(HandledRequests);
            HandledRequests.Clear();
            if (!Stage.WatchServerRequests)
            {
                ConfigData.__PastServerRequests.Clear();
            }

            if (Map != null)
            {
                Stage.Pool.ReturnMapToPool(Map);
            }
            AllSquads.Clear();
            CurrentLevelOptions.ChosenSquads.Clear();
        }
        private ScaledTimer _hivemindTimer = new ScaledTimer();
        private ScaledTimer _checkTriggersTimer = new ScaledTimer();
        private ScaledTimer _updateDebugTimer = new ScaledTimer();
        private ScaledTimer _initialCommandDelayTimer = new ScaledTimer();
        public void SetupHivemind()
        {
            CancelTimer(_hivemindTimer);
            CancelTimer(_updateDebugTimer);
            //CancelInvoke(nameof(GetHiveMindCommands));
            if (Stage.ActivateHiveMind)
            {
                //Invoke(nameof(GetHiveMindCommands), Stage.InitialCommandDelay);
                _hivemindTimer.Reuse(.25f, GetHiveMindCommands, true);
                _initialCommandDelayTimer.Reuse(Stage.InitialCommandDelay - .25f, () =>
                {

                    AddTimer(_hivemindTimer);
                });
                AddTimer(_initialCommandDelayTimer);
            }
            if (Stage.IsDebugging)
            {
                _updateDebugTimer.Reuse(1, UpdateDebugVariables, true);
                AddTimer(_updateDebugTimer);
                //InvokeRepeating(nameof(UpdateDebugVariables), 1, 1);
            }
            CancelTimer(_checkTriggersTimer);
            //CancelInvoke(nameof(CheckTriggers));
            if (ActivateLoadingShipsMidLevel)
            {
                // Removed this to avoid dealing with reinforcements
                //SetTriggers();
                //_checkTriggersTimer.Reuse(5, CheckTriggers, true);
                //AddTimer(_checkTriggersTimer);


                //InvokeRepeating(nameof(CheckTriggers), 5, 5);
            }
        }
        public void MakeSaveLevel()
        {
            SaveLevelOptions = new LevelOptions(ConfigData.GetLevelData().GetNewId(), ConfigData.Configuration.AISide, $"Random Level #{ConfigData.GetLevelData().GetNewId()}", CurrentLevelOptions.MapIndex,
                CurrentLevelOptions.ObstacleMapIndex, CurrentLevelOptions.AsteroidOption == 2 ? 2 : (ActivateCollisionAsteroids ? 1 : 0),
                ActivateFogOfWar ? 1 : 0, ActivateMining ? 1 : 0, -1, ActivateLoadingShipsMidLevel ? 1 : 0, CurrentLevelOptions.EnemyReinforcementDelay, CurrentLevelOptions.EnemyShipTypeOption, 0,
                CurrentLevelOptions.EnemyReinforcements.ToList(), CurrentLevelOptions.EnemySquads.ToList(), new List<SavedSquad>());
        }
        public void SetupShips()
        {


            //if (ConfigData.ChooseRandomLevel)
            //{
            //    ConfigData.SquadsChosenForLevel = ConfigData.SquadsChosenForLevel.Where((chosenSquad) => !MidLevelSquads[chosenSquad.Side - 1].Contains(chosenSquad) && 
            //    chosenSquad.Side != CurrentLevelOptions.Side).ToList();
            //    CurrentLevelOptions.EnemySquads.ForEach((enemySquad) =>
            //    {
            //        Debug.Log($"Chose {enemySquad.Name} for level");
            //        ConfigData.SquadsChosenForLevel.Add((SavedSquad)enemySquad.Clone());
            //    });
            //}
            //else
            //{
            //    ConfigData.SquadsChosenForLevel = ConfigData.SquadsChosenForLevel.Where((chosenSquad) => !MidLevelSquads[chosenSquad.Side - 1].Contains(chosenSquad)).ToList();
            //}
            LevelConstructor.SetupShips(ConfigData.Configuration.AISide);
            LevelConstructor.SetupShips(ConfigData.Configuration.UserSide);
            if (State.GetSquadsBySide(ConfigData.Configuration.UserSide).Count > 0 && State.GetSquadsBySide(ConfigData.Configuration.AISide).Count > 0 && !Stage.IsTraining)
            {
                State.SelectSquad(State.GetSquadByNumber(ConfigData.Configuration.UserSide, 1));
            }
            else if (!Stage.IsTraining)
            {
                Debug.Log($"User squads: {State.GetSquadsBySide(ConfigData.Configuration.UserSide).Count}, AI squads: {State.GetSquadsBySide(ConfigData.Configuration.AISide).Count}");
                Pause();
                Stage.Menus.NoAliveShipsAlert.SetActive(true);
            }
            CalculateShipClearances();
            if (CurrentLevelOptions.EnemyReinforcementDelay == 0)
            {
                CurrentLevelOptions.EnemyReinforcementDelay = ConfigData.StandardReinforcementsDelay;
            }
        }
        private ScaledTimer _timeoutTimer = new ScaledTimer();
        public void SetupMapAndCamera()
        {
            Map.Setup(this);

            StartingPositions[ConfigData.Configuration.AISide - 1] = Map.AIStartingPosition;
            StartingPositions[ConfigData.Configuration.UserSide - 1] = Map.UserStartingPosition;

            // Setup map bounds
            MapWidth = (int)(Mathf.Abs(Map.SpriteRenderer.localBounds.min.x) + Map.SpriteRenderer.localBounds.max.x);
            MapHeight = (int)(Mathf.Abs(Map.SpriteRenderer.localBounds.min.y) + Map.SpriteRenderer.localBounds.max.y);
            HalfMapWidth = MapWidth / 2;
            HalfMapHeight = MapHeight / 2;

            MinX = Map.SpriteRenderer.localBounds.min.x + ConfigData.MapEdgePadding.x;
            MinY = Map.SpriteRenderer.localBounds.min.y + ConfigData.MapEdgePadding.y;
            MaxX = Map.SpriteRenderer.localBounds.max.x - ConfigData.MapEdgePadding.x;
            MaxY = Map.SpriteRenderer.localBounds.max.y - ConfigData.MapEdgePadding.y;
            MapX = Map.SpriteRenderer.localBounds.max.x * 2;
            MapY = Map.SpriteRenderer.localBounds.max.y * 2;
            MaxDistance = Mathf.Sqrt(MapX * MapX + MapY * MapY);
            HalfX = MapX / 2;
            HalfY = MapY / 2;


            if (!Stage.IsTraining && !Stage.UnlockCamera && Stage.PrimaryLevel == this)
            {
                Stage.SetupCamera();

                Stage.SquadTabs.ForEach((tab) =>
                {
                    tab.Background.GetComponent<UnityEngine.UI.Image>().color = Color.white;
                    tab.HideTab();
                });

            }
            else
            {
                //CancelInvoke(nameof(TimeOut));
                CancelTimer(_timeoutTimer);
                _timeoutTimer.Reuse(Stage.TimeoutTime, TimeOut);
                AddTimer(_timeoutTimer);
                //Invoke(nameof(TimeOut), Stage.TimeoutTime);
            }


            if (HasObstacles)
            {
                //CancelInvoke(nameof(SpawnAsteroid));
                //CancelInvoke(nameof(SetLocationHistory));
                //InvokeRepeating(nameof(SetLocationHistory), .5f, .5f);
                
                SpawnObstacles();
                Pathfinder = new Pathfinder(this);

            }
        }
        /// <summary>
        /// Resets the level for Hivemind training
        /// </summary>
        private void TimeOut()
        {
            Debug.Log("Level timed out!");
            Stage.__LevelTimeouts++;
            IsRestarting = true;
            SaveAndEnd();
        }
        private int _save_i;
        private SavedSquad _save_savedSquad;
        private FleetShip _save_fleetship;
        private Ship[] _save_ships;
        private Obstacle[] _save_obstacles;
        private ScaledTimer _levelEndedDialogueTimer = new ScaledTimer();
        /// <summary>
        /// Used for standard play and Hivemind Training. Stores commands, cleans the map, and records the stats.
        /// </summary>
        public void SaveAndEnd()
        {
            //Debug.Log($"Saving and ending");

            if (Stage.RecordStats && !Stage.IsTraining)
            {

                for (_save_i = 0; _save_i < AllSquads.Count; _save_i++)
                {
                    _save_savedSquad = AllSquads[_save_i];
                    if (_save_savedSquad.HasBeenSavedToStorage)
                    {
                        _save_savedSquad = ConfigData.CurrentShips.GetSavedSquad(_save_savedSquad.Id);
                    }
                    else
                    {
                        //Debug.Log($"{savedSquad.Name} has not been saved to storage #{savedSquad.Id}");
                        continue;
                    }

                    //Debug.Log($"Logging stats for sqauds battles fought for {savedSquad.Name}");
                    _save_savedSquad.Stats.BattlesFought++;

                    if (_save_savedSquad.Side == WinningSide)
                    {
                        //ConfigData.Ships.GetSavedSquad(savedSquad.Id).Stats.BattlesWon++;
                        _save_savedSquad.Stats.BattlesWon++;
                    }

                    _save_savedSquad.GetSquadShips().ForEach((ship) =>
                    {
                        _save_fleetship = ship.GetFleetShip();
                        _save_fleetship.BattlesFought++;
                        if (_save_fleetship.Side == WinningSide)
                        {
                            _save_fleetship.BattlesWon++;
                        }
                        //Debug.Log($"{fleetShip.Name} has mined {fleetShip.MineralsMined} minerals in its lifetime. It has mined {fleetShip.MineralsMinedThisLevel} minerals this level");
                        _save_fleetship.MineralsMined += _save_fleetship.MineralsMinedThisLevel;
                        _save_fleetship.MineralsMinedThisLevel = 0;

                    });

                }

                ConfigData.CurrentShips.SaveFleetData();
                ConfigData.CurrentShips.SaveSquadData();

                if (ConfigData.IsPlayingCampaign)
                {
                    SaveCampaignStats();
                }
                
            }
            //Debug.Log($"Resetting scene");
            _save_ships = State.GetShips().ToArray(); // need to convert this to an array because killing a ship removes it from the list of ships in the state

            for (_save_i = 0; _save_i < _save_ships.Length; _save_i++)
            {
                _save_ships[_save_i].EndKill();
            }
            // Should probably remove this
            //if (IsRestarting)
            //{
            //    GetComponents<Command>().ToList().ForEach((command) =>
            //    {
            //        command.SetFinalize("Restarting");
            //    });
            //}
            if (HasObstacles)
            {
                Stage.Pool.ReturnObstacleMapToPool(ObstacleMap, CurrentLevelOptions.ObstacleMapIndex);
            }
            if (State.Obstacles.Count > 0) // Level can have obstacles from asteroids, obstacles, and mining asteroids
            {
                

                _save_obstacles = State.Obstacles.ToArray();
                for (_save_i = 0; _save_i < _save_obstacles.Length; _save_i++)
                {
                    if (_save_obstacles[_save_i].ObstacleType == ConfigData.ObstacleTypes.CollisionAsteroid)
                    {
                        ((CollisionAsteroid)_save_obstacles[_save_i]).Kill(true);
                    }
                    else if (_save_obstacles[_save_i].ObstacleType == ConfigData.ObstacleTypes.MiningAsteroid)
                    {
                        ((MiningAsteroid)_save_obstacles[_save_i]).Kill(true);
                    }
                    else
                    {
                        Debug.LogError($"{_save_obstacles[_save_i].Name} does not have valid obstacle type: {_save_obstacles[_save_i].ObstacleType}");
                    }
                }
            }

            if (State.Projectiles.Count > 0)
            {
                State.Projectiles.ToList().ForEach((projectile) =>
                {
                    //Debug.Log($"Killing {projectile.Name} at the end of the level");
                    projectile.Kill();
                });
            }


            while (State.Deadbodies.Count > 0)
            {
                State.Deadbodies[0].Kill();
                State.Deadbodies.Remove(State.Deadbodies[0]);
            }

            


            //StartNew();


            //Invoke(nameof(StartNew), .1f);
            //Invoke(nameof(ReloadScene), 1f);
            State.StoreCommands();
            State.Release();
            if (!Stage.IsTraining && !Stage.Menus.IsMiniMapOpen)
            {
                Stage.Menus.ToggleMiniMapDisplay();
            }

            //Debug.Log($"{Name} ended and cleared");

            if (Stage.DoesUserHaveController && !IsRestarting)
            {
                _levelEndedDialogueTimer.Reuse(1, LevelEndedDialogue);
                AddTimer(_levelEndedDialogueTimer);

                //Invoke(nameof(LevelEndedDialogue), 1f);
            }
            else
            {
                IsRestarting = false;
                SetupLevel();
            }

        }
        private UserProgressData _saveCampaign_progress = ConfigData.GetUserProgressData();
        private int _saveCampaign_i;
        private SavedSquad _saveCampaign_savedSquad;
        private FleetShip _saveCampaign_fleetShip;
        private void SaveCampaignStats()
        {
            if (WinningSide == ConfigData.Configuration.HumanSide)
            {
                _saveCampaign_progress.HumanWins++;
            }
            else
            {
                _saveCampaign_progress.BeeWins++;
            }

            if (WinningSide == ConfigData.Configuration.UserSide)
            {
                _saveCampaign_progress.AdvanceToNextLevel();
            }

            for (_saveCampaign_i = 0; _saveCampaign_i < AllSquads.Count; _saveCampaign_i++)
            {
                _saveCampaign_savedSquad = AllSquads[_saveCampaign_i];
                _saveCampaign_savedSquad.GetSquadShips().ForEach((ship) =>
                {
                    _saveCampaign_fleetShip = ship.GetFleetShip();
                    //Debug.Log($"{fleetShip.Name} has mined {fleetShip.MineralsMined} minerals in its lifetime. It has mined {fleetShip.MineralsMinedThisLevel} minerals this level");
                    if (_saveCampaign_fleetShip.Side == ConfigData.Configuration.UserSide)
                    {
                        _saveCampaign_progress.MinedTSV += _saveCampaign_fleetShip.MineralsMinedThisLevel;
                    }
                    else
                    {
                        _saveCampaign_progress.HivemindMinedTSV += _saveCampaign_fleetShip.MineralsMinedThisLevel;
                    }

                });
            }

            _saveCampaign_progress.Save();

            if (!Stage.IsTraining)
            {
                Stage.Menus.UpdateScore(_saveCampaign_progress.HumanWins, _saveCampaign_progress.BeeWins);
            }
        }
        private void LevelEndedDialogue()
        {
            Stage.Menus.OpenLevelEndedDialogue();

            if (ConfigData.IsPlayingCampaign)
            {
                if (WinningSide == ConfigData.Configuration.UserSide)
                {
                    Stage.Menus.TryNewSquadsButtonText.text = "Play next level";
                }
                else
                {
                    Stage.Menus.TryNewSquadsButtonText.text = "Try again";
                }
                Stage.Menus.KeepGoingButton.SetActive(false);
            }
        }
        public void Pause()
        {
            //Debug.Log("Paused!");
            if (Stage.ActivateAudio)
            {
                Stage.Audio.Pause();
            }
            State.IsPaused = true;
            Time.timeScale = 0;
        }
        public void UnPause()
        {
            //Debug.Log("UN Paused!");
            State.IsPaused = false;
            if (Stage.ActivateAudio && Stage.PlayMusic)
            {
                Stage.Audio.Play();
            }
            Time.timeScale = Stage.TimeScale;
        }
        private Projectile _f_projectile;
        private int _projectile_power;
        /// <summary>
        /// Adds projectiles to the game. Some projectiles don't use this
        /// </summary>
        /// <param name="type"></param>
        /// <param name="weapon"></param>
        /// <param name="startingPosition"></param>
        /// <param name="angle"></param>
        /// <returns></returns>
        public Projectile AddProjectile(ConfigData.ProjectileTypes type, Weapon weapon, Vector2 startingPosition, float angle)
        {
            //Debug.Log($"Adding projectile {instance.name} at startingPosition: {startingPosition}");
            _f_projectile = Stage.Pool.GetProjectileFromPool(type);
            _f_projectile.transform.parent = Map.transform;

            _projectile_power = weapon.Power;
            if (weapon.Type == ConfigData.WeaponTypes.DualCannon)
            {
                //Debug.Log("This is a dual cannon, splitting the power");
                _projectile_power /= 2;
            }
            //Debug.Log($"Position before setup for {projectile.Id}: {instance.transform.localPosition}, {projectile.GetPosition()}");
            _f_projectile.Setup(this, weapon, weapon.Ship, weapon.TargetShip, startingPosition, angle, weapon.Range, _projectile_power);
            weapon.Ship.ProjectilesInFlight.Add(_f_projectile);
            //Debug.Log($"Position after setup for #{projectile.Id}: {instance.transform.localPosition}, {projectile.GetPosition()}");
            return _f_projectile;
        }
        private Queue<Squad> _hive_squads;
        private Squad _hive_squad;
        //private ScaledTimer _hivemindTimer;
        /// <summary>
        /// Runs though all the hive mind squads that need commands and makes matchup strategy requests for them
        /// </summary>
        private void GetHiveMindCommands()
        {
            //Debug.Log("Giving command");
            if (!State.IsPaused && Stage.ActivateHiveMind && IsLevelSetupOnServer)
            {
                _hive_squads = State.GetSquadsAwaitingHiveMindCommands();  
                while (_hive_squads.Count > 0)
                {
                    _hive_squad = _hive_squads.Dequeue();
                    if (!_hive_squad.IsDead)
                    {
                        //Debug.Log("Giving command");
                        //Debug.Log($"asking for matchup strat");
                        //Debug.Log(squad.damageSentToEnemyShipsBySquad);
                        _hive_squad.MakeMatchupStrat();
                    }
                }
            }
            //Invoke(nameof(GetHiveMindCommands), .25f); // No longer needs to be reused as this can just be called repeatedly on a timer
        }

        public Vector2 GetPosition()
        {
            return transform.localPosition;
        }
        public Vector3 Get3DPosition()
        {
            return transform.localPosition;
        }
        public Vector2 ForceBounds(Vector2 point)
        {
            return ForceBounds(point.x, point.y);
        }
        public Vector2 ForceBounds(float x, float y)
        {
            return Utilities.ForceBounds(x, y, MaxX, MaxY, MinX, MinY);
        }
    }
}
