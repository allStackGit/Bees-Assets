using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Server;
using Assets.Scripts.Settings;
using Assets.Scripts.UI_Components;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts
{
    /// <summary>
    /// Shared application state and configuration roots. Domain-specific constants, enums,
    /// runtime helpers, and campaign routing live in the other ConfigData partial files.
    /// </summary>
    public static partial class ConfigData
    {
        public const bool Test = false;
        public const bool Development = true;
        public const bool Production = !Test && !Development;

        public const string LocalServerHostname = "clashofempire.net";
        public const string GlobalServerHostname = "seagrams7.softether.net";
        public const string TestServerHostname = GlobalServerHostname;
        public const string DevelopmentServerHostname = LocalServerHostname;
        public const string ProductionServerHostname = GlobalServerHostname;
        public const string DevelopmentWebGlWebSocketURL = "wss://clashofempire.net/bees-ws/";
        public const int DevelopmentPort = 7146;
        public const int TestPort = 7143;
        public const int ProductionPort = 7144;
        public const int RLPort = 7242;
        public const int StandardMaxTimeOnQueue = 10;

        public static Configuration Configuration;
        public static StartingSettings StartingSettings;
        public static ShipStats ShipInfo;

        public const int Version = 5;
        public const string BaseFolder = "SaveData";
#if UNITY_EDITOR
        // Generated sprite PNGs must stay outside Assets or Unity's AssetDatabase will import them
        // and create .meta files while the game is running. Library is already editor-local cache data.
        public const string CacheFolder = "../Library/SpriteCache";
#else
        public const string CacheFolder = "SpriteCache";
#endif

        public const string UserProgressFilename = "user_progress";
        public static string[] FleetDataFilenames = { "fleet_data", "campaign_fleet_data", "challenge_fleet_data" };
        public static string[] SavedSquadsDataFilenames = { "saved_squads_data", "campaign_saved_squads_data", "challenge_saved_squads_data" };
        public const string UserSettingsFilename = "user_settings_data";
        public static string[] LevelsDataFilenames = { "levels_data", "campaign_levels_data", "challenge_levels_data" };

        public static bool IsUserProgressDataLoaded;
        public static bool IsUserSettingsDataLoaded;
        public static bool[] IsLevelsDataLoaded = { false, false, false };
        public static bool[] IsSavedSquadsDataLoaded = { false, false, false };
        public static bool[] IsFleetDataLoaded = { false, false, false };

        public static int SquadMakerSide;

        public const bool UseWebSocketSharp = true;
        private static Socket _socket;
        public static Socket Socket
        {
            get
            {
                if (_socket == null)
                {
                    if (Test)
                    {
                        _socket = new Socket(TestPort, TestServerHostname, UseWebSocketSharp);
                    }
                    else if (Development)
                    {
#if UNITY_WEBGL && !UNITY_EDITOR
                        _socket = SecureSocketFactory.CreateWebGl(
                            DevelopmentPort,
                            DevelopmentServerHostname,
                            DevelopmentWebGlWebSocketURL);
#else
                        _socket = new Socket(DevelopmentPort, DevelopmentServerHostname, UseWebSocketSharp);
#endif
                    }
                    else
                    {
                        _socket = SecureSocketFactory.Create(
                            ProductionPort,
                            ProductionServerHostname,
                            UseWebSocketSharp,
                            secure: true);
                    }
                }
                return _socket;
            }
        }

        public static bool FirstTimePlaying;
        public static float ShipTurningRadius;
        public static List<Scenes.Scene> Scenes = new List<Scenes.Scene>();
        public static Scenes.Scene SocketManager;
        public static HashSet<long> UsedHashes = new HashSet<long>();
        public static long UniqueCounter;
        public static WaitForEndOfFrame WaitForEndOfFrame = new WaitForEndOfFrame();
        private static int _maxThreads = 1;
        public static int MaxThreads
        {
            get => _maxThreads;
            // Pathfinder searches are CPU-heavy Task.Run work. Leaving one worker per logical
            // processor (minus one) allows a squad attack to saturate the machine and starve
            // Unity's main/render threads. Keep the existing queue but bound concurrent A* work.
            set => _maxThreads = Mathf.Clamp(value, 1, 4);
        }
        public static string WaitingMessage = "{\"status\": \"waiting\"}";

        private static LevelOptions _levelOptions;
        public static LevelOptions LevelOptions
        {
            get => _levelOptions;
            set => _levelOptions = NormalizeCampaignLevelOptions(value);
        }
        public static bool IsTestingLevel;
        public static bool ChooseRandomLevel;
        public static bool HasSeenPreLevelIntro;
        public static bool HasSeenIntermission;

        public static int ScreenWidth = Screen.width;
        public static int ScreenHeight = Screen.height;

        public static Ships CurrentShips;
        public static Ships FreePlayShips;
        public static Ships CampaignShips;
        public static Ships ChallengeModeShips;
        public static bool IsLoadingUserData;
        public static bool IsUserLoadingCustomSquads;
        public static bool IsUserLoadingCustomEnemySquads;
        public static GameModes CurrentGameMode = GameModes.FreePlay;

        public static bool AreAllSettingsLoaded =>
            (ShipInfo != null && ShipInfo.IsLoaded) &&
            (Configuration != null && Configuration.IsLoaded) &&
            (StartingSettings != null && StartingSettings.IsLoaded);

        public static bool IsAllUserDataLoaded =>
            IsUserProgressDataLoaded &&
            IsFleetDataLoaded[0] && IsFleetDataLoaded[1] && IsFleetDataLoaded[2] &&
            IsSavedSquadsDataLoaded[0] && IsSavedSquadsDataLoaded[1] && IsSavedSquadsDataLoaded[2] &&
            IsUserSettingsDataLoaded &&
            IsLevelsDataLoaded[0] && IsLevelsDataLoaded[1] && IsLevelsDataLoaded[2];

        public static System.Diagnostics.Stopwatch Stopwatch;
        public static UIAudioController UIAudioController;

        public static ServerRequestSet __PastServerRequests = new ServerRequestSet();
        public static int __TotalResends;
        public static int __TotalRequests;
        public static double __AverageTimeOnQueue;
        public static double __TotalLength;
        public static double __AverageLength;
        public static double __TotalC2C;
        public static double __AverageC2C;
        public static double __TotalWireTime;
        public static double __AverageWireTime;
        public static double __TotalProcessingTime;
        public static double __AverageProcessingTime;
        public static long __TotalTimeOnQueue;

        private static ulong _userId;
        public static UserProgressData UserProgressData;
        private static UserSettingsData _userSettingsData;
        private static LevelData _levelData;
        private static LevelData _campaignLevelData;
        private static LevelData _challengeLevelData;
        private static FleetData _fleetData;
        private static FleetData _campaignFleetData;
        private static FleetData _challengeFleetData;
        private static SavedSquadsData _savedSquadsData;
        private static SavedSquadsData _campaignSavedSquadsData;
        private static SavedSquadsData _challengeSavedSquadsData;
    }
}
