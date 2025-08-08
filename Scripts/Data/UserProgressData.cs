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

        public int HumanCampaignSavedSquadId = -1;
        public int BeeCampaignSavedSquadId = -1;
        public int HumanChallengeSavedSquadId = -1;
        public int BeeChallengeSavedSquadId = -1;
        public int HumanFreePlaySavedSquadId = -1;
        public int BeeFreePlaySavedSquadId = -1;
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

        // Unlockables
        public bool HasStartedHumanCampaign = false;
        public bool IsBeeCampaignUnlocked = false;
        public bool IsHumanChallengeUnlocked = false;
        public bool IsBeeChallengeUnlocked = false;
        public bool IsHumanFreePlayUnlocked = false;
        public bool IsBeeFreePlayUnlocked = false;

        // Ship types that the user has unlocked and can see in the codex or in the game
        public HashSet<ConfigData.ShipTypes> VisibleBeeShipTypes;
        public HashSet<ConfigData.ShipTypes> VisibleHumanShipTypes;
        public HashSet<ConfigData.ShipTypes> VisibleCodexBeeShipTypes;
        public HashSet<ConfigData.ShipTypes> VisibleCodexHumanShipTypes;
        public HashSet<ConfigData.ShipTypes> VisibleCodexShipTypes;
        public HashSet<ConfigData.ShipTypes> InvisibleBeeShipTypes;
        public HashSet<ConfigData.ShipTypes> InvisibleHumanShipTypes;
        public HashSet<ConfigData.ShipTypes> VisibleShipTypes;
        public HashSet<ConfigData.ShipTypes> InvisibleShipTypes;
        public HashSet<ConfigData.ShipTypes> AllShipTypes;

        public string PlayerName;


        public LevelOptions CurrentLevel;

        public UserProgressData(bool shouldFileExist): base()
        {
            defaultJsonData = "{" +
                "\"CurrentHumanCampaignLevel\": 1, \"CurrentBeeCampaignLevel\": 1, \"CurrentHumanChallengeLevel\": 1, \"CurrentBeeChallengeLevel\": 1, " +
                "\"HumanCampaignSavedSquadId\": -1, \"BeeCampaignSavedSquadId\": -1, \"HumanChallengeSavedSquadId\": -1, \"BeeChallengeSavedSquadId\": -1," +
                "\"HumanFreePlaySavedSquadId\": -1, \"BeeFreePlaySavedSquadId\": -1," +
                "\"MinedTSV\": 0, \"HivemindMinedTSV\": 0, " +
                "\"HumanCampaignWins\": 0, \"BeeCampaignWins\": 0, \"HumanChallengeWins\": 0, \"BeeChallengeWins\": 0, \"HumanFreePlayWins\": 0, \"BeeFreePlayWins\": 0," +
                "\"IsBeeCampaignUnlocked\": false, " +
                "\"IsHumanChallengeUnlocked\": false, \"IsBeeChallengeUnlocked\": false, " +
                "\"IsHumanFreePlayUnlocked\": false, \"IsBeeFreePlayUnlocked\": false, " +
                "\"VisibleBeeShipTypes\": [\"Honeybee\"], \"VisibleHumanShipTypes\": [\"Scout\", \"Gunship\"], \"InvisibleBeeShipTypes\": [], \"InvisibleHumanShipTypes\": []" +
                ", \"VisibleCodexBeeShipTypes\": [], \"VisibleCodexHumanShipTypes\": [\"Scout\", \"Gunship\"], " +
                "\"PlayerName\": \"Odysseus\""+
            "}";
            
            dynamic json = SetupFile(shouldFileExist, ConfigData.UserProgressFilename, (json) =>
            {
                ConfigData.IsUserProgressDataLoaded = true;
                //Debug.Log("Updated config file");
                //Debug.Log($"JSON from DataFile: {json}");
                CurrentHumanCampaignLevel = json.CurrentHumanCampaignLevel;
                CurrentBeeCampaignLevel = json.CurrentBeeCampaignLevel;
                CurrentHumanChallengeLevel = json.CurrentHumanChallengeLevel;
                CurrentBeeChallengeLevel = json.CurrentBeeChallengeLevel;

                HumanCampaignSavedSquadId = json.HumanCampaignSavedSquadId;
                BeeCampaignSavedSquadId = json.BeeCampaignSavedSquadId;
                HumanChallengeSavedSquadId = json.HumanChallengeSavedSquadId;
                BeeChallengeSavedSquadId = json.BeeChallengeSavedSquadId;
                HumanFreePlaySavedSquadId = json.HumanFreePlaySavedSquadId;
                BeeFreePlaySavedSquadId = json.BeeFreePlaySavedSquadId;

                MinedTSV = json.MinedTSV;
                HivemindMinedTSV = json.HivemindMinedTSV;

                HumanCampaignWins = json.HumanCampaignWins;
                BeeCampaignWins = json.BeeCampaignWins;
                HumanChallengeWins = json.HumanChallengeWins;
                BeeChallengeWins = json.BeeChallengeWins;
                HumanFreePlayWins = json.HumanFreePlayWins;
                BeeFreePlayWins = json.BeeFreePlayWins;

                VisibleBeeShipTypes = new HashSet<ConfigData.ShipTypes>(Utilities.JArrayToShipTypes(json.VisibleBeeShipTypes));
                VisibleHumanShipTypes = new HashSet<ConfigData.ShipTypes>(Utilities.JArrayToShipTypes(json.VisibleHumanShipTypes));
                VisibleCodexBeeShipTypes = new HashSet<ConfigData.ShipTypes>(Utilities.JArrayToShipTypes(json.VisibleCodexBeeShipTypes));
                VisibleCodexHumanShipTypes = new HashSet<ConfigData.ShipTypes>(Utilities.JArrayToShipTypes(json.VisibleCodexHumanShipTypes));
                InvisibleBeeShipTypes = new HashSet<ConfigData.ShipTypes>(Utilities.JArrayToShipTypes(json.InvisibleBeeShipTypes));
                InvisibleHumanShipTypes = new HashSet<ConfigData.ShipTypes>(Utilities.JArrayToShipTypes(json.InvisibleHumanShipTypes));

                VisibleShipTypes = new HashSet<ConfigData.ShipTypes>(VisibleHumanShipTypes.Union(VisibleBeeShipTypes));
                VisibleCodexShipTypes = new HashSet<ConfigData.ShipTypes>(VisibleCodexHumanShipTypes.Union(VisibleCodexBeeShipTypes));
                InvisibleShipTypes = new HashSet<ConfigData.ShipTypes>(InvisibleHumanShipTypes.Union(VisibleBeeShipTypes));
                AllShipTypes = new HashSet<ConfigData.ShipTypes>(InvisibleBeeShipTypes.Union(VisibleShipTypes).Union(InvisibleShipTypes).Union(VisibleBeeShipTypes).
                    Union(new HashSet<ConfigData.ShipTypes>() { ConfigData.ShipTypes.Beacon, ConfigData.ShipTypes.Drone, ConfigData.ShipTypes.Striker }));

                ConfigData.BeeShipTypes = VisibleBeeShipTypes;
                ConfigData.HumanShipTypes = VisibleHumanShipTypes;


            });
            
        }
        public void LoadCurrentLevel()
        {
            
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
        public int GetCurrentLevel()
        {
            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
            {
                if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide)
                {
                    return CurrentHumanCampaignLevel;
                }
                return CurrentBeeCampaignLevel;
            }
            else if (ConfigData.CurrentGameMode == ConfigData.GameModes.Challenge)
            {
                if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide)
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
            if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide)
            {
                SetCurrentLevel(CurrentHumanCampaignLevel + 1);
            }
            else
            {
                SetCurrentLevel(CurrentBeeCampaignLevel + 1);
            }
            
        }
        public int GetNextSavedSquadId()
        {
            int id = -1;
            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
            {
                if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide)
                {
                    id = ++HumanCampaignSavedSquadId;
                }
                else
                { 
                    id = ++BeeCampaignSavedSquadId;
                }
            }
            else if (ConfigData.CurrentGameMode == ConfigData.GameModes.Challenge)
            {
                if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide)
                {
                    id = ++HumanChallengeSavedSquadId;
                }
                else
                {
                    id = ++BeeChallengeSavedSquadId;
                }
            }
            else if (ConfigData.CurrentGameMode == ConfigData.GameModes.FreePlay)
            {
                if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide)
                {
                    id = ++HumanFreePlaySavedSquadId;
                }
                else
                {
                    id = ++BeeFreePlaySavedSquadId;
                }
            }

            Save();
            return id;
            
        }

        public override string ToJson()
        {
            return JsonUtility.ToJson(this);
        }
    }
}