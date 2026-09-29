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
        public GameState State;
        // If the Hive Mind is active, get commands from the server.
        // Dedicated ML-Agents training is owned by the RlOneVsOne policy adapters.
        public bool HasObstacles, ActivateCollisionAsteroids, ActivateMining, ActivateFogOfWar, ActivateLoadingShipsMidLevel;
        public UI_Components.Map Map;
        public LevelConstructor LevelConstructor;
        public Pathfinder Pathfinder;
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
            if (global::RlOneVsOneTrainingBootstrap.IsActiveFor(Stage))
            {
                // Dedicated ML-Agents training is fully local once startup settings have loaded.
                // Keep the level logically available to gameplay components without creating a
                // server game or allowing a later socket close to mark the training level offline.
                IsLevelSetupOnServer = true;
                IsLevelConnectedToServer = true;
            }
            else
            {
                LevelConstructor.RequestServerSetup();
            }

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

            // Setup Game State
            State = gameObject.AddComponent<GameState>();
            State.Setup(this);
            SetupLevel();
        }
    }
}
