using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Scenes;
using Assets.Scripts.Server;
using Assets.Scripts.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.UI;            

namespace Assets.Scripts
{
    // variables that should be accessible across scenes
    public static class ConfigData
    {
        // server and socket settings
        /// <summary>
        /// Test is for beta testing, non-local. Development is for development, local.
        /// </summary>
        public const bool Test = true; // [alert] should be true for beta testing
        public const bool Development = false;
        public const bool Production = !Test && !Development;
        public const string LocalServerHostname = "192.168.36.2";
        public const string GlobalServerHostname = "seagrams7.softether.net";
        public const string TestServerHostname = GlobalServerHostname;
        public const string DevelopmentServerHostname = LocalServerHostname;
        public const string ProductionServerHostname = GlobalServerHostname;
        public const int DevelopmentPort = 7146;
        public const int TestPort = 7143;
        public const int ProductionPort = 7144;
        public const int RLPort = 7242;
        public static int StandardMaxTimeOnQueue = 20;

        public const int ObstaclesLayerMask = 1 << 19; // the layer masks need to all be 1 and then bitwise shifted to the left by the layer number
        public const int ObstacleProximityRangesLayerMask = 1 << 20;
        public const int BeeShipsLayerMask = 1 << 11;

        public const int FogOfWarLayer = 21;
        public const int VisionRangesLayer = 22;

        public static Configuration Configuration;
        public static StartingSettings StartingSettings;
        public static ShipStats ShipInfo;


        // 2 = standard testing,
        // 3 = standard beta,
        // 4 = bennett beta,
        // 5 = ml agents rl testing,
        // 6 = standard testing [highest trained]
        // 7 = new NN training version
        public const int Version = 5; // [alert] should be increased when released
        public const string BaseFolder = "SaveData";
        public const string CacheFolder = "SpriteCache";
        public const string PortraitFolder = "Sprites/People";

        // constant filenames
        public const string UserProgressFilename = "user_progress";
        public static string[] FleetDataFilenames = new string[] { "campaign_fleet_data", "fleet_data" };
        public static string[] SavedSquadsDataFilenames = new string[] { "campaign_saved_squads_data", "saved_squads_data" };
        public const string UserSettingsFilename = "user_settings_data";
        public static string[] LevelsDataFilenames = new string[] {"campaign_levels_data", "levels_data" };


        // data loaded booleans
        public static bool IsUserProgressDataLoaded, IsUserSettingsDataLoaded;
        public static bool[] IsLevelsDataLoaded = new bool[] { false, false };
        public static bool[] IsSavedSquadsDataLoaded = new bool[] { false, false };
        public static bool[] IsFleetDataLoaded = new bool[] { false, false };


        /// <summary>
        /// Size class 3 in the spreeadsheet
        /// </summary>
        public const int Tiny = 1; // this is the base size, equal to 4 World Units
        /// <summary>
        /// Size class 5 in the spreeadsheet
        /// </summary>
        public const float Small = 1.5f;
        /// <summary>
        /// Size class 6 in the spreeadsheet
        /// </summary>
        public const int Medium = 2;
        /// <summary>
        /// Size class 7 in the spreeadsheet
        /// </summary>
        public const int Large = 3;
        /// <summary>
        /// Size class 8 in the spreeadsheet
        /// </summary>
        public const int Huge = 4;
        /// <summary>
        /// Size class 9 in the spreeadsheet
        /// </summary>
        public const int Enormous = 8;
        /// <summary>
        /// Size class 10 in the spreeadsheet
        /// </summary>
        public const int Unfathomable = 32;

