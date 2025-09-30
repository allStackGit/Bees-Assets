using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using UnityEngine;

namespace Assets.Scripts.Data
{
    // class that holds and manages storage for user progress data
    public class UserProgressData : UserData
    {
        public int CurrentHumanCampaignLevel = -1; // a level of -1 indicates that the level data hasn't been loaded yet
        public int CurrentBeeCampaignLevel = -1; 
        public int CurrentHumanChallengeLevel = -1; 
        public int CurrentBeeChallengeLevel = -1;
        /// <summary>
        /// The current global fleet Id of the next ship. Every fleet ship of every game type gets its fleet Id from here unless it's not being saved and has a negative Id
        /// </summary>
        public int FleetId = -1;

        /// <summary>
        ///  The current global saved squad Id of the next saved squad. Every saved squad of every game type gets its saved squad Id from here unless it's not being saved and has a negative Id
        /// </summary>
        public int SavedSquadId = -1;
        public int HumanCampaignSavedSquadNumber = -1;
        public int BeeCampaignSavedSquadNumber = -1;
        public int HumanChallengeSavedSquadNumber = -1;
        public int BeeChallengeSavedSquadNumber = -1;
        public int HumanFreePlaySavedSquadNumber = -1;
        public int BeeFreePlaySavedSquadNumber = -1;
        /// <summary>
        /// How much TSV the user has mined, less whatever the user has spent
        /// </summary>
        public int MinedTSV = 0;
        /// <summary>
        /// How much TSV the AI has mined, less whatever the AI has spent
        /// </summary>
        public int HivemindMinedTSV = 0;

        public int HumanCampaignWins, BeeCampaignWins; 
        public int HumanChallengeWins, BeeChallengeWins;
        public int HumanFreePlayWins, BeeFreePlayWins;
        public int HumanFishTankWins, BeeFishTankWins;

        // Unlockables
        public bool HasStartedHumanCampaign = false;
        public bool IsBeeCampaignUnlocked = false;
        public bool IsHumanChallengeUnlocked = false;
        public bool IsBeeChallengeUnlocked = false;
        public bool IsHumanFreePlayUnlocked = false;
        public bool IsBeeFreePlayUnlocked = false;
        public bool IsFishTankUnlocked = false;

        public bool HasMetAlejandraAndEmilia = false;
        public bool HasSeenBuildInterface = false;
        public bool HasSeenCarrierIntro = false;

        // Ship types that the user has unlocked and can see in the codex or in the game
        public HashSet<ConfigData.ShipTypes> VisibleBeeShipTypes;
        public HashSet<ConfigData.ShipTypes> VisibleHumanShipTypes;
        public HashSet<ConfigData.ShipTypes> VisibleCodexBeeShipTypes;
        public HashSet<ConfigData.ShipTypes> VisibleCodexHumanShipTypes;
        public HashSet<ConfigData.ShipTypes> VisibleCodexShipTypes;
        public HashSet<ConfigData.ShipTypes> VisibleShipTypes;
        public HashSet<ConfigData.ShipTypes> AllShipTypes;
        public HashSet<ConfigData.ShipTypes> UnlockedCampaignShips;

        public string PlayerName;


        public LevelOptions CurrentLevel;

