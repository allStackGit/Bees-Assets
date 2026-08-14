using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Settings;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Steamworks;

namespace Assets.Scripts
{
    public static partial class ConfigData
    {
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
            if (Configuration.UserSide == Configuration.BeeSide)
            {
                Configuration.UserSide = Configuration.HumanSide;
                Configuration.AISide = Configuration.BeeSide;
                SquadMakerSide = Configuration.HumanSide;
                Configuration.SquadMakerFirstSide = Configuration.HumanSide;
                Configuration.SquadMakerSecondSide = Configuration.BeeSide;
            }
            else if (Configuration.UserSide == Configuration.HumanSide)
            {
                Configuration.UserSide = Configuration.BeeSide;
                Configuration.AISide = Configuration.HumanSide;
                SquadMakerSide = Configuration.BeeSide;
                Configuration.SquadMakerFirstSide = Configuration.BeeSide;
                Configuration.SquadMakerSecondSide = Configuration.HumanSide;
            }
        }

        public static Color GetUIColor(string name)
        {
            if (Colors.TryGetValue(name, out Color color))
            {
                return color;
            }

            Debug.LogError($"Tried to get unknown color name: {name} from list of colors.");
            return Colors.GetValueOrDefault("error");
        }

        public static void SetupUserData() // [data-file]
        {
            if (AreAllSettingsLoaded && !IsAllUserDataLoaded && !IsLoadingUserData)
            {
                IsLoadingUserData = true;
                Dictionary<ShipTypes, int> allStartingShips = new Dictionary<ShipTypes, int>();
                StartingSettings.HumanStartingShips.ToList().ForEach(s => allStartingShips.Add(s.Key, s.Value));
                StartingSettings.BeeStartingShips.ToList().ForEach(s => allStartingShips.Add(s.Key, s.Value));

                Dictionary<ShipTypes, int> allCampaignStartingShips = new Dictionary<ShipTypes, int>();
                StartingSettings.HumanCampaignStartingShips.ToList().ForEach(s => allCampaignStartingShips.Add(s.Key, s.Value));
                StartingSettings.BeeCampaignStartingShips.ToList().ForEach(s => allCampaignStartingShips.Add(s.Key, s.Value));

                Dictionary<ShipTypes, int> allChallengeStartingShips = new Dictionary<ShipTypes, int>();
                StartingSettings.HumanChallengeStartingShips.ToList().ForEach(s => allChallengeStartingShips.Add(s.Key, s.Value));
                StartingSettings.BeeChallengeStartingShips.ToList().ForEach(s => allChallengeStartingShips.Add(s.Key, s.Value));

                SetupUserProgressData(!FirstTimePlaying);
                SetupFleetData(!FirstTimePlaying, allStartingShips);
                SetupCampaignFleetData(!FirstTimePlaying, allCampaignStartingShips);
                SetupChallengeFleetData(!FirstTimePlaying, allChallengeStartingShips);
                SetupSavedSquadsData(!FirstTimePlaying);
                SetupCampaignSavedSquadsData(!FirstTimePlaying);
                SetupChallengeSavedSquadsData(!FirstTimePlaying);
                SetupLevelData(!FirstTimePlaying);
                SetupCampaignLevelData();
                SetupChallengeLevelData();
                SetupUserSettingsData(!FirstTimePlaying);
            }
        }

        public static void LoadSettings()
        {
            if (!AreAllSettingsLoaded)
            {
                ShipInfo = new ShipStats(GetUserId());
                Debug.Log($"User id is {_userId}");
                Configuration = new Configuration(GetUserId());
                StartingSettings = new StartingSettings(GetUserId());
            }
        }

        public static ShipStatBlock GetShipInfo(ShipTypes shipType)
        {
            return ShipInfo.ShipStatsList[shipType];
        }

        public static float GetShipSizeFactor(ShipTypes shipType)
        {
            return ShipSizeFactor.GetValueOrDefault(shipType);
        }

