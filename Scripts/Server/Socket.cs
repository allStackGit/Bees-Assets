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
        private void Message(byte[] bytes)
        {
            //Debug.Log($"Got message from server");
            // getting the message as a string
            string message = System.Text.Encoding.UTF8.GetString(bytes);
            //Debug.Log($"Server message: {message}");
            ServerResponse response = JsonUtility.FromJson<ServerResponse>(message);
            //Debug.Log(response);
            string type = response.Type;
            
            if (!HandledRequests.Contains(response.Hash))
            {
                HandledRequests.Add(response.Hash);
                switch (type)
                {
                    case "get-matchup-strategy":
                        //Debug.Log("Handling matchup response!");
                        HandleMatchupResponse(message);
                        return;
                    case "get-strategy":
                        HandleStrategicCommandResponse(message);
                        return;
                    case "send-rl-data":
                        HandleBasicResponse(response);
                        return;
                    case "store-commands":
                        HandleBasicResponse(response);
                        return;
                    case "setup-level":
                        HandleSetupLevelResponse(response);
                        return;
                    case "reconnect-level":
                        HandleReconnectLevelResponse(response);
                        return;
                    case "store-user-data":
                        HandleBasicResponse(response);
                        return;
                    case "get-user-data":
                        HandleUserDataResponse(message);
                        return;
                    case "get-settings":
                        HandleSettingsResponse(message);
                        return;
                    default:
                        Debug.LogError($"No response type from {response}");
                        return;
                }
            }
            else
            {
                Debug.Log($"Got a response for #{response.Hash} Status: {response.Status} which has already been handled");
                ServerRequest sr = GetStandingRequest(response.Hash);
                if (sr != null)
                {
                    StandingRequests.Remove(sr);
                }
            }
           
        }


        public void Send(dynamic content)
        {
            //Debug.Log($"Content: {content}");
            string json = JsonConvert.SerializeObject(content);
            //Debug.Log($"Message to server: {json}");
            ConfigData.__TotalRequests++;
            if (_useWebSocketSharp)
            {
                _webSocketSharpSocket.Send(json);
            }
            else
            {
                _nativeWebSocket.SendText(json);
            }
        }
        public void Update() 
        {
            //Debug.Log("Updating socket");
            if (!_useWebSocketSharp)
            {
                _nativeWebSocket.DispatchMessageQueue();
            }
            while (MessageQueue.Count > 0)
            {
                byte[] message = MessageQueue.Dequeue();
                if (message != null)
                {
                    Message(message);
                }
            }
            CheckStandingRequests();
        }
        public void SendRequest(ServerRequest serverRequest)
        {
            StandingRequests.Add(serverRequest);
            ConfigData.__PastServerRequests.Add(serverRequest);
            switch (serverRequest.Type)
            {
                case "get-matchup-strategy":
                    Send(((MatchupStrategyRequest)serverRequest).Request);
                    return;
                case "get-strategy":
                    Send(((CommandRequest)serverRequest).Request);
                    return;
                case "store-commands":
                    Send(((StoreCommandsRequest)serverRequest).Request);
                    return;
                case "setup-level":
                    Send(((SetupLevelRequest)serverRequest).Request);
                    return;
                case "reconnect-level":
                    Send(((ReconnectLevelRequest)serverRequest).Request);
                    return;
                case "store-user-data":
                    Send(((StoreUserDataRequest)serverRequest).Request);
                    return;
                case "get-user-data":
                    Send(((DataFileRequest)serverRequest).Request);
                    return;
                case "get-settings":
                    //Debug.Log("Sending settings request");
                    Send(((SettingsRequest)serverRequest).Request);
                    return;
                default:
                    Debug.LogError($"No request type from {serverRequest}");
                    return;
            }
            
        }
        private void CheckStandingRequests()
        {
            //Debug.Log("1. Checking on standing requests");
            //List<ServerRequest> resends = new List<ServerRequest>();
            List<ServerRequest> serverRequests = StandingRequests.ToList();
            for (int i = 0; i < serverRequests.Count; i++)
            {
                ServerRequest request = serverRequests[i];

                //request.TimeOnQueue = Time.unscaledTime - request.StartTime;
                //Debug.Log($"Time on Queue for #{request.Hash} - {request.Type} is {request.TimeOnQueue}ms");
                if (request.Type == "get-user-data")
                {
                    DataFileRequest dataFileRequest = (DataFileRequest)request;
                    dataFileRequest.DataFile.WaitForResponse();
                    continue;
                }
                else if (request.Type == "get-settings")
                {
                    SettingsRequest settingsRequest = (SettingsRequest)request;
                    settingsRequest.Settings.WaitForResponse();
                    continue;
                }

                //if (request.TimeOnQueue > request.MaxTimeOnQueue && request.Status == 0)
                //{
                //    Debug.Log($"Request #{request.Hash} - {request.Type} timed out after {request.TimeOnQueue}/{request.MaxTimeOnQueue}s  and is NOT being resent. ");
                //    //request.MaxTimeOnQueue += (int)(ConfigData.StandardMaxTimeOnQueue);
                //    //request.Resends++;
                //    //request.Status = -1;
                //    //resends.Add(request);
                //}
            }
            
            
            //Debug.Log($"2. Loop ended, Resends: {resends.Count}");
            //HandledRequests.AddRange(StandingRequests.Where((r) => r.Status == 1).Select((r) => r.Hash));
            //StandingRequests = StandingRequests.Where((request) => request.Status == 0).ToHashSet(null);
            //Debug.Log($"3. Modified standing requests, Resends: {resends.Count}");

            //resends.ForEach((request) =>
            //{
            //    request.Status = 0;
            //    SendRequest(request);
            //});

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


        private void HandleUserDataResponse(string message)
        {
            //Debug.Log("Got user data from server");
            UserDataResponse userDataResponse = JsonUtility.FromJson<UserDataResponse>(message);
            DataFileRequest standingRequest;
            try
            {
                standingRequest = (DataFileRequest)GetStandingRequest(userDataResponse.Hash);
            }
            catch (Exception e)
            {
                Debug.Log($"Error trying to get standing request #{userDataResponse.Hash}");
                Debug.Log($"Standing requests: {Utilities.ListToString(StandingRequests.ToList())}");
                throw e;
            }
            if (standingRequest != null)
            {
                standingRequest.TimeOnQueue = Time.unscaledTime - standingRequest.StartTime;
                ConfigData.__TotalLatency += standingRequest.TimeOnQueue;

                if (userDataResponse.Filename != "" && userDataResponse.Contents != "")
                {
                    standingRequest.Response = userDataResponse;
                    standingRequest.Status = 1;
                    //Debug.Log($"Set the response {userDataResponse.Filename}, {userDataResponse.Contents}");
                }
                else
                {
                    //Debug.Log("The server data file was null, writing the defaults to the server");
                    string dataFilename = standingRequest.Request.DataFile;

                    if (dataFilename == ConfigData.FleetDataFilenames[0])
                    {
                        FleetData fleetData = ConfigData.GetCampaignFleetData();
                        fleetData.GetDataFile().WriteData(fleetData.GetDefaultJson());
                    }
                    else if (dataFilename == ConfigData.FleetDataFilenames[1])
                    {
                        FleetData fleetData = ConfigData.GetFleetData();
                        fleetData.GetDataFile().WriteData(fleetData.GetDefaultJson());
                    }

                    else if (dataFilename == ConfigData.SavedSquadsDataFilenames[0])
                    {
                        SavedSquadsData savedSquadsData = ConfigData.GetCampaignSavedSquadsData();
                        savedSquadsData.GetDataFile().WriteData(savedSquadsData.GetDefaultJson());
                    }
                    else if (dataFilename == ConfigData.SavedSquadsDataFilenames[1])
                    {
                        SavedSquadsData savedSquadsData = ConfigData.GetSavedSquadsData();
                        savedSquadsData.GetDataFile().WriteData(savedSquadsData.GetDefaultJson());
                    }

                    else if (dataFilename == ConfigData.LevelsDataFilenames[0])
                    {
                        LevelData levelData = ConfigData.GetCampaignLevelData();
                        levelData.GetDataFile().WriteData(levelData.GetDefaultJson());
                    }
                    else if (dataFilename == ConfigData.LevelsDataFilenames[1])
                    {
                        LevelData levelData = ConfigData.GetLevelData();
                        levelData.GetDataFile().WriteData(levelData.GetDefaultJson());
                    }

                    else if (dataFilename == ConfigData.UserProgressFilename)
                    {
                        UserProgressData userProgressData = ConfigData.GetUserProgressData();
                        userProgressData.GetDataFile().WriteData(userProgressData.GetDefaultJson());
                    }

                    else if (dataFilename == ConfigData.UserSettingsFilename)
                    {
                        UserSettingsData userSettingsData = ConfigData.GetUserSettingsData();
                        userSettingsData.GetDataFile().WriteData(userSettingsData.GetDefaultJson());
                    }

                    standingRequest.Status = -1;

                    //Task.Run(async () =>
                    //{
                    //    await Task.Delay(500);
                    //    standingRequest.Status = -1; // indicates the the request needs to be resent
                    //    standingRequest.Response = userDataResponse;
                    //});


                }

                //Debug.Log("Set the response");
            }
            else
            {
                Debug.LogError($"Counldn't find a matching request for {userDataResponse.Hash}");
            }
            
        }
        private void HandleSettingsResponse(string message)
        {
            //Debug.Log("Got user data from server");
            UserDataResponse userDataResponse = JsonUtility.FromJson<UserDataResponse>(message);
            SettingsRequest standingRequest = (SettingsRequest)GetStandingRequest(userDataResponse.Hash);
            if (standingRequest != null)
            {
                if (userDataResponse.Filename != "" && userDataResponse.Contents != "")
                {
                    standingRequest.Status = 1;
                    standingRequest.Response = userDataResponse;
                    standingRequest.TimeOnQueue = Time.unscaledTime - standingRequest.StartTime;
                    ConfigData.__TotalLatency += standingRequest.TimeOnQueue;
                    //Debug.Log($"Set the response {userDataResponse.Filename}, {userDataResponse.Contents}");
                }
                else
                {
                    Debug.LogError($"Null response when requesting settings from the server. {userDataResponse}");

                }

            }
            else
            {
                Debug.LogError($"Couldn't find a matching request for {userDataResponse.Hash}");
            }

        }
        private void HandleMatchupResponse(string message)
        {
            MatchupStrategyResponse matchupResponse = JsonUtility.FromJson<MatchupStrategyResponse>(message);
            MatchupStrategyRequest standingRequest = (MatchupStrategyRequest)GetStandingRequest(matchupResponse.Hash);
            if (standingRequest != null)
            {
                StandingRequests.Remove(standingRequest);
                standingRequest.TimeOnQueue = Time.unscaledTime - standingRequest.StartTime;
                ConfigData.__TotalLatency += standingRequest.TimeOnQueue;
                Squad squad = standingRequest.Squad;
                if (squad != null && !squad.IsDead)
                {
                    //squad.Command = squad.gameObject.AddComponent<Command>();
                    //squad.Command.Setup(squad, true);

                    squad.MatchupStrategy.Setup(Utilities.ConvertMatchupStrategyNameToType[matchupResponse.Name], matchupResponse.OutcomeId, squad);

                    //squad.Command.MatchupStrategy = squad.MatchupStrategy;
                    Squad targetSquad = squad.MatchupStrategy.SortSquads();
                    //Debug.Log($"matchup strategy after sorted");
                    //Debugger.LogSquads(Level.State.GetSquads());
                    Level level = standingRequest.Level;
                    level.HandledRequests.Add(standingRequest.Hash);
                    squad.MakeMatchupAndGetCommand(targetSquad);
                }
                else
                {
                    //Debug.Log("Exception");
                    //Debug.Log($"matchup strategy #{matchupResponse.StrategyId} was received for squad #{matchupResponse.SquadHash} but that squad no longer exists.");
                }
            }
            else
            {
                Debug.Log($"Couldn't find a matching request for {matchupResponse.Hash}");
            }
            
        }  
        private void HandleStrategicCommandResponse(string message)
        {
            CommandResponse commandResponse = JsonUtility.FromJson<CommandResponse>(message);
            CommandRequest standingRequest = (CommandRequest)GetStandingRequest(commandResponse.Hash);

            if (standingRequest != null)
            {
                StandingRequests.Remove(standingRequest);
                standingRequest.TimeOnQueue = Time.unscaledTime - standingRequest.StartTime;
                ConfigData.__TotalLatency += standingRequest.TimeOnQueue;
                Squad squad = standingRequest.Squad;
                Level level = standingRequest.Level;
                level.HandledRequests.Add(standingRequest.Hash);
                ConfigData.CommandTypes commandType = Utilities.ConvertCommandNameToType[commandResponse.Name];
                //Debug.Log($"strategic command response");
                //Debug.Log(squad.damageSentToEnemyShipsBySquad);
                if (squad != null && !level.State.LevelEnded && !squad.IsDead)
                {
                    //Debug.Log("squad is not null");
                    Command command = null;
                    //if (squad.BannedStrats.Contains(commandResponse.Name))
                    //{
                    //    Debug.LogError($"{squad.Name} was given banned strat {commandResponse.Name} #{commandResponse.Hash}, isCached? {commandResponse.IsCached}");
                    //}
                    switch (commandType)
                    {
                        case ConfigData.CommandTypes.Aggressive:
                            if (squad.HasOnlyBombers)
                            {
                                command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun);
                                commandType = ConfigData.CommandTypes.BombingRun;
                            }
                            else if (squad.HasOnlyBarges)
                            {
                                command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Charge);
                                commandType = ConfigData.CommandTypes.Charge;
                            }
                            else
                            {
                                command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Aggressive);
                            }
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Retreat:
                            command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Retreat);
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.MoveToRandom:
                            command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.MoveToRandom);
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.CircleSquad:
                            if (squad.HasOnlyBombers)
                            {
                                command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun);
                                commandType = ConfigData.CommandTypes.BombingRun;
                            }
                            else if (squad.HasOnlyBarges)
                            {
                                command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Charge);
                                commandType = ConfigData.CommandTypes.Charge;
                            }
                            else
                            {
                                command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.CircleSquad);
                            }
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.RightSwipe:
                        case ConfigData.CommandTypes.LeftSwipe:
                            if (squad.HasOnlyBombers)
                            {
                                command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun);
                                commandType = ConfigData.CommandTypes.BombingRun;
                            }
                            else if (squad.HasOnlyBarges)
                            {
                                command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Charge);
                                commandType = ConfigData.CommandTypes.Charge;
                            }
                            else
                            {
                                command = level.Stage.Pool.GetCommandFromPool(commandType);

                            }
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.ClosestFriendly:
                            command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.ClosestFriendly);
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.InAndOut:
                            if (squad.HasOnlyBombers)
                            {
                                command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.BombingRun);
                                commandType = ConfigData.CommandTypes.BombingRun;
                            }
                            else if (squad.HasOnlyBarges)
                            {
                                command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Charge);
                                commandType = ConfigData.CommandTypes.Charge;
                            }
                            else
                            {
                                command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.InAndOut);
                            }
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Patrol:
                            command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Patrol);
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Guard:
                            command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Guard);
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Scouting:
                            command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Scouting);
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.Mining:
                            command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.Mining);
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case ConfigData.CommandTypes.FullRetreat:
                            command = level.Stage.Pool.GetCommandFromPool(ConfigData.CommandTypes.FullRetreat);
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        default:
                            Debug.LogError($"commandResponse doesn't match a known command: {commandResponse.Name}");
                            break;
                    }

                    squad.Command = command;
                    squad.Command.MatchupStrategy = squad.MatchupStrategy;
                    
                    if (commandType == ConfigData.CommandTypes.Patrol)
                    {
                        //Debug.Log("Got a patrol);
                        ((Patrol)squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[commandResponse.ShootingStrategyName], commandResponse.OutcomeId, commandResponse.ShootingStrategyOutcomeId, true, Vector2.zero, Vector2.zero);
                    }
                    else if (commandType == ConfigData.CommandTypes.Guard)
                    {
                        ((Guard)squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[commandResponse.ShootingStrategyName], commandResponse.OutcomeId, commandResponse.ShootingStrategyOutcomeId, true, null);
                    }
                    else if (commandType == ConfigData.CommandTypes.ClosestFriendly)
                    {
                        ((ClosestFriendly)squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[commandResponse.ShootingStrategyName], commandResponse.OutcomeId, commandResponse.ShootingStrategyOutcomeId, true);
                    }
                    else if (commandType == ConfigData.CommandTypes.MoveToRandom)
                    {
                        ((MoveToRandom)squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[commandResponse.ShootingStrategyName], commandResponse.OutcomeId, commandResponse.ShootingStrategyOutcomeId, true);
                    }
                    else if (commandType == ConfigData.CommandTypes.Scouting)
                    {
                        ((Scouting)squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[commandResponse.ShootingStrategyName], commandResponse.OutcomeId, commandResponse.ShootingStrategyOutcomeId, true);
                    }
                    else if (commandType == ConfigData.CommandTypes.Mining)
                    {
                        ((Mining)squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[commandResponse.ShootingStrategyName], commandResponse.OutcomeId, commandResponse.ShootingStrategyOutcomeId, true, squad.GetNearestMiningAsteroid());
                    }
                    else if (commandType == ConfigData.CommandTypes.FullRetreat)
                    {
                        Vector2 position = squad.GetPosition();
                        WarpGate warpGate = (WarpGate) level.State.GetHumanShips().Where((s) => s.IsWarpGate).OrderBy((s) => s.DistanceToPoint(position)).FirstOrDefault();
                        ((FullRetreat)squad.Command).Execute(Utilities.ConvertShootingStrategyNameToType[commandResponse.ShootingStrategyName], commandResponse.OutcomeId, commandResponse.ShootingStrategyOutcomeId, true, warpGate);
                    }
                    else
                    {
                        //if (command is BombingRun && standingRequest.Enemy == null)
                        //{
                        //    Debug.Log($"Trying to execute bombing run ({commandResponse.Name}) for {squad.Name} against null enemy from #{commandResponse.Hash}. IsCached? {commandResponse.IsCached}");
                        //}
                        squad.Command.Execute(commandType, Utilities.ConvertShootingStrategyNameToType[commandResponse.ShootingStrategyName], commandResponse.OutcomeId, commandResponse.ShootingStrategyOutcomeId, false);
                    }

                }
                else
                {
                    //Debug.Log($"Strategic command #{commandResponse.StrategyId} was received for squad #{commandResponse.SquadHash} but that squad no longer exists.");
                }
            }
            else
            {
                Debug.Log($"Couldn't find a matching request for {commandResponse.Hash}");
            }

        }
        private void HandleSetupLevelResponse(ServerResponse response)
        {
            SetupLevelRequest standingRequest = (SetupLevelRequest)GetStandingRequest(response.Hash);

            if (standingRequest != null)
            {
                StandingRequests.Remove(standingRequest);
                standingRequest.Level.IsLevelSetupOnServer = true;
                standingRequest.Level.IsLevelConnectedToServer = true;
                standingRequest.Level.HandledRequests.Add(response.Hash);
                standingRequest.TimeOnQueue = Time.unscaledTime - standingRequest.StartTime;
                ConfigData.__TotalLatency += standingRequest.TimeOnQueue;
                OpenLevels.Add(standingRequest.Level);
            }
            else
            {
                Debug.Log($"Couldn't find a matching request for {response.Hash}");
            }

        }

        private void HandleReconnectLevelResponse(ServerResponse response)
        {
            SetupLevelRequest standingRequest = (SetupLevelRequest)GetStandingRequest(response.Hash);

            if (standingRequest != null)
            {
                StandingRequests.Remove(standingRequest);
                standingRequest.Level.IsLevelConnectedToServer = true;
                standingRequest.Level.HandledRequests.Add(response.Hash);
                standingRequest.TimeOnQueue = Time.unscaledTime - standingRequest.StartTime;
                ConfigData.__TotalLatency += standingRequest.TimeOnQueue;
                Debug.Log($"Reconnected {standingRequest.Level.Name} to the server");
                MarkStrandedRequestsForResending();
            }
            else
            {
                Debug.Log($"Couldn't find a matching request for {response.Hash}");
            }

        }
        private void HandleBasicResponse(ServerResponse response)
        {
            ServerRequest standingRequest = GetStandingRequest(response.Hash);

            if (standingRequest != null)
            {
                StandingRequests.Remove(standingRequest);
                standingRequest.TimeOnQueue = Time.unscaledTime - standingRequest.StartTime;
                ConfigData.__TotalLatency += standingRequest.TimeOnQueue;
            }
            else
            {
                Debug.Log($"Couldn't find a matching request for {response.Hash}");
            }

        }

    }

} 