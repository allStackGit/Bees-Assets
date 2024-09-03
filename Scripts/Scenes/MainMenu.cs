using System;

using UnityEngine;

using UnityEngine.SceneManagement;
using Assets.Scripts;
using System.Collections.Generic;
using TMPro;
using Assets.Scripts.Settings; 
using System.Linq;

namespace Assets.Scripts.Scenes
{
    public class MainMenu : Scene
    {
        public GameObject MenuPanel, MenuPanelBacker;
        public Codex CodexManager;
        new void Start()
        {
            Name = "Main Menu";
            base.Start();
            //Debug.Log($"Started {Name} scene");
        }
        public void ContinueGame()
        {
            Debug.Log($"Continuing Game! User is on level #{ConfigData.GetLevel()}");
            //SceneManager.LoadSceneAsync("Level Intro"); 
            //SceneManager.LoadSceneAsync("Squad Maker");
            DeselectButton();
        }

        protected override void FinalizeSceneWithUserData()
        {
            base.FinalizeSceneWithUserData();
            CodexManager.SetupCodex();
        }

        public void ShowMenuPanel()
        {
            MenuPanel.SetActive(true);
            MenuPanelBacker.SetActive(true);
            DeselectButton();
        }

        public void GoToSettings()
        {
            DeselectButton();
            Debug.Log("Settings!");
        }

        public void GoToTrainingRoom()
        {
            DeselectButton();
            Debug.Log("Training Room!");
            ConfigData.SquadMakerSide = ConfigData.Configuration.SquadMakerFirstSide;
            SceneManager.LoadSceneAsync("Squad Maker", LoadSceneMode.Single);
        }

        public void NewGame()
        {
            // [alert] should give the user an alert saying that this will reset their previous progress, if they've already started a game
            // [alert] should reset user progress data
            ConfigData.SetLevel(1);
            SceneManager.LoadSceneAsync("Level Intro", LoadSceneMode.Single);
            DeselectButton();
            Debug.Log("New Game!"); 
        }
       
        public void ExitGame()
        {
            //ConfigData.SaveAll();
            Debug.Log("Exiting Game!");
            Application.Quit();
        }

        public void DeselectButton()
        {
            EventSystem.GetComponent<UnityEngine.EventSystems.EventSystem>().SetSelectedGameObject(null);
        }

        private void OnDestroy()
        {
            Debug.Log("Destroying main menu scene");
            //Debug.Log("Killing the connection");
            //ConfigData.GetSocket().CloseConnection();   
        }
    }


}