        public static readonly Dictionary<string, Vector2Int> ShipSizes = new Dictionary<string, Vector2Int>() {
            { "Barge",          new Vector2Int(760, 360)},
            { "Beacon",         new Vector2Int(90, 80)},
            { "Carrier",        new Vector2Int(480, 560)},
            { "Cruiser",        new Vector2Int(320, 360)},
            { "Dreadnought",    new Vector2Int(320, 420)},
            { "Drone",          new Vector2Int(160, 160)},
            { "Factory",        new Vector2Int(640, 640)},
            { "Fire Barge",      new Vector2Int(760, 360)},
            { "Flagship",       new Vector2Int(640, 760)},
            { "Frigate",        new Vector2Int(240, 240)},
            { "Gunship",        new Vector2Int(240, 240)},
            { "Scout",          new Vector2Int(200, 160)},
            { "Striker",        new Vector2Int(160, 160)},
            { "Warp Gate",      new Vector2Int(1120, 640)},

            { "Beehive",        new Vector2Int(1360, 1360)},
            { "Bumblebee",      new Vector2Int(680, 480)},
            { "Carpenter Bee",  new Vector2Int(640, 640)},
            { "Honeybee",       new Vector2Int(160, 160)},
            { "Hornet",         new Vector2Int(160, 160)},
            { "Leafcutter",     new Vector2Int(320, 320)},
            { "Queen",          new Vector2Int(6400, 5120)},
            { "Wasp",           new Vector2Int(240, 240)},
            { "Yellow Jacket",  new Vector2Int(160, 160)},
        };

