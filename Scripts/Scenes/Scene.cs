using Assets.Scripts;
using Assets.Scripts.Server;
using Assets.Scripts.UI_Components;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
        public Socket Socket;
        public bool FinalizedScene, WatchServerRequests, HasIndependentSocket;
        //public List<Dialogue> Dialogues = new List<Dialogue>();
        public Dialogue NetworkDisconnection;
        public float TimeScale = 1;
        public long Updates = 0;




        public List<string> __PastServerRequests;
        public float __AverageRequestTime;
        public List<long> __UsedHashes;

        // Start is called before the first frame update
        protected void Start()
        {
            //Debug.Log($"Starting {Name} scene");
            if (HasIndependentSocket || ConfigData.MainSocket == null)
            {
                Socket = ConfigData.Test ? new Socket(ConfigData.TestPort, ConfigData.TestServerHostname, ConfigData.UseWebSocketSharp) : new Socket(ConfigData.DevelopmentPort, ConfigData.DevelopmentServerHostname, ConfigData.UseWebSocketSharp);
            }
            else if (ConfigData.MainSocket != null)
            {
                Socket = ConfigData.MainSocket;
            }


            Socket.SetScene(this);
            InvokeRepeating(nameof(LoadSettingsWhenOpen), .1f, .1f);
            if (NetworkDisconnection == null)
            {
                Debug.Log("Setting Network disconnection dialogue");
                NetworkDisconnection = new Dialogue(DialoguePrefab, "Server disconnected!", "The game needs to be connected to the server in order to function properly.",
                                            new List<string>() { "Retry", "Exit Game" }, new List<UnityAction>() { RetryConnection, Exit });
            }


        }
        public void LoadSettingsWhenOpen()
        {
            if (Socket.IsOpen)
            {
                ConfigData.LoadSettings(this);
                CancelInvoke(nameof(LoadSettingsWhenOpen));
            }
        }
        public void Exit()
        {
            Debug.Log("Exiting game!");
            Application.Quit();
        }
        protected virtual void RetryConnection()
        {
            Socket.MakeSocket();
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
                __PastServerRequests = ConfigData.__PastServerRequests.Select((r) => $"Request #{r.Hash} ({r.Type}) on queue for {r.TimeOnQueue}ms with {r.Resends} resends.").ToList();
                __AverageRequestTime = ConfigData.__PastServerRequests.Select((r) => r.TimeOnQueue).Sum() / ConfigData.__PastServerRequests.Count;
            }


        }
        // Update is called once per frame
        protected void Update()
        {
            Updates++;
            if (Updates%10 == 0)
            {
                Socket.Update();
            }

            if (Socket.HasClosed && !NetworkDisconnection.IsOpen)
            {
                NetworkDisconnection.Show();
            }
            else if (Socket.IsOpen && NetworkDisconnection.IsOpen)
            {
                NetworkDisconnection.Hide();
            }
            if (!NetworkDisconnection.IsOpen)
            {
                // [alert] [debug]
                
                if (WatchServerRequests && Updates%250 == 0)
                {
                    UpdateTestVariables();
                }

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
                        ConfigData.SetupUserData(this);
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

