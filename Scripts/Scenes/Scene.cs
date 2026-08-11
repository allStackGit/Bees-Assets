using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Server;
using Assets.Scripts.Levels;
using Assets.Scripts.UI_Components;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace Assets.Scripts.Scenes
{
    public class Scene : MonoBehaviour
    {
        public string Name = "Base Scene";
        public Camera Camera;
        public UIAudioController UIAudioController;
        public GameObject DialoguePrefab;
        public EventSystem EventSystem;
        public bool IsFinalized, WatchServerRequests, IsSocketManager, IsMainScene;
        //public List<Dialogue> Dialogues = new List<Dialogue>();
        public Dialogue NetworkDisconnection;
        public float TimeScale = 1;
        public Timer SocketTimer, AutomaticReconnectTimer, ResendTimer;
        /// <summary>
        /// The framerate that the application should try to hit. -1 Means syncing it to the monitor refresh rate.
        /// </summary>
        public int TargetFrameRate;
        public ConfigData.SceneTypes Type;

        public int __Updates = 0;
        private int _automaticReconnectAttempts;
        private bool _pausedForNetworkDisconnect;
        private bool _hasShownDeadVersionAlert;


        // Start is called before the first frame update
        protected void Start()
        {
            if (CampaignScenarioIsolation.IsActive)
            {
                enabled = false;
                return;
            }
            //Debug.Log($"Starting {Name} scene");
            ConfigData.Scenes.Add(this);

            if (!ConfigData.HasSocketManager())
            {
                IsSocketManager = true;
                IsMainScene = true;
                ConfigData.SocketManager = this;
                NetworkDisconnection = new Dialogue( DialoguePrefab, "Server disconnected!", "The game needs to be connected to the server in order to function properly.", new List<string>() { "Retry", "Exit Game" }, new List<UnityAction>() { ConfigData.RetryConnection, Exit });
                NetworkDisconnection.SetButtonWidth(1, 100);
                ConfigData.MaxThreads = Mathf.Max(1, SystemInfo.processorCount - 1);

                if (TargetFrameRate > 0)
                {
                    Application.targetFrameRate = TargetFrameRate;
                    Debug.Log($"Target Frame rate set to {Application.targetFrameRate} fps");
                }
                else if (TargetFrameRate == -1)
                {
                    QualitySettings.vSyncCount = 1;
                    Debug.Log($"Target Frane rate set to sync to display at {Screen.currentResolution.refreshRateRatio} fps");
                }
            }
            InvokeRepeating(nameof(LoadSettingsWhenOpen), .1f, .1f);
            SocketTimer = new Timer(.1f, ConfigData.Socket.Update);
            // Request deadlines can differ per request and the configured default is not known
            // when the first scene starts. Poll the cheap deadline check independently of either value.
            ResendTimer = new Timer(1f, ConfigData.Socket.CheckForResends);
            AutomaticReconnectTimer = new Timer(10f, AutomaticConnectionRetry);

            //if (WatchServerRequests)
            //{
            //    InvokeRepeating(nameof(UpdateTestVariables), 1f, 1f);
            //}



        }
        public void LoadSettingsWhenOpen()
        {
            if (ConfigData.Socket.IsOpen)
            {
                ConfigData.LoadSettings();
                CancelInvoke(nameof(LoadSettingsWhenOpen));
            }
        }
        public void Exit()
        {
            Debug.Log("Exiting game!");
            Application.Quit();
        }
        /// <summary>
        /// Finishes setting up the scene when all the user data has been loaded from the server
        /// </summary>
        protected virtual void FinalizeSceneWithUserData()
        {
            //Debug.Log($"Finalizing {Name} Scene");

            if (IsMainScene && ConfigData.CurrentShips == null)
            {
                ConfigData.FreePlayShips = new Ships(ConfigData.GetFleetData(), ConfigData.GetSavedSquadsData());
                ConfigData.CampaignShips = new Ships(ConfigData.GetCampaignFleetData(), ConfigData.GetCampaignSavedSquadsData());
                ConfigData.ChallengeModeShips = new Ships(ConfigData.GetChallengeFleetData(), ConfigData.GetChallengeSavedSquadsData());
                ConfigData.CurrentShips = ConfigData.FreePlayShips;
                ReconcilePersistedIdentityCounters();

            }


            //ConfigData.Ships.ReplaceDeadSquadShips();
            UIAudioController.Instance.PlayMusic();
            IsFinalized = true;
        }

        private static int ReconcileCounterWithIds(int currentCounter, IEnumerable<long> existingIds)
        {
            long maxExistingId = existingIds?.DefaultIfEmpty(currentCounter).Max() ?? currentCounter;
            if (maxExistingId > int.MaxValue)
            {
                throw new InvalidOperationException($"Persisted ID {maxExistingId} exceeds the supported 32-bit counter range.");
            }
            return Math.Max(currentCounter, (int)maxExistingId);
        }

        /// <summary>
        /// Global fleet/squad counters and their objects are persisted in separate files.
        /// Reconcile from the loaded objects before gameplay so a stale/partial checkpoint or
        /// changed starting-fleet definition cannot reuse an existing persistent ID.
        /// </summary>
        private static void ReconcilePersistedIdentityCounters()
        {
            UserProgressData progress = ConfigData.UserProgressData;
            if (progress == null)
            {
                return;
            }

            int safeFleetId = ReconcileCounterWithIds(
                progress.FleetId,
                ConfigData.GetFleetData().GetShips()
                    .Concat(ConfigData.GetCampaignFleetData().GetShips())
                    .Concat(ConfigData.GetChallengeFleetData().GetShips())
                    .Select(ship => ship.Id));

            int safeSavedSquadId = ReconcileCounterWithIds(
                progress.SavedSquadId,
                ConfigData.GetSavedSquadsData().GetSquads()
                    .Concat(ConfigData.GetCampaignSavedSquadsData().GetSquads())
                    .Concat(ConfigData.GetChallengeSavedSquadsData().GetSquads())
                    .Select(squad => (long)squad.Id));

            if (safeFleetId == progress.FleetId && safeSavedSquadId == progress.SavedSquadId)
            {
                return;
            }

            Debug.LogWarning($"Reconciled persisted identity counters: FleetId {progress.FleetId}->{safeFleetId}, SavedSquadId {progress.SavedSquadId}->{safeSavedSquadId}.");
            progress.FleetId = safeFleetId;
            progress.SavedSquadId = safeSavedSquadId;
            progress.Save();
        }
        /// <summary>
        /// Tries to reconnect to the server on a timer whenever the socket remains unopened.
        /// This covers both a previously closed connection and an initial connection attempt that
        /// fails without producing an OnClose callback.
        /// </summary>
        private void AutomaticConnectionRetry()
        {
            if (!IsSocketManager || ConfigData.Socket == null || ConfigData.Socket.KeepClosed ||
                ConfigData.Socket.IsOpen)
            {
                return;
            }

            _automaticReconnectAttempts++;
            Debug.LogWarning($"Trying to automatically reconnect to the server with {Name} (attempt {_automaticReconnectAttempts})");
            ConfigData.RetryConnection();
        }

        private bool AreOpenLevelsReconnected()
        {
            if (Type != ConfigData.SceneTypes.Stage || ConfigData.Socket == null)
            {
                return true;
            }

            return ConfigData.Socket.OpenLevels.All(level =>
                level == null || level.IsLevelConnectedToServer);
        }

        // Update is called once per frame
        protected virtual void Update()
        {
            __Updates++;
            SocketTimer.Update();

            // Do not feed standing requests into a dead WebSocket. They remain in
            // Socket.StandingRequests and the normal resend timer resumes once a connection
            // is open again. Socket.Open first submits ReconnectLevel requests for active levels.
            if (ConfigData.Socket.IsOpen)
            {
                ResendTimer.Update();
            }

            // Retry any socket that remains unopened, including an initial transport failure that
            // reports OnError without OnClose. Keep the disconnect UI below tied to HasClosed so
            // the normal initial connection window does not show a false disconnect dialogue.
            if (IsSocketManager && !ConfigData.Socket.IsOpen && !ConfigData.Socket.KeepClosed)
            {
                AutomaticReconnectTimer.Update();
            }

            if (ConfigData.Socket.HasClosed && IsSocketManager)
            {
                //Debug.Log($"Updating the AutoReconnect Timer. {AutomaticReconnectTimer.Elapsed} seconds have elapsed");

                if (!NetworkDisconnection.IsOpen)
                {
                    Debug.Log($"Network disconnected!");
                    if (Type == ConfigData.SceneTypes.Stage)
                    {
                        Level primaryLevel = ((Stage)this).PrimaryLevel;
                        if (primaryLevel != null && primaryLevel.State != null)
                        {
                            _pausedForNetworkDisconnect = !primaryLevel.State.IsPaused;
                            if (_pausedForNetworkDisconnect)
                            {
                                primaryLevel.Pause();
                            }
                        }
                        else
                        {
                            _pausedForNetworkDisconnect = false;
                        }
                    }
                    NetworkDisconnection.Show();
                }
            }
            
            else if (ConfigData.Socket.IsOpen && IsSocketManager && NetworkDisconnection.IsOpen && AreOpenLevelsReconnected())
            {
                _automaticReconnectAttempts = 0;
                NetworkDisconnection.Hide();
                if (Type == ConfigData.SceneTypes.Stage && _pausedForNetworkDisconnect)
                {
                    ((Stage)this).PrimaryLevel.UnPause();
                }
                _pausedForNetworkDisconnect = false;
            }
            if (!ConfigData.SocketManager.NetworkDisconnection.IsOpen)
            {
                // [alert] [debug]
              
                if (!IsFinalized)
                {
                    if (IsMainScene && ConfigData.AreAllSettingsLoaded && !ConfigData.IsAllUserDataLoaded)
                    {
                        if (ConfigData.Configuration.IsDeadVersion)
                        {
                            if (!_hasShownDeadVersionAlert)
                            {
                                _hasShownDeadVersionAlert = true;
                                Dialogue alert = new Dialogue(DialoguePrefab, "The game is out of date!", "Your version of the game is out of date and needs to be updated.",
                                    new List<string>() { ConfigData.Configuration.OK }, new List<UnityAction>() { Exit });
                                alert.Show();
                            }
                        }
                        else
                        {
                            //Debug.Log($"IsMainScene: {IsMainScene}, ConfigData.AreAllSettingsLoaded {ConfigData.AreAllSettingsLoaded}, !ConfigData.IsAllUserDataLoaded {!ConfigData.IsAllUserDataLoaded}");
                            ConfigData.SetupUserData();
                            ConfigData.CheckDataFiles();
                            ConfigData.SquadMakerSide = ConfigData.Configuration.SquadMakerFirstSide;
                        }
                    }
                    else if (ConfigData.AreAllSettingsLoaded && ConfigData.IsAllUserDataLoaded && !IsFinalized)
                    {

                        FinalizeSceneWithUserData();
                    }
                }
                
            }
            
        }
    }
}
