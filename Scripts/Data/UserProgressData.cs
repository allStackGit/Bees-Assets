using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
        public int CampaignScore = 0;
        public int ChallengeScore = 0;

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
        public bool HasPlayedBefore = false;
        public bool ShowToolTips = true;
        public bool UseMouseScrolling = false;

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
                "\"CurrentHumanCampaignLevel\": 0, \"CurrentBeeCampaignLevel\": 0, \"CurrentHumanChallengeLevel\": 0, \"CurrentBeeChallengeLevel\": 0, \"FleetId\": 1521, \"SavedSquadId\": -1, \"HumanCampaignSavedSquadNumber\": 0, \"BeeCampaignSavedSquadNumber\": 0, \"HumanChallengeSavedSquadNumber\": 0, \"BeeChallengeSavedSquadNumber\": 0, \"HumanFreePlaySavedSquadNumber\": 0, \"BeeFreePlaySavedSquadNumber\": 0, \"MinedTSV\": 0, \"HivemindMinedTSV\": 0, \"CampaignScore\": 0, \"ChallengeScore\": 0, \"HumanCampaignWins\": 0, \"BeeCampaignWins\": 0, \"HumanChallengeWins\": 0, \"BeeChallengeWins\": 0, \"HumanFreePlayWins\": 0, \"BeeFreePlayWins\": 0, \"HumanFishTankWins\": 0, \"BeeFishTankWins\": 0, \"HasStartedHumanCampaign\": false, \"IsBeeCampaignUnlocked\": false, \"IsHumanChallengeUnlocked\": false, \"IsBeeChallengeUnlocked\": false, \"IsHumanFreePlayUnlocked\": false, \"IsBeeFreePlayUnlocked\": false, \"IsFishTankUnlocked\": false, \"HasMetAlejandraAndEmilia\": false, \"HasSeenBuildInterface\": false, \"HasSeenCarrierIntro\": false, \"HasPlayedBefore\": false, \"ShowToolTips\": true, \"UseMouseScrolling\": true, \"VisibleBeeShipTypes\": [\"Honeybee\"], \"VisibleHumanShipTypes\": [\"Scout\", \"Gunship\"], \"UnlockedCampaignShips\": [\"Scout\", \"Gunship\"], \"VisibleCodexBeeShipTypes\": [], \"VisibleCodexHumanShipTypes\": [\"Scout\", \"Gunship\"], \"PlayerName\": \"\"" +
            "}";
            
            dynamic json = SetupFile(shouldFileExist, ConfigData.UserProgressFilename, (json) =>
            {
                ConfigData.IsUserProgressDataLoaded = true;
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
                CampaignScore = json.CampaignScore != null ? json.CampaignScore : 0;
                ChallengeScore = json.ChallengeScore != null ? json.ChallengeScore : 0;

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
                HasPlayedBefore = json.HasPlayedBefore != null ? json.HasPlayedBefore : false;
                ShowToolTips = json.ShowToolTips != null ? json.ShowToolTips : true;
                UseMouseScrolling = json.UseMouseScrolling != null ? json.UseMouseScrolling : true;

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
            });
        }

        public void SetShipTypes()
        {
            VisibleShipTypes = new HashSet<ConfigData.ShipTypes>(VisibleHumanShipTypes.Union(VisibleBeeShipTypes));
            VisibleCodexShipTypes = new HashSet<ConfigData.ShipTypes>(VisibleCodexHumanShipTypes.Union(VisibleCodexBeeShipTypes));
            ConfigData.BeeShipTypes = VisibleBeeShipTypes;
            ConfigData.HumanShipTypes = VisibleHumanShipTypes;

            // Strategy availability is a property of the complete strategy catalog, not of
            // the player's current unlocks. Rebuild this whenever visibility changes so Hive
            // Mind type-target bans always cover every type strategy the server can select.
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
                int currentLevel = GetCurrentLevel(ConfigData.Configuration.UserSide, ConfigData.GameModes.Campaign);
                Stage activeStage = ConfigData.Scenes.OfType<Stage>().LastOrDefault(scene => scene != null && scene.PrimaryLevel != null);
                Level activeLevel = activeStage?.PrimaryLevel;

                if (activeLevel?.CurrentLevelOptions != null && activeLevel.CurrentLevelOptions.Id >= 0)
                {
                    int missionId = activeLevel.CurrentLevelOptions.Id;
                    int targetLevel = missionId + 1;
                    if (CampaignMissionCatalog.IsCampaignComplete(targetLevel))
                    {
                        // The first ID beyond the catalog is the persisted completion sentinel.
                        // MainMenu and the level-end UI use that value to display the completed
                        // campaign state instead of offering the final mission again.
                        Debug.Log($"Campaign mission {missionId} completed the currently available campaign; advancing progress to terminal level {targetLevel}.");
                        SetCurrentLevel(targetLevel);
                        return;
                    }
                    if (currentLevel >= targetLevel)
                    {
                        // Several legacy endings call this method more than once. Advancement is
                        // mission-idempotent so a duplicate call cannot skip the next mission.
                        return;
                    }

                    Debug.Log($"Advancing campaign from {currentLevel} to {targetLevel} after mission {missionId}");
                    SetCurrentLevel(targetLevel);
                    return;
                }

                // Defensive fallback for tooling/non-scene callers that do not own a live Level.
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

        /// <summary>
        /// Gets the next incremental Fleet Id. Does not save the data to "disk"
        /// </summary>
        public int GetNextFleetId()
        {
            return ++FleetId;
        }

        /// <summary>
        /// Gets the next saved squad number for naming purposes. Does not save the data to "disk". Should only be used when creating the user's squads.
        /// </summary>
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
        public int GetNextSavedSquadId()
        {
            return ++SavedSquadId;
        }
    }
}
