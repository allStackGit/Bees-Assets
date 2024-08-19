using Assets.Scripts;
using Assets.Scripts.Server;
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
        public GameObject DialoguePrefab;
        public EventSystem EventSystem;
        public bool FinalizedScene, WatchServerRequests, IsSocketManager;
        //public List<Dialogue> Dialogues = new List<Dialogue>();
        public Dialogue NetworkDisconnection;
        public float TimeScale = 1;
        public Timer Timer;




        public List<string> __PastServerRequests;
        /// <summary>
        /// The average time a request takes to complete in ms
        /// </summary>
        public float __AverageRequestTime;
        public List<long> __UsedHashes;
        public int __Updates = 0;


        // Start is called before the first frame update
        protected void Start()
        {
            ConfigData.MaxThreads = SystemInfo.processorCount - 1;
            //Debug.Log($"Starting {Name} scene");
            ConfigData.Scenes.Add(this);
            
            if (!ConfigData.HasSocketManager())
            {
                IsSocketManager = true;
                ConfigData.SocketManager = this;
                NetworkDisconnection = new Dialogue(DialoguePrefab, "Server disconnected!", "The game needs to be connected to the server in order to function properly.",
                                               new List<string>() { "Retry", "Exit Game" }, new List<UnityAction>() { ConfigData.RetryConnection, Exit });
            }
            InvokeRepeating(nameof(LoadSettingsWhenOpen), .1f, .1f);
            Timer = new Timer(.1f, ConfigData.Socket.Update);
            if (WatchServerRequests)
            {
                InvokeRepeating(nameof(UpdateTestVariables), 1f, 1f);
            }



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

        protected virtual void FinalizeSceneWithUserData()
        {
            //Debug.Log($"Finalizing {Name} Scene");
            ConfigData.AllShips = new Ships(ConfigData.GetFleetData(), ConfigData.GetSavedSquadsData());

            //ConfigData.Ships.ReplaceDeadSquadShips();
            FinalizedScene = true;
        }
        protected void UpdateTestVariables()
        {
            if (ConfigData.__PastServerRequests.Count > 0)
            {
                __UsedHashes = ConfigData.UsedHashes.ToList();
                __PastServerRequests = ConfigData.__PastServerRequests.Select((r) => $"Request #{r.Hash} ({r.Type}) on queue for {r.TimeOnQueue * 1000}ms with {r.Resends} resends.").ToList();
                __AverageRequestTime = (ConfigData.__PastServerRequests.Sum((r) => r.TimeOnQueue) / ConfigData.__PastServerRequests.Count) * 1000;
                //__Updates = Time.frameCount;
            }


        }
        // Update is called once per frame
        protected void Update()
        {
            Timer.Update();
            

            if (ConfigData.Socket.HasClosed && IsSocketManager && !NetworkDisconnection.IsOpen)
            {
                NetworkDisconnection.Show();
            }
            else if (ConfigData.Socket.IsOpen && IsSocketManager && NetworkDisconnection.IsOpen)
            {
                NetworkDisconnection.Hide();
            }
            if (!ConfigData.SocketManager.NetworkDisconnection.IsOpen)
            {
                // [alert] [debug]
              

                if (ConfigData.AreAllSettingsLoaded && !ConfigData.IsAllUserDataLoaded)
                {
                    if (ConfigData.Configuration.IsDeadVersion)
                    {
                        Dialogue alert = new Dialogue(DialoguePrefab, "The game is out of date!", "Your version of the game is out of date and needs to be updated.",
                            new List<string>() {ConfigData.Configuration.OK},new List<UnityAction>() {Exit});
                        alert.Show();
                    }
                    else
                    {
                        ConfigData.SetupUserData();
                        ConfigData.CheckDataFiles();
                        ConfigData.SquadMakerSide = ConfigData.Configuration.SquadMakerFirstSide;
                    }
                }
                else if (ConfigData.AreAllSettingsLoaded && ConfigData.IsAllUserDataLoaded && !FinalizedScene)
                {

                    FinalizeSceneWithUserData();
                }
            }
            
        }
    }
}

