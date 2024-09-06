using System;

using UnityEngine;

using UnityEngine.SceneManagement;
using TMPro;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Assets.Scripts.Scenes;
using Assets.Scripts.UI_Components;
using UnityEngine.Events;
using Assets.Scripts.Settings;
using Assets.Scripts.Data;

namespace Assets.Scripts.UIComponents
{
    public class GameMenus : MonoBehaviour
    {
        public GameObject MenuContainer, LevelEndedDialogue, NoAliveShipsAlert, SquadActionBoxUI, VictoryLabel, DefeatLabel, MiniMapCloseButton, MiniMapOpenButton, MiniMapTopBorder, MiniMapLeftBorder,
            MiniMapCameraCollider, HumanScore, BeeScore, ShipInfoBox;
        public SquadActionBox ActionBox;
        public LevelStage Level;
        public Dialogue ExitConfirmationDialogue;
        public TMP_Text ShipInfoBoxTitle, ShipInfoBoxStats;
        public Codex Codex;

        public bool HoveringOverMiniMapButton;
        public bool IsSquadActionBoxOpen => ActionBox != null && SquadActionBoxUI.activeSelf;
        public bool HasSquadActionBox => Level.HasPlayer && ActionBox != null;


        public void Setup(LevelStage level)
        {
            Level = level;
            if (Level.HasPlayer)
            {
                ActionBox = SquadActionBoxUI.GetComponent<SquadActionBox>();
                Codex.SetupCodex();
                
            }

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
            Debug.Log("Toggling mini map!");
            Level.MiniMapContainer.SetActive(!Level.MiniMapContainer.activeSelf);
            MiniMapCloseButton.SetActive(!MiniMapCloseButton.activeSelf);
            MiniMapOpenButton.SetActive(!MiniMapOpenButton.activeSelf);
            MiniMapLeftBorder.SetActive(!MiniMapLeftBorder.activeSelf);
            MiniMapTopBorder.SetActive(!MiniMapTopBorder.activeSelf);
            MiniMapCameraCollider.SetActive(!MiniMapCameraCollider.activeSelf);
            
        }
        public void CloseDialogue()
        {
            Debug.Log("Deciding not to exit");
            DeselectButton();
            LevelEndedDialogue.SetActive(false);
            MenuContainer.SetActive(false);
            Level.UnPause();
        }
        public void RestartLevel()
        {
            Level.UnPause();
            Level.ReloadScene();
        }
        public void TryNewLevel()
        {
            Level.UnPause();
            ConfigData.SquadMakerSide = ConfigData.Configuration.SquadMakerFirstSide;
            SceneManager.LoadSceneAsync("Squad Maker", LoadSceneMode.Single);
        }
        public void SwitchSides()
        {
            Level.UnPause();
            ConfigData.SwapSides();
            SceneManager.LoadSceneAsync("Squad Maker", LoadSceneMode.Single);
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

            TMP_Text humanScoreText = HumanScore.GetComponentInChildren<TMP_Text>();
            TMP_Text beeScoreText = BeeScore.GetComponentInChildren<TMP_Text>();

            humanScoreText.text = $"Humans: {humanWins}W - {humanLosses}L {humanWinPercentage}%";
            beeScoreText.text = $"Bees: {beeWins}W - {beeLosses}L {beeWinPercentage}%";
        }
        public void BacktoGame()
        {
            Debug.Log("Back to game");
            CloseDialogue();
        }
        public void GoToSettings()
        {
            DeselectButton();
            Debug.Log("Settings!");
        }
        public void DeselectButton()
        {
            Level.EventSystem.SetSelectedGameObject(null);
        }
        public void ShowShipStats(FleetShip ship)
        {

            ShipInfoBoxTitle.text = $"{ship.Name}";
            ShipInfoBoxStats.text = $"Battles: {ship.BattlesFought.ToString("N0")}: {ship.BattlesWon}W - {ship.BattlesLost}L     (#{ConfigData.AllShips.GetShipRanking(ship, "Record")})\n" +
                $"Shots Fired: {ship.ShotsFired.ToString("N0")}     (#{ConfigData.AllShips.GetShipRanking(ship, "ShotsFired")})\n" +
                $"Damage Done: {ship.DamageDone.ToString("N0")}     (#{ConfigData.AllShips.GetShipRanking(ship, "DamageDone")})\n" +
                $"Damage Received: {ship.DamageReceived.ToString("N0")}     (#{ConfigData.AllShips.GetShipRanking(ship, "DamageReceived")})\n" +
                $"Kills: {ship.Kills.ToString("N0")}    (#{ConfigData.AllShips.GetShipRanking(ship, "Kills")})\n" +
                $"{(ship.Type == "Carpenter Bee" || ship.Type == "Factory" ? $"Minerals Mined: {ship.MineralsMined.ToString("N0")}  (#{ConfigData.AllShips.GetShipRanking(ship, "Minerals Mined")})" : "\n")}";


            ShipInfoBox.SetActive(true);
        }



    }
}