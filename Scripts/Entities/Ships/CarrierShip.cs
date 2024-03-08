


using Assets.Scripts.Data;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class CarrierShip : Ship
    {

        public Carrier Carrier;
        public bool HasCarrier => Carrier != null && Carrier != null;

        public void CarrierShipSetup(FleetShip fleetShip, string shipType, Carrier carrier)
        {
            FleetShip = fleetShip;
            ShipType = shipType;
            Carrier = carrier;
        }
    }
}