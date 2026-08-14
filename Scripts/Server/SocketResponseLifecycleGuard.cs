using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Server
{
    [DefaultExecutionOrder(-32000)]
    internal sealed class SocketResponseLifecycleGuard : MonoBehaviour
    {
        private const float HandledResponseRetentionSeconds = 120f;
        private const float HandledResponsePruneIntervalSeconds = 1f;
        private const int MaxTrackedHandledResponses = 4096;

        private static SocketResponseLifecycleGuard _instance;
        private readonly Dictionary<long, float> _handledAt = new Dictionary<long, float>();
        private readonly List<long> _hashesToRemove = new List<long>();
        private float _nextPruneAt;

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
            float now = Time.realtimeSinceStartup;
            if (now < _nextPruneAt && socket.HandledRequests.Count <= MaxTrackedHandledResponses) return;
            _nextPruneAt = now + HandledResponsePruneIntervalSeconds;
            PruneHandledResponses(socket, now);
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

        private static bool IsStaleSquadResponse(Socket socket, ServerResponse response)
        {
            if (response.RequestType != ConfigData.RequestTypes.GetStrategy &&
                response.RequestType != ConfigData.RequestTypes.GetMatchupStrategy)
            {
                return false;
            }

            // A live standing request still owns this response. The ordinary handler will consume
            // it and CanApplySquadResponse will make the final runtime-identity check.
            if (socket.GetStandingRequest(response.Hash) != null)
            {
                return false;
            }

            // RemoveSquad intentionally retires pending command/matchup requests when their squad
            // dies. The server can already have started processing one of those requests, so its
            // response may arrive after the standing request has been removed. Match the response
            // to the historical request and suppress it only when that captured squad lifecycle is
            // no longer current. Truly unknown hashes still reach Socket.Message and remain errors.
            foreach (ServerRequest request in ConfigData.__PastServerRequests)
            {
                if (request == null || request.Hash != response.Hash)
                {
                    continue;
                }

                if (request is CommandRequest commandRequest)
                {
                    return !commandRequest.HasSameSquad();
                }

                if (request is MatchupStrategyRequest matchupRequest)
                {
                    return !matchupRequest.HasSameSquad();
                }

                return false;
            }

            return false;
        }

        internal static bool TryParseResponse(byte[] bytes, out string json, out ServerResponse response)
        {
            json = null;
            response = null;
            if (bytes == null || bytes.Length == 0) return false;
            try
            {
                json = System.Text.Encoding.UTF8.GetString(bytes);
                response = JsonUtility.FromJson<ServerResponse>(json);
                if (response == null || string.IsNullOrEmpty(response.Type) ||
                    !Utilities.ConvertNameToRequestType.TryGetValue(response.Type, out ConfigData.RequestTypes requestType))
                {
                    json = null;
                    response = null;
                    return false;
                }
                response.RequestType = requestType;
                return true;
            }
            catch
            {
                json = null;
                response = null;
                return false;
            }
        }

        internal static bool ShouldSuppressResponse(Socket socket, byte[] bytes)
        {
            return TryParseResponse(bytes, out _, out ServerResponse response) && ShouldSuppressResponse(socket, response);
        }

        internal static bool ShouldSuppressResponse(Socket socket, ServerResponse response)
        {
            if (response == null) return false;

            if (IsStaleSquadResponse(socket, response))
            {
                // Mark the hash handled as well as suppressing this payload so duplicate or delayed
                // copies cannot later fall through into a command handler after the squad is gone.
                socket.HandledRequests.Add(response.Hash);
                return true;
            }

            if (response.Status == 401)
            {
                ServerRequest unauthorizedRequest = socket.GetStandingRequest(response.Hash);
                if (unauthorizedRequest != null)
                {
                    Debug.LogWarning($"Server rejected request #{response.Hash}:{response.RequestType} with status 401; refreshing Steam authentication before retry.");
                    SteamWebApiAuth.Refresh();
                }
                return true;
            }

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

        private void PruneHandledResponses(Socket socket, float now)
        {
            foreach (long hash in socket.HandledRequests)
            {
                if (!_handledAt.ContainsKey(hash)) _handledAt.Add(hash, now);
            }

            _hashesToRemove.Clear();
            foreach (KeyValuePair<long, float> entry in _handledAt)
            {
                if (!socket.HandledRequests.Contains(entry.Key) || now - entry.Value >= HandledResponseRetentionSeconds)
                {
                    _hashesToRemove.Add(entry.Key);
                }
            }

            RemoveTrackedHashes(socket);

            int excessCount = _handledAt.Count - MaxTrackedHandledResponses;
            if (excessCount <= 0)
            {
                return;
            }

            _hashesToRemove.Clear();
            foreach (KeyValuePair<long, float> entry in _handledAt)
            {
                _hashesToRemove.Add(entry.Key);
                excessCount--;
                if (excessCount == 0)
                {
                    break;
                }
            }

            RemoveTrackedHashes(socket);
        }

        private void RemoveTrackedHashes(Socket socket)
        {
            for (int i = 0; i < _hashesToRemove.Count; i++)
            {
                long hash = _hashesToRemove[i];
                socket.HandledRequests.Remove(hash);
                _handledAt.Remove(hash);
            }
        }
    }
}
