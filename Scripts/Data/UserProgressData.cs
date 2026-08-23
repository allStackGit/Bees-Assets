using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Assets.Scripts.Data
{
    // class that holds and manages storage for user progress data
    public class UserProgressData : UserData
    {
        public int CurrentHumanCampaignLevel = -1;
        public int CurrentBeeCampaignLevel = -1;
        public int CurrentHumanChallengeLevel = -1;
        public int CurrentBeeChallengeLevel = -1;
        public int FleetId = -1;
        public int SavedSquadId = -1;
        public int HumanCampaignSavedSquadNumber = -1;
        public int BeeCampaignSavedSquadNumber = -1;
        public int HumanChallengeSavedSquadNumber = -1;
        public int BeeChallengeSavedSquadNumber = -1;
        public int HumanFreePlaySavedSquadNumber = -1;
        public int BeeFreePlaySavedSquadNumber = -1;
        public int MinedTSV = 0;
        public int HivemindMinedTSV = 0;
        public int CampaignScore = 0;
        public int ChallengeScore = 0;

        public int HumanCampaignWins, BeeCampaignWins;
        public int HumanChallengeWins, BeeChallengeWins;
        public int HumanFreePlayWins, BeeFreePlayWins;
        public int HumanFishTankWins, BeeFishTankWins;

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
        public bool HasPlayedBefore = false;
        public bool ShowToolTips = true;
        public bool UseMouseScrolling = false;

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

        public UserProgressData(bool shouldFileExist) : base()
        {
            defaultJsonData = "{" +
                "\"CurrentHumanCampaignLevel\": 0, \"CurrentBeeCampaignLevel\": 0, \"CurrentHumanChallengeLevel\": 0, \"CurrentBeeChallengeLevel\": 0, \"FleetId\": 1521, \"SavedSquadId\": -1, \"HumanCampaignSavedSquadNumber\": 0, \"BeeCampaignSavedSquadNumber\": 0, \"HumanChallengeSavedSquadNumber\": 0, \"BeeChallengeSavedSquadNumber\": 0, \"HumanFreePlaySavedSquadNumber\": 0, \"BeeFreePlaySavedSquadNumber\": 0, \"MinedTSV\": 0, \"HivemindMinedTSV\": 0, \"CampaignScore\": 0, \"ChallengeScore\": 0, \"HumanCampaignWins\": 0, \"BeeCampaignWins\": 0, \"HumanChallengeWins\": 0, \"BeeChallengeWins\": 0, \"HumanFreePlayWins\": 0, \"BeeFreePlayWins\": 0, \"HumanFishTankWins\": 0, \"BeeFishTankWins\": 0, \"HasStartedHumanCampaign\": false, \"IsBeeCampaignUnlocked\": false, \"IsHumanChallengeUnlocked\": false, \"IsBeeChallengeUnlocked\": false, \"IsHumanFreePlayUnlocked\": false, \"IsBeeFreePlayUnlocked\": false, \"IsFishTankUnlocked\": false, \"HasMetAlejandraAndEmilia\": false, \"HasSeenBuildInterface\": false, \"HasSeenCarrierIntro\": false, \"HasPlayedBefore\": false, \"ShowToolTips\": true, \"UseMouseScrolling\": true, \"VisibleBeeShipTypes\": [\"Honeybee\"], \"VisibleHumanShipTypes\": [\"Scout\", \"Gunship\"], \"UnlockedCampaignShips\": [\"Scout\", \"Gunship\"], \"VisibleCodexBeeShipTypes\": [], \"VisibleCodexHumanShipTypes\": [\"Scout\", \"Gunship\"], \"PlayerName\": \"\"" +
                "}";

            SetupFile(shouldFileExist, ConfigData.UserProgressFilename, loadedData =>
            {
                JObject json = AotJson.RequireObject(loadedData, ConfigData.UserProgressFilename);
                ConfigData.IsUserProgressDataLoaded = true;
                CurrentHumanCampaignLevel = json.Value<int>("CurrentHumanCampaignLevel");
                CurrentBeeCampaignLevel = json.Value<int>("CurrentBeeCampaignLevel");
                CurrentHumanChallengeLevel = json.Value<int>("CurrentHumanChallengeLevel");
                CurrentBeeChallengeLevel = json.Value<int>("CurrentBeeChallengeLevel");
                FleetId = json.Value<int>("FleetId");

                SavedSquadId = json.Value<int>("SavedSquadId");
                HumanCampaignSavedSquadNumber = json.Value<int>("HumanCampaignSavedSquadNumber");
                BeeCampaignSavedSquadNumber = json.Value<int>("BeeCampaignSavedSquadNumber");
                HumanChallengeSavedSquadNumber = json.Value<int>("HumanChallengeSavedSquadNumber");
                BeeChallengeSavedSquadNumber = json.Value<int>("BeeChallengeSavedSquadNumber");
                HumanFreePlaySavedSquadNumber = json.Value<int>("HumanFreePlaySavedSquadNumber");
                BeeFreePlaySavedSquadNumber = json.Value<int>("BeeFreePlaySavedSquadNumber");

                MinedTSV = json.Value<int>("MinedTSV");
                HivemindMinedTSV = json.Value<int>("HivemindMinedTSV");
                CampaignScore = json["CampaignScore"]?.Value<int>() ?? 0;
                ChallengeScore = json["ChallengeScore"]?.Value<int>() ?? 0;

                HasStartedHumanCampaign = json.Value<bool>("HasStartedHumanCampaign");
                IsBeeCampaignUnlocked = json.Value<bool>("IsBeeCampaignUnlocked");
                IsHumanChallengeUnlocked = json.Value<bool>("IsHumanChallengeUnlocked");
                IsBeeChallengeUnlocked = json.Value<bool>("IsBeeChallengeUnlocked");
                IsHumanFreePlayUnlocked = json.Value<bool>("IsHumanFreePlayUnlocked");
                IsBeeFreePlayUnlocked = json.Value<bool>("IsBeeFreePlayUnlocked");
                IsFishTankUnlocked = json.Value<bool>("IsFishTankUnlocked");

                HasMetAlejandraAndEmilia = json.Value<bool>("HasMetAlejandraAndEmilia");
                HasSeenBuildInterface = json.Value<bool>("HasSeenBuildInterface");
                HasSeenCarrierIntro = json.Value<bool>("HasSeenCarrierIntro");
                HasPlayedBefore = json["HasPlayedBefore"]?.Value<bool>() ?? false;
                ShowToolTips = json["ShowToolTips"]?.Value<bool>() ?? true;
                UseMouseScrolling = json["UseMouseScrolling"]?.Value<bool>() ?? true;

                HumanCampaignWins = json.Value<int>("HumanCampaignWins");
                BeeCampaignWins = json.Value<int>("BeeCampaignWins");
                HumanChallengeWins = json.Value<int>("HumanChallengeWins");
                BeeChallengeWins = json.Value<int>("BeeChallengeWins");
                HumanFreePlayWins = json.Value<int>("HumanFreePlayWins");
                BeeFreePlayWins = json.Value<int>("BeeFreePlayWins");
                HumanFishTankWins = json.Value<int>("HumanFishTankWins");
                BeeFishTankWins = json.Value<int>("BeeFishTankWins");

                PlayerName = json.Value<string>("PlayerName");

                VisibleBeeShipTypes = new HashSet<ConfigData.ShipTypes>(AotJson.ParseShipTypes(json["VisibleBeeShipTypes"]));
                VisibleHumanShipTypes = new HashSet<ConfigData.ShipTypes>(AotJson.ParseShipTypes(json["VisibleHumanShipTypes"]));
                VisibleCodexBeeShipTypes = new HashSet<ConfigData.ShipTypes>(AotJson.ParseShipTypes(json["VisibleCodexBeeShipTypes"]));
                VisibleCodexHumanShipTypes = new HashSet<ConfigData.ShipTypes>(AotJson.ParseShipTypes(json["VisibleCodexHumanShipTypes"]));
                UnlockedCampaignShips = new HashSet<ConfigData.ShipTypes>(AotJson.ParseShipTypes(json["UnlockedCampaignShips"]));

                SetShipTypes();
            });
        }

        public void SetShipTypes()
        {
            VisibleShipTypes = new HashSet<ConfigData.ShipTypes>(VisibleHumanShipTypes.Union(VisibleBeeShipTypes));
            VisibleCodexShipTypes = new HashSet<ConfigData.ShipTypes>(VisibleCodexHumanShipTypes.Union(VisibleCodexBeeShipTypes));
            ConfigData.BeeShipTypes = VisibleBeeShipTypes;
            ConfigData.HumanShipTypes = VisibleHumanShipTypes;

            AllShipTypes = ConfigData.TypesOfShootingStrategies
                .Where(strategy => (int)strategy > 15)
                .Select(strategy => Utilities.ConvertShootingStrategyToShipType[strategy])
                .ToHashSet();
        }

        public override string ToJson()
        {
            JObject json = new JObject
            {
                ["CurrentHumanCampaignLevel"] = CurrentHumanCampaignLevel,
                ["CurrentBeeCampaignLevel"] = CurrentBeeCampaignLevel,
                ["CurrentHumanChallengeLevel"] = CurrentHumanChallengeLevel,
                ["CurrentBeeChallengeLevel"] = CurrentBeeChallengeLevel,
                ["FleetId"] = FleetId,
                ["SavedSquadId"] = SavedSquadId,
                ["HumanCampaignSavedSquadNumber"] = HumanCampaignSavedSquadNumber,
                ["BeeCampaignSavedSquadNumber"] = BeeCampaignSavedSquadNumber,
                ["HumanChallengeSavedSquadNumber"] = HumanChallengeSavedSquadNumber,
                ["BeeChallengeSavedSquadNumber"] = BeeChallengeSavedSquadNumber,
                ["HumanFreePlaySavedSquadNumber"] = HumanFreePlaySavedSquadNumber,
                ["BeeFreePlaySavedSquadNumber"] = BeeFreePlaySavedSquadNumber,
                ["MinedTSV"] = MinedTSV,
                ["HivemindMinedTSV"] = HivemindMinedTSV,
                ["CampaignScore"] = CampaignScore,
                ["ChallengeScore"] = ChallengeScore,
                ["HumanCampaignWins"] = HumanCampaignWins,
                ["BeeCampaignWins"] = BeeCampaignWins,
                ["HumanChallengeWins"] = HumanChallengeWins,
                ["BeeChallengeWins"] = BeeChallengeWins,
                ["HumanFreePlayWins"] = HumanFreePlayWins,
                ["BeeFreePlayWins"] = BeeFreePlayWins,
                ["HumanFishTankWins"] = HumanFishTankWins,
                ["BeeFishTankWins"] = BeeFishTankWins,
                ["HasStartedHumanCampaign"] = HasStartedHumanCampaign,
                ["IsBeeCampaignUnlocked"] = IsBeeCampaignUnlocked,
                ["IsHumanChallengeUnlocked"] = IsHumanChallengeUnlocked,
                ["IsBeeChallengeUnlocked"] = IsBeeChallengeUnlocked,
                ["IsHumanFreePlayUnlocked"] = IsHumanFreePlayUnlocked,
                ["IsBeeFreePlayUnlocked"] = IsBeeFreePlayUnlocked,
                ["IsFishTankUnlocked"] = IsFishTankUnlocked,
                ["HasMetAlejandraAndEmilia"] = HasMetAlejandraAndEmilia,
                ["HasSeenBuildInterface"] = HasSeenBuildInterface,
                ["HasSeenCarrierIntro"] = HasSeenCarrierIntro,
                ["HasPlayedBefore"] = HasPlayedBefore,
                ["ShowToolTips"] = ShowToolTips,
                ["UseMouseScrolling"] = UseMouseScrolling,
                ["PlayerName"] = PlayerName ?? string.Empty,
                ["VisibleBeeShipTypes"] = ShipTypesToJson(VisibleBeeShipTypes),
                ["VisibleHumanShipTypes"] = ShipTypesToJson(VisibleHumanShipTypes),
                ["VisibleCodexBeeShipTypes"] = ShipTypesToJson(VisibleCodexBeeShipTypes),
                ["VisibleCodexHumanShipTypes"] = ShipTypesToJson(VisibleCodexHumanShipTypes),
                ["UnlockedCampaignShips"] = ShipTypesToJson(UnlockedCampaignShips),
            };
            return json.ToString(Formatting.None);
        }

        private static JArray ShipTypesToJson(IEnumerable<ConfigData.ShipTypes> shipTypes)
        {
            return new JArray((shipTypes ?? Enumerable.Empty<ConfigData.ShipTypes>())
                .OrderBy(shipType => (int)shipType)
                .Select(shipType => Utilities.ConvertShipTypeToName[shipType]));
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
            if (gameMode == ConfigData.GameModes.Challenge)
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
                int currentLevel = GetCurrentLevel(ConfigData.Configuration.UserSide, ConfigData.GameModes.Campaign);
                Stage activeStage = ConfigData.Scenes.OfType<Stage>().LastOrDefault(scene => scene != null && scene.PrimaryLevel != null);
                Level activeLevel = activeStage?.PrimaryLevel;

                if (activeLevel?.CurrentLevelOptions != null && activeLevel.CurrentLevelOptions.Id >= 0)
                {
                    int missionId = activeLevel.CurrentLevelOptions.Id;
                    int targetLevel = missionId + 1;
                    if (CampaignMissionCatalog.IsCampaignComplete(targetLevel))
                    {
                        Debug.Log($"Campaign mission {missionId} completed the currently available campaign; advancing progress to terminal level {targetLevel}.");
                        SetCurrentLevel(targetLevel);
                        return;
                    }
                    if (currentLevel >= targetLevel)
                    {
                        return;
                    }

                    Debug.Log($"Advancing campaign from {currentLevel} to {targetLevel} after mission {missionId}");
                    SetCurrentLevel(targetLevel);
                    return;
                }

                int fallbackTargetLevel = currentLevel + 1;
                if (CampaignMissionCatalog.IsCampaignComplete(fallbackTargetLevel))
                {
                    Debug.Log($"Campaign level {currentLevel} completed the currently available campaign; advancing progress to terminal level {fallbackTargetLevel}.");
                    SetCurrentLevel(fallbackTargetLevel);
                    return;
                }
                SetCurrentLevel(fallbackTargetLevel);
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

        public int GetNextFleetId()
        {
            return ++FleetId;
        }

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
            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Challenge)
            {
                if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide)
                {
                    return HumanChallengeSavedSquadNumber++;
                }
                return BeeChallengeSavedSquadNumber++;
            }

            if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide)
            {
                return HumanFreePlaySavedSquadNumber++;
            }
            return BeeFreePlaySavedSquadNumber++;
        }

        public int GetNextSavedSquadId()
        {
            return ++SavedSquadId;
        }
    }
}
