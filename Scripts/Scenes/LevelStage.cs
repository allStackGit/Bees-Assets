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
        public bool ActivateHiveMind, ActivateBrains, IsTrainingNueralNetwork, IsTrainingHiveMind, UseSemiRandomSquads, UseFullyRandomSquads, ReplaceDeadShips, DoesUserHaveController;
        public int OverrideTimeScale, TimeoutTime, SquadCount;
        public Camera MiniMapCamera;

        public GameObject BargePrefab, BeehivePrefab, BumblebeePrefab, CarpenterBeePrefab, CarrierPrefab, CruiserPrefab, DreadnoughtPrefab, DronePrefab,
            FactoryPrefab, FireShipPrefab, FlagshipPrefab, FrigatePrefab, GunshipPrefab, HoneybeePrefab, HornetPrefab, LeafcutterPrefab, QueenPrefab,
            ScoutPrefab, StrikerPrefab, WarpGatePrefab, WaspPrefab, YellowJacketPrefab,
            Map, UIManager, SelectionBox, SquadBox, MiniMapContainer;
        public GameMenus Menus;
        public LevelInputManager InputManager;
        public dynamic TestObject = null;
        public SpriteRenderer MapRenderer;
        public float MinX, MinY, MaxX, MaxY;
        public Selector Selector;
        public int DefaultZoom, MaxZoom, MinZoom, ZoomSpeed, ScrollSpeed;
        public bool IsTestFiring, UseMouseScrolling;
        public Vector2 UserStartingPosition, AIStartingPosition, MouseScrollDistanceFromEdge, DefaultCameraPosition;
        public AudioController Audio;
        public LevelConstructor LevelConstructor;

        public List<string> HasBeeTypes = new List<string>();
        public List<string> FoundBeeTypes = new List<string>();
        public bool IsLevelSetupOnServer;
        public bool IsLoaded = false;
        public bool RetriedConnection;
        public bool HasPlayer;
        public int WinningSide = 0;
        public float MapX, MapY, MaxDistance, HalfX, HalfY;
        public SimpleMultiAgentGroup AgentGroup;
        public SimpleMultiAgentGroup HumanAgentGroup;
        public float Seconds = 0;
        public int BeeWins, HumanWins;


        public float CurrentZoom => Camera.orthographicSize;
        public bool HasFoundAllBees => HasBeeTypes.Count == FoundBeeTypes.Count;
        public bool DidUserWin => WinningSide == ConfigData.Configuration.UserSide;
        public bool IsPaused => GetState().IsPaused;

        new void Start()
        {
            //Debugger.Log($"Start level scene");
            Name = "Level";
            base.Start();
        }
        private void Setup()
        {
            //Debugger.Log($"Setup scene");
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
                        //Debugger.Log($"Reset environment, {Academy.Instance.StepCount}");
                    };

                }
            }
            else
            {
                Academy.Instance.Dispose();
            }



            // Setup Game State
            _state = gameObject.AddComponent<GameState>();
            _state.Setup(this);

            //ConfigData.SetupSceneManagement(SceneManagement.GetComponent<SceneManagement>());
            if (!IsTrainingNueralNetwork)
            {
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
                    Menus.ActionBox.Setup(this, EventSystem);
                }
                if (Audio != null)
                {
                    Audio.Setup();
                }

            }




            // Setup map bounds
            MapRenderer = Map.GetComponentInChildren<SpriteRenderer>();
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
                //Debugger.Log($"MapRenderer.size.x: {MapRenderer.size.x}, Camera aspect: {Camera.aspect}");
                MiniMapCamera.orthographicSize = (MapRenderer.size.x / (Camera.aspect * 2));
                MaxZoom = (int)MiniMapCamera.orthographicSize;

                Vector2 cameraWorldUnitsSize = Utilities.ScreenPixelsToWorldUnits(new Vector2(MiniMapCamera.pixelWidth, MiniMapCamera.pixelHeight), Camera);
                Transform colliderContainer = Camera.transform.GetChild(0);
                colliderContainer.localScale = cameraWorldUnitsSize;
                Vector2 localizedPosition = DefaultCameraPosition + (Vector2)transform.position;
                Camera.transform.position = new Vector3(localizedPosition.x, localizedPosition.y, -10);

                InputManager.MaintainScrollBoundary();
            }



            //Invoke(nameof(TimedOut), 60 * 5f);
        }
        protected override void FinalizeSceneWithUserData()
        {
            //Debugger.Log($"Finalize scene");
            Physics.autoSimulation = false; // What does this do and is it necessary?
            if (!ConfigData.Configuration.DoesUserHaveController && !DoesUserHaveController)
            {
                Invoke(nameof(TimeOut), TimeoutTime);
            }
            
            LevelConstructor = new LevelConstructor(this);
            LevelConstructor.RequestServerSetup();
            base.FinalizeSceneWithUserData();
            IsLoaded = true;
            Setup();
            LevelConstructor.SetShips();
            _state.OriginalSquadCounts[ConfigData.Configuration.HumanSide - 1] = _state.GetSquadsBySide(ConfigData.Configuration.HumanSide).Count;
            _state.OriginalSquadCounts[ConfigData.Configuration.BeeSide - 1] = _state.GetSquadsBySide(ConfigData.Configuration.BeeSide).Count;
            if (ActivateHiveMind)
            {
                Invoke(nameof(GetHiveMindCommands), .25f);
            }
        }
        protected override void RetryConnection()
        {
            base.RetryConnection();
            RetriedConnection = true;
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
                if (Socket.IsOpen && RetriedConnection)
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
                        state.Ticks++;
                        InputManager.Update();

                    }

                    //InputManager.Update();

                }
            }

        }
        private void FixedUpdate()
        {
            if (IsTrainingNueralNetwork)
            {
                Seconds += Time.deltaTime;
            }
        }

        void LevelOver() // [stats-method] [note]
        {
            if (!IsTrainingNueralNetwork)
            {
                Pause();
                //Debugger.Log("LEVEL OVER!");
                GameState state = GetState();
                state.LevelEnded = true;

                if (state.IsSideKilled(ConfigData.Configuration.BeeSide) && !state.IsSideKilled(ConfigData.Configuration.HumanSide))
                {
                    WinningSide = ConfigData.Configuration.HumanSide;
                    HumanWins++;
                    Debugger.Log($"Humans won! H:{HumanWins} B:{BeeWins}");


                }
                else if (state.IsSideKilled(ConfigData.Configuration.HumanSide) && !state.IsSideKilled(ConfigData.Configuration.BeeSide))
                {
                    WinningSide = ConfigData.Configuration.BeeSide;
                    BeeWins++;
                    Debugger.Log($"Bees won! H:{HumanWins} B:{BeeWins}");
                }
                else if (state.IsSideKilled(ConfigData.Configuration.HumanSide) && state.IsSideKilled(ConfigData.Configuration.BeeSide))
                {
                    Debugger.Log("Both sides are dead!");
                }
                else
                {
                    Debugger.Log("Neither side is dead!");
                }

                if (Menus != null)
                {
                    Menus.UpdateScore(HumanWins, BeeWins);
                }

                //Debugger.Log($"Setting stats for Saved Squads");
                ConfigData.SquadsChosenForLevel.ForEach((savedSquad) =>
                {
                    //Debugger.Log($"Logging stats for sqauds battles fought for {savedSquad.Name}");
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

                });



                UnPause();
            }
            

            if (IsTrainingNueralNetwork)
            {
                
                ResetLevel(false);
            }
            else
            {
                Invoke(nameof(SaveAndEnd), 1f);
            }



        }
        public void ResetLevel(bool isStepTimeout)
        {

            Academy.Instance.StatsRecorder.Add("Episode Time", Seconds);

            //Debugger.Log($"Reset level ({Seconds}), Unclamped Bee reward: {BeeCumaltiveReward}, Unclamped Human reward: {HumanCumulativeReward}");
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
                    //Debugger.Log($"Bees won! They had {remainingBeeTsv} / {state.InitialTsv[ConfigData.Configuration.BeeSide - 1]} remaining TSV or {remainingBeeTSVPercentage} x of the original.");

                    AgentGroup.SetGroupReward(remainingBeeTSVPercentage);
                    HumanAgentGroup.SetGroupReward(-remainingBeeTSVPercentage);
                    //BeeCumaltiveReward += 1f;
                    //HumanCumulativeReward = -1f;
                    //Debugger.Log($"Bees won! Lost {LostBeeShips} bees, reward: {BeeCumaltiveReward}, {HumanCumulativeReward}");

                }
                else if (state.IsSideKilled(ConfigData.Configuration.BeeSide) && !state.IsSideKilled(ConfigData.Configuration.HumanSide))
                {
                    //Debugger.Log($"Humans won! They had {remainingHumanTsv} / {state.InitialTsv[ConfigData.Configuration.HumanSide - 1]} remaining TSV or {remainingHumanTSVPercentage} x of the original.");

                    AgentGroup.SetGroupReward(-remainingHumanTSVPercentage);
                    HumanAgentGroup.SetGroupReward(remainingHumanTSVPercentage);
                    //BeeCumaltiveReward = -1f;
                    //HumanCumulativeReward += 1f;
                    //Debugger.Log($"Humans won! Lost {LostBeeShips} bees, reward: {BeeCumaltiveReward}, {HumanCumulativeReward}");

                }
                else
                {
                    Debugger.Log($"Both sides died! no on won!");
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
            //StartNew();
            Invoke(nameof(StartNew), .1f);
            //WinningSide = 0;
        }
        public void StartNew()
        {
            Seconds = 0;
            //Socket.StandingRequests.Clear();
            Socket.HandledRequests.Clear();
            if (!WatchServerRequests)
            {
                ConfigData.__PastServerRequests.Clear();
            }
            CancelInvoke(nameof(TimeOut));
            //Debugger.Log("Cleared timeout");
            if (!HasPlayer)
            {
                Invoke(nameof(TimeOut), TimeoutTime);
            }
            LevelConstructor.SetShips();

            if (HasPlayer)
            {
                Vector2 localizedPosition = DefaultCameraPosition + (Vector2)transform.position;
                Camera.transform.position = new Vector3(localizedPosition.x, localizedPosition.y, -10);
                InputManager.MaintainScrollBoundary();

            }


        }
        private void TimeOut()
        {
            Debugger.Log("Level timed out!");
            SaveAndEnd();
        }
        private void SaveAndEnd()
        {
            //Debugger.Log($"Savind and ending");
            ConfigData.SquadsChosenForLevel.ForEach((savedSquad) =>
            {
                //Debugger.Log($"Saving stats for {savedSquad.Name}: \n" +
                //$"Battles Fought: {savedSquad.Stats.BattlesFought} \n" +
                //$"Battles Won: {savedSquad.Stats.BattlesWon} \n" +
                //$"Ships Lost: {savedSquad.Stats.ShipsLost} \n" +
                //$"Damage Done: {savedSquad.Stats.DamageDone} \n" +
                //$"Damage Received: {savedSquad.Stats.DamageReceived} \n" +
                //$"Kills: {savedSquad.Stats.Kills} \n");
                if (savedSquad.HasBeenSaved)
                {
                    ConfigData.AllShips.GetSavedSquad(savedSquad.Id).Stats = savedSquad.Stats;
                }
            });
                
            GameState state = GetState();
            state.LogState();
            state.StoreCommands();

            if (ReplaceDeadShips)
            {
                ConfigData.AllShips.SaveFleetData(); // [alert] turn on or off to have ships record stats and die
                ConfigData.AllShips.SaveSquadData();
            }
            //Debugger.Log($"Resetting scene");
            Ship[] ships = state.GetShips().ToArray();

            state.GameOver = false;
            state.LevelEnded = false;
            for(int i = 0; i < ships.Length; i++)
            {
                Ship ship = ships[i];

                ship.Kill(null, true);
            }
            Command[] commands = GetComponents<Command>();
            for (int i = 0; i <  commands.Length; i++)
            {
                Command command = commands[i];
                Destroy(command);
            }
            //StartNew();
            state.ClearLists();
            Invoke(nameof(StartNew), .1f);
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
            //Debugger.Log($"Before scene manager");
            SceneManager.LoadSceneAsync(SceneManager.GetActiveScene().name, LoadSceneMode.Single);
            //Debugger.Log($"After scene manager");
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
             //Debugger.Log($"Adding projectile {instance.name} at startingPosition: {startingPosition}");
            instance = Instantiate(instance, new Vector2(0, 0), Quaternion.identity);
            instance.transform.parent = Map.transform;
            Projectile projectile = (Projectile) instance.GetComponent(typeof(Projectile));
            GameState state = GetState();
            Ship shooter = weapon.Ship;
            Ship target = weapon.TargetShip;
            int power = weapon.Power;
            if (weapon is DualCannon)
            {
                //Debugger.Log("This is a dual cannon, splitting the power");
                power /= 2;
            }
            //Debugger.Log($"Position before setup for {projectile.Id}: {instance.transform.localPosition}, {projectile.GetPosition()}");
            projectile.Setup(this, shooter.Side, state.AddEntity(), weapon, shooter, target, startingPosition, angle, shooter.Range, power);
            //Debugger.Log($"Position after setup for #{projectile.Id}: {instance.transform.localPosition}, {projectile.GetPosition()}");
            state.AddProjectile(projectile);
        }
        public GameState GetState()
        {
            return _state;
        }
        private void GetHiveMindCommands()
        {
            //Debugger.Log("Giving command");
            GameState state = GetState();
            if (!IsPaused && ActivateHiveMind && IsLevelSetupOnServer)
            {
                List<Squad> squads = state.GetSquadsAwaitingHiveMindCommands();  
                if (squads.Count > 0)
                {
                    Squad squad = squads.FirstOrDefault();
                    state.RemoveFromSquadsAwaitingHivemindCommands(squad);
                    if (squad != null)
                    {
                        //Debugger.Log("Giving command");
                        //Debugger.Log($"asking for matchup strat");
                        //Debugger.Log(squad.damageSentToEnemyShipsBySquad);
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
