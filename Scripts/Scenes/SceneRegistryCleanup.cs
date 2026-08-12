using Assets.Scripts;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Scenes
{
    /// <summary>
    /// Keeps ConfigData's process-wide scene registry from retaining destroyed Unity wrappers.
    /// </summary>
    internal static class SceneRegistryCleanup
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneUnloaded -= HandleSceneUnloaded;
            SceneManager.sceneUnloaded += HandleSceneUnloaded;
            PruneDestroyedScenes();
        }

        private static void HandleSceneUnloaded(UnityEngine.SceneManagement.Scene scene)
        {
            PruneDestroyedScenes();
        }

        internal static void PruneDestroyedScenes()
        {
            ConfigData.Scenes.RemoveAll(scene => scene == null);
            if (ConfigData.SocketManager == null)
            {
                ConfigData.SocketManager = null;
            }
        }
    }
}
