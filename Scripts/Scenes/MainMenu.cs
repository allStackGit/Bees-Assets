using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Settings; 
using Assets.Scripts.UI_Components;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Scenes
{
    public class MainMenu : Scene
    {
        public GameObject MenuPanel, MenuPanelBacker;
        public GameObject HumanChallengeModeButton, HumanTrainingRoomButton;
        public Codex CodexManager;
        new void Start()
        {
            Name = "Main Menu";
            base.Start();
            //Debug.Log($"Started {Name} scene");
        }
        public void ContinueGame()
        {
            Debug.Log($"Continuing Game! User is on level #{ConfigData.UserProgressData.GetCurrentLevel()}");
            //SceneManager.LoadSceneAsync("Level Intro"); 
            //SceneManager.LoadSceneAsync("Squad Maker");
            DeselectButton();
        }

        protected override void FinalizeSceneWithUserData()
        { 
            base.FinalizeSceneWithUserData();
            if (!ConfigData.UserProgressData.IsHumanChallengeUnlocked)
            {
                HumanChallengeModeButton.SetActive(false);
            }
            if (!ConfigData.UserProgressData.IsHumanFreePlayUnlocked)
            {
                HumanTrainingRoomButton.SetActive(false);
            }
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

        public void GoToTrainingRoom(string side)
        {
            DeselectButton();
            ConfigData.CurrentGameMode = ConfigData.GameModes.FreePlay;
            ConfigData.CurrentShips = ConfigData.FreePlayShips;
            Debug.Log("Training Room!");
            SetupSquadMaker(side);
        }

        public void NewGame()
        {
            // [alert] should give the user an alert saying that this will reset their previous progress, if they've already started a game
            // [alert] should reset user progress data
            ConfigData.UserProgressData.SetCurrentLevel(1);
            SceneManager.LoadSceneAsync("Level Intro", LoadSceneMode.Single);
            DeselectButton();
            Debug.Log("New Game!"); 
        }
       
        public void PlayCampaign(string side)
        {
            DeselectButton();
            ConfigData.CurrentGameMode = ConfigData.GameModes.Campaign;
            ConfigData.CurrentShips = ConfigData.CampaignShips;

            //Debug.Log(ConfigData.UserProgressData.CurrentLevel?.Name);
            //Debug.Log(ConfigData.UserProgressData.GetCurrentLevel());
            ConfigData.UserProgressData.LoadCurrentLevel();

            if (ConfigData.UserProgressData.CurrentLevel.HasPrelevelIntro)
            {
                SetupSquadMaker(side);
            }
            else
            {
                if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide && !ConfigData.UserProgressData.HasStartedHumanCampaign)
                {
                    ConfigData.SetupFirstTimePlayingHumanCampaign();
                }
                ConfigData.LevelOptions = (LevelOptions)ConfigData.UserProgressData.CurrentLevel.Clone();
                if (ConfigData.UserProgressData.GetCurrentLevel() == 0)
                {
                    ConfigData.LevelOptions.ChosenSquads = ConfigData.CurrentShips.GetSavedSquads().Where(s=> s.Id == 0).ToList();
                    //Debug.Log("Setting starting squads for level 0");
                    //Debug.Log(ConfigData.LevelOptions.ChosenSquads.Count + " squads loaded");

                }
                else 
                {
                    //Debug.Log("Loading saved squads for level " + ConfigData.UserProgressData.GetCurrentLevel());
                    ConfigData.LevelOptions.ChosenSquads = ConfigData.CurrentShips.GetSavedSquads().ToList();
                }
                SceneManager.LoadSceneAsync("Hivemind Training", LoadSceneMode.Single);
            }
        }
        public void PlayChallengeMode(string side)
        {
            DeselectButton();
            ConfigData.CurrentGameMode = ConfigData.GameModes.Challenge;
            ConfigData.CurrentShips = ConfigData.ChallengeModeShips;
            SetupSquadMaker(side);
        }

        public void SetupSquadMaker(string side)
        {
            if ((side == "Humans" && ConfigData.Configuration.HumanSide == ConfigData.Configuration.SquadMakerFirstSide) || (side == "Bees" && ConfigData.Configuration.BeeSide == ConfigData.Configuration.SquadMakerFirstSide))
            {
                ConfigData.SquadMakerSide = ConfigData.Configuration.SquadMakerFirstSide;
            }
            else
            {
                ConfigData.SwapSides();
            }
            SceneManager.LoadSceneAsync("Squad Maker", LoadSceneMode.Single);
        }

        public void ExitGame()
        {
            //ConfigData.SaveAll();
            DeselectButton();
            Debug.Log("Exiting Game!");
            Application.Quit();
        }

        public void DeselectButton()
        {
            UIAudioController.Instance.PlayButtonSound();
            EventSystem.GetComponent<UnityEngine.EventSystems.EventSystem>().SetSelectedGameObject(null);
        }

        private void OnDestroy()
        {
            //Debug.Log("Destroying main menu scene");
            //Debug.Log("Killing the connection");
            //ConfigData.GetSocket().CloseConnection();   
        }
    }


}
