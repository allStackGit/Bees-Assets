using UnityEngine;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Data;
using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;
using Assets.Scripts.Entities.Ships;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Assets.Scripts.Server
{
    public class Socket
    {
        private const int MaxMessagesPerUpdate = 128;
        private const int MaxMainThreadActionsPerUpdate = 64;

        private string _hostname = ConfigData.ProductionServerHostname;
        private int _port = ConfigData.ProductionPort;
        private string _websocketURL;
        private NativeWebSocket.WebSocket _nativeWebSocket = null;
        private WebSocketSharp.WebSocket _webSocketSharpSocket = null;
        private bool _useWebSocketSharp = false;
        private ConcurrentQueue<Action> _mainThreadActions;
        private ConcurrentQueue<SharpOutboundMessage> _sharpOutboundMessages;
        private int _sharpSendWorkerActive;
        private int _socketGeneration;
        private int _connectionAttemptInFlight;

        private sealed class SharpOutboundMessage
        {
            public WebSocketSharp.WebSocket Socket;
            public int Generation;
            public string Json;
        }

        public bool IsOpen;
        public bool IsSecured;
        public bool HasClosed;
        public bool KeepClosed;
        public string Protocol = "ws";
        public StandingRequestSet StandingRequests = new StandingRequestSet();
        public HashSet<long> HandledRequests = new HashSet<long>();
        public ConcurrentQueue<byte[]> MessageQueue = new ConcurrentQueue<byte[]>();
        public List<Level> OpenLevels = new List<Level>();
        private readonly ServerRequestSet _waitableRequests = new ServerRequestSet();
        private readonly List<ServerRequest> _waitableRequestSnapshot = new List<ServerRequest>();

        private ConcurrentQueue<Action> MainThreadActions
        {
            get
            {
                if (_mainThreadActions == null)
                {
                    Interlocked.CompareExchange(ref _mainThreadActions, new ConcurrentQueue<Action>(), null);
                }
                return _mainThreadActions;
            }
        }

        private ConcurrentQueue<SharpOutboundMessage> SharpOutboundMessages
        {
            get
            {
                if (_sharpOutboundMessages == null)
                {
                    Interlocked.CompareExchange(ref _sharpOutboundMessages, new ConcurrentQueue<SharpOutboundMessage>(), null);
                }
                return _sharpOutboundMessages;
            }
        }

        public Socket(int port, string hostname, bool useWebSocketSharp)
            : this(port, hostname, useWebSocketSharp, $"ws://{hostname}:{port}", secured: false)
        {
        }

        internal Socket(int port, string hostname, bool useWebSocketSharp, string websocketUrl, bool secured)
        {
            ConfigData.Stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _hostname = hostname;
            _port = port;
            _useWebSocketSharp = useWebSocketSharp;
            _websocketURL = websocketUrl;
            IsSecured = secured;
            Protocol = secured ? "wss" : "ws";
            MakeSocket();
        }

        public async void MakeSocket()
        {
            if (KeepClosed)
            {
                return;
            }

            if (_useWebSocketSharp)
            {
                if (Interlocked.CompareExchange(ref _connectionAttemptInFlight, 1, 0) != 0)
                {
                    return;
                }

                int generation = Interlocked.Increment(ref _socketGeneration);
                WebSocketSharp.WebSocket socket = new WebSocketSharp.WebSocket(_websocketURL, "game");
                _webSocketSharpSocket = socket;
                if (IsSecured)
                {
                    socket.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;
                }

                socket.OnOpen += (sender, e) =>
                {
                    EnqueueMainThread(generation, socket, () =>
                    {
                        Interlocked.Exchange(ref _connectionAttemptInFlight, 0);
                        Open();
                    });
                };

                socket.OnError += (sender, e) =>
                {
                    EnqueueMainThread(generation, socket, () =>
                    {
                        Interlocked.Exchange(ref _connectionAttemptInFlight, 0);
                        Error(e.Message);
                    });
                };

                socket.OnClose += (sender, e) =>
                {
                    EnqueueMainThread(generation, socket, () =>
                    {
                        Interlocked.Exchange(ref _connectionAttemptInFlight, 0);
                        Close(e.Reason);
                    });
                };

                socket.OnMessage += (sender, e) =>
                {
                    if (!IsCurrentSharpSocket(generation, socket))
                    {
                        return;
                    }
                    if (e == null)
                    {
                        MainThreadActions.Enqueue(() => Debug.LogWarning("Received null message from server"));
                    }
                    else if (e.RawData == null || e.RawData.Length == 0)
                    {
                        MainThreadActions.Enqueue(() => Debug.LogWarning("Received empty/null raw data from server"));
                    }
                    else
                    {
                        MessageQueue.Enqueue(e.RawData.ToArray());
                    }
                };

                socket.ConnectAsync();
            }
            else
            {
                if (Interlocked.CompareExchange(ref _connectionAttemptInFlight, 1, 0) != 0)
                {
                    return;
                }

                int generation = Interlocked.Increment(ref _socketGeneration);
                NativeWebSocket.WebSocket socket = new NativeWebSocket.WebSocket(_websocketURL, "game");
                _nativeWebSocket = socket;
                socket.OnOpen += () => EnqueueNativeMainThread(generation, socket, () =>
                {
                    Interlocked.Exchange(ref _connectionAttemptInFlight, 0);
                    Open();
                });
                socket.OnError += error => EnqueueNativeMainThread(generation, socket, () =>
                {
                    Interlocked.Exchange(ref _connectionAttemptInFlight, 0);
                    Error(error);
                });
                socket.OnClose += e => EnqueueNativeMainThread(generation, socket, () =>
                {
                    Interlocked.Exchange(ref _connectionAttemptInFlight, 0);
                    Close();
                });
                socket.OnMessage += bytes =>
                {
                    if (IsCurrentNativeSocket(generation, socket))
                    {
                        MessageQueue.Enqueue(bytes);
                    }
                };

                try
                {
                    await socket.Connect();
                }
                catch (Exception error)
                {
                    EnqueueNativeMainThread(generation, socket, () =>
                    {
                        Interlocked.Exchange(ref _connectionAttemptInFlight, 0);
                        Error(error.Message);
                    });
                }
            }
        }

        private bool IsCurrentSharpSocket(int generation, WebSocketSharp.WebSocket socket)
        {
            return generation == Volatile.Read(ref _socketGeneration) &&
                   ReferenceEquals(socket, _webSocketSharpSocket);
        }

        private bool IsCurrentNativeSocket(int generation, NativeWebSocket.WebSocket socket)
        {
            return generation == Volatile.Read(ref _socketGeneration) &&
                   ReferenceEquals(socket, _nativeWebSocket);
        }

        private void EnqueueNativeMainThread(
            int generation,
            NativeWebSocket.WebSocket socket,
            Action action)
        {
            MainThreadActions.Enqueue(() =>
            {
                if (IsCurrentNativeSocket(generation, socket))
                {
                    action();
                }
            });
        }

        private void EnqueueMainThread(int generation, WebSocketSharp.WebSocket socket, Action action)
        {
            MainThreadActions.Enqueue(() =>
            {
                if (IsCurrentSharpSocket(generation, socket))
                {
                    action();
                }
            });
        }

        private void Open()
        {
            IsOpen = true;
            if (HasClosed)
            {
                HasClosed = false;
                OpenLevels.ForEach((level) =>
                {
                    ConfigData.Socket.SendRequest(new ReconnectLevelRequest(
                        new SetupLevel(
                            ConfigData.CurrentGameMode == ConfigData.GameModes.FreePlay || ConfigData.CurrentGameMode == ConfigData.GameModes.FishTank
                                ? -1
                                : ConfigData.UserProgressData.GetCurrentLevel(ConfigData.Configuration.UserSide),
                            ConfigData.GetUserId(),
                            level.ServerGameId),
                        ConfigData.StandardMaxTimeOnQueue,
                        level));
                });
            }
            else
            {
            }
        }

        private void Error(string e)
        {
            Debug.LogWarning("Socket Error! " + e);
        }

        private void Close(string reason = null)
        {
            Debug.LogWarning("Connection closed!");
            if (reason != null)
            {
                Debug.LogWarning($"Network Error:{reason}");
            }
            OpenLevels.ForEach((level) =>
            {
                level.IsLevelConnectedToServer = false;
            });
            IsOpen = false;
            HasClosed = true;
        }

        private List<ServerRequest> _standingRequests;

        private List<ServerRequest> SnapshotStandingRequests()
        {
            if (_standingRequests == null)
            {
                _standingRequests = new List<ServerRequest>();
            }
            _standingRequests.Clear();
            _standingRequests.AddRange(StandingRequests);
            return _standingRequests;
        }

        private void MarkStrandedRequestsForResending()
        {
            List<ServerRequest> strandedRequests = SnapshotStandingRequests();
            for (int i = 0; i < strandedRequests.Count; i++)
            {
                ServerRequest sr = strandedRequests[i];
                StandingRequests.Remove(sr);
                Debug.LogWarning($"Resending #{sr.Hash}");
                SendRequest(sr, true);
            }
        }

        private string _f_message;
        private ServerResponse _message_response;
        private ServerRequest _message_request;

        private void Message(string message, ServerResponse response)
        {
            _f_message = message;
            _message_response = response;

            if (TryClaimResponse(_message_response.Hash, _message_response.RequestType))
            {
                switch (_message_response.RequestType)
                {
                    case ConfigData.RequestTypes.GetMatchupStrategy:
                        HandleMatchupResponse(_f_message);
                        return;
                    case ConfigData.RequestTypes.GetStrategy:
                        HandleStrategicCommandResponse(_f_message);
                        return;
                    case ConfigData.RequestTypes.SendRLData:
                    case ConfigData.RequestTypes.StoreCommands:
                    case ConfigData.RequestTypes.StoreUserData:
                        HandleBasicResponse(_message_response);
                        return;
                    case ConfigData.RequestTypes.SetupLevel:
                        HandleSetupLevelResponse(_f_message);
                        return;
                    case ConfigData.RequestTypes.ReconnectLevel:
                        HandleReconnectLevelResponse(_f_message);
                        return;
                    case ConfigData.RequestTypes.GetUserData:
                        HandleUserDataResponse(_f_message);
                        return;
                    case ConfigData.RequestTypes.GetSettings:
                        HandleSettingsResponse(_f_message);
                        return;
                    default:
                        Debug.LogError($"No response type from {_message_response}");
                        return;
                }
            }

            if (HandledRequests.Contains(_message_response.Hash))
            {
                Debug.LogWarning($"Got a duplicate response for #{_message_response.Hash} Status: {_message_response.Status}");
                return;
            }

            ServerRequest standingRequest = GetStandingRequest(_message_response.Hash);
            if (standingRequest == null)
            {
                Debug.LogWarning($"There was no standing request for response #{_message_response.Hash}:{_message_response.RequestType}");
            }
            else
            {
                Debug.LogWarning(
                    $"Ignoring response #{_message_response.Hash}:{_message_response.RequestType}; " +
                    $"the standing request expects {standingRequest.Type}.");
            }
        }

        public void Send(object content)
        {
            string json = JsonConvert.SerializeObject(content);
            if (_useWebSocketSharp)
            {
                WebSocketSharp.WebSocket socket = _webSocketSharpSocket;
                int generation = Volatile.Read(ref _socketGeneration);
                if (socket == null || !IsOpen || socket.ReadyState != WebSocketSharp.WebSocketState.Open)
                {
                    Debug.LogWarning("Deferring server request because the WebSocket is not open.");
                    return;
                }

                SharpOutboundMessages.Enqueue(new SharpOutboundMessage
                {
                    Socket = socket,
                    Generation = generation,
                    Json = json,
                });
                StartSharpSendWorker();
            }
            else
            {
                NativeWebSocket.WebSocket socket = _nativeWebSocket;
                int generation = Volatile.Read(ref _socketGeneration);
                if (socket == null || !IsOpen || socket.State != NativeWebSocket.WebSocketState.Open)
                {
                    Debug.LogWarning("Deferring server request because the WebSocket is not open.");
                    return;
                }

                SendNativeMessage(socket, generation, json);
            }
        }

        private async void SendNativeMessage(
            NativeWebSocket.WebSocket socket,
            int generation,
            string json)
        {
            try
            {
                await socket.SendText(json);
            }
            catch (Exception exception)
            {
                EnqueueNativeMainThread(generation, socket, () => Error(exception.Message));
            }
        }

        private void StartSharpSendWorker()
        {
            if (Interlocked.CompareExchange(ref _sharpSendWorkerActive, 1, 0) != 0)
            {
                return;
            }
            ThreadPool.QueueUserWorkItem(_ => DrainSharpSendQueue());
        }

        private void DrainSharpSendQueue()
        {
            try
            {
                while (SharpOutboundMessages.TryDequeue(out SharpOutboundMessage message))
                {
                    if (!IsCurrentSharpSocket(message.Generation, message.Socket) ||
                        message.Socket.ReadyState != WebSocketSharp.WebSocketState.Open)
                    {
                        continue;
                    }

                    try
                    {
                        message.Socket.Send(message.Json);
                    }
                    catch (Exception exception)
                    {
                        EnqueueMainThread(message.Generation, message.Socket, () =>
                        {
                            Error($"WebSocket send failed: {exception.Message}");
                            Close("WebSocket send failed");
                        });
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _sharpSendWorkerActive, 0);
                if (!SharpOutboundMessages.IsEmpty)
                {
                    StartSharpSendWorker();
                }
            }
        }

        private byte[] _update_message;
        private string _update_parsedMessage;
        private ServerResponse _update_response;

        public void Update()
        {
            int actionsProcessed = 0;
            while (actionsProcessed < MaxMainThreadActionsPerUpdate &&
                   MainThreadActions.TryDequeue(out Action action))
            {
                action();
                actionsProcessed++;
            }

            int messagesProcessed = 0;
            while (messagesProcessed < MaxMessagesPerUpdate && MessageQueue.TryDequeue(out _update_message))
            {
                if (SocketResponseLifecycleGuard.TryParseResponse(_update_message, out _update_parsedMessage, out _update_response))
                {
                    if (!SocketResponseLifecycleGuard.ShouldSuppressResponse(this, _update_response))
                    {
                        Message(_update_parsedMessage, _update_response);
                    }
                }
                else
                {
                    Debug.LogWarning("Ignoring malformed or unsupported server response.");
                }
                messagesProcessed++;
            }

            CheckStandingRequests();
        }

        private ServerRequest _sr;
        private int _index;
        private int _resends;

        public void CheckForResends()
        {
            if (ConfigData.Production && !SteamWebApiAuth.IsReady)
            {
                return;
            }

            _resends = 0;
            List<ServerRequest> standingRequests = SnapshotStandingRequests();
            for (_index = 0; _index < standingRequests.Count; _index++)
            {
                _sr = standingRequests[_index];
                if (_sr.HasExceededQueueTimeout(ConfigData.Stopwatch.ElapsedMilliseconds))
                {
                    StandingRequests.Remove(_sr);
                    Debug.LogWarning($"Resending #{_sr.Hash}:{_sr.Type} because it's been waiting for more than {_sr.MaxTimeOnQueue}s");
                    SendRequest(_sr, true);
                    _resends++;
                }
            }
            if (_resends > 0)
            {
                Debug.LogWarning($"Resending {_resends} timed-out requests");
            }
        }

        public void RefreshAuthenticationTickets(string ticket)
        {
            if (string.IsNullOrEmpty(ticket))
            {
                return;
            }

            foreach (ServerRequest request in StandingRequests)
            {
                TryRefreshAuthenticationTicket(request, ticket);
            }
        }

        public void ResendStandingRequestsAfterAuthenticationRefresh()
        {
            if (!IsOpen || !SteamWebApiAuth.IsReady)
            {
                return;
            }

            List<ServerRequest> standingRequests = SnapshotStandingRequests();
            for (int i = 0; i < standingRequests.Count; i++)
            {
                ServerRequest request = standingRequests[i];
                if (!HasRefreshableAuthenticationPayload(request))
                {
                    continue;
                }
                StandingRequests.Remove(request);
                SendRequest(request, true);
            }
        }

        private static bool HasRefreshableAuthenticationPayload(ServerRequest request)
        {
            return request != null &&
                   (request.Type == ConfigData.RequestTypes.SetupLevel ||
                    request.Type == ConfigData.RequestTypes.ReconnectLevel ||
                    request.Type == ConfigData.RequestTypes.StoreUserData ||
                    request.Type == ConfigData.RequestTypes.GetUserData ||
                    request.Type == ConfigData.RequestTypes.GetSettings);
        }

        private static bool TryRefreshAuthenticationTicket(ServerRequest request, string ticket)
        {
            if (!HasRefreshableAuthenticationPayload(request))
            {
                return false;
            }

            switch (request.Type)
            {
                case ConfigData.RequestTypes.SetupLevel:
                    ((SetupLevelRequest)request).Request.AuthTicket = ticket;
                    return true;
                case ConfigData.RequestTypes.ReconnectLevel:
                    ((ReconnectLevelRequest)request).Request.AuthTicket = ticket;
                    return true;
                case ConfigData.RequestTypes.StoreUserData:
                    ((StoreUserDataRequest)request).Request.AuthTicket = ticket;
                    return true;
                case ConfigData.RequestTypes.GetUserData:
                    ((DataFileRequest)request).Request.AuthTicket = ticket;
                    return true;
                case ConfigData.RequestTypes.GetSettings:
                    ((SettingsRequest)request).Request.AuthTicket = ticket;
                    return true;
                default:
                    return false;
            }
        }

        public void SendRequest(MatchupStrategyRequest serverRequest)
        {
            LogRequest(serverRequest);
            Send(serverRequest.Request);
        }
        public void SendRequest(CommandRequest serverRequest)
        {
            LogRequest(serverRequest);
            Send(serverRequest.Request);
        }
        public void SendRequest(StoreCommandsRequest serverRequest)
        {
            LogRequest(serverRequest);
            Send(serverRequest.Request);
        }
        public void SendRequest(SetupLevelRequest serverRequest)
        {
            LogRequest(serverRequest);
            Send(serverRequest.Request);
        }
        public void SendRequest(ReconnectLevelRequest serverRequest)
        {
            LogRequest(serverRequest);
            Send(serverRequest.Request);
        }
        public void SendRequest(StoreUserDataRequest serverRequest)
        {
            LogRequest(serverRequest);
            Send(serverRequest.Request);
        }
        public void SendRequest(DataFileRequest serverRequest)
        {
            LogRequest(serverRequest);
            Send(serverRequest.Request);
        }
        public void SendRequest(SettingsRequest serverRequest)
        {
            LogRequest(serverRequest);
            Send(serverRequest.Request);
        }

        public void SendRequest(ServerRequest serverRequest, bool isResendRequest)
        {
            LogRequest(serverRequest, isResendRequest);
            switch (serverRequest.Type)
            {
                case ConfigData.RequestTypes.GetMatchupStrategy:
                    Send(((MatchupStrategyRequest)serverRequest).Request);
                    return;
                case ConfigData.RequestTypes.GetStrategy:
                    Send(((CommandRequest)serverRequest).Request);
                    return;
                case ConfigData.RequestTypes.StoreCommands:
                    Send(((StoreCommandsRequest)serverRequest).Request);
                    return;
                case ConfigData.RequestTypes.SetupLevel:
                    Send(((SetupLevelRequest)serverRequest).Request);
                    return;
                case ConfigData.RequestTypes.ReconnectLevel:
                    Send(((ReconnectLevelRequest)serverRequest).Request);
                    return;
                case ConfigData.RequestTypes.StoreUserData:
                    Send(((StoreUserDataRequest)serverRequest).Request);
                    return;
                case ConfigData.RequestTypes.GetUserData:
                    Send(((DataFileRequest)serverRequest).Request);
                    return;
                case ConfigData.RequestTypes.GetSettings:
                    Send(((SettingsRequest)serverRequest).Request);
                    return;
                default:
                    Debug.LogError($"No request type from {serverRequest}");
                    return;
            }
        }

        public void LogRequest(ServerRequest request, bool isResendRequest = false)
        {
            StandingRequests.Add(request);
            if (request.Type == ConfigData.RequestTypes.GetUserData ||
                request.Type == ConfigData.RequestTypes.GetSettings)
            {
                _waitableRequests.Add(request);
            }
            if (ConfigData.SocketManager != null && ConfigData.SocketManager.WatchServerRequests)
            {
                ConfigData.RequestHistory.Add(request);
            }
            else if (request is CommandRequest || request is MatchupStrategyRequest)
            {
                ConfigData.RequestHistory.AddBounded(
                    request,
                    SocketResponseLifecycleGuard.MaxTrackedStaleSquadRequests);
            }
            if (isResendRequest)
            {
                request.StartTime = ConfigData.Stopwatch.ElapsedMilliseconds;
                request.Resends++;
                if (request.Resends > 10)
                {
                    Debug.LogWarning($"Request #{request.Hash} has been resent more than 10 times. This is probably a bug or a really long server delay.");
                }
            }
        }

        private void CheckStandingRequests()
        {
            if (_waitableRequests.Count == 0)
            {
                return;
            }

            _waitableRequestSnapshot.Clear();
            _waitableRequestSnapshot.AddRange(_waitableRequests);
            for (int i = 0; i < _waitableRequestSnapshot.Count; i++)
            {
                ServerRequest request = _waitableRequestSnapshot[i];
                if (!StandingRequests.Contains(request))
                {
                    _waitableRequests.Remove(request);
                    continue;
                }

                if (request is DataFileRequest dataFileRequest)
                {
                    dataFileRequest.DataFile.WaitForResponse();
                }
                else if (request is SettingsRequest settingsRequest)
                {
                    settingsRequest.Settings.WaitForResponse();
                }

                if (!StandingRequests.Contains(request))
                {
                    _waitableRequests.Remove(request);
                }
            }
        }

        public ServerRequest GetStandingRequest(long hash)
        {
            return StandingRequests.TryGetByHash(hash, out ServerRequest request) ? request : null;
        }

        private bool TryClaimResponse(long hash, ConfigData.RequestTypes expectedType)
        {
            ServerRequest request = GetStandingRequest(hash);
            if (request == null || request.Type != expectedType)
            {
                return false;
            }

            return HandledRequests.Add(hash);
        }

        private ServerRequest TakeStandingRequest(long hash, ConfigData.RequestTypes expectedType)
        {
            ServerRequest request = GetStandingRequest(hash);
            if (request == null || request.Type != expectedType)
            {
                return null;
            }
            StandingRequests.Remove(request);
            return request;
        }

        private static bool CanApplySquadResponse(Level level, Squad squad, int expectedItemId)
        {
            return level != null &&
                level.State != null &&
                !level.State.LevelEnded &&
                squad != null &&
                squad.ItemId == expectedItemId &&
                !squad.IsDead;
        }

        private UserDataResponse _userDataResponse;
        private DataFileRequest _standingRequest;
        private string _dataFilename;
        private FleetData _fleetData;
        private SavedSquadsData _savedSquadsData;
        private LevelData _levelData;
        private UserProgressData _userProgressData;
        private UserSettingsData _userSettingsData;

        private void HandleUserDataResponse(string message)
        {
            _userDataResponse = JsonUtility.FromJson<UserDataResponse>(message);
            _standingRequest = (DataFileRequest)GetStandingRequest(_userDataResponse.Hash);
            if (_standingRequest != null)
            {
                if (!string.IsNullOrEmpty(_userDataResponse.Filename) && !string.IsNullOrEmpty(_userDataResponse.Contents))
                {
                    _standingRequest.Response = _userDataResponse;
                    _standingRequest.Status = 1;
                }
                else
                {
                    _dataFilename = _standingRequest.Request.DataFile;
                    if (_dataFilename == ConfigData.FleetDataFilenames[0])
                    {
                        _fleetData = ConfigData.GetFleetData();
                        _fleetData.GetDataFile().WriteData(_fleetData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.FleetDataFilenames[1])
                    {
                        _fleetData = ConfigData.GetCampaignFleetData();
                        _fleetData.GetDataFile().WriteData(_fleetData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.FleetDataFilenames[2])
                    {
                        _fleetData = ConfigData.GetChallengeFleetData();
                        _fleetData.GetDataFile().WriteData(_fleetData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.SavedSquadsDataFilenames[0])
                    {
                        _savedSquadsData = ConfigData.GetSavedSquadsData();
                        _savedSquadsData.GetDataFile().WriteData(_savedSquadsData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.SavedSquadsDataFilenames[1])
                    {
                        _savedSquadsData = ConfigData.GetCampaignSavedSquadsData();
                        _savedSquadsData.GetDataFile().WriteData(_savedSquadsData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.SavedSquadsDataFilenames[2])
                    {
                        _savedSquadsData = ConfigData.GetChallengeSavedSquadsData();
                        _savedSquadsData.GetDataFile().WriteData(_savedSquadsData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.LevelsDataFilenames[0])
                    {
                        _levelData = ConfigData.GetLevelData();
                        _levelData.GetDataFile().WriteData(_levelData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.LevelsDataFilenames[1])
                    {
                        _levelData = ConfigData.GetCampaignLevelData();
                        _levelData.GetDataFile().WriteData(_levelData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.LevelsDataFilenames[2])
                    {
                        _levelData = ConfigData.GetChallengeLevelData();
                        _levelData.GetDataFile().WriteData(_levelData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.UserProgressFilename)
                    {
                        _userProgressData = ConfigData.UserProgressData;
                        _userProgressData.GetDataFile().WriteData(_userProgressData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.UserSettingsFilename)
                    {
                        _userSettingsData = ConfigData.GetUserSettingsData();
                        _userSettingsData.GetDataFile().WriteData(_userSettingsData.GetDefaultJson());
                    }
                    _standingRequest.Status = -1;
                }
            }
            else
            {
                Debug.LogError($"Couldn't find a matching request for {_userDataResponse.Hash}");
            }
        }

        private UserDataResponse _settingsResponse_userData;
        private SettingsRequest _settingsResponse_standingRequest;

        private void HandleSettingsResponse(string message)
        {
            _settingsResponse_userData = JsonUtility.FromJson<UserDataResponse>(message);
            _settingsResponse_standingRequest = (SettingsRequest)GetStandingRequest(_settingsResponse_userData.Hash);
            if (_settingsResponse_standingRequest != null)
            {
                if (!string.IsNullOrEmpty(_settingsResponse_userData.Filename) &&
                    !string.IsNullOrEmpty(_settingsResponse_userData.Contents))
                {
                    _settingsResponse_standingRequest.Status = 1;
                    _settingsResponse_standingRequest.Response = _settingsResponse_userData;
                }
                else
                {
                    Debug.LogError($"Null response when requesting settings from the server. {_settingsResponse_userData}");
                }
            }
            else
            {
                Debug.LogError($"Couldn't find a matching request for {_settingsResponse_userData.Hash}");
            }
        }

        private MatchupStrategyResponse _handleMatchupResponse_matchupResponse;
        private MatchupStrategyRequest _handleMatchupResponse_standingRequest;
        private Squad _handleMatchupResponse_squad;
        private Squad _handleMatchupResponse_targetSquad;
        private Level _handleMatchupResponse_level;

        private void HandleMatchupResponse(string message)
        {
            _handleMatchupResponse_matchupResponse = JsonUtility.FromJson<MatchupStrategyResponse>(message);
            _handleMatchupResponse_standingRequest = TakeStandingRequest(
                _handleMatchupResponse_matchupResponse.Hash,
                ConfigData.RequestTypes.GetMatchupStrategy) as MatchupStrategyRequest;

            if (_handleMatchupResponse_standingRequest != null)
            {
                _handleMatchupResponse_squad = _handleMatchupResponse_standingRequest.Squad;
                _handleMatchupResponse_level = _handleMatchupResponse_standingRequest.Level;
                _handleMatchupResponse_level.RecordSimulationInput("hivemind-matchup-response", message);
                if (CanApplySquadResponse(
                    _handleMatchupResponse_level,
                    _handleMatchupResponse_squad,
                    _handleMatchupResponse_standingRequest.SquadId))
                {
                    ConfigData.MatchupStrategyTypes matchupType;
                    long outcomeId = _handleMatchupResponse_matchupResponse.OutcomeId;
                    if (!Utilities.ConvertMatchupStrategyNameToType.TryGetValue(
                        _handleMatchupResponse_matchupResponse.Name ?? string.Empty,
                        out matchupType))
                    {
                        Debug.LogError(
                            $"Received unknown matchup strategy '{_handleMatchupResponse_matchupResponse.Name ?? "<null>"}'; using Random.");
                        matchupType = ConfigData.MatchupStrategyTypes.Random;
                        // The unknown server outcome cannot be credited to the fallback strategy.
                        outcomeId = 0;
                    }

                    _handleMatchupResponse_squad.MatchupStrategy.Setup(
                        matchupType,
                        outcomeId,
                        _handleMatchupResponse_squad);
                    _handleMatchupResponse_targetSquad = _handleMatchupResponse_squad.MatchupStrategy.SortSquads();
                    _handleMatchupResponse_level.HandledRequests.Add(_handleMatchupResponse_standingRequest.Hash);
                    _handleMatchupResponse_squad.MakeMatchupAndGetCommand(_handleMatchupResponse_targetSquad);
                }
            }
            else
            {
                Debug.LogError($"Couldn't find a matching request for {_handleMatchupResponse_matchupResponse.Hash}");
            }
        }

        private CommandResponse _commandResponse;
        private CommandRequest _strategicStandingRequest;
        private Squad _tempSquad;
        private Level _handleStrategicCommandResponse_level;
        private ConfigData.CommandTypes _tempCommandType;
        private Command _handleStrategicCommandResponse_command;
        private WarpGate _handleStrategicCommandResponse_warpGate;
        private List<Beehive> _beehives;
        private List<float> _beehiveDistances;
        private Vector2 _handleStrategicCommandResponse_position;

        private void RejectStrategicCommandResponse(string reason)
        {
            Debug.LogError(
                $"{_tempSquad?.Name ?? "Squad"} received invalid strategic command response: {reason}.");

            if (_strategicStandingRequest == null)
            {
                return;
            }

            _handleStrategicCommandResponse_level?.HandledRequests.Add(
                _strategicStandingRequest.Hash);
            if (_tempSquad != null && !_tempSquad.IsDead)
            {
                _tempSquad.MakeMatchupAndGetCommand(_strategicStandingRequest.Enemy);
            }
        }

        private void HandleStrategicCommandResponse(string message)
        {
            _commandResponse = JsonUtility.FromJson<CommandResponse>(message);
            if (_commandResponse == null)
            {
                Debug.LogError("Received an invalid strategic command response.");
                return;
            }
            _strategicStandingRequest = TakeStandingRequest(
                _commandResponse.Hash,
                ConfigData.RequestTypes.GetStrategy) as CommandRequest;

            if (_strategicStandingRequest != null)
            {
                _tempSquad = _strategicStandingRequest.Squad;
                _handleStrategicCommandResponse_level = _strategicStandingRequest.Level;
                _handleStrategicCommandResponse_level.RecordSimulationInput("hivemind-command-response", message);
                if (CanApplySquadResponse(
                    _handleStrategicCommandResponse_level,
                    _tempSquad,
                    _strategicStandingRequest.SquadId))
                {
                    if (string.IsNullOrWhiteSpace(_commandResponse.Name) ||
                        !Utilities.ConvertCommandNameToType.TryGetValue(
                            _commandResponse.Name,
                            out _tempCommandType))
                    {
                        RejectStrategicCommandResponse(
                            $"unknown command name {_commandResponse.Name ?? "<null>"}");
                        return;
                    }

                    ConfigData.CommandTypes effectiveCommandType = _tempCommandType;
                    bool commandIsAdaptedForComposition =
                        _tempCommandType == ConfigData.CommandTypes.Aggressive ||
                        _tempCommandType == ConfigData.CommandTypes.CircleSquad ||
                        _tempCommandType == ConfigData.CommandTypes.RightSwipe ||
                        _tempCommandType == ConfigData.CommandTypes.LeftSwipe ||
                        _tempCommandType == ConfigData.CommandTypes.InAndOut;
                    if (commandIsAdaptedForComposition && _tempSquad.HasOnlyBombers)
                    {
                        effectiveCommandType = ConfigData.CommandTypes.BombingRun;
                    }
                    else if (commandIsAdaptedForComposition && _tempSquad.HasOnlyBarges)
                    {
                        effectiveCommandType = ConfigData.CommandTypes.Charge;
                    }

                    if (_strategicStandingRequest.Request.BannedStrats.Contains(_commandResponse.Name) ||
                        (Utilities.ConvertCommandTypeToName.TryGetValue(
                            effectiveCommandType,
                            out string effectiveCommandName) &&
                         _strategicStandingRequest.Request.BannedStrats.Contains(effectiveCommandName)))
                    {
                        RejectStrategicCommandResponse(
                            $"banned command type {_commandResponse.Name}");
                        return;
                    }

                    ConfigData.ShootingStrategyTypes responseShootingStrategy =
                        ConfigData.ShootingStrategyTypes.FirstSeen;
                    if (_tempCommandType != ConfigData.CommandTypes.Retreat &&
                        !Utilities.ConvertShootingStrategyNameToType.TryGetValue(
                            _commandResponse.ShootingStrategyName ?? string.Empty,
                            out responseShootingStrategy))
                    {
                        RejectStrategicCommandResponse(
                            $"unknown shooting strategy {_commandResponse.ShootingStrategyName ?? "<null>"}");
                        return;
                    }

                    _handleStrategicCommandResponse_level.HandledRequests.Add(_strategicStandingRequest.Hash);
                    if (_tempSquad.IsDead)
                    {
                        Debug.LogError($"Squad {_tempSquad} is dead, but received a command response.");
                    }

                    _handleStrategicCommandResponse_command = null;
                    switch (_tempCommandType)
                    {
                        case ConfigData.CommandTypes.BombingRun:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun);
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Charge:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Charge);
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Aggressive:
                            if (_tempSquad.HasOnlyBombers)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun);
                                _tempCommandType = ConfigData.CommandTypes.BombingRun;
                            }
                            else if (_tempSquad.HasOnlyBarges)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Charge);
                                _tempCommandType = ConfigData.CommandTypes.Charge;
                            }
                            else
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Aggressive);
                            }
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Retreat:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Retreat);
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.MoveToRandom:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.MoveToRandom);
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.CircleSquad:
                            if (_tempSquad.HasOnlyBombers)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun);
                                _tempCommandType = ConfigData.CommandTypes.BombingRun;
                            }
                            else if (_tempSquad.HasOnlyBarges)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Charge);
                                _tempCommandType = ConfigData.CommandTypes.Charge;
                            }
                            else
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.CircleSquad);
                            }
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.RightSwipe:
                        case ConfigData.CommandTypes.LeftSwipe:
                            if (_tempSquad.HasOnlyBombers)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun);
                                _tempCommandType = ConfigData.CommandTypes.BombingRun;
                            }
                            else if (_tempSquad.HasOnlyBarges)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Charge);
                                _tempCommandType = ConfigData.CommandTypes.Charge;
                            }
                            else
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(_tempCommandType);
                            }
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.ClosestFriendly:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.ClosestFriendly);
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.InAndOut:
                            if (_tempSquad.HasOnlyBombers)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun);
                                _tempCommandType = ConfigData.CommandTypes.BombingRun;
                            }
                            else if (_tempSquad.HasOnlyBarges)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Charge);
                                _tempCommandType = ConfigData.CommandTypes.Charge;
                            }
                            else
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.InAndOut);
                            }
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Patrol:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Patrol);
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Guard:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Guard);
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Scouting:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Scouting);
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Mining:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Mining);
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.FullRetreat:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.FullRetreat);
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Hold:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Hold);
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Heal:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Heal);
                            _handleStrategicCommandResponse_command.Setup(_tempSquad, true, _strategicStandingRequest.Enemy, _strategicStandingRequest.Matchup);
                            break;
                        default:
                            Debug.LogError($"commandResponse doesn't match a known command: {_commandResponse.Name}");
                            break;
                    }

                    if (_handleStrategicCommandResponse_command == null)
                    {
                        RejectStrategicCommandResponse(
                            $"unsupported command type {_tempCommandType}");
                        return;
                    }

                    _tempSquad.SetCommand(_handleStrategicCommandResponse_command);
                    if (!_handleStrategicCommandResponse_level.Stage.IsTraining)
                    {
                    }

                    if (_tempCommandType == ConfigData.CommandTypes.Aggressive)
                    {
                        ((Aggressive)_tempSquad.GetCommand()).Execute(responseShootingStrategy, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.BombingRun)
                    {
                        ((BombingRun)_tempSquad.GetCommand()).Execute(responseShootingStrategy, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Charge)
                    {
                        ((Charge)_tempSquad.GetCommand()).Execute(responseShootingStrategy, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.CircleSquad)
                    {
                        ((CircleSquad)_tempSquad.GetCommand()).Execute(responseShootingStrategy, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.InAndOut)
                    {
                        ((InAndOut)_tempSquad.GetCommand()).Execute(responseShootingStrategy, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Retreat)
                    {
                        ((Retreat)_tempSquad.GetCommand()).Execute(ConfigData.ShootingStrategyTypes.FirstSeen, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.LeftSwipe || _tempCommandType == ConfigData.CommandTypes.RightSwipe)
                    {
                        ((SwipeSquad)_tempSquad.GetCommand()).Execute(_tempCommandType, responseShootingStrategy, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Patrol)
                    {
                        ((Patrol)_tempSquad.GetCommand()).Execute(responseShootingStrategy, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId, Vector2.zero, Vector2.zero);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Guard)
                    {
                        ((Guard)_tempSquad.GetCommand()).Execute(responseShootingStrategy, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId, null);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.ClosestFriendly)
                    {
                        ((ClosestFriendly)_tempSquad.GetCommand()).Execute(responseShootingStrategy, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.MoveToRandom)
                    {
                        ((MoveToRandom)_tempSquad.GetCommand()).Execute(responseShootingStrategy, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Scouting)
                    {
                        ((Scouting)_tempSquad.GetCommand()).Execute(responseShootingStrategy, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Mining)
                    {
                        ((Mining)_tempSquad.GetCommand()).Execute(responseShootingStrategy, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId, _tempSquad.GetNearestMiningAsteroid());
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.FullRetreat)
                    {
                        _handleStrategicCommandResponse_position = _tempSquad.GetPosition();
                        _handleStrategicCommandResponse_warpGate = null;
                        float closestWarpGateDistance = float.MaxValue;
                        List<Ship> humanShips = _handleStrategicCommandResponse_level.State.GetHumanShips();
                        for (int i = 0; i < humanShips.Count; i++)
                        {
                            Ship ship = humanShips[i];
                            if (!ship.IsWarpGate)
                            {
                                continue;
                            }
                            float distance = ship.DistanceToPoint(_handleStrategicCommandResponse_position);
                            if (distance < closestWarpGateDistance)
                            {
                                closestWarpGateDistance = distance;
                                _handleStrategicCommandResponse_warpGate = (WarpGate)ship;
                            }
                        }
                        ((FullRetreat)_tempSquad.GetCommand()).Execute(responseShootingStrategy, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId, _handleStrategicCommandResponse_warpGate);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Hold)
                    {
                        ((Hold)_tempSquad.GetCommand()).Execute(responseShootingStrategy, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Heal)
                    {
                        _handleStrategicCommandResponse_position = _tempSquad.GetPosition();
                        if (_beehives == null)
                        {
                            _beehives = new List<Beehive>();
                            _beehiveDistances = new List<float>();
                        }
                        _beehives.Clear();
                        _beehiveDistances.Clear();

                        List<Ship> beeShips = _handleStrategicCommandResponse_level.State.GetBeeShips();
                        for (int i = 0; i < beeShips.Count; i++)
                        {
                            Ship ship = beeShips[i];
                            if (!ship.IsBeehive)
                            {
                                continue;
                            }
                            Beehive beehive = (Beehive)ship;
                            if (beehive.ShipsHealingHere.Count >= 4)
                            {
                                continue;
                            }

                            float distance = beehive.DistanceToPoint(_handleStrategicCommandResponse_position);
                            int insertionIndex = _beehives.Count;
                            for (int j = 0; j < _beehiveDistances.Count; j++)
                            {
                                if (distance < _beehiveDistances[j])
                                {
                                    insertionIndex = j;
                                    break;
                                }
                            }
                            _beehives.Insert(insertionIndex, beehive);
                            _beehiveDistances.Insert(insertionIndex, distance);
                        }
                        ((Heal)_tempSquad.GetCommand()).Execute(responseShootingStrategy, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId, _beehives);
                    }
                    else
                    {
                        Debug.LogError($"Unknown command type: {_tempCommandType}");
                    }
                }
            }
            else
            {
                Debug.LogError($"Couldn't find a matching request for {_commandResponse.Hash}");
            }
        }

        private SetupLevelRequest handleSetupLevelResponseStandingRequest;
        private SetupLevelResponse _setupLevelResponse;
        private Level _setupLevel;
        private SetupLevelRequest handleReconnectLevelResponseStandingRequest;
        private Level handleReconnectLevelResponseLevel;
        private ServerRequest handleBasicResponseStandingRequest;

        private void HandleSetupLevelResponse(string message)
        {
            _setupLevelResponse = JsonUtility.FromJson<SetupLevelResponse>(message);
            handleSetupLevelResponseStandingRequest = (SetupLevelRequest)GetStandingRequest(_setupLevelResponse.Hash);
            if (handleSetupLevelResponseStandingRequest != null)
            {
                StandingRequests.Remove(handleSetupLevelResponseStandingRequest);
                _setupLevel = handleSetupLevelResponseStandingRequest.Level;
                _setupLevel.IsLevelSetupOnServer = true;
                _setupLevel.IsLevelConnectedToServer = true;
                _setupLevel.ServerGameId = _setupLevelResponse.GameId;
                _setupLevel.HandledRequests.Add(_setupLevelResponse.Hash);
                OpenLevels.Add(_setupLevel);
            }
            else
            {
                Debug.LogWarning($"Couldn't find a matching request for {_setupLevelResponse.Hash}");
            }
        }

        private void HandleReconnectLevelResponse(string message)
        {
            _setupLevelResponse = JsonUtility.FromJson<SetupLevelResponse>(message);
            handleReconnectLevelResponseStandingRequest = (ReconnectLevelRequest)GetStandingRequest(_setupLevelResponse.Hash);
            if (handleReconnectLevelResponseStandingRequest != null)
            {
                StandingRequests.Remove(handleReconnectLevelResponseStandingRequest);
                handleReconnectLevelResponseLevel = handleReconnectLevelResponseStandingRequest.Level;
                ApplyReconnectLevelResponse(handleReconnectLevelResponseLevel, _setupLevelResponse);
                MarkStrandedRequestsForResending();
            }
            else
            {
                Debug.LogWarning($"Couldn't find a matching request for {_setupLevelResponse.Hash}");
            }
        }

        private static void ApplyReconnectLevelResponse(Level level, SetupLevelResponse response)
        {
            level.IsLevelConnectedToServer = true;
            level.ServerGameId = response.GameId;
            level.HandledRequests.Add(response.Hash);
        }

        private void HandleBasicResponse(ServerResponse response)
        {
            handleBasicResponseStandingRequest = GetStandingRequest(response.Hash);
            if (handleBasicResponseStandingRequest != null)
            {
                StandingRequests.Remove(handleBasicResponseStandingRequest);
            }
            else
            {
            }
        }
    }
}