using Assets.Scripts.Data;
using Assets.Scripts.Levels;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts
{
    public static partial class ConfigData
    {
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
