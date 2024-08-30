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
        public GameObject MenuPanel, MenuPanelBacker, Codex,
            CodexBarge, CodexBeacon, CodexCarrier, CodexCruiser, CodexDreadnought, CodexDrone, CodexFactory, CodexFireShip, CodexFlagship, CodexFrigate, CodexGunship, CodexScout, CodexStriker, 
            CodexWarpGate, CodexBeehive, CodexBumblebee, CodexCarpenterBee, CodexHoneybee, CodexHornet, CodexLeafcutter, CodexQueen, CodexWasp, CodexYellowJacket;
        public bool HasSetupCodex;
        public Dictionary<string, GameObject> CodexShips;
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
        public void ViewCodex()
        {
            //Debug.Log("Viewing codex");
            if (!HasSetupCodex)
            {
                HasSetupCodex = true;
                SetupCodex();

            }

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
