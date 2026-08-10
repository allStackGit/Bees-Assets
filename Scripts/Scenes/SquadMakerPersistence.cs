using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Scenes
{
    /// <summary>
    /// Persists direct FleetShip edits made by Squad Maker when the scene is left.
    /// Squad edits are already saved explicitly; ship fields such as Name are edited on
    /// the canonical FleetShip and otherwise could be lost when leaving without another save.
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
            if (scene.name != SquadMakerScene || ConfigData.CurrentShips == null)
            {
                return;
            }

            ConfigData.CurrentShips.SaveFleetData();
        }
    }
}
