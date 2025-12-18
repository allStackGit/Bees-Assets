using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using Assets.Scripts.UI_Components;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.SocialPlatforms;
using UnityEngine.UI;
using static UnityEngine.GraphicsBuffer;

namespace Assets.Scripts.UIComponents
{
    public class GameMenus : MonoBehaviour
    {
        public GameObject MenuContainer, LevelEndedDialogue, SaveLevelDialogue, NoAliveShipsAlert, SquadActionBoxUI, VictoryLabel, DefeatLabel, MiniMapCloseButton, MiniMapOpenButton, MiniMapTopBorder, MiniMapLeftBorder, MiniMapCameraCollider, MiniMapOutput, HumanScore, BeeScore, ShipInfoBox, KeepGoingButton, ToggleFogOfWarButton, RestartLevelButton, ChooseNewSquadsButton, SwitchSidesButton, SaveAsLevelButton, ExitToMainMenuButton, Scoreboard, TooltipPrefab, WASDTooltip, UIOverlay, HighlightTooltipPrefab, UIHighlightTooltipPrefab, PointerArrow, PlutoCircle, Clock, Counter, RetreatButton, MineralsMinedStatus, MineralsMinedFiller, MissionStatus, PlutoShield, PlutoShieldHealthBar, GameSpeedButton, PausePanel, SummaryPanel;
        public SquadActionBox ActionBox;
        public Level CurrentLevel;
        public Stage Stage;
        public Dialogue ExitConfirmationDialogue, CampaignLevelEndedDialogue, CampaignCompletedDialogue, ConfirmSurrenderDialogue;
        public TMP_Text ShipInfoBoxTitle, ShipInfoBoxStats, TryNewSquadsButtonText, MineralsMinedCount, MissionStatusText, GameSpeedButtonText, ShipsDestroyedText, ShipsReturnedText, ShipsLostText, NewShipsReceivedText, ScoreText, MineralsReceivedText;
        public TMP_InputField LevelNameInput, SupplyCapacityInput;
        public Codex Codex;
        public Controls Controls;
        public SettingsMenu Settings;
        public bool IsMiniMapOpen;

        public bool HoveringOverMiniMapButton;
        public bool IsSquadActionBoxOpen => ActionBox != null && SquadActionBoxUI.activeSelf;
        public bool HasSquadActionBox => !Stage.IsTraining && ActionBox != null && CurrentLevel.CurrentLevelOptions.HasSquadActionBox;


