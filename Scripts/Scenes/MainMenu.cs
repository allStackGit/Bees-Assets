using System;

using UnityEngine;

using UnityEngine.SceneManagement;
using Assets.Scripts;

namespace Assets.Scripts.Scenes
{
    public class MainMenu : Scene
    {
        public GameObject MenuPanel, MenuPanelBacker;
        new void Start()
        {
            Name = "Main Menu";
            base.Start();
            //Debugger.Log($"Started {Name} scene");
        }
        public void ContinueGame()
        {
            Debugger.Log($"Continuing Game! User is on level #{ConfigData.GetLevel()}");
            //SceneManager.LoadSceneAsync("Level Intro"); 
            //SceneManager.LoadSceneAsync("Squad Maker");
            DeselectButton();
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
            Debugger.Log("Settings!");
        }

        public void GoToTrainingRoom()
        {
            DeselectButton();
            Debugger.Log("Training Room!");
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
            Debugger.Log("New Game!"); 
        }

        public void ExitGame()
        {
            //ConfigData.SaveAll();
            Debugger.Log("Exiting Game!");
            Application.Quit();
        }

        public void DeselectButton()
        {
            EventSystem.GetComponent<UnityEngine.EventSystems.EventSystem>().SetSelectedGameObject(null);
        }

        private void OnDestroy()
        {
            Debugger.Log("Destroying main menu scene");
            //Debugger.Log("Killing the connection");
            //ConfigData.GetSocket().CloseConnection();   
        }
    }


}
