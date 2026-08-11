using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Scenes
{
    /// <summary>
    /// Owns persistence and cross-scene cleanup for Squad Maker state that is edited
    /// directly on shared runtime data rather than committed by the individual UI action.
    /// </summary>
    internal static class SquadMakerPersistence
    {
        private const string SquadMakerScene = "Squad Maker";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneUnloaded -= HandleSceneUnloaded;
            SceneManager.sceneUnloaded += HandleSceneUnloaded;
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneUnloaded(UnityEngine.SceneManagement.Scene scene)
        {
            if (scene.name != SquadMakerScene)
            {
                return;
            }

            // Ship fields such as Name are edited directly on the canonical FleetShip.
            // Persist those edits regardless of which Squad Maker exit path was used.
            ConfigData.CurrentShips?.SaveFleetData();
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            if (scene.name != SquadMakerScene)
            {
                return;
            }

            // Custom opposing-force selection moves SquadMakerSide to the second side before
            // loading another Squad Maker. Backing out moves it to the first side and loads
            // Squad Maker again. Clean up only on that explicit return-to-first-side path.
            // Do not clear on Squad Maker unload: normal campaign/challenge/free-play starts
            // also leave from the first side, and their prepared LevelOptions must survive
            // the transition to Space.
            if (ConfigData.IsUserLoadingCustomEnemySquads &&
                ConfigData.Configuration != null &&
                ConfigData.SquadMakerSide == ConfigData.Configuration.SquadMakerFirstSide)
            {
                ConfigData.IsUserLoadingCustomEnemySquads = false;
                ConfigData.LevelOptions = null;
            }
        }
    }
}
