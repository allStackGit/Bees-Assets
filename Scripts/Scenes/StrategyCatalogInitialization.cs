using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Scenes
{
    /// <summary>
    /// Initializes the strategy-target ship universe from the strategy catalog rather than
    /// from the player's unlock/visibility state. This prevents unavailable type strategies
    /// from remaining eligible simply because the player has not discovered that ship type.
    /// </summary>
    internal sealed class StrategyCatalogInitialization : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject host = new GameObject(nameof(StrategyCatalogInitialization));
            DontDestroyOnLoad(host);
            host.AddComponent<StrategyCatalogInitialization>();
        }

        private void Update()
        {
            if (ConfigData.UserProgressData == null)
            {
                return;
            }

            ConfigData.UserProgressData.AllShipTypes = ConfigData.TypesOfShootingStrategies
                .Where(strategy => (int)strategy > 15)
                .Select(strategy => Utilities.ConvertShootingStrategyToShipType[strategy])
                .ToHashSet();

            Destroy(gameObject);
        }
    }
}
