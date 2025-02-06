using UnityEngine;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Scenes;
using Assets.Scripts.Data;
using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;


using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using System;

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
        public HashSet<int> HandledRequests = new HashSet<int>();
        /// <summary>
        /// A queue of all messages received from the server
        /// </summary>
        public Queue<byte[]> MessageQueue = new Queue<byte[]>();
        public List<Level> OpenLevels = new List<Level>();



        public Socket(int port, string hostname, bool useWebSocketSharp)
        {
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
                    MessageQueue.Enqueue(e.RawData);
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
                    ConfigData.Socket.SendRequest(new ReconnectLevelRequest(new SetupLevel(ConfigData.GetUserProgressData().CurrentLevel, ConfigData.GetUserId(), ConfigData.Version),
                    ConfigData.StandardMaxTimeOnQueue,level));
                    Debug.Log($"Trying to reconnect {level.Name} to the server");
                });
            }
            else
            {
                Debug.Log("Connection open!");
            }
        }
        private void Error(string e)
        {
            Debug.Log("Socket Error! " + e);
        }
        private void Close(string reason = null)
        {
            Debug.Log("Connection closed!");
            if (reason != null)
            {
                Debug.Log($"Network Error:{reason}");
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
                Debug.Log($"Resending #{sr.Hash}");
                SendRequest(sr);
            });
        }

        private string _f_message;
        private ServerResponse _message_response;
        private ServerRequest _message_request;
        private void Message(byte[] bytes)
        {
            //Debug.Log($"Got message from server");
            // getting the message as a string
            _f_message = System.Text.Encoding.UTF8.GetString(bytes);
            //Debug.Log($"Server message: {message}");
            _message_response = JsonUtility.FromJson<ServerResponse>(_f_message);
            _message_response.RequestType = Utilities.ConvertNameToRequestType[_message_response.Type];
            //Debug.Log(response);
            
            if (!HandledRequests.Contains(_message_response.Hash))
            {
                HandledRequests.Add(_message_response.Hash);
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
                        HandleSetupLevelResponse(_message_response);
                        return;
                    case ConfigData.RequestTypes.ReconnectLevel:
                        HandleReconnectLevelResponse(_message_response);
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
                Debug.Log($"Got a response for #{_message_response.Hash} Status: {_message_response.Status} which has already been handled");
                _message_request = GetStandingRequest(_message_response.Hash);
                if (_message_request != null)
                {
                    StandingRequests.Remove(_message_request);
                }
            }
           
        }

        private string _send_json;
        public void Send(dynamic content)
        {
            //Debug.Log($"Content: {content}");
            _send_json = JsonConvert.SerializeObject(content);
            //Debug.Log($"Message to server: {json}");
            ConfigData.__TotalRequests++;
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
        public void Update() 
        {
            //Debug.Log("Updating socket");
            if (!_useWebSocketSharp)
            {
                _nativeWebSocket.DispatchMessageQueue();
            }
            while (MessageQueue.Count > 0)
            {
                _update_message = MessageQueue.Dequeue();
                Message(_update_message);
            }
            CheckStandingRequests();
        }
        public void SendRequest(ServerRequest serverRequest)
        {
            StandingRequests.Add(serverRequest);
            ConfigData.__PastServerRequests.Add(serverRequest);
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
                    //Debug.Log("Sending settings request");
                    Send(((SettingsRequest)serverRequest).Request);
                    return;
                default:
                    Debug.LogError($"No request type from {serverRequest}");
                    return;
            }
            
        }
        // ===========================
        // Class-Level Static Variables
        // ===========================

        // Variables for CheckStandingRequests()
        private List<ServerRequest> _checkStandingRequests_serverRequests; // Stores the list of standing requests
        private ServerRequest _checkStandingRequests_currentRequest; // Holds the current request being processed
        private DataFileRequest _checkStandingRequests_dataFileRequest; // Holds a DataFileRequest instance
        private SettingsRequest _checkStandingRequests_settingsRequest; // Holds a SettingsRequest instance
        private int _checkStandingRequests_loopIndex; // Tracks the loop iteration index

        /// <summary>
        /// Checks standing requests and processes them accordingly.
        /// </summary>
        private void CheckStandingRequests()
        {
            // Copy standing requests to a list for processing
            _checkStandingRequests_serverRequests = StandingRequests.ToList();

            // Iterate through the requests using the class-level loop index
            for (_checkStandingRequests_loopIndex = 0;
                 _checkStandingRequests_loopIndex < _checkStandingRequests_serverRequests.Count;
                 _checkStandingRequests_loopIndex++)
            {
                _checkStandingRequests_currentRequest = _checkStandingRequests_serverRequests[_checkStandingRequests_loopIndex];

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
                _standingRequest.TimeOnQueue = Time.unscaledTime - _standingRequest.StartTime;
                ConfigData.__TotalLatency += _standingRequest.TimeOnQueue;

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
                        _fleetData = ConfigData.GetCampaignFleetData();
                        _fleetData.GetDataFile().WriteData(_fleetData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.FleetDataFilenames[1])
                    {
                        _fleetData = ConfigData.GetFleetData();
                        _fleetData.GetDataFile().WriteData(_fleetData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.SavedSquadsDataFilenames[0])
                    {
                        _savedSquadsData = ConfigData.GetCampaignSavedSquadsData();
                        _savedSquadsData.GetDataFile().WriteData(_savedSquadsData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.SavedSquadsDataFilenames[1])
                    {
                        _savedSquadsData = ConfigData.GetSavedSquadsData();
                        _savedSquadsData.GetDataFile().WriteData(_savedSquadsData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.LevelsDataFilenames[0])
                    {
                        _levelData = ConfigData.GetCampaignLevelData();
                        _levelData.GetDataFile().WriteData(_levelData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.LevelsDataFilenames[1])
                    {
                        _levelData = ConfigData.GetLevelData();
                        _levelData.GetDataFile().WriteData(_levelData.GetDefaultJson());
                    }
                    else if (_dataFilename == ConfigData.UserProgressFilename)
                    {
                        _userProgressData = ConfigData.GetUserProgressData();
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
                    _settingsResponse_standingRequest.TimeOnQueue = Time.unscaledTime - _settingsResponse_standingRequest.StartTime;
                    ConfigData.__TotalLatency += _settingsResponse_standingRequest.TimeOnQueue;
                    // Debug.Log($"Set the response {_settingsResponse_userData.Filename}, {_settingsResponse_userData.Contents}");
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
            _handleMatchupResponse_standingRequest = (MatchupStrategyRequest)GetStandingRequest(_handleMatchupResponse_matchupResponse.Hash);

            if (_handleMatchupResponse_standingRequest != null)
            {
                StandingRequests.Remove(_handleMatchupResponse_standingRequest);
                _handleMatchupResponse_standingRequest.TimeOnQueue = Time.unscaledTime - _handleMatchupResponse_standingRequest.StartTime;
                ConfigData.__TotalLatency += _handleMatchupResponse_standingRequest.TimeOnQueue;

                _handleMatchupResponse_squad = _handleMatchupResponse_standingRequest.Squad;

                if (_handleMatchupResponse_squad != null && !_handleMatchupResponse_squad.IsDead)
                {
                    _handleMatchupResponse_squad.MatchupStrategy.Setup(
                        Utilities.ConvertMatchupStrategyNameToType[_handleMatchupResponse_matchupResponse.Name],
                        _handleMatchupResponse_matchupResponse.OutcomeId,
                        _handleMatchupResponse_squad
                    );

                    _handleMatchupResponse_targetSquad = _handleMatchupResponse_squad.MatchupStrategy.SortSquads();

                    _handleMatchupResponse_level = _handleMatchupResponse_standingRequest.Level;
                    _handleMatchupResponse_level.HandledRequests.Add(_handleMatchupResponse_standingRequest.Hash);

                    _handleMatchupResponse_squad.MakeMatchupAndGetCommand(_handleMatchupResponse_targetSquad);
                }
                else
                {
                    Debug.LogWarning($"Matchup strategy #{_handleMatchupResponse_matchupResponse.OutcomeId} was received for squad {_handleMatchupResponse_matchupResponse.Hash}, but the squad no longer exists.");
                }
            }
            else
            {
                Debug.LogWarning($"Couldn't find a matching request for {_handleMatchupResponse_matchupResponse.Hash}");
            }
        }
        //////////////////////////////////////////////////////////////////////////////
        // Class-level variables for HandleStrategicCommandResponse() method:
        //////////////////////////////////////////////////////////////////////////////

        private CommandResponse _handleStrategicCommandResponse_commandResponse;
        private CommandRequest _handleStrategicCommandResponse_standingRequest;
        private Squad _handleStrategicCommandResponse_squad;
        private Level _handleStrategicCommandResponse_level;
        private ConfigData.CommandTypes _handleStrategicCommandResponse_commandType;
        private Command _handleStrategicCommandResponse_command;
        private WarpGate _handleStrategicCommandResponse_warpGate;
        private Vector2 _handleStrategicCommandResponse_position;
        private void HandleStrategicCommandResponse(string message)
        {
            _handleStrategicCommandResponse_commandResponse = JsonUtility.FromJson<CommandResponse>(message);
            _handleStrategicCommandResponse_standingRequest = (CommandRequest)GetStandingRequest(_handleStrategicCommandResponse_commandResponse.Hash);

            if (_handleStrategicCommandResponse_standingRequest != null)
            {
                StandingRequests.Remove(_handleStrategicCommandResponse_standingRequest);  
                _handleStrategicCommandResponse_standingRequest.TimeOnQueue = Time.unscaledTime - _handleStrategicCommandResponse_standingRequest.StartTime;
                ConfigData.__TotalLatency += _handleStrategicCommandResponse_standingRequest.TimeOnQueue;
                _handleStrategicCommandResponse_squad = _handleStrategicCommandResponse_standingRequest.Squad;
                _handleStrategicCommandResponse_level = _handleStrategicCommandResponse_standingRequest.Level;
                _handleStrategicCommandResponse_level.HandledRequests.Add(_handleStrategicCommandResponse_standingRequest.Hash);
               _handleStrategicCommandResponse_commandType = Utilities.ConvertCommandNameToType[_handleStrategicCommandResponse_commandResponse.Name];
                //Debug.Log($"strategic command response");
                //Debug.Log(squad.damageSentToEnemyShipsBySquad);
                if (!_handleStrategicCommandResponse_level.State.LevelEnded && !_handleStrategicCommandResponse_squad.IsDead)
                {
                    //Debug.Log("squad is not null");
                    //if (squad.BannedStrats.Contains(commandResponse.Name))
                    //{
                    //    Debug.LogError($"{squad.Name} was given banned strat {commandResponse.Name} #{commandResponse.Hash}, isCached? {commandResponse.IsCached}");
                    //}
                    switch (_handleStrategicCommandResponse_commandType)
                    {
                        case ConfigData.CommandTypes.Aggressive:
                            if (_handleStrategicCommandResponse_squad.HasOnlyBombers)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun);
                                _handleStrategicCommandResponse_commandType = ConfigData.CommandTypes.BombingRun;
                            }
                            else if (_handleStrategicCommandResponse_squad.HasOnlyBarges)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Charge);
                                _handleStrategicCommandResponse_commandType = ConfigData.CommandTypes.Charge;
                            }
                            else
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Aggressive);
                            }
                            _handleStrategicCommandResponse_command.Setup(_handleStrategicCommandResponse_squad, true, _handleStrategicCommandResponse_standingRequest.Enemy, _handleStrategicCommandResponse_standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Retreat:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Retreat);
                            _handleStrategicCommandResponse_command.Setup(_handleStrategicCommandResponse_squad, true, _handleStrategicCommandResponse_standingRequest.Enemy, _handleStrategicCommandResponse_standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.MoveToRandom:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.MoveToRandom);
                            _handleStrategicCommandResponse_command.Setup(_handleStrategicCommandResponse_squad, true, _handleStrategicCommandResponse_standingRequest.Enemy, _handleStrategicCommandResponse_standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.CircleSquad:
                            if (_handleStrategicCommandResponse_squad.HasOnlyBombers)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun);
                                _handleStrategicCommandResponse_commandType = ConfigData.CommandTypes.BombingRun;
                            }
                            else if (_handleStrategicCommandResponse_squad.HasOnlyBarges)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Charge);
                                _handleStrategicCommandResponse_commandType = ConfigData.CommandTypes.Charge;
                            }
                            else
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.CircleSquad);
                            }
                            _handleStrategicCommandResponse_command.Setup(_handleStrategicCommandResponse_squad, true, _handleStrategicCommandResponse_standingRequest.Enemy, _handleStrategicCommandResponse_standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.RightSwipe:
                        case ConfigData.CommandTypes.LeftSwipe:
                            if (_handleStrategicCommandResponse_squad.HasOnlyBombers)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun);
                                _handleStrategicCommandResponse_commandType = ConfigData.CommandTypes.BombingRun;
                            }
                            else if (_handleStrategicCommandResponse_squad.HasOnlyBarges)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Charge);
                                _handleStrategicCommandResponse_commandType = ConfigData.CommandTypes.Charge;
                            }
                            else
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(_handleStrategicCommandResponse_commandType);

                            }
                            _handleStrategicCommandResponse_command.Setup(_handleStrategicCommandResponse_squad, true, _handleStrategicCommandResponse_standingRequest.Enemy, _handleStrategicCommandResponse_standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.ClosestFriendly:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.ClosestFriendly);
                            _handleStrategicCommandResponse_command.Setup(_handleStrategicCommandResponse_squad, true, _handleStrategicCommandResponse_standingRequest.Enemy, _handleStrategicCommandResponse_standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.InAndOut:
                            if (_handleStrategicCommandResponse_squad.HasOnlyBombers)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun);
                                _handleStrategicCommandResponse_commandType = ConfigData.CommandTypes.BombingRun;
                            }
                            else if (_handleStrategicCommandResponse_squad.HasOnlyBarges)
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Charge);
                                _handleStrategicCommandResponse_commandType = ConfigData.CommandTypes.Charge;
                            }
                            else
                            {
                                _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.InAndOut);
                            }
                            _handleStrategicCommandResponse_command.Setup(_handleStrategicCommandResponse_squad, true, _handleStrategicCommandResponse_standingRequest.Enemy, _handleStrategicCommandResponse_standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Patrol:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Patrol);
                            _handleStrategicCommandResponse_command.Setup(_handleStrategicCommandResponse_squad, true, _handleStrategicCommandResponse_standingRequest.Enemy, _handleStrategicCommandResponse_standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Guard:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Guard);
                            _handleStrategicCommandResponse_command.Setup(_handleStrategicCommandResponse_squad, true, _handleStrategicCommandResponse_standingRequest.Enemy, _handleStrategicCommandResponse_standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Scouting:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Scouting);
                            _handleStrategicCommandResponse_command.Setup(_handleStrategicCommandResponse_squad, true, _handleStrategicCommandResponse_standingRequest.Enemy, _handleStrategicCommandResponse_standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Mining:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Mining);
                            _handleStrategicCommandResponse_command.Setup(_handleStrategicCommandResponse_squad, true, _handleStrategicCommandResponse_standingRequest.Enemy, _handleStrategicCommandResponse_standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.FullRetreat:
                            _handleStrategicCommandResponse_command = _handleStrategicCommandResponse_level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.FullRetreat);
                            _handleStrategicCommandResponse_command.Setup(_handleStrategicCommandResponse_squad, true, _handleStrategicCommandResponse_standingRequest.Enemy, _handleStrategicCommandResponse_standingRequest.Matchup);
                            break;
                        default:
                            Debug.LogError($"commandResponse doesn't match a known command: {_handleStrategicCommandResponse_commandResponse.Name}");
                            break;
                    }

                    _handleStrategicCommandResponse_squad.Command = _handleStrategicCommandResponse_command;
                    _handleStrategicCommandResponse_squad.Command.MatchupStrategy = _handleStrategicCommandResponse_squad.MatchupStrategy;
                    
                    if (_handleStrategicCommandResponse_commandType == ConfigData.CommandTypes.Aggressive)
                    {
                        ((Aggressive)_handleStrategicCommandResponse_squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[_handleStrategicCommandResponse_commandResponse.ShootingStrategyName], _handleStrategicCommandResponse_commandResponse.OutcomeId, _handleStrategicCommandResponse_commandResponse.ShootingStrategyOutcomeId, false);
                    }
                    else if (_handleStrategicCommandResponse_commandType == ConfigData.CommandTypes.BombingRun)
                    {
                        ((BombingRun)_handleStrategicCommandResponse_squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[_handleStrategicCommandResponse_commandResponse.ShootingStrategyName], _handleStrategicCommandResponse_commandResponse.OutcomeId, _handleStrategicCommandResponse_commandResponse.ShootingStrategyOutcomeId, false);
                    }
                    else if (_handleStrategicCommandResponse_commandType == ConfigData.CommandTypes.Charge)
                    {
                        ((Charge)_handleStrategicCommandResponse_squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[_handleStrategicCommandResponse_commandResponse.ShootingStrategyName], _handleStrategicCommandResponse_commandResponse.OutcomeId, _handleStrategicCommandResponse_commandResponse.ShootingStrategyOutcomeId, false);
                    }
                    else if (_handleStrategicCommandResponse_commandType == ConfigData.CommandTypes.CircleSquad)
                    {
                        ((CircleSquad)_handleStrategicCommandResponse_squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[_handleStrategicCommandResponse_commandResponse.ShootingStrategyName], _handleStrategicCommandResponse_commandResponse.OutcomeId, _handleStrategicCommandResponse_commandResponse.ShootingStrategyOutcomeId, false);
                    }
                    else if (_handleStrategicCommandResponse_commandType == ConfigData.CommandTypes.InAndOut)
                    {
                        ((InAndOut)_handleStrategicCommandResponse_squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[_handleStrategicCommandResponse_commandResponse.ShootingStrategyName], _handleStrategicCommandResponse_commandResponse.OutcomeId, _handleStrategicCommandResponse_commandResponse.ShootingStrategyOutcomeId, false);
                    }
                    else if (_handleStrategicCommandResponse_commandType == ConfigData.CommandTypes.Retreat)
                    {
                        ((Retreat)_handleStrategicCommandResponse_squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[_handleStrategicCommandResponse_commandResponse.ShootingStrategyName], _handleStrategicCommandResponse_commandResponse.OutcomeId, _handleStrategicCommandResponse_commandResponse.ShootingStrategyOutcomeId, false);
                    }
                    else if (_handleStrategicCommandResponse_commandType == ConfigData.CommandTypes.LeftSwipe || _handleStrategicCommandResponse_commandType == ConfigData.CommandTypes.RightSwipe)
                    {
                        ((SwipeSquad)_handleStrategicCommandResponse_squad.Command).Execute(_handleStrategicCommandResponse_commandType, Utilities.ConvertShootingStrategyNameToType[_handleStrategicCommandResponse_commandResponse.ShootingStrategyName], _handleStrategicCommandResponse_commandResponse.OutcomeId, _handleStrategicCommandResponse_commandResponse.ShootingStrategyOutcomeId, false);
                    }
                    else if (_handleStrategicCommandResponse_commandType == ConfigData.CommandTypes.Patrol)
                    {
                        //Debug.Log("Got a patrol);
                        ((Patrol)_handleStrategicCommandResponse_squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[_handleStrategicCommandResponse_commandResponse.ShootingStrategyName], _handleStrategicCommandResponse_commandResponse.OutcomeId, _handleStrategicCommandResponse_commandResponse.ShootingStrategyOutcomeId, true, Vector2.zero, Vector2.zero);
                    }
                    else if (_handleStrategicCommandResponse_commandType == ConfigData.CommandTypes.Guard)
                    {
                        ((Guard)_handleStrategicCommandResponse_squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[_handleStrategicCommandResponse_commandResponse.ShootingStrategyName], _handleStrategicCommandResponse_commandResponse.OutcomeId, _handleStrategicCommandResponse_commandResponse.ShootingStrategyOutcomeId, true, null);
                    }
                    else if (_handleStrategicCommandResponse_commandType == ConfigData.CommandTypes.ClosestFriendly)
                    {
                        ((ClosestFriendly)_handleStrategicCommandResponse_squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[_handleStrategicCommandResponse_commandResponse.ShootingStrategyName], _handleStrategicCommandResponse_commandResponse.OutcomeId, _handleStrategicCommandResponse_commandResponse.ShootingStrategyOutcomeId, true);
                    }
                    else if (_handleStrategicCommandResponse_commandType == ConfigData.CommandTypes.MoveToRandom)
                    {
                        ((MoveToRandom)_handleStrategicCommandResponse_squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[_handleStrategicCommandResponse_commandResponse.ShootingStrategyName], _handleStrategicCommandResponse_commandResponse.OutcomeId, _handleStrategicCommandResponse_commandResponse.ShootingStrategyOutcomeId, true);
                    }
                    else if (_handleStrategicCommandResponse_commandType == ConfigData.CommandTypes.Scouting)
                    {
                        ((Scouting)_handleStrategicCommandResponse_squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[_handleStrategicCommandResponse_commandResponse.ShootingStrategyName], _handleStrategicCommandResponse_commandResponse.OutcomeId, _handleStrategicCommandResponse_commandResponse.ShootingStrategyOutcomeId, true);
                    }
                    else if (_handleStrategicCommandResponse_commandType == ConfigData.CommandTypes.Mining)
                    {
                        ((Mining)_handleStrategicCommandResponse_squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[_handleStrategicCommandResponse_commandResponse.ShootingStrategyName], _handleStrategicCommandResponse_commandResponse.OutcomeId, _handleStrategicCommandResponse_commandResponse.ShootingStrategyOutcomeId, true, _handleStrategicCommandResponse_squad.GetNearestMiningAsteroid());
                    }
                    else if (_handleStrategicCommandResponse_commandType == ConfigData.CommandTypes.FullRetreat)
                    {
                        _handleStrategicCommandResponse_position = _handleStrategicCommandResponse_squad.GetPosition();
                        _handleStrategicCommandResponse_warpGate = (WarpGate) _handleStrategicCommandResponse_level.State.GetHumanShips().Where((s) => s.IsWarpGate).OrderBy((s) => s.DistanceToPoint(_handleStrategicCommandResponse_position)).FirstOrDefault();
                        ((FullRetreat)_handleStrategicCommandResponse_squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[_handleStrategicCommandResponse_commandResponse.ShootingStrategyName], _handleStrategicCommandResponse_commandResponse.OutcomeId, _handleStrategicCommandResponse_commandResponse.ShootingStrategyOutcomeId, true, _handleStrategicCommandResponse_warpGate);
                    }

                }
                else
                {
                    //Debug.Log($"Strategic command #{commandResponse.StrategyId} was received for squad #{commandResponse.SquadHash} but that squad no longer exists.");
                }
            }
            else
            {
                Debug.Log($"Couldn't find a matching request for {_handleStrategicCommandResponse_commandResponse.Hash}");
            }

        }
        // HandleSetupLevelResponse variables
        private SetupLevelRequest handleSetupLevelResponseStandingRequest;
        private Level handleSetupLevelResponseLevel;
        private float handleSetupLevelResponseTimeOnQueue;

        // HandleReconnectLevelResponse variables
        private SetupLevelRequest handleReconnectLevelResponseStandingRequest;
        private Level handleReconnectLevelResponseLevel;
        private float handleReconnectLevelResponseTimeOnQueue;

        // HandleBasicResponse variables
        private ServerRequest handleBasicResponseStandingRequest;
        private float handleBasicResponseTimeOnQueue;

        private void HandleSetupLevelResponse(ServerResponse response)
        {
            // Get standing request
            handleSetupLevelResponseStandingRequest = (SetupLevelRequest)GetStandingRequest(response.Hash);

            if (handleSetupLevelResponseStandingRequest != null)
            {
                // Remove from standing requests and process level
                StandingRequests.Remove(handleSetupLevelResponseStandingRequest);
                handleSetupLevelResponseLevel = handleSetupLevelResponseStandingRequest.Level;
                handleSetupLevelResponseLevel.IsLevelSetupOnServer = true;
                handleSetupLevelResponseLevel.IsLevelConnectedToServer = true;
                handleSetupLevelResponseLevel.HandledRequests.Add(response.Hash);

                // Calculate time on queue and update latency
                handleSetupLevelResponseTimeOnQueue = Time.unscaledTime - handleSetupLevelResponseStandingRequest.StartTime;
                handleSetupLevelResponseStandingRequest.TimeOnQueue = handleSetupLevelResponseTimeOnQueue;
                ConfigData.__TotalLatency += handleSetupLevelResponseTimeOnQueue;

                // Add to open levels
                OpenLevels.Add(handleSetupLevelResponseLevel);
            }
            else
            {
                Debug.Log($"Couldn't find a matching request for {response.Hash}");
            }
        }

        private void HandleReconnectLevelResponse(ServerResponse response)
        {
            // Get standing request
            handleReconnectLevelResponseStandingRequest = (SetupLevelRequest)GetStandingRequest(response.Hash);

            if (handleReconnectLevelResponseStandingRequest != null)
            {
                // Remove from standing requests and process level
                StandingRequests.Remove(handleReconnectLevelResponseStandingRequest);
                handleReconnectLevelResponseLevel = handleReconnectLevelResponseStandingRequest.Level;
                handleReconnectLevelResponseLevel.IsLevelConnectedToServer = true;
                handleReconnectLevelResponseLevel.HandledRequests.Add(response.Hash);

                // Calculate time on queue and update latency
                handleReconnectLevelResponseTimeOnQueue = Time.unscaledTime - handleReconnectLevelResponseStandingRequest.StartTime;
                handleReconnectLevelResponseStandingRequest.TimeOnQueue = handleReconnectLevelResponseTimeOnQueue;
                ConfigData.__TotalLatency += handleReconnectLevelResponseTimeOnQueue;

                // Log reconnection and mark stranded requests for resending
                Debug.Log($"Reconnected {handleReconnectLevelResponseLevel.Name} to the server");
                MarkStrandedRequestsForResending();
            }
            else
            {
                Debug.Log($"Couldn't find a matching request for {response.Hash}");
            }
        }

        private void HandleBasicResponse(ServerResponse response)
        {
            // Get standing request
            handleBasicResponseStandingRequest = GetStandingRequest(response.Hash);

            if (handleBasicResponseStandingRequest != null)
            {
                // Remove from standing requests and calculate time on queue
                StandingRequests.Remove(handleBasicResponseStandingRequest);
                handleBasicResponseTimeOnQueue = Time.unscaledTime - handleBasicResponseStandingRequest.StartTime;
                handleBasicResponseStandingRequest.TimeOnQueue = handleBasicResponseTimeOnQueue;
                ConfigData.__TotalLatency += handleBasicResponseTimeOnQueue;
            }
            else
            {
                Debug.Log($"Couldn't find a matching request for {response.Hash}");
            }
        }

    }

} 