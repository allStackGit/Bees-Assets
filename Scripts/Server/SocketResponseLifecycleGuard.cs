using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Server
{
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
            if (_instance != null) return;
            GameObject host = new GameObject("Socket Response Lifecycle Guard");
            DontDestroyOnLoad(host);
            _instance = host.AddComponent<SocketResponseLifecycleGuard>();
        }

        private void Update()
        {
            CampaignCheckpoint.FlushIfReady();
            if (ConfigData.SocketManager == null) return;
            Socket socket = ConfigData.Socket;
            FilterFailedBasicWriteResponses(socket);
            PruneHandledResponses(socket);
        }

        private void FilterFailedBasicWriteResponses(Socket socket)
        {
            _messagesToReplay.Clear();
            while (socket.MessageQueue.TryDequeue(out byte[] bytes))
            {
                if (!ShouldKeepWriteRequestPending(socket, bytes)) _messagesToReplay.Add(bytes);
            }
            for (int i = 0; i < _messagesToReplay.Count; i++) socket.MessageQueue.Enqueue(_messagesToReplay[i]);
        }

        private static bool IsSuccessfulWriteStatus(int status)
        {
            return status == 1 || (status >= 200 && status < 300);
        }

        private static bool ShouldKeepWriteRequestPending(Socket socket, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;

            ServerResponse response;
            try
            {
                string json = System.Text.Encoding.UTF8.GetString(bytes);
                response = JsonUtility.FromJson<ServerResponse>(json);
                if (response == null || string.IsNullOrEmpty(response.Type) ||
                    !Utilities.ConvertNameToRequestType.TryGetValue(response.Type, out ConfigData.RequestTypes requestType))
                    return false;
                response.RequestType = requestType;
            }
            catch { return false; }

            bool isBasicWrite = response.RequestType == ConfigData.RequestTypes.StoreCommands ||
                                response.RequestType == ConfigData.RequestTypes.StoreUserData ||
                                response.RequestType == ConfigData.RequestTypes.SendRLData;
            if (!isBasicWrite || IsSuccessfulWriteStatus(response.Status)) return false;

            bool terminalStoreCommands = response.RequestType == ConfigData.RequestTypes.StoreCommands && response.Status == 409;
            bool terminalAuthorization = response.Status == 403;
            if (terminalStoreCommands || terminalAuthorization)
            {
                Debug.LogWarning($"Server permanently rejected write request #{response.Hash}:{response.RequestType} with status {response.Status}; retiring it instead of retrying indefinitely.");
                return false;
            }

            ServerRequest request = socket.GetStandingRequest(response.Hash);
            if (request != null)
            {
                Debug.LogWarning($"Server rejected write request #{response.Hash}:{response.RequestType} with status {response.Status}; keeping it pending for retry.");
            }
            return true;
        }

        private void PruneHandledResponses(Socket socket)
        {
            float now = Time.realtimeSinceStartup;
            foreach (long hash in socket.HandledRequests)
            {
                if (!_handledAt.ContainsKey(hash)) _handledAt.Add(hash, now);
            }

            _hashesToRemove.Clear();
            foreach (KeyValuePair<long, float> entry in _handledAt)
            {
                if (!socket.HandledRequests.Contains(entry.Key) || now - entry.Value >= HandledResponseRetentionSeconds)
                    _hashesToRemove.Add(entry.Key);
            }

            if (_handledAt.Count - _hashesToRemove.Count > MaxTrackedHandledResponses)
            {
                foreach (KeyValuePair<long, float> entry in _handledAt)
                {
                    if (_hashesToRemove.Contains(entry.Key)) continue;
                    _hashesToRemove.Add(entry.Key);
                    if (_handledAt.Count - _hashesToRemove.Count <= MaxTrackedHandledResponses) break;
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