        public static void CheckDataFiles() // [data-file]
        {
            if (!IsAllUserDataLoaded)
            {
                UserProgressData.WaitForData();
                GetFleetData().WaitForData();
                GetCampaignFleetData().WaitForData();
                GetChallengeFleetData().WaitForData();
                GetSavedSquadsData().WaitForData();
                GetCampaignSavedSquadsData().WaitForData();
                GetChallengeSavedSquadsData().WaitForData();
                GetUserSettingsData().WaitForData();
                GetLevelData().WaitForData();
                GetCampaignLevelData().WaitForData();
                GetChallengeLevelData().WaitForData();
            }
        }

        public static string GetBasePath()
        {
#if UNITY_EDITOR
            string path = Application.dataPath + $"/{BaseFolder}/";
            string path1 = Application.dataPath + $"/{BaseFolder}";
#else
            string path = Application.persistentDataPath + $"/{BaseFolder}/";
            string path1 = Application.persistentDataPath + $"/{BaseFolder}";
#endif
            if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        public static string GetCachePath()
        {
#if UNITY_EDITOR
            string path = Application.dataPath + $"/{CacheFolder}/";
            string path1 = Application.dataPath + $"/{CacheFolder}";
#else
            string path = Application.temporaryCachePath + $"/{CacheFolder}/";
            string path1 = Application.temporaryCachePath + $"/{CacheFolder}";
#endif
            if (!Directory.Exists(path1)) Directory.CreateDirectory(path1);
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// Sets up the first time playing data, this is called when the user plays the game for the first time.
        /// </summary>
        public static void SetupFirstTimePlayingHumanCampaign()
        {
            Debug.Log("Setting up first time playing human campaign data");

            SavedSquad squad = CurrentShips.BuildNewSquad($"Squad #{UserProgressData.HumanCampaignSavedSquadNumber++}", Configuration.HumanSide, ShipTypes.Scout, 1);

            squad = CurrentShips.BuildNewSquad($"Squad #{UserProgressData.HumanCampaignSavedSquadNumber++}", Configuration.HumanSide, ShipTypes.Gunship, 1);
            squad.GetSquadShips().Find(s => s.ShipType == ShipTypes.Gunship).GetFleetShip().Name = "Gunship D-4";
            squad.Stats.Commander = "Tom Pepper";

            for (int i = 0; i < 2; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Honeybee, 1);
            }
            for (int i = 0; i < 14; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Honeybee, 2);
            }
            for (int i = 0; i < 13; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Hornet, 4);
            }
            for (int i = 0; i < 3; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Hornet, 2);
            }
            for (int i = 0; i < 3; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Hornet, 3);
            }
            for (int i = 0; i < 10; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Hornet, 1);
            }
            for (int i = 0; i < 2; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Hornet, 6);
            }
            for (int i = 0; i < 8; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Hornet, 8);
            }
            for (int i = 0; i < 6; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Wasp, 2);
            }
            CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Wasp, 3);
            for (int i = 0; i < 8; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Wasp, 4);
            }
            for (int i = 0; i < 10; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Wasp, 6);
            }
            for (int i = 0; i < 7; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Wasp, 1);
            }
            for (int i = 0; i < 20; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.YellowJacket, 4);
            }
            for (int i = 0; i < 3; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Leafcutter, 2);
            }
            for (int i = 0; i < 2; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Leafcutter, 4);
            }
            for (int i = 0; i < 8; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Leafcutter, 6);
            }
            for (int i = 0; i < 4; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Leafcutter, 1);
            }
            for (int i = 0; i < 4; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.CarpenterBee, 1);
            }
            CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.CarpenterBee, 2);
            CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Bumblebee, 1);
            for (int i = 0; i < 3; i++)
            {
                CurrentShips.BuildNewSquad($"Squad #{UserProgressData.BeeCampaignSavedSquadNumber++}", Configuration.BeeSide, ShipTypes.Bumblebee, 2);
            }

            UserProgressData.HasStartedHumanCampaign = true;
            UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();
        }

        public static ulong GetUserId()
        {
            if (_userId != 0)
            {
                return _userId;
            }

            // Steam identity is preferred, but Steam is optional for startup. SteamManager owns the
            // one initialization attempt so a missing client/native library cannot crash or stall
            // ConfigData while the local identity path remains available.
            if (SteamManager.Initialized)
            {
                try
                {
                    CSteamID steamID = SteamUser.GetSteamID();
                    if (steamID.m_SteamID != 0)
                    {
                        _userId = steamID.m_SteamID;
                        FirstTimePlaying = !HasPlayedBefore();
                        return _userId;
                    }
                }
                catch (System.Exception exception)
                {
                    Debug.LogWarning($"Steam identity could not be read; using local fallback identity. {exception.GetType().Name}: {exception.Message}");
                }
            }
            else
            {
                Debug.LogWarning("Steam API is unavailable; using local fallback identity.");
            }

            int storedUserId = PlayerPrefs.GetInt("user_id");
            bool hadStoredIdentity = storedUserId != 0;
            if (!hadStoredIdentity)
            {
                storedUserId = Utilities.RandomInt();
                if (storedUserId == 0)
                {
                    storedUserId = 1;
                }
                PlayerPrefs.SetInt("user_id", storedUserId);
                PlayerPrefs.Save();
            }

            _userId = unchecked((ulong)(uint)storedUserId);
            FirstTimePlaying = !hadStoredIdentity;
            return _userId;
        }

        public static bool HasPlayedBefore()
        {
            if (!SteamManager.Initialized)
            {
                return false;
            }

            try
            {
                int totalPlaytime;
                bool hasStats = SteamUserStats.GetStat("total_playtime", out totalPlaytime);
                Debug.Log($"totalPlaytime {totalPlaytime}");
                return hasStats && totalPlaytime > 0;
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"Steam playtime could not be read; treating this as a local first-run check. {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        public static void SetupUserProgressData(bool shouldFileExist)
        {
            UserProgressData = new UserProgressData(shouldFileExist);
        }

        public static void SetupUserSettingsData(bool shouldFileExist)
        {
            _userSettingsData = new UserSettingsData(shouldFileExist);
        }

        public static UserSettingsData GetUserSettingsData()
        {
            return _userSettingsData;
        }

        public static void SetupLevelData(bool shouldFileExist) // [data-file]
        {
            _levelData = new LevelData(shouldFileExist, 0);
        }

        public static void SetupCampaignLevelData()
        {
            _campaignLevelData = new LevelData(true, 1);
        }

        public static void SetupChallengeLevelData()
        {
            _challengeLevelData = new LevelData(true, 2);
        }

        public static LevelData GetLevelData() // [data-file]
        {
            return _levelData;
        }

        public static LevelData GetCampaignLevelData()
        {
            return _campaignLevelData;
        }

        public static LevelData GetChallengeLevelData()
        {
            return _challengeLevelData;
        }

        public static void SetupChallengeFleetData(bool shouldFileExist, Dictionary<ShipTypes, int> startingShips) // [data-file]
        {
            _challengeFleetData = new FleetData(shouldFileExist, startingShips, 2);
        }

        public static void SetupCampaignFleetData(bool shouldFileExist, Dictionary<ShipTypes, int> startingShips)
        {
            _campaignFleetData = new FleetData(shouldFileExist, startingShips, 1);
        }

        public static void SetupFleetData(bool shouldFileExist, Dictionary<ShipTypes, int> startingShips)
        {
            _fleetData = new FleetData(shouldFileExist, startingShips, 0);
        }

        public static FleetData GetFleetData() // [data-file]
        {
            return _fleetData;
        }

        public static FleetData GetCampaignFleetData()
        {
            return _campaignFleetData;
        }

        public static FleetData GetChallengeFleetData()
        {
            return _challengeFleetData;
        }

        public static void SetupSavedSquadsData(bool shouldFileExist) // [data-file]
        {
            _savedSquadsData = new SavedSquadsData(shouldFileExist, 0);
        }

        public static void SetupCampaignSavedSquadsData(bool shouldFileExist)
        {
            _campaignSavedSquadsData = new SavedSquadsData(shouldFileExist, 1);
        }

        public static void SetupChallengeSavedSquadsData(bool shouldFileExist)
        {
            _challengeSavedSquadsData = new SavedSquadsData(shouldFileExist, 2);
        }

        public static SavedSquadsData GetSavedSquadsData()
        {
            return _savedSquadsData;
        }

        public static SavedSquadsData GetCampaignSavedSquadsData() // [data-file]
        {
            return _campaignSavedSquadsData;
        }

        public static SavedSquadsData GetChallengeSavedSquadsData()
        {
            return _challengeSavedSquadsData;
        }
    }
}