        public void Setup(Stage stage)
        {
            Stage = stage;
            CurrentLevel = stage.PrimaryLevel;
            if (!Stage.IsTraining)
            {
                ActionBox = SquadActionBoxUI.GetComponent<SquadActionBox>();
                Codex.SetupCodex();
                //Settings.SetupSettings(Stage);
                
                if (ConfigData.CurrentGameMode != ConfigData.GameModes.FreePlay && ConfigData.CurrentGameMode != ConfigData.GameModes.FishTank)
                {
                    ToggleFogOfWarButton.SetActive(false);
                    RestartLevelButton.SetActive(false);
                    ChooseNewSquadsButton.SetActive(false);
                    SwitchSidesButton.SetActive(false);
                    SaveAsLevelButton.SetActive(false);
                    ExitToMainMenuButton.SetActive(false);
                }
            }

            IsMiniMapOpen = MiniMapCloseButton.activeSelf;

            ExitConfirmationDialogue = new Dialogue(Stage.DialoguePrefab, ConfigData.Configuration.AreYouSureExit, ConfigData.Configuration.LevelProgressLost, new List<string>() { ConfigData.Configuration.Yes, ConfigData.Configuration.No }, new List<UnityAction>() { ExitToMainMenu });
            ExitConfirmationDialogue.SetTextBoxHeight(200);

            CampaignLevelEndedDialogue = new Dialogue(Stage.DialoguePrefab, "Level Complete", "Do you want to continue?", new List<string>() { ConfigData.Configuration.Yes, "Exit to Main Menu" }, new List<UnityAction>() { () => {
                Debug.Log("Continue!");
                ConfigData.LoadLevel();
            }, ExitToMainMenu });
            CampaignLevelEndedDialogue.SetTextBoxHeight(100);
            CampaignLevelEndedDialogue.SetButtonWidth(1, 180);

            CampaignCompletedDialogue = new Dialogue(Stage.DialoguePrefab, "Campaign Completed!", "Congratulations! You've finished the Beta Campaign!", new List<string>() { "Exit to Main Menu" }, new List<UnityAction>() {ExitToMainMenu });
            CampaignCompletedDialogue.SetTextBoxHeight(120);
            CampaignCompletedDialogue.SetButtonWidth(0, 180);
            //Debug.Log($"ActionBox:{ActionBox}");
            //Debug.Log($"EventSystem:{EventSystem}");

            ConfirmSurrenderDialogue = new Dialogue(Stage.DialoguePrefab, "Are you sure?", "This will destroy all your ships on this level permanently and end the level.", new List<string>() { ConfigData.Configuration.Yes, ConfigData.Configuration.No }, new List<UnityAction>() { Surrender });
        }
        public void HideMissionSummary()
        {
            SummaryPanel.SetActive(false);
            ShowLevelEndedDialogue();
        }
        public void SetMissionStatus(string status)
        {
            MissionStatusText.text = status;
        }
        public void UpdateMineralsMined(int mined, int max)
        {
            MineralsMinedCount.text = $"{mined}/{max}";
            MineralsMinedFiller.transform.localScale = new Vector2(max == 0 ? 0 : ((float)mined / max) * 425, 1);
        }
        public void TogglePausePanel()
        {
            PausePanel.SetActive(!PausePanel.activeSelf);
        }
        public void OpenMenu()
        {
            //Debug.Log("Opening menu");
            CurrentLevel.Pause();
            MenuContainer.SetActive(true);
        }
        public void ConfirmExitGame()
        {
            if (ConfigData.CurrentGameMode != ConfigData.GameModes.FishTank)
            {
                Debug.Log("Asking for confirmation");
                DeselectButton();
                ExitConfirmationDialogue.Show();
            }
            else
            {
                ExitToMainMenu();
            }

        }
        public void ShowLevelSummary()
        {
            SummaryPanel.SetActive(true);
            ShipsDestroyedText.text = $"Ships Destroyed: {Stage.PrimaryLevel.State.EnemyShipsDestroyedByPlayer}";
            ShipsReturnedText.text = $"Ships Returned: {Stage.PrimaryLevel.State.PlayerShipsReturned}";
            ShipsLostText.text = $"Ships Lost: {Stage.PrimaryLevel.State.PlayerShipsLost}";
            NewShipsReceivedText.text = $"New Ships Received: {Stage.PrimaryLevel.State.PlayerNewShipsReceived}";
            ScoreText.text = $"Score: {Stage.PrimaryLevel.State.PlayerScore}";
            MineralsReceivedText.text = $"Minerals Received: {Stage.PrimaryLevel.State.PlayerMineralsReceived}";
        }
        public void ShowLevelEndedDialogue()
        {
            Stage.TimeScale = 1f;
            Time.timeScale = Stage.TimeScale;
            GameSpeedButtonText.text = $"{Stage.TimeScale}x";
            if (ConfigData.UserProgressData.GetCurrentLevel(ConfigData.Configuration.UserSide) >= ConfigData.Configuration.TotalLevels)
            {
                CampaignCompletedDialogue.Show();
            }
            else
            {
                CampaignLevelEndedDialogue.Show();
            }
        }

