using Assets.Scripts;
using Assets.Scripts.Data;
using Assets.Scripts.Levels;
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
        public GameObject MenuPanel, MenuPanelBacker, CampaignRow, ChallengeRow, TrainingRoomRow, BeesTrainingRoomRow;
        public GameObject HumanChallengeModeButton, HumanTrainingRoomButton, BeeFreePlayButton, HumanCampaignModeButton, CommanderNameDialogue, ResetCampaignButton, ResetChallengeModeButton, ResetTrainingRoomButton, ResetBeesTrainingRoomButton, FishTankButton, CampaignScore, ChallengeScore;
        public TMP_InputField NameInput;
        public TMP_Text CampaignScoreText, ChallengeScoreText;
        public Codex CodexManager;
        public Dialogue ResetConfirmation, ViewToolTipsConfirmation;
        public bool IsResettingCampaign, IsResettingChallenge, IsResettingTrainingRoom, IsResettingBeesTrainingRoom;
        new void Start()
        {
            Name = "Main Menu";
            // Serialized modal instances must never be visible during asynchronous bootstrap.
            // FinalizeSceneWithUserData explicitly decides whether a genuinely new profile needs
            // the commander-name prompt once the server/local save state is known.
            CommanderNameDialogue?.SetActive(false);
            base.Start();
            //Debug.Log($"Started {Name} scene");
        }
        public void ContinueGame()
        {
            Debug.Log($"Continuing Game! User is on level #{ConfigData.UserProgressData.GetCurrentLevel(ConfigData.Configuration.UserSide)}");
            //SceneManager.LoadSceneAsync("Level Intro"); 
            //SceneManager.LoadSceneAsync("Squad Maker");
            DeselectButton();
        }

        protected override void FinalizeSceneWithUserData()
        { 
            base.FinalizeSceneWithUserData();
            CampaignScoreText.text = $"Campaign Score: {ConfigData.UserProgressData.CampaignScore}";


            if (!ConfigData.UserProgressData.IsHumanChallengeUnlocked)
            {
                HumanChallengeModeButton.SetActive(false);
                ResetChallengeModeButton.SetActive(false);
                ChallengeRow.SetActive(false);
                ChallengeScore.SetActive(false);
            }
            else
            {
                ChallengeScoreText.text = $"Challenge Score: {ConfigData.UserProgressData.ChallengeScore}";
            }
            if (!ConfigData.UserProgressData.IsHumanFreePlayUnlocked)
            {
                HumanTrainingRoomButton.SetActive(false);
                ResetTrainingRoomButton.SetActive(false);
                TrainingRoomRow.SetActive(false);
            }
            if (!ConfigData.UserProgressData.IsBeeFreePlayUnlocked)
            {
                BeeFreePlayButton.SetActive(false);
                ResetBeesTrainingRoomButton.SetActive(false);
                BeesTrainingRoomRow.SetActive(false);
            }
            if (!ConfigData.UserProgressData.IsFishTankUnlocked)
            {
                FishTankButton.SetActive(false);
            }
            CodexManager.SetupCodex();
            int currentLevel;

             // Campaign
            currentLevel = ConfigData.UserProgressData.GetCurrentLevel(ConfigData.Configuration.HumanSide, ConfigData.GameModes.Campaign);

            if (CampaignMissionCatalog.IsCampaignComplete(currentLevel))
            {
                SetCampaignCompleteState();
            }

            if (!IsResettingCampaign)
            {
                ResetConfirmation = new Dialogue(DialoguePrefab, ConfigData.Configuration.AreYouSure, "This will set you back to the beginning of the campaign.",
                new List<string>() { ConfigData.Configuration.Yes, ConfigData.Configuration.No }, new List<UnityAction>() { ResetCampaign });
            }

            else if (IsResettingCampaign)
            {
                ConfigData.CampaignShips = new Ships(ConfigData.GetCampaignFleetData(), ConfigData.GetCampaignSavedSquadsData());
                if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
                {
                    ConfigData.CurrentShips = ConfigData.CampaignShips;
                }
                HumanCampaignModeButton.transform.GetChild(0).GetComponent<TMP_Text>().text = "Play Campaign";
                HumanCampaignModeButton.GetComponent<Button>().enabled = true;
                IsResettingCampaign = false;
            }


            // Challenge Mode
            currentLevel = ConfigData.UserProgressData.GetCurrentLevel(ConfigData.Configuration.HumanSide, ConfigData.GameModes.Challenge);
            int challengeLevels = ConfigData.GetChallengeLevelData().GetLevels().Count;
            //Debug.Log($"Current Challenge Level: {currentLevel}, Total Levels: {challengeLevels}");

            if (currentLevel >= challengeLevels)
            {
                HumanChallengeModeButton.transform.GetChild(0).GetComponent<TMP_Text>().text = "Challenge Mode Completed!";
                HumanChallengeModeButton.GetComponent<Button>().enabled = false;
            }


            if (!IsResettingChallenge)
            {
                ResetChallengeModeButton.SetActive(true);
            }

            else if (IsResettingChallenge)
            {
                ConfigData.ChallengeModeShips = new Ships(ConfigData.GetChallengeFleetData(), ConfigData.GetChallengeSavedSquadsData());
                if (ConfigData.CurrentGameMode == ConfigData.GameModes.Challenge)
                {
                    ConfigData.CurrentShips = ConfigData.ChallengeModeShips;
                }
                HumanChallengeModeButton.transform.GetChild(0).GetComponent<TMP_Text>().text = "Play Challenge Mode";
                HumanChallengeModeButton.GetComponent<Button>().enabled = true;
                IsResettingChallenge = false;
            }

            // Training room

            if (ConfigData.UserProgressData.IsHumanFreePlayUnlocked && !IsResettingTrainingRoom)
            {
                ResetTrainingRoomButton.SetActive(true);
            }

            // Bees Training room

            if (ConfigData.UserProgressData.IsBeeFreePlayUnlocked && !IsResettingBeesTrainingRoom)
            {
                ResetBeesTrainingRoomButton.SetActive(true);
            }

            DataFile progressFile = ConfigData.UserProgressData.GetDataFile();
            bool createdRemoteProfile = !ConfigData.Configuration.UseLocalStorage && progressFile.WasCreatedFromMissingStorage;
            bool createdLocalProfile = ConfigData.Configuration.UseLocalStorage && ConfigData.FirstTimePlaying;
            bool needsCommanderName = string.IsNullOrWhiteSpace(ConfigData.UserProgressData.PlayerName) &&
                (createdRemoteProfile || createdLocalProfile);
            CommanderNameDialogue?.SetActive(needsCommanderName);

        }
        public void SubmitName()
        {
            string name = NameInput.text;
            name = Utilities.ValidateInputString(name);
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


        public void GoToFishTank()
        {
            DeselectButton();
            ConfigData.LevelOptions = null;
            ConfigData.CurrentGameMode = ConfigData.GameModes.FishTank;
            ConfigData.CurrentShips = ConfigData.FreePlayShips;
            SceneManager.LoadSceneAsync("Hivemind Training", LoadSceneMode.Single);
        }

        public void ConfirmPlayCampaign()
        {
            if (ConfigData.UserProgressData.HasPlayedBefore && ConfigData.UserProgressData.ShowToolTips)
            {
                ViewToolTipsConfirmation = new Dialogue(DialoguePrefab, "This isn't your first rodeo is it, space cowboy?", "It looks like you've played before. Would you like to disable tooltips?",
                new List<string>() { ConfigData.Configuration.Yes, ConfigData.Configuration.No }, new List<UnityAction>() { DisableTooltips, PlayCampaign,  });
                ViewToolTipsConfirmation.Show();
            }
            else
            {
                Debug.Log("Hasn't played before, or has disabled tooltips, playing Campaign");
                PlayCampaign();
            }
        }

        public void DisableTooltips()
        {
            Debug.Log("Disabling tooltips");
            ConfigData.UserProgressData.ShowToolTips = false;
            ConfigData.UserProgressData.Save();
            PlayCampaign();
        }
       
        public void PlayCampaign()
        {
            Debug.Log("Playing Campaign!");
            HumanCampaignModeButton.GetComponent<Button>().enabled = false;
            if (ConfigData.Configuration.UserSide != ConfigData.Configuration.HumanSide)
            {
                ConfigData.SwapSides();
            }
            DeselectButton();
            ConfigData.CurrentGameMode = ConfigData.GameModes.Campaign;
            ConfigData.CurrentShips = ConfigData.CampaignShips;

            int currentLevel = ConfigData.UserProgressData.GetCurrentLevel(
                ConfigData.Configuration.UserSide,
                ConfigData.GameModes.Campaign);
            if (CampaignMissionCatalog.IsCampaignComplete(currentLevel))
            {
                SetCampaignCompleteState();
                return;
            }

            // If this is the first time the user is playing the human campaign, set up their first level and load it right away
            if (ConfigData.Configuration.UserSide == ConfigData.Configuration.HumanSide && !ConfigData.UserProgressData.HasStartedHumanCampaign)
            {
                ConfigData.UserProgressData.GetCurrentLevelOptions();
                ConfigData.SetupFirstTimePlayingHumanCampaign();
                ConfigData.LevelOptions = (LevelOptions)ConfigData.UserProgressData.CurrentLevel.Clone();
                SceneManager.LoadSceneAsync("Space", LoadSceneMode.Single);
            }
            else
            {
                ConfigData.LoadLevel();
            }
        }

        private void SetCampaignCompleteState()
        {
            HumanCampaignModeButton.transform.GetChild(0).GetComponent<TMP_Text>().text = "Beta Campaign Completed!";
            HumanCampaignModeButton.GetComponent<Button>().enabled = false;
        }

        public void ConfirmResetCampaign()
        {
            ResetConfirmation.SetExplanation("This will set you back to the beginning of the campaign.");
            ResetConfirmation.ChangeButton(0, ConfigData.Configuration.Yes, ResetCampaign);
            ResetConfirmation.Show();
        }
        public void ConfirmResetChallenge()
        {
            ResetConfirmation.SetExplanation("This will set you back to the beginning of the challenge mode.");
            ResetConfirmation.ChangeButton(0, ConfigData.Configuration.Yes, ResetChallenge);
            ResetConfirmation.Show();
        }
        public void ConfirmResetTrainingRoom()
        {
            ResetConfirmation.SetExplanation("This will reset all of your ships and squads for the training room.");
            ResetConfirmation.ChangeButton(0, ConfigData.Configuration.Yes, ResetTrainingRoom);
            ResetConfirmation.Show();
        }

        public void ConfirmResetBeesTrainingRoom()
        {
            ResetConfirmation.SetExplanation("This will reset all of your ships and squads for the Bees training room.");
            ResetConfirmation.ChangeButton(0, ConfigData.Configuration.Yes, ResetBeesTrainingRoom);
            ResetConfirmation.Show();
        }

        public void ResetCampaign()
        {
            Dictionary<ConfigData.ShipTypes, int> allCampaignStartingShips = new Dictionary<ConfigData.ShipTypes, int>();
            ConfigData.StartingSettings.HumanCampaignStartingShips.ToList().ForEach((s) => allCampaignStartingShips.Add(s.Key, s.Value));
            ConfigData.StartingSettings.BeeCampaignStartingShips.ToList().ForEach((s) => allCampaignStartingShips.Add(s.Key, s.Value));

            ConfigData.UserProgressData.HumanCampaignWins = 0;
            ConfigData.UserProgressData.BeeCampaignWins = 0;
            ConfigData.UserProgressData.CurrentHumanCampaignLevel = 0;
            ConfigData.UserProgressData.CurrentBeeCampaignLevel = 0;
            ConfigData.UserProgressData.HasStartedHumanCampaign = false;
            ConfigData.UserProgressData.HumanCampaignSavedSquadNumber = 0;
            ConfigData.UserProgressData.BeeCampaignSavedSquadNumber = 0;
            ConfigData.UserProgressData.HasMetAlejandraAndEmilia = false;
            ConfigData.UserProgressData.HasSeenBuildInterface = false;
            ConfigData.UserProgressData.MinedTSV = 0;
            ConfigData.UserProgressData.CampaignScore = 0;
            ConfigData.UserProgressData.HasPlayedBefore = true;
            ConfigData.UserProgressData.UnlockedCampaignShips = new HashSet<ConfigData.ShipTypes> { ConfigData.ShipTypes.Scout, ConfigData.ShipTypes.Gunship };
            ConfigData.HasSeenPreLevelIntro = false;
            ConfigData.HasSeenIntermission = false;
            TitaniaRouteState.ResetForCampaignRestart();

            ConfigData.IsSavedSquadsDataLoaded[1] = false;
            ConfigData.IsFleetDataLoaded[1] = false;
            ConfigData.IsLoadingUserData = true;
            IsFinalized = false;
            IsResettingCampaign = true;
            ConfigData.CampaignShips = null;

            HumanCampaignModeButton.transform.GetChild(0).GetComponent<TMP_Text>().text = "Resetting...";
            HumanCampaignModeButton.GetComponent<Button>().enabled = false;

            // This is an intentional destructive reset, not startup. Bypass the normal remote-first
            // protection so the constructors build fresh defaults immediately instead of reading the
            // old server rows back into the campaign we just reset.
            ConfigData.SetupCampaignFleetData(false, allCampaignStartingShips, forceCreateDefaults: true);
            ConfigData.SetupCampaignSavedSquadsData(false, forceCreateDefaults: true);
            ConfigData.UserProgressData.Save(); // Save this after the others so changes to fleet and squad ID are saved
        }

        public void ResetChallenge()
        {

            Dictionary<ConfigData.ShipTypes, int> allChallengeStartingShips = new Dictionary<ConfigData.ShipTypes, int>();
            ConfigData.StartingSettings.HumanChallengeStartingShips.ToList().ForEach((s) => allChallengeStartingShips.Add(s.Key, s.Value));
            ConfigData.StartingSettings.BeeChallengeStartingShips.ToList().ForEach((s) => allChallengeStartingShips.Add(s.Key, s.Value));

            ConfigData.UserProgressData.HumanChallengeWins = 0;
            ConfigData.UserProgressData.BeeChallengeWins = 0;
            ConfigData.UserProgressData.CurrentHumanChallengeLevel = 0;
            ConfigData.UserProgressData.CurrentBeeChallengeLevel = 0;
            ConfigData.UserProgressData.HumanChallengeSavedSquadNumber = 0;
            ConfigData.UserProgressData.BeeChallengeSavedSquadNumber = 0;
            ConfigData.UserProgressData.ChallengeScore = 0;


            ConfigData.IsSavedSquadsDataLoaded[2] = false;
            ConfigData.IsFleetDataLoaded[2] = false;
            ConfigData.IsLoadingUserData = true;
            IsFinalized = false;
            IsResettingChallenge = true;
            ConfigData.ChallengeModeShips = null;

            ConfigData.SetupChallengeFleetData(false, allChallengeStartingShips, forceCreateDefaults: true);
            ConfigData.SetupChallengeSavedSquadsData(false, forceCreateDefaults: true);
            ConfigData.UserProgressData.Save(); // Save this after the others so changes to fleet and squad ID are saved


            ResetChallengeModeButton.SetActive(false);
        }
        public void ResetTrainingRoom()
        {
            ConfigData.UserProgressData.HumanFreePlayWins = 0;
            ConfigData.UserProgressData.BeeFreePlayWins = 0;
            ConfigData.UserProgressData.HumanFreePlaySavedSquadNumber = 0;
            ConfigData.UserProgressData.BeeFreePlaySavedSquadNumber = 0;


            ConfigData.GetSavedSquadsData().GetSquads().ToList().ForEach((squad) => {
                if (squad.Side == ConfigData.Configuration.HumanSide)
                {
                    squad.GetSquadShips().ToList().ForEach((ship) =>
                    {
                        squad.RemoveShipFromSquad(ship, false);
                    });
                    ConfigData.GetSavedSquadsData().RemoveSquadFromList(squad);
                }
            });

            List<FleetShip> shipList = ConfigData.GetFleetData().GetShips();
            for (int i = 0; i < shipList.Count; i++)
            {
                if (shipList[i].Side == ConfigData.Configuration.HumanSide)
                {
                    shipList[i] = new FleetShip(ConfigData.UserProgressData.GetNextFleetId(), shipList[i].Type, false, false, 0, 0, 0, 0, 0, 0, 0);
                }
            }


            ConfigData.GetFleetData().Save();
            ConfigData.GetSavedSquadsData().Save();
            ConfigData.UserProgressData.Save(); // Save this after the others so changes to fleet and squad ID are saved

            ConfigData.FreePlayShips = new Ships(ConfigData.GetFleetData(), ConfigData.GetSavedSquadsData());

        }
        public void ResetBeesTrainingRoom()
        {
            ConfigData.UserProgressData.HumanFreePlayWins = 0;
            ConfigData.UserProgressData.BeeFreePlayWins = 0;
            ConfigData.UserProgressData.HumanFreePlaySavedSquadNumber = 0;
            ConfigData.UserProgressData.BeeFreePlaySavedSquadNumber = 0;


            ConfigData.GetSavedSquadsData().GetSquads().ToList().ForEach((squad) => {
                if (squad.Side == ConfigData.Configuration.BeeSide)
                {
                    squad.GetSquadShips().ToList().ForEach((ship) =>
                    {
                        squad.RemoveShipFromSquad(ship, false);
                    });
                    ConfigData.GetSavedSquadsData().RemoveSquadFromList(squad);
                }
            });
            
            List<FleetShip> shipList = ConfigData.GetFleetData().GetShips();
            for (int i = 0; i < shipList.Count; i++)
            {
                if (shipList[i].Side == ConfigData.Configuration.BeeSide)
                {
                    shipList[i] = new FleetShip(ConfigData.UserProgressData.GetNextFleetId(), shipList[i].Type, false, false, 0, 0, 0, 0, 0, 0, 0);
                }   
            }



            ConfigData.GetFleetData().Save();
            ConfigData.GetSavedSquadsData().Save();
            ConfigData.UserProgressData.Save(); // Save this after the others so changes to fleet and squad ID are saved

            ConfigData.FreePlayShips = new Ships(ConfigData.GetFleetData(), ConfigData.GetSavedSquadsData());

        }
        public void PlayChallengeMode(string side)
        {
            if (ConfigData.Configuration.UserSide != ConfigData.Configuration.HumanSide)
            {
                ConfigData.SwapSides();
            }
            DeselectButton();
            ConfigData.CurrentGameMode = ConfigData.GameModes.Challenge;
            ConfigData.CurrentShips = ConfigData.ChallengeModeShips;
            //Debug.Log($"Current Ships: {ConfigData.CurrentShips.ShipListType}");
            //SetupSquadMaker(side);

            if (ConfigData.UserProgressData.GetCurrentLevel(ConfigData.Configuration.UserSide) == 0)
            {
                SceneManager.LoadSceneAsync("Level Intro", LoadSceneMode.Single);
            }
            else
            {
                SetupSquadMaker(side);
            }
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