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

            // Entering custom opposing-force selection moves SquadMakerSide to the second
            // side before the first scene unloads. If the user backs out, GoBack moves it
            // to the first side before unloading instead. Discard that abandoned custom
            // enemy transaction so a later Swarms/Powerful/random setup is not incorrectly
            // treated as custom-enemy loading by Level.Setup().
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
