using Assets.Scripts;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Scenes
{
    /// <summary>
    /// Completed response hashes are only a short-lived duplicate-delivery guard. Keeping every
    /// hash for the lifetime of the process grows without bound during long training/play sessions.
    /// Once the cache becomes large, retain only hashes belonging to requests that are still live;
    /// late duplicates of completed requests are harmless because every response handler already
    /// requires a matching standing request before mutating state.
    /// </summary>
    internal sealed class SocketResponseHistoryCleanup : MonoBehaviour
    {
        private const int MaximumHandledResponseHistory = 4096;
        private float _nextCheck;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject host = new GameObject(nameof(SocketResponseHistoryCleanup));
            DontDestroyOnLoad(host);
            host.AddComponent<SocketResponseHistoryCleanup>();
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup < _nextCheck)
            {
                return;
            }
            _nextCheck = Time.realtimeSinceStartup + 30f;

            if (!ConfigData.HasSocketManager() || ConfigData.Socket.HandledRequests.Count <= MaximumHandledResponseHistory)
            {
                return;
            }

            var standingHashes = ConfigData.Socket.StandingRequests.Select(request => request.Hash).ToHashSet();
            ConfigData.Socket.HandledRequests.RemoveWhere(hash => !standingHashes.Contains(hash));
        }
    }
}
