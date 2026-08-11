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
        /// Prepares the current campaign level and routes to its pre-battle scene.
        /// In-development missions are under active gameplay testing and skip authored
        /// pre-level intros; ready missions retain the normal intro-first flow.
        /// </summary>
        public static void LoadLevel()
        {
            UserProgressData.GetCurrentLevelOptions();
            LevelOptions = (LevelOptions)UserProgressData.CurrentLevel.Clone();

            int currentLevel = UserProgressData.GetCurrentLevel(Configuration.UserSide);
            bool skipIntroForTesting = IsTestingLevel ||
                (CurrentGameMode == GameModes.Campaign &&
                 !CampaignMissionCatalog.IsCampaignComplete(currentLevel) &&
                 CampaignMissionCatalog.ShouldSkipPreLevelIntroForTesting(currentLevel));

            switch (currentLevel)
            {
                case 0:
                    HasSeenPreLevelIntro = false;
                    HasSeenIntermission = false;
                    SceneManager.LoadSceneAsync("Space", LoadSceneMode.Single);
                    Debug.Log("Loading level 0");
                    break;

                case 1:
                    if (skipIntroForTesting || HasSeenPreLevelIntro)
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
                    if (skipIntroForTesting)
                    {
                        HasSeenPreLevelIntro = false;
                        SceneManager.LoadSceneAsync("Squad Maker", LoadSceneMode.Single);
                    }
                    else if (!HasSeenPreLevelIntro)
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
