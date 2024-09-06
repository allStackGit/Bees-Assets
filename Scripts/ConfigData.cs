using Assets.Scripts.Data;
using Assets.Scripts.Scenes;
using Assets.Scripts.Server;
using Assets.Scripts.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;            

namespace Assets.Scripts
{
    // variables that should be accessible across scenes
    public static class ConfigData
    {
        // server and socket settings
        // test is for beta testing, non-local. Development is for development, local.
        public const bool Test = true; // [alert] should be true for beta testing
        public const bool Development = false;
        public const bool Production = !Test && !Development;
        public const string LocalServerHostname = "192.168.36.2";
        public const string GlobalServerHostname = "seagrams7.softether.net";
        public const string RLServerHostname = "127.0.0.1";
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
        public const int Version = 6; // [alert] should be increased when released
        public const string BaseFolder = "SaveData";
        public const string CacheFolder = "SpriteCache";
        public const string PortraitFolder = "Sprites/People";

        // constant filenames
        public const string UserProgressFilename = "user_progress";
        public const string FleetDataFilename = "fleet_data";
        public const string SavedSquadsDataFilename = "saved_squads_data";

        // data loaded booleans
        public static bool IsUserProgressDataLoaded;
        public static bool IsFleetDataLoaded; 
        public static bool IsSavedSquadsDataLoaded;



        public const int Tiny = 1; // this is the base size, equal to 4 World Units
        public const float Small = 1.5f; 
        public const int Medium = 2;
        public const int Large = 3;
        public const int Huge = 4;
        public const int Enormous = 8;
        public const int Unfathomable = 32;

