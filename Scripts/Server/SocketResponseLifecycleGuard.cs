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
            FilterFailedResponses(socket);
            PruneHandledResponses(socket);
        }

        private void FilterFailedResponses(Socket socket)
        {
            _messagesToReplay.Clear();
            while (socket.MessageQueue.TryDequeue(out byte[] bytes))
            {
                if (!ShouldSuppressResponse(socket, bytes)) _messagesToReplay.Add(bytes);
            }
            for (int i = 0; i < _messagesToReplay.Count; i++) socket.MessageQueue.Enqueue(_messagesToReplay[i]);
        }

        private static bool IsSuccessfulWriteStatus(int status)
        {
            return status == 1 || (status >= 200 && status < 300);
        }

        private static bool IsTypedPayloadResponse(ConfigData.RequestTypes requestType)
        {
            return requestType == ConfigData.RequestTypes.SetupLevel ||
                   requestType == ConfigData.RequestTypes.ReconnectLevel ||
                   requestType == ConfigData.RequestTypes.GetMatchupStrategy ||
                   requestType == ConfigData.RequestTypes.GetStrategy;
        }

        /// <summary>
        /// Filters failure acknowledgements before Socket.Message() can claim their hash and dispatch
        /// them by Type into success-only payload handlers. Returning true consumes the response.
        /// Retryable failures leave their standing request intact; terminal typed authorization
        /// failures retire only that request without applying any success state.
        /// </summary>
        private static bool ShouldSuppressResponse(Socket socket, byte[] bytes)
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

            // Missing user data is represented by the normal get-user-data payload with null
            // Filename/Contents and no error status. Authentication/authorization/database errors
            // instead carry an HTTP-like failure Status. Keep those responses away from
            // HandleUserDataResponse/HandleSettingsResponse so they can never enter missing-file
            // initialization; the standing request remains available to the normal resend path.
            bool isDataRead = response.RequestType == ConfigData.RequestTypes.GetUserData ||
                              response.RequestType == ConfigData.RequestTypes.GetSettings;
            if (isDataRead && response.Status >= 400)
            {
                if (socket.GetStandingRequest(response.Hash) != null)
                {
                    Debug.LogWarning($"Server rejected read request #{response.Hash}:{response.RequestType} with status {response.Status}; keeping it pending instead of treating it as missing data.");
                }
                return true;
            }

            // setup/reconnect/strategy failures are basic responses containing only Type/Hash/Status.
            // They must never reach the corresponding success parser: default payload values can mark
            // a Level connected with GameId 0 or feed null strategy names into command handlers.
            if (IsTypedPayloadResponse(response.RequestType) && response.Status >= 400)
            {
                ServerRequest standingRequest = socket.GetStandingRequest(response.Hash);
                if (response.Status == 403)
                {
                    if (standingRequest != null)
                    {
                        socket.StandingRequests.Remove(standingRequest);
                    }
                    Debug.LogWarning($"Server permanently rejected typed request #{response.Hash}:{response.RequestType} with status {response.Status}; retiring it without applying success state.");
                }
                else if (standingRequest != null)
                {
                    Debug.LogWarning($"Server rejected typed request #{response.Hash}:{response.RequestType} with status {response.Status}; keeping it pending for retry without dispatching the incomplete payload.");
                }
                return true;
            }

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
