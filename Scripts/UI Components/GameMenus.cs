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
        public GameObject MenuContainer, LevelEndedDialogue, NoAliveShipsAlert, SquadActionBoxUI, VictoryLabel, DefeatLabel, MiniMapCloseButton, MiniMapOpenButton, HumanScore, BeeScore, Codex,
            CodexBarge, CodexBeacon, CodexCarrier, CodexCruiser, CodexDreadnought, CodexDrone, CodexFactory, CodexFireShip, CodexFlagship, CodexFrigate, CodexGunship, CodexScout, 
            CodexStriker, CodexWarpGate, CodexBeehive, CodexBumblebee, CodexCarpenterBee, CodexHoneybee, CodexHornet, CodexLeafcutter, CodexQueen, CodexWasp, CodexYellowJacket, ShipInfoBox;
        public Dictionary<string, GameObject> CodexShips;
        public SquadActionBox ActionBox;
        public LevelStage Level;
        public Dialogue ExitConfirmationDialogue;
        public TMP_Text ShipInfoBoxTitle, ShipInfoBoxStats;
        public bool HoveringOverMiniMapButton;
        public bool IsSquadActionBoxOpen => ActionBox != null && SquadActionBoxUI.activeSelf;
        public bool HasSquadActionBox => Level.HasPlayer && ActionBox != null;


        public void Setup(LevelStage level)
        {
            Level = level;
            if (Level.HasPlayer)
            {
                ActionBox = SquadActionBoxUI.GetComponent<SquadActionBox>();
                SetupCodex();
                
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
        public void ViewCodex()
        {
            //Debug.Log("Viewing codex");
            Codex.SetActive(true);
        }
        public void ExitCodex()
        {
            Codex.SetActive(false);
        }
        private void SetupCodex()
        {
            CodexShips = new Dictionary<string, GameObject> {
                    {"Barge", CodexBarge },
                    {"Beacon", CodexBeacon },
                    {"Carrier", CodexCarrier },
                    {"Cruiser", CodexCruiser },
                    {"Dreadnought", CodexDreadnought },
                    {"Drone", CodexDrone },
                    {"Factory", CodexFactory },
                    {"Fire Ship", CodexFireShip },
                    {"Flagship", CodexFlagship },
                    {"Frigate", CodexFrigate },
                    {"Gunship", CodexGunship },
                    {"Scout", CodexScout },
                    {"Striker", CodexStriker },
                    {"Warp Gate", CodexWarpGate },
                    {"Beehive", CodexBeehive },
                    {"Bumblebee", CodexBumblebee },
                    {"Carpenter Bee", CodexCarpenterBee },
                    {"Honeybee", CodexHoneybee },
                    {"Hornet", CodexHornet },
                    {"Leafcutter", CodexLeafcutter },
                    {"Queen", CodexQueen },
                    {"Wasp", CodexWasp },
                    {"Yellow Jacket", CodexYellowJacket }
                };

            foreach (KeyValuePair<string, GameObject> ship in CodexShips)
            {
                if (!ConfigData.Configuration.VisibleShipTypes.Contains(ship.Key) && !ConfigData.SpawnedOnlyShipTypes.Contains(ship.Key))
                {
                    ship.Value.SetActive(false);
                    if (ship.Key == "Carrier")
                    {
                        CodexDrone.SetActive(false);
                        CodexStriker.SetActive(false);
                    }
                }
                else
                {
                    TMP_Text description = ship.Value.transform.GetChild(2).GetComponent<TMP_Text>();
                    TMP_Text stats = ship.Value.transform.GetChild(1).GetComponent<TMP_Text>();
                    ShipStatBlock shipInfo = ConfigData.GetShipInfo(ship.Key);

                    description.text = shipInfo.CodexDescription;
                    stats.text =
                        $"Health: {shipInfo.Health.ToString("N0")}\n" +
                        $"Range: {shipInfo.PrintRange()}\n" +
                        $"Power: {shipInfo.PrintPower()}\n" +
                        $"Rate of Fire: {shipInfo.PrintRateOfFire()}\n" +
                        $"Speed: {shipInfo.Speed}\n" +
                        $"Capacity: {(!ConfigData.SpawnedOnlyShipTypes.Contains(ship.Key) ? ConfigData.AllShips.GetShipsOfType(ship.Key).First().GetMaxCapacity().ToString("N0") : "N/A")}";
                }
            }
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