        public static readonly Dictionary<string, Vector2Int> ShipRemainsSizes = new Dictionary<string, Vector2Int>() {
            { "Barge",          new Vector2Int(0, 0)},
            { "Beacon",         new Vector2Int(0, 0)},
            { "Carrier",        new Vector2Int(0, 0)},
            { "Cruiser",        new Vector2Int(0, 0)},
            { "Dreadnought",    new Vector2Int(0, 0)},
            { "Drone",          new Vector2Int(0, 0)},
            { "Factory",        new Vector2Int(0, 0)},
            { "Fire Barge",      new Vector2Int(0, 0)},
            { "Flagship",       new Vector2Int(0, 0)},
            { "Frigate",        new Vector2Int(0, 0)},
            { "Gunship",        new Vector2Int(420, 420)},
            { "Scout",          new Vector2Int(0, 0)},
            { "Striker",        new Vector2Int(0, 0)},
            { "Warp Gate",      new Vector2Int(0, 0)},

            { "Beehive",        new Vector2Int(0, 0)},
            { "Bumblebee",      new Vector2Int(0, 0)},
            { "Carpenter Bee",  new Vector2Int(0, 0)},
            { "Honeybee",       new Vector2Int(0, 0)},
            { "Hornet",         new Vector2Int(0, 0)},
            { "Leafcutter",     new Vector2Int(0, 0)},
            { "Queen",          new Vector2Int(0, 0)},
            { "Wasp",           new Vector2Int(0, 0)},
            { "Yellow Jacket",  new Vector2Int(0, 0)},
        };
        public static readonly Dictionary<string, float> ShipSizeFactor = new Dictionary<string, float>() {
            { "Barge",          Huge},
            { "Beacon",         Tiny},
            { "Carrier",        Large},
            { "Cruiser",        Medium},
            { "Dreadnought",    Medium},
            { "Drone",          Tiny},
            { "Factory",        Huge},
            { "Fire Barge",      Huge},
            { "Flagship",       Huge},
            { "Frigate",        Small},
            { "Gunship",        Small},
            { "Scout",          Tiny},
            { "Striker",        Tiny},
            { "Warp Gate",      Huge},

            { "Beehive",        Enormous},
            { "Bumblebee",      Large},
            { "Carpenter Bee",  Huge},
            { "Honeybee",       Tiny},
            { "Hornet",         Tiny},
            { "Leafcutter",     Medium},
            { "Queen",          Unfathomable},
            { "Wasp",           Small},
            { "Yellow Jacket",  Tiny},
        };
        public static readonly Dictionary<string, Color[]> ChangeableShipColors = new Dictionary<string, Color[]>() {
            { "Barge", new Color[] {new Color(0.235f, 0.753f, 0.498f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.196f, 0.6f, 0.4f, 1) } },
            { "Beacon", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { "Carrier", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { "Cruiser", new Color[] {new Color(0.184f, 0.569f, 0.380f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.196f, 0.6f, 0.4f, 1) } },
            { "Dreadnought", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1)  } },
            { "Drone", new Color[] {new Color(.729f, .729f, .729f, 1) } },
            { "Factory", new Color[] { new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.196f, 0.6f, 0.4f, 1) } },
            { "Fire Barge", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.235f, 0.753f, 0.498f, 1) } },
            { "Flagship", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { "Frigate", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },


            { "Gunship", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.1843f, 0.5686f, 0.3725f, 1),
                new Color(0.1607f, 0.4823f, 0.3215f, 1), new Color(0.1921f, 0.6039f, 0.3960f, 1), new Color(0.1607f, 0.5098f, 0.3215f, 1),
            new Color(0.1450f, 0.4588f, 0.2941f, 1)} },


            { "Scout", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { "Striker", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { "Warp Gate", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },

            // Set the bees to the unset color because none of their colors will change ... Unless the player is the bees?
            { "Beehive",        new Color[] {UnsetColor } },
            { "Bumblebee",      new Color[] {UnsetColor } },
            { "Carpenter Bee",  new Color[] {UnsetColor } },
            { "Honeybee",       new Color[] {UnsetColor } },
            { "Hornet",         new Color[] {UnsetColor } },
            { "Leafcutter",     new Color[] {UnsetColor } },
            { "Queen",          new Color[] {UnsetColor } },
            { "Wasp",           new Color[] {UnsetColor } },
            { "Yellow Jacket",  new Color[] {UnsetColor } },
        };

        /// <summary>
        /// Offset in world units from the front of a ship when aiming at the front of a ship
        /// </summary>
        public static readonly Dictionary<string, float> OffsetFromFrontOfShip = new Dictionary<string, float>()
        {
            { "Barge", .35f },
            { "Beacon", .35f },
            { "Carrier", .35f },
            { "Cruiser", .35f },
            { "Dreadnought", .35f },
            { "Drone", .35f },
            { "Factory", .35f },
            { "Fire Barge", .35f },
            { "Flagship", .35f },
            { "Frigate", .35f },
            { "Gunship", .35f },
            { "Scout", .35f },
            { "Striker", .35f },
            { "Warp Gate", 13f },

            { "Beehive", 4f },
            { "Bumblebee", .35f },
            { "Carpenter Bee", .35f },
            { "Honeybee", .35f },
            { "Hornet", .35f },
            { "Leafcutter", .35f },
            { "Queen", .35f },
            { "Wasp", .35f },
            { "Yellow Jacket", .35f },
        };

        public static readonly HashSet<string> CommandTypes = new HashSet<string> { "Aggressive", "Defensive", "Random", "Circle", "Right Swipe", "Left Swipe", "Closest Friendly",
        "In and Out", "Patrol", "Guard", "Scouting", "Mining", "Full Retreat" };

        public static HashSet<string> BeeShipTypes = new HashSet<string>();
        public static HashSet<string> HumanShipTypes = new HashSet<string>();
        public static readonly HashSet<string> BeeSwarmShips = new HashSet<string> { "Honeybee", "Hornet", "Yellow Jacket" };
        public static readonly HashSet<string> HumanSwarmShips = new HashSet<string> { "Scout", "Carrier", "Gunship" };
        public static readonly HashSet<string> BeePowerfulShips = new HashSet<string> { "Queen", "Bumblebee", "Leafcutter" };
        public static readonly HashSet<string> HumanPowerfulShips = new HashSet<string> { "Flagship", "Fire Barge", "Cruiser", "Dreadnought" };
        public static readonly HashSet<string> SpawnedOnlyShipTypes = new HashSet<string> { "Drone", "Striker", "Beacon" };
        public static readonly HashSet<string> ArmedShipTypes = new HashSet<string> { "Cruiser", "Dreadnought", "Flagship", "Frigate", "Gunship", "Bumblebee", "Hornet", "Leafcutter", "Queen", "Wasp" };
        public static readonly List<Map> Maps = new List<Map> { new Map(0, new Vector2(0, -230), new Vector2(0, 230), "Pluto"), new Map(1, new Vector2(0, -430), new Vector2(0, 430), "Uranus") };
        public static readonly List<ObstacleMap> ObstacleMaps = new List<ObstacleMap> { new ObstacleMap(0, "None"), new ObstacleMap(1, "Maze") , new ObstacleMap(2, "Three Paths") , 
            new ObstacleMap(0, "Forest"), new ObstacleMap(0, "The Wall") };
        public static int SquadMakerSide;

        public const bool UseWebSocketSharp = true; // Whether to use the "WebSocketSharp" implementation of WebSockets or use the "NativeWebSocket" implmentation
        public static Socket Socket = Test ? new Socket(TestPort, TestServerHostname, UseWebSocketSharp) : new Socket(DevelopmentPort, DevelopmentServerHostname, UseWebSocketSharp);
        public static readonly List<int> InitialVisibleShips = Enumerable.Range(0, 3400).ToList(); // // [alert] [server] Starting ships should be pulled from server
        public static bool FirstTimePlaying = true; // [alert] should be linked to whether a user has actually played before   
        public const float CloseEnoughCoordinateVariance = 1.5f; // world units

        public const int FireBargeExplosionSize = 64;
        public const float RefillDistanceToCarrier = 15;
        public const int MinimumDelayPerBeacon = 10;
        public const int BeaconUpdateFrequency = 5;
        public const int MaxBeaconsDroppedPerScout = 5;
        public const int MinimumClearance = 4;
        public const int MinimumAsteroidSpawnDistance = 100;
        public const int MinimumAsteroidSpeed = 2;
        public const int MinimumAsteroidAngularSpeedMultiplier = 5;
        public const int CollisionAsteroidHealthIncrement = 250;
        public const int CollisionAsteroidKillDelay = 1;
        public const int MaximumTsvValueForSeeingAShip = 500;
        public const int MinimumTsvValueForSeeingAShip = 50;
        public const int StandardReinforcementsDelay = 60;
        public const int StandardMaxCommandTime = 120;
        public const float TsvMultiplierForVision = .05f;
        public const float VisionShrinkingMultiplier = .8f;
        public static Vector2 HalfSize = new Vector2(.5f, .5f);
        public const int MiningRate = 750;
        public static float ShipTurningRadius; 
        public static List<Scene> Scenes = new List<Scene>();
        public static Scene SocketManager;
        public static HashSet<long> UsedHashes = new HashSet<long>();
        public static WaitForEndOfFrame WaitForEndOfFrame = new WaitForEndOfFrame();
        public static int MaxThreads;
        public static string WaitingMessage = "{\"status\": \"waiting\"}";

        /// <summary>
        /// The obstacle map index that the user has selected, -1 means no selection
        /// </summary>
        //public static int SelectedObstacleMapIndex = -1;
        //public static int SelectedLevelMapIndex = -1;
        //public static int SelectedAsteroidOption = -1;
        //public static int SelecteFogOfWarOption = -1;
        //public static int SelectedMiningOption = -1;
        //public static int SelectedShipsLoadingMidLevelOption = -1;
        //public static int SelectedEnemyShipTypes = -1;
        public static LevelOptions LevelOptions;
        public static bool ChooseRandomLevel;


        //public static KeyCode[] SquadKeys = new KeyCode[] { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9, KeyCode.Alpha0 };


        public static int ScreenWidth = Screen.width;
        public static int ScreenHeight = Screen.height;
        public const int PixelsPerUnit = 40;
        /// <summary>
        /// How much padding to put on the edges of the map and stop units from moving there.
        /// </summary>
        public static Vector2 MapEdgePadding = new Vector2(5, 5);
        public static Color UnsetColor = Color.clear;

        /// <summary>
        /// The minimum offset between ships in the squad maker in UI world units.
        /// </summary>
        public static Vector2 ShipOffset = new Vector2(20, 20);
        /// <summary>
        /// The distance from the axis before trying to snap the ship into place
        /// </summary>
        public static Vector2 SnapDistance = new Vector2(5, 5);
        /// <summary>
        /// The distance in from the center of the box when placing drag icons in the squad maker
        /// </summary>
        public static int OffsetFromCenterOfSquadMakerDropBox = 230;

        public static Vector2 BaseDragIconSize = new Vector2(.2f, .2f);

        /// <summary>
        /// Supports up to 16 ships, four rows of four columns
        /// </summary>
        public static Vector2[] CarrierDoubleColumnFormationOffsets = new Vector2[] {
            (ShipOffset * new Vector2(-3, 0)),  (ShipOffset * new Vector2(-2, 0)),  (ShipOffset * new Vector2(2, 0)),  (ShipOffset * new Vector2(3, 0)),
            (ShipOffset * new Vector2(-3, -1)), (ShipOffset * new Vector2(-2, -1)), (ShipOffset * new Vector2(2, -1)), (ShipOffset * new Vector2(3, -1)),
            (ShipOffset * new Vector2(-3, -2)), (ShipOffset * new Vector2(-2, -2)), (ShipOffset * new Vector2(2, -2)), (ShipOffset * new Vector2(3, -2)),
            (ShipOffset * new Vector2(-3, -3)), (ShipOffset * new Vector2(-2, -3)), (ShipOffset * new Vector2(2, -3)), (ShipOffset * new Vector2(3, -3)),
            (ShipOffset * new Vector2(-3, -4)), (ShipOffset * new Vector2(-2, -4)), (ShipOffset * new Vector2(2, -4)), (ShipOffset * new Vector2(3, -4)),
        };

        /// <summary>
        /// Supports up to 10 ships, five rows of two columns
        /// </summary>
        public static Vector2[] GeneratedSquadFormationOffsets = new Vector2[] {
            (ShipOffset * new Vector2(-.5f, 0)), (ShipOffset * new Vector2(.5f, 0)), 
            (ShipOffset * new Vector2(-.5f, -.75f)), (ShipOffset * new Vector2(.5f, -.75f)),
            (ShipOffset * new Vector2(-.5f, .75f)), (ShipOffset * new Vector2(.5f, .75f)),
            (ShipOffset * new Vector2(-.5f, -1.5f)), (ShipOffset * new Vector2(.5f, -1.5f)),
            (ShipOffset * new Vector2(-.5f, 1.5f)),  (ShipOffset * new Vector2(.5f, 1.5f)),
        };

        public static Vector2[] CarrierColumnFormationOffsets = new Vector2[] {
             (ShipOffset * new Vector2(-.5f, 0)),  (ShipOffset * new Vector2(.5f, 0)),
             (ShipOffset * new Vector2(-.5f, -1)),  (ShipOffset * new Vector2(.5f, -1)),
             (ShipOffset * new Vector2(-.5f, -2)),  (ShipOffset * new Vector2(.5f, -2)),
             (ShipOffset * new Vector2(-.5f, -3)),  (ShipOffset * new Vector2(.5f, -3)),
             (ShipOffset * new Vector2(-.5f, -4)),  (ShipOffset * new Vector2(.5f, -4)),
             (ShipOffset * new Vector2(-.5f, -5)),  (ShipOffset * new Vector2(.5f, -5)),
             (ShipOffset * new Vector2(-.5f, -6)),  (ShipOffset * new Vector2(.5f, -6)),
             (ShipOffset * new Vector2(-.5f, -7)),  (ShipOffset * new Vector2(.5f, -7)),
             (ShipOffset * new Vector2(-.5f, -8)),  (ShipOffset * new Vector2(.5f, -8)),
             (ShipOffset * new Vector2(-.5f, -9)),  (ShipOffset * new Vector2(.5f, -9)),
             (ShipOffset * new Vector2(-.5f, -10)),  (ShipOffset * new Vector2(.5f, -10)),
        };

        public static Vector2[] QueenYellowJacketSpawnFormation = new Vector2[] { // Supports up to 16 slots
            (ShipOffset * new Vector2(-1, 0)), (ShipOffset * new Vector2(-.5f, 0)),  (ShipOffset * new Vector2(.5f, 0)), (ShipOffset * new Vector2(1, 0)),
            (ShipOffset * new Vector2(-1, -1)), (ShipOffset * new Vector2(-.5f, -1)),  (ShipOffset * new Vector2(.5f, -1)), (ShipOffset * new Vector2(1, -1)),
            (ShipOffset * new Vector2(-1, -2)), (ShipOffset * new Vector2(-.5f, -2)),  (ShipOffset * new Vector2(.5f, -2)), (ShipOffset * new Vector2(1, -2)),
            (ShipOffset * new Vector2(-1, -3)), (ShipOffset * new Vector2(-.5f, -3)),  (ShipOffset * new Vector2(.5f, -3)), (ShipOffset * new Vector2(1, -3)),
        };

        public static Vector2 OriginalSavedSquadLabelSize = new Vector2(240, 64); 

        public static Dictionary<string, Color> Colors = new Dictionary<string, Color>() 
        {
            {"good", new Color32(35, 165, 90, 255)},
            {"warning", new Color32(240, 77, 34, 255)},
            {"medium", new Color32(248, 236, 13, 255)},
            {"bad", new Color32(242, 63, 67, 255)},
            {"human", new Color32(39, 127, 94, 255)},
            {"bee", new Color32(251, 242, 54, 255)},
            {"error", new Color32(243, 33, 33, 255)},
            {"squad-ship-counter", new Color32(60, 57, 57, 255)},
            {"supply-capacity-label", new Color32(60, 57, 57, 255)},
            {"invisible", new Color32(255, 255, 255, 0)},
            {"dropbox-background", new Color32(255, 255, 255, 39)},
            {"action-button-normal", new Color32(245, 245, 245, 255)},
            {"action-button-highlight", new Color32(108, 108, 108, 255)},
            {"detonate-button-normal", new Color32(192, 1, 1, 255)},
            {"detonate-button-highlight", new Color32(200, 99, 99, 255)},
            {"eye-aiming", new Color32(242, 63, 67, 255)},
            {"striker-loaded-indicator", new Color32(34, 175, 76, 255)},
            {"striker-not-loaded-indicator", new Color32(236, 44, 44, 255)},
            {"squadbox-default-color", new Color(0.4761926f, 0.8207547f, 0.4979669f, 0.6941177f)},
            {"saved-squad-label-default-color", new Color(0.6527f, 0.6625f, 0.7169f, 1)},
        };


        //Carrying variables - Changing variables that need to be carried between scenes

        /// <summary>
        /// The current set of ships the player is playing with, either the campaign ships or free play ships
        /// </summary>
        public static Ships CurrentShips = null;
        public static Ships FreePlayShips = null;
        public static Ships CampaignShips = null;
        //public static List<SavedSquad> SquadsChosenForLevel = new List<SavedSquad>();
        public static bool IsLoadingUserData = false;
        public static bool IsUserLoadingCustomSquads, IsUserLoadingCustomEnemySquads;
        public static bool IsPlayingCampaign = false;
        public static bool AreAllSettingsLoaded => (ShipInfo != null && ShipInfo.IsLoaded) && (Configuration != null && Configuration.IsLoaded)
            && (StartingSettings != null && StartingSettings.IsLoaded);
        public static bool IsAllUserDataLoaded => IsUserProgressDataLoaded && IsFleetDataLoaded[0] && IsFleetDataLoaded[1] && IsSavedSquadsDataLoaded[0] && IsSavedSquadsDataLoaded[1] && IsUserSettingsDataLoaded && IsLevelsDataLoaded[0] && IsLevelsDataLoaded[1];  



        // DEBUG VARIABLES
        public static HashSet<ServerRequest> __PastServerRequests = new HashSet<ServerRequest>();
        public static int __HivemindCommands, __TotalRequests, __LevelTimeouts;
        public static double __TotalLatency, __AverageLatency, __TotalLength, __AverageLength;


        // private variables
        private static int _userId = -1; // [alert] should be set to actual userId and linked to Steam or other account Id
        private static UserProgressData _userProgressData = null;
        private static FleetData _fleetData = null;
        private static SavedSquadsData _savedSquadsData = null;
        private static UserSettingsData _userSettingsData = null;
        private static LevelData _levelData = null;

        private static LevelData _campaignLevelData = null;
        private static FleetData _campaignFleetData = null;
        private static SavedSquadsData _campaignSavedSquadsData = null;


        public static bool HasSocketManager()
        {
            return SocketManager != null;
        }

        /// <summary>
        /// Tries to reconnect to the server
        /// </summary>
        public static void RetryConnection()
        {
            Socket.MakeSocket();
        }
        public static void SwapSides()
        {
            //Debug.Log($"Switching sides from {Configuration.UserSide}");
            if (Configuration.UserSide == Configuration.BeeSide) // if it was the bee side switch to the human side
            {
                //Debug.Log($"Switching sides to humans");
                Configuration.UserSide = Configuration.HumanSide;
                Configuration.AISide = Configuration.BeeSide;
                SquadMakerSide = Configuration.HumanSide;
                Configuration.SquadMakerFirstSide = Configuration.HumanSide;
                Configuration.SquadMakerSecondSide = Configuration.BeeSide;
            }
            else if (Configuration.UserSide == Configuration.HumanSide) // switch to bees
            {
                //Debug.Log($"Switching sides to bees, U: {Configuration.UserSide}, AI: {Configuration.AISide}, H: {Configuration.HumanSide}, B: {Configuration.BeeSide}");
                Configuration.UserSide = Configuration.BeeSide;
                Configuration.AISide = Configuration.HumanSide;
                SquadMakerSide = Configuration.BeeSide;
                Configuration.SquadMakerFirstSide = Configuration.BeeSide;
                Configuration.SquadMakerSecondSide = Configuration.HumanSide;
                //Debug.Log($"Switched sides to bees, U: {Configuration.UserSide}, AI: {Configuration.AISide}, H: {Configuration.HumanSide}, B: {Configuration.BeeSide}");
            }
        }
        public static Color GetUIColor(string name)
        {
            List<string> possibleNames = Colors.Keys.ToList();
            if (possibleNames.Contains(name))
            {
                return Colors.GetValueOrDefault(name);

            }
            else
            {
                Debugger.Exception(new Exception($"Tried to get unknown color name: {name} from list of colors."));
                return Colors.GetValueOrDefault("error");
            }
        }
        public static void SetupUserData()
        {
            if (AreAllSettingsLoaded && !IsAllUserDataLoaded && !IsLoadingUserData)
            {
                IsLoadingUserData = true;
                Debug.Log("Setting up user data");

                //Debug.Log($"Current Level before loading user data: {GetLevel()}");
                Dictionary<string, int> allStartingShips = new Dictionary<string, int>();
                StartingSettings.HumanStartingShips.ToList().ForEach((s) => allStartingShips.Add(s.Key, s.Value));
                StartingSettings.BeeStartingShips.ToList().ForEach((s) => allStartingShips.Add(s.Key, s.Value));

                Dictionary<string, int> allCampaignStartingShips = new Dictionary<string, int>();
                StartingSettings.HumanCampaignStartingShips.ToList().ForEach((s) => allCampaignStartingShips.Add(s.Key, s.Value));
                StartingSettings.BeeCampaignStartingShips.ToList().ForEach((s) => allCampaignStartingShips.Add(s.Key, s.Value));


                SetupUserProgressData(!FirstTimePlaying);
                SetupFleetData(!FirstTimePlaying, allStartingShips);
                SetupCampaignFleetData(!FirstTimePlaying, allCampaignStartingShips);
                SetupSavedSquadsData(!FirstTimePlaying);
                SetupUserSettingsData(!FirstTimePlaying);
                SetupLevelData(!FirstTimePlaying);
                //Debug.Log($"Current Level after loading user data: {GetLevel()}");
            }

        }
        public static void LoadSettings()
        {
            if (!AreAllSettingsLoaded)
            {
                //Debug.Log("Trying to load settings");
                ShipInfo = new ShipStats(GetUserId());
                Configuration = new Configuration(GetUserId());
                StartingSettings = new StartingSettings(GetUserId());
            }

        }
        public static ShipStatBlock GetShipInfo(string shipType)
        {
            if (ShipInfo != null)
            {
                return ShipInfo.ShipStatsList.GetValueOrDefault(shipType);
            }
            else
            {
                Debugger.Exception("Tried to get ship info before it was loaded");
            }
            return null;
        }

        public static Vector2 GetShipOffset(string shipType)
        {
            //return ShipOffset * (ShipSizes.GetValueOrDefault(shipType) / BaseShipSize);
            return ShipOffset;
        }

        public static float GetShipSizeFactor(string shipType)
        {
            return ShipSizeFactor.GetValueOrDefault(shipType);
        }
        public static void CheckDataFiles()
        {
            if (!IsAllUserDataLoaded)
            {
                //Debug.Log("Checking Data files...");
                //Debug.Log($"Waiting for User Progress Data");
                GetUserProgressData().WaitForData();
                //Debug.Log($"Waiting for Fleet Data");
                GetFleetData().WaitForData();
                GetCampaignFleetData().WaitForData();
                //Debug.Log($"Waiting for Saved Squads Data");
                GetSavedSquadsData().WaitForData();
                GetCampaignSavedSquadsData().WaitForData();
                //Debug.Log($"Waiting for User Settings Data");
                GetUserSettingsData().WaitForData();
                //Debug.Log($"Waiting for Level Data");
                GetLevelData().WaitForData();
                GetCampaignLevelData().WaitForData();
            }
        }
        public static string GetBasePath()
        {
#if UNITY_EDITOR
            string path = Application.dataPath + $"/{BaseFolder}/";
            string path1 = Application.dataPath + $"/{BaseFolder}";
            if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
#elif UNITY_ANDROID
                    string path = Application.persistentDataPath + $"/{BaseFolder}/";
                    string path1 = Application.persistentDataPath + $"/{BaseFolder}";
                    if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    return path;
#elif UNITY_IPHONE
                    string path = Application.persistentDataPath + $"/{BaseFolder}/";
                    string path1 = Application.persistentDataPath + $"/{BaseFolder}";
                    if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    return path;
#else
                    string path = Application.dataPath + $"/{BaseFolder}/";
                    string path1 = Application.dataPath + $"/{BaseFolder}";
                    if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    return path;
#endif
        }

        public static string GetCachePath()
        {
#if UNITY_EDITOR
            string path = Application.dataPath + $"/{CacheFolder}/";
            string path1 = Application.dataPath + $"/{CacheFolder}";
            if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
#elif UNITY_ANDROID
                    string path = Application.persistentDataPath + $"/{BaseFolder}/";
                    string path1 = Application.persistentDataPath + $"/{BaseFolder}";
                    if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    return path;
#elif UNITY_IPHONE
                    string path = Application.persistentDataPath + $"/{BaseFolder}/";
                    string path1 = Application.persistentDataPath + $"/{BaseFolder}";
                    if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    return path;
#else
                    string path = Application.dataPath + $"/{BaseFolder}/";
                    string path1 = Application.dataPath + $"/{BaseFolder}";
                    if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    return path;
#endif
        }
        public static int GetUserId()
        {
            if (_userId == -1 && !PlayerPrefs.HasKey("userId"))
            {
                _userId = Utilities.RandomInt();
                PlayerPrefs.SetInt("userId", _userId);
            }
            else
            {
                _userId = PlayerPrefs.GetInt("userId");
                FirstTimePlaying = false;
            }
            //Debug.Log($"User id is {_userId}");
            return _userId;
        }
        public static void SetupUserProgressData(bool shouldFileExist)
        {
            _userProgressData = new UserProgressData(shouldFileExist);
        }
        public static UserProgressData GetUserProgressData()
        {
            return _userProgressData;
        }
        public static void SetupUserSettingsData(bool shouldFileExist)
        {
            _userSettingsData = new UserSettingsData(shouldFileExist);
        }
        public static UserSettingsData GetUserSettingsData()
        {
            return _userSettingsData;
        }
        public static void SetupLevelData(bool shouldFileExist)
        {
            _campaignLevelData = new LevelData(shouldFileExist, 0);
            _levelData = new LevelData(shouldFileExist, 1);
        }
        public static LevelData GetLevelData()
        {
            return _levelData;
        }
        public static LevelData GetCampaignLevelData()
        {
            return _campaignLevelData;
        }
        public static void SetupCampaignFleetData(bool shouldFileExist, Dictionary<string, int> startingShips)
        {
            _campaignFleetData = new FleetData(shouldFileExist, startingShips, 0);
        }
        public static void SetupFleetData(bool shouldFileExist, Dictionary<string, int> startingShips)
        {
            _fleetData = new FleetData(shouldFileExist, startingShips, 1);
        }
        public static FleetData GetFleetData()
        {
            return _fleetData;
        }
        public static FleetData GetCampaignFleetData()
        {
            return _campaignFleetData;
        }
        public static void SetupSavedSquadsData(bool shouldFileExist)
        {
            _campaignSavedSquadsData = new SavedSquadsData(shouldFileExist, 0);
            _savedSquadsData = new SavedSquadsData(shouldFileExist, 1);
        }
        public static SavedSquadsData GetSavedSquadsData()
        {
            return _savedSquadsData;
        }
        public static SavedSquadsData GetCampaignSavedSquadsData()
        {
            return _campaignSavedSquadsData;
        }

    }
}