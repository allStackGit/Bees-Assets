using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;
using System.Collections.Generic;
using Assets.Scripts.Scenes;
using Assets.Scripts.UI_Components;
using UnityEngine.Events;
using Assets.Scripts.Data;
using System.Linq;
using System;
using Assets.Scripts.Entities.Ships;

namespace Assets.Scripts.UIComponents
{
    public class GameMenus : MonoBehaviour
    {
        public GameObject MenuContainer, LevelEndedDialogue, SaveLevelDialogue, NoAliveShipsAlert, SquadActionBoxUI, VictoryLabel, DefeatLabel, MiniMapCloseButton, MiniMapOpenButton, 
            MiniMapTopBorder, MiniMapLeftBorder, MiniMapCameraCollider, MiniMapOutput, HumanScore, BeeScore, ShipInfoBox, KeepGoingButton, ToggleFogOfWarButton, RestartLevelButton,
            ChooseNewSquadsButton, SwitchSidesButton, SaveAsLevelButton, ExitToMainMenuButton, Scoreboard;
        public SquadActionBox ActionBox;
        public LevelStage Level;
        public Dialogue ExitConfirmationDialogue;
        public TMP_Text ShipInfoBoxTitle, ShipInfoBoxStats, TryNewSquadsButtonText;
        public TMP_InputField LevelNameInput, SupplyCapacityInput;
        public Codex Codex;
        public SettingsMenu Settings;
        public bool IsMiniMapOpen;

        public bool HoveringOverMiniMapButton;
        public bool IsSquadActionBoxOpen => ActionBox != null && SquadActionBoxUI.activeSelf;
        public bool HasSquadActionBox => !Level.IsTraining && ActionBox != null;


        public void Setup(LevelStage level)
        {
            Level = level;
            if (!Level.IsTraining)
            {
                ActionBox = SquadActionBoxUI.GetComponent<SquadActionBox>();
                Codex.SetupCodex();
                Settings.SetupSettings(Level);
                
                if (ConfigData.IsPlayingCampaign)
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

            ExitConfirmationDialogue = new Dialogue(Level.DialoguePrefab, ConfigData.Configuration.AreYouSureExit, ConfigData.Configuration.LevelProgressLost,
                new List<string>() { ConfigData.Configuration.Yes, ConfigData.Configuration.No }, new List<UnityAction>() { ExitToMainMenu });
            ExitConfirmationDialogue.SetTextBoxHeight(200);
            //Debug.Log($"ActionBox:{ActionBox}");
            //Debug.Log($"EventSystem:{EventSystem}");
        }
        public void OpenMenu()
        {
            Level.Pause();
            MenuContainer.SetActive(true);
        }
        public void ConfirmExitGame()
        {
            Debug.Log("Asking for confirmation");
            DeselectButton();
            ExitConfirmationDialogue.Show();
        }
        public void Exit()
        {
            Debug.Log("Exiting game");
            Application.Quit();
        }
        public void HoverOverMiniMapButton()
        {
            HoveringOverMiniMapButton = true;
        }
        public void ExitMiniMapButton()
        {
            HoveringOverMiniMapButton = false;
        }
        public void ToggleMiniMapDisplay()
        {
            //Debug.Log("Toggling mini map!");
            Level.MiniMapContainer.SetActive(!Level.MiniMapContainer.activeSelf);
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
            Debug.Log("Deciding not to exit");
            DeselectButton();
            LevelEndedDialogue.SetActive(false);
            SaveLevelDialogue.SetActive(false);
            MenuContainer.SetActive(false);
            Level.UnPause();
        }
        public void RestartLevel()
        {
            Level.UnPause();
            Level.IsRestarting = true;
            Level.SaveAndEnd();
        }
        /// <summary>
        /// Kills all of the player's ships to end the level
        /// </summary>
        public void Surrender()
        {
            Level.UnPause();
            MenuContainer.SetActive(false);
            Ship[] ships = Level.GetState().GetShips(ConfigData.Configuration.UserSide).ToArray(); // need to convert this to an array because killing a ship removes it from the list of ships in the state

            for (int i = 0; i < ships.Length; i++)
            {
                Ship ship = ships[i];

                ship.Kill(null, null, null);
            }


        }
        public void TryNewLevel()
        {
            Level.UnPause();
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
            Level.UnPause();
            ConfigData.SwapSides();
            SceneManager.LoadSceneAsync("Squad Maker", LoadSceneMode.Single);
        }
        public void ToggleFogOfWar()
        {
            Level.Map.FogOfWar.SetActive(!Level.Map.FogOfWar.activeSelf);
        }
        public void OpenLevelEndedDialogue()
        {
            Level.Pause();
            LevelEndedDialogue.SetActive(true);
            VictoryLabel.SetActive(Level.DidUserWin);
            DefeatLabel.SetActive(!Level.DidUserWin);
        }
        public void ExitToMainMenu()
        {
            Debug.Log("Exiting to main menu");
            Level.UnPause();
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
            Level.SetupLevel();
        }
        public void ShowLevelSaveDialogue()
        {
            LevelNameInput.text = Level.SaveLevelOptions.Name;
            SupplyCapacityInput.text = $"{Level.GetState().InitialTsv[ConfigData.Configuration.UserSide - 1]}";
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

            //Debug.Log($"LevelData: {LevelData.GetEnemyList()}");
            LevelOptions level = (LevelOptions)Level.SaveLevelOptions.Clone();
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
            Level.EventSystem.SetSelectedGameObject(null);
        }
        public void ShowShipStats(FleetShip ship)
        {

            ShipInfoBoxTitle.text = $"{ship.Name}";
            ShipInfoBoxStats.text = $"Battles: {ship.BattlesFought.ToString("N0")}: {ship.BattlesWon}W - {ship.BattlesLost}L     (#{ConfigData.CurrentShips.GetShipRanking(ship, "Record")})\n" +
                $"Shots Fired: {ship.ShotsFired.ToString("N0")}     (#{ConfigData.CurrentShips.GetShipRanking(ship, "ShotsFired")})\n" +
                $"Damage Done: {ship.DamageDone.ToString("N0")}     (#{ConfigData.CurrentShips.GetShipRanking(ship, "DamageDone")})\n" +
                $"Damage Received: {ship.DamageReceived.ToString("N0")}     (#{ConfigData.CurrentShips.GetShipRanking(ship, "DamageReceived")})\n" +
                $"Kills: {ship.Kills.ToString("N0")}    (#{ConfigData.CurrentShips.GetShipRanking(ship, "Kills")})\n" +
                $"{(ship.Type == "Carpenter Bee" || ship.Type == "Factory" ? $"Minerals Mined: {(ship.MineralsMinedThisLevel + ship.MineralsMined).ToString("N0")}  (#{ConfigData.CurrentShips.GetShipRanking(ship, "Minerals Mined")})" : "\n")}";


            ShipInfoBox.SetActive(true);
        }



    }
}