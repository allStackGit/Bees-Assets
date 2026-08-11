using Assets.Scripts;
using Assets.Scripts.Levels;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Scenes
{
    /// <summary>
    /// Compatibility boundary for the legacy ConfigData.LoadLevel flow.
    /// Campaign levels 2+ still request Squad Maker first. Missions explicitly marked for
    /// intro-bypass testing may continue directly into Squad Maker; all other campaign missions
    /// are synchronously replaced with their authored Level Intro before Squad Maker can render.
    /// Once LevelIntro marks HasSeenPreLevelIntro, allow the subsequent Squad Maker load and
    /// consume the permission so the following mission gets its own intro.
    /// </summary>
    internal static class CampaignSceneRouter
    {
        private const string SpaceScene = "Space";
        private const string SquadMakerScene = "Squad Maker";
        private const string LevelIntroScene = "Level Intro";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            ResetNarrativeStateAtCampaignStart(scene.name);

            if (!IsPendingCampaignSquadMaker(scene.name))
            {
                return;
            }

            if (ConfigData.HasSeenPreLevelIntro)
            {
                ConfigData.HasSeenPreLevelIntro = false;
                return;
            }

            // Do not queue an asynchronous replacement here: that permits Squad Maker to
            // render for one or more frames before the intro finishes loading.
            SceneManager.LoadScene(LevelIntroScene, LoadSceneMode.Single);
        }

        private static void ResetNarrativeStateAtCampaignStart(string sceneName)
        {
            if (sceneName == SpaceScene &&
                ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign &&
                ConfigData.LevelOptions != null &&
                ConfigData.LevelOptions.Id == 0)
            {
                ConfigData.HasSeenPreLevelIntro = false;
                ConfigData.HasSeenIntermission = false;
            }
        }

        internal static bool ShouldRedirectToLevelIntro(string sceneName)
        {
            return IsPendingCampaignSquadMaker(sceneName) && !ConfigData.HasSeenPreLevelIntro;
        }

        private static bool IsPendingCampaignSquadMaker(string sceneName)
        {
            if (sceneName != SquadMakerScene ||
                ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign ||
                ConfigData.LevelOptions == null ||
                ConfigData.IsTestingLevel)
            {
                return false;
            }

            return !CampaignMissionCatalog.ShouldSkipPreLevelIntroForTesting(ConfigData.LevelOptions.Id);
        }
    }
}