        public void Exit()
        {
            Debug.Log("Exiting game");
            Application.Quit();
        }
        public void HoverOverMiniMapButton()
        {
            //Debug.Log("Hovering over mini map button");
            HoveringOverMiniMapButton = true;
        }
        public void ExitMiniMapButton()
        {
            //Debug.Log("Exiting mini map button");
            HoveringOverMiniMapButton = false;
        }
        public void ToggleMiniMapDisplay()
        {
            //Debug.Log("Toggling mini map!");
            CurrentLevel.Stage.MiniMapCameraContainer.SetActive(!CurrentLevel.Stage.MiniMapCameraContainer.activeSelf);
            MiniMapCloseButton.SetActive(!MiniMapCloseButton.activeSelf);
            MiniMapOpenButton.SetActive(!MiniMapOpenButton.activeSelf);
            MiniMapLeftBorder.SetActive(!MiniMapLeftBorder.activeSelf);
            MiniMapTopBorder.SetActive(!MiniMapTopBorder.activeSelf);
            MiniMapCameraCollider.SetActive(!MiniMapCameraCollider.activeSelf);
            MiniMapOutput.SetActive(!MiniMapOutput.activeSelf);
            IsMiniMapOpen = MiniMapCloseButton.activeSelf;
            
        }
        public void CloseDialogue()
        {
            //Debug.Log("Closing dialogue");
            DeselectButton();
            LevelEndedDialogue.SetActive(false);
            SaveLevelDialogue.SetActive(false);
            MenuContainer.SetActive(false);
            CurrentLevel.UnPause();
        }
        public void RestartLevel()
        {
            CurrentLevel.UnPause();
            CurrentLevel.IsRestarting = true;
            CurrentLevel.SaveAndEnd();
            CloseDialogue();
        }
        /// <summary>
        /// Kills all of the player's ships to end the level
        /// </summary>
        public void Surrender()
        {
            CurrentLevel.UnPause();
            CloseDialogue();
            Ship[] ships = CurrentLevel.State.GetShips(ConfigData.Configuration.UserSide).ToArray(); // need to convert this to an array because killing a ship removes it from the list of ships in the state

            for (int i = 0; i < ships.Length; i++)
            {
                Ship ship = ships[i];

                ship.Kill(null, null, null);
            }
            if (ConfigData.CurrentGameMode == ConfigData.GameModes.Campaign)
            {
                //Debug.Log($"Surrending level #{ConfigData.UserProgressData.GetCurrentLevel()}");
                CurrentLevel.CloseLevel();
                CurrentLevel.GetType().GetMethod($"Level{ConfigData.UserProgressData.GetCurrentLevel(ConfigData.Configuration.UserSide)}Ending").Invoke(CurrentLevel, null);
            }

        }
        public void ConfirmSurrender()
        {
            ConfirmSurrenderDialogue.Show();
        }
        public void TryNewLevel()
        {
            DeselectButton();
            CurrentLevel.UnPause();
            CurrentLevel.CloseLevel();
            ConfigData.SquadMakerSide = ConfigData.Configuration.SquadMakerFirstSide;
            SceneManager.LoadSceneAsync("Squad Maker", LoadSceneMode.Single);
        }
        public void ChangeLevelName(string name)
        {
            name = Utilities.ValidateInputString(name);
            //Debug.Log($"Level name changed to {name}");
            LevelNameInput.text = name;
        }
        public void ChangeSupplyCapacity(string capacity)
        {
            int validCapacity;
            bool isValid = int.TryParse(capacity, out validCapacity); // the out keyword allows the method to essentially "return" a second value
            if (!isValid)
            {
                validCapacity = 0;
            }
            SupplyCapacityInput.text = $"{validCapacity}";
        }
        public void SwitchSides()
        {
            DeselectButton();
            CurrentLevel.UnPause();
            CurrentLevel.CloseLevel();
            ConfigData.SwapSides();
            SceneManager.LoadSceneAsync("Squad Maker", LoadSceneMode.Single);
        }
        public void ToggleFogOfWar()
        {
            DeselectButton();
            CurrentLevel.Map.FogOfWar.SetActive(!CurrentLevel.Map.FogOfWar.activeSelf);
        }
        public void ToggleFogOfWar(bool onOrOff)
        {
            DeselectButton();
            CurrentLevel.Map.FogOfWar.SetActive(onOrOff);
        }
        public void OpenLevelEndedDialogue()
        {
            CurrentLevel.Pause();
            LevelEndedDialogue.SetActive(true);
            VictoryLabel.SetActive(CurrentLevel.DidUserWin);
            DefeatLabel.SetActive(!CurrentLevel.DidUserWin);
        }
        public void ExitToMainMenu()
        {
            Debug.Log("Exiting to main menu");
            CurrentLevel.UnPause();
            CurrentLevel.CloseLevel();
            DeselectButton();
            CloseDialogue();
            SceneManager.LoadSceneAsync("Main Menu", LoadSceneMode.Single);
            //MenuContainer.SetActive(false);
        }
        public void UpdateScore(int humanWins, int beeWins)
        {
            int totalGames = humanWins + beeWins;
            int humanLosses = totalGames - humanWins;
            int beeLosses = totalGames - beeWins;
            int humanWinPercentage = (int) (((float) humanWins / totalGames)*100);
            int beeWinPercentage = (int) (((float) beeWins / totalGames)*100);

            if (totalGames > 0)
            {
                TMP_Text humanScoreText = HumanScore.GetComponentInChildren<TMP_Text>();
                TMP_Text beeScoreText = BeeScore.GetComponentInChildren<TMP_Text>();

                humanScoreText.text = $"Humans: {humanWins}W - {humanLosses}L {humanWinPercentage}%";
                beeScoreText.text = $"Bees: {beeWins}W - {beeLosses}L {beeWinPercentage}%";
            }


        }
        public void BacktoGame()
        {

            Debug.Log("Back to game");
            CloseDialogue();
        }
        public void PlayNextRound()
        {
            CloseDialogue();
            CurrentLevel.SetupLevel();
        }
        public void ShowLevelSaveDialogue()
        {
            DeselectButton();
            LevelNameInput.text = CurrentLevel.SaveLevelOptions.Name;
            SupplyCapacityInput.text = $"{CurrentLevel.State.InitialTsv[ConfigData.Configuration.UserSide - 1]}";
            SaveLevelDialogue.SetActive(true);
        }
        public void SaveLevel()
        {
            int capacity;
            bool isValid = int.TryParse(SupplyCapacityInput.text, out capacity); 
            if (!isValid)
            {
                capacity = 0;
            }

            LevelOptions level = (LevelOptions)CurrentLevel.SaveLevelOptions.Clone();
            if (CurrentLevel.HasObstacles)
            {
                Debug.Log($"LevelData: {Utilities.ListToString(CurrentLevel.ObstacleMap.Obstacles)}");
                level.ObstacleList = GameObject.FindGameObjectsWithTag("Obstacle").Select((obstacle) =>
                {
                    Debug.Log($"Making obstacle list for saved level");
                    return ((Vector2)obstacle.transform.localPosition, (Vector2)obstacle.transform.localScale);
                }).ToList();
            }
            else
            {
                level.ObstacleList = new List<(Vector2, Vector2)>();
            }

                level.Name = LevelNameInput.text;
            level.SupplyCapacity = capacity;
            ConfigData.GetLevelData().AddLevel(level);
            ConfigData.GetLevelData().Save();
            CloseDialogue();
        }
        public void GoToSettings()
        {
            DeselectButton();
            //Debug.Log("Settings!");
            Settings.ViewSettings();
            //Settings.ViewControls();
        }
        public void DeselectButton()
        {
            UIAudioController.Instance.PlayButtonSound();
            CurrentLevel.Stage.EventSystem.SetSelectedGameObject(null);
        }
        public void ShowShipStats(FleetShip ship)
        {

            ShipInfoBoxTitle.text = $"{ship.Name}";
            ShipInfoBoxStats.text = $"Battles: {ship.BattlesFought.ToString("N0")}: {ship.BattlesWon}W - {ship.BattlesLost}L     (#{ConfigData.CurrentShips.GetShipRanking(ship, "Record")})\n" +
                $"Shots Fired: {ship.ShotsFired.ToString("N0")}     (#{ConfigData.CurrentShips.GetShipRanking(ship, "ShotsFired")})\n" +
                $"Damage Done: {ship.DamageDone.ToString("N0")}     (#{ConfigData.CurrentShips.GetShipRanking(ship, "DamageDone")})\n" +
                $"Damage Received: {ship.DamageReceived.ToString("N0")}     (#{ConfigData.CurrentShips.GetShipRanking(ship, "DamageReceived")})\n" +
                $"Kills: {ship.Kills.ToString("N0")}    (#{ConfigData.CurrentShips.GetShipRanking(ship, "Kills")})\n" +
                $"{(ship.Type == ConfigData.ShipTypes.CarpenterBee || ship.Type == ConfigData.ShipTypes.Factory ? $"Minerals Mined: {(ship.MineralsMinedThisLevel + ship.MineralsMined).ToString("N0")}  (#{ConfigData.CurrentShips.GetShipRanking(ship, "Minerals Mined")})" : "\n")}";


            ShipInfoBox.SetActive(true);
        }
        public void GoToFishTank()
        {
            DeselectButton();
            ConfigData.CurrentGameMode = ConfigData.GameModes.FishTank;
            ConfigData.CurrentShips = ConfigData.FreePlayShips;
            CloseDialogue();
            SceneManager.LoadSceneAsync("Hivemind Training", LoadSceneMode.Single);
        }

        public void ChangeGameSpeed()
        {
            if (Stage.TimeScale == 1)
            {
                Stage.TimeScale = 1.5f;
            }
            else if (Stage.TimeScale == 1.5f)
            {
                Stage.TimeScale = 2f;
            }
            else
            {
                Stage.TimeScale = 1f;
            }
            Time.timeScale = Stage.TimeScale;
            GameSpeedButtonText.text = $"{Stage.TimeScale}x";
        }



    }
}