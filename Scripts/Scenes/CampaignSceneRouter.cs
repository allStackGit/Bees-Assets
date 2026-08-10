using Assets.Scripts;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Scenes
{
    /// <summary>
    /// Compatibility boundary for the legacy ConfigData.LoadLevel flow.
    /// Campaign levels 2+ currently enter Squad Maker before their authored Level Intro.
    /// Redirect only pre-battle campaign Squad Maker loads whose intro state is still pending.
    /// Once LevelIntro marks HasSeenPreLevelIntro, the same Squad Maker load is allowed through.
    /// </summary>
    internal static class CampaignSceneRouter
    {
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
            if (!ShouldRedirectToLevelIntro(scene.name))
            {
                return;
            }

            SceneManager.LoadSceneAsync(LevelIntroScene, LoadSceneMode.Single);
        }

        internal static bool ShouldRedirectToLevelIntro(string sceneName)
        {
            return sceneName == SquadMakerScene &&
                ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign &&
                ConfigData.LevelOptions != null &&
                !ConfigData.IsTestingLevel &&
                !ConfigData.HasSeenPreLevelIntro;
        }
    }
}
