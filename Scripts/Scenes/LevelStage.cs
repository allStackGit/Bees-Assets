using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Level;
using Assets.Scripts.Level.Commands;
using Assets.Scripts.Server;
using Assets.Scripts.UIComponents;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography;
using Unity.MLAgents;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        public bool ActivateHiveMind, ActivateBrains, IsTrainingNueralNetwork, IsTrainingHiveMind, UseSemiRandomSquads, UseFullyRandomSquads, UseFullyRandomEnemySquads, RecordStats, 
            DoesUserHaveController, HasObstacles, ActivateCollisionAsteroids;
        public int OverrideTimeScale, OverrideUserSide, TimeoutTime, SquadCount;
        public Camera MiniMapCamera;

        public GameObject BargePrefab, BeehivePrefab, BumblebeePrefab, CarpenterBeePrefab, CarrierPrefab, CruiserPrefab, DreadnoughtPrefab, DronePrefab,
            FactoryPrefab, FireShipPrefab, FlagshipPrefab, FrigatePrefab, GunshipPrefab, HoneybeePrefab, HornetPrefab, LeafcutterPrefab, QueenPrefab,
            ScoutPrefab, StrikerPrefab, WarpGatePrefab, WaspPrefab, YellowJacketPrefab,
            Map, UIManager, SelectionBox, SquadBox, MiniMapContainer;
        public List<GameObject> ObstaclePrefabs = new List<GameObject>();
        public List<GameObject> CollisionAsteroidPrefabs = new List<GameObject>();
        public GameMenus Menus;
        public LevelInputManager InputManager;
        public dynamic TestObject = null;
        public SpriteRenderer MapRenderer;
        public float MinX, MinY, MaxX, MaxY;
        public Selector Selector;
        public int DefaultZoom, MaxZoom, MinZoom, ZoomSpeed, ScrollSpeed;
        public bool UseMouseScrolling;
        public Vector2 UserStartingPosition, AIStartingPosition, MouseScrollDistanceFromEdge, DefaultCameraPosition;
        public AudioController Audio;
        public LevelConstructor LevelConstructor;

        public List<string> HasBeeTypes = new List<string>();
        public List<string> FoundBeeTypes = new List<string>();
        public bool IsLevelSetupOnServer;
        public bool IsLoaded = false;
        public bool RetriedConnection;
        public bool HasPlayer;
        public int WinningSide;
        public float MapX, MapY, MaxDistance, HalfX, HalfY;
        public int MapWidth, MapHeight, HalfMapWidth, HalfMapHeight;
        public SimpleMultiAgentGroup AgentGroup;
        public SimpleMultiAgentGroup HumanAgentGroup;
        public float Seconds;
        public HashSet<int> HandledRequests = new HashSet<int>();
        public Pathfinder Pathfinder;
        public int FixedUpdates;
        /// <summary>
        /// How frequently asteroids spawn in this level. Sets the upper bound in seconds of the randomly timed spawn
        /// </summary>
        public int AsteroidSpawnRate;
        /// <summary>
        /// Sets the upper bounds for how fast an asteroid can move
        /// </summary>
        public int AsteroidMaxSpeed;
       

        public float CurrentZoom => Camera.orthographicSize;
        public bool HasFoundAllBees => HasBeeTypes.Count == FoundBeeTypes.Count;
        public bool DidUserWin => WinningSide == ConfigData.Configuration.UserSide;
        public bool IsPaused => GetState().IsPaused;

        public List<string> __CachedPaths;

        new void Start()
        {
            //Debug.Log($"Start level scene");
            Name = "Level";
            base.Start();
        }
        private void Setup()
        {
            //Debug.Log($"Setup scene");
            if (OverrideTimeScale == 0)
            {
                TimeScale = ConfigData.Configuration.TimeScale;
            }
            else
            {
                TimeScale = OverrideTimeScale;

            }
            if (DoesUserHaveController)
            {
                HasPlayer = true;
                if (OverrideUserSide == 1 || OverrideUserSide == 2 && OverrideUserSide != ConfigData.Configuration.UserSide)
                {
                    ConfigData.SwapSides();
                }
            }
            else
            {
                HasPlayer = ConfigData.Configuration.DoesUserHaveController;
            }
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

            //ConfigData.SetupSceneManagement(SceneManagement.GetComponent<SceneManagement>());
            if (!IsTrainingNueralNetwork && !IsTrainingHiveMind)
            {
                MiniMapCamera.gameObject.SetActive(true);
                Camera.orthographicSize = DefaultZoom;

                // Setup  Game menu 
                Menus = UIManager.GetComponentInChildren<GameMenus>();
                Menus.Setup(this);



                // Setup Selection Box
                Selector = SelectionBox.GetComponentInChildren<Selector>();
                Selector.Setup(this, SelectionBox);
                // Setup input manager
                InputManager = new LevelInputManager(this, Selector);


                // Setup Squad Action Box
                if (HasPlayer)
                {
                    Menus.ActionBox.Setup(this, EventSystem, ConfigData.Configuration.UserSide);
                }
                if (Audio != null)
                {
                    Audio.Setup();
                }

            }




            // Setup map bounds
            MapRenderer = Map.GetComponentInChildren<SpriteRenderer>();
            MapWidth = (int) (Mathf.Abs(MapRenderer.localBounds.min.x) + MapRenderer.localBounds.max.x);
            MapHeight = (int) (Mathf.Abs(MapRenderer.localBounds.min.y) + MapRenderer.localBounds.max.y);
            HalfMapWidth = MapWidth / 2;
            HalfMapHeight = MapHeight / 2;



            MinX = MapRenderer.localBounds.min.x + ConfigData.MapEdgePadding.x;
            MinY = MapRenderer.localBounds.min.y + ConfigData.MapEdgePadding.y;
            MaxX = MapRenderer.localBounds.max.x - ConfigData.MapEdgePadding.x;
            MaxY = MapRenderer.localBounds.max.y - ConfigData.MapEdgePadding.y;
            MapX = MapRenderer.localBounds.max.x*2;
            MapY = MapRenderer.localBounds.max.y*2;
            MaxDistance = Mathf.Sqrt(MapX * MapX + MapY * MapY);
            HalfX = MapX / 2;
            HalfY = MapY / 2;

            if (HasPlayer)
            {
                //Debug.Log($"MapRenderer.size.x: {MapRenderer.size.x}, Camera aspect: {Camera.aspect}");
                MiniMapCamera.orthographicSize = (MapRenderer.size.x / (Camera.aspect * 2));
                MaxZoom = (int)MiniMapCamera.orthographicSize;

                Vector2 cameraWorldUnitsSize = Utilities.ScreenPixelsToWorldUnits(new Vector2(MiniMapCamera.pixelWidth, MiniMapCamera.pixelHeight), Camera);
                Transform colliderContainer = Camera.transform.GetChild(0);
                colliderContainer.localScale = cameraWorldUnitsSize;
                Vector2 localizedPosition = DefaultCameraPosition + (Vector2)transform.position;
                Camera.transform.position = new Vector3(localizedPosition.x, localizedPosition.y, -10);

                InputManager.MaintainScrollBoundary();
            }

            if (HasObstacles)
            {
                SpawnObstacles();
                Pathfinder = new Pathfinder(this);
            }

            //Invoke(nameof(TimedOut), 60 * 5f);
        }
        protected override void FinalizeSceneWithUserData()
        {
            //Debug.Log($"Finalize scene");
            if (!ConfigData.Configuration.DoesUserHaveController && !DoesUserHaveController)
            {
                Invoke(nameof(TimeOut), TimeoutTime);
            }
            
            LevelConstructor = new LevelConstructor(this);
            LevelConstructor.RequestServerSetup();
            base.FinalizeSceneWithUserData();
            IsLoaded = true;
            Setup();
            LevelConstructor.SetupShips();
            _state.OriginalSquadCounts[ConfigData.Configuration.HumanSide - 1] = _state.GetSquadsBySide(ConfigData.Configuration.HumanSide).Count;
            _state.OriginalSquadCounts[ConfigData.Configuration.BeeSide - 1] = _state.GetSquadsBySide(ConfigData.Configuration.BeeSide).Count;
            if (ActivateHiveMind)
            {
                Invoke(nameof(GetHiveMindCommands), .25f);
            }
        }

        

        private void SpawnObstacles()
        {
            GameState state = GetState();
            ObstaclePrefabs.ForEach((prefab) =>
            {
                GameObject instance = Instantiate(prefab);
                instance.transform.parent = Map.transform;
                Obstacle obstacle = instance.GetComponent<Obstacle>();
                state.AddObstacle(obstacle);
            });

            if (ActivateCollisionAsteroids)
            {
                Invoke(nameof(SpawnAsteroid), AsteroidSpawnRate + Utilities.RandomInt(AsteroidSpawnRate));
            }
        }
        private void SpawnAsteroid()
        {
            GameState state = GetState();
            GameObject instance = Instantiate(CollisionAsteroidPrefabs[Utilities.RandomInt(CollisionAsteroidPrefabs.Count)]);
            instance.transform.parent = Map.transform;
            CollisionAsteroid asteroid = instance.GetComponent<CollisionAsteroid>();
            asteroid.Setup(this, Pathfinder.ObstacleCount++);

            Invoke(nameof(SpawnAsteroid), Utilities.RandomInt(AsteroidSpawnRate));
            Pathfinder.AddObstacle(asteroid);
            Pathfinder.NeedsToBeUpdated = true;
            state.AddObstacle(asteroid);
        }
        private void UpdateDebugVariables()
        {
            if (Pathfinder != null)
            {
                __CachedPaths = Pathfinder.PathCache.Select((p) => p.ToString()).ToList();

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
                if (ConfigData.Socket.IsOpen && RetriedConnection)
                {
                    LevelConstructor.RequestServerSetup();
                    RetriedConnection = false;
                }


                GameState state = GetState();
                if (state.GameOver && !state.LevelEnded)
                {
                    LevelOver();
                }

                if ((state.IsPaused || NetworkDisconnection.IsOpen || !IsLevelSetupOnServer) && !IsTrainingNueralNetwork)
                {
                    Time.timeScale = 0;
                }
                else
                {
                    if (!IsTrainingNueralNetwork)
                    {
                        Time.timeScale = TimeScale;
                        if (!IsTrainingHiveMind)
                        {
                            InputManager.Update();

                        }
                    }

                    //InputManager.Update();

                }
            }

        }
        private void FixedUpdate()
        {
            if (IsTrainingNueralNetwork || IsTrainingHiveMind)
            {
                Seconds += Time.unscaledDeltaTime;
            }
            FixedUpdates++;

            UpdateDebugVariables();
        }

        /// <summary>
        /// Ends the level and marks the winner
        /// </summary>
        void LevelOver() // [stats-method] [note]
        {
            if (!IsTrainingNueralNetwork)
            {
                Pause();
                //Debug.Log("LEVEL OVER!");
                GameState state = GetState();
                state.LevelEnded = true;
                float fps = Time.frameCount / Time.unscaledTime;
                float fups = FixedUpdates / Time.unscaledTime;
                float latency = __AverageRequestTime;

                if (state.IsSideKilled(ConfigData.Configuration.BeeSide) && !state.IsSideKilled(ConfigData.Configuration.HumanSide))
                {
                    WinningSide = ConfigData.Configuration.HumanSide;
                    ConfigData.__HumanWins++;
                }
                else if (state.IsSideKilled(ConfigData.Configuration.HumanSide) && !state.IsSideKilled(ConfigData.Configuration.BeeSide))
                {
                    WinningSide = ConfigData.Configuration.BeeSide;
                    ConfigData.__BeeWins++;
                }
                else if (state.IsSideKilled(ConfigData.Configuration.HumanSide) && state.IsSideKilled(ConfigData.Configuration.BeeSide))
                {
                    Debug.Log("Both sides are dead!");
                }
                else
                {
                    Debug.Log("Neither side is dead!");
                }

                int totalGames = ConfigData.__HumanWins + ConfigData.__BeeWins;
                int humanWinPercentage = (int)(((float)ConfigData.__HumanWins / totalGames) * 100);
                int beeWinPercentage = (int)(((float)ConfigData.__BeeWins / totalGames) * 100);
                Debug.Log($"{$"H:{ConfigData.__HumanWins}/{totalGames} ({humanWinPercentage}%)".PadRight(15)}{$"fps: {fps}".Substring(0, 9)}  {$"fups: {fups}".Substring(0, 10)}     {$"latency: {(int)(latency*1000)}ms".PadRight(18)} {$"CPS: {ConfigData.__HivemindCommands / Time.unscaledTime}".PadRight(9).Substring(0, 9)}");

                if (Menus != null)
                {
                    Menus.UpdateScore(ConfigData.__HumanWins, ConfigData.__BeeWins);
                }

                //Debug.Log($"Setting stats for Saved Squads");
                for (int i = 0; i < ConfigData.SquadsChosenForLevel.Count; i++)
                {
                    SavedSquad savedSquad = ConfigData.SquadsChosenForLevel[i];
                    if (savedSquad.HasBeenSavedToStorage)
                    {
                        savedSquad = ConfigData.AllShips.GetSavedSquad(savedSquad.Id);
                    }
                    else
                    {
                        continue;
                    }

                    //Debug.Log($"Logging stats for sqauds battles fought for {savedSquad.Name}");
                    savedSquad.Stats.BattlesFought++;

                    if (savedSquad.Side == WinningSide)
                    {
                        //ConfigData.Ships.GetSavedSquad(savedSquad.Id).Stats.BattlesWon++;
                        savedSquad.Stats.BattlesWon++;
                    }

                    savedSquad.GetShips().ForEach((ship) =>
                    {
                        FleetShip fleetShip = ship.GetFleetShip();
                        fleetShip.BattlesFought++;
                        if (fleetShip.Side == WinningSide)
                        {
                            fleetShip.BattlesWon++;
                        }
                    });
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
                    Invoke(nameof(SaveAndEnd), 2f);

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

            AIStartingPosition = new Vector2(UnityEngine.Random.Range(MinX, MaxX), UnityEngine.Random.Range(0, MaxY));

            UserStartingPosition = new Vector2(UnityEngine.Random.Range(MinX, MaxX), UnityEngine.Random.Range(MinY, 0));

            if (UnityEngine.Random.Range(0, 2) > 0)
            {
                Vector2 swap = UserStartingPosition;
                UserStartingPosition = AIStartingPosition;
                AIStartingPosition = swap;
               
            }


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

            state.InitialTsv = new int[] { 0, 0 };
            state.SpottedShips = new List<SpottedShip>[] { new List<SpottedShip>(), new List<SpottedShip>() };


            for (int i = 0; i < ships.Length; i++)
            {
                Ship ship = ships[i];

                ship.Kill(null, true);
            }
            StartNew();
            //Invoke(nameof(StartNew), .1f);
            //WinningSide = 0;
        }
        /// <summary>
        /// Called by both ResetLevel and SaveAndEnd(). Prepares the LevelStage for a new level
        /// </summary>
        public void StartNew()
        {
            GameState state = GetState();
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

            if (HasPlayer)
            {
                Vector2 localizedPosition = DefaultCameraPosition + (Vector2)transform.position;
                Camera.transform.position = new Vector3(localizedPosition.x, localizedPosition.y, -10);
                InputManager.MaintainScrollBoundary();
                if ((OverrideUserSide == 1 || OverrideUserSide == 2) && OverrideUserSide != ConfigData.Configuration.UserSide)
                {
                    ConfigData.SwapSides();
                    Menus.ActionBox.Setup(this, EventSystem, ConfigData.Configuration.UserSide);
                }

            }
            else
            {
                CancelInvoke(nameof(TimeOut));
                Invoke(nameof(TimeOut), TimeoutTime);
            }
            //Debug.Log("Cleared timeout");


            if (HasObstacles)
            {
                SpawnObstacles();
                Pathfinder = new Pathfinder(this);
            }

            LevelConstructor.SetupShips();

            


        }
        /// <summary>
        /// Resets the level for Hivemind training
        /// </summary>
        private void TimeOut()
        {
            Debug.Log("Level timed out!");
            SaveAndEnd();
        }
        /// <summary>
        /// Used for standard play and Hivemind Training. 
        /// </summary>
        private void SaveAndEnd()
        {
            //Debug.Log($"Saving and ending");

                
            GameState state = GetState();
            state.LogState();
            state.StoreCommands();

            if (RecordStats)
            {
                for (int i = 0; i < ConfigData.SquadsChosenForLevel.Count; i++)
                {
                    SavedSquad savedSquad = ConfigData.SquadsChosenForLevel[i];
                    if (savedSquad.HasBeenSavedToStorage)
                    {
                        savedSquad = ConfigData.AllShips.GetSavedSquad(savedSquad.Id);
                    }
                    else
                    {
                        continue;
                    }
                    //Debug.Log($"Saving stats for {savedSquad.Name}: " +
                    //$"Battles Fought: {savedSquad.Stats.BattlesFought} " +
                    //$"Battles Won: {savedSquad.Stats.BattlesWon} " +
                    //$"Ships Lost: {savedSquad.Stats.ShipsLost} " +
                    //$"Damage Done: {savedSquad.Stats.DamageDone} " +
                    //$"Damage Received: {savedSquad.Stats.DamageReceived} " +
                    //$"Kills: {savedSquad.Stats.Kills} ");

                    //savedSquad.GetShips().ForEach((squadShip) =>
                    //{
                    //    FleetShip fleetShip = squadShip.GetFleetShip();
                    //    Debug.Log($"Saving stats for {fleetShip.Name}: " +
                    //    $"Battles Fought: {fleetShip.BattlesFought} " +
                    //    $"Battles Won: {fleetShip.BattlesWon} " +
                    //    $"Damage Done: {fleetShip.DamageDone} " +
                    //    $"Damage Received: {fleetShip.DamageReceived} " +
                    //    $"Shots Fired: {fleetShip.ShotsFired} " +
                    //    $"Kills: {fleetShip.Kills} ");
                    //});
                }

                ConfigData.AllShips.SaveFleetData();
                ConfigData.AllShips.SaveSquadData();
            }
            //Debug.Log($"Resetting scene");
            Ship[] ships = state.GetShips().ToArray(); // need to convert this to an array because killing a ship removes it from the list of ships in the state

            for (int i = 0; i < ships.Length; i++)
            {
                Ship ship = ships[i];

                ship.Kill(null, true);
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
                    obstacle.Kill();
                }
            }


            //StartNew();
            state.ClearLists();
            StartNew();
            //Invoke(nameof(StartNew), .1f);
            //Invoke(nameof(ReloadScene), 1f);

            //if (ConfigData.Configuration.DoesUserHaveController)
            //{
            //    Invoke(nameof(LevelEndedDialogue), 2f);
            //}
            //else
            //{
            //    Invoke(nameof(ReloadScene), 3f);
            //}

        }
        private void LevelEndedDialogue()
        {
            Menus.OpenLevelEndedDialogue();
        }
        public void ReloadScene()
        {
            //Debug.Log($"Before scene manager");
            SceneManager.LoadSceneAsync(SceneManager.GetActiveScene().name, LoadSceneMode.Single);
            //Debug.Log($"After scene manager");
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
            if (Audio != null)
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
            projectile.Setup(this, shooter.Side, state.AddEntity(), weapon, shooter, target, startingPosition, angle, weapon.Range, power);
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