        public UserProgressData(bool shouldFileExist): base()
        {
            defaultJsonData = "{" +
                "\"CurrentHumanCampaignLevel\": 0, \"CurrentBeeCampaignLevel\": 0, \"CurrentHumanChallengeLevel\": 0, \"CurrentBeeChallengeLevel\": 0, \"FleetId\": 1521, \"SavedSquadId\": -1, \"HumanCampaignSavedSquadNumber\": 0, \"BeeCampaignSavedSquadNumber\": 0, \"HumanChallengeSavedSquadNumber\": 0, \"BeeChallengeSavedSquadNumber\": 0, \"HumanFreePlaySavedSquadNumber\": 0, \"BeeFreePlaySavedSquadNumber\": 0, \"MinedTSV\": 0, \"HivemindMinedTSV\": 0, \"HumanCampaignWins\": 0, \"BeeCampaignWins\": 0, \"HumanChallengeWins\": 0, \"BeeChallengeWins\": 0, \"HumanFreePlayWins\": 0, \"BeeFreePlayWins\": 0, \"HumanFishTankWins\": 0, \"BeeFishTankWins\": 0, \"HasStartedHumanCampaign\": false, \"IsBeeCampaignUnlocked\": false, \"IsHumanChallengeUnlocked\": false, \"IsBeeChallengeUnlocked\": false, \"IsHumanFreePlayUnlocked\": false, \"IsBeeFreePlayUnlocked\": false, \"IsFishTankUnlocked\": false, \"HasMetAlejandraAndEmilia\": false, \"HasSeenBuildInterface\": false, \"HasSeenCarrierIntro\": false, \"VisibleBeeShipTypes\": [\"Honeybee\"], \"VisibleHumanShipTypes\": [\"Scout\", \"Gunship\"], \"UnlockedCampaignShips\": [\"Scout\", \"Gunship\"], \"VisibleCodexBeeShipTypes\": [], \"VisibleCodexHumanShipTypes\": [\"Scout\", \"Gunship\"], \"PlayerName\": \"\"" +
            "}";
            
            dynamic json = SetupFile(shouldFileExist, ConfigData.UserProgressFilename, (json) =>
            {
                ConfigData.IsUserProgressDataLoaded = true;
                //Debug.Log($"User progress data is loaded");
                //Debug.Log("Updated config file");
                //Debug.Log($"JSON from DataFile: {json}");
                CurrentHumanCampaignLevel = json.CurrentHumanCampaignLevel;
                CurrentBeeCampaignLevel = json.CurrentBeeCampaignLevel;
                CurrentHumanChallengeLevel = json.CurrentHumanChallengeLevel;
                CurrentBeeChallengeLevel = json.CurrentBeeChallengeLevel;
                FleetId = json.FleetId;

                SavedSquadId = json.SavedSquadId;
                HumanCampaignSavedSquadNumber = json.HumanCampaignSavedSquadNumber;
                BeeCampaignSavedSquadNumber = json.BeeCampaignSavedSquadNumber;
                HumanChallengeSavedSquadNumber = json.HumanChallengeSavedSquadNumber;
                BeeChallengeSavedSquadNumber = json.BeeChallengeSavedSquadNumber;
                HumanFreePlaySavedSquadNumber = json.HumanFreePlaySavedSquadNumber;
                BeeFreePlaySavedSquadNumber = json.BeeFreePlaySavedSquadNumber;

                MinedTSV = json.MinedTSV;
                HivemindMinedTSV = json.HivemindMinedTSV;

                HasStartedHumanCampaign = json.HasStartedHumanCampaign;
                IsBeeCampaignUnlocked = json.IsBeeCampaignUnlocked;
                IsHumanChallengeUnlocked = json.IsHumanChallengeUnlocked;
                IsBeeChallengeUnlocked = json.IsBeeChallengeUnlocked;
                IsHumanFreePlayUnlocked = json.IsHumanFreePlayUnlocked;
                IsBeeFreePlayUnlocked = json.IsBeeFreePlayUnlocked;
                IsFishTankUnlocked = json.IsFishTankUnlocked;

                HasMetAlejandraAndEmilia = json.HasMetAlejandraAndEmilia;
                HasSeenBuildInterface = json.HasSeenBuildInterface;
                HasSeenCarrierIntro = json.HasSeenCarrierIntro;

                HumanCampaignWins = json.HumanCampaignWins;
                BeeCampaignWins = json.BeeCampaignWins;
                HumanChallengeWins = json.HumanChallengeWins;
                BeeChallengeWins = json.BeeChallengeWins;
                HumanFreePlayWins = json.HumanFreePlayWins;
                BeeFreePlayWins = json.BeeFreePlayWins;
                HumanFishTankWins = json.HumanFishTankWins;
                BeeFishTankWins = json.BeeFishTankWins;

                PlayerName = json.PlayerName;

                VisibleBeeShipTypes = new HashSet<ConfigData.ShipTypes>(Utilities.JArrayToShipTypes(json.VisibleBeeShipTypes));
                VisibleHumanShipTypes = new HashSet<ConfigData.ShipTypes>(Utilities.JArrayToShipTypes(json.VisibleHumanShipTypes));
                VisibleCodexBeeShipTypes = new HashSet<ConfigData.ShipTypes>(Utilities.JArrayToShipTypes(json.VisibleCodexBeeShipTypes));
                VisibleCodexHumanShipTypes = new HashSet<ConfigData.ShipTypes>(Utilities.JArrayToShipTypes(json.VisibleCodexHumanShipTypes));
                UnlockedCampaignShips = new HashSet<ConfigData.ShipTypes>(Utilities.JArrayToShipTypes(json.UnlockedCampaignShips));

                SetShipTypes();
                AllShipTypes = new HashSet<ConfigData.ShipTypes>(VisibleShipTypes);
                AllShipTypes.Add(ConfigData.ShipTypes.Beacon);
                AllShipTypes.Add(ConfigData.ShipTypes.Drone);
                AllShipTypes.Add(ConfigData.ShipTypes.Striker);


            });
            
        }

