using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Server
{
    /// <summary>
    /// Guards response lifecycle rules that must hold around the legacy Socket dispatcher:
    /// failed write acknowledgements remain standing/retryable, while handled response hashes
    /// are retained only for a bounded duplicate-suppression window.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    internal sealed class SocketResponseLifecycleGuard : MonoBehaviour
    {
        private const float HandledResponseRetentionSeconds = 120f;
        private const int MaxTrackedHandledResponses = 4096;

        private static SocketResponseLifecycleGuard _instance;
        private readonly List<byte[]> _messagesToReplay = new List<byte[]>();
        private readonly Dictionary<long, float> _handledAt = new Dictionary<long, float>();
        private readonly List<long> _hashesToRemove = new List<long>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (_instance != null)
            {
                return;
            }

            GameObject host = new GameObject("Socket Response Lifecycle Guard");
            DontDestroyOnLoad(host);
            _instance = host.AddComponent<SocketResponseLifecycleGuard>();
        }

        private void Update()
        {
            // Scene.Start establishes the socket manager and creates ConfigData.Socket. Avoid
            // touching the lazy Socket property before the normal scene bootstrap owns it.
            if (ConfigData.SocketManager == null)
            {
                return;
            }

            Socket socket = ConfigData.Socket;
            FilterFailedBasicWriteResponses(socket);
            PruneHandledResponses(socket);
        }

        private void FilterFailedBasicWriteResponses(Socket socket)
        {
            _messagesToReplay.Clear();

            while (socket.MessageQueue.TryDequeue(out byte[] bytes))
            {
                if (ShouldKeepWriteRequestPending(socket, bytes))
                {
                    continue;
                }
                _messagesToReplay.Add(bytes);
            }

            for (int i = 0; i < _messagesToReplay.Count; i++)
            {
                socket.MessageQueue.Enqueue(_messagesToReplay[i]);
            }
        }

        private static bool ShouldKeepWriteRequestPending(Socket socket, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return false;
            }

            ServerResponse response;
            try
            {
                string json = System.Text.Encoding.UTF8.GetString(bytes);
                response = JsonUtility.FromJson<ServerResponse>(json);
                if (response == null || string.IsNullOrEmpty(response.Type) ||
                    !Utilities.ConvertNameToRequestType.TryGetValue(response.Type, out ConfigData.RequestTypes requestType))
                {
                    return false;
                }
                response.RequestType = requestType;
            }
            catch
            {
                return false;
            }

            bool isBasicWrite = response.RequestType == ConfigData.RequestTypes.StoreCommands ||
                                response.RequestType == ConfigData.RequestTypes.StoreUserData ||
                                response.RequestType == ConfigData.RequestTypes.SendRLData;
            if (!isBasicWrite || response.Status == 200)
            {
                return false;
            }

            // BeesServer uses 409 specifically to retire StoreCommands outcomes whose reservation
            // disappeared across a server restart. Those outcomes cannot be made valid by retrying;
            // replaying the request forever would only amplify load. Let the normal basic response
            // handler retire the request, while keeping every other non-successful write retryable.
            if (response.RequestType == ConfigData.RequestTypes.StoreCommands && response.Status == 409)
            {
                Debug.LogWarning($"Server rejected stale StoreCommands request #{response.Hash}; retiring it instead of retrying indefinitely.");
                return false;
            }

            ServerRequest request = socket.GetStandingRequest(response.Hash);
            if (request != null)
            {
                // Do not pass this failure to Socket.HandleBasicResponse(), because that legacy
                // handler removes the standing request regardless of Status. Leaving it standing
                // lets the existing queue-timeout/resend policy retry the write.
                Debug.LogWarning($"Server rejected write request #{response.Hash}:{response.RequestType} with status {response.Status}; keeping it pending for retry.");
            }
            return true;
        }

        private void PruneHandledResponses(Socket socket)
        {
            float now = Time.realtimeSinceStartup;
            foreach (long hash in socket.HandledRequests)
            {
                if (!_handledAt.ContainsKey(hash))
                {
                    _handledAt.Add(hash, now);
                }
            }

            _hashesToRemove.Clear();
            foreach (KeyValuePair<long, float> entry in _handledAt)
            {
                if (!socket.HandledRequests.Contains(entry.Key) ||
                    now - entry.Value >= HandledResponseRetentionSeconds)
                {
                    _hashesToRemove.Add(entry.Key);
                }
            }

            // The time window is the normal bound. The hard cap protects extremely high-throughput
            // training sessions from retaining an unexpectedly large burst inside that window.
            if (_handledAt.Count - _hashesToRemove.Count > MaxTrackedHandledResponses)
            {
                foreach (KeyValuePair<long, float> entry in _handledAt)
                {
                    if (_hashesToRemove.Contains(entry.Key))
                    {
                        continue;
                    }
                    _hashesToRemove.Add(entry.Key);
                    if (_handledAt.Count - _hashesToRemove.Count <= MaxTrackedHandledResponses)
                    {
                        break;
                    }
                }
            }

            for (int i = 0; i < _hashesToRemove.Count; i++)
            {
                long hash = _hashesToRemove[i];
                socket.HandledRequests.Remove(hash);
                _handledAt.Remove(hash);
            }
        }
    }
}
