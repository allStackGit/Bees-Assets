
using NativeWebSocket;
using WebSocketSharp;
using UnityEngine;
using Newtonsoft.Json;
using Assets.Scripts;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Collections;
using Assets.Scripts.Scenes;
using Assets.Scripts.Data;
using Assets.Scripts.Level;
using Assets.Scripts.Level.Commands;
using Assets.Scripts.Entities.Ships;
using System;
using Unity.VisualScripting;

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
        private Scene _scene;
        private bool _useWebSocketSharp = false;

        public bool IsOpen;
        public bool IsSecured;
        public bool HasClosed;
        public bool KeepClosed;
        public string Protocol = "ws";
        public HashSet<ServerRequest> StandingRequests = new HashSet<ServerRequest>();
        public HashSet<long> HandledRequests = new HashSet<long>();
        public Queue<byte[]> MessageQueue = new Queue<byte[]>();



        public Socket(int port, string hostname, bool useWebSocketSharp)
        {
            _hostname = hostname;
            _port = port;
            _useWebSocketSharp = useWebSocketSharp;
            _websocketURL = $"{Protocol}://{_hostname}:{_port}";
            Debugger.Log($"Trying to connect to {_websocketURL}");
            MakeSocket();
        }
        public void SetScene(Scene scene)
        {
            _scene = scene;
        }
        async public void MakeSocket()
        {
            //Debugger.Log("Making socket");
            if (_useWebSocketSharp)
            {
                _webSocketSharpSocket = new WebSocketSharp.WebSocket(_websocketURL, "game");
                if (IsSecured)
                {
                    _webSocketSharpSocket.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;
                }

                //Debugger.Log("Initial State : " + _webSocketSharpSocket.ReadyState);

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
            Debugger.Log("Connection open!");
            IsOpen = true;
            HasClosed = false;
        }
        private void Error(string e)
        {
            Debugger.Log("Socket Error! " + e);
        }
        private void Close(string reason = null)
        {
            Debugger.Log("Connection closed!");
            if (reason != null)
            {
                Debugger.Error(reason);
            }
            IsOpen = false;
            HasClosed = true;
            //_checkQueue.Dispose();
            //if (!KeepClosed)
            //{
            //    MakeSocket();
            //}
        }
        private void Message(byte[] bytes)
        {
            //Debugger.Log($"Got message from server for {_scene}");
            // getting the message as a string
            string message = System.Text.Encoding.UTF8.GetString(bytes);
            //Debugger.Log($"Server message: {message} Thread: {Thread.CurrentThread.ManagedThreadId}");
            ServerResponse response = JsonUtility.FromJson<ServerResponse>(message);
            //Debugger.Log(response);
            string type = response.Type;
            
            if (!HandledRequests.Contains(response.Hash))
            {
                switch (type)
                {
                    case "get-matchup-strategy":
                        //Debugger.Log("Handling matchup response!");
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
                        LevelStage level = (LevelStage)_scene;
                        level.IsLevelSetupOnServer = true;
                        //Debugger.Log("setup on server");
                        HandleBasicResponse(response);
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
                        Debugger.Exception($"No response type from {response}");
                        return;
                }
            }
            else
            {
                Debugger.Log($"Got a response for #{response.Hash} which has already been handled");
                ServerRequest sr = GetStandingRequest(response.Hash);
                sr.Status = 1;

            }
           
        }


        public void Send(dynamic content)
        {
            //Debugger.Log($"Content: {content}");
            string json = JsonConvert.SerializeObject(content);
            //Debugger.Log($"Message to server: {json}");
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
            //Debugger.Log("Updating socket");
            if (!_useWebSocketSharp)
            {
                _nativeWebSocket.DispatchMessageQueue();
            }
            if (MessageQueue.Count > 0)
            {
                Message(MessageQueue.Dequeue());
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
                case "store-user-data":
                    Send(((StoreUserDataRequest)serverRequest).Request);
                    return;
                case "get-user-data":
                    Send(((DataFileRequest)serverRequest).Request);
                    return;
                case "get-settings":
                    //Debugger.Log("Sending settings request");
                    Send(((SettingsRequest)serverRequest).Request);
                    return;
                default:
                    Debugger.Exception($"No request type from {serverRequest}");
                    return;
            }
            
        }
        private void CheckStandingRequests()
        {
            //Debugger.Log("1. Checking on standing requests");
            List<ServerRequest> resends = new List<ServerRequest>();
            foreach (ServerRequest request in StandingRequests)
            {
                request.TimeOnQueue = (int)((Time.unscaledTime - request.StartTime) * 1000);
                //Debugger.Log($"Time on Queue for #{request.Hash} - {request.Type} is {request.TimeOnQueue}ms");
                if (request.Type == "get-user-data")
                {
                    DataFileRequest dataFileRequest = (DataFileRequest)request;
                    dataFileRequest.DataFile.WaitForResponse();
                }
                else if (request.Type == "get-settings")
                {
                    SettingsRequest settingsRequest = (SettingsRequest)request;
                    settingsRequest.Settings.WaitForResponse();
                }

                if (request.TimeOnQueue > request.MaxTimeOnQueue && request.Status == 0)
                {
                    Debugger.Log($"Request #{request.Hash} - {request.Type} timed out after {request.TimeOnQueue}/{request.MaxTimeOnQueue}ms  and is being resent. ");
                    request.MaxTimeOnQueue += (int)(ConfigData.StandardMaxTimeOnQueue);
                    request.Resends++;
                    request.Status = -1;
                    resends.Add(request);
                }
            }
            
            //Debugger.Log($"2. Loop ended, Resends: {resends.Count}");
            List<long> newlyHandledRequests = StandingRequests.Where((r) => r.Status == 1).Select((r) => r.Hash).ToList();
            HandledRequests.AddRange(newlyHandledRequests);
            StandingRequests = StandingRequests.Where((request) => request.Status == 0).ToHashSet(null);
            //Debugger.Log($"3. Modified standing requests, Resends: {resends.Count}");

            resends.ForEach((request) =>
            {
                request.Status = 0;
                SendRequest(request);
            });

        }
        public ServerRequest GetStandingRequest(long hash)
        {
            return StandingRequests.First(r => r.Hash == hash);
        }


        private void HandleUserDataResponse(string message)
        {
            //Debugger.Log("Got user data from server");
            UserDataResponse userDataResponse = JsonUtility.FromJson<UserDataResponse>(message);
            DataFileRequest standingRequest = (DataFileRequest)GetStandingRequest(userDataResponse.Hash);
            if (standingRequest != null)
            {
                if (userDataResponse.Filename != "" && userDataResponse.Contents != "")
                {
                    standingRequest.Status = 1;
                    standingRequest.Response = userDataResponse;
                    //Debugger.Log($"Set the response {userDataResponse.Filename}, {userDataResponse.Contents}");
                }
                else
                {
                    //Debugger.Log("The server data file was null, writing the defaults to the server");
                    string dataFilename = standingRequest.Request.DataFile;
                    switch (dataFilename)
                    {
                        case ConfigData.UserProgressFilename:
                            UserProgressData userProgressData = ConfigData.GetUserProgressData();
                            userProgressData.GetDataFile().WriteData(userProgressData.GetDefaultJson());
                            break;
                    }
                    //Task.Run(async () =>
                    //{
                    //    await Task.Delay(500);
                    //    standingRequest.Status = -1; // indicates the the request needs to be resent
                    //    standingRequest.Response = userDataResponse;
                    //});
                    

                }
                
                //Debugger.Log("Set the response");
            }
            else
            {
                Debugger.Exception($"Counldn't find a matching request for {userDataResponse.Hash}");
            }
            
        }
        private void HandleSettingsResponse(string message)
        {
            //Debugger.Log("Got user data from server");
            UserDataResponse userDataResponse = JsonUtility.FromJson<UserDataResponse>(message);
            SettingsRequest standingRequest = (SettingsRequest)GetStandingRequest(userDataResponse.Hash);
            if (standingRequest != null)
            {
                if (userDataResponse.Filename != "" && userDataResponse.Contents != "")
                {
                    standingRequest.Status = 1;
                    standingRequest.Response = userDataResponse;
                    //Debugger.Log($"Set the response {userDataResponse.Filename}, {userDataResponse.Contents}");
                }
                else
                {
                    Debugger.Exception($"Null response when requesting settings from the server. {userDataResponse}");

                }

            }
            else
            {
                Debugger.Exception($"Couldn't find a matching request for {userDataResponse.Hash}");
            }

        }
        private void HandleMatchupResponse(string message)
        {
            MatchupStrategyResponse matchupResponse = JsonUtility.FromJson<MatchupStrategyResponse>(message);
            MatchupStrategyRequest standingRequest = (MatchupStrategyRequest)GetStandingRequest(matchupResponse.Hash);
            Squad squad = standingRequest.Squad;
            if (standingRequest != null)
            {
                standingRequest.Status = 1;
                if (squad != null)
                {
                    //squad.Command = squad.gameObject.AddComponent<Command>();
                    //squad.Command.Setup(squad, true);
                    squad.MatchupStrategy = new MatchupStrategy(null, squad, matchupResponse.Name, matchupResponse.MatchupString, matchupResponse.MatchupId, matchupResponse.OutcomeId);

                    //squad.Command.MatchupStrategy = squad.MatchupStrategy;
                    Squad targetSquad = squad.MatchupStrategy.SortSquads();
                    //Debugger.Log($"matchup strategy after sorted");
                    //Debugger.LogSquads(level.GetState().GetSquads());
                    LevelStage level = (LevelStage)_scene;
                    GameState state = level.GetState();
                    if (targetSquad != null && !state.GameOver)
                    {
                        squad.MakeMatchup(targetSquad);
                        //Debugger.Log($"matchup strategy after matchup made");
                        //Debugger.LogSquads(level.GetState().GetSquads());
                    }
                    else
                    {
                        //Debugger.Log("Exception");

                        if (!state.GameOver)
                        {
                            Debugger.Exception($"The squad sorter did not return a valid squad");
                        }

                    }
                }
                else
                {
                    //Debugger.Log("Exception");
                    //Debugger.Log($"matchup strategy #{matchupResponse.StrategyId} was received for squad #{matchupResponse.SquadHash} but that squad no longer exists.");
                }
            }
            else
            {
                Debugger.Exception($"Couldn't find a matching request for {matchupResponse.Hash}");
            }
            
        }  
        private void HandleStrategicCommandResponse(string message)
        {
            CommandResponse commandResponse = JsonUtility.FromJson<CommandResponse>(message);
            CommandRequest standingRequest = (CommandRequest)GetStandingRequest(commandResponse.Hash);

            if (standingRequest != null)
            {
                standingRequest.Status = 1;
                Squad squad = standingRequest.Squad;
                LevelStage level = (LevelStage)_scene;
                GameState state = level.GetState();
                //Debugger.Log($"strategic command response");
                //Debugger.Log(squad.damageSentToEnemyShipsBySquad);
                if (squad != null && !state.GameOver)
                {
                    //Debugger.Log("squad is not null");
                    Command command = null;
                    switch (commandResponse.Name)
                    {
                        case "Aggressive":
                            if (squad.HasOnlyBombers)
                            {
                                command = squad.transform.AddComponent<BombingRun>();
                            }
                            else
                            {
                                command = squad.transform.AddComponent<Aggressive>();
                            }
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case "Defensive":
                            command = squad.transform.AddComponent<Retreat>();
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case "Random":
                            command = squad.transform.AddComponent<MoveToRandom>();
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case "Circle":
                            if (squad.HasOnlyBombers)
                            {
                                command = squad.transform.AddComponent<BombingRun>();
                            }
                            else
                            {
                                command = squad.transform.AddComponent<CircleSquad>();
                            }
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case "Right Swipe":
                        case "Left Swipe":
                            if (squad.HasOnlyBombers)
                            {
                                command = squad.transform.AddComponent<BombingRun>();
                            }
                            else
                            {
                                command = squad.transform.AddComponent<SwipeSquad>();
                            }
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case "Closest Friendly":
                            command = squad.transform.AddComponent<ClosestFriendly>();
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case "In and Out":
                            if (squad.HasOnlyBombers)
                            {
                                command = squad.transform.AddComponent<BombingRun>();
                            }
                            else
                            {
                                command = squad.transform.AddComponent<InAndOut>();
                            }
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case "Patrol":
                            command = squad.transform.AddComponent<Patrol>();
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        case "Guard":
                            command = squad.transform.AddComponent<Guard>();
                            command.Setup(squad, true, standingRequest.Enemy, standingRequest.Matchup);
                            break;
                        default:
                            Debugger.Exception($"commandResponse doesn't match a known command: {commandResponse.Name}");
                            break;
                    }

                    squad.Command = command;
                    squad.Command.MatchupStrategy = squad.MatchupStrategy;

                    Strategy strategy = new Strategy(command, commandResponse.Name, commandResponse.MatchupString, commandResponse.MatchupId, commandResponse.OutcomeId);
                    ShootingStrategy shootingStrategy = new ShootingStrategy(command, commandResponse.ShootingStrategyName, commandResponse.ShootingStrategyMatchupString, commandResponse.ShootingStrategyMatchupId, commandResponse.ShootingStrategyOutcomeId);
                    
                    if (commandResponse.Name == "Patrol")
                    {
                        //Debugger.Log("Got a patrol");
                        ((Patrol)command).Execute(strategy, shootingStrategy, commandResponse.OutcomeId, true, Vector2.zero, Vector2.zero);
                    }
                    else if (commandResponse.Name == "Guard")
                    {
                        ((Guard)command).Execute(strategy, shootingStrategy, commandResponse.OutcomeId, true, null);
                    }
                    else if (commandResponse.Name == "Closest Friendly")
                    {
                        ((ClosestFriendly)command).Execute(strategy, shootingStrategy, commandResponse.OutcomeId, true);
                    }
                    else if (commandResponse.Name == "Random")
                    {
                        ((MoveToRandom)command).Execute(strategy, shootingStrategy, commandResponse.OutcomeId, true);
                    }
                    else
                    {
                        command.Execute(strategy, shootingStrategy, commandResponse.OutcomeId, false);
                    }

                }
                else
                {
                    //Debugger.Log($"Strategic command #{commandResponse.StrategyId} was received for squad #{commandResponse.SquadHash} but that squad no longer exists.");
                }
            }
            else
            {
                Debugger.Log($"Couldn't find a matching request for {commandResponse.Hash}");
            }

        }
        private void HandleBasicResponse(ServerResponse response)
        {
            ServerRequest standingRequest = (ServerRequest)GetStandingRequest(response.Hash);

            if (standingRequest != null)
            {
                standingRequest.Status = 1;
            }
            else
            {
                Debugger.Log($"Couldn't find a matching request for {response.Hash}");
            }

        }

    }

} 