        public void SetShipTypes()
        {
            VisibleShipTypes = new HashSet<ConfigData.ShipTypes>(VisibleHumanShipTypes.Union(VisibleBeeShipTypes));
            VisibleCodexShipTypes = new HashSet<ConfigData.ShipTypes>(VisibleCodexHumanShipTypes.Union(VisibleCodexBeeShipTypes));
            ConfigData.BeeShipTypes = VisibleBeeShipTypes;
            ConfigData.HumanShipTypes = VisibleHumanShipTypes;

        }
        public override string ToJson()
        {
            string json = "{" +
                $"\"CurrentHumanCampaignLevel\": {CurrentHumanCampaignLevel}, \"CurrentBeeCampaignLevel\": {CurrentBeeCampaignLevel}, \"CurrentHumanChallengeLevel\": {CurrentHumanChallengeLevel}, \"CurrentBeeChallengeLevel\": {CurrentBeeChallengeLevel}, \"FleetId\": {FleetId}, \"SavedSquadId\": {SavedSquadId}, \"HumanCampaignSavedSquadNumber\": {HumanCampaignSavedSquadNumber}, \"BeeCampaignSavedSquadNumber\": {BeeCampaignSavedSquadNumber}, \"HumanChallengeSavedSquadNumber\": {HumanChallengeSavedSquadNumber}, \"BeeChallengeSavedSquadNumber\": {BeeChallengeSavedSquadNumber}, \"HumanFreePlaySavedSquadNumber\": {HumanFreePlaySavedSquadNumber}, \"BeeFreePlaySavedSquadNumber\": {BeeFreePlaySavedSquadNumber}, \"MinedTSV\": {MinedTSV}, \"HivemindMinedTSV\": {HivemindMinedTSV}, \"HumanCampaignWins\": {HumanCampaignWins}, \"BeeCampaignWins\": {BeeCampaignWins}, \"HumanChallengeWins\": {HumanChallengeWins}, \"BeeChallengeWins\": {BeeChallengeWins}, \"HumanFreePlayWins\": {HumanFreePlayWins}, \"BeeFreePlayWins\": {BeeFreePlayWins}, \"HumanFishTankWins\": {HumanFishTankWins}, \"BeeFishTankWins\": {BeeFishTankWins}, \"HasStartedHumanCampaign\": \"{HasStartedHumanCampaign}\", \"IsBeeCampaignUnlocked\": \"{IsBeeCampaignUnlocked}\", \"IsHumanChallengeUnlocked\": \"{IsHumanChallengeUnlocked}\", \"IsBeeChallengeUnlocked\": \"{IsBeeChallengeUnlocked}\", \"IsHumanFreePlayUnlocked\": \"{IsHumanFreePlayUnlocked}\", \"IsBeeFreePlayUnlocked\": \"{IsBeeFreePlayUnlocked}\", \"IsFishTankUnlocked\": \"{IsFishTankUnlocked}\", \"HasMetAlejandraAndEmilia\": \"{HasMetAlejandraAndEmilia}\", \"HasSeenBuildInterface\": \"{HasSeenBuildInterface}\", \"HasSeenCarrierIntro\": \"{HasSeenCarrierIntro}\", \"PlayerName\": \"{PlayerName}\", \"VisibleBeeShipTypes\": [";

            if (VisibleBeeShipTypes.Count > 0)
            {
                VisibleBeeShipTypes.ToList().ForEach((s) => json += $"\"{Utilities.ConvertShipTypeToName[s]}\", ");
                json = json.Remove(json.Length - 2);
            }

            json += "], \"VisibleHumanShipTypes\": [";
            if (VisibleHumanShipTypes.Count > 0)
            {
                VisibleHumanShipTypes.ToList().ForEach((s) => json += $"\"{Utilities.ConvertShipTypeToName[s]}\", ");
                json = json.Remove(json.Length - 2);
            }

            json += "], \"VisibleCodexBeeShipTypes\": [";
            if (VisibleCodexBeeShipTypes.Count > 0)
            {
                VisibleCodexBeeShipTypes.ToList().ForEach((s) => json += $"\"{Utilities.ConvertShipTypeToName[s]}\", ");
                json = json.Remove(json.Length - 2);
            }

            json += "], \"VisibleCodexHumanShipTypes\": [";
            if (VisibleCodexHumanShipTypes.Count > 0)
            {
                VisibleCodexHumanShipTypes.ToList().ForEach((s) => json += $"\"{Utilities.ConvertShipTypeToName[s]}\", ");
                json = json.Remove(json.Length - 2);
            }

            json += "], \"UnlockedCampaignShips\": [";
            if (UnlockedCampaignShips.Count > 0)
            {
                UnlockedCampaignShips.ToList().ForEach((s) => json += $"\"{Utilities.ConvertShipTypeToName[s]}\", ");
                json = json.Remove(json.Length - 2);
            }

            


            json += "]}";
            return json;
        }
        public void GetCurrentLevelOptions()
        {
            CurrentLevel = ConfigData.GetCampaignLevelData().GetLevel(GetCurrentLevel(ConfigData.Configuration.UserSide));
        }
        public void SetCurrentLevel(int level)
        {
            if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide)
            {
                if (level != CurrentHumanCampaignLevel && ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
                {
                    CurrentHumanCampaignLevel = level;
                    Save();
                }
                else if (level != CurrentHumanChallengeLevel && ConfigData.CurrentGameMode == ConfigData.GameModes.Challenge)
                {
                    CurrentHumanChallengeLevel = level;
                    Save();
                }
            }
            else
            {
                if (level != CurrentBeeCampaignLevel && ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
                {
                    CurrentBeeCampaignLevel = level;
                    Save();
                }
                else if (level != CurrentBeeChallengeLevel && ConfigData.CurrentGameMode == ConfigData.GameModes.Challenge)
                {
                    CurrentBeeChallengeLevel = level;
                    Save();
                }
            }

        }
        public int GetCurrentLevel(int side, ConfigData.GameModes gameMode = ConfigData.GameModes.Unset)
        {

            if (gameMode == ConfigData.GameModes.Unset)
            {
                gameMode = ConfigData.CurrentGameMode;
            }
            if (gameMode == ConfigData.GameModes.Campaign)
            {
                if (side == ConfigData.Configuration.HumanSide)
                {
                    return CurrentHumanCampaignLevel;
                }
                return CurrentBeeCampaignLevel;
            }
            else if (gameMode == ConfigData.GameModes.Challenge)
            {
                if (side == ConfigData.Configuration.HumanSide)
                {
                    return CurrentHumanChallengeLevel;
                }
                return CurrentBeeChallengeLevel;
            }
            Debug.LogError("GetCurrentLevel called when not in Campaign or Challenge mode!");
            return -1;
            

        }
        public void AdvanceToNextLevel()
        {
            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
            {
                if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide)
                {

                    Debug.Log($"Advancing from {CurrentHumanCampaignLevel} to {CurrentHumanCampaignLevel + 1} in the human campaign");
                    SetCurrentLevel(CurrentHumanCampaignLevel + 1);
                }
                else
                {
                    Debug.Log($"Advancing from {CurrentBeeCampaignLevel} to {CurrentBeeCampaignLevel + 1} in the bee campaign");
                    SetCurrentLevel(CurrentBeeCampaignLevel + 1);
                }
            }
            else if (ConfigData.CurrentGameMode == ConfigData.GameModes.Challenge)
            {
                if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide)
                {

                    Debug.Log($"Advancing from {CurrentHumanChallengeLevel} to {CurrentHumanChallengeLevel + 1} in the human challenge mode");
                    SetCurrentLevel(CurrentHumanChallengeLevel + 1);
                }
                else
                {
                    Debug.Log($"Advancing from {CurrentBeeChallengeLevel} to {CurrentBeeChallengeLevel + 1} in the bee challenge mode");
                    SetCurrentLevel(CurrentBeeChallengeLevel + 1);
                }
            }

            
        }
        /// <summary>
        /// Gets the next incremental Fleet Id. Does not save the data to "disk"
        /// </summary>
        /// <returns></returns>
        public int GetNextFleetId()
        {
            return ++FleetId;
        }
        /// <summary>
        /// Gets the next incremental Saved Squad Number for naming purposes. Does not save the data to "disk". Should only be used when creating the user's squads.
        /// </summary>
        /// <returns></returns>
        public int GetNextSavedSquadNumber()
        {
            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
            {
                if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide)
                {
                    return HumanCampaignSavedSquadNumber++;
                }
                return BeeCampaignSavedSquadNumber++;
            }
            else if (ConfigData.CurrentGameMode == ConfigData.GameModes.Challenge)
            {
                if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide)
                {
                    return HumanChallengeSavedSquadNumber++;
                }
                return BeeChallengeSavedSquadNumber++;
            }
            else
            {
                if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide)
                {
                    return HumanFreePlaySavedSquadNumber++;
                }
                return BeeFreePlaySavedSquadNumber++;
            }

        }
        /// <summary>
        /// Returns the next saved squad id. Used for uniquely identifying every saved squad regardless of game mode
        /// </summary>
        /// <returns></returns>
        public int GetNextSavedSquadId()
        {
            return ++SavedSquadId;
        }



    }
}