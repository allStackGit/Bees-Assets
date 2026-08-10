using Assets.Scripts;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Scenes
{
    /// <summary>
    /// Compatibility boundary for the legacy ConfigData.LoadLevel flow.
    /// Campaign levels 2+ currently enter Squad Maker before their authored Level Intro.
    /// Redirect pending pre-battle Squad Maker loads before the scene can be presented to
    /// the player. Once LevelIntro marks HasSeenPreLevelIntro, allow that Squad Maker load
    /// and consume the permission so the following campaign mission still receives its intro.
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

            // ConfigData.LoadLevel still has a legacy Squad Maker-first route for campaign
            // levels 2+. sceneLoaded runs before the first rendered frame, so make that
            // compatibility scene non-presentational and synchronously replace it with the
            // authored intro. This prevents both the Squad Maker flash and the extra async
            // scene-load delay while preserving the post-intro Squad Maker transition.
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                root.SetActive(false);
            }
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
            return sceneName == SquadMakerScene &&
                ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign &&
                ConfigData.LevelOptions != null &&
                !ConfigData.IsTestingLevel;
        }
    }
}
