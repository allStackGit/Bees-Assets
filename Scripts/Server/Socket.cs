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

namespace Assets.Scripts.Server
{
    public class Socket
    {
        private string _hostname = ConfigData.ProductionServerHostname;
        private int _port = ConfigData.ProductionPort;
        private string _websocketURL;
        private NativeWebSocket.WebSocket _nativeWebSocket = null;
        private WebSocketSharp.WebSocket _webSocketSharpSocket = null; 
        //private bool _loadNextLevel;
        private bool _useWebSocketSharp = false;

        public bool IsOpen;
        public bool IsSecured;
        public bool HasClosed;
        public bool KeepClosed;
        public string Protocol = "ws";
        /// <summary>
        /// A hashset of all requests that are still pending
        /// </summary>
        public HashSet<ServerRequest> StandingRequests = new HashSet<ServerRequest>();
        /// <summary>
        /// A hashset of all request IDs that have been handled. Resets every level
        /// </summary>
        public HashSet<long> HandledRequests = new HashSet<long>();
        /// <summary>
        /// A queue of all messages received from the server
        /// </summary>
        public ConcurrentQueue<byte[]> MessageQueue = new ConcurrentQueue<byte[]>();
        public List<Level> OpenLevels = new List<Level>();



        public Socket(int port, string hostname, bool useWebSocketSharp)
        {
            ConfigData.Stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _hostname = hostname;
            _port = port;
            _useWebSocketSharp = useWebSocketSharp;
            _websocketURL = $"{Protocol}://{_hostname}:{_port}";
            Debug.Log($"Trying to connect to {_websocketURL}");
            MakeSocket();
        }
        async public void MakeSocket()
        {
            //Debug.Log("Making socket");
            if (_useWebSocketSharp)
            {
                _webSocketSharpSocket = new WebSocketSharp.WebSocket(_websocketURL, "game");
                if (IsSecured)
                {
                    _webSocketSharpSocket.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;
                }

                //Debug.Log("Initial State : " + _webSocketSharpSocket.ReadyState);

                _webSocketSharpSocket.OnOpen += (sender, e) =>
                {
                    Open();
                };

                _webSocketSharpSocket.OnError += (sender, e) => {
                    Error(e.Message); 
                };

                _webSocketSharpSocket.OnClose += (sender, e) =>
                {
                    Close(e.Reason);
                };

                _webSocketSharpSocket.OnMessage += (sender, e) =>
                {
                    if (e == null)
                    {
                        Debug.LogWarning("Received null message from server");
                    }
                    else if (e.RawData == null)
                    {
                        Debug.LogWarning("Received null raw data from server:");
                        Debug.LogWarning(e);
                        Debug.LogWarning(e.IsPing);
                        Debug.LogWarning(e.IsBinary);
                        Debug.LogWarning(e.IsText);
                        Debug.LogWarning(e.Data);
                    }
                    else if (e.RawData.Length == 0)
                    {
                        Debug.LogWarning("Received empty message from server:");
                        Debug.LogWarning(e);
                        Debug.LogWarning(e);
                        Debug.LogWarning(e.IsPing);
                        Debug.LogWarning(e.IsBinary);
                        Debug.LogWarning(e.IsText);
                        Debug.LogWarning(e.Data);
                    }
                    else
                    {
                        MessageQueue.Enqueue(e.RawData.ToArray());
                    }
                };

                _webSocketSharpSocket.Connect();

            }
            else
            {
                _nativeWebSocket = new NativeWebSocket.WebSocket(_websocketURL, "game");

                _nativeWebSocket.OnOpen += () =>
                {
                    Open();
                };

                _nativeWebSocket.OnError += (e) => { Error(e); };

                _nativeWebSocket.OnClose += (e) =>
                {
                    Close();
                };

                _nativeWebSocket.OnMessage += (bytes) =>
                {
                    MessageQueue.Enqueue(bytes);
                };


                // waiting for messages
                await _nativeWebSocket.Connect();
            }

        }
        private void Open()
        {
            IsOpen = true;
            if (HasClosed)
            {
                HasClosed = false;
                Debug.Log("Connection re-opened!");
                OpenLevels.ForEach((level) =>
                {
                    ConfigData.Socket.SendRequest(new ReconnectLevelRequest(new SetupLevel(ConfigData.CurrentGameMode == ConfigData.GameModes.FreePlay || ConfigData.CurrentGameMode == ConfigData.GameModes.FishTank ? -1 : ConfigData.UserProgressData.GetCurrentLevel(ConfigData.Configuration.UserSide), ConfigData.GetUserId(), level.ServerGameId),
                    ConfigData.StandardMaxTimeOnQueue,level));
                    Debug.Log($"Trying to reconnect {level.Name} to the server");
                });
            }
            else
            {
                Debug.Log($"Connection with {(_useWebSocketSharp ? "WebSocketSharp" : "NativeWebSocket")} open!");
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
                //StandingRequests.ToList().ForEach((sr) => 
                //{
                //    sr.Status = -1; 
                //});
            }
            OpenLevels.ForEach((level) =>
            {
                level.IsLevelConnectedToServer = false;
            });
            IsOpen = false;
            HasClosed = true;
            //_checkQueue.Dispose();
            //if (!KeepClosed)
            //{
            //    MakeSocket();
            //}
        }
        private void MarkStrandedRequestsForResending()
        {
            StandingRequests.ToList().ForEach((sr) =>
            {
                StandingRequests.Remove(sr);
                Debug.LogWarning($"Resending #{sr.Hash}");
                SendRequest(sr, true);
            });
        }

