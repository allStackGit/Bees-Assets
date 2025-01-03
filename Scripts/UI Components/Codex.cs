using Assets.Scripts;
using Assets.Scripts.Settings;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

public class Codex : MonoBehaviour
{
    public GameObject CodexBarge, CodexBeacon, CodexCarrier, CodexCruiser, CodexDreadnought, CodexDrone, CodexFactory, CodexFireShip, CodexFlagship, CodexFrigate, CodexGunship, CodexScout,
             CodexStriker, CodexWarpGate, CodexBeehive, CodexBumblebee, CodexCarpenterBee, CodexHoneybee, CodexHornet, CodexLeafcutter, CodexQueen, CodexWasp, CodexYellowJacket;
    public Dictionary<string, GameObject> CodexShips;

    public void ViewCodex()
    {
        //Debug.Log("Viewing codex");
        gameObject.SetActive(true);
    }
    public void ExitCodex()
    {
        gameObject.SetActive(false);
    }
    public void SetupCodex()
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
                    $"Capacity: {(!ConfigData.SpawnedOnlyShipTypes.Contains(ship.Key) ? ConfigData.FreePlayShips.GetShipsOfType(ship.Key).First().GetMaxCapacity().ToString("N0") : "N/A")}";
            }
        }
    }
}
