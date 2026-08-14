using Assets.Scripts.Data;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts
{
    public static partial class ConfigData
    {
        /// <summary>
        /// Campaign progress owns mission identity. Squad selection may add chosen squads to the
        /// level options, but it must never hand Stage a different persisted campaign mission.
        /// Negative IDs are intentionally left alone because Squad Maker uses them for explicit
        /// test/custom levels.
        /// </summary>
        private static LevelOptions NormalizeCampaignLevelOptions(LevelOptions candidate)
        {
            if (candidate == null || CurrentGameMode != GameModes.Campaign || candidate.Id < 0 ||
                UserProgressData == null || Configuration == null || GetCampaignLevelData() == null)
            {
                return candidate;
            }

            int currentMissionId = UserProgressData.GetCurrentLevel(Configuration.UserSide, GameModes.Campaign);
            if (CampaignMissionCatalog.IsCampaignComplete(currentMissionId) || candidate.Id == currentMissionId)
            {
                return candidate;
            }

            LevelOptions persistedMission = GetCampaignLevelData().GetLevel(currentMissionId);
            if (persistedMission == null)
            {
                Debug.LogError($"Campaign level handoff tried to use level #{candidate.Id}, but current progress is #{currentMissionId} and that persisted mission could not be loaded.");
                return candidate;
            }

            LevelOptions corrected = (LevelOptions)persistedMission.Clone();
            corrected.ChosenSquads = new List<SavedSquad>();
            if (candidate.ChosenSquads != null)
            {
                candidate.ChosenSquads.ForEach(squad => corrected.ChosenSquads.Add((SavedSquad)squad.Clone()));
            }

            Debug.LogWarning($"Corrected mismatched campaign level handoff from #{candidate.Id} ({candidate.Name}) to current mission #{currentMissionId} ({corrected.Name}).");
            return corrected;
        }

        /// <summary>
        /// Prepares the current campaign level and routes to its authored pre-battle scene.
        /// Development/test status must not alter player-facing campaign presentation: intros and
        /// intermissions are part of the mission flow and are skipped only through the player's
        /// explicit Skip control inside the Level Intro scene.
        /// </summary>
        public static void LoadLevel()
        {
            UserProgressData.GetCurrentLevelOptions();
            LevelOptions = (LevelOptions)UserProgressData.CurrentLevel.Clone();

            // A stale test-mode flag used to leak from development runs into persisted campaign
            // missions. Level.SetupLevel treats that flag as a request to skip SetTriggers(), which
            // removes the mission's authored objectives and dialogue. Only explicit negative-ID
            // custom/test levels are allowed to keep test mode while loading campaign scenes.
            if (LevelOptions == null || LevelOptions.Id >= 0)
            {
                IsTestingLevel = false;
            }

            int currentLevel = UserProgressData.GetCurrentLevel(Configuration.UserSide);

            switch (currentLevel)
            {
                case 0:
                    HasSeenPreLevelIntro = false;
                    HasSeenIntermission = false;
                    SceneManager.LoadSceneAsync("Space", LoadSceneMode.Single);
                    Debug.Log("Loading level 0");
                    break;

                case 1:
                    if (HasSeenPreLevelIntro)
                    {
                        HasSeenPreLevelIntro = false;
                        SceneManager.LoadSceneAsync("Space", LoadSceneMode.Single);
                    }
                    else
                    {
                        SceneManager.LoadSceneAsync("Level Intro", LoadSceneMode.Single);
                    }
                    break;

                case 2:
                case 3:
                case 4:
                case 5:
                case 6:
                case 7:
                case 8:
                case 9:
                case 10:
                case 11:
                    if (!HasSeenPreLevelIntro)
                    {
                        SceneManager.LoadSceneAsync("Level Intro", LoadSceneMode.Single);
                    }
                    else
                    {
                        HasSeenPreLevelIntro = false;
                        SceneManager.LoadSceneAsync("Squad Maker", LoadSceneMode.Single);
                    }
                    break;

                default:
                    Debug.LogError($"Tried to load unknown level {currentLevel}");
                    break;
            }
        }
    }
}
