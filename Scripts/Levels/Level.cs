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
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Assets.Scripts.Levels
{
    /// <summary>
    /// A container class for the map and all entities in it. Can coexist or be indepdentent with any number of levels. Belongs to a stage.
    /// </summary>
    public partial class Level: MonoBehaviour
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
        //public SimpleMultiAgentGroup AgentGroup;
        //public SimpleMultiAgentGroup HumanAgentGroup;
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
        /// Some levels have triggers set at the beginning but if a level has continuous triggers then new triggers can be added and need to be checked for throughout the level
        /// </summary>
        public bool HasContinuousTriggers;
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
        public List<Trigger> NextTriggers = new List<Trigger>();
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
        /// <summary>
        /// The Id of the Game connection on the server
        /// </summary>
        public long ServerGameId;
        /// <summary>
        /// The sum of all the minerals in asteroids on the map at the start of the level
        /// </summary>
        public int MaxMinerals;


        public List<string> __BeeHivemindShips, __HumanHivemindShips, __PastCommands, __PathfindingThreads, __CustomLevels, __Timers, __TimerIds;


        public void UpdateDebugVariables()
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
            //__TimerIds = _currentTimerIDs.Select((t) => t.ToString()).ToList(); 

            //string path = $"{ConfigData.GetBasePath()}/debug/minimap_{Utilities.Hash()}.png";
            //Texture2D dest = new Texture2D( MiniMapTexture.width, MiniMapTexture.height, TextureFormat.RGB24, false);
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

            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
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
                //AgentGroup = new SimpleMultiAgentGroup();
                //HumanAgentGroup = new SimpleMultiAgentGroup();

                //if (Stage.IsTrainingNueralNetwork)
                //{
                //    Academy.Instance.OnEnvironmentReset += () =>
                //    {
                //        //Debug.Log($"Reset environment, {Academy.Instance.StepCount}");
                //    };

                //}
            }


            // Setup Game State
            State = gameObject.AddComponent<GameState>();
            State.Setup(this);
            SetupLevel();
        }
    }
}
