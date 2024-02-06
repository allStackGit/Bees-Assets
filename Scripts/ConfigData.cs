using Assets.Scripts.Data;
using Assets.Scripts.Scenes;
using Assets.Scripts.Server;
using Assets.Scripts.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public static int StandardMaxTimeOnQueue = 2000;

        public static Configuration Configuration = null;
        public static StartingSettings StartingSettings = null;
        public static ShipStats ShipInfo = null;


        // 2 = standard testing, 3 = standard beta, 4 = bennett beta, 5 = ml agents rl testing, 6 = fast paced version, 7 = new NN training version
        public const int Version = 6; // [alert] should be increased when released
        public const string BaseFolder = "SaveData";
        public const string PortraitFolder = "Sprites/People";

        // constant filenames
        public const string UserProgressFilename = "user_progress";
        public const string FleetDataFilename = "fleet_data";
        public const string SavedSquadsDataFilename = "saved_squads_data";

        // data loaded booleans
        public static bool IsUserProgressDataLoaded = false;
        public static bool IsFleetDataLoaded = false; 
        public static bool IsSavedSquadsDataLoaded = false;



        public static float Tiny = 1; // this is the base size
        public static float Small = Tiny * 1.5f; 
        public static float Medium = Tiny * 2f;
        public static float Large = Tiny * 3f;
        public static float Huge = Tiny * 4f;
        public static float PlusUltra = Tiny * 8f; 

        public static Vector2 BaseShipSize = new Vector2(160, 160); // This is the base size for ship sizes because it's the smallest ship size, the same size as the drone, striker, honeybee, and hornet

        public static Vector2 TwoThirdsToSquare = new Vector2(1.5f, 1);
        public static readonly Dictionary<string, Vector2> ShipSizes = new Dictionary<string, Vector2>() {
            { "Barge",          new Vector2(760, 360)},
            { "Carrier",        new Vector2(460, 560)},
            { "Cruiser",        new Vector2(320, 360)},
            { "Dreadnought",    new Vector2(320, 420)},
            { "Drone",          new Vector2(160, 160)},
            { "Factory",        Vector2.zero },
            { "Fire Ship",      new Vector2(760, 360)},
            { "Flagship",       new Vector2(640, 760)},
            { "Frigate",        new Vector2(240, 240)},
            { "Gunship",        new Vector2(240, 240)}, 
            { "Scout",          new Vector2(200, 160)},
            { "Striker",        new Vector2(160, 160)},
            { "Warp Gate",      Vector2.zero },

            { "Beehive",        Vector2.zero },
            { "Bumblebee",      new Vector2(680, 480)},
            { "Carpenter Bee",  Vector2.zero },
            { "Honeybee",       new Vector2(160, 160)},
            { "Hornet",         new Vector2(160, 160)},
            { "Leafcutter",     new Vector2(320, 320)},
            { "Queen",          new Vector2(1600, 1280)},
            { "Wasp",           new Vector2(240, 240)},
            { "Yellow Jacket",  new Vector2(160, 160)},
        };
        public static readonly Dictionary<string, float> ShipSizeFactor = new Dictionary<string, float>() {
            { "Barge",          Huge},
            { "Carrier",        Large},
            { "Cruiser",        Medium},
            { "Dreadnought",    Medium},
            { "Drone",          Tiny},
            { "Factory",        0 },
            { "Fire Ship",      Huge},
            { "Flagship",       Huge},
            { "Frigate",        Small},
            { "Gunship",        Small},
            { "Scout",          Tiny},
            { "Warp Gate",      0 },

            { "Beehive",        0 },
            { "Bumblebee",      Large},
            { "Carpenter Bee",  0 },
            { "Honeybee",       Tiny},
            { "Hornet",         Tiny},
            { "Leafcutter",     Medium},
            { "Queen",          PlusUltra},
            { "Wasp",           Small},
            { "Yellow Jacket",  Tiny},
        };
        public static readonly Dictionary<string, Color[]> ChangeableShipColors = new Dictionary<string, Color[]>() {
            { "Barge", new Color[] {new Color(0.235f, 0.753f, 0.498f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.196f, 0.6f, 0.4f, 1) } },
            { "Carrier", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { "Cruiser", new Color[] {new Color(0.184f, 0.569f, 0.380f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.196f, 0.6f, 0.4f, 1) } },
            { "Dreadnought", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1)  } },
            { "Drone", new Color[] {new Color(.729f, .729f, .729f, 1) } },
            { "Factory", new Color[] {UnsetColor } },
            { "Fire Ship", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1), new Color(0.235f, 0.753f, 0.498f, 1) } },
            { "Flagship", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { "Frigate", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { "Gunship", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { "Scout", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { "Striker", new Color[] { new Color(0.196f, 0.6f, 0.4f, 1), new Color(0.161f, 0.510f, 0.337f, 1) } },
            { "Warp Gate", new Color[] {UnsetColor } },

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

        public static int SquadMakerSide;


        //public static Socket Socket = Test ? new Socket(TestPort, TestServerHostname) : new Socket(DevelopmentPort, DevelopmentServerHostname);
        public static readonly List<int> InitialVisibleShips = Enumerable.Range(0, 2900).ToList(); // // [alert] [server] Starting ships should be pulled from server
        public static bool FirstTimePlaying = true; // [alert] should be linked to whether a user has actually played before   
        public static float CloseEnoughCoordinateVariance = 1.5f; // world units
        public static float OffsetFromFront = .25f;

        public static KeyCode[] SquadKeys = new KeyCode[] { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9, KeyCode.Alpha0 };


        public static int ScreenWidth = Screen.width;
        public static int ScreenHeight = Screen.height;
        public static int PixelsPerUnit = 40;
        public static Vector2 MapEdgePadding = new Vector2(5, 5); // How much padding to put on the edges of the map and stop units from moving there. World Units
        public static Color UnsetColor = Color.clear;
        

        /* At the time of this writing (2023-03-24) ship PPU and world unit sizes have not been standardized. When they are, these values will probably change
         *  The base ship unit (48, 64) is 2.5 world units wide and 3.333 world units tall. That translates to a sprite that should be 48 Pixels Per (world) Unit.
         */
        public static Vector2 DragIconSize = new Vector2(48, 64); // the size of the drag icons (in locally scaled units) for all ships regardless of the size of the ship
        public static float WorldUnitScaleFactor = 2.5f;
        public static Vector2 ShipOffset = new Vector2(15, 15); // the minimum offset between ships in the squad maker in UI world units.
        public static Vector2 SnapDistance = new Vector2(5, 5); // the distance from the axis before trying to snap the ship into place


        public static Vector2[] CarrierDoubleColumnFormationOffsets = new Vector2[] {
            (ShipOffset * new Vector2(-3, 0)),  (ShipOffset * new Vector2(-2, 0)),  (ShipOffset * new Vector2(2, 0)),  (ShipOffset * new Vector2(3, 0)),
            (ShipOffset * new Vector2(-3, -1)), (ShipOffset * new Vector2(-2, -1)), (ShipOffset * new Vector2(2, -1)), (ShipOffset * new Vector2(3, -1)),
            (ShipOffset * new Vector2(-3, -2)), (ShipOffset * new Vector2(-2, -2)), (ShipOffset * new Vector2(2, -2)), (ShipOffset * new Vector2(3, -2)),
            (ShipOffset * new Vector2(-3, -3)), (ShipOffset * new Vector2(-2, -3)), (ShipOffset * new Vector2(2, -3)), (ShipOffset * new Vector2(3, -3)),
            (ShipOffset * new Vector2(-3, -4)), (ShipOffset * new Vector2(-2, -4)), (ShipOffset * new Vector2(2, -4)), (ShipOffset * new Vector2(3, -4)),
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

        public static Ships Ships = null; 
        public static List<SavedSquad> SquadsChosenForLevel = new List<SavedSquad>();
        public static bool IsLoadingUserData = false;
        public static bool AreAllSettingsLoaded => (ShipInfo != null && ShipInfo.IsLoaded) && (Configuration != null && Configuration.IsLoaded)
            && (StartingSettings != null && StartingSettings.IsLoaded);
        public static bool IsAllUserDataLoaded => IsUserProgressDataLoaded && IsFleetDataLoaded && IsSavedSquadsDataLoaded;  


        // DEBUG VARIABLEs
        public static HashSet<ServerRequest> __PastServerRequests = new HashSet<ServerRequest>();

        // private variables
        private static int _userId = 2; // [alert] should be set to actual userId and linked to Steam or other account Id
        private static UserProgressData _userProgressData = null;
        private static FleetData _fleetData = null;
        private static SavedSquadsData _savedSquadsData = null;


        public static void SwapSides()
        {
            if (Configuration.UserSide == Configuration.BeeSide)
            {
                Configuration.UserSide = Configuration.HumanSide;
                Configuration.AISide = Configuration.BeeSide;
            }else if (Configuration.UserSide == Configuration.HumanSide)
            {
                Configuration.UserSide = Configuration.BeeSide;
                Configuration.AISide = Configuration.HumanSide;
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
        public static void SetupUserData(Scene scene)
        {
            if (AreAllSettingsLoaded && !IsAllUserDataLoaded && !IsLoadingUserData)
            {
                IsLoadingUserData = true;
                //Debugger.Log("Setting up user data");

                //Debugger.Log($"Current Level before loading user data: {ConfigData.GetLevel()}");
                Dictionary<string, int> allStartingShips = new Dictionary<string, int>();
                StartingSettings.HumanStartingShips.ToList().ForEach((s) => allStartingShips.Add(s.Key, s.Value));
                StartingSettings.BeeStartingShips.ToList().ForEach((s) => allStartingShips.Add(s.Key, s.Value));


                SetupUserProgressData(!FirstTimePlaying, scene);
                SetupFleetData(!FirstTimePlaying, allStartingShips, scene);
                SetupSavedSquadsData(!FirstTimePlaying, scene);
                //Debugger.Log($"Current Level after loading user data: {ConfigData.GetLevel()}");
            }

        }
        public static void LoadSettings(Scene scene)
        {
            if (!AreAllSettingsLoaded)
            {
                Debugger.Log("Trying to load settings");
                ShipInfo = new ShipStats(GetUserId(), scene);
                Configuration = new Configuration(GetUserId(), scene);
                StartingSettings = new StartingSettings(GetUserId(), scene);
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
                //Debugger.Log("Checking Data files");
                GetUserProgressData().WaitForData();
                GetFleetData().WaitForData();
                GetSavedSquadsData().WaitForData();
            }
        }
        public static void SaveAll()
        {
            //    Debugger.Log($"Saving all: {Socket.IsOpen}");
            //    Debugger.Log($"Saving User Progress: {Socket.IsOpen}");
            GetUserProgressData().Save();
            //Debugger.Log($"Saving Ships: {Socket.IsOpen}");
            Ships.SaveFleetData();
            //Debugger.Log($"Saving Squads: {Socket.IsOpen}");
            Ships.SaveSquadData();
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
        public static void SetLevel(int level)
        {
            if (level > 0 && level <= ConfigData.Configuration.TotalLevels)
            {
                GetUserProgressData().SetCurrentLevel(level);
            }else if (level == -1)
            {
                Debugger.Log("The level was set to -1, indicating that the user's progress data has not loaded yet");
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
        public static void SetupUserProgressData(bool shouldFileExist, Scene scene)
        {
            _userProgressData = new UserProgressData(shouldFileExist, scene);
        }
        public static UserProgressData GetUserProgressData()
        {
            return _userProgressData;
        }
        public static void SetupFleetData(bool shouldFileExist, Dictionary<string, int> startingShips, Scene scene)
        {
            _fleetData = new FleetData(shouldFileExist, startingShips, scene);
        }
        public static FleetData GetFleetData()
        {
            return _fleetData;
        }
        public static void SetupSavedSquadsData(bool shouldFileExist, Scene scene)
        {
            _savedSquadsData = new SavedSquadsData(shouldFileExist, scene);
        }
        public static SavedSquadsData GetSavedSquadsData()
        {
            return _savedSquadsData;
        }

    }
}