        public static readonly Dictionary<string, Vector2Int> ShipSizes = new Dictionary<string, Vector2Int>() {
            { "Barge",          new Vector2Int(760, 360)},
            { "Beacon",         new Vector2Int(90, 80)},
            { "Carrier",        new Vector2Int(480, 560)},
            { "Cruiser",        new Vector2Int(320, 360)},
            { "Dreadnought",    new Vector2Int(320, 420)},
            { "Drone",          new Vector2Int(160, 160)},
            { "Factory",        new Vector2Int(640, 640)},
            { "Fire Ship",      new Vector2Int(760, 360)},
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
            { "Queen",          new Vector2Int(1600, 1280)},
            { "Wasp",           new Vector2Int(240, 240)},
            { "Yellow Jacket",  new Vector2Int(160, 160)},
        };
        public static readonly Dictionary<string, float> ShipSizeFactor = new Dictionary<string, float>() {
            { "Barge",          Huge},
            { "Beacon",         Tiny},
            { "Carrier",        Large},
            { "Cruiser",        Medium},
            { "Dreadnought",    Medium},
            { "Drone",          Tiny},
            { "Factory",        Huge},
            { "Fire Ship",      Huge},
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
            { "Fire Ship", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.235f, 0.753f, 0.498f, 1) } },
            { "Flagship", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { "Frigate", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { "Gunship", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
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

        public static readonly HashSet<string> CommandTypes = new HashSet<string> { "Aggressive", "Defensive", "Random", "Circle", "Right Swipe", "Left Swipe", "Closest Friendly",
        "In and Out", "Patrol", "Guard", "Scouting", "Mining", "Full Retreat" };

        public static readonly HashSet<string> SpawnedOnlyShipTypes = new HashSet<string> {"Drone", "Striker", "Beacon"};
        public static readonly HashSet<string> ArmedShipTypes = new HashSet<string> { "Cruiser", "Dreadnought", "Flagship", "Frigate", "Gunship", "Bumblebee", "Hornet", "Leafcutter", "Queen", "Wasp" };

        public static int SquadMakerSide;

        public const bool UseWebSocketSharp = true; // Whether to use the "WebSocketSharp" implementation of WebSockets or use the "NativeWebSocket" implmentation
        public static Socket Socket = Test ? new Socket(TestPort, TestServerHostname, UseWebSocketSharp) : new Socket(DevelopmentPort, DevelopmentServerHostname, UseWebSocketSharp);
        public static readonly List<int> InitialVisibleShips = Enumerable.Range(0, 3400).ToList(); // // [alert] [server] Starting ships should be pulled from server
        public static bool FirstTimePlaying = true; // [alert] should be linked to whether a user has actually played before   
        public const float CloseEnoughCoordinateVariance = 1.5f; // world units
        /// <summary>
        /// Offset in world units from the front of a ship when aiming at the front of a ship
        /// </summary>
        public const float OffsetFromFront = .35f;
        public const int FireShipExplosionSize = 64;
        public const float RefillDistanceToCarrier = 10;
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



        public static KeyCode[] SquadKeys = new KeyCode[] { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9, KeyCode.Alpha0 };


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
        };


        //Carrying variables - Changing variables that need to be carried between scenes

        public static Ships AllShips = null; 
        public static List<SavedSquad> SquadsChosenForLevel = new List<SavedSquad>();
        public static bool IsLoadingUserData = false;
        public static bool AreAllSettingsLoaded => (ShipInfo != null && ShipInfo.IsLoaded) && (Configuration != null && Configuration.IsLoaded)
            && (StartingSettings != null && StartingSettings.IsLoaded);
        public static bool IsAllUserDataLoaded => IsUserProgressDataLoaded && IsFleetDataLoaded && IsSavedSquadsDataLoaded;  



        // DEBUG VARIABLES
        public static HashSet<ServerRequest> __PastServerRequests = new HashSet<ServerRequest>();
        public static int __BeeWins, __HumanWins, __HivemindCommands, __TotalRequests, __LevelTimeouts;
        public static double __TotalLatency, __AverageLatency, __TotalLength, __AverageLength;


        // private variables
        private static int _userId = 2; // [alert] should be set to actual userId and linked to Steam or other account Id
        private static UserProgressData _userProgressData = null;
        private static FleetData _fleetData = null;
        private static SavedSquadsData _savedSquadsData = null;


        public static bool HasSocketManager()
        {
            return SocketManager != null;
        }

        public static void RetryConnection()
        {
            Socket.MakeSocket();
        }
        public static void SwapSides()
        {
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
                //Debug.Log("Setting up user data");

                //Debug.Log($"Current Level before loading user data: {ConfigData.GetLevel()}");
                Dictionary<string, int> allStartingShips = new Dictionary<string, int>();
                StartingSettings.HumanStartingShips.ToList().ForEach((s) => allStartingShips.Add(s.Key, s.Value));
                StartingSettings.BeeStartingShips.ToList().ForEach((s) => allStartingShips.Add(s.Key, s.Value));


                SetupUserProgressData(!FirstTimePlaying);
                SetupFleetData(!FirstTimePlaying, allStartingShips);
                SetupSavedSquadsData(!FirstTimePlaying);
                //Debug.Log($"Current Level after loading user data: {ConfigData.GetLevel()}");
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
                //Debug.Log("Checking Data files");
                GetUserProgressData().WaitForData();
                GetFleetData().WaitForData();
                GetSavedSquadsData().WaitForData();
            }
        }
        public static void SaveAll()
        {
            //    Debug.Log($"Saving all: {Socket.IsOpen}");
            //    Debug.Log($"Saving User Progress: {Socket.IsOpen}");
            GetUserProgressData().Save();
            //Debug.Log($"Saving Ships: {Socket.IsOpen}");
            AllShips.SaveFleetData();
            //Debug.Log($"Saving Squads: {Socket.IsOpen}");
            AllShips.SaveSquadData();
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
        public static void SetLevel(int level)
        {
            if (level > 0 && level <= ConfigData.Configuration.TotalLevels)
            {
                GetUserProgressData().SetCurrentLevel(level);
            }else if (level == -1)
            {
                Debug.Log("The level was set to -1, indicating that the user's progress data has not loaded yet");
            }
            else
            {
                Debugger.Exception(new System.Exception($"There was an attempt to set the level to an invalid number: {level}"));
            }   
        }
        public static int GetLevel()
        {
            if (GetUserProgressData() != null)
            {
                return GetUserProgressData().CurrentLevel;  
            }
            return -1;
            
        }
        public static int GetUserId()
        {
            return _userId;
        }
        public static void SetUserId(int id)
        {
            if (id > 1)
            {
                _userId = id;
            }
        }
        public static void SetupUserProgressData(bool shouldFileExist)
        {
            _userProgressData = new UserProgressData(shouldFileExist);
        }
        public static UserProgressData GetUserProgressData()
        {
            return _userProgressData;
        }
        public static void SetupFleetData(bool shouldFileExist, Dictionary<string, int> startingShips)
        {
            _fleetData = new FleetData(shouldFileExist, startingShips);
        }
        public static FleetData GetFleetData()
        {
            return _fleetData;
        }
        public static void SetupSavedSquadsData(bool shouldFileExist)
        {
            _savedSquadsData = new SavedSquadsData(shouldFileExist);
        }
        public static SavedSquadsData GetSavedSquadsData()
        {
            return _savedSquadsData;
        }

    }
}