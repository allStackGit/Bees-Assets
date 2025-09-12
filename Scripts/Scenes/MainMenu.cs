using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Settings; 
using Assets.Scripts.UI_Components;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.Scenes
{
    public class MainMenu : Scene
    {
        public GameObject MenuPanel, MenuPanelBacker;
        public GameObject HumanChallengeModeButton, HumanTrainingRoomButton, BeeFreePlayButton, HumanCampaignModeButton, CommanderNameDialogue, ResetCampaignButton, ResetChallengeModeButton;
        public TMP_InputField NameInput;
        public Codex CodexManager;
        public Dialogue ResetConfirmation;
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
                ResetChallengeModeButton.SetActive(false);
            }
            if (!ConfigData.UserProgressData.IsHumanFreePlayUnlocked)
            {
                HumanTrainingRoomButton.SetActive(false);
            }
            if (!ConfigData.UserProgressData.IsBeeFreePlayUnlocked)
            {
                BeeFreePlayButton.SetActive(false);
            }
            CodexManager.SetupCodex();

            int currentLevel = ConfigData.UserProgressData.GetCurrentLevel(ConfigData.GameModes.Campaign);
            if (currentLevel >= ConfigData.Configuration.TotalLevels)
            {
                HumanCampaignModeButton.transform.GetChild(0).GetComponent<TMP_Text>().text = "Beta Campaign Completed!";
                HumanCampaignModeButton.GetComponent<Button>().enabled = false;
            }

            if (ConfigData.UserProgressData.PlayerName == "")
            {
                CommanderNameDialogue.SetActive(true);
            }
            if (currentLevel > 1)
            {
                ResetConfirmation = new Dialogue(DialoguePrefab, ConfigData.Configuration.AreYouSure, "This will set you back to the beginning of the campaign.",
                new List<string>() { ConfigData.Configuration.Yes, ConfigData.Configuration.No }, new List<UnityAction>() { ResetCampaign });
            }
        }
        public void SubmitName()
        {
            string name = NameInput.text;
            Debug.Log($"Name: {name}");
            if (name.Trim().Length > 0)
            {
                CommanderNameDialogue.SetActive(false);
                ConfigData.UserProgressData.PlayerName = name;
                ConfigData.UserProgressData.Save();
            }
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

            // If this is the first time the user is playing the human campaign, set up their first level and load it right away
            if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide && !ConfigData.UserProgressData.HasStartedHumanCampaign)
            {
                ConfigData.UserProgressData.GetCurrentLevelOptions();
                ConfigData.SetupFirstTimePlayingHumanCampaign();
                ConfigData.LevelOptions = (LevelOptions)ConfigData.UserProgressData.CurrentLevel.Clone();
                ConfigData.LevelOptions.ChosenSquads = ConfigData.CurrentShips.GetSavedSquads().Where(s => s.Id == 0).ToList();
                SceneManager.LoadSceneAsync("Hivemind Training", LoadSceneMode.Single);
            }
            else
            {
                ConfigData.LoadLevel();
            }
        }
        public void ConfirmResetCampaign()
        {
            ResetConfirmation.Show();
        }
        public void ResetCampaign()
        {
            Dictionary<ConfigData.ShipTypes, int> allStartingShips = new Dictionary<ConfigData.ShipTypes, int>();
            ConfigData.StartingSettings.HumanStartingShips.ToList().ForEach((s) => allStartingShips.Add(s.Key, s.Value));
            ConfigData.StartingSettings.BeeStartingShips.ToList().ForEach((s) => allStartingShips.Add(s.Key, s.Value));

            Dictionary<ConfigData.ShipTypes, int> allCampaignStartingShips = new Dictionary<ConfigData.ShipTypes, int>();
            ConfigData.StartingSettings.HumanCampaignStartingShips.ToList().ForEach((s) => allCampaignStartingShips.Add(s.Key, s.Value));
            ConfigData.StartingSettings.BeeCampaignStartingShips.ToList().ForEach((s) => allCampaignStartingShips.Add(s.Key, s.Value));

            ConfigData.UserProgressData.HumanCampaignWins = 0;
            ConfigData.UserProgressData.BeeCampaignWins = 0;
            ConfigData.UserProgressData.CurrentHumanCampaignLevel = 0;
            ConfigData.UserProgressData.HasStartedHumanCampaign = false;
            ConfigData.UserProgressData.HumanCampaignSavedSquadNumber = 0;
            ConfigData.UserProgressData.BeeCampaignSavedSquadNumber = 0;
            ConfigData.UserProgressData.HasMetAlejandraAndEmilia = false;
            ConfigData.UserProgressData.HasSeenBuildInterface = false;
            ConfigData.UserProgressData.MinedTSV = 0;

            ConfigData.UserProgressData.Save();
            ConfigData.SetupCampaignFleetData(true, allCampaignStartingShips);
            ConfigData.SetupCampaignSavedSquadsData(true);
            ConfigData.SetupCampaignLevelData(true);

            ResetCampaignButton.SetActive(false);
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