        private string _f_message;
        private ServerResponse _message_response;
        private ServerRequest _message_request;
        //private long _c2c, _now;
        private void Message(byte[] bytes)
        {
            // [debug]
            //if (bytes == null || bytes.Length == 0)
            //{
            //    Debug.LogError("Received empty message from server");
            //    return;
            //}
            //ConfigData.__TotalRequests++;
            //Debug.Log($"Got message from server");
            // getting the message as a string
            _f_message = System.Text.Encoding.UTF8.GetString(bytes);
            //Debug.Log($"Server message: {message}");
            _message_response = JsonUtility.FromJson<ServerResponse>(_f_message);
            _message_response.RequestType = Utilities.ConvertNameToRequestType[_message_response.Type];
            //Debug.Log(response);

            // [debug]
            //_message_request = GetStandingRequest(_message_response.Hash);
            //if (_message_request != null)
            //{
            //    _now = ConfigData.Stopwatch.ElapsedMilliseconds;
            //    _c2c = (_now - _message_request.SendTime);
            //    ConfigData.__TotalC2C += _c2c;
            //    ConfigData.__TotalWireTime += _c2c - _message_response.ProcessingTime;
            //    ConfigData.__TotalProcessingTime += _message_response.ProcessingTime;

            //    if (_c2c < 0)
            //    {
            //        Debug.LogWarning($"C2C time is negative! {_c2c}ms");
            //    }
            //    if (_message_response.ProcessingTime < 0)
            //    {
            //        Debug.LogWarning($"Processing time is negative! {_message_response.ProcessingTime}ms");
            //    }
            //    if (_message_response.ProcessingTime > _c2c)
            //    {
            //        Debug.LogWarning($"Processing time is greater than C2C time! {_message_response.ProcessingTime}ms > {_c2c}ms, now: {_now}, SendTime: {_message_request.SendTime}");
            //    }

            //    //Debug.Log($"Received message #{_message_response.Hash} from server. \n" +
            //    //    $"It took {(_message_response.ServerReceiveTime - sr.SendTime)}ms to go from the client to the server. \n" +
            //    //    $"It took {(_message_response.ServerSendTime - _message_response.ServerReceiveTime)}ms to be received, processed, and sent back. \n" +
            //    //    $"It took {(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _message_response.ServerSendTime)}ms to go from the server back to the client. \n" +
            //    //    $"It took {(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sr.SendTime)}ms to go from the client back to the client, round trip.");
            //}


            if (TryClaimResponse(_message_response.Hash))
            {
                //Debug.Log($"Received response for request #{_message_response.Hash}");
                switch (_message_response.RequestType)
                {
                    case ConfigData.RequestTypes.GetMatchupStrategy:
                        //Debug.Log("Handling matchup response!");
                        HandleMatchupResponse(_f_message);
                        return;
                    case ConfigData.RequestTypes.GetStrategy:
                        HandleStrategicCommandResponse(_f_message);
                        return;
                    case ConfigData.RequestTypes.SendRLData:
                        HandleBasicResponse(_message_response);
                        return;
                    case ConfigData.RequestTypes.StoreCommands:
                        HandleBasicResponse(_message_response);
                        return;
                    case ConfigData.RequestTypes.SetupLevel:
                        HandleSetupLevelResponse(_f_message);
                        return;
                    case ConfigData.RequestTypes.ReconnectLevel:
                        HandleReconnectLevelResponse(_f_message);
                        return;
                    case ConfigData.RequestTypes.StoreUserData:
                        HandleBasicResponse(_message_response);
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
            else
            {
                Debug.LogWarning($"Got a response for #{_message_response.Hash} Status: {_message_response.Status} which has already been handled");
                _message_request = GetStandingRequest(_message_response.Hash);
                if (_message_request != null)
                {
                    StandingRequests.Remove(_message_request);
                }
                else
                {
                    Debug.LogWarning($"There was no standing request to remove for #{_message_response.Hash}");
                }
            }
           
        }

        private string _send_json;
        public void Send(dynamic content)
        {
            //Debug.Log($"Content: {content}");
            _send_json = JsonConvert.SerializeObject(content);
            //Debug.Log($"Message to server: {_send_json}");
            if (_useWebSocketSharp)
            {
                _webSocketSharpSocket.Send(_send_json);
            }
            else
            {
                _nativeWebSocket.SendText(_send_json);
            }
        }
        private byte[] _update_message;
        //private float _timeOfCurrentUpdate;
        //private float _timeOfLastUpdate;
        public void Update() 
        {
            //_timeOfCurrentUpdate = ConfigData.Stopwatch.ElapsedMilliseconds ;
            //if (_timeOfCurrentUpdate - _timeOfLastUpdate > 1)
            //{
            //    Debug.LogWarning($"{_timeOfCurrentUpdate - _timeOfLastUpdate}s have passed since the previous socket update");
            //}
            //Debug.Log("Updating socket");


            // Using WebSocketSharp so we don't need this
            //if (!_useWebSocketSharp)
            //{
            //    _nativeWebSocket.DispatchMessageQueue();
            //}


            /*
             
            while (MessageQueue.TryDequeue(out _update_message))
            {
                Message(_update_message);
            }
             */
            //while (MessageQueue.Count > 0) // should be able to replace this once we know that there aren't any errors [debug]
            //{
            //    ;
            //    if (_update_message != null)
            //    {
            //        Message(_update_message);
            //    }
            //    else if (_update_message.Length == 0)
            //    {
            //        Debug.LogWarning($"Received empty message from server. There are {MessageQueue.Count} messages on the queue");
            //    }
            //    else
            //    {
            //        Debug.LogWarning($"Received null message from server. There are {MessageQueue.Count} messages on the queue");
            //    }
            //}
            

            while (MessageQueue.TryDequeue(out _update_message))
            {
                Message(_update_message);
            }

            CheckStandingRequests();

            //_timeOfLastUpdate = _timeOfCurrentUpdate;
        }
        private List<ServerRequest> _standingRequests;
        private ServerRequest _sr;
        private int _index;
        private int _resends;
        /// <summary>
        /// Resends each standing request after that request's configured queue timeout.
        /// </summary>
        public void CheckForResends()
        {
            _resends = 0;
            _standingRequests = StandingRequests.ToList();
            for (_index = 0; _index < _standingRequests.Count; _index++)
            {
                _sr = _standingRequests[_index];
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
                ConfigData.__TotalResends += _resends;
            }

        }
        public void SendRequest(MatchupStrategyRequest serverRequest)
        {
            LogRequest(serverRequest);
            //Debug.Log($"Sending matchup request #{serverRequest.Hash} for Squad {serverRequest.Squad}");
            //serverRequest.Squad.Status = $"Waiting for matchup request #{serverRequest.Hash} since update #{serverRequest.Squad.Level.Stage.__Updates}";
            Send(serverRequest.Request);
        }
        public void SendRequest(CommandRequest serverRequest)
        {
            LogRequest(serverRequest);
            //serverRequest.Squad.Status = $"Waiting for strategy request #{serverRequest.Hash} since update #{serverRequest.Squad.Level.Stage.__Updates}";
            //Debug.Log($"Sending strategy request #{serverRequest.Hash} for Squad {serverRequest.Squad}");
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
            //Debug.Log($"Sending request #{serverRequest.Hash}");
            
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
                    //Debug.Log($"Sending stored commands");
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
                    //Debug.Log("Sending settings request");
                    Send(((SettingsRequest)serverRequest).Request);
                    return;
                default:
                    Debug.LogError($"No request type from {serverRequest}");
                    return;
            }

        }
        /// <summary>
        /// Adds the server request to Standing Requests and optionally adds it to __PastServerRequests
        /// </summary>
        /// <param name="request"></param>
        public void LogRequest(ServerRequest request, bool isResendRequest = false)
        {
            StandingRequests.Add(request);
            ConfigData.__PastServerRequests.Add(request);
            if (isResendRequest)
            {
                request.StartTime = ConfigData.Stopwatch.ElapsedMilliseconds;
                request.Resends++;
                if (request.Resends > 10)
                {
                    Debug.LogWarning($"Request #{request.Hash} has been resent more than 10 times. This is probably a bug or a really long server delay.");
                }

            }
            //else
            //{
            //    request.SendTime = ConfigData.Stopwatch.ElapsedMilliseconds; // [debug]
            //}
        }
        // ===========================
        // Class-Level Static Variables
        // ===========================

        // Variables for CheckStandingRequests()
        private List<ServerRequest> _checkStandingRequests_serverRequests; // Stores the list of standing requests
        private ServerRequest _checkStandingRequests_currentRequest; // Holds the current request being processed
        private DataFileRequest _checkStandingRequests_dataFileRequest; // Holds a DataFileRequest instance
        private SettingsRequest _checkStandingRequests_settingsRequest; // Holds a SettingsRequest instance

        /// <summary>
        /// Checks standing requests and processes them accordingly.
        /// </summary>
        private void CheckStandingRequests()
        {
            // Copy standing requests to a list for processing
            _checkStandingRequests_serverRequests = StandingRequests.ToList();

            // Iterate through the requests using the class-level loop index
            for (_index = 0; _index < _checkStandingRequests_serverRequests.Count; _index++)
            {
                _checkStandingRequests_currentRequest = _checkStandingRequests_serverRequests[_index];

                // Handle specific request types
                if (_checkStandingRequests_currentRequest.Type == ConfigData.RequestTypes.GetUserData)
                {
                    _checkStandingRequests_dataFileRequest = (DataFileRequest)_checkStandingRequests_currentRequest;
                    _checkStandingRequests_dataFileRequest.DataFile.WaitForResponse();
                    continue;
                }
                else if (_checkStandingRequests_currentRequest.Type == ConfigData.RequestTypes.GetSettings)
                {
                    _checkStandingRequests_settingsRequest = (SettingsRequest)_checkStandingRequests_currentRequest;
                    _checkStandingRequests_settingsRequest.Settings.WaitForResponse();
                    continue;
                }
            }
        }

        public ServerRequest GetStandingRequest(long hash)
        {
            //ServerRequest serverRequest = StandingRequests.FirstOrDefault(r => r.Hash == hash);
            //if (serverRequest == null)
            //{
            //    Debug.Log($"There are {StandingRequests.Count} standing requests");
            //    Debug.Log(StandingRequests.Select((r) => $"Request #{r.Hash} ({r.Type}) on queue for {r.TimeOnQueue}ms with {r.Resends} resends.").Aggregate("", (a, b) => $"{a}\n\n{b}"));
            //    Debug.Log($"Could not find a standing request that matched [{hash}]");
            //}
            //return serverRequest;
            return StandingRequests.FirstOrDefault(r => r.Hash == hash);
        }

        private bool TryClaimResponse(long hash)
        {
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


        // Class-level variables for HandleUserDataResponse
        private UserDataResponse _userDataResponse; // Stores the deserialized response from the server
        private DataFileRequest _standingRequest; // Stores the standing request matching the response
        private string _dataFilename; // Stores the filename of the requested data file

        private FleetData _fleetData; // Stores fleet data
        private SavedSquadsData _savedSquadsData; // Stores saved squads data
        private LevelData _levelData; // Stores level data
        private UserProgressData _userProgressData; // Stores user progress data
        private UserSettingsData _userSettingsData; // Stores user settings data

        private void HandleUserDataResponse(string message)
        {
            // Deserialize the user data response
            _userDataResponse = JsonUtility.FromJson<UserDataResponse>(message);
            _standingRequest = (DataFileRequest)GetStandingRequest(_userDataResponse.Hash);

            //try
            //{
            //    _standingRequest = (DataFileRequest)GetStandingRequest(_userDataResponse.Hash);
            //}
            //catch (Exception e)
            //{
            //    Debug.Log($"Error trying to get standing request #{_userDataResponse.Hash}");
            //    Debug.Log($"Standing requests: {Utilities.ListToString(StandingRequests.ToList())}");
            //    throw e;
            //}

            if (_standingRequest != null)
            {
                //_standingRequest.TimeOnQueue = ConfigData.Stopwatch.ElapsedMilliseconds - _standingRequest.StartTime;
                //ConfigData.__TotalTimeOnQueue += _standingRequest.TimeOnQueue;

                if (!string.IsNullOrEmpty(_userDataResponse.Filename) && !string.IsNullOrEmpty(_userDataResponse.Contents))
                {
                    _standingRequest.Response = _userDataResponse;
                    _standingRequest.Status = 1;
                }
                else // [data-file]
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

        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for HandleSettingsResponse() method:
        //////////////////////////////////////////////////////////////////////////////

        private UserDataResponse _settingsResponse_userData;
        private SettingsRequest _settingsResponse_standingRequest;

        private void HandleSettingsResponse(string message)
        {
            // Debug.Log("Got user data from server");
            _settingsResponse_userData = JsonUtility.FromJson<UserDataResponse>(message);
            _settingsResponse_standingRequest = (SettingsRequest)GetStandingRequest(_settingsResponse_userData.Hash);

            if (_settingsResponse_standingRequest != null)
            {
                if (!string.IsNullOrEmpty(_settingsResponse_userData.Filename) &&
                    !string.IsNullOrEmpty(_settingsResponse_userData.Contents))
                {
                    _settingsResponse_standingRequest.Status = 1;
                    _settingsResponse_standingRequest.Response = _settingsResponse_userData;
                    //_settingsResponse_standingRequest.TimeOnQueue = ConfigData.Stopwatch.ElapsedMilliseconds - _settingsResponse_standingRequest.StartTime;
                    //ConfigData.__TotalTimeOnQueue += _settingsResponse_standingRequest.TimeOnQueue;
                    // Debug.Log($"Set the response {_settingsResponse_userData.Filename}, {_settingsResponse_userData.Contents}");
                }
                else
                {
                    Debug.LogError($"Null response when requesting settings from the server. {_settingsResponse_userData}");
                }
            }
            else
            {
                Debug.Log($"Standing requests: {Utilities.ListToString(StandingRequests.ToList())}");
                Debug.LogError($"Couldn't find a matching request for {_settingsResponse_userData.Hash}");
            }
        }
        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for HandleMatchupResponse() method:
        //////////////////////////////////////////////////////////////////////////////

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
                //_handleMatchupResponse_standingRequest.TimeOnQueue = ConfigData.Stopwatch.ElapsedMilliseconds - _handleMatchupResponse_standingRequest.StartTime;
                //ConfigData.__TotalTimeOnQueue += _handleMatchupResponse_standingRequest.TimeOnQueue;

                _handleMatchupResponse_squad = _handleMatchupResponse_standingRequest.Squad;
                _handleMatchupResponse_level = _handleMatchupResponse_standingRequest.Level;
                _handleMatchupResponse_level.RecordSimulationInput("hivemind-matchup-response", message);

                if (CanApplySquadResponse(
                    _handleMatchupResponse_level,
                    _handleMatchupResponse_squad,
                    _handleMatchupResponse_standingRequest.SquadId))
                {
                    _handleMatchupResponse_squad.MatchupStrategy.Setup(
                        Utilities.ConvertMatchupStrategyNameToType[_handleMatchupResponse_matchupResponse.Name],
                        _handleMatchupResponse_matchupResponse.OutcomeId,
                        _handleMatchupResponse_squad
                    );

                    _handleMatchupResponse_targetSquad = _handleMatchupResponse_squad.MatchupStrategy.SortSquads();

                    _handleMatchupResponse_level.HandledRequests.Add(_handleMatchupResponse_standingRequest.Hash);

                    _handleMatchupResponse_squad.MakeMatchupAndGetCommand(_handleMatchupResponse_targetSquad);
                }
                else
                {
                    //Debug.LogWarning($"Matchup strategy #{_handleMatchupResponse_matchupResponse.OutcomeId} was received for squad #{_handleMatchupResponse_matchupResponse.SquadHash}, but the squad no longer exists.");
                }
            }
            else
            {
                Debug.LogError($"Couldn't find a matching request for {_handleMatchupResponse_matchupResponse.Hash}");
            }
        }
        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for HandleStrategicCommandResponse() method:
        //////////////////////////////////////////////////////////////////////////////

        private CommandResponse _commandResponse;
        private CommandRequest _strategicStandingRequest;
        private Squad _tempSquad;
        private Level _handleStrategicCommandResponse_level;
        private ConfigData.CommandTypes _tempCommandType;
        private Command _handleStrategicCommandResponse_command;
        private WarpGate _handleStrategicCommandResponse_warpGate;
        private List<Beehive> _beehives;
        private Vector2 _handleStrategicCommandResponse_position;
        private void HandleStrategicCommandResponse(string message)
        {
            //Debug.Log($"Message: {message}");
            _commandResponse = JsonUtility.FromJson<CommandResponse>(message);
            _strategicStandingRequest = TakeStandingRequest(
                _commandResponse.Hash,
                ConfigData.RequestTypes.GetStrategy) as CommandRequest;

            if (_strategicStandingRequest != null)
            {
                //_strategicStandingRequest.TimeOnQueue = ConfigData.Stopwatch.ElapsedMilliseconds - _strategicStandingRequest.StartTime;
                //ConfigData.__TotalTimeOnQueue += _strategicStandingRequest.TimeOnQueue;
                _tempSquad = _strategicStandingRequest.Squad;
                _handleStrategicCommandResponse_level = _strategicStandingRequest.Level;
                _handleStrategicCommandResponse_level.RecordSimulationInput("hivemind-command-response", message);
                //Debug.Log($"{_tempSquad.Name} received command type: {_tempCommandType}");
                if (CanApplySquadResponse(
                    _handleStrategicCommandResponse_level,
                    _tempSquad,
                    _strategicStandingRequest.SquadId))
                {
                    _handleStrategicCommandResponse_level.HandledRequests.Add(_strategicStandingRequest.Hash);
                    _tempCommandType = Utilities.ConvertCommandNameToType[_commandResponse.Name];

                    //Debug.Log($"strategic command response");
                    //Debug.Log(squad.damageSentToEnemyShipsBySquad);

                    if (_strategicStandingRequest.Request.BannedStrats.Contains(Utilities.ConvertCommandTypeToName[_tempCommandType]))
                    {
                        Debug.LogError($"{_tempSquad.Name} received banned command type: {_tempCommandType}");
                    }
                    if (_tempSquad.IsDead)
                    {
                        Debug.LogError($"Squad {_tempSquad} is dead, but received a command response.");
                    }
                    //Debug.Log("squad is not null");
                    //if (squad.BannedStrats.Contains(commandResponse.Name))
                    //{
                    //    Debug.LogError($"{squad.Name} was given banned strat {commandResponse.Name} #{commandResponse.Hash}, isCached? {commandResponse.IsCached}");
                    //}
                    switch (_tempCommandType)
                    {
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

                    _tempSquad.SetCommand(_handleStrategicCommandResponse_command);
                    //_tempSquad.GetCommand().MatchupStrategy = _tempSquad.MatchupStrategy; // This is only used to store data and isn't important for functionality

                    Debug.Log($"Command response for {_tempSquad} {_handleStrategicCommandResponse_command}");

                    if (_tempCommandType == ConfigData.CommandTypes.Aggressive)
                    {
                        ((Aggressive)_tempSquad.GetCommand()).Execute(Utilities.ConvertShootingStrategyNameToType[_commandResponse.ShootingStrategyName], _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.BombingRun)
                    {
                        ((BombingRun)_tempSquad.GetCommand()).Execute(Utilities.ConvertShootingStrategyNameToType[_commandResponse.ShootingStrategyName], _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Charge)
                    {
                        ((Charge)_tempSquad.GetCommand()).Execute(Utilities.ConvertShootingStrategyNameToType[_commandResponse.ShootingStrategyName], _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.CircleSquad)
                    {
                        ((CircleSquad)_tempSquad.GetCommand()).Execute(Utilities.ConvertShootingStrategyNameToType[_commandResponse.ShootingStrategyName], _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.InAndOut)
                    {
                        ((InAndOut)_tempSquad.GetCommand()).Execute(Utilities.ConvertShootingStrategyNameToType[_commandResponse.ShootingStrategyName], _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Retreat)
                    {
                        ((Retreat)_tempSquad.GetCommand()).Execute(ConfigData.ShootingStrategyTypes.FirstSeen, _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.LeftSwipe || _tempCommandType == ConfigData.CommandTypes.RightSwipe)
                    {
                        ((SwipeSquad)_tempSquad.GetCommand()).Execute(_tempCommandType, Utilities.ConvertShootingStrategyNameToType[_commandResponse.ShootingStrategyName], _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Patrol)
                    {
                        //Debug.Log("Got a patrol);
                        ((Patrol)_tempSquad.GetCommand()).Execute(Utilities.ConvertShootingStrategyNameToType[_commandResponse.ShootingStrategyName], _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId, Vector2.zero, Vector2.zero);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Guard)
                    {
                        ((Guard)_tempSquad.GetCommand()).Execute(Utilities.ConvertShootingStrategyNameToType[_commandResponse.ShootingStrategyName], _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId, null);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.ClosestFriendly)
                    {
                        ((ClosestFriendly)_tempSquad.GetCommand()).Execute(Utilities.ConvertShootingStrategyNameToType[_commandResponse.ShootingStrategyName], _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.MoveToRandom)
                    {
                        ((MoveToRandom)_tempSquad.GetCommand()).Execute(Utilities.ConvertShootingStrategyNameToType[_commandResponse.ShootingStrategyName], _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Scouting)
                    {
                        ((Scouting)_tempSquad.GetCommand()).Execute(Utilities.ConvertShootingStrategyNameToType[_commandResponse.ShootingStrategyName], _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Mining)
                    {
                        ((Mining)_tempSquad.GetCommand()).Execute(Utilities.ConvertShootingStrategyNameToType[_commandResponse.ShootingStrategyName], _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId, _tempSquad.GetNearestMiningAsteroid());
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.FullRetreat)
                    {
                        _handleStrategicCommandResponse_position = _tempSquad.GetPosition();
                        _handleStrategicCommandResponse_warpGate = (WarpGate) _handleStrategicCommandResponse_level.State.GetHumanShips().Where((s) => s.IsWarpGate).OrderBy((s) => s.DistanceToPoint(_handleStrategicCommandResponse_position)).FirstOrDefault();
                        ((FullRetreat)_tempSquad.GetCommand()).Execute(Utilities.ConvertShootingStrategyNameToType[_commandResponse.ShootingStrategyName], _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId, _handleStrategicCommandResponse_warpGate);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Hold)
                    {
                        ((Hold)_tempSquad.GetCommand()).Execute(Utilities.ConvertShootingStrategyNameToType[_commandResponse.ShootingStrategyName], _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId);
                    }
                    else if (_tempCommandType == ConfigData.CommandTypes.Heal)
                    {
                        _handleStrategicCommandResponse_position = _tempSquad.GetPosition();
                        _beehives = _handleStrategicCommandResponse_level.State.GetBeeShips().Where((s) => s.IsBeehive && ((Beehive)s).ShipsHealingHere.Count < 4).Select((s) => (Beehive)s).OrderBy((s) => s.DistanceToPoint(_handleStrategicCommandResponse_position)).ToList();
                        ((Heal)_tempSquad.GetCommand()).Execute(Utilities.ConvertShootingStrategyNameToType[_commandResponse.ShootingStrategyName], _commandResponse.OutcomeId, _commandResponse.ShootingStrategyOutcomeId, _beehives);
                    }
                    else
                    {
                        Debug.LogError($"Unknown command type: {_tempCommandType}");
                    }

                }
                else
                {
                    //Debug.LogWarning($"Strategic command {_handleStrategicCommandResponse_command} was received for squad {_tempSquad} but that squad no longer exists.");
                }
            }
            else
            {
                Debug.LogError($"Couldn't find a matching request for {_commandResponse.Hash}");
            }

        }
        // HandleSetupLevelResponse variables
        private SetupLevelRequest handleSetupLevelResponseStandingRequest;
        private SetupLevelResponse _setupLevelResponse;
        private Level _setupLevel;
        private long handleSetupLevelResponseTimeOnQueue;

        // HandleReconnectLevelResponse variables
        private SetupLevelRequest handleReconnectLevelResponseStandingRequest;
        private Level handleReconnectLevelResponseLevel;
        private long handleReconnectLevelResponseTimeOnQueue;

        // HandleBasicResponse variables
        private ServerRequest handleBasicResponseStandingRequest;
        private long handleBasicResponseTimeOnQueue;

        private void HandleSetupLevelResponse(string message)
        {
            // Get standing request
            _setupLevelResponse = JsonUtility.FromJson<SetupLevelResponse>(message);
            handleSetupLevelResponseStandingRequest = (SetupLevelRequest)GetStandingRequest(_setupLevelResponse.Hash);

            if (handleSetupLevelResponseStandingRequest != null)
            {
                // Remove from standing requests and process level
                StandingRequests.Remove(handleSetupLevelResponseStandingRequest);
                _setupLevel = handleSetupLevelResponseStandingRequest.Level;
                _setupLevel.IsLevelSetupOnServer = true;
                _setupLevel.IsLevelConnectedToServer = true;
                _setupLevel.ServerGameId = _setupLevelResponse.GameId;
                _setupLevel.HandledRequests.Add(_setupLevelResponse.Hash);

                // Calculate time on queue and update latency
                handleSetupLevelResponseTimeOnQueue = ConfigData.Stopwatch.ElapsedMilliseconds - handleSetupLevelResponseStandingRequest.StartTime;
                //handleSetupLevelResponseStandingRequest.TimeOnQueue = handleSetupLevelResponseTimeOnQueue;
                //ConfigData.__TotalTimeOnQueue += handleSetupLevelResponseTimeOnQueue;

                // Add to open levels
                OpenLevels.Add(_setupLevel);
            }
            else
            {
                Debug.LogWarning($"Couldn't find a matching request for {_setupLevelResponse.Hash}");
            }
        }

        private void HandleReconnectLevelResponse(string message)
        {
            // Get standing request
            _setupLevelResponse = JsonUtility.FromJson<SetupLevelResponse>(message);
            handleReconnectLevelResponseStandingRequest = (ReconnectLevelRequest)GetStandingRequest(_setupLevelResponse.Hash);

            if (handleReconnectLevelResponseStandingRequest != null)
            {
                // Remove from standing requests and process level
                StandingRequests.Remove(handleReconnectLevelResponseStandingRequest);
                handleReconnectLevelResponseLevel = handleReconnectLevelResponseStandingRequest.Level;
                ApplyReconnectLevelResponse(handleReconnectLevelResponseLevel, _setupLevelResponse);

                // Calculate time on queue and update latency
                handleReconnectLevelResponseTimeOnQueue = ConfigData.Stopwatch.ElapsedMilliseconds - handleReconnectLevelResponseStandingRequest.StartTime;
                //handleReconnectLevelResponseStandingRequest.TimeOnQueue = handleReconnectLevelResponseTimeOnQueue; // [debug]
                //ConfigData.__TotalTimeOnQueue += handleReconnectLevelResponseTimeOnQueue;

                // Log reconnection and mark stranded requests for resending
                Debug.Log($"Reconnected {handleReconnectLevelResponseLevel.Name} to the server");
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
            // Get standing request
            handleBasicResponseStandingRequest = GetStandingRequest(response.Hash);

            if (handleBasicResponseStandingRequest != null)
            {
                // Remove from standing requests and calculate time on queue
                StandingRequests.Remove(handleBasicResponseStandingRequest);
                handleBasicResponseTimeOnQueue = ConfigData.Stopwatch.ElapsedMilliseconds - handleBasicResponseStandingRequest.StartTime;
                //handleBasicResponseStandingRequest.TimeOnQueue = handleBasicResponseTimeOnQueue; // [debug]
                //ConfigData.__TotalTimeOnQueue += handleBasicResponseTimeOnQueue;
            }
            else
            {
                Debug.Log($"Couldn't find a matching request for {response.Hash}");
            }
        }

    }

} 
