using Assets.Scripts;
using Assets.Scripts.Scenes;
using Assets.Scripts.Settings;
using Assets.Scripts.UI_Components;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

public class Codex : MonoBehaviour
{
    public GameObject CodexBarge, CodexBeacon, CodexCarrier, CodexCruiser, CodexDreadnought, CodexDrone, CodexFactory, CodexFireBarge, CodexFlagship, CodexFrigate, CodexGunship, CodexScout,
             CodexStriker, CodexWarpGate, CodexBeehive, CodexBumblebee, CodexCarpenterBee, CodexHoneybee, CodexHornet, CodexLeafcutter, CodexQueen, CodexWasp, CodexYellowJacket;
    public Dictionary<ConfigData.ShipTypes, GameObject> CodexShips;

    public void ViewCodex()
    {
        //Debug.Log("Viewing codex");
        UIAudioController.Instance.PlayButtonSound();
        gameObject.SetActive(true);
    }
    public void ExitCodex()
    {
        UIAudioController.Instance.PlayButtonSound();
        gameObject.SetActive(false);
    }
    public void SetupCodex()
    {
        CodexShips = new Dictionary<ConfigData.ShipTypes, GameObject> {
                    {ConfigData.ShipTypes.Barge, CodexBarge },
                    {ConfigData.ShipTypes.Beacon, CodexBeacon },
                    {ConfigData.ShipTypes.Carrier, CodexCarrier },
                    {ConfigData.ShipTypes.Cruiser, CodexCruiser },
                    {ConfigData.ShipTypes.Dreadnought, CodexDreadnought },
                    {ConfigData.ShipTypes.Drone, CodexDrone },
                    {ConfigData.ShipTypes.Factory, CodexFactory },
                    {ConfigData.ShipTypes.FireBarge, CodexFireBarge },
                    {ConfigData.ShipTypes.Flagship, CodexFlagship },
                    {ConfigData.ShipTypes.Frigate, CodexFrigate },
                    {ConfigData.ShipTypes.Gunship, CodexGunship },
                    {ConfigData.ShipTypes.Scout, CodexScout },
                    {ConfigData.ShipTypes.Striker, CodexStriker },
                    {ConfigData.ShipTypes.WarpGate, CodexWarpGate },
                    {ConfigData.ShipTypes.Beehive, CodexBeehive },
                    {ConfigData.ShipTypes.Bumblebee, CodexBumblebee },
                    {ConfigData.ShipTypes.CarpenterBee, CodexCarpenterBee },
                    {ConfigData.ShipTypes.Honeybee, CodexHoneybee },
                    {ConfigData.ShipTypes.Hornet, CodexHornet },
                    {ConfigData.ShipTypes.Leafcutter, CodexLeafcutter },
                    {ConfigData.ShipTypes.Queen, CodexQueen },
                    {ConfigData.ShipTypes.Wasp, CodexWasp },
                    {ConfigData.ShipTypes.YellowJacket, CodexYellowJacket }
                };

        foreach (KeyValuePair<ConfigData.ShipTypes, GameObject> ship in CodexShips)
        {
            if (!ConfigData.UserProgressData.VisibleShipTypes.Contains(ship.Key) && !ConfigData.SpawnedOnlyShipTypes.Contains(ship.Key))
            {
                ship.Value.SetActive(false);
                if (ship.Key == ConfigData.ShipTypes.Carrier)
                {
                    CodexDrone.SetActive(false);
                    CodexStriker.SetActive(false);
                }
            }
            else
            {
                TMP_Text description = ship.Value.transform.GetChild(2).GetComponent<TMP_Text>();
                TMP_Text stats = ship.Value.transform.GetChild(1).GetChild(0).GetComponent<TMP_Text>();
                ShipStatBlock shipInfo = ConfigData.GetShipInfo(ship.Key);

                description.text = shipInfo.CodexDescription;
                stats.text =
                    $"Health: {shipInfo.Health.ToString("N0")}\n" +
                    $"Vision: {shipInfo.PrintVision()}\n" +
                    $"Range: {shipInfo.PrintRange()}\n" +
                    $"Power: {shipInfo.PrintPower()}\n" +
                    $"Rate of Fire: {shipInfo.PrintRateOfFire()}\n" +
                    $"Speed: {shipInfo.Speed}\n" +
                    $"Capacity: {(!ConfigData.SpawnedOnlyShipTypes.Contains(ship.Key) ? ConfigData.FreePlayShips.GetShipsOfType(ship.Key).First().GetCapacity().ToString("N0") : "N/A")}";
            }
        }
    }
}
