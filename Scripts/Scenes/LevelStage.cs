using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Level;
using Assets.Scripts.Level.Commands;
using Assets.Scripts.Server;
using Assets.Scripts.UI_Components;
using Assets.Scripts.UIComponents;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Unity.MLAgents;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace Assets.Scripts.Scenes
{
    public class LevelStage : Scene
    {
        //public float __RotationTest;
        //public Vector2 __OriginalPosition;
        private GameState _state;
        // If hivemind is activate, get commands from the server
        // If brains are activated, get actions from the nueral network
        // If IsTrainingNueralNetwork, train the neural network. IsTrainingHiveMind, train the hive mind
        // Training Hivemind or Nueral Network then there is no player, levels are reset every time, and the camera position doesn't matter
        /// <summary>
        /// Determines whether or not FleetShips get marked as dead when ships die. If this is turned off, stats will still record properly but ships won't die off and be replaced
        /// </summary>
        public bool ReplaceDeadShips;
        public bool ActivateHiveMind, ActivateBrains, IsTrainingNueralNetwork, IsTrainingHiveMind, IsTraining, UseFullyRandomSquads, UseFullyRandomEnemySquads, UseOverrideSquads, 
            UseOverrideEnemySquads, RecordStats, DoesUserHaveController, HasObstacles, ActivateCollisionAsteroids, ActivateMining, ActivateFogOfWar, ActivateAudio, ActivateLoadingShipsMidLevel, 
            UseMouseScrolling, IsDebugging, IsTestFiring, MakeEnemyCeaseFire, FullCeaseFire, MakeShotsHarmless, UnlockCamera, HasRandomizedOptions, PlayMusic;
        public int OverrideMapIndex,OverrideTimeScale, OverrideObstacleMapIndex, OverrideUserSide, SpeedMultiplier, GeneratedSquadCountOverride, InitialCommandDelay, TimeoutTime;
        public List<string> OverrideStrats = new List<string> { };
        public List<string> OverrideBeeShipTypes = new List<string> { };
        public List<string> OverrideHumanShipTypes = new List<string> { };
        public GameObject UIManager, SelectionBox, MiniMapContainer;
        public UI_Components.Map Map;
        public AudioController Audio;
        public Camera MiniMapCamera;
        public GameMenus Menus;
        public LevelInputManager InputManager;
        public Selector Selector;
        public LevelConstructor LevelConstructor;
        public Pathfinder Pathfinder;
        public Sprite VisonSprite;
        public SimpleMultiAgentGroup AgentGroup;
        public SimpleMultiAgentGroup HumanAgentGroup;
       

        public GameObject BargePrefab, BeehivePrefab, BumblebeePrefab, CarpenterBeePrefab, CarrierPrefab, CruiserPrefab, DreadnoughtPrefab, DronePrefab,
            FactoryPrefab, FireShipPrefab, FlagshipPrefab, FrigatePrefab, GunshipPrefab, HoneybeePrefab, HornetPrefab, LeafcutterPrefab, QueenPrefab,
            ScoutPrefab, StrikerPrefab, WarpGatePrefab, WaspPrefab, YellowJacketPrefab, BeaconPrefab, ValidPrefab, InvalidPrefab, MovementMarkerPrefab, TargetingMarkerPrefab,
            SquadBox;
        /// <summary>
        /// How frequently asteroids spawn in this level. Sets the upper and lower bounds in seconds of the randomly timed spawn
        /// </summary>
        public int AsteroidMaxSpawnRate, AsteroidMinimumSpawnRate, CurrentAsteroidMaxSpawnRate, CurrentAsteroidMinimumSpawnRate;
        /// <summary>
        /// Sets the upper bounds for how fast an asteroid can move
        /// </summary>
        public int AsteroidMaxSpeed;
        public List<GameObject> EmptyObstacleList, MazePrefabs, ThreePathsPrefabs, ForestPrefabs, TheWallPrefabs = new List<GameObject>();
        public List<GameObject> MiningAsteroidPrefabs = new List<GameObject>();
        public List<GameObject> CollisionAsteroidPrefabs = new List<GameObject>();
        /// <summary>
        /// Asteroids that can potentially be spawned from larger asteroids breaking apart
        /// </summary>
        public List<GameObject> BreakawayAsteroids = new List<GameObject>();
        /// <summary>
        /// Asteroid pieces that spawn (and don't collide) from larger asteroids breaking apart
        /// </summary>
        public List<GameObject> AsteroidPieces = new List<GameObject>();
        /// <summary>
        /// A list of possible maps to load
        /// </summary>
        public List<GameObject> Maps = new List<GameObject>();

        public float MinX, MinY, MaxX, MaxY;
        public int DefaultZoom, ZoomSpeed, ScrollSpeed;
        public Vector2 MouseScrollDistanceFromEdge, DefaultCameraPosition;
        public Vector2[] StartingPositions = new Vector2[2];

        public List<string> HasBeeTypes = new List<string>();
        public List<string> FoundBeeTypes = new List<string>();
        /// <summary>
        /// Whether or not the level has been setup initially on the server
        /// </summary>
        public bool IsLevelSetupOnServer;
        /// <summary>
        /// Whether or not this level is currently connected and setup on the server, regardless of whether other levels are connected
        /// </summary>
        public bool IsLevelConnectedToServer;
        public bool IsLoaded = false;
        public bool RetriedConnection, IsRestarting;
        public bool HasPlayer;
        /// <summary>
        /// Whether or not a tester pressed the pause key to pause the game
        /// </summary>
        public bool IsPausedByTester;
        public int WinningSide;
        public float MapX, MapY, MaxDistance, HalfX, HalfY;
        public int MapWidth, MapHeight, HalfMapWidth, HalfMapHeight, MaximumClearance;
        public Dictionary<string, int> ShipClearances = new Dictionary<string, int>();

        public float Seconds;
        public HashSet<int> HandledRequests = new HashSet<int>();
        public int FixedUpdates, TriggersActivated;
        public float StartTime, TimePaused;
        public List<Trigger> Triggers = new List<Trigger>();
        public List<string> BeeShipTypes, HumanShipTypes = new List<string>();
        public List<SquadTab> SquadTabs;
        public LevelOptions SaveLevelOptions;
        public LevelOptions CurrentLevelOptions;
        public Data.Map MapData;
        public List<SavedSquad> AllSquads = new List<SavedSquad>();




        public float CurrentZoom => Camera.orthographicSize;
        public bool HasFoundAllBees => HasBeeTypes.Count == FoundBeeTypes.Count;
        public bool DidUserWin => WinningSide == ConfigData.Configuration.UserSide;
        public bool IsPaused => GetState().IsPaused;

        public List<string> __BeeHivemindShips, __HumanHivemindShips, __PastCommands, __PathfindingThreads;


        private List<GameObject> _chosenObstacles;
        private Dictionary<int, List<GameObject>> _obstacleLists;

        private void UpdateDebugVariables()
        {
            __BeeHivemindShips = GetState().GetShipsVisibleToHiveMind(ConfigData.Configuration.BeeSide).Select(s => s.ToString()).ToList();
            __HumanHivemindShips = GetState().GetShipsVisibleToHiveMind(ConfigData.Configuration.HumanSide).Select(s => s.ToString()).ToList();
            __PastCommands = GetState().GetPastCommands().Select((c) => $"Command #{c.OutcomeId} - {c.Strategy.Name} for Squad {c.Squad} against [{c.Enemy}] with {c.Tsv} TSV").ToList();
            
            if (Pathfinder != null)
            {
                __PathfindingThreads = Pathfinder.IsThreadActive.Select((s, i) => $"#{i} - {(s ? Pathfinder.Ships[i].Name : s)}").ToList();
            }

            //string path = $"{ConfigData.GetBasePath()}/debug/minimap_{Utilities.Hash()}.png";
            //Texture2D dest = new Texture2D(MiniMapTexture.width, MiniMapTexture.height, TextureFormat.RGB24, false);
            //RenderTexture.active = MiniMapTexture;
            //dest.ReadPixels(new Rect(0, 0, MiniMapTexture.width, MiniMapTexture.height), 0, 0);
            //dest.Apply();
            //File.WriteAllBytes(path, dest.EncodeToPNG());
            GetState().UpdateDebugVariables();
        }

        new void Start()
        {
            //Debug.Log($"Start level scene");
            Name = "Level";
            base.Start();
        }

        private void RandomizeOptions()
        {
            Debug.Log($"Randomizing options...");
            //Debug.Log($"Level selection option: {ConfigData.SelectedLevelMapIndex}");

            if (CurrentLevelOptions.MapIndex == -1)
            {
                CurrentLevelOptions.MapIndex = Utilities.RandomInt(Maps.Count);              
            }
            MapData = ConfigData.Maps[CurrentLevelOptions.MapIndex];
            Map = Instantiate(Maps[CurrentLevelOptions.MapIndex]).GetComponent<UI_Components.Map>();
            Debug.Log($"Playing on the {MapData.Name} ({Map.Name}) at index #{CurrentLevelOptions.MapIndex} map");

            if ((CurrentLevelOptions.ObstacleMapIndex == -1 && Utilities.CoinToss()) || CurrentLevelOptions.ObstacleMapIndex > 0) // User chose random and random chose obstacles OR user chose obstacles
            {
                HasObstacles = true;
                Debug.Log($"The map has obstacles");
                if (CurrentLevelOptions.ObstacleMapIndex == -1)
                {
                    CurrentLevelOptions.ObstacleMapIndex = Utilities.RandomInt(_obstacleLists.Count - 1) + 1;
                }
                _chosenObstacles = _obstacleLists.GetValueOrDefault(CurrentLevelOptions.ObstacleMapIndex);

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
                
                if ((CurrentLevelOptions.AsteroidOption == -1 && Utilities.CoinToss()) || CurrentLevelOptions.AsteroidOption > 0) // User chose random and random chose asteroids OR User chose asteroids
                {
                    HasObstacles = true;
                    _chosenObstacles = EmptyObstacleList;
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

            if (DoesUserHaveController && ((CurrentLevelOptions.FogOfWar == -1 && Utilities.CoinToss()) || CurrentLevelOptions.FogOfWar == 1))
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
            }
            else
            {
                ActivateLoadingShipsMidLevel = false;
                Debug.Log($"The map does not have ships loading midlevel");
            }

            if (CurrentLevelOptions.EnemyShipTypeOption == -1)
            {
                if (ConfigData.Configuration.AISide == ConfigData.Configuration.BeeSide)
                {
                    BeeShipTypes = new List<string>() { BeeShipTypes[Utilities.RandomInt(BeeShipTypes.Count)] };
                    Debug.Log($"The user has selected randomized enemy ship type: {BeeShipTypes[0]}");
                }
                else
                {
                    HumanShipTypes = new List<string>() { HumanShipTypes[Utilities.RandomInt(HumanShipTypes.Count)] };
                    Debug.Log($"The user has selected randomized enemy ship type: {HumanShipTypes[0]}");
                }

            }
            else if (CurrentLevelOptions.EnemyShipTypeOption == 0)
            {
                Debug.Log($"The map does not have a singular enemy ship type");
                if (OverrideBeeShipTypes.Count > 0)
                {
                    BeeShipTypes = OverrideBeeShipTypes;
                }
                else
                {
                    BeeShipTypes = ConfigData.BeeShipTypes.ToList();
                }

                if (OverrideHumanShipTypes.Count > 0)
                {
                    HumanShipTypes = OverrideHumanShipTypes;
                }
                else
                {
                    HumanShipTypes = ConfigData.HumanShipTypes.ToList();
                }
            }
            else
            {
                if (ConfigData.Configuration.AISide == ConfigData.Configuration.BeeSide)
                {
                    BeeShipTypes = new List<string>() { BeeShipTypes[CurrentLevelOptions.EnemyShipTypeOption-1] };
                    Debug.Log($"The user has selected enemy ship type: {BeeShipTypes[0]}");
                }
                else
                {
                    HumanShipTypes = new List<string>() { HumanShipTypes[CurrentLevelOptions.EnemyShipTypeOption-1] };
                    Debug.Log($"The user has selected enemy ship type: {HumanShipTypes[0]}");
                }
            }

        }
        /// <summary>
        /// Takes care of any setup that needs to happen the first time the scene is loaded
        /// </summary>
        protected override void FinalizeSceneWithUserData()
        {
            //Debug.Log($"Finalize scene");
            //StartTime = Time.realtimeSinceStartup;
            if (ConfigData.IsPlayingCampaign)
            {
                ReplaceDeadShips = true;
                RecordStats = true;                
            }
            if (!ConfigData.Configuration.DoesUserHaveController && !DoesUserHaveController)
            {
                Invoke(nameof(TimeOut), TimeoutTime);
            }
            
            LevelConstructor = new LevelConstructor(this);
            LevelConstructor.RequestServerSetup();
            base.FinalizeSceneWithUserData();
            IsLoaded = true;

            if (IsTrainingHiveMind || IsTrainingNueralNetwork)
            {
                IsTraining = true;
            }
            else
            {
                IsTraining = false;
            }

            if (DoesUserHaveController)
            {
                HasPlayer = true;
                if ((OverrideUserSide == 1 || OverrideUserSide == 2) && OverrideUserSide != ConfigData.Configuration.UserSide)
                {
                    ConfigData.SwapSides();
                }
                if (ConfigData.IsUserLoadingCustomSquads)
                {
                    UseFullyRandomSquads = false;
                }
                if (ConfigData.IsUserLoadingCustomEnemySquads)
                {
                    UseFullyRandomEnemySquads = false;
                }
            }
            else
            {
                HasPlayer = ConfigData.Configuration.DoesUserHaveController;
            }

            _obstacleLists = new Dictionary<int, List<GameObject>>()
                {
                    {0, EmptyObstacleList }, // it's important to have this here so we choose an empty level for testing
                    {1, MazePrefabs },
                    {2, ThreePathsPrefabs },
                    {3, ForestPrefabs },
                    {4, TheWallPrefabs }
                };

            if (ActivateBrains)
            {
                AgentGroup = new SimpleMultiAgentGroup();
                HumanAgentGroup = new SimpleMultiAgentGroup();

                if (IsTrainingNueralNetwork)
                {
                    Academy.Instance.OnEnvironmentReset += () =>
                    {
                        //Debug.Log($"Reset environment, {Academy.Instance.StepCount}");
                    };

                }
            }

            // Setup Game State
            _state = gameObject.AddComponent<GameState>();
            _state.Setup(this);

            if (HasPlayer)
            {

                // Setup  Game menu 
                Menus = UIManager.GetComponentInChildren<GameMenus>();
                Menus.Setup(this);
                Menus.ActionBox.Setup(this, EventSystem, ConfigData.Configuration.UserSide);


                // Setup Selection Box
                Selector = SelectionBox.GetComponentInChildren<Selector>();
                Selector.Setup(this, SelectionBox);
                // Setup input manager
                InputManager = new LevelInputManager(this, Selector);


                // Setup Squad Action Box
                if (ActivateAudio && Audio != null)
                {
                    Audio.Setup(PlayMusic);
                }

                if (!ConfigData.IsPlayingCampaign)
                {
                    Menus.Scoreboard.SetActive(false);
                }
                else
                {
                    Menus.UpdateScore(ConfigData.GetUserProgressData().HumanWins, ConfigData.GetUserProgressData().BeeWins);
                }


            }
            else
            {
                if (Audio != null)
                {
                    Audio.gameObject.SetActive(false);
                }
            }
            SetupLevel();

            //// Setup map bounds
            //MapWidth = (int)(Mathf.Abs(Map.SpriteRenderer.localBounds.min.x) + Map.SpriteRenderer.localBounds.max.x);
            //MapHeight = (int)(Mathf.Abs(Map.SpriteRenderer.localBounds.min.y) + Map.SpriteRenderer.localBounds.max.y);
            //HalfMapWidth = MapWidth / 2;
            //HalfMapHeight = MapHeight / 2;



            //MinX = Map.SpriteRenderer.localBounds.min.x + ConfigData.MapEdgePadding.x;
            //MinY = Map.SpriteRenderer.localBounds.min.y + ConfigData.MapEdgePadding.y;
            //MaxX = Map.SpriteRenderer.localBounds.max.x - ConfigData.MapEdgePadding.x;
            //MaxY = Map.SpriteRenderer.localBounds.max.y - ConfigData.MapEdgePadding.y;
            //MapX = Map.SpriteRenderer.localBounds.max.x * 2;
            //MapY = Map.SpriteRenderer.localBounds.max.y * 2;
            //MaxDistance = Mathf.Sqrt(MapX * MapX + MapY * MapY);
            //HalfX = MapX / 2;
            //HalfY = MapY / 2;

            if (HasPlayer && !UnlockCamera)
            {

                Vector2 cameraWorldUnitsSize = Utilities.ScreenPixelsToWorldUnits(new Vector2(MiniMapCamera.pixelWidth, MiniMapCamera.pixelHeight), Camera);
                Transform colliderContainer = Camera.transform.GetChild(0);
                colliderContainer.localScale = cameraWorldUnitsSize;
                Vector2 localizedPosition = DefaultCameraPosition + (Vector2)transform.position;
                Camera.transform.position = new Vector3(localizedPosition.x, localizedPosition.y, -10);

                InputManager.MaintainScrollBoundary();
            }

            //SetupLevel();

            //float end = (Time.realtimeSinceStartup - StartTime) * 1000; // seconds to milliseconds
            //Debug.Log($"It took {Math.Round(end, 2)} ms to set up the level and {Math.Round(Time.realtimeSinceStartup, 2)}s total time.");
        }
        public void CalculateShipClearances()
        {
            List<Ship> ships = GetState().GetShips();
            while (ships.Count > 0)
            {
                string shipType = ships[0].ShipType;

                if (!ShipClearances.ContainsKey(shipType))
                {
                    float width = ships[0].GetHalfWidth();
                    float height = ships[0].GetHalfHeight();
                    int clearance = (width > height ? Mathf.CeilToInt(width) : Mathf.CeilToInt(height));

                    while (clearance % Pathfinder.Scale > 0) // round the clearance up to the nearest multiple of Scale (e.g. round 13 to 16 if the Scale is 4)
                    {
                        clearance++;
                    }
                    clearance /= Pathfinder.Scale;
                    clearance += 2; // 2 for padding
                    clearance = Math.Max(clearance, ConfigData.MinimumClearance);

                    ShipClearances.Add(shipType, clearance);
                    ships.ForEach((s) =>
                    {
                        if (s.ShipType == shipType)
                        {
                            s.Clearance = clearance;
                        }

                    });

                    if (clearance > MaximumClearance)
                    {
                        MaximumClearance = clearance;
                    }
                }



                ships = ships.Where((s) => s.ShipType != shipType).ToList();
            }
        }
        private void SpawnObstacles()
        {
            GameState state = GetState();
            CurrentAsteroidMaxSpawnRate = AsteroidMaxSpawnRate;
            CurrentAsteroidMinimumSpawnRate = AsteroidMinimumSpawnRate;
            _chosenObstacles.ForEach((prefab) =>
            {
                Vector2 position = prefab.transform.position;
                GameObject instance = Instantiate(prefab);
                instance.transform.parent = Map.transform;
                instance.transform.localPosition = position;
                state.AddObstacle(instance.GetComponent<Obstacle>());
            });

            if (ActivateCollisionAsteroids)
            {
                if (CurrentLevelOptions.AsteroidOption == 2)
                {
                    CurrentAsteroidMaxSpawnRate /= 2;
                    CurrentAsteroidMinimumSpawnRate /= 2;
                }
                Invoke(nameof(SpawnAsteroid), AsteroidMinimumSpawnRate + Utilities.RandomInt(CurrentAsteroidMaxSpawnRate - CurrentAsteroidMinimumSpawnRate));
            }
        }
        private void SpawnMiningAsteroids()
        {
            GameState state = GetState();
            MiningAsteroidPrefabs.ForEach((prefab) =>
            {
                Vector2 position = prefab.transform.position;
                GameObject instance = Instantiate(prefab);
                instance.transform.parent = Map.transform;
                instance.transform.localPosition = position;
                MiningAsteroid asteroid = instance.GetComponent<MiningAsteroid>();
                state.AddObstacle(asteroid);
                asteroid.Setup(this, state.GetId());

            });
        }
        private void SpawnAsteroid()
        {
            GameObject instance = Instantiate(CollisionAsteroidPrefabs[Utilities.RandomInt(CollisionAsteroidPrefabs.Count)]);
            AddAsteroid(instance);
            Invoke(nameof(SpawnAsteroid), AsteroidMinimumSpawnRate + Utilities.RandomInt(CurrentAsteroidMaxSpawnRate - CurrentAsteroidMinimumSpawnRate));
        }

        public CollisionAsteroid AddAsteroid(GameObject instance)
        {
            GameState state = GetState();
            instance.transform.parent = Map.transform;
            CollisionAsteroid asteroid = instance.GetComponent<CollisionAsteroid>();
            state.AddObstacle(asteroid);
            asteroid.Setup(this, state.GetId());

            asteroid.MapPointsIndex = Pathfinder.AddObstacle(asteroid);
            return asteroid;
        }
        private void SetTriggers()
        {
            Triggers.Clear();

            Triggers.Add(new Trigger(() =>
            {
                return Time.realtimeSinceStartup - StartTime >= CurrentLevelOptions.EnemyReinforcementDelay;
            }, () =>
            {
                Debug.Log($"{CurrentLevelOptions.EnemyReinforcementDelay} seconds have passed, spawning new enemy ships for side {ConfigData.Configuration.AISide}: {Utilities.ListToString(CurrentLevelOptions.EnemyReinforcements)}");
                Vector2 moveToPoint = StartingPositions[ConfigData.Configuration.AISide - 1];
                LevelConstructor.SpawnShipsAndSquads(CurrentLevelOptions.EnemyReinforcements, StartingPositions[ConfigData.Configuration.AISide - 1] * new Vector2(0, 2), moveToPoint);

            }));

            //Triggers.Add(new Trigger(() =>
            //{
            //    return GetState().GetShips(ConfigData.Configuration.UserSide).Count <= 3;
            //}, () =>
            //{
            //    Debug.Log($"There are only three (or fewer) of our ships left, spawning new friendly ships");
            //    LevelConstructor.AddShipsMidLevel(MidLevelSquads[ConfigData.Configuration.UserSide - 1], StartingPositions[ConfigData.Configuration.UserSide - 1] * new Vector2(0, 3), StartingPositions[ConfigData.Configuration.UserSide - 1]);

            //}));

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
                CancelInvoke(nameof(CheckTriggers));
            }
        }
        new void Update()
        {
            //GameObject.Find("Rotated Point").transform.position = Utilities.RotatePointAroundPoint(GameObject.Find("Pivot").transform.position, __OriginalPosition, __RotationTest);
            base.Update();
            //if (UseRLServer)
            //{
            //    RLSocket.Update();
            //}
            if (IsLoaded)
            {
                GameState state = GetState();
                if (state.GameOver && !state.LevelEnded)
                {
                    LevelOver();
                }

                if ((state.IsPaused || ConfigData.SocketManager.NetworkDisconnection.IsOpen || !IsLevelConnectedToServer) && !IsTrainingNueralNetwork)
                {
                    Time.timeScale = 0;
                    if (!IsTraining)
                    {
                        if (IsPausedByTester && InputManager.HasPauseInput() && Time.realtimeSinceStartup - TimePaused > 1)
                        {
                            IsPausedByTester = false;
                            TimePaused = Time.realtimeSinceStartup;
                            UnPause();
                        }
                    }
                }
                else
                {
                    if (!IsTrainingNueralNetwork)
                    {
                        Time.timeScale = TimeScale;
                        if (!IsTrainingHiveMind)
                        {
                            if (HasPlayer)
                            {
                                InputManager.Update();
                            }
                            if (HasObstacles)
                            {
                                //Debug.Log($"Calling path finder update again");
                                Pathfinder.Update();
                            }

                        }
                    }

                    //InputManager.Update();

                }
            }

        }
        private void FixedUpdate()
        {
            FixedUpdates++;
        }

        /// <summary>
        /// Ends the level and marks the winner
        /// </summary>
        public void LevelOver() // [stats-method] [note]
        {
            if (!IsTrainingNueralNetwork)
            {
                Pause();
                //Debug.Log("LEVEL OVER!");
                GameState state = GetState();

                state.GetAllSquads().ForEach((squad) =>
                {
                    if (squad.HasCommand)
                    {
                        squad.Command.SetFinalize("Level ended");
                    }
                });

                state.LevelEnded = true;
                float fps = Time.frameCount / Time.unscaledTime;
                float fups = FixedUpdates / Time.unscaledTime;
                ConfigData.__TotalLength += Time.realtimeSinceStartup - StartTime;
                ConfigData.__AverageLatency = ConfigData.__TotalLatency / ConfigData.__TotalRequests;

                Debug.Log($"{$"fps: {fps}".PadRight(10).Substring(0, 10)}  {$"fups: {fups}".PadRight(10).Substring(0, 10)}     " +
                      $"{$"latency: {(int)(ConfigData.__AverageLatency * 1000)}ms".PadRight(18)} {$"CPS: {ConfigData.__HivemindCommands / Time.unscaledTime}".PadRight(9).Substring(0, 9)}   " +
                      $"LTO: {ConfigData.__LevelTimeouts} AveLT: {(int)ConfigData.__AverageLength}s");

                if (state.IsSideKilled(ConfigData.Configuration.BeeSide) && !state.IsSideKilled(ConfigData.Configuration.HumanSide))
                {
                    WinningSide = ConfigData.Configuration.HumanSide;
                }
                else if (state.IsSideKilled(ConfigData.Configuration.HumanSide) && !state.IsSideKilled(ConfigData.Configuration.BeeSide))
                {
                    WinningSide = ConfigData.Configuration.BeeSide;
                }
                else if (state.IsSideKilled(ConfigData.Configuration.HumanSide) && state.IsSideKilled(ConfigData.Configuration.BeeSide))
                {
                    Debug.Log("Both sides are dead!");
                }
                else
                {
                    Debug.Log("Neither side is dead!");
                }

                UnPause();
            }
            

            if (IsTrainingNueralNetwork)
            {
                
                ResetLevel(false);
            }
            else
            {
                if (IsTrainingHiveMind)
                {
                    SaveAndEnd();

                }
                else
                {
                    if (GetState().FireShipExplosions.Count > 0)
                    {
                        Invoke(nameof(SaveAndEnd), 5f);

                    }
                    else
                    {
                        Invoke(nameof(SaveAndEnd), .5f);
                    }

                }
            }



        }
        /// <summary>
        /// Used for Nueral Network training. Resets the level.
        /// </summary>
        /// <param name="isStepTimeout"></param>
        public void ResetLevel(bool isStepTimeout)
        {

            Academy.Instance.StatsRecorder.Add("Episode Time", Seconds);

            //Debug.Log($"Reset level ({Seconds}), Unclamped Bee reward: {BeeCumaltiveReward}, Unclamped Human reward: {HumanCumulativeReward}");
            GameState state = GetState();
            Ship[] ships = state.GetShips().ToArray();

            state.GameOver = false;
            state.LevelEnded = false;
            float remainingHumanTsv = ships.Where((s) => s.Side == ConfigData.Configuration.HumanSide).Sum((s) => s.Tsv);
            float remainingHumanTSVPercentage = remainingHumanTsv / state.InitialTsv[ConfigData.Configuration.HumanSide - 1];

            float remainingBeeTsv = ships.Where((s) => s.Side == ConfigData.Configuration.BeeSide).Sum((s) => s.Tsv);
            float remainingBeeTSVPercentage = remainingBeeTsv / state.InitialTsv[ConfigData.Configuration.BeeSide - 1];

            //if (Utilities.RandomInt(10) > 7)
            //{
            //    UserStartingPosition = new Vector2(Utilities.RandomInt((int)MaxX * 2), Utilities.RandomInt((int)MaxY * 2)) - new Vector2(MaxX, MaxY);
            //    //UserStartingPosition = new Vector2(Utilities.RandomInt((int)MaxX), Utilities.RandomInt((int)MaxY)) - new Vector2(MaxX, 0);
            //}

            //UserStartingPosition = new Vector2(Utilities.RandomInt((int)MaxX * 2), UserStartingPosition.y*2) - new Vector2(MaxX, UserStartingPosition.y);

            //AIStartingPosition = new Vector2(Utilities.RandomInt((int)MaxX * 2), AIStartingPosition.y*2) - new Vector2(MaxX, AIStartingPosition.y);

            Map.AIStartingPosition = new Vector2(UnityEngine.Random.Range(MinX, MaxX), UnityEngine.Random.Range(0, MaxY));

            Map.UserStartingPosition = new Vector2(UnityEngine.Random.Range(MinX, MaxX), UnityEngine.Random.Range(MinY, 0));

            if (UnityEngine.Random.Range(0, 2) > 0)
            {
                Vector2 swap = Map.UserStartingPosition;
                Map.UserStartingPosition = Map.AIStartingPosition;
                Map.AIStartingPosition = swap;
               
            }

            StartingPositions[ConfigData.Configuration.AISide - 1] = Map.AIStartingPosition;
            StartingPositions[ConfigData.Configuration.UserSide - 1] = Map.UserStartingPosition;



            if (!isStepTimeout)
            {
                if (state.IsSideKilled(ConfigData.Configuration.HumanSide) && !state.IsSideKilled(ConfigData.Configuration.BeeSide))
                {
                    //WinningSide = ConfigData.Configuration.BeeSide;
                    //Debug.Log($"Bees won! They had {remainingBeeTsv} / {state.InitialTsv[ConfigData.Configuration.BeeSide - 1]} remaining TSV or {remainingBeeTSVPercentage} x of the original.");

                    AgentGroup.SetGroupReward(remainingBeeTSVPercentage);
                    HumanAgentGroup.SetGroupReward(-remainingBeeTSVPercentage);
                    //BeeCumaltiveReward += 1f;
                    //HumanCumulativeReward = -1f;
                    //Debug.Log($"Bees won! Lost {LostBeeShips} bees, reward: {BeeCumaltiveReward}, {HumanCumulativeReward}");

                }
                else if (state.IsSideKilled(ConfigData.Configuration.BeeSide) && !state.IsSideKilled(ConfigData.Configuration.HumanSide))
                {
                    //Debug.Log($"Humans won! They had {remainingHumanTsv} / {state.InitialTsv[ConfigData.Configuration.HumanSide - 1]} remaining TSV or {remainingHumanTSVPercentage} x of the original.");

                    AgentGroup.SetGroupReward(-remainingHumanTSVPercentage);
                    HumanAgentGroup.SetGroupReward(remainingHumanTSVPercentage);
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

            state.SpottedShips = new List<SpottedShip>[] { new List<SpottedShip>(), new List<SpottedShip>() };


            for (int i = 0; i < ships.Length; i++)
            {
                Ship ship = ships[i];

                ship.EndKill();
            }
            SetupLevel();
            //Invoke(nameof(StartNew), .1f);
            //WinningSide = 0;
        }
        /// <summary>
        /// Called by both ResetLevel(), FinalizeSceneWithUserData(), and SaveAndEnd(). Prepares the LevelStage for a new level
        /// </summary>
        public void SetupLevel()
        {
            StartTime = Time.realtimeSinceStartup;


            if (ConfigData.ChooseRandomLevel)
            {
                List<LevelOptions> possibleLevels = ConfigData.GetLevelData().GetLevels().Where((level) => level.Side == ConfigData.Configuration.AISide).ToList();
                CurrentLevelOptions = (LevelOptions)possibleLevels[Utilities.RandomInt(possibleLevels.Count)].Clone();
            }
            else if (ConfigData.LevelOptions == null)
            {
                CurrentLevelOptions = new LevelOptions(ConfigData.GetLevelData().GetNewId(), ConfigData.Configuration.AISide, $"Random Level #{ConfigData.GetLevelData().GetNewId()}");
            }
            else
            {
                CurrentLevelOptions = (LevelOptions)ConfigData.LevelOptions.Clone();
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


            Debug.Log($"Playing level: {CurrentLevelOptions.Name} with squads: {Utilities.ListToString(CurrentLevelOptions.ChosenSquads)}");
            // Check settings and config variables
            SetConfigOptionsAndOverrides();

            //Debug.Log($"The human side is {ConfigData.Configuration.HumanSide}, the Bee side is {ConfigData.Configuration.BeeSide}, the AI side is {ConfigData.Configuration.AISide}, the user side is {ConfigData.Configuration.UserSide}");
            //Debug.Log($"The AI Starting position is {AIStartingPosition}, the user starting position is {UserStartingPosition}");

            //Debug.Log($"Chosen squads: {Utilities.ListToString(CurrentLevelOptions.ChosenSquads)}");
            if (HasRandomizedOptions)
            {
                RandomizeOptions();
            }
            else
            {
                Debug.Log($"The map does not have randomized options");
                CurrentLevelOptions.MapIndex = OverrideMapIndex;
                MapData = ConfigData.Maps[CurrentLevelOptions.MapIndex];
                Map = Instantiate(Maps[CurrentLevelOptions.MapIndex]).GetComponent<UI_Components.Map>();

                
                CurrentLevelOptions.ObstacleMapIndex = OverrideObstacleMapIndex;
                _chosenObstacles = _obstacleLists.GetValueOrDefault(CurrentLevelOptions.ObstacleMapIndex);
            }
            SetupMapAndCamera();

            SetupShips();

            MakeSaveLevel();

            AllSquads.AddRange(CurrentLevelOptions.EnemySquads);
            AllSquads.AddRange(CurrentLevelOptions.ChosenSquads);
            AllSquads.AddRange(CurrentLevelOptions.EnemyReinforcements);

            if (ActivateMining)
            {
                SpawnMiningAsteroids();
            }
            if (ActivateFogOfWar && HasPlayer)
            {

                Map.FogOfWar.SetActive(true);
            }
            else
            {
                Map.FogOfWar.SetActive(false);
            }

            SetupHivemind();

            float end = (Time.realtimeSinceStartup - StartTime) * 1000; // seconds to milliseconds
            Debug.Log($"It took {Math.Round(end, 2)} ms to set up the level and {Math.Round(Time.realtimeSinceStartup, 2)}s total time.");
        }
        public void SetConfigOptionsAndOverrides()
        {
            if (TimeoutTime == 0)
            {
                TimeoutTime = int.MaxValue;
            }

            if (OverrideTimeScale == 0)
            {
                TimeScale = ConfigData.Configuration.TimeScale;
            }
            else
            {
                TimeScale = OverrideTimeScale;

            }
            if (GeneratedSquadCountOverride > 0)
            {
                CurrentLevelOptions.EnemySquadGenerationCount = GeneratedSquadCountOverride;
            }
            else if (CurrentLevelOptions.EnemySquadGenerationCount == 0)
            {
                CurrentLevelOptions.EnemySquadGenerationCount = Utilities.RandomInt(8)+1;
            }

            if (OverrideBeeShipTypes.Count > 0)
            {
                BeeShipTypes = OverrideBeeShipTypes;
            }
            else
            {
                BeeShipTypes = ConfigData.BeeShipTypes.ToList();
            }

            if (OverrideHumanShipTypes.Count > 0)
            {
                HumanShipTypes = OverrideHumanShipTypes;
            }
            else
            {
                HumanShipTypes = ConfigData.HumanShipTypes.ToList();
            }
        }
        public void ResetGameData()
        {
            ConfigData.CurrentShips.ReplaceDeadSquadShips();
            GameState state = GetState();
            state.ResetState();
            state.GameOver = false;
            state.LevelEnded = false;
            Seconds = 0;
            //Socket.StandingRequests.Clear();
            ConfigData.Socket.HandledRequests.Except(HandledRequests);
            HandledRequests.Clear();
            if (!WatchServerRequests)
            {
                ConfigData.__PastServerRequests.Clear();
            }

            if (Map != null)
            {
                Destroy(Map.gameObject);
            }
            AllSquads.Clear();
            CurrentLevelOptions.ChosenSquads.Clear();
        }
        public void SetupHivemind()
        {
            CancelInvoke(nameof(GetHiveMindCommands));
            if (ActivateHiveMind)
            {
                Invoke(nameof(GetHiveMindCommands), InitialCommandDelay);
            }
            if (IsDebugging)
            {
                InvokeRepeating(nameof(UpdateDebugVariables), 1, 1);
            }
            CancelInvoke(nameof(CheckTriggers));
            if (ActivateLoadingShipsMidLevel)
            {
                SetTriggers();
                InvokeRepeating(nameof(CheckTriggers), 5, 5);
            }
        }
        public void MakeSaveLevel()
        {
            if (CurrentLevelOptions.EnemyReinforcementDelay == 0)
            {
                CurrentLevelOptions.EnemyReinforcementDelay = ConfigData.StandardReinforcementsDelay;
            }
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
            GameState state = GetState();
            if (state.GetSquadsBySide(ConfigData.Configuration.UserSide).Count > 0 && state.GetSquadsBySide(ConfigData.Configuration.AISide).Count > 0 && !IsTraining && HasPlayer)
            {
                state.SelectSquad(state.GetSquadByNumber(ConfigData.Configuration.UserSide, 1));
            }
            else if (!IsTraining && HasPlayer)
            {
                Debug.Log($"User squads: {state.GetSquadsBySide(ConfigData.Configuration.UserSide).Count}, AI squads: {state.GetSquadsBySide(ConfigData.Configuration.AISide).Count}");
                Pause();
                Menus.NoAliveShipsAlert.SetActive(true);
            }
            CalculateShipClearances();
        }
        public void SetupMapAndCamera()
        {
            Map.Setup(ConfigData.Maps[CurrentLevelOptions.MapIndex].Name, ConfigData.Maps[CurrentLevelOptions.MapIndex].UserStartingPosition, ConfigData.Maps[CurrentLevelOptions.MapIndex].AIStartingPosition);
            Map.name = Map.Name;
            Map.transform.parent = this.transform;

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


            if (HasPlayer)
            {
                Camera.orthographicSize = DefaultZoom;
                Vector2 localizedPosition = DefaultCameraPosition + (Vector2)transform.position;
                Camera.transform.position = new Vector3(localizedPosition.x, localizedPosition.y, -10);
                InputManager.MaintainScrollBoundary();
                if ((OverrideUserSide == 1 || OverrideUserSide == 2) && OverrideUserSide != ConfigData.Configuration.UserSide)
                {
                    ConfigData.SwapSides();
                    Menus.ActionBox.Setup(this, EventSystem, ConfigData.Configuration.UserSide);
                }

                MiniMapCamera.gameObject.SetActive(true);
                MiniMapCamera.orthographicSize = Map.MiniMapCameraSize;

                SquadTabs.ForEach((tab) =>
                {
                    tab.Background.GetComponent<UnityEngine.UI.Image>().color = Color.white;
                    tab.HideTab();
                });

            }
            else
            {
                CancelInvoke(nameof(TimeOut));
                Invoke(nameof(TimeOut), TimeoutTime);
            }


            if (HasObstacles)
            {
                CancelInvoke(nameof(SpawnAsteroid));
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
            ConfigData.__LevelTimeouts++;
            IsRestarting = true;
            SaveAndEnd();
        }
        /// <summary>
        /// Used for standard play and Hivemind Training. Stores commands, cleans the map, and records the stats.
        /// </summary>
        public void SaveAndEnd()
        {
            //Debug.Log($"Saving and ending");


            GameState state = GetState();
            state.StoreCommands();

            if (RecordStats && !IsTraining)
            {

                for (int i = 0; i < AllSquads.Count; i++)
                {
                    SavedSquad savedSquad = AllSquads[i];
                    if (savedSquad.HasBeenSavedToStorage)
                    {
                        savedSquad = ConfigData.CurrentShips.GetSavedSquad(savedSquad.Id);
                    }
                    else
                    {
                        //Debug.Log($"{savedSquad.Name} has not been saved to storage #{savedSquad.Id}");
                        continue;
                    }

                    //Debug.Log($"Logging stats for sqauds battles fought for {savedSquad.Name}");
                    savedSquad.Stats.BattlesFought++;

                    if (savedSquad.Side == WinningSide)
                    {
                        //ConfigData.Ships.GetSavedSquad(savedSquad.Id).Stats.BattlesWon++;
                        savedSquad.Stats.BattlesWon++;
                    }

                    savedSquad.GetSquadShips().ForEach((ship) =>
                    {
                        FleetShip fleetShip = ship.GetFleetShip();
                        fleetShip.BattlesFought++;
                        if (fleetShip.Side == WinningSide)
                        {
                            fleetShip.BattlesWon++;
                        }
                        //Debug.Log($"{fleetShip.Name} has mined {fleetShip.MineralsMined} minerals in its lifetime. It has mined {fleetShip.MineralsMinedThisLevel} minerals this level");
                        fleetShip.MineralsMined += fleetShip.MineralsMinedThisLevel;
                        fleetShip.MineralsMinedThisLevel = 0;

                    });
                }
                //for (int i = 0; i < ConfigData.SquadsChosenForLevel.Count; i++)
                //{
                //    SavedSquad savedSquad = ConfigData.SquadsChosenForLevel[i];
                //    if (savedSquad.HasBeenSavedToStorage)
                //    {
                //        savedSquad = ConfigData.AllShips.GetSavedSquad(savedSquad.Id);
                //    }
                //    else
                //    {
                //        Debug.Log($"B: {savedSquad.Name} has not been saved to storage #{savedSquad.Id}");
                //        continue;
                //    }
                //    Debug.Log($"Saving stats for {savedSquad.Name}: " +
                //    $"Battles Fought: {savedSquad.Stats.BattlesFought} " +
                //    $"Battles Won: {savedSquad.Stats.BattlesWon} " +
                //    $"Ships Lost: {savedSquad.Stats.ShipsLost} " +
                //    $"Damage Done: {savedSquad.Stats.DamageDone} " +
                //    $"Damage Received: {savedSquad.Stats.DamageReceived} " +
                //    $"Kills: {savedSquad.Stats.Kills} ");

                //    savedSquad.GetShips().ForEach((squadShip) =>
                //    {
                //        FleetShip fleetShip = squadShip.GetFleetShip();
                //        Debug.Log($"Saving stats for {fleetShip.Name}: " +
                //        $"Battles Fought: {fleetShip.BattlesFought} " +
                //        $"Battles Won: {fleetShip.BattlesWon} " +
                //        $"Damage Done: {fleetShip.DamageDone} " +
                //        $"Damage Received: {fleetShip.DamageReceived} " +
                //        $"Shots Fired: {fleetShip.ShotsFired} " +
                //        $"Kills: {fleetShip.Kills} ");
                //    });
                //}

                ConfigData.CurrentShips.SaveFleetData();
                ConfigData.CurrentShips.SaveSquadData();

                if (ConfigData.IsPlayingCampaign)
                {
                    SaveCampaignStats();
                }
                
            }
            //Debug.Log($"Resetting scene");
            Ship[] ships = state.GetShips().ToArray(); // need to convert this to an array because killing a ship removes it from the list of ships in the state

            for (int i = 0; i < ships.Length; i++)
            {
                Ship ship = ships[i];

                ship.EndKill();
            }
            GetComponents<Command>().ToList().ForEach((command) =>
            {
                Destroy(command);
            });
            Obstacle[] obstacles = state.GetObstacles().ToArray();
            for (int i = 0; i < obstacles.Length; i++)
            {
                Obstacle obstacle = obstacles[i];
                if (obstacle != null)
                {
                    if (obstacle.IsCollisionAsteroid)
                    {
                        ((CollisionAsteroid)obstacle).Kill(true);
                    }
                    else
                    {
                        obstacle.Kill();
                    }
                }
            }

            while (state.Deadbodies.Count > 0)
            {
                GameObject deadbody = state.Deadbodies[0];
                state.Deadbodies.Remove(deadbody);
                Destroy(deadbody);
            }


            //StartNew();


            //Invoke(nameof(StartNew), .1f);
            //Invoke(nameof(ReloadScene), 1f);

            if (DoesUserHaveController && !IsRestarting)
            {
                Invoke(nameof(LevelEndedDialogue), 1f);
            }
            else
            {
                IsRestarting = false;
                SetupLevel();
            }

        }
        private void SaveCampaignStats()
        {
            UserProgressData progress = ConfigData.GetUserProgressData();

            if (WinningSide == ConfigData.Configuration.HumanSide)
            {
                progress.HumanWins++;
            }
            else
            {
                progress.BeeWins++;
            }

            if (WinningSide == ConfigData.Configuration.UserSide)
            {
                progress.AdvanceToNextLevel();
            }

            for (int i = 0; i < AllSquads.Count; i++)
            {
                SavedSquad savedSquad = AllSquads[i];
                savedSquad.GetSquadShips().ForEach((ship) =>
                {
                    FleetShip fleetShip = ship.GetFleetShip();
                    //Debug.Log($"{fleetShip.Name} has mined {fleetShip.MineralsMined} minerals in its lifetime. It has mined {fleetShip.MineralsMinedThisLevel} minerals this level");
                    if (fleetShip.Side == ConfigData.Configuration.UserSide)
                    {
                        progress.MinedTSV += fleetShip.MineralsMinedThisLevel;
                    }
                    else
                    {
                        progress.HivemindMinedTSV += fleetShip.MineralsMinedThisLevel;
                    }

                });
            }

            progress.Save();

            if (Menus != null)
            {
                Menus.UpdateScore(ConfigData.GetUserProgressData().HumanWins, ConfigData.GetUserProgressData().BeeWins);
            }
        }
        private void LevelEndedDialogue()
        {
            Menus.OpenLevelEndedDialogue();

            if (ConfigData.IsPlayingCampaign)
            {
                if (WinningSide == ConfigData.Configuration.UserSide)
                {
                    Menus.TryNewSquadsButtonText.text = "Play next level";
                }
                else
                {
                    Menus.TryNewSquadsButtonText.text = "Try again";
                }
                Menus.KeepGoingButton.SetActive(false);
            }
        }
        public void Pause()
        {
            if (Audio != null)
            {
                Audio.Pause();
            }
            GetState().IsPaused = true;
        }
        public void UnPause()
        {
            GetState().IsPaused = false;
            if (Audio != null && PlayMusic)
            {
                Audio.Play();
            }
        }
        // The SplitterShot class adds it's own projectile [note] [projectile-method]
        public void AddProjectile(GameObject instance, Weapon weapon, Vector2 startingPosition, float angle)
        {
             //Debug.Log($"Adding projectile {instance.name} at startingPosition: {startingPosition}");
            instance = Instantiate(instance, new Vector2(0, 0), Quaternion.identity);
            instance.transform.parent = Map.transform;
            Projectile projectile = (Projectile) instance.GetComponent(typeof(Projectile));
            GameState state = GetState();
            Ship shooter = weapon.Ship;
            Ship target = weapon.TargetShip;
            int power = weapon.Power;
            if (weapon is DualCannon)
            {
                //Debug.Log("This is a dual cannon, splitting the power");
                power /= 2;
            }
            //Debug.Log($"Position before setup for {projectile.Id}: {instance.transform.localPosition}, {projectile.GetPosition()}");
            projectile.Setup(this, shooter.Side, state.GetId(), weapon, shooter, target, startingPosition, angle, weapon.Range, power);
            shooter.ProjectilesInFlight.Add(projectile);
            //Debug.Log($"Position after setup for #{projectile.Id}: {instance.transform.localPosition}, {projectile.GetPosition()}");
        }
        public GameState GetState()
        {
            return _state;
        }
        private void GetHiveMindCommands()
        {
            //Debug.Log("Giving command");
            if (!IsPaused && ActivateHiveMind && IsLevelSetupOnServer)
            {
                Queue<Squad> squads = GetState().GetSquadsAwaitingHiveMindCommands();  
                while (squads.Count > 0)
                {
                    Squad squad = squads.Dequeue();
                    if (squad != null && !squad.IsDead)
                    {
                        //Debug.Log("Giving command");
                        //Debug.Log($"asking for matchup strat");
                        //Debug.Log(squad.damageSentToEnemyShipsBySquad);
                        squad.MakeMatchupStrat();
                    }
                }
            }
            Invoke(nameof(GetHiveMindCommands), .25f);
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
