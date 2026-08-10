using Assets.Scripts;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Scenes
{
    /// <summary>
    /// Compatibility boundary for the legacy ConfigData.LoadLevel flow.
    /// Campaign levels 2+ currently enter Squad Maker before their authored Level Intro.
    /// Redirect only pre-battle campaign Squad Maker loads whose intro state is still pending.
    /// Once LevelIntro marks HasSeenPreLevelIntro, allow that Squad Maker load and consume
    /// the permission so the following campaign mission still receives its own intro.
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

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
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

            SceneManager.LoadSceneAsync(LevelIntroScene, LoadSceneMode.Single);
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
            return sceneName == SquadMakerScene &&
                ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign &&
                ConfigData.LevelOptions != null &&
                !ConfigData.IsTestingLevel;
        }
    }
}
